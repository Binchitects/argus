using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Argus.Platform;

/// <summary>The gateway answered with an error, or could not be reached.</summary>
public sealed class GatewayError(string message, int status = 502) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The model gateway (LiteLLM). Every person has an internal user there, keyed
/// by email, and a virtual key their chats are sent with -- which is what makes
/// the gateway's per-person budget and spend apply to the chat in this app as
/// much as to anyone calling the API directly.
/// </summary>
public sealed class Gateway
{
    public string BaseUrl { get; }
    readonly string _masterKey;
    readonly HttpClient _http;

    public Gateway(string baseUrl, string masterKey, HttpClient? http = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _masterKey = masterKey;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary><c>ARGUS_GATEWAY_URL</c> and <c>LITELLM_MASTER_KEY</c>; null when no gateway is configured.</summary>
    public static Gateway? FromEnvironment(HttpClient? http = null)
    {
        var url = Environment.GetEnvironmentVariable("ARGUS_GATEWAY_URL") ?? "";
        var key = Environment.GetEnvironmentVariable("LITELLM_MASTER_KEY") ?? "";
        return url.Length == 0 ? null : new Gateway(url, key, http);
    }

    public bool CanAdminister => _masterKey.Length > 0;

    HttpRequestMessage Request(HttpMethod method, string path, string? key, JsonNode? body = null)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        var bearer = key ?? _masterKey;
        if (bearer.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }

    async Task<JsonNode?> Send(HttpMethod method, string path, JsonNode? body = null, string? key = null, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        using var request = Request(method, path, key, body);
        HttpResponseMessage response;
        string text;
        try
        {
            response = await _http.SendAsync(request, cts.Token);
            text = await response.Content.ReadAsStringAsync(cts.Token);
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw new GatewayError($"The model gateway is unreachable ({exc.Message}).", 503);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new GatewayError($"The model gateway refused {method} {path}: {Describe(text, (int)response.StatusCode)}",
                    (int)response.StatusCode is 401 or 403 or 429 ? (int)response.StatusCode : 502);
            try { return text.Length == 0 ? null : JsonNode.Parse(text); }
            catch (JsonException) { throw new GatewayError($"The model gateway answered {path} with something that is not JSON."); }
        }
    }

    /// <summary>LiteLLM's error bodies nest the message in several shapes; take the first that exists.</summary>
    public static string Describe(string body, int status)
    {
        try
        {
            var node = JsonNode.Parse(body);
            var msg = node?["error"]?["message"]?.ToString() ?? node?["detail"]?["error"]?.ToString()
                      ?? node?["detail"]?.ToString() ?? node?["error"]?.ToString();
            if (!string.IsNullOrWhiteSpace(msg)) return Util.PyStr.Prefix(msg, 300);
        }
        catch (JsonException) { }
        return $"HTTP {status}";
    }

    // --- people ---------------------------------------------------------------------

    /// <summary>Create the internal user, or leave an existing one as it is.</summary>
    public async Task EnsureUser(string email, double? maxBudget = null)
    {
        var body = new JsonObject { ["user_id"] = email, ["user_email"] = email, ["user_role"] = "internal_user", ["auto_create_key"] = false };
        if (maxBudget is not null) body["max_budget"] = maxBudget;
        try { await Send(HttpMethod.Post, "/user/new", body); }
        catch (GatewayError exc) when (exc.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)) { }
    }

    public async Task SetBudget(string email, double? maxBudget) =>
        await Send(HttpMethod.Post, "/user/update", new JsonObject { ["user_id"] = email, ["max_budget"] = maxBudget });

    public async Task DeleteUser(string email) =>
        await Send(HttpMethod.Post, "/user/delete", new JsonObject { ["user_ids"] = new JsonArray(email) });

