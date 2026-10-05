using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena.Tests;

/// <summary>The IDE's calls against the real server: files, search, the agent's changes, and terminals on a real pseudo-terminal.</summary>
public sealed class IdeTests : IDisposable
{
    /// <summary>/proc's SigBlk and SigIgn: none blocked, and none of the signals 1 to 31 ignored.</summary>
    private const string DefaultSignals = @"SigBlk:\s+0{16}\r\nSigIgn:\s+[0-9a-f]{8}[08]0{7}\r\n";

    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    [Fact]
    public async Task Files_are_listed_read_saved_made_renamed_and_deleted_inside_the_working_directory()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write("src/app.txt", "one\n");
        h.Write("README.md", "# Shop\n");
        h.Write(".git/HEAD", "ref: refs/heads/main\n");
        await using var web = await WebRun.StartAsync(h);

        // Folders first, then files; what VS Code hides (.git) is hidden.
        var top = await web.GetJsonAsync("/api/files");
        Assert.Equal(".", top["path"]!.GetValue<string>());
        Assert.Equal(["src:dir", "README.md:file"], top["entries"]!.AsArray().Select(e => $"{e!["name"]}:{e["kind"]}"));
        var src = await web.GetJsonAsync("/api/files?path=src");
        Assert.Equal("src/app.txt", Assert.Single(src["entries"]!.AsArray())!["path"]!.GetValue<string>());

