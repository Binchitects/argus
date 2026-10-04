using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Gateway;

/// <summary>The answer cache's settings, in the Gateway section.</summary>
public sealed class AnswerCacheOptions
{
    /// <summary>off; opt-in: the keys of people who turned it on (Your account); all: every key.</summary>
    public string AnswerCache { get; set; } = AnswerCacheModes.Off;
    /// <summary>How long an answer is kept.</summary>
    public TimeSpan AnswerCacheTtl { get; set; } = TimeSpan.FromDays(1);
    /// <summary>How long what the gateway said of a key (blocked, its models, its person) is believed. Not on the Settings page; tests set 0.</summary>
    public TimeSpan AnswerCacheKeyCheck { get; set; } = TimeSpan.FromSeconds(30);

    public bool On => AnswerCache is AnswerCacheModes.OptIn or AnswerCacheModes.All;
}

public static class AnswerCacheModes
{
    public const string Off = "off";
    public const string OptIn = "opt-in";
    public const string All = "all";
}

/// <summary>A request the cache takes part in: its exact-match hash, the key's hash, and the answer kept for it (null: none yet).</summary>
public sealed record CacheLookup(string Hash, string KeyHash, string Model, JsonObject? Answer);

/// <summary>What the cache remembers between requests: what the gateway said of each key lately, and when old answers were last cleared.</summary>
public sealed class AnswerCacheState
{
    public ConcurrentDictionary<string, (GatewayKeyInfo? Key, bool OptedIn, DateTimeOffset At)> Keys { get; } = new();
    public DateTimeOffset LastCleared { get; set; }
}

/// <summary>
/// The answer cache for API keys: a repeated identical chat completion, with the same key
/// and model, is answered from the app's database instead of the model, so it costs nothing.
/// For pipelines and FAQ bots that ask the same thing; the chat never uses it. A key the
/// gateway would refuse (unknown, blocked, expired, not allowed the model) gets nothing from
/// it: its request goes to the gateway, which refuses it.
/// </summary>
public sealed class AnswerCache(AppDbContext db, ILiteLlm gateway, AnswerCacheState state, IOptionsMonitor<AnswerCacheOptions> options, TimeProvider clock)
{
    /// <summary>Says on each answer it applies to whether it came from the cache: hit or miss.</summary>
    public const string Header = "x-arena-cache";
    public const string Client = "answer-cache";

    public AnswerCacheOptions Options => options.CurrentValue;

    /// <summary>The key's hash as the gateway keeps it: SHA-256 of the key, in hex.</summary>
    public static string HashOf(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>
    /// Whether this request takes part (null: it goes to the gateway as it is), and the answer kept for it.
    /// With <paramref name="read"/> false (Cache-Control: no-cache) nothing is read, but the new answer is kept.
    /// </summary>
    public async Task<CacheLookup?> LookupAsync(string key, JsonObject body, bool read, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.On || body["model"] is not JsonValue m || !m.TryGetValue<string>(out var model) || model.Length == 0)
        {
            return null;
        }
        var keyHash = HashOf(key);
        var (info, optedIn) = await KeyAsync(key, keyHash, o, ct);
        // A key the gateway would refuse: it refuses it, the cache does not answer for it.
        if (info is null || info.Blocked || info.Expires < clock.GetUtcNow() || !(info.Models.Count == 0 || info.Models.Contains(model) || info.Models.Contains("all-proxy-models")))
        {
            return null;
        }
        if (o.AnswerCache == AnswerCacheModes.OptIn && !optedIn)
        {
            return null;
        }
        var hash = HashOf(keyHash + "\n" + Canonical(body));
        if (!read)
        {
            return new CacheLookup(hash, keyHash, model, null);
        }
        var now = clock.GetUtcNow();
        var kept = await db.CachedAnswers.AsNoTracking().Where(a => a.Hash == hash && a.ExpiresAt > now).Select(a => new { a.Id, a.Response }).FirstOrDefaultAsync(ct);
        if (kept is null)
        {
            return new CacheLookup(hash, keyHash, model, null);
        }
        await db.CachedAnswers.Where(a => a.Id == kept.Id).ExecuteUpdateAsync(x => x.SetProperty(a => a.Hits, a => a.Hits + 1), ct);
        return new CacheLookup(hash, keyHash, model, JsonNode.Parse(kept.Response) as JsonObject);
    }

