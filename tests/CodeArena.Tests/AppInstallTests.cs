namespace CodeArena.Tests;

/// <summary>Code Arena taken off a computer as its installers left it there (Linux here: its desktop entry and icon).</summary>
public sealed class AppInstallTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    [Fact]
    public async Task Uninstall_takes_off_the_program_its_menu_entry_and_what_lies_beside_it_and_keeps_the_sessions_unless_asked()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp);
        var home = Path.Combine(h.Root, "home");
        var share = Path.Combine(home, ".local", "share");
        h.Env["HOME"] = home;
        h.Env["XDG_DATA_HOME"] = null;
        string Put(string path, string text = "x")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }
        var exe = Put(Path.Combine(home, ".local", "bin", "code-arena"), "the program");
        var ca = Put(Path.Combine(home, ".local", "bin", Updates.SigningCaFile), "the CA");
        var entry = Put(AppInstall.DesktopEntry(share), "[Desktop Entry]");
        var icon = Put(AppInstall.Icon(share), "png");
        var other = Put(Path.Combine(home, ".local", "bin", "something-else"), "not ours");
        Directory.CreateDirectory(h.Paths.SessionsDir);

        Assert.Equal(0, await h.RunAs(exe, "uninstall"));
        foreach (var gone in new[] { exe, ca, entry, icon })
        {
            Assert.False(File.Exists(gone), gone);
        }
        Assert.True(File.Exists(other));
        Assert.True(Directory.Exists(h.Paths.SessionsDir));
        Assert.Contains("Your sessions and settings stay", h.Out, StringComparison.Ordinal);

        // --all: the sessions and settings too.
        Put(exe, "again");
        Assert.Equal(0, await h.RunAs(exe, "uninstall", "--all"));
        Assert.False(Directory.Exists(h.Paths.DataDir));
        Assert.Contains("with your sessions and settings", h.Out, StringComparison.Ordinal);

        // Run from its source: nothing to take off.
        Assert.Equal(1, await h.RunAs(Path.Combine(h.Root, "dotnet"), "uninstall"));
    }

    [Fact]
    public void A_folder_given_with_C_is_where_it_runs_and_leaves_the_arguments_after_it()
    {
        var folder = Directory.CreateTempSubdirectory("code-arena-c-").FullName;
        try
        {
            Assert.Null(Cli.TakeFolder(["-C", folder, "chat", "--", "-C", "kept"], out var rest, out var at));
            Assert.Equal(Path.GetFullPath(folder), at);
            Assert.Equal(["chat", "--", "-C", "kept"], rest);
            Assert.Null(Cli.TakeFolder(["web"], out rest, out at));
            Assert.Equal(["web"], rest);
            Assert.Null(at);
            Assert.Contains("no folder", Cli.TakeFolder(["--folder", Path.Combine(folder, "none")], out _, out _), StringComparison.Ordinal);
            // Nothing after it (a desktop entry started with no folder): where it is.
            Assert.Null(Cli.TakeFolder(["-C"], out rest, out at));
            Assert.Empty(rest);
            Assert.Null(at);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
