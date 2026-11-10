using System.Security.Claims;
using System.Text;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Projects;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Projects;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Tags;

/// <summary>Tags set on one thing: the whole list (an empty one clears them).</summary>
public sealed record TagsRequest(string[]? Tags);

/// <summary>
/// Tags and summaries: the known tags and the person's own in use; Summarize and tag on their chat or project (it keeps the
/// tags already there and adds the ones that fit); on the prompt log's lines, with prompts.review, tags set by hand and
/// Summarize and tag (a chat's question tags its chat). Each is audited.
/// </summary>
public static class TagEndpoints
{
    public static void MapTags(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api").PersonOnly();
        me.MapGet("/tags", KnownAsync);
        me.MapPost("/chat/conversations/{id:guid}/summarize", SummarizeChatAsync);
        me.MapPost("/projects/{id:guid}/summarize", SummarizeProjectAsync);
        me.MapPut("/projects/{id:guid}/tags", TagProjectAsync);
        var review = app.MapGroup("/api/admin/prompts").RequirePermissions(Core.Access.Permissions.PromptsReview, Core.Access.Permissions.PromptsReview);
        review.MapPut("/{source}/{id:guid}/tags", TagPromptAsync);
        review.MapPost("/{source}/{id:guid}/summarize", SummarizePromptAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static IResult NoModel() => AuthEndpoints.Problem(503, "model", "No model can summarize right now: set a model for small steps (Admin → Settings → Chat), or load your default model.");

    /// <summary>The known tags (Tags:Known), then the person's own chats' and projects' others, most used first.</summary>
    private static async Task<IResult> KnownAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, IOptionsMonitor<TagOptions> options, CancellationToken ct)
    {
        var me = await Me(p, users);
        var m = await access.MembershipAsync(me, ct);
        var chats = await db.Conversations.AsNoTracking().Where(c => c.UserId == me.Id && c.DeletedAt == null && c.Tags.Count > 0).OrderByDescending(c => c.UpdatedAt).Select(c => c.Tags).Take(5000).ToListAsync(ct);
        var projects = new List<List<string>>();
        foreach (var project in await db.Projects.AsNoTracking().Where(x => x.Tags.Count > 0).OrderByDescending(x => x.UpdatedAt).Select(x => x.Id).Take(500).ToListAsync(ct))
        {
            if ((await ProjectAccess.FindAsync(db, project, me.Id, m, ct)).Project is { } seen)
            {
                projects.Add(seen.Tags);
            }
        }
        var used = chats.Concat(projects).SelectMany(t => t).GroupBy(t => t, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new { tag = g.Key, count = g.Count() }).ToList();
        return Results.Ok(new { known = options.CurrentValue.KnownTags, used });
    }

    /// <summary>A chat as the model reads it: its title and its newest messages, the person's and the answers'.</summary>
    internal static async Task<string> ChatTextAsync(AppDbContext db, Llm.Core.Chat.Conversation c, CancellationToken ct)
    {
        var said = await db.ChatMessages.AsNoTracking().Where(x => x.ConversationId == c.Id && (x.Role == "user" || x.Role == "assistant") && x.Content != "")
            .OrderByDescending(x => x.CreatedAt).Take(40).Select(x => new { x.Role, x.Content }).ToListAsync(ct);
        var b = new StringBuilder($"Chat: {c.Title}\n");
        foreach (var x in Enumerable.Reverse(said))
        {
            b.Append(x.Role == "user" ? "\nPerson: " : "\nAssistant: ").Append(x.Content.Length > 1500 ? x.Content[..1500] + "…" : x.Content).Append('\n');
        }
        return b.ToString();
    }

    private static async Task<IResult> SummarizeChatAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TagSummaries summaries, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await RoomAccess.FindAsync(db, id, me, Need.Manage, ct) is not { } c)
        {
            return Results.NotFound();
        }
        if (await summaries.SummarizeAsync(me, await ChatTextAsync(db, c, ct), ct) is not { } s)
        {
            return NoModel();
        }
        c.Brief = s.Brief;
        c.Tags = Tagging.Clean(c.Tags.Concat(s.Tags));
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("chat.summarized", c.Id.ToString(), detail: string.Join(", ", c.Tags), actor: me);
        return Results.Ok(new { brief = c.Brief, tags = c.Tags });
    }

    private static async Task<(Project? Project, IResult? Refused)> ProjectAsync(Guid id, AppUser me, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var (project, role) = await ProjectAccess.FindAsync(db, id, me.Id, await access.MembershipAsync(me, ct), ct);
        return project is null ? (null, Results.NotFound())
            : !ProjectAccess.Holds(role, ProjectMember.Contributor) ? (null, AuthEndpoints.Problem(403, "role", "Viewers see the project; its contributors and owners tag it."))
            : (project, null);
    }

    private static async Task<IResult> TagProjectAsync(Guid id, TagsRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        var (project, refused) = await ProjectAsync(id, me, db, access, ct);
        if (project is null)
        {
            return refused!;
        }
        project.Tags = Tagging.Clean(body.Tags);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("project.tagged", project.Id.ToString(), detail: $"{project.Name}: {string.Join(", ", project.Tags)}", actor: me);
        return Results.Ok(new { tags = project.Tags });
    }

    private static async Task<IResult> SummarizeProjectAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, TagSummaries summaries, Audit audit,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        var (project, refused) = await ProjectAsync(id, me, db, access, ct);
        if (project is null)
        {
            return refused!;
        }
        var b = new StringBuilder($"Project: {project.Name}\n{project.Description}\n");
        foreach (var t in await db.ProjectTasks.AsNoTracking().Where(t => t.ProjectId == project.Id).OrderBy(t => t.Title).Select(t => t.Title).Take(60).ToListAsync(ct))
        {
            b.Append("\nTask: ").Append(t);
        }
        foreach (var c in await db.Conversations.AsNoTracking().Where(c => c.WorkProjectId == project.Id && c.DeletedAt == null).OrderByDescending(c => c.UpdatedAt)
            .Select(c => new { c.Title, c.Brief }).Take(60).ToListAsync(ct))
        {
            b.Append("\nChat: ").Append(c.Title).Append(c.Brief is { } brief ? " — " + brief : "");
        }
        if (await summaries.SummarizeAsync(me, b.ToString(), ct) is not { } s)
        {
            return NoModel();
        }
        project.Brief = s.Brief;
        project.Tags = Tagging.Clean(project.Tags.Concat(s.Tags));
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("project.summarized", project.Id.ToString(), detail: $"{project.Name}: {string.Join(", ", project.Tags)}", actor: me);
        return Results.Ok(new { brief = project.Brief, tags = project.Tags });
    }

    /// <summary>A chat question's chat (its tags are the question's), or null.</summary>
    private static async Task<Llm.Core.Chat.Conversation?> ChatOfAsync(AppDbContext db, Guid message, CancellationToken ct) =>
        await db.ChatMessages.AsNoTracking().Where(m => m.Id == message).Select(m => m.ConversationId).FirstOrDefaultAsync(ct) is var chat && chat != Guid.Empty
            ? await db.Conversations.SingleOrDefaultAsync(c => c.Id == chat, ct) : null;

    private static async Task<IResult> TagPromptAsync(string source, Guid id, TagsRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Audit audit, CancellationToken ct)
    {
        var tags = Tagging.Clean(body.Tags);
        if (source == "api" && await db.PromptLog.SingleOrDefaultAsync(e => e.Id == id, ct) is { } line)
        {
            line.Tags = tags;
        }
        else if (source == "chat" && await ChatOfAsync(db, id, ct) is { } chat)
        {
            chat.Tags = tags;
        }
        else
        {
            return Results.NotFound();
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("prompts.tagged", id.ToString(), detail: $"{source}: {string.Join(", ", tags)}", actor: await Me(p, users));
        return Results.Ok(new { tags });
    }

    private static async Task<IResult> SummarizePromptAsync(string source, Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TagSummaries summaries, Audit audit,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        Summary? s;
        List<string> tags;
        string? brief;
        if (source == "api" && await db.PromptLog.SingleOrDefaultAsync(e => e.Id == id, ct) is { } line)
        {
            if ((s = await summaries.SummarizeAsync(me, $"Question: {line.Prompt}\n\nAnswer: {line.Answer ?? line.Error ?? "(none)"}", ct)) is null)
            {
                return NoModel();
            }
            (line.Brief, line.Tags) = (s.Brief, Tagging.Clean(line.Tags.Concat(s.Tags)));
            (brief, tags) = (line.Brief, line.Tags);
        }
        else if (source == "chat" && await ChatOfAsync(db, id, ct) is { } chat)
        {
            if ((s = await summaries.SummarizeAsync(me, await ChatTextAsync(db, chat, ct), ct)) is null)
            {
                return NoModel();
            }
            (chat.Brief, chat.Tags) = (s.Brief, Tagging.Clean(chat.Tags.Concat(s.Tags)));
            (brief, tags) = (chat.Brief, chat.Tags);
        }
        else
        {
            return Results.NotFound();
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("prompts.summarized", id.ToString(), detail: $"{source}: {string.Join(", ", tags)}", actor: me);
        return Results.Ok(new { brief, tags });
    }
}
