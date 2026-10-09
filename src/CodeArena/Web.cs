using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace CodeArena;

/// <summary>
/// The web interface's page as src/web builds it (vite.code-arena.config.ts into
/// src/CodeArena/web), embedded in the program. Empty when it was not built.
/// </summary>
internal sealed class WebAssets
{
    /// <summary>The page itself; the rest are its scripts, styles, fonts and the diagram runner.</summary>
    public const string Page = "code-arena.html";
    private readonly Dictionary<string, Lazy<byte[]>> _files;

    public WebAssets(IEnumerable<KeyValuePair<string, Func<byte[]>>> files) =>
        _files = files.ToDictionary(f => f.Key, f => new Lazy<byte[]>(f.Value), StringComparer.Ordinal);

    public bool Built => _files.ContainsKey(Page);

    /// <summary>What the build put in the program (resources named web/...).</summary>
    public static WebAssets Embedded()
    {
        var assembly = typeof(WebAssets).Assembly;
        return new WebAssets(assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("web/", StringComparison.Ordinal) || n.StartsWith("web\\", StringComparison.Ordinal))
            .Select(n => new KeyValuePair<string, Func<byte[]>>(n[4..].Replace('\\', '/'), () =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                return copy.ToArray();
            })));
    }

    /// <summary>Files given as text: for tests.</summary>
    public static WebAssets Of(params (string Path, string Text)[] files) =>
        new(files.Select(f => new KeyValuePair<string, Func<byte[]>>(f.Path, () => Encoding.UTF8.GetBytes(f.Text))));

    public byte[]? Read(string path) => _files.TryGetValue(path, out var file) ? file.Value : null;

    public static string TypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" or ".map" or ".webmanifest" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ttf" => "font/ttf",
        ".wasm" => "application/wasm",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };
}

/// <summary>
/// The IDE (code-arena, code-arena web): this folder's agent, files and
/// terminals, driven from a page in the browser. The same Runtime and agent
/// loop as the terminal; the page hears each turn as server-sent events (in the
/// shape Arena's chat streams them) and posts messages, approvals, Stop, and
/// mode and model changes; the editor, search and terminals have their own calls
/// (WebIde.cs). Only this machine can reach it (127.0.0.1), only with this run's
/// token (in the address once, then a cookie), and only from its own page: Host
/// and Origin are checked, so another site, or a name made to point at
/// 127.0.0.1, cannot drive the agent, read the files or type in a terminal.
/// </summary>
internal sealed partial class WebApp : IAgentEvents, IAsyncDisposable
{
    /// <summary>A tool's text sent to the page at most; the model gets its own cut.</summary>
    private const int MaxResultChars = 60_000;

    // The editor's and the language services' workers are files of the page's own (worker-src falls back to script-src); the terminals' WebSockets are named, as not every browser counts ws: as 'self'.
    private string PageCsp => $"default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; font-src 'self'; connect-src 'self' ws://127.0.0.1:{Port} ws://localhost:{Port}; frame-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";
    // The diagram runner (as the web's nginx serves it): an origin of its own, no network, framed by the page only.
    private const string PreviewCsp = "sandbox allow-scripts allow-forms allow-modals; default-src 'none'; script-src 'self' 'unsafe-inline' 'unsafe-eval' blob:; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; media-src data: blob:; connect-src 'none'; frame-src 'none'; worker-src 'none'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'";

    private readonly Runtime _rt;
    private readonly WebAssets _assets;
    private readonly HttpServer _server;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Approval>> _waiting = new();
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Job? _job;
    // The edits made in this run, by result message, with the whole file's lines: the session file keeps only old and new text.
    private readonly Dictionary<string, JsonObject> _diffs = [];
    private string? _diffsOf;
    // The answer being written: its message id, and its thinking's start and length.
    private string? _answerId;
    private long _stepStarted;
    private long _thinkingStarted;
    private int? _thinkingMs;
    // How full the window was when no turn ran: the history is not read while a turn changes it.
    private long _lastUsed;
    // The commands with no time limit: their output to the page as it comes, a few times a second, for as long as they run.
    private readonly Dictionary<int, (StringBuilder Pending, bool Flushing)> _jobOutput = [];

    private WebApp(Runtime rt, WebAssets assets, HttpServer server)
    {
        _rt = rt;
        _assets = assets;
        _server = server;
        StartIde();
    }

