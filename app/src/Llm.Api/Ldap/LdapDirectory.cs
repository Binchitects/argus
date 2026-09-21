using System.Text;
using Microsoft.Extensions.Options;
using Novell.Directory.Ldap;

namespace Llm.Api.Ldap;

/// <summary>A person as the directory describes them.</summary>
public sealed record LdapPerson(string Dn, string UserName, string? Email, string DisplayName, IReadOnlyList<string> Groups);

public interface ILdapDirectory
{
    bool Enabled { get; }

    /// <summary>The person, when the password is right; null for a wrong password or an unknown name.</summary>
    Task<LdapPerson?> AuthenticateAsync(string login, string password, CancellationToken ct = default);

    /// <summary>Re-reads a person for the sync; null when the entry no longer exists.</summary>
    Task<LdapPerson?> FindByDnAsync(string dn, CancellationToken ct = default);

    bool IsAdmin(LdapPerson person);
    bool IsAllowed(LdapPerson person);
}

public sealed class LdapUnavailableException(string message, Exception inner) : Exception(message, inner);

public sealed class LdapDirectory(IOptions<LdapOptions> options) : ILdapDirectory
{
    private readonly LdapOptions _o = options.Value;

    public bool Enabled => _o.Enabled;

    public async Task<LdapPerson?> AuthenticateAsync(string login, string password, CancellationToken ct = default)
    {
        // An empty password makes many servers perform an anonymous bind and
        // report success. Never let that stand in for a person's password.
        if (!Enabled || string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
        {
            return null;
        }
        return await WithServiceAsync(async conn =>
        {
            var filter = string.Format(System.Globalization.CultureInfo.InvariantCulture, _o.UserFilter, EscapeFilter(login.Trim()));
            var matches = await SearchAsync(conn, _o.UserBaseDn, LdapConnection.ScopeSub, filter, ct);
            if (matches.Count != 1)
            {
                return null; // unknown, or ambiguous: refuse rather than guess who is meant
            }
            var entry = matches[0];
            using (var user = await ConnectAsync())
            {
                try
                {
                    await user.BindAsync(entry.Dn, password);
                }
                catch (LdapException ex) when (ex.ResultCode == LdapException.InvalidCredentials)
                {
                    return null;
                }
            }
            return await ToPersonAsync(conn, entry, ct);
        });
    }

    public Task<LdapPerson?> FindByDnAsync(string dn, CancellationToken ct = default) =>
        WithServiceAsync(async conn =>
        {
            try
            {
                var matches = await SearchAsync(conn, dn, LdapConnection.ScopeBase, "(objectClass=*)", ct);
                return matches.Count == 1 ? await ToPersonAsync(conn, matches[0], ct) : null;
            }
            catch (LdapException ex) when (ex.ResultCode == LdapException.NoSuchObject)
            {
                return null;
            }
        });

    public bool IsAdmin(LdapPerson person) => InGroup(person, _o.AdminGroup);

    public bool IsAllowed(LdapPerson person) => string.IsNullOrWhiteSpace(_o.RequiredGroup) || InGroup(person, _o.RequiredGroup);

    private static bool InGroup(LdapPerson person, string? group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return false;
        }
        group = group.Trim();
        return person.Groups.Any(g =>
            string.Equals(g, group, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(CommonName(g), group, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<LdapPerson> ToPersonAsync(LdapConnection conn, LdapEntry entry, CancellationToken ct)
    {
        var attrs = entry.GetAttributeSet();
        var userName = Split(_o.UserNameAttributes).Select(a => Value(attrs, a)).FirstOrDefault(v => !string.IsNullOrEmpty(v))
            ?? CommonName(entry.Dn);
        var display = Split(_o.DisplayNameAttributes).Select(a => Value(attrs, a)).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? userName;
        var groups = new HashSet<string>(Values(attrs, "memberOf"), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(_o.GroupBaseDn))
        {
            var filter = string.Format(System.Globalization.CultureInfo.InvariantCulture, _o.GroupFilter, EscapeFilter(entry.Dn));
            foreach (var g in await SearchAsync(conn, _o.GroupBaseDn, LdapConnection.ScopeSub, filter, ct))
            {
                groups.Add(g.Dn);
            }
        }
        return new LdapPerson(entry.Dn, userName.ToLowerInvariant(), Value(attrs, _o.EmailAttribute)?.ToLowerInvariant(), display, [.. groups]);
    }

    private async Task<T> WithServiceAsync<T>(Func<LdapConnection, Task<T>> work)
    {
        try
        {
            using var conn = await ConnectAsync();
            await conn.BindAsync(_o.BindDn ?? "", _o.BindPassword ?? "");
            return await work(conn);
        }
        catch (LdapException ex) when (ex.ResultCode is LdapException.ConnectError or LdapException.ServerDown or LdapException.InvalidCredentials)
        {
            // InvalidCredentials here is the SERVICE account: a configuration problem, not a wrong user password.
            throw new LdapUnavailableException($"The directory at {_o.Url} is unavailable: {ex.Message}", ex);
        }
    }

    private async Task<LdapConnection> ConnectAsync()
    {
        var uri = new Uri(_o.Url!);
        var ssl = uri.Scheme.Equals("ldaps", StringComparison.OrdinalIgnoreCase);
        var opts = new LdapConnectionOptions();
        if (ssl)
        {
            opts = opts.UseSsl();
        }
        if (_o.IgnoreCertificateErrors)
        {
#pragma warning disable CA5359 // Opt-in for test directories only (Ldap__IgnoreCertificateErrors); startup logs a warning.
            opts = opts.ConfigureRemoteCertificateValidationCallback((_, _, _, _) => true);
#pragma warning restore CA5359
        }
        var conn = new LdapConnection(opts) { ConnectionTimeout = 10_000 };
        try
        {
            await conn.ConnectAsync(uri.Host, uri.IsDefaultPort || uri.Port <= 0 ? (ssl ? 636 : 389) : uri.Port);
            if (_o.StartTls && !ssl)
            {
                await conn.StartTlsAsync();
            }
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    private static async Task<List<LdapEntry>> SearchAsync(LdapConnection conn, string baseDn, int scope, string filter, CancellationToken ct)
    {
        var results = await conn.SearchAsync(baseDn, scope, filter, null, false, ct);
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

    /// <summary>cn=llm-admins,ou=groups,dc=example,dc=com -> llm-admins</summary>
    public static string CommonName(string dn)
    {
        var first = dn.Split(',')[0];
        var eq = first.IndexOf('=', StringComparison.Ordinal);
        return eq >= 0 ? first[(eq + 1)..].Trim() : dn;
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
