using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// Hyper-V on Windows through Windows PowerShell's Hyper-V module. Commands and files go through PowerShell Direct (a
/// Windows guest, signed in with the person's user and password, which reach PowerShell on its stdin and never its
/// command line); the screen, keys and clicks through the VM's own keyboard and mouse (root\virtualization\v2).
/// </summary>
internal sealed class HyperV(string program) : Hypervisor(program)
{
    public override string Kind => "hyperv";
    public override string Title => "Hyper-V";
    public override bool NeedsLogin(GuestNeed need) => need is GuestNeed.Run or GuestNeed.Copy;

    /// <summary>Runs a script; the sign-in, when given, is its stdin's first two lines, read into $cred.</summary>
    private async Task<string> ScriptAsync(string script, TimeSpan timeout, CancellationToken ct, GuestLogin? login = null)
    {
        var head = "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'\n";
        if (login is not null)
        {
            head += "$u = [Console]::In.ReadLine(); $p = [Console]::In.ReadLine()\n" +
                "$cred = New-Object System.Management.Automation.PSCredential($u, (ConvertTo-SecureString $p -AsPlainText -Force))\n";
        }
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(head + script));
        return await MustAsync(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded], timeout, ct,
            login is null ? null : login.User + "\n" + login.Password + "\n");
    }

    private static GuestLogin Need(GuestLogin? login) => login ?? throw new ToolError("Hyper-V needs the guest's user and password for this (PowerShell Direct).");

    public override async Task<List<VmInfo>> ListAsync(CancellationToken ct)
    {
        var json = await ScriptAsync("ConvertTo-Json -Compress -InputObject @(Get-VM | ForEach-Object { @{ name = $_.Name; id = \"$($_.Id)\"; state = \"$($_.State)\" } })", Short, ct);
        return [.. (Json.Parse(json) as JsonArray ?? []).OfType<JsonObject>().Select(v => new VmInfo(v.Str("name") ?? "", v.Str("name") ?? "", this, v.Str("state") == "Running") { State = v.Str("state") })];
    }

    public override async Task<List<string>> DetailsAsync(VmInfo vm, CancellationToken ct)
    {
        var info = Json.ParseObject(await ScriptAsync($"$v = Get-VM -Name {Ps(vm.Id)}; ConvertTo-Json -Compress -InputObject @{{ state = \"$($v.State)\"; heartbeat = \"$($v.Heartbeat)\"; generation = $v.Generation }}", Short, ct));
        return [$"state: {info.Str("state")}", $"heartbeat: {info.Str("heartbeat")} (commands and files need the guest up, a Windows guest, and its sign-in)", $"generation: {info.Long("generation")}"];
    }

    /// <summary>PowerShell Direct reaches Windows guests only.</summary>
    public override Task<bool> WindowsAsync(VmInfo vm, CancellationToken ct) => Task.FromResult(true);

    public override async Task StartAsync(VmInfo vm, bool gui, CancellationToken ct) => await ScriptAsync($"Start-VM -Name {Ps(vm.Id)}", Long, ct);

    public override async Task<bool> StopAsync(VmInfo vm, bool force, TimeSpan wait, CancellationToken ct)
    {
        try
        {
            await ScriptAsync($"Stop-VM -Name {Ps(vm.Id)} {(force ? "-TurnOff" : "-Force")}", force ? Short : wait, ct);
            return true;
        }
        catch (ToolError e) when (e.Message.Contains("did not answer", StringComparison.Ordinal))
        {
            return false;
        }
    }

    public override async Task<List<string>> SnapshotsAsync(VmInfo vm, CancellationToken ct) =>
        [.. (Json.Parse(await ScriptAsync($"ConvertTo-Json -Compress -InputObject @(Get-VMSnapshot -VMName {Ps(vm.Id)} | ForEach-Object {{ $_.Name }})", Short, ct)) as JsonArray ?? [])
            .Select(n => n?.ToString()).OfType<string>()];

    public override async Task TakeAsync(VmInfo vm, string name, CancellationToken ct) => await ScriptAsync($"Checkpoint-VM -Name {Ps(vm.Id)} -SnapshotName {Ps(name)}", Long, ct);

    public override async Task RestoreAsync(VmInfo vm, string name, CancellationToken ct) =>
        await ScriptAsync($"Restore-VMSnapshot -VMName {Ps(vm.Id)} -Name {Ps(name)} -Confirm:$false", Long, ct);

    /// <summary>
    /// New-VM as Hyper-V Manager makes one: generation 2, a new VHDX in the host's disk folder, its processors, the Default
    /// Switch when there is one, and the ISO in a DVD drive booted first. A Linux guest gets the UEFI CA's Secure Boot template
    /// (the Windows one refuses Linux installers); another, Secure Boot off.
    /// </summary>
    public override async Task<VmInfo> CreateAsync(VmSpec spec, CancellationToken ct)
    {
        var n = Ps(spec.Name);
        var script = new StringBuilder()
            .Append($"if (Get-VM -Name {n} -ErrorAction SilentlyContinue) {{ throw 'There is a VM named {spec.Name.Replace("'", "''", StringComparison.Ordinal)} already.' }}\n")
            .Append($"$vhd = Join-Path (Get-VMHost).VirtualHardDiskPath ({n} + '.vhdx')\n")
            .Append($"$vm = New-VM -Name {n} -Generation 2 -MemoryStartupBytes {spec.MemoryMb}MB -NewVHDPath $vhd -NewVHDSizeBytes {spec.DiskGb}GB\n")
            .Append("try {\n")
            .Append($"  Set-VMProcessor -VM $vm -Count {spec.Cpus}\n")
            .Append("  if (Get-VMSwitch -Name 'Default Switch' -ErrorAction SilentlyContinue) { Connect-VMNetworkAdapter -VMName $vm.Name -SwitchName 'Default Switch' }\n")
            .Append(spec.Os switch
            {
                "windows" => "",
                "linux" => "  Set-VMFirmware -VM $vm -SecureBootTemplate MicrosoftUEFICertificateAuthority\n",
                _ => "  Set-VMFirmware -VM $vm -EnableSecureBoot Off\n",
            });
        if (spec.Iso is { } iso)
        {
            script.Append($"  $dvd = Add-VMDvdDrive -VM $vm -Path {Ps(iso)} -Passthru\n  Set-VMFirmware -VM $vm -FirstBootDevice $dvd\n");
        }
        script.Append("} catch { Remove-VM -VM $vm -Force; Remove-Item -Force -ErrorAction SilentlyContinue $vhd; throw }\n");
        await ScriptAsync(script.ToString(), Long, ct);
        return new VmInfo(spec.Name, spec.Name, this, false);
    }

    /// <summary>
    /// Hyper-V has no clone: the VM (or its snapshot) is exported to a temporary folder and imported again as a copy with an
    /// id of its own, its disks in a folder named after the copy, then renamed. The export is deleted after.
    /// </summary>
    public override async Task<VmInfo> CloneAsync(VmInfo vm, string name, string? snapshot, bool linked, CancellationToken ct)
    {
        if (linked)
        {
            throw new ToolError("Hyper-V clones only as full copies (an export imported again): leave linked out.");
        }
        var n = Ps(name);
        var export = snapshot is null ? $"Export-VM -Name {Ps(vm.Id)} -Path $tmp" : $"Export-VMSnapshot -VMName {Ps(vm.Id)} -Name {Ps(snapshot)} -Path $tmp";
        await ScriptAsync(
            $"if (Get-VM -Name {n} -ErrorAction SilentlyContinue) {{ throw 'There is a VM named {name.Replace("'", "''", StringComparison.Ordinal)} already.' }}\n" +
            "$tmp = Join-Path $env:TEMP ('code-arena-clone-' + [guid]::NewGuid().ToString('N'))\n" +
            $"try {{\n  {export}\n" +
            "  $config = Get-ChildItem -Path $tmp -Recurse -Filter *.vmcx | Select-Object -First 1\n" +
            "  $h = Get-VMHost\n" +
            $"  $copy = Import-VM -Path $config.FullName -Copy -GenerateNewId -VirtualMachinePath (Join-Path $h.VirtualMachinePath {n}) -VhdDestinationPath (Join-Path $h.VirtualHardDiskPath {n})\n" +
            $"  Rename-VM -VM $copy -NewName {n}\n" +
            "} finally { Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $tmp }", Long, ct);
        return new VmInfo(name, name, this, false);
    }

    /// <summary>Its checkpoints first (merged into its disks), then the VM, then its disks' files; an ISO in its drive stays.</summary>
    public override async Task DeleteAsync(VmInfo vm, CancellationToken ct) =>
        await ScriptAsync(
            $"$vm = Get-VM -Name {Ps(vm.Id)}\n" +
            "Get-VMSnapshot -VM $vm | Remove-VMSnapshot\n" +
            "while ((Get-VM -Id $vm.Id).Status -match 'Merging') { Start-Sleep -Seconds 2 }\n" +
            "$disks = @(Get-VMHardDiskDrive -VM $vm | ForEach-Object { $_.Path })\n" +
            "Remove-VM -VM $vm -Force\n" +
            "$disks | ForEach-Object { Remove-Item -Force -ErrorAction SilentlyContinue $_ }", Long, ct);

    public override async Task<bool> ReadyAsync(VmInfo vm, GuestLogin? login, CancellationToken ct) =>
        (await ScriptAsync($"\"$((Get-VM -Name {Ps(vm.Id)}).Heartbeat)\"", Short, ct)).Trim().StartsWith("Ok", StringComparison.OrdinalIgnoreCase);

    public override async Task<GuestRun> RunAsync(VmInfo vm, GuestLogin? login, string command, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var json = await ScriptAsync(
                $"$r = Invoke-Command -VMName {Ps(vm.Id)} -Credential $cred -ArgumentList {Ps(command)} -ScriptBlock {{ param($c) $o = & cmd.exe /c $c 2>&1 | Out-String; @{{ out = $o; code = $LASTEXITCODE }} }}\n" +
                "ConvertTo-Json -Compress -InputObject @{ out = $r.out; code = $r.code }", timeout, ct, Need(login));
            var result = Json.ParseObject(json);
            return new GuestRun(result.Int("code") ?? -1, (result.Str("out") ?? "").TrimEnd(), false);
        }
        catch (ToolError e) when (e.Message.Contains("did not answer", StringComparison.Ordinal))
        {
            return new GuestRun(-1, "", true);
        }
    }

    public override async Task CopyInAsync(VmInfo vm, GuestLogin? login, string host, string guest, CancellationToken ct) =>
        await ScriptAsync($"$s = New-PSSession -VMName {Ps(vm.Id)} -Credential $cred; try {{ " +
            $"Invoke-Command -Session $s -ArgumentList {Ps(GuestParent(guest, true))} -ScriptBlock {{ param($d) New-Item -ItemType Directory -Force -Path $d | Out-Null }}; " +
            $"Copy-Item -ToSession $s -Path {Ps(host)} -Destination {Ps(guest)} -Recurse -Force }} finally {{ Remove-PSSession $s }}", Long, ct, Need(login));

    public override async Task CopyOutAsync(VmInfo vm, GuestLogin? login, string guest, string host, CancellationToken ct) =>
        await ScriptAsync($"$s = New-PSSession -VMName {Ps(vm.Id)} -Credential $cred; try {{ Copy-Item -FromSession $s -Path {Ps(guest)} -Destination {Ps(host)} -Recurse -Force }} finally {{ Remove-PSSession $s }}",
            Long, ct, Need(login));

    private static string Machine(string name) =>
        $"$vm = Get-CimInstance -Namespace root\\virtualization\\v2 -ClassName Msvm_ComputerSystem -Filter \"ElementName='{name.Replace("'", "''", StringComparison.Ordinal).Replace("\"", "`\"", StringComparison.Ordinal)}'\"\n";

    /// <summary>The screen as Hyper-V's thumbnail gives it, at its full size: 16-bit (RGB565) pixels, made a PNG here.</summary>
    public override async Task<byte[]> ScreenAsync(VmInfo vm, GuestLogin? login, CancellationToken ct)
    {
        var json = await ScriptAsync(Machine(vm.Id) +
            "$head = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_VideoHead | Select-Object -First 1\n" +
            "$w = [int]$head.CurrentHorizontalResolution; $h = [int]$head.CurrentVerticalResolution\n" +
            "$set = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_VirtualSystemSettingData | Where-Object VirtualSystemType -eq 'Microsoft:Hyper-V:System:Realized'\n" +
            "$svc = Get-CimInstance -Namespace root\\virtualization\\v2 -ClassName Msvm_VirtualSystemManagementService\n" +
            "$r = Invoke-CimMethod -InputObject $svc -MethodName GetVirtualSystemThumbnailImage -Arguments @{ TargetSystem = $set; WidthPixels = [uint16]$w; HeightPixels = [uint16]$h }\n" +
            "ConvertTo-Json -Compress -InputObject @{ w = $w; h = $h; data = [Convert]::ToBase64String([byte[]]$r.ImageData) }", Short, ct);
        var shot = Json.ParseObject(json);
        return VmPictures.Rgb565ToPng(shot.Int("w") ?? 0, shot.Int("h") ?? 0, Convert.FromBase64String(shot.Str("data") ?? ""))
            ?? throw new ToolError("Hyper-V gave no picture of the screen (is the VM running?).");
    }

    public override async Task TypeAsync(VmInfo vm, GuestLogin? login, string text, CancellationToken ct) =>
        await ScriptAsync(Machine(vm.Id) + "$kb = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_Keyboard\n" +
            $"Invoke-CimMethod -InputObject $kb -MethodName TypeText -Arguments @{{ asciiText = {Ps(text)} }} | Out-Null", Short, ct);

    public override async Task KeysAsync(VmInfo vm, GuestLogin? login, IReadOnlyList<KeyChord> keys, CancellationToken ct)
    {
        var script = new StringBuilder(Machine(vm.Id)).Append("$kb = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_Keyboard\n");
        foreach (var chord in keys)
        {
            if (chord.Key == "delete" && chord.Modifiers.Contains("ctrl") && chord.Modifiers.Contains("alt"))
            {
                script.Append("Invoke-CimMethod -InputObject $kb -MethodName TypeCtrlAltDel | Out-Null\n");
                continue;
            }
            foreach (var m in chord.Modifiers)
            {
                script.Append($"Invoke-CimMethod -InputObject $kb -MethodName PressKey -Arguments @{{ keyCode = {VirtualKey(m)} }} | Out-Null\n");
            }
            script.Append($"Invoke-CimMethod -InputObject $kb -MethodName TypeKey -Arguments @{{ keyCode = {VirtualKey(chord.Key)} }} | Out-Null\n");
            foreach (var m in Enumerable.Reverse(chord.Modifiers))
            {
                script.Append($"Invoke-CimMethod -InputObject $kb -MethodName ReleaseKey -Arguments @{{ keyCode = {VirtualKey(m)} }} | Out-Null\n");
            }
        }
        await ScriptAsync(script.ToString(), Short, ct);
    }

    /// <summary>Windows' virtual-key code for a key.</summary>
    internal static int VirtualKey(string key) => key switch
    {
        "backspace" => 0x08, "tab" => 0x09, "enter" => 0x0d, "shift" => 0x10, "ctrl" => 0x11, "alt" => 0x12, "capslock" => 0x14, "esc" => 0x1b,
        "space" => 0x20, "pageup" => 0x21, "pagedown" => 0x22, "end" => 0x23, "home" => 0x24, "left" => 0x25, "up" => 0x26, "right" => 0x27,
        "down" => 0x28, "insert" => 0x2d, "delete" => 0x2e, "win" => 0x5b,
        [var c] when c is >= 'a' and <= 'z' => char.ToUpperInvariant(c),
        [var d] when d is >= '0' and <= '9' => d,
        ['f', .. var n] when int.TryParse(n, out var f) && f is >= 1 and <= 12 => 0x6f + f,
        _ => throw new ToolError($"There is no key {key}."),
    };

    public override async Task ClickAsync(VmInfo vm, GuestLogin? login, int x, int y, string button, bool twice, CancellationToken ct)
    {
        var index = button switch { "right" => 2, "middle" => 3, _ => 1 };
        var click = $"Invoke-CimMethod -InputObject $mouse -MethodName ClickButton -Arguments @{{ buttonIndex = {index} }} | Out-Null\n";
        await ScriptAsync(Machine(vm.Id) + "$mouse = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_SyntheticMouse\n" +
            $"Invoke-CimMethod -InputObject $mouse -MethodName SetAbsolutePosition -Arguments @{{ horizontalPosition = {x}; verticalPosition = {y} }} | Out-Null\n" +
            click + (twice ? click : ""), Short, ct);
    }
}
