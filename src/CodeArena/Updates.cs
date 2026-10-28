using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// Code Arena's own updates, from the Arena it is signed in to. Arena serves the builds of its own version (Connect your
/// tools, /api/downloads/code-arena), so a newer one there is the one to run. A session's start looks, at most once a day,
/// in the background and within a few seconds, and says so; code-arena update downloads the build for this system,
/// checks its size and SHA-256 and, when Arena signs its builds (scripts/sign-code-arena.sh), that its CA signed it, and
/// puts it in this program's place (it runs from the next start).
/// </summary>
internal static class Updates
{
    /// <summary>How often a session's start looks, and how long it waits for Arena.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromDays(1), Wait = TimeSpan.FromSeconds(5);

    /// <summary>A newer build Arena serves for this system.</summary>
    /// <param name="Signed">Arena signs its builds: the list of them, with a code-signing certificate of its CA.</param>
    public sealed record Available(string Version, string Rid, string Url, string? Sha256, long Size, bool Signed = false);

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
            return build is null ? null : new Available(version, Rid, $"{root}/{Rid}", build.Str("sha256"), build.Long("size") ?? 0, body["signature"] is JsonObject);
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
    /// <param name="caFile">The CA this machine trusts for Arena (--ca, the config's, ARENA_CA_CERT); null: the system's.</param>
    public static async Task<(bool Done, string Said)> UpdateAsync(Config config, HttpClient http, Action<string> progress, CancellationToken ct, string? exe = null,
        string? caFile = null)
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
            if (found.Signed && await SignatureProblemAsync(http, found.Url[..found.Url.LastIndexOf('/')], $"{found.Rid}/{Path.GetFileName(exe)}", sum, caFile, ct) is { } problem)
            {
                File.Delete(temporary);
                return (false, $"The download is not the one Arena's CA signed ({problem}): nothing changed.");
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
            return (true, $"Code Arena {Cli.Version} → {found.Version}{(found.Signed ? ", signed by Arena's CA" : "")}: the next start runs it.");
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

    /// <summary>Why a build is not one Arena's CA signed, or null when it is: the builds' list, its signature and the certificate, from Arena.</summary>
    private static async Task<string?> SignatureProblemAsync(HttpClient http, string root, string entry, string sum, string? caFile, CancellationToken ct)
    {
        byte[] sums, signature;
        string pem;
        try
        {
            sums = await http.GetByteArrayAsync(new Uri(root + "/SHA256SUMS"), ct);
            signature = await http.GetByteArrayAsync(new Uri(root + "/SHA256SUMS.sig"), ct);
            pem = await http.GetStringAsync(new Uri(root + "/code-signing.crt"), ct);
        }
        catch (HttpRequestException e)
        {
            return $"Arena did not give the signature: {e.Message}";
        }
        return SignatureProblem(sums, signature, pem, entry, sum, caFile);
    }

    /// <summary>
    /// Why a build is not the one signed, or null when it is: the certificate (the first of <paramref name="pem"/>, the rest
    /// its chain) for code signing, from a CA this machine trusts (<paramref name="caFile"/>, else the system's); the list
    /// signed with its key (RSA, SHA-256); this build's SHA-256 in the list, as sha256sum writes it.
    /// </summary>
    internal static string? SignatureProblem(byte[] sums, byte[] signature, string pem, string entry, string sum, string? caFile)
    {
        var certs = new X509Certificate2Collection();
        try
        {
            certs.ImportFromPem(pem);
            if (certs.Count == 0)
            {
                return "no certificate came with it";
            }
            var leaf = certs[0];
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.3"));
            chain.ChainPolicy.ExtraStore.AddRange(certs.Skip(1).ToArray());
            if (caFile is not null)
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.ImportFromPemFile(caFile);
            }
            if (!chain.Build(leaf))
            {
                return $"its certificate, {leaf.GetNameInfo(X509NameType.SimpleName, false)}, is not one for code signing from a CA this machine trusts for Arena: " +
                    string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0).Distinct());
            }
            using var key = leaf.GetRSAPublicKey();
            if (key is null || !key.VerifyData(sums, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                return "the builds' list is not the one its certificate signed";
            }
            return Encoding.ASCII.GetString(sums).Split('\n').Any(l => l.TrimEnd('\r') == $"{sum}  {entry}") ? null : $"{entry} is not in the list signed";
        }
        catch (CryptographicException e)
        {
            return $"its signature could not be read: {e.Message}";
        }
        finally
        {
            foreach (var c in certs)
            {
                c.Dispose();
            }
        }
    }
}
