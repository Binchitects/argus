using Novell.Directory.Ldap;

namespace Llm.Api.Ldap;

/// <summary>One step of a directory check: what was done and how it went ("ok", "warn" or "fail").</summary>
public sealed record LdapStep(string State, string Text);

/// <summary>A directory check: whether it works, the one thing to say first, and each step.</summary>
public sealed record LdapCheckResult(bool Ok, string Message, IReadOnlyList<LdapStep> Steps);

/// <summary>Where the service account's password for a check comes from.</summary>
public enum PasswordFrom
{
    /// <summary>Typed in the form, not saved yet.</summary>
    Typed,
    /// <summary>The saved one (the field was left blank).</summary>
    Saved,
    /// <summary>Saved, but it no longer decrypts: APP_KEY changed since.</summary>
    SavedUnreadable,
    /// <summary>Saved, but the form names another server or service account: the saved one is never sent there.</summary>
    SavedWithheld,
    /// <summary>None typed and none saved.</summary>
    None,
}

/// <summary>
/// The admin's checks of the directory settings, saved or not: Test (the server, its certificate,
/// the service account, where people are, the groups) and a person's sign-in tried the way the app
/// signs people in. Each says exactly what is wrong, in plain words, with the server's own message.
/// </summary>
public static class LdapCheck
{
    private const string Ok = "ok";
    private const string Warn = "warn";
    private const string Fail = "fail";

    private static readonly string[] MemberAttributes = ["member", "uniqueMember"];
    private static readonly string[] GroupAttributes = ["cn", "objectClass", "memberUid", .. MemberAttributes];

    /// <summary>Active Directory says so in its root entry (LDAP_CAP_ACTIVE_DIRECTORY_OID).</summary>
    private const string ActiveDirectoryCapability = "1.2.840.113556.1.4.800";

    /// <summary>StartTLS, as the root entry lists it among its extensions (RFC 4511).</summary>
    private const string StartTlsExtension = "1.3.6.1.4.1.1466.20037";

    /// <summary>
    /// Connects and signs in with these settings, then looks for people and the groups named. Withheld says
    /// why the saved password is not used (with <see cref="PasswordFrom.SavedWithheld"/>).
    /// </summary>
    public static async Task<LdapCheckResult> TestAsync(LdapOptions o, PasswordFrom password, IReadOnlyList<string>? notes = null, string? withheld = null, CancellationToken ct = default)
    {
        var run = new Run(o, password, notes, withheld);
        using var conn = await run.OpenAsync(ct);
        if (conn is null)
        {
            return run.Result();
        }
        try
        {
            if (await run.PeopleAsync(conn, ct))
            {
                await run.GroupsAsync(conn, ct);
            }
        }
        catch (Exception ex) when (LdapErrors.IsDirectoryFailure(ex))
        {
            run.Failed("The connection failed: " + LdapErrors.Describe(o, ex, null));
        }
        return run.Result();
    }

    /// <summary>
    /// A person's sign-in, made as the app makes it (the same search, the same bind as them, the same
    /// groups), with the settings given: who they would be here, or exactly why not. Nothing is kept.
    /// Refused is whether the directory checked their password and said no: a wrong guess, to count.
    /// </summary>
    public static async Task<(LdapCheckResult Result, LdapPerson? Person, bool Refused)> TryAsync(LdapOptions o, PasswordFrom password, string login, string personPassword, IReadOnlyList<string>? notes = null, string? withheld = null, CancellationToken ct = default)
    {
        var run = new Run(o, password, notes, withheld);
        if (string.IsNullOrWhiteSpace(login))
        {
            return (run.Failed("Type the person's username (or email) to try."), null, false);
        }
        if (string.IsNullOrEmpty(personPassword))
        {
            return (run.Failed("Type their password: an empty one is never tried, as many servers take it for an anonymous sign-in and say yes."), null, false);
        }
        using var conn = await run.OpenAsync(ct);
        if (conn is null)
        {
            return (run.Result(), null, false);
        }
        try
        {
            var person = await run.PersonAsync(conn, login, personPassword, ct);
            return (run.Result(), person, run.PasswordRefused);
        }
        catch (Exception ex) when (LdapErrors.IsDirectoryFailure(ex))
        {
            return (run.Failed("The connection failed: " + LdapErrors.Describe(o, ex, null)), null, run.PasswordRefused);
        }
    }

    /// <summary>One check under way: its settings, what the server says of itself, and the steps so far.</summary>
    private sealed class Run(LdapOptions o, PasswordFrom from, IReadOnlyList<string>? notes, string? withheld)
    {
        private readonly List<LdapStep> _steps = [.. (notes ?? []).Select(n => new LdapStep(Warn, n))];
        private readonly List<string> _contexts = [];
        private string? _summary;

