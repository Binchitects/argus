using Llm.Core.Access;
using Branding;
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
    private const string CompanySignIn = "Company sign-in";
    private const string Chat = "Chat";
    private const string Model = "Model";
    private const string PricesGroup = "Prices";
    private const string CostsGroup = "Costs";
    private const string Speech = "Speech";
    private const string CodeIntel = "Code intelligence (GitLab)";
    private const string Tools = "Python and web";
    private const string PluginsGroup = "Plugins";
    private const string ArenaMcp = "Arena MCP";
    private const string Schedules = "Scheduled tasks";
    private const string Mail = "Email";
    private const string NotificationsGroup = "Notifications";
    private const string AuditGroup = "Audit log";
    private const string ComplianceGroup = "Compliance";
    private const string NoticeGroup = "Sign-in notice";
    private const string Safeguards = "Safeguards";
    private const string Knowledge = "Company knowledge";
    private const string Retention = "Data retention";
    private const string StorageGroup = "Storage";
    private const string Api = "API keys";
    private const string Network = "Network access";
    private const string BotsGroup = "Chat bots";
    private const string SecurityGroup = "Security alerts";
    private const string RoomsGroup = "Rooms and calls";
    private const string MeetingsGroup = "Meetings";
    private const string StudioGroup = "Code Studio";
    private const string LabGroup = "Science lab and 3D";
    private const string DashboardsGroup = "Dashboards";
    private const string ReplicasGroup = "Replicas";
    private const string Oidc = "CompanySignIn:Protocol=oidc";
    private const string Saml = "CompanySignIn:Protocol=saml";



    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        // ---------------------------------------------------------------- app, live --
        new("Branding:ProductName", Branding, "Product name", "Shown in the sidebar, on the sign-in page and in the browser tab.", SettingType.Text, SettingScope.Live)
            { Default = $"{Brand.Product}", Optional = false, Max = 60 },
        new("Branding:SignInHeadline", Branding, "Sign-in headline", "The sentence beside the sign-in form.", SettingType.Text, SettingScope.Live)
            { Default = "Your organisation's model, code search and usage, in one place.", Max = 160 },
        new("Branding:SupportContact", Branding, "Where to get help", "An email address or a link, shown on the sign-in page and in the account menu. Empty hides it.", SettingType.Text, SettingScope.Live)
            { Max = 200 },
        new("Branding:SourceUrl", Branding, "Where the source is", "The complete source of the version you run, linked as \"Source\" beside the version. The AGPL has every user of it offered the source: a modified version points this at its own (LICENSING.md).", SettingType.Url, SettingScope.Live)
            { Default = "https://github.com/Binchitects/argus", Optional = false, Max = 300 },

        new("Auth:SessionIdle", SignIn, "Sign out after idle", "A session with no activity for this long ends.", SettingType.Duration, SettingScope.AppRestart)
            { Default = "01:00:00", Unit = "minutes", Min = 5, Max = 1440, Optional = false, Impact = "Applies to sessions started after the restart." },
        new("Auth:SessionMax", SignIn, "Longest session", "Sign in again after this long, however active.", SettingType.Duration, SettingScope.AppRestart)
            { Default = "12:00:00", Unit = "hours", Min = 1, Max = 168, Optional = false },
        new("Auth:RememberMe", SignIn, "\"Keep me signed in\" lasts", "How long a device stays signed in when that box is ticked.", SettingType.Duration, SettingScope.AppRestart)
            { Default = "30.00:00:00", Unit = "days", Min = 1, Max = 365, Optional = false },
        new("Auth:TwoFactorRequired", SignIn, "Require two-factor sign-in", "Who must sign in with a code from their phone as well as their password. admins: anyone holding an administrative permission; everyone: every person who signs in here. A group can require it of its members too (Admin → Groups → Policies). Who has none sets it up at their next sign-in, before anything else; who signs in with the company's identity provider uses its own second factor. API keys are not affected.", SettingType.Choice, SettingScope.Live)
            { Default = "off", Options = ["off", "admins", "everyone"], Optional = false, Impact = "Applies from each person's next sign-in." },
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

        // Each directory setting's help has an example for OpenLDAP and one for Active Directory.
        new("Ldap:Url", Directory, "Directory server", "Where the directory listens. Empty turns directory sign-in off; local accounts keep working. OpenLDAP: ldap://ldap.example.com:389 (with StartTLS) or ldaps://ldap.example.com:636. Active Directory: ldaps://dc1.corp.example.com:636, a domain controller by its full name.", SettingType.Url, SettingScope.Live)
            { Pattern = @"ldaps?://[^\s/]+(:\d+)?/?", PatternHelp = "ldap://host:389 or ldaps://host:636" },
        new("Ldap:StartTls", Directory, "Use StartTLS", "Encrypts a plain ldap:// connection before anything is sent; leave it off with ldaps://, which is encrypted already. OpenLDAP: the usual way on port 389. Active Directory: works on 389 once the domain controllers have a certificate, though ldaps:// on 636 is more common.", SettingType.Boolean, SettingScope.Live) { Default = "false" },
        new("Ldap:CaCertificate", Directory, "Directory's CA", "When the directory's certificate comes from your company's own CA: that CA's certificate (PEM, -----BEGIN CERTIFICATE-----). Then the certificate must lead to it. Empty: to a CA every system trusts. OpenLDAP: the CA of its TLS certificate (the file olcTLSCACertificateFile names). Active Directory: your enterprise CA's root certificate (on the CA: certutil -ca.cert ca.cer, then certutil -encode ca.cer ca.pem).", SettingType.Text, SettingScope.Live)
            { Max = global::Llm.Api.Chat.Tools.ServerTls.MaxCaChars, Lines = 6 },
        new("Ldap:BindDn", Directory, "Service account", "A read-only account that can search people and groups; never an admin. Empty: anonymous, which most directories refuse. OpenLDAP: its full DN, like cn=readonly,dc=example,dc=com or uid=llm-reader,ou=services,dc=example,dc=com. Active Directory: reader@corp.example.com, CORP\\reader, or its DN (CN=LLM Reader,OU=Service Accounts,DC=corp,DC=example,DC=com, where the CN is its full name, not its login).", SettingType.Text, SettingScope.Live),
        new("Ldap:BindPassword", Directory, "Service account password", "From your directory team; stored encrypted, exactly as typed (a space at either end is part of it). Leave the field blank to keep the saved one (the checks below then use it, with the saved server and service account only, over a connection as safe as the saved one). Active Directory: untick \"User must change password at next logon\" and tick \"Password never expires\" for this account, or it stops working when the password ages.", SettingType.Secret, SettingScope.Live)
            { Exact = true },
        new("Ldap:UserBaseDn", Directory, "Where people are", "People are searched below this DN, at every level beneath it. OpenLDAP: ou=people,dc=example,dc=com. Active Directory: an OU like OU=Staff,DC=corp,DC=example,DC=com, or the whole domain, DC=corp,DC=example,DC=com.", SettingType.Text, SettingScope.Live),
        new("Ldap:UserFilter", Directory, "Which entries are people", "The search for the name someone signs in with; {0} is that name. The default finds OpenLDAP's uid and Active Directory's sAMAccountName and userPrincipalName, and anyone's mail. Active Directory without disabled accounts: (&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2))(|(sAMAccountName={0})(userPrincipalName={0})(mail={0}))). OpenLDAP with one kind of entry: (&(objectClass=inetOrgPerson)(|(uid={0})(mail={0}))).", SettingType.Text, SettingScope.Live)
            { Default = Ldap.LdapOptions.DefaultUserFilter, Optional = false, Max = 1000, Pattern = @"\(.*\{0\}.*\)", PatternHelp = "a filter in brackets, with {0} for the name" },
        new("Ldap:GroupBaseDn", Directory, "Where groups are", "Set it when people's entries do not list their groups in memberOf: groups naming the person as a member are then searched below it, and on OpenLDAP only those count (a group of the same name elsewhere does not). Empty: the groups in their memberOf count, from anywhere in the directory. OpenLDAP: ou=groups,dc=example,dc=com, unless its memberOf overlay covers your kind of group (the test below says). Active Directory: leave it empty; memberOf is always there.", SettingType.Text, SettingScope.Live),
        new("Ldap:AdminGroup", Directory, "Admin group", "Its members are admins here, as of each sign-in and each check. A group's name or its full DN: by its name, every group of that name counts (the test below lists them), so give the DN when another group has the same name. A groupOfNames, groupOfUniqueNames or Active Directory group; a posixGroup's members (memberUid) are not read. Direct members only: a group inside it does not count. OpenLDAP: llm-admins, or cn=llm-admins,ou=groups,dc=example,dc=com. Active Directory: LLM Admins, or CN=LLM Admins,OU=Groups,DC=corp,DC=example,DC=com.", SettingType.Text, SettingScope.Live),
        new("Ldap:RequiredGroup", Directory, "Required group", "Only its members may sign in; people who leave it are disabled at the next check. Named like the admin group. Empty: everyone the search finds. OpenLDAP: llm-users. Active Directory: LLM Users (not Domain Users, which is in nobody's memberOf).", SettingType.Text, SettingScope.Live),
        new("Ldap:SyncInterval", Directory, "Check the directory every", "Everyone who signed in from the directory is read again this often: people who left it, or the required group, are disabled, and people who came back enabled.", SettingType.Duration, SettingScope.Live)
            { Default = "00:15:00", Unit = "minutes", Min = 1, Max = 1440, Optional = false },
        new("Ldap:IgnoreCertificateErrors", Directory, "Accept any certificate", "Testing only: anyone on the network path could pose as the directory and read the service account's password and people's passwords. For a company CA, give it in \"Directory's CA\" instead.", SettingType.Boolean, SettingScope.Live)
            { Default = "false", Dangerous = true },

        new("CompanySignIn:Protocol", CompanySignIn, "Protocol", "oidc: the provider's issuer and a client registered there (Entra ID, Okta, Keycloak, Google, GitLab). saml: SAML 2.0, from the provider's metadata (Entra ID, Okta, Keycloak, ADFS, PingFederate). The other one's settings are kept, unused.", SettingType.Choice, SettingScope.Live)
            { Default = "oidc", Options = ["oidc", "saml"], Optional = false },
        new("CompanySignIn:Issuer", CompanySignIn, "Identity provider", "Its issuer: the app reads .well-known/openid-configuration below it. Entra ID: https://login.microsoftonline.com/<tenant ID>/v2.0, Okta: https://<org>.okta.com, Keycloak: https://<host>/realms/<realm>, Google: https://accounts.google.com, GitLab: its address. Empty turns company sign-in off; local accounts keep working.", SettingType.Url, SettingScope.Live)
            { Pattern = @"https?://\S+", PatternHelp = "https://...", ShownWhen = Oidc },
        new("CompanySignIn:ClientId", CompanySignIn, "Client ID", "The app's registration at the identity provider, with the redirect URI shown below.", SettingType.Text, SettingScope.Live)
            { Max = 200, ShownWhen = Oidc },
        new("CompanySignIn:ClientSecret", CompanySignIn, "Client secret", "From the same registration. Empty for a public client (PKCE alone).", SettingType.Secret, SettingScope.Live)
            { ShownWhen = Oidc },
        new("CompanySignIn:Scopes", CompanySignIn, "Scopes", "Asked for at sign-in, separated by spaces; openid is always added. Add groups when the provider sends groups only for that scope (Okta, some Keycloak setups).", SettingType.Text, SettingScope.Live)
            { Default = "openid profile email", Optional = false, Max = 400, ShownWhen = Oidc },
        new("CompanySignIn:UserNameClaim", CompanySignIn, "Username claim", "The claim with the username here, which must equal their GitLab username: preferred_username (Entra ID, Keycloak, Okta), nickname (GitLab), email (Google). An email-like value gives its part before the @.", SettingType.Text, SettingScope.Live)
            { Default = "preferred_username", Optional = false, Max = 100, ShownWhen = Oidc },
        new("CompanySignIn:GroupsClaim", CompanySignIn, "Groups claim", "The claim with the person's groups, kept for access rules like a directory's: groups (Entra ID, Okta, GitLab, Keycloak with a group mapper). A dotted path reaches into an object (realm_access.roles). Empty: no groups.", SettingType.Text, SettingScope.Live)
            { Default = "groups", Max = 100, ShownWhen = Oidc },
        new("CompanySignIn:SecondFactorMethods", CompanySignIn, "What counts as a second factor", "When two-factor sign-in is required of someone (Settings → Sign-in, or their group), company sign-in lets them in only when the provider says they used a second factor: one of these values in the identity token's amr or acr (OIDC), or the assertion's AuthnContextClassRef (SAML). Comma-separated. Entra ID sends mfa in amr after a second factor; Okta mfa or otp; Keycloak an acr you map to a step-up level; ADFS http://schemas.microsoft.com/claims/multipleauthn.", SettingType.Text, SettingScope.Live)
            { Default = Company.CompanySignInOptions.DefaultSecondFactorMethods, Optional = false, Max = 1000, Lines = 3 },
        new("CompanySignIn:SamlMetadataUrl", CompanySignIn, "Identity provider's metadata", "Where the provider publishes its SAML metadata; the app reads its entity ID, sign-in address and signing certificates there, again every hour. Entra ID: the App Federation Metadata Url (Single sign-on, SAML Certificates), Okta: the app's Metadata URL (Sign On), Keycloak: https://<host>/realms/<realm>/protocol/saml/descriptor.", SettingType.Url, SettingScope.Live)
            { Pattern = @"https?://\S+", PatternHelp = "https://...", ShownWhen = Saml },
        new("CompanySignIn:SamlMetadata", CompanySignIn, "Or its metadata XML", "The metadata file, pasted, when the app cannot reach the provider's address. Not read while the address above is set.", SettingType.Text, SettingScope.Live)
            { Max = 200_000, Lines = 6, ShownWhen = Saml },
        new("CompanySignIn:SamlSsoUrl", CompanySignIn, "Sign-in address, by hand", "Without metadata: the provider's SAML sign-in URL (HTTP-Redirect binding). Set, it wins over the metadata's.", SettingType.Url, SettingScope.Live)
            { Pattern = @"https?://\S+", PatternHelp = "https://...", ShownWhen = Saml },
        new("CompanySignIn:SamlIdpEntityId", CompanySignIn, "Provider's entity ID, by hand", "Without metadata: the Issuer of its answers. Entra ID: https://sts.windows.net/<tenant ID>/, Okta: http://www.okta.com/<id>, Keycloak: https://<host>/realms/<realm>. Set, it wins over the metadata's.", SettingType.Text, SettingScope.Live)
            { Max = 500, ShownWhen = Saml },
        new("CompanySignIn:SamlCertificate", CompanySignIn, "Signing certificate, by hand", "Without metadata: the provider's signing certificate, PEM or its base64; several PEM blocks while it rolls over to a new one. Only these are trusted, never a key inside an answer. Set, it wins over the metadata's.", SettingType.Text, SettingScope.Live)
            { Max = 20_000, Lines = 6, ShownWhen = Saml },
        new("CompanySignIn:SamlEntityId", CompanySignIn, "This app's entity ID", "What the provider knows this app as (Identifier, Audience URI, SP Entity ID). Empty: https://DOMAIN. Answers for another audience are refused.", SettingType.Text, SettingScope.Live)
            { Max = 500, ShownWhen = Saml },
        new("CompanySignIn:SamlUserNameAttribute", CompanySignIn, "Username attribute", "The attribute with the username here, which must equal their GitLab username. Empty: the NameID (Entra ID: the user principal name; Okta: the Okta username; Keycloak: the username). An email-like value gives its part before the @.", SettingType.Text, SettingScope.Live)
            { Max = 300, ShownWhen = Saml },
        new("CompanySignIn:SamlEmailAttribute", CompanySignIn, "Email attribute", "The attribute with the email address, by its Name or FriendlyName. Entra ID: http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress. Without it, an email-like username or NameID is the email.", SettingType.Text, SettingScope.Live)
            { Default = "email", Max = 300, ShownWhen = Saml },
        new("CompanySignIn:SamlDisplayNameAttribute", CompanySignIn, "Display name attribute", "The attribute with the person's name. Entra ID: http://schemas.microsoft.com/identity/claims/displayname. Without it, the username.", SettingType.Text, SettingScope.Live)
            { Default = "displayName", Max = 300, ShownWhen = Saml },
        new("CompanySignIn:SamlGroupsAttribute", CompanySignIn, "Groups attribute", "The attribute with the person's groups, one value each, kept for access rules like a directory's. Entra ID: http://schemas.microsoft.com/ws/2008/06/identity/claims/groups, Keycloak: member (its Group list mapper). Empty: no groups.", SettingType.Text, SettingScope.Live)
            { Default = "groups", Max = 300, ShownWhen = Saml },
        new("CompanySignIn:SamlAllowIdpInitiated", CompanySignIn, "Sign-in started at the provider", "Answers no sign-in here asked for are taken: someone opens the app from the provider's portal (My Apps, the Okta dashboard). Off: they are refused and the person signs in from the sign-in page, which keeps anyone from slipping their own sign-in into someone else's browser.", SettingType.Boolean, SettingScope.Live)
            { Default = "false", ShownWhen = Saml },
        new("CompanySignIn:AdminGroup", CompanySignIn, "Admin group", "Members are admins here, decided at each sign-in. One value of the groups claim (or attribute), exactly (any case): /llm-admins for a Keycloak path, a GitLab group's path, an Entra ID group's object ID, or a SCIM group's name. Empty: nobody from the provider is an admin.", SettingType.Text, SettingScope.Live)
            { Max = 300 },
        new("CompanySignIn:RequiredGroup", CompanySignIn, "Required group", "Only members may sign in, compared like the admin group; someone who left it is disabled at their next sign-in. Empty: everyone the provider lets through.", SettingType.Text, SettingScope.Live)
            { Max = 300 },
        new("CompanySignIn:ButtonLabel", CompanySignIn, "Button label", "The sign-in page says \"Sign in with\" and this: Microsoft, Okta, GitLab, your company account.", SettingType.Text, SettingScope.Live)
            { Default = "your company account", Optional = false, Max = 60 },

        new("Chat:MaxToolRounds", Chat, "Tool calls per answer", $"How many rounds of tool use ({Brand.CodeIntel} searches) one answer may take before it must answer.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "8", Min = 1, Max = 32, Optional = false },
        new("Tools:DisableAfterFailures", Chat, "Turn off a failing tool", "Failed calls in a row after which a server's, a plugin's or an admin's own tool is turned off, and the admins hear of it (Admin → Tools turns it back on). 0: never.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "25", Min = 0, Max = 10_000 },
        new("Chat:ToolTextChars", Chat, "Tool definitions sent whole up to", "Past this many characters of tool definitions, a chat gets in full only the tools it has loaded, and a line for each of the others that the model loads when a question needs it (it stays loaded in that chat). Saves thousands of tokens before the first answer of every chat. 0: always every tool whole.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "6000", Unit = "characters", Min = 0, Max = 500000, Optional = false },
        new("Chat:ToolResultChars", Chat, "Tool result the model reads whole up to", "A longer result (an MCP server's, a file's) goes to the model as its start, its error lines, its outline and its end, and the whole of it becomes a file in the chat that the model reads on with read_file: every later step reads the results again, so one huge result would fill each of them. 0: always whole.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "24000", Unit = "characters", Min = 0, Max = 1000000, Optional = false },
        new("Chat:CodeArenaProviders", Chat, "Code Arena may use other providers", "Code Arena may also offer models of Anthropic, OpenAI, DeepSeek, OpenRouter or any OpenAI-compatible API, with a person's own key (/providers in Code Arena). What its sessions send them leaves the company, and the gateway neither sees nor counts it; budgets, safeguards and the usage dashboards cover the gateway's models only. Off: Code Arena offers the gateway's models alone, and a session on another provider's goes on with one of them.", SettingType.Boolean, SettingScope.Live)
            { Default = "false" },
        new("Chat:CondenseResults", Chat, "Summarise tool results once used", "Before each step, a tool result the model has answered after twice goes to it as a summary (what was asked, its key lines: paths, line numbers, errors, the figures) with a handle read_file reads it whole by; a file read goes as its outline and the lines the model cited; a file read again the same way drops the older read. The chat keeps every result whole, and the step's own results are never summarised. Off: results stay whole until the chat nears its context.", SettingType.Boolean, SettingScope.Live)
            { Default = "true" },
        new("Chat:CompactAt", Chat, "Compact a chat at (% of context)", "When the next message would fill this share of the model's context (as the chat's context gauge shows it), the chat's older messages become a summary the model reads instead (people still see them), and the chat goes on. 0: only when a message would not fit otherwise. A chat is always compacted before a message would not fit, and once more when the model still refuses one as too long. People can choose their own under Your account; this is the default. Anyone can also compact a chat with /compact.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "80", Unit = "%", Min = 0, Max = 95, Optional = false, Pattern = "0|[2-8][0-9]|9[0-5]", PatternHelp = "0, or 20 to 95" },
        new("Chat:MaxUploadBytes", Chat, "Largest attachment", "Per file.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "20971520", Unit = "bytes", Min = 1048576, Max = 104857600, Optional = false },
        new("Chat:RoomMostPeople", Chat, "Most people in a room", "A room (a chat several people share) holds at most this many people, its owner and its groups' members included. 0: no limit. Who may make rooms: the feature.rooms.create permission (Admin → Access).", SettingType.WholeNumber, SettingScope.Live)
            { Default = "50", Min = 0, Max = 10000, Optional = false },
        new("Chat:MaxAttachmentChars", Chat, "Text kept per attachment", "Longer files are cut to this many characters and marked \"cut to fit\". The model reads what does not fit in the question in parts.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "1000000", Min = 1000, Max = 5000000, Optional = false },
        new("Chat:InlineAttachmentChars", Chat, "Text of an attachment in the question", "What of each attachment goes into the question itself. The model reads the rest in parts (the Reading files tool), so a long file does not fill the context.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "30000", Min = 2000, Max = 1000000, Optional = false },
        new("Memory:Digests", Chat, "Digests of past chats", "As a chat or a Code Arena session goes quiet, the model for small steps writes its digest (what was asked, done and decided, the names that matter, how it ended), so the model finds earlier work by what it was about (search_history) and reads it again (open_chat). Only the person's own chats are searched, and only while memory is on for them. Off: no new digests; those written stay, and are still searched.", SettingType.Boolean, SettingScope.AppRestart),
        new("Memory:Garden", Chat, "Tidy memories", "Once a day, each person's memories are tidied: those that say the same are merged into one that keeps every detail, one replaced over a month ago or not used for months is let go (never a pinned one), and a long one is tightened. Every change is listed under Memory → History, with Undo. Off: memories are only changed by people (Tidy now still works).", SettingType.Boolean, SettingScope.AppRestart),
        new("Memory:Enabled", Chat, "Memory", "Answers (the chat's and Code Arena's) read the pinned memories and those that matter to each question, and the model offers to remember what would help later (kept only when the person accepts). Each person sees, edits and deletes their own in Workspace → Memory, and can turn it off there; nobody else sees them, admins included; the company's are kept by those who manage its knowledge. Off: no answer reads or offers memories.", SettingType.Boolean, SettingScope.Live)
            { Default = "true", Optional = false },
        new("Skills:BuiltIn", Chat, "Arena's own skills", "The skills that come with Arena (review code, write tests, debug, plan a change, summarize, meeting notes and more), for everyone in the chat and Code Arena. An admin turns any one off on the Skills page; a person's, a group's or the company's skill of the same name wins over one. Off: none of them.", SettingType.Boolean, SettingScope.AppRestart)
            { Default = "true", Optional = false },
        new("Web:AllowedSites", Tools, "Sites the chat may open", "Host names, comma separated: docs.python.org, *.microsoft.com (a domain and its subdomains), or * for any public site. Empty: the Web tool stays off. Addresses inside your network are never opened.", SettingType.Text, SettingScope.Live)
            { Default = "" },
        new("Browser:Sites", Tools, "Sites the browser agent may open", "Host names, comma separated, as for the Web tool (* for any public site). Empty: the Web tool's sites. Addresses inside your network are never opened, whatever this says.", SettingType.Text, SettingScope.Live)
            { Default = "" },
        new("Browser:BlockedSites", Tools, "Sites the browser agent never opens", "Host names, comma separated (*.example.com for a domain and its subdomains): refused even when the sites above allow them.", SettingType.Text, SettingScope.Live)
            { Default = "" },
        new("Plugins:CatalogUrl", PluginsGroup, "Plugin catalog", "An index.json that lists plugins to install (each zip's address and SHA-256), besides those that come with the app. Empty: only those.", SettingType.Url, SettingScope.Live),
        new("Plugins:CatalogKey", PluginsGroup, "Catalog's public key", "The publisher's ECDSA P-256 public key (PEM). When set, the catalog's index.json.sig must verify against it, or nothing is installed from it.", SettingType.Text, SettingScope.Live)
            { Max = 1000 },
        new("Mcp:Enabled", ArenaMcp, "Arena MCP", "Outside agents (Code Arena, Claude Code, Qwen Code, Continue) get each person's chat tools at https://DOMAIN/mcp, signed in with that person's API key: the tools they may use in the chat, run as them, each call audited (mcp.call).", SettingType.Boolean, SettingScope.Live)
            { Default = "true" },
        new("Mcp:ListWaitSeconds", ArenaMcp, "Wait for a slow tool server", $"Connecting an agent starts {Brand.CodeIntel} and the MCP servers here together and waits this long at most: one slower is listed as not available now, with the reason, and asked again on the next list, so a slow or dead server never holds an agent.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "10", Min = 1, Max = 120, Unit = "seconds", Optional = false },
        new("Web:SearchUrl", Tools, "Search engine", "A SearXNG instance for the Web tool's search. Empty: the web-search feature's own when it is on; otherwise no search, only opening pages.", SettingType.Url, SettingScope.Live),
        new("Sandbox:TimeoutSeconds", Tools, "Longest Python run", "A run still going after this long is stopped, and the model told so.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "60", Min = 5, Max = 300, Unit = "seconds", Optional = false },
        new("Chat:AnswersPerPerson", Chat, "Answers at once, per person", "Answers one person may have running at once, across their chats. More wait their turn, so nobody takes the model from the others. A person's own (Admin → People) or their group's (Admin → Groups) wins, and holds their API requests too.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "1", Min = 1, Max = 16, Optional = false },
        new("Chat:AnswersAtOnce", Chat, "Answers at once, everyone", "Each model runs as many answers at once as it serves (its Answers at once in Admin → Models, and its copies on other GPU servers); the rest wait in that model's line, served in turn (whoever has had least goes first), so people on one model never wait for another's. API requests (chat completions with a key) wait in the same lines. Set this to also limit all models together. 0: no limit beyond each model's own.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "0", Min = 0, Max = 256, Optional = false },
        new("Chat:QueueLength", Chat, "Longest line", "The most requests waiting their turn (chat answers and API requests together). One more is refused at once, as busy: the chat says to try again in a little while, an API request gets HTTP 503 with Retry-After. 0: no limit.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "0", Min = 0, Max = 100_000, Optional = false },
        new("Chat:QueueTimeout", Chat, "Longest wait in line", "An answer that has waited this long for its turn gives up and says the model is busy.", SettingType.Duration, SettingScope.Live)
            { Default = "00:10:00", Unit = "minutes", Min = 1, Max = 120, Optional = false },
        new("Chat:ApiRequestsPerKey", Chat, "API requests at once, per key", "Requests one API key (a coding agent, an IDE, a script) has answered at once; more wait their turn, in order, and go on as one ends (not refused). 0: no limit.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "4", Min = 0, Max = 64, Optional = false },
        new("Chat:ApiQueueTimeout", Chat, "API request's longest wait", "How long one API key's request waits for its turn (above) before it is refused with 429.", SettingType.Duration, SettingScope.Live)
            { Default = "00:10:00", Unit = "minutes", Min = 1, Max = 120, Optional = false },
        new("Gateway:AnswerCache", Api, "Answer cache", "A repeated identical request to /v1/chat/completions with the same API key and model is answered from the app's database instead of the model: it costs nothing, and the response says x-arena-cache: hit. For pipelines and FAQ bots that ask the same thing; the chat never uses it. opt-in: each person turns it on for their key (Your account → API key); all: every key; off: every request goes to the model.", SettingType.Choice, SettingScope.Live)
            { Default = "off", Options = ["off", "opt-in", "all"], Optional = false },
        new("Gateway:AnswerCacheTtl", Api, "Keep cached answers for", "An answer older than this is asked of the model again.", SettingType.Duration, SettingScope.Live)
            { Default = "1.00:00:00", Unit = "hours", Min = 1, Max = 720, Optional = false },
        new("Gateway:RequestsPerMinute", Api, "Requests a minute, per key", "Requests one API key may send to the gateway in a minute; more are refused (HTTP 429, with Retry-After) until the minute is over. A group's own (Admin → Groups) or a person's own (Admin → People) replaces it. The chat is never limited by it. 0: no limit.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "0", Min = 0, Max = Gateway.RateLimits.MaxRequests, Optional = false },
        new("Gateway:TokensPerMinute", Api, "Tokens a minute, per key", "Tokens one API key may use at the gateway in a minute: what the model reads and writes, less the prompt it reads from its cache. A request is let through only if its prompt and its max_tokens fit what is left, so keep it well above the largest request. A group's or a person's own replaces it. 0: no limit.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "0", Min = 0, Max = Gateway.RateLimits.MaxTokens, Optional = false },
        new("Chat:AgentsAtOnce", Chat, "Sub-agents at once", "How many sub-agents of one answer run side by side (the Sub-agents tool); the rest wait their turn. Each is a request to the model like an answer of its own, inside the answer's place in line: 1 runs them one after another.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "3", Min = 1, Max = 8, Optional = false },
        new("Chat:ToolCallTimeout", Chat, "Longest tool call", $"A call to {Brand.CodeIntel} or an MCP server still running after this long is stopped, and the model told so. An MCP server can have its own limit (Admin → Tools). The chat shows how long a call has run, and the progress the server reports.", SettingType.Duration, SettingScope.Live)
            { Default = "01:00:00", Unit = "minutes", Min = 1, Max = 1440, Optional = false },
        new("Quality:PublicLeaderboard", Chat, "Arena leaderboard for everyone", "Everyone sees the leaderboard of the company's models on its own questions, made from the votes of Compare in the chat. Off: admins only (Admin → Quality).", SettingType.Boolean, SettingScope.Live)
            { Default = "true", Optional = false },
        new("Chat:DefaultModel", Model, "Model new chats use", "A chat model's name, or auto for Auto (offered while a model for small steps is set). Empty: the first kept loaded (Admin → Models); with none kept, the first loaded, which stays the one while it is loaded. With 2 or more models at once, this model never makes room for another, and loads again should the engine unload it.", SettingType.Text, SettingScope.Live)
            { Max = 100 },
        new("Chat:SmallModel", Model, "Model for sub-agents and small steps", "A small, fast chat model for the many short steps: sub-agents, chat titles, compaction summaries, the safeguards' check, and Auto, which it brings to the chat's model menu (it answers easy questions itself and hands the rest on). It never thinks for them. With a place left beside it for the other models (3 or more loaded at once, with the big one), it keeps a place of its own and never makes room; at 2 at once it shares the last place with the models loaded on request, and while another sits there each step uses the answer's own model. Keep it loaded (Admin → Models) to keep it loaded whatever the others. Empty: each step uses the answer's own model.", SettingType.Text, SettingScope.Live)
            { Max = 100 },
        new("Chat:TranslationModel", Model, "Model that translates rooms", "A chat model's name, for reading rooms in another language: each message is translated once into a language and kept for all who read in it. A larger model translates better (dates, names, idioms). Empty: the model for small steps.", SettingType.Text, SettingScope.Live)
            { Max = 100 },
        new("Chat:SvgModel", Model, "Model that draws SVG", "A chat model's name, for the media studio's vector drawings: drawing SVG from words, changing it as asked and planning its motions. A larger model draws better; one that sees is shown its drawing and fixes what looks wrong. Empty: each person's own model.", SettingType.Text, SettingScope.Live)
            { Max = 100 },
        new("Media:PictureModel", Model, "Picture model the chat uses", "A picture model's name (Admin → Models), for the chat's pictures when the person does not name one. Empty: the first one they may use.", SettingType.Text, SettingScope.Live)
            { Max = 100 },
        new("Media:VideoModel", Model, "Video model the chat uses", "A video model's name, for the chat's clips. Empty: the first one they may use.", SettingType.Text, SettingScope.Live)
            { Max = 100 },
        new("Media:TranscriptionModel", Model, "Speech to text model", "A speech to text model's name, for Talk and voice messages. Empty: the first one.", SettingType.Text, SettingScope.Live)
            { Max = 100 },
        new("Chat:ThinkingPresets", Model, "Thinking levels offered", "level:Label pairs, comma-separated. The chat offers them per conversation.", SettingType.Text, SettingScope.Live)
            { Default = "xhigh:Deep think,medium:Balanced,low:Quick,off:No thinking", Optional = false, Max = 400, Pattern = @"[a-z]+:[^,]+(,[a-z]+:[^,]+)*", PatternHelp = "level:Label pairs, e.g. medium:Balanced,off:No thinking" },
        new("Chat:DefaultThinking", Model, "Default thinking", "How hard the model thinks when a chat does not choose: one of the levels above.", SettingType.Text, SettingScope.Live)
            { Default = "medium", Optional = false, Max = 20 },
        new("Engine:SessionCacheGb", Model, "RAM for chats that lost their slot", "Each loaded model keeps, in RAM, the chats that another took the slot of (llama.cpp's prompt cache): one coming back reads from there what it had, not its whole history again. 8 GB keeps about a dozen long chats of a hybrid model (about 0.6 GB each with its checkpoints). More keeps more people's chats, but the RAM it takes is not there for the models' files: on a host whose model does not fit RAM and the GPU, keep it small. 0: none.", SettingType.WholeNumber, SettingScope.AppRestart)
            { Default = "8", Min = 0, Max = 512, Optional = false, Unit = "GB", Impact = "The engine restarts and reloads its models: chat and the API pause for a few minutes." },
        new("Engine:KvCacheGb", Model, "Disk for conversations' caches", "Below the RAM above, on KV_CACHE_DIR (a fast disk, an NVMe): a conversation whose slot goes to another (a chat, an agent's session through an API key) is written there, and read back into a slot when it returns, however long after and across the engine's restarts, so the engine reads only what is new instead of the whole history (seconds instead of minutes on a long one). The least lately used go first when it is full. 0: none.", SettingType.WholeNumber, SettingScope.AppRestart)
            { Default = "200", Min = 0, Max = 100000, Optional = false, Unit = "GB", Impact = "Turning it on or off restarts the engine: chat and the API pause for a few minutes." },
        new("Engine:KvCacheDays", Model, "Keep conversations' caches for", "How long a conversation's cache stays on disk after it was last used. 0: for ever (only the size above takes the oldest out).", SettingType.WholeNumber, SettingScope.AppRestart)
            { Default = "0", Min = 0, Max = 3650, Optional = false, Unit = "days" },
        new("Chat:ThinkingBetweenTools", Model, "Thinking between tool calls", "How hard the model thinks in the rounds after a tool's result, where it reads the result and calls the next tool or answers: one of the levels above, never above the chat's own, or same (as the chat's). Each round's thinking is time people wait between two tool calls: about 3 seconds a round at medium on a model writing 10 words a second.", SettingType.Text, SettingScope.Live)
            { Default = "low", Optional = false, Max = 20 },
        new("Engine:ModelsMax", Model, "Models loaded at once", "Those kept loaded included. A place beyond them lets other models load when asked for, beside the loaded ones (in what is left of the GPU, else in RAM), instead of unloading them for everyone. At the limit, an idle model unloads first, the smallest first, unless it is kept loaded, the one new chats use, or the one for small steps while a place is left beside it for the others: those never make room. With none idle, the request waits a minute, then says the engine is full; API keys' requests too (the gateway asks the app first). Should the engine unload one by its own choice, a kept model, or the one new chats use, loads again. 1: one model at a time: a request for another model waits for the loaded one to be idle, a minute at most, then is refused (while people keep the loaded one busy, those on other models are refused); only a kept model stays.", SettingType.WholeNumber, SettingScope.AppRestart)
            { Default = "2", Min = 1, Max = 8, Optional = false, Impact = "The engine restarts and reloads its models: chat and the API pause for a few minutes." },
        new("ModelHours:TimeZone", Model, "Time zone of working hours", "The clock the models' working hours follow (Admin → Models → Working hours): an IANA name such as Europe/Berlin, Asia/Tehran or America/New_York.", SettingType.Text, SettingScope.Live)
            { Default = "UTC", Pattern = @"^(UTC|[A-Za-z]+(/[A-Za-z0-9_+\-]+){1,2})$", PatternHelp = "An IANA time zone, e.g. Europe/Berlin, or UTC." },

        // ---------------------------------------------------------------- prices, live (the gateway learns them at once) --
        new("Prices:InputPerMtok", PricesGroup, "Input, per million tokens", "What a chat model without its own prices (Admin → Models) costs for each million tokens of the prompt the model reads: the conversation, files, tool results. The defaults are about what hosted services charge for a small open model; set your own.", SettingType.Number, SettingScope.Live)
            { Default = "0.20", Unit = "$ per 1M tokens", Min = 0, Max = 1000, Optional = false },
        new("Prices:CachedInputPerMtok", PricesGroup, "Cached input, per million tokens", "Prompt tokens the engine reads from its cache: the start of a conversation it has seen, sent again with each answer. They cost the engine little. A model's cached input is never priced above its input.", SettingType.Number, SettingScope.Live)
            { Default = "0.02", Unit = "$ per 1M tokens", Min = 0, Max = 1000, Optional = false },
        new("Prices:OutputPerMtok", PricesGroup, "Output, per million tokens", "Tokens the model writes, its thinking included.", SettingType.Number, SettingScope.Live)
            { Default = "0.80", Unit = "$ per 1M tokens", Min = 0, Max = 1000, Optional = false },
        new("Prices:PerImage", PricesGroup, "A picture", "Each picture the picture model draws, for the chat or an API key.", SettingType.Number, SettingScope.Live)
            { Default = "0.01", Unit = "$ per picture", Min = 0, Max = 100, Optional = false },
        new("Prices:PerVideoSecond", PricesGroup, "A second of video", "Each second of a clip the video model makes.", SettingType.Number, SettingScope.Live)
            { Default = "0.05", Unit = "$ per second", Min = 0, Max = 100, Optional = false },
        new("Prices:PerAudioMinute", PricesGroup, "A minute of sound turned into text", "Speech to text: a recording or a video's sound track for a model that cannot hear, Talk, an API key's transcriptions. Priced by the sound's length, when the speech server says it (the chat always asks for it).", SettingType.Number, SettingScope.Live)
            { Default = "0.006", Unit = "$ per minute", Min = 0, Max = 100, Optional = false },
        new("Prices:PerThousandCharacters", PricesGroup, "1,000 characters read aloud", "Text to speech: Read aloud, the Speech tool, Talk's answers, an API key's speech.", SettingType.Number, SettingScope.Live)
            { Default = "0.015", Unit = "$ per 1,000 characters", Min = 0, Max = 100, Optional = false },
        new("Prices:PerMusicMinute", PricesGroup, "A minute of music", "Each minute of music the media studio's music model makes.", SettingType.Number, SettingScope.Live)
            { Default = "0.06", Unit = "$ per minute", Min = 0, Max = 100, Optional = false },

        // ---------------------------------------------------------------- running costs, live (Admin → Accounting) --
        new("Costs:PowerPricePerKwh", CostsGroup, "Power, per kWh", "What a kilowatt-hour of electricity costs here. Admin → Accounting counts the GPUs' measured draw and the rest of the machine's, times the cooling overhead.", SettingType.Number, SettingScope.Live)
            { Default = "0.15", Unit = "$ per kWh", Min = 0, Max = 100, Optional = false },
        new("Costs:Pue", CostsGroup, "Cooling and power overhead (PUE)", "What the room draws for each watt the machine draws: cooling, power supplies, UPS. 1.0 is none; a typical server room is 1.3 to 1.6.", SettingType.Number, SettingScope.Live)
            { Default = "1.4", Min = 1, Max = 4, Optional = false },
        new("Costs:OtherPowerWatts", CostsGroup, "The rest of the machine's draw", "What the machine draws besides its GPUs, on average: CPUs, memory, disks, fans. The GPUs' own draw is measured.", SettingType.Number, SettingScope.Live)
            { Default = "300", Unit = "W", Min = 0, Max = 100000, Optional = false },
        new("Costs:HardwarePrice", CostsGroup, "What the hardware cost", "The machine's price (servers, GPUs, network): spread over its life below, a month at a time. 0 leaves it out.", SettingType.Number, SettingScope.Live)
            { Default = "0", Unit = "$", Min = 0, Max = 1000000000, Optional = false },
        new("Costs:HardwareMonths", CostsGroup, "Written off over", "The hardware's life, the months its price is spread over.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "36", Unit = "months", Min = 1, Max = 240, Optional = false },
        new("Costs:ServicePerMonth", CostsGroup, "Service, a month", "Everything else running it costs a month: support contracts, licences, hosting or rack space, the people who look after it.", SettingType.Number, SettingScope.Live)
            { Default = "0", Unit = "$ per month", Min = 0, Max = 1000000000, Optional = false },
        new("Costs:PersonHourly", CostsGroup, "A person's hour", "What an hour of a person's work costs, salary and overheads: a project's work done is set against the hours it would take by hand (each task's and goal's estimate) at this rate.", SettingType.Number, SettingScope.Live)
            { Default = "50", Unit = "$ per hour", Min = 0, Max = 100000, Optional = false },
        new("Costs:ShareBy", CostsGroup, "Share the cost out by", "How Admin → Accounting shares the running cost among people, groups and projects: spend (their use at the prices above, so output, pictures and video weigh more), tokens, or time (the model's time on their requests).", SettingType.Choice, SettingScope.Live)
            { Default = "spend", Options = ["spend", "tokens", "time"], Optional = false },

        new("Chat:RequestTimeout", Chat, "Longest single answer", "An answer still running after this long is stopped.", SettingType.Duration, SettingScope.AppRestart)
            { Default = "00:15:00", Unit = "minutes", Min = 1, Max = 240, Optional = false },

        // ---------------------------------------------------------------- speech, live: everyone's until they choose their own (Your account → Voice) --
        new("Speech:Language", Speech, "Language people speak", "For speech to text (Talk, voice messages, and API keys' speech to text that names no language): auto lets Whisper hear which; a language speech to text knows (en, fa, de) writes everything down in it, which suits short or accented speech.", SettingType.Text, SettingScope.Live)
            { Default = "auto", Optional = false, Max = 4, Pattern = "auto|" + string.Join('|', Llm.Api.Chat.VoiceCatalog.WhisperLanguages), PatternHelp = "auto, or the code of a language Whisper knows, such as en, fa or de" },
        new("Speech:Voices", Speech, "Voice for each language", "The voice that reads each language aloud (read aloud, Talk, the Speech tool, and API keys' speech that names no voice): a text is read in the voice of its language. Choose each from the voices the speech models offer, and Try it; a language left to the first offered gets the first voice offered for it. A voice here that is not offered is said below.", SettingType.Text, SettingScope.Live)
            { Default = "en:kokoro/af_heart,fa:piper-fa/gyro", Optional = false, Max = 1000, Pattern = @"[a-z]{2,3}:[^,:/\s]+/[^,:/\s]+(\s*,\s*[a-z]{2,3}:[^,:/\s]+/[^,:/\s]+)*", PatternHelp = "language:model/voice pairs, e.g. en:kokoro/af_heart,fa:piper-fa/gyro" },
        new("Speech:Speed", Speech, "Reading speed", "1 is the voice's own pace; 0.5 is half as fast, 2 twice as fast.", SettingType.Number, SettingScope.Live)
            { Default = "1", Min = 0.5m, Max = 2, Optional = false },
        new("Speech:ReadAloud", Speech, "Read answers aloud in Talk", "In Talk, and for a voice message, the answer is read aloud as it is written. Off: it is only shown.", SettingType.Boolean, SettingScope.Live)
            { Default = "true", Optional = false },

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
        new("Safeguards:UntrustedToolResults", Safeguards, "Mark the web's content as data", "What web search and pages bring, and the passages company knowledge finds, is marked so the model never follows instructions hidden in it (prompt injection).", SettingType.Boolean, SettingScope.Live)
            { Default = "true", Optional = false },
        new("Safeguards:StrikesToSuspend", Safeguards, "Refusals that suspend an account", "Refused messages in a day after which the account is disabled (an admin turns it back on under People). 0: never.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "0", Min = 0, Max = 1000 },
        new("Safeguards:NotifyAdmins", Safeguards, "Tell the admins of each refusal", "In their bell; the audit log keeps every one (safeguard.refused).", SettingType.Boolean, SettingScope.Live)
            { Default = "true", Optional = false },
        new("Safeguards:RefusalMessage", Safeguards, "What a refused message says", "Shown to the person whose message a rule refused, unless the rule says its own.", SettingType.Text, SettingScope.Live)
            { Default = "This message was not sent: it goes against your organisation's rules for the assistant. Ask an admin if you think it should be allowed.", Max = 500 },
        new("Safeguards:CheckModel", Safeguards, "The model that judges", "The model that reads texts for the topic and injection rules (Admin → Safeguards), once a text for all of them. A small, quick model is best. Empty: the model the text is for.", SettingType.Text, SettingScope.Live)
            { Max = 200 },
        new("Safeguards:CheckApi", Safeguards, "Check API requests too", "API keys' requests pass the rules that apply to the API stage (Admin → Safeguards): secrets, personal data. The gateway asks the app before each one (its guardrail in config/litellm.yaml). Credit applies either way.", SettingType.Boolean, SettingScope.Live)
            { Default = "true", Optional = false },

        // ---------------------------------------------------------------- retention, live --
        new("Retention:AuditYears", Retention, "Keep the audit log for", "Its oldest entries go after this many years (the chain then starts at the oldest kept). HIPAA keeps such records six years; 0: forever.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "6", Unit = "years", Min = 0, Max = 100, Optional = false },
        new("Retention:GatewayLogDays", Retention, "Keep the gateway's request log for", "Each request through the gateway (who, which model, tokens, cost): usage, credit and the dashboards read it. 0: forever.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "400", Unit = "days", Min = 0, Max = 36500, Optional = false },
        new(global::Llm.Api.Oidc.AddressFilter.AllowKey, Network, $"Only these addresses may reach {Brand.Product}",
            $"One address or range a line (10.0.0.0/8, 192.168.1.20, 2001:db8::/32; # starts a comment): the web, the API, the gateway and {Brand.CodeIntel} answer only these, "
            + "as Traefik sees the caller (behind another proxy, its address). Empty: every address. A list that would leave out your own address is refused.",
            SettingType.Text, SettingScope.Live)
            { Max = 20_000, Lines = 5, Dangerous = true, Impact = "Every request from another address is refused at once (403), sign-in and API keys included." },
        new(global::Llm.Api.Oidc.AddressFilter.BlockKey, Network, "Never these addresses",
            "One address or range a line, as above: refused whatever the list above says. A list that would block your own address is refused.",
            SettingType.Text, SettingScope.Live)
            { Max = 20_000, Lines = 5, Dangerous = true, Impact = "Every request from these addresses is refused at once (403)." },
        new("Retention:PromptLogDays", Retention, "Keep API requests in the prompt log for", "Each API request's last question and its answer (Admin → Prompt log, with prompts.review); the chat's are kept with the chats. 0: forever.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "90", Min = 0, Max = 3650, Unit = "days", Optional = false },
        new("Gateway:LogPrompts", Api, "Prompt log of API requests", "Keeps each API request's last question and its answer, for those with prompts.review (Admin → Prompt log); the chat's questions and answers are there from the chats themselves. Off: API requests are not kept.", SettingType.Boolean, SettingScope.Live)
            { Default = "true" },
        new("Gateway:PromptLogKeep", Api, "Prompt log: how much of each question and answer", "The first so many characters of each API request's last question and of its answer are kept; the rest is cut (…).", SettingType.WholeNumber, SettingScope.Live)
            { Default = "20000", Min = 200, Max = 20_000, Unit = "characters", Optional = false },
        new("Gateway:PromptLogPictures", Api, "Prompt log: keep the pictures", "A question's pictures (sent as data) are kept with its line and go with it. Off: only the words.", SettingType.Boolean, SettingScope.Live)
            { Default = "true" },
        new("Gateway:PromptLogModels", Api, "Prompt log: only these models", "One model a line (or comma): only their API requests are kept. Empty: every model's.", SettingType.Text, SettingScope.Live)
            { Max = 4000, Lines = 3 },
        new("Tags:Known", Api, "Tags", "The tags people and the small model choose from for chats, projects and prompts (review, security, …), one a line or comma. Others may be typed; these are offered first, and Summarize and tag picks only from them. Accounting → By tag counts each.", SettingType.Text, SettingScope.Live)
            { Default = "review\nsecurity\nbug\nidea\nquestion\ndocs", Max = 4000, Lines = 6 },
        new("Retention:ToolCallDays", Retention, "Keep the record of tool calls for", "Which tool was called, how long it took, whether it failed (never what was sent): Admin → Tools reads it. 0: forever.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "180", Unit = "days", Min = 0, Max = 36500, Optional = false },
        new("Retention:NotificationDays", Retention, "Keep news in the bell for", "People's notifications, read or not. 0: forever.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "180", Unit = "days", Min = 0, Max = 36500, Optional = false },
        new("Retention:Days", Retention, "Keep chats for", "A chat and its files are deleted this many days after its last message, and so are files nobody uses that are as old. A group can set its own (Admin → Groups); the shortest of a person's groups applies. People on legal hold keep everything. Each deletion is audited, with counts. Empty: forever.", SettingType.WholeNumber, SettingScope.Live)
            { Unit = "days", Min = 1, Max = 36500 },

        // ---------------------------------------------------------------- storage, live --
        new("Storage:AlertPercent", StorageGroup, "Warn when a disk is fuller than", "A disk of the host (or a folder the app mounts) past this share raises an alert: on the Alerts page, in the admins' bell, by email and the alerts webhook, once each time it passes it. The stack's own rules warn at 85% and 95% besides.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "80", Unit = "%", Min = 50, Max = 99, Optional = false },
        new("Storage:PersonMegabytes", StorageGroup, "Room for each person's files", "What one person's files may take: uploads, and the pictures, videos and speech the tools make. Past it, uploads and those tools are refused until they delete chats. An admin can give someone their own (Admin → Storage → People). Empty: no limit.", SettingType.WholeNumber, SettingScope.Live)
            { Unit = "MB", Min = 1, Max = 100_000_000 },
        new("Storage:MediaDays", StorageGroup, "Old generated media", "The clean-up of old pictures, videos and speech the tools made (Admin → Storage) takes them as old after this many days, unless you choose another age there. Nothing is deleted by itself.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "90", Unit = "days", Min = 1, Max = 36500, Optional = false },
        new("Schedules:Enabled", Schedules, "Scheduled tasks", "People may set questions to be asked on a schedule (a daily digest, a weekly report), answered as them, with their model, tools and credit.", SettingType.Boolean, SettingScope.Live)
            { Default = "true" },
        new("Schedules:PerPerson", Schedules, "Tasks per person", "How many scheduled tasks one person may have.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "10", Min = 1, Max = 100, Optional = false },
        new("Schedules:MinInterval", Schedules, "Most often", "A task may not run more often than this, so nobody fills the model's day.", SettingType.Duration, SettingScope.Live)
            { Default = "00:15:00", Unit = "minutes", Min = 1, Max = 1440, Optional = false },
        new("GitLab:Url", Schedules, "GitLab address for tasks", $"Where tasks run by GitLab's events read details (a merge request's changes, a failed job's log) and comment. Empty: {Brand.CodeIntel}'s (GITLAB_URL).", SettingType.Url, SettingScope.Live),
        new("GitLab:BotToken", Schedules, "GitLab bot token", $"A bot account's token (scope api, Reporter in the projects): reads what an event leaves out and writes the comments of tasks that reply in GitLab. Never {Brand.CodeIntel}'s read-only token; each comment is audited.", SettingType.Secret, SettingScope.Live),
        new("Schedules:WebhookHosts", Schedules, "Webhook hosts", "Hosts a task's answer may be posted to, comma separated; *.example.com for a domain and its subdomains. Add your Mattermost or chat server here. Empty: no webhooks.", SettingType.Text, SettingScope.Live)
            { Default = "hooks.slack.com, *.webhook.office.com, *.logic.azure.com, discord.com" },
        new("Knowledge:SyncEvery", Knowledge, "Read sources again every", "How often each knowledge source (Admin → Knowledge) is read again. Only what changed is embedded again; a GitLab project with no new activity is not read at all. Members of GitLab projects are read every 15 minutes whatever this is.", SettingType.Duration, SettingScope.Live)
            { Default = "01:00:00", Unit = "minutes", Min = 15, Max = 10080, Optional = false },
        new("Knowledge:PassageChars", Knowledge, "Passages of long files per question", "With the embedder, a chat's long files and a project's files that do not fit go to the model as their passages that match each question, up to this many characters, instead of their first part.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "12000", Unit = "characters", Min = 2000, Max = 100000, Optional = false },
        new("Knowledge:EmbedUrl", Knowledge, "Embeddings server", "An OpenAI-compatible embeddings server, without /v1: the stack's own embedder (the embed feature), or one of the company's. Company knowledge, the chat's long files and Ask your data embed with it.", SettingType.Url, SettingScope.Live)
            { Default = "http://embed:8080", Optional = false, Pattern = @"https?://\S+", PatternHelp = "http://host:port" },
        new("Knowledge:EmbedModel", Knowledge, "Embedding model", "The model the embeddings server is asked for. The stack's own serves nomic-embed-text.", SettingType.Text, SettingScope.Live)
            { Default = "nomic-embed-text", Optional = false, Max = 200, Impact = "Each source and file is embedded again with it when it is next read (Read sources again every); until then search finds only what it embedded." },
        new("Knowledge:EmbedKey", Knowledge, "Embeddings server's key", "A bearer key, for a server outside the stack that asks for one. Empty: none.", SettingType.Secret, SettingScope.Live),
        new("Knowledge:DocumentPrefix", Knowledge, "Before each passage", "What the embedding model wants before a passage it embeds, a space at the end included: nomic-embed-text wants \"search_document: \". Empty for a model that wants none.", SettingType.Text, SettingScope.Live)
            { Default = "search_document: ", Max = 100, Exact = true, Impact = "Embed again (change the model, or the sources) for passages embedded before to match." },
        new("Knowledge:QueryPrefix", Knowledge, "Before each question", "What it wants before a question: nomic-embed-text wants \"search_query: \". Empty for a model that wants none.", SettingType.Text, SettingScope.Live)
            { Default = "search_query: ", Max = 100, Exact = true },
        new("Knowledge:PgVector", Knowledge, "Compare with pgvector", "Passages are compared in Postgres with pgvector, which the stack's Postgres has. Off for a company Postgres without the extension: plain SQL, slower but the same answers.", SettingType.Boolean, SettingScope.Live)
            { Default = "true" },
        new("Mail:Host", Mail, "SMTP server", "The mail server the app sends email through (scheduled tasks' answers). Empty: no email.", SettingType.Text, SettingScope.Live),
        new("Mail:Port", Mail, "SMTP port", "587 for STARTTLS, 25 for plain SMTP inside the network.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "587", Min = 1, Max = 65535, Optional = false },
        new("Mail:StartTls", Mail, "Use STARTTLS", "Upgrade the connection to TLS before signing in. Off only for a relay inside the network.", SettingType.Boolean, SettingScope.Live)
            { Default = "true" },
        new("Mail:User", Mail, "SMTP user", "For servers that need a sign-in; empty for a relay that does not.", SettingType.Text, SettingScope.Live),
        new("Mail:Password", Mail, "SMTP password", "From your mail team.", SettingType.Secret, SettingScope.Live),
        new("Mail:From", Mail, "Sender", $"The address email comes from, e.g. {Brand.Product} <llm@example.com>.", SettingType.Text, SettingScope.Live),
        new("Notifications:Email", NotificationsGroup, "Email the credit and alert news", "Besides the bell: a person at 80% of their credit and with it used up, and the admins of alerts that start firing and of credit used up, by email (once each). Needs email set up (Email).", SettingType.Boolean, SettingScope.Live)
            { Default = "true", Optional = false },
        new("Audit:Syslog", AuditGroup, "Send it to syslog", "Every entry of the audit log, as it is written and in order, to the company's SIEM or log collector by syslog (RFC 5424, facility log audit, the entry as JSON with its hash): udp://host:514, tcp://host:514 or tls://host:6514 (the collector's certificate is checked). A collector that does not answer is tried again, later each time, and nothing is skipped. Empty: none.", SettingType.Text, SettingScope.Live)
            { Pattern = @"(udp|tcp|tls)://[^\s/:]+:\d{1,5}", PatternHelp = "udp://, tcp:// or tls://host:port", Max = 300 },
        new("Audit:Webhook", AuditGroup, "Post it to a webhook", "Every entry, as it is written, posted as JSON in batches (each entry with its hash, so the receiver can walk the chain too) to this https:// address: a SIEM's HTTP collector (Splunk HEC, Elastic, Sentinel's data collector through a relay). Empty: none.", SettingType.Secret, SettingScope.Live)
            { Pattern = @"https://\S+", PatternHelp = "an https:// URL" },
        new("Audit:WebhookSecret", AuditGroup, "The webhook's signing key", "Each post carries X-Arena-Signature: sha256= and the HMAC-SHA256 of its body with this key, in hex, for the receiver to check. Make one with: openssl rand -hex 32.", SettingType.Secret, SettingScope.Live),

        new("Notice:Title", NoticeGroup, "Title", "Above the notice, on the sign-in page and where people accept it.", SettingType.Text, SettingScope.Live)
            { Default = "Before you use this service", Optional = false, Max = 120 },
        new("Notice:Text", NoticeGroup, "Notice", "What everyone accepts before using the service: acceptable use, the privacy notice, a link to the full policy (Markdown). Shown on the sign-in page; after signing in, each person accepts it before anything else, and each acceptance is kept with its version and audited. Empty: no notice.", SettingType.Text, SettingScope.Live)
            { Max = 20000, Lines = 12 },
        new("Notice:Version", NoticeGroup, "Version", "Change it when the notice changes in substance: everyone accepts the new version at their next step in the app (a typo fix needs no new version).", SettingType.Text, SettingScope.Live)
            { Default = "1", Optional = false, Max = 64 },
        new("Compliance:ReviewEveryDays", ComplianceGroup, "Review access every", "An access review (who holds privileged access, and the accounts unused for long) starts by itself this many days after the last one, and the admins are told. 0: only when started by hand (Admin → Compliance).", SettingType.WholeNumber, SettingScope.Live)
            { Default = "90", Unit = "days", Min = 0, Max = 365, Optional = false },
        new("Compliance:UnusedDays", ComplianceGroup, "An account is unused after", "Nobody signed in to it for this many days: it is in the next access review, and disabled when the next setting says so.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "90", Unit = "days", Min = 7, Max = 3650, Optional = false },
        new("Compliance:DisableUnused", ComplianceGroup, "Disable unused accounts", "Every day, accounts unused that long are disabled (audited, reason \"unused\"); the first admin never is. An admin enables one again under Admin → People.", SettingType.Boolean, SettingScope.Live)
            { Default = "false" },

        new("Notifications:AlertsWebhook", NotificationsGroup, "Alerts webhook", "The admins' news (alerts that start firing, people who used up their credit) is posted here too, once each: a Slack, Teams or Mattermost incoming webhook. Its host must be one of the webhook hosts (Scheduled tasks). Empty: none.", SettingType.Secret, SettingScope.Live)
            { Pattern = @"https://\S+", PatternHelp = "an https:// webhook URL" },

        // ---------------------------------------------------------------- chat bots and email in, live --
        new("Bots:Instructions", BotsGroup, "What the bots are told", "Instructions for every chat a bot or an email starts, after the app's own. Each person is still answered as themselves: their model access, tools and credit.", SettingType.Text, SettingScope.Live)
            { Default = Bots.BotOptions.DefaultInstructions, Max = 4000 },
        new("Bots:Slack:SigningSecret", BotsGroup, "Slack: signing secret", "From the Slack app's Basic Information. Every event Slack sends is checked against it. With the bot token, it turns the Slack bot on: its Request URL (Event Subscriptions: app_mention, message.im) is shown below.", SettingType.Secret, SettingScope.Live),
        new("Bots:Slack:BotToken", BotsGroup, "Slack: bot token", "xoxb-… from OAuth & Permissions, with the scopes app_mentions:read, chat:write, im:history, users:read and users:read.email (people are matched by their email).", SettingType.Secret, SettingScope.Live),
        new("Bots:Slack:Channels", BotsGroup, "Slack: channels", "Channel ids it answers in (C0123…), comma separated. Empty: every channel it is invited to. Direct messages always.", SettingType.Text, SettingScope.Live)
            { Max = 2000 },
        new("Bots:Slack:Model", BotsGroup, "Slack: model", "The model its chats use. Empty: the one a new chat of the person would use.", SettingType.Text, SettingScope.Live)
            { Max = 200 },
        new("Bots:Slack:Tools", BotsGroup, "Slack: tools", "Tool ids (Admin → Tools), comma separated, of those the person may use. Empty: the tools on in new chats; none: no tools.", SettingType.Text, SettingScope.Live)
            { Max = 1000 },
        new("Bots:Mattermost:Url", BotsGroup, "Mattermost: server", "https://chat.example.com. With the bot token and a webhook or command token, it turns the Mattermost bot on.", SettingType.Url, SettingScope.Live)
            { Pattern = @"https?://\S+", PatternHelp = "http(s)://…" },
        new("Bots:Mattermost:BotToken", BotsGroup, "Mattermost: bot token", "A bot account's access token (Integrations → Bot accounts). It reads who asked (their email: the server must let it see addresses) and posts the answers; add the bot to the channels.", SettingType.Secret, SettingScope.Live),
        new("Bots:Mattermost:Tokens", BotsGroup, "Mattermost: webhook and command tokens", "The tokens of the outgoing webhooks (a trigger word such as @code-intel) and slash commands (/ask) that call the address below, comma separated.", SettingType.Secret, SettingScope.Live),
        new("Bots:Mattermost:Channels", BotsGroup, "Mattermost: channels", "Channel ids or names it answers in, comma separated. Empty: every channel its webhooks and commands reach.", SettingType.Text, SettingScope.Live)
            { Max = 2000 },
        new("Bots:Mattermost:Model", BotsGroup, "Mattermost: model", "Empty: the one a new chat of the person would use.", SettingType.Text, SettingScope.Live)
            { Max = 200 },
        new("Bots:Mattermost:Tools", BotsGroup, "Mattermost: tools", "Tool ids, comma separated. Empty: the tools on in new chats; none: no tools.", SettingType.Text, SettingScope.Live)
            { Max = 1000 },
        new("Bots:Teams:AppId", BotsGroup, "Teams: app ID", "The Azure Bot's Microsoft App ID. With its password, it turns the Teams bot on: its messaging endpoint is shown below. Every message's token is checked against the Bot Framework's keys.", SettingType.Text, SettingScope.Live)
            { Max = 100 },
        new("Bots:Teams:AppPassword", BotsGroup, "Teams: app password", "The bot's client secret: it signs in to Microsoft to post the answers and read who asked.", SettingType.Secret, SettingScope.Live),
        new("Bots:Teams:TenantId", BotsGroup, "Teams: tenant", "For a single-tenant bot, its tenant's id. Empty: a multi-tenant bot.", SettingType.Text, SettingScope.Live)
            { Max = 100 },
        new("Bots:Teams:Channels", BotsGroup, "Teams: channels", "Channel or team ids it answers in (19:…@thread.tacv2), comma separated. Empty: every channel it is added to. Chats with the bot always.", SettingType.Text, SettingScope.Live)
            { Max = 2000 },
        new("Bots:Teams:Model", BotsGroup, "Teams: model", "Empty: the one a new chat of the person would use.", SettingType.Text, SettingScope.Live)
            { Max = 200 },
        new("Bots:Teams:Tools", BotsGroup, "Teams: tools", "Tool ids, comma separated. Empty: the tools on in new chats; none: no tools.", SettingType.Text, SettingScope.Live)
            { Max = 1000 },
        new("Bots:Email:Secret", BotsGroup, "Email in: shared secret", "Your mail gateway posts each email it receives to the address below with this secret (X-Mail-Secret, a bearer token, or the password of https://any:SECRET@…). The sender gets the answer by email (Settings → Email must be set). Senders without an account get nothing back.", SettingType.Secret, SettingScope.Live),
        new("Bots:Email:Model", BotsGroup, "Email in: model", "Empty: the one a new chat of the person would use.", SettingType.Text, SettingScope.Live)
            { Max = 200 },
        new("Bots:Email:Tools", BotsGroup, "Email in: tools", "Tool ids, comma separated; none: no tools. A sender's address can be forged: add tools only if your mail gateway checks senders (SPF, DKIM, DMARC). Empty: the tools on in new chats.", SettingType.Text, SettingScope.Live)
            { Default = "none", Max = 1000 },

        // ---------------------------------------------------------------- code-intel --



        new("CodeIntelIndex:Schedule", CodeIntel, "Reindex on schedule", $"When {Brand.CodeIntel} brings the index up to date by itself, as cron (minute hour day month weekday), e.g. */15 * * * * for every 15 minutes. Empty: only GitLab pushes and Index now. Easier set under Indexing.", SettingType.Text, SettingScope.Live)
            { Default = "*/15 * * * *" },
        new("CodeIntelIndex:TimeZone", CodeIntel, "Time zone of the index schedule", "An IANA name such as Europe/Berlin.", SettingType.Text, SettingScope.Live)
            { Default = "UTC", Pattern = @"^(UTC|[A-Za-z]+(/[A-Za-z0-9_+\-]+){1,2})$", PatternHelp = "An IANA time zone, e.g. Europe/Berlin, or UTC." },
        new("Tls:CompanyCa", CodeIntel, "GitLab's CA (a company CA)", $"When GitLab's certificate comes from your company's own CA (or is self-signed): that CA's certificate (PEM, -----BEGIN CERTIFICATE-----; a bundle for an issuing CA with its root). Trusted besides the public CAs by everything that reaches GitLab: sign-in with it, people's GitLab tokens and apps linked, Fix it, scheduled tasks, knowledge sources, {Brand.CodeIntel}'s index and reviews, and Code Studio's workspaces (git clone and push). A certificate must still be for GitLab's name and within its dates. At install: GITLAB_CA_FILE.", SettingType.Text, SettingScope.Live)
            { Max = global::Llm.Api.Chat.Tools.ServerTls.MaxCaChars, Lines = 6 },
        new("Chat:GitlabLinkUrl", CodeIntel, "GitLab address for links", $"Where people's browsers open GitLab, for the files and lines in {Brand.CodeIntel}'s answers. Empty: the GitLab address above. Set it when {Brand.CodeIntel} reaches GitLab by an internal name.", SettingType.Url, SettingScope.Live)
            { Pattern = @"https?://\S+", PatternHelp = "http(s)://…" },

        // ------------------------------------------- what was set only by configuration before --
        new("Auth:SessionRecheck", SignIn, "Check sessions against the account every", "How often a signed-in session is checked again: an account disabled, a password reset or a role taken away ends or changes it at the next check.", SettingType.Duration, SettingScope.AppRestart)
            { Default = "00:01:00", Unit = "minutes", Min = 1, Max = 60, Optional = false },
        new("Auth:SignInsPerMinute", SignIn, "Sign-ins a minute from one address", "A flood guard only: guessing is stopped per account by the lockout above. An office behind one address signing in at nine needs room (300 people within a minute were refused at 120).", SettingType.WholeNumber, SettingScope.AppRestart)
            { Default = "600", Min = 10, Max = 100_000, Optional = false },
        new("Oidc:AccessTokenLifetime", SignIn, "Tokens of the apps that sign in here last", "The access and identity tokens Arena gives the other apps that sign in with it (Grafana-like tools, the gateway's own clients).", SettingType.Duration, SettingScope.AppRestart)
            { Default = "01:00:00", Unit = "minutes", Min = 5, Max = 1440, Optional = false },
        new("Oidc:RefreshTokenLifetime", SignIn, "Their refresh tokens last", "How long such an app stays signed in without the person signing in again.", SettingType.Duration, SettingScope.AppRestart)
            { Default = "12:00:00", Unit = "hours", Min = 1, Max = 720, Optional = false },
        new("Auth:AdminGroup", SignIn, "Admin group other apps see", "The group Arena names its admins in, for the apps that sign in with it (the groups claim) and those behind its sign-in (forwardAuth's Remote-Groups): set it to the name such an app maps to its own admins. Everyone else is in users.", SettingType.Text, SettingScope.Live)
            { Default = "admins", Optional = false, Max = 100, Pattern = @"[A-Za-z0-9][A-Za-z0-9._/-]*", PatternHelp = "one name: letters, digits and . _ / -", Impact = "Each app sees it at its next sign-in." },

        new("Ldap:UserNameAttributes", Directory, "Username attributes", "Tried in order, comma separated; the first a person's entry has is their username here (it must equal their GitLab username). OpenLDAP: uid. Active Directory: sAMAccountName, or userPrincipalName.", SettingType.Text, SettingScope.Live)
            { Default = "uid,sAMAccountName", Optional = false, Max = 200 },
        new("Ldap:EmailAttribute", Directory, "Email attribute", "The attribute with the person's email address: mail on both.", SettingType.Text, SettingScope.Live)
            { Default = "mail", Optional = false, Max = 100 },
        new("Ldap:DisplayNameAttributes", Directory, "Display name attributes", "Tried in order, comma separated, for the name shown: displayName, then cn.", SettingType.Text, SettingScope.Live)
            { Default = "displayName,cn", Optional = false, Max = 200 },
        new("Ldap:GroupFilter", Directory, "Which entries are groups", "The search for a person's groups under \"Where groups are\"; {0} is the person's DN. The default finds groupOfNames, groupOfUniqueNames and Active Directory groups.", SettingType.Text, SettingScope.Live)
            { Default = "(&(|(objectClass=groupOfNames)(objectClass=groupOfUniqueNames)(objectClass=group))(|(member={0})(uniqueMember={0})))", Optional = false, Max = 1000, Pattern = @"\(.*\{0\}.*\)", PatternHelp = "a filter in brackets, with {0} for the person's DN" },

        new("Audit:VerifyEvery", AuditGroup, "Check the whole chain every", "How often every entry of the audit log is checked against the one before it and, when the chain holds, anchored outside the database.", SettingType.Duration, SettingScope.Live)
            { Default = "1.00:00:00", Unit = "hours", Min = 1, Max = 168, Optional = false },
        new("Compliance:ReviewDueDays", ComplianceGroup, "An access review is due after", "Days from the start of an access review to its due date.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "14", Unit = "days", Min = 1, Max = 365, Optional = false },
        new("Security:FailedSignIns", SecurityGroup, "Failed sign-ins that raise an alert", "Failed sign-ins in ten minutes, all accounts together, after which the admins hear of it (Admin → Security).", SettingType.WholeNumber, SettingScope.Live)
            { Default = "20", Min = 2, Max = 100_000, Optional = false },
        new("Security:FailedSignInsOneAccount", SecurityGroup, "Failed sign-ins for one account", "Failed sign-ins in ten minutes for one account that raise an alert.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "10", Min = 2, Max = 10_000, Optional = false },
        new("Security:Exports", SecurityGroup, "Exports an hour by one person", "Exports of a person's data or of the compliance evidence by one admin in an hour that raise an alert.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "3", Min = 1, Max = 1000, Optional = false },
        new("Security:Watch", SecurityGroup, "Raise security alerts", "Whether the audit log and the access history are watched every minute for what raises an alert above (and an administrative role given, a backup failed or missing). Off only where the company's SIEM, fed by Audit log, does the watching.", SettingType.Boolean, SettingScope.Live)
            { Default = "true", Dangerous = true, Impact = "Off: Admin → Security and the admins' bell get no new security alert." },

        new("Sandbox:MaxOutputChars", Tools, "Python's output the model reads", "Of each of a run's output and errors: the rest is cut, and the model told so.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "20000", Unit = "characters", Min = 1000, Max = 1_000_000, Optional = false },
        new("Web:MaxPageChars", Tools, "Of a page, one read returns", "What the Web tool reads of one page at a time; the model asks for the next part when it needs it.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "20000", Unit = "characters", Min = 1000, Max = 1_000_000, Optional = false },
        new("Browser:IdleMinutes", Tools, "Close a chat's browser after", "A browser the chat opened (the browser feature) is closed after this long unused.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "15", Unit = "minutes", Min = 1, Max = 1440, Optional = false },
        new("Browser:MostSessions", Tools, "Browsers open at once", "The most chats with a browser open at once; the one unused longest is closed for a new one.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "6", Min = 1, Max = 100, Optional = false },

        new("Lean:TimeoutSeconds", LabGroup, "Longest proof check", "How long one Lean check may take (loading Mathlib alone takes seconds).", SettingType.WholeNumber, SettingScope.Live)
            { Default = "180", Unit = "seconds", Min = 10, Max = 3600, Optional = false },
        new("Blender:TimeoutSeconds", LabGroup, "Longest 3D run", "How long one Blender run may take (a render on the CPU is slow).", SettingType.WholeNumber, SettingScope.Live)
            { Default = "240", Unit = "seconds", Min = 10, Max = 3600, Optional = false },
        new("Laya:TimeoutSeconds", LabGroup, "The decision model waits up to", "Longest one call to the decision model (the laya feature) may take: a forward pass is milliseconds, but calls wait their turn.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "30", Unit = "seconds", Min = 1, Max = 600, Optional = false },
        new("Blender:MaxOutputChars", LabGroup, "Blender's output the model reads", "Of each of a run's output and errors.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "12000", Unit = "characters", Min = 1000, Max = 1_000_000, Optional = false },

        new("Rooms:Calls:IceServers", RoomsGroup, "STUN or TURN servers", "When people's browsers are not on one network: STUN or TURN servers, comma separated (stun:stun.example.com:3478). Empty: the browsers reach one another directly.", SettingType.Text, SettingScope.Live)
            { Max = 2000, Pattern = @"((stuns?|turns?):[^\s,]+)(\s*,\s*(stuns?|turns?):[^\s,]+)*", PatternHelp = "stun:host:port or turn:host:port, comma separated" },
        new("Rooms:Calls:MostInCall", RoomsGroup, "People in one call", "The most people in a room's call at once.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "8", Min = 2, Max = 50, Optional = false, Impact = "Each person sends to every other: past eight, people on slow links drop out." },
        new("Rooms:Calls:CaptionsKept", RoomsGroup, "Captions kept for late joiners", "The latest captions a room keeps in memory for someone who joins the call late.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "200", Min = 1, Max = 10_000, Optional = false },
        new("Meetings:MaxUploadBytes", MeetingsGroup, "Largest recording", "One meeting's recording, uploaded or recorded.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "62914560", Unit = "bytes", Min = 1_048_576, Max = 4_294_967_296, Optional = false },
        new("Meetings:MostPerPerson", MeetingsGroup, "Meetings one person keeps", "Past it, they delete one before adding another.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "500", Min = 1, Max = 100_000, Optional = false },

        new("Workspaces:Idle", StudioGroup, "A workspace sleeps after", "A Code Studio workspace unused this long sleeps: its engine stops, its files stay.", SettingType.Duration, SettingScope.Live)
            { Default = "00:30:00", Unit = "minutes", Min = 5, Max = 10_080, Optional = false },
        new("Workspaces:MostPerPerson", StudioGroup, "Workspaces one person keeps", "On the server.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "8", Min = 1, Max = 100, Optional = false },
        new("Workspaces:MostMachines", StudioGroup, "Machines one person connects", "Their own machines with Code Arena (each folder one).", SettingType.WholeNumber, SettingScope.Live)
            { Default = "8", Min = 1, Max = 100, Optional = false },

        new("Push:PerPerson", NotificationsGroup, "Devices one person gets news on", "Browsers and phones that show the bell's news as notifications.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "20", Min = 1, Max = 200, Optional = false },
        new("Push:Hosts", NotificationsGroup, "Push services", "The browsers' own push services the app posts to, comma separated; *.example.com for a domain and its subdomains. Airgapped: none reaches out, and people see the news in the bell.", SettingType.Text, SettingScope.Live)
            { Default = "fcm.googleapis.com, *.push.services.mozilla.com, *.push.apple.com, *.notify.windows.com", Max = 2000 },

        new("Knowledge:OcrLanguages", Knowledge, "Languages read in scans", "The languages OCR reads pictures and scanned pages in, Tesseract's names joined with + (eng+deu): those the sandbox has.", SettingType.Text, SettingScope.Live)
            { Default = "eng+fas+ara", Optional = false, Max = 200, Pattern = @"[a-z_]{3,}(\+[a-z_]{3,})*", PatternHelp = "Tesseract names joined with +, e.g. eng+deu" },
        new("Knowledge:IndexWait", Knowledge, "An answer waits for its files' passages", "Longest an answer waits for attached files to be read into passages; those not ready go in whole.", SettingType.Duration, SettingScope.Live)
            { Default = "00:00:15", Unit = "seconds", Min = 1, Max = 600, Optional = false },
        new("Retention:Interval", Retention, "Look for what is past its time every", "How often the clean-up looks for chats, files and logs past the times above.", SettingType.Duration, SettingScope.Live)
            { Default = "01:00:00", Unit = "minutes", Min = 5, Max = 1440, Optional = false },

        new("Gateway:ConnectWaitSeconds", Model, "Wait for the gateway, at most", "How long a chat's request waits for a gateway it cannot reach (restarting after an upgrade takes it 10 to 20 seconds), trying again meanwhile.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "30", Unit = "seconds", Min = 0, Max = 600, Optional = false },
        new("Gateway:KeyWaitSeconds", Model, "Wait for the chat's key, at most", "How long a new installation's first chat waits for the gateway to make the chat's key (its first start sets up its database first).", SettingType.WholeNumber, SettingScope.Live)
            { Default = "90", Unit = "seconds", Min = 0, Max = 600, Optional = false },
        new("Engine:Threads", Model, "CPU threads for the models", "Of llama.cpp's models. Empty: llama.cpp's own choice.", SettingType.WholeNumber, SettingScope.AppRestart)
            { Min = 1, Max = 1024, Impact = "Applies as the models are next loaded." },
        new("Engine:RamReserveBytes", Model, "RAM kept for all but the engine", "What the model planner leaves of the host's memory to the rest of the stack.", SettingType.WholeNumber, SettingScope.AppRestart)
            { Default = "8589934592", Unit = "bytes", Min = 0, Max = 1_099_511_627_776, Optional = false },
        new("Engine:ImageReserveBytes", Model, "GPU memory for the picture server", "What the picture server may take beside the engine on its GPU.", SettingType.WholeNumber, SettingScope.AppRestart)
            { Default = "2147483648", Unit = "bytes", Min = 0, Max = 274_877_906_944, Optional = false },
        new("Engine:EmbedReserveBytes", Model, "GPU memory for the embedder", "What the embedder keeps on its GPU when it runs on one.", SettingType.WholeNumber, SettingScope.AppRestart)
            { Default = "1073741824", Unit = "bytes", Min = 0, Max = 274_877_906_944, Optional = false },
        new("Engine:MediaPausesKept", Model, "Pictures and videos may pause models kept loaded", "A picture or video job short of GPU memory may put a vLLM model kept loaded to sleep (its weights to RAM, back in a few seconds after the job). Off: a model kept loaded is never touched, so no chat waits for it to come back; the job makes room with the models not kept loaded, or runs with what there is.", SettingType.Boolean, SettingScope.Live)
            { Default = "false" },
        new("Engine:VllmStartWait", Model, "Wait for a vLLM model to load", "How long a request, or a model's move, waits for a vLLM or SGLang model it started (a large one loads in minutes).", SettingType.Duration, SettingScope.AppRestart)
            { Default = "00:20:00", Unit = "minutes", Min = 1, Max = 240, Optional = false },
        new("HuggingFace:BaseUrl", Model, "Hugging Face address", "Where models are searched for and downloaded (Admin → Models): Hugging Face itself, or a mirror of it inside the company (an Artifactory or Nexus Hugging Face remote, a hf-mirror). The Hugging Face token, when set, is sent to it.", SettingType.Url, SettingScope.Live)
            { Default = "https://huggingface.co", Optional = false, Pattern = @"https?://\S+", PatternHelp = "https://..." },
        new("Credit:Refresh", PricesGroup, "Read spend again after", "How long the month's spend, once read, serves the credit checks (each read scans the month's requests).", SettingType.Duration, SettingScope.Live)
            { Default = "00:00:30", Unit = "seconds", Min = 1, Max = 3600, Optional = false },

        new("Dashboards:MaxRows", DashboardsGroup, "Rows a table panel reads", "The most rows a dashboard's table or a SQL panel reads.", SettingType.WholeNumber, SettingScope.AppRestart)
            { Default = "5000", Min = 100, Max = 1_000_000, Optional = false },
        new("Dashboards:MaxLogLines", DashboardsGroup, "Log lines a panel shows", "The most log lines a logs panel shows.", SettingType.WholeNumber, SettingScope.AppRestart)
            { Default = "1000", Min = 100, Max = 100_000, Optional = false },
        new("Dashboards:StatementTimeout", DashboardsGroup, "A panel's query may take", "Longest one SQL query of a panel may run.", SettingType.Duration, SettingScope.AppRestart)
            { Default = "00:00:15", Unit = "seconds", Min = 1, Max = 600, Optional = false },
        new("Dashboards:ScrapeInterval", DashboardsGroup, "Prometheus scrapes every", "The scrape interval the dashboards assume: their finest step, and part of $__rate_interval. Prometheus scrapes as config/prometheus/prometheus.yml says (scrape_interval, 15s): change both together.", SettingType.Duration, SettingScope.AppRestart)
            { Default = "00:00:15", Unit = "seconds", Min = 1, Max = 600, Optional = false, Impact = "Prometheus's own interval is changed in its file, then arenactl restart prometheus." },

        new("Replicas:Enabled", ReplicasGroup, "Replicas work together", "Several app replicas on one database (scale.yml, the Helm chart) choose one to lead the once-only work and tell each other of saved settings and answers. Off only for a single replica behind a database pooler that keeps no sessions (PgBouncer in transaction mode), which breaks the lead and the signals.", SettingType.Boolean, SettingScope.AppRestart)
            { Default = "true", Dangerous = true, Impact = "Off with two or more replicas, each leads: scheduled tasks run twice and models load twice." },
        new("Replicas:Renew", ReplicasGroup, "Check the lead every", "How often a replica tries for the lead, or checks it still has it, and counts the others: a replica that stops is replaced within this.", SettingType.Duration, SettingScope.Live)
            { Default = "00:00:05", Unit = "seconds", Min = 1, Max = 300, Optional = false },
        new("Replicas:AskTimeout", ReplicasGroup, "Wait for another replica", "How long a stop, a yes to a tool call or \"answer now\" sent to one replica waits for the replica writing that answer.", SettingType.Duration, SettingScope.Live)
            { Default = "00:00:03", Unit = "seconds", Min = 1, Max = 60, Optional = false },





    ];

    /// <summary>
    /// Settings that decide who is an admin (the directory's and the identity provider's admin group, and the group other apps
    /// read as Arena's admins): changing one makes Owners, so only an Owner changes them, whatever else they hold.
    /// </summary>
    public static readonly IReadOnlySet<string> OwnerOnly = new HashSet<string>(StringComparer.Ordinal) { "Ldap:AdminGroup", "CompanySignIn:AdminGroup", "Auth:AdminGroup" };

    /// <summary>
    /// The permission that changes a setting, by its group on the page: each group's part of settings.manage, and the
    /// directory's and company sign-in's under signin.manage.
    /// </summary>
    public static string PermissionOf(SettingDefinition d) => d.Group switch
    {
        Branding => Permissions.SettingsBranding,
        SignIn => Permissions.SignInManage,
        Directory => Permissions.SignInDirectory,
        CompanySignIn or Oidc or Saml => Permissions.SignInSso,
        Chat => Permissions.SettingsChat,
        Model or PricesGroup or Speech => Permissions.SettingsModels,
        CodeIntel => Permissions.SettingsCodeIntel,
        Tools or PluginsGroup or ArenaMcp => Permissions.SettingsTools,
        Schedules or Mail or NotificationsGroup or BotsGroup => Permissions.SettingsIntegrations,
        Safeguards or SecurityGroup => Permissions.SettingsSafeguards,
        RoomsGroup or MeetingsGroup => Permissions.SettingsChat,
        StudioGroup or LabGroup => Permissions.SettingsTools,
        Knowledge or Retention or StorageGroup => Permissions.SettingsData,
        Api => Permissions.SettingsGateway,
        _ => Permissions.SettingsManage,
    };

    public static readonly IReadOnlyDictionary<string, SettingDefinition> ByKey =
        All.ToDictionary(d => d.Key, StringComparer.Ordinal);

}
