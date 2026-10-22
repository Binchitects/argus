using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat.Tools.Browser;

/// <summary>One chat's browser: its own context (cookies and storage apart from every other chat's) and page.</summary>
public sealed class BrowserSession(Guid conversationId, Guid userId, string context, string session)
{
    public Guid ConversationId { get; } = conversationId;
    /// <summary>Whose chat: only they watch it or take it over.</summary>
    public Guid UserId { get; } = userId;
    public string Context { get; } = context;
    public string Session { get; } = session;
    public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>The person has the browser: the agent's calls wait until they hand it back.</summary>
    public bool TakenOver { get; set; }
    public string Url { get; set; } = "about:blank";
    public string Title { get; set; } = "";
    /// <summary>One thing at a time on the page: the agent's steps and the person's clicks take turns.</summary>
    public SemaphoreSlim Turn { get; } = new(1, 1);
    public (DateTimeOffset At, byte[] Jpeg)? Shot { get; set; }
}

/// <summary>
/// The chats' browsers, on the one browser the app drives (the browser profile's container): a context and a page for each
/// chat that uses it, made on its first call, closed after <see cref="BrowserOptions.IdleMinutes"/> unused (or for a newer
/// one when <see cref="BrowserOptions.MostSessions"/> are open). The DevTools connection is made again when the browser restarted.
/// </summary>
public sealed partial class BrowserSessions(IHttpClientFactory http, IOptionsMonitor<BrowserOptions> options, TimeProvider clock, ILogger<BrowserSessions> logger)
    : BackgroundService
{
    public const string Client = "browser";
    public const int Width = 1280;
    public const int Height = 800;

    private readonly ConcurrentDictionary<Guid, BrowserSession> _sessions = new();
    private readonly SemaphoreSlim _connecting = new(1, 1);
    private Cdp? _cdp;

    private (DateTimeOffset At, string? Problem)? _checked;

    /// <summary>Why there is no browser now, or null; asked of the browser at most every 30 seconds (every tool list asks).</summary>
    public async Task<string?> UnavailableAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.CurrentValue.Url))
        {
            return "The browser does not run here (COMPOSE_PROFILES=browser turns it on).";
        }
        if (_cdp is { Alive: true })
        {
            return null;
        }
        if (_checked is { } kept && clock.GetUtcNow() - kept.At < TimeSpan.FromSeconds(30))
        {
            return kept.Problem;
        }
        string? problem = null;
        try
        {
            await CdpAsync(ct);
        }
        catch (CdpException ex)
        {
            problem = $"The browser is not running (COMPOSE_PROFILES=browser turns it on): {ex.Message}";
        }
        _checked = (clock.GetUtcNow(), problem);
        return problem;
    }

    private async Task<Cdp> CdpAsync(CancellationToken ct)
    {
        if (_cdp is { Alive: true } live)
        {
            return live;
        }
        await _connecting.WaitAsync(ct);
        try
        {
            if (_cdp is { Alive: true } again)
            {
                return again;
            }
            if (_cdp is not null)
            {
                // The browser restarted: its contexts went with it.
                await _cdp.DisposeAsync();
                _sessions.Clear();
            }
            var url = options.CurrentValue.Url is { Length: > 0 } u ? new Uri(u) : throw new CdpException("no browser is set up");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                _cdp = await Cdp.ConnectAsync(url, http.CreateClient(Client), timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new CdpException("it did not answer in time");
            }
            catch (System.Net.WebSockets.WebSocketException ex)
            {
                throw new CdpException(ex.Message);
            }
            return _cdp;
        }
        finally
        {
            _connecting.Release();
        }
    }

    public BrowserSession? Find(Guid conversationId) => _sessions.TryGetValue(conversationId, out var s) && _cdp is { Alive: true } ? s : null;

    /// <summary>The chat's browser, made on its first use.</summary>
    public async Task<BrowserSession> OpenAsync(Guid conversationId, Guid userId, CancellationToken ct)
    {
        var cdp = await CdpAsync(ct);
        if (_sessions.TryGetValue(conversationId, out var open))
        {
            open.LastUsed = clock.GetUtcNow();
            return open;
        }
        while (_sessions.Count >= Math.Max(1, options.CurrentValue.MostSessions) && _sessions.Values.MinBy(s => s.LastUsed) is { } oldest)
        {
            await CloseAsync(oldest.ConversationId, ct);
        }
        var context = (await cdp.SendAsync("Target.createBrowserContext", new JsonObject { ["disposeOnDetach"] = false }, ct: ct))["browserContextId"]!.GetValue<string>();
        var target = (await cdp.SendAsync("Target.createTarget", new JsonObject
        {
            ["url"] = "about:blank", ["browserContextId"] = context, ["width"] = Width, ["height"] = Height,
        }, ct: ct))["targetId"]!.GetValue<string>();
        var session = (await cdp.SendAsync("Target.attachToTarget", new JsonObject { ["targetId"] = target, ["flatten"] = true }, ct: ct))["sessionId"]!.GetValue<string>();
        await cdp.SendAsync("Emulation.setDeviceMetricsOverride", new JsonObject { ["width"] = Width, ["height"] = Height, ["deviceScaleFactor"] = 1, ["mobile"] = false }, session, ct);
        var made = new BrowserSession(conversationId, userId, context, session) { LastUsed = clock.GetUtcNow() };
        _sessions[conversationId] = made;
        return made;
    }

    public async Task CloseAsync(Guid conversationId, CancellationToken ct)
    {
        if (!_sessions.TryRemove(conversationId, out var s) || _cdp is not { Alive: true } cdp)
        {
            return;
        }
        try
        {
            await cdp.SendAsync("Target.disposeBrowserContext", new JsonObject { ["browserContextId"] = s.Context }, ct: ct);
        }
        catch (CdpException)
        {
        }
    }

    public async Task<JsonObject> SendAsync(BrowserSession s, string method, JsonObject? parameters, CancellationToken ct)
    {
        s.LastUsed = clock.GetUtcNow();
        return await (await CdpAsync(ct)).SendAsync(method, parameters, s.Session, ct);
    }

    /// <summary>A script's value on the page (returned by value: JSON).</summary>
    public async Task<JsonNode?> EvaluateAsync(BrowserSession s, string expression, CancellationToken ct)
    {
        var r = await SendAsync(s, "Runtime.evaluate", new JsonObject { ["expression"] = expression, ["returnByValue"] = true, ["awaitPromise"] = true }, ct);
        if (r["exceptionDetails"] is JsonObject failed)
        {
            throw new CdpException("The page's script failed: " + (failed["exception"]?["description"]?.GetValue<string>() ?? failed["text"]?.GetValue<string>() ?? "?"));
        }
        return r["result"]?["value"]?.DeepClone();
    }

    /// <summary>Waits for the page to have loaded (or 15 seconds), and a moment for what it draws after.</summary>
    public async Task SettleAsync(BrowserSession s, CancellationToken ct)
    {
        var until = clock.GetUtcNow().AddSeconds(15);
        while (clock.GetUtcNow() < until)
        {
            if ((await EvaluateAsync(s, "document.readyState", ct))?.GetValue<string>() == "complete")
            {
                break;
            }
            await Task.Delay(250, ct);
        }
        await Task.Delay(400, ct);
    }

    public async Task NavigateAsync(BrowserSession s, string url, CancellationToken ct)
    {
        var r = await SendAsync(s, "Page.navigate", new JsonObject { ["url"] = url }, ct);
        if (r["errorText"]?.GetValue<string>() is { Length: > 0 } error && error != "net::ERR_ABORTED")
        {
            throw new CdpException($"The page could not be opened ({error}).");
        }
        await SettleAsync(s, ct);
    }

    /// <summary>A click at a point of the page's window (CSS pixels).</summary>
    public async Task ClickAsync(BrowserSession s, double x, double y, CancellationToken ct)
    {
        foreach (var type in new[] { "mouseMoved", "mousePressed", "mouseReleased" })
        {
            await SendAsync(s, "Input.dispatchMouseEvent", new JsonObject { ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = "left", ["clickCount"] = 1 }, ct);
        }
    }

    public Task TypeAsync(BrowserSession s, string text, CancellationToken ct) => SendAsync(s, "Input.insertText", new JsonObject { ["text"] = text }, ct);

    /// <summary>The keys a person presses besides letters: Enter, Tab, Escape, Backspace, the arrows, Page Up and Down, Home, End.</summary>
    public static readonly IReadOnlyDictionary<string, (string Code, int Key, string? Text)> Keys = new Dictionary<string, (string, int, string?)>(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = ("Enter", 13, "\r"), ["Tab"] = ("Tab", 9, null), ["Escape"] = ("Escape", 27, null), ["Backspace"] = ("Backspace", 8, null),
        ["ArrowDown"] = ("ArrowDown", 40, null), ["ArrowUp"] = ("ArrowUp", 38, null), ["ArrowLeft"] = ("ArrowLeft", 37, null), ["ArrowRight"] = ("ArrowRight", 39, null),
        ["PageDown"] = ("PageDown", 34, null), ["PageUp"] = ("PageUp", 33, null), ["Home"] = ("Home", 36, null), ["End"] = ("End", 35, null),
    };

    public async Task KeyAsync(BrowserSession s, string key, CancellationToken ct)
    {
        if (!Keys.TryGetValue(key, out var k))
        {
            throw new CdpException($"No key {key}: {string.Join(", ", Keys.Keys)}.");
        }
        var down = new JsonObject { ["type"] = "keyDown", ["key"] = k.Code, ["code"] = k.Code, ["windowsVirtualKeyCode"] = k.Key };
        if (k.Text is not null)
        {
            down["text"] = k.Text;
        }
        await SendAsync(s, "Input.dispatchKeyEvent", down, ct);
        await SendAsync(s, "Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyUp", ["key"] = k.Code, ["code"] = k.Code, ["windowsVirtualKeyCode"] = k.Key }, ct);
    }

    /// <summary>Pages that draw for the app (SVG pictures and frames) at once: each takes a little of the browser's time.</summary>
    private readonly SemaphoreSlim _drawing = new(3, 3);

    /// <summary>
    /// A blank page of its own (its own context: no cookies, no storage, and no network at all) for drawing what the app gives
    /// it, at this size with a see-through background; <paramref name="work"/> sends it DevTools commands. Closed after.
    /// </summary>
    public async Task<T> DrawingPageAsync<T>(int width, int height, Func<Func<string, JsonObject?, Task<JsonObject>>, Task<T>> work, CancellationToken ct)
    {
        var cdp = await CdpAsync(ct);
        await _drawing.WaitAsync(ct);
        string? context = null;
        try
        {
            context = (await cdp.SendAsync("Target.createBrowserContext", new JsonObject { ["disposeOnDetach"] = true }, ct: ct))["browserContextId"]!.GetValue<string>();
            var target = (await cdp.SendAsync("Target.createTarget", new JsonObject
            {
                ["url"] = "about:blank", ["browserContextId"] = context, ["width"] = width, ["height"] = height,
            }, ct: ct))["targetId"]!.GetValue<string>();
            var session = (await cdp.SendAsync("Target.attachToTarget", new JsonObject { ["targetId"] = target, ["flatten"] = true }, ct: ct))["sessionId"]!.GetValue<string>();
            Task<JsonObject> Send(string method, JsonObject? parameters) => cdp.SendAsync(method, parameters, session, ct);
            await Send("Emulation.setDeviceMetricsOverride", new JsonObject { ["width"] = width, ["height"] = height, ["deviceScaleFactor"] = 1, ["mobile"] = false });
            await Send("Emulation.setDefaultBackgroundColorOverride", new JsonObject { ["color"] = new JsonObject { ["r"] = 0, ["g"] = 0, ["b"] = 0, ["a"] = 0 } });
            await Send("Network.enable", null);
            await Send("Network.emulateNetworkConditions", new JsonObject { ["offline"] = true, ["latency"] = 0, ["downloadThroughput"] = -1, ["uploadThroughput"] = -1 });
            return await work(Send);
        }
        finally
        {
            if (context is not null)
            {
                try
                {
                    await cdp.SendAsync("Target.disposeBrowserContext", new JsonObject { ["browserContextId"] = context }, ct: CancellationToken.None);
                }
                catch (CdpException)
                {
                    // The browser went: its pages with it.
                }
            }
            _drawing.Release();
        }
    }

    /// <summary>The page as it is now, as a JPEG (kept a moment: a live view asks often).</summary>
    public async Task<byte[]> ScreenAsync(BrowserSession s, CancellationToken ct)
    {
        if (s.Shot is { } kept && clock.GetUtcNow() - kept.At < TimeSpan.FromMilliseconds(400))
        {
            return kept.Jpeg;
        }
        var r = await SendAsync(s, "Page.captureScreenshot", new JsonObject { ["format"] = "jpeg", ["quality"] = 70 }, ct);
        var jpeg = Convert.FromBase64String(r["data"]?.GetValue<string>() ?? "");
        s.Shot = (clock.GetUtcNow(), jpeg);
        return jpeg;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var idle = clock.GetUtcNow().AddMinutes(-Math.Max(1, options.CurrentValue.IdleMinutes));
                foreach (var s in _sessions.Values.Where(s => s.LastUsed < idle && !s.TakenOver).ToList())
                {
                    try
                    {
                        await CloseAsync(s.ConversationId, stoppingToken);
                    }
                    catch (Exception ex) when (ex is CdpException or OperationCanceledException && !stoppingToken.IsCancellationRequested)
                    {
                        LogClose(logger, s.ConversationId, ex);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_cdp is not null)
        {
            await _cdp.DisposeAsync();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A chat's browser ({Conversation}) could not be closed")]
    private static partial void LogClose(ILogger logger, Guid conversation, Exception ex);
}
