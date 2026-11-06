using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CodeArena;

/// <summary>A virtual machine as its hypervisor lists it: its name, its id there (a UUID, a .vmx path), and whether it runs.</summary>
internal sealed record VmInfo(string Name, string Id, Hypervisor Host, bool Running)
{
    /// <summary>The state in the hypervisor's own words, when it says more than running or not (saved, paused).</summary>
    public string? State { get; init; }

    public string Shown => $"{Name} ({Host.Title}, {(Running ? "running" : State ?? "off")})";
}

/// <summary>How a command in a guest ended: its exit code, what it printed (stdout and stderr together), and whether its time ran out.</summary>
internal sealed record GuestRun(int ExitCode, string Output, bool TimedOut);

/// <summary>What a guest action needs: some hypervisors sign in to the guest for them.</summary>
internal enum GuestNeed { Run, Copy, Screen, Input }

/// <summary>A key with the modifiers held for it (ctrl+alt+delete): its name in lower case, as the person writes it.</summary>
internal sealed record KeyChord(IReadOnlyList<string> Modifiers, string Key)
{
    public static readonly string[] ModifierNames = ["ctrl", "shift", "alt", "win"];

    /// <summary>"ctrl+alt+Delete", "Enter", "shift+tab": a chord; null when it names no key we know.</summary>
    public static KeyChord? Parse(string text)
    {
        var parts = text.Trim().ToLowerInvariant().Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(p => p.Length == 0))
        {
            return null;
        }
        var mods = new List<string>();
        foreach (var m in parts[..^1])
        {
            var name = m switch
            {
                "ctrl" or "control" => "ctrl",
                "shift" => "shift",
                "alt" or "option" => "alt",
                "win" or "meta" or "super" or "cmd" or "command" => "win",
                _ => null,
            };
            if (name is null)
            {
                return null;
            }
            mods.Add(name);
        }
        var key = parts[^1] switch
        {
            "return" => "enter",
            "escape" => "esc",
            "del" => "delete",
            "ins" => "insert",
            "pgup" or "pageup" => "pageup",
            "pgdn" or "pagedown" => "pagedown",
            "bksp" => "backspace",
            var k => k,
        };
        return Keys.Contains(key) ? new KeyChord(mods, key) : null;
    }

    /// <summary>The keys a chord may name besides the letters and digits.</summary>
    public static readonly string[] Named =
    [
        "enter", "esc", "tab", "backspace", "space", "delete", "insert", "home", "end", "pageup", "pagedown", "up", "down", "left", "right",
        "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12", "capslock", "ctrl", "shift", "alt", "win",
    ];

    private static readonly HashSet<string> Keys =
        [.. Named, .. Enumerable.Range('a', 26).Select(c => ((char)c).ToString()), .. Enumerable.Range(0, 10).Select(d => d.ToString(CultureInfo.InvariantCulture))];

    public override string ToString() => string.Join('+', Modifiers.Append(Key));
}

/// <summary>
/// A hypervisor's command-line tool, which Code Arena runs as the person (not in the sandbox: it talks to the hypervisor's
/// service). Each one says what it can do; what it cannot is a <see cref="ToolError"/> saying so.
/// </summary>
internal abstract class Hypervisor(string program)
{
    /// <summary>The program run: VBoxManage, vmrun, virsh, powershell.</summary>
    public string Program { get; } = program;

    /// <summary>virtualbox, vmware, libvirt or hyperv: how a VM is named apart from another hypervisor's.</summary>
    public abstract string Kind { get; }

    public abstract string Title { get; }

    /// <summary>Whether it signs in to the guest for this (with the person's user and password).</summary>
    public abstract bool NeedsLogin(GuestNeed need);

