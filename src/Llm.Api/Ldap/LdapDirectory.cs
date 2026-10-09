using System.Globalization;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Llm.Api.Chat.Tools;
using Llm.Core.Chat;
using Microsoft.Extensions.Options;
using Novell.Directory.Ldap;

namespace Llm.Api.Ldap;

/// <summary>A person as the directory describes them.</summary>
public sealed record LdapPerson(string Dn, string UserName, string? Email, string DisplayName, IReadOnlyList<string> Groups);

/// <summary>
/// A person's sign-in at the directory: who they are when the password is right, else why not, in
/// words for the audit log and the admin's try (never for the person: it can say an account exists).
/// </summary>
/// <param name="Filter">The search that looked for them.</param>
/// <param name="Found">The entries the search found (their DNs).</param>
/// <param name="Failure">
/// Why their password could not be checked at all (the directory busy or unavailable when asked): then
/// nothing is known of it, and nothing may count against them.
/// </param>
public sealed record LdapSignIn(LdapPerson? Person, string Refusal, string Filter, IReadOnlyList<string> Found, string? Failure = null);

public interface ILdapDirectory
{
    bool Enabled { get; }

    /// <summary>
    /// Finds the person and checks their password as the directory sees it. Throws
    /// <see cref="LdapUnavailableException"/> when the directory cannot be used at all.
    /// </summary>
    Task<LdapSignIn> SignInAsync(string login, string password, CancellationToken ct = default);

    /// <summary>Re-reads a person for the sync; null when the entry no longer exists.</summary>
    Task<LdapPerson?> FindByDnAsync(string dn, CancellationToken ct = default);

    /// <summary>
    /// Finds the required group in the directory, and one of its members in it as signing in reads people. Throws
    /// <see cref="LdapUnavailableException"/> when it is not there (a typo in its name, most likely), or signing in
    /// finds none of its members in it (memberOf without it, say): nobody may be disabled for not being in a group
    /// that nobody can be found in.
    /// </summary>
    Task CheckRequiredGroupAsync(CancellationToken ct = default);

    bool IsAdmin(LdapPerson person);
    bool IsAllowed(LdapPerson person);
}

/// <summary>The directory cannot be used (not reached, the service account refused, a search failed): nobody is changed because of it.</summary>
public sealed class LdapUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class LdapDirectory(IOptionsMonitor<LdapOptions> options) : ILdapDirectory
{
    /// <summary>
    /// A person's attributes. With "Where groups are" empty, memberOf too, by name: OpenLDAP's memberOf
    /// overlay sends it only when asked. With it set, as v5.2.0 read them: then only the groups there
    /// count on OpenLDAP, never one of the same name elsewhere (Active Directory sends memberOf anyway).
    /// </summary>
    internal static string[] PersonAttributes(LdapOptions o) => string.IsNullOrWhiteSpace(o.GroupBaseDn) ? ["*", "memberOf"] : ["*"];

    // Read on every use: a change saved in the Settings page applies at once.
    private LdapOptions _o => options.CurrentValue;

    public bool Enabled => _o.Enabled;

