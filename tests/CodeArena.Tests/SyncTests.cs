using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Sessions kept in step with the person's chats in Arena, both ways.</summary>
public sealed class SyncTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static string Role(JsonObject m) => m["role"]!.GetValue<string>();
    private static string Content(JsonObject m) => m["content"]!.ToString();

    private static JsonObject Said(string role, string content) => new() { ["role"] = role, ["content"] = content };

    [Fact]
    public async Task A_session_is_a_chat_in_Arena_and_what_the_web_adds_comes_in_at_the_next_turn()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => Reply.Say($"answer to {FakeGateway.Last(req)}");
        Assert.Equal(0, await h.Run("", "-p", "first question"));

        var chat = Assert.Single(_mcp.Chats.All);
        Assert.Equal(h.Work, chat.Place);
        Assert.Equal(["user", "assistant"], chat.Messages.Select(Role));
        Assert.Equal("answer to first question", Content(chat.Messages[1]));
        Assert.Equal($"{chat.Ref}:1", chat.Messages[1]["ref"]!.GetValue<string>());

        // The person carries on in the web chat; the next turn here hears it first.
        chat.Messages.Add(Said("user", "asked on the web"));
        chat.Messages.Add(Said("assistant", "answered on the web"));
        Assert.Equal(0, await h.Run("", "-p", "second question", "--continue"));
        var sent = _gateway.Requests[^1]["messages"]!.AsArray().Skip(1).Select(m => m!["content"]!.ToString()).ToList();
        Assert.Equal(["first question", "answer to first question", "asked on the web", "answered on the web", "second question"], sent);
        Assert.Equal(6, chat.Messages.Count);
        Assert.Equal("answer to second question", Content(chat.Messages[5]));
        Assert.Single(_mcp.Chats.All);

        // Read back later, the session has the web's messages once, and sends nothing twice.
        Assert.Equal(0, await h.Run("", "-p", "third", "--continue"));
        sent = [.. _gateway.Requests[^1]["messages"]!.AsArray().Skip(1).Select(m => m!["content"]!.ToString())];
        Assert.Equal(1, sent.Count(s => s == "asked on the web"));
        Assert.Equal(8, chat.Messages.Count);
    }

    [Fact]
    public async Task What_Arena_missed_while_down_is_sent_later_once()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => Reply.Say($"answer to {FakeGateway.Last(req)}");
        _mcp.Chats.Status = 503;
        Assert.Equal(0, await h.Run("", "-p", "while Arena is down"));
        Assert.Empty(_mcp.Chats.All);

        _mcp.Chats.Status = 0;
        Assert.Equal(0, await h.Run("", "-p", "now it is up", "--continue"));
        var chat = Assert.Single(_mcp.Chats.All);
        Assert.Equal(["while Arena is down", "answer to while Arena is down", "now it is up", "answer to now it is up"], chat.Messages.Select(Content));
    }

    [Fact]
    public async Task Messages_Arena_had_though_its_answer_was_lost_are_not_sent_twice()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => Reply.Say($"answer to {FakeGateway.Last(req)}");
        Assert.Equal(0, await h.Run("", "-p", "one"));
        var chat = Assert.Single(_mcp.Chats.All);
        // As if the answers to the sends were lost: the session's file says Arena has none of its messages.
        var file = Directory.GetFiles(h.Paths.SessionsDir, "*.jsonl").Single();
        File.AppendAllText(file, new JsonObject { ["type"] = "sync", ["conversation"] = chat.Id, ["server"] = 0, ["pushed"] = 0 }.ToJsonString() + "\n");

        Assert.Equal(0, await h.Run("", "-p", "two", "--continue"));
        Assert.Equal(["one", "answer to one", "two", "answer to two"], chat.Messages.Select(Content));
        var sent = _gateway.Requests[^1]["messages"]!.AsArray().Skip(1).Select(m => m!["content"]!.ToString()).ToList();
        Assert.Equal(["one", "answer to one", "two"], sent);
    }

    [Fact]
    public async Task A_chat_from_the_web_is_continued_here_and_what_is_added_goes_back_to_it()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => Reply.Say($"answer to {FakeGateway.Last(req)}");
        var web = _mcp.Chats.Add("Plan the release", Said("user", "Plan the release"), Said("assistant", "Step one: freeze."));

        Assert.Equal(0, await h.Run("", "-p", "and then?", "--web", web.Id));
        var sent = _gateway.Requests[^1]["messages"]!.AsArray().Skip(1).Select(m => m!["content"]!.ToString()).ToList();
        Assert.Equal(["Plan the release", "Step one: freeze.", "and then?"], sent);
        Assert.Equal(["Plan the release", "Step one: freeze.", "and then?", "answer to and then?"], web.Messages.Select(Content));
        Assert.Single(_mcp.Chats.All);
    }

    [Fact]
    public async Task The_web_chats_are_listed_and_one_is_continued_by_its_number()
    {
        using var h = new Harness(_gateway, _mcp);
        _mcp.Chats.Add("Older chat", Said("user", "old"));
        var newest = _mcp.Chats.Add("Newest chat", Said("user", "new one"), Said("assistant", "sure"));
        Assert.Equal(0, await h.Run("/web\n/web 1\n/sync\n/exit\n", "chat"));
        Assert.Contains("Newest chat", h.Out);
        Assert.Contains("Older chat", h.Out);
        Assert.Contains("Continuing the chat from Arena", h.Out);
        Assert.Contains($"Kept in step with the chat", h.Out);
        Assert.Contains(newest.Id, h.Out);
    }

    [Fact]
    public async Task A_chat_deleted_in_Arena_ends_the_sync_and_the_session_carries_on()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => Reply.Say($"answer to {FakeGateway.Last(req)}");
        Assert.Equal(0, await h.Run("", "-p", "one"));
        _mcp.Chats.All.Clear();
        Assert.Equal(0, await h.Run("", "-p", "two", "--continue"));
        Assert.Empty(_mcp.Chats.All);
        Assert.Contains("\"type\":\"sync\",\"conversation\":null", File.ReadAllText(Directory.GetFiles(h.Paths.SessionsDir, "*.jsonl").Single()));
    }

    [Fact]
    public async Task With_syncChats_off_nothing_goes_to_Arena()
    {
        using var h = new Harness(_gateway, _mcp, c => c["syncChats"] = false);
        Assert.Equal(0, await h.Run("", "-p", "private"));
        Assert.Empty(_mcp.Chats.All);
    }
}
