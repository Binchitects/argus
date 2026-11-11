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

    /// <summary>How long vmrun start may take: a start that takes longer waits on a question in VMware's window.</summary>
    internal static TimeSpan StartWait { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>The device of VMware's monitor module on Linux (vmmon), without which no VM starts.</summary>
    internal static string MonitorDevice { get; set; } = "/dev/vmmon";

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
        // The VMs in VMware's folder for new ones (vm_create makes its own there), each in a folder of its own.
        if (Folder() is { } folder && Directory.Exists(folder))
        {
            foreach (var vmx in Directory.EnumerateDirectories(folder).SelectMany(d => Directory.EnumerateFiles(d, "*.vmx")).Order(StringComparer.Ordinal))
            {
                if (!seen.Any(s => Same(s.Item1, vmx)))
                {
                    seen.Add((vmx, null));
                }
            }
        }
        return seen;
    }

    /// <summary>
    /// The folder VMware makes new VMs in: its preference (prefvmx.defaultVMPath), else its default (~/vmware on Linux,
    /// Documents\Virtual Machines on Windows, ~/Virtual Machines.localized on macOS).
    /// </summary>
    public string? Folder()
    {
        var home = env("HOME") ?? env("USERPROFILE");
        if (home is null)
        {
            return null;
        }
        var prefs = OperatingSystem.IsWindows() ? Path.Combine(env("APPDATA") ?? "", "VMware", "preferences.ini")
            : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Preferences", "VMware Fusion", "preferences")
            : Path.Combine(home, ".vmware", "preferences");
        if (File.Exists(prefs) && File.ReadLines(prefs).Select(l => Setting().Match(l)).FirstOrDefault(m => m.Success && m.Groups[1].Value.Equals("prefvmx.defaultVMPath", StringComparison.OrdinalIgnoreCase)) is { } set
            && set.Groups[2].Value.Length > 0)
        {
            return set.Groups[2].Value;
        }
        return OperatingSystem.IsWindows() ? Path.Combine(home, "Documents", "Virtual Machines")
            : OperatingSystem.IsMacOS() ? Path.Combine(home, "Virtual Machines.localized") : Path.Combine(home, "vmware");
    }

    /// <summary>vmware-vdiskmanager, which makes a VM's disk: beside vmrun, else on PATH.</summary>
    private string DiskManager()
    {
        var name = OperatingSystem.IsWindows() ? "vmware-vdiskmanager.exe" : "vmware-vdiskmanager";
        var dirs = new[] { Path.GetDirectoryName(Program) }.Concat((env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        return dirs.OfType<string>().Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists)
            ?? throw new ToolError($"{Title} has no vmware-vdiskmanager here to make a disk with (VMware Player leaves it out): make the VM in VMware's window, or clone one.");
    }

    /// <summary>
    /// A folder of its own in VMware's folder: a growable disk (vmware-vdiskmanager), then the .vmx naming it, its memory,
    /// processors, a NAT network and the ISO in its CD drive, booting from it while the disk is empty. Listed from its folder.
    /// </summary>
    public override async Task<VmInfo> CreateAsync(VmSpec spec, CancellationToken ct)
    {
        if (spec.Iso?.Contains('"', StringComparison.Ordinal) == true)
        {
            throw new ToolError("The ISO's path has a \" in it, which a .vmx file cannot hold: move or rename it.");
        }
        var dir = Path.Combine(Folder() ?? throw new ToolError("There is no home folder to make the VM in."), spec.Name);
        if (Directory.Exists(dir))
        {
            throw new ToolError($"{dir} is there already: give the VM another name.");
        }
        var tool = DiskManager();
        Directory.CreateDirectory(dir);
        try
        {
            var disk = Path.Combine(dir, spec.Name + ".vmdk");
            var psi = new System.Diagnostics.ProcessStartInfo(tool);
            foreach (var a in new[] { "-c", "-s", $"{spec.DiskGb}GB", "-a", spec.Os == "windows" ? "lsisas1068" : "lsilogic", "-t", "1", disk })
            {
                psi.ArgumentList.Add(a);
            }
            var r = await Proc.PipeAsync(psi, "", Long, 1024 * 1024, ct);
            if (r.ExitCode != 0 || r.TimedOut)
            {
                throw new ToolError($"vmware-vdiskmanager could not make the disk: {Said(r)}");
            }
            var vmx = Path.Combine(dir, spec.Name + ".vmx");
            await File.WriteAllTextAsync(vmx, VmxFile(spec), ct);
            return new VmInfo(spec.Name, vmx, this, false);
        }
        catch (Exception e) when (e is ToolError or IOException or OperationCanceledException)
        {
            Directory.Delete(dir, recursive: true);
            throw;
        }
    }

    /// <summary>A new VM's .vmx: what Workstation's own wizard writes for one, in short.</summary>
    internal static string VmxFile(VmSpec spec)
    {
        var lines = new List<(string, string)>
        {
            (".encoding", "UTF-8"), ("config.version", "8"), ("virtualHW.version", "19"), ("displayName", spec.Name),
            ("guestOS", spec.Os switch { "windows" => "windows9-64", "linux" => "other5xlinux-64", _ => "other-64" }),
            ("memsize", spec.MemoryMb.ToString(CultureInfo.InvariantCulture)), ("numvcpus", spec.Cpus.ToString(CultureInfo.InvariantCulture)),
            ("scsi0.present", "TRUE"), ("scsi0.virtualDev", spec.Os == "windows" ? "lsisas1068" : "lsilogic"),
            ("scsi0:0.present", "TRUE"), ("scsi0:0.fileName", spec.Name + ".vmdk"),
            ("ethernet0.present", "TRUE"), ("ethernet0.connectionType", "nat"), ("ethernet0.virtualDev", "e1000"), ("ethernet0.addressType", "generated"),
            ("usb.present", "TRUE"), ("tools.syncTime", "TRUE"),
            // VMware answers its own questions (moved or copied?) with their defaults: a start with no window never waits on one.
            ("msg.autoAnswer", "TRUE"),
        };
        if (spec.Os == "windows")
        {
            lines.Add(("firmware", "efi"));
        }
        if (spec.Iso is { } iso)
        {
            lines.AddRange([("sata0.present", "TRUE"), ("sata0:1.present", "TRUE"), ("sata0:1.deviceType", "cdrom-image"), ("sata0:1.fileName", iso), ("sata0:1.startConnected", "TRUE")]);
        }
        return string.Concat(lines.Select(l => $"{l.Item1} = \"{l.Item2}\"\n"));
    }

    /// <summary>vmrun clone, into a folder of its own beside the others: full, or linked to the snapshot.</summary>
    public override async Task<VmInfo> CloneAsync(VmInfo vm, string name, string? snapshot, bool linked, CancellationToken ct)
    {
        var dir = Path.Combine(Folder() ?? Path.GetDirectoryName(Path.GetDirectoryName(vm.Id)!)!, name);
        if (Directory.Exists(dir))
        {
            throw new ToolError($"{dir} is there already: give the copy another name.");
        }
        var vmx = Path.Combine(dir, name + ".vmx");
        await MustAsync([.. T, "clone", vm.Id, vmx, linked ? "linked" : "full", .. snapshot is { } s ? new[] { $"-snapshot={s}" } : [], $"-cloneName={name}"], Long, ct);
        return new VmInfo(name, vmx, this, false);
    }

    /// <summary>vmrun deleteVM: the VM's folder goes, its disks and snapshots with it.</summary>
    public override async Task DeleteAsync(VmInfo vm, CancellationToken ct) => await MustAsync([.. T, "deleteVM", vm.Id], Long, ct);

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

    /// <summary>
    /// vmrun start. On Linux a start that fails with VMware's monitor module missing says only "The operation was canceled":
    /// the reason is said with it, and how to load the module.
    /// </summary>
    public override async Task StartAsync(VmInfo vm, bool gui, CancellationToken ct)
    {
        var r = await CallAsync([.. T, "start", vm.Id, gui ? "gui" : "nogui"], StartWait, ct);
        if (r.TimedOut)
        {
            // vmrun start waits, without a word, on a question VMware asks in its window (was the VM moved or copied?).
            throw new ToolError($"{Title} did not start {vm.Name} within {Fmt.Duration(StartWait)}: it is most likely waiting on a question in its window " +
                "(was the VM moved or copied?). Open the VM in VMware and answer it, or put msg.autoAnswer = \"TRUE\" in its .vmx so VMware answers such questions itself.");
        }
        try
        {
            if (r.ExitCode != 0)
            {
                throw new ToolError($"{Title}: {Said(r)}");
            }
        }
        catch (ToolError e) when (OperatingSystem.IsLinux() && !File.Exists(MonitorDevice))
        {
            throw new ToolError($"{e.Message.TrimEnd('.')}. VMware's kernel module vmmon is not loaded ({MonitorDevice} is missing), so no VM starts here. " +
                "Build and load it with sudo vmware-modconfig --console --install-all; with Secure Boot on, the kernel refuses it until it is " +
                "signed with a key enrolled in the firmware (mokutil --import), or Secure Boot is turned off.");
        }
    }

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
    /// A command in the guest, with nothing for vmrun or the guest's shell to unescape: the command goes into a script
    /// file of its own (copied in), run by a second one that sends its output to a file and writes its exit code to
    /// another when it ends. vmrun starts it and returns at once (-noWait), so no vmrun call waits on the command: the
    /// end is looked for every <see cref="Hypervisor.Poll"/>, and past the time allowed the command is killed in the
    /// guest (vmrun killed with it would leave it running, and the guest's next calls waiting behind it). Its input is
    /// empty (NUL, /dev/null): a command that asks for something gets nothing and goes on, rather than waiting until the end.
    /// </summary>
    public override async Task<GuestRun> RunAsync(VmInfo vm, GuestLogin? login, string command, TimeSpan timeout, CancellationToken ct)
    {
        var windows = await WindowsAsync(vm, ct);
        var id = "code-arena-" + Guid.NewGuid().ToString("N")[..12];
        var (dir, sep) = windows ? (@"C:\Windows\Temp", '\\') : ("/tmp", '/');
        string Guest(string suffix) => dir + sep + id + suffix;
        var (script, runner, output, done) = windows ? (Guest(".cmd"), Guest("-run.cmd"), Guest(".txt"), Guest(".done")) : (Guest(".sh"), Guest("-run.sh"), Guest(".txt"), Guest(".done"));
        var files = new List<string>();
        try
        {
            foreach (var (guest, text) in new[] { (script, GuestScript(command, windows)), (runner, GuestRunner(script, output, done, windows)) })
            {
                var host = HostTemp(windows ? ".cmd" : ".sh");
                files.Add(host);
                await File.WriteAllTextAsync(host, text, new System.Text.UTF8Encoding(false), ct);
                await MustAsync([.. T, .. As(login), "CopyFileFromHostToGuest", vm.Id, host, guest], Short, ct);
            }
            string[] program = windows ? [@"C:\Windows\System32\cmd.exe", "/d", "/c", runner] : ["/bin/sh", runner];
            await MustAsync([.. T, .. As(login), "runProgramInGuest", vm.Id, "-noWait", .. program], Short, ct);

            var until = DateTime.UtcNow + timeout;
            int? code = null;
            var stopped = false;
            while (true)
            {
                if (await ExistsAsync(vm, login, done, ct))
                {
                    code = int.TryParse((await ReadGuestAsync(vm, login, done, ct)).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : -1;
                    break;
                }
                if (DateTime.UtcNow >= until)
                {
                    stopped = await KillAsync(vm, login, id, ct);
                    break;
                }
                await Task.Delay(Poll, ct);
            }
            var said = await ReadGuestAsync(vm, login, output, ct);
            return new GuestRun(code ?? -1, said.TrimEnd(), code is null, stopped);
        }
        finally
        {
            foreach (var guest in new[] { script, runner, output, done })
            {
                await CallAsync([.. T, .. As(login), "deleteFileInGuest", vm.Id, guest], Short, CancellationToken.None);
            }
            files.ForEach(Delete);
        }
    }

    /// <summary>The command as a script of its own: a Windows batch file (a for loop's %i written %%i, as a batch file wants it), or sh.</summary>
    internal static string GuestScript(string command, bool windows) => windows
        ? "@echo off\r\n" + Batch(command).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n"
        : command + "\n";

    /// <summary>
    /// What runs the script: in UTF-8, its input empty, its output and errors to one file, then its exit code to another
    /// (written once it ended, so that file is the sign it did). A Windows script runs in a cmd of its own, so its "exit 2" ends only it;
    /// the code's redirection goes first, as "echo 2>file" would send stream 2 to the file instead.
    /// </summary>
    internal static string GuestRunner(string script, string output, string done, bool windows) => windows
        ? $"@echo off\r\nchcp 65001 >NUL\r\ncmd.exe /d /c \"{script}\" <NUL >\"{output}\" 2>&1\r\n>\"{done}\" echo %ERRORLEVEL%\r\n"
        : $"sh {Sh(script)} </dev/null >{Sh(output)} 2>&1\necho $? >{Sh(done)}\n";

    /// <summary>A line typed at cmd's prompt as a batch file holds it: a for loop's variables (%i, %~nxi) doubled, the rest as it was.</summary>
    internal static string Batch(string command)
    {
        var letters = ForVariable().Matches(command).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
        foreach (var l in letters)
        {
            command = Regex.Replace(command, $@"(?<!%)%(~[a-zA-Z$:]*)?{Regex.Escape(l)}(?![A-Za-z0-9_]*%)(?![A-Za-z0-9_])", m => "%" + m.Value);
        }
        return command;
    }

    [GeneratedRegex(@"(?i)\bfor\b(?:\s+/[a-z]+(?:\s+""[^""]*"")?)*\s+(?<!%)%([a-zA-Z])\b")]
    private static partial Regex ForVariable();

    /// <summary>Whether a file is in the guest (vmrun fileExistsInGuest: "The file exists.").</summary>
    private async Task<bool> ExistsAsync(VmInfo vm, GuestLogin? login, string guest, CancellationToken ct)
    {
        var r = await CallAsync([.. T, .. As(login), "fileExistsInGuest", vm.Id, guest], Short, ct);
        return Clean(r.Output).Contains("The file exists", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A guest file's text, copied out ("" when it is not there).</summary>
    private async Task<string> ReadGuestAsync(VmInfo vm, GuestLogin? login, string guest, CancellationToken ct)
    {
        var host = HostTemp(".txt");
        try
        {
            var copied = await CallAsync([.. T, .. As(login), "CopyFileFromGuestToHost", vm.Id, guest, host], Short, ct);
            return copied.ExitCode == 0 && File.Exists(host) ? await File.ReadAllTextAsync(host, ct) : "";
        }
        finally
        {
            Delete(host);
        }
    }

    /// <summary>The command's processes in the guest, found by its files' name in their command line, killed.</summary>
    private async Task<bool> KillAsync(VmInfo vm, GuestLogin? login, string id, CancellationToken ct)
    {
        var listed = await CallAsync([.. T, .. As(login), "listProcessesInGuest", vm.Id], Short, ct);
        var killed = false;
        foreach (var line in Clean(listed.Output).Split('\n').Where(l => l.Contains(id, StringComparison.Ordinal)))
        {
            if (GuestPid().Match(line) is { Success: true } m)
            {
                killed |= (await CallAsync([.. T, .. As(login), "killProcessInGuest", vm.Id, m.Groups[1].Value], Short, ct)).ExitCode == 0;
            }
        }
        return killed;
    }

    [GeneratedRegex(@"pid=(\d+)")]
    private static partial Regex GuestPid();


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
