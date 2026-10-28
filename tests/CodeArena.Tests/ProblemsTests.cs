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
    public void A_build_follows_the_projects_build_configuration_Debug_unless_Release_is_chosen()
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, ".data")).FullName;
        var state = new WorkspaceState(data, _root);
        Problems Checked() => new(new Workspace(_root), new Config(), state);
        Write("Cargo.toml", "[package]\nname = \"shop\"\n");
        Assert.Equal("cargo check --message-format=short", Checked().Command());
        state.Merge(new JsonObject { ["buildConfiguration"] = "Release" });
        Assert.Equal("cargo check --release --message-format=short", Checked().Command());

        Write("Shop.csproj", "<Project />");
        Assert.StartsWith("dotnet build -c Release ", Checked().Command());
        state.Merge(new JsonObject { ["buildConfiguration"] = null });
        Assert.StartsWith("dotnet build -c Debug ", Checked().Command());
        // Without the project's state (no IDE): Debug.
        Assert.StartsWith("dotnet build -c Debug ", New().Command());
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
    public async Task The_page_chooses_Release_and_the_check_builds_it()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write("Shop.csproj", "<Project />");
        await using var web = await WebRun.StartAsync(h);
        Assert.StartsWith("dotnet build -c Debug ", (await web.GetJsonAsync("/api/problems"))["command"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/workspace/state", new JsonObject { ["buildConfiguration"] = "Release" })).StatusCode);
        Assert.StartsWith("dotnet build -c Release ", (await web.GetJsonAsync("/api/problems"))["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_security_reviews_open_findings_are_problems_and_Fix_asks_what_Fix_it_would()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write(".git/config", "[core]\n\tbare = false\n[remote \"origin\"]\n\turl = git@gitlab.example.com:group/shop.git\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n");
        h.Write(".git/HEAD", "ref: refs/heads/feature/pay\n");
        h.Write("src/Orders.cs", "class Orders {}\n");
        await using var web = await WebRun.StartAsync(h);
        // An older Arena does not say its reviews: the view says so, and lists nothing.
        _mcp.Chats.Security = null;
        var older = await web.GetJsonAsync("/api/problems/security");
        Assert.Contains("older", older["said"]!.GetValue<string>(), StringComparison.Ordinal);

        _mcp.Chats.Security = new JsonObject { ["scan"] = null, ["findings"] = new JsonArray() };
        var none = JsonNode.Parse(await (await web.PostAsync("/api/problems/security", new JsonObject())).Content.ReadAsStringAsync())!;
        Assert.Equal("No security review of group/shop at feature/pay in Arena yet: start one in Code Studio → Security.", none["said"]!.GetValue<string>());
        Assert.Equal("?project=group%2Fshop&ref=feature%2Fpay", _mcp.Chats.SecurityAsked[^1]);

        _mcp.Chats.Security = JsonNode.Parse("""
            {"scan": {"id": "s1", "project": "group/shop", "ref": "feature/pay", "commit": "abc123", "finishedAt": "2026-10-28T00:00:00Z", "link": "/code/security/s1"},
             "findings": [
               {"id": "f1", "kind": "code", "path": "src/Orders.cs", "line": 4, "severity": "high", "exploited": false, "cwe": "CWE-89", "rule": null, "title": "SQL built from the request", "ask": "Fix this security weakness in src/Orders.cs at line 4: CWE-89."},
               {"id": "f2", "kind": "secret", "path": "config/app.env", "line": 2, "severity": "medium", "exploited": false, "cwe": "CWE-798", "rule": "aws-access-token", "title": "A secret", "ask": "Take the secret out."}
             ]}
            """)!.AsObject();
        var read = JsonNode.Parse(await (await web.PostAsync("/api/problems/security", new JsonObject())).Content.ReadAsStringAsync())!;
        Assert.Equal((2, $"{_mcp.BaseUrl.TrimEnd('/')}/code/security/s1"), (read["count"]!.GetValue<int>(), read["link"]!.GetValue<string>()));
        var listed = (await web.GetJsonAsync("/api/problems"))["problems"]!.AsArray().Select(p => p!.AsObject()).Where(p => p["source"]!.GetValue<string>() == "security").ToList();
        Assert.Equal(["src/Orders.cs:4 error CWE-89", "config/app.env:2 warning CWE-798"], listed.Select(p => $"{p["path"]}:{p["line"]} {p["severity"]} {p["code"]}"));
        Assert.Equal("SQL built from the request (high, security review)", listed[0]["message"]!.GetValue<string>());
        Assert.Equal("Fix this security weakness in src/Orders.cs at line 4: CWE-89.", listed[0]["ask"]!.GetValue<string>());

        // Fixed in Arena since: read again, it is gone.
        _mcp.Chats.Security["findings"]!.AsArray().RemoveAt(0);
        await web.PostAsync("/api/problems/security", new JsonObject());
        Assert.DoesNotContain((await web.GetJsonAsync("/api/problems"))["problems"]!.AsArray(), p => p!["path"]!.GetValue<string>() == "src/Orders.cs");
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
