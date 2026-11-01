using System.Globalization;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Quality;

/// <summary>
/// Admin → Prompt log, with prompts.review: everyone's questions and their answers, newest first, in any state: waiting,
/// running, done, failed (and why), stopped, refused. The chat's (Code Arena's sessions kept in Arena and rooms among them)
/// are read from the chats themselves, as long as they are kept, a question's state from its answer (running while its chat
/// answers it); the API requests from the prompt log (Gateway:LogPrompts, Retention:PromptLogDays), which keeps each from its
/// arrival. Every search is audited: who looked, for what.
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
        g.MapGet("/files/{source}/{id:guid}", FileAsync);
    }

    /// <summary>A file of a question (a picture, a document) or one its answer made (a picture, a file Python wrote), read at /files/SOURCE/ID.</summary>
    private sealed record FileRef(Guid Id, string Name, string Kind, string ContentType, long Size);

    private sealed record Row(string Source, DateTimeOffset At, Guid Person, string? Model, string Question, string? Answer, Guid? Chat, string? Title, bool Deleted, int? Tokens,
        string State, string? Error, int? DurationMs, Guid? Id = null, Guid? Answered = null)
    {
        public IReadOnlyList<FileRef> Asked { get; set; } = [];
        public IReadOnlyList<FileRef> Made { get; set; } = [];
    }

    /// <summary>The states a search may ask for; "all" (or none) is every one.</summary>
    public static readonly string[] States = [PromptLogEntry.Waiting, PromptLogEntry.Running, PromptLogEntry.Done, PromptLogEntry.Failed, PromptLogEntry.Stopped, PromptLogEntry.Refused];

    /// <summary>With a state asked for, how many of the newest questions a page looks through for it.</summary>
    private const int Scanned = 1000;

    /// <summary>A chat question's state, from its answer's rounds: the last one's, a stop or a failure anywhere winning.</summary>
    private static (string State, string? Error) ChatState(IReadOnlyList<(MessageStatus Status, string? Error)> rounds, bool answering)
    {
        if (answering)
        {
            return (PromptLogEntry.Running, null);
        }
        if (rounds.Count == 0)
        {
            return (PromptLogEntry.Failed, "No answer was kept.");
        }
        if (rounds.FirstOrDefault(r => r.Status == MessageStatus.Failed) is { Status: MessageStatus.Failed } failed)
        {
            return (PromptLogEntry.Failed, failed.Error);
        }
        return rounds.Any(r => r.Status == MessageStatus.Stopped) ? (PromptLogEntry.Stopped, null) : (PromptLogEntry.Done, null);
    }

    private static string Cut(string? s) => s is null ? "" : s.Length <= Shown ? s : s[..(Shown - 1)] + "…";

    /// <summary>What the person typed of a Code Arena message, without the files that went with it (after "&lt;attached&gt;").</summary>
    private static string Typed(string content) => content.IndexOf("\n\n<attached>", StringComparison.Ordinal) is var at and >= 0 ? content[..at] : content;

    private static string Like(string q) => "%" + q.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

    private static async Task<IResult> SearchAsync(string? q, string? person, string? source, string? state, DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset? before,
        AppDbContext db, Audit audit, Chat.AnswerJobs jobs, TimeProvider clock, CancellationToken ct)
    {
        source = source is "chat" or "api" ? source : "all";
        state = state is not null && States.Contains(state) ? state : null;
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
            // A chat's question is answered while its chat answers (its newest question), "waiting" included: the line is the answer's.
            if (state is PromptLogEntry.Waiting or PromptLogEntry.Refused)
            {
                questions = questions.Where(_ => false);
            }
            var asked = await questions.OrderByDescending(x => x.m.CreatedAt).Take(state is null ? Page : Scanned)
                .Select(x => new { x.m.Id, x.m.Content, x.m.CreatedAt, Person = x.m.AuthorId ?? x.c.UserId, Chat = x.c.Id, x.c.Title, Deleted = x.c.DeletedAt != null, x.m.AttachmentsJson })
                .ToListAsync(ct);
            var ids = asked.Select(a => (Guid?)a.Id).ToList();
            // Each question's answer: its first message (whose parent is the question) and the rest of it (AnswerId), its last words.
            var firsts = await db.ChatMessages.AsNoTracking().Where(a => a.Role == "assistant" && ids.Contains(a.ParentId))
                .Select(a => new { a.Id, a.ParentId, a.Model }).ToListAsync(ct);
            var firstIds = firsts.Select(f => (Guid?)f.Id).ToList();
            var rounds = (await db.ChatMessages.AsNoTracking().Where(a => a.Role == "assistant" && (firstIds.Contains(a.AnswerId) || firstIds.Contains(a.Id)))
                .Select(a => new { Answer = a.AnswerId ?? a.Id, a.Content, a.Sequence, Tokens = a.CompletionTokens, a.Status, a.Error, a.AnswerMs }).ToListAsync(ct))
                .GroupBy(a => a.Answer).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Sequence).ToList());
            // The newest question of each chat that is answering now: the one being answered.
            var newest = asked.GroupBy(a => a.Chat).ToDictionary(g => g.Key, g => g.Max(a => a.CreatedAt));
            foreach (var a in asked)
            {
                var first = firsts.FirstOrDefault(f => f.ParentId == a.Id);
                var said = first is not null && rounds.TryGetValue(first.Id, out var r) ? r : [];
                var words = said.LastOrDefault(x => x.Content != "")?.Content;
                var tokens = said.Sum(x => x.Tokens ?? 0);
                var answering = jobs.IsAnswering(a.Chat) && newest[a.Chat] == a.CreatedAt;
                var (now, why) = ChatState([.. said.Select(x => (x.Status, x.Error))], answering);
                if (state is not null && now != state)
                {
                    continue;
                }
                rows.Add(new Row("chat", a.CreatedAt, a.Person, first?.Model, Cut(Typed(a.Content)), words is null ? null : Cut(words), a.Chat, a.Title, a.Deleted,
                    tokens > 0 ? tokens : null, now, why, said.LastOrDefault(x => x.AnswerMs is not null)?.AnswerMs, a.Id, first?.Id)
                {
                    Asked = [.. Chat.ChatService.ParseIds(a.AttachmentsJson).Select(id => new FileRef(id, "", "", "", 0))],
                });
                if (rows.Count == Page)
                {
                    break;
                }
            }
        }
        if (source != "chat")
        {
            var api = db.PromptLog.AsNoTracking().Where(p => p.At < end && p.At >= start && (who == null || p.UserId == who) && (state == null || p.State == state));
            if (text is not null)
            {
                api = api.Where(p => EF.Functions.ILike(p.Prompt, Like(text), "\\") || EF.Functions.ILike(p.Answer!, Like(text), "\\"));
            }
            rows.AddRange((await api.OrderByDescending(p => p.At).Take(Page).ToListAsync(ct)).Select(p => new Row("api", p.At, p.UserId, p.Model, Cut(p.Prompt), p.Answer is null ? null : Cut(p.Answer),
                null, null, false, (p.PromptTokens ?? 0) + (p.CompletionTokens ?? 0) is > 0 and var n ? n : null, p.State, p.Error, p.DurationMs, p.Id)));
        }
        var page = rows.OrderByDescending(r => r.At).Take(Page).ToList();
        await FilesAsync(db, page, ct);
        var people = page.Select(r => r.Person).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => people.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => new { u.Id, u.UserName, u.DisplayName }, ct);
        await audit.WriteAsync("prompts.viewed", person, detail: string.Join("; ", new[]
        {
            $"source {source}", state is null ? null : $"state {state}", text is null ? null : $"text \"{text}\"", person is null ? null : $"person {person}",
            from is null ? null : $"from {from.Value.ToString("O", CultureInfo.InvariantCulture)}", to is null ? null : $"to {to.Value.ToString("O", CultureInfo.InvariantCulture)}",
            $"{page.Count} shown",
        }.OfType<string>()));
        return Results.Ok(new
        {
            items = page.Select(r => new
            {
                r.Source, r.At, person = names.GetValueOrDefault(r.Person), r.Model, r.Question, r.Answer, r.Chat, r.Title, r.Deleted, r.Tokens, r.State, r.Error, r.DurationMs,
                files = r.Asked.Select(f => new { f.Id, f.Name, f.Kind, f.ContentType, f.Size, url = $"/api/admin/prompts/files/{r.Source}/{f.Id}" }),
                made = r.Made.Select(f => new { f.Id, f.Name, f.Kind, f.ContentType, f.Size, url = $"/api/admin/prompts/files/{r.Source}/{f.Id}" }),
            }),
            next = page.Count == Page ? page[^1].At : (DateTimeOffset?)null,
        });
    }
    /// <summary>
    /// Each row's files: a chat question's (what went with it) and what its answer's tools made (pictures, documents,
    /// Python's files), from the chat's own files; an API question's pictures, from its line.
    /// </summary>
    private static async Task FilesAsync(AppDbContext db, List<Row> page, CancellationToken ct)
    {
        var chat = page.Where(r => r.Source == "chat").ToList();
        if (chat.Count > 0)
        {
            var answers = chat.Select(r => r.Answered).OfType<Guid>().Select(id => (Guid?)id).ToList();
            var made = (await db.ChatMessages.AsNoTracking().Where(t => t.Role == "tool" && answers.Contains(t.AnswerId) && t.AttachmentsJson != null)
                .Select(t => new { t.AnswerId, t.AttachmentsJson, t.Sequence }).ToListAsync(ct))
                .GroupBy(t => t.AnswerId!.Value).ToDictionary(g => g.Key, g => g.OrderBy(t => t.Sequence).SelectMany(t => Chat.ChatService.ParseIds(t.AttachmentsJson)).Distinct().ToList());
            var ids = chat.SelectMany(r => r.Asked.Select(f => f.Id)).Concat(made.Values.SelectMany(v => v)).Distinct().ToList();
            var known = await db.ChatAttachments.AsNoTracking().Where(a => ids.Contains(a.Id))
                .Select(a => new FileRef(a.Id, a.FileName, a.Kind, a.ContentType, a.Size)).ToDictionaryAsync(a => a.Id, ct);
            foreach (var r in chat)
            {
                r.Asked = [.. r.Asked.Select(f => known.GetValueOrDefault(f.Id)).OfType<FileRef>()];
                r.Made = r.Answered is { } answer && made.TryGetValue(answer, out var list) ? [.. list.Select(known.GetValueOrDefault).OfType<FileRef>()] : [];
            }
        }
        var lines = page.Where(r => r.Source == "api").Select(r => r.Id).OfType<Guid>().ToList();
        if (lines.Count > 0)
        {
            var pictures = (await db.PromptLogFiles.AsNoTracking().Where(f => lines.Contains(f.EntryId))
                .Select(f => new { f.EntryId, File = new FileRef(f.Id, f.Name, "image", f.ContentType, f.Data.Length) }).ToListAsync(ct))
                .GroupBy(f => f.EntryId).ToDictionary(g => g.Key, g => g.Select(f => f.File).ToList());
            foreach (var r in page.Where(r => r.Source == "api" && r.Id is not null))
            {
                r.Asked = pictures.TryGetValue(r.Id!.Value, out var list) ? list : [];
            }
        }
    }

    /// <summary>
    /// A file of the prompt log, as prompts.review reads it (audited): a chat's file that went with a question or that an
    /// answer made, or an API question's picture. Pictures are shown (an SVG made safe runs nothing); anything else is saved.
    /// </summary>
    private static async Task<IResult> FileAsync(string source, Guid id, AppDbContext db, Audit audit, HttpContext http, CancellationToken ct)
    {
        string name, type, kind;
        byte[] data;
        if (source == "api" && await db.PromptLogFiles.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct) is { } picture)
        {
            (name, type, kind, data) = (picture.Name, picture.ContentType, "image", picture.Data);
        }
        else if (source == "chat" && await db.ChatAttachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) is { } file
            && await db.ChatMessages.AsNoTracking().AnyAsync(m => m.AttachmentsJson != null && m.AttachmentsJson.Contains(id.ToString()), ct))
        {
            (name, type, kind, data) = (file.FileName, file.ContentType, file.Kind, file.Data ?? System.Text.Encoding.UTF8.GetBytes(file.Text));
        }
        else
        {
            return Results.NotFound();
        }
        await audit.WriteAsync("prompts.file.viewed", id.ToString(), detail: $"{source}: {name}");
        http.Response.Headers.CacheControl = "private, max-age=300";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        if (kind == "image" && type.StartsWith("image/", StringComparison.Ordinal))
        {
            if (type == "image/svg+xml")
            {
                http.Response.Headers.ContentSecurityPolicy = Vector.SvgSafe.Policy;
            }
            return Results.File(data, type);
        }
        return Results.File(data, "application/octet-stream", name);
    }
}
