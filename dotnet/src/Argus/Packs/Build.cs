using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Argus.Indexing;
using Argus.Packs.Sources;
using Argus.Store;
using Argus.Util;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace Argus.Packs;

/// <summary>A pack could not be built.</summary>
public sealed class BuildError(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Building a pack from a documentation source (argus/packs/build.py): fetch,
/// parse, chunk, embed (through a persistent cache), quantise, and write the
/// pack atomically. A rebuild over an existing pack keeps every document whose
/// content hash is unchanged, so a docs refresh costs what changed upstream.
/// </summary>
public static class PackBuilder
{
    public const int BuilderVersion = 1;
    public const int EmbedFlush = 256;
    const int ZstdLevel = 10;
    static readonly string[] DocSuffixes = [".html", ".rst", ".mdx", ".md"];
    public const string ArchiveStamp = ".argus-archive";

    public static string FetchSource(ISource source, string dest)
    {
        if (source.ArchiveUrl.Length > 0) return FetchArchive(source, dest);
        dest = Path.GetFullPath(dest);
        if (Directory.Exists(Path.Combine(dest, ".git")))
        {
            Git(dest, "fetch", "--depth", "1", "origin", source.Branch);
            Git(dest, "checkout", "--force", "FETCH_HEAD");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            Git(Path.GetDirectoryName(dest)!, "clone", "--depth", "1", "--branch", source.Branch, source.RepoUrl, dest);
        }
        return ResolveCommit(dest) ?? "";
    }

    static void SafeMembers(IEnumerable<string> names, string kind)
    {
        foreach (var name in names)
        {
            var posix = name.Replace('\\', '/');
            if (posix.StartsWith('/') || posix.StartsWith("../", StringComparison.Ordinal) || posix.Contains("/../"))
                throw new BuildError($"refusing to extract {kind} member {PyStr.Repr(name)}: escapes the destination directory");
            if (posix.Length > 1 && posix[1] == ':')
                throw new BuildError($"refusing to extract {kind} member {PyStr.Repr(name)}: absolute path");
        }
    }

    public static string FetchArchive(ISource source, string dest)
    {
        dest = Path.GetFullPath(dest);
        var url = source.ArchiveUrl;
        if (url.Length == 0) throw new BuildError($"source {PyStr.Repr(source.Name)} declares no archive_url");
        Directory.CreateDirectory(dest);
        var archive = Path.Combine(dest, ".download" + ArchiveSuffix(url));
        string actual;
        long received = 0;
        long? declared;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(600) };
            using var resp = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            declared = resp.Content.Headers.ContentLength;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var src = resp.Content.ReadAsStream())
            using (var dst = File.Create(archive))
            {
                var buffer = new byte[1 << 20];
                int n;
                while ((n = src.Read(buffer)) > 0)
                {
                    hash.AppendData(buffer, 0, n);
                    dst.Write(buffer, 0, n);
                    received += n;
                }
            }
            actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (Exception exc) when (exc is not BuildError)
        {
            throw new BuildError($"could not download {url}: {exc.Message}", exc);
        }
        if (declared is not null && received != declared)
        {
            File.Delete(archive);
            throw new BuildError(
                $"truncated download of {url}: got {received:N0} bytes of {declared:N0}. A proxy or network interruption cut the transfer; the archive was not unpacked.");
        }
        var expected = source.ArchiveSha256;
        if (expected.Length > 0 && expected.ToLowerInvariant() != actual)
        {
            File.Delete(archive);
            throw new BuildError($"archive digest mismatch for {PyStr.Repr(source.Name)}: expected {expected.ToLowerInvariant()}, got {actual}");
        }
        try
        {
            foreach (var stale in Directory.EnumerateFileSystemEntries(dest))
            {
                if (Path.GetFullPath(stale) == Path.GetFullPath(archive)) continue;
                var info = new DirectoryInfo(stale);
                if (info.Exists && info.LinkTarget is null) { try { Directory.Delete(stale, true); } catch (IOException) { } }
                else File.Delete(stale);
            }
            Extract(archive, dest);
        }
        catch (Exception exc) when (exc is not BuildError)
        {
            throw new BuildError($"could not unpack {url}: {exc.Message}", exc);
        }
        finally
        {
            File.Delete(archive);
        }
        var stamp = $"sha256:{actual}";
        File.WriteAllText(Path.Combine(dest, ArchiveStamp), stamp + "\n");
        return stamp;
    }

    static void Extract(string archive, string dest)
    {
        bool isZip;
        using (var fs = File.OpenRead(archive))
        {
            var magic = new byte[4];
            isZip = fs.Read(magic) == 4 && magic[0] == 'P' && magic[1] == 'K';
        }
        if (isZip)
        {
            using var zip = ZipFile.OpenRead(archive);
            SafeMembers(zip.Entries.Select(e => e.FullName), "zip");
            zip.ExtractToDirectory(dest, overwriteFiles: true);
            return;
        }
        var lower = archive.ToLowerInvariant();
        if (lower.EndsWith(".tar.xz") || lower.EndsWith(".tar.bz2"))
        {
            // .NET has no xz or bzip2 codec; the platform tar has both (bsdtar on Windows).
            var list = RunTool("tar", "-tf", archive);
            SafeMembers(PyStr.SplitLines(list), "tar");
            RunTool("tar", "-xf", archive, "-C", dest, "--no-same-owner");
            return;
        }
        using var file = File.OpenRead(archive);
        Stream stream = lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz") ? new GZipStream(file, CompressionMode.Decompress) : file;
        var entries = new List<(string Name, byte[] Data, TarEntryType Type)>();
        using (var reader = new TarReader(stream))
        {
            TarEntry? entry;
            while ((entry = reader.GetNextEntry(copyData: true)) is not null)
            {
                using var ms = new MemoryStream();
                entry.DataStream?.CopyTo(ms);
                entries.Add((entry.Name, ms.ToArray(), entry.EntryType));
            }
        }
        SafeMembers(entries.Select(e => e.Name), "tar");
        foreach (var (name, data, type) in entries)
        {
            if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink) continue;
            var target = Path.Combine(dest, name.Replace('\\', '/'));
            if (type == TarEntryType.Directory) { Directory.CreateDirectory(target); continue; }
            if (type is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, data);
        }
    }

    static string RunTool(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new BuildError($"{exe} {string.Join(" ", args)} failed: {PyStr.Prefix(PyStr.Strip(err), 400)}");
        return outTask.GetAwaiter().GetResult();
    }

    static string ArchiveSuffix(string url)
    {
        var lowered = url.ToLowerInvariant().Split('?', 2)[0];
        foreach (var suffix in new[] { ".tar.gz", ".tgz", ".tar.xz", ".tar.bz2", ".tar", ".zip" })
            if (lowered.EndsWith(suffix, StringComparison.Ordinal)) return suffix;
        return ".bin";
    }

    static string? ResolveSourceCommit(ISource source, string workDir)
    {
        var stamp = Path.Combine(workDir, ArchiveStamp);
        if (File.Exists(stamp))
        {
            var recorded = PyStr.Strip(File.ReadAllText(stamp));
            if (recorded.Length > 0) return recorded;
        }
        var parts = source.PartCheckouts(workDir);
        if (parts is null) return ResolveCommit(workDir);
        var output = new List<string>();
        foreach (var (name, checkout) in parts)
        {
            var commit = ResolveCommit(checkout);
            if (commit is null) return null;
            output.Add($"{name}={commit}");
        }
        return output.Count > 0 ? string.Join(",", output) : null;
    }

    public static string? ResolveCommit(string workDir)
    {
        string? GitOut(params string[] args)
        {
            try
            {
                var r = Mirror.RunGit(workDir, args);
                return r.Code == 0 ? PyStr.Strip(r.Stdout) : null;
            }
            catch (Exception exc) when (exc is System.ComponentModel.Win32Exception or IOException or GitError) { return null; }
        }
        var toplevel = GitOut("rev-parse", "--show-toplevel");
        if (toplevel is null) return null;
        try
        {
            if (Path.GetFullPath(toplevel).TrimEnd('/', '\\') != Path.GetFullPath(workDir).TrimEnd('/', '\\')) return null;
        }
        catch (IOException) { return null; }
        return GitOut("rev-parse", "HEAD");
    }

    static void Git(string cwd, params string[] args)
    {
        var r = Mirror.RunGit(cwd, args);
        if (r.Code != 0) throw new BuildError($"git {string.Join(" ", args)} failed: {PyStr.Prefix(PyStr.Strip(r.Stderr), 400)}");
    }

    public static string BuildPack(ISource source, string workDir, string outPath, string version,
        Func<IReadOnlyList<string>, List<double[]>>? embedFn = null, string? sourceCommit = null, string? cachePath = null,
        bool useCache = true, bool incremental = true, TextWriter? log = null)
    {
        log ??= Console.Out;
        RequireLicence(source);
        var commit = sourceCommit ?? ResolveSourceCommit(source, workDir);
        if (string.IsNullOrEmpty(commit))
            throw new BuildError(
                $"cannot determine the source commit for {PyStr.Repr(source.Name)}: {workDir} is not a git checkout and no source_commit was given");
        embedFn ??= texts => Embed.EmbedBatch(texts);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        var tempPath = outPath + ".building";
        if (!useCache) cachePath = null;
        else cachePath ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, ".embcache.db");
        bool reusable = incremental && ExistingShas(outPath).Count > 0;
        try
        {
            using (var cache = new EmbeddingCache(cachePath))
            {
                if (reusable)
                {
                    var (kept, rebuilt, removed) = WritePackIncremental(source, workDir, tempPath, outPath, embedFn, version, commit, cache);
                    log.WriteLine($"  incremental: {kept} unchanged, {rebuilt} rebuilt, {removed} removed");
                }
                else WritePack(source, workDir, tempPath, embedFn, version, commit, cache);
                if (cache.Enabled && (cache.Hits > 0 || cache.Misses > 0))
                    log.WriteLine($"  embeddings: {cache.Hits} reused, {cache.Misses} computed");
            }
            File.Move(tempPath, outPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch (IOException) { }
            throw;
        }
        return outPath;
    }

    static void RequireLicence(ISource source)
    {
        var missing = new List<string>();
        if (PyStr.Strip(source.License).Length == 0) missing.Add("license");
        if (PyStr.Strip(source.LicenseUrl).Length == 0) missing.Add("license_url");
        if (PyStr.Strip(source.Attribution).Length == 0) missing.Add("attribution");
        if (missing.Count > 0)
            throw new BuildError($"source {PyStr.Repr(source.Name)} records no {string.Join(", ", missing)}; refusing to build a pack that cannot lawfully be shared");
    }

    static IEnumerable<(string, object?)> MetaFor(ISource source, string version, string commit, long docs, long chunks, long symbols, long unresolved) =>
    [
        ("source_name", source.Name), ("source_repo", source.RepoUrl), ("source_branch", source.Branch),
        ("source_commit", commit), ("license", source.License), ("license_url", source.LicenseUrl),
        ("attribution", source.Attribution), ("embedding_model", Embed.Model), ("embedding_dim", Embed.Dim),
        ("builder_version", BuilderVersion), ("pack_version", version), ("doc_count", docs),
        ("chunk_count", chunks), ("symbol_count", symbols), ("unresolved_symbol_count", unresolved),
    ];

    static void WritePack(ISource source, string workDir, string tempPath, Func<IReadOnlyList<string>, List<double[]>> embedFn,
        string version, string commit, EmbeddingCache cache)
    {
        using var compressor = new Compressor(ZstdLevel);
        using var conn = PackFormat.CreatePack(tempPath);
        using var tx = conn.BeginTransaction();
        var docIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var pending = new List<(long, string)>();
        long chunkTotal = 0;
        foreach (var doc in source.IterDocs(workDir))
        {
            var docId = InsertDoc(conn, doc, compressor);
            docIds[DocKey(doc.Path)] = docId;
            foreach (var chunk in ChunksFor(doc))
            {
                var chunkId = InsertChunk(conn, docId, chunk, compressor);
                pending.Add((chunkId, Chunker.EmbedText(chunk)));
                chunkTotal++;
                if (pending.Count >= EmbedFlush) { FlushEmbeddings(conn, pending, embedFn, cache); pending.Clear(); }
            }
        }
        FlushEmbeddings(conn, pending, embedFn, cache);
        var (symbols, skipped) = InsertSymbols(conn, source, workDir, docIds, null);
        tx.Commit();
        PackFormat.WriteMeta(conn, MetaFor(source, version, commit, docIds.Count, chunkTotal, symbols, skipped));
    }

    static List<Chunk> ChunksFor(Doc doc) => Chunker.ChunkMarkdown(doc.Lang == "rst" ? Chunker.RstToAtx(doc.Body) : doc.Body);

    /// <summary>sha256 over path, title, url, lang and body, NUL-separated: the incremental rebuild's change detector.</summary>
    public static string DocSha(Doc doc)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in new[] { doc.Path, doc.Title ?? "", doc.Url ?? "", doc.Lang ?? "", doc.Body })
        {
            hash.AppendData(Encoding.UTF8.GetBytes(part));
            hash.AppendData([0]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static long InsertDoc(SqliteConnection conn, Doc doc, Compressor compressor)
    {
        var payload = Encoding.UTF8.GetBytes(doc.Body);
        Sql.Exec(conn, "INSERT INTO docs (path, title, url, lang, content, content_len, content_sha) VALUES (?, ?, ?, ?, ?, ?, ?)",
            doc.Path, doc.Title, doc.Url, doc.Lang, compressor.Wrap(payload).ToArray(), (long)payload.Length, DocSha(doc));
        var docId = Convert.ToInt64(Sql.Scalar(conn, "SELECT last_insert_rowid()"));
        Sql.Exec(conn, "INSERT INTO docs_fts (rowid, title, body) VALUES (?, ?, ?)", docId, doc.Title, doc.Body);
        return docId;
    }

    static long InsertChunk(SqliteConnection conn, long docId, Chunk chunk, Compressor compressor)
    {
        Sql.Exec(conn, "INSERT INTO chunks (doc_id, heading_path, anchor, start_line, text) VALUES (?, ?, ?, ?, ?)",
            docId, chunk.HeadingPath, chunk.Anchor, (long)chunk.StartLine, compressor.Wrap(Encoding.UTF8.GetBytes(chunk.Body)).ToArray());
        return Convert.ToInt64(Sql.Scalar(conn, "SELECT last_insert_rowid()"));
    }

    static void FlushEmbeddings(SqliteConnection conn, List<(long ChunkId, string Text)> pending,
        Func<IReadOnlyList<string>, List<double[]>> embedFn, EmbeddingCache? cache)
    {
        if (pending.Count == 0) return;
        var model = Embed.Model;
        var dim = Embed.Dim;
        var resolved = new List<(long, byte[], byte[])>();
        var toEmbed = new List<(long ChunkId, string Key, string Text)>();
        foreach (var (chunkId, text) in pending)
        {
            var key = cache is not null ? EmbeddingCache.CacheKey(text, model, dim) : "";
            var hit = cache?.Get(key);
            if (hit is { } h) resolved.Add((chunkId, h.Bits, h.I8));
            else toEmbed.Add((chunkId, key, text));
        }
        if (toEmbed.Count > 0)
        {
            var vectors = embedFn(toEmbed.Select(t => t.Text).ToList());
            if (vectors.Count != toEmbed.Count)
                throw new BuildError($"embedder returned {vectors.Count} vectors for {toEmbed.Count} chunks -- refusing to store misaligned embeddings");
            var fresh = new List<(string, byte[], byte[])>();
            for (int i = 0; i < toEmbed.Count; i++)
            {
                var bits = Quantize.ToBits(vectors[i]);
                var i8 = Quantize.ToInt8(vectors[i]);
                resolved.Add((toEmbed[i].ChunkId, bits, i8));
                if (cache is not null) fresh.Add((toEmbed[i].Key, bits, i8));
            }
            cache?.PutMany(fresh);
        }
        foreach (var (chunkId, bits, i8) in resolved)
        {
            Sql.Exec(conn, "INSERT INTO vec_bin (chunk_id, embedding) VALUES (?, vec_bit(?))", chunkId, bits);
            Sql.Exec(conn, "INSERT INTO vec_i8 (chunk_id, embedding) VALUES (?, vec_int8(?))", chunkId, i8);
        }
    }

    static (long Written, long Skipped) InsertSymbols(SqliteConnection conn, ISource source, string workDir,
        Dictionary<string, long> docIds, HashSet<string>? changed)
    {
        long written = 0, skipped = 0;
        var changedKeys = changed?.Select(DocKey).ToHashSet(StringComparer.Ordinal);
        foreach (var symbol in source.IterSymbols(workDir))
        {
            var key = DocKey(symbol.DocPath);
            if (changedKeys is not null && !changedKeys.Contains(key)) continue;
            if (!docIds.TryGetValue(key, out var docId)) { skipped++; continue; }
            Sql.Exec(conn, "INSERT INTO api_symbols (name, kind, namespace, doc_id, anchor, signature) VALUES (?, ?, ?, ?, ?, ?)",
                symbol.Name, symbol.Kind, symbol.Namespace, docId, symbol.Anchor, symbol.Signature);
            written++;
        }
        return (written, skipped);
    }

    static string DocKey(string path)
    {
        foreach (var suffix in DocSuffixes)
            if (path.EndsWith(suffix, StringComparison.Ordinal)) return path[..^suffix.Length];
        return path;
    }

    static Dictionary<string, (long Id, string Sha)> ExistingShas(string path)
    {
        var result = new Dictionary<string, (long, string)>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        try
        {
            Db.Init();
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            conn.Open();
            var columns = Sql.Query(conn, "PRAGMA table_info(docs)").Select(r => r.Str("name")).ToHashSet();
            if (!columns.Contains("content_sha")) return result;
            foreach (var row in Sql.Query(conn, "SELECT path, id, content_sha FROM docs WHERE content_sha IS NOT NULL"))
                result[row.Str("path")] = (row.Long("id"), row.Str("content_sha"));
        }
        catch (SqliteException) { result.Clear(); }
        return result;
    }

    static void DropDoc(SqliteConnection conn, long docId)
    {
        var chunkIds = Sql.Query(conn, "SELECT id FROM chunks WHERE doc_id = ?", docId).Select(r => r.Long("id")).ToList();
        foreach (var table in new[] { "vec_bin", "vec_i8" })
            foreach (var chunkId in chunkIds)
                Sql.Exec(conn, $"DELETE FROM {table} WHERE chunk_id = ?", chunkId);
        Sql.Exec(conn, "DELETE FROM chunks WHERE doc_id = ?", docId);
        Sql.Exec(conn, "DELETE FROM api_symbols WHERE doc_id = ?", docId);
        var row = Sql.One(conn, "SELECT title, content FROM docs WHERE id = ?", docId);
        if (row is not null)
            Sql.Exec(conn, "INSERT INTO docs_fts (docs_fts, rowid, title, body) VALUES ('delete', ?, ?, ?)",
                docId, row["title"], PackStore.Decompress(row.Bytes("content")));
        Sql.Exec(conn, "DELETE FROM docs WHERE id = ?", docId);
    }

    static (int Kept, int Rebuilt, int Removed) WritePackIncremental(ISource source, string workDir, string tempPath, string previous,
        Func<IReadOnlyList<string>, List<double[]>> embedFn, string version, string commit, EmbeddingCache cache)
    {
        File.Copy(previous, tempPath, overwrite: true);
        var known = ExistingShas(tempPath);
        using var compressor = new Compressor(ZstdLevel);
        using var conn = PackFormat.OpenPackWritable(tempPath);
        using var tx = conn.BeginTransaction();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var docIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var pending = new List<(long, string)>();
        int kept = 0, rebuilt = 0;
        foreach (var doc in source.IterDocs(workDir))
        {
            seen.Add(doc.Path);
            if (known.TryGetValue(doc.Path, out var entry) && entry.Sha == DocSha(doc))
            {
                docIds[DocKey(doc.Path)] = entry.Id;
                kept++;
                continue;
            }
            if (known.ContainsKey(doc.Path)) DropDoc(conn, known[doc.Path].Id);
            var docId = InsertDoc(conn, doc, compressor);
            docIds[DocKey(doc.Path)] = docId;
            changed.Add(doc.Path);
            rebuilt++;
            foreach (var chunk in ChunksFor(doc))
            {
                var chunkId = InsertChunk(conn, docId, chunk, compressor);
                pending.Add((chunkId, Chunker.EmbedText(chunk)));
                if (pending.Count >= EmbedFlush) { FlushEmbeddings(conn, pending, embedFn, cache); pending.Clear(); }
            }
        }
        FlushEmbeddings(conn, pending, embedFn, cache);
        int removed = 0;
        foreach (var (path, (docId, _)) in known)
        {
            if (seen.Contains(path)) continue;
            DropDoc(conn, docId);
            removed++;
        }
        InsertSymbols(conn, source, workDir, docIds, changed);
        var chunkTotal = Convert.ToInt64(Sql.Scalar(conn, "SELECT count(*) FROM chunks"));
        var symbolTotal = Convert.ToInt64(Sql.Scalar(conn, "SELECT count(*) FROM api_symbols"));
        tx.Commit();
        PackFormat.WriteMeta(conn, MetaFor(source, version, commit, docIds.Count, chunkTotal, symbolTotal, 0));
        return (kept, rebuilt, removed);
    }
}
