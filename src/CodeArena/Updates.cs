using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// Code Arena's own updates, from the Arena it is signed in to. Arena serves the builds of its own version (Connect your
/// tools, /api/downloads/code-arena), so a newer one there is the one to run. A session's start looks, at most once a day,
/// in the background and within a few seconds, and says so; code-arena update downloads the build for this system,
/// checks its size and SHA-256, and puts it in this program's place (it runs from the next start).
/// </summary>
internal static class Updates
{
    /// <summary>How often a session's start looks, and how long it waits for Arena.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromDays(1), Wait = TimeSpan.FromSeconds(5);

    /// <summary>A newer build Arena serves for this system.</summary>
    public sealed record Available(string Version, string Rid, string Url, string? Sha256, long Size);

    /// <summary>This program's system, as Arena names its builds (linux-x64, osx-arm64, win-x64…).</summary>
    public static string Rid => RuntimeInformation.RuntimeIdentifier;

    /// <summary>A newer Code Arena Arena serves for this system; null when there is none, or Arena did not say.</summary>
    public static async Task<Available?> CheckAsync(Config config, HttpClient http, TimeSpan wait, CancellationToken ct, string? current = null)
    {
        if (config.Url is not { Length: > 0 } arena)
        {
            return null;
        }
        var root = arena.TrimEnd('/') + "/api/downloads/code-arena";
        using var within = CancellationTokenSource.CreateLinkedTokenSource(ct);
        within.CancelAfter(wait);
        try
        {
            using var res = await http.GetAsync(new Uri(root), within.Token);
            if (!res.IsSuccessStatusCode || Json.ParseObject(await res.Content.ReadAsStringAsync(within.Token)) is not { } body
                || body.Str("version") is not { } version || !Newer(version, current ?? Cli.Version))
            {
                return null;
            }
            var build = (body["builds"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(b => b.Str("rid") == Rid);
            return build is null ? null : new Available(version, Rid, $"{root}/{Rid}", build.Str("sha256"), build.Long("size") ?? 0);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Whether version <paramref name="a"/> is newer than <paramref name="b"/> (x.y.z; a pre-release comes before its release).</summary>
    public static bool Newer(string a, string b)
    {
        static (Version Number, string Pre)? Parse(string v)
        {
            var dash = v.IndexOf('-', StringComparison.Ordinal);
            var number = dash < 0 ? v : v[..dash];
            return Version.TryParse(number, out var n) ? (n, dash < 0 ? "" : v[(dash + 1)..]) : null;
        }
        if (Parse(a.Trim().TrimStart('v')) is not { } x || Parse(b.Trim().TrimStart('v')) is not { } y)
        {
            return false;
        }
        var c = x.Number.CompareTo(y.Number);
        return c != 0 ? c > 0 : x.Pre.Length == 0 ? y.Pre.Length > 0 : y.Pre.Length > 0 && string.CompareOrdinal(x.Pre, y.Pre) > 0;
    }

    /// <summary>What a newer build means for the person, in one line.</summary>
    public static string Said(Available a) => $"Code Arena {a.Version} is available from Arena (this is {Cli.Version}): code-arena update installs it.";

    /// <summary>
    /// The check a session's start makes: once a day (when it last looked is kept in the data folder), what to say about a
    /// newer build, or null. A newer one found within the day is said again without asking Arena.
    /// </summary>
    public static async Task<string?> NoticeAsync(Config config, HttpClient http, AppPaths paths, DateTimeOffset now, CancellationToken ct)
    {
        var file = Path.Combine(paths.DataDir, "update-check.json");
        try
        {
            if (File.Exists(file) && Json.ParseObject(await File.ReadAllTextAsync(file, ct)) is { } last
                && DateTimeOffset.TryParse(last.Str("checked"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) && now - at < Every)
            {
                return last.Str("version") is { } v && Newer(v, Cli.Version) ? $"Code Arena {v} is available from Arena (this is {Cli.Version}): code-arena update installs it." : null;
            }
        }
        catch (IOException)
        {
            // Looked again.
        }
        var found = await CheckAsync(config, http, Wait, ct);
        try
        {
            PrivateFiles.EnsureDirectory(paths.DataDir);
            PrivateFiles.WriteAllText(file, new JsonObject { ["checked"] = now.ToString("o", CultureInfo.InvariantCulture), ["version"] = found?.Version }.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not kept: it looks again next time.
        }
        return found is null ? null : Said(found);
    }

    /// <summary>
    /// Puts Arena's newer build in this program's place: downloaded beside it, checked (size and SHA-256), made runnable, then
    /// renamed over it (on Windows the running one moves aside first, as code-arena.exe.old). What it did, or why it did not.
    /// </summary>
    public static async Task<(bool Done, string Said)> UpdateAsync(Config config, HttpClient http, Action<string> progress, CancellationToken ct, string? exe = null)
    {
        exe ??= Environment.ProcessPath;
        if (exe is null || !Path.GetFileNameWithoutExtension(exe).Equals("code-arena", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "This Code Arena runs from its source (dotnet run, or the build folder): build it again, or download it from Arena's Connect your tools.");
        }
        if (config.Url is not { Length: > 0 })
        {
            return (false, "Not signed in to an Arena yet: code-arena login, then code-arena update.");
        }
        if (await CheckAsync(config, http, TimeSpan.FromSeconds(30), ct) is not { } found)
        {
            return (true, $"Code Arena {Cli.Version} is the newest this Arena has for {Rid}.");
        }
        progress($"Downloading Code Arena {found.Version} for {found.Rid} ({Files.Size(found.Size)}) from Arena…");
        var temporary = exe + ".new";
        try
        {
            using (var res = await http.GetAsync(new Uri(found.Url), HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!res.IsSuccessStatusCode)
                {
                    return (false, $"Arena did not give the build ({(int)res.StatusCode}): try again in a moment.");
                }
                await using var file = File.Create(temporary);
                await using var body = await res.Content.ReadAsStreamAsync(ct);
                await body.CopyToAsync(file, ct);
            }
            var length = new FileInfo(temporary).Length;
            string sum;
            await using (var read = File.OpenRead(temporary))
            {
                sum = Convert.ToHexStringLower(await SHA256.HashDataAsync(read, ct));
            }
            if ((found.Size > 0 && length != found.Size) || (found.Sha256 is { Length: > 0 } want && !want.Equals(sum, StringComparison.OrdinalIgnoreCase)))
            {
                File.Delete(temporary);
                return (false, $"The download did not match what Arena lists ({length:N0} bytes, SHA-256 {sum[..12]}…): nothing changed. Try again.");
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                    | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.Move(temporary, exe, overwrite: true);
            }
            else
            {
                var aside = exe + ".old";
                File.Delete(aside);
                File.Move(exe, aside);
                File.Move(temporary, exe);
            }
            return (true, $"Code Arena {Cli.Version} → {found.Version}: the next start runs it.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // Left beside it: the next update writes over it.
            }
            return (false, $"Code Arena could not be replaced ({e.Message}): nothing changed.");
        }
    }
}
