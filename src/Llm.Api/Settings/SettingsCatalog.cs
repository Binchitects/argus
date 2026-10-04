namespace Llm.Api.Settings;

/// <summary>
/// Every setting an admin can change in the app, in the order the Settings page
/// shows them. They live in the app's database; .env holds only what the stack needs to start.
/// </summary>
public static class SettingsCatalog
{
    private const string Branding = "Branding";
    private const string SignIn = "Sign-in and sessions";
    private const string Directory = "Company directory (LDAP)";
    private const string Chat = "Chat";
    private const string Model = "Model";
    private const string Argus = "Argus (GitLab)";
    private const string Tools = "Python and web";
    private const string Schedules = "Scheduled tasks";
    private const string Mail = "Email";
    private const string Safeguards = "Safeguards";



    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        // ---------------------------------------------------------------- app, live --
        new("Branding:ProductName", Branding, "Product name", "Shown in the sidebar, on the sign-in page and in the browser tab.", SettingType.Text, SettingScope.Live)
            { Default = "Argus Arena", Optional = false, Max = 60 },
        new("Branding:SignInHeadline", Branding, "Sign-in headline", "The sentence beside the sign-in form.", SettingType.Text, SettingScope.Live)
            { Default = "Your organisation's model, code search and usage, in one place.", Max = 160 },
        new("Branding:SupportContact", Branding, "Where to get help", "An email address or a link, shown on the sign-in page and in the account menu. Empty hides it.", SettingType.Text, SettingScope.Live)
            { Max = 200 },

