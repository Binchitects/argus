using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>The model in use, what the gateway says of it, and the thinking level asked for.</summary>
internal sealed class ModelState
{
    public required ModelInfo Info { get; set; }
    public string Name => Info.Id;
    /// <summary>off, low, medium, high, xhigh, or null for the model's own default.</summary>
    public string? Thinking { get; set; }
    /// <summary>The window in tokens: the config's, else the gateway's, else 32,768.</summary>
    public int? ContextOverride { get; set; }
    public int Context => ContextOverride ?? Info.Context ?? 32_768;

    public static readonly string[] Levels = ["off", "low", "medium", "high", "xhigh"];

    /// <summary>What reaches the model's chat template: reasoning_effort, or enable_thinking false for "off" (as the chat sends it).</summary>
    public JsonObject? TemplateKwargs() => !Info.Thinking || Thinking is null or "" or "default" ? null
        : Thinking == "off" ? new JsonObject { ["enable_thinking"] = false }
        : new JsonObject { ["reasoning_effort"] = Thinking };
}

/// <summary>
/// When the session compacts itself, and how much it keeps: shares of the model's window, in
/// percent. At <see cref="At"/>, the older part of the conversation is summarized and the recent
/// part kept whole within <see cref="Target"/>.
/// </summary>
internal sealed class Compaction
{
    public const int DefaultAt = 80;
    public const int DefaultTarget = 25;
    public const int MinAt = 20;
    public const int MaxAt = 95;
    public const int MinTarget = 5;
    /// <summary>The target stays this far below the threshold, so a compaction does not start the next one.</summary>
    public const int Gap = 10;

    public int At { get; private set; } = DefaultAt;
    public int Target { get; private set; } = DefaultTarget;

    /// <summary>Sets both, or says why not (a sentence for the person); a target too close to the threshold is brought down to fit.</summary>
    public string? Set(int? at, int? target)
    {
        var newAt = at ?? At;
        if (newAt is < MinAt or > MaxAt)
        {
            return $"The threshold is from {MinAt}% to {MaxAt}% of the model's window.";
        }
        var newTarget = target ?? Math.Min(Target, newAt - Gap);
        if (newTarget < MinTarget || newTarget > newAt - Gap)
        {
            return $"What is kept is from {MinTarget}% to {newAt - Gap}% of the window ({Gap} points under the threshold, {newAt}%).";
        }
        (At, Target) = (newAt, newTarget);
        return null;
    }

    /// <summary>"70", "70%", "0.7" → 70; null when it is none of these.</summary>
    public static int? Percent(string? text)
    {
        var t = (text ?? "").Trim().TrimEnd('%').Trim();
        if (!double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0)
        {
            return null;
        }
        return (int)Math.Round(n <= 1 ? n * 100 : n);
    }

    public string Describe() => $"compacts at {At}% of the window, keeping the recent part within {Target}%";
}

/// <summary>Tokens spent: per turn, and for the session (sub-agents included).</summary>
internal sealed class Spend
{
    public long Prompt;
    public long Cached;
    public long Completion;
    public int Requests;
    public decimal? Cost;
    private readonly object _gate = new();

    public void Add(TokenUsage u, ModelInfo model)
    {
        Interlocked.Add(ref Prompt, u.Prompt);
        Interlocked.Add(ref Cached, u.Cached);
        Interlocked.Add(ref Completion, u.Completion);
        Interlocked.Increment(ref Requests);
        if (model.InputCost is { } input && model.OutputCost is { } output)
        {
            lock (_gate)
            {
                Cost = (Cost ?? 0) + (u.Prompt - u.Cached) * input + u.Cached * (model.CachedCost ?? input) + u.Completion * output;
            }
        }
    }

    public string Describe(Ui ui)
    {
        var share = Prompt > 0 ? $" ({100.0 * Cached / Prompt:0}% cached)" : "";
        var cost = Cost is { } c && c > 0 ? $" · ≈{c:0.####}" : "";
        return $"{Fmt.Tokens(Prompt)} in{share} · {Fmt.Tokens(Completion)} out · {Requests} request{(Requests == 1 ? "" : "s")}{cost}";
    }
}

