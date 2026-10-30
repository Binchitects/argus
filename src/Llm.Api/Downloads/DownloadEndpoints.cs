using Llm.Api.Access;
namespace Llm.Api.Downloads;

/// <summary>A build of Code Arena the app serves: its system, file name, size and SHA-256.</summary>
public sealed record CodeArenaBuild(string Rid, string System, string FileName, long Size, string? Sha256);

/// <summary>Who signed the builds' list (SHA256SUMS), and until when the certificate holds.</summary>
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
    /// <summary>The systems it is built for, in the order the page offers them.</summary>
    public static readonly (string Rid, string System)[] Systems =
    [
        ("linux-x64", "Linux (x64)"),
        ("linux-arm64", "Linux (ARM64)"),
        ("osx-arm64", "macOS (Apple silicon)"),
        ("osx-x64", "macOS (Intel)"),
        ("win-x64", "Windows (x64)"),
    ];

    public static void MapDownloads(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/downloads/code-arena").AllowAnonymous("Public builds of Code Arena: nothing private.");
        g.MapGet("", List);
        // One line installs it: the build for the machine, checked against its SHA-256, on the person's PATH.
        g.MapGet("/install.sh", (IConfiguration config, Microsoft.Extensions.Options.IOptions<Identity.AuthOptions> auth) =>
            Results.Text(InstallSh($"https://{auth.Value.Domain}", Builds(Root(config)), Signature(config) is not null), "text/x-shellscript; charset=utf-8"));
        g.MapGet("/install.ps1", (IConfiguration config, Microsoft.Extensions.Options.IOptions<Identity.AuthOptions> auth) =>
            Results.Text(InstallPs1($"https://{auth.Value.Domain}", Builds(Root(config)), Signature(config) is not null), "text/plain; charset=utf-8"));
        // The builds' list and its signature (scripts/sign-code-arena.sh, on the host with the CA's key): installers and
        // code-arena update check a build against them with the CA they trust, not only against the SHA-256 listed here.
        g.MapGet("/SHA256SUMS", (IConfiguration config) => File.Exists(Path.Combine(Root(config), "SHA256SUMS"))
            ? Results.File(Path.Combine(Root(config), "SHA256SUMS"), "text/plain") : Results.NotFound(new { error = "No builds here." }));
        g.MapGet("/SHA256SUMS.sig", (IConfiguration config) => Signature(config) is not null
            ? Results.File(Path.Combine(SignatureRoot(config), "SHA256SUMS.sig"), "application/octet-stream") : Results.NotFound(new { error = "The builds are not signed." }));
        g.MapGet("/code-signing.crt", (IConfiguration config) => Signature(config) is not null
            ? Results.File(Path.Combine(SignatureRoot(config), "code-signing.crt"), "application/x-pem-file") : Results.NotFound(new { error = "The builds are not signed." }));
        g.MapGet("/{rid}", Download);
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
    /// Who signed the builds, when the signature there is of exactly the list of the builds served (an older one, made
    /// before an upgrade changed them, signs nothing) and holds for its certificate's key; null: not signed.
    /// </summary>
    public static CodeArenaSignature? Signature(IConfiguration config)
    {
        var (sums, signed, sig, crt) = (Path.Combine(Root(config), "SHA256SUMS"), Path.Combine(SignatureRoot(config), "SHA256SUMS"),
            Path.Combine(SignatureRoot(config), "SHA256SUMS.sig"), Path.Combine(SignatureRoot(config), "code-signing.crt"));
        try
        {
            if (!File.Exists(sums) || !File.Exists(signed) || !File.Exists(sig) || !File.Exists(crt))
            {
                return null;
            }
            var list = File.ReadAllBytes(sums);
            if (!list.AsSpan().SequenceEqual(File.ReadAllBytes(signed)))
            {
                return null;
            }
            using var cert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(File.ReadAllText(crt));
            using var key = System.Security.Cryptography.X509Certificates.RSACertificateExtensions.GetRSAPublicKey(cert);
            return key is not null && key.VerifyData(list, File.ReadAllBytes(sig), System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1)
                ? new CodeArenaSignature(cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false),
                    cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, true), cert.NotAfter.ToUniversalTime())
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The builds there are, with their checksums (SHA256SUMS beside them).</summary>
    public static List<CodeArenaBuild> Builds(string root)
    {
        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        var sumsFile = Path.Combine(root, "SHA256SUMS");
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
            if (file.Exists)
            {
                builds.Add(new CodeArenaBuild(rid, system, name, file.Length, sums.GetValueOrDefault($"{rid}/{name}")));
            }
        }
        return builds;
    }

    private static IResult List(IConfiguration config) =>
        Results.Ok(new { version = AppInfo.Current.Version, builds = Builds(Root(config)), signature = Signature(config) });

    /// <summary>
    /// The installer for Linux and macOS (curl -fsSL …/install.sh | sh): the build for this system, its SHA-256 checked as
    /// this Arena lists it and, when the builds are signed (<paramref name="isSigned"/>), against the list its code-signing certificate
    /// signed, that certificate from a CA the machine (or CODE_ARENA_CA) trusts; put in ~/.local/bin (CODE_ARENA_DIR), then
    /// how to sign in. CODE_ARENA_CA names the CA file on a self-signed deployment.
    /// </summary>
    public static string InstallSh(string arena, IReadOnlyList<CodeArenaBuild> builds, bool isSigned = false)
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
            {{(isSigned ? SignedSh : UnsignedSh)}}chmod 755 "$tmp"
            mv -f "$tmp" "$dir/code-arena"
            echo "Code Arena $version is in $dir/code-arena."
            case ":$PATH:" in *":$dir:"*) ;; *) echo "Add $dir to your PATH (e.g. in ~/.profile): export PATH=\"$dir:\$PATH\"" ;; esac
            echo "Next, sign in: code-arena login --url $arena${CODE_ARENA_CA:+ --ca $CODE_ARENA_CA}"
            echo "Then, in a project: code-arena (it checks this Arena for newer versions; code-arena update installs one)."

            """;
    }

    /// <summary>
    /// install.sh's check of a signed build: the certificate from a CA this machine trusts (or CODE_ARENA_CA), for code
    /// signing; the builds' list signed with its key; this build in the list. Without openssl, said and not checked.
    /// </summary>
    /// <summary>
    /// install.ps1's check of a signed build: the certificate chains to a root Windows trusts, for code signing; the builds'
    /// list signed with its key; this build in the list.
    /// </summary>
    private const string SignedPs1 = """
        $sig = Join-Path $dir '.code-arena.sig'; New-Item -ItemType Directory -Force -Path $sig | Out-Null
        Invoke-WebRequest -UseBasicParsing -Uri "$arena/api/downloads/code-arena/SHA256SUMS" -OutFile (Join-Path $sig 'SHA256SUMS')
        Invoke-WebRequest -UseBasicParsing -Uri "$arena/api/downloads/code-arena/SHA256SUMS.sig" -OutFile (Join-Path $sig 'SHA256SUMS.sig')
        Invoke-WebRequest -UseBasicParsing -Uri "$arena/api/downloads/code-arena/code-signing.crt" -OutFile (Join-Path $sig 'bundle.crt')
        $pem = Get-Content -Raw (Join-Path $sig 'bundle.crt')
        $leaf = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new([Text.Encoding]::ASCII.GetBytes(($pem -split '-----END CERTIFICATE-----')[0] + '-----END CERTIFICATE-----'))
        $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
        $chain.ChainPolicy.ApplicationPolicy.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) | Out-Null
        $sums = [IO.File]::ReadAllBytes((Join-Path $sig 'SHA256SUMS'))
        $key = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($leaf)
        $good = $chain.Build($leaf) -and $key.VerifyData($sums, [IO.File]::ReadAllBytes((Join-Path $sig 'SHA256SUMS.sig')), [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1) -and ([Text.Encoding]::ASCII.GetString($sums) -split "`n" -contains '{sum}  win-x64/code-arena.exe')
        Remove-Item -Recurse -Force $sig
        if (-not $good) { Remove-Item $tmp; throw "Code Arena: the build is not the one this Arena's CA signed (or Windows does not trust that CA: certutil -addstore -f Root ca.crt): nothing installed" }
        Write-Host "Signed by $($leaf.GetNameInfo('SimpleName', $false)), checked."

        """;

    private const string UnsignedSh = "echo \"Code Arena: this Arena does not sign its builds: checked by its SHA-256 only.\"\n";

    private const string SignedSh = """
        if command -v openssl >/dev/null 2>&1; then
          sig="$dir/.code-arena.sig"; mkdir -p "$sig"
          curl "$@" -o "$sig/SHA256SUMS" "$arena/api/downloads/code-arena/SHA256SUMS"
          curl "$@" -o "$sig/SHA256SUMS.sig" "$arena/api/downloads/code-arena/SHA256SUMS.sig"
          curl "$@" -o "$sig/bundle.crt" "$arena/api/downloads/code-arena/code-signing.crt"
          openssl x509 -in "$sig/bundle.crt" -out "$sig/leaf.crt"
          openssl x509 -in "$sig/leaf.crt" -pubkey -noout > "$sig/key.pem"
          if ! openssl verify ${CODE_ARENA_CA:+-CAfile "$CODE_ARENA_CA"} -untrusted "$sig/bundle.crt" "$sig/leaf.crt" >/dev/null 2>&1             || ! openssl x509 -in "$sig/leaf.crt" -noout -ext extendedKeyUsage 2>/dev/null | grep -q "Code Signing"             || ! openssl dgst -sha256 -verify "$sig/key.pem" -signature "$sig/SHA256SUMS.sig" "$sig/SHA256SUMS" >/dev/null 2>&1             || ! grep -q "^$sum  $rid/code-arena$" "$sig/SHA256SUMS"; then
            rm -rf "$sig" "$tmp"; echo "Code Arena: the build is not the one this Arena's CA signed (or the CA is not one this machine trusts: CODE_ARENA_CA names it): nothing installed" >&2; exit 1
          fi
          echo "Signed by $(openssl x509 -in "$sig/leaf.crt" -noout -subject | sed 's/^subject= *//'), checked."
          rm -rf "$sig"
        else
          echo "Code Arena: openssl is not here, so the build's signature is not checked (its SHA-256 is)."
        fi

        """;

    /// <summary>
    /// The installer for Windows (irm …/install.ps1 | iex): the build checked against its SHA-256, in
    /// %LOCALAPPDATA%\Programs\code-arena (CODE_ARENA_DIR), that folder on the person's PATH, then how to sign in.
    /// </summary>
    public static string InstallPs1(string arena, IReadOnlyList<CodeArenaBuild> builds, bool isSigned = false)
    {
        var sum = builds.FirstOrDefault(b => b.Rid == "win-x64")?.Sha256;
        if (sum is null)
        {
            return "Write-Error 'Code Arena: this Arena has no build for Windows yet (an admin adds them: ./arenactl repair, in a checkout of the release, builds them into the app)'\n";
        }
        return $$"""
            # Code Arena from {{arena}}: the build for Windows, checked, on your PATH.
            $ErrorActionPreference = 'Stop'
            $arena = '{{arena}}'
            $dir = if ($env:CODE_ARENA_DIR) { $env:CODE_ARENA_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\code-arena' }
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $exe = Join-Path $dir 'code-arena.exe'
            $tmp = "$exe.new"
            Write-Host 'Downloading Code Arena {{AppInfo.Current.Version}} for Windows...'
            Invoke-WebRequest -UseBasicParsing -Uri "$arena/api/downloads/code-arena/win-x64" -OutFile $tmp
            if ((Get-FileHash $tmp -Algorithm SHA256).Hash.ToLowerInvariant() -ne '{{sum}}') { Remove-Item $tmp; throw 'Code Arena: the download does not match its SHA-256: nothing installed' }
            {{(isSigned ? SignedPs1.Replace("{sum}", sum, StringComparison.Ordinal) : "Write-Host 'Code Arena: this Arena does not sign its builds: checked by its SHA-256 only.'\n")}}Move-Item -Force $tmp $exe
            $path = [Environment]::GetEnvironmentVariable('Path', 'User')
            if (-not (($path -split ';') -contains $dir)) { [Environment]::SetEnvironmentVariable('Path', "$path;$dir", 'User'); Write-Host "Added $dir to your PATH (new terminals have it)." }
            Write-Host "Code Arena is in $exe. Next, sign in: code-arena login --url $arena"
            Write-Host 'Then, in a project: code-arena (it checks this Arena for newer versions; code-arena update installs one).'

            """;
    }

    private static IResult Download(string rid, IConfiguration config)
    {
        if (Builds(Root(config)).FirstOrDefault(b => b.Rid == rid) is not { } build)
        {
            return Results.NotFound(new { error = $"No Code Arena build for {rid}." });
        }
        var path = Path.Combine(Root(config), build.Rid, build.FileName);
        return Results.File(path, "application/octet-stream", build.FileName, enableRangeProcessing: true);
    }
}
