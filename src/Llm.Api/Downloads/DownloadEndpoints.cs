namespace Llm.Api.Downloads;

/// <summary>A build of Arena Code the app serves: its system, file name, size and SHA-256.</summary>
public sealed record ArenaCodeBuild(string Rid, string System, string FileName, long Size, string? Sha256);

/// <summary>
/// Arena Code, the coding agent people run on their own machine: one file per
/// system, put into the image at /downloads/arena-code/&lt;rid&gt;/ by
/// tools/publish-arena-code.sh (src/Llm.Api/Dockerfile). Open to anyone who
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
        var g = app.MapGroup("/api/downloads/arena-code").AllowAnonymous();
        g.MapGet("", List);
        g.MapGet("/{rid}", Download);
        // The certificate people's tools must trust on a self-signed deployment: public, like the web's files.
        var c = app.MapGroup("/api/downloads/certificate").AllowAnonymous();
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
    private static string Root(IConfiguration config) => Path.Combine(config["Downloads:Directory"] is { Length: > 0 } d ? d : "/downloads", "arena-code");

    /// <summary>The builds there are, with their checksums (SHA256SUMS beside them).</summary>
    public static List<ArenaCodeBuild> Builds(string root)
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
        var builds = new List<ArenaCodeBuild>();
        foreach (var (rid, system) in Systems)
        {
            var name = rid.StartsWith("win-", StringComparison.Ordinal) ? "arena-code.exe" : "arena-code";
            var file = new FileInfo(Path.Combine(root, rid, name));
            if (file.Exists)
            {
                builds.Add(new ArenaCodeBuild(rid, system, name, file.Length, sums.GetValueOrDefault($"{rid}/{name}")));
            }
        }
        return builds;
    }

    private static IResult List(IConfiguration config) =>
        Results.Ok(new { version = AppInfo.Current.Version, builds = Builds(Root(config)) });

    private static IResult Download(string rid, IConfiguration config)
    {
        if (Builds(Root(config)).FirstOrDefault(b => b.Rid == rid) is not { } build)
        {
            return Results.NotFound(new { error = $"No Arena Code build for {rid}." });
        }
        var path = Path.Combine(Root(config), build.Rid, build.FileName);
        return Results.File(path, "application/octet-stream", build.FileName, enableRangeProcessing: true);
    }
}
