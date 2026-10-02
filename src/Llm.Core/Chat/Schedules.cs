namespace Llm.Core.Chat;

/// <summary>
/// A question asked on a schedule, as its owner, with their model and tools: a daily
/// digest, a weekly report. Each run is a chat (a new one, or the same one carried on),
/// a notification, and if asked an email and a webhook post.
/// </summary>
public sealed class ScheduledTask
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required string Name { get; set; }
    public required string Prompt { get; set; }
    /// <summary>Five-field cron, in <see cref="TimeZone"/>'s wall clock.</summary>
    public required string Cron { get; set; }
    /// <summary>An IANA zone.</summary>
    public string TimeZone { get; set; } = "UTC";
    /// <summary>null: the model a new chat would use.</summary>
    public string? Model { get; set; }
    public string? Thinking { get; set; }
    /// <summary>null: the tools on in new chats.</summary>
    public List<string>? Tools { get; set; }
    /// <summary>Every run carries on one chat (it reads the runs before), instead of a new chat each time.</summary>
    public bool SameChat { get; set; }
    /// <summary>The chat runs carry on, when <see cref="SameChat"/>.</summary>
    public Guid? ConversationId { get; set; }
    /// <summary>Email the answer to the owner.</summary>
    public bool Email { get; set; }
    /// <summary>A URL the answer is posted to (Slack, Teams, Mattermost), encrypted: such a URL is a secret.</summary>
    public string? WebhookEncrypted { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One run of a scheduled task: when, how it went, its chat, and what was delivered.</summary>
public sealed class ScheduledRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TaskId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>running, done, failed, skipped.</summary>
    public string Status { get; set; } = "running";
    public Guid? ConversationId { get; set; }
    public string? Error { get; set; }
    /// <summary>Run by its owner's "Run now", not by the clock.</summary>
    public bool Manual { get; set; }
    /// <summary>What was sent where: "email sent", "webhook: HTTP 404".</summary>
    public string? Delivery { get; set; }
}

/// <summary>Something for a person to see: a scheduled task's answer. Shown under the bell.</summary>
public sealed class Notification
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required string Title { get; set; }
    public string? Body { get; set; }
    /// <summary>Where it leads in the app, e.g. /chat/{id}.</summary>
    public string? Link { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
}
