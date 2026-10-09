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
    private readonly object _gate = new();
    private Spinner? _spinner;
    private bool _midLine;

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
            Progress.WriteLine(text);
            Progress.Flush();
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

    /// <summary>A line the person types; null at the end of input.</summary>
    public string? ReadLine(string prompt)
    {
        lock (_gate)
        {
            StopSpinnerLocked();
            if (_midLine)
            {
                Out.WriteLine();
                _midLine = false;
            }
            Progress.Write(prompt);
            Progress.Flush();
        }
        return In.ReadLine();
    }

    /// <summary>A secret (the API key): not echoed on a terminal.</summary>
    public string? ReadSecret(string prompt) => SecretReader is not null ? SecretReader(prompt) : ReadLine(prompt);

    /// <summary>Asks to allow an action: yes, no, or always (for this session) where <paramref name="always"/> says what it covers. No one there to ask is a no.</summary>
    public Approval Ask(string question, string? always)
    {
        if (!CanAsk)
        {
            return Approval.No;
        }
        while (true)
        {
            var answer = ReadLine($"{Yellow("?")} {question} {Dim(always is null ? "[y]es, [n]o" : $"[y]es, [n]o, [a]lways {always}")} › ")?.Trim().ToLowerInvariant();
            switch (answer)
            {
                case null or "" or "n" or "no":
                    return Approval.No;
                case "y" or "yes":
                    return Approval.Yes;
                case "a" or "always" when always is not null:
                    return Approval.Always;
            }
        }
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
