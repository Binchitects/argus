using System.Diagnostics;

namespace CodeArena.Tests;

/// <summary>
/// A repository whose own settings name programs, as a .git/config and
/// .gitattributes from an archive could, or as the agent could write them in
/// auto-edit: each program leaves a marker file when it runs. Nothing that
/// only reads may make one. And a link and a setting that lead to a file
/// outside it, which nothing that only reads may show.
/// </summary>
internal sealed class GitTraps : IDisposable
{
    public GitTraps()
    {
        Root = Directory.CreateTempSubdirectory("arena-git-").FullName;
        Repo = Directory.CreateDirectory(Path.Combine(Root, "repo")).FullName;
        Marks = Directory.CreateDirectory(Path.Combine(Root, "marks")).FullName;
        Git("init", "-q");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        Git("config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(Repo, "a.txt"), "one\n");
        File.WriteAllText(Path.Combine(Repo, "c.txt"), "same\n");
        File.WriteAllText(Path.Combine(Repo, "b.dat"), "data\n");
        Git("add", "a.txt", "b.dat", "c.txt");
        Git("commit", "-q", "-m", "first");
        File.AppendAllText(Path.Combine(Repo, "a.txt"), "two\n");
        Git("commit", "-q", "-am", "second");
        // A merge whose merge conflicted in m.txt and n.txt: log and show re-merge it for --remerge-diff, through their merge drivers.
        Git("checkout", "-q", "-b", "side");
        File.WriteAllText(Path.Combine(Repo, "m.txt"), "side\n");
        File.WriteAllText(Path.Combine(Repo, "n.txt"), "side\n");
        Git("add", "m.txt", "n.txt");
        Git("commit", "-q", "-m", "side");
        Git("checkout", "-q", "-");
        File.WriteAllText(Path.Combine(Repo, "m.txt"), "main\n");
        File.WriteAllText(Path.Combine(Repo, "n.txt"), "main\n");
        Git("add", "m.txt", "n.txt");
        Git("commit", "-q", "-m", "main");
        Git("merge", "-q", "side");
        File.WriteAllText(Path.Combine(Repo, "m.txt"), "merged\n");
        File.WriteAllText(Path.Combine(Repo, "n.txt"), "merged\n");
        Git("add", "m.txt", "n.txt");
        Git("commit", "-q", "--no-edit");
        // A signed commit on top: any armored block, which git hands to gpg.program to check.
        var tree = Git("rev-parse", "HEAD^{tree}").Trim();
        var parent = Git("rev-parse", "HEAD").Trim();
        var signed = Git(["hash-object", "-t", "commit", "-w", "--stdin"],
            $"tree {tree}\nparent {parent}\nauthor Test <test@example.com> 1700000000 +0000\ncommitter Test <test@example.com> 1700000000 +0000\n"
            + "gpgsig -----BEGIN PGP SIGNATURE-----\n \n iQEzBAABCAAdFiEE\n -----END PGP SIGNATURE-----\n\nsigned\n").Trim();
        Git("update-ref", "HEAD", signed);

        // The traps. merge= with no name picks the driver with an empty one.
        File.WriteAllText(Path.Combine(Repo, ".gitattributes"), "*.txt filter=evil diff=evil\n*.dat filter=proc\nm.txt merge=evil\nn.txt merge=\n");
        Git("config", "core.fsmonitor", Mark("fsmonitor") + "; false");
        Git("config", "filter.evil.clean", Mark("clean") + "; cat");
        Git("config", "filter.evil.smudge", Mark("smudge") + "; cat");
        Git("config", "filter.proc.process", Mark("process"));
        Git("config", "diff.evil.textconv", Mark("textconv") + "; cat");
        Git("config", "diff.evil.command", Mark("diff-command"));
        Git("config", "diff.external", Mark("external"));
        Git("config", "merge.evil.driver", Mark("merge") + "; false");
        Git("config", "merge..driver", Mark("merge-unnamed") + "; false");
        Git("config", "log.diffMerges", "remerge");
        Git("config", "log.showSignature", "true");
        Git("config", "gpg.program", Script("gpg"));
        Git("config", "core.sshCommand", Mark("ssh"));
        Directory.CreateDirectory(Path.Combine(Repo, ".git", "hooks"));
        foreach (var hook in new[] { "post-index-change", "reference-transaction", "post-checkout" })
        {
            File.Copy(Script("hook-" + hook), Path.Combine(Repo, ".git", "hooks", hook), overwrite: true);
        }

        // A file outside the working directory, and a link inside that leads to it; blame.ignoreRevsFile names it, and blame
        // would say what is in it (no revision: "invalid object name: ...").
        Outside = Path.Combine(Directory.CreateDirectory(Path.Combine(Root, "outside")).FullName, "secret.txt");
        File.WriteAllText(Outside, "secret-outside-content\n");
        File.CreateSymbolicLink(Path.Combine(Repo, "key"), Outside);
        Git("config", "blame.ignoreRevsFile", Outside);

        // The work tree changed: a.txt in what it says, c.txt and b.dat in their times only, which git must read the files for.
        File.AppendAllText(Path.Combine(Repo, "a.txt"), "three\n");
        File.SetLastWriteTimeUtc(Path.Combine(Repo, "c.txt"), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(Repo, "b.dat"), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    public string Root { get; }
    public string Repo { get; }
    public string Marks { get; }
    /// <summary>A file outside the repository, which nothing that reads it may show (Repo/key links to it).</summary>
    public string Outside { get; }

    /// <summary>The markers made so far: the programs of the repository's that ran.</summary>
    public string[] Ran => [.. Directory.GetFiles(Marks).Select(Path.GetFileName).Where(n => !n!.EndsWith(".sh", StringComparison.Ordinal)).Order()!];

    private string Mark(string name) => $"touch {Path.Combine(Marks, name)}";

    private string Script(string name)
    {
        var path = Path.Combine(Marks, name + ".sh");
        File.WriteAllText(path, $"#!/bin/sh\n{Mark(name)}\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    /// <summary>Plain git, unguarded, in the repository: its output.</summary>
    public string Git(params string[] args) => Git(args, null);

    public string Git(string[] args, string? input)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Repo, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var git = Process.Start(psi)!;
        git.StandardInput.Write(input ?? "");
        git.StandardInput.Close();
        var output = git.StandardOutput.ReadToEndAsync();
        var error = git.StandardError.ReadToEndAsync();
        Assert.True(git.WaitForExit(30_000), $"git {string.Join(' ', args)} did not end");
        return output.Result + error.Result;
    }

    public void Clear()
    {
        foreach (var file in Directory.GetFiles(Marks).Where(f => !f.EndsWith(".sh", StringComparison.Ordinal)))
        {
            File.Delete(file);
        }
    }

    public void Dispose()
    {
        Directory.Delete(Root, recursive: true);
    }
}
