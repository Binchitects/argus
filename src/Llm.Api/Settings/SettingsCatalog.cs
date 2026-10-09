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
    private const string Speech = "Speech";
    private const string Argus = "Argus (GitLab)";
    private const string Tools = "Python and web";
    private const string PluginsGroup = "Plugins";
    private const string ArenaMcp = "Arena MCP";
    private const string Schedules = "Scheduled tasks";
    private const string Mail = "Email";
    private const string NotificationsGroup = "Notifications";
    private const string Safeguards = "Safeguards";
    private const string Knowledge = "Company knowledge";
    private const string Retention = "Data retention";
    private const string Api = "API keys";
    private const string BotsGroup = "Chat bots";
    private const string Oidc = "CompanySignIn:Protocol=oidc";
    private const string Saml = "CompanySignIn:Protocol=saml";



    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        // ---------------------------------------------------------------- app, live --
        new("Branding:ProductName", Branding, "Product name", "Shown in the sidebar, on the sign-in page and in the browser tab.", SettingType.Text, SettingScope.Live)
            { Default = "Argus Arena", Optional = false, Max = 60 },
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

        new("Chat:MaxToolRounds", Chat, "Tool calls per answer", "How many rounds of tool use (Argus searches) one answer may take before it must answer.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "8", Min = 1, Max = 32, Optional = false },
        new("Chat:ToolTextChars", Chat, "Tool definitions sent whole up to", "Past this many characters of tool definitions, a chat gets in full only the tools it has loaded, and a line for each of the others that the model loads when a question needs it (it stays loaded in that chat). Saves thousands of tokens before the first answer of every chat. 0: always every tool whole.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "6000", Unit = "characters", Min = 0, Max = 500000, Optional = false },
        new("Chat:ToolResultChars", Chat, "Tool result the model reads whole up to", "A longer result (an MCP server's, a file's) goes to the model as its start, and the whole of it becomes a file in the chat that the model reads on with read_file: every later step reads the results again, so one huge result would fill each of them. 0: always whole.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "24000", Unit = "characters", Min = 0, Max = 1000000, Optional = false },
        new("Chat:AutoCompactPercent", Chat, "Compact a chat at (% of context)", "When a chat fills this share of the model's context, its older messages become a summary the model reads instead (people still see them), and the chat goes on. 0: never; the oldest messages are left out instead. Anyone can also compact a chat with /compact.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "80", Unit = "%", Min = 0, Max = 95, Optional = false },
        new("Chat:MaxUploadBytes", Chat, "Largest attachment", "Per file.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "20971520", Unit = "bytes", Min = 1048576, Max = 104857600, Optional = false },
        new("Chat:MaxAttachmentChars", Chat, "Text kept per attachment", "Longer files are cut to this many characters and marked \"cut to fit\". The model reads what does not fit in the question in parts.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "1000000", Min = 1000, Max = 5000000, Optional = false },
        new("Chat:InlineAttachmentChars", Chat, "Text of an attachment in the question", "What of each attachment goes into the question itself. The model reads the rest in parts (the Reading files tool), so a long file does not fill the context.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "30000", Min = 2000, Max = 1000000, Optional = false },
        new("Memory:Enabled", Chat, "Memory", "Answers read what each person asked the chat to remember, and the model offers to remember what would help later (kept only when the person accepts). Each person sees, edits and deletes their own in Your account → Memory, and can turn it off there; nobody else sees them, admins included. Off: no answer reads or offers memories.", SettingType.Boolean, SettingScope.Live)
            { Default = "true", Optional = false },
        new("Web:AllowedSites", Tools, "Sites the chat may open", "Host names, comma separated: docs.python.org, *.microsoft.com (a domain and its subdomains), or * for any public site. Empty: the Web tool stays off. Addresses inside your network are never opened.", SettingType.Text, SettingScope.Live)
            { Default = "" },
        new("Plugins:CatalogUrl", PluginsGroup, "Plugin catalog", "An index.json that lists plugins to install (each zip's address and SHA-256), besides those that come with the app. Empty: only those.", SettingType.Url, SettingScope.Live),
        new("Plugins:CatalogKey", PluginsGroup, "Catalog's public key", "The publisher's ECDSA P-256 public key (PEM). When set, the catalog's index.json.sig must verify against it, or nothing is installed from it.", SettingType.Text, SettingScope.Live)
            { Max = 1000 },
        new("Mcp:Enabled", ArenaMcp, "Arena MCP", "Outside agents (Code Arena, Claude Code, Qwen Code, Continue) get each person's chat tools at https://DOMAIN/mcp, signed in with that person's API key: the tools they may use in the chat, run as them, each call audited (mcp.call).", SettingType.Boolean, SettingScope.Live)
            { Default = "true" },
        new("Mcp:ListWaitSeconds", ArenaMcp, "Wait for a slow tool server", "Connecting an agent starts Argus and the MCP servers here together and waits this long at most: one slower is listed as not available now, with the reason, and asked again on the next list, so a slow or dead server never holds an agent.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "10", Min = 1, Max = 120, Unit = "seconds", Optional = false },
        new("Web:SearchUrl", Tools, "Search engine", "A SearXNG instance for the Web tool's search. Empty: the websearch profile's own when it is on; otherwise no search, only opening pages.", SettingType.Url, SettingScope.Live),
        new("Sandbox:TimeoutSeconds", Tools, "Longest Python run", "A run still going after this long is stopped, and the model told so.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "60", Min = 5, Max = 300, Unit = "seconds", Optional = false },
        new("Chat:AnswersPerPerson", Chat, "Answers at once, per person", "Answers one person may have running at once, across their chats. More wait their turn, so nobody takes the model from the others.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "1", Min = 1, Max = 16, Optional = false },
        new("Chat:AnswersAtOnce", Chat, "Answers at once, everyone", "Each model runs as many answers at once as it serves (its Answers at once in Admin → Models, and its copies on other GPU servers); the rest wait in that model's line, served in turn (whoever has had least goes first), so people on one model never wait for another's. Set this to also limit all models together. 0: no limit beyond each model's own.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "0", Min = 0, Max = 256, Optional = false },
        new("Chat:QueueTimeout", Chat, "Longest wait in line", "An answer that has waited this long for its turn gives up and says the model is busy.", SettingType.Duration, SettingScope.Live)
            { Default = "00:10:00", Unit = "minutes", Min = 1, Max = 120, Optional = false },
        new("Chat:ApiRequestsPerKey", Chat, "API requests at once, per key", "Requests one API key (Qwen Code, an IDE, a script) may have at the gateway at once; more are refused (HTTP 429) until one ends. 0: no limit.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "2", Min = 0, Max = 64, Optional = false },
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
        new("Chat:ToolCallTimeout", Chat, "Longest tool call", "A call to Argus or an MCP server still running after this long is stopped, and the model told so. An MCP server can have its own limit (Admin → Tools). The chat shows how long a call has run, and the progress the server reports.", SettingType.Duration, SettingScope.Live)
            { Default = "01:00:00", Unit = "minutes", Min = 1, Max = 1440, Optional = false },
        new("Quality:PublicLeaderboard", Chat, "Arena leaderboard for everyone", "Everyone sees the leaderboard of the company's models on its own questions, made from the votes of Compare in the chat. Off: admins only (Admin → Quality).", SettingType.Boolean, SettingScope.Live)
            { Default = "true", Optional = false },
        new("Chat:DefaultModel", Model, "Model new chats use", "A chat model's name, or auto for Auto (offered while a model for small steps is set). Empty: the first kept loaded (Admin → Models); with none kept, the first loaded, which stays the one while it is loaded. With 2 or more models at once, this model never makes room for another, and loads again should the engine unload it.", SettingType.Text, SettingScope.Live)
            { Max = 100 },
        new("Chat:SmallModel", Model, "Model for sub-agents and small steps", "A small, fast chat model for the many short steps: sub-agents, chat titles, compaction summaries, the safeguards' check, and Auto, which it brings to the chat's model menu (it answers easy questions itself and hands the rest on). It never thinks for them. With a place left beside it for the other models (3 or more loaded at once, with the big one), it keeps a place of its own and never makes room; at 2 at once it shares the last place with the models loaded on request, and while another sits there each step uses the answer's own model. Keep it loaded (Admin → Models) to keep it loaded whatever the others. Empty: each step uses the answer's own model.", SettingType.Text, SettingScope.Live)
            { Max = 100 },
        new("Chat:ThinkingPresets", Model, "Thinking levels offered", "level:Label pairs, comma-separated. The chat offers them per conversation.", SettingType.Text, SettingScope.Live)
            { Default = "xhigh:Deep think,medium:Balanced,low:Quick,off:No thinking", Optional = false, Max = 400, Pattern = @"[a-z]+:[^,]+(,[a-z]+:[^,]+)*", PatternHelp = "level:Label pairs, e.g. medium:Balanced,off:No thinking" },
        new("Chat:DefaultThinking", Model, "Default thinking", "How hard the model thinks when a chat does not choose: one of the levels above.", SettingType.Text, SettingScope.Live)
            { Default = "medium", Optional = false, Max = 20 },
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
        new("Safeguards:BlockedPatterns", Safeguards, "Blocked words and patterns", "A message with one is refused. Separate them with ; — a phrase matches as whole words, ignoring case; re: starts a regular expression (e.g. re:\\bproject falcon\\b).", SettingType.Text, SettingScope.Live)
            { Max = 20_000 },
        new("Safeguards:Moderation", Safeguards, "The model checks each message", "check: before answering, the chat's model reads the message as a classifier and refuses one asking for harm in the categories below (learning, safety, news and fiction stay allowed). It adds a short call to every message. off: no check.", SettingType.Choice, SettingScope.Live)
            { Default = "off", Options = ["off", "check"], Optional = false },
        new("Safeguards:ModerationCategories", Safeguards, "Categories it refuses", "What the check looks for.", SettingType.Choices, SettingScope.Live)
            { Default = "violence,self-harm,sexual-minors,weapons,malware,hate", Options = ["violence", "self-harm", "sexual-minors", "weapons", "malware", "hate", "fraud"] },
        new("Safeguards:RedactPii", Safeguards, "Mask personal data", "mask: e-mail addresses, phone and card numbers and IBANs in messages reach the model masked; the chat keeps them as written. off: as written.", SettingType.Choice, SettingScope.Live)
            { Default = "off", Options = ["off", "mask"], Optional = false },
        new("Safeguards:UntrustedToolResults", Safeguards, "Mark the web's content as data", "What web search and pages bring, and the passages company knowledge finds, is marked so the model never follows instructions hidden in it (prompt injection).", SettingType.Boolean, SettingScope.Live)
            { Default = "true", Optional = false },
        new("Safeguards:StrikesToSuspend", Safeguards, "Refusals that suspend an account", "Refused messages in a day after which the account is disabled (an admin turns it back on under People). 0: never.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "0", Min = 0, Max = 1000 },
        new("Safeguards:NotifyAdmins", Safeguards, "Tell the admins of each refusal", "In their bell; the audit log keeps every one (safeguard.refused).", SettingType.Boolean, SettingScope.Live)
            { Default = "true", Optional = false },
        new("Safeguards:RefusalMessage", Safeguards, "What a refused message says", "Shown to the person whose message was refused by a blocked word or the check.", SettingType.Text, SettingScope.Live)
            { Default = "This message was not sent: it goes against your organisation's rules for the assistant. Ask an admin if you think it should be allowed.", Max = 500 },
        new("Safeguards:SecretScanning", Safeguards, "Secrets in messages and files", "Private keys, cloud and service tokens (AWS, GitHub, GitLab, Slack, sk- keys) and passwords (password=..., connection strings, user:password@ in an address). refuse: the message or file is not taken, and the person is told what kind was found. mask: it goes with each secret replaced by a marker. Each one found is audited by its kind, never the secret. A group can set its own (Admin → Groups).", SettingType.Choice, SettingScope.Live)
            { Default = "refuse", Options = ["refuse", "mask", "off"], Optional = false },
        new("Safeguards:CheckApi", Safeguards, "Check API requests too", "API keys' requests pass the same checks: secrets, blocked words, the model's check (on the last question) and personal data. The gateway asks the app before each one (its guardrail in config/litellm.yaml). Credit applies either way.", SettingType.Boolean, SettingScope.Live)
            { Default = "true", Optional = false },

        // ---------------------------------------------------------------- retention, live --
        new("Retention:Days", Retention, "Keep chats for", "A chat and its files are deleted this many days after its last message, and so are files nobody uses that are as old. A group can set its own (Admin → Groups); the shortest of a person's groups applies. People on legal hold keep everything. Each deletion is audited, with counts. Empty: forever.", SettingType.WholeNumber, SettingScope.Live)
            { Unit = "days", Min = 1, Max = 36500 },
        new("Schedules:Enabled", Schedules, "Scheduled tasks", "People may set questions to be asked on a schedule (a daily digest, a weekly report), answered as them, with their model, tools and credit.", SettingType.Boolean, SettingScope.Live)
            { Default = "true" },
        new("Schedules:PerPerson", Schedules, "Tasks per person", "How many scheduled tasks one person may have.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "10", Min = 1, Max = 100, Optional = false },
        new("Schedules:MinInterval", Schedules, "Most often", "A task may not run more often than this, so nobody fills the model's day.", SettingType.Duration, SettingScope.Live)
            { Default = "00:15:00", Unit = "minutes", Min = 1, Max = 1440, Optional = false },
        new("GitLab:Url", Schedules, "GitLab address for tasks", "Where tasks run by GitLab's events read details (a merge request's changes, a failed job's log) and comment. Empty: Argus's (GITLAB_URL).", SettingType.Url, SettingScope.Live),
        new("GitLab:BotToken", Schedules, "GitLab bot token", "A bot account's token (scope api, Reporter in the projects): reads what an event leaves out and writes the comments of tasks that reply in GitLab. Never Argus's read-only token; each comment is audited.", SettingType.Secret, SettingScope.Live),
        new("Schedules:WebhookHosts", Schedules, "Webhook hosts", "Hosts a task's answer may be posted to, comma separated; *.example.com for a domain and its subdomains. Add your Mattermost or chat server here. Empty: no webhooks.", SettingType.Text, SettingScope.Live)
            { Default = "hooks.slack.com, *.webhook.office.com, *.logic.azure.com, discord.com" },
        new("Knowledge:SyncEvery", Knowledge, "Read sources again every", "How often each knowledge source (Admin → Knowledge) is read again. Only what changed is embedded again; a GitLab project with no new activity is not read at all. Members of GitLab projects are read every 15 minutes whatever this is.", SettingType.Duration, SettingScope.Live)
            { Default = "01:00:00", Unit = "minutes", Min = 15, Max = 10080, Optional = false },
        new("Knowledge:PassageChars", Knowledge, "Passages of long files per question", "With the embedder, a chat's long files and a project's files that do not fit go to the model as their passages that match each question, up to this many characters, instead of their first part.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "12000", Unit = "characters", Min = 2000, Max = 100000, Optional = false },
        new("Mail:Host", Mail, "SMTP server", "The mail server the app sends email through (scheduled tasks' answers). Empty: no email.", SettingType.Text, SettingScope.Live),
        new("Mail:Port", Mail, "SMTP port", "587 for STARTTLS, 25 for plain SMTP inside the network.", SettingType.WholeNumber, SettingScope.Live)
            { Default = "587", Min = 1, Max = 65535, Optional = false },
        new("Mail:StartTls", Mail, "Use STARTTLS", "Upgrade the connection to TLS before signing in. Off only for a relay inside the network.", SettingType.Boolean, SettingScope.Live)
            { Default = "true" },
        new("Mail:User", Mail, "SMTP user", "For servers that need a sign-in; empty for a relay that does not.", SettingType.Text, SettingScope.Live),
        new("Mail:Password", Mail, "SMTP password", "From your mail team.", SettingType.Secret, SettingScope.Live),
        new("Mail:From", Mail, "Sender", "The address email comes from, e.g. Argus Arena <llm@example.com>.", SettingType.Text, SettingScope.Live),
        new("Notifications:Email", NotificationsGroup, "Email the credit and alert news", "Besides the bell: a person at 80% of their credit and with it used up, and the admins of alerts that start firing and of credit used up, by email (once each). Needs email set up (Email).", SettingType.Boolean, SettingScope.Live)
            { Default = "true", Optional = false },
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
        new("Bots:Mattermost:Tokens", BotsGroup, "Mattermost: webhook and command tokens", "The tokens of the outgoing webhooks (a trigger word such as @argus) and slash commands (/ask) that call the address below, comma separated.", SettingType.Secret, SettingScope.Live),
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
