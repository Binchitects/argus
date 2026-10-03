using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Llm.Core.Models;
using Microsoft.AspNetCore.DataProtection;

namespace Llm.Api.Models;

/// <summary>A model a remote server lists, with the window it reports when it does.</summary>
public sealed record RemoteOffer(string Id, int? Context);

/// <summary>Whether a server answers now, and the models it lists.</summary>
public sealed record RemoteStatus(bool Up, string? Error, IReadOnlyList<RemoteOffer> Models, DateTimeOffset At);

public sealed class RemoteException(string message) : Exception(message);

/// <summary>
/// Talks to other machines' OpenAI-compatible engines: what models they list, and
/// whether they answer. Their keys are kept encrypted with the app's key ring and
/// never shown again.
/// </summary>
public sealed class RemoteServerClient(IHttpClientFactory http, IDataProtectionProvider protection)
{
    public const string Client = "remote";
    /// <summary>For a server whose certificate is not checked (an admin's choice, per server).</summary>
    public const string Unchecked = "remote-unchecked";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly IDataProtector _keys = protection.CreateProtector("remote-server-key");

    public string? Protect(string? key) => string.IsNullOrEmpty(key) ? null : _keys.Protect(key);

    public string? Unprotect(string? stored) => string.IsNullOrEmpty(stored) ? null : _keys.Unprotect(stored);

    /// <summary>Why this is not a server address, or null: http or https, absolute, no query.</summary>
    public static string? CheckUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && u.Query.Length == 0 && u.Fragment.Length == 0 && u.UserInfo.Length == 0
            ? null
            : "The address is the server's OpenAI-compatible API, like http://10.0.0.5:8000/v1 (http or https, no query).";

    public static string Normalize(string url) => url.Trim().TrimEnd('/');

    /// <summary>The models the server lists (GET /models), or a RemoteException that says why not.</summary>
    public async Task<IReadOnlyList<RemoteOffer>> ModelsAsync(string baseUrl, string? apiKey, bool verifyTls, CancellationToken ct)
    {
        using var client = http.CreateClient(verifyTls ? Client : Unchecked);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(Normalize(baseUrl) + "/models"));
        if (!string.IsNullOrEmpty(apiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        HttpResponseMessage res;
        try
        {
            res = await client.SendAsync(req, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RemoteException($"It did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            throw new RemoteException("Its certificate is not trusted. Put the CA that signed it in config/ca (then docker compose up -d), or turn off the certificate check for this server.");
        }
        catch (HttpRequestException ex)
        {
            throw new RemoteException($"It cannot be reached: {ex.Message}");
        }
        using (res)
        {
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new RemoteException($"It refused the key (HTTP {(int)res.StatusCode}).");
            }
            if (!res.IsSuccessStatusCode)
            {
                throw new RemoteException($"It answered HTTP {(int)res.StatusCode} at {Normalize(baseUrl)}/models: is this its OpenAI-compatible API (usually ending in /v1)?");
            }
            JsonNode? body;
            try
            {
                body = JsonNode.Parse(await res.Content.ReadAsStringAsync(timeout.Token));
            }
            catch (System.Text.Json.JsonException)
            {
                throw new RemoteException("Its /models answer is not JSON: is this its OpenAI-compatible API?");
            }
            var list = body?["data"] as JsonArray ?? body as JsonArray;
            if (list is null)
            {
                throw new RemoteException("Its /models answer has no list of models.");
            }
            return [.. list.OfType<JsonObject>()
                .Where(m => m["id"]?.GetValueKind() == System.Text.Json.JsonValueKind.String)
                .Select(m => new RemoteOffer(m["id"]!.GetValue<string>(), ContextOf(m)))
                .DistinctBy(m => m.Id)];
        }
    }

    /// <summary>The window a server reports, under the name it uses: vLLM, llama.cpp, LiteLLM and others.</summary>
    private static int? ContextOf(JsonObject m)
    {
        foreach (var node in new[] { m["max_model_len"], m["meta"]?["n_ctx"], m["meta"]?["n_ctx_train"], m["context_length"], m["context_window"], m["max_input_tokens"] })
        {
            if (node is not JsonValue v)
            {
                continue;
            }
            if (v.TryGetValue<long>(out var n) && n > 0)
            {
                return (int)Math.Min(n, int.MaxValue);
            }
            if (v.TryGetValue<double>(out var d) && d >= 1)
            {
                return (int)Math.Min(d, int.MaxValue);
            }
        }
        return null;
    }
}

/// <summary>Each server's last answer, kept a little while so the Models page does not ask on every look.</summary>
public sealed class RemoteHealth(RemoteServerClient client)
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<Guid, RemoteStatus> _last = new();

    public async Task<RemoteStatus> StatusAsync(RemoteServer server, CancellationToken ct, bool fresh = false)
    {
        if (!fresh && _last.TryGetValue(server.Id, out var known) && DateTimeOffset.UtcNow - known.At < Fresh)
        {
            return known;
        }
        RemoteStatus now;
        try
        {
            now = new RemoteStatus(true, null, await client.ModelsAsync(server.BaseUrl, client.Unprotect(server.ApiKeyProtected), server.VerifyTls, ct), DateTimeOffset.UtcNow);
        }
        catch (RemoteException ex)
        {
            now = new RemoteStatus(false, ex.Message, [], DateTimeOffset.UtcNow);
        }
        return _last[server.Id] = now;
    }

    public void Forget(Guid id) => _last.TryRemove(id, out _);
}
