extern alias CodeArenaCli;
using Ca = CodeArenaCli::CodeArena;

namespace Llm.Tests;

/// <summary>Code Arena's log on the person's machine (code-arena.log beside the program): the IDE's key never reaches it.</summary>
public sealed class CodeArenaLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("code-arena-log-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    [Trait("Control", "IAM-06")]
    public void Code_Arenas_log_never_holds_the_IDEs_key_and_only_its_user_reads_it()
    {
        string path;
        using (var log = Ca.LogFile.Open(Path.Combine(_dir, "code-arena"), Path.Combine(_dir, "data"))!)
        {
            path = log.Path;
            log.Hide("Zm9yIHRoZSBJREUgb25seQ");
            log.WriteLine("  http://127.0.0.1:4100/?token=Zm9yIHRoZSBJREUgb25seQ");
            log.Write("the key again: Zm9yIHRoZSBJREUg");
            log.Write("b25seQ in two writes\n");
        }
        var text = File.ReadAllText(path);
        Assert.Contains("?token=…", text);
        Assert.Contains("the key again: … in two writes", text);
        Assert.DoesNotContain("Zm9yIHRoZSBJREUgb25seQ", text);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }
}