    /// <summary>Keeps a complete answer for the cache's time; an answer kept before under the same hash is replaced.</summary>
    public async Task StoreAsync(CacheLookup lookup, JsonObject answer, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var usage = answer["usage"] as JsonObject;
        var tokens = (Int(usage, "prompt_tokens") ?? 0) + (Int(usage, "completion_tokens") ?? 0);
        await db.CachedAnswers.Where(a => a.Hash == lookup.Hash).ExecuteDeleteAsync(ct);
        db.CachedAnswers.Add(new CachedAnswer
        {
            Hash = lookup.Hash, KeyHash = lookup.KeyHash, Model = lookup.Model.Length > 200 ? lookup.Model[..200] : lookup.Model,
            Response = answer.ToJsonString(), Tokens = tokens, CreatedAt = now, ExpiresAt = now + options.CurrentValue.AnswerCacheTtl,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The same request answered twice at once: one of them is kept.
        }
        if (now - state.LastCleared > TimeSpan.FromMinutes(10))
        {
            state.LastCleared = now;
            await db.CachedAnswers.Where(a => a.ExpiresAt < now).ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>Forgets what the gateway said of a key: the next request asks again (a person turned the cache on).</summary>
    public void Forget(string keyHash) => state.Keys.TryRemove(keyHash, out _);

    /// <summary>Forgets what the gateway said of these keys and the answers kept for them (a person turned the cache off).</summary>
    public async Task ForgetAsync(IReadOnlyCollection<string> keyHashes, CancellationToken ct)
    {
        foreach (var k in keyHashes)
        {
            Forget(k);
        }
        await db.CachedAnswers.Where(a => keyHashes.Contains(a.KeyHash)).ExecuteDeleteAsync(ct);
    }

    /// <summary>What the gateway says of a key, and whether its person turned the cache on; believed for a few seconds.</summary>
    private async Task<(GatewayKeyInfo? Key, bool OptedIn)> KeyAsync(string key, string keyHash, AnswerCacheOptions o, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (state.Keys.TryGetValue(keyHash, out var known) && now - known.At < o.AnswerCacheKeyCheck)
        {
            return (known.Key, known.OptedIn);
        }
        GatewayKeyInfo? info;
        try
        {
            info = await gateway.KeyInfoAsync(key, ct);
        }
        catch (GatewayException)
        {
            // The gateway cannot say: the request goes to it, as without the cache.
            return (null, false);
        }
        var optedIn = false;
        if (info?.UserId is { Length: > 0 } email)
        {
            var normalized = email.ToUpperInvariant();
            optedIn = await db.Users.AsNoTracking().Where(u => u.NormalizedEmail == normalized).Select(u => u.CacheApiAnswers).FirstOrDefaultAsync(ct);
        }
        if (state.Keys.Count > 10_000)
        {
            state.Keys.Clear();
        }
        state.Keys[keyHash] = (info, optedIn, now);
        return (info, optedIn);
    }

    /// <summary>The request as the exact match sees it: properties in order, without stream, stream_options and user (who asks, and how it is delivered, do not change the answer).</summary>
    public static string Canonical(JsonObject body)
    {
        var copy = (JsonObject)body.DeepClone();
        copy.Remove("stream");
        copy.Remove("stream_options");
        copy.Remove("user");
        return Sorted(copy)!.ToJsonString();
    }

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray a => new JsonArray([.. a.Select(Sorted)]),
        _ => node?.DeepClone(),
    };

    /// <summary>
    /// The chat.completion a gateway response holds: the JSON itself, or one put together from the
    /// stream's chunks (words, thinking, tool calls, usage). Null when it is not a complete answer:
    /// an error, a stream cut short, a choice without its finish.
    /// </summary>
    public static JsonObject? Assemble(string text, bool streamed)
    {
        if (!streamed)
        {
            JsonObject? whole;
            try
            {
                whole = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
            return whole?["choices"] is JsonArray { Count: > 0 } all && all.All(c => c?["finish_reason"] is JsonValue) ? whole : null;
        }
        string? id = null, model = null;
        long created = 0;
        JsonObject? usage = null;
        var done = false;
        var choices = new SortedDictionary<int, Choice>();
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }
            var data = line[5..].Trim();
            if (data == "[DONE]")
            {
                done = true;
                break;
            }
            JsonObject? chunk;
            try
            {
                chunk = JsonNode.Parse(data) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }
            if (chunk is null)
            {
                continue;
            }
            if (chunk["error"] is not null)
            {
                return null;
            }
            id ??= Str(chunk, "id");
            model ??= Str(chunk, "model");
            if (created == 0 && chunk["created"] is JsonValue c && c.TryGetValue<long>(out var at))
            {
                created = at;
            }
            if (chunk["usage"] is JsonObject u)
            {
                usage = (JsonObject)u.DeepClone();
            }
            foreach (var part in (chunk["choices"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var index = Int(part, "index") ?? 0;
                if (!choices.TryGetValue(index, out var choice))
                {
                    choices[index] = choice = new Choice();
                }
                if (part["delta"] is JsonObject delta)
                {
                    choice.Content.Append(Str(delta, "content"));
                    choice.Reasoning.Append(Str(delta, "reasoning_content"));
                    foreach (var call in (delta["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        var at2 = Int(call, "index") ?? 0;
                        if (!choice.Calls.TryGetValue(at2, out var whole))
                        {
                            choice.Calls[at2] = whole = new JsonObject { ["id"] = null, ["type"] = "function", ["function"] = new JsonObject { ["name"] = null, ["arguments"] = "" } };
                        }
                        if (Str(call, "id") is { } callId)
                        {
                            whole["id"] = callId;
                        }
                        if (call["function"] is JsonObject f)
                        {
                            if (Str(f, "name") is { } name)
                            {
                                whole["function"]!["name"] = name;
                            }
                            whole["function"]!["arguments"] = whole["function"]!["arguments"]!.GetValue<string>() + Str(f, "arguments");
                        }
                    }
                }
                if (Str(part, "finish_reason") is { } finish)
                {
                    choice.Finish = finish;
                }
            }
        }
        if (!done || choices.Count == 0 || choices.Values.Any(c => c.Finish is null))
        {
            return null;
        }
        var list = new JsonArray();
        foreach (var (index, choice) in choices)
        {
            var message = new JsonObject { ["role"] = "assistant", ["content"] = choice.Content.Length > 0 || choice.Calls.Count == 0 ? choice.Content.ToString() : null };
            if (choice.Reasoning.Length > 0)
            {
                message["reasoning_content"] = choice.Reasoning.ToString();
            }
            if (choice.Calls.Count > 0)
            {
                message["tool_calls"] = new JsonArray([.. choice.Calls.Values]);
            }
            list.Add(new JsonObject { ["index"] = index, ["message"] = message, ["finish_reason"] = choice.Finish });
        }
        return new JsonObject
        {
            ["id"] = id ?? "chatcmpl-" + Guid.NewGuid().ToString("N"), ["object"] = "chat.completion", ["created"] = created, ["model"] = model,
            ["choices"] = list, ["usage"] = usage,
        };
    }

    /// <summary>A kept answer as it goes back: whole, or as the gateway streams it (its words in one piece, the finish, then [DONE]). Nothing was spent: the usage is zero.</summary>
    public static string Render(JsonObject answer, bool streamed, bool includeUsage)
    {
        var zero = new JsonObject { ["prompt_tokens"] = 0, ["completion_tokens"] = 0, ["total_tokens"] = 0 };
        if (!streamed)
        {
            var whole = (JsonObject)answer.DeepClone();
            whole["usage"] = zero;
            return whole.ToJsonString();
        }
        var sb = new StringBuilder();
        JsonObject Chunk(JsonArray choices) => new()
        {
            ["id"] = answer["id"]?.DeepClone(), ["object"] = "chat.completion.chunk", ["created"] = answer["created"]?.DeepClone(), ["model"] = answer["model"]?.DeepClone(),
            ["choices"] = choices,
        };
        void Write(JsonObject chunk) => sb.Append("data: ").Append(chunk.ToJsonString()).Append("\n\n");
        foreach (var choice in (answer["choices"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var message = choice["message"] as JsonObject ?? [];
            var delta = new JsonObject { ["role"] = "assistant", ["content"] = message["content"]?.DeepClone() };
            if (message["reasoning_content"] is { } reasoning)
            {
                delta["reasoning_content"] = reasoning.DeepClone();
            }
            if (message["tool_calls"] is JsonArray calls)
            {
                delta["tool_calls"] = new JsonArray([.. calls.Select((c, i) =>
                {
                    var call = (JsonObject)c!.DeepClone();
                    call["index"] = i;
                    return (JsonNode)call;
                })]);
            }
            Write(Chunk([new JsonObject { ["index"] = choice["index"]?.DeepClone(), ["delta"] = delta, ["finish_reason"] = null }]));
            Write(Chunk([new JsonObject { ["index"] = choice["index"]?.DeepClone(), ["delta"] = new JsonObject(), ["finish_reason"] = choice["finish_reason"]?.DeepClone() }]));
        }
        if (includeUsage)
        {
            var last = Chunk([]);
            last["usage"] = zero;
            Write(last);
        }
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    private sealed class Choice
    {
        public StringBuilder Content { get; } = new();
        public StringBuilder Reasoning { get; } = new();
        public SortedDictionary<int, JsonObject> Calls { get; } = [];
        public string? Finish { get; set; }
    }

    private static string? Str(JsonObject? o, string name) => o?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonObject? o, string name) => o?[name] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
}

/// <summary>
/// The OpenAI chat completions of API keys, by way of the app while the answer cache is on:
/// Traefik sends gateway.DOMAIN's /v1/chat/completions here while GET /v1/answer-cache says so
/// (its health check), and straight to LiteLLM otherwise or when the app is down. Everything the
/// cache does not answer goes on to LiteLLM as it came, with the caller's own key, and streams back.
/// </summary>
public static class AnswerCacheEndpoints
{
    /// <summary>Requests larger than this go to the gateway without the cache (a question with pictures).</summary>
    private const int MostCachedRequest = 1024 * 1024;
    /// <summary>Answers larger than this are not kept.</summary>
    private const int MostCachedAnswer = 4 * 1024 * 1024;

    /// <summary>Not passed on: they belong to one connection, or to the app's own site.</summary>
    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Proxy-Connection", "Proxy-Authorization",
        "Accept-Encoding", "Content-Length", "Content-Type", "Cookie", "X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Original-For", "X-Original-Proto", "X-Original-Host",
    };

    public static IServiceCollection AddAnswerCache(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AnswerCacheOptions>(config.GetSection("Gateway"));
        services.AddSingleton<AnswerCacheState>();
        services.AddScoped<AnswerCache>();
        services.AddHttpClient(AnswerCache.Client, (sp, c) =>
        {
            c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<LiteLlmOptions>>().Value.Url);
            // An answer streams for as long as the gateway allows (its own timeout).
            c.Timeout = Timeout.InfiniteTimeSpan;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });
        return services;
    }

    public static void MapAnswerCache(this IEndpointRouteBuilder app)
    {
        // Traefik's health check: while it fails (the cache off, or the app down), keys go straight to LiteLLM.
        app.MapGet("/v1/answer-cache", (IOptionsMonitor<AnswerCacheOptions> o) =>
            o.CurrentValue.On ? Results.Ok(new { mode = o.CurrentValue.AnswerCache }) : Results.Json(new { mode = AnswerCacheModes.Off }, statusCode: 503));
        app.MapPost("/v1/chat/completions", ProxyAsync).AllowAnonymous().DisableAntiforgery();
        app.MapPost("/chat/completions", ProxyAsync).AllowAnonymous().DisableAntiforgery();

        var me = app.MapGroup("/api/account/answer-cache").RequireAuthorization();
        me.MapGet("", MineAsync);
        me.MapPut("", ChooseAsync);
    }

    /// <summary>The person's own answer: whether their key's repeated requests come from the cache.</summary>
    public sealed record CacheChoice(bool On);

    private static async Task<IResult> MineAsync(ClaimsPrincipal p, UserManager<AppUser> users, IOptionsMonitor<AnswerCacheOptions> options)
    {
        var me = (await users.GetUserAsync(p))!;
        return Results.Ok(Mine(me, options.CurrentValue));
    }

    private static object Mine(AppUser me, AnswerCacheOptions o) => new
    {
        mode = o.On ? o.AnswerCache : AnswerCacheModes.Off,
        chosen = me.CacheApiAnswers,
        on = o.AnswerCache == AnswerCacheModes.All || (o.AnswerCache == AnswerCacheModes.OptIn && me.CacheApiAnswers),
        ttlHours = Math.Round(o.AnswerCacheTtl.TotalHours, 2),
    };

    private static async Task<IResult> ChooseAsync(CacheChoice body, ClaimsPrincipal p, UserManager<AppUser> users, ILiteLlm gateway, AnswerCache cache, Audit audit,
        IOptionsMonitor<AnswerCacheOptions> options, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        if (me.CacheApiAnswers != body.On)
        {
            me.CacheApiAnswers = body.On;
            await users.UpdateAsync(me);
            await audit.WriteAsync("account.answer_cache", me.UserName, detail: body.On ? "on" : "off");
        }
        // Off: what was kept for their key goes now, and the next request asks the model.
        try
        {
            var keys = (await gateway.KeysAsync(me.Email!, ct)).Select(k => k.Token).ToList();
            if (body.On)
            {
                foreach (var k in keys)
                {
                    cache.Forget(k);
                }
            }
            else
            {
                await cache.ForgetAsync(keys, ct);
            }
        }
        catch (GatewayException)
        {
            // The answers expire on their own; the choice is saved.
        }
        return Results.Ok(Mine(me, options.CurrentValue));
    }

    private static async Task ProxyAsync(HttpContext http, AnswerCache cache, IHttpClientFactory factory)
    {
        var ct = http.RequestAborted;
        var request = http.Request;
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            // The gateway decides, as when it is reached directly.
            limit.MaxRequestBodySize = null;
        }
        var auth = request.Headers.Authorization.ToString();
        var key = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
        var control = request.Headers.CacheControl.ToString();
        byte[]? body = null;
        JsonObject? json = null;
        CacheLookup? lookup = null;
        if (cache.Options.On && key is { Length: > 0 } && request.ContentLength is > 0 and <= MostCachedRequest && !control.Contains("no-store", StringComparison.OrdinalIgnoreCase))
        {
            body = new byte[request.ContentLength.Value];
            var read = 0;
            int n;
            while (read < body.Length && (n = await request.Body.ReadAsync(body.AsMemory(read), ct)) > 0)
            {
                read += n;
            }
            if (read < body.Length)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            try
            {
                json = JsonNode.Parse(body) as JsonObject;
            }
            catch (JsonException)
            {
                // Not JSON: the gateway says what is wrong with it.
            }
            if (json is not null)
            {
                lookup = await cache.LookupAsync(key, json, read: !control.Contains("no-cache", StringComparison.OrdinalIgnoreCase), ct);
            }
        }
        var streamed = json?["stream"] is JsonValue s && s.TryGetValue<bool>(out var on) && on;
        if (lookup?.Answer is { } kept)
        {
            http.Response.Headers[AnswerCache.Header] = "hit";
            var includeUsage = json?["stream_options"]?["include_usage"] is JsonValue u && u.TryGetValue<bool>(out var yes) && yes;
            http.Response.ContentType = streamed ? "text/event-stream" : "application/json";
            await http.Response.WriteAsync(AnswerCache.Render(kept, streamed, includeUsage), ct);
            return;
        }

        using var forward = new HttpRequestMessage(HttpMethod.Post, new Uri(request.Path + request.QueryString, UriKind.Relative))
        {
            Content = body is not null ? new ByteArrayContent(body) : new StreamContent(request.Body),
        };
        foreach (var (name, values) in request.Headers)
        {
            if (!Dropped.Contains(name))
            {
                forward.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }
        if (request.ContentType is { } type)
        {
            forward.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        }
        if (request.ContentLength is { } length)
        {
            forward.Content.Headers.ContentLength = length;
        }
        forward.Headers.TryAddWithoutValidation("X-Forwarded-For", http.Connection.RemoteIpAddress?.ToString());
        forward.Headers.TryAddWithoutValidation("X-Forwarded-Proto", request.Scheme);
        forward.Headers.TryAddWithoutValidation("X-Forwarded-Host", request.Host.Value);

        HttpResponseMessage res;
        try
        {
            res = await factory.CreateClient(AnswerCache.Client).SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
            await http.Response.WriteAsJsonAsync(new { error = new { message = "The model gateway is not reachable right now.", type = "gateway_unreachable", code = "502" } }, ct);
            return;
        }
        using (res)
        {
            http.Response.StatusCode = (int)res.StatusCode;
            foreach (var (name, values) in res.Headers.Concat(res.Content.Headers))
            {
                if (name is not ("Transfer-Encoding" or "Connection" or "Keep-Alive"))
                {
                    http.Response.Headers[name] = values.ToArray();
                }
            }
            if (lookup is not null)
            {
                http.Response.Headers[AnswerCache.Header] = "miss";
            }
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            var keep = lookup is not null && res.IsSuccessStatusCode ? new MemoryStream() : null;
            await using var upstream = await res.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[16 * 1024];
            int n;
            // Each piece goes on as it comes: a streamed answer reaches the caller word by word.
            while ((n = await upstream.ReadAsync(buffer, ct)) > 0)
            {
                await http.Response.Body.WriteAsync(buffer.AsMemory(0, n), ct);
                await http.Response.Body.FlushAsync(ct);
                if (keep is not null)
                {
                    if (keep.Length + n > MostCachedAnswer)
                    {
                        keep = null;
                    }
                    else
                    {
                        keep.Write(buffer, 0, n);
                    }
                }
            }
            var sse = res.Content.Headers.ContentType?.MediaType == "text/event-stream" || streamed;
            if (keep is not null && AnswerCache.Assemble(Encoding.UTF8.GetString(keep.ToArray()), sse) is { } answer)
            {
                await cache.StoreAsync(lookup!, answer, CancellationToken.None);
            }
        }
    }
}
