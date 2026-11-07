// dotnet ide-launcher.dll OUT: writes code-arena-ide.exe there, at the release's version (VERSION, through the build).
if (args is not [var output])
{
    Console.Error.WriteLine("usage: ide-launcher OUT (the code-arena-ide.exe to write)");
    return 2;
}
var version = typeof(CodeArenaIde.IdeLauncher).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
CodeArenaIde.IdeLauncher.Write(output, version);
Console.WriteLine($"{output}: the IDE's launcher for Windows, {new FileInfo(output).Length} bytes");
return 0;
