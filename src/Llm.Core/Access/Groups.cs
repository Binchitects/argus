using System.Text.Json.Serialization;

namespace Llm.Core.Access;

/// <summary>
/// A named set of people that access rules (tools, models) refer to. An app
/// group's members are chosen in the app; a directory group's members are
/// whoever the directory puts in it (<see cref="Directory"/>).
/// </summary>
public sealed class Group
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Description { get; set; }
    /// <summary>For a directory group: its common name or full DN. Null for an app group.</summary>
    public string? Directory { get; set; }
    /// <summary>Made by the company's identity provider through SCIM: it decides the name and the members.</summary>
    public bool Scim { get; set; }
    /// <summary>For a SCIM group: the identity provider's own id for it (externalId), when it sends one.</summary>
    public string? ExternalId { get; set; }
    /// <summary>Its members' place in the answers' line: higher goes first; 0 is everyone's. A person in several groups takes the highest.</summary>
    public int Priority { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Members' chats and their files are kept this many days, then deleted; null: the company's setting. The shortest of a person's groups applies.</summary>
    public int? RetentionDays { get; set; }

    /// <summary>What the group may spend in a calendar month on the chat's answers; null: no group limit of that kind (Credits).</summary>
    public decimal? ChatCredit { get; set; }
    /// <summary>... on its members' API keys' text requests.</summary>
    public decimal? ApiCredit { get; set; }
    /// <summary>... on pictures made.</summary>
    public decimal? PictureCredit { get; set; }
    /// <summary>... on videos made.</summary>
    public decimal? VideoCredit { get; set; }
    /// <summary>... on speech.</summary>
    public decimal? SpeechCredit { get; set; }

    /// <summary>Each credit is each member's, not shared by them all.</summary>
    public bool CreditPerMember { get; set; }

    /// <summary>The label its spend is charged to in the monthly chargeback report.</summary>
    public string? CostCentre { get; set; }

    /// <summary>Secret scanning for members: refuse, mask or off; null: the company's setting.</summary>
    public string? SecretScanning { get; set; }

    /// <summary>Personal data masked for members: mask or off; null: the company's setting.</summary>
    public string? RedactPii { get; set; }

    /// <summary>The model's check of members' messages: check or off; null: the company's setting.</summary>
    public string? Moderation { get; set; }

    /// <summary>Whether the blocked words apply to members; null: they do.</summary>
    public bool? BlockedPatterns { get; set; }

    /// <summary>Requests a minute each member's API key may send to the gateway; null: the company's setting, 0: no limit. A person in several groups gets the highest.</summary>
    public int? RequestsPerMinute { get; set; }

    /// <summary>Tokens a minute each member's API key may use at the gateway; null: the company's setting, 0: no limit. A person in several groups gets the highest.</summary>
    public int? TokensPerMinute { get; set; }
}

/// <summary>Someone an admin put in an app group.</summary>
public sealed class GroupMember
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Who may use something. Admins always may, so they can set it up and test it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Audience>))]
public enum Audience
{
    Everyone = 0,
    Admins = 1,
    /// <summary>Members of the listed groups.</summary>
    Groups = 2,
}

/// <summary>How far something a person made is shared (an assistant, a chat's link).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Reach>))]
public enum Reach
{
    /// <summary>Its owner (and the people they chose to edit it) only.</summary>
    Private = 0,
    /// <summary>Members of the listed groups.</summary>
    Groups = 1,
    /// <summary>Everyone who can sign in.</summary>
    Company = 2,
}
