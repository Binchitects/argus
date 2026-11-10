using System.Text;
using Llm.Api.Access;
namespace Llm.Api.Downloads;

/// <summary>
/// A build of Code Arena the app serves: its system, file name, size and SHA-256 (and, not shown, where it is); on Windows
/// the IDE's launcher beside it (code-arena-ide.exe), when the build has one.
/// </summary>
public sealed record CodeArenaBuild(string Rid, string System, string FileName, long Size, string? Sha256,
    [property: System.Text.Json.Serialization.JsonIgnore] string Path = "", CodeArenaFile? Ide = null);

/// <summary>A file beside a build: its name, size and SHA-256 (and, not shown, where it is).</summary>
public sealed record CodeArenaFile(string FileName, long Size, string? Sha256, [property: System.Text.Json.Serialization.JsonIgnore] string Path = "");

/// <summary>Who signed the builds (the list, SHA256SUMS, and the Windows builds themselves), and until when the certificate holds.</summary>
public sealed record CodeArenaSignature(string Signer, string Issuer, DateTimeOffset Expires);

/// <summary>
/// Code Arena, the coding agent people run on their own machine: one file per
/// system, put into the image at /downloads/code-arena/&lt;rid&gt;/ by
/// tools/publish-code-arena.sh (src/Llm.Api/Dockerfile). Open to anyone who
/// reaches the app, like the web's own files: a build holds no data, and does
/// nothing without a person's API key.
/// </summary>
public static class DownloadEndpoints
{
    /// <summary>The IDE's launcher beside code-arena.exe on Windows (tools/ide-launcher): no console on a double-click.</summary>
    public const string IdeLauncher = "code-arena-ide.exe";

    /// <summary>The systems it is built for, in the order the page offers them.</summary>
    public static readonly (string Rid, string System)[] Systems =
    [
        ("linux-x64", "Linux (x64)"),
        ("linux-arm64", "Linux (ARM64)"),
        ("osx-arm64", "macOS (Apple silicon)"),
        ("osx-x64", "macOS (Intel)"),
        ("win-x64", "Windows (x64)"),
        ("win-arm64", "Windows (ARM64)"),
    ];

