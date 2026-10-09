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
    public List<JsonObject> Messages { get; private set; } = [];
    /// <summary>The answer's text streams to the terminal (the main agent, not in a quiet run).</summary>
    public bool Stream { get; init; } = true;
    /// <summary>The web interface's ear on each turn; null in the terminal.</summary>
    public IAgentEvents? Events { get; set; }
    /// <summary>When the history is compacted, and to what.</summary>
    public Compaction Compaction { get; init; } = new();

    private bool _parallelCalls = true;
    private long _knownTokens;
    private int _knownCount;

    public void Load(IEnumerable<JsonObject> messages)
    {
        Messages = [.. messages];
        _knownTokens = 0;
        _knownCount = 0;
    }

    public void Clear() => Load([]);

    private void Add(JsonObject message)
    {
        Messages.Add(message);
        Session?.Message(message);
    }

    /// <summary>One turn: the person's message in, the final answer out. Cancelling stops it and keeps the history valid.</summary>
    public async Task<string> RunAsync(string input, Spend turn, CancellationToken ct)
    {
        try
        {
            return await TurnAsync(input, turn, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && Context.Jobs is { } jobs)
        {
            // The person stopped the turn: the commands it ran with no time limit stop with it.
            jobs.StopAll("by the person");
            foreach (var job in jobs.All)
            {
                job.Reported = true;
            }
            throw;
        }
    }

    private async Task<string> TurnAsync(string input, Spend turn, CancellationToken ct)
    {
        Add(new JsonObject { ["role"] = "user", ["content"] = input });
        var last = "";
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
            Add(answer.Message);
            _knownCount = Messages.Count;
            last = answer.Text;
            if (answer.ToolCalls.Count == 0)
            {
                if (answer.FinishReason == "length")
                {
                    Warn("The answer was cut at the model's output limit.");
                }
                // A command it started with no time limit still runs: the turn waits for it, however long, then the model carries on.
                if (Context.Jobs is { } jobs && jobs.Running.Count > 0)
                {
                    await WaitForJobsAsync(jobs, ct);
                }
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

    /// <summary>Until one of the running commands ends: no time limit, only the person's Ctrl+C or Stop ends the wait.</summary>
    private async Task WaitForJobsAsync(CommandJobs jobs, CancellationToken ct)
    {
        var running = jobs.Running;
        var what = string.Join(", ", running.Select(j => $"job {j.Id} ({Fmt.OneLine(j.Command, 40)})"));
        Ui.Info($"Waiting for {what} to end: no time limit, Ctrl+C stops {(running.Count == 1 ? "it" : "them")}.");
        Ui.StartSpinner($"Waiting for {(running.Count == 1 ? $"job {running[0].Id}" : $"{running.Count} jobs")}");
        try
        {
            await jobs.WaitAnyAsync(ct);
        }
        finally
        {
            Ui.StopSpinner();
        }
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

    /// <summary>Compacts when the next request would pass the threshold (80% of the window unless the person chose otherwise).</summary>
    public async Task<bool> MaybeCompactAsync(CancellationToken ct)
    {
        if (Messages.Count < 3 || Estimate() < Model.Context * Compaction.At / 100.0)
        {
            return false;
        }
        await CompactAsync(force: false, ct);
        return true;
    }

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
            new JsonObject { ["role"] = "user", ["content"] = "[The conversation so far, summarized to fit the model's window]\n\n" + summary },
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
