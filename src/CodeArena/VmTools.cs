using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// The VM tools: the person's virtual machines on VirtualBox, VMware, libvirt or Hyper-V, to start and stop, snapshot
/// and restore, copy files into and out of, run commands and apps in, look at and type into; and vm_test, which tests a
/// build in a clean snapshot and puts the VM back after. Listing and looking change nothing and never ask; the rest
/// asks as a command does (an "always" covers that tool on that VM). Guest sign-ins come from <see cref="GuestLogins"/>.
/// </summary>
internal sealed class VirtualMachines(IReadOnlyList<Hypervisor> hosts, GuestLogins logins, Func<bool> sees)
{
    public IReadOnlyList<Hypervisor> Hosts { get; } = hosts;
    public GuestLogins Logins { get; } = logins;

    /// <summary>The longest a command in a guest may run, and its default.</summary>
    public const int MostSeconds = 3600;
    public const int DefaultSeconds = 120;

    /// <summary>The most of a command's output kept: its start and its end.</summary>
    public const int MostOutput = 30_000;

    /// <summary>Where screenshots and test runs are kept in the working directory (never committed).</summary>
    public const string Folder = ".code-arena/vm";

    /// <summary>Every VM of every hypervisor, and what each that could not list said.</summary>
    public async Task<(List<VmInfo> Vms, List<string> Problems)> ListAsync(CancellationToken ct)
    {
        var vms = new List<VmInfo>();
        var problems = new List<string>();
        foreach (var host in Hosts)
        {
            try
            {
                vms.AddRange(await host.ListAsync(ct));
            }
            catch (ToolError e)
            {
                problems.Add($"{host.Title} could not list its VMs: {e.Message}");
            }
        }
        return (vms, problems);
    }

