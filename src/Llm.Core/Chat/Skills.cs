using System.Text.Json.Serialization;

namespace Llm.Core.Chat;

/// <summary>Who uses a skill: its owner, the groups they share it with, or everyone (an admin's, or a plugin's).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SkillSharing>))]
public enum SkillSharing
{
    Personal = 0,
    Groups = 1,
    Company = 2,
}

/// <summary>
/// A skill, as Claude Code's are: a slash name (/review-mr), a title, a description of when it is for, instructions for
/// the model, and the parameters it takes (each named in the instructions as {{name}}; $ARGUMENTS is what was typed after
/// it). Used with /name and its parameters, the model is given its instructions, filled in; the chat shows the skill
/// and what it was given, not its text. A skill the model may use on its own is offered to it by its description. A
/// person's own (kept, or shared with groups), the company's (made by admins), or a plugin's (installed and removed with it).
/// </summary>
public sealed class Skill
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>Who made it; null for the company's and a plugin's.</summary>
    public Guid? UserId { get; set; }
    /// <summary>What follows the slash: lowercase letters, digits, - and _.</summary>
    public required string Name { get; set; }
    public required string Title { get; set; }
    /// <summary>What it is for and when to use it: shown in the / menu, and how the model knows when to use it.</summary>
    public string Description { get; set; } = "";
    /// <summary>What the model is told to do, with {{parameters}} and $ARGUMENTS.</summary>
    public required string Instructions { get; set; }
    /// <summary>Its parameters, JSON: <c>[{ name, description, required, default }]</c>.</summary>
    public string Parameters { get; set; } = "[]";
    /// <summary>The model may use it on its own when its description fits what was asked.</summary>
    public bool AutoUse { get; set; } = true;
    public SkillSharing Sharing { get; set; }
    /// <summary>The groups whose members may use it, when shared with groups.</summary>
    public List<Guid> Groups { get; set; } = [];
    /// <summary>The plugin's tool row it came with (McpServer): it is removed with it.</summary>
    public Guid? ServerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
