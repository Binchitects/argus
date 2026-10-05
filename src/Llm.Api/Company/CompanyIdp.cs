using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Llm.Api.Company;

/// <summary>What the identity provider says about a person, once its token (or SAML assertion) has been checked.</summary>
public sealed record CompanyPerson(string Subject, string? UserName, string? Email, bool? EmailVerified, string? DisplayName, IReadOnlyList<string> Groups, bool Saml = false);

/// <summary>Why a company sign-in failed: the identity provider could not be reached, or what it sent is not acceptable.</summary>
public sealed class CompanyIdpException(string message, bool unavailable = false, Exception? inner = null) : Exception(message, inner)
{
    public bool Unavailable { get; } = unavailable;
}

/// <summary>A result of <see cref="CompanyIdp.TestAsync"/>: whether the settings reach a provider, in words.</summary>
public sealed record CompanyTestResult(bool Ok, string Message);

/// <summary>
/// The authorization-code flow with PKCE against the company's identity provider,
/// by hand: its discovery document and keys over HTTP, its identity token checked
/// here (signature, issuer, audience, lifetime, nonce). Discovery and keys are kept
/// for an hour; an unknown key id fetches the keys again (the provider rotated them).
/// </summary>
public sealed class CompanyIdp(IHttpClientFactory http, TimeProvider clock) : IDisposable
{
    public const string Client = "company-idp";

