using Llm.Core.Access;
using Llm.Core.Chat;
using Llm.Core.Identity;
using Llm.Core.Models;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Llm.Core.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
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
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<SafeguardMark> SafeguardMarks => Set<SafeguardMark>();
    public DbSet<ProjectFile> ProjectFiles => Set<ProjectFile>();
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
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ModelAccess> ModelAccess => Set<ModelAccess>();

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
            e.Property(u => u.AnswerLength).HasMaxLength(16);
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
            e.HasIndex(c => c.ProjectId);
            e.HasOne<Project>().WithMany().HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.SetNull);
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
        builder.Entity<Project>(e =>
        {
            e.ToTable("projects");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Instructions).HasMaxLength(20_000);
            e.HasIndex(x => new { x.UserId, x.UpdatedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ProjectFile>(e =>
        {
            e.ToTable("project_files");
            e.HasKey(x => new { x.ProjectId, x.AttachmentId });
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ChatAttachment>().WithMany().HasForeignKey(x => x.AttachmentId).OnDelete(DeleteBehavior.Cascade);
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

        builder.Entity<Group>(e =>
        {
            e.ToTable("groups");
            e.Property(g => g.Name).HasMaxLength(100);
            e.Property(g => g.Description).HasMaxLength(500);
            e.Property(g => g.Directory).HasMaxLength(1000);
            e.HasIndex(g => g.Name).IsUnique();
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
