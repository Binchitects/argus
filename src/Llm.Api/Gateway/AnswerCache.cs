using System.Collections.Concurrent;
using Llm.Api.Access;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Chat;
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
    /// <summary>Gateway:LogPrompts: each API request's last question and answer kept in the prompt log.</summary>
    public bool LogPrompts { get; set; } = true;

    /// <summary>Gateway:PromptLogKeep: how many characters of each question and answer are kept.</summary>
    public int PromptLogKeep { get; set; } = Llm.Core.Chat.PromptLogEntry.MaxText;

    /// <summary>Gateway:PromptLogPictures: a question's pictures kept with it.</summary>
    public bool PromptLogPictures { get; set; } = true;

    /// <summary>Gateway:PromptLogModels: only these models' requests kept (one a line or comma); empty: every model's.</summary>
    public string? PromptLogModels { get; set; }

    /// <summary>A list setting's items: one a line or comma, trimmed, empty ones dropped.</summary>
    public static List<string> Split(string? list) =>
        [.. (list ?? "").Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>off; opt-in: the keys of people who turned it on (Your account); all: every key.</summary>
    public string AnswerCache { get; set; } = AnswerCacheModes.Off;
    /// <summary>How long an answer is kept.</summary>
    public TimeSpan AnswerCacheTtl { get; set; } = TimeSpan.FromDays(1);

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

/// <summary>What the cache remembers between requests: when old answers were last cleared.</summary>
public sealed class AnswerCacheState
{
    public DateTimeOffset LastCleared { get; set; }
}

/// <summary>
/// The answer cache for API keys: a repeated identical chat completion, with the same key
/// and model, is answered from the app's database instead of the model, so it costs nothing.
/// For pipelines and FAQ bots that ask the same thing; the chat never uses it. A key the
/// gateway would refuse (unknown, blocked, expired, not allowed the model) gets nothing from
/// it: its request goes to the gateway, which refuses it.
/// </summary>
public sealed class AnswerCache(AppDbContext db, KeyBook book, AnswerCacheState state, IOptionsMonitor<AnswerCacheOptions> options, TimeProvider clock)
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
        var info = await book.ForHashAsync(keyHash, ct);
        // A key the gateway would refuse: it refuses it, the cache does not answer for it.
        var optedIn = info?.CacheApiAnswers == true;
        if (info is null || info.Disabled || info.ApiOff || info.ExpiredAt(clock.GetUtcNow()) || !(info.Models.Count == 0 || info.Models.Contains(model)))
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
    public void Forget(string keyHash) => book.Changed(keyHash);

    /// <summary>Forgets what the gateway said of these keys and the answers kept for them (a person turned the cache off).</summary>
    public async Task ForgetAsync(IReadOnlyCollection<string> keyHashes, CancellationToken ct)
    {
        foreach (var k in keyHashes)
        {
            Forget(k);
        }
        await db.CachedAnswers.Where(a => keyHashes.Contains(a.KeyHash)).ExecuteDeleteAsync(ct);
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
    /// an error, a stream cut short, a choice without its finish. <paramref name="cut"/>: a stream cut
    /// short (its caller went away, an admin stopped it), put together as far as it went, for the prompt log.
    /// </summary>
    public static JsonObject? Assemble(string text, bool streamed, bool cut = false)
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
        if (choices.Count == 0 || (!cut && (!done || choices.Values.Any(c => c.Finish is null))))
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
/// The completions of API keys, by way of the app: Traefik sends gateway.DOMAIN's /v1/chat/completions
/// (and the other ways agents ask) here whenever the app is up (/healthz), and straight to LiteLLM
/// when it is down. The key's line, the fair line, the prompt log and the answer cache (when on) are
/// here; everything the cache does not answer goes on to LiteLLM as it came, with the caller's own
/// key, and streams back.
/// </summary>
public static class AnswerCacheEndpoints
{
    /// <summary>Requests larger than this go to the gateway without the cache (a question with pictures).</summary>
    private const int MostCachedRequest = 1024 * 1024;
    /// <summary>Answers larger than this are not kept.</summary>
    private const int MostCachedAnswer = 4 * 1024 * 1024;

    // The most of an answer the prompt log reads (it keeps 20,000 characters of it; a stream carries more than its words).
    private const int MostLoggedAnswer = 8 * 1024 * 1024;

    /// <summary>The error's type when an admin took the request out of the line or stopped it (Admin → Line).</summary>
    public const string RemovedType = "removed_by_admin";

    /// <summary>Not passed on: they belong to one connection, or to the app's own site.</summary>
    internal static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Proxy-Connection", "Proxy-Authorization",
        "Accept-Encoding", "Content-Length", "Content-Type", "Cookie", "X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Original-For", "X-Original-Proto", "X-Original-Host",
    };

    public static IServiceCollection AddAnswerCache(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AnswerCacheOptions>(config.GetSection("Gateway"));
        services.AddSingleton<AnswerCacheState>();
        services.AddScoped<AnswerCache>();
        services.AddSingleton<KeyLine>();
        services.AddSingleton<OwnKeyPlaces>();
        services.AddSingleton<KeyBook>();
        services.AddSingleton<KeyPeople>();
        services.AddSingleton<PromptLogWriter>();
        services.AddScoped<Admission>();
        services.AddScoped<RateWindows>();
        services.Configure<Tags.TagOptions>(config.GetSection("Tags"));
        services.AddScoped<Tags.TagSummaries>();
        services.AddScoped<OwnKeys>();
        services.AddScoped<ApiKeys>();
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
        // A browser's preflight (OPTIONS) is answered too: a web page may call the API with its key.
        app.MapMethods("/v1/chat/completions", ["POST", "OPTIONS"], ProxyAsync).AllowAnonymous("The OpenAI-style API: the API key is checked.").DisableAntiforgery();
        app.MapMethods("/chat/completions", ["POST", "OPTIONS"], ProxyAsync).AllowAnonymous("The OpenAI-style API: the API key is checked.").DisableAntiforgery();
        // The other ways agents ask (Anthropic's messages, OpenAI's responses and completions): through the
        // key's line too, not cached.
        foreach (var path in new[] { "/v1/messages", "/messages", "/v1/responses", "/responses", "/v1/completions", "/completions" })
        {
            // A browser's preflight (OPTIONS) is passed on as it is.
            app.MapMethods(path, ["POST", "OPTIONS"], ProxyAsync).AllowAnonymous("The OpenAI- and Anthropic-style API: the API key is checked.").DisableAntiforgery();
        }

        var me = app.MapGroup("/api/account/answer-cache").PersonOnly();
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

    private static async Task<IResult> ChooseAsync(CacheChoice body, ClaimsPrincipal p, UserManager<AppUser> users, ApiKeys apiKeys, AnswerCache cache, Audit audit,
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
        var keys = (await apiKeys.OfAsync(me.Id, ct)).Select(k => k.Hash.TrimEnd()).ToList();
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
        return Results.Ok(Mine(me, options.CurrentValue));
    }

    /// <summary>The longest request read here to pin an agent's session to a slot; a larger one goes as it is.</summary>
    private const int MostPinnedRequest = 64 * 1024 * 1024;

    /// <summary>
    /// An agent's session through an API key, as the engine's slots know conversations: its key, its model, and how its
    /// conversation starts (the system prompt and the first question), which stay the same from turn to turn.
    /// </summary>
    public static Guid? SessionOf(string key, JsonObject request)
    {
        if (request["model"] is not JsonValue mv || !mv.TryGetValue<string>(out var model) || request["messages"] is not JsonArray messages)
        {
            return null;
        }
        var start = messages.OfType<JsonObject>().Take(2).Select(m => m["content"]?.ToJsonString() ?? "").ToList();
        if (start.Count < 2)
        {
            // One message: no session to speak of yet.
            return null;
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u0001', [key, model, .. start])));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static async Task ProxyAsync(HttpContext http, AnswerCache cache, IHttpClientFactory factory, KeyLine line, Chat.EngineRoute route, Chat.AnswerGate gate, KeyPeople people,
        PromptLogWriter prompts)
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
        // Anthropic's clients send the key as x-api-key.
        key ??= request.Headers["x-api-key"].ToString() is { Length: > 0 } x ? x.Trim() : null;
        var completions = request.Path.Value?.EndsWith("/chat/completions", StringComparison.Ordinal) == true && HttpMethods.IsPost(request.Method);
        var control = request.Headers.CacheControl.ToString();
        byte[]? body = null;
        JsonObject? json = null;
        CacheLookup? lookup = null;
        // The app's own gateway (Gateway:Mode own): the request read whole and let in here (Admission), then sent to the
        // engine by ModelGateway instead of LiteLLM, booked in the ledger.
        var own = http.RequestServices.GetRequiredService<ModelGateway>();
        if (KeyMedia.Preflight(http))
        {
            return;
        }
        // Anthropic's Messages API, turned into a chat completion here and its answer back into Anthropic's shapes.
        var anthropic = own.ForApi && HttpMethods.IsPost(request.Method) && request.Path.Value is "/v1/messages" or "/messages";
        var ownMode = (completions || anthropic) && own.ForApi;
        if (ownMode)
        {
            http.Response.Headers.AccessControlAllowOrigin = "*";
        }
        Task FailAsync(int status, string type, string message, int? retryAfter = null, IReadOnlyDictionary<string, string>? headers = null) =>
            anthropic ? AnthropicErrorAsync(http, status, message, retryAfter, headers, ct) : OwnErrorAsync(http, status, type, message, retryAfter, headers, ct);
        Admitted? admitted = null;
        if (ownMode)
        {
            using var whole = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int got;
            while ((got = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (whole.Length + got > MostPinnedRequest)
                {
                    await FailAsync(413, "invalid_request_error", "The request is too large.");
                    return;
                }
                whole.Write(chunk, 0, got);
            }
            body = whole.ToArray();
            try
            {
                json = JsonNode.Parse(body) as JsonObject;
            }
            catch (JsonException)
            {
                json = null;
            }
            if (json is null)
            {
                await FailAsync(400, "invalid_request_error", "The request is not a JSON object.");
                return;
            }
            if (anthropic)
            {
                try
                {
                    json = Protocols.AnthropicMessages.ToChat(json);
                }
                catch (Protocols.AdapterException ex)
                {
                    await FailAsync(ex.Status, "invalid_request_error", ex.Message);
                    return;
                }
                completions = true;
            }
            var (letIn, refusal) = await http.RequestServices.GetRequiredService<Admission>().CheckAsync(key, json, ct);
            if (refusal is not null)
            {
                var holder = key is { Length: > 0 } ? await http.RequestServices.GetRequiredService<KeyBook>().ForAsync(key, ct) : null;
                if (holder is not null)
                {
                    own.Refused(new GatewayCaller(holder.Email, "api", KeyHash: holder.Hash, KeyAlias: holder.Alias), json["model"]?.ToString(), refusal.Status, refusal.Reason, refusal.Message);
                    if (cache.Options.LogPrompts)
                    {
                        prompts.Begin(holder.UserId, holder.Hash, json)?.End(PromptLogEntry.Refused, refusal.Status, error: refusal.Message);
                    }
                }
                await FailAsync(refusal.Status, refusal.Type, refusal.Message, refusal.RetryAfter, refusal.Headers);
                return;
            }
            admitted = letIn;
            // A text the safeguards masked goes masked.
            body = Encoding.UTF8.GetBytes(json.ToJsonString());
            if (!anthropic && cache.Options.On && !control.Contains("no-store", StringComparison.OrdinalIgnoreCase))
            {
                lookup = await cache.LookupAsync(key!, json, read: !control.Contains("no-cache", StringComparison.OrdinalIgnoreCase), ct);
            }
        }
        if (completions && body is null && cache.Options.On && key is { Length: > 0 } && request.ContentLength is > 0 and <= MostCachedRequest && !control.Contains("no-store", StringComparison.OrdinalIgnoreCase))
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
        // An agent's chat completions to a model of this engine: read whole (when not already), to pin its session.
        if (completions && body is null && key is { Length: > 0 } && request.ContentLength is > 0 and <= MostPinnedRequest)
        {
            body = new byte[request.ContentLength.Value];
            var got = 0;
            int r;
            while (got < body.Length && (r = await request.Body.ReadAsync(body.AsMemory(got), ct)) > 0)
            {
                got += r;
            }
            if (got < body.Length)
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
        }
        var streamed = json?["stream"] is JsonValue s && s.TryGetValue<bool>(out var on) && on;
        if (lookup?.Answer is { } kept)
        {
            http.Response.Headers[AnswerCache.Header] = "hit";
            var includeUsage = json?["stream_options"]?["include_usage"] is JsonValue u && u.TryGetValue<bool>(out var yes) && yes;
            http.Response.ContentType = streamed ? "text/event-stream" : "application/json";
            await http.Response.WriteAsync(AnswerCache.Render(kept, streamed, includeUsage), ct);
            // Answered from the cache, and in the prompt log all the same: the person asked it and got this.
            if (cache.Options.LogPrompts && completions && json is not null && await people.ForAsync(key!, ct) is { } cached)
            {
                prompts.Record(cached.Id, AnswerCache.HashOf(key!), json, kept, StatusCodes.Status200OK);
            }
            return;
        }

        // The prompt log (Gateway:LogPrompts): the request from its arrival, whatever becomes of it.
        var logged = cache.Options.LogPrompts && completions && json is not null && key is { Length: > 0 } && await people.ForAsync(key, ct) is { } asker
            ? prompts.Begin(asker.Id, AnswerCache.HashOf(key), json)
            : null;
        using var _closing = logged?.Closing(ct);
        // again: worth asking again in a while (Retry-After); not after an admin's removal.
        async Task RefuseAsync(int status, string message, string type, bool again = true)
        {
            logged?.End(PromptLogEntry.Refused, status, error: message);
            http.Response.StatusCode = status;
            if (again)
            {
                http.Response.Headers.RetryAfter = "30";
            }
            await http.Response.WriteAsJsonAsync(new { error = new { message, type, code = status.ToString(System.Globalization.CultureInfo.InvariantCulture) } }, ct);
        }

        // The key's turn: as many at once as it may have, the rest waiting in order (never refused at once).
        IDisposable? place;
        try
        {
            place = key is { Length: > 0 } ? await line.EnterAsync(key, ct) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logged?.End(PromptLogEntry.Stopped, 499, error: "The caller went away while it waited for its turn.");
            throw;
        }
        using var _place = place;
        if (key is { Length: > 0 } && place is null)
        {
            await RefuseAsync(StatusCodes.Status429TooManyRequests, $"This API key's requests waited longer than {line.Timeout.TotalMinutes:0} minutes for their turn: {line.PlacesFor(key)} are answered at once. Try again in a moment.", "rate_limit_error");
            return;
        }
        // Its turn in the model's line, with the chat's answers: the model's places, everyone's (Chat:AnswersAtOnce), the
        // person's own when set (Admin → People or Groups); a line already as long as it may be refuses it at once.
        Chat.AnswerGate.Place? turn = null;
        if (completions && key is { Length: > 0 } && json?["model"] is JsonValue gm && gm.TryGetValue<string>(out var lineModel) && await people.ForAsync(key, ct) is { } who)
        {
            try
            {
                turn = await gate.EnterAsync(who.Id, who.Priority, lineModel, _ => Task.CompletedTask, ct, who.AtOnce ?? 0, kind: "api");
            }
            catch (Chat.AnswerGate.BusyException ex)
            {
                await RefuseAsync(StatusCodes.Status503ServiceUnavailable, ex.Message, "service_busy");
                return;
            }
            catch (Chat.AnswerGate.TooLongException ex)
            {
                await RefuseAsync(StatusCodes.Status429TooManyRequests, ex.Message, "rate_limit_error");
                return;
            }
            catch (Chat.AnswerGate.RemovedException ex)
            {
                // Taken out of the line by an admin: 403, as the clients ask again at once after a 429 or a 5xx.
                await RefuseAsync(StatusCodes.Status403Forbidden, ex.Message, RemovedType, again: false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logged?.End(PromptLogEntry.Stopped, 499, error: "The caller went away while it waited for its turn.");
                throw;
            }
        }
        using var _turn = turn;
        // An admin's stop (Admin → Line) ends the request as the caller going away would, and the caller is told why.
        using var stopping = turn is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, turn.Stopped);
        var work = stopping?.Token ?? ct;
        bool StoppedByAdmin() => turn?.StopReason is not null && !ct.IsCancellationRequested;
        // completion: the answer as far as it went out, for the prompt log.
        async Task StopAsync(bool sse, JsonObject? completion = null)
        {
            var why = Chat.AnswerGate.StoppedBy(turn!.StopReason!);
            logged?.End(PromptLogEntry.Stopped, StatusCodes.Status403Forbidden, error: why, completion: completion);
            if (!http.Response.HasStarted)
            {
                http.Response.Clear();
                await RefuseAsync(StatusCodes.Status403Forbidden, why, RemovedType, again: false);
            }
            else if (sse)
            {
                // Under way: an error chunk, as the gateway sends one; the clients raise it.
                var error = JsonSerializer.Serialize(new { error = new { message = why, type = RemovedType, code = "403" } });
                await http.Response.WriteAsync($"data: {error}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }
            else
            {
                http.Abort();
            }
        }
        logged?.Running();
        // The agent's session goes back to the engine slot that holds its start (and its cache, from disk when it lost it),
        // as a chat's conversation does: each turn reads only what is new. A model that cannot be made ready says so (503,
        // try again), not a bare error; and the request goes to the copy, or the fallback, the slot was taken on.
        Chat.EngineRoute.Sending? sending = null;
        if (completions && key is { Length: > 0 } && json?["model"] is JsonValue pm && pm.TryGetValue<string>(out var pinned) && SessionOf(key, json) is { } session)
        {
            try
            {
                sending = await route.TakeAsync(pinned, session, work);
            }
            catch (Chat.ChatGatewayException ex) when (!ct.IsCancellationRequested)
            {
                await RefuseAsync(StatusCodes.Status503ServiceUnavailable, ex.Message, "model_not_ready");
                return;
            }
            catch (OperationCanceledException) when (StoppedByAdmin())
            {
                await StopAsync(sse: false);
                return;
            }
        }
        using var _sending = sending;
        if (json is not null && (sending?.Slot is not null || sending?.Model is not null))
        {
            if (sending.Slot is { } slotId)
            {
                json["id_slot"] = slotId;
            }
            if (sending.Model is { } copy)
            {
                json["model"] = copy;
            }
            body = Encoding.UTF8.GetBytes(json.ToJsonString());
        }
        using var forward = new HttpRequestMessage(new HttpMethod(request.Method), new Uri(request.Path + request.QueryString, UriKind.Relative))
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
        if (body is not null)
        {
            forward.Content.Headers.ContentLength = body.Length;
        }
        else if (request.ContentLength is { } length)
        {
            forward.Content.Headers.ContentLength = length;
        }
        forward.Headers.TryAddWithoutValidation("X-Forwarded-For", http.Connection.RemoteIpAddress?.ToString());
        forward.Headers.TryAddWithoutValidation("X-Forwarded-Proto", request.Scheme);
        forward.Headers.TryAddWithoutValidation("X-Forwarded-Host", request.Host.Value);

        HttpResponseMessage res;
        try
        {
            res = ownMode && admitted is not null
                ? await own.ChatAsync(json!, OwnCaller(admitted, json!, http.RequestServices.GetRequiredService<IServiceScopeFactory>()),
                    route.FallbackOf(json!["model"]?.ToString()) is { } fb ? [fb] : [], work, passErrors: true)
                : await factory.CreateClient(AnswerCache.Client).SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, work);
        }
        catch (Chat.ChatGatewayException ex) when (ownMode)
        {
            // The engine refused or could not be reached: the caller gets its words, in the OpenAI shape.
            var status = ex.Status ?? StatusCodes.Status502BadGateway;
            logged?.End(PromptLogEntry.Failed, status, error: ex.Message);
            await FailAsync(status, status == 404 ? "model_not_found" : status >= 500 ? "upstream_error" : "invalid_request_error", ex.Message);
            return;
        }
        catch (OperationCanceledException) when (StoppedByAdmin())
        {
            await StopAsync(sse: false);
            return;
        }
        catch (HttpRequestException)
        {
            logged?.End(PromptLogEntry.Failed, StatusCodes.Status502BadGateway, error: "The model gateway is not reachable right now.");
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
            // Anthropic's caller: its events as the chat completion's stream comes, or its message (or error) at the end.
            var claude = anthropic ? new Protocols.AnthropicMessages.Events(json!["model"]?.ToString() ?? "") : null;
            var claudeStreams = anthropic && res.IsSuccessStatusCode && streamed;
            using var claudeWhole = anthropic && !claudeStreams ? new MemoryStream() : null;
            if (anthropic)
            {
                http.Response.ContentType = claudeStreams ? "text/event-stream" : "application/json";
                http.Response.Headers.ContentLength = null;
            }
            var keep = lookup is not null && res.IsSuccessStatusCode ? new MemoryStream() : null;
            // The prompt log: the answer as it went out, whole (or as far as it went), or the error the caller got.
            var written = logged is not null ? new MemoryStream() : null;
            var sse = res.Content.Headers.ContentType?.MediaType == "text/event-stream" || streamed;
            await using var upstream = await res.Content.ReadAsStreamAsync(work);
            var buffer = new byte[16 * 1024];
            int n;
            try
            {
                // Each piece goes on as it comes: a streamed answer reaches the caller word by word.
                while ((n = await upstream.ReadAsync(buffer, work)) > 0)
                {
                    if (claudeStreams)
                    {
                        await http.Response.WriteAsync(claude!.Feed(buffer.AsSpan(0, n)), ct);
                    }
                    else if (claudeWhole is not null)
                    {
                        claudeWhole.Write(buffer, 0, n);
                    }
                    else
                    {
                        await http.Response.Body.WriteAsync(buffer.AsMemory(0, n), ct);
                    }
                    await http.Response.Body.FlushAsync(ct);
                    if (written is not null && written.Length + n <= MostLoggedAnswer)
                    {
                        written.Write(buffer, 0, n);
                    }
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
            }
            catch (Exception ex) when ((ex is OperationCanceledException or IOException) && StoppedByAdmin())
            {
                // Stopped by an admin while it answered: in the prompt log as stopped, with why and what had gone out.
                await StopAsync(sse, written is not null && res.IsSuccessStatusCode ? AnswerCache.Assemble(Encoding.UTF8.GetString(written.ToArray()), sse, cut: true) : null);
                return;
            }
            catch (Exception ex) when (logged is not null && (ex is OperationCanceledException or IOException || ct.IsCancellationRequested))
            {
                // The caller went away (or the gateway broke off): what had come is kept.
                var partial = written is not null && res.IsSuccessStatusCode ? AnswerCache.Assemble(Encoding.UTF8.GetString(written.ToArray()), sse, cut: true) : null;
                logged.End(ct.IsCancellationRequested ? PromptLogEntry.Stopped : PromptLogEntry.Failed, ct.IsCancellationRequested ? 499 : (int)res.StatusCode,
                    error: ct.IsCancellationRequested ? "The caller went away before the answer was whole." : $"The answer broke off: {ex.Message}", completion: partial);
                throw;
            }
            if (claudeStreams)
            {
                await http.Response.WriteAsync(claude!.End(), ct);
            }
            else if (claudeWhole is not null)
            {
                var said = Encoding.UTF8.GetString(claudeWhole.ToArray());
                string message;
                if (res.IsSuccessStatusCode && (sse ? AnswerCache.Assemble(said, true) : JsonNode.Parse(said) as JsonObject) is { } completion)
                {
                    message = Protocols.AnthropicMessages.FromChat(completion, json!["model"]?.ToString() ?? "").ToJsonString();
                }
                else
                {
                    message = Protocols.AnthropicMessages.Error((int)res.StatusCode, PromptLogWriter.ErrorOf(said) ?? $"The model answered {(int)res.StatusCode}.");
                }
                await http.Response.WriteAsync(message, ct);
            }
            if (logged is not null && written is not null)
            {
                var text = Encoding.UTF8.GetString(written.ToArray());
                if (res.IsSuccessStatusCode)
                {
                    logged.End(PromptLogEntry.Done, (int)res.StatusCode, completion: AnswerCache.Assemble(text, sse));
                }
                else
                {
                    logged.End(PromptLogEntry.Failed, (int)res.StatusCode, error: PromptLogWriter.ErrorOf(text) ?? $"The gateway answered {(int)res.StatusCode}.");
                }
            }
            if (keep is not null && AnswerCache.Assemble(Encoding.UTF8.GetString(keep.ToArray()), sse) is { } answer)
            {
                await cache.StoreAsync(lookup!, answer, CancellationToken.None);
            }
        }
    }

    /// <summary>An error as the OpenAI clients read it, with its Retry-After and rate-limit headers.</summary>
    private static async Task OwnErrorAsync(HttpContext http, int status, string type, string message, int? retryAfter, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        http.Response.StatusCode = status;
        if (retryAfter is { } after)
        {
            http.Response.Headers.RetryAfter = after.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            http.Response.Headers[name] = value;
        }
        await http.Response.WriteAsJsonAsync(new { error = new { message, type, code = status.ToString(System.Globalization.CultureInfo.InvariantCulture) } }, ct);
    }

    /// <summary>
    /// Who an API key's request is for, as the ledger books it: the key's person, a workspace's key as Code Studio's, its tag,
    /// chat and project (Code Arena names them: chat:ID, project:ID); its tokens count to the key's minute once it ends.
    /// </summary>
    private static GatewayCaller OwnCaller(Admitted a, JsonObject json, IServiceScopeFactory scopes)
    {
        var tags = (json["metadata"]?["tags"] as JsonArray ?? []).Select(t => t is JsonValue v && v.TryGetValue<string>(out var x) ? x : null).OfType<string>().ToList();
        Guid? Id(string prefix) => tags.FirstOrDefault(t => t.StartsWith(prefix, StringComparison.Ordinal)) is { } t && Guid.TryParse(t[prefix.Length..], out var g) ? g : null;
        var tag = tags.FirstOrDefault(t => !t.StartsWith(Projects.ProjectTags.ChatPrefix, StringComparison.Ordinal) && !t.StartsWith(Projects.ProjectTags.ProjectPrefix, StringComparison.Ordinal));
        var limited = a.TokensPerMinute is not null;
        return new GatewayCaller(a.Key.Email, a.Key.Kind == Llm.Core.Gateway.ApiKey.Studio ? "studio" : "api", tag, Id(Projects.ProjectTags.ChatPrefix), Id(Projects.ProjectTags.ProjectPrefix),
            a.Key.Hash, a.Key.Alias, limited ? row => _ = Task.Run(async () =>
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<RateWindows>().AddTokensAsync(a.Key.Hash, row.StartedAt,
                    Math.Max(0, row.PromptTokens - row.CachedTokens) + row.CompletionTokens, CancellationToken.None);
            }) : null);
    }

    /// <summary>An error as Anthropic's clients read it, with its Retry-After and rate-limit headers.</summary>
    private static async Task AnthropicErrorAsync(HttpContext http, int status, string message, int? retryAfter, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        http.Response.StatusCode = status;
        if (retryAfter is { } after)
        {
            http.Response.Headers.RetryAfter = after.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            http.Response.Headers[name] = value;
        }
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(Protocols.AnthropicMessages.Error(status, message), ct);
    }
}
