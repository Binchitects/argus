using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Llm.Api.Identity;
using Llm.Core.Chat;

namespace Llm.Api.Chat.Tools;

/// <summary>A server's certificate as it was refused: who it is for, who issued it, and why it is not trusted (for the admin's test).</summary>
public sealed record CertificateProblem(string Subject, string Issuer, IReadOnlyList<string> Names, DateTimeOffset NotBefore, DateTimeOffset NotAfter, bool SelfSigned,
    string Sha256, IReadOnlyList<string> Reasons);

/// <summary>
/// Thrown by a certificate check that refuses the server's certificate, so a refusal is told apart
/// from a handshake that failed for another reason (an old TLS version, no cipher in common).
/// </summary>
public sealed class CertificateRefusedException() : AuthenticationException("The server's certificate was refused.");

/// <summary>
/// How an admin's MCP server or API is trusted over HTTPS: by the system's CAs (the default), by
/// a CA the admin gave (its chain must lead there, and the host name must still match), or not
/// checked at all. Only these servers' own connections are touched: the gateway, Argus, webhooks
/// and the web tool keep the strict check.
/// </summary>
public static class ServerTls
{
    /// <summary>Longest CA text kept: a few certificates of a bundle.</summary>
    public const int MaxCaChars = 64_000;

    private static readonly Oid ServerAuth = new("1.3.6.1.5.5.7.3.1");

    /// <summary>The message people see in the chat when the certificate is refused; the admin's test (Test, or Read it for an API) says why.</summary>
    public static string Untrusted(string server, string test = "Test") =>
        $"{server}'s certificate is not trusted. An admin can trust the CA that signed it, or stop checking it, in Admin → Tools ({test} says why).";

    /// <summary>
    /// A request refused because of the server's certificate (not a server down, a name that does not
    /// resolve, or a handshake that failed for another reason): the checks throw <see cref="CertificateRefusedException"/>.
    /// </summary>
    public static bool IsCertificateError(Exception ex) => ex is HttpRequestException { InnerException: CertificateRefusedException };

    /// <summary>
    /// The system's CAs decide, as without a callback; a refusal throws, so that it is told apart
    /// (<see cref="IsCertificateError"/>). For the MCP clients' shared connections (<see cref="Mcp.Handler"/>).
    /// </summary>
    public static bool Strict(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        errors == SslPolicyErrors.None ? true : throw new CertificateRefusedException();

    /// <summary>
    /// What people are told when no secure connection could be set up with a server for another
    /// reason than its certificate (an old TLS version, no cipher in common); null for any other failure.
    /// </summary>
    public static string? HandshakeFailed(string server, Exception ex)
    {
        if (ex is not HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } || IsCertificateError(ex))
        {
            return null;
        }
        var cause = ex;
        while (cause.InnerException is { } inner)
        {
            cause = inner;
        }
        return $"No secure connection could be set up with {server} ({cause.Message.Trim().TrimEnd('.')}). "
            + "Its certificate is not the reason: it may not speak https there, speak only an old TLS version, or share no cipher with this app.";
    }

    /// <summary>The certificates of a PEM text (a CA, or a bundle). Throws with why not.</summary>
    public static X509Certificate2Collection ReadCa(string pem)
    {
        var certificates = new X509Certificate2Collection();
        certificates.ImportFromPem(pem);
        return certificates.Count > 0 ? certificates : throw new CryptographicException("No certificate found.");
    }

    /// <summary>The CA's certificates for checking a server; none when they do not read (then no certificate is trusted).</summary>
    public static X509Certificate2Collection TrustedCas(string? ca)
    {
        try
        {
            return ca is null ? [] : ReadCa(ca);
        }
        catch (CryptographicException)
        {
            return [];
        }
    }

