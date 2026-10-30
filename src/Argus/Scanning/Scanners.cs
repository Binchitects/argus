using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Argus.Indexing;
using Argus.Server;
using Argus.Util;

namespace Argus.Scanning;

/// <summary>The scanners could not answer (not installed, busy, or failed); the message says which.</summary>
public sealed class ScannersUnavailable(string message, Exception? inner = null) : ToolError(message, inner);

/// <summary>
/// The security scanners (the scanners feature, ARGUS_SCANNERS_URL): a commit of a project's mirror sent to them, and
/// what they found. The commit's files go as a tar (git archive); with its history, as a git bundle of the commit,
/// so gitleaks reads every commit before it too. A bundle needs a ref: one is made for it (refs/arena/scan/...) and
/// removed again, whatever happens. Nothing is kept here: the file sent is deleted when the answer is in.
/// </summary>
public static class Scanners
{
    /// <summary>The largest commit sent; a history larger than this is sent as the commit's files alone.</summary>
    public const long MostBytes = 1024L * 1024 * 1024;
    /// <summary>The longest a scan may take, with its wait behind another.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(45);
    public static readonly IReadOnlyList<string> Known = ["opengrep", "bandit", "gitleaks", "trivy"];
    const string ScanRefs = "refs/arena/scan/";

    /// <summary>Where the scanners are, or null when none are installed.</summary>
    public static string? BaseUrl =>
        Environment.GetEnvironmentVariable("ARGUS_SCANNERS_URL") is { Length: > 0 } u ? u.TrimEnd('/') : null;

    static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient { Timeout = Timeout });

    /// <summary>The client scans are sent with (a test gives its own).</summary>
    public static HttpClient? Client { get; set; }

    static HttpClient Http => Client ?? SharedClient.Value;

    /// <summary>The scanners' answer for the commit: each tool's run and the findings, the most severe first.</summary>
    public static JsonObject Scan(string mirror, string sha, bool history, IReadOnlyList<string>? tools, IReadOnlyList<string>? paths = null)
    {
        var url = BaseUrl ?? throw new ScannersUnavailable(
            "No security scanners are installed here: an admin turns the scanners feature on (./arenactl repair --with scanners, or sh argus-arena-VERSION-offline.run repair --with scanners).");
        var asked = tools is { Count: > 0 } ? tools : Known;
        if (asked.FirstOrDefault(t => !Known.Contains(t)) is { } unknown)
            throw new ToolError($"tools are some of {string.Join(", ", Known)}, not {PyStr.Repr(unknown)}.");
        var file = Path.Combine(Path.GetTempPath(), $"argus-scan-{Guid.NewGuid():N}");
        // Only some files (a change's): those alone are sent, with no history.
        var withHistory = history && asked.Contains("gitleaks") && paths is not { Count: > 0 };
        string? reference = null;
        try
        {
            if (withHistory)
            {
                reference = ScanRefs + Guid.NewGuid().ToString("N");
                Mirror.Git(mirror, ["update-ref", reference, sha]);
                Mirror.Git(mirror, ["bundle", "create", "--quiet", file, reference]);
                if (new FileInfo(file).Length > MostBytes)
                {
                    // Too long a history to send: the commit's files, and gitleaks reads those alone.
                    File.Delete(file);
                    withHistory = false;
                }
            }
            if (!withHistory)
                Mirror.Git(mirror, ["archive", "--format=tar", "-o", file, sha, .. paths is { Count: > 0 } only ? ["--", .. only.Select(p => $":(literal){p}")] : Array.Empty<string>()]);
            if (new FileInfo(file).Length > MostBytes)
                throw new ToolError($"The commit is larger than a scan reads ({MostBytes / 1024 / 1024} MB).");
            var query = $"commit={sha}&tools={string.Join(',', asked)}" + (withHistory ? $"&ref={Uri.EscapeDataString(reference!)}" : "");
            using var body = new StreamContent(File.OpenRead(file));
            return Send(url, query, body, withHistory ? "application/x-git-bundle" : "application/x-tar", sha);
        }
        finally
        {
            if (reference is not null) Mirror.RunGit(mirror, ["update-ref", "-d", reference]);
            if (File.Exists(file)) File.Delete(file);
        }
    }

    /// <summary>The most files, and bytes, a change not yet committed is scanned with.</summary>
    public const int MostFiles = 200;
    public const long MostFileBytes = 20L * 1024 * 1024;

    /// <summary>
    /// Files not committed anywhere (a fix's change before its merge request), by path, scanned as they are: a tar of
    /// them alone, no history. The scanners name a scan by a commit; the tar's own hash stands for one.
    /// </summary>
    public static JsonObject ScanFiles(IReadOnlyDictionary<string, string> files, IReadOnlyList<string>? tools)
    {
        var url = BaseUrl ?? throw new ScannersUnavailable(
            "No security scanners are installed here: an admin turns the scanners feature on (./arenactl repair --with scanners, or sh argus-arena-VERSION-offline.run repair --with scanners).");
        var asked = tools is { Count: > 0 } ? tools : Known;
        if (asked.FirstOrDefault(t => !Known.Contains(t)) is { } unknown)
            throw new ToolError($"tools are some of {string.Join(", ", Known)}, not {PyStr.Repr(unknown)}.");
        if (files.Count is 0 or > MostFiles)
            throw new ToolError($"Send 1 to {MostFiles} files to scan.");
        var tar = new MemoryStream();
        long total = 0;
        using (var writer = new System.Formats.Tar.TarWriter(tar, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (path, text) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                if (path.Length is 0 or > 1000 || path.StartsWith('/') || path.Contains('\\') || path.Split('/').Any(p => p is "" or "." or ".."))
                    throw new ToolError($"{PyStr.Repr(path)} is not a path inside the repository.");
                var data = System.Text.Encoding.UTF8.GetBytes(text);
                if ((total += data.Length) > MostFileBytes)
                    throw new ToolError($"The files are larger than a change is scanned with ({MostFileBytes / 1024 / 1024} MB).");
                writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, path) { DataStream = new MemoryStream(data) });
            }
        }
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(tar.ToArray()));
        tar.Position = 0;
        using var body = new StreamContent(tar);
        return Send(url, $"commit={sha}&tools={string.Join(',', asked)}", body, "application/x-tar", sha);
    }

    static JsonObject Send(string url, string query, HttpContent body, string type, string sha)
    {
        body.Headers.ContentType = new MediaTypeHeaderValue(type);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{url}/scan?{query}") { Content = body };
        HttpResponseMessage response;
        try
        {
            response = Http.Send(request, HttpCompletionOption.ResponseContentRead);
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new ScannersUnavailable($"The security scanners at {url} could not be reached: {exc.Message}", exc);
        }
        using (response)
        {
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            JsonNode? answer;
            try
            {
                answer = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                answer = null;
            }
            if (response.StatusCode == HttpStatusCode.OK && answer is JsonObject found)
                return found;
            var why = answer?["error"]?.ToString() ?? PyStr.Prefix(text, 300);
            throw response.StatusCode == HttpStatusCode.ServiceUnavailable
                ? new ScannersUnavailable($"The security scanners are busy with other scans: try again later ({why}).")
                : new ScannersUnavailable($"The security scanners could not scan {PyStr.Prefix(sha, 12)}: {why}");
        }
    }
}
