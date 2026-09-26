using System.Diagnostics;
using System.Text;
using Argus.Configuration;
using Argus.Util;

namespace Argus.Indexing;

/// <summary>A git command failed.</summary>
public sealed class GitError(string message) : Exception(message);

public readonly record struct Change(string Status, string Path);

/// <summary>
/// Bare mirrors and detached worktrees. The token reaches git
/// only through GIT_ASKPASS and an environment variable -- never an argument,
/// never a file, never a URL -- and is redacted from every error.
/// </summary>
public static class Mirror
{
    public const string TokenEnv = "ARGUS_GIT_ASKPASS_TOKEN";
    /// <summary>Set in git's environment when the argus executable itself is the askpass helper.</summary>
    public const string AskpassModeEnv = "ARGUS_GIT_ASKPASS_MODE";
    const string AskpassUsername = "oauth2";

    const string PosixAskpass = """
        #!/bin/sh
        # GIT_ASKPASS helper for Argus. Not meant to be run by hand.
        # git passes the prompt as $1 and reads the answer from stdout. The token
        # travels only through the ARGUS_GIT_ASKPASS_TOKEN environment variable.
        prompt=$(printf '%s' "$1" | sed 's/^[[:space:]]*//' | tr '[:upper:]' '[:lower:]')
        case "$prompt" in
          username*) printf '%s\n' "oauth2" ;;
          *) printf '%s\n' "$ARGUS_GIT_ASKPASS_TOKEN" ;;
        esac
        """;

    /// <summary>Answer a git credential prompt: the entry point for askpass mode.</summary>
    public static int AnswerAskpass(string[] args)
    {
        var prompt = args.Length > 0 ? args[0] : "";
        Console.Out.Write((PyStr.Strip(prompt).ToLowerInvariant().StartsWith("username", StringComparison.Ordinal)
            ? AskpassUsername
            : Environment.GetEnvironmentVariable(TokenEnv) ?? "") + "\n");
        return 0;
    }

