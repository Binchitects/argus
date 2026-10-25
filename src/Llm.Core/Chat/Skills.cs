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
    /// <summary>One that comes with Arena (the repository's skills/ folder): the company's, kept as Arena writes it.</summary>
    public bool BuiltIn { get; set; }
    /// <summary>A built-in one an admin turned off: nobody uses it until it is turned on again.</summary>
    public bool Off { get; set; }
    /// <summary>Learned from the person's work, waiting for them: not offered to the model until they keep it.</summary>
    public bool Proposed { get; set; }
    /// <summary>typed (written by a person), or learned (from a chat or Code Arena session that worked out a procedure).</summary>
    public string Source { get; set; } = Typed;
    /// <summary>The chat it was learned from.</summary>
    public Guid? SourceId { get; set; }
    /// <summary>Its version: one more each time a better one is applied (or undone).</summary>
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public const string Typed = "typed";
    public const string Learned = "learned";
}

/// <summary>
/// How a skill learned or got better from the person's work (Skills → its history): a skill learned from a chat, or a better
/// version of one used there (it went wrong, or the person corrected it). A proposal waits for the person (Apply, or Not
/// now); one applied keeps what the skill was, so Undo puts it back.
/// </summary>
public sealed class SkillChange
{
    public const string LearnedAction = "learned";
    public const string Improved = "improved";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SkillId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>learned or improved.</summary>
    public required string Action { get; set; }
    /// <summary>Why, in a sentence (what went wrong, what the person corrected, what the chat worked out).</summary>
    public string Reason { get; set; } = "";
    /// <summary>The skill as it was (JSON: title, description, instructions, parameters); null for one learned.</summary>
    public string? Before { get; set; }
    /// <summary>The skill as it is (or would be) after (the same JSON).</summary>
    public required string After { get; set; }
    /// <summary>The chat it came from.</summary>
    public Guid? SourceId { get; set; }
    /// <summary>Waiting for the person.</summary>
    public bool Proposed { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AppliedAt { get; set; }
    public DateTimeOffset? UndoneAt { get; set; }
}

