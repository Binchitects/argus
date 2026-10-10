using Llm.Core.Access;
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
        admin.MapPut("/people/{id:guid}/credits", (Guid id, CreditsRequest body, PeopleService people) =>
            WithPerson(id, people, async u => { await people.SetCreditsAsync(u, body); return Results.NoContent(); }));
        // One kind's credit (the people list sets one kind for many at once).
        admin.MapPut("/people/{id:guid}/credits/{kind}", (Guid id, string kind, CreditRequest body, PeopleService people) =>
            Credits.Parse(kind) is not { } k
                ? Task.FromResult(AuthEndpoints.Problem(404, "kind", "The kinds are chat, api, pictures, video and speech."))
                : WithPerson(id, people, async u =>
                {
                    var credits = CreditsRequest.Of(u);
                    await people.SetCreditsAsync(u, k switch
                    {
                        CreditKind.Chat => credits with { Chat = body.Credit },
                        CreditKind.Api => credits with { Api = body.Credit },
                        CreditKind.Pictures => credits with { Pictures = body.Credit },
                        CreditKind.Video => credits with { Video = body.Credit },
                        _ => credits with { Speech = body.Credit },
                    });
                    return Results.NoContent();
                }));
        admin.MapPut("/people/{id:guid}/api", (Guid id, ApiAccessRequest body, PeopleService people) =>
            WithPerson(id, people, async u => Results.Ok(new { warning = await people.SetApiAccessAsync(u, body.On) })));
        admin.MapPut("/people/{id:guid}/limits", (Guid id, LimitsRequest body, PeopleService people, Models.KeyAccessWatcher keys) =>
            WithPerson(id, people, async u =>
            {
                var warning = await people.SetLimitsAsync(u, body.RequestsPerMinute, body.TokensPerMinute);
                // A key sync already under way read the person before this change: one more after it puts the new limit back if that sync undid it.
                keys.Wake();
                return Results.Ok(new { warning });
            }));
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
        // Local accounts to directory sign-in: what it would do for the chosen people, then doing it.
        admin.MapPost("/ldap/moves/plan", (MoveRequest body, ClaimsPrincipal p, IOptionsMonitor<LdapOptions> ldap, UserManager<AppUser> users, DirectoryMoves moves, CancellationToken ct) =>
            Moving(ldap, async () => Results.Ok(new { people = await moves.PlanAsync(body.Ids ?? [], (await users.GetUserAsync(p))!, ct) })));
        admin.MapPost("/ldap/moves", (MoveRequest body, ClaimsPrincipal p, IOptionsMonitor<LdapOptions> ldap, UserManager<AppUser> users, DirectoryMoves moves, CancellationToken ct) =>
            Moving(ldap, async () =>
            {
                var (moved, refused) = await moves.MoveAsync(body.Ids ?? [], (await users.GetUserAsync(p))!, ct);
                return Results.Ok(new { moved, refused });
            }));
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

    private static async Task<IResult> Moving(IOptionsMonitor<LdapOptions> ldap, Func<Task<IResult>> work)
    {
        if (!ldap.CurrentValue.Enabled)
        {
            return AuthEndpoints.Problem(400, "off", "LDAP is not configured.");
        }
        try
        {
            return await work();
        }
        catch (LdapUnavailableException ex)
        {
            return AuthEndpoints.Problem(503, "unavailable", ex.Message);
        }
    }

    private static async Task<IResult> ListAsync(AppDbContext db, UserManager<AppUser> users, Ledger ledger)
    {
        var admins = (await users.GetUsersInRoleAsync(Roles.Admin)).Select(u => u.Id).ToHashSet();
        var spending = await ledger.ReadAsync();
        var warning = spending.Problem;
        var people = await db.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync();
        return Results.Ok(new
        {
            warning,
            people = people.Select(u => Row(u, admins.Contains(u.Id), spending)),
        });
    }

    private static object Row(AppUser u, bool admin, Spending spending) => new
    {
        id = u.Id,
        userName = u.UserName,
        displayName = u.DisplayName,
        email = u.Email,
        isAdmin = admin,
        source = UserSources.Name(u.Source),
        disabled = u.IsDisabled,
        disabledReason = u.DisabledReason,
        twoFactorEnabled = u.TwoFactorEnabled,
        lockedOut = u.LockoutEnd > DateTimeOffset.UtcNow,
        lastSignInAt = u.LastSignInAt,
        createdAt = u.CreatedAt,
        // This month's spend, every kind together, and kind by kind with the person's own credit of each.
        spend = spending.Month is null ? (decimal?)null : spending.Of(u.Email),
        credits = spending.Kinds(u),
        overCredit = spending.Over(u).Select(Credits.Name),
        apiOff = u.ApiOff,
        legalHoldSince = u.LegalHoldSince,
        legalHoldReason = u.LegalHoldReason,
    };

    /// <summary>A kind's standing for the pages.</summary>
    internal static object Standing(CreditStanding s) => new
    {
        kind = Credits.Name(s.Kind), spent = s.Spent, credit = s.Credit, group = s.Group, groupLeft = s.GroupLeft,
    };

    private static async Task<IResult> CreateAsync(CreatePersonRequest body, PeopleService people)
    {
        try
        {
            var (user, secrets) = await people.CreateAsync(new NewPerson(body.UserName ?? "", body.Email ?? "", body.DisplayName, body.Admin, body.Credits));
            return Results.Created($"/api/admin/people/{user.Id}", new { id = user.Id, secrets.Password, secrets.ApiKey, secrets.Warning });
        }
        catch (PeopleException ex)
        {
            return AuthEndpoints.Problem(400, "invalid", ex.Message);
        }
    }

    private static Task<IResult> GetAsync(Guid id, PeopleService people, UserManager<AppUser> users, ILiteLlm gateway, Ledger ledger, Access.AccessService access, AppDbContext db,
        RateLimits rateLimits, Models.KeyAccess keyAccess, Credit credit) =>
        WithPerson(id, people, async u =>
        {
            IReadOnlyList<GatewayKey> keys = [];
            var spending = await ledger.ReadAsync();
            var warning = spending.Problem;
            try
            {
                keys = await gateway.KeysAsync(u.Email!);
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
                person = Row(u, await users.IsInRoleAsync(u, Roles.Admin), spending),
                // Kind by kind, with the tightest of their groups' credits.
                standing = (await credit.StandingAsync(u))?.Select(Standing),
                groups,
                directoryGroups = u.DirectoryGroups,
                keys = keys.Select(k => new { alias = k.Alias, preview = k.Preview, spend = k.Spend, blocked = k.Blocked, createdAt = k.CreatedAt }),
                limits = await rateLimits.ViewAsync(u, keys, keyAccess.MaxParallel),
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
                if (u.Source != UserSource.Local)
                {
                    throw new PeopleException(u.Source == UserSource.Ldap ? "The directory owns this person's name." : "The company's identity provider owns this person's name.");
                }
                u.DisplayName = name.Trim();
                await users.UpdateAsync(u);
                await audit.WriteAsync("person.rename", u.UserName);
            }
            if (body.Admin is { } admin)
            {
                if (u.Source != UserSource.Local)
                {
                    throw new PeopleException(u.Source == UserSource.Ldap
                        ? "The directory decides this person's role (its admin group)."
                        : "The company's identity provider decides this person's role (its admin group, at each sign-in).");
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