    /// <summary>A VM by its name (any case), its id, "kind:name" (virtualbox:web), or a .vmx file's path.</summary>
    public async Task<VmInfo> FindAsync(string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ToolError("vm is required: a VM's name, as vm_list gives it.");
        }
        name = name.Trim();
        var (vms, problems) = await ListAsync(ct);
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        var kind = colon > 1 && Hosts.Any(h => h.Kind == name[..colon]) ? name[..colon] : null;
        var wanted = kind is null ? name : name[(colon + 1)..];
        var candidates = vms.Where(v => kind is null || v.Host.Kind == kind).ToList();
        var found = candidates.Where(v => v.Name == wanted || v.Id == wanted).ToList();
        if (found.Count == 0)
        {
            found = [.. candidates.Where(v => string.Equals(v.Name, wanted, StringComparison.OrdinalIgnoreCase) || string.Equals(v.Id, wanted, StringComparison.OrdinalIgnoreCase))];
        }
        if (found.Count == 0 && Hosts.OfType<Vmware>().FirstOrDefault() is { } vmware && vmware.Unlisted(wanted) is { } vmx)
        {
            return vmx;
        }
        return found.Count switch
        {
            1 => found[0],
            0 => throw new ToolError($"There is no VM {name}. " + ((vms.Count == 0 ? "No hypervisor here lists any. " : $"There are: {string.Join(", ", vms.Select(v => v.Name))}. ") + string.Join(" ", problems)).TrimEnd()),
            _ => throw new ToolError($"{name} is a VM of {string.Join(" and ", found.Select(v => v.Host.Title))}: say which, as {string.Join(" or ", found.Select(v => $"{v.Host.Kind}:{v.Name}"))}."),
        };
    }

    public List<ToolDef> Tools() =>
    [
        new()
        {
            Name = "vm_list",
            Kind = ToolKind.Read,
            Description = "List the person's virtual machines (VirtualBox, VMware, libvirt/QEMU, Hyper-V) and whether each runs; with vm, one VM's " +
                "details: its guest system, its snapshots, and whether its guest tools answer (what commands and files in it need).",
            Parameters = LocalTools.Schema("""
                {"type":"object","properties":{
                  "vm":{"type":"string","description":"A VM's name for its details; leave out to list them all."}}}
                """),
            Summary = a => a.Str("vm") ?? "all",
            Run = (a, c, ct) => ListToolAsync(a, ct),
        },
        new()
        {
            Name = "vm_power",
            Kind = ToolKind.Machine,
            Description = "Start or stop a VM. start runs it headless (gui: true opens its window). stop asks the guest to shut down and waits for it; " +
                "force: true powers it off at once, as pulling the plug.",
            Parameters = LocalTools.Schema("""
                {"type":"object","properties":{
                  "vm":{"type":"string","description":"The VM's name."},
                  "action":{"type":"string","enum":["start","stop"]},
                  "force":{"type":"boolean","description":"stop: power off at once."},
                  "gui":{"type":"boolean","description":"start: with its window."},
                  "wait":{"type":"integer","description":"stop: seconds to wait for the guest to shut down (default 90)."}},
                  "required":["vm","action"]}
                """),
            Summary = a => $"{a.Str("action")} {a.Str("vm")}{(a.Bool("force") == true ? " (force)" : "")}",
            Run = (a, c, ct) => PowerAsync(a, ct),
        },
        new()
        {
            Name = "vm_snapshot",
            Kind = ToolKind.Machine,
            Description = "Take a snapshot of a VM, or restore one (vm_list with vm lists them). Restoring puts the VM back as it was then, " +
                "and powers it off first where the hypervisor needs that.",
            Parameters = LocalTools.Schema("""
                {"type":"object","properties":{
                  "vm":{"type":"string"},
                  "action":{"type":"string","enum":["take","restore"]},
                  "name":{"type":"string","description":"The snapshot's name."}},
                  "required":["vm","action","name"]}
                """),
            Summary = a => $"{a.Str("action")} {a.Str("name")} on {a.Str("vm")}",
            Run = (a, c, ct) => SnapshotAsync(a, ct),
        },
        new()
        {
            Name = "vm_copy",
            Kind = ToolKind.Machine,
            Description = "Copy a file or folder into a running VM (direction in: from a path here, to a path in the guest) or out of it " +
                "(direction out: from a path in the guest, to a path here). Paths here are in the working directory.",
            Parameters = LocalTools.Schema("""
                {"type":"object","properties":{
                  "vm":{"type":"string"},
                  "direction":{"type":"string","enum":["in","out"]},
                  "from":{"type":"string"},
                  "to":{"type":"string"}},
                  "required":["vm","direction","from","to"]}
                """),
            Summary = a => a.Str("direction") == "out" ? $"{a.Str("vm")}:{a.Str("from")} → {a.Str("to")}" : $"{a.Str("from")} → {a.Str("vm")}:{a.Str("to")}",
            Run = CopyAsync,
        },
        new()
        {
            Name = "vm_run",
            Kind = ToolKind.Machine,
            Description = "Run a command in a running VM's guest, in its shell (sh -c on Linux and macOS guests, cmd /c on Windows), and get its " +
                $"output and exit code. Use it to build, install, start and test an app inside the VM. {DefaultSeconds} s by default, at most {MostSeconds}; " +
                "processes: true lists the guest's processes instead.",
            Parameters = LocalTools.Schema("""
                {"type":"object","properties":{
                  "vm":{"type":"string"},
                  "command":{"type":"string","description":"The command line, as the guest's shell takes it."},
                  "timeout":{"type":"integer","description":"Seconds before it is given up."},
                  "processes":{"type":"boolean","description":"List the guest's processes (no command)."}},
                  "required":["vm"]}
                """),
            Summary = a => $"{a.Str("vm")}: {(a.Bool("processes") == true ? "processes" : Fmt.OneLine(a.Str("command"), 120))}",
            Run = (a, c, ct) => RunToolAsync(a, ct),
        },
        new()
        {
            Name = "vm_screenshot",
            Kind = ToolKind.Read,
            Description = "Take a screenshot of a running VM's screen and look at it (a model that cannot see pictures gets it described). " +
                "It is saved in .code-arena/vm/. Use it to see what an app shows inside the VM, before and after vm_input.",
            Parameters = LocalTools.Schema("""
                {"type":"object","properties":{"vm":{"type":"string"}},"required":["vm"]}
                """),
            Summary = a => a.Str("vm") ?? "",
            Run = ScreenshotToolAsync,
        },
        new()
        {
            Name = "vm_input",
            Kind = ToolKind.Machine,
            Description = "Type into a running VM, press keys, or click: click first (x, y as the last screenshot shows the screen; libvirt and " +
                "Hyper-V only), then text, then keys (\"enter\", \"tab\", \"ctrl+alt+delete\", \"alt+f4\"). Take a screenshot after to see what it did.",
            Parameters = LocalTools.Schema("""
                {"type":"object","properties":{
                  "vm":{"type":"string"},
                  "text":{"type":"string","description":"Text to type, as a keyboard would."},
                  "keys":{"type":"array","items":{"type":"string"},"description":"Keys or chords to press, in order."},
                  "click":{"type":"object","properties":{
                    "x":{"type":"integer"},"y":{"type":"integer"},
                    "button":{"type":"string","enum":["left","right","middle"]},
                    "double":{"type":"boolean"}},"required":["x","y"]}},
                  "required":["vm"]}
                """),
            Summary = a => $"{a.Str("vm")}: {string.Join(", ", new[] { a["click"] is JsonObject k ? $"click {k.Int("x")},{k.Int("y")}" : null, a.Str("text") is { } t ? $"type \"{Fmt.OneLine(t, 40)}\"" : null, a["keys"] is JsonArray ks ? string.Join(' ', ks) : null }.OfType<string>())}",
            Run = (a, c, ct) => InputAsync(a, ct),
        },
        new()
        {
            Name = "vm_create",
            Kind = ToolKind.Machine,
            Description = "Make a new VM, or clone one. create: an empty disk and the installer's ISO (a file on this machine) in its drive, booting " +
                "from it; start it with gui: true and install its system (by vm_screenshot and vm_input, or the person in its window), then its guest " +
                "tools or agent, which commands and files in it need. clone: a copy of a VM that is off, of its state now or of a snapshot " +
                "(linked: sharing the snapshot's disk, where VirtualBox or VMware can), to test in without touching the original.",
            Parameters = LocalTools.Schema($$$"""
                {"type":"object","properties":{
                  "action":{"type":"string","enum":["create","clone"]},
                  "name":{"type":"string","description":"The new VM's name: letters, digits, spaces, '.', '-' and '_'."},
                  "vm":{"type":"string","description":"clone: the VM to copy."},
                  "hypervisor":{"type":"string","enum":["virtualbox","vmware","libvirt","hyperv"],"description":"create: where, when more than one is here."},
                  "iso":{"type":"string","description":"create: the installer's ISO, a path on this machine."},
                  "os":{"type":"string","enum":["linux","windows","other"],"description":"create: its guest system (default linux)."},
                  "memory":{"type":"integer","description":"create: MB of memory (default {{{DefaultMemory}}})."},
                  "cpus":{"type":"integer","description":"create: processors (default 2)."},
                  "disk":{"type":"integer","description":"create: GB of disk, grown as it fills (default {{{DefaultDisk}}})."},
                  "snapshot":{"type":"string","description":"clone: copy this snapshot instead of its state now."},
                  "linked":{"type":"boolean","description":"clone: share the snapshot's disk instead of copying it (needs snapshot)."}},
                  "required":["action","name"]}
                """),
            Summary = a => a.Str("action") == "clone" ? $"clone {a.Str("vm")} as {a.Str("name")}" : $"create {a.Str("name")}",
            Run = (a, c, ct) => CreateAsync(a, c, ct),
        },
        new()
        {
            Name = "vm_delete",
            Kind = ToolKind.Machine,
            // Its disks go for good: the person is asked each time, in every mode.
            AlwaysAsks = true,
            Description = "Delete a VM that is off, with its disks and snapshots, for good (an ISO in its drive stays). The person is asked each time. " +
                "Use it for VMs you made or cloned to test in.",
            Parameters = LocalTools.Schema("""
                {"type":"object","properties":{"vm":{"type":"string","description":"The VM's name."}},"required":["vm"]}
                """),
            Summary = a => $"delete {a.Str("vm")}",
            Run = (a, c, ct) => DeleteAsync(a, ct),
        },
        new()
        {
            Name = "vm_test",
            Kind = ToolKind.Machine,
            Description = "Test a build in a VM from a clean state, in one call: restore the snapshot, start the VM and wait for its guest, copy the " +
                "files in, run the command, copy the logs out and take a screenshot (kept in .code-arena/vm/), then power it off and restore the " +
                "snapshot again (keep: true leaves it running as the test left it). Gives the command's output and exit code.",
            Parameters = LocalTools.Schema("""
                {"type":"object","properties":{
                  "vm":{"type":"string"},
                  "snapshot":{"type":"string","description":"The clean snapshot to start from."},
                  "copy":{"type":"array","items":{"type":"object","properties":{"from":{"type":"string","description":"A path here."},"to":{"type":"string","description":"A path in the guest."}},"required":["from","to"]}},
                  "command":{"type":"string","description":"What runs the test (or the app), in the guest's shell."},
                  "logs":{"type":"array","items":{"type":"string"},"description":"Files in the guest to bring back."},
                  "timeout":{"type":"integer","description":"Seconds the command may take (default 600)."},
                  "boot_wait":{"type":"integer","description":"Seconds to wait for the guest to answer after starting (default 300)."},
                  "keep":{"type":"boolean","description":"Leave the VM running, not restored."}},
                  "required":["vm","snapshot","command"]}
                """),
            Summary = a => $"{a.Str("vm")} from {a.Str("snapshot")}: {Fmt.OneLine(a.Str("command"), 100)}",
            Run = TestAsync,
        },
    ];

    private static string Required(JsonObject a, string name) => a.Str(name) is { Length: > 0 } v ? v : throw new ToolError($"{name} is required.");

    /// <summary>A new VM's memory (MB) and disk (GB) when none is said.</summary>
    public const int DefaultMemory = 2048;
    public const int DefaultDisk = 20;

    /// <summary>A name every hypervisor takes as a VM's and a folder's.</summary>
    private static string NewName(JsonObject a) =>
        Required(a, "name").Trim() is var name && name.Length <= 64 && System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9 ._-]*$") && !name.EndsWith('.')
            ? name : throw new ToolError("name is up to 64 letters, digits, spaces, '.', '-' and '_', starting with a letter or digit.");

    private async Task<ToolResult> CreateAsync(JsonObject a, ToolContext c, CancellationToken ct)
    {
        var name = NewName(a);
        var (vms, _) = await ListAsync(ct);
        if (Required(a, "action") == "clone")
        {
            var source = await FindAsync(a.Str("vm"), ct);
            if (source.Running)
            {
                throw new ToolError($"{source.Name} is running: a clone is made of a VM that is off. Stop it first (vm_power).");
            }
            if (vms.Any(v => v.Host == source.Host && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ToolError($"{source.Host.Title} has a VM named {name} already.");
            }
            var snapshot = a.Str("snapshot") is { Length: > 0 } s ? s : null;
            var linked = a.Bool("linked") == true;
            if (linked && snapshot is null)
            {
                throw new ToolError("A linked clone shares a snapshot's disk: name the snapshot.");
            }
            if (snapshot is not null && !(await source.Host.SnapshotsAsync(source, ct)).Contains(snapshot))
            {
                throw new ToolError($"{source.Name} has no snapshot {snapshot}.");
            }
            var copy = await source.Host.CloneAsync(source, name, snapshot, linked, ct);
            return new ToolResult($"Cloned {source.Name} as {copy.Name} on {source.Host.Title}{(snapshot is null ? "" : $", from {snapshot}")}{(linked ? ", linked to it" : "")} (id {copy.Id}). It is off: start it with vm_power.");
        }
        var host = a.Str("hypervisor") is { Length: > 0 } kind
            ? Hosts.FirstOrDefault(h => h.Kind == kind) ?? throw new ToolError($"There is no {kind} here: there is {string.Join(", ", Hosts.Select(h => h.Kind))}.")
            : Hosts.Count == 1 ? Hosts[0] : throw new ToolError($"Say which hypervisor: {string.Join(", ", Hosts.Select(h => h.Kind))}.");
        if (vms.Any(v => v.Host == host && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ToolError($"{host.Title} has a VM named {name} already.");
        }
        string? iso = null;
        if (a.Str("iso") is { Length: > 0 } given)
        {
            iso = Path.GetFullPath(given, c.Workspace.Root);
            if (!File.Exists(iso))
            {
                throw new ToolError($"There is no ISO {given} here.");
            }
        }
        var os = a.Str("os") is { Length: > 0 } o ? o : "linux";
        if (os is not ("linux" or "windows" or "other"))
        {
            throw new ToolError($"os is linux, windows or other, not {os}.");
        }
        var spec = new VmSpec(name, Math.Clamp(a.Int("memory") ?? DefaultMemory, 256, 1024 * 1024), Math.Clamp(a.Int("cpus") ?? 2, 1, 128), Math.Clamp(a.Int("disk") ?? DefaultDisk, 1, 64 * 1024), iso, os);
        var vm = await host.CreateAsync(spec, ct);
        return new ToolResult($"Made {vm.Name} on {host.Title} ({spec.Shown}; id {vm.Id}). It is off. " +
            (iso is null ? "Its disk is empty and nothing is in its drive: make it again with an iso to install a system from, or clone a VM that has one."
                : "Start it with vm_power (gui: true opens its window) to install its system from the ISO, then install its guest tools (VirtualBox's " +
                  "Guest Additions, VMware Tools, qemu-guest-agent, or a Windows guest's PowerShell for Hyper-V), and take a snapshot of the clean state for vm_test."));
    }

    private async Task<ToolResult> DeleteAsync(JsonObject a, CancellationToken ct)
    {
        var vm = await FindAsync(a.Str("vm"), ct);
        if (vm.Running)
        {
            throw new ToolError($"{vm.Name} is running: stop it first (vm_power).");
        }
        await vm.Host.DeleteAsync(vm, ct);
        Logins.Refused(vm);
        return new ToolResult($"Deleted {vm.Name} from {vm.Host.Title}, with its disks and snapshots.");
    }

    /// <summary>Runs an action with the VM's sign-in when its hypervisor needs one; what goes back has the password blanked.</summary>
    private async Task<T> AsGuestAsync<T>(VmInfo vm, GuestNeed need, CancellationToken ct, Func<GuestLogin?, Task<T>> act)
    {
        var login = vm.Host.NeedsLogin(need) ? await Logins.GetAsync(vm, ct) : null;
        try
        {
            return await act(login);
        }
        catch (ToolError e)
        {
            var said = login?.Hide(e.Message) ?? e.Message;
            if (login is not null && Refusal(said))
            {
                Logins.Refused(vm);
                throw new ToolError($"{said} (the guest refused {login.User}'s sign-in: the next call asks the person again)");
            }
            throw new ToolError(said);
        }
    }

    /// <summary>Whether a hypervisor's error is the guest refusing the user or password.</summary>
    internal static bool Refusal(string said) =>
        said.Contains("VERR_AUTHENTICATION_FAILURE", StringComparison.Ordinal) || said.Contains("not able to logon", StringComparison.OrdinalIgnoreCase)
        || said.Contains("Invalid user name or password", StringComparison.OrdinalIgnoreCase) || said.Contains("user name or password is incorrect", StringComparison.OrdinalIgnoreCase)
        || said.Contains("authentication fail", StringComparison.OrdinalIgnoreCase) || said.Contains("logon failure", StringComparison.OrdinalIgnoreCase);

    internal async Task<ToolResult> ListToolAsync(JsonObject a, CancellationToken ct)
    {
        if (a.Str("vm") is { Length: > 0 } name)
        {
            var vm = await FindAsync(name, ct);
            var lines = new List<string> { $"{vm.Name} ({vm.Host.Title}, id {vm.Id}):" };
            lines.AddRange((await vm.Host.DetailsAsync(vm, ct)).Select(l => "- " + l));
            var snapshots = await vm.Host.SnapshotsAsync(vm, ct);
            lines.Add(snapshots.Count == 0 ? "- snapshots: none (take one with vm_snapshot before testing: vm_test starts from one)" : $"- snapshots: {string.Join(", ", snapshots)}");
            return new ToolResult(string.Join('\n', lines)) { Display = $"{vm.Name}: {snapshots.Count} snapshot{(snapshots.Count == 1 ? "" : "s")}" };
        }
        var (vms, problems) = await ListAsync(ct);
        var sb = new StringBuilder();
        foreach (var host in Hosts)
        {
            var mine = vms.Where(v => v.Host == host).ToList();
            sb.Append(CultureInfo.InvariantCulture, $"{host.Title} ({host.Program}): ");
            sb.Append(mine.Count == 0 ? "no VMs\n" : "\n" + string.Join('\n', mine.Select(v => $"- {v.Name}: {(v.Running ? "running" : v.State ?? "off")}")) + "\n");
        }
        foreach (var p in problems)
        {
            sb.Append(p).Append('\n');
        }
        return new ToolResult(sb.ToString().TrimEnd()) { Display = $"{vms.Count} VM{(vms.Count == 1 ? "" : "s")}, {vms.Count(v => v.Running)} running" };
    }

    private async Task<ToolResult> PowerAsync(JsonObject a, CancellationToken ct)
    {
        var vm = await FindAsync(a.Str("vm"), ct);
        switch (Required(a, "action"))
        {
            case "start":
                if (vm.Running)
                {
                    return new ToolResult($"{vm.Name} is running already.");
                }
                await vm.Host.StartAsync(vm, a.Bool("gui") == true, ct);
                return new ToolResult($"Started {vm.Name}{(a.Bool("gui") == true ? " with its window" : " headless")}. Its guest takes a while to boot: vm_list with vm says when its tools answer.");
            case "stop":
                if (!vm.Running)
                {
                    return new ToolResult($"{vm.Name} is not running.");
                }
                var wait = TimeSpan.FromSeconds(Math.Clamp(a.Int("wait") ?? 90, 5, 900));
                var force = a.Bool("force") == true;
                return await vm.Host.StopAsync(vm, force, wait, ct)
                    ? new ToolResult(force ? $"Powered {vm.Name} off." : $"{vm.Name} shut down.")
                    : new ToolResult($"{vm.Name} still runs after {Fmt.Duration(wait)}: its guest did not shut down (a dialog may hold it). Look with vm_screenshot, or stop it with force: true.", true);
            case var other:
                throw new ToolError($"action is start or stop, not {other}.");
        }
    }

    private async Task<ToolResult> SnapshotAsync(JsonObject a, CancellationToken ct)
    {
        var vm = await FindAsync(a.Str("vm"), ct);
        var name = Required(a, "name");
        switch (Required(a, "action"))
        {
            case "take":
                await vm.Host.TakeAsync(vm, name, ct);
                return new ToolResult($"Took snapshot {name} of {vm.Name}.");
            case "restore":
                var known = await vm.Host.SnapshotsAsync(vm, ct);
                if (!known.Contains(name))
                {
                    throw new ToolError($"{vm.Name} has no snapshot {name}: it has {(known.Count == 0 ? "none" : string.Join(", ", known))}.");
                }
                await vm.Host.RestoreAsync(vm, name, ct);
                return new ToolResult($"Restored {vm.Name} to {name}.");
            case var other:
                throw new ToolError($"action is take or restore, not {other}.");
        }
    }

    private async Task<ToolResult> CopyAsync(JsonObject a, ToolContext c, CancellationToken ct)
    {
        var vm = await Answering(a.Str("vm"), ct);
        var from = Required(a, "from");
        var to = Required(a, "to");
        if (Required(a, "direction") == "out")
        {
            var host = c.Workspace.Resolve(to);
            if (Directory.Exists(host))
            {
                host = Path.Combine(host, GuestName(from));
                c.Workspace.Resolve(host);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(host)!);
            c.BeforeWrite?.Invoke(host);
            await AsGuestAsync(vm, GuestNeed.Copy, ct, async login =>
            {
                await vm.Host.CopyOutAsync(vm, login, from, host, ct);
                return true;
            });
            var size = File.Exists(host) ? Files.Size(new FileInfo(host).Length) : "a folder";
            return new ToolResult($"Copied {vm.Name}:{from} to {c.Workspace.Show(host)} ({size}).");
        }
        var source = c.Workspace.Resolve(from);
        if (!File.Exists(source) && !Directory.Exists(source))
        {
            throw new ToolError($"There is no {from} here.");
        }
        await AsGuestAsync(vm, GuestNeed.Copy, ct, async login =>
        {
            await CopyInAsync(vm, login, source, to, ct);
            return true;
        });
        return new ToolResult($"Copied {c.Workspace.Show(source)} to {vm.Name}:{to}.");
    }

    /// <summary>A file copied in lands in a folder that is there: the guest's folders above it are made first.</summary>
    private static async Task CopyInAsync(VmInfo vm, GuestLogin? login, string source, string guest, CancellationToken ct)
    {
        if (vm.Host is VirtualBox or Vmware)
        {
            var windows = await vm.Host.WindowsAsync(vm, ct);
            await vm.Host.MakeFolderAsync(vm, login, Hypervisor.GuestParent(guest.TrimEnd('/', '\\'), windows), ct);
        }
        await vm.Host.CopyInAsync(vm, login, source, guest, ct);
    }

    private static string GuestName(string path) => path.TrimEnd('/', '\\').Split('/', '\\')[^1] is { Length: > 0 } name ? name : "file";

    /// <summary>A VM that must be running for this (its guest is reached).</summary>
    private async Task<VmInfo> Running(string? name, CancellationToken ct)
    {
        var vm = await FindAsync(name, ct);
        return vm.Running ? vm : throw new ToolError($"{vm.Name} is not running: start it first (vm_power), and give its guest time to boot.");
    }

    // Each guest's tools found answering, and when (asked again a minute later at most).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _ready = new(StringComparer.Ordinal);

    /// <summary>
    /// A running VM whose guest tools answer (what commands and files in it need): said at once when they do not, rather than
    /// a command waiting for its time limit and the model trying again and again.
    /// </summary>
    private async Task<VmInfo> Answering(string? name, CancellationToken ct)
    {
        var vm = await Running(name, ct);
        var key = $"{vm.Host.Title}\n{vm.Id}";
        if (_ready.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(1))
        {
            return vm;
        }
        bool ready;
        try
        {
            using var within = CancellationTokenSource.CreateLinkedTokenSource(ct);
            within.CancelAfter(TimeSpan.FromSeconds(20));
            ready = await vm.Host.ReadyAsync(vm, null, within.Token);
        }
        catch (Exception e) when (e is ToolError or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // The hypervisor did not say: the command is tried, and says itself.
            return vm;
        }
        if (!ready)
        {
            _ready.TryRemove(key, out _);
            throw new ToolError($"{vm.Name}'s guest tools do not answer yet: it may still be booting, or its guest tools (VirtualBox's Guest Additions, " +
                "VMware Tools, the QEMU guest agent, Hyper-V's integration services) are not installed or not running. A command now would only wait. " +
                "Wait for it (vm_list with vm says when they answer), look at its screen with vm_screenshot, or ask the person; do not keep retrying.");
        }
        _ready[key] = DateTime.UtcNow;
        return vm;
    }

    // The commands that failed in a guest, by VM and command: how often in a row, and the last time.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Times, DateTime At, string Said)> _failed = new(StringComparer.Ordinal);

    /// <summary>The most times the same command may fail in a row in a guest before it is refused (a quarter of an hour apart resets it).</summary>
    public const int MostFailures = 2;

    private async Task<ToolResult> RunToolAsync(JsonObject a, CancellationToken ct)
    {
        var vm = await Running(a.Str("vm"), ct);
        if (a.Bool("processes") == true)
        {
            vm = await Answering(vm.Name, ct);
            var list = await AsGuestAsync(vm, GuestNeed.Run, ct, async login => login?.Hide(await vm.Host.ProcessesAsync(vm, login, ct)) ?? await vm.Host.ProcessesAsync(vm, login, ct));
            return new ToolResult(Cut(list)) { Display = $"{list.Split('\n').Length} lines" };
        }
        var command = Required(a, "command");
        var failedKey = $"{vm.Id}\n{command.Trim()}";
        if (_failed.TryGetValue(failedKey, out var before) && before.Times >= MostFailures && DateTime.UtcNow - before.At < TimeSpan.FromMinutes(15))
        {
            throw new ToolError($"`{Fmt.OneLine(command, 80)}` failed {before.Times} times in a row in {vm.Name} already ({before.Said}): it is not run again. " +
                "Something else is wrong: look at the guest's screen (vm_screenshot), check it with vm_list, try another way, or ask the person.");
        }
        vm = await Answering(vm.Name, ct);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(a.Int("timeout") ?? DefaultSeconds, 1, MostSeconds));
        var clock = Stopwatch.StartNew();
        GuestRun run;
        string hidden;
        try
        {
            (run, hidden) = await AsGuestAsync(vm, GuestNeed.Run, ct, async login =>
            {
                var r = await vm.Host.RunAsync(vm, login, command, timeout, ct);
                return (r, login?.Hide(r.Output) ?? r.Output);
            });
        }
        catch (ToolError e)
        {
            Failed(failedKey, Fmt.OneLine(e.Message, 120));
            throw;
        }
        var said = run.TimedOut
            ? $"Gave up after {Fmt.Duration(timeout)} (it may still run in the guest)."
            : $"Exit code {run.ExitCode}, after {Fmt.Duration(clock.Elapsed)}.";
        if (run.TimedOut || run.ExitCode != 0)
        {
            Failed(failedKey, run.TimedOut ? "timed out" : $"exit {run.ExitCode}");
        }
        else
        {
            _failed.TryRemove(failedKey, out _);
        }
        return new ToolResult($"{said}\n{Cut(hidden)}".TrimEnd(), run.TimedOut || run.ExitCode != 0)
        {
            Display = (run.TimedOut ? "timed out" : $"exit {run.ExitCode}") + (hidden.Length > 0 ? "\n" + Fmt.Tail(hidden, 6) : ""),
        };
    }

    private void Failed(string key, string said) =>
        _failed.AddOrUpdate(key, _ => (1, DateTime.UtcNow, said), (_, was) => (DateTime.UtcNow - was.At < TimeSpan.FromMinutes(15) ? was.Times + 1 : 1, DateTime.UtcNow, said));

    private static string Cut(string text) =>
        text.Length <= MostOutput ? text : text[..(MostOutput * 2 / 3)] + $"\n… ({text.Length - MostOutput:N0} characters cut) …\n" + text[^(MostOutput / 3)..];

    private async Task<ToolResult> ScreenshotToolAsync(JsonObject a, ToolContext c, CancellationToken ct)
    {
        var vm = await Running(a.Str("vm"), ct);
        var png = await AsGuestAsync(vm, GuestNeed.Screen, ct, login => vm.Host.ScreenAsync(vm, login, ct));
        var saved = Save(c.Workspace.Root, $"{Safe(vm.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.png", png);
        return Picture(vm, png, saved);
    }

    /// <summary>The screen for the model: the picture when it sees them, else in words; where it was saved, for the person.</summary>
    private ToolResult Picture(VmInfo vm, byte[] png, string saved)
    {
        var size = VmPictures.Size(png) is (var w, var h) ? $"{w}×{h}" : "?";
        if (sees() && png.Length <= Screenshots.MaxBytes)
        {
            return new ToolResult($"Took {vm.Name}'s screen ({size}, saved as {saved}): the picture follows.")
            {
                Pictures = [new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(png) } }],
            };
        }
        return new ToolResult($"Took {vm.Name}'s screen (saved as {saved}). This model cannot see pictures; described: {VmPictures.Describe(png)} " +
            "To read what it says, run a command in the guest (vm_run) or ask the person to look at the file.");
    }

    private async Task<ToolResult> InputAsync(JsonObject a, CancellationToken ct)
    {
        var vm = await Running(a.Str("vm"), ct);
        var keys = new List<KeyChord>();
        foreach (var k in a["keys"] as JsonArray ?? [])
        {
            keys.Add(KeyChord.Parse(k?.ToString() ?? "") ?? throw new ToolError($"There is no key {k}: give names as enter, tab, esc, f5, ctrl+c, alt+f4, ctrl+alt+delete."));
        }
        var text = a.Str("text");
        var click = a["click"] as JsonObject;
        if (click is null && string.IsNullOrEmpty(text) && keys.Count == 0)
        {
            throw new ToolError("Give text, keys or click.");
        }
        var done = new List<string>();
        await AsGuestAsync(vm, GuestNeed.Input, ct, async login =>
        {
            if (click is not null)
            {
                var (x, y) = (click.Int("x") ?? throw new ToolError("click needs x."), click.Int("y") ?? throw new ToolError("click needs y."));
                var button = click.Str("button") ?? "left";
                await vm.Host.ClickAsync(vm, login, x, y, button, click.Bool("double") == true, ct);
                done.Add($"{(click.Bool("double") == true ? "double-clicked" : "clicked")} {button} at {x},{y}");
            }
            if (!string.IsNullOrEmpty(text))
            {
                await vm.Host.TypeAsync(vm, login, text, ct);
                done.Add($"typed {text.Length} character{(text.Length == 1 ? "" : "s")}");
            }
            if (keys.Count > 0)
            {
                await vm.Host.KeysAsync(vm, login, keys, ct);
                done.Add($"pressed {string.Join(' ', keys)}");
            }
            return true;
        });
        return new ToolResult($"In {vm.Name}: {string.Join(", then ", done)}. Take a screenshot to see what it did.");
    }

    /// <summary>
    /// The test in a VM: restore, start, wait for the guest, copy in, run, bring the logs and the screen back, power off
    /// and restore. Whatever fails, the VM is put back (unless keep), and the result says each step.
    /// </summary>
    private async Task<ToolResult> TestAsync(JsonObject a, ToolContext c, CancellationToken ct)
    {
        var vm = await FindAsync(a.Str("vm"), ct);
        var snapshot = Required(a, "snapshot");
        var command = Required(a, "command");
        var copies = (a["copy"] as JsonArray ?? []).OfType<JsonObject>().Select(o => (From: Required(o, "from"), To: Required(o, "to"))).ToList();
        var sources = copies.Select(p => (Host: c.Workspace.Resolve(p.From), p.From, p.To)).ToList();
        if (sources.FirstOrDefault(s => !File.Exists(s.Host) && !Directory.Exists(s.Host)) is { From: { } missing } && missing.Length > 0)
        {
            throw new ToolError($"There is no {missing} here to copy in.");
        }
        var logs = (a["logs"] as JsonArray ?? []).Select(l => l?.ToString()).OfType<string>().Where(l => l.Length > 0).ToList();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(a.Int("timeout") ?? 600, 1, MostSeconds));
        var bootWait = TimeSpan.FromSeconds(Math.Clamp(a.Int("boot_wait") ?? 300, 5, 1800));
        var keep = a.Bool("keep") == true;
        var known = await vm.Host.SnapshotsAsync(vm, ct);
        if (!known.Contains(snapshot))
        {
            throw new ToolError($"{vm.Name} has no snapshot {snapshot}: it has {(known.Count == 0 ? "none (take one of a clean state with vm_snapshot)" : string.Join(", ", known))}.");
        }
        var login = vm.Host.NeedsLogin(GuestNeed.Run) || vm.Host.NeedsLogin(GuestNeed.Copy) ? await Logins.GetAsync(vm, ct) : null;
        var screenLogin = vm.Host.NeedsLogin(GuestNeed.Screen) ? login : null;
        // A folder of its own for each run (with a number when another of the same second has the name).
        var stem = Path.Combine(EnsureIgnored(c.Workspace.Root), $"{Safe(vm.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}");
        var folder = stem;
        for (var n = 2; Directory.Exists(folder); n++)
        {
            folder = $"{stem}-{n}";
        }
        Directory.CreateDirectory(folder);
        var shown = $"{Folder}/{Path.GetFileName(folder)}";
        var steps = new List<string>();
        var clock = Stopwatch.StartNew();
        GuestRun? run = null;
        byte[]? screen = null;
        string? failed = null;
        void Step(string said)
        {
            steps.Add($"{steps.Count + 1}. {said} ({clock.Elapsed.TotalSeconds:0}s)");
            c.Progress?.Invoke($"vm_test {vm.Name}: {said}");
        }
        string Hide(string text) => login?.Hide(text) ?? text;
        try
        {
            await vm.Host.RestoreAsync(vm, snapshot, ct);
            Step($"restored {snapshot}");
            var now = (await vm.Host.ListAsync(ct)).FirstOrDefault(v => v.Id == vm.Id) ?? vm;
            if (!now.Running)
            {
                await vm.Host.StartAsync(now, gui: false, ct);
                Step("started it headless");
            }
            var until = DateTime.UtcNow + bootWait;
            while (!await vm.Host.ReadyAsync(now, login, ct))
            {
                if (DateTime.UtcNow >= until)
                {
                    throw new ToolError($"its guest did not answer within {Fmt.Duration(bootWait)} (are its guest tools or agent installed and set to start?)");
                }
                await Task.Delay(Hypervisor.Poll, ct);
            }
            Step("its guest answers");
            foreach (var (host, from, to) in sources)
            {
                await CopyInAsync(now, login, host, to, ct);
                Step($"copied {from} to {to}");
            }
            run = await vm.Host.RunAsync(now, login, command, timeout, ct);
            Step(run.TimedOut ? $"the command ran past {Fmt.Duration(timeout)} and was given up" : $"the command ended with exit code {run.ExitCode}");
            foreach (var log in logs)
            {
                Directory.CreateDirectory(folder);
                var host = Path.Combine(folder, GuestName(log));
                try
                {
                    await now.Host.CopyOutAsync(now, login, log, host, ct);
                    Step($"brought back {log} as {shown}/{GuestName(log)}");
                }
                catch (ToolError e)
                {
                    Step($"could not bring back {log}: {Hide(e.Message)}");
                }
            }
            try
            {
                screen = await now.Host.ScreenAsync(now, screenLogin, ct);
                Directory.CreateDirectory(folder);
                await File.WriteAllBytesAsync(Path.Combine(folder, "screen.png"), screen, ct);
                Step($"took the screen as {shown}/screen.png");
            }
            catch (ToolError e)
            {
                Step($"could not take the screen: {Hide(e.Message)}");
            }
        }
        catch (ToolError e)
        {
            failed = Hide(e.Message);
            if (login is not null && Refusal(failed))
            {
                Logins.Refused(vm);
            }
        }
        finally
        {
            if (!keep)
            {
                // Put back even when the person stopped the turn: a VM left half-tested would be the next test's start.
                using var back = new CancellationTokenSource(Hypervisor.Long);
                try
                {
                    var now = (await vm.Host.ListAsync(back.Token)).FirstOrDefault(v => v.Id == vm.Id) ?? vm;
                    if (now.Running)
                    {
                        await vm.Host.StopAsync(now, force: true, Hypervisor.Short, back.Token);
                        Step("powered it off");
                    }
                    await vm.Host.RestoreAsync(now with { Running = false }, snapshot, back.Token);
                    Step($"restored {snapshot} again");
                }
                catch (Exception e) when (e is ToolError or OperationCanceledException)
                {
                    Step($"could not put it back: {Hide(e.Message)}");
                    failed ??= "the VM could not be put back as it was";
                }
            }
        }
        var sb = new StringBuilder();
        sb.Append(failed is not null ? $"The test in {vm.Name} stopped: {failed}.\n" : run is { TimedOut: false, ExitCode: 0 } ? $"The test in {vm.Name} passed.\n" : $"The test in {vm.Name} failed.\n");
        sb.Append(string.Join('\n', steps));
        if (run is not null)
        {
            sb.Append("\n\nThe command's output:\n").Append(Cut(Hide(run.Output)));
        }
        if (screen is not null && Picture(vm, screen, $"{shown}/screen.png") is { } picture)
        {
            sb.Append("\n\n").Append(picture.Text);
            return new ToolResult(sb.ToString(), failed is not null || run is not { TimedOut: false, ExitCode: 0 }) { Pictures = picture.Pictures };
        }
        return new ToolResult(sb.ToString(), failed is not null || run is not { TimedOut: false, ExitCode: 0 });
    }

    private static string Safe(string name) => System.Text.RegularExpressions.Regex.Replace(name, "[^A-Za-z0-9_.-]", "_") is { Length: > 0 } safe ? safe : "vm";

    /// <summary>Saves a file in .code-arena/vm (made with a .gitignore of its own); its path as the model sees it.</summary>
    private static string Save(string root, string name, byte[] bytes)
    {
        var folder = EnsureIgnored(root);
        var (stem, extension) = (Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
        for (var n = 2; File.Exists(Path.Combine(folder, name)); n++)
        {
            name = $"{stem}-{n}{extension}";
        }
        File.WriteAllBytes(Path.Combine(folder, name), bytes);
        return $"{Folder}/{name}";
    }

    /// <summary>The folder, made when it is not there, never through a link (nothing is written outside the working directory).</summary>
    private static string EnsureIgnored(string root)
    {
        var top = Path.Combine(root, ".code-arena");
        var folder = Path.Combine(top, "vm");
        foreach (var dir in new[] { top, folder })
        {
            if (new DirectoryInfo(dir) is { Exists: true, LinkTarget: not null } || File.Exists(dir))
            {
                throw new ToolError($"{dir} is a link or a file, not a folder: nothing is saved through it.");
            }
        }
        if (!File.Exists(Path.Combine(folder, ".gitignore")))
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, ".gitignore"), "# What Code Arena's VM tools saved (screens, test logs): kept out of the repository.\n*\n");
        }
        return folder;
    }
}

