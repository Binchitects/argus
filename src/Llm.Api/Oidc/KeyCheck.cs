using System.Collections.Concurrent;
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
/// person's gateway key (sk-...); Argus asks here, from inside the stack's
/// network with ARGUS_KEY, and gets the person's email and username (their
/// GitLab username). Refused through the proxy. The key is never logged or
/// kept: only its hash, for a minute, beside the email the gateway gave.
/// </summary>
public static class KeyCheck
{
    public const string Path = "/api/authz/key";

    /// <summary>How long the gateway's answer for a key is believed. The person is looked up on every check.</summary>
    public static readonly TimeSpan Remember = TimeSpan.FromSeconds(60);

    /// <summary>Headers Traefik adds (and what the forwarded-headers step leaves of them): their presence means the proxy.</summary>
    private static bool ThroughProxy(IHeaderDictionary headers) =>
        headers.Keys.Any(h => h.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || h.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase)
            || h.Equals("X-Real-Ip", StringComparison.OrdinalIgnoreCase) || h.Equals("Forwarded", StringComparison.OrdinalIgnoreCase));

    private sealed record Body(string? Key);

    public static void MapKeyCheck(this IEndpointRouteBuilder app)
    {
        // The gateway's answers by key hash, for this app only (tests run several).
        var known = new ConcurrentDictionary<string, (DateTimeOffset Until, string Email)>(StringComparer.Ordinal);
        app.MapPost(Path, (HttpContext ctx, ILiteLlm gateway, UserManager<AppUser> users, IOptions<ArgusOptions> argus, IOptionsMonitor<ChatOptions> chat,
            TimeProvider clock) => CheckAsync(ctx, gateway, users, argus.Value, chat.CurrentValue, clock, known));
    }

    private static async Task<IResult> CheckAsync(HttpContext ctx, ILiteLlm gateway, UserManager<AppUser> users, ArgusOptions argus, ChatOptions chat,
        TimeProvider clock, ConcurrentDictionary<string, (DateTimeOffset Until, string Email)> known)
    {
        // Argus's credentials: the chat's own when set, and ARGUS_KEY (Argus takes it as both).
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
        if (supplied.Length == 0 || !credentials.Any(c => SecretEquals(supplied, c)))
        {
            return Results.Json(new { status = "credential", error = "Wrong or missing credential." }, statusCode: StatusCodes.Status401Unauthorized);
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

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var now = clock.GetUtcNow();
        string email;
        if (known.TryGetValue(hash, out var hit) && hit.Until > now)
        {
            email = hit.Email;
        }
        else
        {
            GatewayKeyInfo? info;
            try
            {
                info = await gateway.KeyInfoAsync(key, ctx.RequestAborted);
            }
            catch (GatewayException)
            {
                return Results.Json(new { status = "gateway", error = "The gateway could not be asked about the key right now." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            if (info is null || info.Blocked || info.Expires <= now || string.IsNullOrWhiteSpace(info.UserId))
            {
                return Refused();
            }
            email = info.UserId;
            if (known.Count > 1000)
            {
                foreach (var (k, v) in known)
                {
                    if (v.Until <= now)
                    {
                        known.TryRemove(k, out _);
                    }
                }
            }
            var until = now + Remember;
            known[hash] = (info.Expires is { } expires && expires < until ? expires : until, email);
        }

        // The person now: someone disabled, or whose API access was taken, since is refused at once, whatever the gateway said.
        var user = await users.FindByEmailAsync(email);
        if (user is null || user.IsDisabled || user.ApiOff)
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
