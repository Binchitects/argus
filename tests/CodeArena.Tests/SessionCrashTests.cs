using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// A session survives Code Arena stopping at any moment: each line on the disk before it returns, a line cut short by a
/// power-off never takes the next one with it, zeros left before a line are skipped, and an answer half-written is kept
/// as the answer it got to (marked cut short) the next time the session opens.
/// </summary>
[Collection(nameof(SessionStoreDraftCollection))]
public sealed class SessionCrashTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ca-crash-" + Guid.NewGuid().ToString("N"));

    public SessionCrashTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SessionStore.DraftLeft = TimeSpan.FromSeconds(30);
        Directory.Delete(_dir, recursive: true);
    }

    private static string Said(JsonObject m) => m["content"]!.GetValue<string>();

    [Fact]
    public void A_line_cut_short_by_a_power_off_and_zeros_after_it_lose_nothing_written_after()
    {
        var store = SessionStore.Create(_dir, "/work/project", "model-a");
        store.Message(new JsonObject { ["role"] = "user", ["content"] = "first" });
        // The machine went off mid-line: half a line and the zeros the disk left.
        File.AppendAllText(store.File, "{\"type\":\"message\",\"n\":1,\"message\":{\"role\":\"assistant\",\"con");
        File.AppendAllText(store.File, new string('\0', 64));
        var again = SessionStore.Open(Path.Combine(_dir, Path.GetFileName(store.File)));
        again.Message(new JsonObject { ["role"] = "user", ["content"] = "after the power came back" });
        var read = SessionStore.Load(store.File, whole: true);
        Assert.Equal(["first", "after the power came back"], read.Messages.Select(Said));
        Assert.Equal(read.Messages.Select(Said), SessionStore.Load(store.File).Messages.Select(Said));
        // A line with zeros before it (left by an earlier power-off) is read.
        File.AppendAllText(store.File, new string('\0', 16) + "{\"type\":\"message\",\"n\":9,\"message\":{\"role\":\"user\",\"content\":\"behind zeros\"}}\n");
        Assert.Equal("behind zeros", Said(SessionStore.Load(store.File, whole: true).Messages[^1]));
    }

    [Fact]
    public void An_answer_half_written_when_Code_Arena_stopped_is_the_answer_it_got_to_at_the_next_open_and_one_written_whole_leaves_none()
    {
        var store = SessionStore.Create(_dir, "/work/project", "model-a");
        store.Message(new JsonObject { ["role"] = "user", ["content"] = "Explain the parser" });
        store.Draft("The parser reads tokens", "Look at Parse first");
        Assert.True(File.Exists(store.DraftFile));
        // A live Code Arena writes it every few seconds: a fresh one is not taken.
        Assert.False(store.Recover());
        // Killed there; opened again later.
        SessionStore.DraftLeft = TimeSpan.Zero;
        Assert.True(store.Recover());
        var answer = SessionStore.Load(store.File, whole: true).Messages[^1];
        Assert.Equal("assistant", answer["role"]!.GetValue<string>());
        Assert.Equal("The parser reads tokens\n\n" + SessionStore.CutShort, Said(answer));
        Assert.Equal("Look at Parse first", answer["reasoning_content"]!.GetValue<string>());
        Assert.False(File.Exists(store.DraftFile));
        // Arena is sent it as any answer.
        Assert.Equal(2, store.Read(d => d.Waiting));

        // An answer that ends is written whole and its draft goes; a turn that failed drops its draft.
        store.Draft("partial", "");
        store.Message(new JsonObject { ["role"] = "assistant", ["content"] = "whole" });
        Assert.False(File.Exists(store.DraftFile));
        store.Draft("of a failed request", "");
        store.DropDraft();
        Assert.False(store.Recover());
    }
}

[CollectionDefinition(nameof(SessionStoreDraftCollection), DisableParallelization = true)]
public sealed class SessionStoreDraftCollection;
