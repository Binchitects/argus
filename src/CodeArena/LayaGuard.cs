using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// What Laya said of a shell command: how likely it is to destroy, to write outside the workspace, and to reach
/// the network (for a command read in parts, the highest of its parts'). With <see cref="TooLong"/>, it was not read.
/// </summary>
internal sealed record CommandRisk(double Destructive, double Outside, double Network)
{
    /// <summary>The length of a command too long for Laya to read whole: it asks, with no probabilities.</summary>
    public int? TooLong { get; init; }

    /// <summary>Whether any risk is at or past <see cref="LayaGuard.Threshold"/>, or the command was too long to read: then the person is asked, whatever the mode.</summary>
    public bool High => TooLong is not null || Destructive >= LayaGuard.Threshold || Outside >= LayaGuard.Threshold || Network >= LayaGuard.Threshold;

    /// <summary>"Laya: destructive 92%, outside the workspace 4%, network 1%", or "Laya: too long to read whole (23,456 characters)".</summary>
    public string Describe() => TooLong is not null
        ? $"Laya: too long to read whole ({Length()})"
        : $"Laya: destructive {Percent(Destructive)}, outside the workspace {Percent(Outside)}, network {Percent(Network)}";

    /// <summary>The risks past the threshold, in words: "may be destructive (92%) and reach the network (81%)".</summary>
    public string Flagged()
    {
        var said = new List<string>();
        if (Destructive >= LayaGuard.Threshold)
        {
            said.Add($"be destructive ({Percent(Destructive)})");
        }
        if (Outside >= LayaGuard.Threshold)
        {
            said.Add($"write outside the workspace ({Percent(Outside)})");
        }
        if (Network >= LayaGuard.Threshold)
        {
            said.Add($"reach the network ({Percent(Network)})");
        }
        return "may " + string.Join(" and ", said);
    }

    /// <summary>"23,456 characters": the length of a command too long to read.</summary>
    public string Length() => (TooLong ?? 0).ToString("N0", CultureInfo.InvariantCulture) + " characters";

    /// <summary>The higher of each risk: a command read in parts is as risky, on each, as its riskiest part.</summary>
    public CommandRisk Max(CommandRisk other) =>
        new(Math.Max(Destructive, other.Destructive), Math.Max(Outside, other.Outside), Math.Max(Network, other.Network));

    private static string Percent(double p) => Math.Round(p * 100).ToString(CultureInfo.InvariantCulture) + "%";
}

/// <summary>
/// Laya's look at shell commands, through Arena's decide tool when Arena MCP offers it (the
/// laya module runs there). Before a command runs without asking (yolo, or "always" for its
/// first words), Laya is asked whether it is destructive, writes outside the workspace, or
/// reaches the network; at <see cref="Threshold"/> or past it on any, the person is asked
/// anyway, as they are for a command too long for Laya to read whole. Every approval prompt
/// for a command shows the three probabilities. It only adds questions: when Laya cannot
/// answer, the mode decides as it always did.
/// </summary>
internal sealed partial class LayaGuard(McpClient arena, Workspace workspace, Ui ui)
{
    /// <summary>
    /// The probability at which a risk asks. Measured with Laya's English checkpoint on 95 commands
    /// labelled by hand (RealLayaTests.Commands, docs/code-arena.md): 45 of 52 risky ones asked,
    /// 2 of 43 harmless ones; at 0.7, 37 of the risky ones.
    /// </summary>
    public const double Threshold = 0.6;

    /// <summary>
    /// What Laya reads of a state, in characters, with room to spare: the English checkpoint reads
    /// 512 tokens, about 2,000 characters of prose and fewer of shell. A longer command is read in parts.
    /// </summary>
    public const int Readable = 1_500;

    /// <summary>The most parts a command is read in, one decide call each: a longer command asks without being read.</summary>
    public const int MaxParts = 8;

    public const string Tool = "decide";

    private const string Whole = "Shell command: ";
    // What two windows of one long stretch without a seam share, so that the words at their edge are read whole.
    private const int Overlap = 200;
    // The calibrated checkpoint first; the multilingual one reads commands while the English one is not loaded.
    private static readonly string[] Checkpoints = ["english", "multilingual"];
    private bool _warned;