    private static readonly TimeSpan Keep = TimeSpan.FromHours(1);
    private static readonly string[] Algorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
    ];

    private sealed record Provider(
        string ConfiguredIssuer, string Issuer, Uri Authorize, Uri Token, Uri? UserInfo, Uri Jwks, bool PostSecret,
        IReadOnlyList<SecurityKey> Keys, DateTimeOffset FetchedAt, DateTimeOffset KeysAt);

    private Provider? _provider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Random, URL-safe: 256 bits for a state, a nonce or a PKCE verifier.</summary>
    public static string Random() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>PKCE S256: the challenge sent with the authorization request for this verifier.</summary>
    public static string Challenge(string verifier) => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Where to send the browser: the provider's authorization endpoint with this request.</summary>
    public async Task<string> AuthorizeUrlAsync(CompanySignInOptions o, string redirectUri, string state, string nonce, string verifier, CancellationToken ct = default)
    {
        var p = await ProviderAsync(o, ct);
        return QueryHelpers.AddQueryString(p.Authorize.ToString(), new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = o.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = Scopes(o.Scopes),
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = Challenge(verifier),
            ["code_challenge_method"] = "S256",
        });
    }

    /// <summary>Redeems the code at the token endpoint and checks the identity token; the person it names.</summary>
    public async Task<CompanyPerson> RedeemAsync(CompanySignInOptions o, string code, string redirectUri, string verifier, string nonce, CancellationToken ct = default)
    {
        var p = await ProviderAsync(o, ct);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, p.Token);
        if (string.IsNullOrEmpty(o.ClientSecret))
        {
            form["client_id"] = o.ClientId!;
        }
        else if (p.PostSecret)
        {
            form["client_id"] = o.ClientId!;
            form["client_secret"] = o.ClientSecret;
        }
        else
        {
            // RFC 6749 2.3.1: both parts form-encoded before they are joined.
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{Uri.EscapeDataString(o.ClientId!)}:{Uri.EscapeDataString(o.ClientSecret)}")));
        }
        req.Content = new FormUrlEncodedContent(form);
        JsonObject tokens;
        try
        {
            using var res = await http.CreateClient(Client).SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                var error = (TryObject(body) is { } failed ? First(failed, "error") : null) ?? $"HTTP {(int)res.StatusCode}";
                throw new CompanyIdpException($"the identity provider refused the code: {error}", unavailable: (int)res.StatusCode >= 500);
            }
            tokens = TryObject(body) ?? throw new CompanyIdpException("the token endpoint did not answer JSON");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new CompanyIdpException($"the token endpoint cannot be reached: {ex.Message}", unavailable: true, ex);
        }
        var idToken = First(tokens, "id_token") ?? throw new CompanyIdpException("the identity provider sent no identity token (is openid among the scopes?)");
        var claims = await ValidateAsync(o, p, idToken, nonce, ct);
        var subject = First(claims, "sub") ?? throw new CompanyIdpException("the identity token names nobody (no sub)");
        if (p.UserInfo is not null && First(tokens, "access_token") is { Length: > 0 } access)
        {
            await MergeUserInfoAsync(p.UserInfo, access, subject, claims, ct);
        }
        var name = First(claims, "name")
            ?? string.Join(' ', new[] { First(claims, "given_name"), First(claims, "family_name") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new CompanyPerson(
            subject,
            First(claims, o.UserNameClaim),
            First(claims, "email"),
            First(claims, "email_verified") is { } v && bool.TryParse(v, out var verified) ? verified : null,
            string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            string.IsNullOrWhiteSpace(o.GroupsClaim) ? [] : [.. Strings(claims, o.GroupsClaim.Trim()).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>Reads the discovery document and the keys of these settings (saved or not): what an admin needs before saving them.</summary>
    public async Task<CompanyTestResult> TestAsync(CompanySignInOptions o, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(o.Issuer))
        {
            return new(false, "No issuer is set.");
        }
        if (!Uri.TryCreate(o.Issuer.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return new(false, $"{o.Issuer} is not an https:// (or http://) address.");
        }
        try
        {
            var p = await FetchAsync(o.Issuer.Trim(), ct);
            var client = string.IsNullOrWhiteSpace(o.ClientId) ? " Set the client ID to turn it on." : "";
            return new(true, $"Found {p.Issuer}: it signs with {p.Keys.Count} {(p.Keys.Count == 1 ? "key" : "keys")}{(p.UserInfo is null ? "" : " and has a userinfo endpoint")}.{client}");
        }
        catch (CompanyIdpException ex)
        {
            return new(false, char.ToUpperInvariant(ex.Message[0]) + ex.Message[1..] + ".");
        }
    }

    private async Task<Provider> ProviderAsync(CompanySignInOptions o, CancellationToken ct, bool freshKeys = false)
    {
        var issuer = o.Issuer!.Trim();
        var now = clock.GetUtcNow();
        var p = _provider;
        if (p is not null && p.ConfiguredIssuer == issuer && now - p.FetchedAt < Keep && !freshKeys)
        {
            return p;
        }
        await _gate.WaitAsync(ct);
        try
        {
            p = _provider;
            if (p is not null && p.ConfiguredIssuer == issuer && now - p.FetchedAt < Keep)
            {
                if (!freshKeys)
                {
                    return p;
                }
                // At most once a minute: a token with an unknown key id must not make every request fetch.
                if (now - p.KeysAt < TimeSpan.FromMinutes(1))
                {
                    return p;
                }
                p = p with { Keys = await KeysAsync(p.Jwks, ct), KeysAt = now };
            }
            else
            {
                p = await FetchAsync(issuer, ct);
            }
            _provider = p;
            return p;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Provider> FetchAsync(string issuer, CancellationToken ct)
    {
        var url = issuer.TrimEnd('/') + "/.well-known/openid-configuration";
        var doc = TryObject(await GetAsync(url, "discovery document", ct)) ?? throw new CompanyIdpException($"{url} is not a discovery document");
        string? S(string name) => doc[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var said = S("issuer") ?? throw new CompanyIdpException($"{url} names no issuer");
        // OIDC Discovery 4.3: the document must be the issuer's own.
        if (!string.Equals(said.TrimEnd('/'), issuer.TrimEnd('/'), StringComparison.Ordinal))
        {
            throw new CompanyIdpException($"the discovery document is for {said}, not {issuer}");
        }
        Uri Endpoint(string name) => Uri.TryCreate(S(name), UriKind.Absolute, out var u) ? u : throw new CompanyIdpException($"the discovery document has no {name}");
        var methods = doc["token_endpoint_auth_methods_supported"] is JsonArray a ? a.Select(m => m?.ToString() ?? "").ToList() : [];
        var jwks = Endpoint("jwks_uri");
        var now = clock.GetUtcNow();
        return new Provider(issuer, said, Endpoint("authorization_endpoint"), Endpoint("token_endpoint"),
            Uri.TryCreate(S("userinfo_endpoint"), UriKind.Absolute, out var info) ? info : null, jwks,
            PostSecret: methods.Contains("client_secret_post") && !methods.Contains("client_secret_basic"),
            await KeysAsync(jwks, ct), now, now);
    }

    private async Task<IReadOnlyList<SecurityKey>> KeysAsync(Uri jwks, CancellationToken ct)
    {
        try
        {
            var set = new JsonWebKeySet(await GetAsync(jwks.ToString(), "signing keys", ct));
            var keys = set.Keys.Where(k => k.Use is null or "sig").ToList();
            return keys.Count == 0 ? throw new CompanyIdpException($"{jwks} lists no signing keys") : keys;
        }
        catch (ArgumentException ex)
        {
            throw new CompanyIdpException($"{jwks} is not a key set", inner: ex);
        }
    }

    private async Task<string> GetAsync(string url, string what, CancellationToken ct)
    {
        try
        {
            using var res = await http.CreateClient(Client).GetAsync(new Uri(url), ct);
            return res.IsSuccessStatusCode
                ? await res.Content.ReadAsStringAsync(ct)
                : throw new CompanyIdpException($"the {what} at {url} answered HTTP {(int)res.StatusCode}", unavailable: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new CompanyIdpException($"the {what} at {url} cannot be reached: {ex.Message}", unavailable: true, ex);
        }
    }

    private async Task<JsonObject> ValidateAsync(CompanySignInOptions o, Provider p, string idToken, string nonce, CancellationToken ct)
    {
        var result = await CheckAsync(o, p, idToken);
        if (result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            p = await ProviderAsync(o, ct, freshKeys: true);
            result = await CheckAsync(o, p, idToken);
        }
        if (!result.IsValid)
        {
            throw new CompanyIdpException(result.Exception switch
            {
                SecurityTokenSignatureKeyNotFoundException or SecurityTokenInvalidSignatureException => "the identity token's signature does not match the provider's keys",
                SecurityTokenInvalidIssuerException => "the identity token is from another issuer",
                SecurityTokenInvalidAudienceException => "the identity token is for another client",
                SecurityTokenExpiredException or SecurityTokenNotYetValidException => "the identity token is out of date (check the clocks)",
                SecurityTokenInvalidAlgorithmException => "the identity token is signed with an algorithm the app does not accept",
                _ => "the identity token is not valid (" + (result.Exception?.GetType().Name ?? "unknown") + ")",
            });
        }
        var jwt = (JsonWebToken)result.SecurityToken;
        var claims = TryObject(Base64UrlEncoder.Decode(jwt.EncodedPayload)) ?? throw new CompanyIdpException("the identity token's claims are not JSON");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(First(claims, "nonce") ?? ""), Encoding.UTF8.GetBytes(nonce)))
        {
            throw new CompanyIdpException("the identity token's nonce is not this sign-in's");
        }
        // OIDC Core 3.1.3.7: with several audiences, the token must be issued to this client.
        if (Strings(claims, "aud").Count > 1 && First(claims, "azp") != o.ClientId)
        {
            throw new CompanyIdpException("the identity token was issued to another client");
        }
        return claims;
    }

    private static Task<TokenValidationResult> CheckAsync(CompanySignInOptions o, Provider p, string idToken) =>
        new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = p.Issuer,
            ValidAudience = o.ClientId,
            IssuerSigningKeys = p.Keys,
            ValidAlgorithms = Algorithms,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        });

    /// <summary>The userinfo endpoint fills in what the identity token leaves out (GitLab's groups, for one).</summary>
    private async Task MergeUserInfoAsync(Uri endpoint, string accessToken, string subject, JsonObject claims, CancellationToken ct)
    {
        JsonObject? info;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var res = await http.CreateClient(Client).SendAsync(req, ct);
            if (!res.IsSuccessStatusCode || res.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            {
                return;
            }
            info = TryObject(await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return; // the identity token alone still names the person
        }
        // OIDC Core 5.3.2: answers about anyone else are ignored.
        if (info is null || First(info, "sub") != subject)
        {
            return;
        }
        foreach (var (name, value) in info)
        {
            if (!claims.ContainsKey(name) && value is not null)
            {
                claims[name] = value.DeepClone();
            }
        }
    }

    public void Dispose() => _gate.Dispose();

    private static string? First(JsonObject claims, string name) => Strings(claims, name) is { Count: > 0 } values ? values[0] : null;

    /// <summary>A claim's values as text: its exact name first, then a dotted path into objects.</summary>
    public static IReadOnlyList<string> Strings(JsonObject claims, string name)
    {
        JsonNode? node = claims.TryGetPropertyValue(name, out var exact) ? exact : null;
        if (node is null && name.Contains('.', StringComparison.Ordinal))
        {
            node = claims;
            foreach (var part in name.Split('.'))
            {
                node = node is JsonObject o && o.TryGetPropertyValue(part, out var next) ? next : null;
            }
        }
        return node switch
        {
            JsonArray a => [.. a.OfType<JsonValue>().Select(Text).OfType<string>()],
            JsonValue v => Text(v) is { } s ? [s] : [],
            _ => [],
        };
    }

    private static string? Text(JsonValue v) => v.GetValueKind() switch
    {
        JsonValueKind.String => v.GetValue<string>(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => v.ToJsonString(),
        _ => null,
    };

    private static string Scopes(string configured)
    {
        var scopes = configured.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!scopes.Contains("openid"))
        {
            scopes.Insert(0, "openid");
        }
        return string.Join(' ', scopes);
    }

    private static JsonObject? TryObject(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
