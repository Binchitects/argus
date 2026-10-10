using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>The IDE's Problems: the project's check run in the folder, and what it printed read as problems.</summary>
public sealed class ProblemsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("code-arena-problems-").FullName;
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string Write(string relative, string text = "x\n")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private Problems New(string? command = null) => new(new Workspace(_root), new Config { CheckCommand = command });

    [Fact]
    public void Compilers_and_linters_lines_are_read_as_the_folders_problems_errors_first()
    {
        Write("src/App.cs");
        Write("web/app.ts");
        Write("main.c");
        Write("tool.py");
        var found = New().Read($"""
            Build started.
            {Path.Combine(_root, "src", "App.cs")}(12,9): warning CS0168: The variable 'e' is declared but never used [{_root}/App.csproj]
            web/app.ts(3,15): error TS2322: Type 'string' is not assignable to type 'number'.
            main.c:7:3: error: expected ';' before 'return'
            tool.py:1:8: F401 [*] `os` imported but unused
            /usr/include/stdio.h:12:1: error: not ours
            elsewhere.ts(1,1): error TS1005: ';' expected.
            web/app.ts(3,15): error TS2322: Type 'string' is not assignable to type 'number'.
            """);
        Assert.Equal(
            [
                ("main.c", 7, 3, "error", (string?)null, "expected ';' before 'return'"),
                ("web/app.ts", 3, 15, "error", "TS2322", "Type 'string' is not assignable to type 'number'."),
                ("src/App.cs", 12, 9, "warning", "CS0168", "The variable 'e' is declared but never used"),
                ("tool.py", 1, 8, "warning", "F401", "[*] `os` imported but unused"),
            ],
            found.Select(p => (p.Path, p.Line, p.Column, p.Severity, p.Code, p.Message)));
    }

    [Fact]
    public void The_check_is_the_configs_else_found_by_the_projects_files()
    {
        Assert.Null(New().Command());
        Write("package.json", """{"scripts":{"lint":"eslint ."}}""");
        Assert.Equal("npm run --silent lint", New().Command());
        Write("tsconfig.json", "{}");
        Assert.StartsWith("npx --no-install tsc --noEmit", New().Command());
        Write("Shop.csproj", "<Project />");
        Assert.StartsWith("dotnet build", New().Command());
        Assert.Equal("make lint", New("make lint").Command());
    }

    [Fact]
    public async Task The_IDE_runs_the_check_and_lists_what_it_found()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp, c => c["checkCommand"] = "echo 'src/a.ts(2,5): error TS1005: ; expected.'; exit 2");
        h.Write("src/a.ts", "let a\nlet b c\n");
        await using var web = await WebRun.StartAsync(h);
        var before = await web.GetJsonAsync("/api/problems");
        Assert.Null(before["ran"]);
        Assert.StartsWith("echo", before["command"]!.GetValue<string>());

        var ran = await web.PostAsync("/api/problems/run", new JsonObject());
        Assert.Equal(HttpStatusCode.OK, ran.StatusCode);
        var run = JsonNode.Parse(await ran.Content.ReadAsStringAsync())!;
        Assert.Equal(2, run["exitCode"]!.GetValue<int>());
        var problem = Assert.Single(run["problems"]!.AsArray())!;
        Assert.Equal("src/a.ts", problem["path"]!.GetValue<string>());
        Assert.Equal(2, problem["line"]!.GetValue<int>());
        Assert.Equal("error", problem["severity"]!.GetValue<string>());
        Assert.NotNull((await web.GetJsonAsync("/api/problems"))["ran"]);
    }

    [Fact]
    public async Task A_folder_with_no_check_says_how_to_set_one()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        var refused = await web.PostAsync("/api/problems/run", new JsonObject());
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Contains("checkCommand", await refused.Content.ReadAsStringAsync());
    }
}
