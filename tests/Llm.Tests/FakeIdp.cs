using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Llm.Tests;

/// <summary>
/// A company identity provider at https://idp.test: discovery, keys, a token endpoint
/// that checks the client and PKCE, and userinfo. <see cref="Approve"/> plays the
/// person signing in there and returns where the provider sends their browser.
/// </summary>
public sealed class FakeIdp : HttpMessageHandler
{
    public const string Issuer = "https://idp.test";
    public const string ClientId = "arena";
    public const string ClientSecret = "idp-client-secret-for-tests";
    private const string KeyId = "idp-key-1";

    /// <summary>A person at the provider. Groups go in the identity token, or only in userinfo (as GitLab does).</summary>
    public sealed record Person(string Sub, string? UserName, string? Email, string[] Groups, string? Name = null, bool? EmailVerified = null, bool GroupsInUserInfo = false);

    /// <summary>What a forged or broken identity token gets wrong.</summary>
    public sealed record Forgery(string? Issuer = null, string? Audience = null, string? Nonce = null, bool OtherKey = false, bool Expired = false);

    private sealed record Grant(Person Person, string Nonce, string Challenge, string RedirectUri, Forgery? Forgery);

    private readonly RSA _key = RSA.Create(2048);
    // The same key id with another key: a token signed with it must not pass.
    private readonly RSA _other = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, Grant> _codes = new();
    private readonly ConcurrentDictionary<string, Person> _access = new();

    public bool Down { get; set; }

    /// <summary>The person signs in at the provider: the authorization request is checked and the browser sent back with a code.</summary>
    public string Approve(Uri authorize, Person person, Forgery? forgery = null)
    {
        Assert.Equal(Issuer + "/authorize", authorize.GetLeftPart(UriPartial.Path));
        var q = QueryHelpers.ParseQuery(authorize.Query);
        Assert.Equal("code", q["response_type"].ToString());
        Assert.Equal(ClientId, q["client_id"].ToString());
        Assert.Equal("S256", q["code_challenge_method"].ToString());
        Assert.Contains("openid", q["scope"].ToString().Split(' '));
        var redirect = q["redirect_uri"].ToString();
        Assert.Equal($"https://{AppFixture.Domain}/api/auth/company/callback", redirect);
        var code = Guid.NewGuid().ToString("N");
        _codes[code] = new Grant(person, q["nonce"].ToString(), q["code_challenge"].ToString(), redirect, forgery);
        return new Uri(QueryHelpers.AddQueryString(redirect, new Dictionary<string, string?> { ["code"] = code, ["state"] = q["state"].ToString() })).PathAndQuery;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down)
        {
            throw new HttpRequestException("Connection refused (idp.test:443)");
        }
        return request.RequestUri!.AbsolutePath switch
        {
            "/.well-known/openid-configuration" => Json(new Dictionary<string, object>
            {
                ["issuer"] = Issuer,
                ["authorization_endpoint"] = Issuer + "/authorize",
                ["token_endpoint"] = Issuer + "/token",
                ["userinfo_endpoint"] = Issuer + "/userinfo",
                ["jwks_uri"] = Issuer + "/jwks",
                ["response_types_supported"] = new[] { "code" },
                ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "client_secret_post" },
            }),
            "/jwks" => Json(new { keys = new[] { Jwk(_key) } }),
            "/token" => await TokenAsync(request, cancellationToken),
            "/userinfo" => UserInfo(request),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private async Task<HttpResponseMessage> TokenAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var basic = request.Headers.Authorization;
        if (basic?.Scheme != "Basic" || Encoding.UTF8.GetString(Convert.FromBase64String(basic.Parameter!)) != $"{ClientId}:{ClientSecret}")
        {
            return Json(new { error = "invalid_client" }, HttpStatusCode.Unauthorized);
        }
        var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
        if (form["grant_type"] != "authorization_code" || !_codes.TryRemove(form["code"].ToString(), out var grant) || form["redirect_uri"] != grant.RedirectUri)
        {
            return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
        }
        // PKCE: only the app that started the sign-in knows the verifier.
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString())));
        if (challenge != grant.Challenge)
        {
            return Json(new { error = "invalid_grant", error_description = "PKCE verification failed" }, HttpStatusCode.BadRequest);
        }
        var p = grant.Person;
        var f = grant.Forgery ?? new Forgery();
        var claims = new Dictionary<string, object> { ["sub"] = p.Sub, ["nonce"] = f.Nonce ?? grant.Nonce };
        if (p.UserName is not null)
        {
            claims["preferred_username"] = p.UserName;
            claims["nickname"] = p.UserName;
        }
        if (p.Email is not null)
        {
            claims["email"] = p.Email;
        }
        if (p.EmailVerified is { } verified)
        {
            claims["email_verified"] = verified;
        }
        if (p.Name is not null)
        {
            claims["name"] = p.Name;
        }
        if (!p.GroupsInUserInfo)
        {
            claims["groups"] = p.Groups;
        }
        var now = DateTime.UtcNow;
        var key = new RsaSecurityKey(f.OtherKey ? _other : _key) { KeyId = KeyId };
        var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = f.Issuer ?? Issuer,
            Audience = f.Audience ?? ClientId,
            IssuedAt = f.Expired ? now.AddHours(-2) : now,
            NotBefore = f.Expired ? now.AddHours(-2) : now,
            Expires = f.Expired ? now.AddHours(-1) : now.AddMinutes(5),
            Claims = claims,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
        var access = "at-" + Guid.NewGuid().ToString("N");
        _access[access] = p;
        return Json(new { access_token = access, token_type = "Bearer", expires_in = 300, id_token = idToken });
    }

    private HttpResponseMessage UserInfo(HttpRequestMessage request)
    {
        if (request.Headers.Authorization is not { Scheme: "Bearer" } bearer || !_access.TryGetValue(bearer.Parameter!, out var p))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
        return Json(new Dictionary<string, object?> { ["sub"] = p.Sub, ["email"] = p.Email, ["groups"] = p.Groups });
    }

    private static object Jwk(RSA rsa)
    {
        var p = rsa.ExportParameters(false);
        return new { kty = "RSA", use = "sig", alg = "RS256", kid = KeyId, n = Base64UrlEncoder.Encode(p.Modulus), e = Base64UrlEncoder.Encode(p.Exponent) };
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
