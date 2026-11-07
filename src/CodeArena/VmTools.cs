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
        var vm = await Running(a.Str("vm"), ct);
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

    private async Task<ToolResult> RunToolAsync(JsonObject a, CancellationToken ct)
    {
        var vm = await Running(a.Str("vm"), ct);
        if (a.Bool("processes") == true)
        {
            var list = await AsGuestAsync(vm, GuestNeed.Run, ct, async login => login?.Hide(await vm.Host.ProcessesAsync(vm, login, ct)) ?? await vm.Host.ProcessesAsync(vm, login, ct));
            return new ToolResult(Cut(list)) { Display = $"{list.Split('\n').Length} lines" };
        }
        var command = Required(a, "command");
        var timeout = TimeSpan.FromSeconds(Math.Clamp(a.Int("timeout") ?? DefaultSeconds, 1, MostSeconds));
        var clock = Stopwatch.StartNew();
        var (run, hidden) = await AsGuestAsync(vm, GuestNeed.Run, ct, async login =>
        {
            var r = await vm.Host.RunAsync(vm, login, command, timeout, ct);
            return (r, login?.Hide(r.Output) ?? r.Output);
        });
        var said = run.TimedOut
            ? $"Gave up after {Fmt.Duration(timeout)} (it may still run in the guest)."
            : $"Exit code {run.ExitCode}, after {Fmt.Duration(clock.Elapsed)}.";
        return new ToolResult($"{said}\n{Cut(hidden)}".TrimEnd(), run.TimedOut || run.ExitCode != 0)
        {
            Display = (run.TimedOut ? "timed out" : $"exit {run.ExitCode}") + (hidden.Length > 0 ? "\n" + Fmt.Tail(hidden, 6) : ""),
        };
    }

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
    public const string Usage = "code-arena vm list | vm login VM | vm forget VM";

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
            switch (args)
            {
                case [] or ["list"]:
                    var list = await machines.ListToolAsync([], ct);
                    env.Out.WriteLine(list.Text);
                    return 0;
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
