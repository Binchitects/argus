using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Core.Chat;

namespace Llm.Tests;

/// <summary>How a refusal for the context is read, and how a request is measured against the context before it is sent.</summary>
public sealed class PromptBudgetTests
{
    [Fact]
    public void ContextOverflow_reads_llama_cpp_and_LiteLLM_refusals()
    {
        Assert.Equal(new ContextOverflow(true, false, 40_000, 32_768), ContextOverflow.Parse(FakeModel.Overflow(40_000)));
        Assert.Equal(new ContextOverflow(true, false, 34_803, 32_768), ContextOverflow.Parse(FakeModel.LlamaOverflow));
        Assert.Equal(new ContextOverflow(true, true, null, null), ContextOverflow.Parse(FakeModel.SharedCacheFull));
        Assert.Equal(new ContextOverflow(true, false, 36_000, 32_768), ContextOverflow.Parse(FakeModel.OpenAiOverflow));
        // vLLM's and SGLang's, for a prompt and a longest answer that are more than the window together: their counts too.
        Assert.Equal(new ContextOverflow(true, false, 3_070, 32_768), ContextOverflow.Parse(
            "This model's maximum context length is 32768 tokens. However, you requested 31744 output tokens and your prompt contains 3070 input tokens, for a total of 34814 tokens."));
        Assert.Equal(new ContextOverflow(true, false, 3_070, 32_768), ContextOverflow.Parse(
            "Requested token count exceeds the model's maximum context length of 32768 tokens. You requested a total of 34814 tokens: 3070 tokens from the input messages and 31744 tokens for the completion."));
        // llama.cpp's fields in either quote style.
        Assert.Equal(286_311, ContextOverflow.Parse("exceeds the available context size (n_prompt_tokens=286311, n_ctx=262144)").PromptTokens);
        Assert.Equal(262_144, ContextOverflow.Parse("""{"message":"…","n_prompt_tokens":286311,"n_ctx":262144}""").ContextTokens);
        // Others are not.
        Assert.False(ContextOverflow.Parse("""{"error":{"message":"Budget has been exceeded! Current cost: 5.1, Max budget: 5.0"}}""").Overflow);
        Assert.False(ContextOverflow.Parse("Limit type: tokens. Current limit: 1000").Overflow);
        Assert.False(ContextOverflow.Parse(null).Overflow);
    }

    [Fact]
    public void The_person_reads_how_far_over_the_context_a_refusal_was()
    {
        var ex = ChatGatewayException.From(FakeModel.Overflow(40_000), 400);
        Assert.True(ex.ContextOverflow);
        Assert.Equal((40_000L, 32_768L), (ex.PromptTokens, ex.ContextTokens));
        Assert.StartsWith("This chat is longer than the model can read (about 40,000 of 32,768 tokens).", ex.Message, StringComparison.Ordinal);
        // In the stream, with no status: the shared cache's error.
        var shared = ChatGatewayException.From(FakeModel.SharedCacheFull, null);
        Assert.True(shared is { ContextOverflow: true, SharedCache: true });
        // A refusal for another reason says nothing of the context, whatever its words.
        Assert.False(ChatGatewayException.From("""{"error":{"message":"context length"}}""", 429).ContextOverflow);
    }

    [Fact]
    public void The_answer_s_room_is_an_eighth_of_the_context_within_the_model_s_output()
    {
        Assert.Equal(4_096, PromptBudget.Room(32_768, 8_192, null));
        Assert.Equal(32_768, PromptBudget.Room(262_144, 32_768, null));
        Assert.Equal(2_000, PromptBudget.Room(262_144, 2_000, null));
        // Never under 1,024 tokens (a quarter of a tiny context at most), and the chat's own longest answer when it set one.
        Assert.Equal(1_024, PromptBudget.Room(8_192, 512, null));
        Assert.Equal(1_000, PromptBudget.Room(4_000, null, null));
        Assert.Equal(6_000, PromptBudget.Room(32_768, 8_192, 6_000));
        // What the request asks for (max_tokens) is what is kept: never more than the model's longest answer (one chosen
        // for another model), nor than half the context, so the conversation keeps the other half to read. One as long as
        // the model's own (31,744 of 32,768) once left the prompt 369 tokens: every message after left the chat out.
        Assert.Equal((8_192, 8_192), (PromptBudget.Asked(32_768, 8_192, 16_000), PromptBudget.Room(32_768, 8_192, 16_000)));
        Assert.Equal((16_384, 16_384), (PromptBudget.Asked(32_768, 31_744, 31_744), PromptBudget.Room(32_768, 31_744, 31_744)));
        Assert.Equal((16_384, 16_384), (PromptBudget.Asked(32_768, 31_744, 24_000), PromptBudget.Room(32_768, 31_744, 24_000)));
        Assert.Equal(16_384, PromptBudget.Asked(32_768, null, 24_000));
        Assert.Null(PromptBudget.Asked(32_768, 8_192, null));
        // The most a chat may choose: the model's longest answer, at most half its context.
        Assert.Equal((8_192, 16_384, 16_384), (PromptBudget.Most(32_768, 8_192), PromptBudget.Most(32_768, 31_744), PromptBudget.Most(32_768, null)));
        var budget = new PromptBudget(32_768, 4_096);
        Assert.Equal(655, budget.Margin);
        Assert.Equal(32_768 - 4_096 - 655, budget.Limit);
    }

