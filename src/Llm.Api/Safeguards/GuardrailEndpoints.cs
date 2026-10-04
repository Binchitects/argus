using System.Security.Cryptography;
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
/// keys so get the chat's checks (Safeguards.CheckApiAsync) and the credit (one credit over the
/// chat and keys, and the groups' credit). The chat's own requests pass: the chat checked them.
/// Only inside the network (Traefik routes no /internal path) and with the gateway's master key.
/// </summary>
public static partial class GuardrailEndpoints
{
    /// <summary>LiteLLM appends /beta/litellm_basic_guardrail_api to the api_base it is given (http://app:8080/internal/guardrail).</summary>
    public const string Path = "/internal/guardrail/beta/litellm_basic_guardrail_api";

    public static void MapGuardrail(this IEndpointRouteBuilder app) => app.MapPost(Path, CheckAsync).AllowAnonymous();

    private static async Task<IResult> CheckAsync(HttpContext http, IOptions<LiteLlmOptions> gateway, UserManager<AppUser> users, Safeguards safeguards, Credit credit,
        ILoggerFactory logs)
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
        var owner = key is null ? null : Str(key, "user_api_key_user_id") ?? Str(key, "user_api_key_user_email");
        var user = owner is { } email && email.Contains('@') ? await users.FindByEmailAsync(email) : null;
        if (user is { IsDisabled: true })
        {
            return Answer(new ApiVerdict("BLOCKED", "This account is disabled."));
        }
        if (user is not null && await credit.RefusalAsync(user, http.RequestAborted) is { } refusal)
        {
            var logger = logs.CreateLogger(nameof(GuardrailEndpoints));
            LogRefused(logger, user.UserName ?? "?", "credit");
            return Answer(new ApiVerdict("BLOCKED", refusal));
        }
        var texts = (body["texts"] as JsonArray ?? []).Select(t => t is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").ToList();
        var verdict = await safeguards.CheckApiAsync(user, texts, LastQuestion(body) ?? texts.LastOrDefault(), Str(body, "model"), http.RequestAborted);
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
