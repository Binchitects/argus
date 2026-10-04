using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Llm.Api.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.Downloads;

/// <summary>Configuration section "Certificates": where the site's certificate is read.</summary>
public sealed class CertificateOptions
{
    /// <summary>The CA's certificate (PEM) as a file the app can read (Helm: from a Secret); otherwise the certificate the site serves is read.</summary>
    public string? CaFile { get; set; }

    /// <summary>Where the site's TLS is served inside the network (Traefik), as host:port; its chain is read there with the site's name.</summary>
    public string Probe { get; set; } = "traefik:443";
}

/// <summary>
/// The certificate people's tools must trust to reach the site: none when a public CA vouches for it
/// (Trusted), else the CA it chains to (deploy/scripts/make-cert.sh's, or a company's), offered for
/// download. Available is false when the certificate cannot be read.
/// </summary>
public sealed record SiteCertificateInfo(bool Available, bool Trusted, string? Subject, string? Issuer, DateTimeOffset? Expires, string? Sha256, string? Pem)
{
    public static readonly SiteCertificateInfo Unknown = new(false, false, null, null, null, null, null);
}

/// <summary>
/// Reads the certificate the site serves (from Traefik, as a browser would) and picks the one to trust:
/// the root the chain ends in, or the certificate itself when it signs itself. Only public certificates
/// are read: the app never sees a private key. Known for ten minutes.
/// </summary>
public sealed partial class SiteCertificate(IOptions<CertificateOptions> options, IOptions<AuthOptions> auth, TimeProvider clock, ILogger<SiteCertificate> logger)
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);
    private (SiteCertificateInfo Info, DateTimeOffset At)? _known;

    public async Task<SiteCertificateInfo> GetAsync(CancellationToken ct)
    {
        if (_known is { } k && clock.GetUtcNow() - k.At < Fresh)
        {
            return k.Info;
        }
        SiteCertificateInfo info;
        try
        {
            info = options.Value.CaFile is { Length: > 0 } file ? FromFile(file) : await FromSiteAsync(ct);
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException or CryptographicException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogUnreadable(logger, ex.Message);
            info = SiteCertificateInfo.Unknown;
        }
        _known = (info, clock.GetUtcNow());
        return info;
    }

    /// <summary>A CA file: trusted when the system's own roots already vouch for it.</summary>
    private static SiteCertificateInfo FromFile(string file)
    {
        if (!File.Exists(file))
        {
            return SiteCertificateInfo.Unknown;
        }
        var certificates = new X509Certificate2Collection();
        certificates.ImportFromPemFile(file);
        if (certificates.Count == 0)
        {
            return SiteCertificateInfo.Unknown;
        }
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ExtraStore.AddRange(certificates);
        var trusted = chain.Build(certificates[0]);
        return Describe([.. chain.ChainElements.Select(e => e.Certificate)], trusted);
    }

    /// <summary>The chain the site serves, read over TLS with the site's name; trusted when it validates against the system's roots.</summary>
    private async Task<SiteCertificateInfo> FromSiteAsync(CancellationToken ct)
    {
        var probe = options.Value.Probe;
        var colon = probe.LastIndexOf(':');
        var (host, port) = colon > 0 && int.TryParse(probe[(colon + 1)..], out var p) ? (probe[..colon], p) : (probe, 443);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, timeout.Token);
        List<X509Certificate2> served = [];
        var trusted = false;
        await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = auth.Value.Domain,
            RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
            {
                trusted = errors == SslPolicyErrors.None;
                // Copied: the chain is gone once this returns.
                served = chain is { ChainElements.Count: > 0 }
                    ? [.. chain.ChainElements.Select(e => X509CertificateLoader.LoadCertificate(e.Certificate.RawData))]
                    : certificate is null ? [] : [X509CertificateLoader.LoadCertificate(certificate.GetRawCertData())];
                return true;
            },
        }, timeout.Token);
        return served.Count == 0 ? SiteCertificateInfo.Unknown : Describe(served, trusted);
    }

    /// <summary>The certificate to trust: the last of the chain (its root when the site sends it, the certificate itself when it signs itself).</summary>
    private static SiteCertificateInfo Describe(IReadOnlyList<X509Certificate2> chain, bool trusted)
    {
        var top = chain[^1];
        var fingerprint = string.Join(':', SHA256.HashData(top.RawData).Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
        return new SiteCertificateInfo(true, trusted, top.Subject, top.Issuer, top.NotAfter.ToUniversalTime(), fingerprint, top.ExportCertificatePem());
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The site's certificate could not be read: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, string reason);
}
