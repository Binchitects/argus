using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Core.Chat;

namespace Llm.Api.Chat;

/// <summary>
/// A call to the model, timed for its answer's trace: how long until the first token came (the
/// prompt read, and any wait at the engine), and the engine's own timings when the stream carries
/// them (llama.cpp's last chunk; the gateway may leave them out).
/// </summary>
public sealed class ModelTiming
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public TimeSpan? FirstToken { get; private set; }

    public EngineTimings? Engine { get; private set; }

    public TimeSpan Elapsed => _clock.Elapsed;

    public void Saw(StreamEvent e)
    {
        if (e is ReasoningDelta or ContentDelta or ToolCallDelta)
        {
            FirstToken ??= _clock.Elapsed;
        }
        else if (e is EngineTimings t)
        {
            Engine = t;
        }
    }
}

/// <summary>Calls to the model added up (a round of an answer, or a sub-agent's rounds), with their tokens: their time and read and write speeds, for a trace.</summary>
public sealed class TimingTotal
{
    private double _ms, _firstMs, _readTokens, _readMs, _writtenTokens, _writeMs;
    private long _prompt, _cached, _completion;
    private int _calls, _measured;

    public void Add(ModelTiming call, int? prompt, int? cached, int? completion)
    {
        var total = call.Elapsed.TotalMilliseconds;
        _calls++;
        _ms += total;
        _firstMs += call.FirstToken?.TotalMilliseconds ?? total;
        (_prompt, _cached, _completion) = (_prompt + (prompt ?? 0), _cached + (cached ?? 0), _completion + (completion ?? 0));
        if (call.Engine is { ReadTokens: { } read, ReadMs: { } readMs, WrittenTokens: { } written, WriteMs: { } writeMs })
        {
            _measured++;
            (_readTokens, _readMs, _writtenTokens, _writeMs) = (_readTokens + read, _readMs + readMs, _writtenTokens + written, _writeMs + writeMs);
        }
    }

    /// <summary>
    /// The engine's speeds when every call reported them; else the clock's: the prompt not in the
    /// cache, read until the first token, and the tokens written after it.
    /// </summary>
    public JsonObject ToJson()
    {
        var engine = _calls > 0 && _measured == _calls;
        var (read, write) = engine
            ? (PerSecond(_readTokens, _readMs), PerSecond(_writtenTokens, _writeMs))
            : (PerSecond(_prompt - _cached, _firstMs), PerSecond(_completion, _ms - _firstMs));
        return new JsonObject
        {
            ["modelMs"] = (int)_ms, ["firstTokenMs"] = (int)_firstMs, ["readPerSecond"] = read, ["writePerSecond"] = write,
            ["speedFrom"] = engine ? "engine" : "clock",
        };
    }

    private static double? PerSecond(double tokens, double ms) => tokens > 0 && ms > 0 ? Math.Round(tokens / ms * 1000, 1) : null;
}

/// <summary>An answer's own clock, for its trace: its time in line before it started, getting ready, each round, and the round that ends it.</summary>
public sealed class AnswerClock(int queuedMs)
{
    private readonly Stopwatch _since = Stopwatch.StartNew();
    private int? _readyMs;
    private int _rounds;

    /// <summary>The answer's newest round: its end is written there.</summary>
    public ChatMessage? Last { get; private set; }

    /// <summary>A round begins: the first one ends getting ready (its tools started, the chat read).</summary>
    public ModelTiming Round(ChatMessage message)
    {
        _readyMs ??= (int)_since.ElapsedMilliseconds;
        Last = message;
        return new ModelTiming();
    }

    /// <summary>The round's trace (times and numbers only); the first round's also has the time in line and getting ready.</summary>
    public string Trace(ModelTiming timing, ChatMessage message)
    {
        var total = new TimingTotal();
        total.Add(timing, message.PromptTokens, message.CachedTokens, message.CompletionTokens);
        var json = total.ToJson();
        if (_rounds++ == 0)
        {
            json["queueMs"] = queuedMs;
            json["setupMs"] = _readyMs;
        }
        return json.ToJsonString();
    }

    /// <summary>The answer ended: its whole time goes on its last round.</summary>
    public void End()
    {
        if (Last is not null)
        {
            Last.AnswerMs = queuedMs + (int)_since.ElapsedMilliseconds;
        }
    }
}

