using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Llm.Tests;

/// <summary>Arena Code's builds and the site's certificate, served by the app for the Connect your tools page.</summary>
[Collection(nameof(AppCollection))]
public sealed class DownloadTests(AppFixture app)
{
    [Fact]
    public async Task Arena_Code_builds_are_listed_with_their_checksums_and_downloaded_by_system_without_signing_in()
    {
        var dir = Directory.CreateTempSubdirectory("downloads-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "arena-code", "linux-x64"));
            Directory.CreateDirectory(Path.Combine(dir, "arena-code", "win-x64"));
            await File.WriteAllTextAsync(Path.Combine(dir, "arena-code", "linux-x64", "arena-code"), "ELF pretend");
            await File.WriteAllTextAsync(Path.Combine(dir, "arena-code", "win-x64", "arena-code.exe"), "MZ pretend!");
            await File.WriteAllTextAsync(Path.Combine(dir, "arena-code", "SHA256SUMS"), "abc123  linux-x64/arena-code\ndef456  win-x64/arena-code.exe\n");
            await using var f = app.Create(app.ConnectionStringFor("downloads_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
                new Dictionary<string, string?> { ["Downloads:Directory"] = dir });
            using var http = f.CreateClient();

            var list = JsonDocument.Parse(await http.GetStringAsync("/api/downloads/arena-code")).RootElement;
            var builds = list.GetProperty("builds").EnumerateArray().ToList();
            Assert.Equal(["linux-x64", "win-x64"], builds.Select(b => b.GetProperty("rid").GetString()));
            Assert.Equal("Windows (x64)", builds[1].GetProperty("system").GetString());
            Assert.Equal("arena-code.exe", builds[1].GetProperty("fileName").GetString());
            Assert.Equal(11, builds[1].GetProperty("size").GetInt64());
            Assert.Equal("abc123", builds[0].GetProperty("sha256").GetString());

            using var res = await http.GetAsync("/api/downloads/arena-code/win-x64");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("arena-code.exe", res.Content.Headers.ContentDisposition?.FileName);
            Assert.Equal("MZ pretend!", await res.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/downloads/arena-code/osx-arm64")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/downloads/arena-code/..%2F..%2Fetc")).StatusCode);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task An_image_without_builds_lists_none()
    {
        using var http = app.Factory.CreateClient();
        var list = JsonDocument.Parse(await http.GetStringAsync("/api/downloads/arena-code")).RootElement;
        Assert.Empty(list.GetProperty("builds").EnumerateArray());
        Assert.False(string.IsNullOrEmpty(list.GetProperty("version").GetString()));
    }

    /// <summary>A CA (under a root of its own) and a certificate for the site signed by it.</summary>
    private static (X509Certificate2 Ca, X509Certificate2 Leaf) PrivateCa(string domain)
    {
        static X509Certificate2 Issue(string subject, X509Certificate2? issuer, bool ca, out RSA key, string? dns = null)
        {
            key = RSA.Create(2048);
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (ca)
            {
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            }
            if (dns is not null)
            {
                var names = new SubjectAlternativeNameBuilder();
                names.AddDnsName(dns);
                request.CertificateExtensions.Add(names.Build());
            }
            // Each ends a year before its issuer: made a second later, it must not outlive it.
            var (from, to) = (DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(issuer is null ? 10 : ca ? 2 : 1));
            if (issuer is null)
            {
                return request.CreateSelfSigned(from, to);
            }
            using var signed = request.Create(issuer, from, to, RandomNumberGenerator.GetBytes(8));
            return X509CertificateLoader.LoadPkcs12(signed.CopyWithPrivateKey(key).Export(X509ContentType.Pfx), null);
        }
        var root = Issue("CN=Test root", null, true, out var rootKey);
        var ca = Issue("CN=Test CA (" + domain + ")", root, true, out var caKey);
        var leaf = Issue("CN=" + domain, ca, false, out var leafKey, domain);
        rootKey.Dispose(); caKey.Dispose(); leafKey.Dispose();
        return (X509CertificateLoader.LoadCertificate(ca.RawData), leaf);
    }

    [Fact]
    public async Task A_private_CA_is_offered_for_download_with_its_fingerprint_read_from_what_the_site_serves()
    {
        var (ca, leaf) = PrivateCa(AppFixture.Domain);
        // The site's TLS (Traefik's part here): the certificate and the CA it chains to.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var context = SslStreamCertificateContext.Create(leaf, [ca], offline: true);
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                await using var tls = new SslStream(client.GetStream());
                try
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = context }, stop.Token);
                }
                catch (IOException)
                {
                    // the app hangs up once it has the chain
                }
            }
        });
        await using var f = app.Create(app.ConnectionStringFor("certificate_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Certificates:Probe"] = $"127.0.0.1:{port}" });
        using var http = f.CreateClient();

        var info = JsonDocument.Parse(await http.GetStringAsync("/api/downloads/certificate")).RootElement;
        Assert.True(info.GetProperty("available").GetBoolean());
        Assert.False(info.GetProperty("trusted").GetBoolean());
        Assert.Equal(ca.Subject, info.GetProperty("subject").GetString());
        Assert.Equal(string.Join(':', SHA256.HashData(ca.RawData).Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))), info.GetProperty("sha256").GetString());

        using var res = await http.GetAsync("/api/downloads/certificate/ca.crt");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal($"{AppFixture.Domain}-ca.crt", res.Content.Headers.ContentDisposition?.FileName);
        var pem = await res.Content.ReadAsStringAsync();
        Assert.Equal(ca.RawData, X509Certificate2.CreateFromPem(pem).RawData);
        Assert.DoesNotContain("PRIVATE KEY", pem, StringComparison.Ordinal);
        await stop.CancelAsync();
    }

    [Fact]
    public async Task The_CA_comes_from_a_file_when_one_is_given_and_nothing_is_offered_when_it_cannot_be_read()
    {
        var (ca, _) = PrivateCa(AppFixture.Domain);
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, ca.ExportCertificatePem());
            await using (var f = app.Create(app.ConnectionStringFor("cafile_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
                new Dictionary<string, string?> { ["Certificates:CaFile"] = file }))
            {
                var info = JsonDocument.Parse(await f.CreateClient().GetStringAsync("/api/downloads/certificate")).RootElement;
                Assert.True(info.GetProperty("available").GetBoolean());
                Assert.False(info.GetProperty("trusted").GetBoolean());
                Assert.Equal(ca.Subject, info.GetProperty("subject").GetString());
            }
            // Nothing listening: no certificate to offer, and the page shows no card.
            await using var none = app.Create(app.ConnectionStringFor("nocert_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
                new Dictionary<string, string?> { ["Certificates:Probe"] = "127.0.0.1:1" });
            using var http = none.CreateClient();
            Assert.False(JsonDocument.Parse(await http.GetStringAsync("/api/downloads/certificate")).RootElement.GetProperty("available").GetBoolean());
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/downloads/certificate/ca.crt")).StatusCode);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
