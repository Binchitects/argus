using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;

namespace Llm.Api.Gateway;

/// <summary>
/// The rest of the model API with keys, answered by the app itself (Gateway:Mode own): the models a key may use
/// (/v1/models, one by its name, /v1/model/info as Code Arena reads it), embeddings by the embeddings server, booked; and a
/// plain 404 for what no server here serves (rerank, moderations, translations, picture edits). A copy of a model under its
/// own name (NAME@i) is in no list. Anthropic's count_tokens still goes by LiteLLM until the app answers Anthropic's API.
/// </summary>
public static class OwnApi
{
    private static readonly string[] Listed = ["chat", "image_generation", "audio_transcription", "audio_speech", "embedding"];

    private static Task ErrorAsync(HttpContext http, int status, string type, string message)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new { error = new { message, type, code = status.ToString(CultureInfo.InvariantCulture) } }, http.RequestAborted);
    }

    /// <summary>True when it answered (own mode); false: by LiteLLM, as ApiPassage passes it on.</summary>
    public static async Task<bool> TryAsync(HttpContext http)
    {
        var own = http.RequestServices.GetRequiredService<ModelGateway>();
        var path = http.Request.Path.Value ?? "";
        if (!own.ForApi || path.EndsWith("/count_tokens", StringComparison.Ordinal))
        {
            return false;
        }
        if (KeyMedia.Preflight(http))
        {
            return true;
        }
        http.Response.Headers.AccessControlAllowOrigin = "*";
        var ct = http.RequestAborted;
        var auth = http.Request.Headers.Authorization.ToString();
        var secret = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : http.Request.Headers["x-api-key"].ToString();
        var holder = secret is { Length: > 0 } ? await http.RequestServices.GetRequiredService<KeyBook>().ForAsync(secret, ct) : null;
        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        if (holder is null || holder.ExpiredAt(now))
        {
            await ErrorAsync(http, 401, "invalid_api_key", "Authentication Error: this API key is not known (Your account → API keys makes one).");
            return true;
        }
        if (holder.Disabled || holder.ApiOff)
        {
            await ErrorAsync(http, 403, "permission_denied", holder.Disabled ? "This account is disabled." : "Your API access is off. Ask an admin to turn it on (Admin → People).");
            return true;
        }
        var routes = (await own.AllRoutesAsync(ct)).Where(r => r.CopyOf is null && Listed.Contains(r.Mode)).GroupBy(r => r.Name).Select(g => g.First())
            .Where(r => r.Mode == "embedding" || holder.Models.Count == 0 || holder.Models.Contains(r.Name, StringComparer.Ordinal)).ToList();

        if (HttpMethods.IsGet(http.Request.Method) && (path.EndsWith("/models", StringComparison.Ordinal)))
        {
            await http.Response.WriteAsJsonAsync(new { @object = "list", data = routes.Select(Model) }, ct);
            return true;
        }
        if (HttpMethods.IsGet(http.Request.Method) && path.Contains("/models/", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(path[(path.IndexOf("/models/", StringComparison.Ordinal) + 8)..]);
            if (routes.FirstOrDefault(r => r.Name == id) is { } one)
            {
                await http.Response.WriteAsJsonAsync(Model(one), ct);
            }
            else
            {
                await ErrorAsync(http, 404, "model_not_found", $"There is no model {id}.");
            }
            return true;
        }
        if (HttpMethods.IsGet(http.Request.Method) && path.EndsWith("/model/info", StringComparison.Ordinal))
        {
            await http.Response.WriteAsJsonAsync(new { data = routes.Select(Info) }, ct);
            return true;
        }
        if (HttpMethods.IsPost(http.Request.Method) && path.EndsWith("/embeddings", StringComparison.Ordinal))
        {
            JsonObject? body;
            try
            {
                body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct) as JsonObject;
            }
            catch (JsonException)
            {
                body = null;
            }
            if (body is null)
            {
                await ErrorAsync(http, 400, "invalid_request_error", "The request is not a JSON object.");
                return true;
            }
            var embedders = routes.Where(r => r.Mode == "embedding").ToList();
            var asked = body["model"]?.ToString();
            if ((asked is null ? embedders.FirstOrDefault() : embedders.FirstOrDefault(r => r.Name == asked)) is not { } d)
            {
                await ErrorAsync(http, 404, "model_not_found", asked is null ? "There is no embeddings model here." : $"There is no embeddings model {asked}.");
                return true;
            }
            var user = await http.RequestServices.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Core.Identity.AppUser>>().FindByIdAsync(holder.UserId.ToString());
            if (user is not null && await http.RequestServices.GetRequiredService<Credit>().RefusalAsync(user, Core.Access.CreditKind.Api, ct) is { } refusal)
            {
                await ErrorAsync(http, 400, "credit", refusal);
                return true;
            }
            try
            {
                using var res = await own.EmbedAsync(body, d, new GatewayCaller(holder.Email, holder.Kind == Core.Gateway.ApiKey.Studio ? "studio" : "api", KeyHash: holder.Hash, KeyAlias: holder.Alias), ct);
                http.Response.StatusCode = (int)res.StatusCode;
                http.Response.ContentType = "application/json";
                await http.Response.WriteAsync(await res.Content.ReadAsStringAsync(ct), ct);
            }
            catch (ChatGatewayException ex)
            {
                await ErrorAsync(http, ex.Status ?? 502, "upstream_error", ex.Message);
            }
            return true;
        }
        // Rerank, moderations, translations, picture edits and variations: no server here serves them.
        await ErrorAsync(http, 404, "model_not_found", "No model here serves this.");
        return true;
    }

    private static object Model(Deployment d) => new { id = d.Name, @object = "model", created = 0, owned_by = "arena" };

    /// <summary>A model as /v1/model/info gives it (LiteLLM's shape, which Code Arena reads): its window, longest answer, what it reads and does, its prices.</summary>
    private static JsonObject Info(Deployment d)
    {
        var info = (JsonObject)d.Info.DeepClone();
        info["mode"] ??= d.Mode;
        if (d.Price is { } p)
        {
            info["input_cost_per_token"] = p.Input / 1_000_000m;
            info["cache_read_input_token_cost"] = p.CachedInput / 1_000_000m;
            info["output_cost_per_token"] = p.Output / 1_000_000m;
        }
        foreach (var k in new[] { "input_cost_per_image", "input_cost_per_second", "input_cost_per_character" })
        {
            if (d.LiteLlm[k] is { } v)
            {
                info[k] = v.DeepClone();
            }
        }
        return new JsonObject { ["model_name"] = d.Name, ["model_info"] = info };
    }
}
