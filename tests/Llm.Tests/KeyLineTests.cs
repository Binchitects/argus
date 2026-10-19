using Llm.Api.Chat;
using Llm.Api.Gateway;
using Microsoft.Extensions.Options;

namespace Llm.Tests;

/// <summary>
/// An API key's requests above Chat:ApiRequestsPerKey wait their turn, in order, instead of being refused:
/// a coding agent's parallel requests are all answered. Only past Chat:ApiQueueTimeout is one refused.
/// </summary>
public sealed class KeyLineTests
{
    private sealed class Monitor(ChatOptions value) : IOptionsMonitor<ChatOptions>
    {
        public ChatOptions CurrentValue => value;
        public ChatOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ChatOptions, string?> listener) => null;
    }

    [Fact]
    public async Task A_keys_requests_beyond_its_places_wait_in_order_and_go_on_as_one_ends()
    {
        var line = new KeyLine(new Monitor(new ChatOptions { ApiRequestsPerKey = 2, ApiQueueTimeout = TimeSpan.FromSeconds(10) }));
        var a = await line.EnterAsync("sk-a", CancellationToken.None);
        var b = await line.EnterAsync("sk-a", CancellationToken.None);
        Assert.NotNull(a);
        Assert.NotNull(b);
        var order = new List<int>();
        var third = Task.Run(async () => { var p = await line.EnterAsync("sk-a", CancellationToken.None); lock (order) { order.Add(3); } return p; });
        await Task.Delay(100);
        var fourth = Task.Run(async () => { var p = await line.EnterAsync("sk-a", CancellationToken.None); lock (order) { order.Add(4); } return p; });
        await Task.Delay(200);
        Assert.False(third.IsCompleted);
        Assert.Equal(2, line.WaitingFor("sk-a"));
        // Another key has its own places.
        Assert.NotNull(await line.EnterAsync("sk-b", CancellationToken.None));
        a!.Dispose();
        Assert.NotNull(await third.WaitAsync(TimeSpan.FromSeconds(5)));
        b!.Dispose();
        Assert.NotNull(await fourth.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal([3, 4], order);
    }

    [Fact]
    public async Task A_request_that_waits_past_the_timeout_is_refused_and_none_is_limited_at_zero()
    {
        var line = new KeyLine(new Monitor(new ChatOptions { ApiRequestsPerKey = 1, ApiQueueTimeout = TimeSpan.FromMilliseconds(200) }));
        using var held = await line.EnterAsync("sk-a", CancellationToken.None);
        Assert.Null(await line.EnterAsync("sk-a", CancellationToken.None));
        var open = new KeyLine(new Monitor(new ChatOptions { ApiRequestsPerKey = 0 }));
        for (var i = 0; i < 20; i++)
        {
            Assert.NotNull(await open.EnterAsync("sk-a", CancellationToken.None));
        }
    }
}

/// <summary>An agent's session through an API key, as the engine's slots know it: the same start, the same session.</summary>
public sealed class AgentSessionTests
{
    private static System.Text.Json.Nodes.JsonObject Request(string model, params string[] contents) =>
        new() { ["model"] = model, ["messages"] = new System.Text.Json.Nodes.JsonArray([.. contents.Select((c, i) => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["role"] = i == 0 ? "system" : "user", ["content"] = c })]) };

    [Fact]
    public void Turns_of_one_session_share_it_and_another_start_key_or_model_is_another()
    {
        var first = AnswerCacheEndpoints.SessionOf("sk-a", Request("m", "You are a coding agent.", "Fix the build"));
        var later = AnswerCacheEndpoints.SessionOf("sk-a", Request("m", "You are a coding agent.", "Fix the build", "Done. What next?", "Run the tests"));
        Assert.NotNull(first);
        Assert.Equal(first, later);
        Assert.NotEqual(first, AnswerCacheEndpoints.SessionOf("sk-a", Request("m", "You are a coding agent.", "Write the docs")));
        Assert.NotEqual(first, AnswerCacheEndpoints.SessionOf("sk-b", Request("m", "You are a coding agent.", "Fix the build")));
        Assert.NotEqual(first, AnswerCacheEndpoints.SessionOf("sk-a", Request("n", "You are a coding agent.", "Fix the build")));
        // One message is no session yet.
        Assert.Null(AnswerCacheEndpoints.SessionOf("sk-a", Request("m", "Hello")));
    }
}
