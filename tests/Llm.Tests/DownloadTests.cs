using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Llm.Tests;

/// <summary>Code Arena's builds and the site's certificate, served by the app for the Connect your tools page.</summary>
[Collection(nameof(AppCollection))]
public sealed class DownloadTests(AppFixture app)
{
    [Fact]
    public async Task Code_Arena_builds_are_listed_with_their_checksums_and_downloaded_by_system_without_signing_in()
    {
        var dir = Directory.CreateTempSubdirectory("downloads-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "code-arena", "linux-x64"));
            Directory.CreateDirectory(Path.Combine(dir, "code-arena", "win-x64"));
            await File.WriteAllTextAsync(Path.Combine(dir, "code-arena", "linux-x64", "code-arena"), "ELF pretend");
            await File.WriteAllTextAsync(Path.Combine(dir, "code-arena", "win-x64", "code-arena.exe"), "MZ pretend!");
            await File.WriteAllTextAsync(Path.Combine(dir, "code-arena", "SHA256SUMS"), "abc123  linux-x64/code-arena\ndef456  win-x64/code-arena.exe\n");
            await using var f = app.Create(app.ConnectionStringFor("downloads_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
                new Dictionary<string, string?> { ["Downloads:Directory"] = dir });
            using var http = f.CreateClient();

            var list = JsonDocument.Parse(await http.GetStringAsync("/api/downloads/code-arena")).RootElement;
            var builds = list.GetProperty("builds").EnumerateArray().ToList();
            Assert.Equal(["linux-x64", "win-x64"], builds.Select(b => b.GetProperty("rid").GetString()));
            Assert.Equal("Windows (x64)", builds[1].GetProperty("system").GetString());
            Assert.Equal("code-arena.exe", builds[1].GetProperty("fileName").GetString());
            Assert.Equal(11, builds[1].GetProperty("size").GetInt64());
            Assert.Equal("abc123", builds[0].GetProperty("sha256").GetString());

            using var res = await http.GetAsync("/api/downloads/code-arena/win-x64");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("code-arena.exe", res.Content.Headers.ContentDisposition?.FileName);
            Assert.Equal("MZ pretend!", await res.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/downloads/code-arena/osx-arm64")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/downloads/code-arena/..%2F..%2Fetc")).StatusCode);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task One_line_installs_the_build_for_the_machine_checked_against_its_checksum_and_refuses_one_that_does_not_match()
    {
        var dir = Directory.CreateTempSubdirectory("install-").FullName;
        try
        {
            // The downloads as the app serves them, and as file:// gives them to curl here (the script's address put there).
            var served = Path.Combine(dir, "served", "api", "downloads", "code-arena");
            Directory.CreateDirectory(Path.Combine(dir, "code-arena", "linux-x64"));
            Directory.CreateDirectory(Path.Combine(dir, "code-arena", "win-x64"));
            Directory.CreateDirectory(Path.Combine(dir, "code-arena", "win-arm64"));
            Directory.CreateDirectory(served);
            await File.WriteAllTextAsync(Path.Combine(dir, "code-arena", "linux-x64", "code-arena"), "#!/bin/sh\necho code-arena 9.9.9\n");
            await File.WriteAllTextAsync(Path.Combine(dir, "code-arena", "win-x64", "code-arena.exe"), "MZ pretend!");
            await File.WriteAllTextAsync(Path.Combine(dir, "code-arena", "win-arm64", "code-arena.exe"), "MZ pretend, on Arm!");
            File.Copy(Path.Combine(dir, "code-arena", "linux-x64", "code-arena"), Path.Combine(served, "linux-x64"));
            var sum = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(dir, "code-arena", "linux-x64", "code-arena"))));
            await File.WriteAllTextAsync(Path.Combine(dir, "code-arena", "SHA256SUMS"), $"{sum}  linux-x64/code-arena\ndef456  win-x64/code-arena.exe\n789abc  win-arm64/code-arena.exe\n");
            await using var f = app.Create(app.ConnectionStringFor("install_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
                new Dictionary<string, string?> { ["Downloads:Directory"] = dir });
            using var http = f.CreateClient();

            var sh = await http.GetStringAsync("/api/downloads/code-arena/install.sh");
            Assert.Contains($"arena=\"https://{AppFixture.Domain}\"", sh, StringComparison.Ordinal);
            Assert.Contains($"  linux-x64) sum={sum} ;;", sh, StringComparison.Ordinal);
            Assert.DoesNotContain("def456", sh, StringComparison.Ordinal);
            var ps1 = await http.GetStringAsync("/api/downloads/code-arena/install.ps1");
            // Each Windows build's SHA-256, the one for the machine's own system chosen as it runs.
            Assert.Contains("$sums = @{ 'win-x64' = 'def456'; 'win-arm64' = '789abc' }", ps1, StringComparison.Ordinal);
            Assert.Contains("-ne $sum)", ps1, StringComparison.Ordinal);
            Assert.Contains("\"$arena/api/downloads/code-arena/$rid\"", ps1, StringComparison.Ordinal);
            Assert.Contains($"$arena = 'https://{AppFixture.Domain}'", ps1, StringComparison.Ordinal);

            if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            {
                return;
            }
            async Task<(int Code, string Out)> RunAsync(string script)
            {
                var file = Path.Combine(dir, "install.sh");
                await File.WriteAllTextAsync(file, script.Replace($"https://{AppFixture.Domain}", "file://" + Path.Combine(dir, "served"), StringComparison.Ordinal));
                var start = new System.Diagnostics.ProcessStartInfo("sh", file) { RedirectStandardOutput = true, RedirectStandardError = true };
                start.Environment["CODE_ARENA_DIR"] = Path.Combine(dir, "bin");
                // A home of its own: the desktop entry goes there, never into the home of whoever runs the tests.
                start.Environment["HOME"] = Path.Combine(dir, "home");
                start.Environment["XDG_DATA_HOME"] = Path.Combine(dir, "home", "share");
                using var run = System.Diagnostics.Process.Start(start)!;
                var said = await run.StandardOutput.ReadToEndAsync() + await run.StandardError.ReadToEndAsync();
                await run.WaitForExitAsync();
                return (run.ExitCode, said);
            }
            var (code, output) = await RunAsync(sh);
            Assert.True(code == 0, output);
            Assert.Contains("Next, sign in: code-arena login --url", output, StringComparison.Ordinal);
            Assert.Contains($"export PATH=\"{Path.Combine(dir, "bin")}:$PATH\"", output, StringComparison.Ordinal);
            Assert.Equal("#!/bin/sh\necho code-arena 9.9.9\n", await File.ReadAllTextAsync(Path.Combine(dir, "bin", "code-arena")));
            Assert.True(File.GetUnixFileMode(Path.Combine(dir, "bin", "code-arena")).HasFlag(UnixFileMode.UserExecute));

            // A build that is not what the list says is not installed.
            File.Delete(Path.Combine(dir, "bin", "code-arena"));
            (code, output) = await RunAsync(sh.Replace(sum, new string('0', 64), StringComparison.Ordinal));
            Assert.Equal(1, code);
            Assert.Contains("does not match its SHA-256: nothing installed", output, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(dir, "bin", "code-arena")));

