using Branding;
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
    public const string UsageLine = "usage.line";
    public const string AuditRead = "audit.read";
    public const string ComplianceRead = "compliance.read";
    public const string ComplianceManage = "compliance.manage";
    public const string PrivacyManage = "privacy.manage";
    public const string LogsRead = "logs.read";
    public const string MonitoringRead = "monitoring.read";
    public const string RuntimesRestart = "runtimes.restart";
    public const string HardwareRead = "hardware.read";
    public const string FeedbackManage = "feedback.manage";
    public const string BroadcastsSend = "broadcasts.send";
    /// <summary>Not a ".read": the Auditor role, which holds every read, does not get it with them.</summary>
    public const string PromptsReview = "prompts.review";
    public const string AlertsManage = "alerts.manage";
    public const string RetentionManage = "retention.manage";
    public const string LegalHoldManage = "legalhold.manage";
    public const string ExportPerson = "export.person";
    public const string StorageManage = "storage.manage";
    public const string BotsManage = "bots.manage";
    public const string TasksManageAll = "tasks.manage.all";
    public const string CodeIntelRead = "code-intel.read";
    public const string CodeIntelManage = "code-intel.manage";
    public const string CodeIntelExplore = "code-intel.explore";
    public const string RoomsModerate = "rooms.moderate";
    public const string ReviewsAdmin = "reviews.admin";
    public const string SafeguardsRead = "safeguards.read";
    public const string SafeguardsManage = "safeguards.manage";
    public const string SharingManage = "sharing.manage";
    public const string BusinessManage = "business.manage";

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
    public const string AccessDefaults = "access.defaults";
    public const string ComplianceAttest = "compliance.attest";
    public const string ComplianceReviews = "compliance.reviews";
    public const string ComplianceRisks = "compliance.risks";
    public const string CompliancePolicies = "compliance.policies";
    public const string ComplianceAudits = "compliance.audits";
    public const string SharingCompany = "sharing.company";
    public const string SharingGroups = "sharing.groups";
    public const string SkillsCompany = "sharing.skills";
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
    public const string SettingsCodeIntel = "settings.code-intel";
    public const string SettingsRestart = "settings.restart";
    public const string SignInDirectory = "signin.directory";
    public const string SignInSso = "signin.sso";
    public const string SignInMoves = "signin.moves";
    public const string TracesRead = "traces.read";
    public const string StorageRead = "storage.read";
    public const string StorageCleanup = "storage.cleanup";
    public const string StorageDelete = "storage.delete";
    public const string StorageQuota = "storage.quota";
    public const string CodeIntelRepos = "code-intel.repos";
    public const string CodeIntelIndex = "code-intel.index";
    public const string CodeIntelPacks = "code-intel.packs";
    public const string CodeIntelWebhook = "code-intel.webhook";
    public const string ProjectsManage = "projects.manage";

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
    public const string Lab = "feature.lab";
    public const string Wiki = "feature.wiki";
    public const string Calls = "feature.calls";
    public const string SkillsShare = "feature.skills.share";
    public const string AppsShare = "feature.apps.share";
    public const string ProjectsCreate = "feature.projects";
    public const string ProjectsGlobal = "feature.projects.global";

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
        new(ModelsLoad, "Models, tools and knowledge", "Load, unload and reload models.", true),
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
        new(UsageLine, "Money and usage", "Take requests out of the answers' line and stop running ones (Admin → Line), saying why: the person or the caller is told.", true),
        new(AuditRead, "Oversight", "Read the audit log.", true),
        new(ComplianceRead, "Oversight", "See the Compliance center: each control, its checks and the evidence.", true),
        new(ComplianceManage, "Oversight", "Attest the company's part of the controls and run access reviews.", true),
        new(PrivacyManage, "Data", "Answer people's requests about their data: a copy, a correction, restriction, objection and erasure.", true),
        new(LogsRead, "Oversight", "Read the stack's logs and the answers' traces.", true),
        new(MonitoringRead, "Oversight", "See the monitoring dashboards, the quality reports and the stack's state.", true),
        new(RuntimesRestart, "Oversight", "Restart the engines and the picture and video servers, and reset the sandboxes: what runs there is stopped.", true),
        new(HardwareRead, "Oversight", "See the hardware dashboards: the GPUs, CPUs, memory, disks, network, temperatures and power.", true),
        new(FeedbackManage, "Oversight", "Read what people send about Arena (Send feedback), mark it and answer it.", true),
        new(BroadcastsSend, "Oversight", "Send a message to everyone, or to some groups: their bell, a banner above every page, and Code Arena.", true),
        new(PromptsReview, "Oversight", "Read everyone's questions and answers, in the chat and with API keys (the prompt log); each search is audited.", true),
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
        new(CodeIntelRead, $"{Brand.CodeIntel}", $"See {Brand.CodeIntel}'s repositories, indexing and health.", true),
        new(CodeIntelManage, $"{Brand.CodeIntel}", "Choose repositories, index, set packs and the webhook.", true),
        new(CodeIntelExplore, $"{Brand.CodeIntel}", "Explore the code index across repositories.", true),
        new(RoomsModerate, "Rooms and reviews", "List rooms, remove members and delete rooms (not read them).", true),
        new(ReviewsAdmin, "Rooms and reviews", "Manage every project's reviews.", true),
        new(SharingManage, "Sharing", "Share assistants, skills, apps, chats and reviews with the whole company or any group, and look after the company's skills and assistants.", true),
        new(BusinessManage, "Code", "Remove business maps.", true),
        new(ProjectsManage, "Projects", "See every project and what it used, and change or delete any.", true),

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
        new(AccessDefaults, "Access", "Choose the groups new people join at their first sign-in.", true, AccessManage),
        new(ComplianceAttest, "Oversight", "Attest controls, keep the statement of applicability, waivers and certificates.", true, ComplianceManage),
        new(ComplianceReviews, "Oversight", "Run access reviews and decide their items.", true, ComplianceManage),
        new(ComplianceRisks, "Oversight", "Keep the risk register, the suppliers and the AI systems.", true, ComplianceManage),
        new(CompliancePolicies, "Oversight", "Write, approve and review the policies.", true, ComplianceManage),
        new(ComplianceAudits, "Oversight", "Keep audits and their findings, objectives, management reviews, incidents and evidence requests.", true, ComplianceManage),
        new(SharingCompany, "Sharing", "Share assistants, skills, chats and reviews with the whole company.", true, SharingManage),
        new(SharingGroups, "Sharing", "Share with any group, not only one's own.", true, SharingManage),
        new(SkillsCompany, "Sharing", "Change and remove the company's skills and assistants, and turn Arena's own skills off or on.", true, SharingManage),
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
        new(SettingsCodeIntel, "Settings", $"Change {Brand.CodeIntel}'s settings.", true, SettingsManage),
        new(SettingsRestart, "Settings", "Restart the app to apply settings.", true, SettingsManage),
        new(SignInDirectory, "Settings", "Set up the company directory (LDAP) and sync it.", true, SignInManage),
        new(SignInSso, "Settings", "Set up company sign-in (OIDC, SAML) and SCIM.", true, SignInManage),
        new(SignInMoves, "Settings", "Move local accounts to the directory.", true, SignInManage),
        new(TracesRead, "Oversight", "Read the answers' traces.", true, LogsRead),
        new(StorageRead, "Data", "See stored files and disk use.", true, StorageManage),
        new(StorageCleanup, "Data", "Run clean-ups.", true, StorageManage),
        new(StorageDelete, "Data", "Delete people's stored files.", true, StorageManage),
        new(StorageQuota, "Data", "Set people's storage quotas.", true, StorageManage),
        new(CodeIntelRepos, $"{Brand.CodeIntel}", $"Choose {Brand.CodeIntel}'s repositories and their branches.", true, CodeIntelManage),
        new(CodeIntelIndex, $"{Brand.CodeIntel}", "Index repositories now and set the schedule.", true, CodeIntelManage),
        new(CodeIntelPacks, $"{Brand.CodeIntel}", "Install and remove knowledge packs.", true, CodeIntelManage),
        new(CodeIntelWebhook, $"{Brand.CodeIntel}", "Set GitLab's webhook.", true, CodeIntelManage),

        new(Chat, "Chat and knowledge", "Use the chat.", false),
        new(ApiKeys, "Agents and keys", "Have API keys (coding agents, IDEs, scripts).", false),
        new(CodeArena, "Agents and keys", "Use Code Arena.", false),
        new(WebSearch, "Chat and knowledge", "Search and read the web from the chat.", false),
        new(Pictures, "Making", "Make pictures.", false),
        new(Video, "Making", "Make videos.", false),
        new(Speech, "Chat and knowledge", "Speak and hear answers read aloud.", false),
        new(RoomsCreate, "Sharing and rooms", "Make rooms.", false),
        new(ShareLinks, "Sharing and rooms", "Share chats by link.", false),
        new(Tasks, "Agents and keys", "Schedule tasks.", false),
        new(AssistantsShare, "Sharing and rooms", "Share assistants and prompts with groups.", false),
        new(Memory, "Chat and knowledge", "Let the chat remember things.", false),
        new(CodeRead, "Code", $"Ask about the company's code ({Brand.CodeIntel}) and use the code assistant.", false),
        new(ReviewRun, "Code", "Start reviews of merge requests and branches.", false),
        new(CodeStudio, "Code", "Open workspaces in Code Studio: edit, run and debug code on the server or one's own machine.", false),
        new(ReviewPolicy, "Code", "Set a project's automatic reviews.", false),
        new(Python, "Chat and knowledge", "Run Python in the sandbox (data, charts, files).", false),
        new(Canvas, "Chat and knowledge", "Write documents and code with the canvas.", false),
        new(ThreeD, "Making", "Make 3D models.", false),
        new(Browser, "Chat and knowledge", "Let the chat use a browser to do things on websites.", false),
        new(Apps, "Making", "Make apps with the chat and open them (opening them to others: feature.apps.share).", false),
        new(Data, "Making", "Ask the databases an admin connected, and keep boards of charts.", false),
        new(Decks, "Making", "Make slide decks with the model, and download them.", false),
        new(Studio, "Agents and keys", "Build agent workflows in Agent Studio that run as oneself on a trigger.", false),
        new(FixIt, "Agents and keys", "Let an agent change code and open merge requests as oneself (Fix it), with one's own GitLab token.", false),
        new(Plans, "Agents and keys", "Make and keep plans.", false),
        new(Research, "Chat and knowledge", "Run deep research.", false),
        new(Compare, "Chat and knowledge", "Compare two models' answers.", false),
        new(Uploads, "Chat and knowledge", "Attach files to messages.", false),
        new(AssistantsCreate, "Sharing and rooms", "Make assistants.", false),
        new(SkillsCreate, "Sharing and rooms", "Make skills (slash commands with instructions and parameters).", false),
        new(Meetings, "Making", "Record or upload meetings: who said what, the minutes and the action items.", false),
        new(Export, "Chat and knowledge", "Export their own data and chats.", false),
        new(Knowledge, "Chat and knowledge", "Search company knowledge from the chat.", false),
        new(ModelChoice, "Chat and knowledge", "Choose the chat's model (else the company's default answers).", false),
        new(Lab, "Making", "Use the science and quantum lab.", false),
        new(Wiki, "Chat and knowledge", "Read the wiki: company knowledge and the code's pages.", false),
        new(Calls, "Sharing and rooms", "Call in rooms, with voice, video and screen sharing.", false),
        new(SkillsShare, "Sharing and rooms", "Share their skills with their groups.", false),
        new(AppsShare, "Sharing and rooms", "Open their apps to groups or everyone.", false),
        new(ProjectsCreate, "Sharing and rooms", "Make projects, and be their owner.", false),
        new(ProjectsGlobal, "Sharing and rooms", "Make global projects, which everyone sees and may add to.", false),
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
    public const string CodeIntelAdmin = "code-intel-admin";
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
        new(ModelAdmin, "Model admin", "Models, tools, MCP servers and plugins; restarts the engines.",
            [Permissions.ModelsRead, Permissions.ModelsManage, Permissions.ModelsLoad, Permissions.RuntimesRestart, Permissions.ToolsManage, Permissions.McpServersManage, Permissions.PluginsManage]),
        new(Billing, "Billing", "Credit, spend and usage; scoped to some groups, only theirs.", [Permissions.CreditsManage, Permissions.BillingRead, Permissions.UsageRead]),
        new(Auditor, "Auditor", "Reads everything an admin can see, and changes nothing.",
            [.. Permissions.All.Where(p => p.Admin && p.Key.EndsWith(".read", StringComparison.Ordinal)).Select(p => p.Key)]),
        new(CodeIntelAdmin, $"{Brand.CodeIntel} admin", $"{Brand.CodeIntel}: its repositories, indexing, packs and webhook; scoped to some repositories, only those.",
            [Permissions.CodeIntelRead, Permissions.CodeIntelManage, Permissions.CodeIntelExplore]),
        new(RoomModerator, "Room moderator", "Lists rooms, removes members and deletes rooms, without reading them.", [Permissions.RoomsModerate]),
        new(ReviewAdmin, "Review admin", "Every project's reviews and automatic reviews.", [Permissions.ReviewsAdmin, Permissions.ReviewPolicy]),
        new(TeamManager, "Team manager", "A group's manager: its people, their credit and limits, its spend and usage. Given over a group, it reaches only that group.",
            [Permissions.PeopleRead, Permissions.PeopleManage, Permissions.PeopleDisable, Permissions.CreditsManage, Permissions.BillingRead, Permissions.UsageRead]),
        new(Supervisor, "Supervisor", "Watches the service: dashboards, alerts, logs, traces, usage and the audit log; changes nothing.",
            [Permissions.MonitoringRead, Permissions.HardwareRead, Permissions.LogsRead, Permissions.UsageRead, Permissions.AuditRead, Permissions.ModelsRead]),
        new(Operator, "Operator", "Runs the service day to day: takes alerts, loads and unloads models, restarts engines and resets sandboxes, cleans up storage, clears the answers' line, reads logs and dashboards.",
            [Permissions.MonitoringRead, Permissions.HardwareRead, Permissions.LogsRead, Permissions.AlertsManage, Permissions.ModelsRead, Permissions.ModelsLoad, Permissions.RuntimesRestart,
                Permissions.StorageManage, Permissions.UsageLine]),
        new(SafetyOfficer, "Safety officer", "The safeguards: their rules, what they did, and people's requests to review a refusal; reads the audit log.",
            [Permissions.SafeguardsRead, Permissions.SafeguardsManage, Permissions.AuditRead]),
        new(ComplianceOfficer, "Compliance officer", "The Compliance center: the controls and their checks, the company's part attested, access reviews, people's data requests and the evidence; reads the audit log.",
            [Permissions.ComplianceRead, Permissions.ComplianceManage, Permissions.PrivacyManage, Permissions.AuditRead]),
        new(Member, "Member", "The features everyone has: held by everyone.", [.. Permissions.FeatureKeys]),
    ];

    public static BuiltInRole? Find(string key) => All.FirstOrDefault(r => r.Key == key);
}

