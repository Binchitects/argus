using System.Diagnostics;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// Screenshots for the model: a web page (a dev server's, or any address) through a headless Chromium-family browser,
/// or the screen as the desktop's own tool takes it (grim, gnome-screenshot, spectacle, ImageMagick's import or scrot on
/// Linux; screencapture on macOS; .NET's drawing on Windows). The picture goes to the model after the tool's result.
/// </summary>
internal static class Screenshots
{
    /// <summary>The largest picture sent to the model.</summary>
    public const int MaxBytes = 6 * 1024 * 1024;

    public static ToolDef Tool(Func<string, string?> env) => new()
    {
        Name = "screenshot",
        Kind = ToolKind.Shell,
        Description = "Take a screenshot and look at it: a web page (give url: the app you are building, served by its dev server, at a width " +
            "and height you choose) or, with no url, the person's whole screen. Use it to check how a page or the app looks after a change.",
        Parameters = LocalTools.Schema("""
            {"type":"object","properties":{
              "url":{"type":"string","description":"The page to take (http://localhost:5173/...). Leave out for the whole screen."},
              "width":{"type":"integer","description":"The page's width in pixels (default 1280)."},
              "height":{"type":"integer","description":"The page's height in pixels (default 800)."}}}
            """),
        Summary = a => a.Str("url") ?? "the screen",
        Run = (args, ctx, ct) => TakeAsync(args, env, ct),
    };

    private static async Task<ToolResult> TakeAsync(JsonObject args, Func<string, string?> env, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("code-arena-shot-").FullName;
        var file = Path.Combine(dir, "shot.png");
        try
        {
            var url = args.Str("url");
            string what;
            if (url is { Length: > 0 })
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var page) || page.Scheme is not ("http" or "https" or "file"))
                {
                    return new ToolResult("Give url as an address: http://localhost:5173/ or https://...", true);
                }
                var width = Math.Clamp(args.Int("width") ?? 1280, 320, 3840);
                var height = Math.Clamp(args.Int("height") ?? 800, 240, 4000);
                if (AppWindow.Find(env) is not { } browser)
                {
                    return new ToolResult("No Chrome, Edge, Chromium or Brave here to take the page with (CODE_ARENA_BROWSER names one).", true);
                }
                await RunAsync(browser, ["--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run", $"--user-data-dir={Path.Combine(dir, "profile")}",
                    $"--window-size={width},{height}", $"--screenshot={file}", page.AbsoluteUri], ct);
                what = $"{page.AbsoluteUri} at {width}×{height}";
            }
            else
            {
                await ScreenAsync(file, env, ct);
                what = "the screen";
            }
            if (!File.Exists(file) || new FileInfo(file).Length == 0)
            {
                return new ToolResult("The screenshot came out empty.", true);
            }
            var bytes = await File.ReadAllBytesAsync(file, ct);
            if (bytes.Length > MaxBytes)
            {
                return new ToolResult($"The screenshot is {bytes.Length / 1024 / 1024} MB, over {MaxBytes / 1024 / 1024} MB: take a page at a smaller size.", true);
            }
            return new ToolResult($"Took {what} ({bytes.Length / 1024} KB): the picture follows.")
            {
                Pictures = [new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(bytes) } }],
            };
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new ToolResult("The screenshot could not be taken: " + e.Message, true);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The whole screen, with the first tool of this system's that is there.</summary>
    private static async Task ScreenAsync(string file, Func<string, string?> env, CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS())
        {
            await RunAsync("screencapture", ["-x", file], ct);
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            var script = "Add-Type -AssemblyName System.Windows.Forms,System.Drawing; $b=[System.Windows.Forms.SystemInformation]::VirtualScreen; " +
                "$i=New-Object System.Drawing.Bitmap $b.Width,$b.Height; $g=[System.Drawing.Graphics]::FromImage($i); $g.CopyFromScreen($b.Left,$b.Top,0,0,$i.Size); " +
                $"$i.Save('{file.Replace("'", "''", StringComparison.Ordinal)}',[System.Drawing.Imaging.ImageFormat]::Png)";
            await RunAsync("powershell", ["-NoProfile", "-NonInteractive", "-Command", script], ct);
            return;
        }
        if (string.IsNullOrEmpty(env("DISPLAY")) && string.IsNullOrEmpty(env("WAYLAND_DISPLAY")))
        {
            throw new InvalidOperationException("there is no screen here (no DISPLAY or WAYLAND_DISPLAY): take a page with url instead");
        }
        (string Name, string[] Args)[] tools = !string.IsNullOrEmpty(env("WAYLAND_DISPLAY"))
            ? [("grim", [file]), ("gnome-screenshot", ["-f", file]), ("spectacle", ["-b", "-n", "-o", file])]
            : [("gnome-screenshot", ["-f", file]), ("spectacle", ["-b", "-n", "-o", file]), ("import", ["-window", "root", file]), ("scrot", ["-o", file])];
        var path = (env("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
        foreach (var (name, args) in tools)
        {
            if (path.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists) is { } exe)
            {
                await RunAsync(exe, args, ct);
                return;
            }
        }
        throw new InvalidOperationException("no screenshot tool here: install grim (Wayland), gnome-screenshot, spectacle, ImageMagick or scrot");
    }

    private static async Task RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }
        start.Environment.Remove("ARENA_API_KEY");
        using var p = Process.Start(start) ?? throw new InvalidOperationException($"{exe} did not start");
        var err = p.StandardError.ReadToEndAsync(ct);
        _ = p.StandardOutput.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await p.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            throw;
        }
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed: {Fmt.OneLine(await err, 300)}");
        }
    }
}
