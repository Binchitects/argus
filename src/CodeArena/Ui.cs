using System.Text;

namespace CodeArena;

/// <summary>A person's answer to "allow this?".</summary>
internal enum Approval { No, Yes, Always }

/// <summary>
/// The terminal: what the agent says (stdout), its progress and questions, and
/// the person's answers. ANSI colours when the output is a terminal (and
/// NO_COLOR is not set); the same text without them otherwise.
/// </summary>
internal sealed class Ui(TextReader input, TextWriter output, TextWriter error, bool color, bool canAsk)
{
    private const int MaxHeld = 400;
    private readonly object _gate = new();
    private readonly Queue<string> _held = new();
    private Spinner? _spinner;
    private bool _midLine;
    private bool _asking;
    private int _heldDropped;
    private Task<string?>? _reading;

    public TextReader In { get; } = input;
    public TextWriter Out { get; } = output;
    public TextWriter Err { get; } = error;
    public bool Color { get; } = color;
    /// <summary>Whether someone is there to answer (a terminal, or a test's scripted answers).</summary>
    public bool CanAsk { get; } = canAsk;
    /// <summary>One-shot runs: the answer alone on stdout, progress on stderr.</summary>
    public bool Quiet { get; init; }
    /// <summary>Whether the spinner may draw (stderr is a terminal).</summary>
    public bool Animate { get; init; }
    /// <summary>Reads a secret without echoing it, when a real terminal is there.</summary>
    public Func<string, string?>? SecretReader { get; init; }

    private TextWriter Progress => Quiet ? Err : Out;

    public string Paint(string code, string text) => Color && text.Length > 0 ? $"\e[{code}m{text}\e[0m" : text;
    public string Dim(string text) => Paint("2", text);
    public string Bold(string text) => Paint("1", text);
    public string Red(string text) => Paint("31", text);
    public string Green(string text) => Paint("32", text);
    public string Yellow(string text) => Paint("33", text);
    public string Blue(string text) => Paint("34", text);
    public string Magenta(string text) => Paint("35", text);
    public string Cyan(string text) => Paint("36", text);

    /// <summary>The answer's text as it streams (stdout, or nothing in a quiet run).</summary>
    public void Write(string text)
    {
        if (Quiet)
        {
            return;
        }
        lock (_gate)
        {
            StopSpinnerLocked();
            Out.Write(text);
            Out.Flush();
            _midLine = !text.EndsWith('\n');
            PrintHeldLocked();
        }
    }

    /// <summary>A line of progress: tool calls, notices. Starts on a fresh line.</summary>
    public void Line(string text = "")
    {
        lock (_gate)
        {
            StopSpinnerLocked();
            if (_midLine && !Quiet)
            {
                Out.WriteLine();
                _midLine = false;
            }
            PrintHeldLocked();
            Progress.WriteLine(text);
            Progress.Flush();
        }
    }

    /// <summary>
    /// A line from what runs in the background (a command's output, its end), from any thread: held
    /// back while a question waits for its answer or the answer streams mid-line, and printed once
    /// they are done (the last <see cref="MaxHeld"/>); a spinner goes on under it.
    /// </summary>
    public void Background(string text)
    {
        lock (_gate)
        {
            if (_asking || _midLine)
            {
                if (_held.Count >= MaxHeld)
                {
                    _held.Dequeue();
                    _heldDropped++;
                }
                _held.Enqueue(text);
                return;
            }
            // At the prompt: written above what the person is typing, which is drawn again under it.
            if (_spinner is null && Interject?.Invoke(text) == true)
            {
                return;
            }
            PrintBackgroundLocked(text);
        }
    }

    /// <summary>Writes a line above the prompt being edited (the line editor's); false when none is (<see cref="Background"/>).</summary>
    public Func<string, bool>? Interject { get; set; }

    private void PrintBackgroundLocked(string text)
    {
        if (_spinner is not null)
        {
            // Its line is cleared for this one; its next frame is drawn under it.
            Err.Write("\r\e[2K");
            Err.Flush();
        }
        Progress.WriteLine(text);
        Progress.Flush();
    }

    /// <summary>What was held back, once nothing is asked and no answer is mid-line.</summary>
    private void PrintHeldLocked()
    {
        if (_asking || _midLine)
        {
            return;
        }
        if (_heldDropped > 0)
        {
            PrintBackgroundLocked(Dim($"… {_heldDropped:N0} more line{(_heldDropped == 1 ? "" : "s")} came meanwhile (command_output has them)"));
            _heldDropped = 0;
        }
        while (_held.TryDequeue(out var line))
        {
            PrintBackgroundLocked(line);
        }
    }

    public void Info(string text) => Line(Dim(text));
    public void Warn(string text) => Line(Yellow("! " + text));

    /// <summary>Errors always go to stderr.</summary>
    public void Error(string text)
    {
        lock (_gate)
        {
            StopSpinnerLocked();
            if (_midLine && !Quiet)
            {
                Out.WriteLine();
                _midLine = false;
            }
            PrintHeldLocked();
            Err.WriteLine(Red("✗ " + text));
            Err.Flush();
        }
    }

