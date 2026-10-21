using System.Security.Claims;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Code;

public sealed record CodeHostToken(string? Token);

/// <summary>
/// Account → Code hosts: a person's own read-only token at GitHub or Gitea, so that reviews, the code assistant and the
/// chat's code tool read their pull requests there as them ("github:owner/repo"). Checked with the host through Argus
/// as it is linked, kept encrypted, and passed to Argus with each of the person's requests; never shown again.
/// </summary>
public static class CodeHostLinks
{
    public static void MapCodeHostLinks(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/account/code-hosts").PersonOnly();
        g.MapGet("", ListAsync);
        g.MapPut("/{kind}", LinkAsync);
        g.MapDelete("/{kind}", UnlinkAsync);
    }

    private static string Title(string kind) => kind == "github" ? "GitHub" : "Gitea";

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        var saved = await db.PersonCredentials.AsNoTracking().Where(c => c.UserId == me.Id && c.ToolId.StartsWith(ArgusMcp.CodeHostPrefix))
            .ToDictionaryAsync(c => c.ToolId[ArgusMcp.CodeHostPrefix.Length..], ct);
        return Results.Ok(ArgusMcp.CodeHosts.Select(k => new
        {
            kind = k, title = Title(k), linked = saved.ContainsKey(k), account = saved.GetValueOrDefault(k)?.Account, linkedAt = saved.GetValueOrDefault(k)?.CreatedAt,
        }));
    }

    private static async Task<IResult> LinkAsync(string kind, CodeHostToken body, ClaimsPrincipal p, UserManager<AppUser> users, ArgusMcp argus, Plugins.PersonCredentials credentials,
        Microsoft.Extensions.Options.IOptions<AuthOptions> auth, Audit audit, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        if (!ArgusMcp.CodeHosts.Contains(kind))
        {
            return Results.NotFound();
        }
        var token = body.Token?.Trim() ?? "";
        if (token.Length is < 8 or > 500 || token.Any(char.IsWhiteSpace))
        {
            return AuthEndpoints.Problem(400, "token", $"Paste a {Title(kind)} access token (read-only: contents, pull requests and metadata).");
        }
        if (!argus.Enabled)
        {
            return AuthEndpoints.Problem(503, "argus", "Argus is not set up here: it reads the code hosts.");
        }
        if (auth.Value.DataKey is not { Length: > 0 })
        {
            return AuthEndpoints.Problem(400, "data_key", "APP_DATA_KEY is not set, so a token (a secret) cannot be stored encrypted.");
        }
        var (login, problem) = await argus.HostUserAsync(me.Email!, kind, token, ct);
        if (login is null)
        {
            return AuthEndpoints.Problem(400, "token", problem!);
        }
        await credentials.SaveAsync(me.Id, ArgusMcp.CodeHostPrefix + kind, token, null, null, login, ct);
        await audit.WriteAsync("codehost.link", $"{kind}:{login}");
        return Results.Ok(new { kind, account = login });
    }

    private static async Task<IResult> UnlinkAsync(string kind, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Audit audit, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        var toolId = ArgusMcp.CodeHostPrefix + kind;
        if (await db.PersonCredentials.Where(c => c.UserId == me.Id && c.ToolId == toolId).ExecuteDeleteAsync(ct) == 0)
        {
            return Results.NotFound();
        }
        await audit.WriteAsync("codehost.unlink", kind);
        return Results.NoContent();
    }
}
