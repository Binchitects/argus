using System.Text;
using System.Text.Json.Nodes;

namespace ArenaCode;

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
        Add(new JsonObject { ["role"] = "user", ["content"] = input });
        var last = "";
        for (var step = 0; step < MaxSteps; step++)
        {
            await MaybeCompactAsync(ct);
            var printer = new Printer(Ui, Stream && Depth == 0);
            Completion answer;
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
                    Ui.Warn("The answer was cut at the model's output limit.");
                }
                return last;
            }
            await RunToolsAsync(answer.ToolCalls, ct);
        }
        Ui.Warn($"Stopped after {MaxSteps} steps.");
        return last;
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
        var planned = new List<(string Id, string Name, ToolDef? Tool, JsonObject Args, string? Refusal)>();
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
            if (refusal is null && tool is not null && args is not null)
            {
                try
                {
                    refusal = await Permissions.CheckAsync(tool, args, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    FinishCalls(planned.Select(p => p.Id).Concat(calls.OfType<JsonObject>().Select(c => c.Str("id") ?? "")).Distinct(), "Stopped by the person before it ran.");
                    throw;
                }
            }
            planned.Add((id, name, tool, args ?? [], refusal));
        }

        var results = new ToolResult[planned.Count];
        if (planned.Any(p => p.Refusal is null))
        {
            Ui.StartSpinner("Running");
        }
        var running = planned.Select(async (p, i) =>
        {
            ToolResult result;
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

    /// <summary>Compacts when the next request would pass 80% of the window.</summary>
    public async Task<bool> MaybeCompactAsync(CancellationToken ct)
    {
        if (Messages.Count < 3 || Estimate() < Model.Context * 0.8)
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
    /// The recent part stays whole, from a message of the person's, within a
    /// quarter of the window. Forced (/compact): everything is summarized.
    /// </summary>
    public async Task CompactAsync(bool force, CancellationToken ct)
    {
        var budget = Model.Context * 0.25 * 3.5;
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
        if (Estimate() > Model.Context * 0.8)
        {
            Shrink(kept);
        }
        Session?.Compacted(Messages);
        _knownTokens = 0;
        _knownCount = 0;
        Ui.Info($"Compacted: {older.Count} messages summarized, {kept.Count} kept.");
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

    /// <summary>Streams the answer to the terminal: reasoning dimmed, then the text.</summary>
    private sealed class Printer(Ui ui, bool show) : IStreamSink
    {
        private bool _reasoning;
        private bool _any;
        private readonly StringBuilder _said = new();

        public string Said => _said.ToString();

        public void Reasoning(string text)
        {
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
