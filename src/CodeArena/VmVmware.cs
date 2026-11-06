using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// VMware Workstation, Player and Fusion through vmrun (-T ws, player or fusion). A VM is its .vmx file; those known are
/// the ones in VMware's own list (inventory.vmls, the Player's recent ones) and those running. Everything in the guest
/// goes through VMware Tools, signed in with -gu and -gp: vmrun has no other way to take the password.
/// </summary>
internal sealed partial class Vmware(string program, string type, Func<string, string?> env) : Hypervisor(program)
{
    public override string Kind => "vmware";
    public override string Title => type switch { "fusion" => "VMware Fusion", "player" => "VMware Player", _ => "VMware Workstation" };
    public override bool NeedsLogin(GuestNeed need) => true;

    private readonly Dictionary<string, bool> _windows = [];

    /// <summary>vmrun's own lines on Linux (its loader and libaio), which say nothing of the call.</summary>
    protected override string Clean(string text) => string.Join('\n', text.Split('\n').Where(l => !l.StartsWith("[AppLoader]", StringComparison.Ordinal)
        && !l.StartsWith("An up-to-date \"libaio\"", StringComparison.Ordinal)));

    private IEnumerable<string> T => ["-T", type];

    /// <summary>The guest's sign-in, before the command (as vmrun wants it).</summary>
    private static IEnumerable<string> As(GuestLogin? login) =>
        login is null ? throw new ToolError("VMware needs the guest's user and password for this.") : ["-gu", login.User, "-gp", login.Password];

    public override async Task<List<VmInfo>> ListAsync(CancellationToken ct)
    {
        var running = (await MustAsync([.. T, "list"], Short, ct)).Split('\n', StringSplitOptions.TrimEntries)
            .Where(l => l.EndsWith(".vmx", StringComparison.OrdinalIgnoreCase)).ToList();
        var known = Known().ToList();
        foreach (var vmx in running.Where(r => !known.Any(k => Same(k.Vmx, r))))
        {
            known.Add((vmx, null));
        }
        return [.. known.Select(k => new VmInfo(k.Name ?? DisplayName(k.Vmx), k.Vmx, this, running.Any(r => Same(r, k.Vmx))))];
    }

