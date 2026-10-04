using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Llm.Api.Bots;

/// <summary>
/// The Teams bot (an Azure Bot on the Bot Framework): the Bot Connector posts each message
/// to the app with a token it signed. The token is checked against the Bot Framework's
/// published keys (its OpenID metadata), for this bot, this channel and this service
/// address. The asker is found by the email Teams gives for the member; the answer is a
/// reply in the message's conversation (a channel's thread, a chat).
/// </summary>
public sealed partial class TeamsBot(IHttpClientFactory http, IOptionsMonitor<BotOptions> options, TimeProvider clock, ILogger<TeamsBot> logger) : IChatPlatform, IDisposable
{
    /// <summary>Where the Bot Framework publishes its signing keys.</summary>
    public const string Metadata = "https://login.botframework.com/v1/.well-known/openidconfiguration";

    /// <summary>Who signs the Bot Connector's tokens.</summary>
    public const string Issuer = "https://api.botframework.com";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile KeyRing? _keys;
    private volatile Grant? _token;

    /// <summary>The Bot Framework's keys, what each is endorsed for (channels), and when they were read.</summary>
    private sealed record KeyRing(JsonWebKeySet Keys, Dictionary<string, string[]> Endorsements, DateTimeOffset At);

    private sealed record Grant(string Token, DateTimeOffset Until);

    private TeamsOptions O => options.CurrentValue.Teams;

    public string Name => "teams";

    public string Title => "Microsoft Teams";

    /// <summary>A Teams message holds about 28 KB; characters can take more than one byte.</summary>
    public int Limit => 12000;

    public bool Ready => O is { AppId.Length: > 0, AppPassword.Length: > 0 };

    public PlatformOptions Options => O;

