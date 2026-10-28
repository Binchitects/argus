using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CodeArena.Tests;

/// <summary>Code Arena's updates from its Arena: a newer build said at the start (once a day), and code-arena update putting it in place.</summary>
public sealed class UpdateTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    [Fact]
    public void Versions_compare_by_their_numbers_and_a_pre_release_comes_before_its_release()
    {
        Assert.True(Updates.Newer("7.0.0", "6.9.0"));
        Assert.True(Updates.Newer("6.10.0", "6.9.3"));
        Assert.False(Updates.Newer("6.9.0", "6.9.0"));
        Assert.False(Updates.Newer("6.8.9", "6.9.0"));
        Assert.True(Updates.Newer("7.0.0", "7.0.0-rc.1"));
        Assert.False(Updates.Newer("7.0.0-rc.1", "7.0.0"));
        Assert.False(Updates.Newer("not a version", "6.9.0"));
    }

    [Fact]
    public async Task Update_downloads_the_newer_build_checks_it_and_puts_it_in_place()
    {
        using var h = new Harness(_gateway, _mcp);
        var exe = Path.Combine(h.Root, "bin", OperatingSystem.IsWindows() ? "code-arena.exe" : "code-arena");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "the old one");
        _mcp.Build = ("99.0.0", "the new one"u8.ToArray(), null);
        Assert.Equal(0, await h.RunUpdate(exe));
        Assert.Contains("→ 99.0.0: the next start runs it.", h.Out, StringComparison.Ordinal);
        Assert.Equal("the new one", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".new"));

        // Not what Arena lists: nothing changed.
        _mcp.Build = ("99.0.1", "tampered"u8.ToArray(), new string('0', 64));
        Assert.Equal(1, await h.RunUpdate(exe));
        Assert.Contains("did not match what Arena lists", h.Err, StringComparison.Ordinal);
        Assert.Equal("the new one", File.ReadAllText(exe));

        // Nothing newer: said so.
        _mcp.Build = ("0.0.1", "older"u8.ToArray(), null);
        Assert.Equal(0, await h.RunUpdate(exe));
        Assert.Contains("is the newest this Arena has", h.Out, StringComparison.Ordinal);

        // Run from its source: not replaced.
        Assert.Equal(1, await h.RunUpdate(Path.Combine(h.Root, "dotnet")));
        Assert.Contains("runs from its source", h.Err, StringComparison.Ordinal);
    }

    /// <summary>A CA, and a certificate it signs for code signing (or, with <paramref name="usage"/>, for something else).</summary>
    private static (X509Certificate2 Ca, X509Certificate2 Leaf) Pki(string usage = "1.3.6.1.5.5.7.3.3")
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Code Arena (test)", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(6), [1, 2, 3, 4]);
        return (ca, issued.CopyWithPrivateKey(key));
    }

    [Fact]
    public void A_signed_build_is_the_one_whose_list_its_CAs_code_signing_certificate_signed()
    {
        var (ca, leaf) = Pki();
        var caFile = Path.Combine(Path.GetTempPath(), $"ca-{Guid.NewGuid():N}.crt");
        File.WriteAllText(caFile, ca.ExportCertificatePem());
        try
        {
            var sum = new string('a', 64);
            var sums = Encoding.ASCII.GetBytes($"{new string('b', 64)}  linux-x64/code-arena\n{sum}  win-x64/code-arena.exe\n");
            var signature = leaf.GetRSAPrivateKey()!.SignData(sums, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var pem = leaf.ExportCertificatePem() + "\n" + ca.ExportCertificatePem();
            Assert.Null(Updates.SignatureProblem(sums, signature, pem, "win-x64/code-arena.exe", sum, caFile));

            // A list changed after it was signed, a build not in it, another build's sum.
            var changed = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(sums).Replace(sum, new string('c', 64), StringComparison.Ordinal));
            Assert.Contains("not the one its certificate signed", Updates.SignatureProblem(changed, signature, pem, "win-x64/code-arena.exe", new string('c', 64), caFile));
            Assert.Contains("is not in the list signed", Updates.SignatureProblem(sums, signature, pem, "osx-arm64/code-arena", sum, caFile));
            Assert.Contains("is not in the list signed", Updates.SignatureProblem(sums, signature, pem, "win-x64/code-arena.exe", new string('b', 64), caFile));

            // A CA this machine does not trust for Arena (its own CA bundled is not trust), or a certificate not for code signing.
            var (otherCa, _) = Pki();
            var otherFile = caFile + ".other";
            File.WriteAllText(otherFile, otherCa.ExportCertificatePem());
            Assert.Contains("from a CA this machine trusts", Updates.SignatureProblem(sums, signature, pem, "win-x64/code-arena.exe", sum, otherFile));
            File.Delete(otherFile);
            var (serverCa, server) = Pki("1.3.6.1.5.5.7.3.1");
            File.WriteAllText(caFile, serverCa.ExportCertificatePem());
            var serverSigned = server.GetRSAPrivateKey()!.SignData(sums, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            Assert.Contains("for code signing", Updates.SignatureProblem(sums, serverSigned, server.ExportCertificatePem(), "win-x64/code-arena.exe", sum, caFile));
        }
        finally
        {
            File.Delete(caFile);
        }
    }

    [Fact]
    public async Task A_session_says_a_newer_build_once_a_day_and_not_for_a_one_shot_answer()
    {
        using var h = new Harness(_gateway, _mcp);
        _mcp.Build = ("99.0.0", "new"u8.ToArray(), null);
        var config = new Config { Url = _mcp.BaseUrl };
        using var http = new HttpClient();
        var now = DateTimeOffset.UtcNow;
        Assert.Equal($"Code Arena 99.0.0 is available from Arena (this is {Cli.Version}): code-arena update installs it.",
            await Updates.NoticeAsync(config, http, h.Paths, now, CancellationToken.None));
        // Within the day: said again without asking Arena (which now has nothing newer).
        _mcp.Build = ("0.0.1", "old"u8.ToArray(), null);
        Assert.NotNull(await Updates.NoticeAsync(config, http, h.Paths, now.AddHours(2), CancellationToken.None));
        Assert.Null(await Updates.NoticeAsync(config, http, h.Paths, now.AddDays(1.1), CancellationToken.None));

        _gateway.Answer = _ => Reply.Say("ok");
        _mcp.Build = ("99.0.0", "new"u8.ToArray(), null);
        File.Delete(Path.Combine(h.Paths.DataDir, "update-check.json"));
        Assert.Equal(0, await h.Run("", "-p", "hello"));
        Assert.DoesNotContain("is available from Arena", h.Out + h.Err, StringComparison.Ordinal);
    }
}