        // Read, saved with the version it was read at; a stale version is refused, not written over.
        var file = await web.GetJsonAsync("/api/file?path=src/app.txt");
        Assert.Equal("one\n", file["text"]!.GetValue<string>());
        var version = file["version"]!.GetValue<string>();
        var saved = await Json(await web.PostAsync("/api/file", new JsonObject { ["path"] = "src/app.txt", ["text"] = "two\n", ["version"] = version }));
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(h.Work, "src", "app.txt")));
        Assert.NotEqual(version, saved["version"]!.GetValue<string>());
        var stale = await web.PostAsync("/api/file", new JsonObject { ["path"] = "src/app.txt", ["text"] = "three\n", ["version"] = version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("changed on disk since it was opened", await stale.Content.ReadAsStringAsync());
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(h.Work, "src", "app.txt")));

        // A byte-order mark stays; a binary file is not offered as text.
        File.WriteAllBytes(Path.Combine(h.Work, "bom.txt"), [0xEF, 0xBB, 0xBF, (byte)'a']);
        Assert.Equal("a", (await web.GetJsonAsync("/api/file?path=bom.txt"))["text"]!.GetValue<string>());
        await Json(await web.PostAsync("/api/file", new JsonObject { ["path"] = "bom.txt", ["text"] = "b" }));
        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'b'], File.ReadAllBytes(Path.Combine(h.Work, "bom.txt")));
        File.WriteAllBytes(Path.Combine(h.Work, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0, 0, 1]);
        var binary = await web.GetJsonAsync("/api/file?path=logo.png");
        Assert.True(binary["binary"]!.GetValue<bool>());
        Assert.Null(binary["text"]);

        // New folder and file, rename, delete.
        Assert.Equal("dir", (await Json(await web.PostAsync("/api/files/new", new JsonObject { ["path"] = "src/lib", ["kind"] = "dir" })))["kind"]!.GetValue<string>());
        await Json(await web.PostAsync("/api/files/new", new JsonObject { ["path"] = "src/lib/util.txt", ["kind"] = "file" }));
        Assert.Equal("", File.ReadAllText(Path.Combine(h.Work, "src", "lib", "util.txt")));
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/files/new", new JsonObject { ["path"] = "src/lib/util.txt" })).StatusCode);
        var moved = await Json(await web.PostAsync("/api/files/rename", new JsonObject { ["from"] = "src/lib/util.txt", ["to"] = "src/lib/helpers.txt" }));
        Assert.Equal("src/lib/helpers.txt", moved["to"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(h.Work, "src", "lib", "helpers.txt")));
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/files/rename", new JsonObject { ["from"] = "src/lib/helpers.txt", ["to"] = "README.md" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/files/delete", new JsonObject { ["path"] = "src/lib" })).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(h.Work, "src", "lib")));
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/files/delete", new JsonObject { ["path"] = "src/lib" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.GetAsync("/api/file?path=missing.txt")).StatusCode);
    }

    [Fact]
    public async Task Nothing_outside_the_working_directory_is_read_written_moved_or_deleted()
    {
        using var h = new Harness(_gateway, _mcp);
        var secret = Path.Combine(h.Root, "secret.txt");
        File.WriteAllText(secret, "keep out\n");
        h.Write("inside.txt", "in\n");
        await using var web = await WebRun.StartAsync(h);

        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync("/api/file?path=../secret.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync("/api/file?path=" + Uri.EscapeDataString(secret))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync("/api/files?path=..")).StatusCode);
        var write = await web.PostAsync("/api/file", new JsonObject { ["path"] = "../secret.txt", ["text"] = "owned" });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Contains("is outside the folder Code Arena works in", await write.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/files/new", new JsonObject { ["path"] = "../new.txt" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/files/rename", new JsonObject { ["from"] = "inside.txt", ["to"] = "../moved.txt" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/files/rename", new JsonObject { ["from"] = "../secret.txt", ["to"] = "stolen.txt" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/files/delete", new JsonObject { ["path"] = "../secret.txt" })).StatusCode);
        // The working directory itself is not a file to delete or move, however it is written.
        foreach (var root in new[] { ".", "./", "./.", ".//", "" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/files/delete", new JsonObject { ["path"] = root })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/files/rename", new JsonObject { ["from"] = root, ["to"] = "elsewhere" })).StatusCode);
        }
        Assert.True(File.Exists(Path.Combine(h.Work, "inside.txt")));
        Assert.Equal("keep out\n", File.ReadAllText(secret));
        Assert.True(File.Exists(Path.Combine(h.Work, "inside.txt")));
        Assert.False(File.Exists(Path.Combine(h.Root, "new.txt")) || File.Exists(Path.Combine(h.Root, "moved.txt")) || File.Exists(Path.Combine(h.Work, "stolen.txt")));

        if (!OperatingSystem.IsWindows())
        {
            // A link inside that leads outside counts as outside: not listed, not read.
            File.CreateSymbolicLink(Path.Combine(h.Work, "escape.txt"), secret);
            Assert.DoesNotContain((await web.GetJsonAsync("/api/files"))["entries"]!.AsArray(), e => e!["name"]!.GetValue<string>() == "escape.txt");
            Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync("/api/file?path=escape.txt")).StatusCode);
        }

        // And none of it without this run's key.
        using var anonymous = WebRun.Client(web.Port, token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/files")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/file?path=inside.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/terminals")).StatusCode);
    }

    [Fact]
    public async Task The_page_names_files_by_relative_paths_only_and_links_that_lead_out_count_as_outside()
    {
        using var h = new Harness(_gateway, _mcp);
        var secret = Path.Combine(h.Root, "secret.txt");
        File.WriteAllText(secret, "keep out\n");
        var inside = h.Write("inside.txt", "in\n");
        h.Write("src/app.txt", "app\n");
        await using var web = await WebRun.StartAsync(h);

        // An absolute path or a .. is refused, even where it would land inside.
        foreach (var path in new[] { inside, "src/../inside.txt", "./src/../inside.txt", @"src\..\inside.txt", "/etc/hostname" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync("/api/file?path=" + Uri.EscapeDataString(path))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/file", new JsonObject { ["path"] = inside, ["text"] = "owned" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/files/rename", new JsonObject { ["from"] = "inside.txt", ["to"] = "src/../moved.txt" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/file", new JsonObject { ["path"] = "in\0side.txt", ["text"] = "x" })).StatusCode);
        Assert.Equal("in\n", File.ReadAllText(inside));
        // ~ is a name like any other here, not the home folder.
        await Json(await web.PostAsync("/api/files/new", new JsonObject { ["path"] = "~/notes.txt" }));
        Assert.True(File.Exists(Path.Combine(h.Work, "~", "notes.txt")));

        if (OperatingSystem.IsWindows())
        {
            return; // links need a right of their own there
        }
        var outside = Directory.CreateDirectory(Path.Combine(h.Root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "other.txt"), "keep out\n");
        // A link to a file not there yet (saving through it would make it outside), a folder outside,
        // a link inside by name only (through that folder), a link to a file outside, and one that stays inside.
        File.CreateSymbolicLink(Path.Combine(h.Work, "dangling.txt"), Path.Combine(h.Root, "planted.txt"));
        Directory.CreateSymbolicLink(Path.Combine(h.Work, "out"), outside);
        File.CreateSymbolicLink(Path.Combine(h.Work, "sneaky.txt"), Path.Combine(h.Work, "out", "other.txt"));
        File.CreateSymbolicLink(Path.Combine(h.Work, "escape.txt"), secret);
        File.CreateSymbolicLink(Path.Combine(h.Work, "alias.txt"), inside);
        // Inside by name only, as the system reads them: through out, .. is outside's own folder (where secret.txt
        // is); through d, the top of the disk, d/../etc is /etc.
        File.CreateSymbolicLink(Path.Combine(h.Work, "back.txt"), "out/../secret.txt");
        Directory.CreateSymbolicLink(Path.Combine(h.Work, "d"), "/");
        File.CreateSymbolicLink(Path.Combine(h.Work, "host.txt"), "d/../etc/hostname");
        foreach (var link in new[] { "back.txt", "host.txt" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync("/api/file?path=" + link)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/file", new JsonObject { ["path"] = link, ["text"] = "owned" })).StatusCode);
        }
        Assert.Equal("keep out\n", File.ReadAllText(secret));

        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/file", new JsonObject { ["path"] = "dangling.txt", ["text"] = "planted" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/files/new", new JsonObject { ["path"] = "dangling.txt" })).StatusCode);
        Assert.False(File.Exists(Path.Combine(h.Root, "planted.txt")));
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync("/api/files?path=out")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/file", new JsonObject { ["path"] = "out/new.txt", ["text"] = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/files/delete", new JsonObject { ["path"] = "out/other.txt" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/files/rename", new JsonObject { ["from"] = "src/app.txt", ["to"] = "out/app.txt" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync("/api/file?path=sneaky.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync("/api/file?path=escape.txt")).StatusCode);
        Assert.False(File.Exists(Path.Combine(outside, "new.txt")) || File.Exists(Path.Combine(outside, "app.txt")));
        Assert.True(File.Exists(Path.Combine(outside, "other.txt")));
        Assert.Equal("in\n", (await web.GetJsonAsync("/api/file?path=alias.txt"))["text"]!.GetValue<string>());

        // Not listed, not searched, not offered to quick open.
        Assert.Equal(["src", "~", "alias.txt", "inside.txt"], (await web.GetJsonAsync("/api/files"))["entries"]!.AsArray().Select(e => e!["name"]!.GetValue<string>()));
        Assert.Equal(0, (await web.GetJsonAsync("/api/search?q=" + Uri.EscapeDataString("keep out")))["count"]!.GetValue<int>());
        Assert.Equal(["alias.txt", "inside.txt"], (await web.GetJsonAsync("/api/search?q=in"))["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()));
        Assert.Equal(["alias.txt", "inside.txt", "src/app.txt", "~/notes.txt"], (await web.GetJsonAsync("/api/files/all"))["files"]!.AsArray().Select(f => f!.GetValue<string>()));
    }

    [Fact]
    public async Task Every_IDE_call_and_the_terminal_socket_take_only_this_runs_key_from_its_own_host_and_page()
    {
        using var h = new Harness(_gateway, _mcp, c => c["terminalShell"] = "/bin/sh");
        var inside = h.Write("inside.txt", "in\n");
        await using var web = await WebRun.StartAsync(h);
        (string Method, string Path, JsonObject? Body)[] calls =
        [
            ("GET", "/api/files", null),
            ("GET", "/api/files/all", null),
            ("GET", "/api/file?path=inside.txt", null),
            ("POST", "/api/file", new() { ["path"] = "inside.txt", ["text"] = "owned" }),
            ("POST", "/api/files/new", new() { ["path"] = "made.txt" }),
            ("POST", "/api/files/rename", new() { ["from"] = "inside.txt", ["to"] = "moved.txt" }),
            ("POST", "/api/files/delete", new() { ["path"] = "inside.txt" }),
            ("GET", "/api/search?q=in", null),
            ("GET", "/api/changes", null),
            ("GET", "/api/changes/diff?path=inside.txt", null),
            ("POST", "/api/changes/accept", new()),
            ("POST", "/api/changes/revert", new() { ["path"] = "inside.txt" }),
            ("GET", "/api/terminals", null),
            ("POST", "/api/terminals", new() { ["cols"] = 80, ["rows"] = 24 }),
            ("POST", "/api/terminals/close", new() { ["id"] = "1" }),
            ("GET", "/api/terminals/socket?id=1", null),
            ("GET", "/api/preferences", null),
            ("POST", "/api/preferences", new() { ["theme"] = "dark" }),
        ];
        using var anonymous = WebRun.Client(web.Port, token: null);
        var wrong = new List<string>();
        foreach (var (method, path, body) in calls)
        {
            async Task Expect(HttpStatusCode status, string what, HttpClient client, Action<HttpRequestMessage>? change = null)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (body is not null)
                {
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                }
                change?.Invoke(request);
                using var response = await client.SendAsync(request);
                if (response.StatusCode != status)
                {
                    wrong.Add($"{method} {path} {what}: {(int)response.StatusCode}, not {(int)status}");
                }
            }
            await Expect(HttpStatusCode.Unauthorized, "without the key", anonymous);
            await Expect(HttpStatusCode.Forbidden, "for another host name", web.Http, r => r.Headers.Host = $"evil.example:{web.Port}");
            await Expect(HttpStatusCode.Forbidden, "from another site", web.Http, r => r.Headers.Add("Origin", "https://evil.example"));
            await Expect(HttpStatusCode.Forbidden, "from another port's page", web.Http, r => r.Headers.Add("Origin", $"http://localhost:{web.Port + 1}"));
            await Expect(HttpStatusCode.Forbidden, "said by the browser to come from elsewhere", web.Http, r => r.Headers.Add("Sec-Fetch-Site", "same-site"));
            if (body is not null)
            {
                await Expect(HttpStatusCode.UnsupportedMediaType, "as a form", web.Http, r => r.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "text/plain"));
            }
        }
        Assert.Empty(wrong);
        // None of it happened.
        Assert.Equal("in\n", File.ReadAllText(inside));
        Assert.Equal(["inside.txt"], Directory.GetFiles(h.Work).Select(Path.GetFileName));
        Assert.Empty((await web.GetJsonAsync("/api/terminals")).AsArray());
        Assert.False(File.Exists(Path.Combine(h.Paths.DataDir, "ide.json")));

        // The terminal's socket, asked for by hand: only with the key, this host name and this page's origin.
        var id = (await Json(await web.PostAsync("/api/terminals", new JsonObject { ["cols"] = 80, ["rows"] = 24 })))["id"]!.GetValue<string>();
        var host = ("Host", $"127.0.0.1:{web.Port}");
        var key = ("Authorization", $"Bearer {web.Token}");
        var origin = ("Origin", $"http://127.0.0.1:{web.Port}");
        var socket = $"/api/terminals/socket?id={id}";
        Assert.Equal(401, await UpgradeAsync(web.Port, socket, host, origin));
        Assert.Equal(401, await UpgradeAsync(web.Port, socket, host, origin, ("Authorization", "Bearer " + new string('x', 43))));
        Assert.Equal(403, await UpgradeAsync(web.Port, socket, ("Host", $"evil.example:{web.Port}"), key, origin));
        Assert.Equal(403, await UpgradeAsync(web.Port, socket, host, key, ("Origin", "https://evil.example")));
        Assert.Equal(403, await UpgradeAsync(web.Port, socket, host, key, ("Origin", "null")));
        Assert.Equal(403, await UpgradeAsync(web.Port, socket, host, key, origin, ("Sec-Fetch-Site", "cross-site")));
        Assert.Equal(101, await UpgradeAsync(web.Port, socket, host, key, origin, ("Sec-Fetch-Site", "same-origin")));
        Assert.Equal(101, await UpgradeAsync(web.Port, socket, ("Host", $"localhost:{web.Port}"), ("Cookie", $"code_arena_{web.Port}={web.Token}"), ("Origin", $"http://localhost:{web.Port}")));
    }

    [Fact]
    public async Task A_terminals_shell_gets_no_API_key_and_starts_with_every_signal_at_its_default()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp, c => c["terminalShell"] = "/bin/sh");
        await using var web = await WebRun.StartAsync(h);
        string id;
        // code-arena's own environment may hold the key (ARENA_API_KEY): the shell's does not.
        Environment.SetEnvironmentVariable("ARENA_API_KEY", "sk-not-for-the-shell");
        try
        {
            id = (await Json(await web.PostAsync("/api/terminals", new JsonObject { ["cols"] = 80, ["rows"] = 24 })))["id"]!.GetValue<string>();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARENA_API_KEY", null);
        }
        using var terminal = await TerminalSocket.ConnectAsync(web, id);
        await terminal.TypeAsync("echo \"key=[${ARENA_API_KEY-none}]\"\r");
        await terminal.UntilAsync(@"key=\[none\]\r\n");
        if (OperatingSystem.IsLinux())
        {
            // What the runtime ignores (SIGPIPE) or blocks is not passed on to the programs the shell runs:
            // none blocked, none of 1 to 31 ignored (32 and 33 are the C library's own, which it sets up itself).
            await terminal.TypeAsync("grep -E '^Sig(Blk|Ign)' /proc/self/status\r");
            await terminal.UntilAsync(DefaultSignals);
        }
        await terminal.TypeAsync("exit\r");
        Assert.Equal(0, await terminal.ExitAsync());
        Assert.DoesNotContain("sk-not-for-the-shell", terminal.Text);
    }

    [Fact]
    public async Task Search_finds_text_across_the_files_with_case_whole_words_patterns_and_globs()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write("src/app.ts", "const Value = 1;\nlet value2 = value;\n");
        h.Write("docs/guide.md", "The value of things.\n");
        File.WriteAllBytes(Path.Combine(h.Work, "data.bin"), [(byte)'v', (byte)'a', (byte)'l', (byte)'u', (byte)'e', 0]);
        await using var web = await WebRun.StartAsync(h);

        static string[] Hits(JsonNode r) =>
            [.. r["files"]!.AsArray().SelectMany(f => f!["matches"]!.AsArray().Select(m => $"{f["path"]}:{m!["line"]}:{m["column"]}"))];

        var all = await web.GetJsonAsync("/api/search?q=value");
        Assert.Equal(["docs/guide.md:1:5", "src/app.ts:1:7", "src/app.ts:2:5", "src/app.ts:2:14"], Hits(all));
        Assert.Equal(4, all["count"]!.GetValue<int>());
        Assert.False(all["truncated"]!.GetValue<bool>());
        var first = all["files"]![1]!["matches"]![0]!;
        Assert.Equal("const Value = 1;", first["preview"]!.GetValue<string>());
        Assert.Equal(6, first["start"]!.GetValue<int>());
        Assert.Equal(5, first["length"]!.GetValue<int>());

        Assert.Equal(["docs/guide.md:1:5", "src/app.ts:2:5", "src/app.ts:2:14"], Hits(await web.GetJsonAsync("/api/search?q=value&case=1")));
        Assert.Equal(["docs/guide.md:1:5", "src/app.ts:1:7", "src/app.ts:2:14"], Hits(await web.GetJsonAsync("/api/search?q=value&word=1")));
        Assert.Equal(["src/app.ts:2:5"], Hits(await web.GetJsonAsync("/api/search?q=" + Uri.EscapeDataString(@"val(ue)?\d") + "&regex=1")));
        Assert.Equal(["docs/guide.md:1:5"], Hits(await web.GetJsonAsync("/api/search?q=value&include=" + Uri.EscapeDataString("*.md"))));
        Assert.Equal(["src/app.ts:1:7", "src/app.ts:2:5", "src/app.ts:2:14"], Hits(await web.GetJsonAsync("/api/search?q=value&exclude=docs")));
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.GetAsync("/api/search?q=(&regex=1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.GetAsync("/api/search?q=")).StatusCode);
        // A glob not valid (as one is while it is typed) is said back, not a failure of the server.
        foreach (var glob in new[] { "src/{a,b", "[]", "[z-a]" })
        {
            var refused = await web.Http.GetAsync("/api/search?q=value&include=" + Uri.EscapeDataString(glob));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("is not a valid glob", await refused.Content.ReadAsStringAsync());
        }
        Assert.DoesNotContain("The web interface", web.Err);
        // Quick open: every file, the binary one too.
        Assert.Equal(["data.bin", "docs/guide.md", "src/app.ts"], (await web.GetJsonAsync("/api/files/all"))["files"]!.AsArray().Select(f => f!.GetValue<string>()));

        if (OperatingSystem.IsWindows())
        {
            return;
        }
        // A file that cannot be read is passed over, and a named pipe is neither opened nor waited on.
        var locked = Special.Locked(h.Write("locked.md", "value\n"));
        Special.Pipe(Path.Combine(h.Work, "pipe"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var search = await web.Http.GetAsync("/api/search?q=value", deadline.Token);
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        Assert.Equal(locked ? ["docs/guide.md", "src/app.ts"] : ["docs/guide.md", "locked.md", "src/app.ts"],
            JsonNode.Parse(await search.Content.ReadAsStringAsync(deadline.Token))!["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()));
        var pipe = await web.Http.GetAsync("/api/file?path=pipe", deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, pipe.StatusCode);
        Assert.Contains("not a regular file", await pipe.Content.ReadAsStringAsync(deadline.Token));
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/file", new JsonObject { ["path"] = "pipe", ["text"] = "x" })).StatusCode);
    }

    [Fact]
    public async Task A_name_is_taken_exactly_as_it_is_spaces_and_backslashes_included()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // neither is part of a name there
        }
        using var h = new Harness(_gateway, _mcp);
        h.Write("notes", "plain\n");
        h.Write("notes ", "spaced\n");
        h.Write("a/b", "nested\n");
        File.WriteAllText(Path.Combine(h.Work, @"a\b"), "backslash\n");
        await using var web = await WebRun.StartAsync(h);

        Assert.Equal(["a=a", @"a\b=a\b", "notes=notes", "notes =notes "],
            (await web.GetJsonAsync("/api/files"))["entries"]!.AsArray().Select(e => $"{e!["name"]}={e["path"]}"));
        Assert.Equal("spaced\n", (await web.GetJsonAsync("/api/file?path=" + Uri.EscapeDataString("notes ")))["text"]!.GetValue<string>());
        Assert.Equal("backslash\n", (await web.GetJsonAsync("/api/file?path=" + Uri.EscapeDataString(@"a\b")))["text"]!.GetValue<string>());
        Assert.Equal(["a/b", @"a\b", "notes", "notes "], (await web.GetJsonAsync("/api/files/all"))["files"]!.AsArray().Select(f => f!.GetValue<string>()));

        // Saved and deleted: that file, not the one with the name it would be trimmed or split to.
        await Json(await web.PostAsync("/api/file", new JsonObject { ["path"] = "notes ", ["text"] = "spaced again\n" }));
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/files/delete", new JsonObject { ["path"] = @"a\b" })).StatusCode);
        Assert.Equal("plain\n", File.ReadAllText(Path.Combine(h.Work, "notes")));
        Assert.Equal("spaced again\n", File.ReadAllText(Path.Combine(h.Work, "notes ")));
        Assert.Equal("nested\n", File.ReadAllText(Path.Combine(h.Work, "a", "b")));
        Assert.False(File.Exists(Path.Combine(h.Work, @"a\b")));
    }

    [Fact]
    public async Task Closing_a_terminal_ends_its_jobs_in_every_group_even_those_deaf_to_SIGHUP()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // /proc says whether they ended
        }
        using var h = new Harness(_gateway, _mcp, c => c["terminalShell"] = "/bin/sh");
        await using var web = await WebRun.StartAsync(h);
        var id = (await Json(await web.PostAsync("/api/terminals", new JsonObject { ["cols"] = 80, ["rows"] = 24 })))["id"]!.GetValue<string>();
        var pids = new List<int>();
        try
        {
            using var terminal = await TerminalSocket.ConnectAsync(web, id);
            // A job in the background and one in the foreground, each in a process group of its own, both ignoring SIGHUP
            // (as a server that reloads on it does).
            await terminal.TypeAsync("sh -c 'trap \"\" HUP; echo back=$$; exec sleep 600' &\r");
            await terminal.UntilAsync(@"back=\d+\r\n");
            await terminal.TypeAsync("sh -c 'trap \"\" HUP; echo front=$$; exec sleep 600'\r");
            await terminal.UntilAsync(@"front=\d+\r\n");
            pids.AddRange(new[] { "back", "front" }.Select(job => int.Parse(Regex.Match(terminal.Text, job + @"=(\d+)").Groups[1].Value)));
            Assert.All(pids, pid => Assert.False(Ended(pid)));

            Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/terminals/close", new JsonObject { ["id"] = id })).StatusCode);
            // SIGHUP, then SIGKILL 3 s later to what is left of them.
            for (var waited = 0; waited < 15_000 && !pids.All(Ended); waited += 100)
            {
                await Task.Delay(100);
            }
            Assert.All(pids, pid => Assert.True(Ended(pid), $"{pid} still runs"));
        }
        finally
        {
            foreach (var pid in pids.Where(p => !Ended(p)))
            {
                System.Diagnostics.Process.GetProcessById(pid).Kill();
            }
        }
    }

    /// <summary>Gone, or a zombie no one has reaped yet (with no init in a container, no one may).</summary>
    private static bool Ended(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[stat.LastIndexOf(')') + 2] is 'Z' or 'X';
        }
        catch (IOException)
        {
            return true;
        }
    }

    [Fact]
    public async Task The_pages_layout_and_theme_are_kept_in_the_data_folder_for_the_next_run_whatever_its_port()
    {
        using var h = new Harness(_gateway, _mcp);
        var file = Path.Combine(h.Paths.DataDir, "ide.json");
        await using (var web = await WebRun.StartAsync(h))
        {
            Assert.Empty((await web.GetJsonAsync("/api/preferences")).AsObject());
            // Each key the page sends goes over what was there.
            await Json(await web.PostAsync("/api/preferences", new JsonObject { ["layout"] = new JsonObject { ["chat"] = 520, ["panelOpen"] = true } }));
            var both = await Json(await web.PostAsync("/api/preferences", new JsonObject { ["theme"] = "dark" }));
            Assert.Equal(520, both["layout"]!["chat"]!.GetValue<int>());
            Assert.Equal("dark", both["theme"]!.GetValue<string>());
            // 16 KB at most: more is refused, and what was there stays.
            var big = await web.PostAsync("/api/preferences", new JsonObject { ["notes"] = new string('x', 20_000) });
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        }
        Assert.True(File.Exists(file));
        Assert.Empty(Directory.GetFiles(h.Paths.DataDir, "*.tmp"));

        // The next run (a new port, so a new page storage in the browser) gives them back.
        await using (var again = await WebRun.StartAsync(h))
        {
            var kept = await again.GetJsonAsync("/api/preferences");
            Assert.Equal(520, kept["layout"]!["chat"]!.GetValue<int>());
            Assert.True(kept["layout"]!["panelOpen"]!.GetValue<bool>());
            Assert.Equal("dark", kept["theme"]!.GetValue<string>());
            Assert.False(kept.AsObject().ContainsKey("notes"));
            // null forgets one.
            Assert.False((await Json(await again.PostAsync("/api/preferences", new JsonObject { ["theme"] = null }))).AsObject().ContainsKey("theme"));
        }

        // A file that is not JSON reads as none, and the next save replaces it.
        File.WriteAllText(file, "{ not json");
        await using var third = await WebRun.StartAsync(h);
        Assert.Empty((await third.GetJsonAsync("/api/preferences")).AsObject());
        await Json(await third.PostAsync("/api/preferences", new JsonObject { ["theme"] = "light" }));
        Assert.Equal("light", JsonNode.Parse(File.ReadAllText(file))!["theme"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_agents_edits_are_listed_diffed_and_accepted_or_reverted_file_by_file()
    {
        using var h = new Harness(_gateway, _mcp);
        var a = h.Write("a.txt", "one\ntwo\n");
        _gateway.Answer = req => FakeGateway.Last(req) switch
        {
            "edit it" => Reply.Call(("edit_file", """{"path":"a.txt","old_string":"two","new_string":"2"}""")),
            "make one" => Reply.Call(("write_file", """{"path":"src/new.txt","content":"fresh\n"}""")),
            "edit again" => Reply.Call(("edit_file", """{"path":"a.txt","old_string":"one","new_string":"1"}""")),
            _ => Reply.Say("?"),
        };
        await using var web = await WebRun.StartAsync(h, "--mode", "auto-edit");
        Assert.Empty((await web.GetJsonAsync("/api/changes")).AsArray());

        await (await web.SendAsync("edit it")).RestAsync();
        await (await web.SendAsync("make one")).RestAsync();
        var changes = (await web.GetJsonAsync("/api/changes")).AsArray();
        Assert.Equal(["a.txt +1 -1", "src/new.txt +1 -0 new"], changes.Select(c => $"{c!["path"]} +{c["added"]} -{c["removed"]}{(c["created"]!.GetValue<bool>() ? " new" : "")}"));

        // Before the agent, and now: what the diff editor shows.
        var diff = await web.GetJsonAsync("/api/changes/diff?path=a.txt");
        Assert.Equal("one\ntwo\n", diff["original"]!.GetValue<string>());
        Assert.Equal("one\n2\n", diff["modified"]!.GetValue<string>());
        Assert.Null((await web.GetJsonAsync("/api/changes/diff?path=src/new.txt"))["original"]);

        // Revert puts the file back; a file the agent made goes.
        var left = await Json(await web.PostAsync("/api/changes/revert", new JsonObject { ["path"] = "a.txt" }));
        Assert.Equal("one\ntwo\n", File.ReadAllText(a));
        Assert.Equal(["src/new.txt"], left.AsArray().Select(c => c!["path"]!.GetValue<string>()));
        await Json(await web.PostAsync("/api/changes/revert", new JsonObject { ["path"] = "src/new.txt" }));
        Assert.False(File.Exists(Path.Combine(h.Work, "src", "new.txt")));
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.GetAsync("/api/changes/diff?path=a.txt")).StatusCode);

        // Accept keeps the change and forgets the old text.
        await (await web.SendAsync("edit again")).RestAsync();
        Assert.Single((await web.GetJsonAsync("/api/changes")).AsArray());
        Assert.Empty((await Json(await web.PostAsync("/api/changes/accept", new JsonObject { ["path"] = "a.txt" }))).AsArray());
        Assert.Equal("1\ntwo\n", File.ReadAllText(a));
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/changes/revert", new JsonObject { ["path"] = "a.txt" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync("/api/changes/diff?path=../secret.txt")).StatusCode);
    }

    [Fact]
    public async Task A_terminal_runs_a_real_shell_on_a_pseudo_terminal_over_the_servers_socket()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // ConPTY: not on this test runner.
        }
        using var h = new Harness(_gateway, _mcp, c => c["terminalShell"] = "/bin/sh");
        await using var web = await WebRun.StartAsync(h);

        var opened = await Json(await web.PostAsync("/api/terminals", new JsonObject { ["cols"] = 80, ["rows"] = 24 }));
        var id = opened["id"]!.GetValue<string>();
        Assert.Equal("sh", opened["title"]!.GetValue<string>());
        Assert.Equal(id, Assert.Single((await web.GetJsonAsync("/api/terminals")).AsArray())!["id"]!.GetValue<string>());

        using (var terminal = await TerminalSocket.ConnectAsync(web, id))
        {
            await terminal.TypeAsync("echo hi\r");
            // The tty echoes the line typed ("echo hi"); the shell's output is "hi" on a line of its own.
            await terminal.UntilAsync(@"(?<!echo )hi\r\n");
            await terminal.TypeAsync("pwd; echo \"$TERM $TERM_PROGRAM\"\r");
            await terminal.UntilAsync(Regex.Escape(h.Work) + @"\r\nxterm-256color code-arena\r\n");
            // Its controlling terminal (job control, Ctrl+C): /dev/tty opens only with one.
            await terminal.TypeAsync("(exec 3</dev/tty && echo has-a-tty)\r");
            await terminal.UntilAsync(@"(?<!echo )has-a-tty\r\n");

            await terminal.ResizeAsync(100, 40);
            await terminal.TypeAsync("stty size\r");
            await terminal.UntilAsync(@"40 100\r\n");
            Assert.Equal(100, (await web.GetJsonAsync("/api/terminals"))[0]!["cols"]!.GetValue<int>());

            await terminal.TypeAsync("exit 3\r");
            Assert.Equal(3, await terminal.ExitAsync());
        }
        Assert.Equal(3, (await web.GetJsonAsync("/api/terminals"))[0]!["exitCode"]!.GetValue<int>());

        // A page that comes back sees what was on the screen, then that it ended.
        using (var again = await TerminalSocket.ConnectAsync(web, id))
        {
            Assert.Equal(3, await again.ExitAsync());
            Assert.Contains("40 100", again.Text);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/terminals/close", new JsonObject { ["id"] = id })).StatusCode);
        Assert.Empty((await web.GetJsonAsync("/api/terminals")).AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/terminals/close", new JsonObject { ["id"] = id })).StatusCode);
    }

    [Fact]
    public async Task A_terminal_closed_from_the_page_ends_its_shell_and_its_socket_takes_only_this_runs_page()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway, _mcp, c => c["terminalShell"] = "/bin/sh");
        await using var web = await WebRun.StartAsync(h);
        var id = (await Json(await web.PostAsync("/api/terminals", new JsonObject { ["cols"] = 80, ["rows"] = 24 })))["id"]!.GetValue<string>();

        // No key, another site's page, or a terminal that is not there: no socket.
        await Assert.ThrowsAsync<WebSocketException>(() => TerminalSocket.ConnectAsync(web, id, token: null));
        await Assert.ThrowsAsync<WebSocketException>(() => TerminalSocket.ConnectAsync(web, id, origin: "https://evil.example"));
        await Assert.ThrowsAsync<WebSocketException>(() => TerminalSocket.ConnectAsync(web, "99"));
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.GetAsync($"/api/terminals/socket?id={id}")).StatusCode);

        using var terminal = await TerminalSocket.ConnectAsync(web, id);
        await terminal.TypeAsync("sleep 600\r");
        await Task.Delay(300);
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/terminals/close", new JsonObject { ["id"] = id })).StatusCode);
        // SIGHUP to the shell's group: the shell and its sleep end.
        Assert.Equal(128 + 1, await terminal.ExitAsync());
    }

    [Fact]
    public async Task On_macOS_a_terminal_starts_through_code_arenas_own_helper_which_takes_its_terminal_and_folder()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        // macOS's way, run here: code-arena --pty-helper opens the terminal in its new session, then becomes the shell.
        IReadOnlyList<string> helper = Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet" ? PtyHelper.Command() : [Path.Combine(AppContext.BaseDirectory, "code-arena")];
        var dir = Directory.CreateTempSubdirectory("code-arena-pty-").FullName;
        try
        {
            var env = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["TERM"] = "xterm-256color" };
            using var pty = Pty.Start("/bin/sh", [], dir, env, 80, 24, helper);
            var output = new StringBuilder();
            var reading = Task.Run(() =>
            {
                var buffer = new byte[4096];
                int n;
                while ((n = pty.Read(buffer)) > 0)
                {
                    lock (output)
                    {
                        output.Append(Encoding.UTF8.GetString(buffer, 0, n));
                    }
                }
            });
            // Its folder, its controlling terminal, a session it leads, and signals at their defaults (where /proc says).
            var checks = OperatingSystem.IsLinux() ? "[ \"$(cut -d' ' -f6 /proc/$$/stat)\" = $$ ] && echo leads-its-session; grep -E '^Sig(Blk|Ign)' /proc/self/status; " : "";
            pty.Write(Encoding.UTF8.GetBytes($"pwd; (exec 3</dev/tty && echo has-a-tty); {checks}exit 7\r"));
            Assert.Equal(7, await Task.Run(pty.WaitForExit).WaitAsync(TimeSpan.FromSeconds(30)));
            await Task.Delay(200);
            pty.Stop();
            await reading.WaitAsync(TimeSpan.FromSeconds(5));
            string text;
            lock (output)
            {
                text = output.ToString();
            }
            Assert.Matches(Regex.Escape(dir) + @"\r\n", text);
            Assert.Matches(@"(?<!echo )has-a-tty\r\n", text);
            if (OperatingSystem.IsLinux())
            {
                Assert.Matches(@"(?<!echo )leads-its-session\r\n", text);
                Assert.True(Regex.IsMatch(text, DefaultSignals), text);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A WebSocket handshake written by hand, with exactly these headers besides the upgrade's own: the answer's status.</summary>
    private static async Task<int> UpgradeAsync(int port, string path, params (string Name, string Value)[] headers)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        var head = new StringBuilder($"GET {path} HTTP/1.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\n");
        head.Append($"Sec-WebSocket-Key: {Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))}\r\n");
        foreach (var (name, value) in headers)
        {
            head.Append($"{name}: {value}\r\n");
        }
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.Append("\r\n").ToString()));
        // "HTTP/1.1 101 ..."
        var status = new byte[12];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.ReadExactlyAsync(status, timeout.Token);
        return int.Parse(Encoding.ASCII.GetString(status, 9, 3), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    /// <summary>A terminal's WebSocket as the page opens it: its output gathered as text, keys and sizes sent as the page sends them.</summary>
    private sealed class TerminalSocket : IDisposable
    {
        private readonly ClientWebSocket _socket;
        private readonly StringBuilder _text = new();
        private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
        private readonly Task _reading;
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TerminalSocket(ClientWebSocket socket)
        {
            _socket = socket;
            _reading = Task.Run(ReadAsync);
        }

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public static async Task<TerminalSocket> ConnectAsync(WebRun web, string id, string? token = "", string? origin = null)
        {
            var socket = new ClientWebSocket();
            if (token is not null)
            {
                socket.Options.SetRequestHeader("Authorization", "Bearer " + (token.Length > 0 ? token : web.Token));
            }
            socket.Options.SetRequestHeader("Origin", origin ?? $"http://127.0.0.1:{web.Port}");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{web.Port}/api/terminals/socket?id={id}"), timeout.Token);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
            return new TerminalSocket(socket);
        }

        private async Task ReadAsync()
        {
            var buffer = new byte[8192];
            try
            {
                while (true)
                {
                    var r = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    if (r.MessageType == WebSocketMessageType.Text)
                    {
                        var e = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count))!;
                        if (e["type"]!.GetValue<string>() == "exit")
                        {
                            _exit.TrySetResult(e["code"]!.GetValue<int>());
                        }
                        continue;
                    }
                    var chars = new char[_utf8.GetCharCount(buffer, 0, r.Count)];
                    _utf8.GetChars(buffer, 0, r.Count, chars, 0);
                    lock (_text)
                    {
                        _text.Append(chars);
                    }
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Aborted at the end of the test.
            }
            _exit.TrySetException(new Xunit.Sdk.XunitException($"The socket closed without an exit: {Text}"));
        }

        public Task TypeAsync(string keys) => SendAsync(new JsonObject { ["type"] = "input", ["data"] = keys });

        public Task ResizeAsync(int cols, int rows) => SendAsync(new JsonObject { ["type"] = "resize", ["cols"] = cols, ["rows"] = rows });

        private Task SendAsync(JsonObject message) =>
            _socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

        /// <summary>Waits until the output matches.</summary>
        public async Task UntilAsync(string pattern)
        {
            for (var waited = 0; waited < 15_000; waited += 50)
            {
                if (Regex.IsMatch(Text, pattern))
                {
                    return;
                }
                await Task.Delay(50);
            }
            throw new Xunit.Sdk.XunitException($"The terminal never showed /{pattern}/: {Text}");
        }

        public Task<int> ExitAsync() => _exit.Task.WaitAsync(TimeSpan.FromSeconds(15));

        public void Dispose()
        {
            _socket.Abort();
            _socket.Dispose();
            Task.WhenAny(_reading, Task.Delay(TimeSpan.FromSeconds(5))).Wait();
        }
    }
}
