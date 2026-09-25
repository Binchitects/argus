using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Indexing;

/// <summary>
/// Resolve <c>#include</c> strings to concrete files, across repos
/// (argus/resolve.py). An include that cannot be pinned to exactly one file is
/// recorded as unresolved with a reason and contributes no edge: a wrong edge
/// silently corrupts <c>repo_deps</c>, and nothing downstream can tell.
/// </summary>
public static class Resolve
{
    public static class Resolution
    {
        public const string Resolved = "resolved";
        public const string External = "external";
        public const string Ambiguous = "ambiguous";
        public const string NotFound = "not_found";
    }

    public readonly record struct FileRow(long Id, long RepoId, string Path);

    /// <summary>Every /-aligned suffix of <paramref name="path"/>, longest first.</summary>
    public static List<string> PathSuffixes(string path)
    {
        var parts = path.Split('/');
        var output = new List<string>(parts.Length);
        for (int i = 0; i < parts.Length; i++) output.Add(string.Join("/", parts, i, parts.Length - i));
        return output;
    }

    public static Dictionary<string, List<FileRow>> BuildSuffixIndex(IEnumerable<FileRow> rows)
    {
        var index = new Dictionary<string, List<FileRow>>(StringComparer.Ordinal);
        foreach (var row in rows)
            foreach (var suffix in PathSuffixes(row.Path))
            {
                if (!index.TryGetValue(suffix, out var list)) index[suffix] = list = [];
                list.Add(row);
            }
        return index;
    }

    public static readonly string[] HeaderSuffixes = [".h", ".hpp", ".hxx", ".hh", ".inl", ".ipp"];

    static bool IsHeader(string path) => HeaderSuffixes.Any(s => path.EndsWith(s, StringComparison.Ordinal));

    /// <summary>Resolve every include in the database. Returns counts by state.</summary>
    public static Dictionary<string, long> ResolveIncludes(SqliteConnection conn)
    {
        var allFiles = Sql.Query(conn, "SELECT id, repo_id, path FROM files")
            .Select(r => new FileRow(r.Long("id"), r.Long("repo_id"), r.Str("path"))).ToList();
        var headers = allFiles.Where(f => IsHeader(f.Path)).ToList();
        var index = BuildSuffixIndex(headers);
        var byRepoPath = new Dictionary<(long, string), FileRow>();
        foreach (var h in headers) byRepoPath[(h.RepoId, h.Path)] = h;

        var repoNamesById = new Dictionary<long, string>();
        var branchOfRepo = new Dictionary<long, string>();
        foreach (var row in Sql.Query(conn, "SELECT id, path_with_namespace, branch FROM repos"))
        {
            repoNamesById[row.Long("id")] = PyStr.AfterLast(row.Str("path_with_namespace"), '/');
            branchOfRepo[row.Long("id")] = row.Str("branch");
        }
        var repoNames = new HashSet<string>(repoNamesById.Values, StringComparer.Ordinal);

        var vendoredDirs = FindVendoredDirs(headers.Concat(allFiles.Where(f => !IsHeader(f.Path))));

        var counts = new Dictionary<string, long>
        {
            [Resolution.Resolved] = 0, [Resolution.External] = 0,
            [Resolution.Ambiguous] = 0, [Resolution.NotFound] = 0,
        };

        var includes = Sql.Query(conn,
            "SELECT i.id, i.repo_id, i.raw, i.is_angle, f.path AS from_path" +
            "  FROM includes i JOIN files f ON f.id = i.file_id");

        using var tx = conn.BeginTransaction();
        using (var update = Sql.Command(conn,
                   "UPDATE includes SET resolved_file_id = ?, resolved_repo_id = ?, " +
                   "is_external = ?, resolution = ? WHERE id = ?", new object?[5]))
        {
            foreach (var inc in includes)
            {
                var (match, state) = ResolveOne(
                    new IncludeRef(inc.Long("repo_id"), inc.Str("raw"), inc.Long("is_angle") != 0, inc.Str("from_path")),
                    index, byRepoPath, repoNames, repoNamesById, vendoredDirs, branchOfRepo);
                counts[state]++;
                update.Parameters[0].Value = match is { } m1 ? m1.Id : DBNull.Value;
                update.Parameters[1].Value = match is { } m2 ? m2.RepoId : DBNull.Value;
                update.Parameters[2].Value = state == Resolution.External ? 1L : 0L;
                update.Parameters[3].Value = state;
                update.Parameters[4].Value = inc.Long("id");
                update.ExecuteNonQuery();
            }
        }

        Sql.Exec(conn, "UPDATE files SET is_vendored = 0 WHERE is_vendored != 0");
        foreach (var (repoId, directory) in vendoredDirs)
            Sql.Exec(conn,
                "UPDATE files SET is_vendored = 1 WHERE repo_id = ? " +
                "  AND (path = ? OR path LIKE ? || '/%')", repoId, directory, directory);
        tx.Commit();
        return counts;
    }

