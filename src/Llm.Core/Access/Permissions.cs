namespace Llm.Core.Access;

/// <summary>One permission of the catalog: its key, the area it belongs to on the role editor, and what it lets a person do.</summary>
/// <param name="Admin">An administrative permission (only a role gives it); otherwise a feature permission, which rules may also give or take per person or group.</param>
public sealed record PermissionInfo(string Key, string Area, string Description, bool Admin);

/// <summary>
/// The fixed catalog of what a person may do. Administrative permissions come only from the roles
/// a person holds (directly, through a group, or through everyone); feature permissions come from
/// the Member role, which everyone holds, and rules may give or take them per person, group or role.
/// </summary>
public static class Permissions
{
    public const string PeopleRead = "people.read";
    public const string PeopleManage = "people.manage";
    public const string PeopleDisable = "people.disable";
    public const string GroupsManage = "groups.manage";
    public const string AccessRead = "access.read";
    public const string AccessManage = "access.manage";
    public const string ModelsRead = "models.read";
    public const string ModelsManage = "models.manage";
    public const string ModelsLoad = "models.load";
    public const string ToolsManage = "tools.manage";
    public const string PluginsManage = "plugins.manage";
    public const string McpServersManage = "mcpservers.manage";
    public const string KnowledgeManage = "knowledge.manage";
    public const string SettingsRead = "settings.read";
    public const string SettingsManage = "settings.manage";
    public const string SignInManage = "signin.manage";
    public const string CreditsManage = "credits.manage";
    public const string BillingRead = "billing.read";
    public const string UsageRead = "usage.read";
    public const string AuditRead = "audit.read";
    public const string LogsRead = "logs.read";
    public const string MonitoringRead = "monitoring.read";
    public const string AlertsManage = "alerts.manage";
    public const string RetentionManage = "retention.manage";
    public const string LegalHoldManage = "legalhold.manage";
    public const string ExportPerson = "export.person";
    public const string StorageManage = "storage.manage";
    public const string BotsManage = "bots.manage";
    public const string TasksManageAll = "tasks.manage.all";
    public const string ArgusRead = "argus.read";
    public const string ArgusManage = "argus.manage";
    public const string ArgusExplore = "argus.explore";
    public const string RoomsModerate = "rooms.moderate";
    public const string ReviewsAdmin = "reviews.admin";

    public const string Chat = "feature.chat";
    public const string ApiKeys = "feature.api_keys";
    public const string CodeArena = "feature.code_arena";
    public const string WebSearch = "feature.web_search";
    public const string Pictures = "feature.pictures";
    public const string Video = "feature.video";
    public const string Speech = "feature.speech";
    public const string RoomsCreate = "feature.rooms.create";
    public const string ShareLinks = "feature.share_links";
    public const string Tasks = "feature.tasks";
    public const string AssistantsShare = "feature.assistants.share";
    public const string Memory = "feature.memory";
    public const string CodeRead = "code.read";
    public const string ReviewRun = "review.run";
    public const string ReviewPolicy = "review.policy";

