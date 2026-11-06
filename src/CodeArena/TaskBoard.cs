using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// A sub-agent at work, or done: one the agent started (a task call) or one the person started in the background. Its
/// own context, its own tools and its own transcript (<see cref="Work"/>); the chat shows one line of it, the Tasks panel
/// and /tasks the rest.
/// </summary>
internal sealed class AgentTask
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Stopped = "stopped";

    /// <summary>"subagent": the agent's task call; "background": the person's own.</summary>
    public const string SubAgent = "subagent";
    public const string Background = "background";

    /// <summary>t1, t2…: its number in this run.</summary>
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public required string Prompt { get; init; }
    /// <summary>The task call it answers (a sub-agent of the agent's); null for the person's.</summary>
    public string? CallId { get; init; }
    /// <summary>The Arena assistant it works as, or the person's or project's own sub-agent: its name.</summary>
    public string? As { get; init; }
    public string Model { get; set; } = "";
    /// <summary>It may change files or run commands: it waits for any other such task to end first.</summary>
    public bool Writes { get; init; }
    /// <summary>The names of the tools it has.</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Started { get; set; }
    public DateTimeOffset? Ended { get; set; }
    public string Status { get; set; } = Queued;
    public string? Report { get; set; }
    public string? Error { get; set; }
    /// <summary>A background task's report was given to the agent (with the person's next message).</summary>
    public bool Told { get; set; }
    /// <summary>Kept from an earlier run of the session (read from its file): it cannot be stopped, only read.</summary>
    public bool Kept { get; init; }
    public SubAgentWork? Work { get; set; }
    /// <summary>Its work as the session kept it, for a task <see cref="Kept"/>.</summary>
    public JsonObject? KeptWork { get; init; }
    internal CancellationTokenSource Stop { get; } = new();

    public bool Over => Status is Done or Failed or Stopped;
}

/// <summary>
/// The session's tasks: the agent's sub-agents and the person's background tasks, numbered in the order they began. At most
/// <see cref="MostAtOnce"/> run at once (the rest wait their turn), and of those that change files or run commands only one
/// at a time: those that only read run side by side, as several of them never step on each other.
/// </summary>
internal sealed class TaskBoard
{
    /// <summary>Sub-agents running at once at most: each is a request to the model at a time.</summary>
    public const int MostAtOnce = 4;
    /// <summary>The tasks kept on the board: the oldest that ended go first past it.</summary>
    public const int MostKept = 200;

    private readonly object _gate = new();
    private readonly List<AgentTask> _tasks = [];
    private readonly SemaphoreSlim _slots = new(MostAtOnce, MostAtOnce);
    private readonly SemaphoreSlim _writer = new(1, 1);
    private int _next;

    /// <summary>Told when a task begins or ends (the terminal says a background one ended; the IDE reads the board again).</summary>
    public event Action<AgentTask>? Changed;

