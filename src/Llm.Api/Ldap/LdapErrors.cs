using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using Novell.Directory.Ldap;

namespace Llm.Api.Ldap;

/// <summary>
/// What went wrong with the directory, in plain words: the server's own message, Active
/// Directory's reason codes ("data 773"), a refused certificate, a port that does not answer.
/// </summary>
public static partial class LdapErrors
{
    /// <summary>
    /// Active Directory's reasons for refusing a sign-in, from "data 52e" in its message: all of them
    /// are "invalid credentials" to LDAP, though only 52e and 525 mean a wrong name or password.
    /// </summary>
    private static readonly Dictionary<string, string> AdReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["525"] = "there is no such account",
        ["52e"] = "the name or the password is wrong",
        ["530"] = "the account may not sign in at this time of day",
        ["531"] = "the account may not sign in from this computer",
        ["532"] = "its password has expired",
        ["533"] = "the account is disabled",
        ["534"] = "the account may not sign in this way on this computer",
        ["568"] = "the account is in too many groups to sign in",
        ["701"] = "the account has expired",
        ["773"] = "its password must be changed before it can sign in (\"User must change password at next logon\" is ticked)",
        ["775"] = "the account is locked out after too many wrong passwords",
    };

    [GeneratedRegex(@"\bdata ([0-9a-f]{2,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex AdData();

    /// <summary>The server's own message (its diagnostic text), or null when it sent none.</summary>
    public static string? ServerMessage(LdapException ex) =>
        ex.LdapErrorMessage?.Trim('\0', ' ', '\n', '\r') is { Length: > 0 } m ? m : null;

    /// <summary>Active Directory's reason for a refused sign-in, or null (another server, or no code in its message).</summary>
    public static string? AdReason(LdapException ex) =>
        ServerMessage(ex) is { } m && AdData().Match(m) is { Success: true } d
            ? AdReasons.GetValueOrDefault(d.Groups[1].Value) ?? $"Active Directory refused it (code {d.Groups[1].Value})"
            : null;

    /// <summary>Whether the message is Active Directory's (it names AcceptSecurityContext or an LdapErr).</summary>
    public static bool IsActiveDirectoryMessage(LdapException ex) =>
        ServerMessage(ex) is { } m && (m.Contains("AcceptSecurityContext", StringComparison.Ordinal) || m.Contains("LdapErr:", StringComparison.Ordinal));

    /// <summary>The connection itself failed (not reached, closed, timed out): not an answer from the server.</summary>
    public static bool IsConnectionFailure(LdapException ex) =>
        ex.ResultCode is LdapException.ConnectError or LdapException.ServerDown or LdapException.LdapTimeout;

    /// <summary>A failure of the directory or its settings, as opposed to a bug here or a cancelled request.</summary>
    public static bool IsDirectoryFailure(Exception ex) =>
        ex is LdapException or AuthenticationException or IOException or SocketException or TimeoutException;

    /// <summary>Why a person's own sign-in was refused, for the audit log and the admin's try.</summary>
    public static string PersonRefused(LdapException ex) => ex.ResultCode == LdapException.InvalidCredentials
        ? AdReason(ex) ?? "the password is wrong"
        : $"the directory answered {Answer(ex)}";

    /// <summary>The server's answer in words: its result and its own message.</summary>
    public static string Answer(LdapException ex) =>
        ServerMessage(ex) is { } m && !string.Equals(m, ex.ResultCodeToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            ? $"{ex.ResultCodeToString(CultureInfo.InvariantCulture)} ({m})"
            : ex.ResultCodeToString(CultureInfo.InvariantCulture);

    /// <summary>One sentence for a failure, without more context: for the logs, the sync and the sign-in page's admin.</summary>
    public static string Describe(LdapOptions o, Exception ex, IReadOnlyList<string>? certificate)
    {
        if (Unreached(o, ex, certificate, startTls: o.StartTls) is { } unreached)
        {
            return unreached;
        }
        if (ex is not LdapException l)
        {
            return ex.Message;
        }
        return l.ResultCode switch
        {
            LdapException.InvalidCredentials => $"the server refused the service account {o.BindDn}: {AdReason(l) ?? "the DN or the password is wrong"}.",
            LdapException.InvalidDnSyntax => $"the server says a DN is not valid ({ServerMessage(l) ?? "invalid DN"}): check the service account and where people are.",
            LdapException.NoSuchObject => $"\"{o.UserBaseDn}\" (where people are) or a group's place does not exist, or the service account cannot see it.",
            LdapException.ConfidentialityRequired or LdapException.StrongAuthRequired => "the server wants an encrypted connection: use ldaps:// or turn on Use StartTLS.",
            LdapException.InsufficientAccessRights => "the service account may not read there.",
            _ => $"the server answered {Answer(l)}.",
        };
    }

    /// <summary>
    /// Why the server could not be reached or spoken to (the address, the port, TLS, a certificate),
    /// or null when the connection worked and this is an answer of the server.
    /// </summary>
    public static string? Unreached(LdapOptions o, Exception ex, IReadOnlyList<string>? certificate, bool startTls)
    {
        var uri = new Uri(o.Url!.Trim());
        var ldaps = LdapDirectory.IsLdaps(uri);
        var (host, port) = (uri.IdnHost, LdapDirectory.PortOf(uri));
        var at = $"{host}:{port}";
        if (certificate is { Count: > 0 })
        {
            return $"the server's certificate is not trusted: {string.Join(" ", certificate)} Paste the CA that issued it in \"Directory's CA\" or, for a test server only, turn on \"Accept any certificate\".";
        }
        var causes = new List<Exception>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            causes.Add(e);
        }
        if (causes.OfType<SocketException>().FirstOrDefault() is { } s)
        {
            return s.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => $"nothing answers at {at}: check the host and the port (ldap:// is usually 389, ldaps:// 636).",
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => $"the name {host} does not resolve here: check it (the app looks it up from inside its container, where some company names may not resolve).",
                SocketError.TimedOut => $"no answer from {at}: a firewall may drop the traffic, or the address is wrong.",
                SocketError.NetworkUnreachable or SocketError.HostUnreachable => $"there is no route to {host} from here.",
                SocketError.ConnectionReset => $"{at} closed the connection: {(ldaps ? "it may not speak TLS there" : "it may want TLS there (ldaps://)")}.",
                _ => $"could not reach {at}: {s.Message}",
            };
        }
        if (causes.Any(c => c is TimeoutException) || (ex is LdapException { ResultCode: LdapException.LdapTimeout }))
        {
            return $"no answer from {at} within 10 seconds: a firewall may drop the traffic, or nothing listens there.";
        }
        if (causes.OfType<AuthenticationException>().FirstOrDefault() is { } tls)
        {
            return $"no secure connection could be set up with {at} ({tls.Message.TrimEnd('.')}).";
        }
        if (ex is LdapException { ResultCode: LdapException.ConnectError or LdapException.ServerDown } || ex is IOException)
        {
            // A connection that closed at once: usually TLS on one side only.
            return ldaps
                ? $"{at} did not answer as a TLS server: ldaps:// needs the directory's TLS port, usually 636. On 389, write ldap:// and turn on \"Use StartTLS\"."
                : startTls
                    ? $"{at} closed the connection during StartTLS: it may not offer StartTLS there, or it is an ldaps:// port (then write ldaps:// and turn StartTLS off)."
                    : $"{at} closed the connection: if it is the ldaps:// port (usually 636), write ldaps://{host}:{port}.";
        }
        return null;
    }
}
