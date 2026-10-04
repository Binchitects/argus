namespace Llm.Core.Chat;

/// <summary>
/// A thread on a chat platform (a Slack, Mattermost or Teams thread) or an email
/// subject, and the chat its questions go on in: one per thread and person, owned by
/// that person.
/// </summary>
public sealed class BotThread
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>slack, mattermost, teams or email.</summary>
    public required string Platform { get; set; }
    /// <summary>The thread as the platform names it (its channel and first message), or an email's subject without Re: and Fwd:.</summary>
    public required string Thread { get; set; }
    public Guid UserId { get; set; }
    public Guid ConversationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A browser's Web Push subscription: where the bell's news is pushed for one device of a person.</summary>
public sealed class PushSubscription
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    /// <summary>The push service's address for this browser (it is a secret of sorts: whoever has it and the keys can push to it).</summary>
    public required string Endpoint { get; set; }
    /// <summary>The browser's P-256 public key, base64url (RFC 8291's ua_public).</summary>
    public required string P256dh { get; set; }
    /// <summary>The browser's 16-byte authentication secret, base64url.</summary>
    public required string Auth { get; set; }
    /// <summary>What the person sees it as: the browser and system it was made on.</summary>
    public string? Device { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSentAt { get; set; }
    /// <summary>Why the last push to it failed, if it did.</summary>
    public string? LastError { get; set; }
}
