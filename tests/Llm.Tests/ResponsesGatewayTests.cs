using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Gateway;
using Llm.Api.Gateway.Protocols;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// OpenAI's Responses API (Codex) and the legacy completions answered by the app's own gateway (Gateway:Mode own): the
/// request as one chat completion, the answer and its stream as a response; previous_response_id and built-in tools
/// refused; a completion passed on to the engine and booked; a non-streamed answer booked by its usage.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ResponsesGatewayTests(AppFixture app)
{
    [Fact]
    public void A_responses_request_becomes_one_chat_completion_and_its_answer_a_response()
    {
        var chat = OpenAiResponses.ToChat((JsonObject)JsonNode.Parse("""
            {"model":"m","instructions":"Be brief.","max_output_tokens":99,"reasoning":{"effort":"low"},"tool_choice":"auto",
             "tools":[{"type":"function","name":"shell","description":"Runs","parameters":{"type":"object"}}],
             "input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"List files"}]},
                      {"type":"reasoning","id":"rs_1","summary":[]},
                      {"type":"function_call","call_id":"c1","name":"shell","arguments":"{\"cmd\":\"ls\"}"},
                      {"type":"function_call_output","call_id":"c1","output":"a.txt"}]}
            """)!);
        var m = chat["messages"]!.AsArray();
        Assert.Equal(["system", "user", "assistant", "tool"], m.Select(x => x!["role"]!.GetValue<string>()));
        Assert.Equal("List files", m[1]!["content"]!.GetValue<string>());
        Assert.Equal("c1", m[2]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal((99, "low"), (chat["max_tokens"]!.GetValue<int>(), chat["chat_template_kwargs"]!["reasoning_effort"]!.GetValue<string>()));
        Assert.Equal(400, Assert.Throws<AdapterException>(() => OpenAiResponses.ToChat((JsonObject)JsonNode.Parse("""{"model":"m","input":"x","previous_response_id":"resp_1"}""")!)).Status);
        Assert.Equal(400, Assert.Throws<AdapterException>(() => OpenAiResponses.ToChat((JsonObject)JsonNode.Parse("""{"model":"m","input":"x","tools":[{"type":"web_search"}]}""")!)).Status);

        var response = OpenAiResponses.FromChat((JsonObject)JsonNode.Parse("""
            {"choices":[{"finish_reason":"tool_calls","message":{"content":"Running.","tool_calls":[{"id":"c2","function":{"name":"shell","arguments":"{}"}}]}}],"usage":{"prompt_tokens":10,"completion_tokens":2}}
            """)!, "m");
        Assert.Equal(["message", "function_call"], response["output"]!.AsArray().Select(o => o!["type"]!.GetValue<string>()));
        Assert.Equal(("completed", 12), (response["status"]!.GetValue<string>(), response["usage"]!["total_tokens"]!.GetValue<int>()));

        var events = new OpenAiResponses.Events("m");
        var said = events.Feed(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\" there\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));
        var names = said.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(e => e.Split('\n')[0][7..]).ToList();
        Assert.Equal(["response.created", "response.in_progress", "response.output_item.added", "response.content_part.added", "response.output_text.delta", "response.output_text.delta",
            "response.output_text.done", "response.content_part.done", "response.output_item.done", "response.completed"], names);
        Assert.Contains("\"text\":\"Hi there\"", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_streamed_answer_is_booked_by_its_usage()
    {
        var meter = new SseMeter(TimeProvider.System);
        meter.Read(Encoding.UTF8.GetBytes("""{"choices":[{"message":{"content":"Hello"}}],"usage":{"prompt_tokens":9,"completion_tokens":2,"prompt_tokens_details":{"cached_tokens":4}}}"""));
        meter.Finish();
        Assert.Equal((9, 4, 2, true, true), (meter.Prompt, meter.Cached, meter.Completion, meter.Usage, meter.Done));
    }

    [Fact]
    public async Task Codex_and_the_editors_completions_are_answered_and_booked()
    {
        await using var f = app.Create(app.ConnectionStringFor("codex_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?> { ["Gateway:Mode"] = "own" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "cx" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var key = made.GetProperty("apiKey").GetString()!;
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
        async Task<(HttpStatusCode, string)> SendAsync(string path, string body)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new("Bearer", key);
            var res = await client.SendAsync(req);
            return (res.StatusCode, await res.Content.ReadAsStringAsync());
        }

        var (status, whole) = await SendAsync("/v1/responses", """{"model":"Qwen3.8-Flash-Next","input":"Say hi"}""");
        Assert.Equal(HttpStatusCode.OK, status);
        var response = JsonNode.Parse(whole)!;
        Assert.Equal(("response", "completed"), (response["object"]!.GetValue<string>(), response["status"]!.GetValue<string>()));
        Assert.StartsWith("Answer to:", response["output"]!.AsArray().Last()!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);

        var (streamed, events) = await SendAsync("/responses", """{"model":"Qwen3.8-Flash-Next","input":"Say hi","stream":true}""");
        Assert.Equal(HttpStatusCode.OK, streamed);
        Assert.StartsWith("event: response.created", events, StringComparison.Ordinal);
        Assert.Contains("event: response.completed", events, StringComparison.Ordinal);

        var (refused, why) = await SendAsync("/v1/responses", """{"model":"Qwen3.8-Flash-Next","input":"x","previous_response_id":"resp_1"}""");
        Assert.Equal(HttpStatusCode.BadRequest, refused);
        Assert.Contains("previous_response_id", why, StringComparison.Ordinal);

        var (completed, completion) = await SendAsync("/v1/completions", """{"model":"Qwen3.8-Flash-Next","prompt":"function hello","max_tokens":8}""");
        Assert.Equal(HttpStatusCode.OK, completed);
        Assert.Equal(" world();", JsonNode.Parse(completion)!["choices"]![0]!["text"]!.GetValue<string>());

        var gateway = f.Services.GetRequiredService<ModelGateway>();
        Llm.Core.Gateway.GatewayRequest? row = null;
        for (var i = 0; i < 100 && row is null; i++)
        {
            await gateway.FlushAsync(default);
            await using var scope = f.Services.CreateAsyncScope();
            row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().GatewayRequests.AsNoTracking()
                .FirstOrDefaultAsync(r => r.UserId == made.GetProperty("id").GetGuid() && r.Call == "completion");
            if (row is null)
            {
                await Task.Delay(50);
            }
        }
        Assert.NotNull(row);
        Assert.Equal((7, 3, false), (row.PromptTokens, row.CompletionTokens, row.UsageEstimated));
    }
}
