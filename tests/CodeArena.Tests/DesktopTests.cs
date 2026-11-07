namespace CodeArena.Tests;

/// <summary>
/// Code Arena as a desktop program: the IDE's log beside the program, the IDE started on its own on Windows with no console
/// window, and the browser for its window, which says why there is none and opens a tab instead.
/// </summary>
public sealed class DesktopTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("code-arena-desktop-").FullName;

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
        foreach (var dir in Directory.GetDirectories(_dir, "*", SearchOption.AllDirectories))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        Directory.Delete(_dir, recursive: true);
    }

    private string Exe(string folder) => Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, folder)).FullName, "code-arena");

    [Fact]
    public void The_log_is_beside_the_program_each_line_with_its_time_the_key_blanked_and_no_colours()
    {
        var exe = Exe("bin");
        using (var log = LogFile.Open(exe, Path.Combine(_dir, "data"))!)
        {
            Assert.Equal(Path.Combine(Path.GetDirectoryName(exe)!, "code-arena.log"), log.Path);
            log.Hide("s3cr3t-key");
            log.WriteLine("  http://127.0.0.1:4100/?token=s3cr3t-key");
            log.Write("\x1b[31m✗ Port 4100 is taken\x1b[0m\r\n");
            log.Write("half a line");
        }
        var lines = File.ReadAllLines(Path.Combine(_dir, "bin", "code-arena.log"));
        Assert.Equal(3, lines.Length);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} [+-]\d\d:\d\d   http://127\.0\.0\.1:4100/\?token=…$", lines[0]);
        Assert.EndsWith(" ✗ Port 4100 is taken", lines[1]);
        Assert.EndsWith(" half a line", lines[2]);
        Assert.DoesNotContain("s3cr3t-key", File.ReadAllText(Path.Combine(_dir, "bin", "code-arena.log")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir, "bin", "code-arena.log")));
        }
    }

    [Fact]
    public void The_log_rotates_when_full_keeping_three_older_files()
    {
        var exe = Exe("bin");
        using (var log = LogFile.Open(exe, Path.Combine(_dir, "data"), max: 200)!)
        {
            for (var i = 1; i <= 30; i++)
            {
                log.WriteLine($"line {i} of the log, long enough");
            }
        }
        var folder = Path.GetDirectoryName(exe)!;
        Assert.Equal(["code-arena.1.log", "code-arena.2.log", "code-arena.3.log", "code-arena.log"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        Assert.All(Directory.GetFiles(folder), f => Assert.True(new FileInfo(f).Length <= 200 + 80));
        // The newest lines in the log itself, the ones before in .1.
        Assert.Contains("line 30 of", File.ReadAllText(Path.Combine(folder, "code-arena.log")) + File.ReadAllText(Path.Combine(folder, "code-arena.1.log")));
        Assert.DoesNotContain("line 1 of", string.Concat(Directory.GetFiles(folder).Select(File.ReadAllText)));

        // Full when opened (a run before filled it): rotated first.
        File.WriteAllText(Path.Combine(folder, "code-arena.log"), new string('x', 300));
        using (LogFile.Open(exe, Path.Combine(_dir, "data"), max: 200)!)
        {
        }
        Assert.Equal(300, new FileInfo(Path.Combine(folder, "code-arena.1.log")).Length);
    }

    [Fact]
    public async Task Where_the_programs_folder_cannot_be_written_the_log_is_in_the_data_folder_and_help_says_where()
    {
        // A folder that cannot be written needs Unix modes, and a user other than root.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }
        var exe = Exe("readonly");
        File.SetUnixFileMode(Path.GetDirectoryName(exe)!, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var data = Path.Combine(_dir, "data");
        Assert.Equal(Path.Combine(data, "logs", "code-arena.log"), LogFile.Where(exe, data));
        using (var log = LogFile.Open(exe, data)!)
        {
            Assert.Equal(Path.Combine(data, "logs", "code-arena.log"), log.Path);
        }

        // --help says where it is, and finding out leaves nothing behind.
        var beside = Exe("bin");
        var output = new StringWriter();
        var env = new CliEnv { In = new StringReader(""), Out = output, Err = output, Env = _ => null, Cwd = _dir, Paths = new AppPaths(Path.Combine(_dir, "config"), data), Exe = beside };
        Assert.Equal(0, await Cli.RunAsync(["--help"], env));
        Assert.Contains($"The IDE's log (what it says, its key left out): {Path.Combine(_dir, "bin", "code-arena.log")}", output.ToString());
        Assert.False(File.Exists(Path.Combine(_dir, "bin", "code-arena.log")));
    }

    [Fact]
    public async Task The_IDE_logs_what_it_says_with_its_key_blanked_and_its_about_box_says_where()
    {
        using var h = new Harness(_gateway, _mcp);
        using var log = LogFile.Open(Exe("bin"), h.Paths.DataDir)!;
        h.Log = log;
        var windowed = new List<bool>();
        h.Windowed = windowed.Add;
        await using (var web = await WebRun.StartAsync(h, bare: true, browse: _ => true, window: _ => null, TimeSpan.FromSeconds(20)))
        {
            Assert.Equal(log.Path, (await web.GetJsonAsync("/api/state"))["log"]!.GetValue<string>());
            log.Flush();
            var logged = File.ReadAllText(log.Path);
            Assert.Contains($"http://127.0.0.1:{web.Port}/?token=…", logged);
            Assert.DoesNotContain(web.Token, logged);
            Assert.Contains("Code Arena", logged);
            // No window of its own: a tab, and the console (on Windows) comes back to stop it with.
            Assert.Equal([false], windowed);
        }

        // With a window of its own: told so.
        windowed.Clear();
        var closed = new TaskCompletionSource();
        await using (await WebRun.StartAsync(h, bare: true, browse: _ => true, window: _ => closed.Task, TimeSpan.FromSeconds(20)))
        {
            Assert.Equal([true], windowed);
            closed.SetResult();
        }
    }

    [Fact]
    public void Only_the_IDE_started_on_its_own_starts_again_without_a_console_window()
    {
        Assert.True(Cli.Ide([], built: true));
        Assert.True(Cli.Ide(["web", "--port", "4100"], built: true));
        Assert.True(Cli.Ide(["--continue"], built: true));
        // Without its page the bare command is the conversation in the terminal.
        Assert.False(Cli.Ide([], built: false));
        Assert.False(Cli.Ide(["chat"], built: true));
        Assert.False(Cli.Ide(["fix the bug"], built: true));
        Assert.False(Cli.Ide(["-p", "fix it"], built: true));
        Assert.False(Cli.Ide(["login"], built: true));
        Assert.False(Cli.Ide(["--help"], built: true));
        Assert.False(Cli.Ide(["index", "--rebuild"], built: true));
        Assert.False(Cli.Ide(["--no-such-option"], built: true));

        // A double-click (a console of its own) or no console at all: again, once. From a terminal: never.
        Assert.True(DesktopStart.Again(DesktopStart.Start.OwnConsole, ide: true, detached: false));
        Assert.True(DesktopStart.Again(DesktopStart.Start.NoConsole, ide: true, detached: false));
        Assert.False(DesktopStart.Again(DesktopStart.Start.OwnConsole, ide: true, detached: true));
        Assert.False(DesktopStart.Again(DesktopStart.Start.Terminal, ide: true, detached: false));
        Assert.False(DesktopStart.Again(DesktopStart.Start.OwnConsole, ide: false, detached: false));
        // Off Windows a program always runs where it was started.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(DesktopStart.Start.Terminal, DesktopStart.Find());
        }

        // Started again: the same program, arguments and folder, a console with no window, and the mark.
        var child = DesktopStart.Child(@"C:\Tools\code-arena.exe", ["web", "--port", "4100"], @"C:\work\shop");
        Assert.Equal(@"C:\Tools\code-arena.exe", child.FileName);
        Assert.Equal(["web", "--port", "4100"], child.ArgumentList);
        Assert.Equal(@"C:\work\shop", child.WorkingDirectory);
        Assert.True(child.CreateNoWindow);
        Assert.False(child.UseShellExecute);
        Assert.False(child.RedirectStandardOutput);
        Assert.Equal("1", child.Environment[DesktopStart.Detached]);
    }

    [Fact]
    public void An_error_said_while_the_IDE_had_no_console_is_kept_for_its_message_box()
    {
        var screen = new StringWriter();
        var log = new StringWriter();
        var tee = new Tee(screen, log);
        tee.WriteLine("Code Arena 7.7.2, the IDE for /work");
        tee.WriteLine("\x1b[31m✗ Not signed in to an Arena yet. Run: code-arena login\x1b[0m");
        tee.Write("Stopped.\n");
        Assert.Equal("Not signed in to an Arena yet. Run: code-arena login", tee.LastError);
        Assert.Equal(screen.ToString(), log.ToString());
        // The console let go: the log still has every line.
        tee.Screen = TextWriter.Null;
        tee.WriteLine("after");
        Assert.EndsWith($"after{Environment.NewLine}", log.ToString());
        Assert.DoesNotContain("after", screen.ToString());
    }

    [Fact]
    public void A_browser_too_old_for_the_page_gets_no_window_and_says_so_and_a_new_enough_one_does()
    {
        // Linux reads the version from --version (Windows from the file, macOS from Info.plist).
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return;
        }
        string Browser(string name, string version)
        {
            var file = Path.Combine(_dir, name);
            // What Chrome does: --version prints it; anything else is the window, which ends here at once.
            File.WriteAllText(file, $"#!/bin/sh\nif [ \"$1\" = --version ]; then echo '{version}'; fi\nexit 0\n");
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return file;
        }
        var said = new List<string>();
        var profile = Path.Combine(_dir, "window");
        Func<string, string?> Env(string browser) => k => k switch { "CODE_ARENA_BROWSER" => browser, "DISPLAY" => ":0", _ => null };

        Assert.Equal(108, AppWindow.Version(Browser("chromium-108", "Chromium 108.0.5359.94 built on Debian 11.5")));
        Assert.Null(AppWindow.Open("file:///launcher.html", profile, Env(Path.Combine(_dir, "chromium-108")), said.Add));
        Assert.Equal(["chromium-108 108 is older than the IDE needs (Chrome or Edge 109 or newer): it opens in your default browser instead."], said);

        said.Clear();
        var window = AppWindow.Open("file:///launcher.html", profile, Env(Browser("msedge", "Microsoft Edge 109.0.1518.140")), said.Add);
        Assert.NotNull(window);
        Assert.Empty(said);

        // None where CODE_ARENA_BROWSER says: said too.
        Assert.Null(AppWindow.Open("file:///launcher.html", profile, Env(Path.Combine(_dir, "missing")), said.Add));
        Assert.Equal([$"The browser chosen (codeArena.browser, or CODE_ARENA_BROWSER) is no file ({Path.Combine(_dir, "missing")}): the IDE opens in your default browser instead."], said);

        // A version it cannot read is no reason to go without the window.
        Assert.Null(AppWindow.Major("Brave Browser"));
        Assert.Equal(109, AppWindow.Major("109.0.1518.140"));
        Assert.Equal(131, AppWindow.Major("Google Chrome 131.0.6778.85 "));
    }
}
