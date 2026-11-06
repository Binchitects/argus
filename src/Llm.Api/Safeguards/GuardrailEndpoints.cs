using System.Security.Cryptography;
using Llm.Api.Access;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Gateway;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.Safeguards;

/// <summary>
/// The gateway's guardrail: LiteLLM's generic guardrail API (deploy/config/litellm.yaml) posts
/// each request's texts and its key's owner here before sending it, and this answers NONE,
/// BLOCKED with the reason the client sees, or GUARDRAIL_INTERVENED with the texts masked. API
/// keys so get the chat's checks (Safeguards.CheckApiAsync), API access, the credit of the
/// request's kind (the person's and their groups'), and room in the engine for a model that is not loaded
/// as the chat's requests get it (EngineRoute), so the engine never unloads the big model
/// everyone is on for them. The chat's own requests pass: the chat checked them, and made room.
/// Only inside the network (Traefik routes no /internal path) and with the gateway's master key.
/// </summary>
public static partial class GuardrailEndpoints
{
    /// <summary>LiteLLM appends /beta/litellm_basic_guardrail_api to the api_base it is given (http://app:8080/internal/guardrail).</summary>
    public const string Path = "/internal/guardrail/beta/litellm_basic_guardrail_api";

    public static void MapGuardrail(this IEndpointRouteBuilder app) => app.MapPost(Path, CheckAsync).AllowAnonymous("The gateway's guardrail: the internal key is checked.");

    private static async Task<IResult> CheckAsync(HttpContext http, IOptions<LiteLlmOptions> gateway, UserManager<AppUser> users, Safeguards safeguards, Credit credit,
        EngineRoute route, ILoggerFactory logs)
    {
        var expected = gateway.Value.MasterKey;
        var given = http.Request.Headers["x-api-key"].ToString();
        if (string.IsNullOrEmpty(expected) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected)))
        {
            return Results.Unauthorized();
        }
        JsonObject? body;
        try
        {
            body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted) as JsonObject;
        }
        catch (JsonException)
        {
            body = null;
        }
        if (body is null)
        {
            return Results.BadRequest();
        }
        // What the model wrote back is not checked here: only what goes in.
        if (Str(body, "input_type") is "response")
        {
            return Answer(ApiVerdict.None);
        }
        var key = body["request_data"] as JsonObject;
        if (key is not null && Str(key, "user_api_key_alias") == ChatKey.Alias)
        {
            return Answer(ApiVerdict.None);
        }
        // The key's person as the app keeps its keys (by its hash: an email changed since the key was made is theirs), else
        // as LiteLLM names them.
        var owner = key is null ? null : Str(key, "user_api_key_user_id") ?? Str(key, "user_api_key_user_email");
        var holder = key is not null && Str(key, "user_api_key_hash") is { Length: 64 } hash
            ? await http.RequestServices.GetRequiredService<Gateway.KeyBook>().ForHashAsync(hash, http.RequestAborted)
            : null;
        var user = holder is not null ? await users.FindByIdAsync(holder.UserId.ToString())
            : owner is { } email && email.Contains('@') ? await users.FindByEmailAsync(email) : null;
        if (user is { IsDisabled: true })
        {
            return Answer(new ApiVerdict("BLOCKED", "This account is disabled."));
        }
        if (user is not null && await http.RequestServices.GetRequiredService<Access.AccessService>().ApiOffAsync(user, http.RequestAborted))
        {
            return Answer(new ApiVerdict("BLOCKED", "Your API access is off. Ask an admin to turn it on (Admin → People)."));
        }
        // The credit of what the request makes: a picture, a video, speech, or else an API key's text request.
        if (user is not null && await credit.RefusalAsync(user, CreditKinds.OfModel(Str(body, "model"), http.RequestServices.GetRequiredService<Models.MediaControl>()), http.RequestAborted) is { } refusal)
        {
            var logger = logs.CreateLogger(nameof(GuardrailEndpoints));
            LogRefused(logger, user.UserName ?? "?", "credit");
            return Answer(new ApiVerdict("BLOCKED", refusal));
        }
        var texts = (body["texts"] as JsonArray ?? []).Select(t => t is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").ToList();
        var model = Str(body, "model");
        var verdict = await safeguards.CheckApiAsync(user, texts, LastQuestion(body) ?? texts.LastOrDefault(), model, http.RequestAborted, Places(body, texts));
        // Room for its model last, once nothing else holds it back: a model the engine has not loaded, at its limit.
        if (verdict.Action != "BLOCKED" && await route.RoomForKeyAsync(model, http.RequestAborted) is { } full)
        {
            var logger = logs.CreateLogger(nameof(GuardrailEndpoints));
            LogRefused(logger, user?.UserName ?? "?", full);
            return Answer(new ApiVerdict("BLOCKED", full));
        }
        return Answer(verdict);
    }

    /// <summary>The request's last user message, as text (its parts' text joined).</summary>
    private static string? LastQuestion(JsonObject body)
    {
        var last = (body["structured_messages"] as JsonArray ?? []).OfType<JsonObject>().LastOrDefault(m => Str(m, "role") == "user");
        return last?["content"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonArray parts => string.Join("\n", parts.OfType<JsonObject>().Select(p => Str(p, "text")).OfType<string>()),
            _ => null,
        };
    }

    /// <summary>
    /// Where each text is in the request, for a refusal's words: "message 3 of 9 (a tool's result)", by the message whose
    /// content (or one of its parts) it is; a text no message holds is named by its place among the texts.
    /// </summary>
    private static List<string> Places(JsonObject body, List<string> texts)
    {
        var messages = (body["structured_messages"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var lastUser = messages.FindLastIndex(m => Str(m, "role") == "user");
        var taken = new HashSet<int>();
        var places = new List<string>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            var at = Enumerable.Range(0, messages.Count).FirstOrDefault(j => !taken.Contains(j) && Holds(messages[j], texts[i]), -1);
            if (at < 0)
            {
                places.Add($"text {i + 1} of {texts.Count} of the request");
                continue;
            }
            taken.Add(at);
            var role = Str(messages[at], "role") switch
            {
                "system" or "developer" => "the instructions",
                "user" when at == lastUser => "your last message",
                "user" => "a message of yours",
                "assistant" => "an answer",
                "tool" or "function" => "a tool's result",
                var r => r ?? "a message",
            };
            places.Add($"message {at + 1} of {messages.Count} ({role})");
        }
        return places;
    }

    private static bool Holds(JsonObject message, string text) => message["content"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s == text,
        JsonArray parts => parts.OfType<JsonObject>().Any(p => Str(p, "text") == text),
        _ => false,
    };

    private static IResult Answer(ApiVerdict v)
    {
        var json = new JsonObject { ["action"] = v.Action };
        if (v.Reason is { } reason)
        {
            json["blocked_reason"] = reason;
        }
        if (v.Texts is { } texts)
        {
            json["texts"] = new JsonArray([.. texts.Select(t => (JsonNode)JsonValue.Create(t)!)]);
        }
        return Results.Json(json);
    }

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "The guardrail held back an API request of {Person}: {Why}")]
    private static partial void LogRefused(ILogger logger, string person, string why);
}
