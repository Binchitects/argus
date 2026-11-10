using System.Globalization;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Models;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Gateway;

/// <summary>Why a request was not let in: its status, its error's type, the sentence the caller gets, the ledger's reason, and when to ask again.</summary>
public sealed record AdmissionRefusal(int Status, string Type, string Message, string Reason, int? RetryAfter = null, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>A request let in: its key's holder and person, the model it asks, and how many tokens a minute the key may still use (null: no limit).</summary>
public sealed record Admitted(KeyHolder Key, AppUser User, string Model, int? TokensPerMinute);

/// <summary>
/// The checks an API key's request passes before anything is sent, in order (docs/design/own-gateway.md §1.6): the key
/// known and not expired; its person not disabled and with API access; the model there and theirs to use; the requests
/// and tokens a minute (the person's limits, tightened by the key's own); the key's own 30-day budget; the person's credit
/// of the request's kind; the safeguards (a blocked text refused, a masked one sent masked); room for the model. Each
/// refusal says what the clients already read ("Limit type: requests. Current limit: N", "Budget has been exceeded").
/// </summary>
public sealed class Admission(KeyBook keys, AppDbContext db, Microsoft.AspNetCore.Identity.UserManager<AppUser> users, RateLimits rates, RateWindows windows, Credit credit,
    Safeguards.Safeguards safeguards, EngineRoute route, MediaControl media, ModelGateway gateway, TimeProvider clock)
{
    public async Task<(Admitted? Admitted, AdmissionRefusal? Refusal)> CheckAsync(string? secret, JsonObject body, CancellationToken ct)
    {
        var holder = secret is { Length: > 0 } ? await keys.ForAsync(secret, ct) : null;
        if (holder is null)
        {
            return (null, new(401, "invalid_api_key", "Authentication Error: this API key is not known (Your account → API keys makes one).", "invalid_key"));
        }
        if (holder.ExpiredAt(clock.GetUtcNow()))
        {
            return (null, new(401, "invalid_api_key", "Authentication Error: this API key has expired.", "expired"));
        }
        if (holder.Disabled)
        {
            return (null, new(403, "permission_denied", "This account is disabled.", "disabled"));
        }
        if (holder.ApiOff)
        {
            return (null, new(403, "permission_denied", "Your API access is off. Ask an admin to turn it on (Admin → People).", "api_off"));
        }
        var model = body["model"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : null;
        if (model is null)
        {
            return (null, new(400, "invalid_request_error", "The request names no model.", "no_model"));
        }
        if ((await gateway.RoutesForAsync(model, ct)).Count == 0)
        {
            return (null, new(404, "model_not_found", $"There is no model {model}. GET /v1/models lists yours.", "unknown_model"));
        }
        if (holder.Models.Count > 0 && !holder.Models.Contains(model, StringComparer.Ordinal))
        {
            return (null, new(403, "model_not_allowed", $"This key may not use {model}. GET /v1/models lists yours.", "model_not_allowed"));
        }
        if (await users.FindByIdAsync(holder.UserId.ToString()) is not { } user)
        {
            return (null, new(401, "invalid_api_key", "Authentication Error: this API key's person is not here any more.", "invalid_key"));
        }

        // Requests and tokens a minute: the person's limits (theirs, a group's, the company's), the key's own when tighter.
        var own = await db.ApiKeys.AsNoTracking().Where(k => k.Hash == holder.Hash).Select(k => new { k.RequestsPerMinute, k.TokensPerMinute, k.MonthlyBudget })
            .FirstOrDefaultAsync(ct);
        var person = await rates.ForAsync(user, ct);
        var rpm = Tighter(person.RequestsPerMinute.Value, own?.RequestsPerMinute);
        var tpm = Tighter(person.TokensPerMinute.Value, own?.TokensPerMinute);
        if (rpm is not null || tpm is not null)
        {
            var now = clock.GetUtcNow();
            var (requests, tokens) = await windows.CountAsync(holder.Hash, now, ct);
            var reset = (int)Math.Ceiling(60 - now.Second - now.Millisecond / 1000.0);
            if (rpm is { } r && requests > r)
            {
                return (null, Limited("requests", r, 0, reset));
            }
            if (tpm is { } t && tokens >= t)
            {
                return (null, Limited("tokens", t, 0, reset));
            }
        }

        // The key's own budget over the last 30 days.
        if (own?.MonthlyBudget is { } budget)
        {
            var since = clock.GetUtcNow().AddDays(-30);
            var spent = await db.GatewayRequests.AsNoTracking().Where(q => q.KeyHash == holder.Hash && q.StartedAt >= since).SumAsync(q => (decimal?)q.Cost, ct) ?? 0m;
            if (spent >= budget)
            {
                return (null, new(400, "budget_exceeded",
                    $"Budget has been exceeded! Current cost: {spent.ToString("0.####", CultureInfo.InvariantCulture)}, Max budget: {budget.ToString("0.####", CultureInfo.InvariantCulture)}", "budget"));
            }
        }
        if (await credit.RefusalAsync(user, CreditKinds.OfModel(model, media), ct) is { } short_)
        {
            return (null, new(400, "credit", short_, "credit"));
        }

        // The safeguards: every text of the messages, as LiteLLM's guardrail gave them; a masked text goes masked.
        var (texts, places) = Texts(body);
        var verdict = await safeguards.CheckApiAsync(user, [.. texts.Select(t => t.Text)], texts.LastOrDefault(t => t.Question)?.Text, model, ct, places);
        if (verdict.Action == "BLOCKED")
        {
            return (null, new(400, "safeguards", verdict.Reason ?? "The company's safeguards stopped this request.", "safeguards"));
        }
        if (verdict.Texts is { } masked && masked.Count == texts.Count)
        {
            for (var i = 0; i < texts.Count; i++)
            {
                texts[i].Set(masked[i]);
            }
        }
        if (await route.RoomForKeyAsync(model, ct) is { } full)
        {
            return (null, new(503, "model_not_ready", full, "model_not_ready", 30));
        }
        return (new Admitted(holder, user, model, tpm), null);
    }

    /// <summary>The tighter of two limits (null: none).</summary>
    public static int? Tighter(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    /// <summary>A minute's limit reached: the sentence Code Arena and LiteLLM's clients read, and the headers they read.</summary>
    public static AdmissionRefusal Limited(string kind, int limit, int remaining, int reset) => new(429, "rate_limit_error",
        $"Rate limit reached for this key: too many {kind} this minute. Limit type: {kind}. Current limit: {limit.ToString(CultureInfo.InvariantCulture)}", "rate_limit",
        Math.Max(1, reset), new Dictionary<string, string>
        {
            [$"x-ratelimit-limit-{kind}"] = limit.ToString(CultureInfo.InvariantCulture),
            [$"x-ratelimit-remaining-{kind}"] = remaining.ToString(CultureInfo.InvariantCulture),
            [$"x-ratelimit-reset-{kind}"] = $"{Math.Max(1, reset).ToString(CultureInfo.InvariantCulture)}s",
        });

    /// <summary>The texts of the messages in order (a message's content, or each text part), with where each is for a refusal's words.</summary>
    private static (List<Piece> Texts, List<string> Places) Texts(JsonObject body)
    {
        var pieces = new List<Piece>();
        var places = new List<string>();
        var messages = (body["messages"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var lastUser = messages.FindLastIndex(x => x["role"]?.ToString() == "user");
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var role = message["role"]?.ToString() switch
            {
                "system" or "developer" => "the instructions",
                "user" when i == lastUser => "your last message",
                "user" => "a message of yours",
                "assistant" => "an answer",
                "tool" or "function" => "a tool's result",
                var r => r ?? "a message",
            };
            var place = $"message {i + 1} of {messages.Count} ({role})";
            switch (message["content"])
            {
                case JsonValue v when v.TryGetValue<string>(out var s):
                    pieces.Add(new Piece(s, i == lastUser, masked => message["content"] = masked));
                    places.Add(place);
                    break;
                case JsonArray parts:
                    foreach (var part in parts.OfType<JsonObject>().Where(p => p["type"]?.ToString() == "text" && p["text"] is JsonValue))
                    {
                        pieces.Add(new Piece(part["text"]!.ToString(), i == lastUser, masked => part["text"] = masked));
                        places.Add(place);
                    }
                    break;
            }
        }
        return (pieces, places);
    }

    /// <summary>A text of the messages: its words, whether it is the person's last question, and how a masked one replaces it.</summary>
    private sealed record Piece(string Text, bool Question, Action<string> Put)
    {
        public void Set(string masked) => Put(masked);
    }
}

/// <summary>
/// Each API key's requests and tokens a minute, shared by every replica (gateway.rate_windows, UNLOGGED: counts, rebuilt in
/// a minute if lost). A request is counted as it is let in, its tokens when it ends. Keys with no limit never touch it.
/// </summary>
public sealed class RateWindows(AppDbContext db)
{
    private sealed record Window(int Requests, long Tokens);

    private static DateTimeOffset Minute(DateTimeOffset at) => new(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, TimeSpan.Zero);

    /// <summary>This request counted in the key's minute: the requests with it, and the tokens before it.</summary>
    public async Task<(int Requests, long Tokens)> CountAsync(string hash, DateTimeOffset now, CancellationToken ct)
    {
        var minute = Minute(now.ToUniversalTime());
        var got = await db.Database.SqlQueryRaw<Window>("""
            insert into gateway.rate_windows (key_hash, minute, requests, tokens) values ({0}, {1}, 1, 0)
            on conflict (key_hash, minute) do update set requests = gateway.rate_windows.requests + 1
            returning requests as "Requests", tokens as "Tokens"
            """, hash, minute).ToListAsync(ct);
        // The minutes before the last few go: nothing reads them.
        if (now.Second == 0)
        {
            await db.Database.ExecuteSqlRawAsync("delete from gateway.rate_windows where minute < {0}", [minute.AddMinutes(-5)], ct);
        }
        return got.Count == 0 ? (1, 0) : (got[0].Requests, got[0].Tokens);
    }

    /// <summary>A request's tokens (its prompt less what the cache read, and its answer) added to the minute it began in.</summary>
    public Task AddTokensAsync(string hash, DateTimeOffset started, long tokens, CancellationToken ct) => db.Database.ExecuteSqlRawAsync("""
        insert into gateway.rate_windows (key_hash, minute, requests, tokens) values ({0}, {1}, 0, {2})
        on conflict (key_hash, minute) do update set tokens = gateway.rate_windows.tokens + {2}
        """, [hash, Minute(started.ToUniversalTime()), tokens], ct);
}