/// <summary>A chat message as a trace reads it: no words, only sizes, times and tokens (and a delegate call's details, for its sub-agents' times).</summary>
public sealed record TraceRow(Guid Id, Guid ConversationId, Guid? ParentId, int Sequence, string Role, string? ToolName, int Chars, string? AttachmentsJson, string? DetailsJson,
    string? ContextJson, string? Model, int? PromptTokens, int? CachedTokens, int? CompletionTokens, int? ThinkingMs, int? DurationMs, MessageStatus Status, string? TraceJson,
    int? AnswerMs, DateTimeOffset CreatedAt);

public sealed record TraceTokens(long Prompt, long Cached, long Completion)
{
    /// <summary>What part of the prompt the engine read from its cache.</summary>
    public double? CacheShare => Prompt > 0 ? Math.Round((double)Cached / Prompt, 3) : null;
}

/// <summary>A part of the first round's prompt (system, tools, files, your messages...): its characters, and its share of the prompt tokens.</summary>
public sealed record PromptPart(string Kind, long Chars, int? Tokens);

/// <summary>One tool call of a sub-agent: its name, time, and how big its result was.</summary>
public sealed record AgentStepTrace(string Name, int? Ms, bool Failed, int ResultChars);

/// <summary>A sub-agent of a delegate call: its time, the model's share of it, its tokens and speeds, and its tool calls.</summary>
public sealed record AgentTrace(int Index, string Label, int Ms, string? Model, TraceTokens Tokens, int? ModelMs, double? ReadPerSecond, double? WritePerSecond, string? SpeedFrom,
    bool Failed, IReadOnlyList<AgentStepTrace> Steps)
{
    public bool Slowest { get; set; }
}

/// <summary>
/// A step of an answer's timeline: the wait in line (queue), getting ready (setup), a round of the
/// model, a tool call, or a delegate call with its sub-agents. Only what a kind has is set.
/// </summary>
public sealed record TraceStep(string Kind, string Label, int Ms)
{
    public bool Slowest { get; set; }
    public int? Index { get; init; }
    public string? Status { get; init; }
    public int? ThinkingMs { get; init; }
    public int? FirstTokenMs { get; init; }
    public TraceTokens? Tokens { get; init; }
    public double? ReadPerSecond { get; init; }
    public double? WritePerSecond { get; init; }
    public string? SpeedFrom { get; init; }
    public string? Name { get; init; }
    public int? ResultChars { get; init; }
    public int? Files { get; init; }
    public IReadOnlyList<AgentTrace>? Agents { get; init; }
}

/// <summary>Where most of an answer's time went: the step, its share, and why in words.</summary>
public sealed record TraceSlowest(string Kind, string Label, int Ms, double Share, string? Detail);

public sealed record TracePerson(Guid Id, string? UserName, string? DisplayName);

/// <summary>An answer, step by step, for admins: times, tokens, sizes and tool names, never its words.</summary>
public sealed record AnswerTrace(Guid Id, Guid ConversationId, DateTimeOffset At, string? Model, string Status, int Ms, int? QueueMs, int? SetupMs, int Rounds, int ToolCalls,
    int Agents, TraceTokens Tokens, TraceTokens AgentTokens, IReadOnlyList<PromptPart> Prompt, IReadOnlyList<TraceStep> Steps, TraceSlowest? Slowest)
{
    public TracePerson? Person { get; set; }
}

/// <summary>Answers put together from their messages, for Admin → Traces and an answer's Trace.</summary>
public static class AnswerTraces
{
    /// <summary>The answer <paramref name="messageId"/> belongs to (any of its rounds or tool calls): from its question to its last round. Null for a question.</summary>
    public static AnswerTrace? Assemble(IReadOnlyList<TraceRow> rows, Guid messageId)
    {
        var byId = rows.ToDictionary(r => r.Id);
        if (!byId.TryGetValue(messageId, out var target) || target.Role == "user")
        {
            return null;
        }
        var answer = new List<TraceRow>();
        for (var at = target; at is not null && at.Role != "user"; at = at.ParentId is { } p ? byId.GetValueOrDefault(p) : null)
        {
            answer.Insert(0, at);
            if (answer.Count > rows.Count)
            {
                break;
            }
        }
        var children = rows.Where(r => r.ParentId is not null && r.Role != "user").ToLookup(r => r.ParentId!.Value);
        for (var at = children[target.Id].MaxBy(r => r.Sequence); at is not null && answer.Count <= rows.Count; at = children[at.Id].MaxBy(r => r.Sequence))
        {
            answer.Add(at);
        }
        return Build(answer);
    }

