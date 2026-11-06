using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// libvirt's QEMU/KVM machines through virsh. Commands and files go through the QEMU guest agent in the guest (guest-exec,
/// guest-file-*), which runs as the guest's system account: no sign-in. Keys go in with send-key; a click through QEMU's
/// input events, on a machine with a tablet (an absolute pointer: libvirt adds one with a graphical console).
/// </summary>
internal sealed class Libvirt(string program, string uri) : Hypervisor(program)
{
    public override string Kind => "libvirt";
    public override string Title => "libvirt";
    public override bool NeedsLogin(GuestNeed need) => false;

    /// <summary>How much of a file one guest-file-read or guest-file-write carries.</summary>
    public const int Chunk = 48 * 1024;

    /// <summary>The most a file copied out may hold.</summary>
    public const long MostOut = 256L * 1024 * 1024;

    private readonly Dictionary<string, bool> _windows = [];

    private IEnumerable<string> C => ["-c", uri];

    public override async Task<List<VmInfo>> ListAsync(CancellationToken ct)
    {
        var all = Names(await MustAsync([.. C, "list", "--all", "--name"], Short, ct));
        var running = Names(await MustAsync([.. C, "list", "--name"], Short, ct)).ToHashSet(StringComparer.Ordinal);
        return [.. all.Select(n => new VmInfo(n, n, this, running.Contains(n)))];
    }

    private static List<string> Names(string text) => [.. text.Split('\n', StringSplitOptions.TrimEntries).Where(l => l.Length > 0)];

    private async Task<string> StateAsync(VmInfo vm, CancellationToken ct) => (await MustAsync([.. C, "domstate", vm.Id], Short, ct)).Trim();

    public override async Task<List<string>> DetailsAsync(VmInfo vm, CancellationToken ct)
    {
        var lines = new List<string> { $"state: {await StateAsync(vm, ct)}", $"connection: {uri}" };
        if (vm.Running)
        {
            lines.Add(await AgentAsync(vm, new JsonObject { ["execute"] = "guest-ping" }, ct, quiet: true) is not null
                ? "QEMU guest agent: answers (commands and files work)"
                : "QEMU guest agent: does not answer (install qemu-guest-agent in the guest, and give the machine its channel)");
        }
        lines.Add($"guest system: {(await WindowsAsync(vm, ct) ? "Windows" : "not Windows")}");
        return lines;
    }

    public override async Task<bool> WindowsAsync(VmInfo vm, CancellationToken ct)
    {
        if (_windows.TryGetValue(vm.Id, out var known))
        {
            return known;
        }
        var xml = await MustAsync([.. C, "dumpxml", vm.Id], Short, ct);
        var windows = xml.Contains("microsoft.com/win", StringComparison.OrdinalIgnoreCase)
            || (await AgentAsync(vm, new JsonObject { ["execute"] = "guest-get-osinfo" }, ct, quiet: true))?["id"]?.ToString() == "mswindows";
        return _windows[vm.Id] = windows;
    }

    public override async Task StartAsync(VmInfo vm, bool gui, CancellationToken ct) => await MustAsync([.. C, "start", vm.Id], Short, ct);

    public override async Task<bool> StopAsync(VmInfo vm, bool force, TimeSpan wait, CancellationToken ct)
    {
        await MustAsync([.. C, force ? "destroy" : "shutdown", vm.Id], Short, ct);
        var until = DateTime.UtcNow + (force ? Short : wait);
        while (await StateAsync(vm, ct) is not ("shut off" or "crashed"))
        {
            if (DateTime.UtcNow >= until)
            {
                return false;
            }
            await Task.Delay(Poll, ct);
        }
        return true;
    }

    public override async Task<List<string>> SnapshotsAsync(VmInfo vm, CancellationToken ct) => Names(await MustAsync([.. C, "snapshot-list", vm.Id, "--name"], Short, ct));

    public override async Task TakeAsync(VmInfo vm, string name, CancellationToken ct) => await MustAsync([.. C, "snapshot-create-as", vm.Id, name], Long, ct);

