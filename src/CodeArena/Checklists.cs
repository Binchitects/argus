using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// Checklists, kept in Arena (Workspace → Checklists): /checklist turns one of the person's on in this session's chat, the
/// model goes through it and ticks each item with checklist_check, and the web chat shows the same ticks. Needs the
/// session synced to Arena (its chat holds the checklist).
/// </summary>
internal static class SessionChecklist
{
    public const string ToolName = "checklist_check";

    public const string Instructions =
        "The person turned on a checklist in this session: before you say a piece of work is done, go through every item that applies: do it " +
        "or check it with your tools, then tick it with checklist_check, saying in a few words how (\"dotnet test: 42 passed\"); an item that " +
        "does not apply is ticked with why. Do not tick what you did not do or check. End by saying which items are left, if any.";

    public static ToolDef Tool() => new()
    {
        Name = ToolName,
        Kind = ToolKind.Read,
        Description = "Ticks an item of the checklist the person turned on in this session (or takes the tick back), with how it was done or checked.",
        Parameters = LocalTools.Schema("""
            {"type":"object","properties":{
              "item":{"type":"integer","description":"The item's number (1 is the first)"},
              "done":{"type":"boolean","description":"false takes the tick back (default true)"},
              "note":{"type":"string","description":"How it was done or checked, or why it does not apply: short, concrete"}},
             "required":["item","note"]}
            """),
        Summary = a => $"item {a.Int("item")}",
        Run = (a, c, ct) => c.Checklist is { } tick
            ? tick(a, ct)
            : Task.FromResult(new ToolResult("No checklist is on in this session (the person turns one on with /checklist).", Error: true)),
    };