    public static readonly IReadOnlyList<PermissionInfo> All =
    [
        new(PeopleRead, "People and groups", "See people, their accounts, credit and spend.", true),
        new(PeopleManage, "People and groups", "Add people, change their accounts, reset passwords and keys, sign them out, delete them.", true),
        new(PeopleDisable, "People and groups", "Disable and enable people.", true),
        new(GroupsManage, "People and groups", "Make groups, choose their members and set their policies.", true),
        new(AccessRead, "Access", "See roles, who holds them, rules and the access history; use the checker.", true),
        new(AccessManage, "Access", "Make and change roles, assignments and rules.", true),
        new(ModelsRead, "Models, tools and knowledge", "See the models, their state and the GPU.", true),
        new(ModelsManage, "Models, tools and knowledge", "Add, change and remove models, their hours, servers and who may use them.", true),
        new(ModelsLoad, "Models, tools and knowledge", "Load and unload models.", true),
        new(ToolsManage, "Models, tools and knowledge", "Turn the chat's tools on and off and choose who may use them.", true),
        new(PluginsManage, "Models, tools and knowledge", "Install, update and remove plugins.", true),
        new(McpServersManage, "Models, tools and knowledge", "Add and change MCP servers and APIs.", true),
        new(KnowledgeManage, "Models, tools and knowledge", "Add and change company knowledge sources and who may read them.", true),
        new(SettingsRead, "Settings", "See the company's settings.", true),
        new(SettingsManage, "Settings", "Change the company's settings.", true),
        new(SignInManage, "Settings", "Set up the directory, company sign-in and SCIM.", true),
        new(CreditsManage, "Money and usage", "Set people's and groups' credit.", true),
        new(BillingRead, "Money and usage", "See spend by person and group, and the chargeback report.", true),
        new(UsageRead, "Money and usage", "See the usage dashboards and the prompt library's use.", true),
        new(AuditRead, "Oversight", "Read the audit log.", true),
        new(LogsRead, "Oversight", "Read the stack's logs and the answers' traces.", true),
        new(MonitoringRead, "Oversight", "See the monitoring dashboards, the quality reports and the stack's state.", true),
        new(AlertsManage, "Oversight", "Set alert rules and where they go.", true),
        new(RetentionManage, "Data", "Set how long chats are kept.", true),
        new(LegalHoldManage, "Data", "Place and lift legal holds.", true),
        new(ExportPerson, "Data", "Export a person's data (eDiscovery).", true),
        new(StorageManage, "Data", "See and clean up stored files.", true),
        new(BotsManage, "Integrations", "Connect the chat bots (Slack, Teams, mail).", true),
        new(TasksManageAll, "Integrations", "See and change everyone's scheduled tasks.", true),
        new(ArgusRead, "Argus", "See Argus's repositories, indexing and health.", true),
        new(ArgusManage, "Argus", "Choose repositories, index, set packs and the webhook.", true),
        new(ArgusExplore, "Argus", "Explore the code index across repositories.", true),
        new(RoomsModerate, "Rooms and reviews", "List rooms, remove members and delete rooms (not read them).", true),
        new(ReviewsAdmin, "Rooms and reviews", "Manage every project's reviews.", true),

        new(Chat, "Features", "Use the chat.", false),
        new(ApiKeys, "Features", "Have API keys (coding agents, IDEs, scripts).", false),
        new(CodeArena, "Features", "Use Code Arena.", false),
        new(WebSearch, "Features", "Search and read the web from the chat.", false),
        new(Pictures, "Features", "Make pictures.", false),
        new(Video, "Features", "Make videos.", false),
        new(Speech, "Features", "Speak and hear answers read aloud.", false),
        new(RoomsCreate, "Features", "Make rooms.", false),
        new(ShareLinks, "Features", "Share chats by link.", false),
        new(Tasks, "Features", "Schedule tasks.", false),
        new(AssistantsShare, "Features", "Share assistants and prompts with groups.", false),
        new(Memory, "Features", "Let the chat remember things.", false),
        new(CodeRead, "Code", "Ask about the company's code (Argus) and use the code assistant.", false),
        new(ReviewRun, "Code", "Start reviews of merge requests and branches.", false),
        new(ReviewPolicy, "Code", "Set a project's automatic reviews.", false),
    ];

    private static readonly Dictionary<string, PermissionInfo> ByKey = All.ToDictionary(p => p.Key, StringComparer.Ordinal);

    public static PermissionInfo? Find(string key) => ByKey.GetValueOrDefault(key);

    public static bool IsKnown(string key) => ByKey.ContainsKey(key);

    /// <summary>A feature permission: rules may give or take it, and the Member role holds it.</summary>
    public static bool IsFeature(string key) => ByKey.TryGetValue(key, out var p) && !p.Admin;

    /// <summary>Every administrative permission.</summary>
    public static IEnumerable<string> AdminKeys => All.Where(p => p.Admin).Select(p => p.Key);

