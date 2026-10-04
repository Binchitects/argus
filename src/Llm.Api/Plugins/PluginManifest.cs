using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Chat.Tools;

namespace Llm.Api.Plugins;

/// <summary>One of a plugin's settings, filled in by the admin who installs it.</summary>
/// <param name="Type">"text", "url" or "secret" (stored encrypted, never shown again).</param>
public sealed record PluginSetting(string Key, string Title, string Type, bool Required, string? Help);

/// <summary>Where a person signs in to the plugin's service, as a template ({setting} filled in).</summary>
public sealed record PluginOAuth(string Authorize, string Token, string[] Scopes);

/// <summary>
/// A plugin's manifest (plugin.yaml): what it is, where its tools are (an OpenAPI document in
/// the plugin, or an MCP server), how calls authenticate (one key for everyone, or each person's
/// own API key or OAuth account), which functions change something (they ask first), and the
/// settings an admin fills in, and the prompts it adds to the library (Markdown files in the
/// plugin). No plugin code runs in the app.
/// </summary>
public sealed partial record PluginManifest(
    string Name, string Version, string Title, string Description,
    string? OpenApi, string? Mcp, string Url,
    string? PersonAuth, string Header, string Value, PluginOAuth? OAuth, string? Help,
    string[] Writes, PluginSetting[] Settings, string[] Prompts)
{
    public const string FileName = "plugin.yaml";

    public sealed class ManifestException(string message) : Exception(message);

    public static PluginManifest Parse(string text)
    {
        JsonObject doc;
        try
        {
            doc = Chat.Tools.OpenApi.Parse(text);
        }
        catch (Chat.Tools.OpenApi.SpecException ex)
        {
            throw new ManifestException(ex.Message.Replace("OpenAPI document", "manifest", StringComparison.Ordinal));
        }
        string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : n is JsonValue o ? o.ToJsonString() : null;
        var name = Str(doc["name"]) ?? throw new ManifestException("The manifest has no name.");
        if (!PluginName().IsMatch(name))
        {
            throw new ManifestException($"\"{name}\" is not a plugin name: lowercase letters, digits and dashes, up to 50.");
        }
        var tools = doc["tools"] as JsonObject ?? throw new ManifestException("The manifest says no tools (tools: openapi: or mcp:).");
        var openapi = Str(tools["openapi"]);
        var mcp = Str(tools["mcp"]);
        if ((openapi is null) == (mcp is null))
        {
            throw new ManifestException("The tools are an OpenAPI document in the plugin (tools: openapi: file) or an MCP server (tools: mcp: address): one of them.");
        }
        if (openapi is not null && (openapi.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(openapi)))
        {
            throw new ManifestException("The OpenAPI document must be a file inside the plugin.");
        }
        var auth = doc["auth"] as JsonObject ?? [];
        var person = Str(auth["per_person"]) switch
        {
            null or "none" => null,
            "api_key" => "api_key",
            "oauth2" => "oauth2",
            var other => throw new ManifestException($"auth: per_person: {other} is not none, api_key or oauth2."),
        };
        PluginOAuth? oauth = null;
        if (person == "oauth2")
        {
            var o = auth["oauth2"] as JsonObject ?? throw new ManifestException("auth: oauth2: says where people sign in (authorize, token, scopes).");
            oauth = new PluginOAuth(Str(o["authorize"]) ?? throw new ManifestException("auth: oauth2: has no authorize address."),
                Str(o["token"]) ?? throw new ManifestException("auth: oauth2: has no token address."),
                [.. (o["scopes"] as JsonArray ?? []).Select(Str).OfType<string>()]);
        }
        var settings = (doc["settings"] as JsonArray ?? []).OfType<JsonObject>().Select(x => new PluginSetting(
            Str(x["key"]) ?? throw new ManifestException("A setting has no key."),
            Str(x["title"]) ?? Str(x["key"])!,
            Str(x["type"]) is { } t && t is "text" or "url" or "secret" ? t : "text",
            x["required"]?.GetValue<bool>() == true,
            Str(x["help"]))).ToArray();
        if (settings.GroupBy(x => x.Key).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            throw new ManifestException($"The setting {twice.Key} is there twice.");
        }
        string[] prompts = [.. (doc["prompts"] as JsonArray ?? []).Select(Str).OfType<string>()];
        if (prompts.FirstOrDefault(f => f.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(f)) is { } outside)
        {
            throw new ManifestException($"The prompt {outside} must be a file inside the plugin.");
        }
        return new PluginManifest(name, Str(doc["version"]) ?? "0", Str(doc["title"]) ?? name, Str(doc["description"]) ?? "",
            openapi, mcp, Str(tools["url"]) ?? mcp ?? "",
            person, Str(auth["header"]) ?? "Authorization", Str(auth["value"]) ?? "Bearer {token}", oauth, Str(auth["help"]),
            [.. (doc["writes"] as JsonArray ?? []).Select(Str).OfType<string>()], settings, prompts);
    }

    /// <summary>"{gitlab_url}/api/v4" with the settings' values put in.</summary>
    public static string Fill(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v.TrimEnd('/') : m.Value);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,49}$")]
    private static partial Regex PluginName();

    [GeneratedRegex(@"\{([a-z0-9_]+)\}")]
    private static partial Regex Placeholder();
}
