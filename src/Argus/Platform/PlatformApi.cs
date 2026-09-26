using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.Access;
using Argus.Configuration;
using Argus.Server;
using Argus.Store;
using Argus.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace Argus.Platform;

/// <summary>
/// The application's own API under <c>/api</c>: signing in, a person's settings
/// and keys, conversations, and user administration. Everything else --
/// <c>/mcp</c>, <c>/admin</c>, the webhook -- is the code index's surface.
/// </summary>
public static class PlatformApi
{
    public const string SessionCookie = "argus_session";
    /// <summary>Required on every state-changing request made with a session cookie: a cross-site form cannot set it.</summary>
    public const string CsrfHeader = "X-Argus-Request";
    public const string UserItem = "platform.user";
    public const string ViaItem = "platform.via";

    static readonly JsonSerializerOptions JsonOut = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IResult Json(JsonNode? body, int status = 200) =>
        Results.Text(body?.ToJsonString(JsonOut) ?? "null", "application/json", System.Text.Encoding.UTF8, status);

    static IResult Error(string message, int status) => Json(new JsonObject { ["error"] = message }, status);

    public static AppUser? CurrentUser(HttpContext ctx) => ctx.Items[UserItem] as AppUser;

    static async Task<JsonObject> Body(HttpRequest request)
    {
        try { return await JsonNode.ParseAsync(request.Body) as JsonObject ?? []; }
        catch (JsonException) { return []; }
    }

    static string? Str(JsonObject body, string key) => body[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static bool? Bool(JsonObject body, string key) => body[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    static double? Money(JsonObject body, string key, out bool present)
    {
        present = body.ContainsKey(key);
        if (body[key] is JsonValue v && v.TryGetValue<double>(out var d))
        {
            if (d < 0 || double.IsNaN(d) || double.IsInfinity(d)) throw new AccountError("A budget is zero or more.");
            return d;
        }
        return null;
    }

    public static void AddServices(IServiceCollection services) => services.AddSingleton(new LoginThrottle());

    /// <summary>Who is calling, from the session cookie or a personal <c>ak_</c> key; recorded on the context for everything after.</summary>
    public static void Identify(HttpContext ctx, string appDbPath)
    {
        var cookie = ctx.Request.Cookies[SessionCookie];
        var bearer = ArgusServer.ExtractBearer(ctx.Request.Headers.Authorization.ToString());
        if (string.IsNullOrEmpty(cookie) && (bearer is null || !bearer.StartsWith(Users.ApiKeyPrefix, StringComparison.Ordinal))) return;
        using var conn = AppDb.Open(appDbPath);
        if (bearer is not null && bearer.StartsWith(Users.ApiKeyPrefix, StringComparison.Ordinal))
        {
            if (Users.FromApiKey(conn, bearer) is { } byKey) { ctx.Items[UserItem] = byKey; ctx.Items[ViaItem] = "key"; }
            return;
        }
        if (!string.IsNullOrEmpty(cookie) && Users.FromSession(conn, cookie) is { } bySession)
        {
            ctx.Items[UserItem] = bySession;
            ctx.Items[ViaItem] = "session";
        }
    }

    /// <summary>A browser session making a change must say so with <see cref="CsrfHeader"/>.</summary>
    public static bool CsrfOk(HttpContext ctx) =>
        ctx.Items[ViaItem] as string != "session" || HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)
        || ctx.Request.Headers[CsrfHeader].ToString() == "1";

    static void SetSessionCookie(HttpContext ctx, string token, TimeSpan lifetime) =>
        ctx.Response.Cookies.Append(SessionCookie, token, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps, Path = "/",
            MaxAge = lifetime, IsEssential = true,
        });

    static JsonObject Endpoints(HttpContext ctx)
    {
        var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        return new JsonObject
        {
            ["mcp"] = origin + "/mcp",
            ["gateway"] = Environment.GetEnvironmentVariable("ARGUS_PUBLIC_GATEWAY_URL") is { Length: > 0 } g ? g : null,
        };
    }