        /// <summary>Whether the server says it offers StartTLS; null when its root entry could not be read.</summary>
        private bool? _offersStartTls;

        /// <summary>Whether the root entry says it is OpenLDAP, which sends memberOf only when asked for it.</summary>
        private bool _openLdap;

        /// <summary>The directory checked the person's password, and refused it.</summary>
        public bool PasswordRefused { get; private set; }

        private bool Anonymous => string.IsNullOrWhiteSpace(o.BindDn);
        private string Who => Anonymous ? "an anonymous connection" : "the service account";
        private string Password => from == PasswordFrom.Typed ? "the password typed above" : "the saved password";

        public LdapCheckResult Result()
        {
            var failed = _steps.FirstOrDefault(s => s.State == Fail);
            if (failed is not null)
            {
                return new(false, failed.Text, _steps);
            }
            var warnings = _steps.Count(s => s.State == Warn);
            var message = _summary ?? "It works.";
            return new(true, warnings == 0 ? message : $"{message} Read the {(warnings == 1 ? "warning" : $"{warnings} warnings")} below.", _steps);
        }

        public LdapCheckResult Failed(string text)
        {
            _steps.Add(new(Fail, text));
            return Result();
        }

        private void Add(string state, string text) => _steps.Add(new(state, text));

        /// <summary>Checks the settings, connects, secures and signs in; null (with the failed step) when one of them fails.</summary>
        public async Task<LdapConnection?> OpenAsync(CancellationToken ct)
        {
            if (!o.Enabled)
            {
                Failed("No directory server is set: type its address in \"Directory server\", like ldap://ldap.example.com:389 or ldaps://dc1.example.com:636.");
                return null;
            }
            if (!Uri.TryCreate(o.Url!.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "ldap" && uri.Scheme != "ldaps") || uri.IdnHost.Length == 0)
            {
                Failed($"\"{o.Url}\" is not an ldap:// or ldaps:// address. Write it like ldap://ldap.example.com:389 or ldaps://dc1.example.com:636.");
                return null;
            }
            if (LdapDirectory.ServiceAccountProblem(o) is not null)
            {
                Failed(from switch
                {
                    PasswordFrom.SavedUnreadable => "The saved service account password no longer reads: APP_KEY changed since it was saved. Type it again in \"Service account password\".",
                    PasswordFrom.SavedWithheld => $"Type the service account's password: the saved one is sent only to the saved server, as the saved service account, over a connection at least as safe as the saved one, and the form {withheld ?? "changes the server or the account"}.",
                    _ => "The service account has no password: none is typed above and none is saved. Type it in \"Service account password\". (Without one, the server would take it for an anonymous connection.)",
                });
                return null;
            }
            var ldaps = LdapDirectory.IsLdaps(uri);
            var at = $"{uri.IdnHost}:{LdapDirectory.PortOf(uri)}";
            var seen = new LdapDirectory.Seen();
            LdapConnection conn;
            try
            {
                conn = await LdapDirectory.OpenAsync(o, seen, ct);
            }
            catch (Exception ex) when (LdapErrors.IsDirectoryFailure(ex))
            {
                Failed("Could not connect: " + (LdapErrors.Unreached(o, ex, seen, startTls: false) ?? LdapErrors.Describe(o, ex, seen)));
                return null;
            }
            try
            {
                Add(Ok, ldaps ? $"Reached {at}, encrypted (ldaps://)." : $"Reached {at}.");
                await ReadRootAsync(conn, ct);
                if (!ldaps && o.StartTls)
                {
                    if (_offersStartTls == false)
                    {
                        Failed("This server does not offer StartTLS (its root entry does not list it): turn \"Use StartTLS\" off and use ldaps://, or ask the directory team to turn StartTLS on.");
                        conn.Dispose();
                        return null;
                    }
                    try
                    {
                        await conn.StartTlsAsync(ct);
                        Add(Ok, "Encrypted with StartTLS.");
                    }
                    catch (LdapException ex) when (!LdapErrors.IsConnectionFailure(ex))
                    {
                        Failed($"The server does not do StartTLS here ({LdapErrors.Answer(ex)}): turn \"Use StartTLS\" off and use ldaps://, or ask the directory team to turn it on.");
                        conn.Dispose();
                        return null;
                    }
                    catch (Exception ex) when (LdapErrors.IsDirectoryFailure(ex))
                    {
                        Failed("StartTLS failed: " + (LdapErrors.Unreached(o, ex, seen, startTls: true) ?? LdapErrors.Describe(o, ex, seen)));
                        conn.Dispose();
                        return null;
                    }
                }
                else if (ldaps && o.StartTls)
                {
                    Add(Warn, "\"Use StartTLS\" does nothing with ldaps://: the connection is already encrypted. Turn it off.");
                }
                if (!ldaps && !o.StartTls)
                {
                    Add(Warn, "Not encrypted: the service account's password, and people's passwords when they sign in, cross the network as they are. "
                        + (_offersStartTls == true ? "This server offers StartTLS: turn on \"Use StartTLS\"." : "Use ldaps:// or turn on \"Use StartTLS\"."));
                }
                else if (o.IgnoreCertificateErrors)
                {
                    Add(Warn, "The server's certificate is not checked (\"Accept any certificate\" is on): anyone on the network path could pose as the directory. Use it for a test server only.");
                }
                else
                {
                    Add(Ok, string.IsNullOrWhiteSpace(o.CaCertificate) ? "Its certificate is trusted." : "Its certificate leads to the CA given in \"Directory's CA\".");
                }
                if (!await BindAsync(conn, ct))
                {
                    conn.Dispose();
                    return null;
                }
                return conn;
            }
            catch (Exception ex) when (LdapErrors.IsDirectoryFailure(ex))
            {
                Failed("The connection failed: " + LdapErrors.Describe(o, ex, seen));
                conn.Dispose();
                return null;
            }
        }

