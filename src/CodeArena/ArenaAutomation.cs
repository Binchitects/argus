using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// Arena's agents (Agent Studio) and the person's scheduled tasks there, from Code Arena with their key: listed, run, each
/// run followed (/agents, /schedule, and the IDE's Automation view); and Code Arena's own schedules (Schedules.cs) beside them.
/// </summary>
internal sealed partial class Runtime
{
    /// <summary>How long /agents waits for a run it started before it says where to follow it.</summary>
    public static TimeSpan AgentWait { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>A call to Arena about agents or tasks: its answer, or why there is none, as a sentence.</summary>
    public async Task<(JsonNode? Body, string? Problem)> ArenaAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        if (Config.Url is not { Length: > 0 } url || Config.ApiKey is not { Length: > 0 } key)
        {
            return (null, "Sign in to Arena first (code-arena login).");
        }
        try
        {
            using var req = ChatSync.Request(method, url.TrimEnd('/') + "/api/code-arena" + path, key, body);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            using var res = await Http.SendAsync(req, limit.Token);
            var text = await res.Content.ReadAsStringAsync(limit.Token);
            var parsed = text.Length > 0 ? JsonNode.Parse(text) : null;
            return res.IsSuccessStatusCode ? (parsed, null) : (null, SessionChecklist.Why(res.StatusCode, parsed));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            return (null, $"Arena did not answer: {e.Message}");
        }
    }

    /// <summary>The IDE's Automation view: Code Arena's schedules, Arena's scheduled tasks and agents (each with why not, when not).</summary>
    public async Task<JsonObject> AutomationAsync(CancellationToken ct)
    {
        var agents = ArenaAsync(HttpMethod.Get, "/agents", null, ct);
        var tasks = ArenaAsync(HttpMethod.Get, "/scheduled", null, ct);
        var (a, aWhy) = await agents;
        var (t, tWhy) = await tasks;
        return new JsonObject
        {
            ["schedules"] = new JsonArray([.. Schedules.Load(Env.Paths).Select(s =>
            {
                var o = s.ToJson();
                o["next"] = s.NextAt().ToString("o");
                o["when"] = Schedules.When(s);
                return (JsonNode)o;
            })]),
            ["folder"] = Workspace.Root,
            ["agents"] = a?.DeepClone(),
            ["agentsProblem"] = aWhy,
            ["tasks"] = t?.DeepClone(),
            ["tasksProblem"] = tWhy,
        };
    }

    /// <summary>One of Arena's agents run with this text: its run's id, or why not.</summary>
    public async Task<(string? Run, string? Problem)> RunArenaAgentAsync(string id, string text, CancellationToken ct)
    {
        var (body, problem) = await ArenaAsync(HttpMethod.Post, $"/agents/{Uri.EscapeDataString(id)}/run", new JsonObject { ["text"] = text }, ct);
        return problem is null ? ((body as JsonObject)?.Str("id"), null) : (null, problem);
    }

    /// <summary>An agent's run as Arena has it: its status, and its steps.</summary>
    public Task<(JsonNode? Body, string? Problem)> ArenaAgentRunAsync(string run, CancellationToken ct) => ArenaAsync(HttpMethod.Get, $"/agents/runs/{Uri.EscapeDataString(run)}", null, ct);

    /// <summary>One of the person's scheduled tasks in Arena run now; why not, when not.</summary>
    public async Task<string?> RunArenaTaskAsync(string id, CancellationToken ct) => (await ArenaAsync(HttpMethod.Post, $"/scheduled/{Uri.EscapeDataString(id)}/run", null, ct)).Problem;