    /// <summary>Why this text cannot be the CA a server is checked against, or null.</summary>
    public static string? CheckCa(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return "Give the CA's certificate (PEM, -----BEGIN CERTIFICATE-----), pasted or from a file.";
        }
        if (pem.Length > MaxCaChars)
        {
            return $"The CA's certificate is too long (up to {MaxCaChars / 1000} KB).";
        }
        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            return "This holds a private key: give only the CA's certificate (the -----BEGIN CERTIFICATE----- part). A key is never stored here.";
        }
        try
        {
            ReadCa(pem);
            return null;
        }
        catch (CryptographicException)
        {
            return "No certificate reads from this: paste the CA's certificate in PEM (-----BEGIN CERTIFICATE----- … -----END CERTIFICATE-----).";
        }
    }

    /// <summary>
    /// Puts the admin's choice on the server, checked: a CA given with "trust this CA" (or the one
    /// kept), none otherwise. Null choice: unchanged (a new CA alone replaces the one kept). Why not, or null.
    /// </summary>
    public static string? Choose(McpServer server, TlsCheck? tls, string? ca)
    {
        var mode = tls ?? server.Tls;
        if (!Enum.IsDefined(mode))
        {
            return "The certificate check is System, OwnCa or Off.";
        }
        if (mode != TlsCheck.OwnCa)
        {
            server.Tls = mode;
            server.TlsCa = null;
            return null;
        }
        var pem = string.IsNullOrWhiteSpace(ca) ? server.TlsCa : ca.Trim();
        if (CheckCa(pem) is { } bad)
        {
            return bad;
        }
        server.Tls = mode;
        server.TlsCa = pem;
        return null;
    }

    /// <summary>For the audit log: the choice in words, a CA by its name and fingerprint (never the PEM itself).</summary>
    public static string Describe(TlsCheck tls, string? ca) => tls switch
    {
        TlsCheck.Off => "certificate NOT checked (any certificate accepted)",
        TlsCheck.OwnCa => "certificate checked against its own CA: " + string.Join("; ", TrustedCas(ca).Select(c => $"{c.Subject} (SHA-256 {Fingerprint(c)})")),
        _ => "certificate checked against the system's CAs",
    };

    /// <summary>Writes the choice to the audit log when it is new or changed (tool.server_tls), as plain words.</summary>
    public static async Task AuditAsync(Audit audit, McpServer server, TlsCheck before, string? beforeCa)
    {
        if (server.Tls != before || server.TlsCa != beforeCa)
        {
            await audit.WriteAsync("tool.server_tls", server.Name, detail: Describe(server.Tls, server.TlsCa));
        }
    }

    /// <summary>The CAs' names, for the admin's card.</summary>
    public static IReadOnlyList<string> CaNames(string? ca) => [.. TrustedCas(ca).Select(c => c.GetNameInfo(X509NameType.SimpleName, forIssuer: false))];

    /// <summary>"AB:CD:…": the certificate's SHA-256, as browsers show it.</summary>
    public static string Fingerprint(X509Certificate2 certificate) =>
        string.Join(':', SHA256.HashData(certificate.RawData).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>
    /// What is wrong with the certificate a server presented, under the admin's choice; empty when it
    /// is trusted. With its own CA, the chain must lead to one of <paramref name="cas"/> (the system's
    /// roots do not count; a CA given that is not a root, such as the issuing CA alone, is trusted as
    /// far as it goes) and the name must match; nothing is checked when the check is off.
    /// </summary>
    /// <param name="host">The host name asked for, for the message.</param>
    public static List<string> Problems(TlsCheck mode, X509Certificate2Collection? cas, X509Certificate2? certificate, X509Chain? presented, SslPolicyErrors errors, string host)
    {
        List<string> reasons = [];
        if (mode == TlsCheck.Off)
        {
            return reasons;
        }
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            reasons.Add("It sent no certificate.");
            return reasons;
        }
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            reasons.Add($"It is for {string.Join(", ", Names(certificate))}, not {host}.");
        }
        if (mode == TlsCheck.System)
        {
            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            {
                reasons.AddRange(ChainReasons(certificate, presented?.ChainStatus ?? [], null));
            }
            return [.. reasons.Distinct()];
        }
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        if (cas is not null)
        {
            chain.ChainPolicy.CustomTrustStore.AddRange(cas);
        }
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(ServerAuth);
        if (presented is not null)
        {
            // What the server sent besides its own certificate (its intermediates).
            chain.ChainPolicy.ExtraStore.AddRange(presented.ChainPolicy.ExtraStore);
            chain.ChainPolicy.ExtraStore.AddRange(presented.ChainElements.Skip(1).Select(e => e.Certificate).ToArray());
        }
        if (!chain.Build(certificate) && !Reaches(chain, cas))
        {
            reasons.AddRange(ChainReasons(certificate, chain.ChainStatus, cas));
        }
        return [.. reasons.Distinct()];
    }

    /// <summary>
    /// The chain reaches a certificate the admin gave, every link up to it sound, and stops short only
    /// above it: a company's issuing CA given without its root (a custom trust store takes only roots
    /// as anchors), or a server's own certificate.
    /// </summary>
    private static bool Reaches(X509Chain chain, X509Certificate2Collection? cas)
    {
        const X509ChainStatusFlags AboveIt = X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain;
        if (cas is not { Count: > 0 } || chain.ChainStatus.Any(s => (s.Status & ~AboveIt) != 0))
        {
            return false;
        }
        foreach (var element in chain.ChainElements)
        {
            if (cas.Any(c => c.RawData.AsSpan().SequenceEqual(element.Certificate.RawData)))
            {
                return true;
            }
            if (element.ChainElementStatus.Length > 0)
            {
                return false;
            }
        }
        return false;
    }

    private static IEnumerable<string> ChainReasons(X509Certificate2 certificate, X509ChainStatus[] statuses, X509Certificate2Collection? cas)
    {
        var now = DateTimeOffset.UtcNow;
        var selfSigned = certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
        var issuer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
        foreach (var status in statuses.Select(s => s.Status).Distinct())
        {
            yield return status switch
            {
                X509ChainStatusFlags.UntrustedRoot or X509ChainStatusFlags.PartialChain when cas is { Count: > 0 } =>
                    $"It does not lead to the CA you gave ({string.Join(", ", cas.Select(c => c.GetNameInfo(X509NameType.SimpleName, forIssuer: false)))}): it was issued by {issuer}.",
                X509ChainStatusFlags.UntrustedRoot or X509ChainStatusFlags.PartialChain => selfSigned
                    ? "It is self-signed: no CA this server trusts vouches for it."
                    : $"It was issued by {issuer}, a CA this server does not trust.",
                X509ChainStatusFlags.NotTimeValid when certificate.NotAfter.ToUniversalTime() < now => $"It expired on {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}.",
                X509ChainStatusFlags.NotTimeValid when certificate.NotBefore.ToUniversalTime() > now => $"It is not valid before {certificate.NotBefore.ToUniversalTime():yyyy-MM-dd}.",
                X509ChainStatusFlags.NotTimeValid => "A CA certificate in its chain has expired.",
                X509ChainStatusFlags.Revoked => "It has been revoked.",
                X509ChainStatusFlags.NotValidForUsage => "It is not meant for a server (its key usage).",
                X509ChainStatusFlags.NotSignatureValid => "Its signature does not check out.",
                _ => statuses.First(s => s.Status == status).StatusInformation.Trim() is { Length: > 0 } info ? info : status.ToString(),
            };
        }
    }

    /// <summary>The names a certificate is for: its DNS names and addresses, else its common name.</summary>
    public static IReadOnlyList<string> Names(X509Certificate2 certificate)
    {
        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(e => e.EnumerateDnsNames().Concat(e.EnumerateIPAddresses().Select(a => a.ToString()))).Distinct().ToList();
        return names.Count > 0 ? names : [certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false)];
    }

    /// <summary>
    /// The MCP clients' connections (<see cref="Mcp.Handler"/>), with this check of the server's certificate.
    /// With its own CA or no check, a redirect is followed only on the same host: the choice is for that
    /// server, so a redirect to another host comes back as it is (not followed, nothing sent there).
    /// </summary>
    public static HttpMessageHandler Handler(TlsCheck mode, X509Certificate2Collection? cas)
    {
        var handler = Mcp.Handler();
        if (mode == TlsCheck.System)
        {
            return handler;
        }
        if (mode == TlsCheck.Off)
        {
#pragma warning disable CA5359 // Only for a server an admin marked "do not check" (Admin → Tools, audited); the form says what that gives up.
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
        }
        else
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                Problems(mode, cas, certificate is null ? null : certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()),
                    chain, errors, "").Count == 0 ? true : throw new CertificateRefusedException();
        }
        var most = handler.MaxAutomaticRedirections;
        handler.AllowAutoRedirect = false;
        return new SameHostRedirects(handler, most);
    }

    /// <summary>
    /// Follows a server's redirects as the system's client does (a 303, and a 301 or 302 after a POST,
    /// become a GET; never from https to http), but only on the host first asked: any other comes back as it is.
    /// </summary>
    private sealed class SameHostRedirects(HttpMessageHandler inner, int most) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.IdnHost;
            for (var hops = 0; ; hops++)
            {
                var response = await base.SendAsync(request, cancellationToken);
                var status = (int)response.StatusCode;
                var from = request.RequestUri!;
                if (hops >= most || status is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location
                    || !Uri.TryCreate(from, location, out var next) || next.Scheme is not ("http" or "https")
                    || !string.Equals(next.IdnHost, host, StringComparison.OrdinalIgnoreCase)
                    || (from.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps))
                {
                    return response;
                }
                response.Dispose();
                if ((status == 303 && request.Method != HttpMethod.Head) || (status is 301 or 302 && request.Method == HttpMethod.Post))
                {
                    request.Method = HttpMethod.Get;
                    request.Content = null;
                }
                request.RequestUri = next;
            }
        }
    }

    /// <summary>
    /// Connects to the server as the check would, to say why its certificate was refused: who it is
    /// for, who issued it, its dates and the reasons. Null when it cannot be read (not https, not reachable).
    /// </summary>
    public static async Task<CertificateProblem?> ProbeAsync(Uri url, TlsCheck mode, string? ca, CancellationToken ct)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }
        var cas = mode == TlsCheck.OwnCa ? TrustedCas(ca) : null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        CertificateProblem? found = null;
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(url.IdnHost, url.Port, timeout.Token);
            await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = url.IdnHost,
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                {
                    if (certificate is not null)
                    {
                        // Copied: the chain is gone once this returns.
                        var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                        var reasons = Problems(mode, cas, leaf, chain, errors, url.IdnHost);
                        found = new CertificateProblem(leaf.Subject, leaf.Issuer, Names(leaf), leaf.NotBefore.ToUniversalTime(), leaf.NotAfter.ToUniversalTime(),
                            leaf.SubjectName.RawData.AsSpan().SequenceEqual(leaf.IssuerName.RawData), Fingerprint(leaf),
                            reasons.Count > 0 ? reasons : ["It was refused when connecting, though it checks out now: test again."]);
                    }
                    // Nothing is sent: the details are all this was for.
                    return false;
                },
            }, timeout.Token);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Refused (as expected), or it could not be read: whatever was seen is the answer.
        }
        return found;
    }
}