    /// <summary>The checklist as the model and the terminal read it: each item numbered, ticked or not, with its note.</summary>
    public static string Text(JsonObject run)
    {
        var items = run["items"] as JsonArray ?? [];
        var text = new StringBuilder($"Checklist \"{run.Str("name")}\" ({run.Int("done")} of {run.Int("total")} done):\n");
        foreach (var i in items.OfType<JsonObject>())
        {
            text.Append($"{i.Int("number")}. [{(i.Bool("done") == true ? "x" : " ")}] {i.Str("text")}{(i.Str("note") is { } note ? $" ({note})" : "")}\n");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>Arena's answer, or why there is none, as a sentence.</summary>
    public static string Why(HttpStatusCode status, JsonNode? body) =>
        (body as JsonObject)?.Str("error") ?? (body as JsonObject)?.Str("detail") ?? $"Arena answered {(int)status}.";
}

/// <summary>The session's checklist: /checklist (the terminal and the IDE), and checklist_check for the model.</summary>
internal sealed partial class Runtime
{
    // The checklist on in the session's chat, as Arena last said (null: none).
    private JsonObject? _checklist;

    /// <summary>The checklist on in this session's chat, as Arena keeps it (null: none, or no chat in Arena yet).</summary>
    public JsonObject? Checklist => Volatile.Read(ref _checklist);

    /// <summary>
    /// /checklist: none, the one on and the person's checklists; NAME or N turns one on; off turns it off; tick N and
    /// untick N tick by hand. The text to show.
    /// </summary>
    public async Task<string> ChecklistCommandAsync(string arg, CancellationToken ct)
    {
        if (Sync is null || Config.Url is not { Length: > 0 } url || Config.ApiKey is not { Length: > 0 } key)
        {
            return "Checklists are kept in Arena: sign in to it first (code-arena login), so this session syncs to a chat there.";
        }
        var words = arg.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            switch (words.FirstOrDefault()?.ToLowerInvariant())
            {
                case null:
                {
                    var lists = await ListAsync(url, key, ct);
                    await RefreshChecklistAsync(ct);
                    var mine = lists.Count == 0
                        ? "You have no checklists yet: make them in Arena (Workspace → Checklists)."
                        : "Yours: " + string.Join(", ", lists.Select((c, i) => $"{i + 1}. {c.Str("name")}")) + " (/checklist N or NAME turns one on).";
                    return (Checklist is { } on ? SessionChecklist.Text(on) + "\n" : "No checklist is on in this session.\n") + mine;
                }
                case "off":
                    await CallAsync(HttpMethod.Delete, "", null, ct);
                    Volatile.Write(ref _checklist, null);
                    return "The checklist is off.";
                case "tick" or "untick" when words.Length == 2 && int.TryParse(words[1], out var number):
                    return SessionChecklist.Text(await TickAsync(number, words[0].Equals("tick", StringComparison.OrdinalIgnoreCase), null, "person", ct));
                default:
                {
                    var lists = await ListAsync(url, key, ct);
                    var chosen = int.TryParse(arg.Trim(), out var n) && n >= 1 && n <= lists.Count ? lists[n - 1]
                        : lists.FirstOrDefault(c => string.Equals(c.Str("name"), arg.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (chosen is null)
                    {
                        return $"You have no checklist named {arg.Trim()}: /checklist lists yours.";
                    }
                    if (Sync.Conversation is null)
                    {
                        return "This session has no chat in Arena yet: say something first, then turn the checklist on.";
                    }
                    var run = await CallAsync(HttpMethod.Put, "", new JsonObject { ["checklistId"] = chosen.Str("id") }, ct);
                    Volatile.Write(ref _checklist, run);
                    return SessionChecklist.Text(run!) + "\nThe model goes through it, and ticks each item as it does it.";
                }
            }
        }
        catch (ChecklistError e)
        {
            return e.Message;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return $"Arena did not answer: {e.Message}";
        }
    }

    /// <summary>The checklist on in the chat, read again from Arena (the web page may have ticked or changed it); as it was when Arena does not answer.</summary>
    public async Task RefreshChecklistAsync(CancellationToken ct)
    {
        try
        {
            if (Sync?.ChatCallAsync(HttpMethod.Get, "/checklist", null, ct) is not { } call || await call is not { } answer || (int)answer.Status >= 500)
            {
                return;
            }
            Volatile.Write(ref _checklist, answer.Status == HttpStatusCode.OK ? answer.Body as JsonObject : null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            // Arena away: the checklist as last read.
        }
    }

    /// <summary>checklist_check: the model ticks an item.</summary>
    private async Task<ToolResult> CheckItemAsync(JsonObject a, CancellationToken ct)
    {
        try
        {
            var run = await TickAsync(a.Int("item") ?? 0, a.Bool("done") != false, a.Str("note"), "model", ct);
            return new ToolResult(SessionChecklist.Text(run));
        }
        catch (ChecklistError e)
        {
            return new ToolResult(e.Message, Error: true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new ToolResult($"Arena did not answer: {e.Message}", Error: true);
        }
    }

    private async Task<JsonObject> TickAsync(int item, bool done, string? note, string by, CancellationToken ct)
    {
        var run = await CallAsync(HttpMethod.Post, "/tick", new JsonObject { ["item"] = item, ["done"] = done, ["note"] = note, ["by"] = by }, ct)
            ?? throw new ChecklistError("No checklist is on in this session.");
        Volatile.Write(ref _checklist, run);
        return run;
    }

    private async Task<JsonObject?> CallAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        if (Sync?.ChatCallAsync(method, "/checklist" + path, body, ct) is not { } call || await call is not { } answer)
        {
            throw new ChecklistError("This session has no chat in Arena yet: say something first.");
        }
        if (answer.Status is HttpStatusCode.NoContent)
        {
            return null;
        }
        if ((int)answer.Status >= 400)
        {
            throw new ChecklistError(SessionChecklist.Why(answer.Status, answer.Body));
        }
        return answer.Body as JsonObject;
    }

    private async Task<List<JsonObject>> ListAsync(string url, string key, CancellationToken ct)
    {
        using var req = ChatSync.Request(HttpMethod.Get, url.TrimEnd('/') + "/api/code-arena/checklists", key, null);
        using var res = await Http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            throw new ChecklistError(SessionChecklist.Why(res.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null));
        }
        return [.. (JsonNode.Parse(text) as JsonArray ?? []).OfType<JsonObject>()];
    }
}

internal sealed class ChecklistError(string message) : Exception(message);