    /// <summary>/agents: Arena's agents numbered; N (or a name) and text runs one and waits for it, saying what it gave.</summary>
    public async Task<string> AgentsCommandAsync(string arg, CancellationToken ct)
    {
        var (listed, problem) = await ArenaAsync(HttpMethod.Get, "/agents", null, ct);
        if (problem is not null)
        {
            return problem;
        }
        var agents = (listed as JsonArray ?? []).OfType<JsonObject>().ToList();
        var words = arg.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return agents.Count == 0
                ? "You have no agents in Arena yet: make one there, under Agent Studio."
                : string.Join("\n", agents.Select((g, i) => $"{i + 1}. {g.Str("name")}{(g.Bool("enabled") == false ? " (off)" : "")}" +
                    $"{(g.Str("description") is { Length: > 0 } d ? $": {Fmt.OneLine(d, 100)}" : "")}{(g.Bool("mine") == true ? "" : $" ({g.Str("owner")}'s)")}")) +
                  "\n/agents N TEXT runs one with that text.";
        }
        var chosen = int.TryParse(words[0], out var n) && n >= 1 && n <= agents.Count ? agents[n - 1]
            : agents.FirstOrDefault(g => string.Equals(g.Str("name"), words[0], StringComparison.OrdinalIgnoreCase));
        if (chosen?.Str("id") is not { } id)
        {
            return $"There is no agent {words[0]}: /agents lists yours.";
        }
        var (run, why) = await RunArenaAgentAsync(id, words.Length > 1 ? words[1] : "", ct);
        if (run is null)
        {
            return why ?? "Arena did not start it.";
        }
        var until = DateTime.UtcNow + AgentWait;
        while (DateTime.UtcNow < until)
        {
            var (detail, _) = await ArenaAgentRunAsync(run, ct);
            if (detail is JsonObject d && d.Str("status") is "done" or "failed" or "stopped" or "declined")
            {
                return AgentRunText(d);
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return $"{chosen.Str("name")} is still running: Agent Studio in Arena shows it (run {run}).";
    }

    /// <summary>A finished run as the terminal says it: how it ended, and what each step gave.</summary>
    public static string AgentRunText(JsonObject run)
    {
        var text = new StringBuilder($"{run.Str("workflow")}: {run.Str("status")}{(run.Str("error") is { Length: > 0 } e ? $" ({e})" : "")}\n");
        foreach (var s in (run["steps"] as JsonArray ?? []).OfType<JsonObject>())
        {
            text.Append($"- {s.Str("name") ?? s.Str("kind")}: {s.Str("status")}");
            if (s["output"] is JsonNode output && output.ToString() is { Length: > 0 } said)
            {
                text.Append(": ").Append(Fmt.OneLine(said, 300));
            }
            text.Append('\n');
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// /schedule: Code Arena's schedules and Arena's scheduled tasks; add, remove, on, off, run (as code-arena schedule);
    /// "arena N" runs one of Arena's now.
    /// </summary>
    public async Task<string> ScheduleCommandAsync(string arg, CancellationToken ct)
    {
        var words = Schedules.Words(arg);
        if (words is ["arena", var which])
        {
            var (listed, problem) = await ArenaAsync(HttpMethod.Get, "/scheduled", null, ct);
            var tasks = (listed as JsonArray ?? []).OfType<JsonObject>().ToList();
            var chosen = int.TryParse(which, out var n) && n >= 1 && n <= tasks.Count ? tasks[n - 1] : tasks.FirstOrDefault(t => string.Equals(t.Str("name"), which, StringComparison.OrdinalIgnoreCase));
            return problem ?? (chosen?.Str("id") is { } id ? await RunArenaTaskAsync(id, ct) ?? $"{chosen.Str("name")} runs now in Arena: its answer comes to you there." : $"There is no task {which} in Arena: /schedule lists them.");
        }
        using var output = new StringWriter();
        var code = await Schedules.CommandAsync([.. words], new Schedules.Place(Env.Paths, Workspace.Root, Env.Exe ?? Environment.ProcessPath ?? "code-arena", output, output), ct);
        var said = output.ToString().TrimEnd();
        if (words.Count == 0 && code == 0)
        {
            var (listed, problem) = await ArenaAsync(HttpMethod.Get, "/scheduled", null, ct);
            var tasks = (listed as JsonArray ?? []).OfType<JsonObject>().ToList();
            said = "Here (Code Arena's): " + said + "\n\nIn Arena: " + (problem ?? (tasks.Count == 0 ? "none." : "\n" + string.Join("\n", tasks.Select((t, i) =>
                $"{i + 1}. {t.Str("name")}: {t.Str("cron")}{(t.Bool("enabled") == false ? " (off)" : "")}")) + "\n/schedule arena N runs one now."));
        }
        return said;
    }
}
