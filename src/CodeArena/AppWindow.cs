using System.Diagnostics;

namespace CodeArena;

/// <summary>
/// Code Arena's own desktop window: a Chromium-family browser (Chrome, Edge, Chromium, Brave) in app mode, a window
/// with no tabs and no address bar, on a profile of Code Arena's own in its data folder. The browser keys VS Code
/// uses (Ctrl+W, Ctrl+T, Ctrl+N) are the page's there. CODE_ARENA_BROWSER names the browser to use. One older than the
/// page is built for (<see cref="MinBrowser"/>) gets no window: the IDE opens in the default browser, which may be newer.
/// </summary>
internal static partial class AppWindow
{
    /// <summary>The oldest Chrome or Edge the IDE's page is built for: src/web/src/lib/browsers.ts says the same.</summary>
    public const int MinBrowser = 109;

    /// <summary>
    /// Opens the window on the address (the launcher file's: never the key, which a command line shows every user);
    /// null when there is no such browser, one too old, or no desktop, and <paramref name="said"/> is told why (not for
    /// no desktop: there is no browser to open then either). The task ends when the browser it started does.
    /// </summary>
    public static Task? Open(string url, string profile, Func<string, string?> env, Action<string>? said = null)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && string.IsNullOrEmpty(env("DISPLAY")) && string.IsNullOrEmpty(env("WAYLAND_DISPLAY")))
        {
            return null;
        }
        if (Find(env) is not { } browser)
        {
            said?.Invoke(env("CODE_ARENA_BROWSER") is { Length: > 0 } chosen
                ? $"The browser chosen (codeArena.browser, or CODE_ARENA_BROWSER) is no file ({chosen}): the IDE opens in your default browser instead."
                : "No Chrome, Edge, Chromium or Brave here for the IDE's own window: it opens in your default browser instead.");
            return null;
        }
        if (Version(browser) is { } version && version < MinBrowser)
        {
            said?.Invoke($"{Path.GetFileNameWithoutExtension(browser)} {version} is older than the IDE needs (Chrome or Edge {MinBrowser} or newer): it opens in your default browser instead.");
            return null;
        }
        if (!Profile(profile))
        {
            return null;
        }
        var start = new ProcessStartInfo(browser)
        {
            UseShellExecute = false,
            // Its own log lines would land in code-arena's terminal.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { $"--app={url}", $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check", "--class=CodeArena" })
        {
            start.ArgumentList.Add(arg);
        }
        start.Environment.Remove("ARENA_API_KEY");
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return WaitAsync(process);
    }

    private static async Task WaitAsync(Process process)
    {
        using (process)
        {
            await process.WaitForExitAsync();
        }
    }

    /// <summary>
    /// The browser's major version; null when it cannot be read. Windows: the program's file version. macOS: the app's
    /// Info.plist. Elsewhere: what --version prints (Chrome and its kin print it without opening a window).
    /// </summary>
    public static int? Version(string browser)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return FileVersionInfo.GetVersionInfo(browser).FileMajorPart is > 0 and var major ? major : null;
            }
            if (OperatingSystem.IsMacOS())
            {
                var plist = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(browser)!)!, "Info.plist");
                return File.Exists(plist) && PlistVersion().Match(File.ReadAllText(plist)) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;
            }
            var start = new ProcessStartInfo(browser) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--version");
            start.Environment.Remove("ARENA_API_KEY");
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }
            var said = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return Major(said.Result);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The major version in what a browser says of itself ("Chromium 108.0.5359.94", "109.0.1518.140").</summary>
    internal static int? Major(string text) =>
        MajorVersion().Match(text) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var major) ? major : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(\d{2,4})\.\d+\.\d+")]
    private static partial System.Text.RegularExpressions.Regex MajorVersion();

    [System.Text.RegularExpressions.GeneratedRegex(@"<key>CFBundleShortVersionString</key>\s*<string>(\d+)")]
    private static partial System.Text.RegularExpressions.Regex PlistVersion();

    /// <summary>The browser: CODE_ARENA_BROWSER, else the first of Chrome, Edge, Chromium and Brave installed.</summary>
    public static string? Find(Func<string, string?> env)
    {
        if (env("CODE_ARENA_BROWSER") is { Length: > 0 } chosen)
        {
            return File.Exists(chosen) ? chosen : null;
        }
        if (OperatingSystem.IsWindows())
        {
            string[] roots = [.. new[] { env("ProgramFiles"), env("ProgramFiles(x86)"), env("LocalAppData") }.OfType<string>().Where(r => r.Length > 0)];
            string[] apps = [@"Google\Chrome\Application\chrome.exe", @"Microsoft\Edge\Application\msedge.exe", @"Chromium\Application\chrome.exe", @"BraveSoftware\Brave-Browser\Application\brave.exe"];
            return apps.SelectMany(app => roots.Select(root => Path.Combine(root, app))).FirstOrDefault(File.Exists);
        }
        if (OperatingSystem.IsMacOS())
        {
            string[] apps = ["Google Chrome.app/Contents/MacOS/Google Chrome", "Microsoft Edge.app/Contents/MacOS/Microsoft Edge", "Chromium.app/Contents/MacOS/Chromium", "Brave Browser.app/Contents/MacOS/Brave Browser"];
            string[] roots = ["/Applications", Path.Combine(env("HOME") ?? "", "Applications")];
            return apps.SelectMany(app => roots.Select(root => Path.Combine(root, app))).FirstOrDefault(File.Exists);
        }
        string[] names = ["google-chrome-stable", "google-chrome", "chromium", "chromium-browser", "microsoft-edge-stable", "microsoft-edge", "brave-browser"];
        var path = (env("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in names)
        {
            foreach (var dir in path)
            {
                var file = Path.Combine(dir, name);
                // A snap cannot read the data folder's hidden folders, where the profile and the launcher are.
                if (File.Exists(file) && !Real(file).StartsWith("/snap/", StringComparison.Ordinal))
                {
                    return file;
                }
            }
        }
        return null;
    }

    private static string Real(string file)
    {
        try
        {
            return new FileInfo(file).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file;
        }
        catch (IOException)
        {
            return file;
        }
    }

    /// <summary>The window's profile folder, the person's alone (it keeps the page's cookies); false when it cannot be.</summary>
    private static bool Profile(string dir)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(dir);
                return true;
            }
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (new DirectoryInfo(dir).LinkTarget is not null)
            {
                return false;
            }
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
