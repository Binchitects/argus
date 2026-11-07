using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Core.Chat;

namespace CodeArena;

/// <summary>How the model's tool calls went in a session: each kind of fault, what was mended, and calls made again after one failed.</summary>
internal sealed class CallStats
{
    public int Calls { get; set; }
    /// <summary>A tool that is not there; arguments that were not JSON; arguments the schema refused (missing, wrong type).</summary>
    public int UnknownTool { get; set; }
    public int BadJson { get; set; }
    public int BadArguments { get; set; }
    /// <summary>An edit whose old_string was not in the file (or not once).</summary>
    public int EditMisses { get; set; }
    /// <summary>Any other result that was an error (a file not there, a command refused).</summary>
    public int OtherErrors { get; set; }
    /// <summary>Calls mended before they ran (a name, the JSON, an argument's name or type).</summary>
    public int Mended { get; set; }
    /// <summary>The same call (tool and arguments) made again after it failed.</summary>
    public int Retries { get; set; }

    /// <summary>The calls that failed for how they were made: the tool, the JSON or the arguments.</summary>
    public int CallFaults => UnknownTool + BadJson + BadArguments;

    public int Failed => CallFaults + EditMisses + OtherErrors;

    public JsonObject ToJson() => new()
    {
        ["calls"] = Calls, ["unknownTool"] = UnknownTool, ["badJson"] = BadJson, ["badArguments"] = BadArguments, ["editMisses"] = EditMisses,
        ["otherErrors"] = OtherErrors, ["mended"] = Mended, ["retries"] = Retries,
    };
}

/// <summary>
/// The tool results the model has used, made small before each step (<see cref="ResultCondenser"/>): kept whole as the
/// session's results (read_result reads them again), summarised in the history the model reads. And the model's calls
/// mended before they run (<see cref="ToolCallRepair"/>), the history keeping the mended call.
/// </summary>
internal sealed partial class Agent
{
    /// <summary>Earlier tool results the model has used are summarised before each step (config condenseResults; on by default).</summary>
    public bool CondenseResults { get; set; } = true;

    /// <summary>Tool calls a model got wrong where what it meant is plain are mended before they run (on; off only to measure without it).</summary>
    public bool RepairCalls { get; set; } = true;

    /// <summary>How this agent's tool calls went.</summary>
    public CallStats CallStats { get; } = new();

    /// <summary>What condensing has done in this session (since it was loaded).</summary>
    public Condensed CondensedSoFar { get; private set; }

    /// <summary>What a call was mended into before it ran, by its id: said under its result, so the model calls it so next time.</summary>
    private readonly Dictionary<string, string> _mended = new(StringComparer.Ordinal);

    /// <summary>Calls refused before they ran for their arguments (a required one missing), by id: the refusal, with the tool's parameters.</summary>
    private readonly Dictionary<string, string> _misfit = new(StringComparer.Ordinal);

    /// <summary>The calls that failed, as tool and arguments: made again, it is a retry.</summary>
    private readonly HashSet<string> _failedCalls = new(StringComparer.Ordinal);

    private ResultCondenser? _condenser;

    private ResultCondenser Condenser => _condenser ??= new ResultCondenser
    {
        UseOf = name => name switch
        {
            "read_file" => ToolUse.Read,
            "edit_file" or "write_file" => ToolUse.Write,
            _ => ToolUse.Other,
        },
        Keep = (_, _, text) => KeptAs().Match(text) is { Success: true } kept ? Handle(kept.Groups[1].Value)
            : KeptResults.Keep(Context, text) is { } id ? Handle(id) : null,
        Outline = Outline,
        Settled = text => text.StartsWith("[Cleared to make room in the window:", StringComparison.Ordinal),
    };

    private static string Handle(string id) => $"it is kept as {id}, read_result {{\"id\":\"{id}\"}} reads it (find: a line's text)";

