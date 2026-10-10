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
        // Only what is still there to take (a file a command made and removed since the status is gone), or a deletion git knows of.
        files = [.. files.Where(f => now[f] is null || File.Exists(Path.Combine(before.Root, f)) || Directory.Exists(Path.Combine(before.Root, f)))];
        if (files.Count == 0)
        {
            return null;
        }
        var add = await AddAsync(before.Root, files, ct);
        if (add.Code != 0 && add.Output.Contains("did not match any file", StringComparison.Ordinal))
        {
            // One went between the look and the add: the others go in.
            files = [.. files.Where(f => File.Exists(Path.Combine(before.Root, f)) || Directory.Exists(Path.Combine(before.Root, f)))];
            add = files.Count > 0 ? await AddAsync(before.Root, files, ct) : (0, "");
        }
        if (add.Code != 0)
        {
            return new TurnCommit(null, subject, files, left, $"git add failed: {Fmt.OneLine(add.Output, 300)}");
        }
        if (files.Count == 0)
        {
            return null;
        }
        var paths = string.Join('\0', files) + "\0";
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
        // The message from a file, the paths on stdin: no argument list too long, however many files.
        var messageFile = Path.GetTempFileName();
        (int Code, string Output) commit;
        try
        {
            await File.WriteAllTextAsync(messageFile, message, ct);
            commit = await GitAsync(before.Root, ["--literal-pathspecs", "commit", "-q", "-F", messageFile, "--pathspec-from-file=-", "--pathspec-file-nul"], ct, paths, who);
            if (commit.Code != 0 && Unknown(commit.Output))
            {
                // git before 2.25: the paths as arguments.
                commit = await GitAsync(before.Root, ["--literal-pathspecs", "commit", "-q", "-F", messageFile, "--", .. files], ct, null, who);
            }
        }
        finally
        {
            File.Delete(messageFile);
        }
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

    /// <summary>
    /// Stages the files: taken literally (a path like <c>app/[id]/page.tsx</c> is that file, not a pattern), from git's stdin;
    /// with a git before 2.26, which cannot read them from there, as arguments a few hundred at a time.
    /// </summary>
    private static async Task<(int Code, string Output)> AddAsync(string root, IReadOnlyList<string> files, CancellationToken ct)
    {
        var add = await GitAsync(root, ["--literal-pathspecs", "add", "-A", "--pathspec-from-file=-", "--pathspec-file-nul"], ct, string.Join('\0', files) + "\0");
        if (add.Code == 0 || !Unknown(add.Output))
        {
            return add;
        }
        foreach (var chunk in files.Chunk(200))
        {
            add = await GitAsync(root, ["--literal-pathspecs", "add", "-A", "--", .. chunk], ct);
            if (add.Code != 0)
            {
                return add;
            }
        }
        return add;
    }

    /// <summary>git said it does not know an option: an older git.</summary>
    private static bool Unknown(string output) => output.Contains("unknown option", StringComparison.OrdinalIgnoreCase);

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

    internal static async Task<(int Code, string Output)> GitAsync(string dir, IReadOnlyList<string> args, CancellationToken ct, string? input = null, CommitIdentity? author = null)
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
            return (-1, $"git could not be run: {e.Message}");
        }
        using (process)
        {
            var started = DateTime.UtcNow;
            try
            {
                if (input is not null)
                {
                    await process.StandardInput.WriteAsync(input);
                }
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // git ended without reading it (the index locked, an option it does not know): what it said tells why.
            }
            // Read to the end without a token: a read cut short would lose what git said.
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            // A hook or a signing program that waits for someone: given up after two minutes, never left hanging;
            // the turn's Ctrl+C or Stop gives it up at once.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                await StopAsync(process, dir, started);
                ct.ThrowIfCancellationRequested();
                return (-1, $"git {args.First(a => !a.StartsWith('-'))} did not end within two minutes (a hook, or a signing program waiting for a passphrase?)");
            }
            // Something a hook left running in the background can hold git's output open: what came in two seconds is enough.
            var both = Task.WhenAll(output, error);
            if (await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)) != both)
            {
                return (process.ExitCode, "");
            }
            return (process.ExitCode, await output + await error);
        }
    }

    /// <summary>
    /// Stops git as an interrupted git stops: told to end (SIGTERM, which it takes by removing its lock files and ending its
    /// hook), killed with what it started only if it has not within a few seconds. Then a .git/index.lock it could not
    /// remove itself (made since it started) is removed, or every later commit, and the person's own git, would fail.
    /// </summary>
    private static async Task StopAsync(Process process, string dir, DateTime started)
    {
        try
        {
            if (!OperatingSystem.IsWindows() && !process.HasExited)
            {
                _ = kill(process.Id, 15);
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(wait.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Still running: killed below.
        }
        catch (InvalidOperationException)
        {
            // It ended meanwhile.
        }
        var killed = false;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                killed = true;
            }
        }
        catch (InvalidOperationException)
        {
            // It ended meanwhile.
        }
        if (killed && await RepositoryAsync(dir) is { } git && Path.Combine(git, "index.lock") is var lockFile && File.Exists(lockFile)
            && File.GetLastWriteTimeUtc(lockFile) >= started.AddSeconds(-1))
        {
            try
            {
                File.Delete(lockFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Said by the next git that finds it.
            }
        }
    }

    /// <summary>The repository's .git folder (a worktree's own), or null.</summary>
    private static async Task<string?> RepositoryAsync(string dir)
    {
        var git = await GitAsync(dir, ["rev-parse", "--absolute-git-dir"], CancellationToken.None);
        return git.Code == 0 && git.Output.Trim() is { Length: > 0 } path ? path : null;
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int kill(int pid, int signal);
}
