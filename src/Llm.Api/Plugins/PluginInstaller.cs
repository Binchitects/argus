using System.Text.Json.Nodes;
using Llm.Api.Settings;
using Llm.Core.Chat;

namespace Llm.Api.Plugins;

/// <summary>
/// A plugin installed is an admin's tool row (McpServer) made from its manifest: its address,
/// its OpenAPI document or MCP server, how calls sign in, which functions ask first, and the
/// admin's values for its settings (secrets encrypted). Who may use it, and asking first for
/// everything, are set on its card in Admin → Tools as for any tool.
/// </summary>
public static class PluginInstaller
{
    /// <summary>Puts the package and the settings into the row (new or installed before). Why not, or null.</summary>
    /// <param name="given">The admin's values; a secret left empty keeps the one saved.</param>
    public static string? Apply(McpServer server, PluginPackage package, IReadOnlyDictionary<string, string?> given, string? dataKey)
    {
        var manifest = package.Manifest;
        var saved = Stored(server);
        var values = new JsonObject();
        foreach (var setting in manifest.Settings)
        {
            var value = given.GetValueOrDefault(setting.Key)?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                if (saved[setting.Key] is { } kept)
                {
                    values[setting.Key] = kept.DeepClone();
                    continue;
                }
                if (setting.Required)
                {
                    return $"{setting.Title} is required.";
                }
                continue;
            }
            if (setting.Type == "url" && !(Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
            {
                return $"{setting.Title} must be an http(s) URL.";
            }
            if (setting.Type == "secret")
            {
                if (string.IsNullOrEmpty(dataKey))
                {
                    return "APP_DATA_KEY is not set, so a secret cannot be stored encrypted.";
                }
                values[setting.Key] = new JsonObject { ["encrypted"] = SettingsCrypto.Encrypt(value, dataKey) };
            }
            else
            {
                values[setting.Key] = value;
            }
        }
        server.PluginSettings = values.ToJsonString();
        var plain = Values(server, dataKey);
        var url = PluginManifest.Fill(manifest.Url, plain);
        if (!(Uri.TryCreate(url, UriKind.Absolute, out var address) && address.Scheme is "http" or "https"))
        {
            return $"The plugin's address ({url}) is not an http(s) URL: check its settings.";
        }
        if (package.Spec is { } spec)
        {
            try
            {
                if (Chat.Tools.OpenApi.Operations(Chat.Tools.OpenApi.Parse(spec), "").Count == 0)
                {
                    return "The plugin's OpenAPI document lists no operations.";
                }
            }
            catch (Chat.Tools.OpenApi.SpecException ex)
            {
                return ex.Message;
            }
        }
        if (server.Name.Length == 0)
        {
            server.Name = manifest.Title;
        }
        server.Description = manifest.Description.Length > 0 ? manifest.Description[..Math.Min(500, manifest.Description.Length)] : null;
        server.Url = url;
        server.Spec = package.Spec;
        server.Plugin = manifest.Name;
        server.PluginVersion = manifest.Version;
        server.Manifest = package.ManifestText;
        server.PersonAuth = manifest.PersonAuth;
        server.Writes = [.. manifest.Writes];
        server.HeaderName = manifest.Header;
        // One key for everyone: the header's value from the settings ({token} is the "token" setting).
        server.HeaderValueEncrypted = manifest.PersonAuth is null && plain.ContainsKey("token") && !string.IsNullOrEmpty(dataKey)
            ? SettingsCrypto.Encrypt(PluginManifest.Fill(manifest.Value, plain), dataKey)
            : null;
        return null;
    }

    /// <summary>The settings' values, secrets decrypted.</summary>
    public static IReadOnlyDictionary<string, string> Values(McpServer server, string? dataKey)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, node) in Stored(server))
        {
            var value = node is JsonObject o && o["encrypted"]?.GetValue<string>() is { } enc ? SettingsCrypto.Decrypt(enc, dataKey) : node?.GetValue<string>();
            if (value is not null)
            {
                values[key] = value;
            }
        }
        return values;
    }

    /// <summary>For the admin's page: each setting, and its value unless it is a secret (then only whether it is set).</summary>
    public static JsonArray View(McpServer server)
    {
        var saved = Stored(server);
        return new JsonArray([.. PluginManifest.Parse(server.Manifest!).Settings.Select(s => (JsonNode)new JsonObject
        {
            ["key"] = s.Key, ["title"] = s.Title, ["type"] = s.Type, ["required"] = s.Required, ["help"] = s.Help,
            ["value"] = s.Type == "secret" ? null : saved[s.Key]?.DeepClone(), ["set"] = saved[s.Key] is not null,
        })]);
    }

    private static JsonObject Stored(McpServer server) =>
        server.PluginSettings is { Length: > 0 } json && JsonNode.Parse(json) is JsonObject o ? o : [];
}