    public const int VendorMinFiles = 4;
    public const double VendorMinShare = 0.6;

    /// <summary>Directories that are a bundled copy of another indexed repository (see resolve.py).</summary>
    public static HashSet<(long RepoId, string Dir)> FindVendoredDirs(IEnumerable<FileRow> rows)
    {
        var byRepo = new Dictionary<long, List<string>>();
        var order = new List<long>();
        foreach (var r in rows)
        {
            if (!byRepo.TryGetValue(r.RepoId, out var list)) { byRepo[r.RepoId] = list = []; order.Add(r.RepoId); }
            list.Add(r.Path);
        }

        var owned = new Dictionary<long, Dictionary<string, int>>();
        foreach (var repoId in order)
        {
            var depths = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var path in byRepo[repoId])
            {
                var name = PyStr.AfterLast(path, '/');
                var depth = PyStr.Count(path, '/');
                if (!depths.TryGetValue(name, out var d) || depth < d) depths[name] = depth;
            }
            owned[repoId] = depths;
        }

        var vendored = new HashSet<(long, string)>();
        foreach (var repoId in order)
        {
            var dirs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var dirOrder = new List<string>();
            foreach (var path in byRepo[repoId])
            {
                var directory = path.Contains('/') ? PyStr.BeforeLast(path, '/') : "";
                if (!dirs.TryGetValue(directory, out var set)) { dirs[directory] = set = new(StringComparer.Ordinal); dirOrder.Add(directory); }
                set.Add(PyStr.AfterLast(path, '/'));
            }
            foreach (var directory in dirOrder)
            {
                var names = dirs[directory];
                if (directory.Length == 0 || names.Count < VendorMinFiles) continue;
                int here = PyStr.Count(directory, '/') + 1;
                foreach (var otherId in order)
                {
                    if (otherId == repoId) continue;
                    var otherNames = owned[otherId];
                    var shared = names.Where(otherNames.ContainsKey).ToList();
                    if (shared.Count < VendorMinFiles) continue;
                    if ((double)shared.Count / names.Count < VendorMinShare) continue;
                    int theirs = shared.Max(n => otherNames[n]);
                    if (theirs < here)
                    {
                        vendored.Add((repoId, directory));
                        break;
                    }
                }
            }
        }
        return vendored;
    }

    /// <summary>True if <paramref name="path"/> sits under a directory named after another indexed repository.</summary>
    public static bool IsVendoredCopy(string path, string ownRepo, IReadOnlySet<string> repoNames)
    {
        var parts = path.Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i] != ownRepo && repoNames.Contains(parts[i])) return true;
        return false;
    }

    static bool InVendoredDir(long repoId, string path, HashSet<(long, string)> vendoredDirs)
    {
        if (vendoredDirs.Count == 0) return false;
        var parts = path.Split('/');
        int n = parts.Length - 1;
        for (int i = n; i > 0; i--)
            if (vendoredDirs.Contains((repoId, string.Join("/", parts, 0, i)))) return true;
        return false;
    }

    static readonly HashSet<string> SystemHeaders = new(PyStr.SplitWhitespace("""
        assert.h complex.h ctype.h errno.h fenv.h float.h inttypes.h iso646.h limits.h
        locale.h math.h setjmp.h signal.h stdalign.h stdarg.h stdatomic.h stdbool.h
        stddef.h stdint.h stdio.h stdlib.h stdnoreturn.h string.h tgmath.h threads.h
        time.h uchar.h wchar.h wctype.h
        aio.h alloca.h byteswap.h cpio.h dirent.h dlfcn.h endian.h err.h fcntl.h
        fmtmsg.h fnmatch.h ftw.h getopt.h glob.h grp.h iconv.h langinfo.h libgen.h
        malloc.h memory.h monetary.h ndbm.h netdb.h nl_types.h paths.h poll.h
        pthread.h pwd.h regex.h sched.h search.h semaphore.h spawn.h stdio_ext.h
        strings.h syslog.h sysexits.h sysinfo.h tar.h termios.h trace.h ulimit.h
        unistd.h utime.h utmpx.h values.h wordexp.h
        socket.h in.h tcp.h inet.h un.h select.h wait.h stat.h ioctl.h mman.h uio.h
        resource.h utsname.h param.h times.h ipc.h shm.h sem.h msg.h statvfs.h
        sockio.h filio.h ttycom.h if.h route.h ip.h
        """), StringComparer.Ordinal);

    static readonly HashSet<string> SystemHeaderDirs = new(StringComparer.Ordinal)
    {
        "sys", "netinet", "arpa", "net", "bits", "asm", "asm-generic",
        "linux", "rpc", "rpcsvc", "scsi", "mtd", "protocols", "xlocale",
    };

    static bool IsSystemHeader(string raw)
    {
        var name = PyStr.LStrip(raw, "./");
        if (SystemHeaders.Contains(name)) return true;
        int slash = name.IndexOf('/');
        if (slash < 0) return false;
        var head = name.Substring(0, slash);
        var rest = name.Substring(slash + 1);
        return rest.Length > 0 && SystemHeaderDirs.Contains(head);
    }

    public readonly record struct IncludeRef(long RepoId, string Raw, bool IsAngle, string FromPath);

    public static (FileRow? Match, string State) ResolveOne(
        IncludeRef inc, Dictionary<string, List<FileRow>> index, Dictionary<(long, string), FileRow> byRepoPath,
        IReadOnlySet<string>? repoNames = null, IReadOnlyDictionary<long, string>? repoNamesById = null,
        HashSet<(long, string)>? vendoredDirs = null, IReadOnlyDictionary<long, string>? branchOfRepo = null)
    {
        repoNames ??= new HashSet<string>();
        repoNamesById ??= new Dictionary<long, string>();
        vendoredDirs ??= [];
        branchOfRepo ??= new Dictionary<long, string>();
        var raw = PyStr.Strip(inc.Raw);

        if (!inc.IsAngle)
        {
            var relative = PosixPath.NormPath(PosixPath.Join(PosixPath.DirName(inc.FromPath), raw));
            if (byRepoPath.TryGetValue((inc.RepoId, relative), out var local)) return (local, Resolution.Resolved);
        }

        var candidates = index.TryGetValue(raw, out var found) ? found.ToList() : [];

        var outside = candidates.Where(c => c.RepoId != inc.RepoId).ToList();
        if (outside.Count > 0)
        {
            var canonical = outside.Where(c =>
                !IsVendoredCopy(c.Path, repoNamesById.GetValueOrDefault(c.RepoId, ""), repoNames)
                && !InVendoredDir(c.RepoId, c.Path, vendoredDirs)).ToList();
            candidates = candidates.Where(c => c.RepoId == inc.RepoId).Concat(canonical).ToList();
        }

        if (candidates.Count == 0)
            return (null, inc.IsAngle ? Resolution.External : Resolution.NotFound);

        var sameRepo = candidates.Where(c => c.RepoId == inc.RepoId).ToList();
        if (sameRepo.Count > 0) candidates = sameRepo;
        else if (IsSystemHeader(raw)) return (null, Resolution.External);

        if (branchOfRepo.Count > 0)
        {
            branchOfRepo.TryGetValue(inc.RepoId, out var want);
            var sameBranch = candidates.Where(c => branchOfRepo.TryGetValue(c.RepoId, out var b) && b == want).ToList();
            if (sameBranch.Count > 0) candidates = sameBranch;
        }

        if (candidates.Count == 1) return (candidates[0], Resolution.Resolved);

        int shortest = candidates.Min(c => PyStr.Count(c.Path, '/'));
        var fewest = candidates.Where(c => PyStr.Count(c.Path, '/') == shortest).ToList();
        if (fewest.Count == 1) return (fewest[0], Resolution.Resolved);

        return (null, Resolution.Ambiguous);
    }
}