/// <summary>code-arena vm: the VMs here, and the guests' sign-ins kept in the keyring (login, forget), without a model.</summary>
internal static class VmCommand
{
    public const string Usage = "code-arena vm list [VM] | vm login VM | vm forget VM | vm start VM [--gui] | vm stop VM [--force] | " +
        "vm create NAME [--iso FILE] [--os linux|windows|other] [--memory MB] [--cpus N] [--disk GB] [--hypervisor KIND] | " +
        "vm clone VM NAME [--snapshot S] [--linked] | vm delete VM [--yes]";

    private static JsonObject With(JsonObject o, params (string Key, string Value)[] set)
    {
        foreach (var (key, value) in set)
        {
            o[key] = value;
        }
        return o;
    }

    /// <summary>"--iso a.iso --linked": the options after the words, as a tool's arguments (a flag alone is true).</summary>
    internal static JsonObject Options(IEnumerable<string> words)
    {
        var o = new JsonObject();
        var list = words.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ToolError($"{list[i]} is not an option ({Usage}).");
            }
            var key = list[i][2..];
            if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                var value = list[++i];
                o[key] = int.TryParse(value, CultureInfo.InvariantCulture, out var n) && key is "memory" or "cpus" or "disk" ? n : value;
            }
            else
            {
                o[key] = true;
            }
        }
        return o;
    }

    public static async Task<int> RunAsync(string[] args, CliEnv env, CancellationToken ct)
    {
        var ui = new Ui(env.In, env.Out, env.Err, color: false, canAsk: env.InTerminal) { SecretReader = env.ReadSecret };
        var hosts = Hypervisors.Find(env.Options);
        if (hosts.Count == 0)
        {
            env.Err.WriteLine("code-arena: no hypervisor here: VirtualBox (VBoxManage), VMware (vmrun), libvirt (virsh) or Hyper-V. Install one, or name its program (the IDE's Settings → codeArena.vm, or CODE_ARENA_VBOXMANAGE, CODE_ARENA_VMRUN, CODE_ARENA_VIRSH).");
            return 1;
        }
        var keyring = Keyring.For(env.Env);
        var machines = new VirtualMachines(hosts, new GuestLogins(keyring, ui), () => false);
        try
        {
            // The tools' own calls, as the agent makes them, in the folder it is run in.
            async Task<int> Tool(string name, JsonObject a)
            {
                var result = await machines.Tools().Single(t => t.Name == name).Run(a, new ToolContext { Workspace = new Workspace(env.Cwd), Ui = ui }, ct);
                (result.Error ? env.Err : env.Out).WriteLine(result.Error ? $"code-arena: {result.Text}" : result.Text);
                return result.Error ? 1 : 0;
            }
            switch (args)
            {
                case [] or ["list"]:
                    var list = await machines.ListToolAsync([], ct);
                    env.Out.WriteLine(list.Text);
                    return 0;
                case ["list", var one]:
                    return await Tool("vm_list", new JsonObject { ["vm"] = one });
                case ["start", var name, .. var rest]:
                    return await Tool("vm_power", With(Options(rest), ("vm", name), ("action", "start")));
                case ["stop", var name, .. var rest]:
                    return await Tool("vm_power", With(Options(rest), ("vm", name), ("action", "stop")));
                case ["create", var name, .. var rest]:
                    return await Tool("vm_create", With(Options(rest), ("name", name), ("action", "create")));
                case ["clone", var source, var name, .. var rest]:
                    return await Tool("vm_create", With(Options(rest), ("vm", source), ("name", name), ("action", "clone")));
                case ["delete", var name, .. var rest]:
                    var yes = Options(rest).Bool("yes") == true;
                    if (!yes && !(env.InTerminal && ui.Answer($"Delete {name} with its disks and snapshots, for good? [y/N]")?.Trim().ToLowerInvariant() is "y" or "yes"))
                    {
                        env.Err.WriteLine(env.InTerminal ? "code-arena: nothing deleted." : "code-arena: deleting a VM asks first: add --yes to delete it without asking.");
                        return 1;
                    }
                    return await Tool("vm_delete", new JsonObject { ["vm"] = name });
                case ["login", var name]:
                    var vm = await machines.FindAsync(name, ct);
                    if (!keyring.CanSave)
                    {
                        env.Err.WriteLine($"code-arena: {keyring.Name}: nothing can be kept. Code Arena asks for the sign-in in each session instead.");
                        return 1;
                    }
                    var user = ui.Answer($"User in {vm.Name} ({vm.Host.Title}):")?.Trim();
                    var password = string.IsNullOrEmpty(user) ? null : ui.Answer($"Password of {user}:", secret: true);
                    if (string.IsNullOrEmpty(user) || password is null)
                    {
                        env.Err.WriteLine("code-arena: no sign-in given: nothing kept.");
                        return 1;
                    }
                    if (!await keyring.SaveAsync(GuestLogins.Id(vm), new GuestLogin(user, password), ct))
                    {
                        env.Err.WriteLine($"code-arena: {keyring.Name} did not keep it (is it unlocked?).");
                        return 1;
                    }
                    env.Out.WriteLine($"Kept {user}'s sign-in for {vm.Name} in {keyring.Name}.");
                    return 0;
                case ["forget", var name]:
                    var gone = await machines.FindAsync(name, ct);
                    env.Out.WriteLine(await keyring.ForgetAsync(GuestLogins.Id(gone), ct)
                        ? $"Forgot the sign-in for {gone.Name}."
                        : $"{keyring.Name} held no sign-in for {gone.Name}.");
                    return 0;
                default:
                    env.Err.WriteLine($"code-arena: {Usage}");
                    return 2;
            }
        }
        catch (ToolError e)
        {
            env.Err.WriteLine($"code-arena: {e.Message}");
            return 1;
        }
    }
}
