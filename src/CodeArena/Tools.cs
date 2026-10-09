using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>What a tool does to the person's machine, which decides when it asks first.</summary>
internal enum ToolKind
{
    /// <summary>Reads only: never asks.</summary>
    Read,
    /// <summary>Changes files.</summary>
    Edit,
    /// <summary>Runs commands.</summary>
    Shell,
    /// <summary>Runs on an MCP server.</summary>
    Remote,
    /// <summary>A sub-agent: its own tools ask for themselves.</summary>
    Agent,
}

/// <summary>A tool's answer for the model, and what the terminal shows of it (a diff, a summary).</summary>
internal sealed record ToolResult(string Text, bool Error = false)
{
    public string? Display { get; init; }
    /// <summary>The file an edit changed, for the web interface's diff.</summary>
    public FileChange? Change { get; init; }
}

/// <summary>A file as an edit found it (null: it was new) and as it left it.</summary>
internal sealed record FileChange(string Path, string? Before, string After);

/// <summary>A tool the model can call: its name, description and JSON schema, and what runs.</summary>
internal sealed class ToolDef
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonObject Parameters { get; init; }
    public required Func<JsonObject, ToolContext, CancellationToken, Task<ToolResult>> Run { get; init; }
    public ToolKind Kind { get; init; }
    /// <summary>The MCP server it comes from ("arena" for Arena's own), or null for a local tool.</summary>
    public string? Server { get; init; }
    /// <summary>Its name at its MCP server (Name may be renamed: arena_read_file, mcp__server__tool).</summary>
    public string? RemoteName { get; init; }
    /// <summary>A remote tool that runs without asking: Arena's own, or one of a server the person trusts.</summary>
    public bool Trusted { get; init; }
    /// <summary>A remote tool that changes nothing (MCP readOnlyHint; Arena's, unless it says otherwise): for plan mode and sub-agents.</summary>
    public bool ChangesNothing { get; init; }
    /// <summary>The call in a few words for the terminal (a path, a pattern, a command).</summary>
    public Func<JsonObject, string>? Summary { get; init; }
    /// <summary>Asks the person in every mode, yolo too (stopping a command they let run with no time limit).</summary>
    public bool AlwaysAsks { get; init; }

    public JsonObject Spec() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = Name, ["description"] = Description, ["parameters"] = Parameters.Clone() },
    };

    /// <summary>Whether it changes nothing on this machine: what plan mode and sub-agents get.</summary>
    public bool ReadOnly => Kind == ToolKind.Read || (Kind == ToolKind.Remote && ChangesNothing);
}

/// <summary>What tools work with: the allowed folders, the terminal, the shell, the to-do list, sub-agents.</summary>
internal sealed class ToolContext
{
    public required Workspace Workspace { get; init; }
    public required Ui Ui { get; init; }
    /// <summary>The shell for run_shell, when the config names one.</summary>
    public string? Shell { get; init; }
    /// <summary>The commands run with no time limit; null where none may run (a sub-agent).</summary>
    public CommandJobs? Jobs { get; init; }
    public List<TodoItem> Todos { get; set; } = [];
    /// <summary>Runs a sub-agent (description, prompt) and returns its report.</summary>
    public Func<string, string, CancellationToken, Task<string>>? SubAgent { get; set; }
    /// <summary>Called when the to-do list changes, so the session keeps it.</summary>
    public Action<List<TodoItem>>? TodosChanged { get; set; }
}

/// <summary>One line of the to-do list.</summary>
internal sealed record TodoItem(string Content, string Status);

/// <summary>The permission modes: how much runs without asking.</summary>
internal enum Mode { Ask, AutoEdit, Plan, Yolo }

internal static class Modes
{
    public static readonly string[] Names = ["ask", "auto-edit", "plan", "yolo"];

    public static Mode? Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "ask" or "default" => Mode.Ask,
        "auto-edit" or "autoedit" or "auto" or "accept-edits" => Mode.AutoEdit,
        "plan" or "read-only" => Mode.Plan,
        "yolo" => Mode.Yolo,
        _ => null,
    };

    public static string Name(this Mode mode) => mode switch
    {
        Mode.AutoEdit => "auto-edit",
        Mode.Plan => "plan",
        Mode.Yolo => "yolo",
        _ => "ask",
    };

    public static string Describe(this Mode mode) => mode switch
    {
        Mode.AutoEdit => "file edits run without asking; commands ask",
        Mode.Plan => "read-only: no edits, no commands",
        Mode.Yolo => "nothing asks: edits and commands run at once",
        _ => "edits and commands ask first",
    };
}

/// <summary>A question for the web interface: the call waiting, and what "always" would cover.</summary>
internal sealed record ApprovalQuestion(string CallId, ToolDef Tool, JsonObject Args, string Always)
{
    /// <summary>Laya's probabilities for a command (LayaGuard), and why it asks when the mode would not.</summary>
    public string? Risk { get; init; }
}

/// <summary>
/// Decides whether a call runs: reads always do; edits and commands ask, or not,
/// by the mode; "always" remembers the answer for the session (all edits, a
/// command by its first two words, an MCP tool by name).
/// </summary>
internal sealed class Permissions(Ui ui, Mode mode)
{
    private readonly HashSet<string> _always = [];
    private readonly SemaphoreSlim _asking = new(1, 1);

    public Mode Mode { get; set; } = mode;

    /// <summary>Asks in the web interface instead of the terminal, when set.</summary>
    public Func<ApprovalQuestion, CancellationToken, Task<Approval>>? Asker { get; set; }