/// <summary>
/// What a front end other than the terminal hears of a turn (the web interface):
/// a call for each thing the terminal shows, as it happens.
/// </summary>
internal interface IAgentEvents
{
    /// <summary>A request to the model starts: its answer streams next.</summary>
    void Step();
    void Reasoning(string text);
    void Text(string text);
    /// <summary>The model's answer is in: its tokens, when the gateway said.</summary>
    void StepDone(TokenUsage? usage);
    /// <summary>A call the model made, before it is allowed or run.</summary>
    void ToolCall(string id, string name, ToolDef? tool, string arguments);
    /// <summary>A call's result; index is its place among the answer's calls; declined when the permissions refused it.</summary>
    void ToolResult(string id, string name, int index, ToolResult result, TimeSpan took, bool declined);
    /// <summary>The history was replaced by a summary (compaction).</summary>
    void Compacted(string notice);
    void Notice(string text);
}

/// <summary>
/// The agent loop: ask the model, run the tools it calls (those that need no
/// question at once), give back their results, until it answers without a
/// call. Compacts the history when it nears the model's window.
/// </summary>
internal sealed class Agent
{
    public required GatewayClient Gateway { get; init; }
    public required ToolBox Tools { get; init; }
    public required Permissions Permissions { get; init; }
    public required ToolContext Context { get; init; }
    public required Ui Ui { get; init; }
    public required ModelState Model { get; init; }
    /// <summary>Rebuilt for every request: the mode or model may have changed.</summary>
    public required Func<string> SystemPrompt { get; init; }
    public SessionStore? Session { get; set; }
    public Spend Total { get; init; } = new();
    /// <summary>0 for the main agent, 1 for a sub-agent (indented, not streamed).</summary>
    public int Depth { get; init; }
    public int MaxSteps { get; init; } = 200;
    /// <summary>How often one turn carries on after a connection dropped mid-answer before it stops.</summary>
    public const int MaxInterruptions = 3;

    /// <summary>How many turns in a row the model may carry on from commands that ended, with no message from the person in between.</summary>
    public const int MaxFollowUps = 20;

    private int _followUps;

    /// <summary>The model carried on <see cref="MaxFollowUps"/> times with no message from the person: it waits for one.</summary>
    public bool FollowUpsSpent => _followUps > MaxFollowUps;
    public List<JsonObject> Messages { get; private set; } = [];
    /// <summary>The answer's text streams to the terminal (the main agent, not in a quiet run).</summary>
    public bool Stream { get; init; } = true;
    /// <summary>The web interface's ear on each turn; null in the terminal.</summary>
    public IAgentEvents? Events { get; set; }
    /// <summary>When the history is compacted, and to what.</summary>
    public Compaction Compaction { get; init; } = new();
    /// <summary>What the web chat added since the last turn, taken in as a turn starts (the main conversation only).</summary>
    public Func<CancellationToken, Task<IReadOnlyList<JsonObject>>>? BeforeTurn { get; set; }
    /// <summary>A checkpoint before each turn (/rewind); null: none kept (a sub-agent).</summary>
    public Checkpoints? Checkpoints { get; set; }
    /// <summary>Commits each turn's changes under this name (the main conversation only); null: no commits.</summary>
    public CommitIdentity? CommitAs { get; set; }
    /// <summary>The files turns changed but did not commit (stopped or failed), with their content then: still the harness's next turn.</summary>
    private Dictionary<string, string?> _uncommitted = new(StringComparer.Ordinal);

    private bool _parallelCalls = true;
    private long _knownTokens;
    private int _knownCount;

    public void Load(IEnumerable<JsonObject> messages)
    {
        Messages = [.. messages];
        _knownTokens = 0;
        _knownCount = 0;
        _compactedAt = 0;
        _followUps = 0;
    }

    public void Clear() => Load([]);

    /// <summary>
    /// Messages the person added in the web chat: the model hears them (the session has them written already). Said with
    /// <paramref name="say"/> (the terminal's prompt: above what is being typed), else as a line of its own.
    /// </summary>
    public void TakeIn(IReadOnlyList<JsonObject> messages, Action<string>? say = null)
    {
        if (messages.Count == 0)
        {
            return;
        }
        Messages.AddRange(messages);
        var question = messages.LastOrDefault(m => m.Str("role") == "user")?.Str("content");
        var text = $"Taken in from the web chat: {messages.Count} message{(messages.Count == 1 ? "" : "s")}" +
                   (question is { Length: > 0 } ? $", the last question \"{Fmt.OneLine(question, 80)}\"." : ".");
        if (say is null)
        {
            Ui.Info(text);
        }
        else
        {
            say(text);
        }
        Events?.Notice(text);
    }

