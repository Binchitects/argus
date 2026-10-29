using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Core.Safeguards;

/// <summary>
/// One safeguard rule (Admin → Safeguards): a detector, where it looks (questions, files, answers, tool calls and their
/// results, the API), whom it applies to (everyone, or groups and people), with which models, and what it does when it
/// finds something. Rules run in their order; "allow" before another rule of the same detector exempts what it covers. A
/// rule in monitor mode only records what it would have done.
/// </summary>
public sealed class SafeguardRule
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Records what it would have done, and does nothing.</summary>
    public bool Monitor { get; set; }

    /// <summary>secrets, pii, words, topic, injection, links or files.</summary>
    public required string Detector { get; set; }
    /// <summary>question, file, answer, tool_call, tool_result, api.</summary>
    public List<string> Stages { get; set; } = [];
    /// <summary>The groups and people it applies to; both empty: everyone.</summary>
    public List<Guid> Groups { get; set; } = [];
    public List<Guid> People { get; set; } = [];
    /// <summary>The models it applies to: names, "*" patterns, or "@external" (any model on another server or provider); empty: all.</summary>
    public List<string> Models { get; set; } = [];

    /// <summary>block, mask, warn, justify, log, reroute or allow.</summary>
    public required string Action { get; set; }
    /// <summary>What the person is told; null: the company's refusal (or warning).</summary>
    public string? Message { get; set; }
    /// <summary>For reroute: the model the question goes to instead.</summary>
    public string? RerouteModel { get; set; }
    /// <summary>A block counts towards the person's suspension (Safeguards:StrikesToSuspend).</summary>
    public bool Strike { get; set; }

    /// <summary>secrets: the kinds it looks for; pii: email, phone, card, iban, ip, ssn. Empty: all.</summary>
    public List<string> Kinds { get; set; } = [];
    /// <summary>words: words, phrases or "re:" patterns, separated by ";" or new lines.</summary>
    public string? Patterns { get; set; }
    /// <summary>topic: the subjects it refuses, one a line ("name: what it covers").</summary>
    public string? Topics { get; set; }
    /// <summary>topic: the built-in harms it looks for (violence, self-harm, sexual-minors, weapons, malware, hate, fraud).</summary>
    public List<string> Categories { get; set; } = [];
    /// <summary>links: domains (a domain covers its subdomains).</summary>
    public List<string> Domains { get; set; } = [];
    /// <summary>links: the domains are the only ones allowed; otherwise they are the ones refused.</summary>
    public bool DomainsAllowed { get; set; }
    /// <summary>files: extensions refused (".exe").</summary>
    public List<string> Extensions { get; set; } = [];
    /// <summary>files: the largest file taken, in MB.</summary>
    public int? MaxFileMb { get; set; }
    /// <summary>injection (and topic): the model reads the text too, beside the patterns.</summary>
    public bool ModelCheck { get; set; }

    /// <summary>The plugin that brought it (its name); it goes when the plugin does.</summary>
    public string? Plugin { get; set; }
    public Guid? CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static readonly string[] AllDetectors = ["secrets", "pii", "words", "topic", "injection", "links", "files", "commands", "exfiltration"];
    public static readonly string[] AllStages = ["question", "file", "answer", "tool_call", "tool_result", "api"];
    public static readonly string[] AllActions = ["block", "mask", "warn", "justify", "log", "reroute", "allow"];
}

/// <summary>
/// What a rule did, or would have done in monitor mode: whose text, where, which rule, what it found (kinds and matched
/// words, never a secret or personal data) and an excerpt for the review when the text holds neither. The person may
/// ask for a refusal to be reviewed; an admin allows it (the same text then passes that rule for a day) or confirms it.
/// </summary>
public sealed class SafeguardEvent
{
    public const string Asked = "asked";
    public const string Allowed = "allowed";
    public const string Confirmed = "confirmed";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? RuleId { get; set; }
    public string RuleName { get; set; } = "";
    /// <summary>Null for a key the app does not know.</summary>
    public Guid? UserId { get; set; }
    public required string Stage { get; set; }
    public required string Detector { get; set; }
    /// <summary>blocked, masked, warned, justified, logged, rerouted, allowed.</summary>
    public required string Outcome { get; set; }
    public bool Monitor { get; set; }
    public string? Detail { get; set; }
    public string? Excerpt { get; set; }
    /// <summary>The reason the person gave (justify), or the reason they asked for a review.</summary>
    public string? Justification { get; set; }
    public string? Model { get; set; }
    public Guid? ConversationId { get; set; }
    /// <summary>The text's SHA-256, for an allowed review: the same text passes the rule until <see cref="AllowedUntil"/>.</summary>
    public string? TextHash { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    public string? Review { get; set; }
    public string? ReviewReason { get; set; }
    public Guid? ReviewedById { get; set; }
    public string? ReviewNote { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset? AllowedUntil { get; set; }
}

public static class SafeguardTables
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<SafeguardRule>(e =>
        {
            e.ToTable("safeguard_rules");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Detector).HasMaxLength(20);
            e.Property(x => x.Action).HasMaxLength(20);
            e.Property(x => x.Message).HasMaxLength(2000);
            e.Property(x => x.RerouteModel).HasMaxLength(200);
            e.Property(x => x.Plugin).HasMaxLength(100);
            e.HasIndex(x => x.Order);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<SafeguardEvent>(e =>
        {
            e.ToTable("safeguard_events");
            e.Property(x => x.RuleName).HasMaxLength(200);
            e.Property(x => x.Stage).HasMaxLength(20);
            e.Property(x => x.Detector).HasMaxLength(20);
            e.Property(x => x.Outcome).HasMaxLength(20);
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.Property(x => x.Excerpt).HasMaxLength(2000);
            e.Property(x => x.Justification).HasMaxLength(2000);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.TextHash).HasMaxLength(64);
            e.Property(x => x.Review).HasMaxLength(20);
            e.Property(x => x.ReviewReason).HasMaxLength(2000);
            e.Property(x => x.ReviewNote).HasMaxLength(2000);
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.UserId, x.At });
            e.HasIndex(x => x.Review);
            e.HasOne<SafeguardRule>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ReviewedById).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
