using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>The admins' messages (the Arena's broadcasts) said under the terminal's banner.</summary>
public sealed class BroadcastTerminalTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    [Fact]
    public async Task The_chat_says_the_broadcasts_on_now_as_it_starts()
    {
        _mcp.Chats.Broadcasts = new JsonArray(
            new JsonObject { ["id"] = "b1", ["title"] = "Arena restarts at 22:00", ["body"] = "Save your work.", ["level"] = "warning", ["dismissable"] = true },
            new JsonObject { ["id"] = "b2", ["title"] = "New models are in", ["level"] = "info", ["dismissable"] = true });
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("/exit\n", "chat"));
        Assert.Contains("From the admins: Arena restarts at 22:00 (Save your work.)", h.Out + h.Err, StringComparison.Ordinal);
        Assert.Contains("From the admins: New models are in", h.Out + h.Err, StringComparison.Ordinal);
    }
}