    private void Add(JsonObject message)
    {
        Messages.Add(message);
        Session?.Message(message);
    }

    /// <summary>
    /// One turn: the person's message in, the final answer out. Cancelling stops it and keeps the history valid.
    /// It ends when the model answers: a command started with no time limit, or in the background, keeps running
    /// (listed, stoppable), and the model hears how it ended at the next step or turn. Ctrl+C or Stop also stops
    /// the commands this turn started with no time limit; those in the background run on. With no input, the turn
    /// carries on from what is there (a command that ended while nobody was asking: <see cref="ContinueAsync"/>).
    /// </summary>
    /// <param name="takeIn">Whether the web chat's news is taken in first (<see cref="BeforeTurn"/>); the IDE takes it in itself.</param>
    public async Task<string> RunAsync(string? input, Spend turn, CancellationToken ct, bool takeIn = true)
    {
        if (input is not null)
        {
            _followUps = 0;
        }
        else if (Depth == 0 && ++_followUps > MaxFollowUps)
        {
            // The model carried on from command after command with nobody there: it waits for the person now (the commands'
            // ends are told with their next message).
            Warn($"The model carried on {MaxFollowUps} times in a row from commands that ended, with no message from you: it waits for yours now.");
            return "";
        }
        if (takeIn && BeforeTurn is { } news && Depth == 0)
        {
            TakeIn(await news(ct));
        }
        var asked = input ?? "Carry on after the commands that ended";
        if (Depth == 0)
        {
            // @path: the file goes with the message.
            if (input is not null)
            {
                var (expanded, files) = Mentions.Expand(input, Context.Workspace);
                if (files.Count > 0)
                {
                    Ui.Info($"With the message: {string.Join(", ", files)}.");
                    input = expanded;
                }
            }
            Checkpoints?.Begin(Messages.Count, asked);
            Context.Turn++;
        }
        var before = await SnapshotAsync(ct);
        var done = false;
        try
        {
            var answer = await TurnAsync(input, turn, ct);
            done = true;
            await CommitAsync(before, asked, ct);
            return answer;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && Context.Jobs is { } jobs)
        {
            await StopJobsAsync(jobs, Context.Turn);
            throw;
        }
        catch (Exception e) when (Context.Jobs is { Running.Count: > 0 })
        {
            // Only the person stops a command with no time limit: a failed request to the model does not.
            Warn($"The model cannot carry on: {Fmt.OneLine(e.Message, 300)} The commands still running go on (/jobs lists them, /jobs stop N stops one); " +
                 "the model hears how each ended when it does, or with your next message.");
            throw;
        }
        finally
        {
            if (!done)
            {
                await KeepUncommittedAsync(before);
            }
        }
    }

    /// <summary>A turn of no new message: the model carries on from the commands that ended while nobody asked (their ends are its news).</summary>
    public Task<string> ContinueAsync(Spend turn, CancellationToken ct, bool takeIn = true) => RunAsync(null, turn, ct, takeIn);

    /// <summary>The repository as the turn begins, the files earlier turns left uncommitted counted as the harness's; null: no commits.</summary>
    private async Task<TurnCommits.Snapshot?> SnapshotAsync(CancellationToken ct)
    {
        if (CommitAs is null || Depth > 0 || await TurnCommits.TakeAsync(Context.Workspace.Root, ct) is not { } snapshot)
        {
            return null;
        }
        var dirty = snapshot.Dirty.Where(kv => !(_uncommitted.TryGetValue(kv.Key, out var ours) && ours == kv.Value)).ToDictionary(StringComparer.Ordinal);
        return snapshot with { Dirty = dirty };
    }

