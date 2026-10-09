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

    /// <summary>The connection itself failed (not reached, closed, timed out): not an answer from the server.</summary>
    public static bool IsConnectionFailure(LdapException ex) =>
        ex.ResultCode is LdapException.ConnectError or LdapException.ServerDown or LdapException.LdapTimeout;

    /// <summary>A failure of the directory or its settings, as opposed to a bug here or a cancelled request.</summary>
    public static bool IsDirectoryFailure(Exception ex) =>
        ex is LdapException or AuthenticationException or IOException or SocketException or TimeoutException;

    /// <summary>
    /// Whether the directory refused a person's own sign-in because of them: a wrong password (with Active
    /// Directory's reasons), or an account it will not let in. Anything else (busy, unavailable, out of time,
    /// an encrypted connection wanted) is the directory's state, and never counts as a wrong guess.
    /// </summary>
    public static bool IsPersonRefusal(LdapException ex) =>
        ex.ResultCode is LdapException.InvalidCredentials or LdapException.InappropriateAuthentication or LdapException.UnwillingToPerform
            or LdapException.ConstraintViolation; // 389 Directory Server's "exceed password retry limit"

    /// <summary>Why a person's password could not be checked at all, to follow "the directory": its answer, in words.</summary>
    public static string PersonNotChecked(string dn, LdapException ex) => ex.ResultCode is LdapException.ConfidentialityRequired or LdapException.StrongAuthRequired
        ? $"wants an encrypted connection before a password is sent ({Answer(ex)}): use ldaps:// or turn on \"Use StartTLS\""
        : $"answered {Answer(ex)} when asked to check the password of {dn}";

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
    internal static string Describe(LdapOptions o, Exception ex, LdapDirectory.Seen? seen)
    {
        if (Unreached(o, ex, seen, startTls: o.StartTls) is { } unreached)
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
    internal static string? Unreached(LdapOptions o, Exception ex, LdapDirectory.Seen? seen, bool startTls)
    {
        var uri = new Uri(o.Url!.Trim());
        var ldaps = LdapDirectory.IsLdaps(uri);
        var (host, port) = (uri.IdnHost, LdapDirectory.PortOf(uri));
        var at = $"{host}:{port}";
        if (seen?.Certificate is { Count: > 0 } certificate)
        {
            return $"the server's certificate is not trusted: {string.Join(" ", certificate)} {CertificateFix(certificate, host)}";
        }
        var causes = new List<Exception>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            causes.Add(e);
        }
        if (seen?.ClientCertificateAsked == true && (causes.Any(c => c is AuthenticationException or IOException or SocketException)
            || ex is LdapException { ResultCode: LdapException.ConnectError or LdapException.ServerDown }))
        {
            // OpenLDAP's TLSVerifyClient demand: every client without a certificate of its own is cut off.
            return $"{at} asked this app for a client certificate during TLS and closed the connection without one: the directory demands one, and the app has none to give. "
                + "On OpenLDAP set TLSVerifyClient (olcTLSVerifyClient) to never or allow; on the osixia/openldap image, LDAP_TLS_VERIFY_CLIENT=never or try (its default, demand, refuses every client without one).";
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

    /// <summary>
    /// What to do about a refused certificate, by what is wrong with it (in ServerTls's words): the CA
    /// that issued it given, a certificate in date (no CA pasted here mends an expired one), or the
    /// name it is for used.
    /// </summary>
    public static string CertificateFix(IReadOnlyList<string> reasons, string host)
    {
        var name = reasons.Any(r => r.StartsWith("It is for ", StringComparison.Ordinal));
        var dates = reasons.Any(r => r.Contains(" expired on ", StringComparison.Ordinal) || r.Contains(" is not valid before ", StringComparison.Ordinal) || r.Contains("outside its dates", StringComparison.Ordinal));
        var trust = reasons.Any(r => r.Contains("self-signed", StringComparison.Ordinal) || r.Contains("does not trust", StringComparison.Ordinal) || r.Contains("does not lead to the CA you gave", StringComparison.Ordinal));
        var fixes = new List<string>();
        if (name)
        {
            fixes.Add($"Write \"Directory server\" with a name the certificate is for, or give the directory a certificate for {host}.");
        }
        if (dates)
        {
            fixes.Add(trust
                ? "Give the directory a certificate that is in date, from a CA that is in date, and paste that CA in \"Directory's CA\" (or check this machine's clock)."
                : "Give the directory a certificate that is in date, from a CA that is in date (or check this machine's clock): pasting a CA does not mend dates.");
        }
        else if (trust || !name)
        {
            fixes.Add("Paste the CA that issued it in \"Directory's CA\".");
        }
        return string.Join(" ", fixes) + " For a test server only, \"Accept any certificate\" turns the check off.";
    }
}