    /// <summary>The most time an administrative call (a list, a start) takes.</summary>
    public static TimeSpan Short { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The most time a long one takes (a snapshot, a restore, a copy).</summary>
    public static TimeSpan Long { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How often a wait (for a VM to stop, for a command in it to end) looks again.</summary>
    public static TimeSpan Poll { get; set; } = TimeSpan.FromSeconds(2);

    public abstract Task<List<VmInfo>> ListAsync(CancellationToken ct);

    /// <summary>What a person should know of one VM besides its state: its guest system, snapshots, tools in the guest.</summary>
    public abstract Task<List<string>> DetailsAsync(VmInfo vm, CancellationToken ct);

    public abstract Task<bool> WindowsAsync(VmInfo vm, CancellationToken ct);

    public abstract Task StartAsync(VmInfo vm, bool gui, CancellationToken ct);

    /// <summary>Asks the guest to shut down and waits up to <paramref name="wait"/>; force: powers it off at once. False: it still runs.</summary>
    public abstract Task<bool> StopAsync(VmInfo vm, bool force, TimeSpan wait, CancellationToken ct);

    public abstract Task<List<string>> SnapshotsAsync(VmInfo vm, CancellationToken ct);

    public abstract Task TakeAsync(VmInfo vm, string name, CancellationToken ct);

    /// <summary>Restores a snapshot; a running VM is powered off first where the hypervisor needs that.</summary>
    public abstract Task RestoreAsync(VmInfo vm, string name, CancellationToken ct);

    /// <summary>Whether the guest answers (its tools or agent run): what a command in it needs.</summary>
    public abstract Task<bool> ReadyAsync(VmInfo vm, GuestLogin? login, CancellationToken ct);

    /// <summary>Runs a command line in the guest's shell (sh -c, or cmd /c on Windows).</summary>
    public abstract Task<GuestRun> RunAsync(VmInfo vm, GuestLogin? login, string command, TimeSpan timeout, CancellationToken ct);

    public abstract Task CopyInAsync(VmInfo vm, GuestLogin? login, string host, string guest, CancellationToken ct);

    public abstract Task CopyOutAsync(VmInfo vm, GuestLogin? login, string guest, string host, CancellationToken ct);

    /// <summary>The guest's screen as a PNG.</summary>
    public abstract Task<byte[]> ScreenAsync(VmInfo vm, GuestLogin? login, CancellationToken ct);

    public abstract Task TypeAsync(VmInfo vm, GuestLogin? login, string text, CancellationToken ct);

    public abstract Task KeysAsync(VmInfo vm, GuestLogin? login, IReadOnlyList<KeyChord> keys, CancellationToken ct);

    /// <summary>A click at a point of the screen as the screenshot shows it; button left, right or middle.</summary>
    public virtual Task ClickAsync(VmInfo vm, GuestLogin? login, int x, int y, string button, bool twice, CancellationToken ct) =>
        throw new ToolError($"{Title}'s {Path.GetFileName(Program)} cannot click. Use keys (tab, enter, alt+f4), or a command in the guest (xdotool on a Linux desktop, a UI test driver on Windows).");

    /// <summary>The processes running in the guest, a line each.</summary>
    public virtual async Task<string> ProcessesAsync(VmInfo vm, GuestLogin? login, CancellationToken ct)
    {
        var run = await RunAsync(vm, login, await WindowsAsync(vm, ct) ? "tasklist" : "ps -eo pid,user,etime,args", Short, ct);
        return run.ExitCode == 0 ? run.Output : throw new ToolError($"The process list failed (exit {run.ExitCode}): {Fmt.OneLine(run.Output, 300)}");
    }

    /// <summary>Makes a folder in the guest and those above it (none: it is there already).</summary>
    public virtual async Task MakeFolderAsync(VmInfo vm, GuestLogin? login, string folder, CancellationToken ct)
    {
        var windows = await WindowsAsync(vm, ct);
        var run = await RunAsync(vm, login, windows ? $"if not exist \"{folder}\" mkdir \"{folder}\"" : $"mkdir -p -- {Sh(folder)}", Short, ct);
        if (run.ExitCode != 0)
        {
            throw new ToolError($"The folder {folder} could not be made in the guest: {Fmt.OneLine(run.Output, 300)}");
        }
    }

    /// <summary>The guest folder a guest path is in, for its own system (C:\a\b.txt → C:\a; /a/b → /a).</summary>
    public static string GuestParent(string path, bool windows)
    {
        var at = windows ? path.LastIndexOfAny(['\\', '/']) : path.LastIndexOf('/');
        return at <= 0 ? (windows ? path : "/") : windows && at == 2 && path[1] == ':' ? path[..3] : path[..at];
    }

    /// <summary>A word for sh, in single quotes.</summary>
    public static string Sh(string word) => "'" + word.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>A string for PowerShell, in single quotes.</summary>
    public static string Ps(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>Runs the program with these arguments (and this on its stdin).</summary>
    protected async Task<Proc.PipeResult> CallAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct, string? input = null)
    {
        var psi = new ProcessStartInfo(Program);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        try
        {
            return await Proc.PipeAsync(psi, input ?? "", timeout, 4 * 1024 * 1024, ct);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new ToolError($"{Program} could not be started: {e.Message}");
        }
    }

    /// <summary>Runs it and gives what it printed; a failure is a <see cref="ToolError"/> with what it said.</summary>
    protected async Task<string> MustAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct, string? input = null)
    {
        var r = await CallAsync(args, timeout, ct, input);
        if (r.TimedOut)
        {
            throw new ToolError($"{Title} did not answer within {Fmt.Duration(timeout)}.");
        }
        if (r.ExitCode != 0)
        {
            throw new ToolError($"{Title}: {Said(r)}");
        }
        return Clean(r.Output);
    }

    /// <summary>What a failed call said: its error lines, else its output.</summary>
    protected string Said(Proc.PipeResult r)
    {
        var text = Clean(r.Error).Trim();
        return Fmt.OneLine(text.Length > 0 ? text : Clean(r.Output), 400) is { Length: > 0 } said ? said : $"{Path.GetFileName(Program)} ended with exit code {r.ExitCode}.";
    }

    /// <summary>Output without the lines a tool says about itself on every call (VMware's loader on Linux).</summary>
    protected virtual string Clean(string text) => text;

    /// <summary>A file name for a temporary file of ours, on the host.</summary>
    protected static string HostTemp(string extension) => Path.Combine(Path.GetTempPath(), $"code-arena-vm-{Guid.NewGuid():N}{extension}");

    protected static void Delete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>The hypervisors on this machine: their programs on PATH or in their usual install folders.</summary>
internal static class Hypervisors
{
    /// <summary>
    /// Each one found, in a fixed order. CODE_ARENA_VBOXMANAGE, CODE_ARENA_VMRUN and CODE_ARENA_VIRSH name a program of
    /// their own; LIBVIRT_DEFAULT_URI the libvirt connection (qemu:///system by default).
    /// </summary>
    public static List<Hypervisor> Find(Func<string, string?> env, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var windows = OperatingSystem.IsWindows();
        var mac = OperatingSystem.IsMacOS();
        var found = new List<Hypervisor>();
        var programFiles = new[] { env("ProgramFiles"), env("ProgramFiles(x86)"), env("ProgramW6432") }.OfType<string>().Where(p => p.Length > 0).Distinct().ToList();
        string Exe(string name) => windows ? name + ".exe" : name;

        var vbox = Program(env, exists, "CODE_ARENA_VBOXMANAGE", [Exe("VBoxManage"), .. windows || mac ? Array.Empty<string>() : ["vboxmanage"]],
            windows ? [env("VBOX_MSI_INSTALL_PATH"), env("VBOX_INSTALL_PATH"), .. programFiles.Select(p => Path.Combine(p, "Oracle", "VirtualBox"))]
            : mac ? ["/Applications/VirtualBox.app/Contents/MacOS", "/usr/local/bin"]
            : ["/usr/lib/virtualbox", "/opt/VirtualBox"]);
        if (vbox is not null)
        {
            found.Add(new VirtualBox(vbox));
        }

        var vmrun = Program(env, exists, "CODE_ARENA_VMRUN", [Exe("vmrun")],
            windows ? [.. programFiles.SelectMany(p => new[] { Path.Combine(p, "VMware", "VMware Workstation"), Path.Combine(p, "VMware", "VMware Player"), Path.Combine(p, "VMware", "VMware VIX") })]
            : mac ? ["/Applications/VMware Fusion.app/Contents/Library", "/Applications/VMware Fusion.app/Contents/Public"]
            : ["/usr/lib/vmware/bin"]);
        if (vmrun is not null)
        {
            var type = mac ? "fusion"
                : windows ? (vmrun.Contains("Workstation", StringComparison.OrdinalIgnoreCase) || programFiles.Any(p => exists(Path.Combine(p, "VMware", "VMware Workstation", "vmware.exe"))) ? "ws" : "player")
                : exists("/usr/bin/vmware") || !exists("/usr/bin/vmplayer") ? "ws" : "player";
            found.Add(new Vmware(vmrun, type, env));
        }

        if (!windows && Program(env, exists, "CODE_ARENA_VIRSH", ["virsh"], mac ? ["/opt/homebrew/bin", "/usr/local/bin"] : []) is { } virsh)
        {
            found.Add(new Libvirt(virsh, env("LIBVIRT_DEFAULT_URI") is { Length: > 0 } uri ? uri : "qemu:///system"));
        }

        if (windows && env("SystemRoot") is { Length: > 0 } root
            && exists(Path.Combine(root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            && Directory.Exists(Path.Combine(root, "System32", "WindowsPowerShell", "v1.0", "Modules", "Hyper-V")))
        {
            found.Add(new HyperV(Path.Combine(root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")));
        }
        return found;
    }

    /// <summary>The program a variable names, else the first of the names on PATH, else in the usual folders.</summary>
    private static string? Program(Func<string, string?> env, Func<string, bool> exists, string variable, string[] names, IEnumerable<string?> usual)
    {
        if (env(variable) is { Length: > 0 } named)
        {
            return exists(named) ? named : null;
        }
        var path = (env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var dir in path.Concat(usual.OfType<string>().Where(u => u.Length > 0)))
        {
            foreach (var name in names)
            {
                var full = Path.Combine(dir, name);
                if (exists(full))
                {
                    return full;
                }
            }
        }
        return null;
    }
}

/// <summary>VirtualBox through VBoxManage: guestcontrol for commands and files (the Guest Additions in the guest), controlvm for the screen and keys.</summary>
internal sealed class VirtualBox(string program) : Hypervisor(program)
{
    public override string Kind => "virtualbox";
    public override string Title => "VirtualBox";
    public override bool NeedsLogin(GuestNeed need) => need is GuestNeed.Run or GuestNeed.Copy;

    private readonly Dictionary<string, bool> _windows = [];

    public override async Task<List<VmInfo>> ListAsync(CancellationToken ct)
    {
        var all = Pairs(await MustAsync(["list", "vms"], Short, ct));
        var running = Pairs(await MustAsync(["list", "runningvms"], Short, ct)).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. all.Select(p => new VmInfo(p.Name, p.Id, this, running.Contains(p.Id)))];
    }

    /// <summary>"name" {uuid} lines.</summary>
    private static List<(string Name, string Id)> Pairs(string text)
    {
        var list = new List<(string, string)>();
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries))
        {
            var open = line.LastIndexOf(" {", StringComparison.Ordinal);
            if (line.StartsWith('"') && open > 1 && line.EndsWith('}'))
            {
                list.Add((line[1..(open - 1)], line[(open + 2)..^1]));
            }
        }
        return list;
    }

    private async Task<Dictionary<string, string>> InfoAsync(VmInfo vm, CancellationToken ct)
    {
        var info = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in (await MustAsync(["showvminfo", vm.Id, "--machinereadable"], Short, ct)).Split('\n', StringSplitOptions.TrimEntries))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                info.TryAdd(line[..eq].Trim('"'), line[(eq + 1)..].Trim('"'));
            }
        }
        return info;
    }

    public override async Task<List<string>> DetailsAsync(VmInfo vm, CancellationToken ct)
    {
        var info = await InfoAsync(vm, ct);
        var lines = new List<string>
        {
            $"state: {info.GetValueOrDefault("VMState", vm.Running ? "running" : "off")}",
            $"guest system: {info.GetValueOrDefault("ostype", "not said")}",
            info.GetValueOrDefault("GuestAdditionsVersion") is { Length: > 0 } additions
                ? $"Guest Additions: {additions} (run level {info.GetValueOrDefault("GuestAdditionsRunLevel", "0")}; commands and files need 2 or more)"
                : "Guest Additions: none seen (commands and files in the guest need them)",
        };
        if (info.GetValueOrDefault("CurrentSnapshotName") is { Length: > 0 } current)
        {
            lines.Add($"current snapshot: {current}");
        }
        return lines;
    }

    public override async Task<bool> WindowsAsync(VmInfo vm, CancellationToken ct)
    {
        if (!_windows.TryGetValue(vm.Id, out var windows))
        {
            windows = _windows[vm.Id] = (await InfoAsync(vm, ct)).GetValueOrDefault("ostype", "").Contains("Windows", StringComparison.OrdinalIgnoreCase);
        }
        return windows;
    }

    public override async Task StartAsync(VmInfo vm, bool gui, CancellationToken ct) =>
        await MustAsync(["startvm", vm.Id, "--type", gui ? "gui" : "headless"], Short, ct);

    public override async Task<bool> StopAsync(VmInfo vm, bool force, TimeSpan wait, CancellationToken ct)
    {
        if (force)
        {
            await MustAsync(["controlvm", vm.Id, "poweroff"], Short, ct);
            return true;
        }
        await MustAsync(["controlvm", vm.Id, "acpipowerbutton"], Short, ct);
        return await WaitOffAsync(vm, wait, ct);
    }

    private async Task<bool> WaitOffAsync(VmInfo vm, TimeSpan wait, CancellationToken ct)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            if ((await InfoAsync(vm, ct)).GetValueOrDefault("VMState") is not ("running" or "stopping" or "paused"))
            {
                return true;
            }
            if (DateTime.UtcNow >= until)
            {
                return false;
            }
            await Task.Delay(Poll, ct);
        }
    }

    public override async Task<List<string>> SnapshotsAsync(VmInfo vm, CancellationToken ct)
    {
        var r = await CallAsync(["snapshot", vm.Id, "list", "--machinereadable"], Short, ct);
        if (r.ExitCode != 0)
        {
            // A VM with none says so and ends with 1.
            return (r.Output + r.Error).Contains("does not have any snapshots", StringComparison.OrdinalIgnoreCase) ? [] : throw new ToolError($"{Title}: {Said(r)}");
        }
        return [.. r.Output.Split('\n', StringSplitOptions.TrimEntries).Where(l => l.StartsWith("SnapshotName", StringComparison.Ordinal) && l.Contains('='))
            .Select(l => l[(l.IndexOf('=', StringComparison.Ordinal) + 1)..].Trim('"'))];
    }

    public override async Task TakeAsync(VmInfo vm, string name, CancellationToken ct) =>
        await MustAsync(["snapshot", vm.Id, "take", name, .. vm.Running ? new[] { "--live" } : []], Long, ct);

    public override async Task RestoreAsync(VmInfo vm, string name, CancellationToken ct)
    {
        // A snapshot is restored on a machine that is off.
        if ((await InfoAsync(vm, ct)).GetValueOrDefault("VMState") is "running" or "paused" or "stuck")
        {
            await MustAsync(["controlvm", vm.Id, "poweroff"], Short, ct);
            await WaitOffAsync(vm, Short, ct);
        }
        await MustAsync(["snapshot", vm.Id, "restore", name], Long, ct);
    }

    public override async Task<bool> ReadyAsync(VmInfo vm, GuestLogin? login, CancellationToken ct)
    {
        var info = await InfoAsync(vm, ct);
        return info.GetValueOrDefault("VMState") == "running" && int.TryParse(info.GetValueOrDefault("GuestAdditionsRunLevel"), out var level) && level >= 2;
    }

    /// <summary>guestcontrol's sign-in: the user, and the password in a file of ours (never on the command line), deleted after.</summary>
    private async Task<T> AsGuestAsync<T>(GuestLogin? login, Func<List<string>, Task<T>> run)
    {
        if (login is null)
        {
            throw new ToolError("VirtualBox needs the guest's user and password for this.");
        }
        var file = HostTemp(".txt");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var stream = new FileStream(file, options))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(login.Password));
            }
            return await run(["--username", login.User, "--passwordfile", file]);
        }
        finally
        {
            Delete(file);
        }
    }

    public override async Task<GuestRun> RunAsync(VmInfo vm, GuestLogin? login, string command, TimeSpan timeout, CancellationToken ct)
    {
        var windows = await WindowsAsync(vm, ct);
        string[] program = windows ? [@"C:\Windows\System32\cmd.exe", "cmd.exe", "/c", command] : ["/bin/sh", "/bin/sh", "-c", command];
        return await AsGuestAsync(login, async sign =>
        {
            var r = await CallAsync(["guestcontrol", vm.Id, "run", .. sign, "--timeout", ((long)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
                "--wait-stdout", "--wait-stderr", "--exe", program[0], "--", .. program[1..]], timeout + Short, ct);
            // VBoxManage says its own errors so; the rest is the guest's command.
            if (r.Error.Contains("VBoxManage: error:", StringComparison.Ordinal))
            {
                throw new ToolError($"{Title}: {Said(r)}");
            }
            var output = (r.Output + (r.Error.Length > 0 && r.Output.Length > 0 && !r.Output.EndsWith('\n') ? "\n" : "") + r.Error).TrimEnd();
            return new GuestRun(r.ExitCode, output, r.TimedOut || r.ExitCode == 19);
        });
    }

    public override async Task MakeFolderAsync(VmInfo vm, GuestLogin? login, string folder, CancellationToken ct) =>
        await AsGuestAsync(login, sign => MustAsync(["guestcontrol", vm.Id, "mkdir", .. sign, "--parents", folder], Short, ct));

    public override async Task CopyInAsync(VmInfo vm, GuestLogin? login, string host, string guest, CancellationToken ct) =>
        await AsGuestAsync(login, sign => MustAsync(["guestcontrol", vm.Id, "copyto", .. sign, .. Directory.Exists(host) ? new[] { "--recursive" } : [], host, guest], Long, ct));

    public override async Task CopyOutAsync(VmInfo vm, GuestLogin? login, string guest, string host, CancellationToken ct) =>
        await AsGuestAsync(login, sign => MustAsync(["guestcontrol", vm.Id, "copyfrom", .. sign, "--recursive", guest, host], Long, ct));

    public override async Task<byte[]> ScreenAsync(VmInfo vm, GuestLogin? login, CancellationToken ct)
    {
        var file = HostTemp(".png");
        try
        {
            await MustAsync(["controlvm", vm.Id, "screenshotpng", file], Short, ct);
            return await File.ReadAllBytesAsync(file, ct);
        }
        finally
        {
            Delete(file);
        }
    }

    public override async Task TypeAsync(VmInfo vm, GuestLogin? login, string text, CancellationToken ct) =>
        await MustAsync(["controlvm", vm.Id, "keyboardputstring", text], Short, ct);

    public override async Task KeysAsync(VmInfo vm, GuestLogin? login, IReadOnlyList<KeyChord> keys, CancellationToken ct)
    {
        foreach (var chord in keys)
        {
            await MustAsync(["controlvm", vm.Id, "keyboardputscancode", .. Scancodes(chord)], Short, ct);
        }
    }

    /// <summary>A chord as PS/2 set 1 scancodes: the modifiers and the key pressed, then let go in the other order.</summary>
    internal static List<string> Scancodes(KeyChord chord)
    {
        var down = chord.Modifiers.Append(chord.Key).Select(Make).ToList();
        var codes = new List<string>();
        foreach (var (extended, code) in down)
        {
            codes.AddRange(extended ? ["e0", code.ToString("x2", CultureInfo.InvariantCulture)] : [code.ToString("x2", CultureInfo.InvariantCulture)]);
        }
        foreach (var (extended, code) in Enumerable.Reverse(down))
        {
            codes.AddRange(extended ? ["e0", (code | 0x80).ToString("x2", CultureInfo.InvariantCulture)] : [(code | 0x80).ToString("x2", CultureInfo.InvariantCulture)]);
        }
        return codes;
    }

    private static (bool Extended, int Code) Make(string key) => key switch
    {
        "esc" => (false, 0x01), "backspace" => (false, 0x0e), "tab" => (false, 0x0f), "enter" => (false, 0x1c), "ctrl" => (false, 0x1d),
        "shift" => (false, 0x2a), "alt" => (false, 0x38), "space" => (false, 0x39), "capslock" => (false, 0x3a),
        "f1" => (false, 0x3b), "f2" => (false, 0x3c), "f3" => (false, 0x3d), "f4" => (false, 0x3e), "f5" => (false, 0x3f), "f6" => (false, 0x40),
        "f7" => (false, 0x41), "f8" => (false, 0x42), "f9" => (false, 0x43), "f10" => (false, 0x44), "f11" => (false, 0x57), "f12" => (false, 0x58),
        "home" => (true, 0x47), "up" => (true, 0x48), "pageup" => (true, 0x49), "left" => (true, 0x4b), "right" => (true, 0x4d), "end" => (true, 0x4f),
        "down" => (true, 0x50), "pagedown" => (true, 0x51), "insert" => (true, 0x52), "delete" => (true, 0x53), "win" => (true, 0x5b),
        "1" => (false, 0x02), "2" => (false, 0x03), "3" => (false, 0x04), "4" => (false, 0x05), "5" => (false, 0x06), "6" => (false, 0x07),
        "7" => (false, 0x08), "8" => (false, 0x09), "9" => (false, 0x0a), "0" => (false, 0x0b),
        "q" => (false, 0x10), "w" => (false, 0x11), "e" => (false, 0x12), "r" => (false, 0x13), "t" => (false, 0x14), "y" => (false, 0x15),
        "u" => (false, 0x16), "i" => (false, 0x17), "o" => (false, 0x18), "p" => (false, 0x19), "a" => (false, 0x1e), "s" => (false, 0x1f),
        "d" => (false, 0x20), "f" => (false, 0x21), "g" => (false, 0x22), "h" => (false, 0x23), "j" => (false, 0x24), "k" => (false, 0x25),
        "l" => (false, 0x26), "z" => (false, 0x2c), "x" => (false, 0x2d), "c" => (false, 0x2e), "v" => (false, 0x2f), "b" => (false, 0x30),
        "n" => (false, 0x31), "m" => (false, 0x32),
        _ => throw new ToolError($"There is no key {key}."),
    };
}