    /// <summary>Every feature permission.</summary>
    public static IEnumerable<string> FeatureKeys => All.Where(p => !p.Admin).Select(p => p.Key);
}

/// <summary>A role that comes with the app: it cannot be changed, only cloned into a custom role.</summary>
public sealed record BuiltInRole(string Key, string Name, string Description, IReadOnlyList<string> Permissions);

/// <summary>The roles that come with the app.</summary>
public static class BuiltInRoles
{
    public const string Owner = "owner";
    public const string PeopleAdmin = "people-admin";
    public const string ModelAdmin = "model-admin";
    public const string Billing = "billing";
    public const string Auditor = "auditor";
    public const string ArgusAdmin = "argus-admin";
    public const string RoomModerator = "room-moderator";
    public const string ReviewAdmin = "review-admin";
    public const string Member = "member";
    public const string TeamManager = "team-manager";
    public const string Supervisor = "supervisor";
    public const string Operator = "operator";

    public static readonly IReadOnlyList<BuiltInRole> All =
    [
        new(Owner, "Owner", "Everything, and every resource, unless a rule for the person themselves takes one away.", [.. Permissions.All.Select(p => p.Key)]),
        new(PeopleAdmin, "People admin", "People, groups and their credit; scoped to some groups, only theirs.",
            [Permissions.PeopleRead, Permissions.PeopleManage, Permissions.PeopleDisable, Permissions.GroupsManage, Permissions.CreditsManage]),
        new(ModelAdmin, "Model admin", "Models, tools, MCP servers and plugins.",
            [Permissions.ModelsRead, Permissions.ModelsManage, Permissions.ModelsLoad, Permissions.ToolsManage, Permissions.McpServersManage, Permissions.PluginsManage]),
        new(Billing, "Billing", "Credit, spend and usage; scoped to some groups, only theirs.", [Permissions.CreditsManage, Permissions.BillingRead, Permissions.UsageRead]),
        new(Auditor, "Auditor", "Reads everything an admin can see, and changes nothing.",
            [.. Permissions.All.Where(p => p.Admin && p.Key.EndsWith(".read", StringComparison.Ordinal)).Select(p => p.Key)]),
        new(ArgusAdmin, "Argus admin", "Argus: its repositories, indexing, packs and webhook; scoped to some repositories, only those.",
            [Permissions.ArgusRead, Permissions.ArgusManage, Permissions.ArgusExplore]),
        new(RoomModerator, "Room moderator", "Lists rooms, removes members and deletes rooms, without reading them.", [Permissions.RoomsModerate]),
        new(ReviewAdmin, "Review admin", "Every project's reviews and automatic reviews.", [Permissions.ReviewsAdmin, Permissions.ReviewPolicy]),
        new(TeamManager, "Team manager", "A group's manager: its people, their credit and limits, its spend and usage. Given over a group, it reaches only that group.",
            [Permissions.PeopleRead, Permissions.PeopleManage, Permissions.PeopleDisable, Permissions.CreditsManage, Permissions.BillingRead, Permissions.UsageRead]),
        new(Supervisor, "Supervisor", "Watches the service: dashboards, alerts, logs, traces, usage and the audit log; changes nothing.",
            [Permissions.MonitoringRead, Permissions.LogsRead, Permissions.UsageRead, Permissions.AuditRead, Permissions.ModelsRead]),
        new(Operator, "Operator", "Runs the service day to day: takes alerts, loads and unloads models, cleans up storage, reads logs and dashboards.",
            [Permissions.MonitoringRead, Permissions.LogsRead, Permissions.AlertsManage, Permissions.ModelsRead, Permissions.ModelsLoad, Permissions.StorageManage]),
        new(Member, "Member", "The features everyone has: held by everyone.", [.. Permissions.FeatureKeys]),
    ];

    public static BuiltInRole? Find(string key) => All.FirstOrDefault(r => r.Key == key);
}
