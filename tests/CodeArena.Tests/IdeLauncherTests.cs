using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using CodeArenaIde;

namespace CodeArena.Tests;

/// <summary>
/// "After starting code arena a shell window is open": code-arena.exe is a console program, so Windows gives it a console
/// on a double-click. code-arena-ide.exe beside it is a program of the GUI subsystem, a few kilobytes, that starts it with
/// no console window. Read here as Windows reads it, and run here on .NET, which runs a .NET Framework program as it is.
/// </summary>
public sealed class IdeLauncherTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ide-launcher-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void It_is_a_small_windows_program_with_no_console_that_Windows_runs_with_nothing_installed()
    {
        var image = IdeLauncher.Build(new Version(7, 8, 3, 0));
        Assert.True(image.Length < 8 * 1024, $"{image.Length} bytes");
        // The same bytes every time: one SHA-256 for the list and the signature.
        Assert.Equal(image, IdeLauncher.Build(new Version(7, 8, 3, 0)));

        using var pe = new PEReader(new MemoryStream(image));
        Assert.Equal(Subsystem.WindowsGui, pe.PEHeaders.PEHeader!.Subsystem);
        Assert.Equal(Machine.I386, pe.PEHeaders.CoffHeader.Machine);
        // IL only, for any CPU: x64, and ARM64 natively on Windows 11.
        Assert.Equal(CorFlags.ILOnly, pe.PEHeaders.CorHeader!.Flags);
        var md = pe.GetMetadataReader();
        Assert.Equal("v4.0.30319", md.MetadataVersion);
        Assert.Equal(["mscorlib 4.0.0.0", "System 4.0.0.0"], md.AssemblyReferences.Select(r => md.GetAssemblyReference(r)).Select(r => $"{md.GetString(r.Name)} {r.Version}"));
        Assert.Equal(new Version(7, 8, 3, 0), md.GetAssemblyDefinition().Version);
    }

    [Fact]
    public void It_starts_code_arena_beside_it_with_its_arguments_and_folder_marked_as_started_without_a_console()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The fake code-arena.exe below is a shell script.
        }
        var app = Path.Combine(_dir, "app");
        Directory.CreateDirectory(app);
        IdeLauncher.Write(Path.Combine(app, IdeLauncher.FileName), new Version(1, 0));
        File.WriteAllText(Path.Combine(app, "code-arena-ide.runtimeconfig.json"),
            """{"runtimeOptions":{"tfm":"net10.0","rollForward":"LatestMajor","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""");
        var said = Path.Combine(_dir, "said.txt");
        var fake = Path.Combine(app, IdeLauncher.Program);
        // What it was started with, written whole at once (the launcher does not wait for it).
        File.WriteAllText(fake, $"#!/bin/sh\nprintf '%s|%s|%s|%s' \"$CODE_ARENA_DETACHED\" \"$(pwd)\" \"$#\" \"$*\" > '{said}.new' && mv '{said}.new' '{said}'\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var work = Path.Combine(_dir, "my project");
        Directory.CreateDirectory(work);

        var code = Run(app, work, "web", "--port", "8765", "my folder");
        Assert.Equal(0, code);
        Written(said);
        Assert.Equal($"1|{work}|4|web --port 8765 my folder", File.ReadAllText(said));

        // No arguments: the IDE, as a double-click starts it.
        File.Delete(said);
        Assert.Equal(0, Run(app, work));
        Written(said);
        Assert.Equal($"1|{work}|0|", File.ReadAllText(said));

        // Without code-arena.exe it ends with 1 (on Windows a message box says so).
        File.Delete(fake);
        Assert.NotEqual(0, Run(app, work));
    }

    /// <summary>Until the fake code-arena.exe has written what it was started with (it runs on after the launcher ends).</summary>
    private static void Written(string file)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(file))
        {
            Assert.True(DateTime.UtcNow < until, "code-arena.exe was not started");
            Thread.Sleep(20);
        }
    }

    /// <summary>The launcher, run by the dotnet that runs these tests, in a folder; its exit code.</summary>
    private static int Run(string app, string cwd, params string[] args)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var psi = new ProcessStartInfo(dotnet) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in (string[])["exec", "--runtimeconfig", Path.Combine(app, "code-arena-ide.runtimeconfig.json"), Path.Combine(app, IdeLauncher.FileName), .. args])
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(30_000), "the launcher did not end");
        _ = output.Result + error.Result;
        return p.ExitCode;
    }
}
