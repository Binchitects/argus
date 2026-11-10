using System.Diagnostics;

namespace CodeArena;

/// <summary>
/// Code Arena as an application on the computer, as the installers leave it (the Arena's install.sh and install.ps1, run by
/// the setup files people download from its Connect your tools): where each system lists it, and its taking off. The names
/// here are the installers' own: both sides change together.
/// </summary>
internal static class AppInstall
{
    /// <summary>What the menus, the shortcuts and the uninstall list call it.</summary>
    public const string Title = "Code Arena";

    /// <summary>Windows: its entry in Settings → Apps (Programs and Features), for the current user.</summary>
    public const string UninstallKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodeArena";

    /// <summary>Windows: "Open in Code Arena" on a folder, and in a folder's empty space.</summary>
    public static readonly string[] FolderMenuKeys = [@"HKCU\Software\Classes\Directory\shell\CodeArena", @"HKCU\Software\Classes\Directory\Background\shell\CodeArena"];

    /// <summary>Linux: its desktop entry and its icon, in the person's own share folder (XDG).</summary>
    public static string DesktopEntry(string share) => Path.Combine(share, "applications", "code-arena.desktop");

    public static string Icon(string share) => Path.Combine(share, "icons", "hicolor", "512x512", "apps", "code-arena.png");

    /// <summary>macOS: the application in the person's Applications folder.</summary>
    public static string MacApp(string home) => Path.Combine(home, "Applications", Title + ".app");

    /// <summary>The person's share folder: XDG_DATA_HOME, else ~/.local/share.</summary>
    public static string Share(Func<string, string?> env, string home) => env("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".local", "share");

    /// <summary>The files beside the program the installers put there.</summary>
    private static readonly string[] Beside = [Updates.IdeLauncher, Updates.SigningCaFile];

    /// <summary>
    /// Takes Code Arena off: the system's entries for it (Windows: its uninstall entry, its folder menu, its shortcuts and
    /// its folder on PATH; macOS: its app; Linux: its desktop entry and icon), the program and what lies beside it. The
    /// person's sessions and settings stay, unless <paramref name="all"/>. On Windows the program cannot remove itself
    /// while it runs: its folder goes a moment after it ends.
    /// </summary>
    public static int Uninstall(CliEnv env, Ui ui, bool all)
    {
        var exe = env.Exe ?? Environment.ProcessPath;
        if (exe is null || !Path.GetFileNameWithoutExtension(exe).Equals("code-arena", StringComparison.OrdinalIgnoreCase))
        {
            ui.Error("This Code Arena runs from its source (dotnet run, or the build folder): there is nothing installed to take off.");
            return 1;
        }
        var home = env.Env("HOME") ?? env.Env("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folder = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        var gone = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var key in FolderMenuKeys.Append(UninstallKey))
            {
                if (Reg("delete", key, "/f"))
                {
                    gone.Add(key);
                }
            }
            foreach (var place in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
            {
                var link = Path.Combine(place, Title + ".lnk");
                if (place.Length > 0 && File.Exists(link))
                {
                    File.Delete(link);
                    gone.Add(link);
                }
            }
            var path = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            var kept = path.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(p => !string.Equals(p.TrimEnd('\\'), folder, StringComparison.OrdinalIgnoreCase)).ToList();
            if (kept.Count != path.Split(';', StringSplitOptions.RemoveEmptyEntries).Length)
            {
                Environment.SetEnvironmentVariable("Path", string.Join(';', kept), EnvironmentVariableTarget.User);
                gone.Add($"{folder} from your PATH");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (Directory.Exists(MacApp(home)))
            {
                Directory.Delete(MacApp(home), recursive: true);
                gone.Add(MacApp(home));
            }
        }
        else
        {
            var share = Share(env.Env, home);
            foreach (var file in new[] { DesktopEntry(share), Icon(share) }.Where(File.Exists))
            {
                File.Delete(file);
                gone.Add(file);
            }
        }
        foreach (var file in Beside.Select(b => Path.Combine(folder, b)).Where(File.Exists))
        {
            if (!OperatingSystem.IsWindows() || !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
                gone.Add(file);
            }
        }
        if (all)
        {
            foreach (var dir in new[] { env.Paths.ConfigDir, env.Paths.DataDir }.Distinct().Where(Directory.Exists))
            {
                Directory.Delete(dir, recursive: true);
                gone.Add(dir);
            }
        }
        if (OperatingSystem.IsWindows())
        {
            // The program and its IDE launcher, once this ends; their folder too when nothing else is in it.
            var files = string.Join(' ', new[] { exe, Path.Combine(folder, Updates.IdeLauncher) }.Select(f => $"\"{f}\""));
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & del /f /q {files} & rmdir \"{folder}\"") { CreateNoWindow = true, UseShellExecute = false });
            gone.Add($"{exe} (a moment after this ends)");
        }
        else
        {
            File.Delete(exe);
            gone.Add(exe);
        }
        foreach (var what in gone)
        {
            ui.Info($"removed {what}");
        }
        ui.Line(all
            ? "Code Arena is taken off this computer, with your sessions and settings."
            : $"Code Arena is taken off this computer. Your sessions and settings stay in {env.Paths.ConfigDir} and {env.Paths.DataDir} (code-arena uninstall --all takes them too; install it again from your Arena's Connect your tools).");
        return 0;
    }

    /// <summary>reg.exe with these arguments, quietly: whether it did it (false when the key is not there).</summary>
    private static bool Reg(params string[] args)
    {
        var psi = new ProcessStartInfo("reg.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode == 0;
    }
}
