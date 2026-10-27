using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Argus.Configuration;
using Argus.Indexing;
using Argus.Store;
using Argus.Util;

namespace Argus.CodeHosts;

/// <summary>A file at a commit: its text, or why there is none to show.</summary>
public sealed record FileAtCommit(string Text, long Size, bool Binary, bool TooLarge);

/// <summary>One line <c>git grep</c> found.</summary>
public sealed record GrepHit(string Path, long Line, string Text);

/// <summary>
/// Code at any commit of a project, from its git mirror: a merge request's head, a branch that is not
/// indexed, a commit. What is missing is fetched by ref, never by a bare commit id, so a mirror holds
/// only what the project's own refs reach. A project left out of the index gets a fetch-only mirror (a bare
/// repository, nothing indexed from it). A merge request's head is kept as refs/arena/mr/N and remembered
/// in mr_refs, so it is dropped again when the merge request is merged or closed, or after
/// <see cref="UnusedDays"/> days unused. Fetches into one mirror run one at a time.
/// </summary>
public static partial class CodeAt
{
    public const int UnusedDays = 30;
    /// <summary>A file larger than this is not read: its size is said instead.</summary>
    public const long MaxBlob = 2 * 1024 * 1024;
    const string ArenaRefs = "refs/arena/mr/";