    /// <summary>Ends a streamed answer with a newline when it did not end with one.</summary>
    public void EndLine()
    {
        lock (_gate)
        {
            if (_midLine && !Quiet)
            {
                Out.WriteLine();
                Out.Flush();
            }
            _midLine = false;
            PrintHeldLocked();
        }
    }

    public void StartSpinner(string label)
    {
        if (!Animate)
        {
            return;
        }
        lock (_gate)
        {
            _spinner ??= new Spinner(this, label);
        }
    }

    public void StopSpinner()
    {
        lock (_gate)
        {
            StopSpinnerLocked();
        }
    }

    private void StopSpinnerLocked()
    {
        if (_spinner is null)
        {
            return;
        }
        _spinner.Stop();
        _spinner = null;
        Err.Write("\r\e[2K");
        Err.Flush();
    }

    /// <summary>A line the person types; null at the end of input. A line typed while a turn waited and not taken yet comes first.</summary>
    public string? ReadLine(string prompt) => ReadLine(prompt, null);

    /// <summary>
    /// A line, as <see cref="ReadLine(string)"/>; <paramref name="wake"/>, asked while none comes, ends the wait with
    /// <see cref="LineEditor.Woken"/> when it says so (a command ended: a turn carries on from it). The line being read
    /// meanwhile is the next one read.
    /// </summary>
    public string? ReadLine(string prompt, Func<bool>? wake)
    {
        Task<string?>? reading;
        lock (_gate)
        {
            StopSpinnerLocked();
            if (_midLine)
            {
                Out.WriteLine();
                _midLine = false;
            }
            PrintHeldLocked();
            Progress.Write(prompt);
            Progress.Flush();
            reading = _reading;
        }
        if (wake is null)
        {
            return reading is null ? In.ReadLine() : Take(reading);
        }
        var read = reading ?? NextLineAsync();
        while (!read.Wait(TimeSpan.FromMilliseconds(50)))
        {
            if (wake())
            {
                return LineEditor.Woken;
            }
        }
        return Take(read);
    }

    /// <summary>
    /// The next line typed, read on a thread of its own while something else is awaited (a turn
    /// waiting for its commands): the same read until it is taken (<see cref="Take"/>), so a line
    /// not taken then is the next one <see cref="ReadLine"/> returns. Nothing is read ahead.
    /// </summary>
    public Task<string?> NextLineAsync()
    {
        lock (_gate)
        {
            return _reading ??= Task.Run(() => In.ReadLine());
        }
    }

    /// <summary>The line of that read (waiting for it): the next read starts afresh.</summary>
    public string? Take(Task<string?> read)
    {
        lock (_gate)
        {
            if (_reading == read)
            {
                _reading = null;
            }
        }
        return read.GetAwaiter().GetResult();
    }

    /// <summary>A secret (the API key): not echoed on a terminal.</summary>
    public string? ReadSecret(string prompt) => SecretReader is not null ? SecretReader(prompt) : ReadLine(prompt);

    /// <summary>
    /// Reads a question's answer in place of <see cref="ReadLine"/>: the terminal's line editor (so what was typed while the
    /// model worked stays for the next message, and Ctrl+C at the question stops the turn); null for no answer.
    /// </summary>
    public Func<string, string?>? AnswerReader { get; set; }

    /// <summary>Told of a line given at a question that is not an answer: it is the person's next message, not lost.</summary>
    public Action<string>? NotAnAnswer { get; set; }

