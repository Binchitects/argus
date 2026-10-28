using System.Globalization;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Quality;

/// <summary>
/// Admin → Prompt log, with prompts.review: everyone's questions and their answers, newest first. The chat's (Code Arena's
/// sessions kept in Arena and rooms among them) are read from the chats themselves, as long as they are kept; the API
/// requests from the prompt log (Gateway:LogPrompts, Retention:PromptLogDays). Every search is audited: who looked, for what.
/// </summary>
public static class PromptLogEndpoints
{
    /// <summary>The most a search gives at once; the next page goes on from the last one's time.</summary>
    public const int Page = 50;

    /// <summary>The most of a question or an answer a row carries.</summary>
    public const int Shown = 4000;

    public static void MapPromptLog(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/prompts").RequirePermissions(Llm.Core.Access.Permissions.PromptsReview, Llm.Core.Access.Permissions.PromptsReview);
        g.MapGet("", SearchAsync);
    }

    private sealed record Row(string Source, DateTimeOffset At, Guid Person, string? Model, string Question, string? Answer, Guid? Chat, string? Title, bool Deleted, int? Tokens);

    private static string Cut(string? s) => s is null ? "" : s.Length <= Shown ? s : s[..(Shown - 1)] + "…";

    /// <summary>What the person typed of a Code Arena message, without the files that went with it (after "&lt;attached&gt;").</summary>
    private static string Typed(string content) => content.IndexOf("\n\n<attached>", StringComparison.Ordinal) is var at and >= 0 ? content[..at] : content;

    private static string Like(string q) => "%" + q.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

    private static async Task<IResult> SearchAsync(string? q, string? person, string? source, DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset? before, AppDbContext db,
        Audit audit, TimeProvider clock, CancellationToken ct)
    {
        source = source is "chat" or "api" ? source : "all";
        var text = q?.Trim() is { Length: > 0 } t ? t : null;
        Guid? who = null;
        if (person?.Trim() is { Length: > 0 } named)
        {
            var lower = named.ToLowerInvariant();
            who = await db.Users.AsNoTracking().Where(u => u.UserName == lower || u.Email == named).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
            if (who is null)
            {
                return AuthEndpoints.Problem(404, "person", $"There is no one named {named}.");
            }
        }
        var end = before ?? to ?? clock.GetUtcNow().AddMinutes(1);
        var start = from ?? DateTimeOffset.MinValue;
        var rows = new List<Row>();
        if (source != "api")
        {
            var questions = db.ChatMessages.AsNoTracking().Where(m => m.Role == "user" && m.CreatedAt < end && m.CreatedAt >= start)
                .Join(db.Conversations.AsNoTracking(), m => m.ConversationId, c => c.Id, (m, c) => new { m, c })
                .Where(x => who == null || (x.m.AuthorId ?? x.c.UserId) == who);
            if (text is not null)
            {
                questions = questions.Where(x => EF.Functions.ILike(x.m.Content, Like(text), "\\"));
            }
            var asked = await questions.OrderByDescending(x => x.m.CreatedAt).Take(Page)
                .Select(x => new { x.m.Id, x.m.Content, x.m.CreatedAt, Person = x.m.AuthorId ?? x.c.UserId, Chat = x.c.Id, x.c.Title, Deleted = x.c.DeletedAt != null }).ToListAsync(ct);
            var ids = asked.Select(a => (Guid?)a.Id).ToList();
            // Each question's answer: its first message (whose parent is the question) and the rest of it (AnswerId), its last words.
            var firsts = await db.ChatMessages.AsNoTracking().Where(a => a.Role == "assistant" && ids.Contains(a.ParentId))
                .Select(a => new { a.Id, a.ParentId, a.Model }).ToListAsync(ct);
            var firstIds = firsts.Select(f => (Guid?)f.Id).ToList();
            var answers = (await db.ChatMessages.AsNoTracking().Where(a => a.Role == "assistant" && a.Content != "" && (firstIds.Contains(a.AnswerId) || firstIds.Contains(a.Id)))
                .Select(a => new { Answer = a.AnswerId ?? a.Id, a.Content, a.Sequence, Tokens = a.CompletionTokens }).ToListAsync(ct))
                .GroupBy(a => a.Answer).ToDictionary(g => g.Key, g => (Text: g.OrderBy(a => a.Sequence).Last().Content, Tokens: g.Sum(a => a.Tokens ?? 0)));
            foreach (var a in asked)
            {
                var first = firsts.FirstOrDefault(f => f.ParentId == a.Id);
                var answer = first is not null && answers.TryGetValue(first.Id, out var said) ? said : default;
                rows.Add(new Row("chat", a.CreatedAt, a.Person, first?.Model, Cut(Typed(a.Content)), answer.Text is null ? null : Cut(answer.Text), a.Chat, a.Title, a.Deleted,
                    answer.Tokens > 0 ? answer.Tokens : null));
            }
        }
        if (source != "chat")
        {
            var api = db.PromptLog.AsNoTracking().Where(p => p.At < end && p.At >= start && (who == null || p.UserId == who));
            if (text is not null)
            {
                api = api.Where(p => EF.Functions.ILike(p.Prompt, Like(text), "\\") || EF.Functions.ILike(p.Answer!, Like(text), "\\"));
            }
            rows.AddRange((await api.OrderByDescending(p => p.At).Take(Page).ToListAsync(ct)).Select(p => new Row("api", p.At, p.UserId, p.Model, Cut(p.Prompt), p.Answer is null ? null : Cut(p.Answer),
                null, null, false, (p.PromptTokens ?? 0) + (p.CompletionTokens ?? 0) is > 0 and var n ? n : null)));
        }
        var page = rows.OrderByDescending(r => r.At).Take(Page).ToList();
        var people = page.Select(r => r.Person).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => people.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => new { u.Id, u.UserName, u.DisplayName }, ct);
        await audit.WriteAsync("prompts.viewed", person, detail: string.Join("; ", new[]
        {
            $"source {source}", text is null ? null : $"text \"{text}\"", person is null ? null : $"person {person}",
            from is null ? null : $"from {from.Value.ToString("O", CultureInfo.InvariantCulture)}", to is null ? null : $"to {to.Value.ToString("O", CultureInfo.InvariantCulture)}",
            $"{page.Count} shown",
        }.OfType<string>()));
        return Results.Ok(new
        {
            items = page.Select(r => new
            {
                r.Source, r.At, person = names.GetValueOrDefault(r.Person), r.Model, r.Question, r.Answer, r.Chat, r.Title, r.Deleted, r.Tokens,
            }),
            next = page.Count == Page ? page[^1].At : (DateTimeOffset?)null,
        });
    }
}