    static string AskpassProgram(string askpassDir, IDictionary<string, string?> env)
    {
        Directory.CreateDirectory(askpassDir);
        var self = Environment.ProcessPath;
        if (OperatingSystem.IsWindows())
        {
            if (self is not null && Path.GetFileNameWithoutExtension(self).Equals("argus", StringComparison.OrdinalIgnoreCase))
            {
                env[AskpassModeEnv] = "1";
                return self;
            }
            var cmd = Path.Combine(askpassDir, "git_askpass.cmd");
            File.WriteAllText(cmd, "@echo off\r\n" +
                "echo %~1| findstr /b /i /c:\"Username\" >nul && (echo oauth2) || (echo %" + TokenEnv + "%)\r\n");
            return cmd;
        }
        var script = Path.Combine(askpassDir, "git_askpass.sh");
        File.WriteAllText(script, PosixAskpass.Replace("\r\n", "\n") + "\n");
        File.SetUnixFileMode(script, File.GetUnixFileMode(script) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        return script;
    }

    /// <summary>The environment for a credentialed git operation; null means inherit unchanged.</summary>
    static Dictionary<string, string?>? AuthEnv(IndexConfig index, string? token, GitLabConfig? gitlab)
    {
        if (string.IsNullOrEmpty(token) && gitlab is null) return null;
        var env = new Dictionary<string, string?>();
        if (!string.IsNullOrEmpty(token))
        {
            env["GIT_ASKPASS"] = AskpassProgram(Path.Combine(index.DataDir, ".askpass"), env);
            env[TokenEnv] = token;
        }
        if (gitlab is not null) Tls.GitEnv(gitlab, env);
        return env;
    }

    static string Redact(string text, IEnumerable<string> secrets)
    {
        foreach (var s in secrets)
            if (!string.IsNullOrEmpty(s)) text = text.Replace(s, "***");
        return text;
    }

    public sealed record GitResult(int Code, string Stdout, string Stderr);

    public static GitResult RunGit(string cwd, IEnumerable<string> args, IDictionary<string, string?>? env = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;
        // Never let git fall back to prompting on a terminal it does not own.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var proc = Process.Start(psi) ?? throw new GitError("git could not be started");
        proc.StandardInput.Close();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return new GitResult(proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    static string Git(string cwd, IEnumerable<string> args, IDictionary<string, string?>? env = null, IReadOnlyList<string>? secrets = null)
    {
        var list = args.ToList();
        var result = RunGit(cwd, list, env);
        if (result.Code != 0)
        {
            secrets ??= [];
            var cmd = Redact(string.Join(" ", list), secrets);
            var stderr = Redact(PyStr.Prefix(PyStr.Strip(result.Stderr), 500), secrets);
            throw new GitError($"git {cmd} failed: {stderr}");
        }
        return result.Stdout;
    }

    public static string MirrorPath(IndexConfig index, long gitlabId) => Path.Combine(index.MirrorsDir, $"{gitlabId}.git");

    /// <summary>Worktree for one project at one branch; the branch is percent-encoded so names cannot collide.</summary>
    public static string TreePath(IndexConfig index, long gitlabId, string? branch = null) =>
        branch is null
            ? Path.Combine(index.TreesDir, gitlabId.ToString())
            : Path.Combine(index.TreesDir, gitlabId.ToString(), BranchDir(branch));

    /// <summary>urllib.parse.quote(branch, safe=""): unreserved characters stay, everything else is %XX.</summary>
    public static string BranchDir(string branch)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(branch))
        {
            char c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~')) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    public static string EnsureMirror(IndexConfig index, Project project, string cloneUrl, string? token = null, GitLabConfig? gitlab = null)
    {
        var env = AuthEnv(index, token, gitlab);
        IReadOnlyList<string> secrets = string.IsNullOrEmpty(token) ? [] : [token];
        string[] authArgs = string.IsNullOrEmpty(token) ? [] : ["-c", "credential.helper="];
        var path = MirrorPath(index, project.GitlabId);
        if (Directory.Exists(path) || File.Exists(path))
        {
            Retarget(path, cloneUrl, authArgs, env, secrets);
            Git(path, [.. authArgs, "fetch", "--prune", "--quiet", "origin", "+refs/heads/*:refs/heads/*"], env, secrets);
            return path;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Git(Path.GetDirectoryName(path)!, [.. authArgs, "clone", "--mirror", "--quiet", cloneUrl, path], env, secrets);
        return path;
    }

    static void Retarget(string mirror, string cloneUrl, string[] authArgs, IDictionary<string, string?>? env, IReadOnlyList<string> secrets)
    {
        string current;
        try { current = PyStr.Strip(Git(mirror, ["config", "--get", "remote.origin.url"])); }
        catch (Exception) { return; }
        if (current == cloneUrl) return;
        Git(mirror, [.. authArgs, "remote", "set-url", "origin", cloneUrl], env, secrets);
    }

    public static string HeadSha(string mirror, string branch) => PyStr.Strip(Git(mirror, ["rev-parse", branch]));

    public static bool IsAncestor(string mirror, string old, string @new)
    {
        var r = RunGit(mirror, ["merge-base", "--is-ancestor", old, @new]);
        return r.Code switch
        {
            0 => true,
            1 => false,
            _ => throw new GitError($"git merge-base --is-ancestor failed: {PyStr.Prefix(PyStr.Strip(r.Stderr), 500)}"),
        };
    }

    public static bool CommitExists(string mirror, string sha) => RunGit(mirror, ["cat-file", "-e", $"{sha}^{{commit}}"]).Code == 0;

    static List<Change> FullListing(string mirror, string sha) =>
        Git(mirror, ["ls-tree", "-r", "--name-only", "-z", sha]).Split('\0')
            .Where(p => p.Length > 0).Select(p => new Change("A", p)).ToList();

    /// <summary>(full_reindex, changes) from one ancestry resolution.</summary>
    public static (bool Full, List<Change> Changes) ChangesSince(string mirror, string? oldSha, string newSha)
    {
        if (oldSha is null || !CommitExists(mirror, oldSha) || !IsAncestor(mirror, oldSha, newSha))
            return (true, FullListing(mirror, newSha));
        return (false, DiffChanges(mirror, oldSha, newSha));
    }

    static List<Change> DiffChanges(string mirror, string oldSha, string newSha)
    {
        var fields = Git(mirror, ["diff", "--name-status", "--no-renames", "-z", $"{oldSha}..{newSha}"])
            .Split('\0').Where(f => f.Length > 0).ToList();
        var changes = new List<Change>();
        for (int i = 0; i + 1 < fields.Count; i += 2)
        {
            var status = fields[i].Substring(0, 1);
            var path = fields[i + 1];
            if (status == "T") status = "M";
            if (status is "A" or "M" or "D" && path.Length > 0) changes.Add(new Change(status, path));
        }
        return changes;
    }

    public static string SyncWorktree(IndexConfig index, long gitlabId, string mirror, string sha, string? branch = null)
    {
        var tree = TreePath(index, gitlabId, branch);
        if (Directory.Exists(tree) && !IsWorktree(tree))
        {
            try { Directory.Delete(tree, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (Directory.Exists(tree))
        {
            Git(tree, ["checkout", "--force", "--detach", sha]);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tree)!);
            try { Git(mirror, ["worktree", "prune"]); } catch (GitError) { }
            Git(mirror, ["worktree", "add", "--force", "--detach", tree, sha]);
        }
        return tree;
    }

    static bool IsWorktree(string tree)
    {
        try { Git(tree, ["rev-parse", "--git-dir"]); return true; }
        catch (GitError) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>Every path in the tree mapped to its blob sha, in one git call.</summary>
    public static Dictionary<string, string> BlobShas(string mirror, string sha)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in Git(mirror, ["ls-tree", "-r", "-z", sha]).Split('\0'))
        {
            if (record.Length == 0) continue;
            int tab = record.IndexOf('\t');
            var meta = tab >= 0 ? record.Substring(0, tab) : record;
            var path = tab >= 0 ? record.Substring(tab + 1) : "";
            var parts = PyStr.SplitWhitespace(meta);
            if (parts.Count >= 3 && path.Length > 0) result[path] = parts[2];
        }
        return result;
    }

    public static List<string> ListBranches(string mirror) =>
        PyStr.SplitLines(Git(mirror, ["for-each-ref", "--format=%(refname:short)", "refs/heads/"]))
            .Select(l => PyStr.Strip(l)).Where(l => l.Length > 0).ToList();

    /// <summary>The default branch (always) plus anything matching the patterns, in git's order.</summary>
    public static List<string> SelectBranches(IReadOnlyList<string> available, IReadOnlyList<string> patterns, string defaultBranch)
    {
        var chosen = available.Contains(defaultBranch) ? new List<string> { defaultBranch } : [];
        foreach (var b in available)
        {
            if (chosen.Contains(b)) continue;
            if (patterns.Any(p => Fnmatch.MatchCase(b, p))) chosen.Add(b);
        }
        return chosen;
    }
}

/// <summary>Python's fnmatch.fnmatchcase: shell globbing with *, ?, [seq] and [!seq].</summary>
public static class Fnmatch
{
    public static bool MatchCase(string name, string pattern) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, Translate(pattern), System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static string Translate(string pat)
    {
        var sb = new StringBuilder(@"\A(?s:");
        int i = 0, n = pat.Length;
        while (i < n)
        {
            char c = pat[i++];
            if (c == '*') { sb.Append(".*"); while (i < n && pat[i] == '*') i++; }
            else if (c == '?') sb.Append('.');
            else if (c == '[')
            {
                int j = i;
                if (j < n && pat[j] == '!') j++;
                if (j < n && pat[j] == ']') j++;
                while (j < n && pat[j] != ']') j++;
                if (j >= n) sb.Append(@"\[");
                else
                {
                    var stuff = pat.Substring(i, j - i).Replace(@"\", @"\\");
                    i = j + 1;
                    if (stuff.StartsWith('!')) stuff = "^" + stuff.Substring(1);
                    else if (stuff.StartsWith('^')) stuff = @"\" + stuff;
                    sb.Append('[').Append(stuff).Append(']');
                }
            }
            else sb.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
        }
        return sb.Append(@")\z").ToString();
    }
}
