using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// Code Arena without an Arena: a provider of the person's own answers (their key), or the IDE works without a model and
/// says how to get one; a provider added there answers from then on. A terminal run with neither says what to do.
/// </summary>
public sealed class SignedOutTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeGateway _other = new() { Models = ["deep-chat"] };
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _other.Dispose();
        _mcp.Dispose();
    }

    private static void SignedOut(JsonObject c)
    {
        c.Remove("url");
        c.Remove("gateway");
        c.Remove("apiKey");
    }

    [Fact]
    public async Task Signed_out_a_providers_model_answers_and_nothing_goes_to_an_Arena()
    {
        _other.Answer = _ => Reply.Say("From my own provider.");
        using var h = new Harness(_gateway, _mcp, c =>
        {
            SignedOut(c);
            c["providers"] = new JsonObject { ["local"] = new JsonObject { ["kind"] = "openai", ["url"] = _other.Url, ["apiKey"] = FakeGateway.Key } };
        });
        Assert.Equal(0, await h.Run("", "-p", "hello"));
        Assert.Contains("From my own provider.", h.Out);
        Assert.Equal("deep-chat", Assert.Single(_other.Requests).Str("model"));
        Assert.Empty(_gateway.Requests);
    }

    [Fact]
    public async Task Signed_out_with_no_provider_the_terminal_says_how_to_go_on()
    {
        using var h = new Harness(_gateway, _mcp, SignedOut);
        Assert.Equal(1, await h.Run("", "-p", "hello"));
        var said = h.Out + h.Err;
        Assert.Contains("code-arena login", said, StringComparison.Ordinal);
        Assert.Contains("/providers add", said, StringComparison.Ordinal);
        Assert.Contains("open the IDE", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signed_out_the_IDE_works_without_a_model_and_a_provider_added_there_answers()
    {
        _other.Answer = _ => Reply.Say("Now with a model.");
        using var h = new Harness(_gateway, _mcp, SignedOut);
        h.Write("notes.txt", "kept\n");
        await using var web = await WebRun.StartAsync(h);
        var state = await web.GetJsonAsync("/api/state");
        Assert.True(state["signedOut"]!.GetValue<bool>());
        Assert.True(state["noModel"]!.GetValue<bool>());
        // The editor works.
        Assert.Equal("kept\n", (await web.GetJsonAsync("/api/file?path=notes.txt"))["text"]!.GetValue<string>());
        Assert.False((await web.GetJsonAsync("/api/account"))["signedIn"]!.GetValue<bool>());

        // A turn says how to get a model.
        var events = await (await web.SendAsync("hello")).RestAsync();
        Assert.Contains(events, e => e.ToJsonString().Contains("No model to answer", StringComparison.Ordinal));

        var added = await web.PostAsync("/api/providers", new JsonObject { ["name"] = "local", ["url"] = _other.Url, ["kind"] = "openai", ["key"] = FakeGateway.Key });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        state = await web.GetJsonAsync("/api/state");
        Assert.Equal("local:deep-chat", state["model"]!.GetValue<string>());
        Assert.False(state["noModel"]!.GetValue<bool>());
        await (await web.SendAsync("hello again")).RestAsync();
        Assert.Contains(_other.Requests, r => r.Str("model") == "deep-chat");
        Assert.Empty(_gateway.Requests);
    }
}
