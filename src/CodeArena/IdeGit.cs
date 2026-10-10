using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// The IDE's Source Control: the folder's git as the person works it (not the agent's turn commits): what changed since
/// the last commit, staged or not; each file before and now; stage, unstage, discard and commit; the branches and the
/// last commits. Paths are the folder's, with "/"; a repository bigger than the folder shows the folder's files only.
/// The person's own name commits, as their git is set up.
/// </summary>
internal sealed class IdeGit(Workspace workspace)
{
    /// <summary>The most files listed.</summary>
    public const int MostFiles = 2_000;

    private static Task<(int Code, string Output)> Git(string dir, IReadOnlyList<string> args, CancellationToken ct, string? input = null) =>
        TurnCommits.GitAsync(dir, args, ct, input);

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

    /// <summary>The last commits: short hash, subject, author and when.</summary>
    public async Task<JsonArray> LogAsync(CancellationToken ct)
    {
        if (await RootAsync(ct) is null)
        {
            return [];
        }
        var (code, output) = await Git(workspace.Root, ["log", "-n", "30", "--pretty=format:%h%x1f%s%x1f%an%x1f%aI%x1e"], ct);
        return code != 0 ? [] : new JsonArray([.. output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries).Select(r => r.Trim('\n').Split('\x1f')).Where(f => f.Length == 4)
            .Select(f => (JsonNode)new JsonObject { ["hash"] = f[0], ["subject"] = f[1], ["author"] = f[2], ["at"] = f[3] })]);
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