    public override async Task RestoreAsync(VmInfo vm, string name, CancellationToken ct) => await MustAsync([.. C, "snapshot-revert", vm.Id, name], Long, ct);

    public override async Task<bool> ReadyAsync(VmInfo vm, GuestLogin? login, CancellationToken ct) =>
        await AgentAsync(vm, new JsonObject { ["execute"] = "guest-ping" }, ct, quiet: true) is not null;

    /// <summary>A guest agent command's "return"; null when quiet and it failed (the agent does not answer).</summary>
    private async Task<JsonNode?> AgentAsync(VmInfo vm, JsonObject command, CancellationToken ct, bool quiet = false)
    {
        var r = await CallAsync([.. C, "qemu-agent-command", vm.Id, Json.Line(command)], Short, ct);
        if (r.ExitCode != 0 || r.TimedOut)
        {
            return quiet ? null : throw new ToolError($"The QEMU guest agent: {Said(r)}");
        }
        return Json.ParseObject(r.Output.Trim())?["return"] is { } answer ? answer : quiet ? null : throw new ToolError("The QEMU guest agent gave no answer.");
    }

    public override async Task<GuestRun> RunAsync(VmInfo vm, GuestLogin? login, string command, TimeSpan timeout, CancellationToken ct)
    {
        var windows = await WindowsAsync(vm, ct);
        var started = await AgentAsync(vm, new JsonObject
        {
            ["execute"] = "guest-exec",
            ["arguments"] = new JsonObject
            {
                ["path"] = windows ? @"C:\Windows\System32\cmd.exe" : "/bin/sh",
                ["arg"] = windows ? new JsonArray("/c", command) : new JsonArray("-c", command),
                ["capture-output"] = true,
            },
        }, ct);
        var pid = started?["pid"]?.GetValue<long>() ?? throw new ToolError("The QEMU guest agent did not say the command's process.");
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            var status = await AgentAsync(vm, new JsonObject { ["execute"] = "guest-exec-status", ["arguments"] = new JsonObject { ["pid"] = pid } }, ct);
            if (status?["exited"]?.GetValue<bool>() == true)
            {
                var output = Decode(status["out-data"]) + Decode(status["err-data"]);
                return new GuestRun(status["exitcode"]?.GetValue<int>() ?? (status["signal"] is { } signal ? 128 + signal.GetValue<int>() : -1), output.TrimEnd(), false);
            }
            if (DateTime.UtcNow >= until)
            {
                return new GuestRun(-1, $"(still running in the guest as process {pid.ToString(CultureInfo.InvariantCulture)})", true);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(500, Poll.TotalMilliseconds)), ct);
        }
    }

    private static string Decode(JsonNode? data) => data?.ToString() is { Length: > 0 } b64 ? Encoding.UTF8.GetString(Convert.FromBase64String(b64)) : "";

    public override async Task CopyInAsync(VmInfo vm, GuestLogin? login, string host, string guest, CancellationToken ct)
    {
        var windows = await WindowsAsync(vm, ct);
        var sep = windows ? '\\' : '/';
        if (!Directory.Exists(host))
        {
            await MakeFolderAsync(vm, login, GuestParent(guest, windows), ct);
            await WriteAsync(vm, host, guest, ct);
            return;
        }
        await MakeFolderAsync(vm, login, guest, ct);
        foreach (var dir in Directory.EnumerateDirectories(host, "*", SearchOption.AllDirectories))
        {
            await MakeFolderAsync(vm, login, guest.TrimEnd(sep) + sep + Path.GetRelativePath(host, dir).Replace(Path.DirectorySeparatorChar, sep), ct);
        }
        foreach (var file in Directory.EnumerateFiles(host, "*", SearchOption.AllDirectories))
        {
            await WriteAsync(vm, file, guest.TrimEnd(sep) + sep + Path.GetRelativePath(host, file).Replace(Path.DirectorySeparatorChar, sep), ct);
        }
    }

    private async Task WriteAsync(VmInfo vm, string host, string guest, CancellationToken ct)
    {
        var handle = (await AgentAsync(vm, new JsonObject { ["execute"] = "guest-file-open", ["arguments"] = new JsonObject { ["path"] = guest, ["mode"] = "wb" } }, ct))?.GetValue<long>()
            ?? throw new ToolError($"The guest agent could not open {guest}.");
        try
        {
            await using var file = File.OpenRead(host);
            var buffer = new byte[Chunk];
            int n;
            while ((n = await file.ReadAsync(buffer, ct)) > 0)
            {
                await AgentAsync(vm, new JsonObject { ["execute"] = "guest-file-write", ["arguments"] = new JsonObject { ["handle"] = handle, ["buf-b64"] = Convert.ToBase64String(buffer, 0, n) } }, ct);
            }
        }
        finally
        {
            await AgentAsync(vm, new JsonObject { ["execute"] = "guest-file-close", ["arguments"] = new JsonObject { ["handle"] = handle } }, CancellationToken.None, quiet: true);
        }
    }

    public override async Task CopyOutAsync(VmInfo vm, GuestLogin? login, string guest, string host, CancellationToken ct)
    {
        var handle = (await AgentAsync(vm, new JsonObject { ["execute"] = "guest-file-open", ["arguments"] = new JsonObject { ["path"] = guest, ["mode"] = "rb" } }, ct))?.GetValue<long>()
            ?? throw new ToolError($"The guest agent could not open {guest}.");
        var done = false;
        try
        {
            await using (var file = File.Create(host))
            {
                while (true)
                {
                    var read = await AgentAsync(vm, new JsonObject { ["execute"] = "guest-file-read", ["arguments"] = new JsonObject { ["handle"] = handle, ["count"] = Chunk } }, ct);
                    if (read?["buf-b64"]?.ToString() is { Length: > 0 } b64)
                    {
                        await file.WriteAsync(Convert.FromBase64String(b64), ct);
                    }
                    if (file.Length > MostOut)
                    {
                        throw new ToolError($"{guest} is over {MostOut / 1024 / 1024} MB: copy a smaller file, or pack the part you need.");
                    }
                    if (read?["eof"]?.GetValue<bool>() != false)
                    {
                        break;
                    }
                }
            }
            done = true;
        }
        finally
        {
            await AgentAsync(vm, new JsonObject { ["execute"] = "guest-file-close", ["arguments"] = new JsonObject { ["handle"] = handle } }, CancellationToken.None, quiet: true);
            if (!done)
            {
                Delete(host);
            }
        }
    }

    /// <summary>virsh screenshot writes what QEMU gives (a PPM, or a PNG from a newer QEMU): a PNG either way.</summary>
    public override async Task<byte[]> ScreenAsync(VmInfo vm, GuestLogin? login, CancellationToken ct)
    {
        var file = HostTemp(".img");
        try
        {
            await MustAsync([.. C, "screenshot", vm.Id, "--file", file], Short, ct);
            var bytes = await File.ReadAllBytesAsync(file, ct);
            return VmPictures.IsPng(bytes) ? bytes : VmPictures.PpmToPng(bytes) ?? throw new ToolError("The screenshot came in a format other than PNG or PPM.");
        }
        finally
        {
            Delete(file);
        }
    }

    public override async Task TypeAsync(VmInfo vm, GuestLogin? login, string text, CancellationToken ct)
    {
        foreach (var c in text)
        {
            var keys = Typed(c) ?? throw new ToolError($"libvirt types as a US keyboard does: there is no key for '{c}'. Copy a file with the text in, or run a command instead.");
            await MustAsync([.. C, "send-key", vm.Id, "--codeset", "linux", .. keys], Short, ct);
        }
    }

    public override async Task KeysAsync(VmInfo vm, GuestLogin? login, IReadOnlyList<KeyChord> keys, CancellationToken ct)
    {
        foreach (var chord in keys)
        {
            await MustAsync([.. C, "send-key", vm.Id, "--codeset", "linux", .. chord.Modifiers.Append(chord.Key).Select(Linux)], Short, ct);
        }
    }

    /// <summary>A key's name in Linux's codes (KEY_A, KEY_LEFTCTRL).</summary>
    internal static string Linux(string key) => key switch
    {
        "ctrl" => "KEY_LEFTCTRL", "shift" => "KEY_LEFTSHIFT", "alt" => "KEY_LEFTALT", "win" => "KEY_LEFTMETA", "enter" => "KEY_ENTER", "esc" => "KEY_ESC",
        "pageup" => "KEY_PAGEUP", "pagedown" => "KEY_PAGEDOWN",
        _ => "KEY_" + key.ToUpperInvariant(),
    };

    /// <summary>The keys that type a character on a US keyboard (shift with it where it needs one); null for one there is none for.</summary>
    internal static string[]? Typed(char c)
    {
        if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
        {
            return ["KEY_" + char.ToUpperInvariant(c)];
        }
        if (c is >= 'A' and <= 'Z')
        {
            return ["KEY_LEFTSHIFT", "KEY_" + c];
        }
        const string plain = " \n\t-=[]\\;',./`";
        string[] plainKeys = ["KEY_SPACE", "KEY_ENTER", "KEY_TAB", "KEY_MINUS", "KEY_EQUAL", "KEY_LEFTBRACE", "KEY_RIGHTBRACE", "KEY_BACKSLASH", "KEY_SEMICOLON", "KEY_APOSTROPHE", "KEY_COMMA", "KEY_DOT", "KEY_SLASH", "KEY_GRAVE"];
        const string shifted = "!@#$%^&*()_+{}|:\"<>?~";
        string[] shiftedKeys = ["KEY_1", "KEY_2", "KEY_3", "KEY_4", "KEY_5", "KEY_6", "KEY_7", "KEY_8", "KEY_9", "KEY_0", "KEY_MINUS", "KEY_EQUAL", "KEY_LEFTBRACE", "KEY_RIGHTBRACE", "KEY_BACKSLASH", "KEY_SEMICOLON", "KEY_APOSTROPHE", "KEY_COMMA", "KEY_DOT", "KEY_SLASH", "KEY_GRAVE"];
        return plain.IndexOf(c) is var p and >= 0 ? [plainKeys[p]] : shifted.IndexOf(c) is var s and >= 0 ? ["KEY_LEFTSHIFT", shiftedKeys[s]] : null;
    }

    /// <summary>QEMU's absolute pointer: 0 to 32767 across the screen, which a screenshot measures.</summary>
    public override async Task ClickAsync(VmInfo vm, GuestLogin? login, int x, int y, string button, bool twice, CancellationToken ct)
    {
        var (width, height) = VmPictures.Size(await ScreenAsync(vm, login, ct)) ?? throw new ToolError("The screen's size could not be read.");
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            throw new ToolError($"({x}, {y}) is off the screen, which is {width}×{height}.");
        }
        JsonObject Abs(string axis, int at, int size) => new() { ["type"] = "abs", ["data"] = new JsonObject { ["axis"] = axis, ["value"] = size <= 1 ? 0 : at * 32767 / (size - 1) } };
        JsonObject Button(bool down) => new() { ["type"] = "btn", ["data"] = new JsonObject { ["down"] = down, ["button"] = button } };
        await MonitorAsync(vm, [Abs("x", x, width), Abs("y", y, height)], ct);
        for (var i = 0; i < (twice ? 2 : 1); i++)
        {
            await MonitorAsync(vm, [Button(true)], ct);
            await MonitorAsync(vm, [Button(false)], ct);
        }
    }

    private async Task MonitorAsync(VmInfo vm, JsonObject[] events, CancellationToken ct) =>
        await MustAsync([.. C, "qemu-monitor-command", vm.Id, Json.Line(new JsonObject
        {
            ["execute"] = "input-send-event",
            ["arguments"] = new JsonObject { ["events"] = new JsonArray([.. events]) },
        })], Short, ct);
}