    public static void Map(WebApplication app, ArgusConfig cfg, string appDbPath, Gateway? gateway, ChatService chat)
    {
        var api = app.MapGroup("/api");

        // --- signing in --------------------------------------------------------------
        api.MapPost("/auth/login", async (HttpContext ctx, LoginThrottle throttle) =>
        {
            var body = await Body(ctx.Request);
            var name = Str(body, "username") ?? "";
            var password = Str(body, "password") ?? "";
            var address = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (throttle.RetryAfter(name, address) is { } wait)
            {
                ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Error($"Too many failed sign-ins. Try again in {Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes))} minute(s).", 429);
            }
            using var conn = AppDb.Open(appDbPath);
            var user = await Task.Run(() => Users.CheckPassword(conn, name, password));
            if (user is null)
            {
                throttle.Failed(name, address);
                return Error("That username and password do not match an active account.", 401);
            }
            throttle.Succeeded(name);
            SetSessionCookie(ctx, Users.StartSession(conn, user.Id, ctx.Request.Headers.UserAgent.ToString()), Users.SessionLifetime);
            return Json(new JsonObject { ["user"] = user.ToJson(), ["endpoints"] = Endpoints(ctx) });
        });

        api.MapPost("/auth/logout", (HttpContext ctx) =>
        {
            if (ctx.Request.Cookies[SessionCookie] is { Length: > 0 } token)
            {
                using var conn = AppDb.Open(appDbPath);
                Users.EndSession(conn, token);
            }
            ctx.Response.Cookies.Delete(SessionCookie, new CookieOptions { Path = "/" });
            return Json(new JsonObject { ["ok"] = true });
        });

        // --- the person themselves ---------------------------------------------------
        api.MapGet("/me", async (HttpContext ctx) =>
        {
            var user = CurrentUser(ctx)!;
            var result = new JsonObject { ["user"] = user.ToJson(), ["endpoints"] = Endpoints(ctx) };
            if (gateway is { CanAdminister: true })
            {
                try
                {
                    var (spend, budget, _) = await gateway.UserInfo(user.Email);
                    result["usage"] = new JsonObject { ["spend"] = spend, ["max_budget"] = budget };
                }
                catch (GatewayError exc) { result["usage_error"] = exc.Message; }
            }
            return Json(result);
        });

        api.MapPost("/me/password", async (HttpContext ctx) =>
        {
            var user = CurrentUser(ctx)!;
            var body = await Body(ctx.Request);
            using var conn = AppDb.Open(appDbPath);
            if (await Task.Run(() => Users.CheckPassword(conn, user.Username, Str(body, "current") ?? "")) is null)
                return Error("The current password is not right.", 403);
            Users.SetPassword(conn, user.Id, Str(body, "new") ?? "");
            SetSessionCookie(ctx, Users.StartSession(conn, user.Id, ctx.Request.Headers.UserAgent.ToString()), Users.SessionLifetime);
            return Json(new JsonObject { ["ok"] = true });
        });

        api.MapGet("/me/keys", (HttpContext ctx) =>
        {
            using var conn = AppDb.Open(appDbPath);
            return Json(new JsonArray(Users.ApiKeys(conn, CurrentUser(ctx)!.Id).Select(r => (JsonNode?)r.ToJson()).ToArray()));
        });

        api.MapPost("/me/keys", async (HttpContext ctx) =>
        {
            var body = await Body(ctx.Request);
            using var conn = AppDb.Open(appDbPath);
            var (key, id) = Users.CreateApiKey(conn, CurrentUser(ctx)!.Id, Str(body, "name") ?? "");
            return Json(new JsonObject { ["id"] = id, ["key"] = key }, 201);
        });

        api.MapDelete("/me/keys/{id:long}", (HttpContext ctx, long id) =>
        {
            using var conn = AppDb.Open(appDbPath);
            return Users.DeleteApiKey(conn, CurrentUser(ctx)!.Id, id) ? Json(new JsonObject { ["ok"] = true }) : Error("No such key.", 404);
        });

        // Model keys live in the gateway: an OpenAI-compatible key for editors and scripts.
        api.MapGet("/me/model-keys", async (HttpContext ctx) =>
        {
            if (gateway is not { CanAdminister: true }) return Error("No model gateway is configured.", 503);
            var user = CurrentUser(ctx)!;
            var (_, _, keys) = await gateway.UserInfo(user.Email);
            var list = new JsonArray();
            foreach (var k in keys.OfType<JsonObject>())
            {
                var alias = k["key_alias"]?.ToString() ?? "";
                if (alias.StartsWith("chat:", StringComparison.Ordinal)) continue;
                list.Add(new JsonObject
                {
                    ["token"] = k["token"]?.ToString(), ["alias"] = alias, ["masked"] = k["key_name"]?.ToString(),
                    ["spend"] = k["spend"]?.DeepClone(), ["created_at"] = k["created_at"]?.DeepClone(),
                });
            }
            return Json(list);
        });

        api.MapPost("/me/model-keys", async (HttpContext ctx) =>
        {
            if (gateway is not { CanAdminister: true }) return Error("No model gateway is configured.", 503);
            var user = CurrentUser(ctx)!;
            var body = await Body(ctx.Request);
            var alias = (Str(body, "name") ?? "").Trim();
            if (alias.Length is 0 or > 60 || alias.StartsWith("chat:", StringComparison.Ordinal))
                return Error("Give the key a name of up to 60 characters.", 400);
            await gateway.EnsureUser(user.Email);
            var (key, token) = await gateway.CreateKey(user.Email, alias);
            return Json(new JsonObject { ["key"] = key, ["token"] = token }, 201);
        });

        api.MapDelete("/me/model-keys/{token}", async (HttpContext ctx, string token) =>
        {
            if (gateway is not { CanAdminister: true }) return Error("No model gateway is configured.", 503);
            var user = CurrentUser(ctx)!;
            var (_, _, keys) = await gateway.UserInfo(user.Email);
            var owned = keys.OfType<JsonObject>().Any(k => k["token"]?.ToString() == token
                                                           && !(k["key_alias"]?.ToString() ?? "").StartsWith("chat:", StringComparison.Ordinal));
            if (!owned) return Error("No such key.", 404);
            await gateway.DeleteKeys([token]);
            return Json(new JsonObject { ["ok"] = true });
        });

        // --- chat ---------------------------------------------------------------------
        api.MapGet("/models", async (HttpContext ctx) =>
        {
            if (gateway is null) return Json(new JsonArray());
            using var conn = AppDb.Open(appDbPath);
            var models = await gateway.Models(await chat.KeyFor(conn, CurrentUser(ctx)!));
            return Json(new JsonArray(models.Select(m => (JsonNode?)m).ToArray()));
        });

        api.MapGet("/conversations", (HttpContext ctx) =>
        {
            using var conn = AppDb.Open(appDbPath);
            return Json(new JsonArray(Conversations.List(conn, CurrentUser(ctx)!.Id).Select(r => (JsonNode?)r.ToJson()).ToArray()));
        });

        api.MapPost("/conversations", async (HttpContext ctx) =>
        {
            var body = await Body(ctx.Request);
            using var conn = AppDb.Open(appDbPath);
            return Json(Conversations.Create(conn, CurrentUser(ctx)!.Id, Str(body, "title"), Str(body, "model")).ToJson(), 201);
        });

        api.MapGet("/conversations/{id}", (HttpContext ctx, string id) =>
        {
            using var conn = AppDb.Open(appDbPath);
            if (Conversations.Get(conn, CurrentUser(ctx)!.Id, id) is not { } c) return Error("No such conversation.", 404);
            var o = c.ToJson();
            o["messages"] = new JsonArray(Conversations.Messages(conn, id).Select(m => (JsonNode?)Conversations.MessageJson(m)).ToArray());
            return Json(o);
        });

        api.MapPatch("/conversations/{id}", async (HttpContext ctx, string id) =>
        {
            var body = await Body(ctx.Request);
            var title = Str(body, "title");
            if (string.IsNullOrWhiteSpace(title)) return Error("A title cannot be empty.", 400);
            using var conn = AppDb.Open(appDbPath);
            return Conversations.Rename(conn, CurrentUser(ctx)!.Id, id, title) ? Json(new JsonObject { ["ok"] = true }) : Error("No such conversation.", 404);
        });

        api.MapDelete("/conversations/{id}", (HttpContext ctx, string id) =>
        {
            using var conn = AppDb.Open(appDbPath);
            return Conversations.Delete(conn, CurrentUser(ctx)!.Id, id) ? Json(new JsonObject { ["ok"] = true }) : Error("No such conversation.", 404);
        });

        api.MapPost("/conversations/{id}/messages", async (HttpContext ctx, string id) =>
        {
            var body = await Body(ctx.Request);
            await chat.Stream(ctx, CurrentUser(ctx)!, id, Str(body, "content") ?? "", Str(body, "model"), Bool(body, "tools") ?? true);
        });

        // --- administration -----------------------------------------------------------
        var admin = api.MapGroup("/admin").AddEndpointFilter(async (context, next) =>
            CurrentUser(context.HttpContext) is { IsAdmin: true } ? await next(context) : Error("Administrators only.", 403));

        admin.MapGet("/overview", async () =>
        {
            var services = new JsonArray();
            async Task Probe(string name, string? url)
            {
                if (string.IsNullOrEmpty(url)) return;
                var sw = Stopwatch.StartNew();
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                    using var resp = await client.GetAsync(url);
                    services.Add(new JsonObject { ["name"] = name, ["ok"] = resp.IsSuccessStatusCode, ["detail"] = $"HTTP {(int)resp.StatusCode}", ["ms"] = sw.ElapsedMilliseconds });
                }
                catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or UriFormatException)
                {
                    services.Add(new JsonObject { ["name"] = name, ["ok"] = false, ["detail"] = exc.GetType().Name, ["ms"] = sw.ElapsedMilliseconds });
                }
            }
            if (gateway is not null)
            {
                var (ok, detail, ms) = await gateway.Health();
                services.Add(new JsonObject { ["name"] = "Model gateway (LiteLLM)", ["ok"] = ok, ["detail"] = detail, ["ms"] = ms });
            }
            await Probe("Chat model (llama.cpp)", Environment.GetEnvironmentVariable("ARGUS_LLAMACPP_HEALTH_URL"));
            await Probe("Embeddings (llama.cpp)", Indexing.Embed.BaseUrl + "/health");

            using var conn = AppDb.Open(appDbPath);
            var users = Users.List(conn);
            var result = new JsonObject
            {
                ["services"] = services,
                ["users"] = new JsonObject
                {
                    ["total"] = users.Count, ["admins"] = users.Count(u => u.IsAdmin && !u.Disabled), ["disabled"] = users.Count(u => u.Disabled),
                },
            };
            if (gateway is { CanAdminister: true })
            {
                try
                {
                    var all = await gateway.AllUsers();
                    result["spend"] = users.Sum(u => all.TryGetValue(u.Email, out var s) ? s.Spend : 0);
                }
                catch (GatewayError exc) { result["spend_error"] = exc.Message; }
            }
            return Json(result);
        });

        admin.MapGet("/users", async () =>
        {
            using var conn = AppDb.Open(appDbPath);
            var users = Users.List(conn);
            Dictionary<string, (double Spend, double? MaxBudget)>? usage = null;
            string? usageError = null;
            if (gateway is { CanAdminister: true })
            {
                try { usage = await gateway.AllUsers(); }
                catch (GatewayError exc) { usageError = exc.Message; }
            }
            var list = new JsonArray();
            foreach (var u in users)
            {
                var o = u.ToJson();
                if (usage is not null && usage.TryGetValue(u.Email, out var s)) { o["spend"] = s.Spend; o["max_budget"] = s.MaxBudget; }
                list.Add(o);
            }
            return Json(new JsonObject { ["users"] = list, ["usage_error"] = usageError });
        });

        admin.MapPost("/users", async (HttpContext ctx) =>
        {
            var body = await Body(ctx.Request);
            var password = Str(body, "password");
            var generated = string.IsNullOrEmpty(password);
            if (generated) password = Passwords.Generate();
            var budget = Money(body, "max_budget", out _);
            using var conn = AppDb.Open(appDbPath);
            var user = Users.Create(conn, Str(body, "username") ?? "", Str(body, "email") ?? "", password!, Str(body, "role") ?? "user",
                Str(body, "display_name") ?? "", Str(body, "gitlab_username"));
            var result = new JsonObject { ["user"] = user.ToJson() };
            if (generated) result["password"] = password;
            if (gateway is { CanAdminister: true })
            {
                try { await gateway.EnsureUser(user.Email, budget); }
                catch (GatewayError exc) { result["warning"] = "The account exists, but the gateway did not record it yet: " + exc.Message; }
            }
            return Json(result, 201);
        });

        admin.MapPatch("/users/{id:long}", async (HttpContext ctx, long id) =>
        {
            var body = await Body(ctx.Request);
            var budget = Money(body, "max_budget", out var budgetPresent);
            var me = CurrentUser(ctx)!;
            if (id == me.Id && (Bool(body, "disabled") == true || (Str(body, "role") is { } r && r != "admin")))
                return Error("You cannot disable or demote yourself; ask another administrator.", 409);
            using var conn = AppDb.Open(appDbPath);
            var user = Users.Update(conn, id, Str(body, "display_name"), Str(body, "role"), Bool(body, "disabled"),
                Str(body, "gitlab_username"), clearGitlab: body.ContainsKey("gitlab_username") && body["gitlab_username"] is null);
            var result = new JsonObject { ["user"] = user.ToJson() };
            if (budgetPresent && gateway is { CanAdminister: true })
            {
                await gateway.EnsureUser(user.Email);
                await gateway.SetBudget(user.Email, budget);
            }
            return Json(result);
        });

        admin.MapPost("/users/{id:long}/reset-password", (long id) =>
        {
            using var conn = AppDb.Open(appDbPath);
            if (Users.Get(conn, id) is null) return Error("No such person.", 404);
            var password = Passwords.Generate();
            Users.SetPassword(conn, id, password);
            return Json(new JsonObject { ["password"] = password });
        });

        admin.MapDelete("/users/{id:long}", async (HttpContext ctx, long id) =>
        {
            if (id == CurrentUser(ctx)!.Id) return Error("You cannot delete yourself; ask another administrator.", 409);
            using var conn = AppDb.Open(appDbPath);
            var user = Users.Get(conn, id);
            if (user is null) return Error("No such person.", 404);
            Users.Delete(conn, id);
            var result = new JsonObject { ["ok"] = true };
            if (gateway is { CanAdminister: true })
            {
                try { await gateway.DeleteUser(user.Email); }
                catch (GatewayError exc) { result["warning"] = "Deleted here; the gateway still has their record: " + exc.Message; }
            }
            return Json(result);
        });
    }

    /// <summary>Turn the account layer's refusals into answers rather than 500s.</summary>
    public static async Task Errors(HttpContext ctx, Func<Task> next)
    {
        try { await next(); }
        catch (AccountError exc) when (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = exc.Status;
            await ctx.Response.WriteAsJsonAsync(new { error = exc.Message });
        }
        catch (GatewayError exc) when (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = exc.Status;
            await ctx.Response.WriteAsJsonAsync(new { error = exc.Message });
        }
    }

    /// <summary>The React app: hashed assets cached for a year, the shell never, anything else that is not an API route gets the shell.</summary>
    public static void MapWeb(WebApplication app, string? webRoot)
    {
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "same-origin";
            h["X-Frame-Options"] = "DENY";
            await next();
        });
        if (string.IsNullOrEmpty(webRoot) || !File.Exists(Path.Combine(webRoot, "index.html"))) return;
        var files = new PhysicalFileProvider(Path.GetFullPath(webRoot));
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = c =>
            {
                c.Context.Response.Headers.CacheControl = c.Context.Request.Path.StartsWithSegments("/assets")
                    ? "public, max-age=31536000, immutable" : "no-cache";
            },
        });
        var shell = Path.Combine(Path.GetFullPath(webRoot), "index.html");
        app.MapFallback(async (HttpContext ctx) =>
        {
            var path = ctx.Request.Path;
            if (path.StartsWithSegments("/api") || path.StartsWithSegments("/admin") || path.StartsWithSegments("/mcp")
                || path.StartsWithSegments("/hook") || !HttpMethods.IsGet(ctx.Request.Method))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.SendFileAsync(shell);
        });
    }
}
