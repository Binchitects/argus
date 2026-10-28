using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>Arena refused the sign-in (a wrong password, a locked account, the second factor), in its own words.</summary>
internal sealed class ArenaSignInException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}

/// <summary>
/// Code Arena signed in to Arena with a user name and password, as Arena's sign-in page signs a person in, so its lockout,
/// its directory and its second factor hold. A key of the person's own is then made for this machine ("Code Arena on
/// NAME"; one of that name before it is revoked first) and the web session signed out again: the key is what Code Arena
/// keeps, as with code-arena login and a key pasted in. Someone who signs in with the company's account has no password
/// here: they sign in to Arena in the browser and make a key there.
/// </summary>
internal sealed class ArenaSignIn(Uri arena, string? caFile) : IDisposable
{
    // Its own cookies: the session lives only as long as this sign-in.
    private readonly HttpClient _http = Net.Client(caFile);

    /// <summary>The name of the key made for this machine.</summary>
    public static string KeyName => $"Code Arena on {Environment.MachineName}";

    /// <summary>The password checked: true when the second factor is asked for next (<see cref="CodeAsync"/>).</summary>
    public async Task<bool> PasswordAsync(string userName, string password, CancellationToken ct)
    {
        var said = await SendAsync(HttpMethod.Post, "/api/auth/login", new JsonObject { ["userName"] = userName, ["password"] = password, ["remember"] = false }, ct);
        return said?["status"]?.GetValue<string>() == "2fa";
    }

    /// <summary>The second factor: a code from the authenticator app (or a recovery code, with its hyphen).</summary>
    public async Task CodeAsync(string code, CancellationToken ct) =>
        await SendAsync(HttpMethod.Post, "/api/auth/login/2fa", new JsonObject { ["code"] = code, ["recovery"] = code.Contains('-'), ["remember"] = false }, ct);

    /// <summary>Signed in: the key made for this machine (shown nowhere, kept in config.json), then the web session signed out.</summary>
    public async Task<string> KeyAsync(CancellationToken ct)
    {
        try
        {
            var name = KeyName;
            var mine = await SendAsync(HttpMethod.Get, "/api/account/keys/own", null, ct);
            foreach (var old in (mine?["keys"] as JsonArray ?? []).OfType<JsonObject>().Where(k => k["kind"]?.GetValue<string>() == "own" && k["name"]?.GetValue<string>() == name))
            {
                await SendAsync(HttpMethod.Delete, $"/api/account/keys/own/{Uri.EscapeDataString(old["id"]!.GetValue<string>())}", null, ct);
            }
            var made = await SendAsync(HttpMethod.Post, "/api/account/keys/own", new JsonObject { ["name"] = name }, ct);
            return made?["apiKey"]?.GetValue<string>() is { Length: > 0 } key ? key : throw new ArenaSignInException("Arena made no key.");
        }
        finally
        {
            try
            {
                await SendAsync(HttpMethod.Post, "/api/auth/logout", new JsonObject(), CancellationToken.None);
            }
            catch (Exception e) when (e is ArenaSignInException or HttpRequestException or TaskCanceledException)
            {
                // The session ends on its own: it was signed in for this only.
            }
        }
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, new Uri(arena, path));
        // As Arena's own page: a request a browser would not send across sites.
        req.Headers.Add("X-Requested-With", "code-arena");
        req.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var res = await _http.SendAsync(req, timeout.Token);
        var text = await res.Content.ReadAsStringAsync(timeout.Token);
        JsonNode? said = null;
        try
        {
            said = text.Length > 0 ? JsonNode.Parse(text) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            // Not Arena's JSON: said below by the status.
        }
        if (res.IsSuccessStatusCode)
        {
            return said;
        }
        var error = said?["error"]?.GetValue<string>();
        throw new ArenaSignInException(res.StatusCode switch
        {
            HttpStatusCode.NotFound when path == "/api/auth/login" => $"{arena} is not an Arena that signs people in with a password: check the address.",
            HttpStatusCode.Forbidden when error is null => "Arena refused: sign in on its web page once (to accept its notice or set up two-factor sign-in), then here.",
            _ => error ?? $"Arena answered {(int)res.StatusCode} {res.ReasonPhrase}.",
        }, said?["status"]?.GetValue<string>());
    }

    public void Dispose() => _http.Dispose();
}