        /// <summary>
        /// The server's root entry, read before signing in as servers allow: what it holds, whether it is
        /// Active Directory or OpenLDAP, and whether it offers StartTLS.
        /// </summary>
        private async Task ReadRootAsync(LdapConnection conn, CancellationToken ct)
        {
            try
            {
                var root = await conn.ReadAsync("", ["namingContexts", "defaultNamingContext", "supportedCapabilities", "supportedExtension", "vendorName", "objectClass"], ct);
                var attrs = root.GetAttributeSet();
                var activeDirectory = attrs.TryGetValue("supportedCapabilities", out var caps) && caps.StringValueArray.Contains(ActiveDirectoryCapability);
                var openLdap = _openLdap = attrs.TryGetValue("objectClass", out var classes) && classes.StringValueArray.Contains("OpenLDAProotDSE", StringComparer.OrdinalIgnoreCase);
                if (attrs.TryGetValue("supportedExtension", out var extensions))
                {
                    _offersStartTls = extensions.StringValueArray.Contains(StartTlsExtension);
                }
                _contexts.AddRange(LdapDirectory.Contexts(attrs));
                var vendor = attrs.TryGetValue("vendorName", out var v) ? v.StringValue : null;
                var kind = activeDirectory ? "Active Directory" : openLdap ? "OpenLDAP" : vendor ?? "an LDAP directory";
                Add(Ok, _contexts.Count > 0 ? $"It is {kind}, holding {Holds}." : $"It is {kind}.");
            }
            catch (LdapException)
            {
                // Some servers show nothing before a sign-in: the checks go on without it.
            }
        }

        private string Holds => string.Join(" and ", _contexts.Select(c => $"\"{c}\""));

        /// <summary>Whether a DN cannot be on this server: it is not below anything the server holds.</summary>
        private bool Outside(string dn) => _contexts.Count > 0 && LdapDirectory.Rdns(dn) is not null && !_contexts.Any(c => LdapDirectory.IsUnder(dn, c));

        /// <summary>Signs in as the service account, or anonymously; false (with the failed step) when the server says no.</summary>
        private async Task<bool> BindAsync(LdapConnection conn, CancellationToken ct)
        {
            try
            {
                await LdapDirectory.BindServiceAsync(conn, o, ct);
            }
            catch (LdapException ex) when (!LdapErrors.IsConnectionFailure(ex))
            {
                Failed(Anonymous ? AnonymousRefused(ex) : await ServiceRefusedAsync(ex, ct));
                return false;
            }
            if (Anonymous)
            {
                Add(Warn, "Connected anonymously: no service account is set. Most directories show little or nothing to anonymous connections, and Active Directory nothing at all.");
            }
            else
            {
                Add(Ok, $"Signed in as {o.BindDn!.Trim()} with {Password}.");
            }
            return true;
        }

        private static string AnonymousRefused(LdapException ex) => ex.ResultCode switch
        {
            LdapException.ConfidentialityRequired or LdapException.StrongAuthRequired =>
                "The server wants an encrypted connection first: use ldaps:// or turn on \"Use StartTLS\".",
            _ => $"The server does not let anonymous connections in ({LdapErrors.Answer(ex)}): set a service account and its password.",
        };