/// <summary>A starting point for a custom role: Admin → Access makes a role of it, which can then be changed.</summary>
public sealed record RoleTemplate(string Key, string Name, string Description, IReadOnlyList<string> Permissions);

/// <summary>Common roles a company makes besides the built-in ones.</summary>
public static class RoleTemplates
{
    public static readonly IReadOnlyList<RoleTemplate> All =
    [
        new("help-desk", "Help desk", "Resets passwords and two-factor sign-in, disables and enables people, and answers access requests.",
            [Permissions.PeopleRead, Permissions.PeoplePasswords, Permissions.PeopleDisable, Permissions.AccessRead, Permissions.AccessRequests]),
        new("team-lead", "Team lead", "A team's people, their API limits, credit, spend and usage. Give it over the team's group.",
            [Permissions.PeopleRead, Permissions.PeopleLimits, Permissions.CreditsManage, Permissions.BillingRead, Permissions.UsageRead]),
        new("knowledge-curator", "Knowledge curator", "Company knowledge and the databases people ask: their sources and who may read them.",
            [Permissions.KnowledgeManage, Permissions.DataManage]),
        new("integrations-admin", "Integrations admin", "Chat bots, MCP servers, plugins, everyone's scheduled tasks and their settings.",
            [Permissions.BotsManage, Permissions.McpServersManage, Permissions.PluginsManage, Permissions.TasksManageAll, Permissions.SettingsIntegrations, Permissions.SettingsTools]),
        new("security-reviewer", "Security reviewer", "Every project's reviews and automatic reviews, the safeguards' log and the audit log.",
            [Permissions.ReviewsAdmin, Permissions.ReviewPolicy, Permissions.SafeguardsRead, Permissions.AuditRead]),
        new("data-steward", "Data steward", "People's data requests, retention, legal holds, eDiscovery exports and stored files.",
            [Permissions.PrivacyManage, Permissions.RetentionManage, Permissions.LegalHoldManage, Permissions.ExportPerson, Permissions.StorageRead]),
        new("content-moderator", "Content moderator", "Rooms, the company's shared skills and assistants, and what people send about Arena.",
            [Permissions.RoomsModerate, Permissions.SharingManage, Permissions.FeedbackManage]),
        new("access-admin", "Access admin", "Roles, who holds them, rules, requests and the groups new people join; not the people themselves.",
            [Permissions.AccessManage, Permissions.GroupsMembers, Permissions.PeopleRead]),
    ];
}