    public static void MapDownloads(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/downloads/code-arena").AllowAnonymous("Public builds of Code Arena: nothing private.");
        g.MapGet("", List);
        // One line installs it: the build for the machine, checked against its SHA-256, on the person's PATH.
        g.MapGet("/install.sh", (IConfiguration config, Microsoft.Extensions.Options.IOptions<Identity.AuthOptions> auth) =>
            Results.Text(InstallSh($"https://{auth.Value.Domain}", Builds(config), Signature(config) is not null, SigningRoot(config)), "text/x-shellscript; charset=utf-8"));
        g.MapGet("/install.ps1", (IConfiguration config, Microsoft.Extensions.Options.IOptions<Identity.AuthOptions> auth) =>
            Results.Text(InstallPs1($"https://{auth.Value.Domain}", Builds(config), Signature(config) is not null, SigningRoot(config)), "text/plain; charset=utf-8"));
        // The builds' list and its signature (scripts/sign-code-arena.sh, on the host with the CA's key): installers and
        // code-arena update check a build against them with the CA they trust, not only against the SHA-256 listed here.
        g.MapGet("/SHA256SUMS", (IConfiguration config) => Path.Combine(Signature(config) is not null ? SignatureRoot(config) : Root(config), "SHA256SUMS") is var list
            && File.Exists(list) ? Results.File(list, "text/plain") : Results.NotFound(new { error = "No builds here." }));
        g.MapGet("/SHA256SUMS.sig", (IConfiguration config) => Signature(config) is not null
            ? Results.File(Path.Combine(SignatureRoot(config), "SHA256SUMS.sig"), "application/octet-stream") : Results.NotFound(new { error = "The builds are not signed." }));
        g.MapGet("/code-signing.crt", (IConfiguration config) => Signature(config) is not null
            ? Results.File(Path.Combine(SignatureRoot(config), "code-signing.crt"), "application/x-pem-file") : Results.NotFound(new { error = "The builds are not signed." }));
        // A file to download and open, per system: it installs Code Arena from this Arena as an application (Settings >
        // Apps on Windows, Applications on macOS, the menus on Linux), its address in it, and this Arena's CA when the site's
        // certificate is its own (trusted for this Arena alone).
        g.MapGet("/setup/{system}", async (string system, SiteCertificate site, Microsoft.Extensions.Options.IOptions<Identity.AuthOptions> auth, CancellationToken ct) =>
        {
            var arena = $"https://{auth.Value.Domain}";
            var cert = await site.GetAsync(ct);
            var ca = cert is { Trusted: false, Pem: { Length: > 0 } pem } ? pem : null;
            return system switch
            {
                "windows" => Results.File(Encoding.UTF8.GetBytes(SetupCmd(arena, ca).ReplaceLineEndings("\r\n")), "application/octet-stream", "Install Code Arena.cmd"),
                "macos" => Results.File(Encoding.UTF8.GetBytes(SetupSh(arena, ca, keepOpen: true)), "application/octet-stream", "Install Code Arena.command"),
                "linux" => Results.File(Encoding.UTF8.GetBytes(SetupSh(arena, ca, keepOpen: false)), "application/octet-stream", "install-code-arena.sh"),
                _ => Results.NotFound(new { error = "No setup for that system: windows, macos or linux." }),
            };
        });
        g.MapGet("/{rid}", Download);
        // On Windows, the IDE's launcher beside the program: a GUI program that starts it with no console window.
        g.MapGet("/{rid}/ide", (string rid, IConfiguration config) => Builds(config).FirstOrDefault(b => b.Rid == rid)?.Ide is { } ide
            ? Results.File(ide.Path, "application/octet-stream", ide.FileName, enableRangeProcessing: true)
            : Results.NotFound(new { error = $"No IDE launcher for {rid}." }));
        // The certificate people's tools must trust on a self-signed deployment: public, like the web's files.
        var c = app.MapGroup("/api/downloads/certificate").AllowAnonymous("The stack's public CA certificate.");
        c.MapGet("", async (SiteCertificate site, CancellationToken ct) =>
        {
            var i = await site.GetAsync(ct);
            return Results.Ok(new { i.Available, i.Trusted, i.Subject, i.Issuer, i.Expires, i.Sha256 });
        });
        c.MapGet("/ca.crt", async (SiteCertificate site, Microsoft.Extensions.Options.IOptions<Identity.AuthOptions> auth, CancellationToken ct) =>
            await site.GetAsync(ct) is { Pem: { } pem }
                ? Results.File(System.Text.Encoding.ASCII.GetBytes(pem), "application/x-x509-ca-cert", $"{auth.Value.Domain}-ca.crt")
                : Results.NotFound(new { error = "The site's certificate could not be read." }));
    }

    /// <summary>Downloads:Directory, /downloads in the image.</summary>
    /// <summary>Where the Code Arena builds this Arena serves are.</summary>
    public static string CodeArenaRoot(IConfiguration config) => Root(config);

    private static string Root(IConfiguration config) => Path.Combine(config["Downloads:Directory"] is { Length: > 0 } d ? d : "/downloads", "code-arena");

    /// <summary>Downloads:SignatureDirectory: what scripts/sign-code-arena.sh wrote, mounted read only.</summary>
    private static string SignatureRoot(IConfiguration config) => config["Downloads:SignatureDirectory"] is { Length: > 0 } d ? d : "/downloads-signature";

