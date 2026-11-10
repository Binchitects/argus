using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

using Branding;
namespace Llm.Api.Operations;

public sealed class CodeIntelException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// code-intel's operator surface (/admin/...), called with the admin token, which
/// never leaves the server. The app passes code-intel's JSON through as it is: until
/// phase 4, code-intel decides what an index or a pack looks like.
/// </summary>
public sealed class CodeIntelAdmin(HttpClient http, IOptions<CodeIntelOptions> options)
{
    public bool Enabled => options.Value.Enabled;

    public Task<JsonNode?> GetAsync(string path, CancellationToken ct = default) => SendAsync(HttpMethod.Get, path, null, ct);

    public Task<JsonNode?> PostAsync(string path, JsonNode body, CancellationToken ct = default) => SendAsync(HttpMethod.Post, path, body, ct);

    public Task<JsonNode?> PatchAsync(string path, JsonNode body, CancellationToken ct = default) => SendAsync(HttpMethod.Patch, path, body, ct);

    public Task<JsonNode?> PutAsync(string path, JsonNode body, CancellationToken ct = default) => SendAsync(HttpMethod.Put, path, body, ct);

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        if (!Enabled)
        {
            throw new CodeIntelException($"{Brand.CodeIntel} is not configured (CODE_INTEL_ADMIN_TOKEN is empty, or the code-intel profile is off).", 404);
        }
        using var req = new HttpRequestMessage(method, new Uri(options.Value.Url.TrimEnd('/') + "/admin/" + path));
        req.Headers.Add("X-Code-Intel-Admin-Token", options.Value.AdminToken);
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
            throw new CodeIntelException($"{Brand.CodeIntel} did not answer.", 502);
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
                var message = json?["error"]?.GetValue<string>() ?? $"{Brand.CodeIntel} answered HTTP {(int)res.StatusCode}.";
                throw new CodeIntelException(message, (int)res.StatusCode is 400 or 404 or 409 ? (int)res.StatusCode : 502);
            }
            return json;
        }
    }
}