    /// <summary>
    /// Whether the Bot Connector sent this activity: its bearer token is signed by a key the
    /// Bot Framework publishes (and endorses for the activity's channel), issued for this bot,
    /// not expired, and names the activity's service address.
    /// </summary>
    public async Task<bool> AuthenticAsync(string? authorization, JsonObject activity, CancellationToken ct)
    {
        if (O.AppId is not { Length: > 0 } appId || authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var token = authorization["Bearer ".Length..].Trim();
        var channel = activity["channelId"]?.GetValue<string>() ?? "";
        var serviceUrl = activity["serviceUrl"]?.GetValue<string>() ?? "";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var keys = await KeysAsync(refresh: attempt > 0, ct);
            if (keys is null)
            {
                return false;
            }
            var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidIssuer = Issuer, ValidAudience = appId, IssuerSigningKeys = keys.Keys.GetSigningKeys(), ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                RequireSignedTokens = true, RequireExpirationTime = true, ClockSkew = TimeSpan.FromMinutes(5),
                LifetimeValidator = (before, expires, _, p) => (before is null || before.Value - p.ClockSkew <= clock.GetUtcNow().UtcDateTime)
                    && expires is { } e && clock.GetUtcNow().UtcDateTime <= e + p.ClockSkew,
            });
            if (!result.IsValid)
            {
                // A key made since the last look: look again, once.
                if (result.Exception is SecurityTokenSignatureKeyNotFoundException && attempt == 0)
                {
                    continue;
                }
                LogRefused(logger, result.Exception?.Message ?? "invalid");
                return false;
            }
            var jwt = (JsonWebToken)result.SecurityToken;
            // The key must be endorsed for the channel the activity says it comes from.
            if (!keys.Endorsements.TryGetValue(jwt.Kid ?? "", out var endorsed) || !endorsed.Contains(channel, StringComparer.OrdinalIgnoreCase))
            {
                LogRefused(logger, $"its key is not endorsed for {channel}");
                return false;
            }
            var claimed = jwt.TryGetPayloadValue<string>("serviceurl", out var s) ? s : null;
            if (claimed is null || !string.Equals(claimed.TrimEnd('/'), serviceUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                LogRefused(logger, "its service address differs from the activity's");
                return false;
            }
            return true;
        }
        return false;
    }

    /// <summary>The Bot Framework's keys and what each is endorsed for, read once a day (or when a token names a new key).</summary>
    private async Task<KeyRing?> KeysAsync(bool refresh, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (_keys is { } cached && now - cached.At < TimeSpan.FromHours(24) && !(refresh && now - cached.At > TimeSpan.FromMinutes(5)))
        {
            return cached;
        }
        await _lock.WaitAsync(ct);
        try
        {
            var client = http.CreateClient(BotEndpoints.Client);
            var metadata = JsonNode.Parse(await client.GetStringAsync(Metadata, ct));
            if (metadata?["jwks_uri"]?.GetValue<string>() is not { } jwksUri || !Uri.TryCreate(jwksUri, UriKind.Absolute, out var jwks) || jwks.Scheme != Uri.UriSchemeHttps)
            {
                return _keys;
            }
            var json = await client.GetStringAsync(jwks, ct);
            var endorsements = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var k in JsonNode.Parse(json)?["keys"]?.AsArray() ?? [])
            {
                if (k?["kid"]?.GetValue<string>() is { } kid)
                {
                    endorsements[kid] = [.. (k["endorsements"]?.AsArray() ?? []).Select(x => x?.GetValue<string>() ?? "")];
                }
            }
            _keys = new KeyRing(new JsonWebKeySet(json), endorsements, now);
            return _keys;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException or TaskCanceledException)
        {
            LogKeys(logger, ex.Message);
            return _keys;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The question in a message activity; null for any other (a member added, a reaction).</summary>
    public static BotQuestion? Read(JsonObject activity, out string channel, out bool direct)
    {
        var conversation = activity["conversation"] as JsonObject;
        direct = conversation?["conversationType"]?.GetValue<string>() == "personal";
        channel = activity["channelData"]?["channel"]?["id"]?.GetValue<string>() ?? conversation?["id"]?.GetValue<string>() ?? "";
        if (activity["type"]?.GetValue<string>() != "message" || conversation?["id"]?.GetValue<string>() is not { Length: > 0 } id
            || activity["from"]?["id"]?.GetValue<string>() is not { Length: > 0 } from || activity["serviceUrl"]?.GetValue<string>() is not { Length: > 0 } serviceUrl)
        {
            return null;
        }
        var name = activity["from"]?["name"]?.GetValue<string>() is { Length: > 0 } n ? n : "there";
        return new BotQuestion(id, from, name, BotText.TeamsQuestion(activity["text"]?.GetValue<string>() ?? ""), new JsonObject
        {
            ["serviceUrl"] = serviceUrl.TrimEnd('/'), ["conversation"] = id, ["activity"] = activity["id"]?.GetValue<string>(),
            ["bot"] = activity["recipient"]?.DeepClone(), ["from"] = activity["from"]?.DeepClone(),
        });
    }

    public async Task<BotQuestion> ResolveAsync(BotQuestion question, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{question.Reply["serviceUrl"]}/v3/conversations/{Uri.EscapeDataString(question.Thread)}/members/{Uri.EscapeDataString(question.User)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        using var response = await http.CreateClient(BotEndpoints.Client).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return question;
        }
        var member = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var email = member?["email"]?.GetValue<string>() is { Length: > 0 } e ? e : member?["userPrincipalName"]?.GetValue<string>();
        return question with { Email = email is { Length: > 0 } && email.Contains('@', StringComparison.Ordinal) ? email : null };
    }

    public async Task PostAsync(BotQuestion question, string text, CancellationToken ct)
    {
        var activity = question.Reply["activity"]?.GetValue<string>();
        var path = $"{question.Reply["serviceUrl"]}/v3/conversations/{Uri.EscapeDataString(question.Thread)}/activities" + (activity is null ? "" : $"/{Uri.EscapeDataString(activity)}");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        request.Content = new StringContent(new JsonObject
        {
            ["type"] = "message", ["text"] = text, ["textFormat"] = "markdown", ["replyToId"] = activity,
            ["from"] = question.Reply["bot"]?.DeepClone(), ["recipient"] = question.Reply["from"]?.DeepClone(),
            ["conversation"] = new JsonObject { ["id"] = question.Thread },
        }.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.CreateClient(BotEndpoints.Client).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"the Bot Connector said {(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }

    /// <summary>The bot's own token for the Bot Connector (client credentials), kept until five minutes before it expires.</summary>
    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is { } t && clock.GetUtcNow() < t.Until)
        {
            return t.Token;
        }
        var tenant = O.TenantId is { Length: > 0 } id ? id.Trim() : "botframework.com";
        using var response = await http.CreateClient(BotEndpoints.Client).PostAsync($"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = O.AppId ?? "", ["client_secret"] = O.AppPassword ?? "", ["scope"] = "https://api.botframework.com/.default",
            }), ct);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!response.IsSuccessStatusCode || body?["access_token"]?.GetValue<string>() is not { Length: > 0 } token)
        {
            throw new InvalidOperationException($"the bot could not sign in to Microsoft: {body?["error_description"]?.GetValue<string>() ?? body?["error"]?.GetValue<string>() ?? ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
        var seconds = body["expires_in"]?.GetValueKind() == JsonValueKind.Number ? body["expires_in"]!.GetValue<int>() : 3600;
        _token = new Grant(token, clock.GetUtcNow().AddSeconds(Math.Max(60, seconds - 300)));
        return token;
    }

    public void Dispose() => _lock.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Teams: an activity was refused: {Reason}")]
    private static partial void LogRefused(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Teams: the Bot Framework's keys could not be read: {Reason}")]
    private static partial void LogKeys(ILogger logger, string reason);
}
