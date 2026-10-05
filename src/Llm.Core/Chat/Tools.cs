using System.Text.Json.Serialization;
using Llm.Core.Access;

namespace Llm.Core.Chat;

/// <summary>
/// An admin's choices for one tool: a built-in one ("argus", "image",
/// "calculator", "time") or an MCP server ("mcp:{id}"). A tool without a row
/// has the defaults: on, for everyone, on in new chats, runs without asking.
/// </summary>
public sealed class ToolSetting
{
    public required string ToolId { get; set; }
    public bool Enabled { get; set; } = true;
    public Audience Audience { get; set; }
    public List<Guid> Groups { get; set; } = [];
    /// <summary>On in a new chat; people can still switch it per chat.</summary>
    public bool OnByDefault { get; set; } = true;
    /// <summary>Each call waits for the person to allow it.</summary>
    public bool AskFirst { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>An MCP server, or an API by its OpenAPI document, that an admin added: its tools become a tool in the chat.</summary>
public sealed class McpServer
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Description { get; set; }
    /// <summary>Its streamable HTTP endpoint, e.g. https://tools.example.com/mcp.</summary>
    public required string Url { get; set; }
    /// <summary>A header sent with every request, e.g. Authorization; its value is encrypted (Settings crypto).</summary>
    public string? HeaderName { get; set; }
    public string? HeaderValueEncrypted { get; set; }
    /// <summary>When set, the person's email goes in this header, for servers that answer per person.</summary>
    public string? EmailHeader { get; set; }
    /// <summary>Longest one call may take, in minutes; null: Chat:ToolCallTimeout. Some tools run for an hour.</summary>
    public int? CallTimeoutMinutes { get; set; }
    /// <summary>An API's OpenAPI document (JSON or YAML): then it is an API, Url is where it is, and its operations are the tools.</summary>
    public string? Spec { get; set; }
    /// <summary>The plugin it was installed from (its name), with its version and manifest; null for one an admin added by hand.</summary>
    public string? Plugin { get; set; }
    public string? PluginVersion { get; set; }
    public string? Manifest { get; set; }
    /// <summary>The admin's values for the plugin's settings, as JSON; a secret's value is encrypted (Settings crypto).</summary>
    public string? PluginSettings { get; set; }
    /// <summary>"api_key" or "oauth2": each person connects their own account, and calls go as them; null: one key for everyone (the header).</summary>
    public string? PersonAuth { get; set; }
    /// <summary>Functions (their own names, without the prefix) that change something: each call asks the person first.</summary>
    public List<string> Writes { get; set; } = [];
    /// <summary>How its HTTPS certificate is checked: against the system's CAs, its own CA (<see cref="TlsCa"/>), or not at all.</summary>
    public TlsCheck Tls { get; set; }
    /// <summary>The CA it must chain to, for <see cref="TlsCheck.OwnCa"/>: certificates in PEM (public; kept out of logs all the same).</summary>
    public string? TlsCa { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>How an admin's server's HTTPS certificate is checked.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TlsCheck>))]
public enum TlsCheck
{
    /// <summary>Against the CAs the system trusts: the default.</summary>
    System = 0,
    /// <summary>Its chain must lead to the CA the admin gave, and the host name must still match.</summary>
    OwnCa = 1,
    /// <summary>Any certificate is accepted: encrypted, but whoever is in the way could pose as the server.</summary>
    Off = 2,
}

/// <summary>A person's own account at a plugin's service: an API key, or OAuth tokens. Encrypted (Settings crypto).</summary>
public sealed class PersonCredential
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    /// <summary>The tool it is for ("mcp:{server id}").</summary>
    public required string ToolId { get; set; }
    public required string SecretEncrypted { get; set; }
    public string? RefreshEncrypted { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>Who the service says it is, when it says (for the person's own page).</summary>
    public string? Account { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
