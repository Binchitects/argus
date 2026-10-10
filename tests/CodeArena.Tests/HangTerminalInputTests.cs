using System.Text;
using System.Text.RegularExpressions;

namespace CodeArena.Tests;

/// <summary>
/// One terminal's input, as the real console has it (Program.cs): the prompt's line editor reads it key by
/// key (Console.ReadKey) and a question reads it a line at a time (Console.In.ReadLine), both from the same
/// keys. What is typed while no one reads waits, as a terminal holds it, for whichever reader comes next.
/// </summary>
internal sealed class SharedTerminal : TextReader, IKeyboard
{
    private readonly object _gate = new();
    private readonly Queue<char> _chars = new();
    private bool _closed;

    public int Width => 200;
    public int Height => 1000;
    public Action? Suspend => null;

    /// <summary>What the person types; \n is Enter.</summary>
    public void Type(string text)
    {
        lock (_gate)
        {
            foreach (var c in text)
            {
                _chars.Enqueue(c);
            }
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>The end of input: every read waiting returns.</summary>
    public void End()
    {
        lock (_gate)
        {
            _closed = true;
            Monitor.PulseAll(_gate);
        }
    }

    private char? Take()
    {
        lock (_gate)
        {
            while (_chars.Count == 0 && !_closed)
            {
                Monitor.Wait(_gate);
            }
            return _chars.Count > 0 ? _chars.Dequeue() : null;
        }
    }

    public bool KeyAvailable
    {
        get
        {
            lock (_gate)
            {
                return _chars.Count > 0;
            }
        }
    }

    public bool WaitForKey(TimeSpan wait)
    {
        lock (_gate)
        {
            if (_chars.Count == 0 && !_closed)
            {
                Monitor.Wait(_gate, wait);
            }
            return _chars.Count > 0 || _closed;
        }
    }

    public ConsoleKeyInfo? ReadKey() => Take() switch
    {
        null => null,
        '\n' => new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
        var c => new ConsoleKeyInfo(c.Value, char.IsAsciiLetter(c.Value) ? ConsoleKey.A + (char.ToUpperInvariant(c.Value) - 'A') : 0, char.IsAsciiLetterUpper(c.Value), false, false),
    };

    public IDisposable CaptureCtrlC() => new Nothing();

    public override string? ReadLine()
    {
        var line = new StringBuilder();
        while (true)
        {
            if (Take() is not { } c)
            {
                return line.Length > 0 ? line.ToString() : null;
            }
            if (c == '\n')
            {
                return line.ToString();
            }
            line.Append(c);
        }
    }

    public override int Read() => Take() is { } c ? c : -1;

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>Output written from several threads, read while the program runs.</summary>
internal sealed class LockedWriter : TextWriter
{
    private readonly StringBuilder _text = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_text)
        {
            _text.Append(value);
        }
    }

    public override void Write(string? value)
    {
        lock (_text)
        {
            _text.Append(value);
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_text)
        {
            _text.Append(buffer, index, count);
        }
    }

    public override string ToString()
    {
        lock (_text)
        {
            return _text.ToString();
        }
    }
}

/// <summary>
/// "The arena code stucks in middle of work and second promt does not perform", in the terminal: what the
/// person types while a turn runs, or waits at a question, must reach the reader it is meant for. Each test
/// has a deadline, so a stall fails it instead of hanging.
/// </summary>
public sealed class HangTerminalInputTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private const string Edit = """{"path":"a.txt","old_string":"old","new_string":"new"}""";

    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    /// <summary>code-arena chat in a terminal with the prompt's line editor, on a thread of its own (its reads block).</summary>
    private static (Task<int> Run, CliEnv Env, LockedWriter Out) Start(Harness h, SharedTerminal terminal)
    {
        var output = new LockedWriter();
        var env = new CliEnv
        {
            In = terminal,
            Out = output,
            Err = output,
            Env = k => h.Env.GetValueOrDefault(k),
            Cwd = h.Work,
            Paths = h.Paths,
            InTerminal = true,
            Keys = terminal,
        };
        return (Task.Run(() => Cli.RunAsync(["chat"], env)), env, output);
    }