    /// <summary>Asks to allow an action: yes, no, or always (for this session) where <paramref name="always"/> says what it covers. No one there to ask is a no.</summary>
    public Approval Ask(string question, string? always)
    {
        if (!CanAsk)
        {
            return Approval.No;
        }
        // What runs in the background waits for the answer: it would push the question off the screen.
        lock (_gate)
        {
            _asking = true;
        }
        try
        {
            while (true)
            {
                var prompt = $"{Yellow("?")} {question} {Dim(always is null ? "[y]es, [n]o" : $"[y]es, [n]o, [a]lways {always}")} › ";
                var typed = AnswerReader is { } reader ? ReadAnswer(reader, prompt) : ReadLine(prompt);
                var answer = typed?.Trim().TrimEnd('.', '!').ToLowerInvariant();
                switch (answer)
                {
                    case null or "" or "n" or "no" or "nope" or "nah":
                        return Approval.No;
                    case "y" or "yes" or "yeah" or "yep" or "ok" or "okay" or "sure":
                        return Approval.Yes;
                    case "a" or "always" when always is not null:
                        return Approval.Always;
                }
                if (NotAnAnswer is { } keep && typed!.Trim().Length > 1)
                {
                    // A message meant for the model, not an answer: kept as the next one, and this question is a no.
                    keep(typed.Trim());
                    return Approval.No;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _asking = false;
                PrintHeldLocked();
            }
        }
    }

    /// <summary>
    /// The answer read by the line editor, with nothing printed in the background meanwhile. The question is a line of its
    /// own (the terminal wraps it), and the editor's prompt a short one under it: one wider than the screen would be drawn
    /// again at every key.
    /// </summary>
    private string? ReadAnswer(Func<string, string?> reader, string prompt)
    {
        lock (_gate)
        {
            StopSpinnerLocked();
            if (_midLine)
            {
                Out.WriteLine();
                _midLine = false;
            }
            PrintHeldLocked();
            Out.WriteLine(prompt.TrimEnd().TrimEnd('›').TrimEnd());
            Out.Flush();
        }
        return reader(Yellow("› "));
    }

    /// <summary>Braille frames on stderr, redrawn every 100 ms with the seconds waited.</summary>
    private sealed class Spinner
    {
        private static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
        private readonly Timer _timer;
        private readonly DateTime _start = DateTime.UtcNow;
        private bool _stopped;
        private int _frame;

        public Spinner(Ui ui, string label)
        {
            _timer = new Timer(_ =>
            {
                lock (ui._gate)
                {
                    if (_stopped)
                    {
                        return;
                    }
                    var seconds = (int)(DateTime.UtcNow - _start).TotalSeconds;
                    ui.Err.Write($"\r{ui.Cyan(Frames[_frame++ % Frames.Length])} {ui.Dim($"{label} {seconds}s · Ctrl+C stops")}");
                    ui.Err.Flush();
                }
            }, null, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(100));
        }

        /// <summary>Called under the Ui's lock, so no frame is drawn after the line is cleared.</summary>
        public void Stop()
        {
            _stopped = true;
            _timer.Dispose();
        }
    }
}

/// <summary>Ctrl+C: stops the turn that is running; at the prompt, twice in a row leaves.</summary>
internal sealed class CancelKey
{
    private CancellationTokenSource? _turn;
    private DateTime _lastIdlePress = DateTime.MinValue;

    /// <summary>Where the hint goes when Ctrl+C is pressed at the prompt.</summary>
    public Ui? Ui { get; set; }

    /// <summary>When the last press was: a read that ended because of it is not the end of input.</summary>
    public DateTime LastPress { get; private set; } = DateTime.MinValue;

    /// <summary>A token for one turn; Ctrl+C cancels it until the turn ends.</summary>
    public CancellationTokenSource BeginTurn()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _turn, cts);
        return cts;
    }

    public void EndTurn() => Interlocked.Exchange(ref _turn, null);

    /// <summary>True: handled (keep running). False: pressed twice at the prompt, so the program ends.</summary>
    public bool Press()
    {
        LastPress = DateTime.UtcNow;
        if (Volatile.Read(ref _turn) is { } turn)
        {
            try
            {
                turn.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The turn ended as the key was pressed.
            }
            return true;
        }
        if (DateTime.UtcNow - _lastIdlePress < TimeSpan.FromSeconds(2))
        {
            return false;
        }
        _lastIdlePress = DateTime.UtcNow;
        Ui?.Line();
        Ui?.Info("(Ctrl+C again to leave, or /exit)");
        return true;
    }
}

/// <summary>Plain-text helpers for what the terminal shows.</summary>
internal static class Fmt
{
    /// <summary>At most max characters on one line.</summary>
    public static string OneLine(string? text, int max = 100)
    {
        var flat = (text ?? "").Replace("\r", "").Replace('\n', ' ').Replace('\t', ' ').Trim();
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }

    /// <summary>The first lines of a text, with how many more there are.</summary>
    public static string Head(string text, int lines, int width = 160)
    {
        var all = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var sb = new StringBuilder();
        foreach (var line in all.Take(lines))
        {
            sb.Append(line.Length > width ? line[..width] + "…" : line).Append('\n');
        }
        if (all.Length > lines)
        {
            sb.Append($"… {all.Length - lines} more lines\n");
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The last lines of a text.</summary>
    public static string Tail(string text, int lines, int width = 160)
    {
        var all = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var sb = new StringBuilder();
        if (all.Length > lines)
        {
            sb.Append($"… {all.Length - lines} lines before\n");
        }
        foreach (var line in all.Skip(Math.Max(0, all.Length - lines)))
        {
            sb.Append(line.Length > width ? line[..width] + "…" : line).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>"41s", "3m 12s", "2h 05m".</summary>
    public static string Duration(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s"
        : $"{Math.Max(0, (int)t.TotalSeconds)}s";

    /// <summary>12,345 → "12.3k".</summary>
    public static string Tokens(long n) => n switch
    {
        < 1000 => n.ToString(),
        < 100_000 => $"{n / 1000.0:0.#}k",
        < 1_000_000 => $"{n / 1000}k",
        _ => $"{n / 1_000_000.0:0.##}M",
    };

    /// <summary>Indents every line.</summary>
    public static string Indent(string text, string prefix) =>
        string.Join('\n', text.Split('\n').Select(l => prefix + l));
}
