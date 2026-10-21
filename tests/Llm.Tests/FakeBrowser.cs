using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Llm.Tests;

/// <summary>
/// Chromium's DevTools as the browser agent drives them: /json/version, then one WebSocket that answers the commands it
/// sends (contexts, pages, navigation, the page's scripts, input, screenshots) over a small shop: its home page, a
/// laptop's page with Buy now, a sign-in page with a password field, and a search. Every command is kept, in order.
/// </summary>
public sealed partial class FakeBrowser : IAsyncDisposable
{
    private readonly WebApplication _app;

    /// <summary>An element of a page: what the snapshot lists, and what clicking it does.</summary>
    public sealed record Element(int Ref, string Role, string Name, string Tag, string Type = "", bool InForm = false, bool Secret = false, bool Search = false,
        bool Field = false, string? Goes = null);

    public sealed record Page(string Title, string Text, Element[] Elements);

    public Dictionary<string, Page> Pages { get; } = new()
    {
        ["https://shop.test/"] = new("Shop", "Welcome to the shop. Laptops and phones.",
        [
            new(1, "link", "Laptops", "a", Goes: "https://shop.test/laptop"),
            new(2, "searchbox", "Search the shop", "input", "search", Search: true, Field: true),
            new(3, "link", "Sign in", "a", Goes: "https://shop.test/sign-in"),
        ]),
        ["https://shop.test/laptop"] = new("Laptop X1", "Laptop X1. 1,299 euros. In stock.",
        [
            new(1, "button", "Buy now", "button", "submit", InForm: true, Goes: "https://shop.test/bought"),
            new(2, "link", "Back to the shop", "a", Goes: "https://shop.test/"),
        ]),
        ["https://shop.test/sign-in"] = new("Sign in", "Sign in to the shop.",
        [
            new(1, "email", "Email", "input", "email", InForm: true, Field: true),
            new(2, "password", "Password", "input", "password", InForm: true, Secret: true, Field: true),
        ]),
        ["https://shop.test/bought"] = new("Thank you", "Your order is placed.", []),
        ["https://shop.test/search?q=phone"] = new("Results", "3 phones found.", []),
    };

    public string Url { get; }
    public ConcurrentQueue<(string Method, JsonObject Params, string? Session)> Commands { get; } = new();

    /// <summary>What was typed, in order.</summary>
    public ConcurrentQueue<string> Typed { get; } = new();

    private readonly ConcurrentDictionary<string, (string Url, List<string> Back, Element? Last)> _pages = new();
    private int _contexts;