    /// <summary>Laya's look at commands (LayaGuard.cs), when Arena MCP offers decide.</summary>
    public LayaGuard? Guard { get; set; }

    /// <summary>
    /// Done once Arena's tools have had their first try at connecting (in the background): a command
    /// waits for it, so Laya looks at it whenever Arena offers decide, as when the session waited for Arena.
    /// </summary>
    public Task GuardReady { get; set; } = Task.CompletedTask;

    /// <summary>Null when the call may run; otherwise why not, for the model.</summary>
    public async Task<string?> CheckAsync(ToolDef tool, JsonObject args, CancellationToken ct, string? callId = null)
    {
        if (Mode == Mode.Plan && !tool.ReadOnly && tool.Kind != ToolKind.Agent)
        {
            return $"Plan mode is read-only: {tool.Name} is not allowed. Finish the plan; the person switches the mode (/mode) to carry it out.";
        }
        // A command with no time limit asks where commands ask (ask, auto-edit); yolo runs it, watched, and the person can stop it.
        var unlimited = tool.Kind == ToolKind.Shell && args.Bool("no_time_limit") == true;
        var ask = tool.AlwaysAsks || tool.Kind switch
        {
            ToolKind.Read or ToolKind.Agent => false,
            ToolKind.Remote when tool.Trusted || tool.ReadOnly => false,
            ToolKind.Edit => Mode == Mode.Ask,
            _ => Mode != Mode.Yolo,
        };
        if (tool.Kind == ToolKind.Shell && !tool.AlwaysAsks)
        {
            await GuardReady.WaitAsync(ct);
        }
        // A command Laya flags asks whatever the mode; its probabilities show whenever a command asks.
        var risk = tool.Kind == ToolKind.Shell && !tool.AlwaysAsks && Guard is { } guard ? await guard.AssessAsync(args.Str("command"), ct) : null;
        var flagged = risk?.High == true;
        if (!ask && !flagged)
        {
            return null;
        }
        var (key, always) = tool.Kind switch
        {
            ToolKind.Edit => ("edit", "for file edits"),
            ToolKind.Shell when !tool.AlwaysAsks && CommandPrefix(args.Str("command")) is { Length: > 0 } prefix => unlimited
                ? ("shell-unlimited:" + prefix, $"for `{prefix} …` with no time limit")
                : ("shell:" + prefix, $"for `{prefix} …`"),
            _ => ("tool:" + tool.Name, $"for {tool.Name}"),
        };
        var label = unlimited ? $"{tool.Name} with no time limit" : tool.Name;
        // "Always" covers the commands Laya finds as it found this one: said to an unflagged one, not one it flags or could
        // not read all of; said to a flagged one, the next flagged one; said to one it could not read all of, the next such.
        var remembered = risk?.Risky == true ? "laya:" + key : risk?.Unread is not null ? "unread:" + key : key;
        await _asking.WaitAsync(ct);
        try
        {
            if (_always.Contains(remembered))
            {
                return null;
            }
            if (Asker is null && !ui.CanAsk)
            {
                return flagged ? LayaGuard.CannotAsk(tool.Name, risk!) : $"{label} needs the person's approval, and this run cannot ask. " +
                       "Say what you would have done; the person can run again with --mode auto-edit (edits) or --mode yolo (everything).";
            }
            var answer = Asker is { } asker
                ? await asker(new ApprovalQuestion(callId ?? "", tool, args, always) { Risk = LayaGuard.Note(risk, ask) }, ct)
                : ui.Ask(LayaGuard.Question(label, risk, ask), always);
            switch (answer)
            {
                case Approval.Always:
                    _always.Add(key);
                    _always.Add(remembered);
                    return null;
                case Approval.Yes:
                    return null;
                default:
                    return $"The person declined this {tool.Name} call. Ask them what to do instead, or find another way.";
            }
        }
        finally
        {
            _asking.Release();
        }
    }

    /// <summary>"npm test -- --watch" → "npm test": what "always" allows of a command.</summary>
    public static string CommandPrefix(string? command)
    {
        var words = (command ?? "").Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(words.Length > 1 && !words[1].StartsWith('-') && !words[1].Contains('/') ? 2 : 1));
    }
}

/// <summary>
/// The tools of a session: local ones, Arena's, Argus's, the person's MCP servers', and the sub-agent.
/// A server's tools join when it connects and go when it is lost (from a background thread): each
/// read is of the list as it was at that moment.
/// </summary>
internal sealed class ToolBox(IEnumerable<ToolDef> tools)
{
    private readonly object _gate = new();
    private IReadOnlyList<ToolDef> _all = [.. tools];

    public IReadOnlyList<ToolDef> All => Volatile.Read(ref _all);

    /// <summary>What the model is offered: everything, or in plan mode only what changes nothing.</summary>
    public IEnumerable<ToolDef> Offered(Mode mode) => mode == Mode.Plan ? All.Where(t => t.ReadOnly || t.Kind == ToolKind.Agent) : All;

    public ToolDef? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    /// <summary>A sub-agent's: reading tools only, and no sub-agents of its own.</summary>
    public ToolBox ForSubAgent() => new(All.Where(t => t.ReadOnly));

    /// <summary>Changes the list as one step: what <paramref name="change"/> makes of the current one.</summary>
    public void Update(Func<IReadOnlyList<ToolDef>, IEnumerable<ToolDef>> change)
    {
        lock (_gate)
        {
            Volatile.Write(ref _all, [.. change(_all)]);
        }
    }
}
