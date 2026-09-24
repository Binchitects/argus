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
        await SendAsync(HttpMethod.Post, "/user/update", new JsonObject { ["user_id"] = email, ["max_budget"] = budget }, ct);
        // The end-user record is what makes a ceiling bind on the chat path,
        // where everyone shares one gateway key (see deploy/identity-proxy).
        var endUser = new JsonObject { ["user_id"] = email, ["max_budget"] = budget };
        var updated = await SendAsync(HttpMethod.Post, "/end_user/update", endUser, ct, allowStatus: [400, 404]);
        if (updated is null)
        {
            await SendAsync(HttpMethod.Post, "/end_user/new", new JsonObject { ["user_id"] = email, ["max_budget"] = budget }, ct);
        }
    }

    public async Task<string> GenerateKeyAsync(string email, string keyAlias, CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Post, "/key/generate", new JsonObject { ["user_id"] = email, ["key_alias"] = keyAlias }, ct);
        return res?["key"]?.GetValue<string>() is { Length: > 0 } key
            ? key
            : throw new GatewayException("The gateway created no key.");
    }

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
                DateTimeOffset.TryParse(Str(k, "created_at"), CultureInfo.InvariantCulture, out var at) ? at : null));
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
        await SendAsync(HttpMethod.Post, "/end_user/delete", new JsonObject { ["user_ids"] = new JsonArray(JsonValue.Create(email)) }, ct, allowStatus: [400, 404]);
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