    /// <summary>
    /// Commits what the turn changed under Code Arena's name, and says so; a turn that did not end keeps its files for the next.
    /// The repository's hooks run as for any commit: after two seconds the person is told, and Ctrl+C or Stop skips the commit.
    /// </summary>
    private async Task CommitAsync(TurnCommits.Snapshot? before, string input, CancellationToken ct)
    {
        if (before is null || CommitAs is null)
        {
            return;
        }
        if (Depth == 0 && Context.Jobs?.Running.Any(j => !j.Background && j.Turn == Context.Turn) == true)
        {
            // A command the turn started still writes (a build, an install): its files are not taken half done. The turn
            // that carries on once it ends commits them.
            await KeepUncommittedAsync(before);
            return;
        }
        try
        {
            var committing = TurnCommits.CommitAsync(before, input, CommitAs, Model.Name, Session?.Id, ct);
            if (await Task.WhenAny(committing, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)) != committing)
            {
                Ui.Info("Committing the turn's changes (the repository's hooks run)… Ctrl+C or Stop skips it.");
                Events?.Notice("Committing the turn's changes (the repository's hooks run)… Stop skips it.");
            }
            if (await committing is { } commit)
            {
                if (commit.Hash is not null)
                {
                    _uncommitted.Clear();
                }
                else if (commit.Problem is not null)
                {
                    await KeepUncommittedAsync(before);
                }
                var text = commit.Describe();
                Ui.Info(text);
                Events?.Notice(text);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Warn("The commit was skipped: the turn's changes stay uncommitted, and the next turn that ends commits them.");
            await KeepUncommittedAsync(before);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Warn($"The turn's changes are not committed: {Fmt.OneLine(e.Message, 200)}");
        }
    }

    /// <summary>A turn stopped or failed: what it changed stays uncommitted, and still counts as the harness's at the next turn.</summary>
    private async Task KeepUncommittedAsync(TurnCommits.Snapshot? before)
    {
        if (before is not null && await TurnCommits.ChangedAsync(before, CancellationToken.None) is { } changed)
        {
            foreach (var (path, hash) in changed)
            {
                _uncommitted[path] = hash;
            }
        }
    }

    /// <summary>
    /// The person stopped the turn: the commands it started with no time limit stop with it (those in the background run on),
    /// and their ends are said before the turn's.
    /// </summary>
    private static async Task StopJobsAsync(CommandJobs jobs, int turn)
    {
        var running = jobs.Running.Where(j => j.Turn == turn && !j.Background).ToList();
        foreach (var job in running)
        {
            job.Stop("by the person");
        }
        // A stopped command ends in a moment (its output read to the end): the watchers hear of it while the turn is still theirs.
        await Task.WhenAny(Task.WhenAll(running.Select(j => j.Done)), Task.Delay(TimeSpan.FromSeconds(5)));
        foreach (var job in running)
        {
            job.Reported = true;
        }
    }

