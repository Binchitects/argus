using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// What Laya said of a shell command: how likely it is to destroy, to write outside the workspace, and to reach
/// the network (for a command read in parts, the highest of its parts'). With <see cref="Unread"/>, Laya did not
/// read all of it, and it asks.
/// </summary>
internal sealed record CommandRisk(double Destructive, double Outside, double Network)
{
    /// <summary>What Laya did not read of the command, and why: "30,021 characters, too long to read whole", "no answer for part 2 of 3: …".</summary>
    public string? Unread { get; init; }

    /// <summary>Whether Laya read any of it: a command too long to send has no probabilities.</summary>
    public bool Read { get; init; } = true;

    /// <summary>A command Laya was not shown at all, and why.</summary>
    public static CommandRisk NotRead(string why) => new(0, 0, 0) { Unread = why, Read = false };

    /// <summary>Whether any risk is at or past <see cref="LayaGuard.Threshold"/>.</summary>
    public bool Risky => Destructive >= LayaGuard.Threshold || Outside >= LayaGuard.Threshold || Network >= LayaGuard.Threshold;

    /// <summary>Whether the person is asked, whatever the mode: a risk past the threshold, or a part Laya did not read.</summary>
    public bool High => Risky || Unread is not null;

    /// <summary>"Laya: destructive 92%, outside the workspace 4%, network 1%", or "Laya: 23,456 characters, too long to read whole".</summary>
    public string Describe() => !Read
        ? $"Laya: {Unread}"
        : $"Laya: destructive {Percent(Destructive)}, outside the workspace {Percent(Outside)}, network {Percent(Network)}" + (Unread is null ? "" : $" on what it read; {Unread}");

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
/// anyway, as they are for a command Laya could not read all of. Every approval prompt for a
/// command shows the three probabilities. It only adds questions: when Laya cannot answer
/// at all, the mode decides as it always did.
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
    /// What one state may cost, in Laya's tokens as <see cref="Tokens"/> counts them. The English checkpoint
    /// reads 512 tokens, the question included: 464 are left for the state after the longest of the three.
    /// A part Laya still cuts (it says so) is read again in smaller parts.
    /// </summary>
    public const int Budget = 440;

    /// <summary>The most parts a command is read in, one decide call each: a command that takes more asks without being read whole.</summary>
    public const int MaxParts = 8;

    public const string Tool = "decide";

    private const string Whole = "Shell command: ";
    // The calibrated checkpoint, and the one that reads commands while it is not loaded.
    private const string English = "english";
    private const string Multilingual = "multilingual";
    private bool _warned;

    /// <summary>How long Laya may take over one decide call (a command in parts makes one call for each).</summary>
    public TimeSpan Patience { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Told the one warning that Laya did not check a command, besides the terminal: the IDE's page.</summary>
    public Action<string>? Notify { get; set; }

    /// <summary>A guard when Arena serves decide, else null.</summary>
    public static LayaGuard? For(McpClient? arena, Workspace workspace, Ui ui) =>
        arena?.Tools.Any(t => t.Str("name") == Tool) == true ? new LayaGuard(arena, workspace, ui) : null;

    /// <summary>
    /// What Laya says of the command, or null when it cannot say at all (then nothing changes). A command
    /// Laya read only some of (too long, a part it cut even in smaller parts, a part it did not answer for)
    /// comes back with <see cref="CommandRisk.Unread"/>, and asks.
    /// </summary>
    public async Task<CommandRisk?> AssessAsync(string? command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }
        var facts = Facts(command, workspace);
        var parts = Parts(Flat(command), Room(facts));
        var tooLong = command.Trim().Length.ToString("N0", CultureInfo.InvariantCulture) + " characters, too long to read whole";
        if (parts.Count > MaxParts)
        {
            // What Laya cannot read is not found harmless: it asks.
            return CommandRisk.NotRead(tooLong);
        }
        CommandRisk? read = null;
        var checkpoint = English;
        for (var i = 0; i < parts.Count; i++)
        {
            var state = State(facts, parts, i);
            var (result, why) = await CallAsync(state, checkpoint, ct);
            if (result is { IsError: true } && checkpoint == English && NotLoaded().IsMatch(result.Text))
            {
                // decide is offered while either checkpoint is loaded: without the English one, the multilingual one reads.
                checkpoint = Multilingual;
                (result, why) = await CallAsync(state, checkpoint, ct);
            }
            var risk = result is { IsError: false } ? Parse(result.Text) : null;
            if (risk is null)
            {
                why ??= result!.IsError ? result.Text : "its answer could not be read";
                if (read is null)
                {
                    Warn(why);
                    return null;
                }
                // Laya read some of it and not the rest: what it did not read asks, as what it flagged does.
                return read with { Unread = $"no answer for part {i + 1} of {parts.Count}: {Fmt.OneLine(why, 120)}" };
            }
            read = read?.Max(risk) ?? risk;
            if (Cut(result!.Text) is { } cut)
            {
                // Laya cut this state short, and counted its tokens: so many for each counted here. The part's text again,
                // in parts that fit by Laya's count with a tenth to spare, beside the label and the facts.
                var scale = cut.Tokens / Tokens(state);
                var room = 0.9 * cut.Read / scale - (Tokens(state) - Tokens(parts[i]));
                var smaller = room >= 1 ? Parts(parts[i], room) : [];
                if (smaller.Count < 2 || parts.Count - 1 + smaller.Count > MaxParts)
                {
                    return read with { Unread = tooLong };
                }
                parts.RemoveAt(i);
                parts.InsertRange(i, smaller);
                i--;
            }
        }
        return read;
    }

