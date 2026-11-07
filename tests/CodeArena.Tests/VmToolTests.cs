using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// The VM tools against stand-ins for VBoxManage, vmrun, virsh and secret-tool on PATH (no hypervisor here): each logs
/// how it was called, and runs a "guest" command on this machine, so a command's output and exit code are real.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class VmToolTests : IDisposable
{
    private const string Password = "s3cret-Pa55";
    private readonly FakeGateway _gateway = new();

    public void Dispose() => _gateway.Dispose();

    private static void Script(string dir, string name, string body)
    {
        var file = Path.Combine(dir, name);
        File.WriteAllText(file, "#!/bin/sh\n" + body.Replace("@PASSWORD@", Password, StringComparison.Ordinal));
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>A folder of stand-ins, PATH for the run; its screen.png a black 4×2 screen.</summary>
    private static string Bin(Harness h)
    {
        var bin = Directory.CreateDirectory(Path.Combine(h.Root, "bin")).FullName;
        h.Env["PATH"] = bin;
        h.Env["HOME"] = h.Root;
        File.WriteAllBytes(Path.Combine(bin, "screen.png"), VmPictures.Png(4, 2, new byte[4 * 2 * 3]));
        return bin;
    }

    private static void VBoxManage(string bin) => Script(bin, "VBoxManage", """
        d="$(dirname "$0")"
        printf '%s\n' "$*" >> "$d/vbox.log"
        cmd="$1"; shift
        case "$cmd" in
        list)
          if [ "$1" = vms ]; then
            echo '"test-vm" {11111111-2222-3333-4444-555555555555}'
            echo '"Win Box" {66666666-7777-8888-9999-000000000000}'
          elif [ -f "$d/running" ]; then
            echo '"test-vm" {11111111-2222-3333-4444-555555555555}'
          fi;;
        showvminfo)
          if [ -f "$d/running" ]; then echo 'VMState="running"'; else echo 'VMState="poweroff"'; fi
          echo 'ostype="Ubuntu (64-bit)"'
          echo 'GuestAdditionsVersion="7.0.20 r163906"'
          echo 'GuestAdditionsRunLevel=2';;
        startvm) touch "$d/running";;
        controlvm)
          case "$2" in
          poweroff|acpipowerbutton) rm -f "$d/running";;
          screenshotpng) cp "$d/screen.png" "$3";;
          esac;;
        snapshot)
          case "$2" in
          list) echo 'SnapshotName="clean"'; echo 'SnapshotName-1="later"'; echo 'CurrentSnapshotName="later"';;
          restore) if [ -f "$d/running" ]; then echo "VBoxManage: error: The machine is running" >&2; exit 1; fi;;
          esac;;
        guestcontrol)
          sub="$2"; shift 2
          pw=""
          while [ $# -gt 0 ]; do
            case "$1" in
            --passwordfile) pw="$(cat "$2")"; ls -l "$2" | cut -c1-10 > "$d/password-mode"; echo "$2" > "$d/password-file"; shift;;
            --username|--timeout|--exe) shift;;
            --) shift; break;;
            --*) ;;
            *) break;;
            esac
            shift
          done
          if [ "$pw" != "@PASSWORD@" ]; then echo "VBoxManage: error: The specified user was not able to logon on guest" >&2; exit 1; fi
          case "$sub" in
          run) exec "$@";;
          mkdir) mkdir -p "$1";;
          copyto|copyfrom) cp -R "$1" "$2";;
          esac;;
        esac
        """);

    /// <summary>secret-tool with a keyring of one entry, kept in files beside it.</summary>
    private static void SecretTool(string bin, string? user = null, string? password = null)
    {
        Script(bin, "secret-tool", """
            d="$(dirname "$0")"
            printf '%s\n' "$*" >> "$d/keyring.log"
            case "$1" in
            lookup) [ -f "$d/kept-password" ] && cat "$d/kept-password" || exit 1;;
            search) [ -f "$d/kept-user" ] || exit 1; echo "[/org/freedesktop/secrets/collection/login/1]"; echo "secret = $(cat "$d/kept-password")"; echo "attribute.user = $(cat "$d/kept-user")";;
            store) cat > "$d/kept-password"; prev=""; for a in "$@"; do [ "$prev" = user ] && printf '%s' "$a" > "$d/kept-user"; prev="$a"; done;;
            clear) rm -f "$d/kept-password" "$d/kept-user";;
            esac
            """);
        if (user is not null)
        {
            File.WriteAllText(Path.Combine(bin, "kept-user"), user);
            File.WriteAllText(Path.Combine(bin, "kept-password"), password);
        }
    }

    /// <summary>The model's calls in order, then a last word.</summary>
    private void Calls(params (string Name, string Args)[] calls)
    {
        // By the results the conversation has so far: each request is answered as its place says, whatever else asks.
        _gateway.Answer = req => FakeGateway.History(req).OfType<JsonObject>().Count(m => m["role"]?.ToString() == "tool") is var n && n < calls.Length
            ? Reply.Call(calls[n]) : Reply.Say("Done.");
    }

    private List<string> Results() =>
        [.. FakeGateway.History(_gateway.Requests.Last()).OfType<JsonObject>().Where(m => m["role"]?.ToString() == "tool").Select(m => m["content"]!.ToString())];

    /// <summary>Nothing the model, the terminal, the log or the session saw holds the password.</summary>
    private void NowhereIsThePassword(Harness h)
    {
        Assert.DoesNotContain(Password, string.Join("\n", _gateway.Requests.Select(Json.Line)), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, h.Out + h.Err, StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(h.Paths.DataDir, "*", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(h.Work, "*", SearchOption.AllDirectories)))
        {
            Assert.DoesNotContain(Password, File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Hypervisors_are_found_on_PATH_in_their_usual_folders_and_by_a_variable()
    {
        var env = new Dictionary<string, string?> { ["PATH"] = "/fake/bin" };
        var files = new HashSet<string> { "/fake/bin/VBoxManage", "/usr/lib/vmware/bin/vmrun", "/fake/bin/virsh" };
        var found = Hypervisors.Find(k => env.GetValueOrDefault(k), files.Contains);
        Assert.Equal(["virtualbox", "vmware", "libvirt"], found.Select(f => f.Kind));
        Assert.Equal("/usr/lib/vmware/bin/vmrun", found[1].Program);
        // Named by a variable: that program, and none when it is not there.
        env["CODE_ARENA_VBOXMANAGE"] = "/opt/vbox/VBoxManage";
        Assert.DoesNotContain(Hypervisors.Find(k => env.GetValueOrDefault(k), files.Contains), f => f.Kind == "virtualbox");
        files.Add("/opt/vbox/VBoxManage");
        Assert.Equal("/opt/vbox/VBoxManage", Hypervisors.Find(k => env.GetValueOrDefault(k), files.Contains)[0].Program);
        Assert.Empty(Hypervisors.Find(_ => null, _ => false));
    }

    [Fact]
    public async Task Changing_a_VM_asks_as_a_command_does_and_always_covers_that_tool_on_that_VM_only()
    {
        var tools = new VirtualMachines([], new GuestLogins(new NoKeyring(), new Ui(TextReader.Null, TextWriter.Null, TextWriter.Null, false, false)), () => false).Tools();
        ToolDef T(string name) => tools.Single(t => t.Name == name);
        JsonObject On(string vm) => new() { ["vm"] = vm, ["command"] = "make test" };
        var output = new StringWriter();
        var p = new Permissions(new Ui(new StringReader("a\nn\n"), output, output, false, true), Mode.Ask);
        // Listing and looking change nothing: never asked.
        Assert.Null(await p.CheckAsync(T("vm_list"), [], default));
        Assert.Null(await p.CheckAsync(T("vm_screenshot"), On("test-vm"), default));
        Assert.Null(await p.CheckAsync(T("vm_run"), On("test-vm"), default));
        Assert.Contains("[a]lways for vm_run on test-vm", output.ToString(), StringComparison.Ordinal);
        Assert.Null(await p.CheckAsync(T("vm_run"), On("test-vm"), default));
        Assert.Contains("declined", await p.CheckAsync(T("vm_run"), On("prod-vm"), default));
        // Auto-edit asks too; yolo does not; plan mode refuses.
        Assert.Equal(ToolKind.Machine, T("vm_power").Kind);
        p.Mode = Mode.Yolo;
        Assert.Null(await p.CheckAsync(T("vm_test"), On("x"), default));
        p.Mode = Mode.Plan;
        Assert.Contains("Plan mode is read-only", await p.CheckAsync(T("vm_snapshot"), On("x"), default));
        Assert.Null(await p.CheckAsync(T("vm_list"), [], default));
        // A kept rule names VMs: vm_run(test-*) runs unasked in the test VMs, and only there.
        using var h = new Harness(_gateway);
        var ruled = new Permissions(new Ui(new StringReader("n\n"), output, output, false, true), Mode.Ask) { Workspace = new Workspace(h.Work) };
        ruled.Rules.Allow.Add("vm_run(test-*)");
        Assert.Null(await ruled.CheckAsync(T("vm_run"), On("test-vm"), default));
        Assert.Contains("declined", await ruled.CheckAsync(T("vm_run"), On("prod-vm"), default));
    }

    [Fact]
    public async Task A_VirtualBox_VM_is_started_and_a_command_runs_in_it_with_the_keyring_sign_in_kept_out_of_sight()
    {
        using var h = new Harness(_gateway);
        var bin = Bin(h);
        VBoxManage(bin);
        SecretTool(bin, "tester", Password);
        Calls(("vm_list", "{}"), ("vm_power", """{"vm":"test-vm","action":"start"}"""), ("vm_list", """{"vm":"test-vm"}"""),
            ("vm_run", """{"vm":"Test-VM","command":"echo hello from the guest; echo oops >&2; exit 3"}"""));
        Assert.Equal(0, await h.Run("", "-p", "run it in the VM", "--mode", "yolo"));
        var results = Results();
        Assert.Contains("- test-vm: off", results[0], StringComparison.Ordinal);
        Assert.Contains("- Win Box: off", results[0], StringComparison.Ordinal);
        Assert.Contains("Started test-vm headless", results[1], StringComparison.Ordinal);
        Assert.Contains("snapshots: clean, later", results[2], StringComparison.Ordinal);
        Assert.Contains("Guest Additions: 7.0.20", results[2], StringComparison.Ordinal);
        Assert.StartsWith("Exit code 3", results[3], StringComparison.Ordinal);
        Assert.Contains("hello from the guest", results[3], StringComparison.Ordinal);
        Assert.Contains("oops", results[3], StringComparison.Ordinal);
        var log = File.ReadAllText(Path.Combine(bin, "vbox.log"));
        Assert.Contains("startvm 11111111-2222-3333-4444-555555555555 --type headless", log, StringComparison.Ordinal);
        Assert.Contains("--username tester --passwordfile", log, StringComparison.Ordinal);
        // The password went in a file of the person's own (0600), deleted after; never on a command line.
        Assert.DoesNotContain(Password, log, StringComparison.Ordinal);
        Assert.Equal("-rw-------", File.ReadAllText(Path.Combine(bin, "password-mode")).Trim());
        Assert.False(File.Exists(File.ReadAllText(Path.Combine(bin, "password-file")).Trim()));
        Assert.Contains("lookup service code-arena-vm vm virtualbox:test-vm", File.ReadAllText(Path.Combine(bin, "keyring.log")), StringComparison.Ordinal);
        NowhereIsThePassword(h);
    }

    [Fact]
    public async Task Without_a_keyring_the_terminal_asks_for_the_sign_in_unseen_and_a_run_that_cannot_ask_says_how_to_keep_one()
    {
        using var h = new Harness(_gateway);
        var bin = Bin(h);
        VBoxManage(bin);
        File.WriteAllText(Path.Combine(bin, "running"), "");
        Calls(("vm_run", """{"vm":"test-vm","command":"echo $((6 * 7))"}"""));
        Assert.Equal(0, await h.Run("", "-p", "run it", "--mode", "yolo"));
        Assert.Contains("code-arena vm login \"test-vm\"", Results()[0], StringComparison.Ordinal);

        h.Secret = Password;
        Calls(("vm_run", """{"vm":"test-vm","command":"echo $((6 * 7))"}"""), ("vm_run", """{"vm":"test-vm","command":"echo again"}"""));
        Assert.Equal(0, await h.Run("go\ntester\n/exit\n", "chat", "--mode", "yolo"));
        var results = Results();
        Assert.StartsWith("Exit code 0", results[0], StringComparison.Ordinal);
        Assert.Contains("42", results[0], StringComparison.Ordinal);
        // Asked once for the session.
        Assert.Contains("again", results[1], StringComparison.Ordinal);
        Assert.Equal(2, (h.Out + h.Err).Split("User in test-vm (VirtualBox)").Length);
        NowhereIsThePassword(h);
    }

    [Fact]
    public async Task The_prompt_s_own_editor_reads_the_password_as_dots_and_keeps_it_out_of_the_history()
    {
        using var h = new Harness(_gateway);
        var bin = Bin(h);
        VBoxManage(bin);
        File.WriteAllText(Path.Combine(bin, "running"), "");
        Calls(("vm_run", """{"vm":"test-vm","command":"echo typed in"}"""));
        h.Keys = new ScriptedKeys().Type($"go\ntester\n{Password}\n/exit\n");
        Assert.Equal(0, await h.Run("", "chat", "--mode", "yolo"));
        Assert.StartsWith("Exit code 0", Results()[0], StringComparison.Ordinal);
        Assert.Contains(new string('•', Password.Length), h.Out, StringComparison.Ordinal);
        Assert.Equal(["go"], new InputHistory(h.Paths.HistoryDir, h.Work).Entries());
        NowhereIsThePassword(h);
    }

    [Fact]
    public async Task A_sign_in_the_guest_refuses_is_asked_for_again_and_kept_when_the_person_says()
    {
        using var h = new Harness(_gateway);
        var bin = Bin(h);
        VBoxManage(bin);
        SecretTool(bin, "tester", "an-old-password");
        File.WriteAllText(Path.Combine(bin, "running"), "");
        h.Secret = Password;
        Calls(("vm_run", """{"vm":"test-vm","command":"echo one"}"""), ("vm_run", """{"vm":"test-vm","command":"echo two"}"""));
        // Enter keeps the user the keyring had; y keeps the new password in it.
        Assert.Equal(0, await h.Run("go\n\ny\n/exit\n", "chat", "--mode", "yolo"));
        var results = Results();
        Assert.Contains("not able to logon", results[0], StringComparison.Ordinal);
        Assert.Contains("the guest refused tester's sign-in", results[0], StringComparison.Ordinal);
        Assert.StartsWith("Exit code 0", results[1], StringComparison.Ordinal);
        Assert.Equal(Password, File.ReadAllText(Path.Combine(bin, "kept-password")));
        Assert.Equal("tester", File.ReadAllText(Path.Combine(bin, "kept-user")));
        Assert.Contains("store --label Code Arena: virtualbox:test-vm service code-arena-vm vm virtualbox:test-vm user tester", File.ReadAllText(Path.Combine(bin, "keyring.log")), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, File.ReadAllText(Path.Combine(bin, "keyring.log")), StringComparison.Ordinal);
        Assert.DoesNotContain("an-old-password", string.Join("\n", _gateway.Requests.Select(Json.Line)), StringComparison.Ordinal);
        NowhereIsThePassword(h);
    }

    [Fact]
    public async Task A_test_in_a_VM_restores_runs_brings_back_the_logs_and_the_screen_and_puts_the_VM_back()
    {
        using var h = new Harness(_gateway);
        var bin = Bin(h);
        VBoxManage(bin);
        SecretTool(bin, "tester", Password);
        var guest = Path.Combine(h.Root, "guest");
        h.Write("dist/app.sh", $"echo started > {guest}/app.log; echo app ok\n");
        var args = new JsonObject
        {
            ["vm"] = "test-vm",
            ["snapshot"] = "clean",
            ["copy"] = new JsonArray(new JsonObject { ["from"] = "dist/app.sh", ["to"] = $"{guest}/app/app.sh" }),
            ["command"] = $"sh {guest}/app/app.sh",
            ["logs"] = new JsonArray($"{guest}/app.log", $"{guest}/missing.log"),
        };
        Calls(("vm_test", args.ToJsonString()), ("vm_test", args.DeepClone().AsObject().Also(a => a["command"] = "echo broken; exit 1").ToJsonString()));
        Assert.Equal(0, await h.Run("", "-p", "test the build in the VM", "--mode", "yolo"));
        var results = Results();
        Assert.StartsWith("The test in test-vm passed.", results[0], StringComparison.Ordinal);
        Assert.Contains("app ok", results[0], StringComparison.Ordinal);
        Assert.Contains("could not bring back", results[0], StringComparison.Ordinal);
        Assert.Contains("restored clean again", results[0], StringComparison.Ordinal);
        var runs = Directory.GetDirectories(Path.Combine(h.Work, ".code-arena", "vm")).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(2, runs.Count);
        var run = runs[0];
        Assert.Equal("started\n", File.ReadAllText(Path.Combine(run, "app.log")));
        Assert.True(VmPictures.IsPng(File.ReadAllBytes(Path.Combine(run, "screen.png"))));
        Assert.Contains("*", File.ReadAllText(Path.Combine(h.Work, ".code-arena", "vm", ".gitignore")), StringComparison.Ordinal);
        Assert.True(results[0].Contains("This model cannot see pictures; described: The screen is 4×2", StringComparison.Ordinal), results[0]);
        // The steps in their order, and the VM left as the snapshot had it.
        var log = File.ReadAllLines(Path.Combine(bin, "vbox.log")).Where(l => !l.StartsWith("list", StringComparison.Ordinal) && !l.StartsWith("showvminfo", StringComparison.Ordinal)).ToList();
        string[] order = ["snapshot 11111111-2222-3333-4444-555555555555 restore clean", "startvm", "guestcontrol 11111111-2222-3333-4444-555555555555 mkdir", "copyto", "guestcontrol 11111111-2222-3333-4444-555555555555 run",
            "copyfrom", "screenshotpng", "controlvm 11111111-2222-3333-4444-555555555555 poweroff", "snapshot 11111111-2222-3333-4444-555555555555 restore clean"];
        var at = 0;
        foreach (var step in order)
        {
            at = log.FindIndex(at, l => l.Contains(step, StringComparison.Ordinal));
            Assert.True(at >= 0, $"{step} is not where it should be in:\n{string.Join('\n', log)}");
            at++;
        }
        Assert.False(File.Exists(Path.Combine(bin, "running")));
        // A failing test says so, and the VM is put back all the same.
        Assert.StartsWith("The test in test-vm failed.", results[1], StringComparison.Ordinal);
        Assert.Contains("exit code 1", results[1], StringComparison.Ordinal);
        Assert.Contains("restored clean again", results[1], StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(bin, "running")));
        NowhereIsThePassword(h);
    }

    [Fact]
    public async Task VMware_runs_a_command_through_a_file_in_the_guest_and_its_output_holds_no_password()
    {
        using var h = new Harness(_gateway);
        var bin = Bin(h);
        var vmx = Path.Combine(h.Root, "vms", "web.vmx");
        Directory.CreateDirectory(Path.GetDirectoryName(vmx)!);
        File.WriteAllText(vmx, "displayName = \"web\"\nguestOS = \"ubuntu-64\"\n");
        Directory.CreateDirectory(Path.Combine(h.Root, ".vmware"));
        File.WriteAllText(Path.Combine(h.Root, ".vmware", "inventory.vmls"), $".encoding = \"UTF-8\"\nvmlist1.config = \"{vmx}\"\nvmlist1.DisplayName = \"Web server\"\nindex.count = \"1\"\n");
        File.WriteAllText(Path.Combine(bin, "vmx-path"), vmx);
        Script(bin, "vmrun", """
            d="$(dirname "$0")"
            printf '%s\n' "$*" >> "$d/vmrun.log"
            echo "[AppLoader] Use shipped Linux kernel AIO access library."
            echo 'An up-to-date "libaio" or "libaio1" package from your system is preferred.'
            pw=""
            while [ $# -gt 0 ]; do
              case "$1" in
              -T|-gu) shift;;
              -gp) pw="$2"; shift;;
              *) break;;
              esac
              shift
            done
            cmd="$1"; shift
            case "$cmd" in
            list) if [ -f "$d/running" ]; then echo "Total running VMs: 1"; cat "$d/vmx-path"; else echo "Total running VMs: 0"; fi;;
            start) touch "$d/running";;
            stop) rm -f "$d/running";;
            listSnapshots) echo "Total snapshots: 1"; echo "clean";;
            checkToolsState) echo "The tools are running";;
            runProgramInGuest)
              [ "$pw" = "@PASSWORD@" ] || { echo "Error: Invalid user name or password for the guest OS"; exit 255; }
              shift; "$@"; code=$?
              if [ $code -ne 0 ]; then echo "Guest program exited with non-zero exit code: $code"; exit 255; fi;;
            CopyFileFromGuestToHost|CopyFileFromHostToGuest) cp "$2" "$3";;
            deleteFileInGuest) rm -f "$2";;
            directoryExistsInGuest) echo "The directory does not exist.";;
            captureScreen) cp "$d/screen.png" "$2";;
            esac
            """);
        SecretTool(bin, "admin", Password);
        Calls(("vm_list", "{}"), ("vm_power", """{"vm":"Web server","action":"start"}"""),
            ("vm_run", $$"""{"vm":"Web server","command":"echo the password is $(cat {{bin}}/kept-password); exit 2"}"""));
        Assert.Equal(0, await h.Run("", "-p", "run it", "--mode", "yolo"));
        var results = Results();
        Assert.Contains("VMware Workstation", results[0], StringComparison.Ordinal);
        Assert.Contains("- Web server: off", results[0], StringComparison.Ordinal);
        Assert.StartsWith("Exit code 2", results[2], StringComparison.Ordinal);
        Assert.Contains("the password is ••••", results[2], StringComparison.Ordinal);
        Assert.DoesNotContain("AppLoader", string.Join("\n", results), StringComparison.Ordinal);
        var log = File.ReadAllText(Path.Combine(bin, "vmrun.log"));
        Assert.Contains($"-T ws start {vmx} nogui", log, StringComparison.Ordinal);
        Assert.Contains("CopyFileFromGuestToHost", log, StringComparison.Ordinal);
        Assert.Contains("deleteFileInGuest", log, StringComparison.Ordinal);
        // vmrun takes the password as -gp only: it is on its command line, and nowhere else.
        Assert.Contains($"-gu admin -gp {Password} runProgramInGuest", log, StringComparison.Ordinal);
        File.Delete(Path.Combine(bin, "vmrun.log"));
        NowhereIsThePassword(h);
    }

    [Fact]
    public async Task Libvirt_runs_through_the_guest_agent_types_on_a_US_keyboard_clicks_by_the_screen_and_describes_it()
    {
        using var h = new Harness(_gateway);
        var bin = Bin(h);
        var ppm = Encoding.ASCII.GetBytes("P6\n# QEMU\n10 5\n255\n").Concat(new byte[10 * 5 * 3]).ToArray();
        File.WriteAllBytes(Path.Combine(bin, "screen.ppm"), ppm);
        Script(bin, "virsh", """
            d="$(dirname "$0")"
            printf '%s\n' "$*" >> "$d/virsh.log"
            shift 2
            cmd="$1"; shift
            case "$cmd" in
            list) echo "lab"; [ "$1" = "--all" ] && echo "spare"; true;;
            domstate) echo running;;
            dumpxml) echo "<domain><name>$1</name></domain>";;
            qemu-agent-command)
              case "$2" in
              *guest-exec-status*) echo '{"return":{"exited":true,"exitcode":4,"out-data":"aGkK"}}';;
              *guest-exec*) echo '{"return":{"pid":42}}';;
              *guest-get-osinfo*) echo '{"return":{"id":"ubuntu"}}';;
              *) echo '{"return":{}}';;
              esac;;
            screenshot) cp "$d/screen.ppm" "$3";;
            esac
            """);
        Calls(("vm_run", """{"vm":"lab","command":"echo hi; exit 4"}"""), ("vm_input", """{"vm":"lab","text":"Hi!","keys":["ctrl+c","Enter"],"click":{"x":9,"y":4}}"""),
            ("vm_screenshot", """{"vm":"lab"}"""), ("vm_input", """{"vm":"lab","keys":["hyper+q"]}"""));
        Assert.Equal(0, await h.Run("", "-p", "look", "--mode", "yolo"));
        var results = Results();
        Assert.StartsWith("Exit code 4", results[0], StringComparison.Ordinal);
        Assert.Contains("hi", results[0], StringComparison.Ordinal);
        Assert.Contains("clicked left at 9,4, then typed 3 characters, then pressed ctrl+c enter", results[1], StringComparison.Ordinal);
        Assert.Contains("The screen is 10×5", results[2], StringComparison.Ordinal);
        Assert.Contains("blank", results[2], StringComparison.Ordinal);
        Assert.Contains("There is no key hyper+q", results[3], StringComparison.Ordinal);
        var log = File.ReadAllText(Path.Combine(bin, "virsh.log"));
        Assert.Contains("-c qemu:///system qemu-agent-command lab", log, StringComparison.Ordinal);
        Assert.Contains("send-key lab --codeset linux KEY_LEFTSHIFT KEY_H\n", log, StringComparison.Ordinal);
        Assert.Contains("send-key lab --codeset linux KEY_I\n", log, StringComparison.Ordinal);
        Assert.Contains("send-key lab --codeset linux KEY_LEFTSHIFT KEY_1\n", log, StringComparison.Ordinal);
        Assert.Contains("send-key lab --codeset linux KEY_LEFTCTRL KEY_C\n", log, StringComparison.Ordinal);
        Assert.Contains("""{"type":"abs","data":{"axis":"x","value":32767}},{"type":"abs","data":{"axis":"y","value":32767}}""", log, StringComparison.Ordinal);
        Assert.Contains("""{"type":"btn","data":{"down":true,"button":"left"}}""", log, StringComparison.Ordinal);
        var saved = Directory.GetFiles(Path.Combine(h.Work, ".code-arena", "vm"), "lab-*.png").Single();
        Assert.True(VmPictures.IsPng(File.ReadAllBytes(saved)));
    }

    [Fact]
    public async Task A_model_that_sees_gets_the_VM_screen_as_a_picture()
    {
        _gateway.Vision = true;
        using var h = new Harness(_gateway);
        var bin = Bin(h);
        VBoxManage(bin);
        File.WriteAllText(Path.Combine(bin, "running"), "");
        Calls(("vm_screenshot", """{"vm":"test-vm"}"""));
        Assert.Equal(0, await h.Run("", "-p", "look", "--mode", "ask"));
        var picture = FakeGateway.History(_gateway.Requests.Last()).OfType<JsonObject>().Last(m => m["role"]?.ToString() == "user");
        Assert.StartsWith("data:image/png;base64,", picture["content"]![1]!["image_url"]!["url"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("Took test-vm's screen (4×2, saved as .code-arena/vm/test-vm-", Results()[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_IDE_asks_for_the_sign_in_in_a_dialog_and_the_password_never_comes_back_to_the_page()
    {
        using var h = new Harness(_gateway);
        var bin = Bin(h);
        VBoxManage(bin);
        File.WriteAllText(Path.Combine(bin, "running"), "");
        Calls(("vm_run", """{"vm":"test-vm","command":"echo from the IDE"}"""));
        await using var web = await WebRun.StartAsync(h, "--mode", "yolo");
        using var page = await web.SendAsync("run it");
        var asked = await page.UntilAsync("vm_login");
        Assert.Equal("test-vm", asked["vm"]!.GetValue<string>());
        Assert.Equal("VirtualBox", asked["hypervisor"]!.GetValue<string>());
        Assert.False(asked["canSave"]!.GetValue<bool>());
        var id = asked["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/vm-login", new JsonObject { ["id"] = id, ["user"] = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/vm-login", new JsonObject { ["id"] = id, ["user"] = "tester", ["password"] = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/vm-login", new JsonObject { ["id"] = id, ["cancel"] = true })).StatusCode);
        var events = await page.RestAsync();
        Assert.Contains(events, e => e["type"]!.GetValue<string>() == "vm_login_done" && e["id"]!.GetValue<string>() == id);
        Assert.Contains(events, e => e["type"]!.GetValue<string>() == "tool_result" && e.ToJsonString().Contains("from the IDE", StringComparison.Ordinal));
        Assert.DoesNotContain(Password, string.Join("\n", page.Seen.Select(e => e.ToJsonString())), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, string.Join("\n", _gateway.Requests.Select(Json.Line)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VirtualBox_makes_a_VM_with_a_disk_and_the_ISO_clones_one_that_is_off_and_deletes_it_with_its_disks()
    {
        using var h = new Harness(_gateway);
        var bin = Bin(h);
        var iso = h.Write("isos/alpine.iso", "an ISO");
        Script(bin, "VBoxManage", """
            d="$(dirname "$0")"
            printf '%s\n' "$*" >> "$d/vbox.log"
            case "$1" in
            list)
              [ "$2" = vms ] && { echo '"base" {11111111-2222-3333-4444-555555555555}'; for f in "$d"/vm-*; do [ -f "$f" ] && cat "$f"; done; }; true;;
            showvminfo) echo 'VMState="poweroff"';;
            createvm)
              echo "Virtual machine '$3' is created and registered."
              echo "UUID: 99999999-0000-0000-0000-000000000001"
              echo "Settings file: '$d/VirtualBox VMs/$3/$3.vbox'"
              echo "\"$3\" {99999999-0000-0000-0000-000000000001}" > "$d/vm-$3";;
            createmedium) [ -f "$d/disk-fails" ] && { echo "VBoxManage: error: no room" >&2; exit 1; }; true;;
            snapshot) echo 'SnapshotName="clean"';;
            clonevm) echo "Machine has been successfully cloned as \"$4\""; echo "\"$4\" {99999999-0000-0000-0000-000000000002}" > "$d/vm-$4";;
            unregistervm) rm -f "$d"/vm-*;;
            esac
            """);
        Calls(("vm_create", $$"""{"action":"create","name":"alpine-test","iso":"isos/alpine.iso","memory":1024,"cpus":1,"disk":8}"""),
            ("vm_create", """{"action":"create","name":"base"}"""),
            ("vm_create", """{"action":"clone","vm":"base","name":"base copy","snapshot":"clean","linked":true}"""),
            ("vm_create", """{"action":"clone","vm":"base","name":"x","linked":true}"""),
            ("vm_create", """{"action":"create","name":"../evil"}"""),
            ("vm_delete", """{"vm":"alpine-test"}"""));
        Assert.Equal(0, await h.Run("go\ny\n/exit\n", "chat", "--mode", "yolo"));
        var results = Results();
        Assert.StartsWith("Made alpine-test on VirtualBox (1,024 MB, 1 CPU, 8 GB of disk, alpine.iso in its drive", results[0], StringComparison.Ordinal);
        Assert.Contains("VirtualBox has a VM named base already", results[1], StringComparison.Ordinal);
        Assert.Contains("Cloned base as base copy on VirtualBox, from clean, linked to it", results[2], StringComparison.Ordinal);
        Assert.Contains("A linked clone shares a snapshot's disk", results[3], StringComparison.Ordinal);
        Assert.Contains("name is up to 64 letters", results[4], StringComparison.Ordinal);
        Assert.StartsWith("Deleted alpine-test from VirtualBox", results[5], StringComparison.Ordinal);
        var log = File.ReadAllText(Path.Combine(bin, "vbox.log"));
        Assert.Contains("createvm --name alpine-test --ostype Linux_64 --register", log, StringComparison.Ordinal);
        Assert.Contains("modifyvm 99999999-0000-0000-0000-000000000001 --memory 1024 --cpus 1 --nic1 nat", log, StringComparison.Ordinal);
        Assert.Contains($"createmedium disk --filename {bin}/VirtualBox VMs/alpine-test/alpine-test.vdi --size 8192 --format VDI", log, StringComparison.Ordinal);
        Assert.Contains($"storageattach 99999999-0000-0000-0000-000000000001 --storagectl SATA --port 1 --device 0 --type dvddrive --medium {iso}", log, StringComparison.Ordinal);
        Assert.Contains("clonevm 11111111-2222-3333-4444-555555555555 --name base copy --register --snapshot clean --options link", log, StringComparison.Ordinal);
        Assert.Contains("unregistervm 99999999-0000-0000-0000-000000000001 --delete", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_VM_half_made_is_taken_away_again_and_deleting_one_asks_the_person_in_every_mode()
    {
        using var h = new Harness(_gateway);
        var bin = Bin(h);
        VBoxManage(bin);
        File.WriteAllText(Path.Combine(bin, "running"), "");
        Calls(("vm_delete", """{"vm":"test-vm"}"""));
        // yolo asks too: -p cannot ask, so nothing is deleted.
        Assert.Equal(0, await h.Run("", "-p", "delete it", "--mode", "yolo"));
        Assert.Contains("vm_delete needs the person's approval, and this run cannot ask", Results()[0], StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(bin, "vbox.log")));
        // Asked and allowed, a running VM is refused all the same.
        Assert.Equal(0, await h.Run("go\ny\n/exit\n", "chat", "--mode", "yolo"));
        Assert.Contains("test-vm is running: stop it first", Results()[0], StringComparison.Ordinal);

        var tools = new VirtualMachines([], new GuestLogins(new NoKeyring(), new Ui(TextReader.Null, TextWriter.Null, TextWriter.Null, false, false)), () => false).Tools();
        Assert.True(tools.Single(t => t.Name == "vm_delete").AlwaysAsks);
        Assert.Equal(ToolKind.Machine, tools.Single(t => t.Name == "vm_create").Kind);

        // A disk that cannot be made: the VM registered for it is unregistered again.
        File.Delete(Path.Combine(bin, "vbox.log"));
        Script(bin, "VBoxManage", """
            d="$(dirname "$0")"
            printf '%s\n' "$*" >> "$d/vbox.log"
            case "$1" in
            list) true;;
            createvm) echo "UUID: 99999999-0000-0000-0000-000000000001"; echo "Settings file: '$d/vms/$3/$3.vbox'";;
            createmedium) echo "VBoxManage: error: Could not create the medium storage unit: no room" >&2; exit 1;;
            esac
            """);
        Calls(("vm_create", """{"action":"create","name":"full"}"""));
        Assert.Equal(0, await h.Run("", "-p", "make it", "--mode", "yolo"));
        Assert.Contains("no room", Results()[0], StringComparison.Ordinal);
        Assert.EndsWith("unregistervm 99999999-0000-0000-0000-000000000001 --delete\n", File.ReadAllText(Path.Combine(bin, "vbox.log")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_vm_command_makes_starts_stops_and_deletes_a_VM_without_a_model_and_deleting_asks_first()
    {
        using var h = new Harness(_gateway);
        var bin = Bin(h);
        VBoxManage(bin);
        File.AppendAllText(Path.Combine(bin, "VBoxManage"), """

            case "$cmd" in
            createvm) echo "UUID: 99999999-0000-0000-0000-000000000001"; echo "Settings file: '$d/vms/$2/$2.vbox'";;
            unregistervm) echo deleted >> "$d/deleted";;
            esac
            """);
        Assert.Equal(0, await h.Run("", "vm", "create", "tiny", "--memory", "256", "--disk", "1", "--os", "other"));
        Assert.Contains("Made tiny on VirtualBox (256 MB, 2 CPUs, 1 GB of disk", h.Out, StringComparison.Ordinal);
        Assert.Contains("It is off. Its disk is empty and nothing is in its drive: make it again with an iso to install a system from, or clone a VM that has one.\n", h.Out, StringComparison.Ordinal);
        Assert.Contains("createvm --name tiny --ostype Other_64 --register", File.ReadAllText(Path.Combine(bin, "vbox.log")), StringComparison.Ordinal);
        Assert.Equal(0, await h.Run("", "vm", "start", "test-vm"));
        Assert.Equal(0, await h.Run("", "vm", "stop", "test-vm", "--force"));
        Assert.Contains("controlvm 11111111-2222-3333-4444-555555555555 poweroff", File.ReadAllText(Path.Combine(bin, "vbox.log")), StringComparison.Ordinal);
        Assert.Equal(1, await h.Run("n\n", "vm", "delete", "test-vm"));
        Assert.Contains("Delete test-vm with its disks and snapshots, for good?", h.Out + h.Err, StringComparison.Ordinal);
        Assert.Contains("nothing deleted", h.Err, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(bin, "deleted")));
        Assert.Equal(0, await h.Run("y\n", "vm", "delete", "test-vm"));
        Assert.True(File.Exists(Path.Combine(bin, "deleted")));
        Assert.Equal(1, await h.Run("", "vm", "create", "x", "iso"));
        Assert.Contains("iso is not an option", h.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VMware_that_cannot_start_a_VM_for_want_of_its_kernel_module_says_so_and_how_to_load_it()
    {
        using var h = new Harness(_gateway);
        var bin = Bin(h);
        // As this machine's Workstation answers when vmmon is not loaded (Secure Boot refuses the unsigned module).
        Script(bin, "vmrun", """
            echo "[AppLoader] Use shipped Linux kernel AIO access library."
            shift 2
            case "$1" in
            list) echo "Total running VMs: 0";;
            start) echo "Error: The operation was canceled"; exit 255;;
            esac
            """);
        var vmx = h.Write("vms/a.vmx", "displayName = \"a\"\n");
        var device = Vmware.MonitorDevice;
        Vmware.MonitorDevice = Path.Combine(h.Root, "no-vmmon");
        try
        {
            Assert.Equal(1, await h.Run("", "vm", "start", vmx));
        }
        finally
        {
            Vmware.MonitorDevice = device;
        }
        Assert.Contains("The operation was canceled", h.Err, StringComparison.Ordinal);
        Assert.Contains("VMware's kernel module vmmon is not loaded", h.Err, StringComparison.Ordinal);
        Assert.Contains("vmware-modconfig --console --install-all", h.Err, StringComparison.Ordinal);
        Assert.Contains("Secure Boot", h.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VMware_makes_a_VM_in_its_folder_lists_it_from_there_clones_it_and_deletes_it()
    {
        using var h = new Harness(_gateway);
        var bin = Bin(h);
        Script(bin, "vmrun", """
            d="$(dirname "$0")"
            printf '%s\n' "$*" >> "$d/vmrun.log"
            echo "[AppLoader] Use shipped Linux kernel AIO access library."
            shift 2
            case "$1" in
            list) echo "Total running VMs: 0";;
            clone) mkdir -p "$(dirname "$3")"; echo "displayName = \"copy\"" > "$3";;
            deleteVM) cp "$2" "$d/deleted.vmx"; rm -rf "$(dirname "$2")";;
            esac
            """);
        Script(bin, "vmware-vdiskmanager", """
            printf '%s\n' "$*" >> "$(dirname "$0")/vdisk.log"
            for a in "$@"; do last="$a"; done
            echo "Virtual disk creation successful." ; touch "$last"
            """);
        var iso = h.Write("ubuntu.iso", "an ISO");
        Calls(("vm_create", """{"action":"create","name":"web","iso":"ubuntu.iso","os":"windows","disk":30}"""), ("vm_list", "{}"),
            ("vm_create", """{"action":"clone","vm":"web","name":"web2"}"""), ("vm_delete", """{"vm":"web"}"""));
        Assert.Equal(0, await h.Run("go\ny\n/exit\n", "chat", "--mode", "yolo"));
        var results = Results();
        var vmx = Path.Combine(h.Root, "vmware", "web", "web.vmx");
        Assert.StartsWith($"Made web on VMware Workstation (2,048 MB, 2 CPUs, 30 GB of disk, ubuntu.iso in its drive; id {vmx})", results[0], StringComparison.Ordinal);
        var text = File.ReadAllText(Path.Combine(bin, "deleted.vmx"));
        Assert.Contains("guestOS = \"windows9-64\"", text, StringComparison.Ordinal);
        Assert.Contains("firmware = \"efi\"", text, StringComparison.Ordinal);
        Assert.Contains($"sata0:1.fileName = \"{iso}\"", text, StringComparison.Ordinal);
        Assert.Contains("scsi0:0.fileName = \"web.vmdk\"", text, StringComparison.Ordinal);
        Assert.Equal($"-c -s 30GB -a lsisas1068 -t 1 {Path.Combine(h.Root, "vmware", "web", "web.vmdk")}\n", File.ReadAllText(Path.Combine(bin, "vdisk.log")));
        Assert.Contains("- web: off", results[1], StringComparison.Ordinal);
        Assert.Contains("Cloned web as web2", results[2], StringComparison.Ordinal);
        var log = File.ReadAllText(Path.Combine(bin, "vmrun.log"));
        Assert.Contains($"-T ws clone {vmx} {Path.Combine(h.Root, "vmware", "web2", "web2.vmx")} full -cloneName=web2", log, StringComparison.Ordinal);
        Assert.Contains($"-T ws deleteVM {vmx}", log, StringComparison.Ordinal);
        Assert.StartsWith("Deleted web from VMware Workstation", results[3], StringComparison.Ordinal);
        Assert.False(File.Exists(vmx));
    }

    [Fact]
    public async Task Libvirt_defines_a_VM_on_a_new_volume_with_the_guest_agent_s_channel_clones_with_virt_clone_and_deletes_only_its_disks()
    {
        using var h = new Harness(_gateway);
        var bin = Bin(h);
        Script(bin, "virsh", """
            d="$(dirname "$0")"
            printf '%s\n' "$*" >> "$d/virsh.log"
            shift 2
            case "$1" in
            list) [ "$2" = "--all" ] && { echo "lab"; [ -f "$d/made" ] && cat "$d/made"; }; true;;
            vol-create-as) true;;
            vol-path) echo "/var/lib/libvirt/images/$4";;
            define) cp "$2" "$d/defined.xml"; echo "dev" > "$d/made";;
            domblklist)
              echo " Type   Device   Target   Source"
              echo "------------------------------------------------"
              echo " file   disk     vda      /var/lib/libvirt/images/lab.qcow2"
              echo " file   cdrom    sda      /isos/debian.iso"
              echo " file   cdrom    sdb      -";;
            esac
            """);
        Script(bin, "virt-clone", """
            printf '%s\n' "$*" >> "$(dirname "$0")/clone.log"
            echo "Clone 'lab-copy' created successfully."
            """);
        h.Write("debian.iso", "an ISO");
        Calls(("vm_create", """{"action":"create","name":"dev","iso":"debian.iso","memory":4096,"cpus":4}"""),
            ("vm_create", """{"action":"clone","vm":"lab","name":"lab-copy"}"""),
            ("vm_create", """{"action":"clone","vm":"lab","name":"lab-old","snapshot":"before"}"""),
            ("vm_delete", """{"vm":"lab"}"""));
        Assert.Equal(0, await h.Run("go\ny\n/exit\n", "chat", "--mode", "yolo"));
        var results = Results();
        Assert.StartsWith("Made dev on libvirt (4,096 MB, 4 CPUs, 20 GB of disk, debian.iso in its drive", results[0], StringComparison.Ordinal);
        var xml = System.Xml.Linq.XElement.Parse(File.ReadAllText(Path.Combine(bin, "defined.xml")));
        Assert.Equal("dev", xml.Element("name")!.Value);
        Assert.Equal("4096", xml.Element("memory")!.Value);
        Assert.Equal("/var/lib/libvirt/images/dev.qcow2", xml.Descendants("disk").First().Element("source")!.Attribute("file")!.Value);
        Assert.Contains(xml.Descendants("channel"), c => c.Element("target")!.Attribute("name")!.Value == "org.qemu.guest_agent.0");
        Assert.Equal(Path.Combine(h.Work, "debian.iso"), xml.Descendants("disk").Single(d => d.Attribute("device")!.Value == "cdrom").Element("source")!.Attribute("file")!.Value);
        Assert.Contains("Cloned lab as lab-copy on libvirt", results[1], StringComparison.Ordinal);
        Assert.Equal("--connect qemu:///system --original lab --name lab-copy --auto-clone\n", File.ReadAllText(Path.Combine(bin, "clone.log")));
        Assert.Contains("lab has no snapshot before", results[2], StringComparison.Ordinal);
        Assert.StartsWith("Deleted lab from libvirt", results[3], StringComparison.Ordinal);
        var log = File.ReadAllText(Path.Combine(bin, "virsh.log"));
        Assert.Contains("vol-create-as default dev.qcow2 20G --format qcow2", log, StringComparison.Ordinal);
        // The disk's volume goes; the CD-ROM's ISO stays.
        Assert.Contains("undefine lab --snapshots-metadata --managed-save --nvram --storage vda\n", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hyper_V_makes_clones_and_deletes_a_VM_by_its_own_cmdlets()
    {
        using var h = new Harness(_gateway);
        var bin = Bin(h);
        // PowerShell as a stand-in: it keeps each script it is given (-EncodedCommand, UTF-16) and answers an empty list.
        Script(bin, "powershell", """
            d="$(dirname "$0")"
            for a in "$@"; do last="$a"; done
            printf '%s' "$last" | base64 -d | iconv -f UTF-16LE -t UTF-8 >> "$d/scripts.ps1"
            printf '\n----\n' >> "$d/scripts.ps1"
            echo '[]'
            """);
        var hyperv = new HyperV(Path.Combine(bin, "powershell"));
        await hyperv.CreateAsync(new VmSpec("Win 11", 4096, 2, 64, @"C:\isos\win11.iso", "windows"), default);
        await hyperv.CreateAsync(new VmSpec("tux", 1024, 1, 10, null, "linux"), default);
        await hyperv.CloneAsync(new VmInfo("Win 11", "Win 11", hyperv, false), "Win 11 test", "clean", false, default);
        await Assert.ThrowsAsync<ToolError>(() => hyperv.CloneAsync(new VmInfo("tux", "tux", hyperv, false), "t2", null, true, default));
        await hyperv.DeleteAsync(new VmInfo("tux", "tux", hyperv, false), default);
        var scripts = File.ReadAllText(Path.Combine(bin, "scripts.ps1")).Split("\n----\n");
        Assert.Contains("New-VM -Name 'Win 11' -Generation 2 -MemoryStartupBytes 4096MB -NewVHDPath $vhd -NewVHDSizeBytes 64GB", scripts[0], StringComparison.Ordinal);
        Assert.Contains(@"Add-VMDvdDrive -VM $vm -Path 'C:\isos\win11.iso' -Passthru", scripts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("SecureBootTemplate", scripts[0], StringComparison.Ordinal);
        Assert.Contains("Set-VMFirmware -VM $vm -SecureBootTemplate MicrosoftUEFICertificateAuthority", scripts[1], StringComparison.Ordinal);
        Assert.Contains("Export-VMSnapshot -VMName 'Win 11' -Name 'clean' -Path $tmp", scripts[2], StringComparison.Ordinal);
        Assert.Contains("Import-VM -Path $config.FullName -Copy -GenerateNewId", scripts[2], StringComparison.Ordinal);
        Assert.Contains("Rename-VM -VM $copy -NewName 'Win 11 test'", scripts[2], StringComparison.Ordinal);
        Assert.Contains("Get-VMSnapshot -VM $vm | Remove-VMSnapshot", scripts[3], StringComparison.Ordinal);
        Assert.Contains("Remove-VM -VM $vm -Force", scripts[3], StringComparison.Ordinal);
    }

    [Fact]
    public void Keys_pictures_and_refusals_are_read_as_the_hypervisors_say_them()
    {
        Assert.Equal("ctrl+alt+delete", KeyChord.Parse("Ctrl+Alt+Del")!.ToString());
        Assert.Equal("enter", KeyChord.Parse("Return")!.ToString());
        Assert.Null(KeyChord.Parse("ctrl+"));
        Assert.Null(KeyChord.Parse("hyper+x"));
        Assert.Equal(["1d", "38", "e0", "53", "e0", "d3", "b8", "9d"], VirtualBox.Scancodes(KeyChord.Parse("ctrl+alt+delete")!));
        Assert.Equal(["1c", "9c"], VirtualBox.Scancodes(KeyChord.Parse("enter")!));
        Assert.Equal("KEY_LEFTMETA", Libvirt.Linux("win"));
        Assert.Equal(["KEY_LEFTSHIFT", "KEY_SEMICOLON"], Libvirt.Typed(':')!);
        Assert.Null(Libvirt.Typed('é'));
        Assert.Equal(0x74, HyperV.VirtualKey("f5"));
        Assert.Equal('Q', HyperV.VirtualKey("q"));

        var rgb = new byte[3 * 2 * 3];
        rgb[0] = 255;
        rgb[16] = 200;
        var png = VmPictures.Png(3, 2, rgb);
        Assert.Equal((3, 2), VmPictures.Size(png));
        Assert.Equal(rgb, VmPictures.Read(png)!.Value.Rgb);
        Assert.Contains("blank", VmPictures.Describe(VmPictures.Png(8, 8, new byte[8 * 8 * 3])), StringComparison.Ordinal);
        var white = Enumerable.Repeat((byte)255, 8 * 8 * 3).ToArray();
        Assert.Contains("100% of it white", VmPictures.Describe(VmPictures.Png(8, 8, white)), StringComparison.Ordinal);
        Assert.Equal((2, 1), VmPictures.Size(VmPictures.Rgb565ToPng(2, 1, [0x00, 0xf8, 0xe0, 0x07])!));
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0 }, VmPictures.Read(VmPictures.Rgb565ToPng(2, 1, [0x00, 0xf8, 0xe0, 0x07])!)!.Value.Rgb);
        Assert.Null(VmPictures.PpmToPng(Encoding.ASCII.GetBytes("P3\n1 1\n255\n0 0 0\n")));

        var login = new GuestLogin("tester", "hunter22");
        Assert.Equal("tester", login.ToString());
        Assert.Equal("pw: •••• and ••••", login.Hide("pw: hunter22 and hunter22"));
        Assert.Equal("a b", new GuestLogin("u", "a").Hide("a b"));
        Assert.True(VirtualMachines.Refusal("VBoxManage: error: VERR_AUTHENTICATION_FAILURE"));
        Assert.True(VirtualMachines.Refusal("Error: Invalid user name or password for the guest OS"));
        Assert.False(VirtualMachines.Refusal("Error: The virtual machine is not powered on"));
        Assert.Equal("/home/me", Hypervisor.GuestParent("/home/me/app.sh", windows: false));
        Assert.Equal("/", Hypervisor.GuestParent("/app.sh", windows: false));
        Assert.Equal(@"C:\Users\me", Hypervisor.GuestParent(@"C:\Users\me\app.exe", windows: true));
        Assert.Equal(@"C:\", Hypervisor.GuestParent(@"C:\app.exe", windows: true));
        Assert.Equal("'it'\\''s'", Hypervisor.Sh("it's"));
    }
}

internal static class JsonAlso
{
    public static JsonObject Also(this JsonObject o, Action<JsonObject> change)
    {
        change(o);
        return o;
    }
}