    private static AnswerTrace Build(List<TraceRow> answer)
    {
        var rounds = answer.Where(m => m.Role == "assistant").ToList();
        var first = rounds.FirstOrDefault();
        var last = rounds.LastOrDefault() ?? answer[^1];
        var firstTrace = Parse(first?.TraceJson);
        var queueMs = Int(firstTrace, "queueMs");
        var setupMs = Int(firstTrace, "setupMs");
        var steps = new List<TraceStep>();
        if (queueMs is > 0)
        {
            steps.Add(new TraceStep("queue", "Waiting in line", queueMs.Value));
        }
        if (setupMs is { } setup)
        {
            steps.Add(new TraceStep("setup", "Getting ready", setup));
        }
        var index = 0;
        foreach (var m in answer)
        {
            if (m.Role == "assistant")
            {
                var t = Parse(m.TraceJson);
                steps.Add(new TraceStep("round", $"Model, round {++index}", m.DurationMs ?? 0)
                {
                    Index = index, Status = Status(m.Status), ThinkingMs = m.ThinkingMs, FirstTokenMs = Int(t, "firstTokenMs"),
                    Tokens = new TraceTokens(m.PromptTokens ?? 0, m.CachedTokens ?? 0, m.CompletionTokens ?? 0),
                    ReadPerSecond = Number(t, "readPerSecond"), WritePerSecond = Number(t, "writePerSecond"), SpeedFrom = t?["speedFrom"]?.GetValue<string>(),
                });
            }
            else if (m.Role == "tool")
            {
                var agents = m.ToolName == "delegate" ? Agents(m.DetailsJson) : null;
                steps.Add(new TraceStep(agents is null ? "tool" : "agents", agents is null ? m.ToolName ?? "tool" : "Sub-agents", m.DurationMs ?? 0)
                {
                    Name = m.ToolName, Status = Status(m.Status), ResultChars = m.Chars, Files = Count(m.AttachmentsJson), Agents = agents,
                });
            }
        }
        var tokens = new TraceTokens(rounds.Sum(r => (long)(r.PromptTokens ?? 0)), rounds.Sum(r => (long)(r.CachedTokens ?? 0)), rounds.Sum(r => (long)(r.CompletionTokens ?? 0)));
        var allAgents = steps.SelectMany(s => s.Agents ?? []).ToList();
        var agentTokens = new TraceTokens(allAgents.Sum(a => a.Tokens.Prompt), allAgents.Sum(a => a.Tokens.Cached), allAgents.Sum(a => a.Tokens.Completion));
        var ms = last.AnswerMs ?? steps.Sum(s => s.Ms);
        return new AnswerTrace(last.Id, last.ConversationId, answer[0].CreatedAt, last.Model, Status(last.Status), ms, queueMs, setupMs, rounds.Count,
            answer.Count(m => m.Role == "tool"), allAgents.Count, tokens, agentTokens, Prompt(first), steps, Slowest(steps, ms));
    }

    /// <summary>A delegate call's sub-agents, from what the call keeps for the page: only their times, tokens, speeds and tool names.</summary>
    private static List<AgentTrace>? Agents(string? details)
    {
        if (Parse(details)?["agents"] is not JsonArray agents)
        {
            return null;
        }
        return [.. agents.OfType<JsonObject>().Select((a, i) =>
        {
            var usage = a["usage"] as JsonObject;
            var speed = a["speed"] as JsonObject;
            var steps = (a["steps"] as JsonArray ?? []).OfType<JsonObject>().Select(s => new AgentStepTrace(
                s["name"]?.GetValue<string>() ?? "tool", Int(s, "ms"), s["isError"]?.GetValue<bool>() == true, s["result"]?.GetValue<string>()?.Length ?? 0)).ToList();
            return new AgentTrace(i + 1, $"Sub-agent {i + 1}", Int(a, "ms") ?? 0, a["model"]?.GetValue<string>(),
                new TraceTokens(Int(usage, "prompt") ?? 0, Int(usage, "cached") ?? 0, Int(usage, "completion") ?? 0), Int(speed, "modelMs"),
                Number(speed, "readPerSecond"), Number(speed, "writePerSecond"), speed?["speedFrom"]?.GetValue<string>(), a["error"] is JsonValue, steps);
        })];
    }