        private async Task<string> ServiceRefusedAsync(LdapException ex, CancellationToken ct)
        {
            var dn = o.BindDn!.Trim();
            switch (ex.ResultCode)
            {
                case LdapException.InvalidCredentials:
                    if (LdapErrors.AdReason(ex) is { } why)
                    {
                        return why is "the name or the password is wrong" or "there is no such account"
                            ? $"Active Directory refused the service account {dn}: {why}. Check the account's name and {Password}. On Active Directory it can also be written user@domain or DOMAIN\\user, which is easier to get right than its DN (whose CN is the account's full name, not its login)."
                            : $"Active Directory refused the service account {dn}: {why}. The name and the password are right; fix the account in Active Directory (a service account's password is best set never to expire), then test again.";
                    }
                    var said = LdapErrors.ServerMessage(ex) is { } m ? $" It said: {m}." : "";
                    if (Outside(dn))
                    {
                        return $"The server refused the service account {dn}: no such entry can be here, as this server holds {Holds} and {dn} is not below it.{said}";
                    }
                    if (LooksLikeDn(dn))
                    {
                        // OpenLDAP answers a DN with no entry as it answers a wrong password: an anonymous look tells them apart where it may.
                        switch (await LookAsync(dn, ct))
                        {
                            case (true, _):
                                return $"The server refused the service account {dn} with {Password}: the password is wrong. The DN is right (an entry has it), unless that entry has no password of its own (no userPassword) or a password policy has locked it.{said}";
                            case (false, var matched):
                                return $"The server refused the service account {dn}: no entry has this DN. The part of it that exists is \"{matched}\": check the rest letter by letter.{said}";
                        }
                    }
                    return $"The server refused the service account {dn} with {Password}: the DN or the password is wrong. The server says the same for both, and does not let an anonymous look see whether the DN exists, so this test cannot tell which. Check the DN letter by letter (a typo in it reads the same as a wrong password), then the password.{said}";
                case LdapException.InvalidDnSyntax:
                    return NotADn("The service account", dn, ex);
                case LdapException.ConfidentialityRequired or LdapException.StrongAuthRequired:
                    return "The server wants an encrypted connection before a password is sent: use ldaps:// or turn on \"Use StartTLS\".";
                case LdapException.UnwillingToPerform:
                    return $"The server will not let {dn} sign in: {LdapErrors.Answer(ex)}.";
                default:
                    return $"The server refused the service account {dn}: {LdapErrors.Answer(ex)}.";
            }
        }

        /// <summary>
        /// Whether an entry has this DN, as an anonymous look sees it (a new connection, no sign-in): yes; no,
        /// with the part of it that exists; or null when the server shows an anonymous look nothing there.
        /// </summary>
        private async Task<(bool Exists, string? Matched)?> LookAsync(string dn, CancellationToken ct)
        {
            try
            {
                using var look = await LdapDirectory.ConnectAsync(o, new LdapDirectory.Seen(), ct);
                await look.ReadAsync(dn, ["1.1"], ct);
                return (true, null);
            }
            catch (LdapException ex) when (ex.ResultCode == LdapException.NoSuchObject && ex.MatchedDn is { Length: > 0 } matched)
            {
                // OpenLDAP names the part that exists only when the entry is truly missing, never for one it hides.
                return (false, matched);
            }
            catch (Exception ex) when (LdapErrors.IsDirectoryFailure(ex))
            {
                return null;
            }
        }

        private static string NotADn(string what, string value, LdapException ex)
        {
            var form = value.Contains('@', StringComparison.Ordinal) ? "user@domain"
                : value.Contains('\\', StringComparison.Ordinal) && !value.Contains('=', StringComparison.Ordinal) ? "DOMAIN\\user"
                : !value.Contains('=', StringComparison.Ordinal) ? "plain name"
                : null;
            if (form is not null)
            {
                return $"{what} \"{value}\" is not a DN, and this server takes only DNs: write the full DN, like cn=reader,ou=services,dc=example,dc=com. The {form} form works only with Active Directory.";
            }
            return $"{what} \"{value}\" is not a valid DN ({LdapErrors.ServerMessage(ex) ?? "invalid DN"}). A DN is name=value pairs joined by commas, like cn=reader,ou=services,dc=example,dc=com; a comma inside a value is written \\,.";
        }

        /// <summary>A place (where people or groups are) that is not a DN: a domain name gets its DN form as the example.</summary>
        private static string NotAPlace(string what, string value, LdapException ex)
        {
            var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var example = !value.Contains('=', StringComparison.Ordinal) && parts.Length > 1 && parts.All(p => p.All(c => char.IsLetterOrDigit(c) || c == '-'))
                ? $"the domain {value} is {string.Join(',', parts.Select(p => "dc=" + p))}, and its people could be below ou=people,{string.Join(',', parts.Select(p => "dc=" + p))}"
                : "like ou=people,dc=example,dc=com";
            return $"{what}, \"{value}\", is not a DN ({LdapErrors.ServerMessage(ex) ?? "invalid DN"}): write it as name=value pairs joined by commas; {example}.";
        }

