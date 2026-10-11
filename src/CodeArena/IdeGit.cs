using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// The IDE's Source Control: the folder's git as the person works it (not the agent's turn commits): what changed since
/// the last commit, staged or not; a file as any commit or the index has it; stage, unstage, discard and commit; the
/// branches, the last commits, a file's history and what a commit changed. Paths are the folder's, with "/"; a
/// repository bigger than the folder shows the folder's files only. The person's own name commits, as their git is set up.
/// </summary>
internal sealed partial class IdeGit(Workspace workspace)
{
    /// <summary>The most files listed.</summary>
    public const int MostFiles = 2_000;

    /// <summary>The most commits a log lists.</summary>
    public const int MostCommits = 1_000;

    private static Task<(int Code, string Output)> Git(string dir, IReadOnlyList<string> args, CancellationToken ct, string? input = null) =>
        TurnCommits.GitAsync(dir, args, ct, input);

    /// <summary>
    /// A revision the page may name: a hash, a branch, a tag, main~2 or HEAD^; never an option (no "-" first), no space,
    /// no ":" (\z, not $, which would let a line break through at the end).
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/@{}~^-]{0,199}\z")]
    private static partial Regex Revision();

    /// <summary>The repository's top folder; null: the folder is in none (or git is not there).</summary>
    private async Task<string?> RootAsync(CancellationToken ct)
    {
        var (code, output) = await Git(workspace.Root, ["rev-parse", "--show-toplevel"], ct);
        return code == 0 && output.Trim() is { Length: > 0 } root ? Path.GetFullPath(root) : null;
    }

    private async Task<string> RequireRootAsync(CancellationToken ct) =>
        await RootAsync(ct) ?? throw new IdeError(404, "no_git", "This folder is in no git repository (git init makes one).");

    /// <summary>A path of the repository's (git's, from its top) as the folder's; null: outside the folder.</summary>
    private string? Ours(string root, string gitPath)
    {
        var rel = Path.GetRelativePath(workspace.Root, Path.GetFullPath(Path.Combine(root, gitPath)));
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? null : rel.Replace('\\', '/');
    }

    /// <summary>The folder's paths checked (inside it, no "..") for git, as the folder has them.</summary>
    private List<string> Paths(JsonObject body)
    {
        var paths = (body["paths"] as JsonArray ?? []).Select(p => p is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().Where(p => p.Length > 0).ToList();
        if (paths.Count == 0)
        {
            throw new IdeError(400, "invalid", "Say which files.");
        }
        foreach (var p in paths)
        {
            try
            {
                workspace.Resolve(p);
            }
            catch (ToolError e)
            {
                throw new IdeError(403, "outside", e.Message);
            }
        }
        return paths;
    }

    /// <summary>
    /// The branch, how far it is from its upstream, and each changed file: its state in the index (staged) and in the
    /// folder (not), as git's letters (M, A, D, R, ? for untracked).
    /// </summary>
    public async Task<JsonObject> StatusAsync(CancellationToken ct)
    {
        if (await RootAsync(ct) is not { } root)
        {
            return new JsonObject { ["repository"] = false };
        }
        var (_, branch) = await Git(workspace.Root, ["rev-parse", "--abbrev-ref", "HEAD"], ct);
        var (counted, counts) = await Git(workspace.Root, ["rev-list", "--left-right", "--count", "@{upstream}...HEAD"], ct);
        var (_, status) = await Git(workspace.Root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], ct);
        var files = new JsonArray();
        var entries = status.Split('\0');
        for (var i = 0; i < entries.Length && files.Count < MostFiles; i++)
        {
            var e = entries[i];
            if (e.Length < 4)
            {
                continue;
            }
            var (x, y, path) = (e[0], e[1], e[3..]);
            string? from = null;
            if (x is 'R' or 'C')
            {
                // A rename: the next entry is where it was.
                from = i + 1 < entries.Length ? entries[++i] : null;
            }
            if (Ours(root, path) is not { } ours)
            {
                continue;
            }
            files.Add(new JsonObject
            {
                ["path"] = ours,
                ["staged"] = x is ' ' or '?' ? null : x.ToString(),
                ["changed"] = y == ' ' ? null : y == '?' ? "?" : y.ToString(),
                ["from"] = from is null ? null : Ours(root, from),
            });
        }
        var behindAhead = counted == 0 ? counts.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) : [];
        return new JsonObject
        {
            ["repository"] = true,
            ["branch"] = branch.Trim() is "HEAD" or "" ? null : branch.Trim(),
            ["behind"] = behindAhead.Length == 2 ? int.Parse(behindAhead[0]) : null,
            ["ahead"] = behindAhead.Length == 2 ? int.Parse(behindAhead[1]) : null,
            ["files"] = files,
        };
    }

    /// <summary>A file at the last commit (null: it is new) and now in the folder (null: it is deleted).</summary>
    public async Task<JsonObject> TextsAsync(string? path, CancellationToken ct)
    {
        await RequireRootAsync(ct);
        if (string.IsNullOrEmpty(path))
        {
            throw new IdeError(400, "invalid", "Say which file.");
        }
        string full;
        try
        {
            full = workspace.Resolve(path);
        }
        catch (ToolError e)
        {
            throw new IdeError(403, "outside", e.Message);
        }
        var (code, before) = await Git(workspace.Root, ["show", $"HEAD:./{path}"], ct);
        return new JsonObject
        {
            ["path"] = path,
            ["original"] = code == 0 ? before : null,
            ["modified"] = File.Exists(full) ? await File.ReadAllTextAsync(full, ct) : null,
        };
    }

    /// <summary>Stages files (all of what changed in them, a deleted one's deletion too).</summary>
    public async Task StageAsync(JsonObject body, CancellationToken ct) =>
        Check(await Git(workspace.Root, ["--literal-pathspecs", "add", "-A", "--", .. Paths(body)], ct), "staged");

    /// <summary>
    /// Stages exactly this text as the file's (part of its changes, as a compare's Stage Change does), the working tree
    /// left as it is: hashed as <c>git add</c> would (the file's clean filters apply, and its byte-order mark is kept),
    /// with the mode the index has (100755 stays executable; a new file is 100644). A link or a submodule is refused.
    /// </summary>
    public async Task StageTextAsync(string? full, string text, CancellationToken ct)
    {
        var root = await RequireRootAsync(ct);
        var name = await InRepoAsync(full, ct);
        var file = full!;
        if (Directory.Exists(file))
        {
            throw new IdeError(400, "folder", $"{workspace.Show(file)} is a folder.");
        }
        var (listed, entries) = await Git(root, ["--literal-pathspecs", "ls-files", "-s", "-z", "--", name], ct);
        Check((listed, entries), "staged");
        // "<mode> <object> <stage>\t<path>" each: the file's first stage when it is in conflict, none when it is new.
        var entry = entries.Split('\0').FirstOrDefault(e => e.EndsWith('\t' + name, StringComparison.Ordinal));
        var mode = entry is null ? "100644" : entry.Split(' ')[0];
        if (mode is not ("100644" or "100755"))
        {
            throw new IdeError(409, "not_text", $"git keeps {workspace.Show(file)} as a link or a submodule: stage it whole.");
        }
        var (hashed, said) = await Git(root, ["hash-object", "-w", "--stdin", $"--path={name}"], ct, Bom(file) ? (char)0xFEFF + text : text);
        Check((hashed, said), "staged");
        // What git said after the hash (a line-ending warning) is not part of it.
        var hash = said.Split('\n')[0].Trim();
        Check(await Git(root, ["update-index", "--add", "--cacheinfo", $"{mode},{hash},{name}"], ct), "staged");
    }

    /// <summary>Whether the file in the folder begins with a UTF-8 byte-order mark (which the editor's text leaves out).</summary>
    private static bool Bom(string full)
    {
        if (!File.Exists(full) || !Files.Regular(full))
        {
            return false;
        }
        Span<byte> start = stackalloc byte[3];
        using var file = File.OpenRead(full);
        return file.ReadAtLeast(start, 3, throwOnEndOfStream: false) == 3 && start is [0xEF, 0xBB, 0xBF];
    }

    /// <summary>Takes files out of what the next commit has (their changes stay in the folder).</summary>
    public async Task UnstageAsync(JsonObject body, CancellationToken ct)
    {
        var paths = Paths(body);
        var (hasHead, _) = await Git(workspace.Root, ["rev-parse", "--verify", "-q", "HEAD"], ct);
        // Before the first commit there is nothing to go back to: the files leave the index.
        Check(hasHead == 0
            ? await Git(workspace.Root, ["--literal-pathspecs", "reset", "-q", "HEAD", "--", .. paths], ct)
            : await Git(workspace.Root, ["--literal-pathspecs", "rm", "-q", "--cached", "-r", "--", .. paths], ct), "unstaged");
    }

    /// <summary>Puts files back as the index has them (as the last commit, when not staged); a file git does not know is deleted.</summary>
    public async Task DiscardAsync(JsonObject body, CancellationToken ct)
    {
        var paths = Paths(body);
        var root = await RequireRootAsync(ct);
        var (_, known) = await Git(workspace.Root, ["--literal-pathspecs", "ls-files", "-z", "--full-name", "--", .. paths], ct);
        var tracked = known.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(p => Ours(root, p) ?? p).ToHashSet(StringComparer.Ordinal);
        var restore = paths.Where(tracked.Contains).ToList();
        if (restore.Count > 0)
        {
            Check(await Git(workspace.Root, ["--literal-pathspecs", "checkout", "-q", "--", .. restore], ct), "put back");
        }
        foreach (var p in paths.Where(p => !tracked.Contains(p)))
        {
            var full = workspace.Resolve(p);
            if (File.Exists(full))
            {
                File.Delete(full);
            }
        }
    }

    /// <summary>Commits what is staged (all of what changed, staged first, with <c>all</c>), with the message: its short hash.</summary>
    public async Task<string> CommitAsync(JsonObject body, CancellationToken ct)
    {
        var message = body.Str("message")?.Trim();
        if (string.IsNullOrEmpty(message))
        {
            throw new IdeError(400, "invalid", "Write the commit's message first.");
        }
        await RequireRootAsync(ct);
        if (body.Bool("all") == true)
        {
            Check(await Git(workspace.Root, ["add", "-A", "--", "."], ct), "staged");
        }
        var (staged, _) = await Git(workspace.Root, ["diff", "--cached", "--quiet"], ct);
        if (staged == 0)
        {
            throw new IdeError(409, "nothing", "Nothing is staged to commit: stage the files first, or commit all.");
        }
        Check(await Git(workspace.Root, ["commit", "-q", "-F", "-"], ct, message), "committed");
        var (_, hash) = await Git(workspace.Root, ["rev-parse", "--short", "HEAD"], ct);
        return hash.Trim();
    }

    /// <summary>
    /// The last commits (<paramref name="limit"/> of them, 30 unless said): short hash, subject, author and when. With a
    /// file (<paramref name="full"/>): the commits that changed it, followed across renames, each also with what
    /// happened to it there (<c>status</c>, git's letter), its name in that commit (<c>pathAt</c>) and, at a rename, its
    /// name before (<c>from</c>); either is null where it was outside the folder. None before the first commit.
    /// </summary>
    public async Task<JsonArray> LogAsync(string? full, string? limit, CancellationToken ct)
    {
        var most = 30;
        if (!string.IsNullOrEmpty(limit) && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out most) || most < 1))
        {
            throw new IdeError(400, "invalid", "The limit is a number of commits, 1 or more.");
        }
        most = Math.Min(most, MostCommits);
        if (await RootAsync(ct) is not { } root)
        {
            return [];
        }
        if (full is null)
        {
            var (code, output) = await Git(workspace.Root, ["-c", "log.showSignature=false", "log", "-n", Number(most), "--pretty=format:%h%x1f%s%x1f%an%x1f%aI%x1e"], ct);
            return code != 0 ? [] : new JsonArray([.. output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries).Select(r => r.Trim('\n').Split('\x1f')).Where(f => f.Length == 4)
                .Select(f => (JsonNode)new JsonObject { ["hash"] = f[0], ["subject"] = f[1], ["author"] = f[2], ["at"] = f[3] })]);
        }
        var name = await InRepoAsync(full, ct);
        if (Directory.Exists(full))
        {
            // --follow follows one file: a folder's commits would each name the first file they changed in it.
            throw new IdeError(400, "folder", $"{workspace.Show(full)} is a folder: its files have a history, one at a time.");
        }
        var (followed, history) = await Git(root, ["-c", "log.showSignature=false", "--literal-pathspecs", "log", "--follow", "--name-status", "-z", "-n", Number(most),
            "--pretty=format:%x1e%h%x1f%s%x1f%an%x1f%aI", "--", name], ct);
        var log = new JsonArray();
        if (followed != 0)
        {
            return log;
        }
        foreach (var record in history.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var end = record.IndexOfAny(['\n', '\0']);
            var head = (end < 0 ? record : record[..end]).Split('\x1f');
            if (head.Length != 4)
            {
                continue;
            }
            // With -z each field ends with a NUL: the letter (R100 for a rename), then the path, or the old and the new one.
            var fields = (end < 0 ? "" : record[end..]).TrimStart('\n', '\0').Split('\0');
            string? status = null;
            string? from = null;
            var at = name;
            if (fields.Length >= 2 && fields[0].Length > 0 && char.IsAsciiLetterUpper(fields[0][0]))
            {
                status = fields[0][..1];
                (from, at) = status is "R" or "C" && fields.Length >= 3 ? (fields[1], fields[2]) : (null, fields[1]);
            }
            log.Add(new JsonObject
            {
                ["hash"] = head[0],
                ["subject"] = head[1],
                ["author"] = head[2],
                ["at"] = head[3],
                ["status"] = status,
                ["pathAt"] = Ours(root, at),
                ["from"] = from is null ? null : Ours(root, from),
            });
            // The commits before a rename know the file by its old name (a merge, which has no letter, by the one it has).
            name = from ?? at;
        }
        return log;
    }

    /// <summary>
    /// A commit: its full hash, its parents', subject, author and when, and the files of the folder it changed (as git's
    /// letters; <c>from</c>: where a renamed one was, null outside the folder), against its first parent. A root commit
    /// adds every file it has. <c>truncated</c>: more files changed than are listed.
    /// </summary>
    public async Task<JsonObject> CommitFilesAsync(string? rev, CancellationToken ct)
    {
        var root = await RequireRootAsync(ct);
        if (string.IsNullOrEmpty(rev))
        {
            throw new IdeError(400, "invalid", "Say which commit.");
        }
        var hash = await CommitOfAsync(rev, ct) ?? throw NoRevision(rev);
        var (code, head) = await Git(root, ["-c", "log.showSignature=false", "show", "-s", "--format=%H%x1f%P%x1f%s%x1f%an%x1f%aI", hash], ct);
        var about = head.Split('\n')[0].Split('\x1f');
        if (code != 0 || about.Length != 5)
        {
            throw new IdeError(409, "git", $"git could not show the commit: {Fmt.OneLine(head.Trim(), 400)}");
        }
        var parents = about[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // A merge: what it brought into its first parent, as the history shows it.
        var (listed, changes) = await Git(root, parents.Length > 1
            ? ["diff-tree", "-r", "-z", "-M", "--name-status", parents[0], hash]
            : ["diff-tree", "-r", "-z", "-M", "--root", "--no-commit-id", "--name-status", hash], ct);
        if (listed != 0)
        {
            throw new IdeError(409, "git", $"git could not list the commit's files: {Fmt.OneLine(changes.Trim(), 400)}");
        }
        var files = new JsonArray();
        var truncated = false;
        var fields = changes.Split('\0');
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            var status = fields[i];
            if (status.Length == 0 || !char.IsAsciiLetterUpper(status[0]))
            {
                break;
            }
            string? from = null;
            var path = fields[i + 1];
            if (status[0] is 'R' or 'C' && i + 2 < fields.Length)
            {
                from = path;
                path = fields[i + 2];
                i++;
            }
            if (Ours(root, path) is not { } ours)
            {
                continue;
            }
            if (files.Count == MostFiles)
            {
                truncated = true;
                break;
            }
            files.Add(new JsonObject { ["path"] = ours, ["status"] = status[..1], ["from"] = from is null ? null : Ours(root, from) });
        }
        return new JsonObject
        {
            ["hash"] = about[0],
            ["parents"] = new JsonArray([.. parents.Select(p => (JsonNode)p)]),
            ["subject"] = about[2],
            ["author"] = about[3],
            ["at"] = about[4],
            ["files"] = files,
            ["truncated"] = truncated,
        };
    }

    /// <summary>
    /// A file as a revision has it: <c>HEAD</c>, <c>:</c> (the index) or a commit-ish (a hash, a branch, a tag, main~2).
    /// Its text, or why there is none: not there (<c>exists</c> false; HEAD before the first commit too), binary or not
    /// UTF-8, or larger than the editor opens. <c>commit</c>: the revision's full hash (null for the index). A revision
    /// git does not know is 404; one that could be read as an option, or with a space or a ":" in it, is 400.
    /// </summary>
    public async Task<JsonObject> ShowAsync(string? full, string? rev, CancellationToken ct)
    {
        var root = await RequireRootAsync(ct);
        var name = await InRepoAsync(full, ct);
        rev = string.IsNullOrEmpty(rev) ? "HEAD" : rev;
        var commit = rev == ":" ? null : await CommitOfAsync(rev, ct);
        var result = new JsonObject
        {
            ["path"] = workspace.Show(full!),
            ["rev"] = rev,
            ["commit"] = commit,
            ["exists"] = false,
            ["size"] = null,
            ["text"] = null,
            ["binary"] = false,
            ["tooLarge"] = false,
        };
        if (commit is null && rev != ":")
        {
            // HEAD names no commit only before the first one: nothing is there yet.
            return rev == "HEAD" ? result : throw NoRevision(rev);
        }
        // git reads the question as a line, a carriage return before its end dropped: x\r would be read as x.
        if (name.AsSpan().IndexOfAny('\n', '\r') >= 0)
        {
            throw new IdeError(400, "invalid", "git cannot be asked for a file with a line break in its name.");
        }
        // Stage 0 named: a path like 1:x is a file's name, not stage 1 of x.
        var (_, found) = await Git(root, ["cat-file", "--batch-check"], ct, $"{(commit is null ? ":0" : commit)}:{name}\n");
        // "<object> blob <size>"; a folder is a tree and a submodule's commit is not in this repository: no file either way.
        var parts = found.Split('\n')[0].Split(' ');
        if (parts.Length != 3 || parts[1] != "blob" || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
        {
            return result;
        }
        result["exists"] = true;
        result["size"] = size;
        if (size > IdeFiles.MaxTextBytes)
        {
            result["tooLarge"] = true;
            return result;
        }
        var bytes = await BlobAsync(root, parts[0], ct)
            ?? throw new IdeError(409, "git", $"git could not read {workspace.Show(full!)} as {rev} has it.");
        if (IdeFiles.Text(bytes) is { } text)
        {
            result["text"] = text;
        }
        else
        {
            result["binary"] = true;
        }
        return result;
    }

    /// <summary>The full hash of the commit a revision names; null when git knows of none. A revision that is not one is 400.</summary>
    private async Task<string?> CommitOfAsync(string rev, CancellationToken ct)
    {
        if (!Revision().IsMatch(rev))
        {
            throw new IdeError(400, "invalid", "That is not a revision: give a hash, a branch or a tag (main~2, HEAD^ and the like), with no space in it and not starting with -.");
        }
        var (code, output) = await Git(workspace.Root, ["rev-parse", "--verify", "-q", $"{rev}^{{commit}}"], ct);
        var hash = output.Split('\n')[0].Trim();
        return code == 0 && hash.Length >= 40 && hash.All(char.IsAsciiHexDigitLower) ? hash : null;
    }

    private static IdeError NoRevision(string rev) => new(404, "no_revision", $"git knows no commit {rev} here.");

    /// <summary>A file of the folder (checked already: inside it) as git names it, from the repository's top.</summary>
    private async Task<string> InRepoAsync(string? full, CancellationToken ct)
    {
        if (full is null || !workspace.InRoot(full) || workspace.Show(full) is "." or "")
        {
            throw new IdeError(400, "invalid", "Say which file.");
        }
        var (code, prefix) = await Git(workspace.Root, ["rev-parse", "--show-prefix"], ct);
        if (code != 0)
        {
            throw new IdeError(404, "no_git", "This folder is in no git repository (git init makes one).");
        }
        return prefix.Split('\n')[0] + workspace.Show(full);
    }

    private static string Number(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>A blob's bytes as git keeps them (no filters, no text conversion); null when git cannot give them (within a minute).</summary>
    private static async Task<byte[]?> BlobAsync(string dir, string hash, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in (string[])["cat-file", "blob", hash])
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment.Remove("ARENA_API_KEY");
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return null;
        }
        using (process)
        {
            process.StandardInput.Close();
            using var bytes = new MemoryStream();
            var reading = process.StandardOutput.BaseStream.CopyToAsync(bytes, CancellationToken.None);
            var said = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(1));
            try
            {
                await process.WaitForExitAsync(limit.Token);
                await Task.WhenAll(reading, said).WaitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It ended meanwhile.
                }
                ct.ThrowIfCancellationRequested();
                return null;
            }
            return process.ExitCode == 0 ? bytes.ToArray() : null;
        }
    }

    /// <summary>The local branches, the one checked out first.</summary>
    public async Task<JsonArray> BranchesAsync(CancellationToken ct)
    {
        if (await RootAsync(ct) is null)
        {
            return [];
        }
        var (_, output) = await Git(workspace.Root, ["branch", "--format=%(HEAD)%(refname:short)"], ct);
        return new JsonArray([.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).OrderByDescending(b => b.StartsWith('*'))
            .Select(b => (JsonNode)new JsonObject { ["name"] = b.TrimStart('*', ' '), ["current"] = b.StartsWith('*') })]);
    }

    /// <summary>Checks out another branch (git refuses when it would lose what is not committed, and says why).</summary>
    public async Task SwitchAsync(JsonObject body, CancellationToken ct)
    {
        var name = body.Str("branch")?.Trim();
        if (string.IsNullOrEmpty(name) || name.StartsWith('-'))
        {
            throw new IdeError(400, "invalid", "Say which branch.");
        }
        Check(await Git(workspace.Root, ["switch", name], ct), "switched");
    }

    private static void Check((int Code, string Output) result, string what)
    {
        if (result.Code != 0)
        {
            throw new IdeError(409, "git", $"git refused, nothing {what}: {Fmt.OneLine(result.Output.Trim(), 400)}");
        }
    }
}