/// <summary>
/// The connection to each admin's server, by its certificate check: the shared MCP client when the
/// system's CAs decide (never changed), else one handler per server and choice, kept while the choice
/// stands and made anew when it changes. One left behind is not disposed: a long call may still be on it,
/// and its connections close when idle.
/// </summary>
public static class ServerClients
{
    private sealed record Entry(string Choice, HttpClient Client);

    private static readonly ConcurrentDictionary<Guid, Entry> Clients = new();

    /// <summary>The server's own connection.</summary>
    public static HttpClient For(IHttpClientFactory factory, McpServer server)
    {
        if (server.Tls == TlsCheck.System)
        {
            Clients.TryRemove(server.Id, out _);
            return factory.CreateClient(ToolRegistry.McpClient);
        }
        var choice = $"{server.Tls}:{(server.TlsCa is { } ca ? Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ca))) : "")}";
        if (Clients.TryGetValue(server.Id, out var known) && known.Choice == choice)
        {
            return known.Client;
        }
        var made = new Entry(choice, Once(factory, server.Tls, server.TlsCa));
        return Clients.AddOrUpdate(server.Id, made, (_, now) => now.Choice == choice ? now : made).Client;
    }

    /// <summary>A connection with this choice for one use (a test, a document fetched before saving): the caller disposes it.</summary>
    public static HttpClient Once(IHttpClientFactory factory, TlsCheck tls, string? ca) => tls == TlsCheck.System
        ? factory.CreateClient(ToolRegistry.McpClient)
        : new HttpClient(ServerTls.Handler(tls, tls == TlsCheck.OwnCa ? ServerTls.TrustedCas(ca) : null)) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>A server removed: its connection goes too.</summary>
    public static void Forget(Guid id) => Clients.TryRemove(id, out _);
}