        /// <summary>Where people are: the place exists, and the user filter finds people there.</summary>
        public async Task<bool> PeopleAsync(LdapConnection conn, CancellationToken ct)
        {
            var baseDn = o.UserBaseDn.Trim();
            if (baseDn.Length == 0)
            {
                var example = _contexts.Count > 0 ? $": this server holds {Holds}, so for example \"ou=people,{_contexts[0]}\" or \"{_contexts[0]}\" itself" : ", like ou=people,dc=example,dc=com";
                Failed($"\"Where people are\" is empty: give the DN that people are below{example}.");
                return false;
            }
            if (!await PlaceAsync(conn, "where people are", baseDn, ct))
            {
                return false;
            }
            var filter = (string.IsNullOrWhiteSpace(o.UserFilter) ? LdapOptions.DefaultUserFilter : o.UserFilter.Trim()).Replace("{0}", "*", StringComparison.Ordinal);
            var (count, more, first, error) = await CountAsync(conn, baseDn, filter, ct);
            if (error is not null)
            {
                Failed(error.ResultCode is LdapException.FilterError or LdapException.ProtocolError
                    ? $"The user filter is not valid: {filter} ({LdapErrors.Answer(error)})."
                    : $"Looking for people below \"{baseDn}\" failed: {LdapErrors.Answer(error)}.");
                return false;
            }
            if (count == 0)
            {
                Failed($"Nobody below \"{baseDn}\" matches the user filter, as {Who} sees it. Check \"Where people are\", and that {Who} may read people there. The filter: {filter}");
                return false;
            }
            var people = more ? $"{count} people or more (the server stops counting there)" : count == 1 ? "1 person" : $"{count} people";
            Add(Ok, $"Found {people} below \"{baseDn}\", such as {first}.");
            _summary = $"It works: {(Anonymous ? "connected anonymously" : $"signed in as {o.BindDn!.Trim()}")} and found {people} below \"{baseDn}\".";
            return true;
        }

        /// <summary>People matching the filter: how many (up to a limit), whether there are more, and the first.</summary>
        private static async Task<(int Count, bool More, string? First, LdapException? Error)> CountAsync(LdapConnection conn, string baseDn, string filter, CancellationToken ct)
        {
            const int Most = 1000;
            var (count, first) = (0, (string?)null);
            try
            {
                // Only the names (1.1): a count, not the people.
                var results = await conn.SearchAsync(baseDn, LdapConnection.ScopeSub, filter, ["1.1"], false, new LdapSearchConstraints { MaxResults = Most }, ct);
                while (await results.HasMoreAsync(ct))
                {
                    try
                    {
                        var entry = await results.NextAsync(ct);
                        first ??= entry.Dn;
                        count++;
                    }
                    catch (LdapReferralException)
                    {
                        // Active Directory points to its other partitions from the domain's root: not people.
                    }
                }
                return (count, false, first, null);
            }
            catch (LdapException ex) when (ex.ResultCode is LdapException.SizeLimitExceeded or LdapException.AdminLimitExceeded)
            {
                // The server's limit (OpenLDAP 500, Active Directory 1000) or this one: plenty of people.
                return (count, true, first, null);
            }
            catch (LdapException ex) when (!LdapErrors.IsConnectionFailure(ex))
            {
                return (count, false, first, ex);
            }
        }

        /// <summary>A DN that must exist (where people are, where groups are); false (with the failed step) when it does not.</summary>
        private async Task<bool> PlaceAsync(LdapConnection conn, string what, string dn, CancellationToken ct)
        {
            try
            {
                if (await conn.ReadAsync(dn, ["1.1"], ct) is not null)
                {
                    return true;
                }
                NotThere(what, dn, null);
            }
            catch (LdapException ex) when (ex.ResultCode == LdapException.NoSuchObject)
            {
                NotThere(what, dn, ex.MatchedDn);
            }
            catch (LdapException ex) when (ex.ResultCode == LdapException.InvalidDnSyntax)
            {
                Failed(NotAPlace(what == "where people are" ? "\"Where people are\"" : "\"Where groups are\"", dn, ex));
            }
            catch (LdapException ex) when (!LdapErrors.IsConnectionFailure(ex))
            {
                Failed($"Reading \"{dn}\" ({what}) failed: {LdapErrors.Answer(ex)}.");
            }
            return false;
        }

