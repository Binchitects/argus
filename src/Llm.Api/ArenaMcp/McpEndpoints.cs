using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Api.Gateway;
using Llm.Api.Identity;
using Llm.Api.Settings;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.ArenaMcp;

/// <summary>Configuration section "Mcp": Arena MCP, each person's chat tools for outside agents (Settings → Arena MCP).</summary>
public sealed class McpOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long connecting (initialize) and listing wait for the tools that are a network round
    /// trip (Argus, admins' MCP servers): they start together, and one that has not answered by
    /// then is listed as not available now, so an agent is never held by a slow or dead server.
    /// </summary>
    public int ListWaitSeconds { get; set; } = 10;
}

/// <summary>Calls running now, so a client's notifications/cancelled stops the one it means.</summary>
public sealed class McpCalls
{
    private readonly ConcurrentDictionary<(Guid User, string Session, string Request), CancellationTokenSource> _running = new();

    public IDisposable Track(Guid user, string session, string request, CancellationTokenSource call)
    {
        _running[(user, session, request)] = call;
        return new Untrack(() => _running.TryRemove(new((user, session, request), call)));
    }

    public void Cancel(Guid user, string session, string request)
    {
        if (_running.TryGetValue((user, session, request), out var call))
        {
            call.Cancel();
        }
    }

    private sealed class Untrack(Action done) : IDisposable
    {
        public void Dispose() => done();
    }
}

/// <summary>
/// Arena MCP: https://DOMAIN/mcp, the MCP server (streamable HTTP, 2025-06-18) that gives an
/// outside agent (Code Arena, Claude Code, Qwen Code, Continue...) the person's chat tools, signed
/// in with their own API key. Stateless: every request carries the key, a list is kept a minute,
/// and a session id is only a label (for cancelling a call). Requests answer as JSON; a tool call
/// answers as server-sent events, with its progress and a comment line while it runs.
/// </summary>
public static class McpEndpoints
{
    public const string Path = "/mcp";

    /// <summary>The protocol versions served, newest first.</summary>
    public static readonly string[] Versions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>How often a running call says it is still running (proxies drop a silent connection).</summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    public static void AddArenaMcp(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<McpOptions>(config.GetSection("Mcp"));
        services.AddMemoryCache();
        services.AddScoped<McpPeople>();
        services.AddScoped<McpTools>();
        services.AddScoped<McpPrompts>();
        services.AddSingleton<McpCalls>();
    }

