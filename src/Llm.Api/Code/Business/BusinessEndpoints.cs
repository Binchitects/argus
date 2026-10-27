using System.Security.Claims;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Code;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Code.Business;

/// <summary>A project to map: its path in GitLab (group/project).</summary>
public sealed record BusinessRequest(string? Project);

/// <summary>
/// The business map's routes (/api/code/business): the maps the person may read, one in full, and a project mapped (or
/// mapped again) with their access, at their cost. An admin removes a map.
/// </summary>
public static class BusinessEndpoints
{
    public static void MapBusiness(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/code/business").RequireFeature("code-studio").PersonOnly().RequirePermission(Core.Access.Permissions.CodeRead);
        g.MapGet("", ListAsync);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPost("", MapAsync);
        g.MapPost("/{id:guid}/again", AgainAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static object Summary(BusinessSystem b) => new
    {
        b.Id, b.Project, b.Name, b.Domain, b.Summary, b.Status, b.Step, b.Error, b.Model, b.BuiltAt,
        counts = BusinessProfile.Read(b.Profile) is var p ? new
        {
            capabilities = p.Capabilities.Count, entities = p.Entities.Count, rules = p.Rules.Count, processes = p.Processes.Count,
            integrations = p.Integrations.Count, glossary = p.Glossary.Count,
        } : null,
    };

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, ArgusMcp argus, BusinessMap map, CancellationToken ct)
    {
        if (!argus.Enabled)
        {
            return Results.Ok(new { argus = false, systems = Array.Empty<object>() });
        }
        var me = await Me(p, users);
        return Results.Ok(new { argus = true, systems = (await map.VisibleAsync(db, argus, me, ct)).Select(Summary) });
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, ArgusMcp argus, BusinessMap map,
        IConfiguration config, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.BusinessSystems.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, ct) is not { } b || !await map.MayReadAsync(argus, me, b.Project, ct))
        {
            return Results.NotFound();
        }
        var gitlab = config["Argus:GitlabUrl"]?.TrimEnd('/');
        return Results.Ok(new
        {
            system = Summary(b), profile = BusinessProfile.Read(b.Profile),
            // Where a source opens: the project in GitLab (its files on the default branch, its issues, merge requests and wiki).
            web = gitlab is { Length: > 0 } ? $"{gitlab}/{b.Project}" : null,
        });
    }

    private static async Task<IResult> MapAsync(BusinessRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, ArgusMcp argus, BusinessMap map,
        Audit audit, CancellationToken ct)
    {
        if (!argus.Enabled)
        {
            return AuthEndpoints.Problem(503, "argus", "Argus is not deployed here, so there is nothing to map with.");
        }
        var me = await Me(p, users);
        var project = (body.Project ?? "").Trim().Trim('/');
        if (project.Length is 0 or > 300 || !project.Contains('/', StringComparison.Ordinal))
        {
            return AuthEndpoints.Problem(400, "project", "Name the project by its path in GitLab: group/project.");
        }
        if (!await map.MayReadAsync(argus, me, project, ct))
        {
            return AuthEndpoints.Problem(404, "project", $"Argus does not show you {project}: check its path, and that you are a member of it.");
        }
        var system = await db.BusinessSystems.SingleOrDefaultAsync(b => b.Project == project, ct);
        if (system is { Status: BusinessSystem.Building })
        {
            return AuthEndpoints.Problem(409, "building", $"{project} is being mapped now.");
        }
        if (system is null)
        {
            system = new BusinessSystem { Project = project, Name = project[(project.LastIndexOf('/') + 1)..] };
            db.BusinessSystems.Add(system);
        }
        Restart(system, me);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("business.map", project);
        map.Start(system.Id);
        return Results.Created($"/api/code/business/{system.Id}", Summary(system));
    }

    private static async Task<IResult> AgainAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, ArgusMcp argus, BusinessMap map,
        Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.BusinessSystems.SingleOrDefaultAsync(b => b.Id == id, ct) is not { } system || !await map.MayReadAsync(argus, me, system.Project, ct))
        {
            return Results.NotFound();
        }
        if (system.Status == BusinessSystem.Building)
        {
            return AuthEndpoints.Problem(409, "building", $"{system.Project} is being mapped now.");
        }
        Restart(system, me);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("business.map", system.Project);
        map.Start(system.Id);
        return Results.Ok(Summary(system));
    }

    private static void Restart(BusinessSystem system, AppUser me)
    {
        (system.Status, system.Step, system.Error, system.BuiltBy) = (BusinessSystem.Building, "Starting", null, me.Id);
        (system.PromptTokens, system.CompletionTokens) = (0, 0);
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, Audit audit,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!(await access.MembershipAsync(me, ct)).IsAdmin)
        {
            return AuthEndpoints.Problem(403, "admin", "Only admins remove a business map.");
        }
        if (await db.BusinessSystems.SingleOrDefaultAsync(b => b.Id == id, ct) is not { } system)
        {
            return Results.NotFound();
        }
        db.BusinessSystems.Remove(system);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("business.remove", system.Project);
        return Results.NoContent();
    }
}