/// <summary>The parts of Python's posixpath the resolver needs.</summary>
public static class PosixPath
{
    public static string DirName(string p)
    {
        int i = p.LastIndexOf('/') + 1;
        var head = p.Substring(0, i);
        if (head.Length > 0 && head != new string('/', head.Length)) head = head.TrimEnd('/');
        return head;
    }

    public static string Join(string a, string b)
    {
        if (b.StartsWith('/')) return b;
        if (a.Length == 0 || a.EndsWith('/')) return a + b;
        return a + "/" + b;
    }

    public static string NormPath(string path)
    {
        if (path.Length == 0) return ".";
        int initialSlashes = path.StartsWith('/') ? 1 : 0;
        if (initialSlashes == 1 && path.StartsWith("//") && !path.StartsWith("///")) initialSlashes = 2;
        var comps = path.Split('/');
        var newComps = new List<string>();
        foreach (var comp in comps)
        {
            if (comp.Length == 0 || comp == ".") continue;
            if (comp != ".." || (initialSlashes == 0 && newComps.Count == 0) || (newComps.Count > 0 && newComps[^1] == ".."))
                newComps.Add(comp);
            else if (newComps.Count > 0)
                newComps.RemoveAt(newComps.Count - 1);
        }
        var result = string.Join("/", newComps);
        if (initialSlashes > 0) result = new string('/', initialSlashes) + result;
        return result.Length == 0 ? "." : result;
    }
}
