using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// The IDE's Accounts menu: whose key Code Arena works with, signing in with a user name and password (Arena's sign-in, its
/// second factor too; a key of the person's own made for this machine), and signing out (that key revoked, and forgotten
/// here). Not in Code Studio, whose workspace works with Arena's key for it.
/// </summary>
internal sealed partial class WebApp
{
    // A sign-in waiting for its second factor: Arena's half-signed-in session, kept for 5 minutes.
    private (ArenaSignIn SignIn, string Url, DateTimeOffset Until)? _signingIn;

    /// <summary>The account calls; false when the path is none of them.</summary>
    private async Task<bool> AccountAsync(HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        switch (req.Method, req.Path)
        {
            case ("GET", "/api/account"):
                await res.JsonAsync(200, await AccountJsonAsync(ct), ct);
                return true;
            case ("POST", "/api/account/sign-in"):
            {
                if (!FoldersOpen)
                {
                    throw new IdeError(409, "not_here", "This workspace is Code Studio's: it works with Arena's key for it.");
                }
                var body = Body(req);
                var url = Config.NormalizeUrl(body.Str("url") is { Length: > 0 } given ? given : _rt.Config.Url ?? throw new IdeError(400, "url", "Give your Arena's address."));
                if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var arena) || arena.Scheme is not ("https" or "http"))
                {
                    throw new IdeError(400, "url", $"{url} is not a web address (https://DOMAIN).");
                }
                if (body.Str("userName") is not { Length: > 0 } userName || body.Str("password") is not { Length: > 0 } password)
                {
                    throw new IdeError(400, "invalid", "Give your user name and password.");
                }
                ForgetSignIn();
                var signIn = new ArenaSignIn(arena, Net.CaFile(null, _rt.Config.Ca, _rt.Env.Env));
                try
                {
                    if (await signIn.PasswordAsync(userName, password, ct))
                    {
                        _signingIn = (signIn, url, DateTimeOffset.UtcNow.AddMinutes(5));
                        await res.JsonAsync(200, new JsonObject { ["status"] = "2fa" }, ct);
                        return true;
                    }
                    await SignedInAsync(signIn, url, res, ct);
                }
                catch (Exception e) when (e is ArenaSignInException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    signIn.Dispose();
                    throw new IdeError(e is ArenaSignInException { Code: "locked" or "banned" } ? 429 : 401, "sign_in", e is TaskCanceledException ? $"{url} did not answer in time." : e.Message);
                }
                return true;
            }
            case ("POST", "/api/account/sign-in/code"):
            {
                if (_signingIn is not { } pending || pending.Until < DateTimeOffset.UtcNow)
                {
                    ForgetSignIn();
                    throw new IdeError(409, "expired", "The sign-in waited too long for its code: sign in again.");
                }
                try
                {
                    await pending.SignIn.CodeAsync(Body(req).Str("code")?.Trim() ?? "", ct);
                }
                catch (Exception e) when (e is ArenaSignInException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    // A wrong code may be typed again, within the 5 minutes.
                    throw new IdeError(401, "sign_in", e.Message);
                }
                _signingIn = null;
                try
                {
                    await SignedInAsync(pending.SignIn, pending.Url, res, ct);
                }
                catch (Exception e) when (e is ArenaSignInException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    pending.SignIn.Dispose();
                    throw new IdeError(401, "sign_in", e.Message);
                }
                return true;
            }
            case ("POST", "/api/account/feedback"):
            {
                // Send feedback: to Arena with the person's key, as from its own pages (Admin → Feedback reads it).
                if (_rt.Config.Url is not { Length: > 0 } url || _rt.Config.ApiKey is not { Length: > 0 } key)
                {
                    throw new IdeError(409, "signed_out", "Sign in to Arena first: feedback goes there.");
                }
                var body = Body(req);
                using var send = ChatSync.Request(HttpMethod.Post, url.TrimEnd('/') + "/api/code-arena/feedback", key,
                    new JsonObject { ["kind"] = body.Str("kind"), ["text"] = body.Str("text"), ["page"] = body.Str("page") });
                using var answer = await _rt.Http.SendAsync(send, ct);
                var said = await answer.Content.ReadAsStringAsync(ct);
                if (!answer.IsSuccessStatusCode)
                {
                    throw new IdeError((int)answer.StatusCode, "arena", Json.ParseObject(said)?.Str("error") ?? $"Arena answered {(int)answer.StatusCode}.");
                }
                await res.JsonAsync(200, Json.ParseObject(said) ?? new JsonObject(), ct);
                return true;
            }
            case ("POST", "/api/account/sign-out"):
            {
                if (!FoldersOpen)
                {
                    throw new IdeError(409, "not_here", "This workspace is Code Studio's: it works with Arena's key for it.");
                }
                var revoked = false;
                if (_rt.Config.Url is { Length: > 0 } url && _rt.Config.ApiKey is { Length: > 0 } key)
                {
                    try
                    {
                        using var out_ = ChatSync.Request(HttpMethod.Post, url.TrimEnd('/') + "/api/code-arena/sign-out", key, new JsonObject());
                        using var answer = await _rt.Http.SendAsync(out_, ct);
                        revoked = answer.IsSuccessStatusCode;
                    }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                    {
                        // Arena not there: the key is forgotten here all the same (Account → API keys revokes it).
                    }
                }
                _rt.Config.ApiKey = null;
                _rt.Config.Save(_rt.Env.Paths.ConfigFile);
                await res.JsonAsync(200, new JsonObject { ["signedOut"] = true, ["revoked"] = revoked }, ct);
                return true;
            }
        }
        return false;
    }

    /// <summary>Signed in: the machine's key kept in config.json (with Arena's address), and Code Arena started again with it.</summary>
    private async Task SignedInAsync(ArenaSignIn signIn, string url, HttpResponse res, CancellationToken ct)
    {
        string key;
        using (signIn)
        {
            key = await signIn.KeyAsync(ct);
        }
        if (!string.Equals(url, _rt.Config.Url, StringComparison.OrdinalIgnoreCase))
        {
            // Another Arena: its own gateway (the default for its address) and the system's trust.
            (_rt.Config.Url, _rt.Config.Gateway) = (url, null);
        }
        _rt.Config.ApiKey = key;
        _rt.Config.Save(_rt.Env.Paths.ConfigFile);
        await res.JsonAsync(200, new JsonObject { ["status"] = "ok", ["key"] = ArenaSignIn.KeyName }, ct);
        // Answered first: Code Arena then starts again here with the new key, as Open Folder does.
        _folderOpened.TrySetResult(_rt.Workspace.Root);
    }

    private void ForgetSignIn()
    {
        _signingIn?.SignIn.Dispose();
        _signingIn = null;
    }

    /// <summary>Whose key this is, as Arena says (null when Arena cannot say), and whether the person may see Admin → People.</summary>
    private async Task<JsonObject> AccountJsonAsync(CancellationToken ct)
    {
        var json = new JsonObject { ["arena"] = _rt.Config.Url, ["signedIn"] = _rt.Config.ApiKey is { Length: > 0 }, ["codeStudio"] = !FoldersOpen, ["account"] = null };
        if (_rt.Config.Url is not { Length: > 0 } url || _rt.Config.ApiKey is not { Length: > 0 } key)
        {
            return json;
        }
        try
        {
            using var req = ChatSync.Request(HttpMethod.Get, url.TrimEnd('/') + "/api/code-arena/me", key, null);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            using var answer = await _rt.Http.SendAsync(req, limit.Token);
            json["account"] = answer.IsSuccessStatusCode ? Json.ParseObject(await answer.Content.ReadAsStringAsync(limit.Token)) : null;
            if (!answer.IsSuccessStatusCode)
            {
                json["error"] = $"Arena answered {(int)answer.StatusCode}: the key may be revoked. Sign in again.";
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            json["error"] = $"{url} did not answer.";
        }
        return json;
    }
}