        private void NotThere(string what, string dn, string? matched)
        {
            var part = matched is { Length: > 0 } ? $" The part of it that exists is \"{matched}\"." : "";
            var holds = Outside(dn) ? $" This server holds {Holds}." : "";
            Failed($"\"{dn}\" ({what}) does not exist on this server, or {Who} cannot see it.{part}{holds}");
        }

        /// <summary>Where groups are, and the admin and required groups: each found, and how a member's groups will be known.</summary>
        public async Task GroupsAsync(LdapConnection conn, CancellationToken ct)
        {
            var groupBase = o.GroupBaseDn?.Trim();
            if (!string.IsNullOrEmpty(groupBase) && !await PlaceAsync(conn, "where groups are", groupBase, ct))
            {
                return;
            }
            await GroupAsync(conn, "Admin group", o.AdminGroup, required: false, ct);
            await GroupAsync(conn, "Required group", o.RequiredGroup, required: true, ct);
        }

        private async Task GroupAsync(LdapConnection conn, string label, string? group, bool required, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(group))
            {
                return;
            }
            group = group.Trim();
            var nobody = required ? "nobody from the directory can sign in" : "nobody from the directory will be an admin here";
            LdapEntry? found;
            (string What, string Fix)? unread;
            List<LdapEntry> same = [];
            try
            {
                found = await LdapDirectory.FindGroupAsync(o, conn, group, _contexts, GroupAttributes, ct);
                unread = await UnreadAsync(conn, group, found, ct);
                if (unread is null && !LooksLikeDn(group))
                {
                    same = await LdapDirectory.NamedAsync(conn, group, Counted, ["1.1"], anyKind: false, ct);
                }
            }
            catch (LdapException ex) when (!LdapErrors.IsConnectionFailure(ex))
            {
                Add(Warn, $"{label}: looking for \"{group}\" failed ({LdapErrors.Answer(ex)}).");
                return;
            }
            if (found is null || unread is { })
            {
                Add(required ? Fail : Warn, $"{label}: {unread?.What}, so {nobody}. {unread?.Fix}");
                return;
            }
            Add(Ok, $"{label}: found {found.Dn}.");
            if (same.Count > 1)
            {
                // By its name, every group of that name counts: another app's "admins" would make its members admins here.
                var shown = string.Join("; ", same.Take(5).Select(g => g.Dn)) + (same.Count > 5 ? "; ..." : "");
                var each = required ? "the members of each may sign in" : "the members of each are admins here";
                var narrower = _openLdap && string.IsNullOrWhiteSpace(o.GroupBaseDn) ? ", or set \"Where groups are\" to a place that holds only it" : "";
                Add(required ? Warn : Fail, $"{label}: {same.Count} groups go by \"{group}\" ({shown}), and every one counts: {each}. Name the one meant by its full DN{narrower}.");
            }
            if (string.IsNullOrWhiteSpace(o.GroupBaseDn) && !await MemberOfWorksAsync(conn, found, ct))
            {
                Add(required ? Fail : Warn, $"{label}: its members do not show it in their memberOf (this server's memberOf overlay is off, or covers another kind of group), so {nobody}. Set \"Where groups are\" (like {Parent(found.Dn)}) and groups are searched there.");
            }
            else if (!string.IsNullOrWhiteSpace(o.GroupBaseDn) && _openLdap && !LdapDirectory.IsUnder(found.Dn, o.GroupBaseDn.Trim()))
            {
                // Named by its DN, elsewhere: with "Where groups are" set, OpenLDAP's people have only the groups there.
                Add(required ? Fail : Warn, $"{label}: {found.Dn} is not below \"{o.GroupBaseDn.Trim()}\" (where groups are), and on OpenLDAP only the groups there count, so {nobody}. Name a group below it, or empty \"Where groups are\" if its members show it in their memberOf.");
            }
        }

        /// <summary>
        /// Where a group's name counts when signing in: below "Where groups are" on OpenLDAP (which sends memberOf
        /// only when asked, and it is not asked then); else anywhere, as memberOf names groups all over the directory.
        /// </summary>
        private IReadOnlyList<string> Counted =>
            !string.IsNullOrWhiteSpace(o.GroupBaseDn) && (_openLdap || _contexts.Count == 0) ? [o.GroupBaseDn.Trim()]
            : _contexts.Count > 0 ? _contexts : [o.UserBaseDn.Trim()];