    /// <summary>Whether the condition held within the time given.</summary>
    private static async Task<bool> Within(TimeSpan limit, Func<bool> condition)
    {
        var deadline = Task.Delay(limit);
        while (!condition())
        {
            if (await Task.WhenAny(deadline, Task.Delay(50)) == deadline)
            {
                return condition();
            }
        }
        return true;
    }

    /// <summary>The end of input, so whatever still reads returns and the program ends.</summary>
    private static async Task Finish(SharedTerminal terminal, Task<int> run)
    {
        terminal.End();
        await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
    }

    private bool Sent(string text) => _gateway.Requests.Any(r => FakeGateway.Last(r) == text);

    private static int Count(string text, string what) => Regex.Matches(text, Regex.Escape(what)).Count;

    private static string Plain(LockedWriter output) => Regex.Replace(output.ToString(), @"\e\[[0-9;?]*[A-Za-z]", "");

    private static string Tail(string text, int max = 1500) => text.Length <= max ? text : "…" + text[^max..];

    [Fact]
    public async Task A_prompt_typed_while_the_model_works_is_not_taken_as_the_answer_to_its_question()
    {
        using var h = new Harness(_gateway, _mcp);
        var file = h.Write("a.txt", "old\n");
        var terminal = new SharedTerminal();
        _gateway.Answer = req => FakeGateway.Last(req) switch
        {
            "second prompt" => Reply.Say("Done with the second prompt."),
            "edit it" => TypedMeanwhile(terminal, "second prompt\n", Reply.Call(("edit_file", Edit))),
            _ when FakeGateway.HasToolResults(req) => Reply.Say("Edited."),
            _ => Reply.Say("?"),
        };
        terminal.Type("edit it\n");
        var (run, _, output) = Start(h, terminal);
        try
        {
            // The person types their next message while the model works (as other coding agents queue it), then
            // answers the question they see: y.
            Assert.True(await Within(Deadline, () => Plain(output).Contains("Allow edit_file?", StringComparison.Ordinal)), "No question within 30 s:\n" + Tail(Plain(output)));
            terminal.Type("y\n");

            var sent = await Within(Deadline, () => Sent("second prompt"));
            var said = Plain(output);
            Assert.True(sent,
                $"Within 30 s the second prompt never reached the model. The question was asked {Count(said, "Allow edit_file?")} time(s); " +
                $"a.txt is now {File.ReadAllText(file).Trim()}; the program {(run.IsCompleted ? "ended" : "still runs")}; " +
                $"the model got: [{string.Join(" | ", _gateway.Requests.Select(r => Fmt.OneLine(FakeGateway.Last(r), 40)))}]\n{Tail(said)}");
            // The prompt typed ahead was kept for after the turn, and the y answered the question: the edit was made.
            Assert.Contains("Taken when this turn ends: second prompt", said);
            Assert.Equal("new", File.ReadAllText(file).Trim());
        }
        finally
        {
            await Finish(terminal, run);
        }
    }

