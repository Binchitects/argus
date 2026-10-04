using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ArenaCode;

/// <summary>
/// The one HttpClient every request goes through: the system's trust store, plus
/// a CA of the company's own when one is given (--ca, ARENA_CA_CERT, the config,
/// SSL_CERT_FILE). Proxies come from HTTPS_PROXY / NO_PROXY as .NET reads them.
/// </summary>
internal static class Net
{
    public static string UserAgent => $"arena-code/{Cli.Version}";

    /// <summary>The CA file to use: --ca first, then ARENA_CA_CERT, the config, SSL_CERT_FILE.</summary>
    public static string? CaFile(string? option, string? configured, Func<string, string?> env) =>
        new[] { option, env("ARENA_CA_CERT"), configured, env("SSL_CERT_FILE") }.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f));

    public static HttpClient Client(string? caFile)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (caFile is { Length: > 0 })
        {
            var roots = LoadCertificates(caFile);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) => Trusted(certificate, chain, errors, roots);
        }
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("arena-code", Cli.Version));
        return http;
    }

    /// <summary>Every certificate in a PEM bundle, or the one certificate of a DER file.</summary>
    public static X509Certificate2Collection LoadCertificates(string file)
    {
        if (!File.Exists(file))
        {
            throw new FileNotFoundException($"The CA file {file} does not exist.");
        }
        var certificates = new X509Certificate2Collection();
        var text = File.ReadAllText(file);
        if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
        {
            certificates.ImportFromPem(text);
        }
        else
        {
            certificates.Add(X509CertificateLoader.LoadCertificateFromFile(file));
        }
        if (certificates.Count == 0)
        {
            throw new CryptographicException($"{file} holds no certificate.");
        }
        return certificates;
    }

    /// <summary>
    /// Trusted when the system trusts it, or when its chain ends at one of the
    /// given roots. A certificate for another name is never trusted.
    /// </summary>
    public static bool Trusted(X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors, X509Certificate2Collection roots)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return false;
        }
        var leaf = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (presented is not null)
        {
            foreach (var element in presented.ChainElements)
            {
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }
        return chain.Build(leaf);
    }

    /// <summary>What went wrong reaching an address, in words a person can act on.</summary>
    public static string Explain(Exception e, string url)
    {
        for (var inner = e; inner is not null; inner = inner.InnerException)
        {
            switch (inner)
            {
                case AuthenticationException:
                    return $"The certificate of {url} is not trusted. If your Arena's certificate comes from your company's own CA, " +
                           "give that CA's file once: arena-code login --ca ca.crt (or set ARENA_CA_CERT).";
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData }:
                    return $"The name in {url} does not resolve. Check the address, your DNS or VPN.";
                case SocketException { SocketErrorCode: SocketError.ConnectionRefused }:
                    return $"Nothing answers at {url} (connection refused).";
                case SocketException s:
                    return $"Could not reach {url}: {s.Message}";
                case TimeoutException or TaskCanceledException:
                    return $"{url} did not answer in time. Behind a proxy? Set HTTPS_PROXY (and NO_PROXY for your Arena when it is inside).";
            }
        }
        return $"Could not reach {url}: {e.Message}";
    }
}