        /// <summary>
        /// Why a group setting can match nobody, when it names no group signing in reads (<paramref name="found"/> is
        /// what <see cref="LdapDirectory.FindGroupAsync"/> found): what it names instead, and what to do. Null when it names one.
        /// </summary>
        private async Task<(string What, string Fix)?> UnreadAsync(LdapConnection conn, string group, LdapEntry? found, CancellationToken ct)
        {
            if (found is not null)
            {
                return Posix(found) ? PosixGroup(found.Dn) : null;
            }
            var named = await LdapDirectory.NamedAsync(conn, group, LdapDirectory.Places(o, _contexts), GroupAttributes, anyKind: true, ct);
            if (named.FirstOrDefault(Posix) is { } posix)
            {
                return PosixGroup(posix.Dn);
            }
            if (named.FirstOrDefault() is { } other)
            {
                var classes = other.GetAttributeSet().TryGetValue("objectClass", out var c) ? string.Join(", ", c.StringValueArray.Where(x => !x.Equals("top", StringComparison.OrdinalIgnoreCase))) : "unknown";
                return ($"\"{group}\" names {other.Dn}, which is not a group (its objectClass: {classes})",
                    "Name a group: a groupOfNames, a groupOfUniqueNames or an Active Directory group, by its name or its full DN.");
            }
            return (LooksLikeDn(group) ? $"no group has the DN \"{group}\", as {Who} sees it" : $"no group \"{group}\" found {LdapDirectory.GroupPlaces(o, _contexts)}, as {Who} sees it",
                "Check the name (a group's name or its full DN).");
        }

        /// <summary>A group whose members are listed by uid only (memberUid), as a posixGroup's are: nobody is ever found in it here.</summary>
        private static bool Posix(LdapEntry entry)
        {
            var attrs = entry.GetAttributeSet();
            return !MemberAttributes.Any(attrs.ContainsKey)
                && (attrs.ContainsKey("memberUid") || (attrs.TryGetValue("objectClass", out var c) && c.StringValueArray.Contains("posixGroup", StringComparer.OrdinalIgnoreCase)));
        }

        private static (string What, string Fix) PosixGroup(string dn) =>
            ($"{dn} is a posixGroup, whose members (memberUid) are not read here",
                "Signing in reads groupOfNames and groupOfUniqueNames groups (member, uniqueMember) and Active Directory's groups: name one of those. With the rfc2307bis schema, a posixGroup can be a groupOfNames too, its members listed in member.");

        /// <summary>For a try: why nobody can be in a group setting; null when it names a group signing in reads, or cannot be looked up.</summary>
        private async Task<(string What, string Fix)?> UnreadNoteAsync(LdapConnection conn, string group, CancellationToken ct)
        {
            group = group.Trim();
            try
            {
                return await UnreadAsync(conn, group, await LdapDirectory.FindGroupAsync(o, conn, group, _contexts, GroupAttributes, ct), ct);
            }
            catch (LdapException ex) when (!LdapErrors.IsConnectionFailure(ex))
            {
                return null;
            }
        }

        /// <summary>Whether a member of the group has it in memberOf (asked for by name), as the app reads groups without "Where groups are".</summary>
        private static async Task<bool> MemberOfWorksAsync(LdapConnection conn, LdapEntry group, CancellationToken ct)
        {
            var attrs = group.GetAttributeSet();
            var member = MemberAttributes.Select(a => attrs.TryGetValue(a, out var m) ? m.StringValueArray.FirstOrDefault(v => v.Length > 0) : null).FirstOrDefault(v => v is not null);
            if (member is null)
            {
                return true; // nobody in it to look at: nothing to say
            }
            try
            {
                var entry = await conn.ReadAsync(member, ["memberOf"], ct);
                return entry.GetAttributeSet().TryGetValue("memberOf", out var of) && of.StringValueArray.Any(g => LdapDirectory.SameDn(g, group.Dn));
            }
            catch (LdapException)
            {
                return true; // the member cannot be read: no judgement
            }
        }

