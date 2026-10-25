using Llm.Api.Access;
namespace Llm.Api.Downloads;

/// <summary>A build of Code Arena the app serves: its system, file name, size and SHA-256.</summary>
public sealed record CodeArenaBuild(string Rid, string System, string FileName, long Size, string? Sha256);

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
            Results.Text(InstallSh($"https://{auth.Value.Domain}", Builds(Root(config))), "text/x-shellscript; charset=utf-8"));
        g.MapGet("/install.ps1", (IConfiguration config, Microsoft.Extensions.Options.IOptions<Identity.AuthOptions> auth) =>
            Results.Text(InstallPs1($"https://{auth.Value.Domain}", Builds(Root(config))), "text/plain; charset=utf-8"));
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
    private static string Root(IConfiguration config) => Path.Combine(config["Downloads:Directory"] is { Length: > 0 } d ? d : "/downloads", "code-arena");

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
        Results.Ok(new { version = AppInfo.Current.Version, builds = Builds(Root(config)) });

    /// <summary>
    /// The installer for Linux and macOS (curl -fsSL …/install.sh | sh): the build for this system, its SHA-256 checked as
    /// this Arena lists it, put in ~/.local/bin (CODE_ARENA_DIR), then how to sign in. CODE_ARENA_CA names the CA file on a
    /// self-signed deployment.
    /// </summary>
    public static string InstallSh(string arena, IReadOnlyList<CodeArenaBuild> builds)
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
            {{sums}}  *) echo "Code Arena: this Arena has no build for $rid yet (an admin adds them: tools/fetch-code-arena-packs.sh)" >&2; exit 1 ;;
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
            chmod 755 "$tmp"
            mv -f "$tmp" "$dir/code-arena"
            echo "Code Arena $version is in $dir/code-arena."
            case ":$PATH:" in *":$dir:"*) ;; *) echo "Add $dir to your PATH (e.g. in ~/.profile): export PATH=\"$dir:\$PATH\"" ;; esac
            echo "Next, sign in: code-arena login --url $arena${CODE_ARENA_CA:+ --ca $CODE_ARENA_CA}"
            echo "Then, in a project: code-arena (it checks this Arena for newer versions; code-arena update installs one)."

            """;
    }

    /// <summary>
    /// The installer for Windows (irm …/install.ps1 | iex): the build checked against its SHA-256, in
    /// %LOCALAPPDATA%\Programs\code-arena (CODE_ARENA_DIR), that folder on the person's PATH, then how to sign in.
    /// </summary>
    public static string InstallPs1(string arena, IReadOnlyList<CodeArenaBuild> builds)
    {
        var sum = builds.FirstOrDefault(b => b.Rid == "win-x64")?.Sha256;
        if (sum is null)
        {
            return "Write-Error 'Code Arena: this Arena has no build for Windows yet (an admin adds them: tools/fetch-code-arena-packs.sh)'\n";
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
            Move-Item -Force $tmp $exe
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
