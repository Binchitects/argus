using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>A long session keeps working when another sign-in on this machine replaced its key: the newer key saved is taken.</summary>
public sealed class KeyRefreshTests : IDisposable
{
    private readonly FakeGateway _gateway = new();

    public void Dispose() => _gateway.Dispose();

    [Fact]
    public async Task A_key_revoked_by_a_newer_sign_in_is_replaced_by_the_one_saved_and_the_turn_is_answered()
    {
        _gateway.Answer = _ => Reply.Say("still here");
        using var h = new Harness(_gateway);
        await using var web = await WebRun.StartAsync(h);
        await (await web.SendAsync("first")).RestAsync();

        // Another window signed in: this machine's key is a new one, the old one revoked, and config.json has the new one.
        const string newer = "sk-arena-newer-key-9876543210";
        _gateway.Accepted = newer;
        var config = JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!.AsObject();
        config["apiKey"] = newer;
        File.WriteAllText(h.Paths.ConfigFile, config.ToJsonString());

        var events = await (await web.SendAsync("second")).RestAsync();
        Assert.Contains(events, e => e["type"]?.ToString() == "done");
        Assert.Contains("still here", (await web.GetJsonAsync("/api/session"))["messages"]!.AsArray()[^1]!["content"]!.ToString(), StringComparison.Ordinal);
        Assert.Contains("Arena's key changed", web.Out + web.Err, StringComparison.Ordinal);

        // A key no one saved: the refusal stands, said as it was.
        _gateway.Accepted = "sk-someone-else";
        var refused = await (await web.SendAsync("third")).RestAsync();
        Assert.Contains(refused, e => e["type"]?.ToString() == "error" && e["message"]!.ToString().Contains("refused your API key", StringComparison.Ordinal));
    }
}
