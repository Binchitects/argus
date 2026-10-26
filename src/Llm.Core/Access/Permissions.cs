namespace Llm.Core.Access;

/// <summary>One permission of the catalog: its key, the area it belongs to on the role editor, and what it lets a person do.</summary>
/// <param name="Admin">An administrative permission (only a role gives it); otherwise a feature permission, which rules may also give or take per person or group.</param>
/// <param name="Parent">The broader permission that holds this one too: a role holding <c>people.manage</c> may do each of its parts.</param>
public sealed record PermissionInfo(string Key, string Area, string Description, bool Admin, string? Parent = null);

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
    public const string DataManage = "data.manage";
    public const string SettingsRead = "settings.read";
    public const string SettingsManage = "settings.manage";
    public const string SignInManage = "signin.manage";
    public const string CreditsManage = "credits.manage";
    public const string BillingRead = "billing.read";
    public const string UsageRead = "usage.read";
    public const string AuditRead = "audit.read";
    public const string ComplianceRead = "compliance.read";
    public const string ComplianceManage = "compliance.manage";
    public const string PrivacyManage = "privacy.manage";
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
    public const string SafeguardsRead = "safeguards.read";
    public const string SafeguardsManage = "safeguards.manage";

    // The parts of the broader permissions above: a role may hold one part alone (each one's Parent holds it too).
    public const string SafeguardsReview = "safeguards.review";
    public const string PeopleCreate = "people.create";
    public const string PeopleEdit = "people.edit";
    public const string PeopleDelete = "people.delete";
    public const string PeoplePasswords = "people.passwords";
    public const string PeopleKeys = "people.keys";
    public const string PeopleLimits = "people.limits";
    public const string GroupsCreate = "groups.create";
    public const string GroupsEdit = "groups.edit";
    public const string GroupsDelete = "groups.delete";
    public const string GroupsMembers = "groups.members";
    public const string GroupsPolicies = "groups.policies";
    public const string AccessRoles = "access.roles";
    public const string AccessAssign = "access.assign";
    public const string AccessRules = "access.rules";
    public const string AccessRequests = "access.requests";
    public const string ModelsAdd = "models.add";
    public const string ModelsEdit = "models.edit";
    public const string ModelsDelete = "models.delete";
    public const string ModelsDownload = "models.download";
    public const string ModelsServers = "models.servers";
    public const string ModelsHours = "models.hours";
    public const string ModelsAccess = "models.access";
    public const string ToolsRead = "tools.read";
    public const string ToolsEdit = "tools.edit";
    public const string ToolsAccess = "tools.access";
    public const string PluginsRead = "plugins.read";
    public const string PluginsInstall = "plugins.install";
    public const string PluginsConfigure = "plugins.configure";
    public const string PluginsRemove = "plugins.remove";
    public const string KnowledgeRead = "knowledge.read";
    public const string KnowledgeAdd = "knowledge.add";
    public const string KnowledgeEdit = "knowledge.edit";
    public const string KnowledgeSync = "knowledge.sync";
    public const string KnowledgeDelete = "knowledge.delete";
    public const string SettingsBranding = "settings.branding";
    public const string SettingsChat = "settings.chat";
    public const string SettingsModels = "settings.models";
    public const string SettingsTools = "settings.tools";
    public const string SettingsSafeguards = "settings.safeguards";
    public const string SettingsIntegrations = "settings.integrations";
    public const string SettingsData = "settings.data";
    public const string SettingsGateway = "settings.gateway";
    public const string SettingsArgus = "settings.argus";
    public const string SettingsRestart = "settings.restart";
    public const string SignInDirectory = "signin.directory";
    public const string SignInSso = "signin.sso";
    public const string SignInMoves = "signin.moves";
    public const string TracesRead = "traces.read";
    public const string StorageRead = "storage.read";
    public const string StorageCleanup = "storage.cleanup";
    public const string StorageDelete = "storage.delete";
    public const string StorageQuota = "storage.quota";
    public const string ArgusRepos = "argus.repos";
    public const string ArgusIndex = "argus.index";
    public const string ArgusPacks = "argus.packs";
    public const string ArgusWebhook = "argus.webhook";

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
    public const string CodeStudio = "code.studio";
    public const string ReviewPolicy = "review.policy";
    public const string Python = "feature.python";
    public const string Canvas = "feature.canvas";
    public const string ThreeD = "feature.3d";
    public const string Browser = "feature.browser";
    public const string Apps = "feature.apps";
    public const string Data = "feature.data";
    public const string Decks = "feature.decks";
    public const string Studio = "feature.studio";
    public const string FixIt = "feature.fixit";
    public const string Plans = "feature.plans";
    public const string Research = "feature.research";
    public const string Compare = "feature.compare";
    public const string Uploads = "feature.uploads";
    public const string AssistantsCreate = "feature.assistants.create";
    public const string SkillsCreate = "feature.skills";
    public const string Meetings = "feature.meetings";
    public const string Export = "feature.export";
    public const string Knowledge = "feature.knowledge";
    public const string ModelChoice = "feature.model_choice";

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
        new(DataManage, "Models, tools and knowledge", "Connect databases for people to ask, choose their tables and who may ask them, and read what was asked.", true),
        new(SettingsRead, "Settings", "See the company's settings.", true),
        new(SettingsManage, "Settings", "Change the company's settings.", true),
        new(SignInManage, "Settings", "Set up the directory, company sign-in and SCIM.", true),
        new(CreditsManage, "Money and usage", "Set people's and groups' credit.", true),
        new(BillingRead, "Money and usage", "See spend by person and group, and the chargeback report.", true),
        new(UsageRead, "Money and usage", "See the usage dashboards and the prompt library's use.", true),
        new(AuditRead, "Oversight", "Read the audit log.", true),
        new(ComplianceRead, "Oversight", "See the Compliance center: each control, its checks and the evidence.", true),
        new(ComplianceManage, "Oversight", "Attest the company's part of the controls and run access reviews.", true),
        new(PrivacyManage, "Data", "Answer people's requests about their data: a copy, a correction, restriction, objection and erasure.", true),
        new(LogsRead, "Oversight", "Read the stack's logs and the answers' traces.", true),
        new(MonitoringRead, "Oversight", "See the monitoring dashboards, the quality reports and the stack's state.", true),
        new(AlertsManage, "Oversight", "Set alert rules and where they go.", true),
        new(SafeguardsRead, "Safeguards", "See the safeguard rules, what they did, and try texts on the test bench.", true),
        new(SafeguardsManage, "Safeguards", "Make, change, order and remove safeguard rules.", true),
        new(SafeguardsReview, "Safeguards", "Answer people's requests to review a refusal.", true, SafeguardsManage),
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

        new(PeopleCreate, "People and groups", "Add people.", true, PeopleManage),
        new(PeopleEdit, "People and groups", "Change people's names, emails and sources.", true, PeopleManage),
        new(PeopleDelete, "People and groups", "Delete people (their chats and files go with them).", true, PeopleManage),
        new(PeoplePasswords, "People and groups", "Reset people's passwords and two-factor sign-in, and sign them out.", true, PeopleManage),
        new(PeopleKeys, "People and groups", "Make people's API keys anew, and turn their API access on and off.", true, PeopleManage),
        new(PeopleLimits, "People and groups", "Set people's API rate limits.", true, PeopleManage),
        new(GroupsCreate, "People and groups", "Make groups (app and directory groups).", true, GroupsManage),
        new(GroupsEdit, "People and groups", "Rename groups and change their priority.", true, GroupsManage),
        new(GroupsDelete, "People and groups", "Delete groups.", true, GroupsManage),
        new(GroupsMembers, "People and groups", "Choose app groups' members.", true, GroupsManage),
        new(GroupsPolicies, "People and groups", "Set groups' retention, credit, safeguards and rate limits.", true, GroupsManage),
        new(AccessRoles, "Access", "Make, change and delete custom roles.", true, AccessManage),
        new(AccessAssign, "Access", "Give and take roles.", true, AccessManage),
        new(AccessRules, "Access", "Add and remove allow and deny rules.", true, AccessManage),
        new(AccessRequests, "Access", "Answer people's requests for access.", true, AccessManage),
        new(ModelsAdd, "Models, tools and knowledge", "Add models from the library.", true, ModelsManage),
        new(ModelsEdit, "Models, tools and knowledge", "Change models' settings and keep them loaded.", true, ModelsManage),
        new(ModelsDelete, "Models, tools and knowledge", "Remove models.", true, ModelsManage),
        new(ModelsDownload, "Models, tools and knowledge", "Download models from Hugging Face and fetch the default ones.", true, ModelsManage),
        new(ModelsServers, "Models, tools and knowledge", "Add and change other GPU servers.", true, ModelsManage),
        new(ModelsHours, "Models, tools and knowledge", "Set models' hours.", true, ModelsManage),
        new(ModelsAccess, "Models, tools and knowledge", "Choose who may use each model.", true, ModelsManage),
        new(ToolsRead, "Models, tools and knowledge", "See the chat's tools and MCP servers.", true, ToolsManage),
        new(ToolsEdit, "Models, tools and knowledge", "Turn tools on and off, and set how they ask.", true, ToolsManage),
        new(ToolsAccess, "Models, tools and knowledge", "Choose who may use each tool.", true, ToolsManage),
        new(PluginsRead, "Models, tools and knowledge", "See the plugins and the catalog.", true, PluginsManage),
        new(PluginsInstall, "Models, tools and knowledge", "Install and update plugins.", true, PluginsManage),
        new(PluginsConfigure, "Models, tools and knowledge", "Set plugins' settings and who may use them.", true, PluginsManage),
        new(PluginsRemove, "Models, tools and knowledge", "Remove plugins.", true, PluginsManage),
        new(KnowledgeRead, "Models, tools and knowledge", "See the knowledge sources and their documents.", true, KnowledgeManage),
        new(KnowledgeAdd, "Models, tools and knowledge", "Add knowledge sources.", true, KnowledgeManage),
        new(KnowledgeEdit, "Models, tools and knowledge", "Change knowledge sources and who may read them.", true, KnowledgeManage),
        new(KnowledgeSync, "Models, tools and knowledge", "Read knowledge sources again now, and test them.", true, KnowledgeManage),
        new(KnowledgeDelete, "Models, tools and knowledge", "Remove knowledge sources.", true, KnowledgeManage),
        new(SettingsBranding, "Settings", "Change the branding settings.", true, SettingsManage),
        new(SettingsChat, "Settings", "Change the chat's settings.", true, SettingsManage),
        new(SettingsModels, "Settings", "Change the model, engine, prices and speech settings.", true, SettingsManage),
        new(SettingsTools, "Settings", "Change Python, the web, plugins' and Arena MCP's settings.", true, SettingsManage),
        new(SettingsSafeguards, "Settings", "Change the safeguards.", true, SettingsManage),
        new(SettingsIntegrations, "Settings", "Change email, notifications, scheduled tasks and chat bots' settings.", true, SettingsManage),
        new(SettingsData, "Settings", "Change retention, storage and company knowledge settings.", true, SettingsManage),
        new(SettingsGateway, "Settings", "Change the API keys' settings.", true, SettingsManage),
        new(SettingsArgus, "Settings", "Change Argus's settings.", true, SettingsManage),
        new(SettingsRestart, "Settings", "Restart the app to apply settings.", true, SettingsManage),
        new(SignInDirectory, "Settings", "Set up the company directory (LDAP) and sync it.", true, SignInManage),
        new(SignInSso, "Settings", "Set up company sign-in (OIDC, SAML) and SCIM.", true, SignInManage),
        new(SignInMoves, "Settings", "Move local accounts to the directory.", true, SignInManage),
        new(TracesRead, "Oversight", "Read the answers' traces.", true, LogsRead),
        new(StorageRead, "Data", "See stored files and disk use.", true, StorageManage),
        new(StorageCleanup, "Data", "Run clean-ups.", true, StorageManage),
        new(StorageDelete, "Data", "Delete people's stored files.", true, StorageManage),
        new(StorageQuota, "Data", "Set people's storage quotas.", true, StorageManage),
        new(ArgusRepos, "Argus", "Choose Argus's repositories and their branches.", true, ArgusManage),
        new(ArgusIndex, "Argus", "Index repositories now and set the schedule.", true, ArgusManage),
        new(ArgusPacks, "Argus", "Install and remove knowledge packs.", true, ArgusManage),
        new(ArgusWebhook, "Argus", "Set GitLab's webhook.", true, ArgusManage),

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
        new(CodeStudio, "Code", "Open workspaces in Code Studio: edit, run and debug code on the server or one's own machine.", false),
        new(ReviewPolicy, "Code", "Set a project's automatic reviews.", false),
        new(Python, "Features", "Run Python in the sandbox (data, charts, files).", false),
        new(Canvas, "Features", "Write documents and code with the canvas.", false),
        new(ThreeD, "Features", "Make 3D models.", false),
        new(Browser, "Features", "Let the chat use a browser to do things on websites.", false),
        new(Apps, "Features", "Publish apps the chat builds, for oneself, groups or everyone.", false),
        new(Data, "Features", "Ask the databases an admin connected, and keep boards of charts.", false),
        new(Decks, "Features", "Make slide decks with the model, and download them.", false),
        new(Studio, "Features", "Build agent workflows in Agent Studio that run as oneself on a trigger.", false),
        new(FixIt, "Features", "Let an agent change code and open merge requests as oneself (Fix it), with one's own GitLab token.", false),
        new(Plans, "Features", "Make and keep plans.", false),
        new(Research, "Features", "Run deep research.", false),
        new(Compare, "Features", "Compare two models' answers.", false),
        new(Uploads, "Features", "Attach files to messages.", false),
        new(AssistantsCreate, "Features", "Make assistants.", false),
        new(SkillsCreate, "Features", "Make skills (slash commands with instructions and parameters).", false),
        new(Meetings, "Features", "Record or upload meetings: who said what, the minutes and the action items.", false),
        new(Export, "Features", "Export their own data and chats.", false),
        new(Knowledge, "Features", "Search company knowledge from the chat.", false),
        new(ModelChoice, "Features", "Choose the chat's model (else the company's default answers).", false),
    ];

    private static readonly Dictionary<string, PermissionInfo> ByKey = All.ToDictionary(p => p.Key, StringComparer.Ordinal);

    public static PermissionInfo? Find(string key) => ByKey.GetValueOrDefault(key);

    public static bool IsKnown(string key) => ByKey.ContainsKey(key);

    /// <summary>A feature permission: rules may give or take it, and the Member role holds it.</summary>
    public static bool IsFeature(string key) => ByKey.TryGetValue(key, out var p) && !p.Admin;

    /// <summary>Every administrative permission.</summary>
    public static IEnumerable<string> AdminKeys => All.Where(p => p.Admin).Select(p => p.Key);

    /// <summary>The broader permission that holds this one, if any.</summary>
    public static string? ParentOf(string key) => ByKey.GetValueOrDefault(key)?.Parent;

    /// <summary>The permission's area word ("people" of people.passwords): holding any of an area's permissions lets one read the area.</summary>
    public static string AreaOf(string key) => key[..Math.Max(0, key.IndexOf('.'))];

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
    public const string SafetyOfficer = "safety-officer";
    public const string ComplianceOfficer = "compliance-officer";

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
        new(SafetyOfficer, "Safety officer", "The safeguards: their rules, what they did, and people's requests to review a refusal; reads the audit log.",
            [Permissions.SafeguardsRead, Permissions.SafeguardsManage, Permissions.AuditRead]),
        new(ComplianceOfficer, "Compliance officer", "The Compliance center: the controls and their checks, the company's part attested, access reviews, people's data requests and the evidence; reads the audit log.",
            [Permissions.ComplianceRead, Permissions.ComplianceManage, Permissions.PrivacyManage, Permissions.AuditRead]),
        new(Member, "Member", "The features everyone has: held by everyone.", [.. Permissions.FeatureKeys]),
    ];

    public static BuiltInRole? Find(string key) => All.FirstOrDefault(r => r.Key == key);
}
