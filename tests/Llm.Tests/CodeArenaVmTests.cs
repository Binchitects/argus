extern alias CodeArenaCli;
using System.Text.Json.Nodes;
using Ca = CodeArenaCli::CodeArena;

namespace Llm.Tests;

/// <summary>Code Arena's VM tools: a guest's password stays out of sight, and a VM changes only when the person allows it.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class CodeArenaVmTests : IDisposable
{
    private const string Password = "guest-Pa55-word";
    private readonly string _dir = Directory.CreateTempSubdirectory("code-arena-vm-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>VBoxManage as a stand-in: it logs its arguments and the password file's mode, and runs the guest's command here.</summary>
    private string FakeVBoxManage()
    {
        var file = Path.Combine(_dir, "VBoxManage");
        File.WriteAllText(file, """
            #!/bin/sh
            d="$(dirname "$0")"
            printf '%s\n' "$*" >> "$d/vbox.log"
            case "$1" in
            list) echo '"test-vm" {11111111-2222-3333-4444-555555555555}';;
            showvminfo) echo 'VMState="running"'; echo 'ostype="Ubuntu (64-bit)"';;
            guestcontrol)
              while [ $# -gt 0 ] && [ "$1" != "--" ]; do
                [ "$1" = "--passwordfile" ] && { ls -l "$2" | cut -c1-10 > "$d/mode"; cat "$2" > /dev/null; }
                shift
              done
              shift; exec "$@";;
            esac
            """);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return file;
    }

    private (Ca.VirtualMachines Machines, Ca.ToolContext Context) Machines(string answers)
    {
        var ui = new Ca.Ui(new StringReader(answers), TextWriter.Null, TextWriter.Null, false, true) { SecretReader = _ => Password };
        var machines = new Ca.VirtualMachines([new Ca.VirtualBox(FakeVBoxManage())], new Ca.GuestLogins(new Ca.NoKeyring(), ui), () => false);
        var work = Directory.CreateDirectory(Path.Combine(_dir, "work")).FullName;
        return (machines, new Ca.ToolContext { Workspace = new Ca.Workspace(work), Ui = ui });
    }

    [Fact]
    [Trait("Control", "DAT-04")]
    public async Task A_VMs_guest_password_never_reaches_the_model_or_a_command_line()
    {
        var (machines, context) = Machines("tester\n");
        var run = machines.Tools().Single(t => t.Name == "vm_run");
        // The guest prints it (a script echoing its settings): the model sees it blanked.
        File.WriteAllText(Path.Combine(_dir, "settings.txt"), $"password={Password}\n");
        var result = await run.Run(new JsonObject { ["vm"] = "test-vm", ["command"] = $"cat {_dir}/settings.txt" }, context, default);
        Assert.False(result.Error, result.Text);
        Assert.Contains("••••", result.Text);
        Assert.DoesNotContain(Password, result.Text);
        Assert.DoesNotContain(Password, File.ReadAllText(Path.Combine(_dir, "vbox.log")));
        Assert.Equal("-rw-------", File.ReadAllText(Path.Combine(_dir, "mode")).Trim());
        Assert.Equal("tester", new Ca.GuestLogin("tester", Password).ToString());
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task Changing_a_VM_asks_the_person_and_always_covers_that_tool_on_that_VM_only()
    {
        var (machines, _) = Machines("");
        var tools = machines.Tools();
        var output = new StringWriter();
        var permissions = new Ca.Permissions(new Ca.Ui(new StringReader("a\nn\n"), output, output, false, true), Ca.Mode.AutoEdit);
        var run = tools.Single(t => t.Name == "vm_run");
        Assert.Null(await permissions.CheckAsync(tools.Single(t => t.Name == "vm_list"), [], default));
        Assert.Null(await permissions.CheckAsync(run, new JsonObject { ["vm"] = "test-vm", ["command"] = "make" }, default));
        Assert.Null(await permissions.CheckAsync(run, new JsonObject { ["vm"] = "test-vm", ["command"] = "make test" }, default));
        Assert.Contains("declined", await permissions.CheckAsync(run, new JsonObject { ["vm"] = "prod-vm", ["command"] = "make" }, default));
        permissions.Mode = Ca.Mode.Plan;
        Assert.Contains("read-only", await permissions.CheckAsync(tools.Single(t => t.Name == "vm_snapshot"), new JsonObject { ["vm"] = "test-vm" }, default));
    }
}
