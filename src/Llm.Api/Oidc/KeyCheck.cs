using Llm.Api.Access;
using System.Security.Cryptography;
using System.Text;
using Llm.Api.Chat;
using Llm.Api.Gateway;
using Llm.Api.Operations;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.Oidc;

/// <summary>
/// Whose an API key is, for Argus. A coding agent connects to Argus with the
/// person's API key (sk-...); Argus asks here, from inside the stack's
/// network with ARGUS_KEY, and gets the person's email and username (their
/// GitLab username). Refused through the proxy. The key is never logged or
/// kept: the app's keys are read by its hash (<see cref="KeyBook"/>).
/// </summary>
public static class KeyCheck
{
    public const string Path = "/api/authz/key";

    /// <summary>Headers Traefik adds (and what the forwarded-headers step leaves of them): their presence means the proxy.</summary>
    private static bool ThroughProxy(IHeaderDictionary headers) =>
        headers.Keys.Any(h => h.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || h.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase)
            || h.Equals("X-Real-Ip", StringComparison.OrdinalIgnoreCase) || h.Equals("Forwarded", StringComparison.OrdinalIgnoreCase));

    private sealed record Body(string? Key);

    public static void MapKeyCheck(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, (HttpContext ctx, KeyBook book, UserManager<AppUser> users, IOptions<ArgusOptions> argus, IOptionsMonitor<ChatOptions> chat,
            TimeProvider clock, Access.AccessService access) => CheckAsync(ctx, book, users, access, argus.Value, chat.CurrentValue, clock))
            .AllowAnonymous("Argus and Laya ask whose an API key is: the key itself is checked.");
        app.MapPost(CodePath, (HttpContext ctx, UserManager<AppUser> users, IOptions<ArgusOptions> argus, IOptionsMonitor<ChatOptions> chat, Access.AccessService access) =>
                CodeAsync(ctx, users, access, argus.Value, chat.CurrentValue))
            .AllowAnonymous("Argus asks which repositories a person may read by the app's rules: Argus's own credential is checked.");
    }

    public const string CodePath = "/api/authz/code";

    private sealed record CodeBody(string? Email, string[]? Repos);

    /// <summary>
    /// Which of Argus's repositories (by path) a person may read by the app's rules (Admin → Access: code.read, and
    /// repository rules, on "gitlab:group/project"); GitLab's own rights stay the ceiling in Argus. No repository rules: every one.
    /// </summary>
    private static async Task<IResult> CodeAsync(HttpContext ctx, UserManager<AppUser> users, Access.AccessService access, ArgusOptions argus, ChatOptions chat)
    {
        if (Caller(ctx, argus, chat) is { } refused)
        {
            return refused;
        }
        CodeBody? body;
        try
        {
            body = await ctx.Request.ReadFromJsonAsync<CodeBody>(ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            body = null;
        }
        if (string.IsNullOrWhiteSpace(body?.Email) || body.Repos is null || body.Repos.Length > 100_000)
        {
            return Results.Json(new { error = "Send {\"email\": \"...\", \"repos\": [\"group/project\", ...]}." }, statusCode: StatusCodes.Status400BadRequest);
        }
        var user = await users.FindByEmailAsync(body.Email.Trim());
        if (user is null || user.IsDisabled)
        {
            return Results.Ok(new { read = false, allowed = Array.Empty<string>() });
        }
        var m = await access.MembershipAsync(user, ctx.RequestAborted);
        if (!m.Has(Core.Access.Permissions.CodeRead))
        {
            return Results.Ok(new { read = false, allowed = Array.Empty<string>() });
        }
        return Results.Ok(new { read = true, allowed = body.Repos.Where(r => m.Check(Core.Access.ResourceKinds.Repo, r.Contains(':', StringComparison.Ordinal) ? r : "gitlab:" + r, Core.Access.Permissions.CodeRead).Allowed) });
    }

    /// <summary>Argus's credential, from inside the stack's network; null when it is right, else the refusal.</summary>
    private static IResult? Caller(HttpContext ctx, ArgusOptions argus, ChatOptions chat)
    {
        string[] credentials = [.. new[] { chat.ArgusChatToken, argus.AdminToken }.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!)];
        if (credentials.Length == 0)
        {
            return Results.NotFound();
        }
        if (ThroughProxy(ctx.Request.Headers))
        {
            return Results.Json(new { status = "proxy", error = "Keys are checked only for Argus, inside the stack's network, not through the proxy." },
                statusCode: StatusCodes.Status403Forbidden);
        }
        var auth = ctx.Request.Headers.Authorization.ToString();
        var supplied = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : "";
        return supplied.Length == 0 || !credentials.Any(c => SecretEquals(supplied, c))
            ? Results.Json(new { status = "credential", error = "Wrong or missing credential." }, statusCode: StatusCodes.Status401Unauthorized)
            : null;
    }

    private static async Task<IResult> CheckAsync(HttpContext ctx, KeyBook book, UserManager<AppUser> users, Access.AccessService access, ArgusOptions argus, ChatOptions chat,
        TimeProvider clock)
    {
        // Argus's credentials: the chat's own when set, and ARGUS_KEY (Argus takes it as both).
        if (Caller(ctx, argus, chat) is { } refused)
        {
            return refused;
        }

        Body? body;
        try
        {
            body = await ctx.Request.ReadFromJsonAsync<Body>(ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            body = null;
        }
        var key = body?.Key?.Trim() ?? "";
        if (key.Length == 0)
        {
            return Results.Json(new { error = "Send the key as {\"key\": \"sk-...\"}." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var holder = await book.ForAsync(key, ctx.RequestAborted);
        if (holder is null || holder.ExpiredAt(clock.GetUtcNow()))
        {
            return Refused();
        }

        // The person now (by the key's owner: an email changed since is theirs): someone disabled, or whose API access was
        // taken, since is refused at once.
        var user = await users.FindByIdAsync(holder.UserId.ToString());
        if (user is null || user.IsDisabled || await access.ApiOffAsync(user, ctx.RequestAborted))
        {
            return Refused();
        }
        return Results.Ok(new { email = user.Email, username = user.UserName });
    }

    private static IResult Refused() =>
        Results.Json(new { status = "invalid_key", error = "That API key is not valid: unknown, blocked or expired, or its account is disabled." },
            statusCode: StatusCodes.Status401Unauthorized);

    private static bool SecretEquals(string supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
}