    public IReadOnlyList<AgentTask> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _tasks];
            }
        }
    }

    public int RunningCount
    {
        get
        {
            lock (_gate)
            {
                return _tasks.Count(t => !t.Over);
            }
        }
    }

    public AgentTask? Find(string id)
    {
        lock (_gate)
        {
            return _tasks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase) || (t.CallId is not null && t.CallId == id));
        }
    }

    /// <summary>A new task, queued, with the next number.</summary>
    public AgentTask Add(string kind, string title, string prompt, string? callId, string? @as, string model, bool writes, IReadOnlyList<string> tools)
    {
        lock (_gate)
        {
            var task = new AgentTask
            {
                Id = $"t{++_next}", Kind = kind, Title = title, Prompt = prompt, CallId = callId, As = @as, Model = model, Writes = writes, Tools = tools,
            };
            _tasks.Add(task);
            while (_tasks.Count > MostKept && _tasks.FindIndex(t => t.Over) is >= 0 and var old)
            {
                _tasks.RemoveAt(old);
            }
            return task;
        }
    }

    /// <summary>
    /// Runs a task when a place is free (and, for one that writes, when no other that writes runs): stopped by
    /// <paramref name="ct"/> (the turn that started it) or by <see cref="StopTask"/>. Its report, or its error thrown.
    /// </summary>
    public async Task<string> RunAsync(AgentTask task, Func<CancellationToken, Task<string>> run, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, task.Stop.Token);
        var token = linked.Token;
        var slot = false;
        var writer = false;
        try
        {
            await _slots.WaitAsync(token);
            slot = true;
            if (task.Writes)
            {
                await _writer.WaitAsync(token);
                writer = true;
            }
            task.Started = DateTimeOffset.UtcNow;
            task.Status = AgentTask.Running;
            Changed?.Invoke(task);
            var report = await run(token);
            task.Report = report;
            task.Status = AgentTask.Done;
            return report;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            task.Status = AgentTask.Stopped;
            task.Error = task.Stop.IsCancellationRequested ? "Stopped by the person." : "Stopped with the turn that started it.";
            throw;
        }
        catch (Exception e)
        {
            task.Status = AgentTask.Failed;
            task.Error = e.Message;
            throw;
        }
        finally
        {
            if (writer)
            {
                _writer.Release();
            }
            if (slot)
            {
                _slots.Release();
            }
            task.Ended = DateTimeOffset.UtcNow;
            Changed?.Invoke(task);
        }
    }

    /// <summary>Stops a task that is waiting or running; false when there is none such.</summary>
    public bool StopTask(string id)
    {
        if (Find(id) is not { Over: false, Kept: false } task)
        {
            return false;
        }
        task.Stop.Cancel();
        return true;
    }

    /// <summary>Stops every task still at work (a new session, Code Arena closing).</summary>
    public void StopAll()
    {
        foreach (var task in All.Where(t => !t.Over && !t.Kept))
        {
            task.Stop.Cancel();
        }
    }

    /// <summary>
    /// The reports of the person's background tasks that ended since the agent last heard, as a note for it (null: none):
    /// given with the person's next message, as a command that ended is.
    /// </summary>
    public (string Text, JsonArray Work)? TakeNews()
    {
        List<AgentTask> ended;
        lock (_gate)
        {
            // Over, and its work whole (it is made whole just after the board hears a failure).
            ended = [.. _tasks.Where(t => t.Kind == AgentTask.Background && t.Over && !t.Told && !t.Kept && t.Work?.Json.Str("status") != "running")];
            foreach (var t in ended)
            {
                t.Told = true;
            }
        }
        if (ended.Count == 0)
        {
            return null;
        }
        var parts = ended.Select(t => t.Status == AgentTask.Done
            ? $"The person's background task {t.Id} ({t.Title}) is done. Its report:\n{t.Report}"
            : $"The person's background task {t.Id} ({t.Title}) {(t.Status == AgentTask.Stopped ? "was stopped" : "failed")}: {t.Error}");
        // Their work goes with the note, so the session keeps it (and Arena, with the chat) as it keeps a task call's.
        return ("(Background tasks ended while you were not asked.)\n\n" + string.Join("\n\n", parts), new JsonArray([.. ended.Select(t => t.Work?.Json.DeepClone()).OfType<JsonNode>()]));
    }

    /// <summary>A new session: the board is cleared, what still runs stopped, and the session's own sub-agents put back as they were kept.</summary>
    public void Reset(IEnumerable<JsonObject> kept)
    {
        StopAll();
        lock (_gate)
        {
            _tasks.Clear();
            _next = 0;
            foreach (var work in kept)
            {
                var status = work.Str("status") is { } s && s is AgentTask.Done or AgentTask.Failed or AgentTask.Stopped ? s : AgentTask.Stopped;
                _tasks.Add(new AgentTask
                {
                    Id = $"t{++_next}", Kind = work.Str("kind") == AgentTask.Background ? AgentTask.Background : AgentTask.SubAgent, Title = work.Str("title") ?? "task", Prompt = work.Str("instructions") ?? "", Kept = true,
                    Model = work.Str("model") ?? "", Status = status, Error = work.Str("error"), Report = work.Str("text"), KeptWork = work, Told = true,
                });
            }
        }
    }

    /// <summary>A task as the Tasks panel lists it (<paramref name="whole"/>: with its transcript, as the page shows a sub-agent's work).</summary>
    public static JsonObject ToJson(AgentTask t, bool whole)
    {
        var work = t.Work;
        var (prompt, cached, completion) = work?.Usage ?? (0, 0, 0);
        var kept = t.KeptWork;
        if (kept?["usage"] is JsonObject u)
        {
            (prompt, cached, completion) = (u.Long("prompt") ?? 0, u.Long("cached") ?? 0, u.Long("completion") ?? 0);
        }
        long? ms = t.Started is { } started ? (long)((t.Ended ?? DateTimeOffset.UtcNow) - started).TotalMilliseconds : kept?.Long("ms");
        var o = new JsonObject
        {
            ["id"] = t.Id,
            ["kind"] = t.Kind,
            ["title"] = t.Title,
            ["status"] = t.Status,
            ["call"] = t.CallId,
            ["as"] = t.As,
            ["model"] = t.Model,
            ["writes"] = t.Writes,
            ["created"] = t.Created.ToString("o"),
            ["started"] = t.Started?.ToString("o"),
            ["ended"] = t.Ended?.ToString("o"),
            ["ms"] = ms,
            ["tokens"] = new JsonObject { ["prompt"] = prompt, ["cached"] = cached, ["completion"] = completion },
            ["calls"] = work?.Calls ?? (kept?["steps"] as JsonArray)?.Count ?? 0,
            ["doing"] = t.Over ? null : work?.Doing ?? (t.Status == AgentTask.Queued ? "waiting for a free place" : null),
            ["error"] = t.Error,
            ["kept"] = t.Kept,
        };
        if (whole)
        {
            o["prompt"] = t.Prompt;
            o["tools"] = new JsonArray([.. t.Tools.Select(n => (JsonNode)n)]);
            o["report"] = t.Report;
            o["work"] = work?.Snapshot() ?? kept?.DeepClone();
        }
        return o;
    }
}