            // install.ps1 as Windows on Arm runs it (PowerShell is in the SDK's image; the downloads answered from here): the
            // ARM64 build is the one fetched and checked against its own SHA-256.
            if (File.Exists("/usr/bin/pwsh"))
            {
                var fetched = Path.Combine(dir, "fetched.txt");
                var mocked = $$"""
                    function Invoke-WebRequest { param([string]$Uri, [string]$OutFile, [switch]$UseBasicParsing) Add-Content -Path '{{fetched}}' -Value $Uri; Set-Content -Path $OutFile -Value 'MZ' -NoNewline }
                    $env:LOCALAPPDATA = '{{dir}}'
                    """ + ps1;
                await File.WriteAllTextAsync(Path.Combine(dir, "install.ps1"), mocked);
                var start = new System.Diagnostics.ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-File", Path.Combine(dir, "install.ps1")]) { RedirectStandardOutput = true, RedirectStandardError = true };
                start.Environment["PROCESSOR_ARCHITECTURE"] = "ARM64";
                using var run = System.Diagnostics.Process.Start(start)!;
                var said = await run.StandardOutput.ReadToEndAsync() + await run.StandardError.ReadToEndAsync();
                await run.WaitForExitAsync();
                Assert.Contains("for Windows (win-arm64)", said, StringComparison.Ordinal);
                Assert.Equal([$"https://{AppFixture.Domain}/api/downloads/code-arena/win-arm64"], await File.ReadAllLinesAsync(fetched));
                // The pretend build is not what the list says: nothing installed.
                Assert.Contains("does not match its SHA-256", said, StringComparison.Ordinal);
                Assert.False(File.Exists(Path.Combine(dir, "Programs", "code-arena", "code-arena.exe")));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// "After starting code arena a shell window is open": on Windows the IDE's launcher (code-arena-ide.exe, a GUI program)
    /// is served beside code-arena.exe, listed with its own SHA-256, installed with it, checked the same way, and the
    /// desktop's and the Start menu's shortcuts open it; code-arena.exe stays the terminals'.
    /// </summary>
    [Fact]
    public async Task On_Windows_the_IDE_launcher_is_served_beside_the_build_installed_with_it_and_the_shortcuts_open_it()
    {
        var dir = Directory.CreateTempSubdirectory("launcher-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "code-arena", "win-x64"));
            Directory.CreateDirectory(Path.Combine(dir, "code-arena", "win-arm64"));
            // What the mocked download below writes for each file.
            var mz = Convert.ToHexStringLower(SHA256.HashData("MZ"u8.ToArray()));
            await File.WriteAllTextAsync(Path.Combine(dir, "code-arena", "win-x64", "code-arena.exe"), "MZ");
            await File.WriteAllTextAsync(Path.Combine(dir, "code-arena", "win-x64", "code-arena-ide.exe"), "MZ GUI");
            await File.WriteAllTextAsync(Path.Combine(dir, "code-arena", "win-arm64", "code-arena.exe"), "MZ");
            await File.WriteAllTextAsync(Path.Combine(dir, "code-arena", "SHA256SUMS"), $"{mz}  win-arm64/code-arena.exe\n{mz}  win-x64/code-arena-ide.exe\n{mz}  win-x64/code-arena.exe\n");
            await using var f = app.Create(app.ConnectionStringFor("launcher_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
                new Dictionary<string, string?> { ["Downloads:Directory"] = dir });
            using var http = f.CreateClient();

            var builds = JsonDocument.Parse(await http.GetStringAsync("/api/downloads/code-arena")).RootElement.GetProperty("builds").EnumerateArray().ToList();
            var x64 = builds.Single(b => b.GetProperty("rid").GetString() == "win-x64").GetProperty("ide");
            Assert.Equal(("code-arena-ide.exe", 6L, mz), (x64.GetProperty("fileName").GetString(), x64.GetProperty("size").GetInt64(), x64.GetProperty("sha256").GetString()));
            Assert.False(x64.TryGetProperty("path", out _));
            Assert.Equal(JsonValueKind.Null, builds.Single(b => b.GetProperty("rid").GetString() == "win-arm64").GetProperty("ide").ValueKind);
            using (var res = await http.GetAsync("/api/downloads/code-arena/win-x64/ide"))
            {
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
                Assert.Equal("code-arena-ide.exe", res.Content.Headers.ContentDisposition?.FileName);
                Assert.Equal("MZ GUI", await res.Content.ReadAsStringAsync());
            }
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/downloads/code-arena/win-arm64/ide")).StatusCode);

            var ps1 = await http.GetStringAsync("/api/downloads/code-arena/install.ps1");
            Assert.Contains($"$ides = @{{ 'win-x64' = '{mz}' }}", ps1, StringComparison.Ordinal);
            if (!File.Exists("/usr/bin/pwsh"))
            {
                return;
            }
            // install.ps1 as Windows runs it, the downloads and the shortcuts' COM object answered from here.
            var fetched = Path.Combine(dir, "fetched.txt");
            var links = Path.Combine(dir, "links.txt");
            var registry = Path.Combine(dir, "registry.txt");
            var mocked = $$"""
                function Invoke-WebRequest { param([string]$Uri, [string]$OutFile, [switch]$UseBasicParsing) Add-Content -Path '{{fetched}}' -Value $Uri; Set-Content -Path $OutFile -Value 'MZ' -NoNewline }
                function New-Item { param([string]$Path, [switch]$Force, [string]$ItemType)
                  if ($Path -like 'HKCU:*') { Add-Content -Path '{{registry}}' -Value "key $Path"; return }
                  Microsoft.PowerShell.Management\New-Item @PSBoundParameters }
                function Set-ItemProperty { param([string]$Path, [string]$Name, $Value, [string]$Type) Add-Content -Path '{{registry}}' -Value "$Path|$Name|$Value" }
                function New-Object { param([string]$ComObject)
                  $shell = [pscustomobject]@{}
                  $shell | Add-Member ScriptMethod CreateShortcut { param($path)
                    $link = [pscustomobject]@{ Path = $path; TargetPath = ''; WorkingDirectory = ''; IconLocation = ''; Description = '' }
                    $link | Add-Member ScriptMethod Save { Add-Content -Path '{{links}}' -Value "$($this.Path)|$($this.TargetPath)|$($this.IconLocation)" }
                    return $link }
                  return $shell }
                $env:LOCALAPPDATA = '{{dir}}'
                """ + ps1;
            await File.WriteAllTextAsync(Path.Combine(dir, "install.ps1"), mocked);
            var start = new System.Diagnostics.ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-File", Path.Combine(dir, "install.ps1")]) { RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["PROCESSOR_ARCHITECTURE"] = "AMD64";
            start.Environment["HOME"] = dir;
            // A desktop to put a shortcut on (here only as a folder there: .NET names none that is not there).
            Directory.CreateDirectory(Path.Combine(dir, "Desktop"));
            using var run = System.Diagnostics.Process.Start(start)!;
            var said = await run.StandardOutput.ReadToEndAsync() + await run.StandardError.ReadToEndAsync();
            await run.WaitForExitAsync();
            Assert.True(run.ExitCode == 0, said);
            Assert.Equal([$"https://{AppFixture.Domain}/api/downloads/code-arena/win-x64", $"https://{AppFixture.Domain}/api/downloads/code-arena/win-x64/ide"],
                await File.ReadAllLinesAsync(fetched));
            var installed = Path.Combine(dir, "Programs", "code-arena");
            Assert.True(File.Exists(Path.Combine(installed, "code-arena.exe")));
            Assert.True(File.Exists(Path.Combine(installed, "code-arena-ide.exe")));
            var made = await File.ReadAllLinesAsync(links);
            Assert.NotEmpty(made);
            Assert.All(made, l => Assert.EndsWith($"Code Arena.lnk|{Path.Combine(installed, "code-arena-ide.exe")}|{Path.Combine(installed, "code-arena.exe")},0", l));
            Assert.Contains("open the IDE with no console window", said, StringComparison.Ordinal);
            // An application as Windows lists them: its uninstaller, and "Open in Code Arena" on folders, through the IDE's launcher.
            var keys = await File.ReadAllLinesAsync(registry);
            var exe = Path.Combine(installed, "code-arena.exe");
            Assert.Contains(@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodeArena|DisplayName|Code Arena", keys);
            Assert.Contains($@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodeArena|UninstallString|""{exe}"" uninstall", keys);
            Assert.Contains($@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodeArena|Publisher|{Branding.Brand.Product}", keys);
            foreach (var menu in new[] { @"Directory\shell\CodeArena", @"Directory\Background\shell\CodeArena" })
            {
                Assert.Contains($@"HKCU:\Software\Classes\{menu}|(default)|Open in Code Arena", keys);
                Assert.Contains($@"HKCU:\Software\Classes\{menu}\command|(default)|""{Path.Combine(installed, "code-arena-ide.exe")}"" -C ""%V""", keys);
            }
            Assert.Contains("Settings > Apps", said, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A CA, and a code-signing certificate it signs, as scripts/sign-code-arena.sh makes them.</summary>
    internal static (X509Certificate2 Ca, X509Certificate2 Leaf) CodeSigning(string name)
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest($"CN={name} CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=Code Arena ({name})", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.3")], false));
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(6), [5, 6, 7, 8]);
        return (ca, issued.CopyWithPrivateKey(key));
    }

    [Fact]
    public async Task Signed_builds_are_served_with_their_signature_and_installed_only_when_the_Arenas_own_CA_signed_them_whatever_the_machine_trusts()
    {
        var dir = Directory.CreateTempSubdirectory("signed-").FullName;
        try
        {
            var builds = Path.Combine(dir, "code-arena");
            var signed = Path.Combine(dir, "signature");
            var served = Path.Combine(dir, "served", "api", "downloads", "code-arena");
            Directory.CreateDirectory(Path.Combine(builds, "linux-x64"));
            Directory.CreateDirectory(Path.Combine(builds, "win-x64"));
            Directory.CreateDirectory(signed);
            Directory.CreateDirectory(served);
            await File.WriteAllTextAsync(Path.Combine(builds, "linux-x64", "code-arena"), "#!/bin/sh\necho code-arena 9.9.9\n");
            var sum = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(builds, "linux-x64", "code-arena"))));
            await File.WriteAllTextAsync(Path.Combine(builds, "win-x64", "code-arena.exe"), "MZ pretend!");
            var imageWinSum = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(builds, "win-x64", "code-arena.exe"))));
            var image = Encoding.ASCII.GetBytes($"{sum}  linux-x64/code-arena\n{imageWinSum}  win-x64/code-arena.exe\n");
            await File.WriteAllBytesAsync(Path.Combine(builds, "SHA256SUMS"), image);
            // As sign-code-arena.sh leaves it: the Windows build Authenticode-signed beside the list, which has its SHA-256.
            Directory.CreateDirectory(Path.Combine(signed, "win-x64"));
            var signedExe = Encoding.ASCII.GetBytes("MZ pretend, signed!");
            await File.WriteAllBytesAsync(Path.Combine(signed, "win-x64", "code-arena.exe"), signedExe);
            var winSum = Convert.ToHexStringLower(SHA256.HashData(signedExe));
            var sums = Encoding.ASCII.GetBytes($"{sum}  linux-x64/code-arena\n{winSum}  win-x64/code-arena.exe\n");
            var (ca, leaf) = CodeSigning("test");
            await File.WriteAllBytesAsync(Path.Combine(signed, "SHA256SUMS.image"), image);
            await File.WriteAllBytesAsync(Path.Combine(signed, "SHA256SUMS"), sums);
            await File.WriteAllBytesAsync(Path.Combine(signed, "SHA256SUMS.sig"), leaf.GetRSAPrivateKey()!.SignData(sums, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            await File.WriteAllTextAsync(Path.Combine(signed, "code-signing.crt"), leaf.ExportCertificatePem() + "\n" + ca.ExportCertificatePem() + "\n");
            await using var f = app.Create(app.ConnectionStringFor("signed_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
                new Dictionary<string, string?> { ["Downloads:Directory"] = dir, ["Downloads:SignatureDirectory"] = signed });
            using var http = f.CreateClient();

            var list = JsonDocument.Parse(await http.GetStringAsync("/api/downloads/code-arena")).RootElement;
            Assert.Equal(("Code Arena (test)", "test CA"), (list.GetProperty("signature").GetProperty("signer").GetString(), list.GetProperty("signature").GetProperty("issuer").GetString()));
            Assert.Equal(sums, await http.GetByteArrayAsync("/api/downloads/code-arena/SHA256SUMS"));
            Assert.Equal(winSum, list.GetProperty("builds").EnumerateArray().Single(b => b.GetProperty("rid").GetString() == "win-x64").GetProperty("sha256").GetString());
            Assert.Equal(signedExe, await http.GetByteArrayAsync("/api/downloads/code-arena/win-x64"));
            Assert.False(list.GetProperty("builds")[0].TryGetProperty("path", out _));
            foreach (var file in new[] { "SHA256SUMS", "SHA256SUMS.sig", "code-signing.crt" })
            {
                await File.WriteAllBytesAsync(Path.Combine(served, file), await http.GetByteArrayAsync($"/api/downloads/code-arena/{file}"));
            }
            File.Copy(Path.Combine(builds, "linux-x64", "code-arena"), Path.Combine(served, "linux-x64"));
            var sh = await http.GetStringAsync("/api/downloads/code-arena/install.sh");
            Assert.Contains("is not the one this Arena's CA signed", sh, StringComparison.Ordinal);
            var ps1 = await http.GetStringAsync("/api/downloads/code-arena/install.ps1");
            Assert.Contains("[System.Security.Cryptography.X509Certificates.X509Chain]::new()", ps1, StringComparison.Ordinal);
            Assert.Contains($"$sums = @{{ 'win-x64' = '{winSum}' }}", ps1, StringComparison.Ordinal);
            Assert.Contains("-contains \"$sum  $rid/code-arena.exe\")", ps1, StringComparison.Ordinal);

            // The scripts carry the Arena's signing CA, the root of code-signing.crt: they check against it alone, and keep it.
            Assert.Contains(ca.ExportCertificatePem().Trim(), sh, StringComparison.Ordinal);
            Assert.Contains(ca.ExportCertificatePem().Trim(), ps1, StringComparison.Ordinal);
            Assert.Contains("X509RevocationMode]::NoCheck", ps1, StringComparison.Ordinal);
            var (otherCa, otherLeaf) = CodeSigning("other");
            async Task SignWithAsync(X509Certificate2 by, X509Certificate2 itsCa)
            {
                await File.WriteAllBytesAsync(Path.Combine(served, "SHA256SUMS.sig"), by.GetRSAPrivateKey()!.SignData(sums, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                await File.WriteAllTextAsync(Path.Combine(served, "code-signing.crt"), by.ExportCertificatePem() + "\n" + itsCa.ExportCertificatePem() + "\n");
            }

            if (OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64
                && File.Exists("/usr/bin/openssl"))
            {
                async Task<(int Code, string Out)> RunAsync(X509Certificate2? trusted)
                {
                    var file = Path.Combine(dir, "install.sh");
                    await File.WriteAllTextAsync(file, sh.Replace($"https://{AppFixture.Domain}", "file://" + Path.Combine(dir, "served"), StringComparison.Ordinal));
                    var start = new System.Diagnostics.ProcessStartInfo("sh", file) { RedirectStandardOutput = true, RedirectStandardError = true };
                    start.Environment["CODE_ARENA_DIR"] = Path.Combine(dir, "bin");
                    // A home of its own: the desktop entry goes there, never into the home of whoever runs the tests.
                    start.Environment["HOME"] = Path.Combine(dir, "home");
                    start.Environment["XDG_DATA_HOME"] = Path.Combine(dir, "home", "share");
                    if (trusted is not null)
                    {
                        await File.WriteAllTextAsync(Path.Combine(dir, "trusted.crt"), trusted.ExportCertificatePem() + "\n");
                        start.Environment["CODE_ARENA_CA"] = Path.Combine(dir, "trusted.crt");
                    }
                    using var run = System.Diagnostics.Process.Start(start)!;
                    var said = await run.StandardOutput.ReadToEndAsync() + await run.StandardError.ReadToEndAsync();
                    await run.WaitForExitAsync();
                    return (run.ExitCode, said);
                }
                // Thunar has an action of the person's own already: kept, Code Arena's added once however often it installs.
                Directory.CreateDirectory(Path.Combine(dir, "home", ".config", "Thunar"));
                await File.WriteAllTextAsync(Path.Combine(dir, "home", ".config", "Thunar", "uca.xml"),
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<actions>\n<action><icon>mine</icon><name>Mine</name><unique-id>1</unique-id><command>true</command><patterns>*</patterns><directories/></action>\n</actions>\n");
                // A machine that trusts nothing of this Arena's, or another CA: installed, and the Arena's CA kept beside it for updates.
                foreach (var trusted in new[] { null, otherCa })
                {
                    var (code, output) = await RunAsync(trusted);
                    Assert.True(code == 0, output);
                    Assert.Contains("Signed by", output, StringComparison.Ordinal);
                    // In the menus as an application, offered for folders, starting the IDE in the folder given.
                    var entry = await File.ReadAllTextAsync(Path.Combine(dir, "home", "share", "applications", "code-arena.desktop"));
                    Assert.Contains($"Exec=\"{Path.Combine(dir, "bin", "code-arena")}\" -C %f", entry, StringComparison.Ordinal);
                    Assert.Contains("MimeType=inode/directory;", entry, StringComparison.Ordinal);
                    Assert.Contains($"Comment=An IDE in your browser around a coding agent, on {Branding.Brand.Product}", entry, StringComparison.Ordinal);
                    Assert.Contains("in your applications menu", output, StringComparison.Ordinal);
                    // "Open in Code Arena" on folders in each file manager, each starting code-arena -C FOLDER.
                    var home = Path.Combine(dir, "home");
                    var exe = Path.Combine(dir, "bin", "code-arena");
                    var nautilus = Path.Combine(home, "share", "nautilus", "scripts", "Open in Code Arena");
                    foreach (var script in new[] { nautilus, Path.Combine(home, ".config", "caja", "scripts", "Open in Code Arena") })
                    {
                        Assert.Contains($"exec \"{exe}\" -C \"$dir\"", await File.ReadAllTextAsync(script), StringComparison.Ordinal);
                        Assert.True(File.GetUnixFileMode(script).HasFlag(UnixFileMode.UserExecute), script);
                    }
                    Assert.Contains($"[\"{exe}\", \"-C\", path]", await File.ReadAllTextAsync(Path.Combine(home, "share", "nautilus-python", "extensions", "code_arena.py")), StringComparison.Ordinal);
                    foreach (var menu in new[] { Path.Combine(home, "share", "kio", "servicemenus", "code-arena.desktop"), Path.Combine(home, "share", "kservices5", "ServiceMenus", "code-arena.desktop") })
                    {
                        Assert.Contains($"Exec=\"{exe}\" -C %f", await File.ReadAllTextAsync(menu), StringComparison.Ordinal);
                        Assert.True(File.GetUnixFileMode(menu).HasFlag(UnixFileMode.UserExecute), menu);
                    }
                    Assert.Contains($"Exec=\"{exe}\" -C %F", await File.ReadAllTextAsync(Path.Combine(home, "share", "nemo", "actions", "code-arena.nemo_action")), StringComparison.Ordinal);
                    Assert.Contains($"Exec=\"{exe}\" -C %P", await File.ReadAllTextAsync(Path.Combine(home, "share", "nemo", "actions", "code-arena-here.nemo_action")), StringComparison.Ordinal);
                    var uca = await File.ReadAllTextAsync(Path.Combine(home, ".config", "Thunar", "uca.xml"));
                    Assert.Contains($"<command>\"{exe}\" -C %f</command>", uca, StringComparison.Ordinal);
                    Assert.Contains("<action><icon>mine</icon>", uca, StringComparison.Ordinal);
                    Assert.Single(System.Text.RegularExpressions.Regex.Matches(uca, "<unique-id>code-arena</unique-id>"));
                    // The script picks the folder chosen, else the one open.
                    using (var check = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sh", ["-n", nautilus]) { RedirectStandardError = true })!)
                    {
                        await check.WaitForExitAsync();
                        Assert.Equal(0, check.ExitCode);
                    }
                    Assert.True(File.Exists(Path.Combine(dir, "bin", "code-arena")));
                    Assert.Equal(ca.ExportCertificatePem().Trim(), (await File.ReadAllTextAsync(Path.Combine(dir, "bin", "code-arena-signing-ca.pem"))).Trim());
                    Directory.Delete(Path.Combine(dir, "bin"), true);
                }

                // Signed by another CA (the address taken over), its CA sent with it, even one the machine trusts: not installed.
                await SignWithAsync(otherLeaf, otherCa);
                var (refused, said) = await RunAsync(otherCa);
                Assert.Equal(1, refused);
                Assert.Contains("is not the one this Arena's CA signed", said, StringComparison.Ordinal);
                Assert.False(File.Exists(Path.Combine(dir, "bin", "code-arena")));
                await SignWithAsync(leaf, ca);
            }

            // install.ps1 as Windows runs it (PowerShell is in the SDK's image), the downloads answered from what is served here:
            // a machine that does not trust the Arena's CA installs a build that CA signed, keeps the CA, and refuses another's.
            if (File.Exists("/usr/bin/pwsh"))
            {
                async Task<string> RunPs1Async()
                {
                    var mocked = $$"""
                        function Invoke-WebRequest { param([string]$Uri, [string]$OutFile, [switch]$UseBasicParsing) Copy-Item -Path (Join-Path '{{served}}' ($Uri -replace '^.*/api/downloads/code-arena/', '')) -Destination $OutFile }
                        function Get-AuthenticodeSignature { param($FilePath) [pscustomobject]@{ Status = 'NotSigned'; StatusMessage = 'not on Linux'; SignerCertificate = $null } }
                        $env:LOCALAPPDATA = '{{dir}}'
                        $env:CODE_ARENA_NO_SHORTCUTS = '1'
                        $env:CODE_ARENA_NO_DESKTOP = '1'
                        """ + ps1;
                    await File.WriteAllTextAsync(Path.Combine(dir, "install.ps1"), mocked);
                    var start = new System.Diagnostics.ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-File", Path.Combine(dir, "install.ps1")]) { RedirectStandardOutput = true, RedirectStandardError = true };
                    start.Environment["PROCESSOR_ARCHITECTURE"] = "AMD64";
                    using var run = System.Diagnostics.Process.Start(start)!;
                    var said = await run.StandardOutput.ReadToEndAsync() + await run.StandardError.ReadToEndAsync();
                    await run.WaitForExitAsync();
                    return said;
                }
                await File.WriteAllBytesAsync(Path.Combine(served, "win-x64"), signedExe);
                var installed = Path.Combine(dir, "Programs", "code-arena");
                var output = await RunPs1Async();
                Assert.Contains("Signed by Code Arena (test), checked.", output, StringComparison.Ordinal);
                Assert.Equal(signedExe, await File.ReadAllBytesAsync(Path.Combine(installed, "code-arena.exe")));
                Assert.Equal(ca.ExportCertificatePem().Trim(), (await File.ReadAllTextAsync(Path.Combine(installed, "code-arena-signing-ca.pem"))).Trim());
                Directory.Delete(installed, true);

                await SignWithAsync(otherLeaf, otherCa);
                output = await RunPs1Async();
                Assert.Contains("is not the one this Arena's CA signed", output, StringComparison.Ordinal);
                Assert.False(File.Exists(Path.Combine(installed, "code-arena.exe")));
                await SignWithAsync(leaf, ca);
            }

            // A signature of another list (made before an upgrade changed the builds) signs nothing: the image's builds, as they are.
            await File.WriteAllTextAsync(Path.Combine(builds, "SHA256SUMS"), $"{new string('f', 64)}  linux-x64/code-arena\n{imageWinSum}  win-x64/code-arena.exe\n");
            list = JsonDocument.Parse(await http.GetStringAsync("/api/downloads/code-arena")).RootElement;
            Assert.Equal(JsonValueKind.Null, list.GetProperty("signature").ValueKind);
            Assert.Equal("MZ pretend!", await http.GetStringAsync("/api/downloads/code-arena/win-x64"));
            Assert.Contains(imageWinSum, await http.GetStringAsync("/api/downloads/code-arena/SHA256SUMS"), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/downloads/code-arena/SHA256SUMS.sig")).StatusCode);
            Assert.Contains("does not sign its builds", await http.GetStringAsync("/api/downloads/code-arena/install.sh"), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Each_system_has_a_setup_to_download_and_open_that_installs_from_this_Arena()
    {
        await using var f = app.Create(app.ConnectionStringFor("setup_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>());
        using var http = f.CreateClient();
        var arena = $"https://{AppFixture.Domain}";
        var windows = await http.GetAsync("/api/downloads/code-arena/setup/windows");
        await StatusAssert.Is(HttpStatusCode.OK, windows);
        Assert.Equal("Install Code Arena.cmd", windows.Content.Headers.ContentDisposition?.FileNameStar ?? windows.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var cmd = await windows.Content.ReadAsStringAsync();
        // A command file: Windows' line ends, and install.ps1 of this Arena run in Windows PowerShell.
        Assert.Contains("\r\n", cmd, StringComparison.Ordinal);
        Assert.StartsWith("@echo off", cmd, StringComparison.Ordinal);
        Assert.Contains($"Invoke-RestMethod -UseBasicParsing -Uri '{arena}/api/downloads/code-arena/install.ps1' | Invoke-Expression", cmd, StringComparison.Ordinal);
        foreach (var (system, file, keepOpen) in new[] { ("macos", "Install Code Arena.command", true), ("linux", "install-code-arena.sh", false) })
        {
            var r = await http.GetAsync($"/api/downloads/code-arena/setup/{system}");
            await StatusAssert.Is(HttpStatusCode.OK, r);
            Assert.Equal(file, r.Content.Headers.ContentDisposition?.FileNameStar ?? r.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
            var sh = await r.Content.ReadAsStringAsync();
            Assert.StartsWith("#!/bin/sh", sh, StringComparison.Ordinal);
            Assert.Contains($"curl -fsSL \"$@\" \"$arena/api/downloads/code-arena/install.sh\" | sh", sh, StringComparison.Ordinal);
            Assert.Contains($"arena=\"{arena}\"", sh, StringComparison.Ordinal);
            Assert.Equal(keepOpen, sh.Contains("Press Return to close", StringComparison.Ordinal));
            if (File.Exists("/bin/sh"))
            {
                var file2 = Path.Combine(Path.GetTempPath(), $"setup-{Guid.NewGuid():N}.sh");
                await File.WriteAllTextAsync(file2, sh);
                using var check = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sh", ["-n", file2]) { RedirectStandardError = true })!;
                var said = await check.StandardError.ReadToEndAsync();
                await check.WaitForExitAsync();
                File.Delete(file2);
                Assert.True(check.ExitCode == 0, said);
            }
        }
        await StatusAssert.Is(HttpStatusCode.NotFound, await http.GetAsync("/api/downloads/code-arena/setup/beos"));

        // With the Arena's own CA, it is carried, and trusted for this Arena alone.
        var (ca, _) = CodeSigning("site");
        var pem = ca.ExportCertificatePem();
        Assert.Contains(pem.Trim(), Llm.Api.Downloads.DownloadEndpoints.SetupCmd(arena, pem), StringComparison.Ordinal);
        Assert.Contains("ServerCertificateValidationCallback", Llm.Api.Downloads.DownloadEndpoints.SetupCmd(arena, pem), StringComparison.Ordinal);
        Assert.DoesNotContain("ServerCertificateValidationCallback", Llm.Api.Downloads.DownloadEndpoints.SetupCmd(arena, null), StringComparison.Ordinal);
        var withCa = Llm.Api.Downloads.DownloadEndpoints.SetupSh(arena, pem, keepOpen: false);
        Assert.Contains(pem.Trim(), withCa, StringComparison.Ordinal);
        Assert.Contains("set -- --cacert \"$ca\"", withCa, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_image_without_builds_lists_none()
    {
        using var http = app.Factory.CreateClient();
        var list = JsonDocument.Parse(await http.GetStringAsync("/api/downloads/code-arena")).RootElement;
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
            // A certificate ends before its issuer does (a second later is refused).
            var (from, to) = (DateTimeOffset.UtcNow.AddDays(-1), issuer is null ? DateTimeOffset.UtcNow.AddYears(10) : new DateTimeOffset(issuer.NotAfter).AddDays(-1));
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