    /// <summary>A part of a file's outline, by the index's scanners: each symbol on a line, nested, with its lines in the file.</summary>
    private static string? Outline(string path, string text, int first)
    {
        var symbols = FileOutline.Symbols(path, text);
        if (symbols.Count == 0)
        {
            return null;
        }
        var sb = new StringBuilder();
        var open = new Stack<int>();
        foreach (var s in symbols)
        {
            while (open.Count > 0 && open.Peek() < s.Start)
            {
                open.Pop();
            }
            if (sb.Length > 1_200)
            {
                sb.Append("  … more symbols\n");
                break;
            }
            var head = s.Signature is { Length: > 0 } sig ? Fmt.OneLine(sig, 100) : $"{s.Kind} {s.Name}";
            sb.Append(new string(' ', 2 * (open.Count + 1))).Append(head).Append($"  [{s.Start + first - 1}-{s.End + first - 1}]\n");
            open.Push(s.End);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Summarises the tool results the model has used (two answers or more behind), before a step: kept whole as the
    /// session's results, their summaries in the history from now on. Said in a line; the session keeps the new history.
    /// </summary>
    internal Condensed Condense()
    {
        if (!CondenseResults || Messages.Count < 4)
        {
            return default;
        }
        var changed = new List<int>();
        var done = Condenser.Condense(Messages, changed);
        if (done.Count == 0)
        {
            return done;
        }
        CondensedSoFar += done;
        // Measured anew: what the gateway counted last included them.
        (_knownTokens, _knownCount) = (0, 0);
        // Code answers show again what they had shown in the results summarised.
        Context.Shown?.Clear();
        // Every message keeps its place: the session keeps the results' new words, by their places.
        Session?.Condensed([.. changed.Select(i => (i, Messages[i].Str("content") ?? ""))]);
        var said = done.Describe(CharsPerToken) + " read_result reads a result whole again.";
        Ui.Line(Ui.Dim(Prefix + said));
        Events?.Notice(said);
        return done;
    }

    /// <summary>
    /// The model's calls, mended before the answer is kept: a tool named as another harness names it, arguments that are
    /// not quite JSON, arguments named or typed not as its schema says. The answer keeps the mended call (the model
    /// reads it so from now on, and a chat template that reads the arguments as JSON can); what was mended is said under
    /// its result. A call whose arguments still do not fit is refused with the tool's parameters. Counted either way.
    /// </summary>
    private void MendCalls(JsonArray calls)
    {
        foreach (var call in calls.OfType<JsonObject>())
        {
            var id = call.Str("id") ?? "";
            if (call["function"] is not JsonObject function)
            {
                continue;
            }
            var name = function.Str("name") ?? "";
            var raw = function.Str("arguments");
            var fixes = new List<string>();
            var tool = Tools.Find(name);
            if (tool is null)
            {
                var meant = ToolCallRepair.ToolName(name, Tools.Offered().Select(t => t.Name).ToList());
                if (meant is null || !RepairCalls)
                {
                    CallStats.UnknownTool++;
                    continue;
                }
                fixes.Add($"{name} → {meant}");
                function["name"] = meant;
                (name, tool) = (meant, Tools.Find(meant));
            }
            var args = string.IsNullOrWhiteSpace(raw) ? [] : Json.ParseObject(raw);
            if (args is null)
            {
                if (!RepairCalls || ToolCallRepair.Arguments(raw, out var fix) is not { } mended)
                {
                    CallStats.BadJson++;
                    continue;
                }
                fixes.Add(fix ?? "the JSON mended");
                args = mended;
            }
            if (tool is not null)
            {
                var fitted = ToolCallRepair.Fit(name, args, tool.Parameters);
                if (fitted.Problem is not null && RepairCalls)
                {
                    // Without mending, the tool says what is wrong itself, and the result is counted as it comes.
                    CallStats.BadArguments++;
                    _misfit[id] = fitted.Problem;
                }
                if (RepairCalls)
                {
                    fixes.AddRange(fitted.Fixes);
                    args = fitted.Args;
                }
            }
            if (fixes.Count > 0 && RepairCalls)
            {
                function["arguments"] = Json.Line(args);
                _mended[id] = string.Join("; ", fixes);
                CallStats.Mended++;
            }
        }
    }

    /// <summary>A call's result, counted: its fault if it failed, and whether it was made again after failing.</summary>
    private void Count(string name, JsonObject args, ToolResult result, string? refusal)
    {
        CallStats.Calls++;
        var key = name + " " + Json.Line(args);
        if (!result.Error)
        {
            _failedCalls.Remove(key);
            return;
        }
        if (!_failedCalls.Add(key))
        {
            CallStats.Retries++;
        }
        // Unknown tools, bad JSON and refused arguments were counted as the call was read (MendCalls).
        if (refusal is not null && (refusal.StartsWith("There is no tool named", StringComparison.Ordinal) || refusal.StartsWith("The arguments for", StringComparison.Ordinal)
            || refusal.StartsWith("The call to", StringComparison.Ordinal)))
        {
            return;
        }
        if (result.Text.Contains("old_string was not found", StringComparison.Ordinal) || result.Text.Contains("old_string appears", StringComparison.Ordinal))
        {
            CallStats.EditMisses++;
        }
        else if (ArgumentFault().IsMatch(result.Text))
        {
            CallStats.BadArguments++;
        }
        else
        {
            CallStats.OtherErrors++;
        }
    }

    /// <summary>A tool's error that says how it was called was wrong (a required argument, a value it does not take).</summary>
    [GeneratedRegex(@"\b(?:is required|are required|must be|is not one of|not a valid|unknown (?:argument|parameter))\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArgumentFault();

    [GeneratedRegex(@"is kept as (r\d+)")]
    private static partial Regex KeptAs();
}
