using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Llm.Api.Operations;

public sealed class ArgusException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Argus's operator surface (/admin/...), called with the admin token, which
/// never leaves the server. The app passes Argus's JSON through as it is: until
/// phase 4, Argus decides what an index or a pack looks like.
/// </summary>
public sealed class ArgusAdmin(HttpClient http, IOptions<ArgusOptions> options)
{
    public bool Enabled => options.Value.Enabled;

    public Task<JsonNode?> GetAsync(string path, CancellationToken ct = default) => SendAsync(HttpMethod.Get, path, null, ct);

    public Task<JsonNode?> PostAsync(string path, JsonNode body, CancellationToken ct = default) => SendAsync(HttpMethod.Post, path, body, ct);

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        if (!Enabled)
        {
            throw new ArgusException("Argus is not configured (ARGUS_ADMIN_TOKEN is empty, or the argus profile is off).", 404);
        }
        using var req = new HttpRequestMessage(method, new Uri(options.Value.Url.TrimEnd('/') + "/admin/" + path));
        req.Headers.Add("X-Argus-Admin-Token", options.Value.AdminToken);
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new ArgusException("Argus did not answer.", 502);
        }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            JsonNode? json = null;
            try
            {
                json = text.Length == 0 ? new JsonObject() : JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                // Not JSON: reported below by status.
            }
            if (!res.IsSuccessStatusCode)
            {
                var message = json?["error"]?.GetValue<string>() ?? $"Argus answered HTTP {(int)res.StatusCode}.";
                throw new ArgusException(message, (int)res.StatusCode is 400 or 404 or 409 ? (int)res.StatusCode : 502);
            }
            return json;
        }
    }
}
