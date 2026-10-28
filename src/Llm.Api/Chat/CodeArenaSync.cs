using System.Text;
using Llm.Api.Access;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.ArenaMcp;
using Llm.Api.Chat.Tools;
using Llm.Api.Endpoints;
using Llm.Api.Gateway;
using Llm.Api.Models;
using Llm.Api.Operations;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Llm.Api.Chat;

/// <summary>
/// A message as Code Arena and this end exchange it: the model's own shape, with its model, tokens and time. <paramref name="Id"/>
/// is this end's id for it (in what this end gives); <paramref name="Ref"/> is Code Arena's place for it (session:number), so a
/// message sent twice is known as one. <paramref name="Status"/>: how an answer ended when not as it should ("stopped" by the
/// person, "failed", "declined"); <paramref name="Kind"/> "note": a message of Code Arena's own to the model (the connection
/// dropped), shown as a notice, not as the person's words. <paramref name="Attachments"/>: what went with a question here (its
/// text files are in <paramref name="Content"/> already, as the web's model reads them; its pictures Code Arena fetches).
/// <paramref name="Quote"/>: the part of an earlier answer a question quotes, kept apart from its words both ways (ChatQuotes).
/// </summary>
public sealed record SyncMessage(
    string Role,
    JsonNode? Content,
    string? Reasoning = null,
    JsonNode? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null,
    string? Model = null,
    int? PromptTokens = null,
    int? CachedTokens = null,
    int? CompletionTokens = null,
    DateTimeOffset? At = null,
    string? Ref = null,
    Guid? Id = null,
    string? Status = null,
    string? Kind = null,
    IReadOnlyList<SyncAttachment>? Attachments = null,
    string? Quote = null);

/// <summary>A file that went with a question on the web: "image" (fetched with the key from /api/code-arena/attachments/{id}), "text", "file", "audio", "video".</summary>
public sealed record SyncAttachment(Guid Id, string Name, string Kind, string ContentType);

public sealed record SyncNewChat(string? Ref, string? Place, string? Model, string? Title, string? Project = null);

/// <summary>A summary of the branch from its start down to the message <paramref name="UpTo"/>, read instead of those messages.</summary>
public sealed record SyncSummary(Guid UpTo, string? Text);

/// <summary>
/// Messages Code Arena adds after <paramref name="Parent"/> (the last message it has from here; null: from the start), and the
/// summary its compaction wrote. <paramref name="Branch"/>: they go on a branch of their own from <paramref name="Parent"/>,
/// shown from then on (Code Arena went back to before a turn, or kept its own way while the web moved elsewhere).
/// </summary>
public sealed record SyncAppend(Guid? Parent, bool Branch, List<SyncMessage>? Messages, SyncSummary? Summary = null);

/// <summary>
/// The branch on screen as Code Arena reads it: the messages after <paramref name="Base"/> (the last message both sides have; null:
/// from the start), <paramref name="Fork"/> when what Code Arena last had is not on the branch any more (the web edited a question,
/// answered again or showed another branch: Code Arena goes on from <paramref name="Base"/>), the branch's last summary, and
/// whether more follow (<paramref name="More"/>: read again after the last one given).
/// </summary>
public sealed record SyncRead(
    Guid Id,
    string Title,
    string? Model,
    string? Origin,
    string? OriginPlace,
    int Count,
    string Stamp,
    Guid? Base,
    bool Fork,
    bool More,
    object? Summary,
    IEnumerable<SyncMessage> Messages,
    string? Status = null,
    IReadOnlyList<Guid>? Ids = null);

/// <summary>
/// Code Arena's chats kept in step with the person's chats here, both ways, signed in by their API key (as Arena MCP is):
/// a session there is a chat here (its folder shown), continued here with the chat's tools, and a chat here can be
/// continued there. Every message goes with its id here: Code Arena reads what follows the last message it has, and adds
/// after a message it names, so an edit, a regenerated answer or another branch shown on the web is seen as such. While
/// Code Arena answers it holds the chat's turn (<see cref="TurnLease"/>): the web queues the person's messages meanwhile.
/// </summary>
public static class CodeArenaSync
{
    public const string Origin = "code-arena";
    /// <summary>The most messages one request adds.</summary>
    public const int MostAtOnce = 500;
    /// <summary>The most messages one read gives (the rest: read again after the last one).</summary>
    public const int MostRead = 500;
    /// <summary>How long Code Arena's turn holds the chat unless it renews it (each of its sends does, and it asks again meanwhile).</summary>
    public static readonly TimeSpan TurnLease = TimeSpan.FromSeconds(90);