    /// <summary>How long Laya may take over one command, all its parts together.</summary>
    public TimeSpan Patience { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Told the one warning that Laya did not check a command, besides the terminal: the IDE's page.</summary>
    public Action<string>? Notify { get; set; }

    /// <summary>A guard when Arena serves decide, else null.</summary>
    public static LayaGuard? For(McpClient? arena, Workspace workspace, Ui ui) =>
        arena?.Tools.Any(t => t.Str("name") == Tool) == true ? new LayaGuard(arena, workspace, ui) : null;

    /// <summary>What Laya says of the command, or null when it cannot say (then nothing changes).</summary>
    public async Task<CommandRisk?> AssessAsync(string? command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }
        if (States(command, workspace) is not { } states)
        {
            // What Laya cannot read is not found harmless: it asks.
            return new CommandRisk(0, 0, 0) { TooLong = command.Trim().Length };
        }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(Patience);
        try
        {
            CommandRisk? highest = null;
            string? checkpoint = null;
            foreach (var state in states)
            {
                McpResult? result = null;
                foreach (var name in checkpoint is null ? Checkpoints : [checkpoint])
                {
                    result = await arena.CallAsync(Tool, Request(state, name), wait.Token);
                    if (!result.IsError)
                    {
                        checkpoint = name;
                        break;
                    }
                }
                if (result!.IsError || Parse(result.Text) is not { } risk)
                {
                    Warn(result.IsError ? result.Text : "its answer could not be read");
                    return null;
                }
                highest = highest?.Max(risk) ?? risk;
            }
            return highest;
        }
        catch (Exception e) when (e is McpException or IOException or HttpRequestException)
        {
            Warn(e.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Warn($"it did not answer within {Patience.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds");
        }
        return null;
    }

    /// <summary>
    /// What Laya reads of a command: first the working folder and the paths outside it that the command
    /// names (found here: Laya reads text, it does not compare paths), then the command on one line, its
    /// line breaks written \n so that none can pose as the lines before. A command longer than Laya reads
    /// is read in parts, cut after its line breaks, &amp;&amp;, ||, ; and |, each with the same lines first.
    /// Null when that takes more than <see cref="MaxParts"/>.
    /// </summary>
    public static IReadOnlyList<string>? States(string command, Workspace workspace)
    {
        var outside = OutsidePaths(command, workspace);
        var facts = $"Working folder: {workspace.Root}\nPaths outside the working folder: {(outside.Count > 0 ? Fmt.OneLine(string.Join(", ", outside), 300) : "none")}\n";
        var line = Flat(command);
        if (facts.Length + Whole.Length + line.Length <= Readable)
        {
            return [facts + Whole + line];
        }
        var room = Math.Max(Readable - facts.Length - $"Shell command, part {MaxParts} of {MaxParts}: ".Length, 2 * Overlap);
        if (line.Length > MaxParts * room)
        {
            return null;
        }
        var parts = new List<string>();
        var part = new StringBuilder();
        foreach (var piece in Pieces(line))
        {
            if (part.Length > 0 && part.Length + piece.Length > room)
            {
                parts.Add(part.ToString());
                part.Clear();
            }
            if (piece.Length <= room)
            {
                part.Append(piece);
                continue;
            }
            for (var start = 0; ; start += room - Overlap)
            {
                var end = Math.Min(start + room, piece.Length);
                parts.Add(piece[start..end]);
                if (end == piece.Length)
                {
                    break;
                }
            }
        }
        parts.Add(part.ToString());
        parts.RemoveAll(string.IsNullOrWhiteSpace);
        return parts.Count > MaxParts ? null : [.. parts.Select((p, i) => $"{facts}Shell command, part {i + 1} of {parts.Count}: {p.Trim()}")];
    }

    /// <summary>The command on one line, as Laya reads it: its line breaks written \n, runs of blanks one space.</summary>
    private static string Flat(string command) => Blanks().Replace(LineBreaks().Replace(command.Trim(), @"\n"), " ");

    /// <summary>The line cut after each seam (\n, &amp;&amp;, ||, ;, |): the pieces join back into it.</summary>
    private static IEnumerable<string> Pieces(string line)
    {
        var from = 0;
        foreach (Match seam in Seams().Matches(line))
        {
            yield return line[from..(seam.Index + seam.Length)];
            from = seam.Index + seam.Length;
        }
        yield return line[from..];
    }

    /// <summary>The decide call for one state: three yes/no questions, and the checkpoint that reads it.</summary>
    public static JsonObject Request(string state, string checkpoint) => new()
    {
        ["state"] = state,
        ["questions"] = new JsonArray(
            YesNo("destructive", "Does this command delete, overwrite or discard files, data or history?"),
            YesNo("outside", "Does this command write to, change or delete one of the paths outside the working folder?"),
            YesNo("network", "Does this command download, upload, install packages, push to a git remote, or connect to another machine?")),
        ["checkpoint"] = checkpoint,
    };

    private static JsonObject YesNo(string id, string question) => new() { ["id"] = id, ["type"] = "noul", ["question"] = question };

    /// <summary>The three probabilities from decide's answer, or null when it is not one.</summary>
    public static CommandRisk? Parse(string text)
    {
        var answers = Json.ParseObject(text)?["answers"] as JsonObject;
        double? P(string id) => answers?[id]?["noul"] is JsonValue v && v.TryGetValue<double>(out var p) ? p : null;
        return P("destructive") is { } d && P("outside") is { } o && P("network") is { } n ? new CommandRisk(d, o, n) : null;
    }

    /// <summary>The paths a command names outside every allowed folder: ~, $HOME, .., absolute paths elsewhere (not /dev/null, not URLs).</summary>
    public static IReadOnlyList<string> OutsidePaths(string command, Workspace workspace)
    {
        var found = new List<string>();
        foreach (var token in Separators().Split(command))
        {
            if (token.Length == 0 || token is "/dev/null" or "NUL" || token.Contains("://", StringComparison.Ordinal) || token.Contains('@'))
            {
                continue;
            }
            string full;
            try
            {
                if (token.StartsWith('~') || token.StartsWith("$HOME", StringComparison.Ordinal) || token.StartsWith("%USERPROFILE%", StringComparison.OrdinalIgnoreCase))
                {
                    var rest = token[(token.StartsWith('~') ? 1 : token.StartsWith('$') ? 5 : 13)..].TrimStart('/', '\\');
                    full = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), rest));
                }
                else if (token.StartsWith("..", StringComparison.Ordinal) || token.StartsWith('/') || token.StartsWith('\\') || WindowsDrive().IsMatch(token))
                {
                    full = Path.GetFullPath(token, workspace.Root);
                }
                else
                {
                    continue;
                }
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (!workspace.Inside(Path.TrimEndingDirectorySeparator(full)) && !found.Contains(token))
            {
                found.Add(token);
            }
        }
        return [.. found.Take(8)];
    }

    /// <summary>The terminal's question for a command: with Laya's probabilities, and why it asks when the mode would not.</summary>
    public static string Question(string tool, CommandRisk? risk, bool modeAsks) =>
        Note(risk, modeAsks) is { } note ? $"Allow {tool}? ({note})" : $"Allow {tool}?";

    /// <summary>What an approval prompt shows of Laya's look: the probabilities, and why it asks when the mode would not.</summary>
    public static string? Note(CommandRisk? risk, bool modeAsks) =>
        risk is null ? null
        : modeAsks ? risk.Describe()
        : risk.TooLong is not null ? $"This command is too long for Laya to read whole ({risk.Length()}), so this asks although the mode would run it."
        : $"Laya says this command {risk.Flagged()}, so this asks although the mode would run it. {risk.Describe()}";

    /// <summary>For a run that cannot ask: why a command Laya flagged, or could not read whole, does not run.</summary>
    public static string CannotAsk(string tool, CommandRisk risk) => risk.TooLong is not null
        ? $"{tool} was not run: it is too long for Laya to read whole ({risk.Length()}), so it needs the person's approval, and this run cannot ask. " +
          "Write long text with the file tools, and run shorter commands."
        : $"{tool} was not run: Laya says this command {risk.Flagged()}, which needs the person's approval, and this run cannot ask. " +
          "Say what you would have done, or find a safer way.";

    private void Warn(string why)
    {
        if (_warned)
        {
            return;
        }
        _warned = true;
        var text = $"Laya did not check this command ({Fmt.OneLine(why, 120)}): commands run as the mode says.";
        ui.Warn(text);
        Notify?.Invoke(text);
    }

    [GeneratedRegex(@"[\s;&|<>()'""=`]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex WindowsDrive();

    [GeneratedRegex(@"\r\n|[\n\r\u0085  ]")]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"[ \t\v\f]+")]
    private static partial Regex Blanks();

    [GeneratedRegex(@"&&|\|\||;|\||\\n")]
    private static partial Regex Seams();
}