        /// <summary>The person's sign-in, as the app makes it, step by step; the person when it works.</summary>
        public async Task<LdapPerson?> PersonAsync(LdapConnection conn, string login, string password, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(o.UserBaseDn))
            {
                Failed("\"Where people are\" is empty: people are searched below it.");
                return null;
            }
            var name = LdapDirectory.SignInName(login);
            if (name != login.Trim())
            {
                Add(Ok, $"Looking for \"{name}\": the domain in front is left out, as at sign-in.");
            }
            LdapSignIn signIn;
            try
            {
                signIn = await LdapDirectory.SignInWithAsync(o, conn, login, password, ct);
            }
            catch (LdapUnavailableException)
            {
                // Where groups are is missing: signing in would refuse everyone with "cannot be reached".
                Failed($"Their groups cannot be read: {LdapDirectory.GroupBaseMissing(o)}. Signing in would answer \"the directory cannot be reached\" for everyone.");
                return null;
            }
            catch (LdapException ex) when (ex.ResultCode == LdapException.NoSuchObject)
            {
                await PlaceAsync(conn, "where people are", o.UserBaseDn.Trim(), ct);
                if (!_steps.Any(s => s.State == Fail))
                {
                    Failed($"Looking for \"{name}\" failed: {LdapErrors.Answer(ex)}.");
                }
                return null;
            }
            catch (LdapException ex) when (!LdapErrors.IsConnectionFailure(ex))
            {
                Failed($"Looking for \"{name}\" failed: {LdapErrors.Answer(ex)}. The search was {LdapDirectory.UserFilterFor(o, name)}");
                return null;
            }
            if (signIn.Found.Count == 0)
            {
                Failed($"Nobody below \"{o.UserBaseDn.Trim()}\" is called \"{name}\", as {Who} sees it. The search was {signIn.Filter}");
                return null;
            }
            if (signIn.Found.Count > 1)
            {
                Failed($"{signIn.Found.Count} entries match \"{name}\" ({string.Join("; ", signIn.Found.Take(5))}): the app refuses rather than guess who is meant. Make the user filter narrower.");
                return null;
            }
            Add(Ok, $"Found {signIn.Found[0]}.");
            if (signIn.Failure is { } failure)
            {
                Failed($"Their password could not be checked: the directory {failure}. Signing in would answer \"the directory cannot be reached\" for now, and count nothing against them.");
                return null;
            }
            if (signIn.Person is not { } person)
            {
                PasswordRefused = true;
                Failed($"Their password was not accepted: {signIn.Refusal}.");
                return null;
            }
            Add(Ok, "Their password is right.");
            Add(Ok, $"Here they would be \"{person.UserName}\", {person.Email ?? "with no email"}, shown as \"{person.DisplayName}\".");
            var groups = person.Groups.Select(LdapDirectory.CommonName).Order(StringComparer.OrdinalIgnoreCase).ToList();
            Add(groups.Count == 0 && (!string.IsNullOrWhiteSpace(o.AdminGroup) || !string.IsNullOrWhiteSpace(o.RequiredGroup)) ? Warn : Ok,
                groups.Count > 0 ? $"Their groups: {string.Join(", ", groups)}."
                : string.IsNullOrWhiteSpace(o.GroupBaseDn) ? "No groups found for them: with \"Where groups are\" empty, only their memberOf counts."
                : $"No groups found for them below \"{o.GroupBaseDn.Trim()}\".");
            // In the order signing in checks them.
            if (!LdapDirectory.IsAllowed(o, person))
            {
                // Said with why nobody can be, when the setting names no group signing in reads (a posixGroup, say).
                var why = await UnreadNoteAsync(conn, o.RequiredGroup!, ct) is { } u ? $", and nobody can be: {u.What}. {u.Fix}" : ".";
                Failed($"They are not in the required group \"{o.RequiredGroup!.Trim()}\", so they may not sign in{why}");
                return null;
            }
            if (person.Email is null)
            {
                Failed($"Their entry has no email ({o.EmailAttribute}): the app needs one to make their account, so they cannot sign in.");
                return null;
            }
            Add(Ok, LdapDirectory.IsAdmin(o, person) ? $"An admin here: they are in \"{o.AdminGroup!.Trim()}\"." : "A member here, not an admin.");
            if (!LdapDirectory.IsAdmin(o, person) && !string.IsNullOrWhiteSpace(o.AdminGroup) && await UnreadNoteAsync(conn, o.AdminGroup, ct) is { } admins)
            {
                Add(Warn, $"Nobody can be an admin through \"Admin group\": {admins.What}. {admins.Fix}");
            }
            _summary = $"{person.UserName} can sign in{(LdapDirectory.IsAdmin(o, person) ? ", as an admin" : "")}.";
            return person;
        }
    }

    /// <summary>Adds this app's own verdict to a try the directory passed: its refusal (a local account by that name, say), or how they come in.</summary>
    public static LdapCheckResult WithAppVerdict(LdapCheckResult result, string? refusal, string verdict)
    {
        if (!result.Ok)
        {
            return result;
        }
        if (refusal is not null)
        {
            var text = $"The directory lets them in, but this app does not: {refusal}.";
            return new(false, text, [.. result.Steps, new LdapStep(Fail, text)]);
        }
        return result with { Steps = [.. result.Steps, new LdapStep(Ok, verdict)] };
    }

    private static bool LooksLikeDn(string value) => value.Contains('=', StringComparison.Ordinal);

    private static string Parent(string dn)
    {
        var comma = dn.IndexOf(',', StringComparison.Ordinal);
        return comma >= 0 ? dn[(comma + 1)..] : dn;
    }
}
