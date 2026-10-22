using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Identity;
using Llm.Api.Settings;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Code.Fixes;

/// <summary>Why GitLab could not be asked, or said no, in words for the person.</summary>
public sealed class GitLabException(string message) : Exception(message);

/// <summary>
/// GitLab as one person: their own token (scope api), linked for Fix it, kept encrypted. It reads the branch to fix and
/// opens the merge request as them: never the app's or Argus's account.
/// </summary>
public sealed class GitLabAsPerson(IHttpClientFactory http, AppDbContext db, IOptionsMonitor<Schedules.GitLabOptions> options, IOptions<Operations.ArgusOptions> argus,
    IOptions<AuthOptions> auth)
{
    public const string ToolId = "fixit:gitlab";
    public const long MostArchive = 100L * 1024 * 1024;

    public string? BaseUrl => (string.IsNullOrWhiteSpace(options.CurrentValue.Url) ? argus.Value.GitlabUrl : options.CurrentValue.Url)?.TrimEnd('/');

    /// <summary>The person's token, or null when they have not linked one.</summary>
    public async Task<string?> TokenAsync(Guid userId, CancellationToken ct)
    {
        var saved = await db.PersonCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.UserId == userId && c.ToolId == ToolId, ct);
        return saved is null || auth.Value.DataKey is not { Length: > 0 } key ? null : SettingsCrypto.Decrypt(saved.SecretEncrypted, key);
    }

    private async Task<HttpResponseMessage> SendAsync(string token, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        if (BaseUrl is not { Length: > 0 } baseUrl)
        {
            throw new GitLabException("GitLab's address is not set (GITLAB_URL).");
        }
        using var request = new HttpRequestMessage(method, $"{baseUrl}/api/v4{path}");
        request.Headers.Add("PRIVATE-TOKEN", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        try
        {
            return await http.CreateClient(Schedules.GitLabBot.Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new GitLabException($"GitLab could not be reached: {ex.Message}");
        }
    }

    private static async Task<GitLabException> RefusedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        string? said = null;
        try
        {
            said = JsonNode.Parse(text)?["message"]?.ToJsonString().Trim('"') ?? JsonNode.Parse(text)?["error"]?.ToString();
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return new GitLabException($"GitLab said {(int)response.StatusCode}{(said is { Length: > 0 } ? $": {said}" : "")}.");
    }

    public async Task<JsonNode> GetAsync(string token, string path, CancellationToken ct)
    {
        using var response = await SendAsync(token, HttpMethod.Get, path, null, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await RefusedAsync(response, ct);
        }
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) ?? new JsonObject();
    }

    public async Task<string> TextAsync(string token, string path, CancellationToken ct)
    {
        using var response = await SendAsync(token, HttpMethod.Get, path, null, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : throw await RefusedAsync(response, ct);
    }

    public async Task<JsonNode> PostAsync(string token, string path, JsonNode body, CancellationToken ct)
    {
        using var response = await SendAsync(token, HttpMethod.Post, path, body, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await RefusedAsync(response, ct);
        }
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) ?? new JsonObject();
    }

    /// <summary>The branch's files as GitLab packs them (tar.gz), at most 100 MB.</summary>
    public async Task<byte[]> ArchiveAsync(string token, string project, string sha, CancellationToken ct)
    {
        using var response = await SendAsync(token, HttpMethod.Get, $"/projects/{Uri.EscapeDataString(project)}/repository/archive.tar.gz?sha={Uri.EscapeDataString(sha)}", null, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await RefusedAsync(response, ct);
        }
        if (response.Content.Headers.ContentLength > MostArchive)
        {
            throw new GitLabException("The branch's files are more than 100 MB packed: too many to fix here.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var m = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (m.Length + read > MostArchive)
            {
                throw new GitLabException("The branch's files are more than 100 MB packed: too many to fix here.");
            }
            m.Write(buffer, 0, read);
        }
        return m.ToArray();
    }

    public static string Project(string path) => $"/projects/{Uri.EscapeDataString(path)}";
}
