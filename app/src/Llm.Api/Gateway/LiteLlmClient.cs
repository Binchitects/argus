using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Llm.Api.Gateway;

public sealed class LiteLlmOptions
{
    public string Url { get; set; } = "http://litellm:4000";
    public string? MasterKey { get; set; }
}

public sealed class LiteLlmClient(HttpClient http) : ILiteLlm
{
    public async Task EnsureUserAsync(string email, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["user_id"] = email,
            ["user_email"] = email,
            ["user_role"] = "internal_user",
            // LiteLLM otherwise mints a key of its own here: a live credential nobody
            // was shown, next to the one we generate and hand over. Measured.
            ["auto_create_key"] = false,
        };
        // Already present is fine.
        await SendAsync(HttpMethod.Post, "/user/new", body, ct, allowStatus: [400, 409]);
    }

    public async Task SetBudgetAsync(string email, decimal? budget, CancellationToken ct = default)
    {
        // The API-key path: the internal user's ceiling.
        await SendAsync(HttpMethod.Post, "/user/update", new JsonObject { ["user_id"] = email, ["max_budget"] = budget }, ct);

        // The chat path: the end user's ceiling, which lives in LiteLLM's budget table.
        // /end_user/update answers 200 and IGNORES max_budget (measured: the stored
        // limit stayed at 5 after "setting" 0), so the limit is changed where it is kept.
        var info = await SendAsync(HttpMethod.Get, $"/end_user/info?end_user_id={Uri.EscapeDataString(email)}", null, ct, allowStatus: [400, 404]);
        if (info is null)
        {
            await SendAsync(HttpMethod.Post, "/end_user/new", new JsonObject { ["user_id"] = email, ["max_budget"] = budget }, ct);
        }
        else if (Str(info.AsObject(), "budget_id") is { Length: > 0 } budgetId)
        {
            await SendAsync(HttpMethod.Post, "/budget/update", new JsonObject { ["budget_id"] = budgetId, ["max_budget"] = budget }, ct);
        }
        else if (budget is not null)
        {
            var created = await SendAsync(HttpMethod.Post, "/budget/new", new JsonObject { ["max_budget"] = budget }, ct);
            var newId = created?["budget_id"]?.GetValue<string>() ?? throw new GatewayException("The gateway created no budget.");
            await SendAsync(HttpMethod.Post, "/end_user/update", new JsonObject { ["user_id"] = email, ["budget_id"] = newId }, ct);
        }
    }

    public async Task<string> GenerateKeyAsync(string email, string keyAlias, IReadOnlyList<string>? models = null, int? maxParallel = null, CancellationToken ct = default)
    {
        var body = new JsonObject { ["user_id"] = email, ["key_alias"] = keyAlias };
        if (models is { Count: > 0 })
        {
            body["models"] = new JsonArray([.. models.Select(m => (JsonNode)m)]);
        }
        if (maxParallel is > 0)
        {
            body["max_parallel_requests"] = maxParallel;
        }
        var res = await SendAsync(HttpMethod.Post, "/key/generate", body, ct);
        return res?["key"]?.GetValue<string>() is { Length: > 0 } key
            ? key
            : throw new GatewayException("The gateway created no key.");
    }

    public async Task<string> GenerateServiceKeyAsync(string keyAlias, CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Post, "/key/generate", new JsonObject { ["key_alias"] = keyAlias, ["metadata"] = new JsonObject { ["purpose"] = "the app's chat; spend is billed to each request's user" } }, ct);
        return res?["key"]?.GetValue<string>() is { Length: > 0 } key ? key : throw new GatewayException("The gateway created no key.");
    }

    public async Task<IReadOnlyList<GatewayModel>> ModelsAsync(CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get, "/model/info", null, ct);
        var models = new List<GatewayModel>();
        foreach (var row in (res?["data"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (row["model_name"]?.GetValue<string>() is not { Length: > 0 } name || models.Any(m => m.Name == name))
            {
                continue;
            }
            var info = row["model_info"] as JsonObject;
            var mode = info?["mode"]?.GetValue<string>() ?? "chat";
            if (mode is not ("chat" or "image_generation"))
            {
                continue; // embeddings and the like are not for the chat
            }
            int? Int(string k) => info?[k] is JsonValue v && v.TryGetValue<long>(out var n) ? (int)Math.Min(n, int.MaxValue) : null;
            bool? Flag(string k) => info?[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
            decimal? PerMtok(string k) => info?[k] is JsonValue v && v.TryGetValue<decimal>(out var d) ? d * 1_000_000m : null;
            models.Add(new GatewayModel(name, Int("max_input_tokens"), Int("max_output_tokens"),
                Flag("supports_vision") ?? false, Flag("supports_function_calling") ?? true, Flag("supports_reasoning") ?? true,
                PerMtok("input_cost_per_token"), PerMtok("cache_read_input_token_cost"), PerMtok("output_cost_per_token"), mode));
        }
        return models;
    }

    public async Task SetKeyAccessAsync(string token, IReadOnlyList<string> models, int? maxParallel, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Post, "/key/update", new JsonObject
        {
            ["key"] = token,
            ["models"] = new JsonArray([.. models.Select(m => (JsonNode)m)]),
            // LiteLLM refuses a key's requests past this many at once (429).
            ["max_parallel_requests"] = maxParallel is > 0 ? maxParallel : null,
        }, ct);

    public async Task<IReadOnlyList<ManagedModel>> ManagedModelsAsync(CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get, "/model/info", null, ct);
        return [.. (res?["data"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(row => row["model_info"]?["llm_app"]?.GetValue<string>() == "local" && row["model_info"]?["id"] is not null)
            .Select(row => new ManagedModel(row["model_info"]!["id"]!.GetValue<string>(), row["model_name"]?.GetValue<string>() ?? "",
                row["model_info"]?["llm_app_fingerprint"]?.GetValue<string>()))];
    }

    public async Task AddModelAsync(string name, JsonObject litellmParams, JsonObject modelInfo, string fingerprint, CancellationToken ct = default)
    {
        var info = (JsonObject)modelInfo.DeepClone();
        info["llm_app"] = "local";
        info["llm_app_fingerprint"] = fingerprint;
        await SendAsync(HttpMethod.Post, "/model/new", new JsonObject { ["model_name"] = name, ["litellm_params"] = litellmParams.DeepClone(), ["model_info"] = info }, ct);
    }

    public async Task DeleteModelAsync(string id, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Post, "/model/delete", new JsonObject { ["id"] = id }, ct, allowStatus: [404]);

    public async Task<IReadOnlyList<GatewayKey>> KeysAsync(string email, CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get, $"/key/list?user_id={Uri.EscapeDataString(email)}&return_full_object=true", null, ct);
        var rows = res is JsonObject o ? o["keys"] as JsonArray : res as JsonArray;
        var keys = new List<GatewayKey>();
        foreach (var row in rows ?? [])
        {
            if (row is JsonValue v && v.TryGetValue<string>(out var bare))
            {
                keys.Add(new GatewayKey(bare, "", null, 0, false, null));
                continue;
            }
            if (row is not JsonObject k || Str(k, "token") is not { Length: > 0 } token)
            {
                continue;
            }
            keys.Add(new GatewayKey(token, Str(k, "key_alias") ?? "", Str(k, "key_name"), Dec(k, "spend") ?? 0,
                k["blocked"]?.GetValueKind() == JsonValueKind.True,
                DateTimeOffset.TryParse(Str(k, "created_at"), CultureInfo.InvariantCulture, out var at) ? at : null,
                [.. (k["models"] as JsonArray ?? []).Select(m => m?.GetValue<string>() ?? "")],
                k["max_parallel_requests"] is JsonValue p && p.TryGetValue<int>(out var parallel) ? parallel : null));
        }
        return keys;
    }

    public async Task DeleteKeysAsync(IEnumerable<string> tokens, CancellationToken ct = default)
    {
        var list = tokens.ToList();
        if (list.Count > 0)
        {
            await SendAsync(HttpMethod.Post, "/key/delete", new JsonObject { ["keys"] = new JsonArray([.. list.Select(t => JsonValue.Create(t))]) }, ct);
        }
    }

    public async Task SetBlockedAsync(IEnumerable<string> tokens, bool blocked, CancellationToken ct = default)
    {
        foreach (var token in tokens)
        {
            await SendAsync(HttpMethod.Post, blocked ? "/key/block" : "/key/unblock", new JsonObject { ["key"] = token }, ct);
        }
    }

    public async Task<IReadOnlyDictionary<string, GatewayUser>> UsersAsync(CancellationToken ct = default)
    {
        var users = new Dictionary<string, GatewayUser>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; ; page++)
        {
            var res = await SendAsync(HttpMethod.Get, $"/user/list?page={page}&page_size=100", null, ct);
            var rows = res is JsonObject o ? o["users"] as JsonArray : res as JsonArray;
            if (rows is null || rows.Count == 0)
            {
                break;
            }
            foreach (var u in rows.OfType<JsonObject>())
            {
                if (Str(u, "user_id") is { Length: > 0 } id)
                {
                    users[id] = new GatewayUser(id, Dec(u, "spend") ?? 0, Dec(u, "max_budget"));
                }
            }
            if (rows.Count < 100)
            {
                break;
            }
        }
        return users;
    }

    public async Task DeleteUserAsync(string email, CancellationToken ct = default)
    {
        await DeleteKeysAsync((await KeysAsync(email, ct)).Select(k => k.Token), ct);
        var ids = new JsonObject { ["user_ids"] = new JsonArray(JsonValue.Create(email)) };
        await SendAsync(HttpMethod.Post, "/user/delete", ids, ct, allowStatus: [400, 404]);
        var endUser = await SendAsync(HttpMethod.Get, $"/end_user/info?end_user_id={Uri.EscapeDataString(email)}", null, ct, allowStatus: [400, 404]);
        await SendAsync(HttpMethod.Post, "/end_user/delete", new JsonObject { ["user_ids"] = new JsonArray(JsonValue.Create(email)) }, ct, allowStatus: [400, 404]);
        if (endUser is JsonObject eu && Str(eu, "budget_id") is { Length: > 0 } budgetId)
        {
            await SendAsync(HttpMethod.Post, "/budget/delete", new JsonObject { ["id"] = budgetId }, ct, allowStatus: [400, 404]);
        }
    }

    /// <returns>The parsed body, or null when the status was one of <paramref name="allowStatus"/>.</returns>
    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct, int[]? allowStatus = null)
    {
        using var req = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new GatewayException("The gateway is unreachable.", null, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation.
            throw new GatewayException("The gateway did not answer in time.", null, ex);
        }
        using (res)
        {
            if (!res.IsSuccessStatusCode)
            {
                if (allowStatus?.Contains((int)res.StatusCode) == true)
                {
                    return null;
                }
                throw new GatewayException($"The gateway refused the request (HTTP {(int)res.StatusCode}).", (int)res.StatusCode);
            }
            var text = await res.Content.ReadAsStringAsync(ct);
            return text.Length == 0 ? new JsonObject() : JsonNode.Parse(text);
        }
    }

    private static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static decimal? Dec(JsonObject o, string name) => o[name] switch
    {
        JsonValue v when v.TryGetValue<decimal>(out var d) => d,
        JsonValue v when v.TryGetValue<double>(out var f) => (decimal)f,
        _ => null,
    };
}