    public FakeBrowser()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.UseWebSockets();
        // As Chromium says it: localhost and its own port, which the app replaces with the address it reaches it at.
        _app.MapGet("/json/version", () => Results.Json(new { Browser = "Chrome/141", webSocketDebuggerUrl = "ws://localhost:9223/devtools/browser/fake" }));
        _app.Map("/devtools/browser/fake", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await ServeAsync(socket, ctx.RequestAborted);
        });
        _app.StartAsync().GetAwaiter().GetResult();
        Url = _app.Urls.First();
    }

    public string CurrentUrl(string session) => _pages.TryGetValue(session, out var p) ? p.Url : "";

    private async Task ServeAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult got;
            try
            {
                got = await socket.ReceiveAsync(buffer, ct);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                return;
            }
            if (got.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
            message.Write(buffer, 0, got.Count);
            if (!got.EndOfMessage)
            {
                continue;
            }
            var command = JsonNode.Parse(message.ToArray())!.AsObject();
            message.SetLength(0);
            var method = command["method"]!.GetValue<string>();
            var parameters = command["params"] as JsonObject ?? [];
            var session = command["sessionId"]?.GetValue<string>();
            Commands.Enqueue((method, parameters, session));
            var answer = new JsonObject { ["id"] = command["id"]!.GetValue<int>() };
            var result = Answer(method, parameters, session);
            if (result is JsonObject r)
            {
                answer["result"] = r;
            }
            else
            {
                answer["error"] = new JsonObject { ["code"] = -32000, ["message"] = $"{method} failed" };
            }
            await socket.SendAsync(Encoding.UTF8.GetBytes(answer.ToJsonString()), WebSocketMessageType.Text, true, ct);
        }
    }

    private JsonObject? Answer(string method, JsonObject p, string? session)
    {
        switch (method)
        {
            case "Target.createBrowserContext":
                return new JsonObject { ["browserContextId"] = $"ctx{Interlocked.Increment(ref _contexts)}" };
            case "Target.createTarget":
                return new JsonObject { ["targetId"] = "t-" + p["browserContextId"] };
            case "Target.attachToTarget":
            {
                var s = "s-" + p["targetId"];
                _pages[s] = ("about:blank", [], null);
                return new JsonObject { ["sessionId"] = s };
            }
            case "Page.navigate":
            {
                var page = _pages[session!];
                page.Back.Add(page.Url);
                _pages[session!] = (p["url"]!.GetValue<string>(), page.Back, null);
                return new JsonObject { ["frameId"] = "f1" };
            }
            case "Page.captureScreenshot":
                return new JsonObject { ["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("jpeg of " + _pages[session!].Url)) };
            case "Input.insertText":
                Typed.Enqueue(p["text"]!.GetValue<string>());
                return [];
            case "Input.dispatchMouseEvent" when p["type"]?.GetValue<string>() == "mouseReleased":
            {
                var page = _pages[session!];
                if (page.Last?.Goes is { } goes)
                {
                    page.Back.Add(page.Url);
                    _pages[session!] = (goes, page.Back, null);
                }
                return [];
            }
            case "Input.dispatchKeyEvent" when p["type"]?.GetValue<string>() == "keyDown" && p["key"]?.GetValue<string>() == "Enter":
            {
                var page = _pages[session!];
                if (page.Last is { Search: true })
                {
                    page.Back.Add(page.Url);
                    _pages[session!] = ($"https://shop.test/search?q={Typed.LastOrDefault()}", page.Back, null);
                }
                return [];
            }
            case "Runtime.evaluate":
                return new JsonObject { ["result"] = new JsonObject { ["type"] = "object", ["value"] = Evaluate(p["expression"]!.GetValue<string>(), session!) } };
            default:
                return [];
        }
    }

    private JsonNode? Evaluate(string expression, string session)
    {
        var (url, back, _) = _pages[session];
        Pages.TryGetValue(url, out var page);
        if (expression == "document.readyState")
        {
            return "complete";
        }
        if (expression.Contains("history.back()", StringComparison.Ordinal))
        {
            if (back.Count > 0)
            {
                _pages[session] = (back[^1], back[..^1], null);
            }
            return null;
        }
        if (expression.Contains("document.activeElement", StringComparison.Ordinal))
        {
            return true;
        }
        if (ElementAsked().Match(expression) is { Success: true } asked)
        {
            var e = page?.Elements.FirstOrDefault(x => x.Ref == int.Parse(asked.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            _pages[session] = (url, back, e);
            return e is null ? null : new JsonObject
            {
                ["x"] = 100 + e.Ref, ["y"] = 200, ["tag"] = e.Tag, ["type"] = e.Type, ["inForm"] = e.InForm, ["text"] = e.Name, ["field"] = e.Field, ["secret"] = e.Secret, ["search"] = e.Search,
            };
        }
        if (expression.Contains("elements: out", StringComparison.Ordinal))
        {
            return new JsonObject
            {
                ["url"] = url, ["title"] = page?.Title ?? "", ["text"] = page?.Text ?? "", ["more"] = false, ["scroll"] = 0, ["height"] = 800, ["view"] = 800,
                ["elements"] = new JsonArray([.. (page?.Elements ?? []).Select(e => (JsonNode)new JsonObject
                {
                    ["ref"] = e.Ref, ["role"] = e.Role, ["name"] = e.Name, ["on"] = true, ["value"] = "", ["checked"] = false,
                })]),
            };
        }
        if (expression.Contains("innerText", StringComparison.Ordinal))
        {
            return page?.Text ?? "";
        }
        return null;
    }

    [GeneratedRegex("data-arena-ref=\"(\\d+)\"\\]'\\);\\s*if \\(!e\\) return null;")]
    private static partial Regex ElementAsked();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