    [Fact]
    public async Task Ctrl_C_at_a_question_stops_the_turn_and_the_next_prompt_is_sent()
    {
        using var h = new Harness(_gateway, _mcp);
        var file = h.Write("a.txt", "old\n");
        var terminal = new SharedTerminal();
        _gateway.Answer = req => FakeGateway.Last(req) switch
        {
            "second prompt" => Reply.Say("Done with the second prompt."),
            "edit it" => Reply.Call(("edit_file", Edit)),
            _ when FakeGateway.HasToolResults(req) => Reply.Say("Edited."),
            _ => Reply.Say("?"),
        };
        terminal.Type("edit it\n");
        var (run, env, output) = Start(h, terminal);
        try
        {
            Assert.True(await Within(Deadline, () => Plain(output).Contains("Allow edit_file?", StringComparison.Ordinal)), "No question within 30 s:\n" + Tail(Plain(output)));

            // Ctrl+C, as Program.cs's CancelKeyPress handler takes it (on Linux and macOS the read goes on; only
            // Windows cuts it short), then, the turn looking stuck, the next message.
            env.Cancel.Press();
            var stopped = await Within(TimeSpan.FromSeconds(10), () => Plain(output).Contains("Stopped.", StringComparison.Ordinal));
            terminal.Type("second prompt\n");
            var sent = await Within(Deadline, () => Sent("second prompt"));

            var said = Plain(output);
            Assert.True(stopped && sent,
                $"Ctrl+C at the question {(stopped ? "stopped" : "did not stop")} the turn within 10 s, and the second prompt " +
                $"{(sent ? "was" : "was never")} sent within 30 s. The question was asked {Count(said, "Allow edit_file?")} time(s); " +
                $"a.txt is now {File.ReadAllText(file).Trim()}; the program {(run.IsCompleted ? "ended" : "still runs")}; " +
                $"the model got: [{string.Join(" | ", _gateway.Requests.Select(r => Fmt.OneLine(FakeGateway.Last(r), 40)))}]\n{Tail(said)}");
        }
        finally
        {
            await Finish(terminal, run);
        }
    }

    [Fact]
    public async Task A_prompt_typed_while_the_model_works_with_no_question_is_sent_after_the_turn()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write("a.txt", "old\n");
        var terminal = new SharedTerminal();
        _gateway.Answer = req => FakeGateway.Last(req) switch
        {
            "second prompt" => Reply.Say("Done with the second prompt."),
            "read it" => TypedMeanwhile(terminal, "second prompt\n", Reply.Call(("read_file", """{"path":"a.txt"}"""))),
            _ when FakeGateway.HasToolResults(req) => Reply.Say("Read."),
            _ => Reply.Say("?"),
        };
        terminal.Type("read it\n");
        var (run, _, output) = Start(h, terminal);
        try
        {
            var sent = await Within(Deadline, () => Sent("second prompt"));
            Assert.True(sent, "Within 30 s the second prompt never reached the model:\n" + Tail(Plain(output)));
        }
        finally
        {
            await Finish(terminal, run);
        }
    }

    [Fact]
    public async Task The_prompt_after_a_turn_that_waited_for_a_long_command_is_sent()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp, c => c["mode"] = "yolo");
        var terminal = new SharedTerminal();
        _gateway.Answer = req => FakeGateway.Last(req) switch
        {
            "second prompt" => Reply.Say("Done with the second prompt."),
            var last when last.Contains("job 1 has ended", StringComparison.Ordinal) => Reply.Say("Build done."),
            _ when FakeGateway.HasToolResults(req) => Reply.Say("It builds; I wait for it."),
            _ => Reply.Call(("run_shell", """{"command":"sleep 2; echo built","no_time_limit":true}""")),
        };
        terminal.Type("build it\n");
        var (run, _, output) = Start(h, terminal);
        try
        {
            Assert.True(await Within(Deadline, () => Plain(output).Contains("Build done.", StringComparison.Ordinal)), "The turn did not end within 30 s:\n" + Tail(Plain(output)));
            // At the prompt again, the person types the next message.
            terminal.Type("second prompt\n");
            var sent = await Within(Deadline, () => Sent("second prompt"));
            Assert.True(sent,
                $"Within 30 s the second prompt never reached the model; the model got: [{string.Join(" | ", _gateway.Requests.Select(r => Fmt.OneLine(FakeGateway.Last(r), 40)))}]\n{Tail(Plain(output))}");
        }
        finally
        {
            await Finish(terminal, run);
        }
    }

    /// <summary>The person types while the model is answering: before its reply (a tool call) arrives.</summary>
    private static Reply TypedMeanwhile(SharedTerminal terminal, string text, Reply reply)
    {
        terminal.Type(text);
        return reply;
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }
}