    [Fact]
    public void Token_dense_text_is_estimated_from_the_last_answer_s_count()
    {
        Assert.Null(PromptBudget.Measured("""{"you":2000}""", 100));
        Assert.Equal(2.0, PromptBudget.Measured("""{"system":1000,"you":19000}""", 10_000));
        // Kept within reason: 2 to 4.5 characters a token.
        Assert.Equal(PromptBudget.FewestCharsPerToken, PromptBudget.Measured("""{"you":1000}""", 1_000));
        Assert.Equal(PromptBudget.MostCharsPerToken, PromptBudget.Measured("""{"you":100000}""", 1_000));
        // The newest answer that says.
        Assert.Equal(2.5, PromptBudget.Measured([
            new ChatMessage { Role = "assistant", ContextJson = """{"you":3000}""", PromptTokens = 1_000 },
            new ChatMessage { Role = "assistant", ContextJson = """{"you":2500}""", PromptTokens = 1_000 },
            new ChatMessage { Role = "user", Content = "hi" },
        ]));

        var budget = new PromptBudget(32_768, 4_096);
        Assert.Equal(PromptBudget.DefaultCharsPerToken, budget.CharsPerToken);
        Assert.Equal(1_000 + 2 * PromptBudget.PerMessage, budget.Tokens(3_000, 2));
        budget.Learn(9_000, 4_500);
        Assert.Equal(2.0, budget.CharsPerToken);
        // A refusal counted more tokens than the estimate: never fewer again.
        budget = new PromptBudget(32_768, 4_096);
        budget.Refused(90_000, 40_000);
        Assert.Equal(2.25, budget.CharsPerToken);
        // Sure not to fit only past the context at 4.5 characters a token.
        Assert.False(budget.CannotFit(140_000, 1));
        Assert.True(budget.CannotFit(150_000, 1));
    }

    [Fact]
    public void A_request_counts_its_tool_definitions_and_pictures()
    {
        var request = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = new string('s', 100) },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "look" }, new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:" } }),
                },
                new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "c" }) }),
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "function" }),
        };
        Assert.Equal(100 + 4 + 3_500 + """[{"id":"c"}]""".Length + """[{"type":"function"}]""".Length, PromptBudget.Chars(request, 3_500));
    }

    [Fact]
    public void Tool_results_but_the_last_two_are_cut_to_their_start()
    {
        var big = new string('r', 10_000);
        var messages = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = "s" },
            new JsonObject { ["role"] = "tool", ["content"] = big },
            new JsonObject { ["role"] = "user", ["content"] = "q" },
            new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray() },
            new JsonObject { ["role"] = "tool", ["content"] = big },
            new JsonObject { ["role"] = "tool", ["content"] = "short" },
            new JsonObject { ["role"] = "tool", ["content"] = big },
            new JsonObject { ["role"] = "tool", ["content"] = big });
        Assert.True(ChatService.CutToolResults(messages, 2));
        string Content(int i) => messages[i]!["content"]!.GetValue<string>();
        // Before the question: another answer's (the history), left as it is.
        Assert.Equal(big, Content(1));
        Assert.StartsWith(new string('r', ChatService.CutResultChars) + "\n\n[Cut here: the result was 10,000 characters", Content(4), StringComparison.Ordinal);
        Assert.Equal("short", Content(5));
        Assert.Equal((big, big), (Content(6), Content(7)));
        // Nothing more to cut.
        Assert.False(ChatService.CutToolResults(messages, 2));
    }

    [Fact]
    public void A_cut_tool_result_says_how_to_read_it_whole_again()
    {
        // Before: "call the tool again", even when the chat keeps it whole and the model can read it, and a big result's
        // own note (the file it is kept as) was cut away with the rest.
        var big = new string('r', 10_000);
        var kept = new string('k', 5_000) + "\n\n[The result is 90,000 characters, 3,000 lines; above are its first 5,000. All of it is the file web-result-a1b2c3.txt in this chat: read_file reads on from line 120, search_file finds lines in it.]\n[Its end, from line 2990:]\nbye";
        var messages = new JsonArray(
            new JsonObject { ["role"] = "user", ["content"] = "q" },
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call_7", ["content"] = big },
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call_8", ["content"] = kept },
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call_9", ["content"] = big },
            new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call_10", ["content"] = big });
        Assert.True(ChatService.CutToolResults(messages, 0, canRead: true));
        string Content(int i) => messages[i]!["content"]!.GetValue<string>();
        Assert.EndsWith("read_file with file \"result:call_7\" (from_line to read on, search_file to find lines).]", Content(1), StringComparison.Ordinal);
        Assert.Contains("All of it is the file web-result-a1b2c3.txt in this chat", Content(2), StringComparison.Ordinal);
        Assert.True(Content(2).Length < 2_600);
        // Cut once: a second pass leaves it.
        Assert.False(ChatService.CutToolResults(messages, 0, canRead: true));
        // A model that reads no files is told to call the tool again.
        var plain = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "q" }, new JsonObject { ["role"] = "tool", ["tool_call_id"] = "c", ["content"] = big },
            new JsonObject { ["role"] = "tool", ["content"] = "a" }, new JsonObject { ["role"] = "tool", ["content"] = "b" });
        ChatService.CutToolResults(plain, 0);
        Assert.EndsWith("Call the tool again for a part of it if you need more.]", plain[1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }
}
