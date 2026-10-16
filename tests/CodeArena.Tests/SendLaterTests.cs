using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Alone: it shortens how long after the start the sessions Arena lacks are sent, for every run in this process.</summary>
[CollectionDefinition(nameof(SendLaterCollection), DisableParallelization = true)]
public sealed class SendLaterCollection;

/// <summary>What Arena lacks of the earlier sessions, every folder's, goes in the background as Code Arena starts.</summary>
[Collection(nameof(SendLaterCollection))]
public sealed class SendLaterTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public SendLaterTests() => _gateway.Answer = req => Reply.Say($"answer to {FakeGateway.Last(req)}");

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    [Fact]
    public async Task At_the_start_what_Arena_lacks_of_every_folder_s_sessions_is_sent_in_the_background_and_said_once()
    {
        var before = Runtime.SendLaterAfter;
        Runtime.SendLaterAfter = TimeSpan.FromMilliseconds(200);
        try
        {
            using var h = new Harness(_gateway, _mcp, c => c["syncChats"] = false);
            Assert.Equal(0, await h.Run("", "-p", "in this folder"));
            var other = Path.Combine(h.Root, "other");
            Directory.CreateDirectory(other);
            h.Cwd = other;
            Assert.Equal(0, await h.Run("", "-p", "in another folder"));
            h.Cwd = null;
            var config = JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!.AsObject();
            config["syncChats"] = true;
            File.WriteAllText(h.Paths.ConfigFile, config.ToJsonString());

            var typed = new TypedLines();
            var output = new StringWriter();
            var screen = TextWriter.Synchronized(output);
            var env = new CliEnv
            {
                In = typed, Out = screen, Err = TextWriter.Synchronized(new StringWriter()), Env = k => h.Env.GetValueOrDefault(k),
                Cwd = h.Work, Paths = h.Paths, InTerminal = true,
            };
            var run = Task.Run(() => Cli.RunAsync(["chat"], env));
            try
            {
                var until = DateTime.UtcNow + Deadline;
                while (_mcp.Chats.All.Count < 2 || _mcp.Chats.All.Any(c => c.Messages.Count < 2))
                {
                    Assert.True(DateTime.UtcNow < until, "The earlier sessions did not reach Arena.");
                    await Task.Delay(50);
                }
                Assert.Equal(["in this folder", "in another folder"], _mcp.Chats.All.Select(c => c.Messages[0]["content"]!.GetValue<string>()).Order().Reverse());
                Assert.Contains(_mcp.Chats.All, c => c.Place == other);
                // Said once, at the prompt.
                await Task.Delay(500);
                typed.Add("/sync");
            }
            finally
            {
                typed.Add("/exit");
                Assert.Equal(0, await run.WaitAsync(Deadline));
            }
            string shown;
            lock (screen)
            {
                shown = output.ToString();
            }
            Assert.Equal(1, shown.Split("Sent to Arena in the background: 2 earlier sessions it did not have.").Length - 1);
            Assert.All(SessionStore.List(h.Paths.SessionsDir, null, 10), s => Assert.False(s.Unsent));
        }
        finally
        {
            Runtime.SendLaterAfter = before;
        }
    }
}
