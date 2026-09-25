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
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Someone an admin put in an app group.</summary>
public sealed class GroupMember
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Who may use something. Admins always may, so they can set it up and test it.</summary>
public enum Audience
{
    Everyone = 0,
    Admins = 1,
    /// <summary>Members of the listed groups.</summary>
    Groups = 2,
}