    /// <summary>Spend and budget for one person; nulls when the gateway has no record of them.</summary>
    public async Task<(double Spend, double? MaxBudget, JsonArray Keys)> UserInfo(string email)
    {
        var node = await Send(HttpMethod.Get, "/user/info?user_id=" + Uri.EscapeDataString(email));
        var info = node?["user_info"];
        double spend = info?["spend"] is JsonValue s && s.TryGetValue<double>(out var sv) ? sv : 0;
        double? budget = info?["max_budget"] is JsonValue b && b.TryGetValue<double>(out var bv) ? bv : null;
        return (spend, budget, node?["keys"] as JsonArray ?? []);
    }

    /// <summary>Spend and budget of every internal user, by email.</summary>
    public async Task<Dictionary<string, (double Spend, double? MaxBudget)>> AllUsers()
    {
        var node = await Send(HttpMethod.Get, "/user/list?page_size=1000");
        var list = node?["users"] as JsonArray ?? node as JsonArray ?? [];
        var result = new Dictionary<string, (double, double?)>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in list)
        {
            var id = u?["user_id"]?.ToString();
            if (string.IsNullOrEmpty(id)) continue;
            double spend = u?["spend"] is JsonValue s && s.TryGetValue<double>(out var sv) ? sv : 0;
            double? budget = u?["max_budget"] is JsonValue b && b.TryGetValue<double>(out var bv) ? bv : null;
            result[id] = (spend, budget);
        }
        return result;
    }

    // --- keys -----------------------------------------------------------------------

    /// <summary>A new virtual key owned by the person. Returns the secret (shown once) and its hashed token.</summary>
    public async Task<(string Key, string Token)> CreateKey(string email, string alias)
    {
        var node = await Send(HttpMethod.Post, "/key/generate", new JsonObject
        {
            ["user_id"] = email, ["key_alias"] = alias, ["metadata"] = new JsonObject { ["issued_by"] = "argus" },
        });
        var key = node?["key"]?.ToString();
        if (string.IsNullOrEmpty(key)) throw new GatewayError("The model gateway issued no key.");
        return (key, node?["token"]?.ToString() ?? node?["token_id"]?.ToString() ?? "");
    }

    public async Task DeleteKeys(IEnumerable<string> keysOrTokens) =>
        await Send(HttpMethod.Post, "/key/delete", new JsonObject { ["keys"] = new JsonArray(keysOrTokens.Select(k => (JsonNode?)k).ToArray()) });

    // --- models and health ----------------------------------------------------------

    public async Task<List<string>> Models(string? key)
    {
        var node = await Send(HttpMethod.Get, "/v1/models", key: key);
        return (node?["data"] as JsonArray ?? []).Select(m => m?["id"]?.ToString() ?? "").Where(m => m.Length > 0).ToList();
    }

    public async Task<(bool Ok, string Detail, long Ms)> Health()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await Send(HttpMethod.Get, "/health/liveliness", timeout: TimeSpan.FromSeconds(3), key: "");
            return (true, "live", sw.ElapsedMilliseconds);
        }
        catch (GatewayError exc) { return (false, exc.Message, sw.ElapsedMilliseconds); }
    }

    // --- chat -----------------------------------------------------------------------

    /// <summary>
    /// A streamed chat completion: yields each server-sent event's JSON as it
    /// arrives. The caller's cancellation token ends the upstream request too,
    /// so a closed browser tab stops the model generating.
    /// </summary>
    public async IAsyncEnumerable<JsonObject> StreamChat(JsonObject body, string? key,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        body["stream"] = true;
        body["stream_options"] = new JsonObject { ["include_usage"] = true };
        using var request = Request(HttpMethod.Post, "/v1/chat/completions", key, body);
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException exc) { throw new GatewayError($"The model gateway is unreachable ({exc.Message}).", 503); }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                throw new GatewayError(Describe(text, (int)response.StatusCode),
                    (int)response.StatusCode is 401 or 403 or 429 or 400 ? (int)response.StatusCode : 502);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") yield break;
                JsonObject? chunk;
                try { chunk = JsonNode.Parse(data) as JsonObject; }
                catch (JsonException) { continue; }
                if (chunk?["error"] is { } error)
                    throw new GatewayError(error["message"]?.ToString() ?? error.ToJsonString());
                if (chunk is not null) yield return chunk;
            }
        }
    }
}
