using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Gateway;

/// <summary>Past costs worked out again: requests at the gateway, and the parts of chat answers (rounds, sub-agents, pictures, video, speech).</summary>
public sealed record Recalculation(DateTimeOffset From, DateTimeOffset To, bool OnlyFree, SpendChange Requests, SpendChange Answers, bool Applied);

/// <summary>
/// Admin → Settings → Prices → Recalculate past costs: what was booked before a model had a price
/// (or before cached input had one) is worked out again at today's prices, over a time range, in the
/// gateway's request log (what usage, credit and the dashboards count) and in the chats (what each
/// answer shows). First counted, then applied on request, and audited. It never runs by itself.
/// </summary>
public sealed class CostRecalculation(AppDbContext db, PriceBook prices, SpendLog log, Identity.Audit audit)
{
    /// <summary>The tool functions whose calls cost what they made.</summary>
    private static readonly string[] Media = ["generate_image", "generate_video", "speak"];

    public async Task<Recalculation> RunAsync(DateTimeOffset from, DateTimeOffset to, bool onlyFree, bool apply, CancellationToken ct = default)
    {
        var models = await prices.ModelsAsync(ct);
        var requests = await log.RepriceAsync(from, to, onlyFree, models, prices.Defaults, apply, ct);
        var answers = await AnswersAsync(from, to, onlyFree, apply, ct);
        var result = new Recalculation(from, to, onlyFree, requests, answers, apply);
        if (apply)
        {
            var day = (DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            await audit.WriteAsync("usage.recalculate", $"{day(from)} to {day(to)} UTC", detail:
                $"{(onlyFree ? "costs booked as free" : "every cost")}: {requests.Rows:N0} requests, {SpendLog.Money(requests.Before)} → {SpendLog.Money(requests.After)}; " +
                $"{answers.Rows:N0} parts of chat answers, {SpendLog.Money(answers.Before)} → {SpendLog.Money(answers.After)}");
        }
        return result;
    }

    /// <summary>
    /// The chats' rounds and tool calls of the range, by when they ran, at today's prices: a round by its
    /// tokens; a picture, video or speech call by the files it made; a delegate call by its sub-agents'
    /// tokens and the files their own such calls made (its details name them).
    /// </summary>
    private async Task<SpendChange> AnswersAsync(DateTimeOffset from, DateTimeOffset to, bool onlyFree, bool apply, CancellationToken ct)
    {
        var parts = await db.ChatMessages.AsNoTracking()
            .Where(m => m.AnswerId != null && m.CreatedAt >= from && m.CreatedAt < to && (!onlyFree || m.Cost == null || m.Cost == 0)
                && (m.PromptTokens != null || (m.Role == "tool" && m.Status == MessageStatus.Complete && Media.Contains(m.ToolName!))))
            .Select(m => new { m.Id, m.ToolName, m.Model, m.PromptTokens, m.CachedTokens, m.CompletionTokens, m.Cost, m.AttachmentsJson, Details = m.ToolName == "delegate" ? m.DetailsJson : null })
            .ToListAsync(ct);
        // What each made: a media call its own files; a delegate call its sub-agents' media calls' files.
        var made = parts.ToDictionary(p => p.Id, p => p.Details is not null ? AgentsMade(p.Details)
            : p.PromptTokens is null ? [(p.ToolName!, ChatService.ParseIds(p.AttachmentsJson).ToList())] : new List<(string Tool, List<Guid> Files)>());
        var fileIds = made.Values.SelectMany(calls => calls.SelectMany(c => c.Files)).ToHashSet();
        var files = await db.ChatAttachments.AsNoTracking().Where(a => fileIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Kind, a.Seconds, Chars = a.Text.Length }).ToDictionaryAsync(a => a.Id, ct);
        var changed = new Dictionary<Guid, decimal>();
        var before = 0m;
        var unpriced = 0;
        foreach (var p in parts)
        {
            // A round at its model's prices; a delegate call's sub-agents at theirs (one model for all of a call's).
            decimal? cost = p.PromptTokens is { } prompt ? await prices.CostAsync(p.Model ?? AgentsModel(p.Details), prompt, p.CachedTokens ?? 0, p.CompletionTokens ?? 0, ct) : 0;
            foreach (var (tool, ids) in made[p.Id])
            {
                // What it made, by its files; with them deleted since (retention), it is not known any more.
                var kept = ids.Where(files.ContainsKey).Select(id => files[id]).ToList();
                cost = kept.Count == 0 ? null : cost + tool switch
                {
                    "generate_image" => prices.Images(kept.Count(f => f.Kind == "image")),
                    "generate_video" => prices.Video(kept.Sum(f => f.Seconds ?? 0)),
                    _ => prices.Speech(kept.Sum(f => f.Chars)),
                };
            }
            if (cost is not { } c)
            {
                unpriced++;
                continue;
            }
            if (p.Cost is { } old && Math.Abs(old - c) < 1e-12m)
            {
                continue;
            }
            changed[p.Id] = c;
            before += p.Cost ?? 0;
        }
        if (apply)
        {
            foreach (var chunk in changed.Keys.Chunk(1000))
            {
                foreach (var m in await db.ChatMessages.Where(m => chunk.Contains(m.Id)).ToListAsync(ct))
                {
                    m.Cost = changed[m.Id];
                }
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
        }
        return new SpendChange(changed.Count, before, changed.Values.Sum(), unpriced);
    }

    /// <summary>A delegate call's sub-agents' picture, video and speech calls that made files, from its details.</summary>
    private static List<(string Tool, List<Guid> Files)> AgentsMade(string details)
    {
        try
        {
            return [.. (JsonNode.Parse(details)?["agents"] as JsonArray ?? []).OfType<JsonObject>()
                .SelectMany(a => (a["steps"] as JsonArray ?? []).OfType<JsonObject>())
                .Where(s => s["name"]?.GetValue<string>() is { } n && Media.Contains(n) && s["files"] is JsonArray { Count: > 0 })
                .Select(s => (s["name"]!.GetValue<string>(), ((JsonArray)s["files"]!).OfType<JsonObject>()
                    .Select(f => Guid.TryParse(f["id"]?.ToString(), out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList()))];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The model a delegate call's sub-agents ran on, from its details.</summary>
    private static string? AgentsModel(string? details)
    {
        try
        {
            return details is null ? null : JsonNode.Parse(details)?["agents"]?[0]?["model"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
