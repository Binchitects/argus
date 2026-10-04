using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Llm.Tests;

/// <summary>
/// The chat platforms the bots talk to, and the browsers' push services, by host:
///   slack.com                 users.info (emails from <see cref="SlackEmails"/>) and chat.postMessage
///   mattermost.test           users/{id}, posts/{id} (threads from <see cref="MattermostRoots"/>) and posting
///   login.botframework.com    the Bot Framework's OpenID metadata and keys (<see cref="BotFrameworkKey"/>, endorsed for msteams)
///   login.microsoftonline.com the bot's own token
///   smba.test                 the Bot Connector: a conversation's members and replies
///   fcm.googleapis.com, updates.push.services.mozilla.com  push services, answering <see cref="PushAnswer"/>
/// Everything posted is kept in <see cref="Posts"/>.
/// </summary>
public sealed class FakeChatPlatforms : HttpMessageHandler
{
    public sealed record Post(string Method, Uri Url, string Body, IReadOnlyDictionary<string, string> Headers, byte[] Bytes);

    public ConcurrentQueue<Post> Posts { get; } = new();

    public ConcurrentDictionary<string, string> SlackEmails { get; } = new();

    public ConcurrentDictionary<string, string> MattermostEmails { get; } = new();

    /// <summary>Post id → its thread's first post (a reply in a thread).</summary>
    public ConcurrentDictionary<string, string> MattermostRoots { get; } = new();

    public ConcurrentDictionary<string, string> TeamsEmails { get; } = new();

    public HttpStatusCode PushAnswer { get; set; } = HttpStatusCode.Created;

    /// <summary>The key the Bot Framework signs the Bot Connector's tokens with.</summary>
    public RSA BotFrameworkKey { get; } = RSA.Create(2048);

    public const string Kid = "bf-key-1";

    /// <summary>A Bot Connector token for <paramref name="audience"/>, as the Bot Framework makes them; <paramref name="key"/> to sign with another key.</summary>
    public string BotConnectorToken(string audience, string serviceUrl, RSA? key = null, string kid = Kid, string issuer = "https://api.botframework.com") =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = audience, Expires = DateTime.UtcNow.AddMinutes(10), NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Claims = new Dictionary<string, object> { ["serviceurl"] = serviceUrl },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key ?? BotFrameworkKey) { KeyId = kid }, SecurityAlgorithms.RsaSha256),
        });

    public IEnumerable<Post> To(string host, string path) => Posts.Where(p => p.Url.Host == host && Uri.UnescapeDataString(p.Url.AbsolutePath) == path);

    /// <summary>Waits for a post to a place, up to ten seconds.</summary>
    public async Task<Post> WaitAsync(string host, string path, Func<Post, bool>? match = null)
    {
        for (var i = 0; i < 200; i++)
        {
            if (To(host, path).FirstOrDefault(p => match?.Invoke(p) ?? true) is { } found)
            {
                return found;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"Nothing was posted to {host}{path}.");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        if (request.Method != HttpMethod.Get)
        {
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            Posts.Enqueue(new Post(request.Method.Method, url, Encoding.UTF8.GetString(bytes), headers, bytes));
        }
        var auth = request.Headers.Authorization?.ToString();
        return (url.Host, url.AbsolutePath) switch
        {
            ("slack.com", "/api/users.info") when auth == "Bearer xoxb-test" =>
                Json(SlackEmails.TryGetValue(Query(url, "user"), out var se) ? new JsonObject { ["ok"] = true, ["user"] = new JsonObject { ["profile"] = new JsonObject { ["email"] = se } } }
                    : new JsonObject { ["ok"] = false, ["error"] = "user_not_found" }),
            ("slack.com", "/api/chat.postMessage") when auth == "Bearer xoxb-test" => Json(new JsonObject { ["ok"] = true, ["ts"] = "1.2" }),
            ("slack.com", _) => Json(new JsonObject { ["ok"] = false, ["error"] = "invalid_auth" }),

            ("mattermost.test", _) when auth != "Bearer mm-bot-token" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            ("mattermost.test", var p) when p.StartsWith("/api/v4/users/", StringComparison.Ordinal) =>
                Json(new JsonObject { ["id"] = p[14..], ["email"] = MattermostEmails.GetValueOrDefault(p[14..], "") }),
            ("mattermost.test", var p) when p.StartsWith("/api/v4/posts/", StringComparison.Ordinal) =>
                Json(new JsonObject { ["id"] = p[14..], ["root_id"] = MattermostRoots.GetValueOrDefault(p[14..], "") }),
            ("mattermost.test", "/api/v4/posts") => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"id":"answer"}""") },

            ("login.botframework.com", "/v1/.well-known/openidconfiguration") =>
                Json(new JsonObject { ["issuer"] = "https://api.botframework.com", ["jwks_uri"] = "https://login.botframework.com/v1/.well-known/keys" }),
            ("login.botframework.com", "/v1/.well-known/keys") => Json(Keys()),
            ("login.microsoftonline.com", _) => Json(new JsonObject { ["access_token"] = "bot-access-token", ["expires_in"] = 3600, ["token_type"] = "Bearer" }),

            ("smba.test", _) when auth != "Bearer bot-access-token" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            ("smba.test", var p) when request.Method == HttpMethod.Get && p.Contains("/members/", StringComparison.Ordinal) =>
                TeamsEmails.TryGetValue(Uri.UnescapeDataString(p[(p.LastIndexOf('/') + 1)..]), out var te)
                    ? Json(new JsonObject { ["id"] = "member", ["email"] = te, ["userPrincipalName"] = te })
                    : new HttpResponseMessage(HttpStatusCode.NotFound),
            ("smba.test", _) => Json(new JsonObject { ["id"] = "reply-1" }),

            ("fcm.googleapis.com" or "updates.push.services.mozilla.com", _) => new HttpResponseMessage(PushAnswer),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private JsonObject Keys()
    {
        var p = BotFrameworkKey.ExportParameters(false);
        return new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject
            {
                ["kty"] = "RSA", ["use"] = "sig", ["kid"] = Kid, ["n"] = Base64UrlEncoder.Encode(p.Modulus), ["e"] = Base64UrlEncoder.Encode(p.Exponent),
                ["endorsements"] = new JsonArray("msteams", "skype"),
            }),
        };
    }

    private static string Query(Uri url, string key) =>
        url.Query.TrimStart('?').Split('&').Select(kv => kv.Split('=', 2)).Where(kv => kv[0] == key).Select(kv => Uri.UnescapeDataString(kv[1])).FirstOrDefault() ?? "";

    private static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

}
