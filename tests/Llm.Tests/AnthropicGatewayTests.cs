using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Gateway.Protocols;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>
/// Anthropic's Messages API answered by the app's own gateway (Gateway:Mode own): the request as one chat completion, the
/// answer, its stream and its errors in Anthropic's shapes; count_tokens; a server tool refused clearly.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class AnthropicGatewayTests(AppFixture app)
{
    [Fact]
    public void A_messages_request_becomes_one_chat_completion()
    {
        var chat = AnthropicMessages.ToChat((JsonObject)JsonNode.Parse("""
            {"model":"m","max_tokens":100,"system":[{"type":"text","text":"Be brief."}],"stop_sequences":["END"],"thinking":{"type":"enabled","budget_tokens":1000},
             "tools":[{"name":"read","description":"Reads a file","input_schema":{"type":"object","properties":{"path":{"type":"string"}}}}],"tool_choice":{"type":"any"},
             "messages":[
               {"role":"user","content":[{"type":"text","text":"Look"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AA=="}}]},
               {"role":"assistant","content":[{"type":"text","text":"Reading."},{"type":"tool_use","id":"t1","name":"read","input":{"path":"a.txt"}}]},
               {"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"hello"},{"type":"text","text":"And?"}]}]}
            """)!);
        var m = chat["messages"]!.AsArray();
        Assert.Equal(("system", "Be brief."), (m[0]!["role"]!.GetValue<string>(), m[0]!["content"]!.GetValue<string>()));
        Assert.Equal("data:image/png;base64,AA==", m[1]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal(("read", """{"path":"a.txt"}"""), (m[2]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>(), m[2]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>()));
        Assert.Equal(("tool", "t1", "hello"), (m[3]!["role"]!.GetValue<string>(), m[3]!["tool_call_id"]!.GetValue<string>(), m[3]!["content"]!.GetValue<string>()));
        Assert.Equal("And?", m[4]!["content"]!.GetValue<string>());
        Assert.Equal("required", chat["tool_choice"]!.GetValue<string>());
        Assert.Equal("Reads a file", chat["tools"]![0]!["function"]!["description"]!.GetValue<string>());
        Assert.Equal("END", chat["stop"]![0]!.GetValue<string>());
        Assert.True(chat["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        var server = Assert.Throws<AdapterException>(() => AnthropicMessages.ToChat((JsonObject)JsonNode.Parse("""{"model":"m","messages":[],"tools":[{"type":"web_search_20250305","name":"web_search"}]}""")!));
        Assert.Equal(400, server.Status);
    }

    [Fact]
    public void An_answer_and_its_stream_come_back_in_Anthropics_shapes()
    {
        var message = AnthropicMessages.FromChat((JsonObject)JsonNode.Parse("""
            {"choices":[{"finish_reason":"tool_calls","message":{"reasoning_content":"hm","content":"Let me look.","tool_calls":[{"id":"c1","function":{"name":"read","arguments":"{\"path\":\"a\"}"}}]}}],
             "usage":{"prompt_tokens":100,"completion_tokens":7,"prompt_tokens_details":{"cached_tokens":60}}}
            """)!, "m");
        Assert.Equal(["thinking", "text", "tool_use"], message["content"]!.AsArray().Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("tool_use", message["stop_reason"]!.GetValue<string>());
        Assert.Equal((40, 7, 60), (message["usage"]!["input_tokens"]!.GetValue<int>(), message["usage"]!["output_tokens"]!.GetValue<int>(), message["usage"]!["cache_read_input_tokens"]!.GetValue<int>()));

        var stream = new AnthropicMessages.Events("m");
        var said = stream.Feed(Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"hm\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"read\",\"arguments\":\"{\\\"p\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\":1}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":3}}\n\ndata: [DONE]\n\n"));
        var events = said.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(e => e.Split('\n')[0][7..]).ToList();
        Assert.Equal(["message_start", "content_block_start", "content_block_delta", "content_block_stop", "content_block_start", "content_block_delta", "content_block_stop",
            "content_block_start", "content_block_delta", "content_block_delta", "content_block_stop", "message_delta", "message_stop"], events);
        Assert.Contains("\"stop_reason\":\"tool_use\"", said, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"input_json_delta\"", said, StringComparison.Ordinal);
        Assert.Equal("", stream.End());
    }

    [Fact]
    public async Task Claude_Codes_requests_are_answered_with_an_x_api_key_in_both_ways_and_counted()
    {
        await using var f = app.Create(app.ConnectionStringFor("claude_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?> { ["Gateway:Mode"] = "own" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "cc" + Guid.NewGuid().ToString("N")[..8];
        var key = (await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }))).GetProperty("apiKey").GetString()!;
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
        async Task<(HttpStatusCode, string)> SendAsync(string path, string body, string? apiKey = null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("x-api-key", apiKey ?? key);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            var res = await client.SendAsync(req);
            return (res.StatusCode, await res.Content.ReadAsStringAsync());
        }

        var (status, whole) = await SendAsync("/v1/messages", """{"model":"Qwen3.8-Flash-Next","max_tokens":50,"messages":[{"role":"user","content":"Say hi"}]}""");
        Assert.Equal(HttpStatusCode.OK, status);
        var message = JsonNode.Parse(whole)!;
        Assert.Equal(("message", "end_turn"), (message["type"]!.GetValue<string>(), message["stop_reason"]!.GetValue<string>()));
        Assert.Contains(message["content"]!.AsArray(), b => b!["type"]!.GetValue<string>() == "text" && b["text"]!.GetValue<string>().StartsWith("Answer to:", StringComparison.Ordinal));
        Assert.Equal(40, message["usage"]!["cache_read_input_tokens"]!.GetValue<int>());

        var (streamed, events) = await SendAsync("/messages", """{"model":"Qwen3.8-Flash-Next","max_tokens":50,"stream":true,"messages":[{"role":"user","content":"Say hi"}]}""");
        Assert.Equal(HttpStatusCode.OK, streamed);
        Assert.StartsWith("event: message_start", events, StringComparison.Ordinal);
        Assert.Contains("event: content_block_delta", events, StringComparison.Ordinal);
        Assert.EndsWith("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n", events, StringComparison.Ordinal);

        var (counted, count) = await SendAsync("/v1/messages/count_tokens", """{"model":"Qwen3.8-Flash-Next","messages":[{"role":"user","content":"How many tokens is this?"}]}""");
        Assert.Equal(HttpStatusCode.OK, counted);
        Assert.True(JsonNode.Parse(count)!["input_tokens"]!.GetValue<int>() > 0);

        var (refused, error) = await SendAsync("/v1/messages", """{"model":"Qwen3.8-Flash-Next","max_tokens":5,"messages":[{"role":"user","content":"x"}]}""", "sk-nobody");
        Assert.Equal(HttpStatusCode.Unauthorized, refused);
        Assert.Equal(("error", "authentication_error"), (JsonNode.Parse(error)!["type"]!.GetValue<string>(), JsonNode.Parse(error)!["error"]!["type"]!.GetValue<string>()));
        var (server, why) = await SendAsync("/v1/messages", """{"model":"Qwen3.8-Flash-Next","max_tokens":5,"messages":[{"role":"user","content":"x"}],"tools":[{"type":"web_search_20250305","name":"web_search"}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, server);
        Assert.Contains("invalid_request_error", why, StringComparison.Ordinal);
    }
}
