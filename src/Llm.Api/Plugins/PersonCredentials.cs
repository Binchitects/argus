using System.Text.Json.Nodes;
using Llm.Api.Identity;
using Llm.Api.Settings;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Plugins;

/// <summary>
/// Each person's own account at a plugin's service: an API key they paste, or OAuth tokens from
/// signing in there. Kept encrypted (Settings crypto); an expired OAuth token is refreshed here.
/// </summary>
public sealed class PersonCredentials(AppDbContext db, IHttpClientFactory http, IOptions<AuthOptions> auth)
{
    private string Key => auth.Value.DataKey is { Length: > 0 } k ? k : throw new InvalidOperationException("APP_DATA_KEY is not set.");

    /// <summary>The person's token for a tool, refreshed when it has expired; null when they have not connected (or it cannot be refreshed).</summary>
    public async Task<string?> TokenAsync(McpServer server, Guid userId, CancellationToken ct)
    {
        var toolId = Chat.Tools.McpServerTool.Prefix + server.Id;
        if (await db.PersonCredentials.SingleOrDefaultAsync(c => c.UserId == userId && c.ToolId == toolId, ct) is not { } saved)
        {
            return null;
        }
        if (saved.ExpiresAt is { } expires && expires < DateTimeOffset.UtcNow.AddMinutes(1))
        {
            if (saved.RefreshEncrypted is null || await RefreshAsync(server, saved, ct) is false)
            {
                return null;
            }
        }
        return SettingsCrypto.Decrypt(saved.SecretEncrypted, Key);
    }

    public async Task SaveAsync(Guid userId, string toolId, string secret, string? refresh, DateTimeOffset? expires, string? account, CancellationToken ct)
    {
        var saved = await db.PersonCredentials.SingleOrDefaultAsync(c => c.UserId == userId && c.ToolId == toolId, ct);
        if (saved is null)
        {
            saved = new PersonCredential { UserId = userId, ToolId = toolId, SecretEncrypted = "" };
            db.PersonCredentials.Add(saved);
        }
        saved.SecretEncrypted = SettingsCrypto.Encrypt(secret, Key);
        saved.RefreshEncrypted = refresh is null ? null : SettingsCrypto.Encrypt(refresh, Key);
        saved.ExpiresAt = expires;
        saved.Account = account;
        saved.CreatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The plugin's settings, secrets decrypted: to fill its templates and sign in at its service.</summary>
    public IReadOnlyDictionary<string, string> Settings(McpServer server) => PluginInstaller.Values(server, auth.Value.DataKey);

    /// <summary>Trades an OAuth code (or a refresh token) for tokens at the plugin's token address.</summary>
    public async Task<(string Token, string? Refresh, DateTimeOffset? Expires)> ExchangeAsync(McpServer server, Dictionary<string, string> form, CancellationToken ct)
    {
        var manifest = PluginManifest.Parse(server.Manifest!);
        var values = Settings(server);
        form["client_id"] = values.GetValueOrDefault("client_id", "");
        form["client_secret"] = values.GetValueOrDefault("client_secret", "");
        using var res = await http.CreateClient(PluginCatalog.Client).PostAsync(PluginManifest.Fill(manifest.OAuth!.Token, values), new FormUrlEncodedContent(form), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode || JsonNode.Parse(body) is not JsonObject tokens || tokens["access_token"]?.GetValue<string>() is not { Length: > 0 } token)
        {
            throw new PluginCatalog.PluginException($"{server.Name} did not give a token ({(int)res.StatusCode}).");
        }
        DateTimeOffset? expires = tokens["expires_in"] is JsonValue e && e.TryGetValue<long>(out var seconds) ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null;
        return (token, tokens["refresh_token"]?.GetValue<string>(), expires);
    }

    private async Task<bool> RefreshAsync(McpServer server, PersonCredential saved, CancellationToken ct)
    {
        try
        {
            var (token, refresh, expires) = await ExchangeAsync(server, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = SettingsCrypto.Decrypt(saved.RefreshEncrypted!, Key) ?? "",
            }, ct);
            saved.SecretEncrypted = SettingsCrypto.Encrypt(token, Key);
            saved.RefreshEncrypted = refresh is null ? saved.RefreshEncrypted : SettingsCrypto.Encrypt(refresh, Key);
            saved.ExpiresAt = expires;
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is PluginCatalog.PluginException or HttpRequestException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
