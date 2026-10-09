using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodeArena;

/// <summary>Who a commit Code Arena makes is by: git's author. The committer stays the person (their own git settings).</summary>
internal sealed record CommitIdentity(string Name, string Email)
{
    public static readonly CommitIdentity Default = new("Code Arena", "code-arena@localhost");

    /// <summary>The identity the config gives, else Code Arena at the Arena's host (code-arena@arena.example.com).</summary>
    public static CommitIdentity From(Config config)
    {
        var host = Uri.TryCreate(config.Url, UriKind.Absolute, out var u) && u.HostNameType == UriHostNameType.Dns ? u.Host : "localhost";
        return new(config.CommitName is { Length: > 0 } name ? name : Default.Name, config.CommitEmail is { Length: > 0 } email ? email : $"code-arena@{host}");
    }

    /// <summary>
    /// Every command the agent runs commits as Code Arena: a `git commit` the model makes is told apart from the
    /// person's own by its author, as is each turn's own commit.
    /// </summary>
    public void Apply(IDictionary<string, string?> env)
    {
        env["GIT_AUTHOR_NAME"] = Name;
        env["GIT_AUTHOR_EMAIL"] = Email;
    }
}

/// <summary>What a turn's commit did: its commit, the files in it, and the files left for the person (with why).</summary>
internal sealed record TurnCommit(string? Hash, string Subject, IReadOnlyList<string> Files, IReadOnlyList<string> Left, string? Problem)
{
    public string Describe()
    {
        var sb = new StringBuilder();
        if (Hash is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Committed {Files.Count} file{(Files.Count == 1 ? "" : "s")} as Code Arena: {Hash} {Subject}");
        }
        else if (Problem is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $"The turn's changes are not committed: {Problem}");
        }
        if (Left.Count > 0)
        {
            sb.Append(sb.Length > 0 ? "\n" : "").Append(CultureInfo.InvariantCulture,
                $"Not committed, as they held your own changes before this turn: {string.Join(", ", Left.Take(5))}{(Left.Count > 5 ? $" and {Left.Count - 5} more" : "")}.");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Each turn's changes committed under Code Arena's name, so the history tells what the harness wrote from what the
/// person did. Only files the turn changed go in: a file that already held the person's own uncommitted changes when
/// the turn began is left for them, and so is everything else they have staged or changed.
/// </summary>
internal static class TurnCommits
{
    /// <summary>The repository's uncommitted files as a turn begins, each with its content's hash (null: deleted).</summary>
    internal sealed record Snapshot(string Root, IReadOnlyDictionary<string, string?> Dirty);

    public static async Task<Snapshot?> TakeAsync(string cwd, CancellationToken ct)
    {
        var root = await GitAsync(cwd, ["rev-parse", "--show-toplevel"], ct);
        if (root.Code != 0 || root.Output.Trim() is not { Length: > 0 } top)
        {
            return null;
        }
        top = Path.GetFullPath(top.Trim());
        var dirty = await DirtyAsync(top, ct);
        return dirty is null ? null : new Snapshot(top, dirty);
    }

    /// <summary>The files the turn changed that were not the person's uncommitted work as it began, with their content now.</summary>
    public static async Task<Dictionary<string, string?>?> ChangedAsync(Snapshot before, CancellationToken ct) =>
        await DirtyAsync(before.Root, ct) is { } now
            ? now.Where(kv => !before.Dirty.ContainsKey(kv.Key)).ToDictionary(StringComparer.Ordinal)
            : null;

    /// <summary>Commits what the turn changed. Null when it changed nothing; a problem (a merge under way, a hook that refused) is said.</summary>
    public static async Task<TurnCommit?> CommitAsync(Snapshot before, string request, CommitIdentity who, string? model, string? session, CancellationToken ct)
    {
        var now = await DirtyAsync(before.Root, ct);
        if (now is null)
        {
            return null;
        }
        var changed = now.Where(kv => !before.Dirty.TryGetValue(kv.Key, out var was) || was != kv.Value).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        if (changed.Count == 0)
        {
            return null;
        }
        // The person's work in progress stays theirs: a file they had changed is not taken into the harness's commit.
        var left = changed.Where(before.Dirty.ContainsKey).ToList();
        var files = changed.Except(left).ToList();
        var subject = Subject(request);
        if (files.Count == 0)
        {
            return new TurnCommit(null, subject, [], left, null);
        }
        if (Busy(before.Root) is { } busy)
        {
            return new TurnCommit(null, subject, files, left, busy);
        }
        var add = await GitAsync(before.Root, ["add", "-A", "--", .. files], ct);
        if (add.Code != 0)
        {
            return new TurnCommit(null, subject, files, left, $"git add failed: {Fmt.OneLine(add.Output, 300)}");
        }
        var trailers = new List<string>();
        if (model is { Length: > 0 })
        {
            trailers.Add($"Code-Arena-Model: {model}");
        }
        if (session is { Length: > 0 })
        {
            trailers.Add($"Code-Arena-Session: {session}");
        }
        var message = $"{subject}\n\n{Body(request)}{(trailers.Count > 0 ? "\n\n" + string.Join('\n', trailers) : "")}\n";
        var commit = await GitAsync(before.Root, ["commit", "-q", "-F", "-", "--", .. files], ct, message, who);
        if (commit.Code != 0)
        {
            return new TurnCommit(null, subject, files, left, $"git commit failed: {Fmt.OneLine(commit.Output, 300)}");
        }
        var hash = await GitAsync(before.Root, ["rev-parse", "--short", "HEAD"], ct);
        return new TurnCommit(hash.Output.Trim(), subject, files, left, null);
    }

    /// <summary>The commit's first line: the request's first line, within 72 characters.</summary>
    internal static string Subject(string request)
    {
        var line = request.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "Changes";
        return line.Length <= 72 ? line : line[..69].TrimEnd() + "...";
    }

    private static string Body(string request)
    {
        var text = request.Trim();
        return "Asked of Code Arena:\n" + string.Join('\n', (text.Length <= 2000 ? text : text[..2000] + "...").Split('\n').Select(l => l.Length == 0 ? "" : "> " + l));
    }

    /// <summary>A merge, rebase, cherry-pick or revert under way: the person's to finish, and no place for a commit of the harness's.</summary>
    private static string? Busy(string root)
    {
        var git = Path.Combine(root, ".git");
        if (!Directory.Exists(git))
        {
            return null;
        }
        foreach (var (file, what) in new[] { ("MERGE_HEAD", "a merge"), ("CHERRY_PICK_HEAD", "a cherry-pick"), ("REVERT_HEAD", "a revert"), ("rebase-merge", "a rebase"), ("rebase-apply", "a rebase") })
        {
            if (File.Exists(Path.Combine(git, file)) || Directory.Exists(Path.Combine(git, file)))
            {
                return $"{what} is under way: finish it, then commit them";
            }
        }
        return null;
    }

    /// <summary>The uncommitted files (changed, staged, new or deleted), by their path in the repository, each with its content's hash.</summary>
    private static async Task<Dictionary<string, string?>?> DirtyAsync(string root, CancellationToken ct)
    {
        var status = await GitAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=all"], ct);
        if (status.Code != 0)
        {
            return null;
        }
        var dirty = new Dictionary<string, string?>(StringComparer.Ordinal);
        var parts = status.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var entry = parts[i];
            if (entry.Length < 4)
            {
                continue;
            }
            var path = entry[3..];
            dirty[path] = Hash(Path.Combine(root, path));
            if (entry[0] is 'R' or 'C')
            {
                // A rename's or copy's old path follows it: gone from the working tree.
                i++;
                if (i < parts.Length)
                {
                    dirty[parts[i]] = null;
                }
            }
        }
        return dirty;
    }

    private static string? Hash(string file)
    {
        try
        {
            return File.Exists(file) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) : null;
        }
        catch (IOException)
        {
            return "unreadable";
        }
        catch (UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    private static async Task<(int Code, string Output)> GitAsync(string dir, IReadOnlyList<string> args, CancellationToken ct, string? input = null, CommitIdentity? author = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment.Remove("ARENA_API_KEY");
        author?.Apply(psi.Environment);
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, "git is not installed");
        }
        using (process)
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input);
            }
            process.StandardInput.Close();
            // A hook or a signing program that waits for someone: given up after two minutes, never left hanging.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(2));
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return (-1, $"git {args[0]} did not end within two minutes (a hook, or a signing program waiting for a passphrase?)");
            }
            return (process.ExitCode, await output + await error);
        }
    }
}