    private static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>The .vmx files VMware lists (Workstation's and Fusion's inventory, the Player's recent ones), with their names.</summary>
    private IEnumerable<(string Vmx, string? Name)> Known()
    {
        var home = env("HOME") ?? env("USERPROFILE") ?? "";
        var files = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(env("APPDATA") ?? "", "VMware", "inventory.vmls"), Path.Combine(env("APPDATA") ?? "", "VMware", "preferences.ini") }
            : OperatingSystem.IsMacOS()
                ? [Path.Combine(home, "Library", "Application Support", "VMware Fusion", "vmInventory")]
                : [Path.Combine(home, ".vmware", "inventory.vmls"), Path.Combine(home, ".vmware", "preferences")];
        var seen = new List<(string, string?)>();
        foreach (var file in files.Where(File.Exists))
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(file))
            {
                if (Setting().Match(line) is { Success: true } m)
                {
                    values[m.Groups[1].Value] = m.Groups[2].Value;
                }
            }
            // vmlist3.config = "/vms/a.vmx", vmlist3.DisplayName = "a"; pref.mruVM0.filename, pref.mruVM0.displayName.
            foreach (var (key, value) in values)
            {
                var stem = key.EndsWith(".config", StringComparison.OrdinalIgnoreCase) ? key[..^".config".Length]
                    : key.EndsWith(".filename", StringComparison.OrdinalIgnoreCase) ? key[..^".filename".Length] : null;
                if (stem is null || !value.EndsWith(".vmx", StringComparison.OrdinalIgnoreCase) || seen.Any(s => Same(s.Item1, value)))
                {
                    continue;
                }
                seen.Add((value, values.GetValueOrDefault(stem + ".DisplayName") is { Length: > 0 } name ? name : null));
            }
        }
        return seen;
    }

    [GeneratedRegex("""^\s*([A-Za-z0-9_.]+)\s*=\s*"(.*)"\s*$""")]
    private static partial Regex Setting();

    /// <summary>A .vmx file's setting (displayName, guestOS), or null.</summary>
    private static string? Vmx(string vmx, string key)
    {
        try
        {
            return File.ReadLines(vmx).Select(l => Setting().Match(l)).FirstOrDefault(m => m.Success && m.Groups[1].Value.Equals(key, StringComparison.OrdinalIgnoreCase))?.Groups[2].Value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string DisplayName(string vmx) => Vmx(vmx, "displayName") is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(vmx);

    /// <summary>A .vmx file named by its path that VMware does not list (yet): it is a VM too.</summary>
    public VmInfo? Unlisted(string path) =>
        path.EndsWith(".vmx", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? new VmInfo(DisplayName(path), Path.GetFullPath(path), this, false) : null;

    public override async Task<List<string>> DetailsAsync(VmInfo vm, CancellationToken ct)
    {
        var tools = vm.Running ? Fmt.OneLine(await MustAsync([.. T, "checkToolsState", vm.Id], Short, ct), 60) : "the VM is off";
        return [$"file: {vm.Id}", $"guest system: {Vmx(vm.Id, "guestOS") ?? "not said"}", $"VMware Tools: {tools} (commands, files, the screen and typing need them running)"];
    }

    public override Task<bool> WindowsAsync(VmInfo vm, CancellationToken ct)
    {
        if (!_windows.TryGetValue(vm.Id, out var windows))
        {
            windows = _windows[vm.Id] = (Vmx(vm.Id, "guestOS") ?? "").StartsWith("win", StringComparison.OrdinalIgnoreCase);
        }
        return Task.FromResult(windows);
    }

    public override async Task StartAsync(VmInfo vm, bool gui, CancellationToken ct) => await MustAsync([.. T, "start", vm.Id, gui ? "gui" : "nogui"], Long, ct);

    public override async Task<bool> StopAsync(VmInfo vm, bool force, TimeSpan wait, CancellationToken ct)
    {
        var r = await CallAsync([.. T, "stop", vm.Id, force ? "hard" : "soft"], wait + Short, ct);
        if (r.TimedOut)
        {
            return false;
        }
        return r.ExitCode == 0 ? true : throw new ToolError($"{Title}: {Said(r)}");
    }

    public override async Task<List<string>> SnapshotsAsync(VmInfo vm, CancellationToken ct) =>
        [.. (await MustAsync([.. T, "listSnapshots", vm.Id], Short, ct)).Split('\n', StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0 && !l.StartsWith("Total snapshots", StringComparison.OrdinalIgnoreCase))];

    public override async Task TakeAsync(VmInfo vm, string name, CancellationToken ct) => await MustAsync([.. T, "snapshot", vm.Id, name], Long, ct);

    public override async Task RestoreAsync(VmInfo vm, string name, CancellationToken ct) => await MustAsync([.. T, "revertToSnapshot", vm.Id, name], Long, ct);

    public override async Task<bool> ReadyAsync(VmInfo vm, GuestLogin? login, CancellationToken ct)
    {
        var r = await CallAsync([.. T, "checkToolsState", vm.Id], Short, ct);
        return r.ExitCode == 0 && Clean(r.Output).Contains("running", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// runProgramInGuest gives no output: the command's goes to a file in the guest's temporary folder, copied back and
    /// deleted. Its exit code is the one vmrun reports ("Guest program exited with non-zero exit code: 3").
    /// </summary>
    public override async Task<GuestRun> RunAsync(VmInfo vm, GuestLogin? login, string command, TimeSpan timeout, CancellationToken ct)
    {
        var windows = await WindowsAsync(vm, ct);
        var id = Guid.NewGuid().ToString("N")[..12];
        var guestOut = windows ? $@"C:\Windows\Temp\code-arena-{id}.txt" : $"/tmp/code-arena-{id}.txt";
        string[] program = windows
            ? [@"C:\Windows\System32\cmd.exe", "/c", $"({command}) > \"{guestOut}\" 2>&1"]
            : ["/bin/sh", "-c", $"({command}) > {Sh(guestOut)} 2>&1"];
        var r = await CallAsync([.. T, .. As(login), "runProgramInGuest", vm.Id, .. program], timeout, ct);
        var said = Clean(r.Output + "\n" + r.Error);
        var code = r.ExitCode;
        if (ExitCode().Match(said) is { Success: true } m)
        {
            code = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        else if (r.ExitCode != 0 && !r.TimedOut)
        {
            throw new ToolError($"{Title}: {Said(r)}");
        }
        var host = HostTemp(".txt");
        try
        {
            var copied = await CallAsync([.. T, .. As(login), "CopyFileFromGuestToHost", vm.Id, guestOut, host], Short, ct);
            var output = copied.ExitCode == 0 && File.Exists(host) ? await File.ReadAllTextAsync(host, ct) : "";
            await CallAsync([.. T, .. As(login), "deleteFileInGuest", vm.Id, guestOut], Short, ct);
            return new GuestRun(r.TimedOut ? -1 : code, output.TrimEnd(), r.TimedOut);
        }
        finally
        {
            Delete(host);
        }
    }

    [GeneratedRegex(@"exit code:\s*(-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ExitCode();

    /// <summary>A folder goes file by file, its folders made first.</summary>
    public override async Task CopyInAsync(VmInfo vm, GuestLogin? login, string host, string guest, CancellationToken ct)
    {
        if (!Directory.Exists(host))
        {
            await MustAsync([.. T, .. As(login), "CopyFileFromHostToGuest", vm.Id, host, guest], Long, ct);
            return;
        }
        var windows = await WindowsAsync(vm, ct);
        var sep = windows ? '\\' : '/';
        await MakeFolderAsync(vm, login, guest, ct);
        foreach (var dir in Directory.EnumerateDirectories(host, "*", SearchOption.AllDirectories))
        {
            await MakeFolderAsync(vm, login, guest.TrimEnd(sep) + sep + Path.GetRelativePath(host, dir).Replace(Path.DirectorySeparatorChar, sep), ct);
        }
        foreach (var file in Directory.EnumerateFiles(host, "*", SearchOption.AllDirectories))
        {
            await MustAsync([.. T, .. As(login), "CopyFileFromHostToGuest", vm.Id, file, guest.TrimEnd(sep) + sep + Path.GetRelativePath(host, file).Replace(Path.DirectorySeparatorChar, sep)], Long, ct);
        }
    }

    public override async Task CopyOutAsync(VmInfo vm, GuestLogin? login, string guest, string host, CancellationToken ct)
    {
        var r = await CallAsync([.. T, .. As(login), "directoryExistsInGuest", vm.Id, guest], Short, ct);
        if (Clean(r.Output).Contains("The directory exists", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolError($"{guest} is a folder: vmrun copies files out one at a time. Pack it in the guest first (vm_run: tar czf /tmp/out.tgz -C {guest} . ; or Compress-Archive on Windows), then copy that file.");
        }
        await MustAsync([.. T, .. As(login), "CopyFileFromGuestToHost", vm.Id, guest, host], Long, ct);
    }

    public override async Task<byte[]> ScreenAsync(VmInfo vm, GuestLogin? login, CancellationToken ct)
    {
        var file = HostTemp(".png");
        try
        {
            await MustAsync([.. T, .. As(login), "captureScreen", vm.Id, file], Short, ct);
            return await File.ReadAllBytesAsync(file, ct);
        }
        finally
        {
            Delete(file);
        }
    }

    public override async Task TypeAsync(VmInfo vm, GuestLogin? login, string text, CancellationToken ct) =>
        await MustAsync([.. T, .. As(login), "typeKeystrokesInGuest", vm.Id, text], Short, ct);

    /// <summary>vmrun types text only: enter and tab as their characters, nothing held with them.</summary>
    public override async Task KeysAsync(VmInfo vm, GuestLogin? login, IReadOnlyList<KeyChord> keys, CancellationToken ct)
    {
        if (keys.FirstOrDefault(k => k.Modifiers.Count > 0 || k.Key is not ("enter" or "tab" or "space")) is { } other)
        {
            throw new ToolError($"vmrun cannot press {other}: it types text only (enter, tab and space among it). Run a command in the guest instead, or use VirtualBox, libvirt or Hyper-V for keys.");
        }
        await TypeAsync(vm, login, string.Concat(keys.Select(k => k.Key switch { "enter" => "\n", "tab" => "\t", _ => " " })), ct);
    }

    public override async Task<string> ProcessesAsync(VmInfo vm, GuestLogin? login, CancellationToken ct) =>
        await MustAsync([.. T, .. As(login), "listProcessesInGuest", vm.Id], Short, ct);
}
