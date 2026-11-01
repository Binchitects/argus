using System.Diagnostics;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// What one sub-agent (a task call) does, as it does it: its thinking, its tool calls with their results, and its words,
/// sent to the page as the chat's "agent" events (the call's id, the agent's place) and kept as the page shows a
/// sub-agent's work (AgentWork), to go beside the call's result in the session.
/// </summary>
internal sealed class SubAgentWork(string callId, string title, string instructions, IAgentEvents? page) : IAgentEvents
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

    /// <summary>The work as the page shows it once done (AgentWork).</summary>
    public JsonObject Json { get; } = new() { ["title"] = title, ["instructions"] = instructions, ["status"] = "running" };

    public void Start() => Send(new JsonObject { ["event"] = "start", ["title"] = title, ["instructions"] = instructions });

    /// <summary>It ended: with its report, or with what went wrong.</summary>
    public void Done(string? model, string? error)
    {
        var ms = (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        JsonObject? usage = _counted ? new JsonObject { ["prompt"] = _prompt, ["cached"] = _cached, ["completion"] = _completion } : null;
        lock (_gate)
        {
            Json["reasoning"] = _reasoning;
            Json["text"] = _text;
            Json["steps"] = _steps.DeepClone();
            Json["status"] = error is null ? "done" : "failed";
            Json["error"] = error;
            Json["ms"] = ms;
            Json["model"] = model;
            Json["usage"] = usage?.DeepClone();
        }
        Send(new JsonObject { ["event"] = "done", ["error"] = error, ["ms"] = ms, ["model"] = model, ["usage"] = usage });
    }

    private void Send(JsonObject e)
    {
        e["type"] = "agent";
        e["id"] = callId;
        e["index"] = 0;
        page?.SubAgent(e);
    }

    void IAgentEvents.Step()
    {
    }

    void IAgentEvents.Reasoning(string text)
    {
        lock (_gate)
        {
            _reasoning += text;
        }
        Send(new JsonObject { ["event"] = "reasoning", ["text"] = text });
    }

    void IAgentEvents.Text(string text)
    {
        lock (_gate)
        {
            _text += text;
        }
        Send(new JsonObject { ["event"] = "content", ["text"] = text });
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
        lock (_gate)
        {
            // Words before a tool call were on the way to it: what it says last is its answer (as the page folds them).
            _text = "";
            _steps.Add(new JsonObject { ["id"] = id, ["name"] = name, ["arguments"] = arguments });
        }
        Send(new JsonObject { ["event"] = "tool_call", ["call"] = new JsonObject { ["id"] = id, ["name"] = name, ["arguments"] = arguments } });
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
        Send(new JsonObject { ["event"] = "tool_result", ["call"] = new JsonObject { ["id"] = id }, ["text"] = text, ["isError"] = error });
    }

    void IAgentEvents.Compacted(string notice)
    {
    }

    void IAgentEvents.Notice(string text)
    {
    }
}
