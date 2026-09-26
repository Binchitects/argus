using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Argus.Indexing;
using Argus.Util;

namespace Argus.Packs;

/// <summary>A pack could not be installed, listed, fetched or indexed.</summary>
public sealed class RegistryError(string message, Exception? inner = null) : Exception(message, inner);

public sealed record InstalledPack(
    string Name, string Version, string Path, string EmbeddingModel, string EmbeddingDim, long SizeBytes,
    string License, string Attribution, string SourceCommit, bool Compatible, string IncompatibleReason = "");

public sealed record IndexEntry(string Name, string Version, string Url, string Sha256, long SizeBytes, string License);

/// <summary>A pack file in the pack library, by its path there, and whether it is the one loaded under its name.</summary>
public sealed record LibraryPack(string File, InstalledPack Pack, bool Loaded);

/// <summary>
/// Installing, listing and publishing packs. A pack
/// names its own file from its metadata, so the name is validated before it is
/// used as a path; an install stages to a temp file and renames into place, so a
/// failed download never replaces a working pack.
/// </summary>
public static class Registry
{
    public const string PackSuffix = ".arguspack";
    static readonly Regex NameRe = new(@"^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    const int DownloadChunk = 1 << 20;

    /// <summary>Tests replace this to serve downloads from memory.</summary>
    public static Func<HttpMessageHandler>? HandlerOverride { get; set; }

    static HttpClient Client(double timeoutSeconds) =>
        new(HandlerOverride?.Invoke() ?? new HttpClientHandler { AllowAutoRedirect = true }, disposeHandler: true)
            { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

    public static InstalledPack Install(string urlOrPath, string destDir, string? expectedSha256 = null)
    {
        Directory.CreateDirectory(destDir);
        var staging = Path.Combine(destDir, $".incoming-{Guid.NewGuid():N}.tmp");
        string name, finalPath;
        Dictionary<string, string> meta;
        bool compatible;
        string reason;
        try
        {
            var digest = Stage(urlOrPath, staging);
            if (!string.IsNullOrEmpty(expectedSha256) && digest.ToLowerInvariant() != expectedSha256.Trim().ToLowerInvariant())
                throw new RegistryError($"checksum mismatch for {urlOrPath}: expected {expectedSha256.Trim().ToLowerInvariant()}, got {digest}");
            meta = ReadPackMeta(staging);
            name = RequireName(meta.GetValueOrDefault("source_name", ""));
            (compatible, reason) = Compatibility(meta);
            finalPath = Path.Combine(destDir, $"{name}{PackSuffix}");
            File.Move(staging, finalPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(staging); } catch (IOException) { }
            throw;
        }
        return new InstalledPack(name, meta.GetValueOrDefault("pack_version", ""), finalPath,
            meta.GetValueOrDefault("embedding_model", ""), meta.GetValueOrDefault("embedding_dim", ""),
            new FileInfo(finalPath).Length, meta.GetValueOrDefault("license", ""), meta.GetValueOrDefault("attribution", ""),
            meta.GetValueOrDefault("source_commit", ""), compatible, reason);
    }

    /// <summary>The packs to search: a link whose library file is gone is left out, so one missing pack never stops the rest.</summary>
    public static List<string> PackFiles(string dir) => Entries(dir).Where(Reachable).ToList();

    /// <summary>A file there to read: a plain one, or a link whose target is (File.Exists is true for a broken link).</summary>
    static bool Reachable(string path)
    {
        var info = new FileInfo(path);
        return info.LinkTarget is null ? info.Exists : info.ResolveLinkTarget(returnFinalTarget: true) is { Exists: true };
    }

    static List<string> Entries(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*" + PackSuffix).OrderBy(p => p, StringComparer.Ordinal).ToList()
            : [];

    public static List<InstalledPack> ListInstalled(string destDir)
    {
        var packs = new List<InstalledPack>();
        foreach (var path in Entries(destDir))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            if (!Reachable(path))
            {
                packs.Add(new InstalledPack(stem, "", path, "", "", 0, "", "", "", false, $"its library file is gone ({LinkedFrom(path)}): unload it"));
                continue;
            }
            Dictionary<string, string> meta;
            try { meta = ReadPackMeta(path); }
            catch (RegistryError exc)
            {
                packs.Add(new InstalledPack(stem, "", path, "", "", new FileInfo(path).Length, "", "", "", false, $"unreadable: {exc.Message}"));
                continue;
            }
            var (compatible, reason) = Compatibility(meta);
            packs.Add(new InstalledPack(meta.GetValueOrDefault("source_name", stem), meta.GetValueOrDefault("pack_version", ""), path,
                meta.GetValueOrDefault("embedding_model", ""), meta.GetValueOrDefault("embedding_dim", ""), new FileInfo(path).Length,
                meta.GetValueOrDefault("license", ""), meta.GetValueOrDefault("attribution", ""), meta.GetValueOrDefault("source_commit", ""),
                compatible, reason));
        }
        return packs;
    }

    /// <summary>
    /// The pack files in a library folder (read-only; two levels deep), each read
    /// for what it is. One is loaded when the pack of its name in
    /// <paramref name="destDir"/> is a link to it.
    /// </summary>
    public static List<LibraryPack> ListLibrary(string libraryDir, string destDir)
    {
        if (!Directory.Exists(libraryDir)) return [];
        var root = Path.GetFullPath(libraryDir);
        var files = Directory.EnumerateFiles(root, "*" + PackSuffix, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
            .Where(f => !Path.GetFileName(f).StartsWith('.')).OrderBy(f => f, StringComparer.Ordinal);
        var packs = new List<LibraryPack>();
        foreach (var path in files)
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            InstalledPack pack;
            try
            {
                var meta = ReadPackMeta(path);
                var (compatible, reason) = Compatibility(meta);
                pack = new InstalledPack(meta.GetValueOrDefault("source_name", Path.GetFileNameWithoutExtension(path)), meta.GetValueOrDefault("pack_version", ""), path,
                    meta.GetValueOrDefault("embedding_model", ""), meta.GetValueOrDefault("embedding_dim", ""), new FileInfo(path).Length,
                    meta.GetValueOrDefault("license", ""), meta.GetValueOrDefault("attribution", ""), meta.GetValueOrDefault("source_commit", ""),
                    compatible && NameRe.IsMatch(meta.GetValueOrDefault("source_name", "")), compatible ? (NameRe.IsMatch(meta.GetValueOrDefault("source_name", "")) ? "" : "its name is not a valid pack name") : reason);
            }
            catch (RegistryError exc)
            {
                pack = new InstalledPack(Path.GetFileNameWithoutExtension(path), "", path, "", "", new FileInfo(path).Length, "", "", "", false, $"unreadable: {exc.Message}");
            }
            packs.Add(new LibraryPack(relative, pack, LinkedFrom(Path.Combine(destDir, pack.Name + PackSuffix)) == path));
        }
        return packs;
    }

    /// <summary>
    /// Loads a pack from the library: a link under its own name in <paramref name="destDir"/>,
    /// so nothing is copied and it takes effect on the next search. Only a readable pack
    /// built with this server's embedding model: another's vectors would not compare.
    /// </summary>
    public static InstalledPack Load(string libraryDir, string relative, string destDir)
    {
        var root = Path.GetFullPath(libraryDir).TrimEnd('/') + "/";
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.Ordinal) || !full.EndsWith(PackSuffix, StringComparison.Ordinal) || !File.Exists(full))
            throw new RegistryError($"no pack file {PyStr.Repr(relative)} in the pack library");
        var meta = ReadPackMeta(full);
        var name = RequireName(meta.GetValueOrDefault("source_name", ""));
        var (compatible, reason) = Compatibility(meta);
        if (!compatible) throw new RegistryError($"{name} cannot be loaded: {reason}");
        Directory.CreateDirectory(destDir);
        var link = Path.Combine(destDir, $".link-{Guid.NewGuid():N}.tmp");
        File.CreateSymbolicLink(link, full);
        try
        {
            File.Move(link, Path.Combine(destDir, $"{name}{PackSuffix}"), overwrite: true);
        }
        catch
        {
            File.Delete(link);
            throw;
        }
        return new InstalledPack(name, meta.GetValueOrDefault("pack_version", ""), full, meta.GetValueOrDefault("embedding_model", ""),
            meta.GetValueOrDefault("embedding_dim", ""), new FileInfo(full).Length, meta.GetValueOrDefault("license", ""),
            meta.GetValueOrDefault("attribution", ""), meta.GetValueOrDefault("source_commit", ""), true);
    }

