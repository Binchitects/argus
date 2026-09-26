using System.Security.Claims;
using Llm.Api.Gateway;
using Llm.Api.Identity;
using Llm.Api.Ldap;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Endpoints;

public static class AdminEndpoints
{
    public const string Policy = "admin";

    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization(Policy);
        admin.MapGet("/people", ListAsync);
        admin.MapPost("/people", CreateAsync);
        admin.MapGet("/people/{id:guid}", GetAsync);
        admin.MapPatch("/people/{id:guid}", UpdateAsync);
        admin.MapPost("/people/{id:guid}/password", (Guid id, PeopleService people) =>
            WithPerson(id, people, async u => Results.Ok(await people.ResetPasswordAsync(u))));
        admin.MapPost("/people/{id:guid}/key", (Guid id, PeopleService people) =>
            WithPerson(id, people, async u => Results.Ok(await people.RotateKeyAsync(u))));
        admin.MapPut("/people/{id:guid}/budget", (Guid id, BudgetRequest body, PeopleService people) =>
            WithPerson(id, people, async u => { await people.SetBudgetAsync(u, body.Budget); return Results.NoContent(); }));
        admin.MapPost("/people/{id:guid}/2fa/reset", (Guid id, PeopleService people) =>
            WithPerson(id, people, async u => { await people.ResetTwoFactorAsync(u); return Results.NoContent(); }));
        admin.MapPost("/people/{id:guid}/sign-out", (Guid id, PeopleService people, Audit audit) =>
            WithPerson(id, people, async u => { await people.EndSessionsAsync(u); await audit.WriteAsync("person.sign_out", u.UserName); return Results.NoContent(); }));
        admin.MapDelete("/people/{id:guid}", async (Guid id, ClaimsPrincipal p, UserManager<AppUser> users, PeopleService people) =>
        {
            var actor = (await users.GetUserAsync(p))!;
            return await WithPerson(id, people, async u => { await people.DeleteAsync(actor, u); return Results.NoContent(); });
        });
        admin.MapGet("/audit", AuditAsync);
        admin.MapGet("/sign-in", (IOptionsMonitor<LdapOptions> monitor) =>
        {
            var ldap = monitor.CurrentValue;
            return Results.Ok(new
            {
                ldap = ldap.Enabled,
                ldapUrl = ldap.Enabled ? ldap.Url : null,
                adminGroup = ldap.AdminGroup,
                requiredGroup = ldap.RequiredGroup,
                syncMinutes = ldap.SyncInterval.TotalMinutes,
            });
        });
        admin.MapPost("/ldap/sync", async (IOptionsMonitor<LdapOptions> ldap, LdapSync sync, Audit audit) =>
        {
            if (!ldap.CurrentValue.Enabled)
            {
                return AuthEndpoints.Problem(400, "off", "LDAP is not configured.");
            }
            try
            {
                var (checkedCount, disabled) = await sync.RunOnceAsync();
                await audit.WriteAsync("ldap.sync", detail: $"checked {checkedCount}, disabled {disabled}");
                return Results.Ok(new { @checked = checkedCount, disabled });
            }
            catch (LdapUnavailableException ex)
            {
                return AuthEndpoints.Problem(503, "unavailable", ex.Message);
            }
        });
    }

    private static async Task<IResult> ListAsync(AppDbContext db, UserManager<AppUser> users, ILiteLlm gateway)
    {
        var admins = (await users.GetUsersInRoleAsync(Roles.Admin)).Select(u => u.Id).ToHashSet();
        IReadOnlyDictionary<string, GatewayUser> standing;
        string? warning = null;
        try
        {
            standing = await gateway.UsersAsync();
        }
        catch (GatewayException ex)
        {
            standing = new Dictionary<string, GatewayUser>();
            warning = $"Spend and credit are missing: {ex.Message}";
        }
        var people = await db.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync();
        return Results.Ok(new
        {
            warning,
            people = people.Select(u => Row(u, admins.Contains(u.Id), standing.GetValueOrDefault(u.Email ?? ""))),
        });
    }

    private static object Row(AppUser u, bool admin, GatewayUser? g) => new
    {
        id = u.Id,
        userName = u.UserName,
        displayName = u.DisplayName,
        email = u.Email,
        isAdmin = admin,
        source = u.Source == UserSource.Ldap ? "ldap" : "local",
        disabled = u.IsDisabled,
        disabledReason = u.DisabledReason,
        twoFactorEnabled = u.TwoFactorEnabled,
        lockedOut = u.LockoutEnd > DateTimeOffset.UtcNow,
        lastSignInAt = u.LastSignInAt,
        createdAt = u.CreatedAt,
        spend = g?.Spend,
        budget = g?.Budget,
    };

    private static async Task<IResult> CreateAsync(CreatePersonRequest body, PeopleService people)
    {
        try
        {
            var (user, secrets) = await people.CreateAsync(new NewPerson(body.UserName ?? "", body.Email ?? "", body.DisplayName, body.Admin, body.Budget));
            return Results.Created($"/api/admin/people/{user.Id}", new { id = user.Id, secrets.Password, secrets.ApiKey, secrets.Warning });
        }
        catch (PeopleException ex)
        {
            return AuthEndpoints.Problem(400, "invalid", ex.Message);
        }
    }

    private static Task<IResult> GetAsync(Guid id, PeopleService people, UserManager<AppUser> users, ILiteLlm gateway, Access.AccessService access, AppDbContext db) =>
        WithPerson(id, people, async u =>
        {
            IReadOnlyList<GatewayKey> keys = [];
            GatewayUser? standing = null;
            string? warning = null;
            try
            {
                keys = await gateway.KeysAsync(u.Email!);
                (await gateway.UsersAsync()).TryGetValue(u.Email!, out standing);
            }
            catch (GatewayException ex)
            {
                warning = ex.Message;
            }
            var member = await access.MembershipAsync(u);
            var groups = await db.Groups.AsNoTracking().Where(g => member.Groups.Contains(g.Id)).OrderBy(g => g.Name)
                .Select(g => new { g.Id, g.Name, directory = g.Directory != null }).ToListAsync();
            return Results.Ok(new
            {
                person = Row(u, await users.IsInRoleAsync(u, Roles.Admin), standing),
                groups,
                directoryGroups = u.DirectoryGroups,
                keys = keys.Select(k => new { alias = k.Alias, preview = k.Preview, spend = k.Spend, blocked = k.Blocked, createdAt = k.CreatedAt }),
                warning,
            });
        });

    private static async Task<IResult> UpdateAsync(Guid id, UpdatePersonRequest body, ClaimsPrincipal p, UserManager<AppUser> users, PeopleService people, Audit audit)
    {
        var actor = (await users.GetUserAsync(p))!;
        return await WithPerson(id, people, async u =>
        {
            if (body.DisplayName is { } name && name.Trim() != u.DisplayName)
            {
                if (u.Source == UserSource.Ldap)
                {
                    throw new PeopleException("The directory owns this person's name.");
                }
                u.DisplayName = name.Trim();
                await users.UpdateAsync(u);
                await audit.WriteAsync("person.rename", u.UserName);
            }
            if (body.Admin is { } admin)
            {
                if (u.Source == UserSource.Ldap)
                {
                    throw new PeopleException("The directory decides this person's role (its admin group).");
                }
                await people.SetAdminAsync(actor, u, admin);
            }
            if (body.Disabled is { } disabled)
            {
                await people.SetDisabledAsync(actor, u, disabled);
            }
            return Results.NoContent();
        });
    }

    private static async Task<IResult> AuditAsync(AppDbContext db, int take = 200, long? before = null)
    {
        var q = db.AuditEvents.AsNoTracking();
        if (before is { } b)
        {
            q = q.Where(e => e.Id < b);
        }
        return Results.Ok(await q.OrderByDescending(e => e.Id).Take(Math.Clamp(take, 1, 1000)).ToListAsync());
    }

    private static async Task<IResult> WithPerson(Guid id, PeopleService people, Func<AppUser, Task<IResult>> action)
    {
        var user = await people.FindAsync(id);
        if (user is null)
        {
            return Results.NotFound();
        }
        try
        {
            return await action(user);
        }
        catch (PeopleException ex)
        {
            return AuthEndpoints.Problem(400, "invalid", ex.Message);
        }
        catch (GatewayException ex)
        {
            return AuthEndpoints.Problem(502, "gateway", ex.Message);
        }
    }
}