    public async Task<LdapSignIn> SignInAsync(string login, string password, CancellationToken ct = default)
    {
        var o = _o;
        // An empty password makes many servers perform an anonymous bind and
        // report success. Never let that stand in for a person's password.
        if (!o.Enabled || string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
        {
            return new(null, "no name or no password", "", []);
        }
        var signIn = await WithServiceAsync(o, conn => SignInWithAsync(o, conn, login, password, ct), ct);
        // Busy or unavailable is the directory's state, not a wrong password: "cannot be reached", and nobody counted.
        return signIn.Failure is { } failure ? throw new LdapUnavailableException($"The directory at {o.Url} cannot be used: it {failure}.") : signIn;
    }

    public Task<LdapPerson?> FindByDnAsync(string dn, CancellationToken ct = default)
    {
        var o = _o;
        return WithServiceAsync(o, async conn =>
        {
            List<LdapEntry> matches;
            try
            {
                matches = await SearchAsync(conn, dn, LdapConnection.ScopeBase, "(objectClass=*)", PersonAttributes(o), ct);
            }
            catch (LdapException ex) when (ex.ResultCode == LdapException.NoSuchObject)
            {
                // Only their own entry missing means they left: a failure below (their groups) is the settings', and changes nobody.
                return null;
            }
            return matches.Count == 1 ? await ToPersonAsync(o, conn, matches[0], ct) : null;
        }, ct);
    }

    public Task CheckRequiredGroupAsync(CancellationToken ct = default)
    {
        var o = _o;
        if (string.IsNullOrWhiteSpace(o.RequiredGroup))
        {
            return Task.CompletedTask;
        }
        return WithServiceAsync(o, async conn =>
        {
            var contexts = string.IsNullOrWhiteSpace(o.GroupBaseDn) ? await ContextsAsync(conn, ct) : [];
            var group = await FindGroupAsync(o, conn, o.RequiredGroup, contexts, MemberAttributes, ct)
                ?? throw new LdapUnavailableException($"The directory at {o.Url} cannot be used: {RequiredGroupMissing(o, contexts)}.");
            return await UnseenAsync(o, conn, group, ct) is { } unseen
                ? throw new LdapUnavailableException($"The directory at {o.Url} cannot be used: {unseen}.")
                : true;
        }, ct);
    }

    /// <summary>The attributes that list a group's members, as signing in reads groups.</summary>
    internal static readonly string[] MemberAttributes = ["member", "uniqueMember"];

    /// <summary>
    /// Why signing in finds nobody in the required group, which is there: its members, read as signing in reads a
    /// person, are not in it (memberOf without it, or the group outside "Where groups are"). Null when one of them
    /// is, or when none can be read to tell. A few members are enough: signing in reads every person alike.
    /// </summary>
    private static async Task<string?> UnseenAsync(LdapOptions o, LdapConnection conn, LdapEntry group, CancellationToken ct)
    {
        var attrs = group.GetAttributeSet();
        var members = MemberAttributes.SelectMany(a => attrs.TryGetValue(a, out var m) ? m.StringValueArray : []).Where(m => m.Length > 0).Take(5).ToList();
        var read = false;
        foreach (var member in members)
        {
            List<LdapEntry> entries;
            try
            {
                entries = await SearchAsync(conn, member, LdapConnection.ScopeBase, "(objectClass=*)", PersonAttributes(o), ct);
            }
            catch (LdapException ex) when (!LdapErrors.IsConnectionFailure(ex))
            {
                continue; // gone, or hidden from the service account: another one tells
            }
            if (entries.Count != 1)
            {
                continue;
            }
            read = true;
            if (IsAllowed(o, await ToPersonAsync(o, conn, entries[0], ct)))
            {
                return null;
            }
        }
        if (!read)
        {
            return null;
        }
        var nobody = "so signing in finds nobody in it, and nobody is disabled for not being in it";
        var test = "in the Settings page, where \"Test the settings\" says why";
        return string.IsNullOrWhiteSpace(o.GroupBaseDn)
            ? $"the required group {group.Dn} is there, but its members do not show it in their memberOf (this server's memberOf overlay is off, or covers another kind of group), {nobody}: set \"Where groups are\" (like {Parent(group.Dn)}) {test}"
            : !IsUnder(group.Dn, o.GroupBaseDn.Trim())
                ? $"the required group {group.Dn} is not below \"{o.GroupBaseDn.Trim()}\" (where groups are), where signing in looks for people's groups, {nobody}: name a group below it, or empty \"Where groups are\" if its members show it in their memberOf, {test}"
                : $"the required group {group.Dn} is there, but signing in does not find its members in it (the group search below \"{o.GroupBaseDn.Trim()}\" does not match them), {nobody}: check the group and its members {test}";
    }

    /// <summary>The DN above this one: where an entry is.</summary>
    internal static string Parent(string dn)
    {
        var first = FirstPart(dn);
        return first.Length < dn.Length ? dn[(first.Length + 1)..] : dn;
    }

    /// <summary>Why nobody is disabled for the required group: no group signing in reads goes by it.</summary>
    internal static string RequiredGroupMissing(LdapOptions o, IReadOnlyList<string> contexts)
    {
        var group = o.RequiredGroup?.Trim() ?? "";
        var where = group.Contains('=', StringComparison.Ordinal) ? "(no group has that DN, or the service account cannot see it)" : GroupPlaces(o, contexts);
        return $"no required group \"{group}\" is found {where}, so nobody is disabled for not being in it: fix \"Required group\" in the Settings page, where \"Test the settings\" says why";
    }

    /// <summary>Where a group named by its name is looked for, in words.</summary>
    internal static string GroupPlaces(LdapOptions o, IReadOnlyList<string> contexts) =>
        !string.IsNullOrWhiteSpace(o.GroupBaseDn) ? $"below \"{o.GroupBaseDn.Trim()}\""
        : contexts.Count > 0 ? "in " + string.Join(" and ", contexts.Select(c => $"\"{c}\""))
        : $"below \"{o.UserBaseDn.Trim()}\"";

    /// <summary>Where a group named by its name is looked for: below "Where groups are", else in the server's naming contexts, else below where people are.</summary>
    internal static IReadOnlyList<string> Places(LdapOptions o, IReadOnlyList<string> contexts) =>
        !string.IsNullOrWhiteSpace(o.GroupBaseDn) ? [o.GroupBaseDn.Trim()] : contexts.Count > 0 ? contexts : [o.UserBaseDn.Trim()];

    /// <summary>
    /// The kinds of group signing in reads: OpenLDAP's groupOfNames and groupOfUniqueNames (member, uniqueMember)
    /// and Active Directory's group. Not a posixGroup alone: its members (memberUid) are plain uids, which neither
    /// memberOf nor the group search sees, so nobody would ever be found in it.
    /// </summary>
    private static readonly string[] GroupClasses = ["groupOfNames", "groupOfUniqueNames", "group"];

    private static readonly string GroupClassFilter = "(|" + string.Concat(GroupClasses.Select(c => $"(objectClass={c})")) + ")";

    /// <summary>Whether an entry, read with its objectClass, is a group signing in reads.</summary>
    internal static bool IsGroup(LdapEntry entry) =>
        entry.GetAttributeSet().TryGetValue("objectClass", out var classes) && classes.StringValueArray.Any(c => GroupClasses.Contains(c, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// A group as a setting names it: by its DN, or by its name below "Where groups are", else in the
    /// server's naming contexts (else below where people are). Only a group that signing in would match
    /// counts, a name saved by v5.2.0 included ("Sales\" for CN=Sales\, EMEA). Null when there is none.
    /// </summary>
    internal static async Task<LdapEntry?> FindGroupAsync(LdapOptions o, LdapConnection conn, string group, IReadOnlyList<string> contexts, string[] attributes, CancellationToken ct) =>
        (await NamedAsync(conn, group, Places(o, contexts), attributes, anyKind: false, ct, firstOnly: true)).FirstOrDefault();

    /// <summary>
    /// The entries a group setting names: the entry with its DN, or those its name goes by (as signing in reads
    /// names) in these places, up to 100 in each. Only groups signing in reads, unless <paramref name="anyKind"/>:
    /// then whatever has the name, to say what it is. Each is read with its objectClass.
    /// </summary>
    internal static async Task<List<LdapEntry>> NamedAsync(LdapConnection conn, string group, IEnumerable<string> places, string[] attributes, bool anyKind, CancellationToken ct, bool firstOnly = false)
    {
        group = group.Trim();
        attributes = [.. attributes.Where(a => a != "1.1").Append("objectClass").Distinct(StringComparer.OrdinalIgnoreCase)];
        if (group.Contains('=', StringComparison.Ordinal))
        {
            try
            {
                // Only what signing in would match: the DN as it compares there, and a group of a kind it reads.
                var entry = await conn.ReadAsync(group, attributes, ct);
                return entry is not null && IsNamed(entry.Dn, group) && (anyKind || IsGroup(entry)) ? [entry] : [];
            }
            catch (LdapException ex) when (ex.ResultCode is LdapException.NoSuchObject or LdapException.InvalidDnSyntax)
            {
                return [];
            }
        }
        var filter = anyKind ? NameFilter(group) : $"(&{GroupClassFilter}{NameFilter(group)})";
        var found = new List<LdapEntry>();
        foreach (var place in places)
        {
            try
            {
                var hits = await SearchAsync(conn, place, LdapConnection.ScopeSub, filter, attributes, ct, most: 100);
                found.AddRange(hits.Where(g => IsNamed(g.Dn, group) && !found.Any(f => SameDn(f.Dn, g.Dn))));
            }
            catch (LdapException ex) when (ex.ResultCode == LdapException.NoSuchObject)
            {
                // Not visible from there: the next place.
            }
            if (firstOnly && found.Count > 0)
            {
                break;
            }
        }
        return found;
    }

    /// <summary>
    /// The search for a group by its name: its cn as written and with its escapes read; for a name v5.2.0
    /// cut at an escaped comma ("Sales\"), the cns that begin with it and that comma.
    /// </summary>
    private static string NameFilter(string group)
    {
        if ((group.Length - group.TrimEnd('\\').Length) % 2 == 1)
        {
            return $"(cn={EscapeFilter(Unescape(group[..^1]) + ",")}*)";
        }
        var read = Unescape(group);
        return read == group ? $"(cn={EscapeFilter(group)})" : $"(|(cn={EscapeFilter(group)})(cn={EscapeFilter(read)}))";
    }

    /// <summary>The server's naming contexts, from its root entry: where its entries are. Empty when it shows none.</summary>
    private static async Task<List<string>> ContextsAsync(LdapConnection conn, CancellationToken ct)
    {
        try
        {
            return Contexts((await conn.ReadAsync("", ["namingContexts", "defaultNamingContext"], ct)).GetAttributeSet());
        }
        catch (LdapException ex) when (!LdapErrors.IsConnectionFailure(ex))
        {
            return [];
        }
    }

    /// <summary>The naming contexts a root entry lists, its default first, without Active Directory's own partitions (no people or groups there).</summary>
    internal static List<string> Contexts(LdapAttributeSet root)
    {
        var contexts = new List<string>();
        if (root.TryGetValue("defaultNamingContext", out var main) && main.StringValue is { Length: > 0 } d)
        {
            contexts.Add(d);
        }
        if (root.TryGetValue("namingContexts", out var all))
        {
            contexts.AddRange(all.StringValueArray.Where(c => c.Length > 0 && !contexts.Contains(c, StringComparer.OrdinalIgnoreCase)
                && !c.StartsWith("CN=Configuration,", StringComparison.OrdinalIgnoreCase) && !c.StartsWith("CN=Schema,", StringComparison.OrdinalIgnoreCase)
                && !c.StartsWith("DC=ForestDnsZones,", StringComparison.OrdinalIgnoreCase) && !c.StartsWith("DC=DomainDnsZones,", StringComparison.OrdinalIgnoreCase)));
        }
        return contexts;
    }

    public bool IsAdmin(LdapPerson person) => IsAdmin(_o, person);

    public bool IsAllowed(LdapPerson person) => IsAllowed(_o, person);

    public static bool IsAdmin(LdapOptions o, LdapPerson person) => InGroup(person, o.AdminGroup);

    public static bool IsAllowed(LdapOptions o, LdapPerson person) => string.IsNullOrWhiteSpace(o.RequiredGroup) || InGroup(person, o.RequiredGroup);

    private static bool InGroup(LdapPerson person, string? group) =>
        !string.IsNullOrWhiteSpace(group) && person.Groups.Any(g => IsNamed(g, group));

    /// <summary>
    /// Whether a directory group (its DN) goes by this name: its full DN, or its common name, in any case. A DN
    /// matches however it is written: spaces around its commas and equals signs, \, or \2C for a comma.
    /// </summary>
    public static bool IsNamed(string dn, string group)
    {
        group = group.Trim();
        return Names(dn).Any(n => string.Equals(n, group, StringComparison.OrdinalIgnoreCase))
            || (group.Contains('=', StringComparison.Ordinal) && SameDn(dn, group));
    }

    /// <summary>
    /// Whether two DNs name the same entry, however each is written; false when either is not a DN. Their first
    /// parts are compared first: most DNs differ there, and the groups page compares many.
    /// </summary>
    public static bool SameDn(string a, string b) =>
        NormalRdn(FirstPart(a.Trim())) is { } first && first == NormalRdn(FirstPart(b.Trim()))
        && Rdns(a) is { } x && Rdns(b) is { } y && x.SequenceEqual(y, StringComparer.Ordinal);

    /// <summary>Whether a DN is the other or below it, however each is written.</summary>
    public static bool IsUnder(string dn, string above) =>
        Rdns(dn) is { } d && Rdns(above) is { } a && d.Count >= a.Count && d.Skip(d.Count - a.Count).SequenceEqual(a, StringComparer.Ordinal);

    /// <summary>
    /// A DN's parts (its RDNs), each in one form to compare: the attribute and the value in lower case, the
    /// value's escapes read and the spaces around it left out, written again with its special characters
    /// escaped (a multi-valued RDN's values in order). Null when it is not a DN.
    /// </summary>
    internal static List<string>? Rdns(string dn)
    {
        dn = dn.Trim();
        if (dn.Length == 0)
        {
            return null;
        }
        var rdns = new List<string>();
        foreach (var rdn in SplitUnescaped(dn, ",;"))
        {
            if (NormalRdn(rdn) is not { } normal)
            {
                return null;
            }
            rdns.Add(normal);
        }
        return rdns;
    }

    /// <summary>One part of a DN in the form <see cref="Rdns"/> gives it; null when it is not attribute=value.</summary>
    private static string? NormalRdn(string rdn)
    {
        var values = new List<string>(1);
        foreach (var pair in SplitUnescaped(rdn, "+"))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var type = eq > 0 ? pair[..eq].Trim() : "";
            if (type.Length == 0)
            {
                return null;
            }
            var value = Unescape(TrimValue(pair[(eq + 1)..])).ToLowerInvariant();
            var sb = new StringBuilder(type.ToLowerInvariant()).Append('=');
            foreach (var c in value)
            {
                if (c is '\\' or ',' or '+' or '=' or ';' or '"' or '<' or '>')
                {
                    sb.Append('\\');
                }
                sb.Append(c);
            }
            values.Add(sb.ToString());
        }
        values.Sort(StringComparer.Ordinal);
        return string.Join('+', values);
    }

    /// <summary>The parts of a DN between the separators given that are not escaped.</summary>
    private static List<string> SplitUnescaped(string value, string separators)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                i++;
            }
            else if (separators.Contains(value[i], StringComparison.Ordinal))
            {
                parts.Add(value[start..i]);
                start = i + 1;
            }
        }
        parts.Add(value[start..]);
        return parts;
    }

    /// <summary>A DN value without the spaces around it, keeping an escaped one at its end (\ ).</summary>
    private static string TrimValue(string value)
    {
        value = value.TrimStart(' ');
        var end = value.Length;
        while (end > 0 && value[end - 1] == ' ' && (end - 1 - value[..(end - 1)].TrimEnd('\\').Length) % 2 == 0)
        {
            end--;
        }
        return value[..end];
    }

    /// <summary>
    /// The names a directory group goes by: its DN, its common name, and the name v5.2.0 gave it (cut at
    /// the first comma, escapes left in: CN=Sales\, EMEA,... was "Sales\"), which groups and settings saved
    /// then may still hold.
    /// </summary>
    public static IEnumerable<string> Names(string dn)
    {
        var name = CommonName(dn);
        var first = dn.Split(',')[0];
        var eq = first.IndexOf('=', StringComparison.Ordinal);
        var old = eq >= 0 ? first[(eq + 1)..].Trim() : dn;
        return old == name ? [dn, name] : [dn, name, old];
    }

    /// <summary>
    /// The name to look for: what was typed, without a "DOMAIN\" in front (how Windows people
    /// often type it; the domain is the directory's own).
    /// </summary>
    internal static string SignInName(string login)
    {
        var name = login.Trim();
        var slash = name.LastIndexOf('\\');
        return slash >= 0 ? name[(slash + 1)..].Trim() : name;
    }

    /// <summary>The user filter for one name, escaped. Not string.Format: a filter may hold other braces.</summary>
    internal static string UserFilterFor(LdapOptions o, string name) =>
        (string.IsNullOrWhiteSpace(o.UserFilter) ? LdapOptions.DefaultUserFilter : o.UserFilter.Trim()).Replace("{0}", EscapeFilter(name), StringComparison.Ordinal);

    /// <summary>Finds the person below the user base and binds as them: one sign-in, as the app and the admin's try make it.</summary>
    internal static async Task<LdapSignIn> SignInWithAsync(LdapOptions o, LdapConnection service, string login, string password, CancellationToken ct)
    {
        var name = SignInName(login);
        var filter = UserFilterFor(o, name);
        if (name.Length == 0 || string.IsNullOrEmpty(password))
        {
            return new(null, "no name or no password", filter, []);
        }
        var matches = await SearchAsync(service, o.UserBaseDn, LdapConnection.ScopeSub, filter, PersonAttributes(o), ct);
        var found = matches.Select(m => m.Dn).ToList();
        if (matches.Count != 1)
        {
            // Unknown, or ambiguous: refuse rather than guess who is meant.
            return new(null, matches.Count == 0 ? "nobody by that name" : $"{matches.Count} entries match the name", filter, found);
        }
        var entry = matches[0];
        using (var user = await ConnectAsync(o, new Seen(), ct))
        {
            try
            {
                await user.BindAsync(entry.Dn, password, ct);
            }
            catch (LdapException ex) when (LdapErrors.IsPersonRefusal(ex))
            {
                return new(null, LdapErrors.PersonRefused(ex), filter, found);
            }
            catch (LdapException ex) when (!LdapErrors.IsConnectionFailure(ex))
            {
                return new(null, "", filter, found, LdapErrors.PersonNotChecked(entry.Dn, ex));
            }
        }
        return new(await ToPersonAsync(o, service, entry, ct), "", filter, found);
    }

    internal static async Task<LdapPerson> ToPersonAsync(LdapOptions o, LdapConnection conn, LdapEntry entry, CancellationToken ct)
    {
        var attrs = entry.GetAttributeSet();
        var userName = Split(o.UserNameAttributes).Select(a => Value(attrs, a)).FirstOrDefault(v => !string.IsNullOrEmpty(v))
            ?? CommonName(entry.Dn);
        var display = Split(o.DisplayNameAttributes).Select(a => Value(attrs, a)).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? userName;
        var groups = new HashSet<string>(Values(attrs, "memberOf"), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(o.GroupBaseDn))
        {
            var filter = o.GroupFilter.Replace("{0}", EscapeFilter(entry.Dn), StringComparison.Ordinal);
            List<LdapEntry> found;
            try
            {
                found = await SearchAsync(conn, o.GroupBaseDn.Trim(), LdapConnection.ScopeSub, filter, ["1.1"], ct);
            }
            catch (LdapException ex) when (ex.ResultCode == LdapException.NoSuchObject)
            {
                // A typo in the settings, not the person gone: nobody may be judged by it.
                throw new LdapUnavailableException($"The directory at {o.Url} cannot be used: {GroupBaseMissing(o)}.", ex);
            }
            foreach (var g in found)
            {
                groups.Add(g.Dn);
            }
        }
        return new LdapPerson(entry.Dn, userName.ToLowerInvariant(), Value(attrs, o.EmailAttribute)?.ToLowerInvariant(), display, [.. groups]);
    }

    /// <summary>Why a person's groups cannot be read: "Where groups are" is not there.</summary>
    internal static string GroupBaseMissing(LdapOptions o) =>
        $"\"{o.GroupBaseDn?.Trim()}\" (where groups are) does not exist, or the service account cannot see it: fix \"Where groups are\" in the Settings page";

    /// <summary>
    /// Why the service account cannot be used as set, before trying: a name with no password. Many
    /// servers would take that as an anonymous connection and say yes, which is never what was meant.
    /// </summary>
    internal static string? ServiceAccountProblem(LdapOptions o) =>
        !string.IsNullOrWhiteSpace(o.BindDn) && string.IsNullOrEmpty(o.BindPassword)
            ? "the service account has no password (it was never saved, or it no longer reads because APP_KEY changed): type it again in the Settings page"
            : null;

    /// <summary>
    /// Signs in as the service account, or anonymously when there is none. Never with a password
    /// and no name: servers refuse that as wrong credentials (a saved password left behind).
    /// </summary>
    internal static Task BindServiceAsync(LdapConnection conn, LdapOptions o, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(o.BindDn) ? conn.BindAsync("", "", ct) : conn.BindAsync(o.BindDn.Trim(), o.BindPassword, ct);

    private static async Task<T> WithServiceAsync<T>(LdapOptions o, Func<LdapConnection, Task<T>> work, CancellationToken ct)
    {
        if (ServiceAccountProblem(o) is { } problem)
        {
            throw new LdapUnavailableException($"The directory at {o.Url} cannot be used: {problem}.");
        }
        var seen = new Seen();
        try
        {
            using var conn = await ConnectAsync(o, seen, ct);
            await BindServiceAsync(conn, o, ct);
            return await work(conn);
        }
        catch (Exception ex) when (LdapErrors.IsDirectoryFailure(ex))
        {
            // Any failure here is the directory's or its settings' (the service account refused, a
            // search refused, a certificate refused), never a person's: nobody is judged by it.
            throw new LdapUnavailableException($"The directory at {o.Url} cannot be used: {LdapErrors.Describe(o, ex, seen)}", ex);
        }
    }

    /// <summary>What the TLS handshake saw, to say why it failed: the server's certificate refused, and why; a client certificate asked for.</summary>
    internal sealed class Seen
    {
        public List<string>? Certificate { get; set; }

        /// <summary>The server asked for a certificate of this app's own, which it has none of.</summary>
        public bool ClientCertificateAsked { get; set; }
    }

    internal static bool IsLdaps(Uri uri) => uri.Scheme.Equals("ldaps", StringComparison.OrdinalIgnoreCase);

    internal static int PortOf(Uri uri) => uri.IsDefaultPort || uri.Port <= 0 ? (IsLdaps(uri) ? 636 : 389) : uri.Port;

    /// <summary>Connects (with TLS for ldaps://), then StartTLS when it is asked for.</summary>
    internal static async Task<LdapConnection> ConnectAsync(LdapOptions o, Seen seen, CancellationToken ct)
    {
        var conn = await OpenAsync(o, seen, ct);
        try
        {
            if (o.StartTls && !IsLdaps(new Uri(o.Url!.Trim())))
            {
                await conn.StartTlsAsync(ct);
            }
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    /// <summary>The connection, with TLS for ldaps:// and the certificate checked as the settings say; no StartTLS yet.</summary>
    internal static async Task<LdapConnection> OpenAsync(LdapOptions o, Seen seen, CancellationToken ct)
    {
        var uri = new Uri(o.Url!.Trim());
        var host = uri.IdnHost;
        var opts = new LdapConnectionOptions();
        if (IsLdaps(uri))
        {
            opts = opts.UseSsl();
        }
        var cas = string.IsNullOrWhiteSpace(o.CaCertificate) ? null : ServerTls.TrustedCas(o.CaCertificate);
        opts = opts.ConfigureRemoteCertificateValidationCallback((_, certificate, chain, errors) =>
        {
            if (o.IgnoreCertificateErrors)
            {
                return true; // Opt-in for test directories only (Ldap:IgnoreCertificateErrors); the sync logs a warning.
            }
            var leaf = certificate is null ? null : certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            var problems = ServerTls.Problems(cas is null ? TlsCheck.System : TlsCheck.OwnCa, cas, leaf, chain, errors, host);
            // The system's CAs decide by the errors; the reasons only word them.
            var trusted = cas is null ? errors == SslPolicyErrors.None : problems.Count == 0;
            if (!trusted)
            {
                seen.Certificate = problems.Count > 0 ? problems : [$"It is not trusted ({errors})."];
            }
            return trusted;
        });
        // Called when the server asks for a client certificate (with its own already in hand): none is given, but it is noted.
        opts = opts.ConfigureLocalCertificateSelectionCallback((_, _, _, remote, _) =>
        {
            if (remote is not null)
            {
                seen.ClientCertificateAsked = true;
            }
            return null!;
        });
        var conn = new LdapConnection(opts) { ConnectionTimeout = 10_000 };
        try
        {
            await conn.ConnectAsync(host, PortOf(uri), ct);
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    /// <summary>The entries a search finds, up to <paramref name="most"/>; referrals to other servers are not followed.</summary>
    internal static async Task<List<LdapEntry>> SearchAsync(LdapConnection conn, string baseDn, int scope, string filter, string[]? attributes, CancellationToken ct, int most = 1000)
    {
        var constraints = new LdapSearchConstraints { MaxResults = most };
        var results = await conn.SearchAsync(baseDn, scope, filter, attributes, false, constraints, ct);
        var list = new List<LdapEntry>();
        while (await results.HasMoreAsync(ct))
        {
            try
            {
                list.Add(await results.NextAsync(ct));
            }
            catch (LdapReferralException)
            {
                // Referrals to other servers are not followed.
            }
        }
        return list;
    }

    private static string? Value(LdapAttributeSet attrs, string name) =>
        attrs.TryGetValue(name, out var a) ? a.StringValue : null;

    private static string[] Values(LdapAttributeSet attrs, string name) =>
        attrs.TryGetValue(name, out var a) ? a.StringValueArray : [];

    private static string[] Split(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>cn=llm-admins,ou=groups,dc=example,dc=com -> llm-admins; CN=Smith\, Jane,OU=Staff,... -> Smith, Jane.</summary>
    public static string CommonName(string dn)
    {
        var first = FirstPart(dn);
        var eq = first.IndexOf('=', StringComparison.Ordinal);
        return eq >= 0 ? Unescape(first[(eq + 1)..].Trim()) : dn;
    }

    /// <summary>The first attribute=value of a DN, up to the first comma that is not escaped.</summary>
    private static string FirstPart(string dn)
    {
        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '\\')
            {
                i++;
            }
            else if (dn[i] is ',' or ';')
            {
                return dn[..i];
            }
        }
        return dn;
    }

    /// <summary>RFC 4514's escapes in a DN value: \, and \2C alike.</summary>
    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }
        // Hex escapes are UTF-8 bytes (\C3\A9 is é): collected as bytes, then read as text.
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                if (i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
                {
                    bytes.Add(byte.Parse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 2;
                    continue;
                }
                i++; // \, \+ \" and the like: the character itself
            }
            var length = char.IsHighSurrogate(value[i]) && i + 1 < value.Length ? 2 : 1;
            bytes.AddRange(Encoding.UTF8.GetBytes(value.Substring(i, length)));
            i += length - 1;
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }

    /// <summary>RFC 4515: the characters that would change a filter's meaning.</summary>
    public static string EscapeFilter(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            _ = c switch
            {
                '\\' => sb.Append(@"\5c"),
                '*' => sb.Append(@"\2a"),
                '(' => sb.Append(@"\28"),
                ')' => sb.Append(@"\29"),
                '\0' => sb.Append(@"\00"),
                _ => sb.Append(c),
            };
        }
        return sb.ToString();
    }
}
