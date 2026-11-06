using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// A sub-agent asked for: what the person sees (<paramref name="Description"/>), its whole task, and how it works: as one of
/// the person's or the project's own sub-agents (<paramref name="Agent"/>) or as an Arena assistant (<paramref name="Assistant"/>,
/// its instructions, model, tools and skills), on another <paramref name="Model"/>, with only the <paramref name="Tools"/> named
/// (globs of the agent's own tools; null: the reading tools). <paramref name="Background"/>: the person's own task, not a call's.
/// </summary>
internal sealed record SubAgentAsk(string Description, string Prompt, string? Agent = null, string? Assistant = null, string? Model = null,
    IReadOnlyList<string>? Tools = null, bool Background = false);

/// <summary>Sub-agents: the agent's task calls and the person's background tasks, each on the board (<see cref="TaskBoard"/>).</summary>
internal sealed partial class Runtime
{
    /// <summary>The session's sub-agents and background tasks.</summary>
    public TaskBoard Tasks { get; } = new();

    /// <summary>The tools a sub-agent never has: those of the main conversation only (its plan, its jobs, more sub-agents).</summary>
    private static readonly HashSet<string> MainOnly = new(StringComparer.Ordinal)
    {
        "task", "run_steps", "plan", "stop_command", "command_output", "ask_agent", SessionChecklist.ToolName, GoalTools.CheckName,
    };

    private async Task<string> RunSubAgentAsync(SystemPrompt.Inputs inputs, string description, string prompt, string? agent, CancellationToken ct) =>
        (await RunSubAgentWorkAsync(inputs, new SubAgentAsk(description, prompt, agent), ct)).Report;

    /// <summary>
    /// The tools a sub-agent gets: the reading tools, or (asked for by name) those of the agent's own it names, never more
    /// than plan mode allows; and whether any of them changes something (it then runs alone among those that do).
    /// </summary>
    internal (ToolBox Tools, bool Writes) SubAgentTools(IReadOnlyList<string>? asked, CustomAgent? own, IReadOnlyList<string>? groups)
    {
        var reading = Tools.ForSubAgent().All;
        IEnumerable<ToolDef> chosen = reading;
        if (asked is { Count: > 0 })
        {
            chosen = Tools.All.Where(t => !MainOnly.Contains(t.Name) && t.Kind != ToolKind.Agent && (t.Group is null || Demand.IsOff(t.Group) != true)
                && asked.Any(n => PermissionRules.Glob(n, t.Name)));
            if (Permissions.Mode == Mode.Plan)
            {
                // Plan mode reads only, its sub-agents too.
                chosen = chosen.Where(t => t.ReadOnly);
            }
        }
        if (own is { Tools.Count: > 0 })
        {
            // Its own list narrows the tools; it never adds one that changes something.
            chosen = chosen.Where(t => own.Tools.Any(n => PermissionRules.Glob(n, t.Name)));
        }
        if (groups is not null)
        {
            // An assistant's tools: Arena's groups it has on (the local tools are always there).
            chosen = chosen.Where(t => t.Group is null || t.Group.StartsWith("mcp:", StringComparison.Ordinal) || groups.Contains(t.Group));
        }
        var list = chosen.ToList();
        return (new ToolBox(list), list.Any(t => !t.ReadOnly));
    }