    /// <summary>The first round's prompt by part, in characters and in its share of the prompt tokens the model counted.</summary>
    private static List<PromptPart> Prompt(TraceRow? first)
    {
        if (Parse(first?.ContextJson) is not JsonObject parts)
        {
            return [];
        }
        var chars = parts.Select(p => (p.Key, Chars: p.Value is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0)).Where(p => p.Chars > 0).ToList();
        var sum = chars.Sum(p => p.Chars);
        return [.. chars.Select(p => new PromptPart(p.Key, p.Chars, first!.PromptTokens is { } t && sum > 0 ? (int)Math.Round((double)p.Chars / sum * t) : null))];
    }

    /// <summary>The step that took longest, a sub-agent rather than its delegate call (they run side by side), and why in words.</summary>
    private static TraceSlowest? Slowest(List<TraceStep> steps, int total)
    {
        var candidates = steps.Where(s => s.Agents is not { Count: > 0 }).Select(s => (s.Ms, Step: s, Agent: (AgentTrace?)null))
            .Concat(steps.SelectMany(s => (s.Agents ?? []).Select(a => (a.Ms, Step: s, Agent: (AgentTrace?)a))))
            .Where(c => c.Ms > 0).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }
        var (ms, step, agent) = candidates.MaxBy(c => c.Ms);
        if (agent is not null)
        {
            agent.Slowest = true;
        }
        else
        {
            step.Slowest = true;
        }
        var share = total > 0 ? Math.Round(Math.Min(1, (double)ms / total), 3) : 0;
        return agent is not null
            ? new TraceSlowest("agent", agent.Label, ms, share, AgentWhy(agent))
            : new TraceSlowest(step.Kind, step.Kind == "tool" ? step.Name ?? step.Label : step.Label, ms, share, StepWhy(step));
    }

    private static string? StepWhy(TraceStep s) => s.Kind switch
    {
        "queue" => "Other answers were being written: the model serves only a few at once.",
        "setup" => "Starting the chat's tools and reading the chat before the first round.",
        "round" when s.FirstTokenMs is { } first && first * 2 >= s.Ms && s.Tokens is { } t =>
            $"Mostly reading the prompt: {N(t.Prompt - t.Cached)} tokens not in the cache" + (s.ReadPerSecond is { } r ? $", at {N(r)} a second." : "."),
        "round" when s.Tokens is { } t => $"Mostly writing: {N(t.Completion)} tokens" + (s.WritePerSecond is { } w ? $", at {N(w)} a second." : "."),
        "tool" when s.ResultChars is { } c => $"The tool's own time; {N(c)} characters came back.",
        _ => null,
    };

    private static string AgentWhy(AgentTrace a)
    {
        var tools = a.Steps.Sum(s => s.Ms ?? 0);
        if (a.ModelMs is { } model && model >= tools)
        {
            return $"Mostly the model: {N(a.Tokens.Completion)} tokens written" + (a.WritePerSecond is { } w ? $" at {N(w)} a second" : "") +
                $", {N(a.Tokens.Prompt)} read ({N(a.Tokens.CacheShare is { } c ? c * 100 : 0)}% from the cache).";
        }
        var slowest = a.Steps.MaxBy(s => s.Ms ?? 0);
        return slowest is null ? "Its tool calls." : $"Mostly its tool calls: {slowest.Name} took {N((slowest.Ms ?? 0) / 1000.0)} s.";
    }

    private static string N(double n) => n.ToString(n >= 100 || n % 1 == 0 ? "N0" : "N1", CultureInfo.InvariantCulture);

    private static string Status(MessageStatus s) => s.ToString().ToLowerInvariant();

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? Int(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? (int)d : null;

    private static double? Number(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;

    private static int Count(string? idsJson)
    {
        try
        {
            return idsJson is not null && JsonNode.Parse(idsJson) is JsonArray a ? a.Count : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
