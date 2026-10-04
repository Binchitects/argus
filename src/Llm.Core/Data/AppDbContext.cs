using Llm.Core.Access;
using Llm.Core.Chat;
using Llm.Core.Identity;
using Llm.Core.Models;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Llm.Core.Data;

public partial class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    /// <summary>Cookie, 2FA and OIDC key material survives restarts and is shared by every replica.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatAttachment> ChatAttachments => Set<ChatAttachment>();
    public DbSet<AttachmentPage> AttachmentPages => Set<AttachmentPage>();
    public DbSet<Assistant> Assistants => Set<Assistant>();
    public DbSet<QueuedMessage> QueuedMessages => Set<QueuedMessage>();
    public DbSet<SafeguardMark> SafeguardMarks => Set<SafeguardMark>();
    public DbSet<AssistantFile> AssistantFiles => Set<AssistantFile>();
    public DbSet<ChatShare> ChatShares => Set<ChatShare>();
    public DbSet<ChatShareView> ChatShareViews => Set<ChatShareView>();
    public DbSet<Canvas> Canvases => Set<Canvas>();
    public DbSet<CanvasVersion> CanvasVersions => Set<CanvasVersion>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<ToolSetting> ToolSettings => Set<ToolSetting>();
    public DbSet<McpServer> McpServers => Set<McpServer>();
    public DbSet<PersonCredential> PersonCredentials => Set<PersonCredential>();
    public DbSet<LocalModel> LocalModels => Set<LocalModel>();
    public DbSet<RemoteServer> RemoteServers => Set<RemoteServer>();
    public DbSet<ModelWindow> ModelWindows => Set<ModelWindow>();
    public DbSet<ModelDownload> ModelDownloads => Set<ModelDownload>();
    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();
    public DbSet<ScheduledRun> ScheduledRuns => Set<ScheduledRun>();
    public DbSet<TaskEvent> TaskEvents => Set<TaskEvent>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ModelAccess> ModelAccess => Set<ModelAccess>();
    public DbSet<Memory> Memories => Set<Memory>();
    public DbSet<SavedPrompt> Prompts => Set<SavedPrompt>();
    public DbSet<KnowledgeSource> KnowledgeSources => Set<KnowledgeSource>();
    public DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>();
    public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();
    public DbSet<KnowledgeReaders> KnowledgeReaders => Set<KnowledgeReaders>();
    public DbSet<AnswerFeedback> AnswerFeedback => Set<AnswerFeedback>();
    public DbSet<ArenaMatch> ArenaMatches => Set<ArenaMatch>();
    public DbSet<CachedAnswer> CachedAnswers => Set<CachedAnswer>();
    public DbSet<BotThread> BotThreads => Set<BotThread>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.UseOpenIddict<Guid>();

        builder.Entity<Setting>(e =>
        {
            e.ToTable("settings");
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasColumnName("key").HasMaxLength(200);
            e.Property(s => s.Value).HasColumnName("value");
            e.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        });

        builder.Entity<AppUser>(e =>
        {
            e.Property(u => u.DisplayName).HasMaxLength(200);
            e.Property(u => u.LdapDn).HasMaxLength(1000);
            e.Property(u => u.OidcSubject).HasMaxLength(255);
            e.Property(u => u.ScimExternalId).HasMaxLength(255);
            e.HasIndex(u => u.OidcSubject);
            e.Property(u => u.AnswerLength).HasMaxLength(16);
            e.Property(u => u.LegalHoldReason).HasMaxLength(500);
            e.Property(u => u.DirectoryGroups).HasDefaultValueSql("'{}'::text[]");
            e.HasIndex(u => u.NormalizedEmail).IsUnique();
        });

        builder.Entity<Conversation>(e =>
        {
            e.ToTable("conversations");
            e.Property(c => c.Title).HasMaxLength(200);
            e.Property(c => c.Thinking).HasMaxLength(20);
            e.Property(c => c.LoadedTools).HasDefaultValueSql("'{}'::text[]");
            e.Property(c => c.Model).HasMaxLength(200);
            e.Property(c => c.SystemPrompt).HasMaxLength(20000);
            e.HasIndex(c => new { c.UserId, c.UpdatedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Messages).WithOne().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
            // Assistants were projects: their tables and columns keep the old names.
            e.Property(c => c.AssistantId).HasColumnName("ProjectId");
            e.HasIndex(c => c.AssistantId);
            e.HasOne<Assistant>().WithMany().HasForeignKey(c => c.AssistantId).OnDelete(DeleteBehavior.SetNull);
            // A chat deleted under legal hold is kept, and hidden from every query but the hold's own (IgnoreQueryFilters).
            e.HasQueryFilter(c => c.DeletedAt == null);
        });
        builder.Entity<ChatMessage>(e =>
        {
            e.ToTable("chat_messages");
            e.Property(m => m.Role).HasMaxLength(20);
            e.Property(m => m.ToolCallId).HasMaxLength(200);
            e.Property(m => m.ToolName).HasMaxLength(200);
            e.Property(m => m.Model).HasMaxLength(200);
            e.HasIndex(m => new { m.ConversationId, m.Sequence }).IsUnique();
            e.HasIndex(m => new { m.ConversationId, m.ParentId });
            // Admin → Traces: the slowest answers of a time range.
            // Answers by when (the quality page), and the finished answers by when (the slowest, for traces).
            e.HasIndex(m => m.CreatedAt);
            e.HasIndex(m => m.CreatedAt, "IX_chat_messages_Answered").HasFilter("\"AnswerMs\" IS NOT NULL");
        });
        builder.Entity<ChatAttachment>(e =>
        {
            e.ToTable("chat_attachments");
            e.Property(a => a.FileName).HasMaxLength(260);
            e.Property(a => a.ContentType).HasMaxLength(200);
            e.Property(a => a.Kind).HasMaxLength(20);
            e.HasIndex(a => a.UserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<QueuedMessage>(e =>
        {
            e.ToTable("queued_messages");
            e.HasIndex(q => new { q.ConversationId, q.Position });
            e.HasOne<Conversation>().WithMany().HasForeignKey(q => q.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Assistant>(e =>
        {
            e.ToTable("projects");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Instructions).HasMaxLength(20_000);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.Thinking).HasMaxLength(20);
            e.Property(x => x.Starters).HasDefaultValueSql("'{}'::text[]");
            e.Property(x => x.Icon).HasMaxLength(20).HasDefaultValue("bot");
            e.Property(x => x.Color).HasMaxLength(20).HasDefaultValue("blue");
            e.Property(x => x.Groups).HasDefaultValueSql("'{}'::uuid[]");
            e.Property(x => x.EditorPeople).HasDefaultValueSql("'{}'::uuid[]");
            e.Property(x => x.EditorGroups).HasDefaultValueSql("'{}'::uuid[]");
            e.HasIndex(x => new { x.UserId, x.UpdatedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<AssistantFile>(e =>
        {
            e.ToTable("project_files");
            e.Property(x => x.AssistantId).HasColumnName("ProjectId");
            e.HasKey(x => new { x.AssistantId, x.AttachmentId });
            e.HasOne<Assistant>().WithMany().HasForeignKey(x => x.AssistantId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ChatAttachment>().WithMany().HasForeignKey(x => x.AttachmentId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ChatShare>(e =>
        {
            e.ToTable("chat_shares");
            e.Property(x => x.Groups).HasDefaultValueSql("'{}'::uuid[]");
            // One link per chat: sharing again changes it.
            e.HasIndex(x => x.ConversationId).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ChatShareView>(e =>
        {
            e.ToTable("chat_share_views");
            e.HasKey(x => new { x.ShareId, x.UserId });
            e.HasOne<ChatShare>().WithMany().HasForeignKey(x => x.ShareId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<SafeguardMark>(e =>
        {
            e.ToTable("safeguard_marks");
            e.Property(x => x.Kind).HasMaxLength(20);
            e.HasIndex(x => new { x.UserId, x.Kind, x.At });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<AttachmentPage>(e =>
        {
            e.ToTable("attachment_pages");
            e.HasKey(p => new { p.AttachmentId, p.Number });
            e.HasOne<ChatAttachment>().WithMany().HasForeignKey(p => p.AttachmentId).OnDelete(DeleteBehavior.Cascade);
        });
        QualityModel.Configure(builder);
        CanvasTables.Configure(builder);

        builder.Entity<Group>(e =>
        {
            e.ToTable("groups");
            e.Property(g => g.Name).HasMaxLength(100);
            e.Property(g => g.Description).HasMaxLength(500);
            e.Property(g => g.Directory).HasMaxLength(1000);
            e.Property(g => g.ExternalId).HasMaxLength(255);
            e.HasIndex(g => g.Name).IsUnique();
            e.Property(g => g.CostCentre).HasMaxLength(100);
            e.Property(g => g.SecretScanning).HasMaxLength(10);
            e.Property(g => g.RedactPii).HasMaxLength(10);
            e.Property(g => g.Moderation).HasMaxLength(10);
        });
        builder.Entity<GroupMember>(e =>
        {
            e.ToTable("group_members");
            e.HasKey(m => new { m.GroupId, m.UserId });
            e.HasIndex(m => m.UserId);
            e.HasOne<Group>().WithMany().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ToolSetting>(e =>
        {
            e.ToTable("tool_settings");
            e.HasKey(t => t.ToolId);
            e.Property(t => t.ToolId).HasMaxLength(100);
            e.Property(t => t.Groups).HasDefaultValueSql("'{}'::uuid[]");
        });
        builder.Entity<McpServer>(e =>
        {
            e.ToTable("mcp_servers");
            e.Property(m => m.Name).HasMaxLength(100);
            e.Property(m => m.Description).HasMaxLength(500);
            e.Property(m => m.Url).HasMaxLength(2000);
            e.Property(m => m.HeaderName).HasMaxLength(200);
            e.Property(m => m.EmailHeader).HasMaxLength(200);
            e.Property(m => m.Plugin).HasMaxLength(100);
            e.Property(m => m.PluginVersion).HasMaxLength(50);
            e.Property(m => m.PersonAuth).HasMaxLength(20);
            e.Property(m => m.Writes).HasDefaultValueSql("'{}'::text[]");
            e.HasIndex(m => m.Name).IsUnique();
        });
        builder.Entity<PersonCredential>(e =>
        {
            e.ToTable("person_credentials");
            e.Property(c => c.ToolId).HasMaxLength(100);
            e.Property(c => c.Account).HasMaxLength(200);
            e.HasIndex(c => new { c.UserId, c.ToolId }).IsUnique();
            e.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<LocalModel>(e =>
        {
            e.ToTable("local_models");
            e.HasKey(m => m.Name);
            e.Property(m => m.Name).HasMaxLength(100);
            e.Property(m => m.File).HasMaxLength(1000);
            e.Property(m => m.Projector).HasMaxLength(1000);
            e.Property(m => m.KvType).HasMaxLength(20);
            e.Property(m => m.Placement).HasMaxLength(10).HasDefaultValue("auto");
            e.Property(m => m.Devices).HasMaxLength(100);
            e.Property(m => m.DraftHead).HasMaxLength(1000);
            e.Property(m => m.DraftMax).HasDefaultValue(3);
            e.Property(m => m.ExtraPreset).HasMaxLength(4000);
        });
        builder.Entity<RemoteServer>(e =>
        {
            e.ToTable("remote_servers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.BaseUrl).HasMaxLength(1000);
            e.Property(x => x.ApiKeyProtected).HasMaxLength(4000);
            e.OwnsMany(x => x.Models, m => m.ToJson());
        });
        builder.Entity<ScheduledTask>(e =>
        {
            e.ToTable("scheduled_tasks");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Trigger).HasMaxLength(20).HasDefaultValue("schedule");
            e.Property(x => x.TriggerSecretHash).HasMaxLength(64);
            e.Property(x => x.Events).HasDefaultValueSql("'{}'::text[]");
            e.Property(x => x.Cron).HasMaxLength(200);
            e.Property(x => x.TimeZone).HasMaxLength(100);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.Thinking).HasMaxLength(50);
            e.Property(x => x.WebhookEncrypted).HasMaxLength(4000);
            e.Property(x => x.RunningOn).HasMaxLength(100);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.NextRunAt);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ScheduledRun>(e =>
        {
            e.ToTable("scheduled_runs");
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.Delivery).HasMaxLength(1000);
            e.HasIndex(x => new { x.TaskId, x.StartedAt });
            e.HasOne<ScheduledTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<TaskEvent>(e =>
        {
            e.ToTable("task_events");
            e.Property(x => x.ReplyKind).HasMaxLength(20);
            e.Property(x => x.ReplyId).HasMaxLength(100);
            e.HasIndex(x => new { x.TaskId, x.CreatedAt });
            e.HasOne<ScheduledTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Body).HasMaxLength(2000);
            e.Property(x => x.Link).HasMaxLength(500);
            e.Property(x => x.Kind).HasMaxLength(20).HasDefaultValue("task");
            e.Property(x => x.Key).HasMaxLength(300);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.Key }).IsUnique().HasFilter("\"Key\" IS NOT NULL");
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ModelDownload>(e =>
        {
            e.ToTable("model_downloads");
            e.Property(x => x.Repo).HasMaxLength(200);
            e.Property(x => x.Revision).HasMaxLength(100);
            e.Property(x => x.Dir).HasMaxLength(300);
            e.Property(x => x.State).HasMaxLength(20);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.CreatedBy).HasMaxLength(256);
            e.OwnsMany(x => x.Files, f => f.ToJson());
        });
        builder.Entity<ModelWindow>(e =>
        {
            e.ToTable("model_windows");
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.DefaultModel).HasMaxLength(200);
            e.Property(x => x.Days).HasDefaultValueSql("'{}'::integer[]");
            e.Property(x => x.Keep).HasDefaultValueSql("'{}'::text[]");
        });
        builder.Entity<ModelAccess>(e =>
        {
            e.ToTable("model_access");
            e.HasKey(m => m.Model);
            e.Property(m => m.Model).HasMaxLength(200);
            e.Property(m => m.Groups).HasDefaultValueSql("'{}'::uuid[]");
        });
        builder.Entity<BotThread>(e =>
        {
            e.ToTable("bot_threads");
            e.Property(x => x.Platform).HasMaxLength(20);
            e.Property(x => x.Thread).HasMaxLength(500);
            e.HasIndex(x => new { x.Platform, x.Thread, x.UserId }).IsUnique();
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<PushSubscription>(e =>
        {
            e.ToTable("push_subscriptions");
            e.Property(x => x.Endpoint).HasMaxLength(2000);
            e.Property(x => x.P256dh).HasMaxLength(200);
            e.Property(x => x.Auth).HasMaxLength(100);
            e.Property(x => x.Device).HasMaxLength(200);
            e.Property(x => x.LastError).HasMaxLength(500);
            e.HasIndex(x => x.Endpoint).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<CachedAnswer>(e =>
        {
            e.ToTable("answer_cache");
            e.Property(a => a.Hash).HasMaxLength(64);
            e.Property(a => a.KeyHash).HasMaxLength(64);
            e.Property(a => a.Model).HasMaxLength(200);
            e.HasIndex(a => a.Hash).IsUnique();
            e.HasIndex(a => a.KeyHash);
            e.HasIndex(a => a.ExpiresAt);
        });

        builder.Entity<Memory>(e =>
        {
            e.ToTable("memories");
            e.Property(m => m.Text).HasMaxLength(500);
            e.HasIndex(m => new { m.UserId, m.UpdatedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<SavedPrompt>(e =>
        {
            e.ToTable("prompts");
            e.Property(p => p.Name).HasMaxLength(40);
            e.Property(p => p.Title).HasMaxLength(100);
            e.Property(p => p.Text).HasMaxLength(20_000);
            e.Property(p => p.Groups).HasDefaultValueSql("'{}'::uuid[]");
            e.HasIndex(p => p.UserId);
            e.HasIndex(p => p.ServerId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<McpServer>().WithMany().HasForeignKey(p => p.ServerId).OnDelete(DeleteBehavior.Cascade);
        });
        KnowledgeModel(builder);

        builder.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.Property(a => a.Action).HasMaxLength(100);
            e.Property(a => a.Actor).HasMaxLength(256);
            e.Property(a => a.Target).HasMaxLength(256);
            e.Property(a => a.Ip).HasMaxLength(64);
            e.HasIndex(a => a.At);
        });
    }
}

/// <summary>A key/value the app owns at runtime (prices, feature switches, ...).</summary>
public class Setting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