    /// <summary>This run's key to the page: 256 random bits.</summary>
    public string Token { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public IPEndPoint Endpoint => _server.Endpoint;
    public int Port => _server.Endpoint.Port;
    /// <summary>The address to open: the token in it once; the page then lives on a cookie.</summary>
    public string Address => $"http://127.0.0.1:{Port}/?token={Token}";
    /// <summary>Done once a browser has the page, the key's cookie with it.</summary>
    public Task Opened => _opened.Task;
    /// <summary>Per port: two runs side by side keep their own.</summary>
    private string CookieName => $"code_arena_{Port}";

    /// <summary>Listens on 127.0.0.1 at the port (0: a free one) and takes over the runtime's events and questions.</summary>
    public static WebApp Start(Runtime rt, WebAssets assets, int port)
    {
        var app = new WebApp(rt, assets, new HttpServer(port));
        rt.Agent.Events = app;
        rt.Permissions.Asker = app.AskAsync;
        rt.Jobs.Started += app.JobStarted;
        rt.Jobs.Output += app.JobOutput;
        rt.Jobs.Ended += app.JobEnded;
        app._server.OnError = e => rt.Ui.Error($"The web interface: {e.Message}");
        app._server.Start(app.HandleAsync);
        return app;
    }

    // ---------------------------------------------------------------- requests

    private async Task HandleAsync(HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        res.Headers["X-Content-Type-Options"] = "nosniff";
        res.Headers["Referrer-Policy"] = "no-referrer";
        res.Headers["Cache-Control"] = "no-store";
        // A name that points at 127.0.0.1 (DNS rebinding) still says its own name here.
        if (!LocalName(req.Header("Host"), "", Port))
        {
            res.KeepAlive = false;
            await res.TextAsync(403, "This server answers only to http://127.0.0.1 on this machine.", ct);
            return;
        }
        // The diagram runner and its files load from an opaque origin, without cookies: public code, no data.
        if (req.Path == "/preview.html" || req.Path.StartsWith("/preview/assets/", StringComparison.Ordinal))
        {
            await PreviewAsync(req, res, ct);
            return;
        }
        if (req.Header("Origin") is { } origin && !LocalName(origin, "http://", Port))
        {
            await res.ErrorAsync(403, "forbidden", "Requests from other sites are refused.", ct);
            return;
        }
        if (req.Path == "/" && req.Query.TryGetValue("token", out var given))
        {
            if (!TokenIs(given))
            {
                await NoEntryAsync(res, "This link is not the one this run of code-arena printed: it makes a new key each time it starts.", ct);
                return;
            }
            // The key moves to a cookie, and out of the address bar: by a page that moves itself to /, not a redirect.
            // A browser opened with the launcher file comes from another site (file://): on a redirect from there it
            // leaves a SameSite=Strict cookie out, as on this request; from a page of this server's own, it sends it.
            res.Headers["Set-Cookie"] = $"{CookieName}={Token}; Path=/; HttpOnly; SameSite=Strict";
            await HandOffAsync(res, ct);
            return;
        }
        var bearer = req.Header("Authorization") is { } auth && auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..].Trim() : null;
        var api = req.Path.StartsWith("/api/", StringComparison.Ordinal);
        var cookie = TokenIs(req.Cookie(CookieName));
        if (!cookie && !TokenIs(bearer))
        {
            if (api)
            {
                await res.ErrorAsync(401, "unauthorized", "Open the address code-arena printed in your terminal: it carries this run's key.", ct);
            }
            else
            {
                await NoEntryAsync(res, "Open the address code-arena printed in your terminal: it carries this run's key.", ct);
            }
            return;
        }
        if (api)
        {
            if (req.Header("Sec-Fetch-Site") is { } site && site is not ("same-origin" or "none"))
            {
                await res.ErrorAsync(403, "forbidden", "Requests from other sites are refused.", ct);
                return;
            }
            // JSON only: a form or a plain-text post from another page cannot pass for the page's own.
            if (req.Method is not ("GET" or "HEAD") && !(req.Header("Content-Type") ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                await res.ErrorAsync(415, "unsupported", "Send JSON (Content-Type: application/json).", ct);
                return;
            }
            await ApiAsync(req, res, ct);
            return;
        }
        if (cookie && req.Path == "/" && req.Method == "GET")
        {
            // A browser has the page, the cookie having come through: the key in the address was not all it took.
            _opened.TrySetResult();
        }
        await StaticAsync(req, res, ct);
    }

    /// <summary>127.0.0.1 or localhost at this port, as a Host header ("") or an Origin ("http://").</summary>
    private static bool LocalName(string? value, string scheme, int port)
    {
        if (value is null)
        {
            return false;
        }
        foreach (var name in new[] { "127.0.0.1", "localhost" })
        {
            if (value.Equals($"{scheme}{name}:{port}", StringComparison.OrdinalIgnoreCase) || (port == 80 && value.Equals(scheme + name, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    private bool TokenIs(string? value) =>
        value is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(Token));

    private static Task NoEntryAsync(HttpResponse res, string why, CancellationToken ct)
    {
        res.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'";
        var html = $"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="color-scheme" content="light dark"><title>Code Arena</title></head>
            <body style="font: 15px/1.5 system-ui, sans-serif; max-width: 36rem; margin: 15vh auto; padding: 0 1rem">
            <h1 style="font-size: 1.25rem">Code Arena</h1><p>{WebUtility.HtmlEncode(why)}</p></body></html>
            """;
        return res.TextAsync(401, html, ct, "text/html; charset=utf-8");
    }

    /// <summary>The answer to the address with the key: a page that goes on to / at once (replacing itself in the tab's history), the cookie set.</summary>
    private static Task HandOffAsync(HttpResponse res, CancellationToken ct)
    {
        res.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        res.Headers["X-Frame-Options"] = "DENY";
        const string html = """
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta http-equiv="refresh" content="0;url=/"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="color-scheme" content="light dark"><title>Code Arena</title></head>
            <body style="font: 15px/1.5 system-ui, sans-serif; max-width: 36rem; margin: 15vh auto; padding: 0 1rem"><p>Opening Code Arena. If it does not open, <a href="/">open it here</a>.</p></body></html>
            """;
        return res.TextAsync(200, html, ct, "text/html; charset=utf-8");
    }

    private async Task StaticAsync(HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        if (req.Method != "GET")
        {
            await res.TextAsync(405, "Only GET here.", ct);
            return;
        }
        var name = req.Path == "/" ? WebAssets.Page : req.Path.TrimStart('/');
        if (_assets.Read(name) is not { } bytes)
        {
            await res.TextAsync(404, "Not found.", ct);
            return;
        }
        if (name == WebAssets.Page)
        {
            res.Headers["Cache-Control"] = "no-cache";
            res.Headers["Content-Security-Policy"] = PageCsp;
            res.Headers["X-Frame-Options"] = "DENY";
            res.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
            res.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        }
        else if (name.StartsWith("assets/", StringComparison.Ordinal))
        {
            // Fingerprinted by Vite: the same name is the same file.
            res.Headers["Cache-Control"] = "private, max-age=31536000, immutable";
        }
        else
        {
            res.Headers["Cache-Control"] = "no-cache";
        }
        await res.SendAsync(200, bytes, WebAssets.TypeOf(name), ct);
    }

    private async Task PreviewAsync(HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        var name = req.Path.TrimStart('/');
        if (req.Method != "GET" || _assets.Read(name) is not { } bytes)
        {
            await res.TextAsync(404, "Not found.", ct);
            return;
        }
        if (name == "preview.html")
        {
            res.Headers["Cache-Control"] = "no-cache";
            res.Headers["Content-Security-Policy"] = PreviewCsp;
            res.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), clipboard-read=()";
        }
        else
        {
            res.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
            res.Headers["Access-Control-Allow-Origin"] = "*";
        }
        await res.SendAsync(200, bytes, WebAssets.TypeOf(name), ct);
    }

    // --------------------------------------------------------------------- API

    private async Task ApiAsync(HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        switch (req.Method, req.Path)
        {
            case ("GET", "/api/state"):
                await res.JsonAsync(200, State(), ct);
                return;
            case ("GET", "/api/chat/config"):
                await res.JsonAsync(200, ChatConfig(), ct);
                return;
            case ("GET", "/api/sessions"):
                await res.JsonAsync(200, Sessions(), ct);
                return;
            case ("GET", "/api/session"):
                await res.JsonAsync(200, SessionJson(), ct);
                return;
            case ("GET", "/api/history"):
                await res.JsonAsync(200, Sent(), ct);
                return;
            case ("GET", "/api/turn"):
                if (Current() is { } running)
                {
                    await StreamAsync(running, res, ct);
                }
                else
                {
                    await res.NoContentAsync(ct);
                }
                return;
            case ("POST", "/api/messages"):
                await MessageAsync(req, res, ct);
                return;
            case ("POST", "/api/approvals"):
                await ApproveAsync(req, res, ct);
                return;
            case ("POST", "/api/stop"):
                if (Current() is { } job)
                {
                    await job.Stop.CancelAsync();
                    await res.NoContentAsync(ct);
                }
                else
                {
                    await res.ErrorAsync(409, "idle", "Nothing is running.", ct);
                }
                return;
            case ("POST", "/api/compact"):
                await CompactAsync(res, ct);
                return;
            case ("POST", "/api/settings"):
                await SettingsAsync(req, res, ct);
                return;
            case ("POST", "/api/servers/retry"):
                var server = req.Json()?.Str("name");
                var links = _rt.Links.Where(l => server is null ? l.State != LinkState.Connected : l.Name == server).ToList();
                if (server is not null && links.Count == 0)
                {
                    await res.ErrorAsync(404, "not_found", $"There is no server {server}.", ct);
                    return;
                }
                foreach (var link in links)
                {
                    link.Retry();
                }
                await res.JsonAsync(200, State(), ct);
                return;
            case ("POST", "/api/jobs/stop"):
                if (_rt.Jobs.Find(req.Json()?.Int("id") ?? 0) is not { } stopping)
                {
                    await res.ErrorAsync(404, "not_found", "There is no such job.", ct);
                    return;
                }
                stopping.Stop("by the person, in the IDE");
                await res.NoContentAsync(ct);
                return;
            case ("POST", "/api/sessions/new"):
                await SwitchAsync(res, null, ct);
                return;
            case ("POST", "/api/sessions/resume"):
                var id = req.Json()?.Str("id") ?? "";
                if (!SessionId().IsMatch(id) || SessionStore.Find(_rt.Env.Paths.SessionsDir, id) is not { } file)
                {
                    await res.ErrorAsync(404, "not_found", $"There is no saved session {id}.", ct);
                    return;
                }
                await SwitchAsync(res, file, ct);
                return;
        }
        if (await IdeAsync(req, res, ct))
        {
            return;
        }
        await res.ErrorAsync(404, "not_found", "There is no such call.", ct);
    }

    [GeneratedRegex("^[0-9A-Za-z-]{1,64}$")]
    private static partial Regex SessionId();

    private Job? Current()
    {
        lock (_gate)
        {
            return _job;
        }
    }

    private JsonObject State()
    {
        var git = SystemPrompt.GitRoot(_rt.Workspace.Root);
        var root = _rt.Workspace.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return new JsonObject
        {
            // The about box: the version, its licence and its source (LICENSING.md).
            ["name"] = "Code Arena",
            ["version"] = Cli.Version,
            ["license"] = Cli.License,
            ["source"] = Cli.Source,
            // The help's "Read more": the manual of the Arena signed in to.
            ["manual"] = _rt.Config.ManualUrl,
            ["folder"] = _rt.Workspace.Root,
            ["project"] = Path.GetFileName(root) is { Length: > 0 } project ? project : root,
            ["branch"] = git is null ? null : SystemPrompt.GitBranch(git),
            ["model"] = _rt.Model.Name,
            ["context"] = _rt.Model.Context,
            // How full the window is (about), and when the session compacts itself: the status bar and the settings show them.
            ["contextUsed"] = Current() is null ? _lastUsed = _rt.Agent.Estimate() : _lastUsed,
            ["compactAt"] = _rt.Compaction.At,
            ["compactTarget"] = _rt.Compaction.Target,
            ["servers"] = new JsonArray([.. _rt.Links.Select(l => (JsonNode)new JsonObject
            {
                ["name"] = l.Name,
                ["title"] = l.Title,
                ["url"] = l.Url,
                ["state"] = l.State.ToString().ToLowerInvariant(),
                ["tools"] = _rt.Tools.All.Count(t => t.Server == l.Name),
                ["status"] = _rt.Describe(l),
                ["error"] = l.State == LinkState.Connected ? null : l.Error,
                ["nextTry"] = l.NextTry is { } next ? new DateTimeOffset(next, TimeSpan.Zero).ToString("o") : null,
            })]),
            ["jobs"] = new JsonArray([.. _rt.Jobs.All.Select(j => (JsonNode)new JsonObject
            {
                ["id"] = j.Id, ["command"] = j.Command, ["running"] = j.Running, ["status"] = j.Status(),
            })]),
            ["thinking"] = _rt.Model.Thinking,
            ["mode"] = _rt.Permissions.Mode.Name(),
            ["modes"] = new JsonArray([.. Modes.Names.Select(n => (JsonNode)new JsonObject { ["name"] = n, ["description"] = Modes.Parse(n)!.Value.Describe() })]),
            ["session"] = _rt.Session.Id,
            ["busy"] = Current() is not null,
            ["arenaTools"] = _rt.ArenaConnected,
            ["tools"] = new JsonObject
            {
                ["local"] = _rt.Tools.All.Count(t => t.Server is null),
                ["servers"] = new JsonArray([.. _rt.Tools.All.Where(t => t.Server is not null).GroupBy(t => t.Server!)
                    .Select(g => (JsonNode)new JsonObject { ["name"] = _rt.Links.FirstOrDefault(l => l.Name == g.Key)?.Title ?? g.Key, ["count"] = g.Count() })]),
            },
        };
    }

    /// <summary>The models in the shape of Arena's chat config: the page's model picker and tool output read it as they do there.</summary>
    private JsonObject ChatConfig() => new()
    {
        ["model"] = _rt.Model.Name,
        ["models"] = new JsonArray([.. _rt.Models.Select(m => (JsonNode)new JsonObject
        {
            ["name"] = m.Id,
            ["context"] = m.Id == _rt.Model.Name ? _rt.Model.Context : _rt.Config.Context ?? m.Context,
            ["maxOutput"] = m.MaxOutput,
            ["vision"] = false,
            ["tools"] = m.Tools,
            ["thinking"] = m.Thinking,
            ["loaded"] = true,
            // Per million tokens, as Arena shows them.
            ["prices"] = new JsonObject { ["input"] = m.InputCost * 1_000_000, ["cachedInput"] = m.CachedCost * 1_000_000, ["output"] = m.OutputCost * 1_000_000 },
        })]),
        ["auto"] = null,
        ["presets"] = new JsonArray([.. ModelState.Levels.Select(l => (JsonNode)new JsonObject { ["level"] = l, ["label"] = l == "xhigh" ? "Extra high" : char.ToUpperInvariant(l[0]) + l[1..] })]),
        ["defaultThinking"] = null,
        ["argus"] = false,
        ["tools"] = new JsonArray(),
        ["gitlabUrl"] = null,
        ["maxUploadBytes"] = 0,
        ["imageTypes"] = new JsonArray(),
    };

    /// <summary>This folder's saved sessions, newest first.</summary>
    private JsonArray Sessions() => new([.. SessionStore.List(_rt.Env.Paths.SessionsDir, _rt.Workspace.Root, 200).Select(s => (JsonNode)new JsonObject
    {
        ["id"] = s.Id,
        ["title"] = s.Preview.Length > 0 ? s.Preview : "(no question)",
        ["updatedAt"] = new DateTimeOffset(s.Updated).ToString("o"),
        ["messages"] = s.Messages,
    })]);

    /// <summary>
    /// What was sent in this folder, newest first, the terminal's and the page's alike, without the commands only
    /// the terminal has: the chat's ↑ goes on to these after the session's own messages.
    /// </summary>
    private JsonArray Sent() => new([.. _rt.History.Entries().AsEnumerable().Reverse().Where(t => !Repl.TerminalOnly(t)).Distinct().Take(500)
        .Select(t => (JsonNode)new JsonObject { ["text"] = t })]);

    /// <summary>The session open now, as the page shows it.</summary>
    private JsonObject SessionJson()
    {
        var data = File.Exists(_rt.Session.File) ? SessionStore.Load(_rt.Session.File) : new SessionData { Id = _rt.Session.Id };
        var diffs = History.Diffs(data);
        lock (_diffs)
        {
            if (_diffsOf == _rt.Session.Id)
            {
                foreach (var (message, diff) in _diffs)
                {
                    diffs[message] = diff.DeepClone();
                }
            }
        }
        return new JsonObject
        {
            ["id"] = _rt.Session.Id,
            ["messages"] = History.Messages(data),
            ["diffs"] = diffs,
            ["busy"] = Current() is not null,
            ["usage"] = new JsonObject { ["prompt"] = data.Prompt, ["cached"] = data.Cached, ["completion"] = data.Completion },
        };
    }

    private async Task MessageAsync(HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        var text = req.Json()?.Str("text")?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            await res.ErrorAsync(400, "invalid", "Write a message first.", ct);
            return;
        }
        if (Begin("answer") is not { } job)
        {
            await res.ErrorAsync(409, "busy", "An answer is being written: wait for it, or stop it.", ct);
            return;
        }
        // Kept for ↑, here and in the terminal: the folder's history.
        _rt.History.Add(text);
        var count = _rt.Agent.Messages.Count;
        job.Emit(new JsonObject { ["type"] = "question", ["id"] = $"m{count}", ["parentId"] = count == 0 ? null : $"m{count - 1}" });
        job.Running = Task.Run(() => RunTurnAsync(job, text));
        await StreamAsync(job, res, ct);
    }

    /// <summary>A new job when none runs; null when one does.</summary>
    private Job? Begin(string kind)
    {
        lock (_gate)
        {
            if (_job is not null)
            {
                return null;
            }
            return _job = new Job(kind);
        }
    }

    private async Task RunTurnAsync(Job job, string text)
    {
        var turn = new Spend();
        _rt.Turn = turn;
        _answerId = null;
        try
        {
            await _rt.Agent.RunAsync(text, turn, job.Stop.Token);
        }
        catch (OperationCanceledException) when (job.Stop.IsCancellationRequested)
        {
            job.Emit(new JsonObject { ["type"] = "stopped", ["id"] = _answerId });
        }
        catch (Exception e) when (e is GatewayException or HttpRequestException or IOException or McpException)
        {
            job.Emit(new JsonObject { ["type"] = "error", ["message"] = e.Message });
        }
        catch (Exception e)
        {
            _rt.Ui.Error($"The turn failed: {e}");
            job.Emit(new JsonObject { ["type"] = "error", ["message"] = $"Something went wrong in code-arena: {e.Message}" });
        }
        finally
        {
            _rt.Turn = null;
            End(job, new JsonObject
            {
                ["type"] = "done",
                ["id"] = _answerId,
                ["usage"] = new JsonObject { ["prompt"] = turn.Prompt, ["cached"] = turn.Cached, ["completion"] = turn.Completion, ["requests"] = turn.Requests },
            });
        }
    }

    /// <summary>The job's last event, then it is over: the next can start before its watchers hear the end.</summary>
    private void End(Job job, JsonObject last)
    {
        job.Emit(last);
        lock (_gate)
        {
            if (_job == job)
            {
                _job = null;
            }
        }
        job.Finish();
    }

    private async Task CompactAsync(HttpResponse res, CancellationToken ct)
    {
        if (_rt.Agent.Messages.Count < 2)
        {
            await res.ErrorAsync(409, "empty", "Nothing to compact yet.", ct);
            return;
        }
        if (Begin("compact") is not { } job)
        {
            await res.ErrorAsync(409, "busy", "Compact when the answer is done.", ct);
            return;
        }
        job.Emit(new JsonObject { ["type"] = "compacting" });
        job.Running = Task.Run(async () =>
        {
            try
            {
                await _rt.Agent.CompactAsync(force: true, job.Stop.Token);
            }
            catch (OperationCanceledException) when (job.Stop.IsCancellationRequested)
            {
                job.Emit(new JsonObject { ["type"] = "notice", ["kind"] = "stopped", ["text"] = "Stopped: the conversation is as it was." });
            }
            catch (Exception e) when (e is GatewayException or HttpRequestException or IOException)
            {
                job.Emit(new JsonObject { ["type"] = "error", ["message"] = e.Message });
            }
            finally
            {
                End(job, new JsonObject { ["type"] = "done", ["id"] = null });
            }
        });
        await StreamAsync(job, res, ct);
    }

    /// <summary>A job's events from its start, then as they come, until it is over.</summary>
    private static async Task StreamAsync(Job job, HttpResponse res, CancellationToken ct)
    {
        await res.StartEventsAsync(ct);
        var reader = job.Watch();
        Task<bool>? next = null;
        while (true)
        {
            next ??= reader.WaitToReadAsync(ct).AsTask();
            if (await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(15), ct)) != next)
            {
                await res.CommentAsync(ct);
                continue;
            }
            var more = await next;
            next = null;
            if (!more)
            {
                break;
            }
            while (reader.TryRead(out var e))
            {
                await res.EventAsync(e, ct);
            }
        }
        await res.EndAsync(ct);
    }

    private async Task ApproveAsync(HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        var body = req.Json();
        var id = body?.Str("id") ?? "";
        Approval? answer = body?.Str("answer") switch
        {
            "allow" => Approval.Yes,
            "always" => Approval.Always,
            "deny" => Approval.No,
            _ => null,
        };
        if (answer is null)
        {
            await res.ErrorAsync(400, "invalid", "The answer is allow, always or deny.", ct);
            return;
        }
        if (!_waiting.TryRemove(id, out var question))
        {
            await res.ErrorAsync(404, "not_found", "That call is not waiting for an answer any more.", ct);
            return;
        }
        question.TrySetResult(answer.Value);
        await res.NoContentAsync(ct);
    }

    /// <summary>Asks the page whether a call may run, and waits for the answer (Stop ends the wait).</summary>
    private async Task<Approval> AskAsync(ApprovalQuestion q, CancellationToken ct)
    {
        var id = q.CallId.Length > 0 ? q.CallId : $"call_{Guid.NewGuid():N}";
        var answer = new TaskCompletionSource<Approval>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = answer;
        try
        {
            Emit(new JsonObject
            {
                ["type"] = "approval",
                ["id"] = id,
                ["name"] = q.Tool.Name,
                ["arguments"] = Json.Line(q.Args),
                ["tool"] = q.Tool.Server ?? "local",
                ["title"] = q.Tool.Name,
                ["always"] = q.Always,
                ["risk"] = q.Risk,
            });
            return await answer.Task.WaitAsync(ct);
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }
    }

    private async Task SettingsAsync(HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        if (req.Json() is not { } body)
        {
            await res.ErrorAsync(400, "invalid", "Send the settings as a JSON object.", ct);
            return;
        }
        Mode? mode = null;
        if (body.Str("mode") is { } m && (mode = Modes.Parse(m)) is null)
        {
            await res.ErrorAsync(400, "invalid", $"There is no mode {m}: {string.Join(", ", Modes.Names)}.", ct);
            return;
        }
        ModelInfo? model = null;
        if (body.Str("model") is { } name && (model = _rt.Models.FirstOrDefault(x => x.Id == name)) is null)
        {
            await res.ErrorAsync(400, "invalid", $"The gateway has no model {name}.", ct);
            return;
        }
        var thinking = body.ContainsKey("thinking") ? body.Str("thinking") ?? "default" : null;
        if (thinking is not null && thinking != "default" && !ModelState.Levels.Contains(thinking))
        {
            await res.ErrorAsync(400, "invalid", $"Thinking is one of: default, {string.Join(", ", ModelState.Levels)}.", ct);
            return;
        }
        // When the session compacts itself: kept in config.json, as /compact-at keeps it.
        if ((body.ContainsKey("compactAt") || body.ContainsKey("compactTarget"))
            && _rt.SetCompaction(body.Int("compactAt"), body.Int("compactTarget")) is { } wrong)
        {
            await res.ErrorAsync(400, "invalid", wrong, ct);
            return;
        }
        if (mode is { } newMode)
        {
            _rt.Permissions.Mode = newMode;
        }
        if (model is not null && model.Id != _rt.Model.Name)
        {
            _rt.SwitchModel(model);
        }
        if (thinking is not null)
        {
            _rt.Model.Thinking = thinking == "default" ? null : thinking;
        }
        await res.JsonAsync(200, State(), ct);
    }

    /// <summary>A new session (file null), or a saved one: not while a turn runs. An empty session is not kept.</summary>
    private async Task SwitchAsync(HttpResponse res, string? file, CancellationToken ct)
    {
        bool idle;
        lock (_gate)
        {
            idle = _job is null;
            if (idle)
            {
                var empty = _rt.Agent.Messages.Count == 0;
                if (file is null)
                {
                    if (!empty)
                    {
                        _rt.NewSession();
                    }
                }
                else if (!PathsEqual(file, _rt.Session.File))
                {
                    if (empty)
                    {
                        TryDelete(_rt.Session.File);
                    }
                    _rt.Resume(file);
                }
            }
        }
        if (!idle)
        {
            await res.ErrorAsync(409, "busy", "Stop the answer first, or wait for it.", ct);
            return;
        }
        await res.JsonAsync(200, SessionJson(), ct);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left behind: an empty session is not listed anyway.
        }
    }

    // ------------------------------------------------------- the agent's events

    private void Emit(JsonObject e) => Current()?.Emit(e);

    void IAgentEvents.Step()
    {
        var count = _rt.Agent.Messages.Count;
        _answerId = $"m{count}";
        _stepStarted = Stopwatch.GetTimestamp();
        _thinkingStarted = 0;
        _thinkingMs = null;
        Emit(new JsonObject { ["type"] = "assistant", ["id"] = _answerId, ["parentId"] = count == 0 ? null : $"m{count - 1}", ["model"] = _rt.Model.Name });
    }

    void IAgentEvents.Reasoning(string text)
    {
        if (_thinkingStarted == 0)
        {
            _thinkingStarted = Stopwatch.GetTimestamp();
        }
        Emit(new JsonObject { ["type"] = "reasoning", ["text"] = text });
    }

    void IAgentEvents.Text(string text)
    {
        EndThinking();
        Emit(new JsonObject { ["type"] = "content", ["text"] = text });
    }

    private void EndThinking()
    {
        if (_thinkingStarted != 0 && _thinkingMs is null)
        {
            _thinkingMs = (int)Stopwatch.GetElapsedTime(_thinkingStarted).TotalMilliseconds;
            Emit(new JsonObject { ["type"] = "thought", ["ms"] = _thinkingMs });
        }
    }

    void IAgentEvents.StepDone(TokenUsage? usage)
    {
        EndThinking();
        Emit(new JsonObject
        {
            ["type"] = "usage",
            ["prompt"] = usage?.Prompt,
            ["cached"] = usage?.Cached,
            ["completion"] = usage?.Completion,
            ["thinkingMs"] = _thinkingMs,
            ["durationMs"] = (long)Stopwatch.GetElapsedTime(_stepStarted).TotalMilliseconds,
        });
    }

    void IAgentEvents.ToolCall(string id, string name, ToolDef? tool, string arguments) =>
        Emit(new JsonObject { ["type"] = "tool_call", ["id"] = id, ["name"] = name, ["arguments"] = arguments, ["tool"] = tool?.Server ?? "local" });

    void IAgentEvents.ToolResult(string id, string name, int index, ToolResult result, TimeSpan took, bool declined)
    {
        // The results are added to the history after the answer's message, in the calls' order.
        var text = result.Text.Length <= MaxResultChars ? result.Text : result.Text[..MaxResultChars] + $"\n… ({result.Text.Length - MaxResultChars:N0} more characters)";
        var messageId = $"m{_rt.Agent.Messages.Count + index}";
        var e = new JsonObject
        {
            ["type"] = "tool_result",
            ["id"] = id,
            ["messageId"] = messageId,
            ["name"] = name,
            ["text"] = text,
            ["isError"] = result.Error && !declined,
            ["declined"] = declined,
            ["noAccess"] = false,
            ["durationMs"] = (long)took.TotalMilliseconds,
        };
        if (result.Change is { } change && !result.Error)
        {
            _changes.Record(change);
            var diff = History.DiffJson(change.Path, change.Before, change.After, numbered: true);
            e["diff"] = diff;
            lock (_diffs)
            {
                if (_diffsOf != _rt.Session.Id)
                {
                    _diffs.Clear();
                    _diffsOf = _rt.Session.Id;
                }
                _diffs[messageId] = (JsonObject)diff.DeepClone();
            }
        }
        Emit(e);
    }

    void IAgentEvents.Compacted(string notice)
    {
        // The messages are not the same any more (nor their places): the page takes the history afresh.
        lock (_diffs)
        {
            _diffs.Clear();
        }
        var session = SessionJson();
        Emit(new JsonObject { ["type"] = "reset", ["messages"] = session["messages"]!.DeepClone(), ["diffs"] = session["diffs"]!.DeepClone() });
        Emit(new JsonObject { ["type"] = "notice", ["kind"] = "compacted", ["text"] = notice });
    }

    void IAgentEvents.Notice(string text) => Emit(new JsonObject { ["type"] = "notice", ["kind"] = "warning", ["text"] = text });

    // ------------------------------------------------- commands with no time limit

    private void JobStarted(CommandJob job) =>
        Emit(new JsonObject { ["type"] = "job", ["job"] = job.Id, ["command"] = job.Command, ["running"] = true, ["status"] = job.Status() });

    /// <summary>
    /// A job's output, gathered for a quarter of a second at a time: the page gets a few events a second, not one per
    /// write, until the command ends. What comes faster than a page keeps (<see cref="Job.KeptOutput"/>) is sent as its end.
    /// </summary>
    private void JobOutput(CommandJob job, string text)
    {
        lock (_jobOutput)
        {
            (StringBuilder Pending, bool Flushing) state = _jobOutput.TryGetValue(job.Id, out var s) ? s : (new StringBuilder(), false);
            state.Pending.Append(text);
            if (state.Pending.Length > Job.KeptOutput * 2)
            {
                state.Pending.Remove(0, state.Pending.Length - Job.KeptOutput);
            }
            if (!state.Flushing)
            {
                state.Flushing = true;
                _ = Task.Delay(250).ContinueWith(_ => FlushJob(job.Id), TaskScheduler.Default);
            }
            _jobOutput[job.Id] = state;
        }
    }

    private void FlushJob(int id)
    {
        string text;
        lock (_jobOutput)
        {
            if (!_jobOutput.TryGetValue(id, out var state))
            {
                return;
            }
            text = state.Pending.ToString();
            state.Pending.Clear();
            _jobOutput[id] = (state.Pending, false);
        }
        if (text.Length > 0)
        {
            Current()?.EmitOutput(id, text);
        }
    }

    private void JobEnded(CommandJob job)
    {
        FlushJob(job.Id);
        lock (_jobOutput)
        {
            _jobOutput.Remove(job.Id);
        }
        Emit(new JsonObject
        {
            ["type"] = "job_end", ["job"] = job.Id, ["running"] = false, ["status"] = job.Status(), ["exitCode"] = job.ExitCode, ["stopped"] = job.StoppedBy is not null,
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Current() is { } job)
        {
            await job.Stop.CancelAsync();
            await Task.WhenAny(job.Running, Task.Delay(TimeSpan.FromSeconds(5)));
        }
        await _terminals.DisposeAsync();
        await _server.DisposeAsync();
        _rt.Agent.Events = null;
        _rt.Permissions.Asker = null;
        _rt.Jobs.Started -= JobStarted;
        _rt.Jobs.Output -= JobOutput;
        _rt.Jobs.Ended -= JobEnded;
        if (_rt.Agent.Messages.Count == 0)
        {
            TryDelete(_rt.Session.File);
        }
    }

    /// <summary>
    /// One turn (or compaction) and its events: kept from the start, so a page
    /// that comes back mid-turn replays them, and sent to every page watching.
    /// </summary>
    private sealed class Job(string kind)
    {
        /// <summary>What a page keeps of a command's output, and so what a page that comes back is sent of it: the end.</summary>
        public const int KeptOutput = 64 * 1024;

        private readonly object _gate = new();
        // Each event as sent, or a command's output kept as one event (its end), in the place of its first piece.
        private readonly List<object> _events = [];
        private readonly Dictionary<int, OutputTail> _outputs = [];
        private readonly List<Channel<string>> _watchers = [];
        private bool _done;

        public string Kind { get; } = kind;
        public CancellationTokenSource Stop { get; } = new();
        public Task Running { get; set; } = Task.CompletedTask;

        public void Emit(JsonObject e)
        {
            var line = Json.Line(e);
            lock (_gate)
            {
                if (_done)
                {
                    return;
                }
                _events.Add(line);
                foreach (var w in _watchers)
                {
                    w.Writer.TryWrite(line);
                }
            }
        }

        /// <summary>
        /// A piece of a command's output: sent as it comes, however long the command runs; kept for a page that
        /// comes back as the end of it only, so a command that writes for hours does not fill the memory.
        /// </summary>
        public void EmitOutput(int job, string text)
        {
            var line = Json.Line(new JsonObject { ["type"] = "job_output", ["job"] = job, ["text"] = text });
            lock (_gate)
            {
                if (_done)
                {
                    return;
                }
                if (!_outputs.TryGetValue(job, out var tail))
                {
                    _outputs[job] = tail = new OutputTail(job);
                    _events.Add(tail);
                }
                tail.Add(text);
                foreach (var w in _watchers)
                {
                    w.Writer.TryWrite(line);
                }
            }
        }

        public void Finish()
        {
            lock (_gate)
            {
                _done = true;
                foreach (var w in _watchers)
                {
                    w.Writer.TryComplete();
                }
                _watchers.Clear();
            }
        }

        public ChannelReader<string> Watch()
        {
            var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            lock (_gate)
            {
                foreach (var e in _events)
                {
                    channel.Writer.TryWrite(e is OutputTail tail ? tail.Event() : (string)e);
                }
                if (_done)
                {
                    channel.Writer.TryComplete();
                }
                else
                {
                    _watchers.Add(channel);
                }
            }
            return channel.Reader;
        }

        /// <summary>The end of a command's output so far, at most <see cref="KeptOutput"/>, with a line where the start was cut.</summary>
        private sealed class OutputTail(int job)
        {
            private readonly StringBuilder _text = new();
            private bool _cut;

            public void Add(string text)
            {
                _text.Append(text);
                if (_text.Length > KeptOutput * 2)
                {
                    _text.Remove(0, _text.Length - KeptOutput);
                    _cut = true;
                }
            }

            public string Event()
            {
                var text = _text.Length > KeptOutput ? _text.ToString(_text.Length - KeptOutput, KeptOutput) : _text.ToString();
                return Json.Line(new JsonObject
                {
                    ["type"] = "job_output",
                    ["job"] = job,
                    ["text"] = _cut || _text.Length > KeptOutput ? "… (the start is not shown here: the agent reads the output with command_output)\n" + text : text,
                });
            }
        }
    }
}

/// <summary>A saved session as the page shows it: messages in the shape of Arena's chat, and the diffs of its edits.</summary>
internal static partial class History
{
    private const string StoppedMark = "(stopped by the person)";
    private const string SummaryMark = "[The conversation so far, summarized";

    public static JsonArray Messages(SessionData data)
    {
        var list = new JsonArray();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < data.Messages.Count; i++)
        {
            var m = data.Messages[i];
            var role = m.Str("role") ?? "user";
            var content = m.Str("content") ?? "";
            var status = "complete";
            JsonArray? calls = null;
            string? toolName = null;
            string? callId = null;
            string? summary = null;
            if (role == "assistant")
            {
                if (content.EndsWith(StoppedMark, StringComparison.Ordinal))
                {
                    content = content[..^StoppedMark.Length].TrimEnd();
                    status = "stopped";
                }
                if (m["tool_calls"] is JsonArray { Count: > 0 } made)
                {
                    calls = [];
                    foreach (var call in made.OfType<JsonObject>())
                    {
                        var id = call.Str("id") ?? "";
                        var name = call["function"].Str("name") ?? "";
                        names[id] = name;
                        calls.Add(new JsonObject { ["id"] = id, ["function"] = new JsonObject { ["name"] = name, ["arguments"] = call["function"].Str("arguments") ?? "{}" } });
                    }
                }
            }
            else if (role == "tool")
            {
                callId = m.Str("tool_call_id");
                toolName = callId is null ? null : names.GetValueOrDefault(callId);
                status = ToolStatus(content);
            }
            else if (role == "user" && content.StartsWith(SummaryMark, StringComparison.Ordinal))
            {
                var start = content.IndexOf("\n\n", StringComparison.Ordinal);
                summary = start < 0 ? content : content[(start + 2)..];
            }
            var answer = data.Answers.TryGetValue(i, out var a) ? a : default;
            list.Add(new JsonObject
            {
                ["id"] = $"m{i}",
                ["parentId"] = i == 0 ? null : $"m{i - 1}",
                ["role"] = role,
                ["content"] = content,
                ["reasoning"] = null,
                ["toolName"] = toolName,
                ["toolCallId"] = callId,
                ["toolCalls"] = calls,
                ["attachments"] = new JsonArray(),
                ["status"] = status,
                ["error"] = null,
                ["model"] = role == "assistant" ? answer.Model : null,
                ["promptTokens"] = answer.Usage?.Prompt,
                ["cachedTokens"] = answer.Usage?.Cached,
                ["completionTokens"] = answer.Usage?.Completion,
                ["thinkingMs"] = null,
                ["durationMs"] = null,
                ["createdAt"] = "",
                ["noAccess"] = false,
                ["summary"] = summary,
            });
        }
        return list;
    }

    /// <summary>What a tool's saved text says of how it went: refused, failed or done.</summary>
    public static string ToolStatus(string text) =>
        text.StartsWith("The person declined", StringComparison.Ordinal) || text.StartsWith("Plan mode is read-only", StringComparison.Ordinal)
        || text.Contains("needs the person's approval, and this run cannot ask", StringComparison.Ordinal) ? "declined"
        : text.StartsWith("Error", StringComparison.Ordinal) || text.StartsWith("There is no tool named", StringComparison.Ordinal)
          || text.StartsWith("The arguments for ", StringComparison.Ordinal) || text.StartsWith("Stopped by the person", StringComparison.Ordinal)
          || Failed().IsMatch(text) ? "failed"
        : "complete";

    [GeneratedRegex(@"\[(exit code (-\d+|[1-9]\d*)|timed out[^\]]*)\]\s*$")]
    private static partial Regex Failed();

    /// <summary>
    /// The saved edits' diffs, by the result's message: edit_file's old and new
    /// text (the file around them is not kept, so without line numbers). A live
    /// edit's diff has the whole file's.
    /// </summary>
    public static JsonObject Diffs(SessionData data)
    {
        var diffs = new JsonObject();
        var calls = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        for (var i = 0; i < data.Messages.Count; i++)
        {
            var m = data.Messages[i];
            foreach (var call in (m["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
            {
                calls[call.Str("id") ?? ""] = call;
            }
            if (m.Str("role") != "tool" || !calls.TryGetValue(m.Str("tool_call_id") ?? "", out var made) || made["function"].Str("name") != "edit_file"
                || ToolStatus(m.Str("content") ?? "") != "complete" || Json.ParseObject(made["function"].Str("arguments")) is not { } args)
            {
                continue;
            }
            var before = args.Str("old_string") ?? "";
            diffs[$"m{i}"] = DiffJson(args.Str("path") ?? "", before.Length == 0 ? null : before, args.Str("new_string") ?? "", numbered: false);
        }
        return diffs;
    }

    /// <summary>{path, added, removed, more, created, lines: [[op, old, new, text], …]}: an edit as the page draws it.</summary>
    public static JsonObject DiffJson(string path, string? before, string after, bool numbered)
    {
        const int max = 400;
        List<DiffLine> lines;
        int more, added, removed;
        if (string.IsNullOrEmpty(before))
        {
            var all = Diff.Lines(after);
            lines = [.. all.Take(max).Select((t, i) => new DiffLine('+', 0, i + 1, t))];
            (more, added, removed) = (Math.Max(0, all.Length - max), all.Length, 0);
        }
        else
        {
            (lines, more) = Diff.Hunks(before, after, maxLines: max);
            (added, removed) = Diff.Count(before, after);
        }
        return new JsonObject
        {
            ["path"] = path,
            ["added"] = added,
            ["removed"] = removed,
            ["more"] = more,
            ["created"] = before is null,
            ["lines"] = new JsonArray([.. lines.Select(l => (JsonNode)new JsonArray(l.Op.ToString(), numbered ? l.Old : 0, numbered ? l.New : 0, l.Text))]),
        };
    }
}

/// <summary>Opens an address in the person's browser.</summary>
internal static class Browser
{
    /// <summary>False when there is no browser to open (no desktop, nothing to open it with).</summary>
    public static bool Open(string url, Func<string, string?> env)
    {
        // A Linux machine without a desktop would get a text browser in this terminal, if anything.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && string.IsNullOrEmpty(env("DISPLAY")) && string.IsNullOrEmpty(env("WAYLAND_DISPLAY")))
        {
            return false;
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