    /// <summary>
    /// A sub-agent's run, on the board, and what it did: for a task call its start and end go to the page as the call's "agent"
    /// events (its steps stay the task's: the Tasks panel reads them), and are kept with the result.
    /// </summary>
    private async Task<(string Report, JsonObject? Work)> RunSubAgentWorkAsync(SystemPrompt.Inputs inputs, SubAgentAsk ask, CancellationToken ct,
        TaskCompletionSource<AgentTask>? onBoard = null)
    {
        CustomAgent? own = null;
        if (ask.Agent is { Length: > 0 } agent && (own = Extensions.Agents.FirstOrDefault(a => string.Equals(a.Name, agent, StringComparison.OrdinalIgnoreCase))) is null)
        {
            throw new ToolError($"There is no sub-agent {agent}{(Extensions.Agents.Count > 0 ? $": yours are {string.Join(", ", Extensions.Agents.Select(a => a.Name))}" : "")}.");
        }
        ArenaAssistant? assistant = null;
        if (ask.Assistant is { Length: > 0 } named)
        {
            assistant = await FindAssistantAsync(named, ct)
                ?? throw new ToolError($"There is no assistant {named} you may use in Arena{(_assistants is { Count: > 0 } list ? $": yours are {string.Join(", ", list.Select(a => a.Name))}" : "")}.");
        }
        var (tools, writes) = SubAgentTools(ask.Tools, own, assistant?.Tools);
        var wanted = ask.Model ?? own?.Model ?? assistant?.Model;
        if (ask.Model is { Length: > 0 } m && Models.All(x => x.Id != m))
        {
            throw new ToolError($"The gateway has no model {m}: {string.Join(", ", Models.Select(x => x.Id))}.");
        }
        var model = wanted is { } name && Models.FirstOrDefault(x => x.Id == name) is { } info
            ? new ModelState { Info = info, Thinking = assistant?.Thinking ?? Model.Thinking, DefaultLevel = Model.DefaultLevel, ContextOverride = Config.Context }
            : Model;
        var callId = ask.Background ? null : Agent.CurrentCall.Value is { Length: > 0 } call ? call : null;
        var task = Tasks.Add(ask.Background ? AgentTask.Background : AgentTask.SubAgent, ask.Description, ask.Prompt, callId, own?.Name ?? assistant?.Name, model.Name, writes,
            [.. tools.All.Select(t => t.Name)]);
        onBoard?.TrySetResult(task);
        // A sub-agent that changes things does not ask the person (nobody watches its steps): what its mode would ask is refused, and it says so.
        var quiet = new Ui(new StringReader(""), TextWriter.Null, TextWriter.Null, color: false, canAsk: false);
        var permissions = !writes ? Permissions : new Permissions(quiet, Permissions.Mode)
        {
            Rules = Permissions.Rules, Workspace = Permissions.Workspace, Guard = Permissions.Guard, GuardReady = Permissions.GuardReady, GuardWait = Permissions.GuardWait,
            CannotAsk = "A sub-agent does not ask the person: in this mode {0} needs their approval. Do without it, and say in your report what you would have done.",
        };
        var sub = new Agent
        {
            Gateway = Gateway,
            Tools = tools,
            Permissions = permissions,
            // Its own record of what code answers showed it: it sees none of the agent's conversation.
            Context = new ToolContext
            {
                Workspace = Workspace, Ui = quiet, Shell = Config.Shell, Sandbox = Sandbox, Code = CodeIndex, Shown = [], MayRead = MayRead,
                BeforeWrite = writes ? Context.BeforeWrite : null,
            },
            // Its steps are the task's: the terminal and the chat show one line of it.
            Ui = quiet,
            Model = model,
            SystemPrompt = () => SubAgentPrompt(inputs, own, assistant, writes),
            Total = Total,
            Depth = 1,
            Stream = false,
            MaxSteps = writes ? 120 : 60,
            Compaction = Compaction,
        };
        var work = new SubAgentWork(callId, ask.Description, ask.Prompt, callId is null ? null : Agent.Events, task.Id);
        work.Json["kind"] = task.Kind;
        task.Work = work;
        sub.Events = work;
        work.Start();
        // A task call's tokens count in the turn that made it; the person's own task is the session's alone.
        var spend = ask.Background ? new Spend() : Turn ?? new Spend();
        string report;
        try
        {
            report = await Tasks.RunAsync(task, async token =>
            {
                var said = await sub.RunAsync(ask.Prompt, spend, token);
                // Its work is whole before the board says it is done (a background task's news carries it).
                work.Done(model.Name, null);
                return said;
            }, ct);
        }
        catch (Exception e)
        {
            work.Done(model.Name, task.Status == AgentTask.Stopped ? task.Error : e.Message, stopped: task.Status == AgentTask.Stopped);
            if (task.Status == AgentTask.Stopped && !ct.IsCancellationRequested)
            {
                // Stopped from the Tasks panel while the turn goes on: the agent hears it and carries on without it.
                return ($"The person stopped this sub-agent ({ask.Description}) before it reported.", work.Json);
            }
            throw;
        }
        return (report.Trim().Length > 0 ? report : $"The sub-agent ({ask.Description}) returned no report.", work.Json);
    }

    /// <summary>A sub-agent's prompt: its own instructions or its assistant's first, then what every sub-agent is told.</summary>
    private static string SubAgentPrompt(SystemPrompt.Inputs inputs, CustomAgent? own, ArenaAssistant? assistant, bool writes)
    {
        var prompt = SystemPrompt.Build(inputs, subAgent: true, writes: writes);
        if (assistant is not null)
        {
            prompt = SystemPrompt.AssistantText(assistant) + "\n\n" + prompt;
        }
        return own is null ? prompt : own.Prompt + "\n\n" + prompt;
    }

    /// <summary>The sub-agents a session kept (with their calls' results, and with the notes of the person's tasks), in their order.</summary>
    private static IEnumerable<(JsonObject Work, string? Call)> KeptTasks(SessionData data) => data.Agents.OrderBy(p => p.Key)
        .SelectMany(p => p.Value.OfType<JsonObject>().Select(w => (w, p.Key < data.Messages.Count ? data.Messages[p.Key].Str("tool_call_id") : null)));

    private SystemPrompt.Inputs Inputs => _inputs ?? throw new InvalidOperationException("Code Arena has not started yet.");

    /// <summary>
    /// A task the person starts in the background: a sub-agent with the reading tools (<paramref name="edits"/>: the session's
    /// tools, one such task at a time), as an assistant or on a model if they chose. It runs while they go on with the chat; its
    /// report reaches the agent with their next message.
    /// </summary>
    public async Task<AgentTask> StartBackgroundAsync(string prompt, string? title, string? assistant, string? model, bool edits, CancellationToken ct)
    {
        var inputs = Inputs;
        if (assistant is { Length: > 0 } && await FindAssistantAsync(assistant, ct) is null)
        {
            throw new ToolError($"There is no assistant {assistant} you may use in Arena.");
        }
        if (model is { Length: > 0 } && Models.All(x => x.Id != model))
        {
            throw new ToolError($"The gateway has no model {model}.");
        }
        var description = title is { Length: > 0 } t ? Fmt.OneLine(t, 80) : Fmt.OneLine(prompt, 60);
        var ask = new SubAgentAsk(description, prompt, null, assistant, model, edits ? ["*"] : null, Background: true);
        var onBoard = new TaskCompletionSource<AgentTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                await RunSubAgentWorkAsync(inputs, ask, _tasksStop.Token, onBoard);
            }
            catch (Exception e) when (e is OperationCanceledException or GatewayException or HttpRequestException or IOException or ToolError or McpException)
            {
                // On the board as it ended: the panel and /tasks say how. One that never got there says why.
                onBoard.TrySetException(e);
            }
        }, CancellationToken.None);
        // On the board once its run began: what is waited for here, not its end.
        return await onBoard.Task.WaitAsync(ct);
    }

    // What stops the person's background tasks when Code Arena closes.
    private readonly CancellationTokenSource _tasksStop = new();
}