        new("Auth:SessionIdle", SignIn, "Sign out after idle", "A session with no activity for this long ends.", SettingType.Duration, SettingScope.AppRestart)
            { Default = "01:00:00", Unit = "minutes", Min = 5, Max = 1440, Optional = false, Impact = "Applies to sessions started after the restart." },
        new("Auth:SessionMax", SignIn, "Longest session", "Sign in again after this long, however active.", SettingType.Duration, SettingScope.AppRestart)
            { Default = "12:00:00", Unit = "hours", Min = 1, Max = 168, Optional = false },
        new("Auth:RememberMe", SignIn, "\"Keep me signed in\" lasts", "How long a device stays signed in when that box is ticked.", SettingType.Duration, SettingScope.AppRestart)
            { Default = "30.00:00:00", Unit = "days", Min = 1, Max = 365, Optional = false },
        new("Throttle:MaxFailuresPerAccount", SignIn, "Wrong passwords before a lock", "Failed sign-ins for one account from one address, within the window, before that pair is locked.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "5", Min = 3, Max = 100, Optional = false },
        new("Throttle:AccountBan", SignIn, "Lock for", "How long that account is locked for that address.", SettingType.Duration, SettingScope.Live)
            { Default = "12:00:00", Unit = "hours", Min = 1, Max = 168, Optional = false },
        new("Throttle:MaxFailuresPerAddress", SignIn, "Failures from one address", "Failed sign-ins from one address, for any accounts, before the address is blocked.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "50", Min = 10, Max = 10000, Optional = false },
        new("Throttle:AddressBan", SignIn, "Block an address for", "How long a blocked address stays blocked.", SettingType.Duration, SettingScope.Live)
            { Default = "01:00:00", Unit = "hours", Min = 1, Max = 168, Optional = false },
        new("Throttle:Window", SignIn, "Counting window", "Failures older than this are forgotten.", SettingType.Duration, SettingScope.Live)
            { Default = "00:10:00", Unit = "minutes", Min = 1, Max = 1440, Optional = false },

        new("Ldap:Url", Directory, "Directory server", "ldap://host:389 or ldaps://host:636. Empty turns directory sign-in off; local accounts keep working.", SettingType.Url, SettingScope.Live)
            { Pattern = @"ldaps?://[^\s/]+(:\d+)?/?", PatternHelp = "ldap://host:389 or ldaps://host:636" },
        new("Ldap:StartTls", Directory, "Use StartTLS", "Upgrade a plain ldap:// connection to TLS before binding.", SettingType.Boolean, SettingScope.Live) { Default = "false" },
        new("Ldap:BindDn", Directory, "Service account", "A read-only account that can search people and groups, as a DN. Never an admin.", SettingType.Text, SettingScope.Live),
        new("Ldap:BindPassword", Directory, "Service account password", "From your directory team.", SettingType.Secret, SettingScope.Live),
        new("Ldap:UserBaseDn", Directory, "Where people are", "People are searched below this DN. Active Directory: an OU, or the domain root.", SettingType.Text, SettingScope.Live),
        new("Ldap:GroupBaseDn", Directory, "Where groups are", "Only for directories without memberOf (OpenLDAP without the overlay). Leave empty on Active Directory.", SettingType.Text, SettingScope.Live),
        new("Ldap:AdminGroup", Directory, "Admin group", "Members are admins here. A group name or its full DN.", SettingType.Text, SettingScope.Live),
        new("Ldap:RequiredGroup", Directory, "Required group", "Only members may sign in; people who leave it are disabled at the next check. Empty: everyone in the directory.", SettingType.Text, SettingScope.Live),
        new("Ldap:SyncInterval", Directory, "Check the directory every", "People who left are disabled and people who came back enabled.", SettingType.Duration, SettingScope.Live)
            { Default = "00:15:00", Unit = "minutes", Min = 1, Max = 1440, Optional = false },
        new("Ldap:IgnoreCertificateErrors", Directory, "Accept any certificate", "Testing only: anyone on the network path could read the service account's password.", SettingType.Boolean, SettingScope.Live)
            { Default = "false", Dangerous = true },

        new("Chat:MaxToolRounds", Chat, "Tool calls per answer", "How many rounds of tool use (Argus searches) one answer may take before it must answer.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "8", Min = 1, Max = 32, Optional = false },
        new("Chat:ToolTextChars", Chat, "Tool definitions sent whole up to", "Past this many characters of tool definitions, a chat gets in full only the tools it has loaded, and a line for each of the others that the model loads when a question needs it (it stays loaded in that chat). Saves thousands of tokens before the first answer of every chat. 0: always every tool whole.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "6000", Unit = "characters", Min = 0, Max = 500000, Optional = false },
        new("Chat:AutoCompactPercent", Chat, "Compact a chat at (% of context)", "When a chat fills this share of the model's context, its older messages become a summary the model reads instead (people still see them), and the chat goes on. 0: never; the oldest messages are left out instead. Anyone can also compact a chat with /compact.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "80", Unit = "%", Min = 0, Max = 95, Optional = false },
        new("Chat:MaxUploadBytes", Chat, "Largest attachment", "Per file.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "20971520", Unit = "bytes", Min = 1048576, Max = 104857600, Optional = false },
        new("Chat:MaxAttachmentChars", Chat, "Text kept per attachment", "Longer files are cut to this many characters and marked \"cut to fit\". The model reads what does not fit in the question in parts.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "1000000", Min = 1000, Max = 5000000, Optional = false },
        new("Chat:InlineAttachmentChars", Chat, "Text of an attachment in the question", "What of each attachment goes into the question itself. The model reads the rest in parts (the Reading files tool), so a long file does not fill the context.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "30000", Min = 2000, Max = 1000000, Optional = false },
        new("Web:AllowedSites", Tools, "Sites the chat may open", "Host names, comma separated: docs.python.org, *.microsoft.com (a domain and its subdomains), or * for any public site. Empty: the Web tool stays off. Addresses inside your network are never opened.", SettingType.Text, SettingScope.Live)
            { Default = "" },
        new("Web:SearchUrl", Tools, "Search engine", "A SearXNG instance for the Web tool's search. Empty: the websearch profile's own when it is on; otherwise no search, only opening pages.", SettingType.Url, SettingScope.Live),
        new("Sandbox:TimeoutSeconds", Tools, "Longest Python run", "A run still going after this long is stopped, and the model told so.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "60", Min = 5, Max = 300, Unit = "seconds", Optional = false },
        new("Chat:AnswersPerPerson", Chat, "Answers at once, per person", "Answers one person may have running at once, across their chats. More wait their turn, so nobody takes the model from the others.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "1", Min = 1, Max = 16, Optional = false },
        new("Chat:AnswersAtOnce", Chat, "Answers at once, everyone", "Answers the chat runs at once; the rest wait in line, served in turn (whoever has had least goes first). 0: as many as the engine serves at once (People served at once).", SettingType.WholeNumber, SettingScope.Live)
            { Default = "0", Min = 0, Max = 256, Optional = false },
        new("Chat:QueueTimeout", Chat, "Longest wait in line", "An answer that has waited this long for its turn gives up and says the model is busy.", SettingType.Duration, SettingScope.Live)
            { Default = "00:10:00", Unit = "minutes", Min = 1, Max = 120, Optional = false },
        new("Chat:ApiRequestsPerKey", Chat, "API requests at once, per key", "Requests one API key (Qwen Code, an IDE, a script) may have at the gateway at once; more are refused (HTTP 429) until one ends. 0: no limit.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "2", Min = 0, Max = 64, Optional = false },
        new("Chat:AgentsAtOnce", Chat, "Sub-agents at once", "How many sub-agents of one answer run side by side (the Sub-agents tool); the rest wait their turn. Each is a request to the model like an answer of its own, inside the answer's place in line: 1 runs them one after another.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "3", Min = 1, Max = 8, Optional = false },
        new("Chat:ToolCallTimeout", Chat, "Longest tool call", "A call to Argus or an MCP server still running after this long is stopped, and the model told so. An MCP server can have its own limit (Admin → Tools). The chat shows how long a call has run, and the progress the server reports.", SettingType.Duration, SettingScope.Live)
            { Default = "01:00:00", Unit = "minutes", Min = 1, Max = 1440, Optional = false },
        new("Chat:DefaultModel", Model, "Model new chats use", "A chat model's name. Empty: the first kept loaded (Admin → Models).", SettingType.Text, SettingScope.Live)
            { Max = 100 },
        new("Chat:ThinkingPresets", Model, "Thinking levels offered", "level:Label pairs, comma-separated. The chat offers them per conversation.", SettingType.Text, SettingScope.Live)
            { Default = "xhigh:Deep think,medium:Balanced,low:Quick,off:No thinking", Optional = false, Max = 400, Pattern = @"[a-z]+:[^,]+(,[a-z]+:[^,]+)*", PatternHelp = "level:Label pairs, e.g. medium:Balanced,off:No thinking" },
        new("Chat:DefaultThinking", Model, "Default thinking", "How hard the model thinks when a chat does not choose: one of the levels above.", SettingType.Text, SettingScope.Live)
            { Default = "medium", Optional = false, Max = 20 },
        new("Engine:ModelsMax", Model, "Models loaded at once", "Those kept loaded included. A place beyond them lets other models load when asked for.", SettingType.WholeNumber, SettingScope.AppRestart)
            { Default = "1", Min = 1, Max = 8, Optional = false, Impact = "The engine restarts and reloads its models: chat and the API pause for a few minutes." },
        new("ModelHours:TimeZone", Model, "Time zone of working hours", "The clock the models' working hours follow (Admin → Models → Working hours): an IANA name such as Europe/Berlin, Asia/Tehran or America/New_York.", SettingType.Text, SettingScope.Live)
            { Default = "UTC", Pattern = @"^(UTC|[A-Za-z]+(/[A-Za-z0-9_+\-]+){1,2})$", PatternHelp = "An IANA time zone, e.g. Europe/Berlin, or UTC." },
        new("Chat:RequestTimeout", Chat, "Longest single answer", "An answer still running after this long is stopped.", SettingType.Duration, SettingScope.AppRestart)
            { Default = "00:15:00", Unit = "minutes", Min = 1, Max = 240, Optional = false },

        // ---------------------------------------------------------------- safeguards, live --
        new("Safeguards:Enabled", Safeguards, "Safeguards on", "Everything below. Off: no limits, no checks (the gateway's credit still applies).", SettingType.Boolean, SettingScope.Live)
            { Default = "true", Optional = false },
        new("Safeguards:MaxMessageChars", Safeguards, "Longest message", "In characters; longer ones are refused with a word to attach it as a file. 0: no limit.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "100000", Min = 0, Max = 10_000_000 },
        new("Safeguards:MaxAttachmentsPerMessage", Safeguards, "Files per message", "0: no limit.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "20", Min = 0, Max = 1000 },
        new("Safeguards:MessagesPerMinute", Safeguards, "Messages per minute, per person", "More are refused until the minute passes (HTTP 429). 0: no limit.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "20", Min = 0, Max = 10_000 },
        new("Safeguards:MessagesPerDay", Safeguards, "Messages per day, per person", "0: no limit (credit still applies).", SettingType.WholeNumber, SettingScope.Live)
            { Default = "0", Min = 0, Max = 1_000_000 },
        new("Safeguards:ImagesPerDay", Safeguards, "Pictures per day, per person", "Drawn by the image tool. 0: no limit.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "100", Min = 0, Max = 100_000 },
        new("Safeguards:ResearchPerDay", Safeguards, "Deep research per day, per person", "Each one is several sub-agents reading the web for minutes. 0: no limit.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "20", Min = 0, Max = 10_000 },
        new("Safeguards:BlockedPatterns", Safeguards, "Blocked words and patterns", "A message with one is refused. Separate them with ; — a phrase matches as whole words, ignoring case; re: starts a regular expression (e.g. re:\\bproject falcon\\b).", SettingType.Text, SettingScope.Live)
            { Max = 20_000 },
        new("Safeguards:Moderation", Safeguards, "The model checks each message", "check: before answering, the chat's model reads the message as a classifier and refuses one asking for harm in the categories below (learning, safety, news and fiction stay allowed). It adds a short call to every message. off: no check.", SettingType.Choice, SettingScope.Live)
            { Default = "off", Options = ["off", "check"], Optional = false },
        new("Safeguards:ModerationCategories", Safeguards, "Categories it refuses", "What the check looks for.", SettingType.Choices, SettingScope.Live)
            { Default = "violence,self-harm,sexual-minors,weapons,malware,hate", Options = ["violence", "self-harm", "sexual-minors", "weapons", "malware", "hate", "fraud"] },
        new("Safeguards:RedactPii", Safeguards, "Mask personal data", "mask: e-mail addresses, phone and card numbers and IBANs in messages reach the model masked; the chat keeps them as written. off: as written.", SettingType.Choice, SettingScope.Live)
            { Default = "off", Options = ["off", "mask"], Optional = false },
        new("Safeguards:UntrustedToolResults", Safeguards, "Mark the web's content as data", "What web search and pages bring is marked so the model never follows instructions hidden in it (prompt injection).", SettingType.Boolean, SettingScope.Live)
            { Default = "true", Optional = false },
        new("Safeguards:StrikesToSuspend", Safeguards, "Refusals that suspend an account", "Refused messages in a day after which the account is disabled (an admin turns it back on under People). 0: never.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "0", Min = 0, Max = 1000 },
        new("Safeguards:NotifyAdmins", Safeguards, "Tell the admins of each refusal", "In their bell; the audit log keeps every one (safeguard.refused).", SettingType.Boolean, SettingScope.Live)
            { Default = "true", Optional = false },
        new("Safeguards:RefusalMessage", Safeguards, "What a refused message says", "Shown to the person whose message was refused by a blocked word or the check.", SettingType.Text, SettingScope.Live)
            { Default = "This message was not sent: it goes against your organisation's rules for the assistant. Ask an admin if you think it should be allowed.", Max = 500 },
        new("Schedules:Enabled", Schedules, "Scheduled tasks", "People may set questions to be asked on a schedule (a daily digest, a weekly report), answered as them, with their model, tools and credit.", SettingType.Boolean, SettingScope.Live)
            { Default = "true" },
        new("Schedules:PerPerson", Schedules, "Tasks per person", "How many scheduled tasks one person may have.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "10", Min = 1, Max = 100, Optional = false },
        new("Schedules:MinInterval", Schedules, "Most often", "A task may not run more often than this, so nobody fills the model's day.", SettingType.Duration, SettingScope.Live)
            { Default = "00:15:00", Unit = "minutes", Min = 1, Max = 1440, Optional = false },
        new("Schedules:WebhookHosts", Schedules, "Webhook hosts", "Hosts a task's answer may be posted to, comma separated; *.example.com for a domain and its subdomains. Add your Mattermost or chat server here. Empty: no webhooks.", SettingType.Text, SettingScope.Live)
            { Default = "hooks.slack.com, *.webhook.office.com, *.logic.azure.com, discord.com" },
        new("Mail:Host", Mail, "SMTP server", "The mail server the app sends email through (scheduled tasks' answers). Empty: no email.", SettingType.Text, SettingScope.Live),
        new("Mail:Port", Mail, "SMTP port", "587 for STARTTLS, 25 for plain SMTP inside the network.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "587", Min = 1, Max = 65535, Optional = false },
        new("Mail:StartTls", Mail, "Use STARTTLS", "Upgrade the connection to TLS before signing in. Off only for a relay inside the network.", SettingType.Boolean, SettingScope.Live)
            { Default = "true" },
        new("Mail:User", Mail, "SMTP user", "For servers that need a sign-in; empty for a relay that does not.", SettingType.Text, SettingScope.Live),
        new("Mail:Password", Mail, "SMTP password", "From your mail team.", SettingType.Secret, SettingScope.Live),
        new("Mail:From", Mail, "Sender", "The address email comes from, e.g. Argus Arena <llm@example.com>.", SettingType.Text, SettingScope.Live),

        // ---------------------------------------------------------------- Argus --



        new("ArgusIndex:Schedule", Argus, "Reindex on schedule", "When Argus brings the index up to date by itself, as cron (minute hour day month weekday), e.g. */15 * * * * for every 15 minutes. Empty: only GitLab pushes and Index now. Easier set under Indexing.", SettingType.Text, SettingScope.Live)
            { Default = "*/15 * * * *" },
        new("ArgusIndex:TimeZone", Argus, "Time zone of the index schedule", "An IANA name such as Europe/Berlin.", SettingType.Text, SettingScope.Live)
            { Default = "UTC", Pattern = @"^(UTC|[A-Za-z]+(/[A-Za-z0-9_+\-]+){1,2})$", PatternHelp = "An IANA time zone, e.g. Europe/Berlin, or UTC." },
        new("Chat:GitlabLinkUrl", Argus, "GitLab address for links", "Where people's browsers open GitLab, for the files and lines in Argus's answers. Empty: the GitLab address above. Set it when Argus reaches GitLab by an internal name.", SettingType.Url, SettingScope.Live)
            { Pattern = @"https?://\S+", PatternHelp = "http(s)://…" },





    ];

    public static readonly IReadOnlyDictionary<string, SettingDefinition> ByKey =
        All.ToDictionary(d => d.Key, StringComparer.Ordinal);

}