    /// <summary>The file a link points to (absolute), or null for a plain file or none.</summary>
    public static string? LinkedFrom(string path)
    {
        return new FileInfo(path).LinkTarget is { } target ? Path.GetFullPath(target, Path.GetDirectoryName(path)!) : null;
    }

    public static bool Remove(string name, string destDir)
    {
        var path = Path.Combine(destDir, $"{RequireName(name)}{PackSuffix}");
        // A link whose library file went away is still there to remove.
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public static List<IndexEntry> FetchIndex(string url)
    {
        HttpResult response;
        try
        {
            using var client = Client(30.0);
            using var resp = client.GetAsync(url).GetAwaiter().GetResult();
            response = new HttpResult((int)resp.StatusCode, resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            throw new RegistryError($"GET {url} failed: {exc.Message}", exc);
        }
        if (response.Status != 200) throw new RegistryError($"GET {url} returned {response.Status}: {PyStr.Prefix(response.Text, 200)}");
        JsonNode? body;
        try { body = JsonNode.Parse(response.Text); }
        catch (JsonException exc) { throw new RegistryError($"GET {url}: index is not JSON: {PyStr.Prefix(response.Text, 200)}", exc); }
        if ((body as JsonObject)?["packs"] is not JsonArray raw) throw new RegistryError($"GET {url}: index has no 'packs' list");
        var entries = new List<IndexEntry>();
        foreach (var item in raw)
        {
            if (item is not JsonObject obj) throw new RegistryError($"GET {url}: index entry is not an object: {item?.ToJsonString()}");
            var missing = new[] { "name", "version", "url", "sha256" }.Where(k => !Truthy(obj[k])).ToList();
            if (missing.Count > 0)
                throw new RegistryError($"GET {url}: index entry {PyStr.Repr(obj["name"]?.ToString() ?? "?")} is missing {string.Join(", ", missing)}");
            entries.Add(new IndexEntry(obj["name"]!.ToString(), obj["version"]!.ToString(), obj["url"]!.ToString(), obj["sha256"]!.ToString(),
                Truthy(obj["size_bytes"]) ? Convert.ToInt64(obj["size_bytes"]!.ToString()) : 0, obj["license"]?.ToString() ?? ""));
        }
        return entries;
    }

    static bool Truthy(JsonNode? n) => n switch
    {
        null => false,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        _ => true,
    };

    public static JsonObject WriteIndex(string destDir, string baseUrl, string? name = null)
    {
        var b = baseUrl.TrimEnd('/');
        var entries = new JsonArray();
        var skipped = new JsonArray();
        foreach (var path in PackFiles(destDir))
        {
            Dictionary<string, string> meta;
            try
            {
                using var conn = PackFormat.OpenPack(path);
                meta = PackFormat.ReadMeta(conn);
            }
            catch (Exception exc)
            {
                skipped.Add(new JsonObject { ["file"] = Path.GetFileName(path), ["reason"] = PyStr.Prefix($"{exc.GetType().Name}: {exc.Message}", 120) });
                continue;
            }
            var entryName = meta.GetValueOrDefault("source_name", "");
            if (name is not null && entryName != name) continue;
            if (entryName.Length == 0)
            {
                skipped.Add(new JsonObject { ["file"] = Path.GetFileName(path), ["reason"] = "pack records no source_name" });
                continue;
            }
            entries.Add(new JsonObject
            {
                ["name"] = entryName,
                ["version"] = meta.GetValueOrDefault("pack_version", ""),
                ["url"] = b.Length > 0 ? $"{b}/{Path.GetFileName(path)}" : Path.GetFileName(path),
                ["sha256"] = Sha256File(path),
                ["size_bytes"] = new FileInfo(path).Length,
                ["license"] = meta.GetValueOrDefault("license", ""),
                ["attribution"] = meta.GetValueOrDefault("attribution", ""),
                ["source_commit"] = meta.GetValueOrDefault("source_commit", ""),
            });
        }
        return new JsonObject { ["schema"] = 1, ["packs"] = entries, ["skipped"] = skipped };
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    static string Stage(string urlOrPath, string staging)
    {
        if (urlOrPath.StartsWith("http://", StringComparison.Ordinal) || urlOrPath.StartsWith("https://", StringComparison.Ordinal))
            return Download(urlOrPath, staging);
        if (!File.Exists(urlOrPath)) throw new RegistryError($"no such pack file: {urlOrPath}");
        using var src = File.OpenRead(urlOrPath);
        using var dst = File.Create(staging);
        return CopyHashing(src, dst);
    }

    static string Download(string url, string staging)
    {
        try
        {
            using var client = Client(300.0);
            using var resp = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if ((int)resp.StatusCode != 200) throw new RegistryError($"GET {url} returned {(int)resp.StatusCode}");
            using var src = resp.Content.ReadAsStream();
            using var dst = File.Create(staging);
            return CopyHashing(src, dst);
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new RegistryError($"GET {url} failed: {exc.Message}", exc);
        }
    }

    static string CopyHashing(Stream src, Stream dst)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[DownloadChunk];
        int n;
        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, n);
            dst.Write(buffer, 0, n);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static Dictionary<string, string> ReadPackMeta(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection conn;
        try { conn = PackFormat.OpenPack(path); }
        catch (Exception exc) { throw new RegistryError($"{Path.GetFileName(path)} is not a readable pack: {exc.Message}", exc); }
        Dictionary<string, string> meta;
        try { meta = PackFormat.ReadMeta(conn); }
        catch (Exception exc) { throw new RegistryError($"{Path.GetFileName(path)} has no readable pack_meta: {exc.Message}", exc); }
        finally { conn.Dispose(); }
        if (meta.Count == 0) throw new RegistryError($"{Path.GetFileName(path)} has an empty pack_meta");
        return meta;
    }

    static string RequireName(string name)
    {
        if (!NameRe.IsMatch(name ?? ""))
            throw new RegistryError(
                $"invalid pack name {PyStr.Repr(name)}: names must match {NameRe} (a pack names its own file, so this is what keeps a downloaded pack inside the directory it was installed into)");
        return name!;
    }

    static (bool, string) Compatibility(Dictionary<string, string> meta)
    {
        try { PackFormat.RequireCompatible(meta, Embed.Model, Embed.Dim); }
        catch (PackMismatch exc) { return (false, exc.Message); }
        return (true, "");
    }
}

/// <summary>
/// A persistent cache of chunk embeddings for pack builds.
/// Every failure degrades to "not cached": the cache is an accelerator, never a
/// dependency.
/// </summary>
public sealed class EmbeddingCache : IDisposable
{
    public const int CacheFormat = 1;
    Microsoft.Data.Sqlite.SqliteConnection? _conn;
    public int Hits { get; private set; }
    public int Misses { get; private set; }

    public static string CacheKey(string text, string model, int dim) =>
        $"{CacheFormat}:{model}:{dim}:{Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))}";

    public EmbeddingCache(string? path)
    {
        if (path is null) return;
        try
        {
            Store.Db.Init();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var conn = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            conn.Open();
            Sql.Script(conn, "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS embeddings (key TEXT PRIMARY KEY, bits BLOB NOT NULL, i8 BLOB NOT NULL);");
            _conn = conn;
        }
        catch (Exception exc) when (exc is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            _conn = null;
        }
    }

    public bool Enabled => _conn is not null;

    public (byte[] Bits, byte[] I8)? Get(string key)
    {
        if (_conn is null) return null;
        Row? row;
        try { row = Sql.One(_conn, "SELECT bits, i8 FROM embeddings WHERE key = ?", key); }
        catch (Microsoft.Data.Sqlite.SqliteException) { return null; }
        if (row is null) { Misses++; return null; }
        Hits++;
        return (row.Bytes("bits")!, row.Bytes("i8")!);
    }

    public void PutMany(IReadOnlyList<(string Key, byte[] Bits, byte[] I8)> rows)
    {
        if (_conn is null || rows.Count == 0) return;
        try
        {
            using var tx = _conn.BeginTransaction();
            foreach (var (k, b, i) in rows)
                Sql.Exec(_conn, "INSERT OR REPLACE INTO embeddings (key, bits, i8) VALUES (?, ?, ?)", k, b, i);
            tx.Commit();
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
    }

    public void Dispose()
    {
        _conn?.Dispose();
        _conn = null;
    }
}