    static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.Ordinal);

    static object LockFor(string mirror) => Locks.GetOrAdd(Path.GetFullPath(mirror), _ => new object());

    /// <summary>A branch or tag name a caller may give, or a commit id; never an option, a range or a reflog entry.</summary>
    public static bool ValidRev(string rev) =>
        RevShape().IsMatch(rev) && !rev.Contains("..", StringComparison.Ordinal) && !rev.EndsWith(".lock", StringComparison.Ordinal)
        && !rev.EndsWith('/') && !rev.Contains("//", StringComparison.Ordinal);

    /// <summary>A whole or abbreviated commit id.</summary>
    public static bool IsSha(string rev) => ShaShape().IsMatch(rev);

    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9_./+\-]{0,250}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RevShape();

    [GeneratedRegex(@"\A[0-9a-fA-F]{7,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ShaShape();

    /// <summary>
    /// The project's mirror: the indexer's, when it has one, else a bare repository made for fetching only.
    /// Its remote is pointed at <paramref name="cloneUrl"/> either way.
    /// </summary>
    public static string Mirror(IndexConfig index, long gitlabId, string cloneUrl)
    {
        var path = Indexing.Mirror.MirrorPath(index, gitlabId);
        lock (LockFor(path))
        {
            if (Directory.Exists(path))
            {
                var current = Indexing.Mirror.RunGit(path, ["config", "--get", "remote.origin.url"]);
                if (current.Code != 0) Indexing.Mirror.Git(path, ["remote", "add", "origin", cloneUrl]);
                else if (PyStr.Strip(current.Stdout) != cloneUrl) Indexing.Mirror.Git(path, ["remote", "set-url", "origin", cloneUrl]);
                return path;
            }
            // Made aside and moved into place, so the indexer never finds half a repository.
            Directory.CreateDirectory(index.MirrorsDir);
            var aside = Path.Combine(index.MirrorsDir, $".{gitlabId}.{Guid.NewGuid():N}.git");
            try
            {
                Indexing.Mirror.Git(index.MirrorsDir, ["init", "--quiet", "--bare", aside]);
                Indexing.Mirror.Git(aside, ["remote", "add", "origin", cloneUrl]);
                Directory.Move(aside, path);
            }
            catch (IOException) when (Directory.Exists(path))
            {
                // The indexer made it meanwhile: that one is the mirror.
            }
            finally
            {
                if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
            }
            return path;
        }
    }

    /// <summary>The commit a name or id stands for in the mirror; null when it has none.</summary>
    public static string? Resolve(string mirror, string rev)
    {
        var r = Indexing.Mirror.RunGit(mirror, ["rev-parse", "--verify", "--quiet", "--end-of-options", rev + "^{commit}"]);
        return r.Code == 0 ? PyStr.Strip(r.Stdout) : null;
    }

    /// <summary>
    /// Fetch these refspecs from the project's GitLab with the service credential: false when it has no such
    /// ref; a <see cref="GitError"/> (the token redacted) when it could not be asked.
    /// </summary>
    static bool Fetch(IndexConfig index, GitLabConfig gitlab, string mirror, params string[] refspecs)
    {
        var token = Credentials.GitPassword(gitlab);
        var env = Indexing.Mirror.AuthEnv(index, token, gitlab);
        string[] auth = string.IsNullOrEmpty(token) ? [] : ["-c", "credential.helper="];
        Indexing.Mirror.GitResult r;
        lock (LockFor(mirror))
            r = Indexing.Mirror.RunGit(mirror, [.. auth, "fetch", "--quiet", "--no-tags", "--no-write-fetch-head", "origin", .. refspecs], env);
        if (r.Code == 0) return true;
        if (r.Stderr.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase)) return false;
        var stderr = PyStr.Prefix(PyStr.Strip(r.Stderr), 300);
        if (!string.IsNullOrEmpty(token)) stderr = stderr.Replace(token, "***", StringComparison.Ordinal);
        throw new GitError($"git fetch {string.Join(" ", refspecs)} failed: {stderr}");
    }

    /// <summary>
    /// The commit of a branch, a tag or a commit id, fetched first when it is a name (a branch moves) or
    /// missing (a commit on a branch fetched since); null when the project has nothing by that name.
    /// </summary>
    public static string? Rev(IndexConfig index, GitLabConfig gitlab, string mirror, string rev)
    {
        if (IsSha(rev))
        {
            if (Resolve(mirror, rev) is { } known) return known;
            Fetch(index, gitlab, mirror, "+refs/heads/*:refs/heads/*");
            if (Resolve(mirror, rev) is { } onBranch) return onBranch;
            Fetch(index, gitlab, mirror, "+refs/tags/*:refs/tags/*");
            return Resolve(mirror, rev);
        }
        var name = rev.StartsWith("refs/", StringComparison.Ordinal) ? rev : null;
        if (name is null)
        {
            if (Fetch(index, gitlab, mirror, $"+refs/heads/{rev}:refs/heads/{rev}")) return Resolve(mirror, $"refs/heads/{rev}");
            if (Fetch(index, gitlab, mirror, $"+refs/tags/{rev}:refs/tags/{rev}")) return Resolve(mirror, $"refs/tags/{rev}");
            return null;
        }
        // A full ref name: only branches and tags, never another ref the mirror holds.
        if (!name.StartsWith("refs/heads/", StringComparison.Ordinal) && !name.StartsWith("refs/tags/", StringComparison.Ordinal)) return null;
        return Fetch(index, gitlab, mirror, $"+{name}:{name}") ? Resolve(mirror, name) : null;
    }

    /// <summary>
    /// A merge request's head, fetched into the mirror as refs/arena/mr/N and remembered with when it was used;
    /// refs unused for <see cref="UnusedDays"/> days are dropped first. Null when the host has no such ref.
    /// </summary>
    public static string? Change(IndexConfig index, GitLabConfig gitlab, string dbPath, string mirror, ICodeHost host, long gitlabId, long number, long now)
    {
        Prune(dbPath, mirror, gitlabId, now);
        var local = ArenaRefs + number;
        if (!Fetch(index, gitlab, mirror, $"+{host.ChangeRef(number)}:{local}")) return null;
        var sha = Resolve(mirror, local);
        if (sha is null) return null;
        Bookkeeping("remember a merge request's head", () =>
        {
            using var conn = Db.Connect(dbPath);
            Sql.Exec(conn, """
                INSERT INTO mr_refs (gitlab_id, iid, sha, fetched_at, used_at) VALUES (?, ?, ?, ?, ?)
                ON CONFLICT(gitlab_id, iid) DO UPDATE SET sha = excluded.sha, fetched_at = excluded.fetched_at, used_at = excluded.used_at
                """, gitlabId, number, sha, now, now);
        });
        return sha;
    }

    /// <summary>
    /// Keeping track of the heads fetched never fails a read: the index database may be held by the indexer's
    /// writes past its busy timeout. A head not remembered is fetched again when next asked for.
    /// </summary>
    static void Bookkeeping(string what, Action write)
    {
        try { write(); }
        catch (Microsoft.Data.Sqlite.SqliteException exc) { Console.Error.WriteLine($"could not {what}: {exc.Message}"); }
    }

    /// <summary>The merge requests of this project whose heads are kept.</summary>
    public static HashSet<long> Kept(string dbPath, long gitlabId)
    {
        using var conn = Db.ConnectReadonly(dbPath);
        return Sql.Query(conn, "SELECT iid FROM mr_refs WHERE gitlab_id = ?", gitlabId).Select(r => r.Long("iid")).ToHashSet();
    }

    /// <summary>Merge requests merged or closed: their heads are not kept.</summary>
    public static void Forget(string dbPath, string mirror, long gitlabId, IEnumerable<long> numbers) =>
        Bookkeeping("drop merge request heads", () =>
        {
            using var conn = Db.Connect(dbPath);
            foreach (var number in numbers)
                Drop(conn, mirror, gitlabId, number);
        });

    static void Drop(Microsoft.Data.Sqlite.SqliteConnection conn, string mirror, long gitlabId, long number)
    {
        // The mirror may be gone already: the indexer removes those of projects GitLab no longer lists.
        if (Directory.Exists(mirror))
            lock (LockFor(mirror)) Indexing.Mirror.RunGit(mirror, ["update-ref", "-d", ArenaRefs + number]);
        Sql.Exec(conn, "DELETE FROM mr_refs WHERE gitlab_id = ? AND iid = ?", gitlabId, number);
    }

    /// <summary>Drop the merge request heads of this project unused for <see cref="UnusedDays"/> days.</summary>
    public static void Prune(string dbPath, string mirror, long gitlabId, long now) =>
        Bookkeeping("drop unused merge request heads", () =>
        {
            using var conn = Db.Connect(dbPath);
            var stale = Sql.Query(conn, "SELECT iid FROM mr_refs WHERE gitlab_id = ? AND used_at < ?", gitlabId, now - UnusedDays * 86400L)
                .Select(r => r.Long("iid")).ToList();
            foreach (var iid in stale) Drop(conn, mirror, gitlabId, iid);
        });

    /// <summary>Every file's path at a commit.</summary>
    public static List<string> ListFiles(string mirror, string sha)
    {
        var r = Indexing.Mirror.RunGit(mirror, ["ls-tree", "-r", "--name-only", "-z", sha]);
        if (r.Code != 0) throw new GitError($"git ls-tree failed: {PyStr.Prefix(PyStr.Strip(r.Stderr), 300)}");
        return [.. r.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>A file at a commit; null when the commit has no such file (a directory is not one).</summary>
    public static FileAtCommit? Show(string mirror, string sha, string path)
    {
        var spec = $"{sha}:{path}";
        var type = Indexing.Mirror.RunGit(mirror, ["cat-file", "-t", spec]);
        if (type.Code != 0 || PyStr.Strip(type.Stdout) != "blob") return null;
        var size = Indexing.Mirror.RunGit(mirror, ["cat-file", "-s", spec]);
        if (size.Code != 0 || !long.TryParse(PyStr.Strip(size.Stdout), out var bytes)) return null;
        if (bytes > MaxBlob) return new FileAtCommit("", bytes, false, true);
        var text = Indexing.Mirror.Git(mirror, ["cat-file", "blob", spec]);
        return text.Contains('\0') ? new FileAtCommit("", bytes, true, false) : new FileAtCommit(text, bytes, false, false);
    }

    /// <summary>
    /// Lines matching <paramref name="pattern"/> (an extended regular expression, or fixed text) at a commit,
    /// in text files under <paramref name="pathspec"/>; at most <paramref name="limit"/>, and whether there were more.
    /// </summary>
    public static (List<GrepHit> Hits, bool More) Grep(string mirror, string sha, string pattern, string? pathspec, bool ignoreCase, bool fixedText, int limit)
    {
        List<string> args = ["grep", "-n", "-I", "--no-color", "--full-name", "-z", fixedText ? "-F" : "-E"];
        if (ignoreCase) args.Add("-i");
        args.AddRange(["--max-count", (limit + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "-e", pattern, sha, "--"]);
        if (!string.IsNullOrEmpty(pathspec)) args.Add(pathspec);
        var r = Indexing.Mirror.RunGit(mirror, args);
        if (r.Code == 1) return ([], false);
        if (r.Code != 0) throw new GitError($"git grep failed: {PyStr.Prefix(PyStr.Strip(r.Stderr), 300)}");
        var hits = new List<GrepHit>();
        var prefix = sha + ":";
        foreach (var record in r.Stdout.Split('\n'))
        {
            if (record.Length == 0) continue;
            var parts = record.Split('\0', 3);
            if (parts.Length < 3 || !long.TryParse(parts[1], out var line)) continue;
            var file = parts[0].StartsWith(prefix, StringComparison.Ordinal) ? parts[0][prefix.Length..] : parts[0];
            if (hits.Count == limit) return (hits, true);
            hits.Add(new GrepHit(file, line, parts[2].Length > 400 ? parts[2][..400] : parts[2]));
        }
        return (hits, false);
    }
}
