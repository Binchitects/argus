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
    /// <summary>What runs it: "schedule" (its cron), "webhook" (any system posting to its address) or "gitlab" (GitLab's events).</summary>
    public string Trigger { get; set; } = "schedule";
    /// <summary>The SHA-256 of the secret a webhook or GitLab sends with each event.</summary>
    public string? TriggerSecretHash { get; set; }
    /// <summary>The GitLab events it takes: merge_request, pipeline_failed, issue.</summary>
    public List<string> Events { get; set; } = [];
    /// <summary>Its answer goes back to GitLab as a comment on the merge request, issue or commit (by the GitLab bot, never Argus's token).</summary>
    public bool ReplyInGitLab { get; set; }
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
    /// <summary>What it is about: answer (one written while nobody watched), task, usage (credit), alert (the system's, for admins), download.</summary>
    public string Kind { get; set; } = "task";
    public required string Title { get; set; }
    public string? Body { get; set; }
    /// <summary>Where it leads in the app, e.g. /chat/{id}.</summary>
    public string? Link { get; set; }
    /// <summary>What it is the news of, once per person (e.g. one alert's firing, a credit threshold): it is not said twice.</summary>
    public string? Key { get; set; }
    /// <summary>Cleared by the person: news said once (with a key) stays as a record, hidden, so it is not said again.</summary>
    public bool Cleared { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
}