    public static void MapCodeArenaSync(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/code-arena").AllowAnonymous("Code Arena: the person's API key is checked on each call.");
        g.MapGet("/chats", ListAsync);
        g.MapPost("/chats", CreateAsync);
        g.MapGet("/chats/{id:guid}", ReadAsync);
        g.MapPost("/chats/{id:guid}/messages", AppendAsync);
        g.MapPost("/chats/{id:guid}/turn", TurnAsync);
        g.MapDelete("/chats/{id:guid}/turn", TurnEndAsync);
        g.MapPost("/chats/{id:guid}/continued", ContinuedAsync);
        g.MapGet("/attachments/{id:guid}", AttachmentAsync);
        g.MapGet("/preferences", PreferencesAsync);
        g.MapGet("/config", ConfigAsync);
        g.MapGet("/skills", SkillsAsync);
        g.MapGet("/security", SecurityAsync);
    }

    /// <summary>
    /// The skills the person's plugins bring (each a SKILL.md and the files beside it), for Code Arena to keep with theirs: only
    /// the plugins whose tool they may use. Its own and the project's skills of the same name win there.
    /// </summary>
    /// <summary>
    /// The latest security review of a repository's branch the person may read (their own, or everyone's with
    /// reviews.admin), for Code Arena's Problems: its open findings (the doubted ones left out), the most severe first, each
    /// with what Fix it would ask, so the IDE's agent is given the weakness, its path and the references. A branch with no
    /// review of its own falls back to one of the default branch (a review that named none).
    /// </summary>
    private static async Task<IResult> SecurityAsync(HttpContext http, McpPeople people, AccessService access, AppDbContext db, string? project, string? @ref)
    {
        var (me, refusal) = await CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        if (project?.Trim().Trim('/') is not { Length: > 0 and <= 500 } path)
        {
            return AuthEndpoints.Problem(400, "project", "Name the repository: group/project.");
        }
        var ct = http.RequestAborted;
        var everyone = await access.MayAsync(me, Core.Access.Permissions.ReviewsAdmin, ct);
        var reviews = db.SecurityScans.AsNoTracking().Where(s => s.Project == path && s.Status == Core.Code.SecurityScan.Done && (everyone || s.UserId == me.Id));
        var scan = (@ref is { Length: > 0 } branch ? await reviews.Where(s => s.Ref == branch).OrderByDescending(s => s.FinishedAt).FirstOrDefaultAsync(ct) : null)
            ?? await reviews.Where(s => s.Ref == null).OrderByDescending(s => s.FinishedAt).FirstOrDefaultAsync(ct);
        if (scan is null)
        {
            return Results.Ok(new { scan = (object?)null, findings = Array.Empty<object>() });
        }
        static int Rank(string severity) => severity switch { "critical" => 0, "high" => 1, "medium" => 2, _ => 3 };
        var findings = (await db.SecurityFindings.AsNoTracking().Where(f => f.ScanId == scan.Id && f.State == Core.Code.SecurityFinding.Open && f.Verified != false).ToListAsync(ct))
            .OrderBy(f => f.Exploited ? 0 : 1).ThenBy(f => Rank(f.Severity)).ThenBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Line).ToList();
        return Results.Ok(new
        {
            scan = new { scan.Id, scan.Project, scan.Ref, scan.Commit, scan.FinishedAt, link = $"/code/security/{scan.Id}" },
            findings = findings.Select(f => new { f.Id, f.Kind, f.Path, f.Line, f.Severity, f.Exploited, f.Cwe, f.Rule, f.Title, ask = Code.Fixes.FixEndpoints.SecurityAsk(f, scan) }),
        });
    }

    private static async Task<IResult> SkillsAsync(HttpContext http, McpPeople people, ToolRegistry registry, AccessService access, AppDbContext db)
    {
        var (me, refusal) = await CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        var usable = (await registry.ForAsync(await access.MembershipAsync(me, http.RequestAborted), http.RequestAborted)).Select(t => t.Tool.Id).ToHashSet();
        var skills = await Plugins.PluginContent.SkillsAsync(db, usable, http.RequestAborted);
        return Results.Ok(new
        {
            skills = skills.Select(s => new { s.Skill.Name, s.Skill.Description, s.Plugin, s.Version, files = s.Skill.Files }),
            // Arena's own skills an admin turned off: Code Arena leaves them out too.
            off = await db.Skills.AsNoTracking().Where(s => s.BuiltIn && s.Off).Select(s => s.Name).ToListAsync(http.RequestAborted),
        });
    }

    /// <summary>
    /// The web chat's config for the key's person (<see cref="ChatEndpoints"/>' /api/chat/config): Code Arena's model picker
    /// shows which models are loaded and see pictures, the deployment's thinking levels and default, and its links to GitLab.
    /// </summary>
    private static async Task<IResult> ConfigAsync(HttpContext http, McpPeople people, ToolRegistry registry, AccessService access, ModelPolicy policy,
        IOptions<ArgusOptions> argusOptions, ArgusMcp argus, ChatModels models, SmallModel small, IOptionsMonitor<ChatOptions> chat, Models.ToolModeBook toolModes)
    {
        var (me, refusal) = await CallerAsync(http, people);
        return me is null ? refusal! : Results.Ok(await ChatEndpoints.ConfigForAsync(me, registry, access, policy, argusOptions, argus, models, small, chat, toolModes, http.RequestAborted));
    }

    /// <summary>
    /// What Code Arena takes from the person's account: when their chats compact (Your account → Chat; null: they follow the
    /// company's, compactAtDefault). Code Arena compacts at it unless a flag or its config.json says otherwise.
    /// </summary>
    private static async Task<IResult> PreferencesAsync(HttpContext http, McpPeople people, IOptionsMonitor<ChatOptions> chat)
    {
        var (me, refusal) = await CallerAsync(http, people);
        return me is null ? refusal! : Results.Ok(new { compactAt = me.CompactAt, compactAtDefault = chat.CurrentValue.CompactAt });
    }

    /// <summary>What a read gives of each message beyond its row: tool results marked as the web's model reads them, attachments' text up to so much.</summary>
    private sealed record Outward(Safeguards.Safeguards Safeguards, int Budget);

    private static Outward OutwardOf(Safeguards.Safeguards safeguards, IOptionsMonitor<ChatOptions> chat) => new(safeguards, chat.CurrentValue.InlineAttachmentChars);

    /// <summary>Whether Code Arena is answering in the chat now (its turn holds it): the web queues the person's messages.</summary>
    public static bool Answering(Conversation c) => c.CodeArenaTurnUntil is { } until && until > DateTimeOffset.UtcNow;

    /// <summary>
    /// A turn of Code Arena's that lapsed (it stopped, or lost Arena, without ending it) lets the chat go: the messages queued
    /// on the web meanwhile go now. True when there was one.
    /// </summary>
    public static async Task<bool> LapsedAsync(AppDbContext db, QueuedMessages queue, Guid id, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (await db.Conversations.Where(c => c.Id == id && c.CodeArenaTurnUntil != null && c.CodeArenaTurnUntil <= now)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.CodeArenaTurnUntil, (DateTimeOffset?)null), ct) == 0)
        {
            return false;
        }
        await queue.StartNextAsync(id);
        return true;
    }

    internal static async Task<(AppUser? Me, IResult? Refusal)> CallerAsync(HttpContext http, McpPeople people)
    {
        try
        {
            var caller = await people.FromHeaderAsync(http.Request.Headers.Authorization.ToString() is { Length: > 0 } a ? a : null, http.RequestAborted);
            if (caller.User is not { } me)
            {
                return (null, AuthEndpoints.Problem(401, "key", caller.Refusal ?? McpPeople.NoKey));
            }
            // Code Arena is a feature of its own (Admin → Access).
            var access = http.RequestServices.GetRequiredService<Access.AccessService>();
            return (await access.MayAsync(me, Core.Access.Permissions.CodeArena, http.RequestAborted))
                ? (me, null)
                : (null, AuthEndpoints.Problem(403, "feature", "Code Arena is not among what you may use: ask an admin."));
        }
        catch (GatewayException)
        {
            return (null, AuthEndpoints.Problem(503, "gateway", "The gateway cannot say whose that key is now: try again in a moment."));
        }
    }

    private static IResult Gone() => AuthEndpoints.Problem(404, "gone", "That chat is not there any more: it was deleted.");

    /// <summary>A chat's messages on the branch on screen, counted for the list: one row each.</summary>
    private sealed record BranchCount(Guid Id, int Count);

    /// <summary>
    /// The person's chats, newest first, to list beside Code Arena's own sessions and continue one there: a page of
    /// <paramref name="limit"/> (200 at most), those changed before <paramref name="before"/> (the last one's UpdatedAt) for
    /// the next page; <c>more</c> says whether there is one. Each says how many messages its branch on screen has (what a
    /// read of it gives). <c>me</c> says whose chats they are: Code Arena shows it, as a key of another account syncs with
    /// that account's chats.
    /// </summary>
    private static async Task<IResult> ListAsync(HttpContext http, McpPeople people, AppDbContext db, int limit = 30, DateTimeOffset? before = null)
    {
        var (me, refusal) = await CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        var take = Math.Clamp(limit, 1, 200);
        var chats = await db.Conversations.AsNoTracking().Where(c => c.UserId == me.Id && c.Kind == null && c.ArchivedAt == null && (before == null || c.UpdatedAt < before))
            .OrderByDescending(c => c.UpdatedAt).Take(take + 1)
            .Select(c => new { c.Id, c.Title, c.Origin, c.OriginRef, c.OriginPlace, c.Model, c.UpdatedAt, c.CodeArenaTurnUntil })
            .ToListAsync(http.RequestAborted);
        var shown = chats.Take(take).ToList();
        var ids = shown.Select(c => c.Id).ToArray();
        // From each chat's last message up through its parents, in the database: a long chat is not read to count it.
        var counts = await db.Database.SqlQueryRaw<BranchCount>(
            """
            WITH RECURSIVE branch("Id", "ParentId", "ConversationId", "Depth") AS (
                SELECT m."Id", m."ParentId", m."ConversationId", 1 FROM chat_messages m
                JOIN conversations c ON c."CurrentLeafId" = m."Id" AND c."Id" = m."ConversationId"
                WHERE c."Id" = ANY(@ids)
                UNION ALL
                SELECT m."Id", m."ParentId", m."ConversationId", b."Depth" + 1 FROM chat_messages m
                JOIN branch b ON m."Id" = b."ParentId" AND m."ConversationId" = b."ConversationId"
                WHERE b."Depth" < 1000000)
            SELECT "ConversationId" AS "Id", count(*)::int AS "Count" FROM branch GROUP BY "ConversationId"
            """, new NpgsqlParameter("ids", ids)).ToDictionaryAsync(r => r.Id, r => r.Count, http.RequestAborted);
        return Results.Ok(new
        {
            chats = shown.Select(c => new
            {
                c.Id, c.Title, c.Origin, c.OriginRef, c.OriginPlace, c.Model, c.UpdatedAt, messages = counts.GetValueOrDefault(c.Id),
                codeArena = c.CodeArenaTurnUntil > DateTimeOffset.UtcNow,
            }),
            more = chats.Count > take,
            me = new { email = me.Email, name = me.DisplayName },
        });
    }

    /// <summary>The chat for a Code Arena session: made the first time, the same one after (by the session's id).</summary>
    private static async Task<IResult> CreateAsync(SyncNewChat body, HttpContext http, McpPeople people, AppDbContext db)
    {
        var (me, refusal) = await CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        var reference = Clip(body.Ref, 128);
        if (reference is not null && await db.Conversations.FirstOrDefaultAsync(c => c.UserId == me.Id && c.Origin == Origin && c.OriginRef == reference, http.RequestAborted) is { } known)
        {
            return Results.Ok(new { id = known.Id, created = false });
        }
        var c = new Conversation
        {
            UserId = me.Id,
            Origin = Origin,
            OriginRef = reference,
            OriginPlace = Clip(body.Place, 1000),
            ProjectKey = Clip(body.Project, 300),
            Model = Clip(body.Model, 200),
            Title = Clip(body.Title, 200) ?? "New chat",
        };
        db.Conversations.Add(c);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { id = c.Id, created = true });
    }

    /// <summary>
    /// The branch on screen after what Code Arena has (<paramref name="since"/>: the last message it has from here; none: from
    /// the start), at most <paramref name="limit"/> messages, with its count and stamp (the branch's last message). Given the
    /// stamp of a read that brought nothing new, and the branch has not moved since, it says only that (<c>unchanged</c>):
    /// Code Arena asks every few seconds while it waits, and a long chat is not read each time.
    /// </summary>
    private static async Task<IResult> ReadAsync(Guid id, HttpContext http, McpPeople people, AppDbContext db, AnswerJobs jobs, Safeguards.Safeguards safeguards,
        IOptionsMonitor<ChatOptions> chat, Guid? since = null, string? stamp = null, int limit = 200)
    {
        var (me, refusal) = await CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        if (await db.Conversations.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.UserId == me.Id, http.RequestAborted) is not { } c)
        {
            return Gone();
        }
        if (jobs.IsAnswering(id))
        {
            return AuthEndpoints.Problem(409, "answering", "The chat is answering on the web: its messages come when the answer ends.");
        }
        if (stamp is not null && stamp == Stamp(c.CurrentLeafId))
        {
            return Results.Ok(new { unchanged = true, stamp });
        }
        var tree = await TreeAsync(db, id, http.RequestAborted);
        return Results.Ok(await ReadFromAsync(db, c, tree, since, Math.Clamp(limit, 0, MostRead), OutwardOf(safeguards, chat), http.RequestAborted));
    }

    /// <summary>
    /// Adds Code Arena's new messages after the one it names. When the web added after that one meanwhile, or shows a branch
    /// it is not on, nothing is added: 409 with what Code Arena lacks (as a read after it gives), to take in before sending
    /// again. Messages it sent before whose answer was lost are known by their ref and not added twice; its tool results go
    /// straight after their call, before what the web added.
    /// </summary>
    private static async Task<IResult> AppendAsync(Guid id, SyncAppend body, HttpContext http, McpPeople people, AppDbContext db, AnswerJobs jobs,
        Safeguards.Safeguards safeguards, IOptionsMonitor<ChatOptions> chat)
    {
        var (me, refusal) = await CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        var ct = http.RequestAborted;
        if (await db.Conversations.SingleOrDefaultAsync(c => c.Id == id && c.UserId == me.Id, ct) is not { } c)
        {
            return Gone();
        }
        var messages = body.Messages ?? [];
        if (messages.Count > MostAtOnce)
        {
            return AuthEndpoints.Problem(400, "too_many", $"At most {MostAtOnce} messages at once.");
        }
        if (messages.FirstOrDefault(m => m.Role is not ("user" or "assistant" or "tool")) is { } odd)
        {
            return AuthEndpoints.Problem(400, "role", $"A message's role is user, assistant or tool, not \"{odd.Role}\".");
        }
        if (jobs.IsAnswering(id))
        {
            return AuthEndpoints.Problem(409, "answering", "The chat is answering on the web: send again when the answer ends.");
        }
        var tree = await TreeAsync(db, id, ct);
        var parent = body.Parent;
        if (parent is { } named && !tree.ById.ContainsKey(named))
        {
            // A message this chat does not have (Code Arena's file is from another chat, or the message went): it reads the chat again.
            return Results.Conflict(await ReadFromAsync(db, c, tree, named, MostRead, OutwardOf(safeguards, chat), ct) with { Status = "behind" });
        }
        // Files that went with Code Arena's questions (uploaded through its IDE): kept as the questions' attachments, the
        // person's own only.
        var wanted = messages.Where(m => m.Role == "user").SelectMany(m => m.Attachments ?? []).Select(a => a.Id).Distinct().ToList();
        var owned = wanted.Count == 0 ? [] : (await db.ChatAttachments.Where(a => wanted.Contains(a.Id) && a.UserId == me.Id).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        var ids = new List<Guid>();
        var rest = messages;
        // Sent before, its answer lost on the way: those already here are not added twice.
        while (rest.Count > 0 && Clip(rest[0].Ref, 160) is { } reference && tree.Children[parent].FirstOrDefault(n => n.SyncRef == reference) is { } had)
        {
            ids.Add(had.Id);
            parent = had.Id;
            rest = rest[1..];
        }
        var sequence = tree.Nodes.Count == 0 ? 0 : tree.Nodes.Max(n => n.Sequence);
        if (!body.Branch && (parent is null ? tree.Path.Count > 0 : parent != c.CurrentLeafId))
        {
            if (parent is { } p && tree.At.TryGetValue(p, out var place) && rest.Count > 0 && tree.ById[p] is { Role: "assistant", Calls: true }
                && rest[0].Role == "tool" && place + 1 < tree.Path.Count && tree.Path[place + 1].Role != "tool")
            {
                // The web added after a call whose results Code Arena still had to send: they go straight after the call, and what the web added follows them.
                var calls = CallIds(await db.ChatMessages.AsNoTracking().Where(m => m.Id == p).Select(m => m.ToolCallsJson).SingleAsync(ct));
                var results = rest.TakeWhile(m => m.Role == "tool" && m.ToolCallId is { } call && calls.Contains(call)).ToList();
                if (results.Count > 0)
                {
                    var last = Add(db, id, p, results, ref sequence, ids, owned);
                    var next = await db.ChatMessages.SingleAsync(m => m.Id == tree.Path[place + 1].Id, ct);
                    next.ParentId = last;
                    c.UpdatedAt = DateTimeOffset.UtcNow;
                    Renew(c);
                    await db.SaveChangesAsync(ct);
                    tree = await TreeAsync(db, id, ct);
                    parent = last;
                }
            }
            // The web added after it, or shows another branch: what Code Arena lacks, to take in first.
            return Results.Conflict(await ReadFromAsync(db, c, tree, parent, MostRead, OutwardOf(safeguards, chat), ct) with { Status = "behind", Ids = ids });
        }
        var leaf = parent;
        var count = (parent is null ? 0 : Depth(tree, parent.Value)) + rest.Count;
        if (rest.Count > 0)
        {
            leaf = Add(db, id, parent, rest, ref sequence, ids, owned);
            // An archived chat stays so: only the person's continuing it from Code Arena (/continued) brings it back to the list.
            c.UpdatedAt = DateTimeOffset.UtcNow;
            if (c.Title == "New chat" && rest.FirstOrDefault(m => m.Role == "user") is { } first && TitleOf(Text(first.Content)) is { } title)
            {
                c.Title = title;
            }
            // Continued on the web, the chat answers with the model Code Arena answered with last.
            if (rest.LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Model)) is { } answer)
            {
                c.Model = Clip(answer.Model, 200);
            }
        }
        if (leaf != c.CurrentLeafId && (rest.Count > 0 || body.Branch))
        {
            c.CurrentLeafId = leaf;
            c.UpdatedAt = DateTimeOffset.UtcNow;
        }
        var summarized = false;
        if (body.Summary is { Text: { Length: > 0 } text } summary && (tree.ById.ContainsKey(summary.UpTo) || ids.Contains(summary.UpTo)))
        {
            // Where the web's own compaction keeps it: the web's next answer reads the summary instead of what it covers.
            var covered = await db.ChatMessages.FindAsync([summary.UpTo], ct);
            covered!.Summary = text;
            summarized = true;
        }
        Renew(c);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { ids, count, stamp = Stamp(c.CurrentLeafId), summarized });
    }

    /// <summary>
    /// The messages added after <paramref name="parent"/>, each the next one's parent; the last one's id. A question's
    /// attachments are kept when they are the person's (<paramref name="owned"/>).
    /// </summary>
    private static Guid? Add(AppDbContext db, Guid conversation, Guid? parent, IEnumerable<SyncMessage> messages, ref int sequence, List<Guid> ids, HashSet<Guid> owned)
    {
        foreach (var m in messages)
        {
            var message = new ChatMessage
            {
                ConversationId = conversation,
                ParentId = parent,
                Sequence = ++sequence,
                Role = m.Role,
                Content = Text(m.Content),
                Quote = m.Role == "user" && ChatQuotes.Clean(m.Quote) is { } quote ? ChatQuotes.Cut(quote) : null,
                AttachmentsJson = m.Role == "user" && (m.Attachments ?? []).Select(a => a.Id).Where(owned.Contains).Distinct().ToArray() is { Length: > 0 } files
                    ? JsonSerializer.Serialize(files) : null,
                Reasoning = string.IsNullOrEmpty(m.Reasoning) ? null : m.Reasoning,
                ToolCallsJson = m.Role == "assistant" && m.ToolCalls is JsonArray { Count: > 0 } calls ? calls.ToJsonString() : null,
                ToolCallId = m.Role == "tool" ? Clip(m.ToolCallId, 200) : null,
                ToolName = m.Role == "tool" ? Clip(m.Name, 200) : null,
                Model = m.Role == "assistant" ? Clip(m.Model, 200) : null,
                PromptTokens = m.PromptTokens,
                CachedTokens = m.CachedTokens,
                CompletionTokens = m.CompletionTokens,
                CreatedAt = m.At ?? DateTimeOffset.UtcNow,
                SyncRef = Clip(m.Ref, 160),
                // Stopped by the person in Code Arena: shown as stopped, as an answer stopped here is.
                Status = m.Role == "assistant" && m.Status == "stopped" ? MessageStatus.Stopped : MessageStatus.Complete,
                // Code Arena's own word to its model: a notice on the web, not the person's.
                DetailsJson = m.Role == "user" && m.Kind == "note" ? """{"note":true}""" : null,
            };
            db.ChatMessages.Add(message);
            ids.Add(message.Id);
            parent = message.Id;
        }
        return parent;
    }

    /// <summary>Code Arena's turn starts: the chat is its until it ends the turn (or stops renewing it). Not while the web answers.</summary>
    private static async Task<IResult> TurnAsync(Guid id, HttpContext http, McpPeople people, AppDbContext db, AnswerJobs jobs)
    {
        var (me, refusal) = await CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        if (await db.Conversations.SingleOrDefaultAsync(c => c.Id == id && c.UserId == me.Id, http.RequestAborted) is not { } c)
        {
            return Gone();
        }
        if (jobs.IsAnswering(id))
        {
            return AuthEndpoints.Problem(409, "answering", "The chat is answering on the web: Code Arena's turn starts when the answer ends.");
        }
        c.CodeArenaTurnUntil = DateTimeOffset.UtcNow + TurnLease;
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { until = c.CodeArenaTurnUntil });
    }

    /// <summary>Code Arena's turn ended: the chat is free, and the messages the person queued on the web meanwhile go now.</summary>
    private static async Task<IResult> TurnEndAsync(Guid id, HttpContext http, McpPeople people, AppDbContext db, QueuedMessages queue)
    {
        var (me, refusal) = await CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        if (await db.Conversations.SingleOrDefaultAsync(c => c.Id == id && c.UserId == me.Id, http.RequestAborted) is not { } c)
        {
            return Gone();
        }
        if (c.CodeArenaTurnUntil is not null)
        {
            c.CodeArenaTurnUntil = null;
            await db.SaveChangesAsync(http.RequestAborted);
            await queue.StartNextAsync(id);
        }
        return Results.NoContent();
    }

    /// <summary>The person continues the chat in Code Arena: archived, it comes back to the list (once: Code Arena's sends leave it be).</summary>
    private static async Task<IResult> ContinuedAsync(Guid id, HttpContext http, McpPeople people, AppDbContext db)
    {
        var (me, refusal) = await CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        if (await db.Conversations.SingleOrDefaultAsync(c => c.Id == id && c.UserId == me.Id, http.RequestAborted) is not { } c)
        {
            return Gone();
        }
        if (c.ArchivedAt is not null)
        {
            c.ArchivedAt = null;
            await db.SaveChangesAsync(http.RequestAborted);
        }
        return Results.NoContent();
    }

    /// <summary>A picture (or another file kept whole) the person attached on the web, for their own key only: Code Arena's model looks at it.</summary>
    private static async Task<IResult> AttachmentAsync(Guid id, HttpContext http, McpPeople people, AppDbContext db)
    {
        var (me, refusal) = await CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        var file = await db.ChatAttachments.AsNoTracking().Where(a => a.Id == id && a.UserId == me.Id && a.Data != null)
            .Select(a => new { a.Data, a.ContentType, a.FileName }).SingleOrDefaultAsync(http.RequestAborted);
        return file is null ? AuthEndpoints.Problem(404, "gone", "That file is not there.") : Results.File(file.Data!, file.ContentType, file.FileName);
    }

    /// <summary>A send of Code Arena's renews its turn, when it holds one.</summary>
    private static void Renew(Conversation c)
    {
        if (Answering(c))
        {
            c.CodeArenaTurnUntil = DateTimeOffset.UtcNow + TurnLease;
        }
    }

    /// <summary>The branch on screen as one word: any message added to the chat, or another branch shown, changes it.</summary>
    private static string Stamp(Guid? leaf) => leaf?.ToString("N") ?? "empty";

    /// <summary>A message as the tree knows it, without its words: what finding the branch and the places on it needs.</summary>
    private sealed record Node(Guid Id, Guid? ParentId, int Sequence, string Role, bool Calls, bool Summarized, string? SyncRef);

    /// <summary>The chat's messages as a tree, and its branch on screen: ids and parents only, the words read later for those given.</summary>
    private sealed class Tree(List<Node> nodes, Guid? leaf)
    {
        public List<Node> Nodes { get; } = nodes;
        public Dictionary<Guid, Node> ById { get; } = nodes.ToDictionary(n => n.Id);
        /// <summary>Each message's replies (null: the first questions), oldest first.</summary>
        public ILookup<Guid?, Node> Children { get; } = nodes.OrderBy(n => n.Sequence).ToLookup(n => n.ParentId);
        public List<Node> Path { get; } = PathOf(nodes, leaf);
        private Dictionary<Guid, int>? _at;
        /// <summary>Each message's place on the branch on screen.</summary>
        public Dictionary<Guid, int> At => _at ??= Path.Select((n, i) => (n.Id, i)).ToDictionary(x => x.Id, x => x.i);

        private static List<Node> PathOf(List<Node> nodes, Guid? leaf)
        {
            var byId = nodes.ToDictionary(n => n.Id);
            var path = new List<Node>();
            var seen = new HashSet<Guid>();
            for (var id = leaf; id is { } current && byId.TryGetValue(current, out var n) && seen.Add(current); id = n.ParentId)
            {
                path.Add(n);
            }
            path.Reverse();
            return path;
        }
    }

    private static async Task<Tree> TreeAsync(AppDbContext db, Guid conversation, CancellationToken ct)
    {
        var leaf = await db.Conversations.AsNoTracking().Where(c => c.Id == conversation).Select(c => c.CurrentLeafId).SingleAsync(ct);
        var nodes = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversation)
            .Select(m => new Node(m.Id, m.ParentId, m.Sequence, m.Role, m.ToolCallsJson != null, m.Summary != null, m.SyncRef)).ToListAsync(ct);
        return new Tree(nodes, leaf);
    }

    /// <summary>How many messages lead to <paramref name="id"/> from the start, itself included.</summary>
    private static int Depth(Tree tree, Guid id)
    {
        var depth = 0;
        var seen = new HashSet<Guid>();
        for (Guid? at = id; at is { } current && tree.ById.TryGetValue(current, out var n) && seen.Add(current); at = n.ParentId)
        {
            depth++;
        }
        return depth;
    }

    /// <summary>
    /// The branch on screen after <paramref name="since"/>, as Code Arena reads it. When <paramref name="since"/> is not on it, the
    /// branch after the last message they share (a fork); when that is none, all of it.
    /// </summary>
    private static async Task<SyncRead> ReadFromAsync(AppDbContext db, Conversation c, Tree tree, Guid? since, int limit, Outward outward, CancellationToken ct)
    {
        var (start, @base, fork) = (0, (Guid?)null, false);
        if (since is { } s)
        {
            if (tree.At.TryGetValue(s, out var place))
            {
                (start, @base) = (place + 1, s);
            }
            else
            {
                fork = true;
                var seen = new HashSet<Guid>();
                for (var up = tree.ById.GetValueOrDefault(s)?.ParentId; up is { } u && seen.Add(u); up = tree.ById.GetValueOrDefault(u)?.ParentId)
                {
                    if (tree.At.TryGetValue(u, out var shared))
                    {
                        (start, @base) = (shared + 1, u);
                        break;
                    }
                }
            }
        }
        var page = tree.Path.Skip(start).Take(limit).Select(n => n.Id).ToList();
        var bodies = await db.ChatMessages.AsNoTracking().Where(m => page.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        object? summary = null;
        if (tree.Path.LastOrDefault(n => n.Summarized) is { } covered)
        {
            summary = new { upTo = covered.Id, text = bodies.GetValueOrDefault(covered.Id)?.Summary ?? await db.ChatMessages.AsNoTracking().Where(m => m.Id == covered.Id).Select(m => m.Summary).SingleAsync(ct) };
        }
        // The files that went with the questions given: their text (not a picture's bytes, which Code Arena fetches when its model sees).
        var fileIds = bodies.Values.Where(m => m.Role == "user").SelectMany(m => ChatService.ParseIds(m.AttachmentsJson)).ToHashSet();
        var files = fileIds.Count == 0 ? new Dictionary<Guid, ChatAttachment>() : await db.ChatAttachments.AsNoTracking().Where(a => fileIds.Contains(a.Id) && a.UserId == c.UserId)
            .Select(a => new ChatAttachment { Id = a.Id, FileName = a.FileName, ContentType = a.ContentType, Kind = a.Kind, Text = a.Text, Truncated = a.Truncated, Seconds = a.Seconds })
            .ToDictionaryAsync(a => a.Id, ct);
        return new SyncRead(c.Id, c.Title, c.Model, c.Origin, c.OriginPlace, tree.Path.Count, Stamp(c.CurrentLeafId), @base, fork, start + page.Count < tree.Path.Count, summary,
            [.. page.Select(i => Out(bodies[i], files, outward))]);
    }

    private static HashSet<string> CallIds(string? json)
    {
        try
        {
            return json is not null && JsonNode.Parse(json) is JsonArray calls
                ? [.. calls.OfType<JsonObject>().Select(call => call["id"]?.GetValue<string>()).OfType<string>()]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// A message as Code Arena reads it: as the web's own model reads it (a question with its files' text after its words, a
    /// tool's result marked as data when it came from the web or the company's documents), with what the web shows of it.
    /// </summary>
    private static SyncMessage Out(ChatMessage m, Dictionary<Guid, ChatAttachment> files, Outward outward)
    {
        var content = m.Content;
        List<SyncAttachment>? attached = null;
        if (m.Role == "user" && ChatService.ParseIds(m.AttachmentsJson).Where(files.ContainsKey).Select(id => files[id]).ToList() is { Count: > 0 } with)
        {
            var text = new StringBuilder(content);
            foreach (var f in with)
            {
                switch (f.Kind)
                {
                    case "image":
                        // Fetched by Code Arena when its model sees pictures; named there otherwise.
                        break;
                    case "audio" or "video":
                        text.Append("\n\n[").Append(f.Kind == "audio" ? "sound" : "video").Append(" attached: ").Append(f.FileName);
                        text.Append(f.Text.Length > 0 ? $"; what is said in it:\n{f.Text}]" : "]");
                        break;
                    default:
                        text.Append(ChatService.AttachmentText(f, null, outward.Budget, canReadFiles: false, here: false));
                        break;
                }
            }
            content = text.ToString();
            attached = [.. with.Select(f => new SyncAttachment(f.Id, f.FileName, f.Kind, f.ContentType))];
        }
        else if (m.Role == "tool")
        {
            content = outward.Safeguards.Untrusted(m.ToolName ?? "", m.Content);
        }
        return new SyncMessage(
            m.Role,
            content,
            m.Reasoning,
            m.ToolCallsJson is null ? null : JsonNode.Parse(m.ToolCallsJson),
            m.ToolCallId,
            m.ToolName,
            m.Model,
            m.PromptTokens,
            m.CachedTokens,
            m.CompletionTokens,
            m.CreatedAt,
            m.SyncRef,
            m.Id,
            m.Status == MessageStatus.Complete ? null : m.Status.ToString().ToLowerInvariant(),
            Note(m) ? "note" : null,
            attached,
            m.Role == "user" ? m.Quote : null);
    }

    /// <summary>A message of Code Arena's own to its model (the connection dropped), kept as a notice: not the person's words.</summary>
    private static bool Note(ChatMessage m)
    {
        if (m.Role != "user" || m.DetailsJson is null)
        {
            return false;
        }
        try
        {
            return JsonNode.Parse(m.DetailsJson)?["note"]?.GetValue<bool>() == true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>A message's text: a string as it is; parts (text and images), their text, an image said as such.</summary>
    private static string Text(JsonNode? content) => content switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray parts => string.Join("\n", parts.OfType<JsonObject>().Select(p => p["type"]?.GetValue<string>() == "text" ? p["text"]?.GetValue<string>() ?? "" : "[an image]")),
        _ => content.ToJsonString(),
    };

    private static string? TitleOf(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line is null ? null : line.Length <= 80 ? line : line[..77].TrimEnd() + "...";
    }

    private static string? Clip(string? value, int most) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= most ? value.Trim() : value.Trim()[..most];
}
