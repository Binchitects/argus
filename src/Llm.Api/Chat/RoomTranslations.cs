using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

public sealed record TranslateRequest(string? Language, IReadOnlyList<Guid>? Ids);

/// <summary>
/// Rooms in every language: each reader reads the room in theirs. A message is translated into a language once, by the
/// model for small steps (as the reader who first asks: their credit), and kept for everyone who reads in it; one already
/// in that language is kept as such and never asked again.
/// </summary>
public static class RoomTranslations
{
    /// <summary>The languages a room can be read in: their codes, English names and own names.</summary>
    public static readonly IReadOnlyList<(string Code, string Name, string Own)> Languages =
    [
        ("en", "English", "English"), ("fa", "Persian", "فارسی"), ("ar", "Arabic", "العربية"), ("de", "German", "Deutsch"), ("fr", "French", "Français"),
        ("es", "Spanish", "Español"), ("it", "Italian", "Italiano"), ("pt", "Portuguese", "Português"), ("nl", "Dutch", "Nederlands"), ("sv", "Swedish", "Svenska"),
        ("pl", "Polish", "Polski"), ("tr", "Turkish", "Türkçe"), ("ru", "Russian", "Русский"), ("uk", "Ukrainian", "Українська"), ("hi", "Hindi", "हिन्दी"),
        ("ur", "Urdu", "اردو"), ("zh", "Chinese", "中文"), ("ja", "Japanese", "日本語"), ("ko", "Korean", "한국어"), ("id", "Indonesian", "Bahasa Indonesia"),
    ];

    /// <summary>Messages asked at once, and how much of each is translated.</summary>
    public const int MostIds = 60;
    public const int MostChars = 4000;
    /// <summary>Messages the model is given in one question, and their characters at most.</summary>
    public const int Batch = 10;
    public const int BatchChars = 6000;

    public static void MapRoomTranslations(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/rooms").PersonOnly();
        g.MapGet("/languages", () => Results.Ok(Languages.Select(l => new { code = l.Code, name = l.Name, own = l.Own })));
        g.MapPost("/{id:guid}/translations", TranslateAsync);
    }

    private static readonly System.Text.CompositeFormat Instructions = System.Text.CompositeFormat.Parse("""
        You translate messages of a group chat. Translate each message into {0}, as a person who speaks it would write it: the
        same tone, the same Markdown, and names, @mentions, links, code and numbers as they are. A message already in {0} is "=".
        Answer with JSON only: {{"messages": [{{"id": "…", "text": "…"}}]}}, one for each message, in their order.
        """);

    private static async Task<IResult> TranslateAsync(Guid id, TranslateRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, OneQuestion one,
        CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        if (await RoomAccess.FindAsync(db, id, me, Need.Read, ct) is not { IsRoom: true })
        {
            return Results.NotFound();
        }
        if (Languages.FirstOrDefault(l => l.Code == body.Language) is not { Code: { } code } language)
        {
            return AuthEndpoints.Problem(400, "language", "Choose a language the room can be read in.");
        }
        var ids = (body.Ids ?? []).Distinct().Take(MostIds).ToList();
        var kept = await db.MessageTranslations.AsNoTracking().Where(t => ids.Contains(t.MessageId) && t.Language == code).ToDictionaryAsync(t => t.MessageId, t => t.Text, ct);
        var have = kept.Keys.ToList();
        var missing = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ConversationId == id && ids.Contains(m.Id) && !have.Contains(m.Id) && (m.Role == "user" || m.Role == "assistant") && m.Content != "")
            .OrderBy(m => m.Sequence).Select(m => new { m.Id, m.Content }).ToListAsync(ct);
        string? problem = null;
        foreach (var batch in Batches(missing.Select(m => (m.Id, Text: m.Content.Length > MostChars ? m.Content[..MostChars] : m.Content))))
        {
            var (made, why) = await AskAsync(one, me, language.Name, batch, ct);
            if (why is not null)
            {
                problem = why;
                break;
            }
            foreach (var (mid, text) in made)
            {
                kept[mid] = text;
                db.MessageTranslations.Add(new MessageTranslation { MessageId = mid, Language = code, Text = text });
            }
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Another reader's ask kept them first: theirs stand.
                db.ChangeTracker.Clear();
            }
        }
        return Results.Ok(new
        {
            translations = kept.Select(k => new { id = k.Key, text = k.Value, same = k.Value is null }),
            error = problem,
        });
    }

    private static IEnumerable<List<(Guid Id, string Text)>> Batches(IEnumerable<(Guid Id, string Text)> messages)
    {
        var batch = new List<(Guid Id, string Text)>();
        var chars = 0;
        foreach (var m in messages)
        {
            if (batch.Count > 0 && (batch.Count >= Batch || chars + m.Text.Length > BatchChars))
            {
                yield return batch;
                batch = [];
                chars = 0;
            }
            batch.Add(m);
            chars += m.Text.Length;
        }
        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>One question for a batch: the translations by message (null: in that language already); or why not.</summary>
    private static async Task<(Dictionary<Guid, string?> Made, string? Problem)> AskAsync(OneQuestion one, AppUser me, string language, List<(Guid Id, string Text)> batch,
        CancellationToken ct)
    {
        var messages = new JsonArray([.. batch.Select((m, i) => (JsonNode)new JsonObject { ["id"] = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), ["text"] = m.Text })]);
        var asked = await one.AskAsync(me, new JsonObject { ["messages"] = messages }.ToJsonString(Mcp.Plain), string.Format(System.Globalization.CultureInfo.InvariantCulture, Instructions, language),
            "small", Math.Min(8000, batch.Sum(m => m.Text.Length) * 2 + 400), "translate", ct);
        if (asked.Problem is not null)
        {
            return ([], "The messages could not be translated now.");
        }
        var made = new Dictionary<Guid, string?>();
        var text = asked.Text ?? "";
        var (start, end) = (text.IndexOf('{'), text.LastIndexOf('}'));
        if (start < 0 || end <= start)
        {
            return ([], "The model did not translate them this time.");
        }
        try
        {
            var answer = JsonNode.Parse(text[start..(end + 1)], null, new JsonDocumentOptions { AllowTrailingCommas = true });
            foreach (var m in (answer?["messages"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (int.TryParse(m["id"]?.ToString(), out var n) && n >= 1 && n <= batch.Count && m["text"] is JsonValue v && v.TryGetValue<string>(out var t))
                {
                    var trimmed = t.Trim();
                    made[batch[n - 1].Id] = trimmed == "=" || trimmed == batch[n - 1].Text.Trim() ? null : trimmed;
                }
            }
        }
        catch (JsonException)
        {
            return ([], "The model did not translate them this time.");
        }
        return (made, null);
    }
}
