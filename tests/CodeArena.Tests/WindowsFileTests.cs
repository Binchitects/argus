using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>What the tools met on Windows: UTF-16 files, Windows line endings, files others hold open, Git Bash and its paths.</summary>
public sealed class WindowsFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("arena-win-").FullName;
    private readonly StringWriter _out = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ToolContext Context() => new() { Workspace = new Workspace(_root), Ui = new Ui(new StringReader(""), _out, _out, color: false, canAsk: false) };

    private Task<ToolResult> Run(string tool, object args) =>
        LocalTools.All().Single(t => t.Name == tool).Run(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject(), Context(), CancellationToken.None);

    [Fact]
    public async Task A_utf16_file_with_its_mark_is_text_read_searched_edited_and_kept_utf16()
    {
        // As Windows PowerShell's > writes it.
        var file = Path.Combine(_root, "out.txt");
        File.WriteAllText(file, "first line\r\nerror: disk full\r\n", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        var read = await Run("read_file", new { path = "out.txt" });
        Assert.Contains("error: disk full", read.Text);
        Assert.DoesNotContain("binary", read.Text);
        var found = await Run("grep", new { pattern = "disk full" });
        Assert.Contains("out.txt:2: error: disk full", found.Text);

        await Run("edit_file", new { path = "out.txt", old_string = "disk full", new_string = "disk fine" });
        var bytes = File.ReadAllBytes(file);
        Assert.Equal([0xFF, 0xFE], bytes[..2]);
        Assert.Equal("first line\r\nerror: disk fine\r\n", new UnicodeEncoding(false, true).GetString(bytes[2..]));
        Assert.False(Files.LooksBinary(file));
        // A real binary is still one.
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), [0x50, 0x4B, 0x00, 0x01]);
        Assert.True(Files.LooksBinary(Path.Combine(_root, "a.bin")));
    }

    [Fact]
    public async Task Write_file_keeps_a_file_s_windows_line_endings()
    {
        var file = Path.Combine(_root, "win.cs");
        File.WriteAllText(file, "class A\r\n{\r\n}\r\n");
        await Run("write_file", new { path = "win.cs", content = "class B\n{\n}\n" });
        Assert.Equal("class B\r\n{\r\n}\r\n", File.ReadAllText(file));
        // A file with \n stays so, and a new file is as written.
        File.WriteAllText(file, "a\nb\n");
        await Run("write_file", new { path = "win.cs", content = "c\nd\n" });
        Assert.Equal("c\nd\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task A_file_another_program_holds_open_for_writing_is_still_read_and_searched()
    {
        var file = Path.Combine(_root, "server.log");
        File.WriteAllText(file, "started\nlistening on 8080\n");
        // As a running server holds its log: open to write, sharing reads and writes (Windows refuses a read that does not share).
        using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.Contains("listening on 8080", (await Run("read_file", new { path = "server.log" })).Text);
        Assert.Contains("server.log:2:", (await Run("grep", new { pattern = "8080" })).Text);
        Assert.Equal("started\nlistening on 8080\n", Files.ReadText(file).Text);
    }

    [Fact]
    public void On_windows_run_shell_uses_git_s_bash_then_pwsh_then_cmd()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string?> env = new() { ["PATH"] = @"C:\Windows\System32;C:\Tools\Git\cmd", ["ProgramFiles"] = @"C:\Program Files" };
        string Shell() => LocalTools.WindowsShell(k => env.GetValueOrDefault(k), files.Contains);

        // System32's bash.exe is WSL's: never taken.
        files.Add(@"C:\Windows\System32\bash.exe");
        Assert.Equal("cmd.exe", Shell());
        files.Add(@"C:\Windows\System32\pwsh.exe");
        Assert.Equal(@"C:\Windows\System32\pwsh.exe", Shell());
        // Beside the git on PATH (Git\cmd's git.exe: Git\bin\bash.exe).
        files.Add(@"C:\Tools\Git\cmd\git.exe");
        files.Add(@"C:\Tools\Git\bin\bash.exe");
        Assert.Equal(@"C:\Tools\Git\bin\bash.exe", Shell());
        // Where Git for Windows installs it, first.
        files.Add(@"C:\Program Files\Git\bin\bash.exe");
        Assert.Equal(@"C:\Program Files\Git\bin\bash.exe", Shell());
        // One the person configured is theirs.
        Assert.Equal("powershell.exe", LocalTools.ShellName("powershell.exe"));
    }

    [Theory]
    [InlineData("/c/Users/me/proj/app.cs", "C:/Users/me/proj/app.cs")]
    [InlineData("/d", "D:/")]
    [InlineData("/d/", "D:/")]
    [InlineData("/src/app.cs", "/src/app.cs")]
    [InlineData("src/app.cs", "src/app.cs")]
    [InlineData("/cd/x", "/cd/x")]
    public void A_path_as_git_bash_writes_it_is_read_as_windows_writes_it(string bash, string windows) =>
        Assert.Equal(windows, Workspace.FromBash(bash));
}
