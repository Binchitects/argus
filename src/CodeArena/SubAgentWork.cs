using System.Diagnostics;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// What one sub-agent (a task call, or a task the person started) does, as it does it: its thinking, its tool calls with
/// their results, and its words, kept as the page shows a sub-agent's work (AgentWork) for the Tasks panel to read while it
/// runs and to go beside the call's result in the session. The chat itself hears only that it started and how it ended
/// (the chat's "agent" events, by the call's id): its steps are the task's own, never the chat's.
/// </summary>
internal sealed class SubAgentWork(string? callId, string title, string instructions, IAgentEvents? page, string? task = null) : IAgentEvents
{
    /// <summary>A tool's result as kept and sent: enough to read what it found.</summary>
    private const int MaxResultChars = 8_000;

    private readonly object _gate = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly JsonArray _steps = [];
    private string _reasoning = "";
    private string _text = "";
    private long _prompt;
    private long _cached;
    private long _completion;
    private bool _counted;
    private int _calls;
    private string? _doing;

    /// <summary>The work as the page shows it once done (AgentWork).</summary>
    public JsonObject Json { get; } = new() { ["title"] = title, ["instructions"] = instructions, ["status"] = "running" };

    /// <summary>How many tool calls it made so far.</summary>
    public int Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls;
            }
        }
    }

    /// <summary>What it does now, in a few words (its last tool call, or "thinking", "writing"); null before its first step.</summary>
    public string? Doing
    {
        get
        {
            lock (_gate)
            {
                return _doing;
            }
        }
    }

    /// <summary>The tokens it used so far: prompt, of them read from the cache, and written.</summary>
    public (long Prompt, long Cached, long Completion) Usage
    {
        get
        {
            lock (_gate)
            {
                return (_prompt, _cached, _completion);
            }
        }
    }

    /// <summary>Its work as it stands, while it runs too: what the Tasks panel opens.</summary>
    public JsonObject Snapshot()
    {
        lock (_gate)
        {
            var o = (JsonObject)Json.DeepClone();
            if (o.Str("status") == "running")
            {
                o["reasoning"] = _reasoning;
                o["text"] = _text;
                o["steps"] = _steps.DeepClone();
                o["ms"] = (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
                o["usage"] = _counted ? new JsonObject { ["prompt"] = _prompt, ["cached"] = _cached, ["completion"] = _completion } : null;
            }
            return o;
        }
    }

    public void Start() => Send(new JsonObject { ["event"] = "start", ["title"] = title, ["instructions"] = instructions, ["task"] = task });

    /// <summary>It ended: with its report, or with what went wrong (<paramref name="stopped"/>: the person or the agent stopped it).</summary>
    public void Done(string? model, string? error, bool stopped = false)
    {
        var ms = (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        JsonObject? usage;
        lock (_gate)
        {
            usage = _counted ? new JsonObject { ["prompt"] = _prompt, ["cached"] = _cached, ["completion"] = _completion } : null;
            Json["reasoning"] = _reasoning;
            Json["text"] = _text;
            Json["steps"] = _steps.DeepClone();
            Json["status"] = error is null ? "done" : stopped ? "stopped" : "failed";
            Json["error"] = error;
            Json["ms"] = ms;
            Json["model"] = model;
            Json["usage"] = usage?.DeepClone();
            _doing = null;
        }
        Send(new JsonObject { ["event"] = "done", ["error"] = error, ["ms"] = ms, ["model"] = model, ["usage"] = usage, ["task"] = task });
    }

    private void Send(JsonObject e)
    {
        if (callId is null)
        {
            return;
        }
        e["type"] = "agent";
        e["id"] = callId;
        e["index"] = 0;
        page?.SubAgent(e);
    }

    void IAgentEvents.Step()
    {
        lock (_gate)
        {
            _doing = "thinking";
        }
    }

    void IAgentEvents.Reasoning(string text)
    {
        lock (_gate)
        {
            _reasoning += text;
        }
    }

    void IAgentEvents.Text(string text)
    {
        lock (_gate)
        {
            _text += text;
            _doing = "writing";
        }
    }

    void IAgentEvents.StepDone(TokenUsage? usage)
    {
        if (usage is null)
        {
            return;
        }
        lock (_gate)
        {
            (_prompt, _cached, _completion, _counted) = (_prompt + usage.Prompt, _cached + usage.Cached, _completion + usage.Completion, true);
        }
    }

    void IAgentEvents.ToolCall(string id, string name, ToolDef? tool, string arguments)
    {
        var args = CodeArena.Json.ParseObject(arguments);
        var summary = tool?.Summary is { } s && args is not null ? s(args) : "";
        lock (_gate)
        {
            // Words before a tool call were on the way to it: what it says last is its answer (as the page folds them).
            _text = "";
            _calls++;
            _doing = Fmt.OneLine(summary.Length > 0 ? $"{name} {summary}" : name, 100);
            _steps.Add(new JsonObject { ["id"] = id, ["name"] = name, ["arguments"] = arguments });
        }
    }

    void IAgentEvents.ToolResult(string id, string name, int index, ToolResult result, TimeSpan took, bool declined)
    {
        var text = result.Text.Length <= MaxResultChars ? result.Text : result.Text[..MaxResultChars] + $"\n… ({result.Text.Length - MaxResultChars:N0} more characters)";
        var error = result.Error && !declined;
        lock (_gate)
        {
            if (_steps.OfType<JsonObject>().LastOrDefault(s => s.Str("id") == id) is { } step)
            {
                step["result"] = text;
                step["isError"] = error;
            }
        }
    }

    void IAgentEvents.Compacted(string notice)
    {
    }

    void IAgentEvents.Notice(string text)
    {
    }
}
