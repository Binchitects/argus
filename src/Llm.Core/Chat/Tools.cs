using System.Text.Json.Serialization;
using Llm.Core.Access;

namespace Llm.Core.Chat;

/// <summary>
/// An admin's choices for one tool: a built-in one ("argus", "image",
/// "calculator", "time") or an MCP server ("mcp:{id}"). A tool without a row
/// has the defaults: on, on in new chats, runs without asking. Who may use it
/// is access rules on the tool (<see cref="Access.ResourceKinds.Tool"/>).
/// </summary>
public sealed class ToolSetting
{
    public required string ToolId { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>On in a new chat; people can still switch it per chat.</summary>
    public bool OnByDefault { get; set; } = true;
    /// <summary>Each call waits for the person to allow it.</summary>
    public bool AskFirst { get; set; }

    /// <summary>The name and description people see, instead of the tool's own; null: its own.</summary>
    public string? Title { get; set; }
    public string? Description { get; set; }
    /// <summary>The admin's words to the model about the tool: added to its own, or in place of them.</summary>
    public string? Instructions { get; set; }
    public bool ReplaceInstructions { get; set; }
    /// <summary>Its functions (by their own names) the model is not offered.</summary>
    public List<string> DisabledFunctions { get; set; } = [];
    /// <summary>Its functions whose every call waits for the person to allow it.</summary>
    public List<string> AskFirstFunctions { get; set; } = [];
    /// <summary>
    /// Per function, as JSON: {"function": {"description": "...", "fixed": {"arg": value}, "defaults": {"arg": value}}}. A fixed
    /// argument is always sent as the admin set it (and the model is not offered it); a default fills one the model left out.
    /// </summary>
    public string? FunctionsJson { get; set; }
    /// <summary>Calls one person may make in a day; null or 0: no limit.</summary>
    public int? CallsPerDay { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A tool's settings for one group's members, over the company's: on in new chats, asking first, calls a day.</summary>
public sealed class ToolGroupSetting
{
    public required string ToolId { get; set; }
    public Guid GroupId { get; set; }
    public bool? OnByDefault { get; set; }
    public bool? AskFirst { get; set; }
    public int? CallsPerDay { get; set; }
}

/// <summary>One call of a tool: by whom, which function, how long, and whether it failed (its error, short). For quotas, health and numbers.</summary>
public sealed class ToolCall
{
    public long Id { get; set; }
    public required string ToolId { get; set; }
    public required string Function { get; set; }
    public Guid? UserId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public int DurationMs { get; set; }
    public bool Failed { get; set; }
    public string? Error { get; set; }
}

/// <summary>A named set of tools a chat starts with, or switches to (Admin → Tools → Toolsets); for everyone, or some groups.</summary>
public sealed class Toolset
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public List<string> Tools { get; set; } = [];
    /// <summary>The groups that see it; empty: everyone.</summary>
    public List<Guid> Groups { get; set; } = [];
    /// <summary>New chats of these groups' members start with it.</summary>
    public List<Guid> DefaultFor { get; set; } = [];
    /// <summary>The plugin that brought it, when one did.</summary>
    public string? Plugin { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A tool an admin made without code (Admin → Tools → New tool): an HTTP request to an address the admin fixed, its
/// parameters filling its path, query or body; or a prompt run on a model of the admin's choice.
/// </summary>
public sealed class CustomTool
{
    public const string Http = "http";
    public const string Prompt = "prompt";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public string Title { get; set; } = "";
    /// <summary>What it does, for the model (and people).</summary>
    public string Description { get; set; } = "";
    /// <summary>Its parameters, as JSON: [{"name", "type" (string, number, boolean), "description", "required"}].</summary>
    public string ParametersJson { get; set; } = "[]";
    // http
    public string? Method { get; set; }
    /// <summary>The address, its parameters as {name} in the path or query; the scheme and host are fixed.</summary>
    public string? UrlTemplate { get; set; }
    /// <summary>Headers as JSON {"name": "value"}, encrypted (Settings crypto): keys and tokens.</summary>
    public string? HeadersEncrypted { get; set; }
    /// <summary>The body, a JSON template with {name} placeholders; null: none.</summary>
    public string? BodyTemplate { get; set; }
    /// <summary>The part of a JSON answer to keep (a dotted path: data.items); null: all of it.</summary>
    public string? ResultPath { get; set; }
    /// <summary>Calls that change something wait for the person.</summary>
    public bool AskFirst { get; set; }
    // prompt
    public string? Model { get; set; }
    public string? PromptTemplate { get; set; }
    public string? SystemPrompt { get; set; }
    public Guid? CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
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
    /// <summary>What the admin granted the plugin at install (and at updates that asked for more).</summary>
    public List<string> PluginPermissions { get; set; } = [];
    /// <summary>Kept at its version: no update is offered.</summary>
    public bool PluginPinned { get; set; }
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

/// <summary>A version of a plugin as it was installed: its files, so it can be put back (rollback).</summary>
public sealed class PluginRelease
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ServerId { get; set; }
    public required string Version { get; set; }
    /// <summary>Every file the version named, as JSON {"name": "text"}.</summary>
    public required string FilesJson { get; set; }
    public List<string> Permissions { get; set; } = [];
    public Guid? InstalledById { get; set; }
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
}
