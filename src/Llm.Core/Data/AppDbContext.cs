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
    public DbSet<Safeguards.SafeguardRule> SafeguardRules => Set<Safeguards.SafeguardRule>();
    public DbSet<Safeguards.SafeguardEvent> SafeguardEvents => Set<Safeguards.SafeguardEvent>();
    public DbSet<AssistantFile> AssistantFiles => Set<AssistantFile>();
    public DbSet<ChatShare> ChatShares => Set<ChatShare>();
    public DbSet<RoomMember> RoomMembers => Set<RoomMember>();
    public DbSet<RoomGroup> RoomGroups => Set<RoomGroup>();
    public DbSet<RoomInvite> RoomInvites => Set<RoomInvite>();
    public DbSet<Code.CodeReview> CodeReviews => Set<Code.CodeReview>();
    public DbSet<Plans.Plan> Plans => Set<Plans.Plan>();
    public DbSet<Plans.PlanStep> PlanSteps => Set<Plans.PlanStep>();
    public DbSet<Code.ReviewFinding> ReviewFindings => Set<Code.ReviewFinding>();
    public DbSet<Code.SecurityScan> SecurityScans => Set<Code.SecurityScan>();
    public DbSet<Code.SecurityFinding> SecurityFindings => Set<Code.SecurityFinding>();
    public DbSet<Code.ReviewRule> ReviewRules => Set<Code.ReviewRule>();
    public DbSet<ChatShareView> ChatShareViews => Set<ChatShareView>();
    public DbSet<Canvas> Canvases => Set<Canvas>();
    public DbSet<CanvasVersion> CanvasVersions => Set<CanvasVersion>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<ToolSetting> ToolSettings => Set<ToolSetting>();
    public DbSet<ToolGroupSetting> ToolGroupSettings => Set<ToolGroupSetting>();
    public DbSet<ToolCall> ToolCalls => Set<ToolCall>();
    public DbSet<Toolset> Toolsets => Set<Toolset>();
    public DbSet<CustomTool> CustomTools => Set<CustomTool>();
    public DbSet<PluginRelease> PluginReleases => Set<PluginRelease>();
    public DbSet<PluginGroupSetting> PluginGroupSettings => Set<PluginGroupSetting>();
    public DbSet<McpServer> McpServers => Set<McpServer>();
    public DbSet<PersonCredential> PersonCredentials => Set<PersonCredential>();
    public DbSet<LocalModel> LocalModels => Set<LocalModel>();
    public DbSet<MediaModel> MediaModels => Set<MediaModel>();
    public DbSet<RemoteServer> RemoteServers => Set<RemoteServer>();
    public DbSet<ModelWindow> ModelWindows => Set<ModelWindow>();
    public DbSet<ModelToolMode> ModelToolModes => Set<ModelToolMode>();
    public DbSet<ModelDownload> ModelDownloads => Set<ModelDownload>();
    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();
    public DbSet<TaskTemplate> TaskTemplates => Set<TaskTemplate>();
    public DbSet<Apps.ArenaApp> Apps => Set<Apps.ArenaApp>();
    public DbSet<Apps.ArenaAppVersion> AppVersions => Set<Apps.ArenaAppVersion>();
    public DbSet<Apps.ArenaAppData> AppData => Set<Apps.ArenaAppData>();
    public DbSet<Apps.ArenaAppAsk> AppAsks => Set<Apps.ArenaAppAsk>();
    public DbSet<Insights.DataSource> DataSources => Set<Insights.DataSource>();
    public DbSet<Insights.DataQuery> DataQueries => Set<Insights.DataQuery>();
    public DbSet<Insights.DataBoard> DataBoards => Set<Insights.DataBoard>();
    public DbSet<Insights.DataPin> DataPins => Set<Insights.DataPin>();
    public DbSet<Decks.Deck> Decks => Set<Decks.Deck>();
    public DbSet<Decks.DeckVersion> DeckVersions => Set<Decks.DeckVersion>();
    public DbSet<Decks.DeckTemplate> DeckTemplates => Set<Decks.DeckTemplate>();
    public DbSet<Studio.Workflow> Workflows => Set<Studio.Workflow>();
    public DbSet<Studio.WorkflowRun> WorkflowRuns => Set<Studio.WorkflowRun>();
    public DbSet<Studio.WorkflowRunStep> WorkflowRunSteps => Set<Studio.WorkflowRunStep>();
    public DbSet<Code.CodeFix> CodeFixes => Set<Code.CodeFix>();
    public DbSet<Identity.OwnKey> OwnKeys => Set<Identity.OwnKey>();
    public DbSet<ProductFeedback> ProductFeedback => Set<ProductFeedback>();
    public DbSet<PromptLogEntry> PromptLog => Set<PromptLogEntry>();
    public DbSet<Checklist> Checklists => Set<Checklist>();
    public DbSet<ChecklistRun> ChecklistRuns => Set<ChecklistRun>();
    public DbSet<Code.StudioWorkspace> StudioWorkspaces => Set<Code.StudioWorkspace>();
    public DbSet<Code.BusinessSystem> BusinessSystems => Set<Code.BusinessSystem>();
    public DbSet<Meetings.Meeting> Meetings => Set<Meetings.Meeting>();
    public DbSet<Meetings.MeetingAction> MeetingActions => Set<Meetings.MeetingAction>();
    public DbSet<Panels.Panel> Panels => Set<Panels.Panel>();
    public DbSet<Media.MediaItem> MediaItems => Set<Media.MediaItem>();
    public DbSet<Media.MediaJob> MediaJobs => Set<Media.MediaJob>();
    public DbSet<Media.MediaStory> MediaStories => Set<Media.MediaStory>();
    public DbSet<Media.MediaVector> MediaVectors => Set<Media.MediaVector>();
    public DbSet<Media.MediaVectorVersion> MediaVectorVersions => Set<Media.MediaVectorVersion>();
    public DbSet<MessageTranslation> MessageTranslations => Set<MessageTranslation>();
    public DbSet<ScheduledRun> ScheduledRuns => Set<ScheduledRun>();
    public DbSet<TaskEvent> TaskEvents => Set<TaskEvent>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AccessRole> AccessRoles => Set<AccessRole>();
    public DbSet<AccessAssignment> AccessAssignments => Set<AccessAssignment>();
    public DbSet<AccessRule> AccessRules => Set<AccessRule>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<AccessChange> AccessChanges => Set<AccessChange>();
    public DbSet<AccessMeta> AccessMeta => Set<AccessMeta>();
    public DbSet<Memory> Memories => Set<Memory>();
    public DbSet<MemoryChange> MemoryChanges => Set<MemoryChange>();
    public DbSet<SkillChange> SkillChanges => Set<SkillChange>();
    public DbSet<Compliance.ComplianceAttestation> ComplianceAttestations => Set<Compliance.ComplianceAttestation>();
    public DbSet<Compliance.AccessReview> AccessReviews => Set<Compliance.AccessReview>();
    public DbSet<Compliance.DataRequest> DataRequests => Set<Compliance.DataRequest>();
    public DbSet<Compliance.NoticeAcceptance> NoticeAcceptances => Set<Compliance.NoticeAcceptance>();
    public DbSet<Compliance.SecurityAlert> SecurityAlerts => Set<Compliance.SecurityAlert>();
    public DbSet<Compliance.Incident> Incidents => Set<Compliance.Incident>();
    public DbSet<Compliance.IncidentEvent> IncidentEvents => Set<Compliance.IncidentEvent>();
    public DbSet<Compliance.Risk> Risks => Set<Compliance.Risk>();
    public DbSet<Compliance.RiskAction> RiskActions => Set<Compliance.RiskAction>();
    public DbSet<Compliance.RiskEvent> RiskEvents => Set<Compliance.RiskEvent>();
    public DbSet<Compliance.SoaEntry> SoaEntries => Set<Compliance.SoaEntry>();
    public DbSet<Compliance.SoaApproval> SoaApprovals => Set<Compliance.SoaApproval>();
    public DbSet<Compliance.Policy> Policies => Set<Compliance.Policy>();
    public DbSet<Compliance.PolicyVersion> PolicyVersions => Set<Compliance.PolicyVersion>();
    public DbSet<Compliance.PolicyAcknowledgement> PolicyAcknowledgements => Set<Compliance.PolicyAcknowledgement>();
    public DbSet<Compliance.ComplianceAudit> ComplianceAudits => Set<Compliance.ComplianceAudit>();
    public DbSet<Compliance.Finding> Findings => Set<Compliance.Finding>();
    public DbSet<Compliance.Certificate> Certificates => Set<Compliance.Certificate>();
    public DbSet<Compliance.ControlWaiver> ControlWaivers => Set<Compliance.ControlWaiver>();
    public DbSet<Compliance.Supplier> Suppliers => Set<Compliance.Supplier>();
    public DbSet<Compliance.AiSystem> AiSystems => Set<Compliance.AiSystem>();
    public DbSet<Compliance.ComplianceSnapshot> ComplianceSnapshots => Set<Compliance.ComplianceSnapshot>();
    public DbSet<Compliance.ControlOwner> ControlOwners => Set<Compliance.ControlOwner>();
    public DbSet<Compliance.ManagementReview> ManagementReviews => Set<Compliance.ManagementReview>();
    public DbSet<Compliance.SecurityObjective> SecurityObjectives => Set<Compliance.SecurityObjective>();
    public DbSet<Compliance.EvidenceRequest> EvidenceRequests => Set<Compliance.EvidenceRequest>();
    public DbSet<Compliance.AccessReviewItem> AccessReviewItems => Set<Compliance.AccessReviewItem>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<KnowledgeSource> KnowledgeSources => Set<KnowledgeSource>();
    public DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>();
    public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();
    public DbSet<KnowledgeReaders> KnowledgeReaders => Set<KnowledgeReaders>();
    public DbSet<AnswerFeedback> AnswerFeedback => Set<AnswerFeedback>();
    public DbSet<ArenaMatch> ArenaMatches => Set<ArenaMatch>();
    public DbSet<CachedAnswer> CachedAnswers => Set<CachedAnswer>();
    public DbSet<BotThread> BotThreads => Set<BotThread>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        // A model for each files' key: their converters seal with it.
        optionsBuilder.ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, FileKeyModelCacheKeyFactory>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        // People's files sealed at rest with the key from APP_KEY (FileSealing); none: stored as they are.
        var sealer = new FileSealer(Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptions>(this)
            .FindExtension<FileKeyExtension>()?.Key);
        var files = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<byte[], byte[]>(v => sealer.Seal(v), v => sealer.Open(v));
        var maybeFiles = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<byte[]?, byte[]?>(v => v == null ? null : sealer.Seal(v), v => v == null ? null : sealer.Open(v));
        builder.Entity<ChatAttachment>().Property(a => a.Data).HasConversion(maybeFiles);
        builder.Entity<ChatAttachment>().Property(a => a.Sound).HasConversion(maybeFiles);
        builder.Entity<ChatAttachment>().Property(a => a.Text).HasConversion(v => sealer.SealText(v), v => sealer.OpenText(v));
        builder.Entity<AttachmentPage>().Property(p => p.Data).HasConversion(files);
        builder.Entity<Decks.DeckTemplate>().Property(t => t.Data).HasConversion(files);
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
            e.Property(u => u.SeenRelease).HasMaxLength(32);
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
            e.Property(c => c.ProjectKey).HasMaxLength(300);
            e.Property(c => c.Digest).HasMaxLength(4000);
            e.Property(c => c.SystemPrompt).HasMaxLength(20000);
            e.Property(c => c.Origin).HasMaxLength(32);
            e.Property(c => c.OriginRef).HasMaxLength(128);
            e.Property(c => c.OriginPlace).HasMaxLength(1000);
            e.Property(c => c.Kind).HasMaxLength(16);
            e.Property(c => c.LabField).HasMaxLength(20);
            e.HasIndex(c => new { c.UserId, c.Origin, c.OriginRef });
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
            e.Property(m => m.SyncRef).HasMaxLength(160);
            e.Property(m => m.Mentions).HasDefaultValueSql("'{}'::uuid[]");
            e.HasOne<AppUser>().WithMany().HasForeignKey(m => m.AuthorId).OnDelete(DeleteBehavior.SetNull);
            // A person's questions by when: rate limits count by author.
            e.HasIndex(m => new { m.AuthorId, m.CreatedAt }).HasFilter("\"Role\" = 'user'");
            e.HasIndex(m => new { m.ConversationId, m.Sequence }).IsUnique();
            e.HasIndex(m => new { m.ConversationId, m.ParentId });
            // Admin → Traces: the slowest answers of a time range.
            // Answers by when (the quality page), and the finished answers by when (the slowest, for traces).
            e.HasIndex(m => m.CreatedAt);
            e.HasIndex(m => m.CreatedAt, "IX_chat_messages_Answered").HasFilter("\"AnswerMs\" IS NOT NULL");
            // Usage → prompts: an answer's rounds and tool calls together.
            e.HasIndex(m => m.AnswerId);
        });
        builder.Entity<ChatAttachment>(e =>
        {
            e.ToTable("chat_attachments");
            e.Property(a => a.FileName).HasMaxLength(260);
            e.Property(a => a.ContentType).HasMaxLength(200);
            e.Property(a => a.Kind).HasMaxLength(20);
            e.Property(a => a.Origin).HasMaxLength(20);
            e.HasIndex(a => a.UserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<QueuedMessage>(e =>
        {
            e.ToTable("queued_messages");
            e.Property(q => q.Mentions).HasDefaultValueSql("'{}'::uuid[]");
            e.HasOne<AppUser>().WithMany().HasForeignKey(q => q.UserId).OnDelete(DeleteBehavior.Cascade);
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
        builder.Entity<AccessRequest>(e =>
        {
            e.ToTable("access_requests");
            e.Property(x => x.ResourceKind).HasMaxLength(32);
            e.Property(x => x.Resource).HasMaxLength(300);
            e.Property(x => x.Reason).HasMaxLength(2000);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Note).HasMaxLength(2000);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => x.UserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AccessRole>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Plans.Plan>(e =>
        {
            e.ToTable("plans");
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Goal).HasMaxLength(4000);
            e.Property(x => x.Source).HasMaxLength(8).HasDefaultValue(Llm.Core.Plans.Plan.FromChat);
            e.Property(x => x.Place).HasMaxLength(200);
            e.Property(x => x.Review).HasMaxLength(16);
            e.HasIndex(x => new { x.UserId, x.UpdatedAt });
            e.HasIndex(x => x.ConversationId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Plans.PlanStep>(e =>
        {
            e.ToTable("plan_steps");
            // Made here (a version 7 Guid): a step added to a plan already kept is added, not taken for one stored.
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.Property(x => x.Owner).HasMaxLength(120);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.DependsOn).HasDefaultValueSql("'{}'::integer[]");
            e.Property(x => x.Check).HasMaxLength(1000);
            e.Property(x => x.Files).HasDefaultValueSql("'{}'::text[]");
            e.Property(x => x.CheckResult).HasMaxLength(8);
            e.Property(x => x.CheckOutput).HasMaxLength(4000);
            e.HasIndex(x => new { x.PlanId, x.Number }).IsUnique();
        });
        builder.Entity<Code.CodeReview>(e =>
        {
            e.ToTable("code_reviews");
            e.Property(x => x.Project).HasMaxLength(500);
            e.Property(x => x.FromRef).HasMaxLength(300);
            e.Property(x => x.ToRef).HasMaxLength(300);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.WebUrl).HasMaxLength(1000);
            e.Property(x => x.HeadSha).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Step).HasMaxLength(200);
            e.Property(x => x.Verdict).HasMaxLength(16);
            e.Property(x => x.Risk).HasMaxLength(16);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.Trigger).HasMaxLength(16);
            e.Property(x => x.ShareGroups).HasDefaultValueSql("'{}'::uuid[]");
            e.Property(x => x.SharePeople).HasDefaultValueSql("'{}'::uuid[]");
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.Project, x.Iid });
            e.HasIndex(x => x.Reach);
            e.HasIndex(x => new { x.RuleId, x.CreatedAt });
            e.HasOne<Code.ReviewRule>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Findings).WithOne().HasForeignKey(f => f.ReviewId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Code.SecurityScan>(e =>
        {
            e.ToTable("security_scans");
            e.Property(x => x.Project).HasMaxLength(500);
            e.Property(x => x.Ref).HasMaxLength(300);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Step).HasMaxLength(200);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.Commit).HasMaxLength(64);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.Project, x.CreatedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Findings).WithOne().HasForeignKey(f => f.ScanId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Code.SecurityFinding>(e =>
        {
            e.ToTable("security_findings");
            e.Property(x => x.Path).HasMaxLength(1000);
            e.Property(x => x.SinkCode).HasMaxLength(1000);
            e.Property(x => x.Cwe).HasMaxLength(16);
            e.Property(x => x.CweTitle).HasMaxLength(300);
            e.Property(x => x.Severity).HasMaxLength(16);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Source).HasMaxLength(1000);
            e.Property(x => x.State).HasMaxLength(16);
            e.Property(x => x.Why).HasMaxLength(2000);
            e.Property(x => x.Kind).HasMaxLength(16).HasDefaultValueSql("'code'");
            e.Property(x => x.FoundBy).HasDefaultValueSql("'{}'::text[]");
            e.Property(x => x.Rule).HasMaxLength(500);
            e.Property(x => x.Commit).HasMaxLength(64);
            e.Property(x => x.FoundAt).HasDefaultValueSql("now()");
            e.HasIndex(x => new { x.ScanId, x.Severity });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.DecidedById).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<Code.ReviewRule>(e =>
        {
            e.ToTable("review_rules");
            e.Property(x => x.Project).HasMaxLength(500);
            e.Property(x => x.Branch).HasMaxLength(300);
            e.Property(x => x.Base).HasMaxLength(300);
            e.Property(x => x.Cron).HasMaxLength(100);
            e.Property(x => x.TimeZone).HasMaxLength(100);
            e.Property(x => x.SecretHash).HasMaxLength(64);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.Property(x => x.TargetBranches).HasDefaultValueSql("'{}'::text[]");
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.Project);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<Code.ReviewFinding>(e =>
        {
            e.ToTable("review_findings");
            e.Property(x => x.Path).HasMaxLength(1000);
            e.Property(x => x.Severity).HasMaxLength(16);
            e.Property(x => x.Category).HasMaxLength(32);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.State).HasMaxLength(16);
            e.HasIndex(x => x.ReviewId);
        });
        builder.Entity<RoomMember>(e =>
        {
            e.ToTable("room_members");
            e.HasKey(x => new { x.ConversationId, x.UserId });
            e.Property(x => x.Role).HasMaxLength(16);
            e.Property(x => x.Notify).HasMaxLength(16);
            e.HasIndex(x => x.UserId);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<RoomInvite>(e =>
        {
            e.ToTable("room_invites");
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.Role).HasMaxLength(16);
            e.HasIndex(x => x.ConversationId);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<RoomGroup>(e =>
        {
            e.ToTable("room_groups");
            e.HasKey(x => new { x.ConversationId, x.GroupId });
            e.Property(x => x.Role).HasMaxLength(16);
            e.HasIndex(x => x.GroupId);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Group>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Checklist>(e =>
        {
            e.ToTable("checklists");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Items).HasDefaultValueSql("'{}'::text[]");
            e.HasIndex(x => new { x.UserId, x.Name });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ChecklistRun>(e =>
        {
            e.ToTable("checklist_runs");
            e.Property(x => x.Name).HasMaxLength(100);
            e.OwnsMany(x => x.Items, i => i.ToJson());
            // One at a time in a chat: turning on another replaces it.
            e.HasIndex(x => x.ConversationId).IsUnique();
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Checklist>().WithMany().HasForeignKey(x => x.ChecklistId).OnDelete(DeleteBehavior.SetNull);
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
        });
        builder.Entity<GroupMember>(e =>
        {
            e.ToTable("group_members");
            e.HasKey(m => new { m.GroupId, m.UserId });
            e.HasIndex(m => m.UserId);
            e.HasOne<Group>().WithMany().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        AccessTables.Configure(builder);
        Safeguards.SafeguardTables.Configure(builder);

        builder.Entity<ToolGroupSetting>(e =>
        {
            e.ToTable("tool_group_settings");
            e.HasKey(t => new { t.ToolId, t.GroupId });
            e.Property(t => t.ToolId).HasMaxLength(100);
            e.HasOne<Llm.Core.Access.Group>().WithMany().HasForeignKey(t => t.GroupId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ToolCall>(e =>
        {
            e.ToTable("tool_calls");
            e.Property(t => t.ToolId).HasMaxLength(100);
            e.Property(t => t.Function).HasMaxLength(200);
            e.Property(t => t.Error).HasMaxLength(500);
            e.HasIndex(t => new { t.ToolId, t.At });
            e.HasIndex(t => new { t.UserId, t.ToolId, t.At });
            e.HasOne<AppUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<Toolset>(e =>
        {
            e.ToTable("toolsets");
            e.Property(t => t.Name).HasMaxLength(100);
            e.Property(t => t.Description).HasMaxLength(500);
            e.Property(t => t.Plugin).HasMaxLength(100);
            e.HasIndex(t => t.Name).IsUnique();
        });
        builder.Entity<CustomTool>(e =>
        {
            e.ToTable("custom_tools");
            e.Property(t => t.Name).HasMaxLength(60);
            e.HasIndex(t => t.Name).IsUnique();
            e.Property(t => t.Kind).HasMaxLength(10);
            e.Property(t => t.Title).HasMaxLength(100);
            e.Property(t => t.Description).HasMaxLength(2000);
            e.Property(t => t.Method).HasMaxLength(10);
            e.Property(t => t.UrlTemplate).HasMaxLength(2000);
            e.Property(t => t.ResultPath).HasMaxLength(200);
            e.Property(t => t.Model).HasMaxLength(200);
            e.HasOne<AppUser>().WithMany().HasForeignKey(t => t.CreatedById).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<PluginGroupSetting>(e =>
        {
            e.ToTable("plugin_group_settings");
            e.HasKey(x => new { x.ServerId, x.GroupId });
            e.HasIndex(x => x.GroupId);
            e.HasOne<McpServer>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Group>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<PluginRelease>(e =>
        {
            e.ToTable("plugin_releases");
            e.Property(x => x.Version).HasMaxLength(50);
            e.HasIndex(x => new { x.ServerId, x.InstalledAt });
            e.HasOne<McpServer>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.InstalledById).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<ToolSetting>(e =>
        {
            e.ToTable("tool_settings");
            e.HasKey(t => t.ToolId);
            e.Property(t => t.ToolId).HasMaxLength(100);
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
            e.Property(m => m.EngineKind).HasMaxLength(20).HasDefaultValue(EngineKinds.LlamaCpp);
            e.Property(m => m.Engine).HasMaxLength(100);
            e.Property(m => m.Replicas).HasDefaultValue(1);
            e.Property(m => m.Quantization).HasMaxLength(40);
            e.Property(m => m.SpecDecode).HasMaxLength(20);
            e.Property(m => m.Sleep).HasDefaultValue(true);
            e.Property(m => m.Fallback).HasMaxLength(100);
            e.Property(m => m.KvTier).HasMaxLength(20);
            e.Property(m => m.DraftHead).HasMaxLength(1000);
            e.Property(m => m.DraftMax).HasDefaultValue(3);
            e.Property(m => m.ExtraPreset).HasMaxLength(4000);
        });
        builder.Entity<MediaModel>(e =>
        {
            e.ToTable("media_models");
            e.HasKey(m => m.Name);
            e.Property(m => m.Name).HasMaxLength(100);
            e.Property(m => m.Mode).HasMaxLength(30);
            e.HasIndex(m => m.Mode);
            e.OwnsMany(m => m.Files, f => f.ToJson());
            e.Property(m => m.SpeechId).HasMaxLength(300);
            e.Property(m => m.Sampler).HasMaxLength(50);
            e.Property(m => m.Scheduler).HasMaxLength(50);
            e.Property(m => m.NegativePrompt).HasMaxLength(2000);
            e.Property(m => m.DefaultVoice).HasMaxLength(100);
            e.Property(m => m.Language).HasMaxLength(20);
            e.Property(m => m.Vae).HasMaxLength(10).HasDefaultValue("gpu");
            e.Property(m => m.FlashAttention).HasDefaultValue(true);
            e.Property(m => m.ExtraArgs).HasMaxLength(4000);
            e.Ignore(m => m.Server);
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
        builder.Entity<Apps.ArenaApp>(e =>
        {
            e.ToTable("apps");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Reach).HasMaxLength(16);
            e.Property(x => x.GroupIds).HasDefaultValueSql("'{}'::uuid[]");
            e.HasIndex(x => x.OwnerId);
            e.HasIndex(x => x.Reach);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Apps.ArenaApp>().WithMany().HasForeignKey(x => x.RemixOfId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<Apps.ArenaAppVersion>(e =>
        {
            e.ToTable("app_versions");
            e.Property(x => x.Note).HasMaxLength(200);
            e.HasIndex(x => new { x.AppId, x.Version }).IsUnique();
            e.HasOne<Apps.ArenaApp>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Apps.ArenaAppData>(e =>
        {
            e.ToTable("app_data");
            e.HasKey(x => new { x.AppId, x.UserId, x.Key });
            e.Property(x => x.Key).HasMaxLength(200);
            e.HasOne<Apps.ArenaApp>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Apps.ArenaAppAsk>(e =>
        {
            e.ToTable("app_asks");
            e.Property(x => x.Model).HasMaxLength(200);
            e.HasIndex(x => new { x.UserId, x.At });
            e.HasOne<Apps.ArenaApp>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Insights.DataSource>(e =>
        {
            e.ToTable("data_sources");
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.ConnectionProtected).HasMaxLength(8000);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.Property(x => x.Reach).HasMaxLength(16);
            e.Property(x => x.Tables).HasDefaultValueSql("'{}'::text[]");
            e.Property(x => x.GroupIds).HasDefaultValueSql("'{}'::uuid[]");
        });
        builder.Entity<Insights.DataQuery>(e =>
        {
            e.ToTable("data_queries");
            e.Property(x => x.Question).HasMaxLength(2000);
            e.Property(x => x.Via).HasMaxLength(16);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.HasIndex(x => new { x.SourceId, x.At });
            e.HasIndex(x => new { x.UserId, x.At });
            e.HasOne<Insights.DataSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Insights.DataBoard>(e =>
        {
            e.ToTable("data_boards");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Reach).HasMaxLength(16);
            e.Property(x => x.GroupIds).HasDefaultValueSql("'{}'::uuid[]");
            e.HasIndex(x => x.OwnerId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Insights.DataPin>(e =>
        {
            e.ToTable("data_pins");
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Question).HasMaxLength(2000);
            e.Property(x => x.Refresh).HasMaxLength(16);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.HasIndex(x => new { x.BoardId, x.Position });
            e.HasIndex(x => new { x.Refresh, x.RanAt });
            e.HasOne<Insights.DataBoard>().WithMany().HasForeignKey(x => x.BoardId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Insights.DataSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Decks.Deck>(e =>
        {
            e.ToTable("decks");
            e.Property(x => x.Title).HasMaxLength(200);
            e.HasIndex(x => new { x.OwnerId, x.UpdatedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Decks.DeckTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<Decks.DeckVersion>(e =>
        {
            e.ToTable("deck_versions");
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => new { x.DeckId, x.Version }).IsUnique();
            e.HasOne<Decks.Deck>().WithMany().HasForeignKey(x => x.DeckId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Decks.DeckTemplate>(e =>
        {
            e.ToTable("deck_templates");
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
        });
        builder.Entity<Studio.Workflow>(e =>
        {
            e.ToTable("workflows");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Reach).HasMaxLength(16);
            e.Property(x => x.SecretHash).HasMaxLength(64);
            e.Property(x => x.GroupIds).HasDefaultValueSql("'{}'::uuid[]");
            e.HasIndex(x => x.OwnerId);
            e.HasIndex(x => x.NextRunAt);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Studio.WorkflowRun>(e =>
        {
            e.ToTable("workflow_runs");
            e.Property(x => x.Trigger).HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => new { x.WorkflowId, x.StartedAt });
            e.HasIndex(x => x.Status);
            e.HasOne<Studio.Workflow>().WithMany().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Studio.WorkflowRunStep>(e =>
        {
            e.ToTable("workflow_run_steps");
            e.Property(x => x.Path).HasMaxLength(500);
            e.Property(x => x.StepId).HasMaxLength(50);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Link).HasMaxLength(500);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => new { x.RunId, x.Path }).IsUnique();
            e.HasOne<Studio.WorkflowRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<PromptLogEntry>(e =>
        {
            e.ToTable("prompt_log");
            e.Property(x => x.KeyToken).HasMaxLength(64);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.Prompt).HasMaxLength(PromptLogEntry.MaxText);
            e.Property(x => x.Answer).HasMaxLength(PromptLogEntry.MaxText);
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.UserId, x.At });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ProductFeedback>(e =>
        {
            e.ToTable("product_feedback");
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Text).HasMaxLength(4000);
            e.Property(x => x.Page).HasMaxLength(500);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Answer).HasMaxLength(4000);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Identity.OwnKey>(e =>
        {
            e.ToTable("own_keys");
            e.HasKey(x => x.Token);
            e.Property(x => x.Token).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.MonthlyBudget).HasPrecision(12, 4);
            e.HasIndex(x => x.UserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Code.CodeFix>(e =>
        {
            e.ToTable("code_fixes");
            e.Property(x => x.Project).HasMaxLength(500);
            e.Property(x => x.Branch).HasMaxLength(255);
            e.Property(x => x.BaseSha).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Source).HasMaxLength(16);
            e.Property(x => x.SourceId).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Step).HasMaxLength(100);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.MergeRequestUrl).HasMaxLength(1000);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Meetings.Meeting>(e =>
        {
            e.ToTable("meetings");
            e.HasIndex(m => new { m.UserId, m.CreatedAt });
            e.Property(m => m.Title).HasMaxLength(300);
            e.Property(m => m.Status).HasMaxLength(20);
            e.Property(m => m.Step).HasMaxLength(300);
            e.Property(m => m.Error).HasMaxLength(2000);
            e.Property(m => m.Language).HasMaxLength(20);
            e.Property(m => m.Transcript).HasColumnType("jsonb");
            e.Property(m => m.Speakers).HasColumnType("jsonb");
            e.Property(m => m.Decisions).HasColumnType("jsonb");
            e.Property(m => m.Summary).HasMaxLength(20_000);
            e.Property(m => m.Model).HasMaxLength(200);
            e.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(m => m.Actions).WithOne().HasForeignKey(a => a.MeetingId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Meetings.MeetingAction>(e =>
        {
            e.ToTable("meeting_actions");
            // Made with their ids: one found through its meeting is new, not one to update.
            e.Property(a => a.Id).ValueGeneratedNever();
            e.Property(a => a.Text).HasMaxLength(1000);
            e.Property(a => a.Owner).HasMaxLength(200);
            // The reminders' look: what is due, not done and not reminded yet.
            e.HasIndex(a => new { a.Due, a.Done, a.RemindedAt });
        });
        builder.Entity<Code.BusinessSystem>(e =>
        {
            e.ToTable("business_systems");
            e.Property(b => b.Project).HasMaxLength(300);
            e.HasIndex(b => b.Project).IsUnique();
            e.Property(b => b.Name).HasMaxLength(200);
            e.Property(b => b.Domain).HasMaxLength(300);
            e.Property(b => b.Summary).HasMaxLength(4000);
            e.Property(b => b.Profile).HasColumnType("jsonb");
            e.Property(b => b.Status).HasMaxLength(20);
            e.Property(b => b.Step).HasMaxLength(300);
            e.Property(b => b.Error).HasMaxLength(2000);
            e.Property(b => b.Model).HasMaxLength(200);
        });
        builder.Entity<Code.StudioWorkspace>(e =>
        {
            e.ToTable("studio_workspaces");
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Project).HasMaxLength(500);
            e.Property(x => x.Branch).HasMaxLength(255);
            e.Property(x => x.KeyAlias).HasMaxLength(200);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => new { x.UserId, x.UsedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Panels.Panel>(e =>
        {
            e.ToTable("panels");
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Media.MediaItem>(e =>
        {
            e.ToTable("media_items");
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Action).HasMaxLength(16);
            e.Property(x => x.Prompt).HasMaxLength(4000);
            e.Property(x => x.Model).HasMaxLength(200);
            e.HasIndex(x => new { x.OwnerId, x.Kind, x.CreatedAt });
            e.HasIndex(x => x.AttachmentId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ChatAttachment>().WithMany().HasForeignKey(x => x.AttachmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Media.MediaItem>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<Media.MediaJob>(e =>
        {
            e.ToTable("media_jobs");
            e.Property(x => x.Action).HasMaxLength(16);
            e.Property(x => x.Prompt).HasMaxLength(4000);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Phase).HasMaxLength(200);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => new { x.OwnerId, x.Status });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MessageTranslation>(e =>
        {
            e.ToTable("message_translations");
            e.HasKey(x => new { x.MessageId, x.Language });
            e.Property(x => x.Language).HasMaxLength(12);
            e.HasOne<ChatMessage>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Media.MediaVector>(e =>
        {
            e.ToTable("media_vectors");
            e.HasKey(x => x.ItemId);
            e.Property(x => x.Source).HasMaxLength(16);
            e.Property(x => x.Style).HasMaxLength(40);
            e.HasOne<Media.MediaItem>().WithOne().HasForeignKey<Media.MediaVector>(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Media.MediaVectorVersion>(e =>
        {
            e.ToTable("media_vector_versions");
            e.HasKey(x => new { x.ItemId, x.Version });
            e.Property(x => x.Change).HasMaxLength(300);
            e.HasOne<Media.MediaVector>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Media.MediaStory>(e =>
        {
            e.ToTable("media_stories");
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Brief).HasMaxLength(8000);
            e.Property(x => x.Music).HasMaxLength(1000);
            e.Property(x => x.MusicMadeFor).HasMaxLength(1100);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Phase).HasMaxLength(200);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => new { x.OwnerId, x.UpdatedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Media.MediaItem>().WithMany().HasForeignKey(x => x.VideoItemId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<TaskTemplate>(e =>
        {
            e.ToTable("task_templates");
            e.Property(x => x.Plugin).HasMaxLength(50);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Cron).HasMaxLength(100);
            e.HasIndex(x => new { x.Plugin, x.Name }).IsUnique();
            e.HasOne<McpServer>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
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
            e.Property(d => d.Kind).HasMaxLength(10).HasDefaultValue("files");
            e.ToTable("model_downloads");
            e.Property(x => x.Repo).HasMaxLength(200);
            e.Property(x => x.Revision).HasMaxLength(100);
            e.Property(x => x.Dir).HasMaxLength(300);
            e.Property(x => x.State).HasMaxLength(20);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.CreatedBy).HasMaxLength(256);
            e.OwnsMany(x => x.Files, f => f.ToJson());
        });
        builder.Entity<ModelToolMode>(e =>
        {
            e.ToTable("model_tool_modes");
            e.HasKey(x => x.Model);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.Mode).HasMaxLength(10);
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
            // Those kept before scopes and kinds were: a person's facts, typed.
            e.Property(m => m.Scope).HasMaxLength(20).HasDefaultValue(Memory.Person);
            e.Property(m => m.Kind).HasMaxLength(20).HasDefaultValue(Memory.Fact);
            e.Property(m => m.Source).HasMaxLength(20).HasDefaultValue(Memory.Typed);
            e.Property(m => m.ProjectKey).HasMaxLength(300);
            e.HasIndex(m => new { m.UserId, m.UpdatedAt });
            e.HasIndex(m => new { m.UserId, m.Proposed }).HasFilter("\"Proposed\"");
            e.HasIndex(m => new { m.UserId, m.Scope, m.ProjectKey });
            e.HasIndex(m => m.Scope).HasFilter("\"Scope\" = 'company'");
            e.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MemoryChange>(e =>
        {
            e.ToTable("memory_changes");
            e.Property(c => c.Action).HasMaxLength(16);
            e.Property(c => c.Reason).HasMaxLength(300);
            e.Property(c => c.Before).HasMaxLength(20_000);
            e.Property(c => c.After).HasMaxLength(500);
            e.HasIndex(c => new { c.UserId, c.At });
            e.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Compliance.SecurityAlert>(e =>
        {
            e.ToTable("security_alerts");
            e.Property(a => a.Kind).HasMaxLength(32);
            e.Property(a => a.Title).HasMaxLength(300);
            e.Property(a => a.Detail).HasMaxLength(2000);
            e.Property(a => a.Key).HasMaxLength(200);
            e.HasIndex(a => a.Key).IsUnique();
            e.HasIndex(a => a.At);
        });
        builder.Entity<Compliance.Incident>(e =>
        {
            e.ToTable("incidents");
            e.Property(i => i.Title).HasMaxLength(300);
            e.Property(i => i.Summary).HasMaxLength(4000);
            e.Property(i => i.Severity).HasMaxLength(16);
            e.Property(i => i.Status).HasMaxLength(16);
            e.Property(i => i.OpenedBy).HasMaxLength(256);
            e.Property(i => i.Lessons).HasMaxLength(4000);
            e.HasIndex(i => new { i.Status, i.DetectedAt });
        });
        builder.Entity<Compliance.IncidentEvent>(e =>
        {
            e.ToTable("incident_events");
            e.Property(v => v.By).HasMaxLength(256);
            e.Property(v => v.Kind).HasMaxLength(32);
            e.Property(v => v.Text).HasMaxLength(4000);
            e.HasIndex(v => new { v.IncidentId, v.At });
            e.HasOne<Compliance.Incident>().WithMany().HasForeignKey(v => v.IncidentId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Compliance.Risk>(e =>
        {
            e.ToTable("risks");
            e.Property(r => r.Title).HasMaxLength(300);
            e.Property(r => r.Description).HasMaxLength(4000);
            e.Property(r => r.Category).HasMaxLength(16);
            e.Property(r => r.Threat).HasMaxLength(2000);
            e.Property(r => r.Owner).HasMaxLength(256);
            e.Property(r => r.Treatment).HasMaxLength(16);
            e.Property(r => r.Plan).HasMaxLength(4000);
            e.Property(r => r.AcceptedBy).HasMaxLength(256);
            e.Property(r => r.AcceptedWhy).HasMaxLength(2000);
            e.Property(r => r.CreatedBy).HasMaxLength(256);
            e.HasIndex(r => new { r.ClosedAt, r.ReviewDue });
        });
        builder.Entity<Compliance.RiskAction>(e =>
        {
            e.ToTable("risk_actions");
            e.Property(a => a.Text).HasMaxLength(2000);
            e.Property(a => a.Owner).HasMaxLength(256);
            e.Property(a => a.DoneBy).HasMaxLength(256);
            e.HasIndex(a => a.RiskId);
            e.HasOne<Compliance.Risk>().WithMany().HasForeignKey(a => a.RiskId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Compliance.RiskEvent>(e =>
        {
            e.ToTable("risk_events");
            e.Property(v => v.By).HasMaxLength(256);
            e.Property(v => v.Kind).HasMaxLength(32);
            e.Property(v => v.Text).HasMaxLength(4000);
            e.HasIndex(v => new { v.RiskId, v.At });
            e.HasOne<Compliance.Risk>().WithMany().HasForeignKey(v => v.RiskId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Compliance.SoaEntry>(e =>
        {
            e.ToTable("soa_entries");
            e.Property(s => s.Framework).HasMaxLength(32);
            e.Property(s => s.Clause).HasMaxLength(32);
            e.Property(s => s.Status).HasMaxLength(16);
            e.Property(s => s.Justification).HasMaxLength(2000);
            e.Property(s => s.UpdatedBy).HasMaxLength(256);
            e.HasIndex(s => new { s.Framework, s.Clause }).IsUnique();
        });
        builder.Entity<Compliance.SoaApproval>(e =>
        {
            e.ToTable("soa_approvals");
            e.Property(s => s.Framework).HasMaxLength(32);
            e.Property(s => s.ApprovedBy).HasMaxLength(256);
            e.Property(s => s.Note).HasMaxLength(2000);
            e.HasIndex(s => new { s.Framework, s.Version }).IsUnique();
        });
        builder.Entity<Compliance.Policy>(e =>
        {
            e.ToTable("policies");
            e.Property(p => p.Key).HasMaxLength(64);
            e.Property(p => p.Title).HasMaxLength(200);
            e.Property(p => p.Owner).HasMaxLength(256);
            e.Property(p => p.CreatedBy).HasMaxLength(256);
            e.HasIndex(p => p.Key).IsUnique();
        });
        builder.Entity<Compliance.PolicyVersion>(e =>
        {
            e.ToTable("policy_versions");
            e.Property(v => v.Text).HasMaxLength(200_000);
            e.Property(v => v.Changes).HasMaxLength(2000);
            e.Property(v => v.CreatedBy).HasMaxLength(256);
            e.Property(v => v.ApprovedBy).HasMaxLength(256);
            e.Property(v => v.ReviewedBy).HasMaxLength(256);
            e.HasIndex(v => new { v.PolicyId, v.Version }).IsUnique();
            e.HasOne<Compliance.Policy>().WithMany().HasForeignKey(v => v.PolicyId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Compliance.PolicyAcknowledgement>(e =>
        {
            e.ToTable("policy_acknowledgements");
            e.HasIndex(a => new { a.PolicyVersionId, a.UserId }).IsUnique();
            e.HasIndex(a => a.UserId);
            e.HasOne<Compliance.PolicyVersion>().WithMany().HasForeignKey(a => a.PolicyVersionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Identity.AppUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Compliance.ComplianceAudit>(e =>
        {
            e.ToTable("compliance_audits");
            e.Property(a => a.Title).HasMaxLength(300);
            e.Property(a => a.Kind).HasMaxLength(32);
            e.Property(a => a.Framework).HasMaxLength(32);
            e.Property(a => a.Scope).HasMaxLength(4000);
            e.Property(a => a.Auditor).HasMaxLength(256);
            e.Property(a => a.Conclusion).HasMaxLength(4000);
            e.Property(a => a.CreatedBy).HasMaxLength(256);
            e.HasIndex(a => a.Starts);
        });
        builder.Entity<Compliance.Finding>(e =>
        {
            e.ToTable("findings");
            e.Property(f => f.Title).HasMaxLength(300);
            e.Property(f => f.Kind).HasMaxLength(16);
            e.Property(f => f.Description).HasMaxLength(4000);
            e.Property(f => f.Clause).HasMaxLength(64);
            e.Property(f => f.Control).HasMaxLength(16);
            e.Property(f => f.Owner).HasMaxLength(256);
            e.Property(f => f.Correction).HasMaxLength(4000);
            e.Property(f => f.RootCause).HasMaxLength(4000);
            e.Property(f => f.CorrectiveAction).HasMaxLength(4000);
            e.Property(f => f.Status).HasMaxLength(16);
            e.Property(f => f.DoneBy).HasMaxLength(256);
            e.Property(f => f.VerifiedBy).HasMaxLength(256);
            e.Property(f => f.Verification).HasMaxLength(4000);
            e.Property(f => f.CreatedBy).HasMaxLength(256);
            e.HasIndex(f => new { f.Status, f.Due });
            e.HasOne<Compliance.ComplianceAudit>().WithMany().HasForeignKey(f => f.AuditId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<Compliance.Certificate>(e =>
        {
            e.ToTable("certificates");
            e.Property(c => c.Framework).HasMaxLength(32);
            e.Property(c => c.Body).HasMaxLength(256);
            e.Property(c => c.Number).HasMaxLength(128);
            e.Property(c => c.Scope).HasMaxLength(4000);
            e.Property(c => c.CreatedBy).HasMaxLength(256);
        });
        builder.Entity<Compliance.ControlWaiver>(e =>
        {
            e.ToTable("control_waivers");
            e.Property(x => x.Control).HasMaxLength(64);
            e.Property(x => x.Reason).HasMaxLength(4000);
            e.Property(x => x.Compensating).HasMaxLength(4000);
            e.Property(x => x.ApprovedBy).HasMaxLength(256);
        });
        builder.Entity<Compliance.Supplier>(e =>
        {
            e.ToTable("suppliers");
            e.Property(s => s.Name).HasMaxLength(200);
            e.Property(s => s.Kind).HasMaxLength(16);
            e.Property(s => s.Service).HasMaxLength(1000);
            e.Property(s => s.Location).HasMaxLength(200);
            e.Property(s => s.Certifications).HasMaxLength(2000);
            e.Property(s => s.Agreements).HasMaxLength(2000);
            e.Property(s => s.Risk).HasMaxLength(16);
            e.Property(s => s.Owner).HasMaxLength(256);
            e.Property(s => s.Notes).HasMaxLength(4000);
            e.Property(s => s.Source).HasMaxLength(128);
            e.Property(s => s.ReviewedBy).HasMaxLength(256);
            e.Property(s => s.CreatedBy).HasMaxLength(256);
            e.HasIndex(s => s.Source);
        });
        builder.Entity<Compliance.AiSystem>(e =>
        {
            e.ToTable("ai_systems");
            e.Property(s => s.Name).HasMaxLength(200);
            e.Property(s => s.Kind).HasMaxLength(16);
            e.Property(s => s.Source).HasMaxLength(128);
            e.Property(s => s.Purpose).HasMaxLength(2000);
            e.Property(s => s.IntendedUse).HasMaxLength(2000);
            e.Property(s => s.Affected).HasMaxLength(2000);
            e.Property(s => s.Class).HasMaxLength(16);
            e.Property(s => s.ClassWhy).HasMaxLength(2000);
            e.Property(s => s.HighRiskArea).HasMaxLength(64);
            e.Property(s => s.Role).HasMaxLength(16);
            e.Property(s => s.Owner).HasMaxLength(256);
            e.Property(s => s.Oversight).HasMaxLength(2000);
            e.Property(s => s.Evaluation).HasMaxLength(2000);
            e.Property(s => s.ImpactAssessment).HasMaxLength(8000);
            e.Property(s => s.ImpactAssessedBy).HasMaxLength(256);
            e.Property(s => s.ReviewedBy).HasMaxLength(256);
            e.Property(s => s.CreatedBy).HasMaxLength(256);
            e.HasIndex(s => s.Source);
        });
        builder.Entity<Compliance.ComplianceSnapshot>(e =>
        {
            e.ToTable("compliance_snapshots");
            e.Property(s => s.Check).HasMaxLength(64);
            e.Property(s => s.State).HasMaxLength(16);
            e.Property(s => s.Detail).HasMaxLength(4000);
            e.HasIndex(s => new { s.Day, s.Check }).IsUnique();
        });
        builder.Entity<Compliance.ControlOwner>(e =>
        {
            e.ToTable("control_owners");
            e.HasKey(o => o.ControlId);
            e.Property(o => o.ControlId).HasMaxLength(16);
            e.Property(o => o.Owner).HasMaxLength(256);
            e.Property(o => o.SetBy).HasMaxLength(256);
        });
        builder.Entity<Compliance.ManagementReview>(e =>
        {
            e.ToTable("management_reviews");
            e.Property(r => r.Attendees).HasMaxLength(2000);
            e.Property(r => r.Inputs).HasMaxLength(100_000);
            e.Property(r => r.Minutes).HasMaxLength(20_000);
            e.Property(r => r.Decisions).HasMaxLength(10_000);
            e.Property(r => r.CreatedBy).HasMaxLength(256);
            e.HasIndex(r => r.HeldAt);
        });
        builder.Entity<Compliance.SecurityObjective>(e =>
        {
            e.ToTable("security_objectives");
            e.Property(o => o.Title).HasMaxLength(300);
            e.Property(o => o.Check).HasMaxLength(64);
            e.Property(o => o.Owner).HasMaxLength(256);
            e.Property(o => o.CreatedBy).HasMaxLength(256);
        });
        builder.Entity<Compliance.EvidenceRequest>(e =>
        {
            e.ToTable("evidence_requests");
            e.Property(r => r.Text).HasMaxLength(4000);
            e.Property(r => r.Clause).HasMaxLength(64);
            e.Property(r => r.RequestedBy).HasMaxLength(256);
            e.Property(r => r.Answer).HasMaxLength(8000);
            e.Property(r => r.AnsweredBy).HasMaxLength(256);
            e.HasIndex(r => r.RequestedAt);
        });
        builder.Entity<Compliance.NoticeAcceptance>(e =>
        {
            e.ToTable("notice_acceptances");
            e.Property(a => a.Version).HasMaxLength(64);
            e.Property(a => a.Ip).HasMaxLength(64);
            e.HasIndex(a => new { a.UserId, a.Version });
            e.HasOne<AppUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Compliance.DataRequest>(e =>
        {
            e.ToTable("data_requests");
            e.Property(r => r.Person).HasMaxLength(256);
            e.Property(r => r.Email).HasMaxLength(256);
            e.Property(r => r.Kind).HasMaxLength(16);
            e.Property(r => r.Details).HasMaxLength(4000);
            e.Property(r => r.Status).HasMaxLength(16);
            e.Property(r => r.ReceivedBy).HasMaxLength(256);
            e.Property(r => r.DoneBy).HasMaxLength(256);
            e.Property(r => r.Outcome).HasMaxLength(4000);
            e.HasIndex(r => new { r.Status, r.DueAt });
            e.HasIndex(r => r.UserId);
        });
        builder.Entity<Compliance.AccessReview>(e =>
        {
            e.ToTable("access_reviews");
            e.Property(r => r.Title).HasMaxLength(200);
            e.Property(r => r.CreatedBy).HasMaxLength(256);
            e.Property(r => r.ClosedBy).HasMaxLength(256);
            e.HasIndex(r => r.CreatedAt);
        });
        builder.Entity<Compliance.AccessReviewItem>(e =>
        {
            e.ToTable("access_review_items");
            e.Property(i => i.Kind).HasMaxLength(16);
            e.Property(i => i.Subject).HasMaxLength(300);
            e.Property(i => i.What).HasMaxLength(500);
            e.Property(i => i.Decision).HasMaxLength(16);
            e.Property(i => i.DecidedBy).HasMaxLength(256);
            e.Property(i => i.Note).HasMaxLength(1000);
            e.Property(i => i.Outcome).HasMaxLength(500);
            e.HasIndex(i => i.ReviewId);
            e.HasOne<Compliance.AccessReview>().WithMany().HasForeignKey(i => i.ReviewId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Compliance.ComplianceAttestation>(e =>
        {
            e.ToTable("compliance_attestations");
            e.Property(a => a.ControlId).HasMaxLength(16);
            e.Property(a => a.Actor).HasMaxLength(256);
            e.Property(a => a.Note).HasMaxLength(2000);
            e.HasIndex(a => new { a.ControlId, a.At });
        });
        builder.Entity<SkillChange>(e =>
        {
            e.ToTable("skill_changes");
            e.Property(c => c.Action).HasMaxLength(16);
            e.Property(c => c.Reason).HasMaxLength(500);
            e.Property(c => c.Before).HasMaxLength(40_000);
            e.Property(c => c.After).HasMaxLength(40_000);
            e.HasIndex(c => new { c.SkillId, c.At });
            e.HasIndex(c => new { c.UserId, c.Proposed });
            e.HasOne<Skill>().WithMany().HasForeignKey(c => c.SkillId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Skill>(e =>
        {
            e.ToTable("skills");
            e.Property(p => p.Name).HasMaxLength(40);
            e.Property(p => p.Title).HasMaxLength(100);
            e.Property(p => p.Description).HasMaxLength(1000).HasDefaultValue("");
            e.Property(p => p.Instructions).HasMaxLength(20_000);
            e.Property(p => p.Parameters).HasMaxLength(10_000).HasDefaultValue("[]");
            e.Property(p => p.AutoUse).HasDefaultValue(true);
            e.Property(p => p.Groups).HasDefaultValueSql("'{}'::uuid[]");
            e.Property(p => p.Source).HasMaxLength(16).HasDefaultValue(Skill.Typed);
            e.Property(p => p.Version).HasDefaultValue(1);
            e.HasIndex(p => p.UserId);
            // Each built-in one once, whichever replica writes them.
            e.HasIndex(p => p.Name).IsUnique().HasFilter("\"BuiltIn\"").HasDatabaseName("IX_skills_Name_BuiltIn");
            e.HasIndex(p => p.ServerId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<McpServer>().WithMany().HasForeignKey(p => p.ServerId).OnDelete(DeleteBehavior.Cascade);
        });
        KnowledgeModel(builder);
        SamlModel(builder);
        StorageModel(builder);

        builder.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.Property(a => a.Action).HasMaxLength(100);
            e.Property(a => a.Actor).HasMaxLength(256);
            e.Property(a => a.Target).HasMaxLength(256);
            e.Property(a => a.Ip).HasMaxLength(64);
            e.Property(a => a.Hash).HasMaxLength(64);
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