    private async Task<string> TurnAsync(string? input, Spend turn, CancellationToken ct)
    {
        if (input is not null)
        {
            Add(new JsonObject { ["role"] = "user", ["content"] = input });
        }
        var last = "";
        var interrupted = 0;
        for (var step = 0; step < MaxSteps; step++)
        {
            await MaybeCompactAsync(ct);
            ReportJobs();
            var printer = new Printer(Ui, Stream && Depth == 0, Events);
            Completion answer;
            Events?.Step();
            Ui.StartSpinner(step == 0 ? "Thinking" : "Working");
            try
            {
                answer = await AskAsync(printer, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Ui.StopSpinner();
                printer.End();
                // What was said before the stop stays, so the next turn follows from it.
                Add(new JsonObject { ["role"] = "assistant", ["content"] = printer.Said.Length > 0 ? printer.Said + "\n\n(stopped by the person)" : "(stopped by the person)" });
                throw;
            }
            finally
            {
                Ui.StopSpinner();
            }
            printer.End();
            Events?.StepDone(answer.Usage);
            if (answer.Usage is { } usage)
            {
                turn.Add(usage, Model.Info);
                if (!ReferenceEquals(turn, Total))
                {
                    Total.Add(usage, Model.Info);
                }
                Session?.Usage(usage);
                _knownTokens = usage.Prompt + usage.Completion;
            }
            if (answer.FinishReason == GatewayClient.Interrupted)
            {
                // The connection dropped mid-answer: what came is kept (when it said anything), and the model carries on from it.
                if (++interrupted > MaxInterruptions)
                {
                    Warn($"The connection dropped mid-answer {interrupted} times in this turn: stopping here. Send a message to carry on.");
                    if (answer.Text.Length > 0)
                    {
                        Add(answer.Message);
                    }
                    return answer.Text;
                }
                Warn("The connection dropped while the model was answering: it carries on from where it stopped.");
                if (answer.Text.Length > 0)
                {
                    Add(answer.Message);
                    Add(new JsonObject { ["role"] = "user", ["content"] = "(The connection dropped while you were answering: your answer so far is above. Carry on from where it stopped, without repeating it.)" });
                }
                continue;
            }
            Add(answer.Message);
            _knownCount = Messages.Count;
            last = answer.Text;
            if (answer.ToolCalls.Count == 0)
            {
                if (answer.FinishReason == "length")
                {
                    Warn("The answer was cut at the model's output limit.");
                }
                // A command still running goes on without the turn: the model hears how it ended next time, and a
                // command that ends while nobody asks starts a turn of its own (ContinueAsync).
                if (ReportJobs())
                {
                    continue;
                }
                return last;
            }
            await RunToolsAsync(answer.ToolCalls, ct);
        }
        Warn($"Stopped after {MaxSteps} steps.");
        return last;
    }

    /// <summary>
    /// Tells the model of the commands with no time limit that ended since it last heard, each as a
    /// command_output call of its own with the result (a tool result is data, not the person's
    /// words); false when none did.
    /// </summary>
    private bool ReportJobs()
    {
        if (Context.Jobs?.TakeEnded() is not { Count: > 0 } ended)
        {
            return false;
        }
        foreach (var job in ended)
        {
            var id = $"call_job_{job.Id}_{Guid.NewGuid():N}"[..24];
            var args = $$"""{"job":{{job.Id}}}""";
            var tool = Tools.Find("command_output");
            var result = new ToolResult(job.Notice(), job.ExitCode != 0 || job.StoppedBy is not null) { Display = Ui.Dim($"job {job.Id}: {job.Status()}") };
            Events?.Step();
            Events?.StepDone(null);
            Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "",
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = "command_output", ["arguments"] = args },
                }),
            });
            ShowCall("command_output", tool, Json.ParseObject(args));
            Events?.ToolCall(id, "command_output", tool, args);
            ShowResult("command_output", result, false);
            Events?.ToolResult(id, "command_output", 0, result, (job.Ended ?? DateTime.UtcNow) - job.Started, false);
            Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = result.Text });
        }
        return true;
    }

    private void Warn(string text)
    {
        Ui.Warn(text);
        Events?.Notice(text);
    }

    private async Task<Completion> AskAsync(IStreamSink sink, CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["model"] = Model.Name,
            ["messages"] = new JsonArray([new JsonObject { ["role"] = "system", ["content"] = SystemPrompt() }, .. Messages.Select(m => (JsonNode)m.Clone())]),
        };
        var tools = Model.Info.Tools ? Tools.Offered(Permissions.Mode).Select(t => (JsonNode)t.Spec()).ToArray() : [];
        if (tools.Length > 0)
        {
            request["tools"] = new JsonArray(tools);
            if (_parallelCalls)
            {
                request["parallel_tool_calls"] = true;
            }
        }
        if (Model.TemplateKwargs() is { } kwargs)
        {
            request["chat_template_kwargs"] = kwargs;
        }
        try
        {
            return await Gateway.CompleteAsync(request, sink, ct);
        }
        catch (GatewayException e) when (e.Status == 400 && _parallelCalls && e.Message.Contains("parallel_tool_calls", StringComparison.OrdinalIgnoreCase))
        {
            // A backend that does not take the option: one call at a time, then.
            _parallelCalls = false;
            request.Remove("parallel_tool_calls");
            return await Gateway.CompleteAsync(request, sink, ct);
        }
    }

    /// <summary>Questions first, one at a time; then every allowed call at once. Every call gets a result, in order.</summary>
    private async Task RunToolsAsync(JsonArray calls, CancellationToken ct)
    {
        var planned = new List<(string Id, string Name, ToolDef? Tool, JsonObject Args, string? Refusal, bool Declined)>();
        foreach (var call in calls.OfType<JsonObject>())
        {
            var id = call.Str("id") ?? "";
            var name = call["function"].Str("name") ?? "";
            var raw = call["function"].Str("arguments");
            var args = string.IsNullOrWhiteSpace(raw) ? new JsonObject() : Json.ParseObject(raw);
            var tool = Tools.Find(name);
            string? refusal = null;
            if (tool is null)
            {
                refusal = $"There is no tool named {name}. The tools: {string.Join(", ", Tools.Offered(Permissions.Mode).Select(t => t.Name))}.";
            }
            else if (args is null)
            {
                refusal = $"The arguments for {name} were not valid JSON: {Fmt.OneLine(raw, 200)}";
            }
            ShowCall(name, tool, args);
            Events?.ToolCall(id, name, tool, string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            var declined = false;
            if (refusal is null && tool is not null && args is not null)
            {
                try
                {
                    refusal = await Permissions.CheckAsync(tool, args, ct, id);
                    declined = refusal is not null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    FinishCalls(planned.Select(p => p.Id).Concat(calls.OfType<JsonObject>().Select(c => c.Str("id") ?? "")).Distinct(), "Stopped by the person before it ran.");
                    throw;
                }
            }
            planned.Add((id, name, tool, args ?? [], refusal, declined));
        }

        var results = new ToolResult[planned.Count];
        if (planned.Any(p => p.Refusal is null))
        {
            Ui.StartSpinner("Running");
        }
        var running = planned.Select(async (p, i) =>
        {
            ToolResult result;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (p.Refusal is not null || p.Tool is null)
            {
                result = new ToolResult(p.Refusal ?? "Not run.", true) { Display = Ui.Yellow(Fmt.OneLine(p.Refusal, 140)) };
            }
            else
            {
                try
                {
                    result = await p.Tool.Run(p.Args, Context, ct);
                }
                catch (ToolError e)
                {
                    result = new ToolResult("Error: " + e.Message, true);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    result = new ToolResult("Stopped by the person while it ran.", true);
                }
                catch (McpException e)
                {
                    result = new ToolResult($"Error from the {p.Tool.Server} tools: {e.Message}", true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or HttpRequestException)
                {
                    result = new ToolResult($"Error: {e.Message}", true);
                }
            }
            results[i] = result;
            ShowResult(p.Name, result, planned.Count > 1);
            Events?.ToolResult(p.Id, p.Name, i, result, clock.Elapsed, p.Declined);
        }).ToList();
        await Task.WhenAll(running);
        var limit = MaxToolChars;
        for (var i = 0; i < planned.Count; i++)
        {
            var text = results[i].Text;
            if (text.Length > limit)
            {
                text = text[..(limit * 2 / 3)] + $"\n\n… ({text.Length - limit:N0} characters cut: ask for less, e.g. a line range or a narrower search) …\n\n" + text[^(limit / 3)..];
            }
            Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = planned[i].Id, ["content"] = text });
        }
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>About a third of the window, in characters, and never more than 40,000.</summary>
    private int MaxToolChars => Math.Clamp(Model.Context * 3 / 2, 4_000, 40_000);

    private void FinishCalls(IEnumerable<string> ids, string text)
    {
        foreach (var id in ids)
        {
            Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = text });
        }
    }

    private string Prefix => Depth == 0 ? "" : new string(' ', Depth * 2) + "⎿ ";

    private void ShowCall(string name, ToolDef? tool, JsonObject? args)
    {
        var summary = tool?.Summary is { } s && args is not null ? s(args) : args is null ? "" : Fmt.OneLine(Json.Line(args), 80);
        var label = tool?.Server is { } server && server != "arena" ? $"{name} ({server})" : name;
        Ui.Line($"{Prefix}{Ui.Magenta("●")} {Ui.Bold(label)} {Ui.Dim(Fmt.OneLine(summary, 110))}");
    }

    /// <summary>A result under a ⎿; named when several calls ran at once, since they finish in any order.</summary>
    private void ShowResult(string name, ToolResult result, bool named)
    {
        var indent = new string(' ', Depth * 2) + "  ";
        string shown;
        if (result.Display is { } display)
        {
            shown = display;
        }
        else
        {
            var head = Fmt.Head(result.Text, Depth == 0 ? 4 : 1, 140);
            shown = result.Error ? Ui.Red(head) : Ui.Dim(head);
        }
        if (Depth > 0)
        {
            shown = Fmt.OneLine(shown, 120);
        }
        var lines = shown.Split('\n');
        var first = (named ? Ui.Dim(name + " · ") : "") + lines[0];
        Ui.Line(string.Join('\n', lines.Skip(1).Select(l => indent + "  " + l).Prepend(indent + Ui.Dim("⎿ ") + first)));
    }

    /// <summary>
    /// Compacts when the next request would pass the threshold (80% of the window unless the person chose otherwise). After a
    /// compaction that could not bring it under (the turn alone is that big), not again until it grows by a tenth of the window.
    /// </summary>
    public async Task<bool> MaybeCompactAsync(CancellationToken ct)
    {
        var at = Model.Context * Compaction.At / 100.0;
        // Never later than 95% of the window, whatever a compaction before could not bring it under.
        if (Messages.Count < 3 || Estimate() < Math.Min(Math.Max(at, _compactedAt + Model.Context / 10.0), Model.Context * 0.95))
        {
            return false;
        }
        Events?.Notice("Compacting the conversation to fit the model's window…");
        await CompactAsync(force: false, ct);
        var now = Estimate();
        _compactedAt = now >= at ? now : 0;
        return true;
    }

    /// <summary>The size a compaction left the conversation at when it stayed over the threshold (0: it got under).</summary>
    private long _compactedAt;

    /// <summary>The summary a compaction puts first, and the model's answer to it.</summary>
    private const string SummaryHead = "[The conversation so far, summarized to fit the model's window]";

    private static bool IsSummary(List<JsonObject> older) =>
        older.Count == 2 && older[0].Str("content")?.StartsWith(SummaryHead, StringComparison.Ordinal) == true;

    /// <summary>Tokens the next request will hold: the last count the gateway gave, plus what was added since (about 3.5 characters a token).</summary>
    public long Estimate()
    {
        var added = Messages.Skip(_knownCount).Sum(Size);
        if (_knownTokens > 0)
        {
            return _knownTokens + (long)(added / 3.5);
        }
        var tools = Tools.Offered(Permissions.Mode).Sum(t => Json.Line(t.Spec()).Length);
        return (long)((SystemPrompt().Length + tools + added) / 3.5);
    }

    private static int Size(JsonObject m) => (m.Str("content")?.Length ?? 0) + (m["tool_calls"] is JsonArray calls ? Json.Line(calls).Length : 0) + 10;

    /// <summary>
    /// Replaces the older part of the history with a summary the model writes.
    /// The recent part stays whole, from a message of the person's, within the
    /// compaction's target (a quarter of the window unless the person chose
    /// otherwise). Forced (/compact): everything is summarized.
    /// </summary>
    public async Task CompactAsync(bool force, CancellationToken ct)
    {
        var budget = Model.Context * Compaction.Target / 100.0 * 3.5;
        var keepFrom = Messages.Count;
        if (!force)
        {
            double size = 0;
            for (var i = Messages.Count - 1; i >= 0; i--)
            {
                size += Size(Messages[i]);
                if (size > budget)
                {
                    break;
                }
                if (Messages[i].Str("role") == "user")
                {
                    keepFrom = i;
                }
            }
            if (keepFrom == Messages.Count)
            {
                // The recent part alone is too big: from the last message of the person's, then.
                keepFrom = Messages.FindLastIndex(m => m.Str("role") == "user");
            }
        }
        var older = Messages.Take(Math.Max(keepFrom, 0)).ToList();
        var kept = Messages.Skip(Math.Max(keepFrom, 0)).ToList();
        if (!force && IsSummary(older))
        {
            // Only the last summary is older: summarizing it again would only lose more. The long results are cut instead.
            Shrink(kept);
            Messages = [.. older, .. kept];
            Session?.Compacted(Messages);
            _knownTokens = 0;
            _knownCount = 0;
            Events?.Compacted("Long tool results were cut to fit the model's window.");
            return;
        }
        if (older.Count < 2)
        {
            Shrink(kept);
            Messages = kept;
            Session?.Compacted(Messages);
            _knownTokens = 0;
            _knownCount = 0;
            Events?.Compacted("Long tool results were cut to fit the model's window.");
            return;
        }
        Ui.StartSpinner("Compacting the conversation");
        string summary;
        try
        {
            summary = await SummarizeAsync(older, ct);
        }
        finally
        {
            Ui.StopSpinner();
        }
        Messages =
        [
            new JsonObject { ["role"] = "user", ["content"] = SummaryHead + "\n\n" + summary },
            new JsonObject { ["role"] = "assistant", ["content"] = "Understood: I have the summary and carry on from there." },
            .. kept,
        ];
        if (Estimate() > Model.Context * Compaction.At / 100.0)
        {
            Shrink(kept);
        }
        Session?.Compacted(Messages);
        _knownTokens = 0;
        _knownCount = 0;
        Ui.Info($"Compacted: {older.Count} messages summarized, {kept.Count} kept.");
        Events?.Compacted($"Compacted: {older.Count} messages summarized, {kept.Count} kept.");
    }

    /// <summary>Long tool results, except the last two, cut to their start: a last resort when the recent part is too big.</summary>
    private static void Shrink(List<JsonObject> messages)
    {
        var tools = messages.Select((m, i) => (m, i)).Where(x => x.m.Str("role") == "tool").Select(x => x.i).ToList();
        foreach (var i in tools.Take(Math.Max(0, tools.Count - 2)))
        {
            if (messages[i].Str("content") is { Length: > 2000 } text)
            {
                messages[i]["content"] = text[..2000] + "\n… (cut to save room)";
            }
        }
    }

    private async Task<string> SummarizeAsync(List<JsonObject> older, CancellationToken ct)
    {
        var transcript = new StringBuilder();
        foreach (var m in older)
        {
            var content = m.Str("content") ?? "";
            switch (m.Str("role"))
            {
                case "user":
                    transcript.Append("PERSON: ").Append(content).Append("\n\n");
                    break;
                case "assistant":
                    if (content.Length > 0)
                    {
                        transcript.Append("AGENT: ").Append(content).Append("\n\n");
                    }
                    foreach (var call in (m["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        transcript.Append($"AGENT CALLED {call["function"].Str("name")} {Fmt.OneLine(call["function"].Str("arguments"), 300)}\n");
                    }
                    break;
                case "tool":
                    transcript.Append("RESULT: ").Append(content.Length > 1500 ? content[..1500] + " …" : content).Append("\n\n");
                    break;
            }
        }
        var max = (int)(Model.Context * 0.6 * 3.5);
        var text = transcript.ToString();
        if (text.Length > max)
        {
            text = "…\n" + text[^max..];
        }
        var request = new JsonObject
        {
            ["model"] = Model.Name,
            ["messages"] = new JsonArray(
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = "You summarize a coding session between a person and a coding agent so the agent can carry on from the summary alone.",
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "Summarize this session. Keep: the person's goals and instructions, decisions made, files read or changed (with paths), " +
                                  "commands run and what they showed, errors met, the state of the work and the next steps. Be concrete and brief " +
                                  "(at most 400 words). Invent nothing.\n\n---\n\n" + text,
                }),
            ["max_tokens"] = 2048,
        };
        if (Model.Info.Thinking)
        {
            request["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
        }
        var answer = await Gateway.CompleteAsync(request, null, ct);
        if (answer.Usage is { } usage)
        {
            Total.Add(usage, Model.Info);
            Session?.Usage(usage);
        }
        return answer.Text.Trim() is { Length: > 0 } s ? s : "(no summary)";
    }

    /// <summary>Streams the answer to the terminal: reasoning dimmed, then the text; and to the web interface when it listens.</summary>
    private sealed class Printer(Ui ui, bool show, IAgentEvents? events) : IStreamSink
    {
        private bool _reasoning;
        private bool _any;
        private readonly StringBuilder _said = new();

        public string Said => _said.ToString();

        public void Reasoning(string text)
        {
            events?.Reasoning(text);
            if (!show)
            {
                return;
            }
            if (!_reasoning)
            {
                ui.Write(ui.Dim("thinking: "));
                _reasoning = true;
            }
            ui.Write(ui.Dim(text));
            _any = true;
        }

        public void Text(string text)
        {
            _said.Append(text);
            events?.Text(text);
            if (!show)
            {
                return;
            }
            if (_reasoning)
            {
                ui.Write("\n\n");
                _reasoning = false;
            }
            ui.Write(text);
            _any = true;
        }

        public void End()
        {
            if (_any)
            {
                ui.EndLine();
            }
        }
    }
}
