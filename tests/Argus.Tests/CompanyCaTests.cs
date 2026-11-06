using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Argus.Configuration;
using Argus.Indexing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Argus.Tests;

/// <summary>A CA and a server certificate it signed, as a company's own CA issues its GitLab's.</summary>
public sealed class OwnCa : IDisposable
{
    public X509Certificate2 Ca { get; }
    readonly RSA _caKey = RSA.Create(2048);

    public OwnCa(string name = "Test Company Root CA")
    {
        var request = new CertificateRequest($"CN={name}", _caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        Ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
    }

    public string Pem => Ca.ExportCertificatePem();

    /// <summary>A server certificate for these names, with its key.</summary>
    public X509Certificate2 Server(string host = "localhost")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Ca, true, false));
        using var signed = request.Create(Ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(12));
        // Kestrel wants a key it can use on every platform: through PKCS#12.
        return X509CertificateLoader.LoadPkcs12(signed.CopyWithPrivateKey(key).Export(X509ContentType.Pkcs12), null);
    }

    public void Dispose()
    {
        Ca.Dispose();
        _caKey.Dispose();
    }
}

/// <summary>A GitLab-like server over TLS with a certificate of its own CA: GitLab's API answers who the token is.</summary>
public sealed class TlsGitLab : IAsyncDisposable
{
    readonly WebApplication _app;
    public string Url { get; }

    public TlsGitLab(X509Certificate2 certificate)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificate)));
        _app = b.Build();
        _app.MapGet("/api/v4/user", () => Results.Text("""{"id": 7, "username": "svc", "state": "active"}""", "application/json"));
        _app.StartAsync().GetAwaiter().GetResult();
        Url = _app.Urls.First().Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// A GitLab on the company's own CA: Argus's API client and its git trust the CA the stack is given (its install file
/// or Settings, as the app writes it), live, and nothing else that the system does not trust.
/// </summary>
[Collection("process-state")]
public sealed class CompanyCaTests
{
    [Fact]
    [Trait("Control", "NET-01")]
    public async Task GitLab_on_a_company_CA_is_reached_once_the_stack_is_given_the_CA_and_without_a_restart()
    {
        using var dir = new TempDir();
        using var ca = new OwnCa();
        using var cert = ca.Server();
        await using var gitlab = new TlsGitLab(cert);
        var extra = Path.Combine(dir.Path, "company-ca.pem");
        using var env = new EnvScope((Tls.ExtraCaEnv, extra));
        var cfg = GitLabConfig.Create(gitlab.Url, "svc");
        // One client for the whole test, as Argus's shared ones live: it must see the CA given after it was made.
        using var client = Tls.ClientFor(cfg, 5);

        // Before: the connection is closed, as people saw it.
        var refused = Assert.ThrowsAny<HttpRequestException>(() => Tls.Get(client, gitlab.Url + "/api/v4/user"));
        Assert.IsType<System.Security.Authentication.AuthenticationException>(refused.InnerException);
        Assert.False(Tls.GitEnv(cfg, new Dictionary<string, string?>()).ContainsKey("GIT_SSL_CAINFO"));

        // The CA given (the app writes it there from Settings): trusted at once.
        File.WriteAllText(extra, ca.Pem);
        Assert.Equal(200, Tls.Get(client, gitlab.Url + "/api/v4/user").Status);

        // git is given one bundle: the system's CAs and the company's.
        var git = Tls.GitEnv(cfg, new Dictionary<string, string?>(), dir.Path);
        var bundle = File.ReadAllText(git["GIT_SSL_CAINFO"]!);
        Assert.Contains(ca.Pem.Trim(), bundle.Replace("\r\n", "\n"), StringComparison.Ordinal);

        // Another CA's certificate is still refused: the company CA is added, nothing is let through.
        using var other = new OwnCa("Someone Else's CA");
        using var otherCert = other.Server();
        await using var impostor = new TlsGitLab(otherCert);
        Assert.ThrowsAny<HttpRequestException>(() => Tls.Get(client, impostor.Url + "/api/v4/user"));

        // Taken away: refused again.
        File.WriteAllText(extra, "");
        File.SetLastWriteTimeUtc(extra, DateTime.UtcNow.AddSeconds(5));
        using var fresh = Tls.ClientFor(cfg, 5);
        Assert.ThrowsAny<HttpRequestException>(() => Tls.Get(fresh, gitlab.Url + "/api/v4/user"));
    }

    [Fact]
    public async Task Git_clones_from_a_GitLab_on_a_company_CA_with_the_bundle()
    {
        using var dir = new TempDir();
        using var ca = new OwnCa();
        using var cert = ca.Server();
        await using var gitlab = new TlsGitLab(cert);
        var extra = dir.File("company-ca.pem", ca.Pem);
        using var env = new EnvScope((Tls.ExtraCaEnv, extra));
        var cfg = GitLabConfig.Create(gitlab.Url, "svc");

        // git reaches the server (no repository there: not found), rather than refusing its certificate.
        string Said(IDictionary<string, string?> e) => Mirror.RunGit(dir.Path, ["ls-remote", gitlab.Url + "/group/project.git"], e).Stderr;
        Assert.Contains("certificate", Said(new Dictionary<string, string?>()), StringComparison.OrdinalIgnoreCase);
        var trusted = Said(Tls.GitEnv(cfg, new Dictionary<string, string?>(), dir.Path));
        Assert.DoesNotContain("certificate", trusted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not found", trusted, StringComparison.Ordinal);
    }
}