    /// <summary>
    /// Who signed the builds, when the signature there is of the image's builds as they are (SHA256SUMS.image: one made
    /// before an upgrade changed them signs nothing) and its list (SHA256SUMS, with the SHA-256 of the Authenticode-signed
    /// Windows builds beside it in place of the image's) holds for its certificate's key; null: not signed.
    /// </summary>
    /// <summary>
    /// The CA at the top of the code-signing certificate's chain (the last of code-signing.crt, when it signs itself), as
    /// PEM; null when the builds are not signed or the chain ends below a root (a public CA's: the machine's store has it).
    /// The installers carry it and check against it alone, then keep it beside the program for code-arena update: a build
    /// is taken only when this Arena's CA signed it, whether or not the machine trusts that CA, or the Arena's site.
    /// </summary>
    public static string? SigningRoot(IConfiguration config)
    {
        var crt = Path.Combine(SignatureRoot(config), "code-signing.crt");
        if (Signature(config) is null || !File.Exists(crt))
        {
            return null;
        }
        try
        {
            var certs = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection();
            certs.ImportFromPemFile(crt);
            using var top = certs[^1];
            var root = top.SubjectName.RawData.AsSpan().SequenceEqual(top.IssuerName.RawData) ? top.ExportCertificatePem() : null;
            foreach (var c in certs)
            {
                c.Dispose();
            }
            return root;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public static CodeArenaSignature? Signature(IConfiguration config)
    {
        var (image, madeFrom, list, sig, crt) = (Path.Combine(Root(config), "SHA256SUMS"), Path.Combine(SignatureRoot(config), "SHA256SUMS.image"),
            Path.Combine(SignatureRoot(config), "SHA256SUMS"), Path.Combine(SignatureRoot(config), "SHA256SUMS.sig"), Path.Combine(SignatureRoot(config), "code-signing.crt"));
        try
        {
            if (!File.Exists(image) || !File.Exists(madeFrom) || !File.Exists(list) || !File.Exists(sig) || !File.Exists(crt))
            {
                return null;
            }
            if (!File.ReadAllBytes(image).AsSpan().SequenceEqual(File.ReadAllBytes(madeFrom)))
            {
                return null;
            }
            var signed = File.ReadAllBytes(list);
            using var cert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(File.ReadAllText(crt));
            using var key = System.Security.Cryptography.X509Certificates.RSACertificateExtensions.GetRSAPublicKey(cert);
            return key is not null && key.VerifyData(signed, File.ReadAllBytes(sig), System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1)
                ? new CodeArenaSignature(cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false),
                    cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, true), cert.NotAfter.ToUniversalTime())
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The builds served: the image's, each Windows one Authenticode-signed when the builds are signed (<see cref="Signature"/>).</summary>
    public static List<CodeArenaBuild> Builds(IConfiguration config) =>
        Signature(config) is not null ? Builds(Root(config), SignatureRoot(config)) : Builds(Root(config));

    /// <summary>
    /// The builds there are, with their checksums (SHA256SUMS beside them); with <paramref name="signedRoot"/>, the signed
    /// copies there of the builds that have one, and the checksums of its list.
    /// </summary>
    public static List<CodeArenaBuild> Builds(string root, string? signedRoot = null)
    {
        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        var sumsFile = Path.Combine(signedRoot ?? root, "SHA256SUMS");
        if (File.Exists(sumsFile))
        {
            foreach (var line in File.ReadLines(sumsFile))
            {
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                {
                    sums[parts[1].TrimStart('*')] = parts[0];
                }
            }
        }
        var builds = new List<CodeArenaBuild>();
        foreach (var (rid, system) in Systems)
        {
            var name = rid.StartsWith("win-", StringComparison.Ordinal) ? "code-arena.exe" : "code-arena";
            var file = new FileInfo(Path.Combine(root, rid, name));
            if (!file.Exists)
            {
                continue;
            }
            if (signedRoot is not null && new FileInfo(Path.Combine(signedRoot, rid, name)) is { Exists: true } copy)
            {
                file = copy;
            }
            builds.Add(new CodeArenaBuild(rid, system, name, file.Length, sums.GetValueOrDefault($"{rid}/{name}"), file.FullName,
                rid.StartsWith("win-", StringComparison.Ordinal) ? Beside(rid, IdeLauncher) : null));
        }
        return builds;

        CodeArenaFile? Beside(string rid, string name)
        {
            var file = new FileInfo(Path.Combine(root, rid, name));
            if (!file.Exists)
            {
                return null;
            }
            if (signedRoot is not null && new FileInfo(Path.Combine(signedRoot, rid, name)) is { Exists: true } copy)
            {
                file = copy;
            }
            return new CodeArenaFile(name, file.Length, sums.GetValueOrDefault($"{rid}/{name}"), file.FullName);
        }
    }

    private static IResult List(IConfiguration config) =>
        Results.Ok(new { version = AppInfo.Current.Version, builds = Builds(config), signature = Signature(config) });

    /// <summary>
    /// The installer for Linux and macOS (curl -fsSL …/install.sh | sh): the build for this system, its SHA-256 checked as
    /// this Arena lists it and, when the builds are signed (<paramref name="isSigned"/>), against the list its code-signing certificate
    /// signed, that certificate from a CA the machine (or CODE_ARENA_CA) trusts; put in ~/.local/bin (CODE_ARENA_DIR), then
    /// how to sign in. CODE_ARENA_CA names the CA file on a self-signed deployment.
    /// </summary>
    public static string InstallSh(string arena, IReadOnlyList<CodeArenaBuild> builds, bool isSigned = false, string? signingRoot = null)
    {
        var sums = string.Concat(builds.Where(b => !b.Rid.StartsWith("win-", StringComparison.Ordinal) && b.Sha256 is not null)
            .Select(b => $"  {b.Rid}) sum={b.Sha256} ;;\n"));
        return $$"""
            #!/bin/sh
            # Code Arena from {{arena}}: the build for this machine, checked, on your PATH.
            set -eu
            arena="{{arena}}"
            version="{{AppInfo.Current.Version}}"
            case "$(uname -s)" in Linux) os=linux ;; Darwin) os=osx ;; *) echo "Code Arena: no build for $(uname -s): see $arena/setup" >&2; exit 1 ;; esac
            case "$(uname -m)" in x86_64|amd64) arch=x64 ;; arm64|aarch64) arch=arm64 ;; *) echo "Code Arena: no build for $(uname -m)" >&2; exit 1 ;; esac
            rid="$os-$arch"
            case "$rid" in
            {{sums}}  *) echo "Code Arena: this Arena has no build for $rid yet (an admin adds them: ./arenactl repair, in a checkout of the release, builds them into the app)" >&2; exit 1 ;;
            esac
            dir="${CODE_ARENA_DIR:-$HOME/.local/bin}"
            mkdir -p "$dir"
            tmp="$dir/.code-arena.new"
            set -- -fsSL
            [ -n "${CODE_ARENA_CA:-}" ] && set -- "$@" --cacert "$CODE_ARENA_CA"
            echo "Downloading Code Arena $version for $rid..."
            curl "$@" -o "$tmp" "$arena/api/downloads/code-arena/$rid"
            if command -v sha256sum >/dev/null 2>&1; then got=$(sha256sum "$tmp" | cut -d' ' -f1); else got=$(shasum -a 256 "$tmp" | cut -d' ' -f1); fi
            if [ "$got" != "$sum" ]; then rm -f "$tmp"; echo "Code Arena: the download does not match its SHA-256: nothing installed" >&2; exit 1; fi
            {{(isSigned ? SignedSh(signingRoot) : UnsignedSh)}}chmod 755 "$tmp"
            mv -f "$tmp" "$dir/code-arena"
            echo "Code Arena $version is in $dir/code-arena."
            {{AppEntrySh}}
            case ":$PATH:" in *":$dir:"*) ;; *) echo "Add $dir to your PATH (e.g. in ~/.profile): export PATH=\"$dir:\$PATH\"" ;; esac
            echo "Next, sign in: code-arena login --url $arena${CODE_ARENA_CA:+ --ca $CODE_ARENA_CA}"
            echo "Then, in a project: code-arena (it checks this Arena for newer versions; code-arena update installs one)."

            """;
    }

    /// <summary>Where the installers keep the Arena's signing CA, beside the program: code-arena update checks against it.</summary>
    public const string SigningCaFile = "code-arena-signing-ca.pem";

    /// <summary>
    /// install.ps1's check of a signed build: the certificate for code signing, chained to the Arena's signing CA it carries
    /// (<paramref name="root"/>: whether or not Windows trusts it, which is then kept beside the program), else to a root
    /// Windows trusts; no revocation check (the Arena's certificates name no list to check); the builds' list signed with its
    /// key; this build in the list.
    /// </summary>
    private static string SignedPs1(string? root) => $$"""
        $sig = Join-Path $dir '.code-arena.sig'; New-Item -ItemType Directory -Force -Path $sig | Out-Null
        Invoke-WebRequest -UseBasicParsing -Uri "$arena/api/downloads/code-arena/SHA256SUMS" -OutFile (Join-Path $sig 'SHA256SUMS')
        Invoke-WebRequest -UseBasicParsing -Uri "$arena/api/downloads/code-arena/SHA256SUMS.sig" -OutFile (Join-Path $sig 'SHA256SUMS.sig')
        Invoke-WebRequest -UseBasicParsing -Uri "$arena/api/downloads/code-arena/code-signing.crt" -OutFile (Join-Path $sig 'bundle.crt')
        $certs = @([regex]::Matches((Get-Content -Raw (Join-Path $sig 'bundle.crt')), '-----BEGIN CERTIFICATE-----[\s\S]+?-----END CERTIFICATE-----') | ForEach-Object { [System.Security.Cryptography.X509Certificates.X509Certificate2]::new([Text.Encoding]::ASCII.GetBytes($_.Value)) })
        $leaf = $certs[0]
        $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
        $chain.ChainPolicy.ApplicationPolicy.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) | Out-Null
        $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        foreach ($c in ($certs | Select-Object -Skip 1)) { $chain.ChainPolicy.ExtraStore.Add($c) | Out-Null }
        {{(root is null ? "$chained = $chain.Build($leaf)" : $$"""
        $rootPem = @'
        {{root.Trim()}}
        '@
        $root = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new([Text.Encoding]::ASCII.GetBytes($rootPem))
        $chain.ChainPolicy.ExtraStore.Add($root) | Out-Null
        # This Arena's CA, carried here: taken whether or not Windows trusts it, and only it.
        $chain.ChainPolicy.VerificationFlags = [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::AllowUnknownCertificateAuthority
        $chained = $chain.Build($leaf) -and $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate.Thumbprint -eq $root.Thumbprint
        """)}}
        $sums = [IO.File]::ReadAllBytes((Join-Path $sig 'SHA256SUMS'))
        $key = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($leaf)
        $listed = [Text.Encoding]::ASCII.GetString($sums) -split "`n"
        $good = $chained -and $key.VerifyData($sums, [IO.File]::ReadAllBytes((Join-Path $sig 'SHA256SUMS.sig')), [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1) -and ($listed -contains "$sum  $rid/code-arena.exe") -and (-not $ideSum -or ($listed -contains "$ideSum  $rid/code-arena-ide.exe"))
        $why = ($chain.ChainStatus | ForEach-Object { $_.StatusInformation.Trim() } | Where-Object { $_ }) -join '; '
        Remove-Item -Recurse -Force $sig
        if (-not $good) { Remove-Item $tmp; if ($ideSum) { Remove-Item $ideTmp }; throw "Code Arena: the build is not the one this Arena's CA signed{{(root is null ? " (or Windows does not trust that CA: certutil -user -addstore Root ca.crt)" : "")}}$(if ($why) { " ($why)" }): nothing installed" }
        {{(root is null ? "" : "Set-Content -Path (Join-Path $dir '" + SigningCaFile + "') -Value $rootPem -Encoding ascii\n")}}Write-Host "Signed by $($leaf.GetNameInfo('SimpleName', $false)), checked."
        $auth = Get-AuthenticodeSignature $tmp
        if ($auth.Status -eq 'Valid') { Write-Host "Windows names its publisher: $($auth.SignerCertificate.GetNameInfo('SimpleName', $false))." }
        else { Write-Host "Windows does not take its Authenticode signature ($($auth.Status): $($auth.StatusMessage)): it runs, with no publisher named." }

        """;

    /// <summary>
    /// install.sh's registration of Code Arena as an application (not with CODE_ARENA_NO_DESKTOP): on Linux a desktop entry
    /// with its icon, in the menus and offered for folders ("Open with"); on macOS Code Arena.app in ~/Applications, for the
    /// Dock, Launchpad and Spotlight. Each starts the IDE (code-arena -C FOLDER, the home folder by default); code-arena
    /// uninstall takes them off (src/CodeArena/AppInstall.cs: the same names).
    /// </summary>
    private static string AppEntrySh => $$"""
        if [ -z "${CODE_ARENA_NO_DESKTOP:-}" ]; then
          if [ "$os" = linux ]; then
            share="${XDG_DATA_HOME:-$HOME/.local/share}"
            mkdir -p "$share/applications" "$share/icons/hicolor/512x512/apps"
            curl "$@" -o "$share/icons/hicolor/512x512/apps/code-arena.png" "$arena/icons/icon-512.png" 2>/dev/null || true
            cat > "$share/applications/code-arena.desktop" <<DESKTOP
        [Desktop Entry]
        Type=Application
        Name=Code Arena
        GenericName=Coding agent and IDE
        Comment=An IDE in your browser around a coding agent, on {{Branding.Brand.Product}}
        Exec="$dir/code-arena" -C %f
        Icon=code-arena
        Terminal=false
        Categories=Development;IDE;
        MimeType=inode/directory;
        StartupWMClass=code-arena
        DESKTOP
            command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$share/applications" >/dev/null 2>&1
            echo "Code Arena is in your applications menu, and offered to open folders."
          elif [ "$os" = osx ]; then
            app="$HOME/Applications/Code Arena.app"
            rm -rf "$app"; mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
            printf '#!/bin/sh\nexec "%s" -C "${1:-$HOME}"\n' "$dir/code-arena" > "$app/Contents/MacOS/code-arena"
            chmod 755 "$app/Contents/MacOS/code-arena"
            if curl "$@" -o "$app/Contents/Resources/icon.png" "$arena/icons/icon-512.png" 2>/dev/null && command -v sips >/dev/null 2>&1; then
              sips -s format icns "$app/Contents/Resources/icon.png" --out "$app/Contents/Resources/code-arena.icns" >/dev/null 2>&1
              rm -f "$app/Contents/Resources/icon.png"
            fi
            cat > "$app/Contents/Info.plist" <<PLIST
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>CFBundleName</key><string>Code Arena</string>
          <key>CFBundleDisplayName</key><string>Code Arena</string>
          <key>CFBundleIdentifier</key><string>dev.arena.code-arena</string>
          <key>CFBundleExecutable</key><string>code-arena</string>
          <key>CFBundleIconFile</key><string>code-arena</string>
          <key>CFBundlePackageType</key><string>APPL</string>
          <key>CFBundleShortVersionString</key><string>$version</string>
          <key>LSMinimumSystemVersion</key><string>12.0</string>
        </dict></plist>
        PLIST
            echo "Code Arena is in your Applications folder ($app): Launchpad, Spotlight and the Dock find it."
          fi
        fi
        """;

    /// <summary>
    /// Windows' setup: a command file, opened with a double-click, that runs install.ps1 from this Arena in Windows
    /// PowerShell (no administrator rights: everything is the person's own). With the Arena's CA (<paramref name="ca"/>, when
    /// the site's certificate is its own), the downloads trust the site's certificate when that CA signed it, and only then.
    /// </summary>
    public static string SetupCmd(string arena, string? ca) => $$"""
        @echo off
        rem Code Arena from {{arena}}: installed for you (no administrator rights), checked against this Arena's signature.
        powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$s = Get-Content -Raw -LiteralPath '%~f0'; Invoke-Expression ($s.Substring($s.IndexOf('#' + 'POWERSHELL#') + 12))"
        echo.
        pause
        exit /b
        #POWERSHELL#
        $ErrorActionPreference = 'Stop'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        {{(ca is null ? "" : $$"""
        $global:ArenaCa = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new([Text.Encoding]::ASCII.GetBytes(@'
        {{ca.Trim()}}
        '@))
        # This Arena's own CA: its site's certificate is taken when that CA signed it (whether or not Windows trusts the CA).
        [Net.ServicePointManager]::ServerCertificateValidationCallback = {
          param($sender, $certificate, $chain, $errors)
          if ($errors -eq 'None') { return $true }
          $c = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
          $c.ChainPolicy.ExtraStore.Add($global:ArenaCa) | Out-Null
          $c.ChainPolicy.VerificationFlags = [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::AllowUnknownCertificateAuthority
          $c.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
          $c.Build([System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificate)) -and $c.ChainElements[$c.ChainElements.Count - 1].Certificate.Thumbprint -eq $global:ArenaCa.Thumbprint
        }
        """)}}
        Invoke-RestMethod -UseBasicParsing -Uri '{{arena}}/api/downloads/code-arena/install.ps1' | Invoke-Expression

        """;

    /// <summary>
    /// macOS' setup (Install Code Arena.command, which Finder opens in Terminal) and Linux': a script that runs install.sh
    /// from this Arena. With the Arena's CA, kept as the person's (code-arena login uses it too), curl trusts it for this Arena.
    /// </summary>
    public static string SetupSh(string arena, string? ca, bool keepOpen) => $$"""
        #!/bin/sh
        # Code Arena from {{arena}}: installed for you (in your home, no sudo), checked against this Arena's signature.
        set -eu
        arena="{{arena}}"
        set --
        {{(ca is null ? "" : $$"""
        ca="${XDG_CONFIG_HOME:-$HOME/.config}/code-arena/arena-ca.pem"
        mkdir -p "$(dirname "$ca")"
        cat > "$ca" <<'ARENA_CA'
        {{ca.Trim()}}
        ARENA_CA
        export CODE_ARENA_CA="$ca"
        set -- --cacert "$ca"
        """)}}
        curl -fsSL "$@" "$arena/api/downloads/code-arena/install.sh" | sh
        {{(keepOpen ? "printf 'Done. Press Return to close this window. '; read -r _\n" : "")}}
        """;

    private const string UnsignedSh = "echo \"Code Arena: this Arena does not sign its builds: checked by its SHA-256 only.\"\n";

    /// <summary>
    /// install.sh's check of a signed build: the certificate for code signing, chained to the Arena's signing CA it carries
    /// (<paramref name="root"/>, kept beside the program), else to a CA this machine trusts (or CODE_ARENA_CA); the builds'
    /// list signed with its key; this build in the list. Without openssl, said and not checked.
    /// </summary>
    private static string SignedSh(string? root) => $$"""
        if command -v openssl >/dev/null 2>&1; then
          sig="$dir/.code-arena.sig"; mkdir -p "$sig"
          curl "$@" -o "$sig/SHA256SUMS" "$arena/api/downloads/code-arena/SHA256SUMS"
          curl "$@" -o "$sig/SHA256SUMS.sig" "$arena/api/downloads/code-arena/SHA256SUMS.sig"
          curl "$@" -o "$sig/bundle.crt" "$arena/api/downloads/code-arena/code-signing.crt"
        {{(root is null ? "" : "cat > \"$sig/root.pem\" <<'ARENA_CA'\n" + root.Trim() + "\nARENA_CA\n")}}  openssl x509 -in "$sig/bundle.crt" -out "$sig/leaf.crt"
          openssl x509 -in "$sig/leaf.crt" -pubkey -noout > "$sig/key.pem"
          if ! openssl verify {{(root is null ? "${CODE_ARENA_CA:+-CAfile \"$CODE_ARENA_CA\"}" : "-CAfile \"$sig/root.pem\"")}} -untrusted "$sig/bundle.crt" "$sig/leaf.crt" >/dev/null 2>&1 \
            || ! openssl x509 -in "$sig/leaf.crt" -noout -ext extendedKeyUsage 2>/dev/null | grep -q "Code Signing" \
            || ! openssl dgst -sha256 -verify "$sig/key.pem" -signature "$sig/SHA256SUMS.sig" "$sig/SHA256SUMS" >/dev/null 2>&1 \
            || ! grep -q "^$sum  $rid/code-arena$" "$sig/SHA256SUMS"; then
            rm -rf "$sig" "$tmp"; echo "Code Arena: the build is not the one this Arena's CA signed{{(root is null ? " (or the CA is not one this machine trusts: CODE_ARENA_CA names it)" : "")}}: nothing installed" >&2; exit 1
          fi
          echo "Signed by $(openssl x509 -in "$sig/leaf.crt" -noout -subject | sed 's/^subject= *//'), checked."
        {{(root is null ? "" : "  cp \"$sig/root.pem\" \"$dir/" + SigningCaFile + "\"\n")}}  rm -rf "$sig"
        else
          echo "Code Arena: openssl is not here, so the build's signature is not checked (its SHA-256 is)."
        fi

        """;

    /// <summary>
    /// The installer for Windows (irm …/install.ps1 | iex): the build for the machine (x64, or ARM64 on Windows on Arm)
    /// checked against its SHA-256, in %LOCALAPPDATA%\Programs\code-arena (CODE_ARENA_DIR), that folder on the person's
    /// PATH, then how to sign in. With it, the IDE's launcher (code-arena-ide.exe), checked the same way, and a shortcut to
    /// it on the desktop and in the Start menu (not with CODE_ARENA_NO_SHORTCUTS): the IDE with no console window.
    /// </summary>
    public static string InstallPs1(string arena, IReadOnlyList<CodeArenaBuild> builds, bool isSigned = false, string? signingRoot = null)
    {
        var windows = builds.Where(b => b.Rid.StartsWith("win-", StringComparison.Ordinal) && b.Sha256 is not null).ToList();
        if (windows.Count == 0)
        {
            return "Write-Error 'Code Arena: this Arena has no build for Windows yet (an admin adds them: ./arenactl repair, in a checkout of the release, builds them into the app)'\n";
        }
        var sums = string.Join("; ", windows.Select(b => $"'{b.Rid}' = '{b.Sha256}'"));
        var ides = string.Join("; ", windows.Where(b => b.Ide?.Sha256 is not null).Select(b => $"'{b.Rid}' = '{b.Ide!.Sha256}'"));
        return $$"""
            # Code Arena from {{arena}}: the build for Windows, checked, on your PATH.
            $ErrorActionPreference = 'Stop'
            $arena = '{{arena}}'
            # The machine's own system, not this PowerShell's (an x64 one runs emulated on Windows on Arm).
            $rid = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
            $sums = @{ {{sums}} }
            if (-not $sums.ContainsKey($rid)) { throw "Code Arena: this Arena has no build for $rid yet (an admin adds it: ./arenactl repair, in a checkout of the release)" }
            $sum = $sums[$rid]
            # The IDE's launcher beside it: a GUI program that starts code-arena.exe with no console window.
            $ides = @{ {{ides}} }
            $ideSum = $ides[$rid]
            $dir = if ($env:CODE_ARENA_DIR) { $env:CODE_ARENA_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\code-arena' }
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $exe = Join-Path $dir 'code-arena.exe'
            $tmp = "$exe.new"
            $ide = Join-Path $dir 'code-arena-ide.exe'
            $ideTmp = "$ide.new"
            Write-Host "Downloading Code Arena {{AppInfo.Current.Version}} for Windows ($rid)..."
            Invoke-WebRequest -UseBasicParsing -Uri "$arena/api/downloads/code-arena/$rid" -OutFile $tmp
            if ((Get-FileHash $tmp -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sum) { Remove-Item $tmp; throw 'Code Arena: the download does not match its SHA-256: nothing installed' }
            if ($ideSum) {
              Invoke-WebRequest -UseBasicParsing -Uri "$arena/api/downloads/code-arena/$rid/ide" -OutFile $ideTmp
              if ((Get-FileHash $ideTmp -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ideSum) { Remove-Item $tmp, $ideTmp; throw 'Code Arena: the IDE launcher does not match its SHA-256: nothing installed' }
            }
            {{(isSigned ? SignedPs1(signingRoot) : "Write-Host 'Code Arena: this Arena does not sign its builds: checked by its SHA-256 only.'\n")}}Move-Item -Force $tmp $exe
            if ($ideSum) {
              Move-Item -Force $ideTmp $ide
              if (-not $env:CODE_ARENA_NO_SHORTCUTS) {
                $shell = New-Object -ComObject WScript.Shell
                foreach ($place in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs')) | Where-Object { $_ }) {
                  $link = $shell.CreateShortcut((Join-Path $place 'Code Arena.lnk'))
                  $link.TargetPath = $ide
                  $link.WorkingDirectory = $HOME
                  $link.IconLocation = "$exe,0"
                  $link.Description = 'Code Arena: the IDE, with no console window'
                  $link.Save()
                }
                Write-Host 'Code Arena is on your desktop and in the Start menu: they open the IDE with no console window.'
              }
            }
            # An application of this person's, as Windows lists them: in Settings > Apps (Programs and Features), its
            # uninstaller code-arena.exe uninstall; "Open in Code Arena" on folders. code-arena uninstall takes them off
            # (src/CodeArena/AppInstall.cs: the same keys).
            if (-not $env:CODE_ARENA_NO_DESKTOP) {
              $app = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodeArena'
              New-Item -Path $app -Force | Out-Null
              $values = @{ DisplayName = 'Code Arena'; DisplayVersion = '{{AppInfo.Current.Version}}'; Publisher = '{{Branding.Brand.Product.Replace("'", "''")}}'
                DisplayIcon = "$exe,0"; InstallLocation = $dir; UninstallString = "`"$exe`" uninstall"; URLInfoAbout = $arena }
              foreach ($v in $values.GetEnumerator()) { Set-ItemProperty -Path $app -Name $v.Key -Value $v.Value }
              Set-ItemProperty -Path $app -Name NoModify -Value 1 -Type DWord
              Set-ItemProperty -Path $app -Name NoRepair -Value 1 -Type DWord
              Set-ItemProperty -Path $app -Name EstimatedSize -Value ([int]((Get-Item $exe).Length / 1KB)) -Type DWord
              $opener = if ($ideSum) { $ide } else { $exe }
              foreach ($menu in 'Directory\shell\CodeArena', 'Directory\Background\shell\CodeArena') {
                $key = "HKCU:\Software\Classes\$menu"
                New-Item -Path "$key\command" -Force | Out-Null
                Set-ItemProperty -Path $key -Name '(default)' -Value 'Open in Code Arena'
                Set-ItemProperty -Path $key -Name Icon -Value "$exe,0"
                Set-ItemProperty -Path "$key\command" -Name '(default)' -Value "`"$opener`" -C `"%V`""
              }
              Write-Host 'Code Arena is in Settings > Apps (to uninstall it there), and "Open in Code Arena" is on folders.'
            }
            $path = [Environment]::GetEnvironmentVariable('Path', 'User')
            if (-not (($path -split ';') -contains $dir)) { [Environment]::SetEnvironmentVariable('Path', "$path;$dir", 'User'); Write-Host "Added $dir to your PATH (new terminals have it)." }
            Write-Host "Code Arena is in $exe. Next, sign in: code-arena login --url $arena"
            Write-Host 'Then, in a project: code-arena (it checks this Arena for newer versions; code-arena update installs one).'

            """;
    }

    private static IResult Download(string rid, IConfiguration config)
    {
        if (Builds(config).FirstOrDefault(b => b.Rid == rid) is not { } build)
        {
            return Results.NotFound(new { error = $"No Code Arena build for {rid}." });
        }
        return Results.File(build.Path, "application/octet-stream", build.FileName, enableRangeProcessing: true);
    }
}
