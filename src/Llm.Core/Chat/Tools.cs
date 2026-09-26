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

/// <summary>An MCP server an admin added: its tools become a tool in the chat.</summary>
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
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
