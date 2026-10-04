using System.Text.Json.Serialization;

namespace Llm.Core.Chat;

/// <summary>Who uses a prompt of the library: its owner, the groups they share it with, or everyone (an admin's, or a plugin's).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PromptSharing>))]
public enum PromptSharing
{
    Personal = 0,
    Groups = 1,
    Company = 2,
}

/// <summary>
/// A prompt of the library: a slash name (/review), a title, and a text with {{variables}}
/// that the composer asks for and fills in before sending. A person's own (kept, or shared
/// with groups to use), the company's (made by admins, owned by nobody), or a plugin's
/// (installed and removed with it).
/// </summary>
public sealed class SavedPrompt
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>Who made it; null for the company's and a plugin's.</summary>
    public Guid? UserId { get; set; }
    /// <summary>What follows the slash: lowercase letters, digits, - and _.</summary>
    public required string Name { get; set; }
    public required string Title { get; set; }
    public required string Text { get; set; }
    public PromptSharing Sharing { get; set; }
    /// <summary>The groups whose members may use it, when shared with groups.</summary>
    public List<Guid> Groups { get; set; } = [];
    /// <summary>The plugin's tool row it came with (McpServer): it is removed with it.</summary>
    public Guid? ServerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