    /// <summary>One decide call, within <see cref="Patience"/>: its result, or why there is none.</summary>
    private async Task<(McpResult? Result, string? Why)> CallAsync(string state, string checkpoint, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(Patience);
        try
        {
            return (await arena.CallAsync(Tool, Request(state, checkpoint), wait.Token), null);
        }
        catch (Exception e) when (e is McpException or IOException or HttpRequestException)
        {
            return (null, e.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, $"it did not answer within {Patience.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds");
        }
    }

    /// <summary>
    /// What Laya reads of a command: the command on one line (its line breaks written \n, so that none can
    /// pose as the lines after it), then the working folder and the paths outside it that the command names
    /// (found here: Laya reads text, it does not compare paths). A command longer than <see cref="Budget"/> is
    /// read in parts, cut after its line breaks, &amp;&amp;, ||, ; and |, each with the same lines after it.
    /// Null when that takes more than <see cref="MaxParts"/>.
    /// </summary>
    public static IReadOnlyList<string>? States(string command, Workspace workspace)
    {
        var facts = Facts(command, workspace);
        var parts = Parts(Flat(command), Room(facts));
        return parts.Count > MaxParts ? null : [.. parts.Select((_, i) => State(facts, parts, i))];
    }

    /// <summary>The lines after the command: the working folder, and the paths outside it the command names.</summary>
    private static string Facts(string command, Workspace workspace)
    {
        var outside = OutsidePaths(command, workspace);
        return $"\nWorking folder: {workspace.Root}\nPaths outside the working folder: {(outside.Count > 0 ? Fmt.OneLine(string.Join(", ", outside), 300) : "none")}";
    }

    /// <summary>
    /// The state for part i: the part, labelled as one when there are several, then the facts. The command goes
    /// first: on the 95 labelled commands, Laya asked before 41 of the risky ones with the facts first, 45 after.
    /// </summary>
    private static string State(string facts, List<string> parts, int i) =>
        (parts.Count == 1 ? Whole : $"Shell command (part {i + 1} of {parts.Count}): ") + parts[i] + facts;

    /// <summary>What is left of <see cref="Budget"/> for the command after the facts and the longest label.</summary>
    private static double Room(string facts) => Budget - Tokens(facts) - Tokens($"Shell command (part {MaxParts} of {MaxParts}): ");

    /// <summary>
    /// The line in parts of at most <paramref name="room"/> tokens each: cut after its seams (\n, &amp;&amp;, ||, ;, |),
    /// the pieces packed together while they fit; a piece longer than that in windows that overlap by a quarter,
    /// so that the words at their edge are read whole.
    /// </summary>
    private static List<string> Parts(string line, double room)
    {
        var parts = new List<string>();
        var part = new StringBuilder();
        foreach (var piece in Pieces(line))
        {
            if (parts.Count > MaxParts)
            {
                // Already more than is read: the rest need not be cut.
                return parts;
            }
            if (part.Length > 0 && Tokens(part.ToString() + piece) > room)
            {
                parts.Add(part.ToString());
                part.Clear();
            }
            if (Tokens(piece) <= room)
            {
                part.Append(piece);
                continue;
            }
            for (var start = 0; parts.Count <= MaxParts;)
            {
                var end = Fit(piece, start, room);
                parts.Add(piece[start..end]);
                if (end == piece.Length)
                {
                    break;
                }
                start = Math.Max(start + 1, end - Math.Min(200, (end - start) / 4));
            }
        }
        parts.Add(part.ToString());
        parts.RemoveAll(string.IsNullOrWhiteSpace);
        return [.. parts.Select(p => p.Trim())];
    }

