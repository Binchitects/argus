using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// Language servers relayed to a page: a fake server (a shell script that echoes what it gets, and keeps a copy) named in
/// config.json's "languageServers", the file URIs rewritten both ways, initialize given what the server decides, the
/// refusals, the log, and a server stopped with its page. Where servers are found, and how URIs map, without one.
/// </summary>
public sealed class LanguageServerTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("code-arena-lsp-").FullName;

    /// <summary>An executable script.</summary>
    private string Script(string path, string body)
    {
        var full = Path.Combine(_dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return full;
    }

    /// <summary>
    /// The fake server: it says its process id and environment on stderr, then echoes every message it gets (so the page
    /// gets back what the server got, as the page's again), and keeps a copy of them in the file $RECEIVED.
    /// </summary>
    private string Echo() => Script("bin/echo-server", "echo \"pid=$$ key=[${ARENA_API_KEY-none}] goproxy=[${GOPROXY-none}] own=[${OWN-none}] cwd=[$(pwd)]\" >&2\nexec tee \"$RECEIVED\"");

    /// <summary>A harness whose config.json names these language servers (the sandbox off: the fake writes its copy outside the folder).</summary>
    private Harness Fake(JsonObject servers) => new(_gateway, _mcp, c =>
    {
        c["languageServers"] = servers;
        c["sandbox"] = "off";
    });

    private JsonObject Server(string received, JsonObject? more = null)
    {
        var o = new JsonObject { ["command"] = new JsonArray(Echo()), ["languages"] = new JsonArray("plaintext"), ["env"] = new JsonObject { ["RECEIVED"] = received, ["OWN"] = "yes" } };
        foreach (var (k, v) in more ?? [])
        {
            o[k] = v?.DeepClone();
        }
        return o;
    }

    /// <summary>The messages the fake got, as it got them (framed): those whole so far.</summary>
    private static async Task<List<JsonObject>> ReceivedAsync(string file)
    {
        var all = new List<JsonObject>();
        if (!File.Exists(file))
        {
            return all;
        }
        var reader = new FrameReader(new MemoryStream(await File.ReadAllBytesAsync(file)), 64 * 1024 * 1024);
        try
        {
            while (await reader.ReadAsync(default) is { } m)
            {
                all.Add(m);
            }
        }
        catch (FramingException)
        {
            // The last one is still being written.
        }
        return all;
    }

    private static async Task<List<JsonObject>> ReceivedAsync(string file, int count)
    {
        for (var waited = 0; waited < 20_000; waited += 20)
        {
            if (await ReceivedAsync(file) is { } all && all.Count >= count)
            {
                return all;
            }
            await Task.Delay(20);
        }
        throw new Xunit.Sdk.XunitException($"The server got {(await ReceivedAsync(file)).Count} messages, not {count}.");
    }

    [Fact]
    public async Task Initialize_is_given_the_folder_and_the_options_and_every_file_URI_is_rewritten_both_ways()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var received = Path.Combine(_dir, "received.lsp");
        using var h = Fake(new JsonObject
        {
            // A built-in server, given another program: the catalog's options, with config.json's over them.
            ["typescript"] = Server(received, new JsonObject { ["initializationOptions"] = new JsonObject { ["preferences"] = new JsonObject { ["quotePreference"] = "single" } } }),
        });
        // The folder is reached through a link: the server's URIs of where it leads are the folder's too.
        var link = Path.Combine(h.Root, "linked");
        Directory.CreateSymbolicLink(link, h.Work);
        h.Cwd = link;
        h.Write("node_modules/typescript/lib/tsserver.js", "");
        await using var web = await WebRun.StartAsync(h);
        var root = (await web.GetJsonAsync("/api/state"))["folder"]!.GetValue<string>();
        Assert.Equal(link, root);
        await DebugSessionTests.TrustAsync(web);

        using var page = await LspSocket.ConnectAsync(web, "typescript");
        // What the page says of the folder, the process and the options is not taken.
        await page.SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["processId"] = 4242, ["rootUri"] = "file:///elsewhere", ["rootPath"] = "/elsewhere", ["capabilities"] = new JsonObject(),
                ["initializationOptions"] = new JsonObject { ["tsserver"] = new JsonObject { ["path"] = "/tmp/evil.js" } },
            },
        });
        var sent = (await ReceivedAsync(received, 1))[0]["params"]!;
        Assert.Equal(Environment.ProcessId, sent["processId"]!.GetValue<int>());
        Assert.Equal("file://" + root, sent["rootUri"]!.GetValue<string>());
        Assert.Equal(root, sent["rootPath"]!.GetValue<string>());
        Assert.Equal("file://" + root, sent["workspaceFolders"]![0]!["uri"]!.GetValue<string>());
        Assert.Equal("linked", sent["workspaceFolders"]![0]!["name"]!.GetValue<string>());
        var options = sent["initializationOptions"]!;
        Assert.True(options["disableAutomaticTypingAcquisition"]!.GetValue<bool>());
        Assert.Equal(Path.Combine(root, "node_modules", "typescript", "lib", "tsserver.js"), options["tsserver"]!["path"]!.GetValue<string>());
        Assert.Equal("single", options["preferences"]!["quotePreference"]!.GetValue<string>());
        // Echoed: the folder is the page's file:/// again.
        var echoed = await page.UntilAsync(m => m["method"]?.GetValue<string>() == "initialize", "initialize echoed");
        Assert.Equal("file:///", echoed["params"]!["rootUri"]!.GetValue<string>());
        Assert.Equal("file:///", echoed["params"]!["workspaceFolders"]![0]!["uri"]!.GetValue<string>());

        // Every whole file URI, and the keys that are one; never a document's text or an edit's.
        var uris = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["method"] = "test/uris",
            ["params"] = new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = "file:///src/a%20b%C3%A9.ts", ["text"] = "file:///kept/as/is" },
                ["library"] = "outside:///usr/include/stdio.h",
                ["real"] = "outside://" + LspUris.FileUri(Path.Combine(h.Work, "src", "real.ts"))["file://".Length..],
                ["changes"] = new JsonObject { ["file:///src/x.ts"] = new JsonArray(new JsonObject { ["newText"] = "file:///kept/too" }) },
                ["list"] = new JsonArray("file:///README.md", "not a uri"),
                ["escape"] = "file:///../secret.txt",
            },
        };
        await page.SendAsync(uris);
        var got = (await ReceivedAsync(received, 2))[1]["params"]!;
        Assert.Equal($"file://{root}/src/a%20b%C3%A9.ts", got["textDocument"]!["uri"]!.GetValue<string>());
        Assert.Equal("file:///kept/as/is", got["textDocument"]!["text"]!.GetValue<string>());
        Assert.Equal("file:///usr/include/stdio.h", got["library"]!.GetValue<string>());
        Assert.Equal([$"file://{root}/src/x.ts"], got["changes"]!.AsObject().Select(c => c.Key));
        Assert.Equal("file:///kept/too", got["changes"]![$"file://{root}/src/x.ts"]![0]!["newText"]!.GetValue<string>());
        Assert.Equal($"file://{root}/README.md", got["list"]![0]!.GetValue<string>());
        Assert.Equal("not a uri", got["list"]![1]!.GetValue<string>());
        // A path of the page's that would leave the folder is not made one of the server's.
        Assert.Equal("file:///../secret.txt", got["escape"]!.GetValue<string>());

        var back = (await page.UntilAsync(m => m["method"]?.GetValue<string>() == "test/uris", "the URIs echoed"))["params"]!;
        Assert.Equal("file:///src/a%20b%C3%A9.ts", back["textDocument"]!["uri"]!.GetValue<string>());
        Assert.Equal("file:///kept/as/is", back["textDocument"]!["text"]!.GetValue<string>());
        Assert.Equal("outside:///usr/include/stdio.h", back["library"]!.GetValue<string>());
        // Where the link leads is the folder too.
        Assert.Equal("file:///src/real.ts", back["real"]!.GetValue<string>());
        Assert.Equal(["file:///src/x.ts"], back["changes"]!.AsObject().Select(c => c.Key));
        Assert.Equal("file:///README.md", back["list"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task A_server_runs_only_in_a_trusted_folder_one_page_at_a_time_six_at_most()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var servers = new JsonObject();
        for (var i = 1; i <= 7; i++)
        {
            servers[$"fake{i}"] = Server(Path.Combine(_dir, $"received{i}.lsp"));
        }
        using var h = Fake(servers);
        h.Env["PATH"] = Path.Combine(_dir, "empty");
        await using var web = await WebRun.StartAsync(h);
        var host = ("Host", $"127.0.0.1:{web.Port}");
        var key = ("Authorization", $"Bearer {web.Token}");
        var origin = ("Origin", $"http://127.0.0.1:{web.Port}");

        // Listed: config.json's own first, then the built-in ones, missing here, with how to get them.
        var listed = (await web.GetJsonAsync("/api/lsp")).AsArray();
        Assert.Equal(["fake1", "fake2", "fake3", "fake4", "fake5", "fake6", "fake7"], listed.Take(7).Select(s => s!["id"]!.GetValue<string>()));
        Assert.True(listed[0]!["found"]!.GetValue<bool>());
        Assert.Equal(Path.Combine(_dir, "bin", "echo-server"), listed[0]!["path"]!.GetValue<string>());
        Assert.Equal(["plaintext"], listed[0]!["languages"]!.AsArray().Select(l => l!.GetValue<string>()));
        var ts = listed.Single(s => s!["id"]!.GetValue<string>() == "typescript")!;
        Assert.False(ts["found"]!.GetValue<bool>());
        Assert.Equal(["typescript", "javascript"], ts["languages"]!.AsArray().Select(l => l!.GetValue<string>()));
        Assert.Contains("npm install -g typescript-language-server", ts["install"]!.GetValue<string>());
        foreach (var id in new[] { "pyright", "basedpyright", "pylsp", "gopls", "rust-analyzer", "clangd", "csharp-ls", "json", "css", "html", "yaml", "bash" })
        {
            Assert.Contains(listed, s => s!["id"]!.GetValue<string>() == id);
        }

        // Not until the folder is trusted: nothing starts.
        Assert.Equal(403, await UpgradeAsync(web.Port, "/api/lsp/socket?server=fake1", host, key, origin));
        Assert.False(File.Exists(Path.Combine(_dir, "received1.lsp")));
        await DebugSessionTests.TrustAsync(web);
        Assert.Equal(404, await UpgradeAsync(web.Port, "/api/lsp/socket?server=nothing", host, key, origin));
        Assert.Equal(404, await UpgradeAsync(web.Port, "/api/lsp/socket?server=pyright", host, key, origin));
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.GetAsync("/api/lsp/socket?server=fake1")).StatusCode);

        var pages = new List<LspSocket>();
        try
        {
            for (var i = 1; i <= 6; i++)
            {
                pages.Add(await LspSocket.ConnectAsync(web, $"fake{i}"));
            }
            Assert.Equal(409, await UpgradeAsync(web.Port, "/api/lsp/socket?server=fake7", host, key, origin));
            // The page's WebSocket sees only that it was refused: the log says why.
            Assert.Contains("6 language servers are running", (await web.GetJsonAsync("/api/lsp/log?server=fake7"))["refused"]!.GetValue<string>());
            Assert.Contains("pyright-langserver is not installed", (await web.GetJsonAsync("/api/lsp/log?server=pyright"))["refused"]!.GetValue<string>());

            // The same server again: the page that had it is told, and its server goes.
            var pid = await PidAsync(web, "fake1");
            using var again = await LspSocket.ConnectAsync(web, "fake1");
            var ended = await pages[0].UntilAsync(m => m["method"]?.GetValue<string>() == "codeArena/ended", "the first page told");
            Assert.True(ended["params"]!["takenOver"]!.GetValue<bool>());
            Assert.Contains("Another page", ended["params"]!["reason"]!.GetValue<string>());
            await pages[0].Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await GoneAsync(pid);
            await again.SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "test/hello" });
            await again.UntilAsync(m => m["method"]?.GetValue<string>() == "test/hello", "the new server echoing");

            // Trust taken back: every server stops, and its page is told why.
            using var untrust = await web.PostAsync("/api/workspace/trust", new JsonObject { ["trusted"] = false });
            Assert.Equal(HttpStatusCode.OK, untrust.StatusCode);
            var stopped = await again.UntilAsync(m => m["method"]?.GetValue<string>() == "codeArena/ended", "the page told");
            Assert.Contains("no longer trusted", stopped["params"]!["reason"]!.GetValue<string>());
            await again.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            foreach (var p in pages)
            {
                p.Dispose();
            }
        }
    }

    [Fact]
    public async Task Closing_the_page_stops_the_server_and_everything_it_started_its_log_is_kept_and_it_never_gets_the_key()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var received = Path.Combine(_dir, "received.lsp");
        // config.json's entry for a built-in one: the catalog's environment (GOPROXY=off), with config.json's own.
        using var h = Fake(new JsonObject { ["gopls"] = Server(received) });
        await using var web = await WebRun.StartAsync(h);
        await DebugSessionTests.TrustAsync(web);
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.GetAsync("/api/lsp/log?server=nothing")).StatusCode);
        var none = await web.GetJsonAsync("/api/lsp/log?server=gopls");
        Assert.Empty(none["lines"]!.AsArray());
        Assert.False(none["running"]!.GetValue<bool>());

        Environment.SetEnvironmentVariable("ARENA_API_KEY", "sk-not-for-language-servers");
        int pid;
        try
        {
            using var page = await LspSocket.ConnectAsync(web, "gopls");
            pid = await PidAsync(web, "gopls");
            var log = await web.GetJsonAsync("/api/lsp/log?server=gopls");
            Assert.True(log["running"]!.GetValue<bool>());
            var said = log["lines"]![0]!.GetValue<string>();
            Assert.Contains("key=[none]", said);
            Assert.Contains("goproxy=[off]", said);
            Assert.Contains("own=[yes]", said);
            Assert.Contains($"cwd=[{h.Work}]", said);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARENA_API_KEY", null);
        }

        // The page gone: shutdown, then exit; the fake answers neither, so it is killed 2 s later.
        var clock = Stopwatch.StartNew();
        await GoneAsync(pid);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"It took {clock.Elapsed}.");
        var sent = await ReceivedAsync(received, 2);
        Assert.Equal("shutdown", sent[^2]["method"]!.GetValue<string>());
        Assert.Equal("exit", sent[^1]["method"]!.GetValue<string>());
        for (var waited = 0; (await web.GetJsonAsync("/api/lsp/log?server=gopls"))["running"]!.GetValue<bool>(); waited += 50)
        {
            Assert.True(waited < 10_000, "The server is still said to run.");
            await Task.Delay(50);
        }
        var after = await web.GetJsonAsync("/api/lsp/log?server=gopls");
        Assert.StartsWith($"pid={pid} ", after["lines"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task The_calls_and_the_socket_are_behind_the_key_and_this_page()
    {
        var received = Path.Combine(_dir, "received.lsp");
        using var h = Fake(new JsonObject { ["fake"] = Server(received) });
        await using var web = await WebRun.StartAsync(h);
        await DebugSessionTests.TrustAsync(web);
        using var anonymous = WebRun.Client(web.Port, token: null);
        var wrong = new List<string>();
        foreach (var path in new[] { "/api/lsp", "/api/lsp/log?server=fake", "/api/lsp/socket?server=fake" })
        {
            async Task Expect(HttpStatusCode status, string what, HttpClient client, Action<HttpRequestMessage>? change = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                change?.Invoke(request);
                using var response = await client.SendAsync(request);
                if (response.StatusCode != status)
                {
                    wrong.Add($"{path} {what}: {(int)response.StatusCode}, not {(int)status}");
                }
            }
            await Expect(HttpStatusCode.Unauthorized, "without the key", anonymous);
            await Expect(HttpStatusCode.Forbidden, "from another site", web.Http, r => r.Headers.Add("Origin", "https://evil.example"));
            await Expect(HttpStatusCode.Forbidden, "said by the browser to come from elsewhere", web.Http, r => r.Headers.Add("Sec-Fetch-Site", "cross-site"));
        }
        Assert.Empty(wrong);

        var host = ("Host", $"127.0.0.1:{web.Port}");
        var key = ("Authorization", $"Bearer {web.Token}");
        var origin = ("Origin", $"http://127.0.0.1:{web.Port}");
        const string socket = "/api/lsp/socket?server=fake";
        Assert.Equal(401, await UpgradeAsync(web.Port, socket, host, origin));
        Assert.Equal(403, await UpgradeAsync(web.Port, socket, host, key, ("Origin", "https://evil.example")));
        Assert.Equal(403, await UpgradeAsync(web.Port, socket, host, key, ("Origin", $"http://localhost:{web.Port + 1}")));
        Assert.False(File.Exists(received));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(101, await UpgradeAsync(web.Port, socket, host, key, origin));
        }
    }

    [Fact]
    public async Task Servers_are_found_on_PATHs_full_folders_and_where_they_install_python_ones_in_the_projects_environment_first()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var work = Directory.CreateDirectory(Path.Combine(_dir, "work")).FullName;
        var bin = Path.GetDirectoryName(Script("bin/gopls", "echo 'golang.org/x/tools/gopls v0.16.1'"))!;
        Script("bin/pylsp", "true");
        var venv = Script("work/.venv/bin/pylsp", "true");
        Script("home/.cargo/bin/rust-analyzer", "echo 'rust-analyzer 1.80.0 (05147895 2024-07-21)'");
        Script("bin/clangd-17", "true");
        var clangd = Script("bin/clangd-18", "echo 'Ubuntu clangd version 18.1.3 (1ubuntu1)'");
        Script("bin/yaml-language-server", "true");
        LanguageServers Servers(Config config, string path) =>
            new(new Workspace(work), config, Path.Combine(_dir, "data"), k => k switch { "PATH" => path, "HOME" => Path.Combine(_dir, "home"), _ => null }, () => Sandbox.None, () => true);
        JsonObject Of(JsonArray list, string id) => list.Single(s => s!["id"]!.GetValue<string>() == id)!.AsObject();

        var list = await Servers(new Config(), bin).ListAsync(default);
        Assert.Equal(Path.Combine(bin, "gopls"), Of(list, "gopls")["path"]!.GetValue<string>());
        Assert.Equal("0.16.1", Of(list, "gopls")["version"]!.GetValue<string>());
        Assert.Equal(venv, Of(list, "pylsp")["path"]!.GetValue<string>());
        Assert.Null(Of(list, "pylsp")["version"]);
        Assert.Equal("1.80.0", Of(list, "rust-analyzer")["version"]!.GetValue<string>());
        Assert.Equal(clangd, Of(list, "clangd")["path"]!.GetValue<string>());
        Assert.False(Of(list, "pyright")["found"]!.GetValue<bool>());
        Assert.Contains("pyright-langserver is not installed", Of(list, "pyright")["reason"]!.GetValue<string>());
        // The YAML server is answered, unless config.json says otherwise, that it fetches no schema store.
        Assert.False(Of(list, "yaml")["settings"]!["yaml"]!["schemaStore"]!["enable"]!.GetValue<bool>());
        // A folder in PATH given relatively would be the project's own: not looked in.
        var relative = await Servers(new Config(), Path.GetRelativePath(Environment.CurrentDirectory, bin)).ListAsync(default);
        Assert.False(Of(relative, "gopls")["found"]!.GetValue<bool>());

        var config = new Config();
        config.LanguageServers["gopls"] = LanguageServerConfig.From(new JsonObject { ["disabled"] = true });
        config.LanguageServers["lua"] = LanguageServerConfig.From(new JsonObject { ["command"] = new JsonArray("gopls", "--lua"), ["languages"] = new JsonArray("lua"), ["settings"] = new JsonObject { ["Lua"] = new JsonObject { ["x"] = 1 } } });
        config.LanguageServers["broken"] = LanguageServerConfig.From(new JsonObject { ["command"] = "lua-language-server" });
        config.LanguageServers["nameless"] = LanguageServerConfig.From(new JsonObject { ["languages"] = new JsonArray("java") });
        list = await Servers(config, bin).ListAsync(default);
        Assert.Equal(["lua", "broken", "nameless"], list.Take(3).Select(s => s!["id"]!.GetValue<string>()));
        Assert.Equal(Path.Combine(bin, "gopls"), Of(list, "lua")["path"]!.GetValue<string>());
        Assert.Equal(1, Of(list, "lua")["settings"]!["Lua"]!["x"]!.GetValue<int>());
        Assert.Contains("\"languageServers\".\"broken\"", Of(list, "broken")["reason"]!.GetValue<string>());
        Assert.Contains("no \"command\"", Of(list, "nameless")["reason"]!.GetValue<string>());
        Assert.False(Of(list, "gopls")["found"]!.GetValue<bool>());
        Assert.Contains("Turned off", Of(list, "gopls")["reason"]!.GetValue<string>());
        Assert.Null(Of(list, "gopls")["install"]);

        // config.json keeps the entries as written, broken or not.
        var file = Path.Combine(_dir, "config.json");
        File.WriteAllText(file, """{"languageServers": {"lua": {"command": ["lua-language-server"], "languages": ["lua"]}, "broken": {"command": "x"}}}""");
        var loaded = Config.Load(file);
        Assert.Equal(["lua-language-server"], loaded.LanguageServers["lua"].Command);
        Assert.NotNull(loaded.LanguageServers["broken"].Problem);
        loaded.Save(file);
        var saved = JsonNode.Parse(File.ReadAllText(file))!["languageServers"]!;
        Assert.Equal("x", saved["broken"]!["command"]!.GetValue<string>());
        Assert.Equal("lua", saved["lua"]!["languages"]![0]!.GetValue<string>());
    }

    [Fact]
    public void The_page_and_the_server_name_files_each_their_own_way()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var root = Path.Combine(_dir, "my project");
        var uris = new LspUris(root);
        var server = "file://" + _dir + "/my%20project";
        Assert.Equal(server, LspUris.FileUri(root));
        Assert.Equal(server, uris.ServerUri("file:///"));
        Assert.Equal(server + "/src/caf%C3%A9%23.ts", uris.ServerUri("file:///src/caf%C3%A9%23.ts"));
        Assert.Equal(server + "/src/a.ts#L3", uris.ServerUri("file:///src/a.ts#L3"));
        Assert.Equal("file:///etc/passwd", uris.ServerUri("outside:///etc/passwd"));
        Assert.Equal("untitled:Untitled-1", uris.ServerUri("untitled:Untitled-1"));
        // Nothing of the page's leads out of the folder: left as written, which names no file of the server's.
        Assert.Equal("file:///../x", uris.ServerUri("file:///../x"));
        Assert.Equal("file:////etc/passwd", uris.ServerUri("file:////etc/passwd"));
        Assert.Equal("file:///%2Fetc/passwd", uris.ServerUri("file:///%2Fetc/passwd"));
        Assert.Equal("file:///a%5C..%5C..%5Cx", uris.ServerUri("file:///a%5C..%5C..%5Cx"));

        Assert.Equal("file:///", uris.PageUri(server));
        Assert.Equal("file:///", uris.PageUri(server + "/"));
        Assert.Equal("file:///src/caf%C3%A9%23.ts", uris.PageUri(server + "/src/café%23.ts"));
        Assert.Equal("file:///src/a.ts", uris.PageUri(server + "/lib/../src/a.ts"));
        // Beside the folder, with a name it starts with: outside.
        Assert.Equal("outside://" + _dir + "/my%20project2/x.ts", uris.PageUri(server + "2/x.ts"));
        Assert.Equal("outside:///usr/lib/node_modules/typescript/lib/lib.d.ts", uris.PageUri("file:///usr/lib/node_modules/typescript/lib/lib.d.ts"));
        Assert.Equal("outside://server/share/x.ts", uris.PageUri("file://server/share/x.ts"));
        Assert.Equal("https://example.com/x", uris.PageUri("https://example.com/x"));
    }

    /// <summary>The server's process id, from what it said first.</summary>
    private static async Task<int> PidAsync(WebRun web, string id)
    {
        for (var waited = 0; waited < 10_000; waited += 20)
        {
            var lines = (await web.GetJsonAsync($"/api/lsp/log?server={id}"))["lines"]!.AsArray();
            if (lines.Count > 0 && lines[0]!.GetValue<string>() is var first && first.StartsWith("pid=", StringComparison.Ordinal))
            {
                return int.Parse(first[4..first.IndexOf(' ', StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture);
            }
            await Task.Delay(20);
        }
        throw new Xunit.Sdk.XunitException($"{id} said nothing.");
    }

    private static async Task GoneAsync(int pid)
    {
        for (var waited = 0; waited < 10_000; waited += 20)
        {
            if (!Directory.Exists($"/proc/{pid}") || File.ReadAllText($"/proc/{pid}/stat").Split(' ')[2] == "Z")
            {
                return;
            }
            await Task.Delay(20);
        }
        throw new Xunit.Sdk.XunitException($"Process {pid} is still running.");
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
        var status = new byte[12];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.ReadExactlyAsync(status, timeout.Token);
        return int.Parse(Encoding.ASCII.GetString(status, 9, 3), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A language server's WebSocket as the page opens it: every message kept.</summary>
internal sealed class LspSocket : IDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly List<JsonObject> _seen = [];
    private readonly Task _reading;

    private LspSocket(ClientWebSocket socket)
    {
        _socket = socket;
        _reading = Task.Run(ReadAsync);
    }

    /// <summary>Done when the server closed the socket.</summary>
    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<JsonObject> Seen
    {
        get
        {
            lock (_seen)
            {
                return [.. _seen];
            }
        }
    }

    public static async Task<LspSocket> ConnectAsync(WebRun web, string server)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + web.Token);
        socket.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{web.Port}");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{web.Port}/api/lsp/socket?server={server}"), timeout.Token);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new LspSocket(socket);
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var r = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage)
                {
                    continue;
                }
                var json = JsonNode.Parse(message.ToArray())!.AsObject();
                message.SetLength(0);
                lock (_seen)
                {
                    _seen.Add(json);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        Closed.TrySetResult();
    }

    public Task SendAsync(JsonObject message) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>The first message seen that matches, waiting for it up to 20 s.</summary>
    public async Task<JsonObject> UntilAsync(Func<JsonObject, bool> match, string what)
    {
        for (var waited = 0; waited < 20_000; waited += 20)
        {
            if (Seen.FirstOrDefault(match) is { } m)
            {
                return m;
            }
            await Task.Delay(20);
        }
        throw new Xunit.Sdk.XunitException($"No {what} within 20 s; seen: {string.Join(" | ", Seen.Select(m => Fmt.OneLine(m.ToJsonString(), 160)))}");
    }

    public void Dispose()
    {
        _socket.Abort();
        _socket.Dispose();
        Task.WhenAny(_reading, Task.Delay(TimeSpan.FromSeconds(5))).Wait();
    }
}
