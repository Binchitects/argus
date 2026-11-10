using Llm.Core.Identity;

namespace Llm.Core.Notifications;

/// <summary>
/// A message an admin sends to everyone, or to some groups: it rings their bell (and their phone, when it may), shows above
/// every page of the Arena and in Code Arena until they dismiss it or it ends, and stays listed for the admins.
/// </summary>
public sealed class Broadcast
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";
    public static readonly string[] Levels = [Info, Warning, Critical];

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Title { get; set; }
    public string? Body { get; set; }
    /// <summary>info, warning or critical (a critical one cannot be dismissed while it lasts).</summary>
    public string Level { get; set; } = Info;
    /// <summary>The groups it is for; empty: everyone.</summary>
    public Guid[] Groups { get; set; } = [];
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>When it ends by itself; null: when an admin ends it.</summary>
    public DateTimeOffset? Until { get; set; }
    /// <summary>When an admin ended it.</summary>
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>How many people it was sent to.</summary>
    public int Sent { get; set; }
}

/// <summary>A person dismissed a broadcast: it no longer shows for them.</summary>
public sealed class BroadcastDismissal
{
    public Guid BroadcastId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