    /// <summary>The furthest end, after start, of a window of text within room tokens (at least one character; no token is more than 7).</summary>
    private static int Fit(string text, int start, double room)
    {
        int low = start + 1, high = (int)Math.Min(text.Length, start + 7 * Math.Max(room, 1));
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (Tokens(text[start..mid]) <= room)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }
        return low;
    }

    /// <summary>
    /// Laya's tokens in a text, counted from above: measured against the English checkpoint's tokenizer on shell,
    /// code, prose, paths, JSON, hashes, base64, Persian, Arabic, Russian and Chinese, it is at or above the true
    /// count, about a fifth above for code and prose. Words cost by their runs (a lowercase or capitalised run up
    /// to 6 letters a token, capitals 1.5, digits 2); a word that keeps changing case and digits (a hash, base64)
    /// 0.9 a character; other ASCII one each, a space nothing; other characters 1, 2 or 4 by their UTF-8 length.
    /// Random letters and rare scripts cost more than this: Laya says when it cut a state, and it is read again.
    /// </summary>
    public static double Tokens(string text)
    {
        double tokens = 0;
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (char.IsAsciiLetterOrDigit(c))
            {
                var start = i;
                while (i < text.Length && char.IsAsciiLetterOrDigit(text[i]))
                {
                    i++;
                }
                tokens += WordTokens(text.AsSpan(start, i - start));
                continue;
            }
            // A space joins the word after it; a 3-byte character costs 2, and so does each half of a 4-byte one.
            tokens += c switch
            {
                ' ' => 0,
                < '\u0800' => 1,
                _ => 2,
            };
            i++;
        }
        return tokens;
    }

    private static double WordTokens(ReadOnlySpan<char> word)
    {
        double runs = 0;
        var changes = 0;
        for (var i = 0; i < word.Length;)
        {
            var start = i;
            if (char.IsAsciiLetterLower(word[i]) || (char.IsAsciiLetterUpper(word[i]) && i + 1 < word.Length && char.IsAsciiLetterLower(word[i + 1])))
            {
                for (i++; i < word.Length && char.IsAsciiLetterLower(word[i]); i++)
                {
                }
                runs += Math.Ceiling((i - start) / 6.0);
            }
            else if (char.IsAsciiLetterUpper(word[i]))
            {
                for (; i < word.Length && char.IsAsciiLetterUpper(word[i]) && !(i + 1 < word.Length && char.IsAsciiLetterLower(word[i + 1])); i++)
                {
                }
                runs += Math.Ceiling((i - start) / 1.5);
            }
            else
            {
                for (; i < word.Length && char.IsAsciiDigit(word[i]); i++)
                {
                }
                runs += Math.Ceiling((i - start) / 2.0);
            }
        }
        for (var i = 1; i < word.Length; i++)
        {
            // A change of kind between neighbours, except a capital starting a word.
            if (Kind(word[i - 1]) != Kind(word[i]) && !(char.IsAsciiLetterUpper(word[i - 1]) && char.IsAsciiLetterLower(word[i])))
            {
                changes++;
            }
        }
        return word.Length >= 8 && changes >= word.Length / 4.0 ? Math.Max(runs, 0.9 * word.Length) : runs;

        static int Kind(char c) => char.IsAsciiLetterLower(c) ? 0 : char.IsAsciiLetterUpper(c) ? 1 : 2;
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

    /// <summary>When Laya cut the state short (decide's "truncated"): the state's tokens and how many it read.</summary>
    public static (double Tokens, double Read)? Cut(string text) =>
        Json.ParseObject(text)?["truncated"] is JsonObject cut && cut["tokens"] is JsonValue t && t.TryGetValue<double>(out var tokens)
        && cut["read"] is JsonValue r && r.TryGetValue<double>(out var read) && tokens > read && read >= 0 ? (tokens, read) : null;

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
        : risk.Risky ? $"Laya says this command {risk.Flagged()}, so this asks although the mode would run it. {risk.Describe()}"
        : $"Laya could not read all of this command, so this asks although the mode would run it. {risk.Describe()}";

    /// <summary>For a run that cannot ask: why a command Laya flagged, or could not read all of, does not run.</summary>
    public static string CannotAsk(string tool, CommandRisk risk) => risk.Risky
        ? $"{tool} was not run: Laya says this command {risk.Flagged()}, which needs the person's approval, and this run cannot ask. " +
          "Say what you would have done, or find a safer way."
        : $"{tool} was not run: Laya could not read all of it ({risk.Unread}), so it needs the person's approval, and this run cannot ask. " +
          "Write long text with the file tools, and run shorter commands.";

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

    // The laya server's refusal while the checkpoint is not loaded (deploy/services/laya/server.py, Decider.choose).
    [GeneratedRegex(@"\bthe english checkpoint is (waiting|loading|failed)")]
    private static partial Regex NotLoaded();
}
