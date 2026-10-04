using System.Security.Claims;
using Llm.Api.Endpoints;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

public sealed record ProjectRequest(string? Name = null, string? Description = null, string? Instructions = null);

public sealed record ProjectFileRequest(Guid AttachmentId);

/// <summary>Projects (as in ChatGPT and Claude): chats together, with instructions and files every answer in them reads.</summary>
public static class ProjectEndpoints
{
    public const int MaxProjects = 200;
    /// <summary>With the embedder, answers read a big project's files by their passages (Knowledge/Retrieval.cs).</summary>
    public const int MaxFiles = 200;

    public static void MapProjects(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects").RequireAuthorization();
        g.MapGet("", ListAsync);
        g.MapPost("", CreateAsync);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPatch("/{id:guid}", UpdateAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
        g.MapPost("/{id:guid}/files", AddFileAsync);
        g.MapDelete("/{id:guid}/files/{attachmentId:guid}", RemoveFileAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        var list = await db.Projects.AsNoTracking().Where(x => x.UserId == me.Id).OrderByDescending(x => x.UpdatedAt)
            .Select(x => new
            {
                x.Id, x.Name, x.Description, x.UpdatedAt,
                chats = db.Conversations.Count(c => c.ProjectId == x.Id),
                files = db.ProjectFiles.Count(f => f.ProjectId == x.Id),
            }).ToListAsync(ct);
        return Results.Ok(list);
    }

    private static IResult? Check(ProjectRequest body, bool creating)
    {
        if (creating && string.IsNullOrWhiteSpace(body.Name))
        {
            return AuthEndpoints.Problem(400, "name", "Give the project a name.");
        }
        if (body.Name is { } n && (n.Trim().Length is 0 or > 100))
        {
            return AuthEndpoints.Problem(400, "name", "A name has 1 to 100 characters.");
        }
        if (body.Description is { Length: > 500 })
        {
            return AuthEndpoints.Problem(400, "description", "A description has at most 500 characters.");
        }
        return body.Instructions is { Length: > 20_000 } ? AuthEndpoints.Problem(400, "instructions", "Instructions have at most 20,000 characters.") : null;
    }

    private static async Task<IResult> CreateAsync(ProjectRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (Check(body, true) is { } problem)
        {
            return problem;
        }
        if (await db.Projects.CountAsync(x => x.UserId == me.Id, ct) >= MaxProjects)
        {
            return AuthEndpoints.Problem(409, "limit", $"At most {MaxProjects} projects each: remove one first.");
        }
        var project = new Project
        {
            UserId = me.Id, Name = body.Name!.Trim(), Description = Blank(body.Description), Instructions = Blank(body.Instructions),
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/projects/{project.Id}", new { project.Id, project.Name, project.Description, project.Instructions, project.UpdatedAt });
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == me.Id, ct) is not { } project)
        {
            return Results.NotFound();
        }
        var files = await db.ProjectFiles.AsNoTracking().Where(f => f.ProjectId == id)
            .Join(db.ChatAttachments, f => f.AttachmentId, a => a.Id, (f, a) => new { f.AddedAt, a })
            .OrderBy(x => x.AddedAt)
            .Select(x => new { x.a.Id, x.a.FileName, x.a.Size, x.a.Truncated, x.a.Kind, x.a.ContentType, original = x.a.Kind != "image" && x.a.Data != null, x.AddedAt })
            .ToListAsync(ct);
        var chats = await db.Conversations.AsNoTracking().Where(c => c.ProjectId == id).OrderByDescending(c => c.UpdatedAt).Take(300)
            .Select(c => new { c.Id, c.Title, c.UpdatedAt, c.ArchivedAt }).ToListAsync(ct);
        return Results.Ok(new { project.Id, project.Name, project.Description, project.Instructions, project.CreatedAt, project.UpdatedAt, files, chats });
    }

    private static async Task<IResult> UpdateAsync(Guid id, ProjectRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Projects.SingleOrDefaultAsync(x => x.Id == id && x.UserId == me.Id, ct) is not { } project)
        {
            return Results.NotFound();
        }
        if (Check(body, false) is { } problem)
        {
            return problem;
        }
        if (body.Name is { } name)
        {
            project.Name = name.Trim();
        }
        if (body.Description is not null)
        {
            project.Description = Blank(body.Description);
        }
        if (body.Instructions is not null)
        {
            project.Instructions = Blank(body.Instructions);
        }
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Removes a project: its chats stay (out of any project), or go too with ?chats=delete.</summary>
    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, string? chats = null, CancellationToken ct = default)
    {
        var me = await Me(p, users);
        if (await db.Projects.SingleOrDefaultAsync(x => x.Id == id && x.UserId == me.Id, ct) is not { } project)
        {
            return Results.NotFound();
        }
        if (chats == "delete")
        {
            await db.Conversations.Where(c => c.ProjectId == id && c.UserId == me.Id).ExecuteDeleteAsync(ct);
        }
        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> AddFileAsync(Guid id, ProjectFileRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Knowledge.FileIndex index,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Projects.SingleOrDefaultAsync(x => x.Id == id && x.UserId == me.Id, ct) is not { } project)
        {
            return Results.NotFound();
        }
        if (!await db.ChatAttachments.AnyAsync(a => a.Id == body.AttachmentId && a.UserId == me.Id, ct))
        {
            return AuthEndpoints.Problem(400, "attachment", "Upload the file first; only your own files can be added.");
        }
        if (await db.ProjectFiles.AnyAsync(f => f.ProjectId == id && f.AttachmentId == body.AttachmentId, ct))
        {
            return Results.NoContent();
        }
        if (await db.ProjectFiles.CountAsync(f => f.ProjectId == id, ct) >= MaxFiles)
        {
            return AuthEndpoints.Problem(409, "limit", $"At most {MaxFiles} files a project.");
        }
        db.ProjectFiles.Add(new ProjectFile { ProjectId = id, AttachmentId = body.AttachmentId });
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        // Embedded now, so a project too big to inline is read by its passages from its first question.
        _ = index.QueueAsync(body.AttachmentId);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveFileAsync(Guid id, Guid attachmentId, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!await db.Projects.AnyAsync(x => x.Id == id && x.UserId == me.Id, ct))
        {
            return Results.NotFound();
        }
        return await db.ProjectFiles.Where(f => f.ProjectId == id && f.AttachmentId == attachmentId).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();
    }
}