    public static void MapArenaMcp(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, PostAsync);
        // No stream of the server's own (it sends nothing unasked) and no sessions to end.
        app.MapMethods(Path, ["GET", "DELETE"], (HttpContext http) =>
        {
            http.Response.Headers.Allow = "POST";
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        });
        app.MapGet("/.well-known/mcp", Discovery);
        app.MapGet("/api/account/mcp", InfoAsync).RequireAuthorization();
    }

    /// <summary>For clients that look: where the server is, and how to sign in to it.</summary>
    private static IResult Discovery(IOptionsMonitor<McpOptions> options, IOptions<AuthOptions> auth, IOptionsMonitor<BrandingOptions> branding) =>
        !options.CurrentValue.Enabled ? Results.NotFound() : Results.Ok(new
        {
            name = "arena",
            title = Product(branding),
            version = AppInfo.Current.Version,
            url = auth.Value.Origin + Path,
            transport = "streamable-http",
            protocolVersions = Versions,
            authentication = new { type = "bearer", header = "Authorization: Bearer <your API key>", key = "Your API key (sk-…), from Your account → API key." },
            documentation = auth.Value.Origin + "/setup",
        });

    /// <summary>For the person's Connect your tools page: whether it is on, its address, and the tools it serves them.</summary>
    private static async Task<IResult> InfoAsync(ClaimsPrincipal p, UserManager<AppUser> users, McpTools tools, IOptionsMonitor<McpOptions> options, IOptions<AuthOptions> auth,
        CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        var enabled = options.CurrentValue.Enabled;
        var served = enabled ? await tools.ServedAsync(me, ct) : [];
        return Results.Ok(new
        {
            enabled,
            url = auth.Value.Origin + Path,
            tools = served.Select(t => new { id = t.Tool.Id, title = t.Tool.Title, askFirst = t.Setting.AskFirst }),
        });
    }

    private static async Task PostAsync(HttpContext http, IOptionsMonitor<McpOptions> options, McpPeople people, McpTools tools, McpPrompts prompts, McpCalls calls,
        IOptionsMonitor<BrandingOptions> branding, Models.ModelPolicy policy, Chat.ChatModels models)
    {
        var ct = http.RequestAborted;
        if (!options.CurrentValue.Enabled)
        {
            await RefuseAsync(http, StatusCodes.Status404NotFound, "Arena MCP is turned off here (an admin turns it on under Settings → Arena MCP).");
            return;
        }
        McpCaller caller;
        try
        {
            caller = await people.FromHeaderAsync(http.Request.Headers.Authorization.ToString() is { Length: > 0 } h ? h : null, ct);
        }
        catch (GatewayException)
        {
            await RefuseAsync(http, StatusCodes.Status503ServiceUnavailable, "The gateway cannot check API keys at the moment. Try again shortly.");
            return;
        }
        if (caller.User is not { } user)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer realm=\"arena\"";
            await RefuseAsync(http, StatusCodes.Status401Unauthorized, caller.Refusal!);
            return;
        }
        var said = http.Request.Headers["MCP-Protocol-Version"].ToString();
        if (said.Length > 0 && !Versions.Contains(said))
        {
            await RefuseAsync(http, StatusCodes.Status400BadRequest, $"MCP-Protocol-Version {said} is not served here; these are: {string.Join(", ", Versions)}.");
            return;
        }
        // Without the header, the client speaks 2025-03-26 (the specification says to assume so).
        var protocol = said.Length > 0 ? said : "2025-03-26";
        var session = http.Request.Headers["Mcp-Session-Id"].ToString();

        using var reader = new StreamReader(http.Request.Body);
        var raw = await reader.ReadToEndAsync(ct);
        // Each message read as the chat reads a tool call's arguments: a key written twice keeps its
        // last value (JsonObject would throw on it).
        List<JsonObject?> batch;
        JsonValueKind kind;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            kind = doc.RootElement.ValueKind;
            batch = kind == JsonValueKind.Array
                ? [.. doc.RootElement.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Object ? ChatService.Arguments(e.GetRawText()) : null)]
                : [];
        }
        catch (JsonException)
        {
            await WriteAsync(http, StatusCodes.Status400BadRequest, Error(null, -32700, "The body is not JSON."));
            return;
        }
        // The model a new chat of theirs starts with: an agent with no model of its own takes it (Code Arena).
        async Task<string?> DefaultModelAsync(CancellationToken token) => (await policy.ForAsync(user, await models.ListAsync(token), token)).Default?.Name ?? models.DefaultName;
        var handler = new Handler(user, protocol, session, tools, prompts, calls, Product(branding), DefaultModelAsync);

        // A batch (2025-03-26 allows them): each answered, together, as JSON.
        if (kind == JsonValueKind.Array)
        {
            var answers = new JsonArray();
            foreach (var item in batch)
            {
                if (item is null)
                {
                    answers.Add(Error(null, -32600, "A JSON-RPC message is an object."));
                }
                else if (IsRequest(item))
                {
                    answers.Add(await handler.AnswerAsync(item, null, ct));
                }
                else
                {
                    handler.Notice(item);
                }
            }
            if (answers.Count == 0)
            {
                http.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }
            await WriteAsync(http, StatusCodes.Status200OK, answers);
            return;
        }
        if (kind != JsonValueKind.Object)
        {
            await WriteAsync(http, StatusCodes.Status400BadRequest, Error(null, -32600, "A JSON-RPC message is an object."));
            return;
        }
        var message = ChatService.Arguments(raw);
        if (!IsRequest(message))
        {
            // A notification, or an answer to a request this server never makes.
            handler.Notice(message);
            http.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }
        var method = message["method"]!.GetValue<string>();
        if (method == "tools/call" && http.Request.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await StreamAsync(http, handler, message, ct);
            return;
        }
        var answer = await handler.AnswerAsync(message, null, ct);
        if (method == "initialize")
        {
            // Only a label: it tells this client's calls from another's when one is cancelled.
            http.Response.Headers["Mcp-Session-Id"] = Guid.NewGuid().ToString("N");
        }
        await WriteAsync(http, StatusCodes.Status200OK, answer);
    }

    /// <summary>A tool call as server-sent events: its progress as it comes, a comment line while it runs, then its answer.</summary>
    private static async Task StreamAsync(HttpContext http, Handler handler, JsonObject message, CancellationToken ct)
    {
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        // Tell proxies not to buffer: progress should reach the agent as it happens.
        http.Response.Headers["X-Accel-Buffering"] = "no";
        using var writing = new SemaphoreSlim(1, 1);
        async Task SendAsync(string text)
        {
            await writing.WaitAsync(ct);
            try
            {
                await http.Response.WriteAsync(text, ct);
                await http.Response.Body.FlushAsync(ct);
            }
            finally
            {
                writing.Release();
            }
        }
        try
        {
            await http.Response.Body.FlushAsync(ct);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var beats = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(Heartbeat);
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    await SendAsync(": still working\n\n");
                }
            }, CancellationToken.None);
            JsonObject answer;
            try
            {
                answer = await handler.AnswerAsync(message, note => SendAsync("data: " + note.ToJsonString(Mcp.Plain) + "\n\n"), ct);
            }
            finally
            {
                await stop.CancelAsync();
                try
                {
                    await beats;
                }
                catch (OperationCanceledException)
                {
                    // The heartbeat stopped, as it should.
                }
            }
            await SendAsync("data: " + answer.ToJsonString(Mcp.Plain) + "\n\n");
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException && ct.IsCancellationRequested)
        {
            // The client went away: the call was stopped with it.
        }
    }

    /// <summary>One person's requests, as JSON-RPC.</summary>
    private sealed class Handler(AppUser user, string protocol, string session, McpTools tools, McpPrompts prompts, McpCalls calls, string product,
        Func<CancellationToken, Task<string?>> defaultModel)
    {
        /// <summary>A notification: a cancelled call is stopped; the rest need nothing.</summary>
        public void Notice(JsonObject message)
        {
            if (message["method"]?.GetValue<string>() == "notifications/cancelled" && (message["params"] as JsonObject)?["requestId"] is { } request)
            {
                calls.Cancel(user.Id, session, request.ToJsonString());
            }
        }

        /// <param name="notify">Where a tool call's progress goes, when the client asked for it and can hear it.</param>
        public async Task<JsonObject> AnswerAsync(JsonObject message, Func<JsonObject, Task>? notify, CancellationToken ct)
        {
            var id = message["id"]!;
            var method = message["method"]!.GetValue<string>();
            var @params = message["params"] as JsonObject ?? [];
            return method switch
            {
                "initialize" => Result(id, await InitializeAsync(@params, ct)),
                "ping" => Result(id, []),
                "tools/list" => Result(id, new JsonObject { ["tools"] = (await tools.CatalogAsync(user, fresh: false, ct)).Tools.DeepClone() }),
                "tools/call" => await CallAsync(id, @params, notify, ct),
                "prompts/list" => Result(id, new JsonObject { ["prompts"] = await prompts.ListAsync(user, ct) }),
                "prompts/get" => await prompts.GetAsync(user, Str(@params, "name") ?? "", @params["arguments"]?.DeepClone() as JsonObject ?? [], ct) is { } prompt
                    ? Result(id, prompt)
                    : Error(id, -32602, $"There is no prompt named {Str(@params, "name")} here."),
                "resources/list" => Result(id, new JsonObject { ["resources"] = new JsonArray() }),
                "resources/templates/list" => Result(id, new JsonObject { ["resourceTemplates"] = new JsonArray() }),
                "resources/read" => await tools.ReadAsync(user, Str(@params, "uri") ?? "", ct) is { } file
                    ? Result(id, file)
                    : Error(id, -32002, $"There is no file of yours at {Str(@params, "uri")}."),
                _ => Error(id, -32601, $"{method} is not served here."),
            };
        }

        private async Task<JsonObject> InitializeAsync(JsonObject @params, CancellationToken ct)
        {
            var asked = Str(@params, "protocolVersion");
            var catalog = await tools.CatalogAsync(user, fresh: true, ct);
            return new JsonObject
            {
                ["protocolVersion"] = asked is not null && Versions.Contains(asked) ? asked : Versions[0],
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject { ["listChanged"] = false },
                    ["prompts"] = new JsonObject { ["listChanged"] = false },
                    ["resources"] = new JsonObject(),
                },
                ["serverInfo"] = new JsonObject { ["name"] = "arena", ["title"] = product, ["version"] = AppInfo.Current.Version },
                ["instructions"] = catalog.Instructions,
                // MCP's place for what a client may use beyond the protocol: the person's default chat model.
                ["_meta"] = new JsonObject { ["arena/defaultModel"] = await defaultModel(ct) },
            };
        }

        private async Task<JsonObject> CallAsync(JsonNode id, JsonObject @params, Func<JsonObject, Task>? notify, CancellationToken ct)
        {
            var name = Str(@params, "name");
            if (string.IsNullOrEmpty(name))
            {
                return Error(id, -32602, "Say which tool to call in name.");
            }
            var arguments = @params["arguments"] switch
            {
                null => [],
                JsonObject given => (JsonObject)given.DeepClone(),
                _ => null,
            };
            if (arguments is null)
            {
                return Error(id, -32602, "The arguments are a JSON object.");
            }
            Func<McpProgress, Task>? progress = notify is not null && (@params["_meta"] as JsonObject)?["progressToken"] is { } token
                ? p => notify(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "notifications/progress",
                    ["params"] = new JsonObject { ["progressToken"] = token.DeepClone(), ["progress"] = p.Progress, ["total"] = p.Total, ["message"] = p.Message },
                })
                : null;
            using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var tracked = calls.Track(user.Id, session, id.ToJsonString(), call);
            ToolResult? result;
            try
            {
                result = await tools.CallAsync(user, name, arguments, progress, call.Token);
            }
            catch (OperationCanceledException) when (call.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                result = new ToolResult("The call was cancelled.", IsError: true);
            }
            return result is null ? Error(id, -32602, $"There is no tool named {name} here.") : Result(id, tools.Content(result, protocol));
        }
    }

    private static bool IsRequest(JsonObject message) => message["method"] is JsonValue m && m.TryGetValue<string>(out _) && message["id"] is JsonValue;

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static async Task WriteAsync(HttpContext http, int status, JsonNode body)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(body.ToJsonString(Mcp.Plain), http.RequestAborted);
    }

    /// <summary>Refused before any JSON-RPC: the reason, as the app's other refusals say it.</summary>
    private static async Task RefuseAsync(HttpContext http, int status, string error)
    {
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(new { error }, http.RequestAborted);
    }

    private static string Product(IOptionsMonitor<BrandingOptions> branding) =>
        string.IsNullOrWhiteSpace(branding.CurrentValue.ProductName) ? AppInfo.Current.Name : branding.CurrentValue.ProductName;
}
