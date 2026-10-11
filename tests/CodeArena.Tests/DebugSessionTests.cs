using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Debug sessions against a fake adapter on a real socket: the relay between pages and the adapter, what a page that comes back sees, and how a session ends.</summary>
public sealed class DebugSessionTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();
    private readonly FakeAdapter _adapter = new();

    private Harness Fake() => new(_gateway, _mcp, c => c["debugAdapters"] = new JsonObject { ["gdb"] = new JsonObject { ["port"] = _adapter.Port } });

    private static readonly JsonObject Launch = new() { ["name"] = "fake", ["type"] = "gdb", ["request"] = "launch", ["program"] = "/bin/true", ["preLaunchTask"] = "build" };

    /// <summary>Trusts the folder (a debug session runs only in one), then starts a session.</summary>
    private static async Task<JsonObject> StartAsync(WebRun web, JsonObject? configuration = null)
    {
        await TrustAsync(web);
        var response = await web.PostAsync("/api/debug/start", new JsonObject { ["configuration"] = (configuration ?? Launch).DeepClone() });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    internal static async Task TrustAsync(WebRun web)
    {
        using var trusted = await web.PostAsync("/api/workspace/trust", new JsonObject { ["trusted"] = true });
        Assert.Equal(HttpStatusCode.OK, trusted.StatusCode);
    }

    [Fact]
    public async Task Each_response_goes_to_the_page_that_asked_with_its_own_number_and_events_go_to_every_page()
    {
        using var h = Fake();
        await using var web = await WebRun.StartAsync(h);
        var started = await StartAsync(web);
        var id = started["id"]!.GetValue<string>();
        Assert.Equal("gdb", started["adapter"]!.GetValue<string>());
        Assert.Equal("gdb", started["adapterId"]!.GetValue<string>());
        // VS Code's own keys are not the adapter's; the folder is the program's when none is given.
        Assert.Null(started["configuration"]!["preLaunchTask"]);
        Assert.Equal(h.Work, started["configuration"]!["cwd"]!.GetValue<string>());
        Assert.Equal(h.Work, started["root"]!.GetValue<string>());

        using var a = await DebugSocket.ConnectAsync(web, id);
        using var b = await DebugSocket.ConnectAsync(web, id);
        Assert.Equal("starting", (await a.EventAsync("codeArena.snapshot"))["body"]!["state"]!.GetValue<string>());
        await b.EventAsync("codeArena.snapshot");

        // Both pages number their first request 1: each gets its own answer, under its own number.
        var initialize = await a.RequestAsync("initialize", new JsonObject { ["adapterID"] = "gdb" });
        var threads = await b.RequestAsync("threads");
        Assert.Equal("initialize", initialize["command"]!.GetValue<string>());
        Assert.Equal(1, initialize["request_seq"]!.GetValue<int>());
        Assert.Equal("threads", threads["command"]!.GetValue<string>());
        Assert.Equal(1, threads["request_seq"]!.GetValue<int>());
        Assert.DoesNotContain(a.Seen, m => DebugSocket.Is(m, "response") && m["command"]!.GetValue<string>() == "threads");
        Assert.DoesNotContain(b.Seen, m => DebugSocket.Is(m, "response") && m["command"]!.GetValue<string>() == "initialize");
        // The adapter saw two numbers, not the pages' one.
        var seqs = _adapter.Received.Select(r => r["seq"]!.GetValue<long>()).ToList();
        Assert.Equal(seqs.Count, seqs.Distinct().Count());

        // Events reach both; the session says where it is.
        await b.EventAsync("initialized");
        await a.SendAsync("launch", Launch.DeepClone().AsObject());
        await a.RequestAsync("configurationDone");
        foreach (var page in new[] { a, b })
        {
            Assert.Equal("breakpoint", (await page.EventAsync("stopped"))["body"]!["reason"]!.GetValue<string>());
            await page.EventAsync("codeArena.state", e => e["body"]!["state"]!.GetValue<string>() == "stopped");
        }
        Assert.Equal("stopped", (await web.GetJsonAsync("/api/debug/sessions"))[0]!["state"]!.GetValue<string>());

        // A continue that succeeds means running, whether or not the adapter says "continued".
        await b.RequestAsync("continue", new JsonObject { ["threadId"] = 1 });
        await a.EventAsync("codeArena.state", e => e["body"]!["state"]!.GetValue<string>() == "running" && a.Seen.IndexOf(e) > a.Seen.FindIndex(m => m["event"]?.GetValue<string>() == "stopped"));

        // Stopped: terminate, the program ends, then disconnect; no waiting for the 3 s.
        var clock = Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/debug/stop", new JsonObject { ["id"] = id })).StatusCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2.5), $"The stop took {clock.Elapsed}.");
        Assert.Equal(["terminate", "disconnect"], _adapter.Commands.SkipWhile(c => c != "terminate"));
        await a.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await b.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(a.Seen, m => m["event"]?.GetValue<string>() == "terminated");
        Assert.Empty((await web.GetJsonAsync("/api/debug/sessions")).AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/debug/stop", new JsonObject { ["id"] = id })).StatusCode);
    }

    [Fact]
    public async Task A_page_that_comes_back_gets_where_the_session_is_and_the_last_output()
    {
        using var h = Fake();
        await using var web = await WebRun.StartAsync(h);
        var id = (await StartAsync(web))["id"]!.GetValue<string>();
        using (var first = await DebugSocket.ConnectAsync(web, id))
        {
            await first.RequestAsync("initialize");
            await first.RequestAsync("configurationDone");
            await first.EventAsync("stopped");
            // 400 KB of output: more than is kept.
            await first.RequestAsync("spam", new JsonObject { ["count"] = 40, ["size"] = 10_000 });
            await first.EventAsync("output", e => e["body"]!["output"]!.GetValue<string>().StartsWith("0039 ", StringComparison.Ordinal));
        }

        using var again = await DebugSocket.ConnectAsync(web, id);
        var snapshot = await again.EventAsync("codeArena.snapshot");
        var body = snapshot["body"]!;
        Assert.Equal(0, again.Seen.IndexOf(snapshot));
        Assert.Equal("stopped", body["state"]!.GetValue<string>());
        Assert.Equal("breakpoint", body["stopped"]!["reason"]!.GetValue<string>());
        Assert.Equal(1, Assert.Single(body["threads"]!.AsArray())!["threadId"]!.GetValue<int>());
        Assert.True(body["capabilities"]!["supportsTerminateRequest"]!.GetValue<bool>());
        Assert.Equal("gdb", body["configuration"]!["type"]!.GetValue<string>());

        await again.EventAsync("output", e => e["body"]!["output"]!.GetValue<string>().StartsWith("0039 ", StringComparison.Ordinal));
        var outputs = again.Seen.Where(m => m["event"]?.GetValue<string>() == "output").ToList();
        var kept = outputs.Sum(m => Encoding.UTF8.GetByteCount(m.ToJsonString()));
        Assert.InRange(kept, 200 * 1024, DebugSession.OutputKept);
        // The newest, in order; the oldest (and the "hello" before them) dropped.
        var numbers = outputs.Select(m => int.Parse(m["body"]!["output"]!.GetValue<string>()[..4], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(Enumerable.Range(40 - numbers.Count, numbers.Count), numbers);
        Assert.True(numbers.Count < 40);
    }

    [Fact]
    public async Task An_adapter_that_dies_without_saying_so_ends_the_session_for_every_page()
    {
        using var h = Fake();
        await using var web = await WebRun.StartAsync(h);
        var id = (await StartAsync(web))["id"]!.GetValue<string>();
        using var page = await DebugSocket.ConnectAsync(web, id);
        await page.RequestAsync("initialize");
        var crash = await page.SendAsync("crash");

        // The question it never answered is answered for it, then the program is said to have ended.
        var failed = await page.UntilAsync(m => DebugSocket.Is(m, "response") && m["request_seq"]!.GetValue<int>() == crash, "the crash's answer");
        Assert.False(failed["success"]!.GetValue<bool>());
        await page.EventAsync("exited");
        await page.EventAsync("terminated");
        var ended = await page.EventAsync("codeArena.state", e => e["body"]!["state"]!.GetValue<string>() == "ended");
        Assert.Contains("before the session did", ended["body"]!["error"]!.GetValue<string>());
        await page.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Still listed, ended; a page that comes now hears how it ended, and is let go.
        Assert.Equal("ended", (await web.GetJsonAsync("/api/debug/sessions"))[0]!["state"]!.GetValue<string>());
        using var late = await DebugSocket.ConnectAsync(web, id);
        await late.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("ended", late.Seen[0]["body"]!["state"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/debug/stop", new JsonObject { ["id"] = id })).StatusCode);
    }

    [Fact]
    public async Task Stop_asks_to_terminate_then_to_disconnect_and_lets_the_adapter_go_after_3_s()
    {
        using var h = Fake();
        _adapter.Ends = false;
        await using var web = await WebRun.StartAsync(h);
        var id = (await StartAsync(web))["id"]!.GetValue<string>();
        using var page = await DebugSocket.ConnectAsync(web, id);
        await page.RequestAsync("initialize");

        var clock = Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/debug/stop", new JsonObject { ["id"] = id })).StatusCode);
        Assert.InRange(clock.Elapsed, DebugSession.StopWait - TimeSpan.FromMilliseconds(200), DebugSession.StopWait + TimeSpan.FromSeconds(3));
        Assert.Equal(["initialize", "terminate", "disconnect"], _adapter.Commands);
        Assert.True(_adapter.Received.Last()["arguments"]!["terminateDebuggee"]!.GetValue<bool>());
        await _adapter.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.EventAsync("terminated");
        Assert.DoesNotContain(page.Seen, m => m["body"]?["error"] is not null);
    }

    [Fact]
    public async Task Stopping_a_session_attached_to_a_program_lets_the_program_go()
    {
        using var h = Fake();
        await using var web = await WebRun.StartAsync(h);
        var id = (await StartAsync(web, new JsonObject { ["name"] = "service", ["type"] = "gdb", ["request"] = "attach", ["pid"] = 4242 }))["id"]!.GetValue<string>();
        using var page = await DebugSocket.ConnectAsync(web, id);
        await page.RequestAsync("initialize");

        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/debug/stop", new JsonObject { ["id"] = id })).StatusCode);
        Assert.Equal(["initialize", "disconnect"], _adapter.Commands);
        Assert.False(_adapter.Received.Last()["arguments"]!["terminateDebuggee"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Stopping_an_adapter_that_never_answers_kills_it_and_all_it_started()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var pidFile = Path.Combine(Path.GetTempPath(), $"code-arena-adapter-{Guid.NewGuid():N}");
        using var h = new Harness(_gateway, _mcp, c => c["debugAdapters"] = new JsonObject
        {
            ["gdb"] = new JsonObject { ["command"] = new JsonArray("/bin/sh", "-c", $"sleep 600 & echo $! > {pidFile}; wait") },
        });
        await using var web = await WebRun.StartAsync(h);
        var id = (await StartAsync(web))["id"]!.GetValue<string>();
        for (var waited = 0; !File.Exists(pidFile) || File.ReadAllText(pidFile).Trim().Length == 0; waited += 50)
        {
            Assert.True(waited < 10_000, "The adapter did not start.");
            await Task.Delay(50);
        }
        var sleeper = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        File.Delete(pidFile);
        Assert.True(StdioChildTests.Alive(sleeper));

        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/debug/stop", new JsonObject { ["id"] = id })).StatusCode);
        for (var waited = 0; StdioChildTests.Alive(sleeper); waited += 50)
        {
            Assert.True(waited < 10_000, "What the adapter started still runs.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task A_file_outside_the_folder_is_read_only_through_a_session_whose_adapter_named_it()
    {
        using var h = Fake();
        var outside = Path.Combine(h.Root, "lib", "util.h");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "int util(void);\n");
        var other = Path.Combine(h.Root, "secret.txt");
        File.WriteAllText(other, "keep out\n");
        h.Write("main.c", "int main(void) { return 0; }\n");
        _adapter.StackPath = outside;
        await using var web = await WebRun.StartAsync(h);
        var id = (await StartAsync(web))["id"]!.GetValue<string>();
        string Source(string path) => $"/api/debug/source?id={id}&path={Uri.EscapeDataString(path)}";

        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync(Source(outside))).StatusCode);
        using var page = await DebugSocket.ConnectAsync(web, id);
        await page.RequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });

        var named = await web.GetJsonAsync(Source(outside));
        Assert.Equal("int util(void);\n", named["text"]!.GetValue<string>());
        Assert.True(named["readOnly"]!.GetValue<bool>());
        Assert.Equal(outside, named["path"]!.GetValue<string>());
        // Not named, or reached by a way round: refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync(Source(other))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync(Source("../secret.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.GetAsync(Source(Path.Combine(h.Work, "..", "secret.txt")))).StatusCode);
        // The folder's own files, as the file calls read them.
        var inside = await web.GetJsonAsync(Source(Path.Combine(h.Work, "main.c")));
        Assert.Equal("main.c", inside["path"]!.GetValue<string>());
        Assert.False(inside["readOnly"]!.GetValue<bool>());
        Assert.Equal("main.c", (await web.GetJsonAsync(Source("main.c")))["path"]!.GetValue<string>());

        // Code Arena's own files, its config.json with the key, are never shown, even named, even through a link.
        var link = Path.Combine(h.Root, "settings.json");
        File.CreateSymbolicLink(link, h.Paths.ConfigFile);
        _adapter.Loaded.AddRange([h.Paths.ConfigFile, link]);
        Assert.Equal(2, (await page.RequestAsync("loadedSources"))["body"]!["sources"]!.AsArray().Count);
        foreach (var own in new[] { h.Paths.ConfigFile, link })
        {
            using var refused = await web.Http.GetAsync(Source(own));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("Code Arena's own", await refused.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.GetAsync($"/api/debug/source?id=99&path={Uri.EscapeDataString(outside)}")).StatusCode);
    }

    [Fact]
    public async Task A_configuration_with_no_adapter_here_says_how_to_get_one_and_one_no_adapter_takes_says_why()
    {
        using var h = Fake();
        await using var web = await WebRun.StartAsync(h);
        // Nothing runs before the folder is trusted, as in VS Code's Restricted Mode; what is wrong with a configuration is said first.
        var untrusted = await web.PostAsync("/api/debug/start", new JsonObject { ["configuration"] = Launch.DeepClone() });
        Assert.Equal(HttpStatusCode.Conflict, untrusted.StatusCode);
        Assert.Equal("untrusted", JsonNode.Parse(await untrusted.Content.ReadAsStringAsync())!["status"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/debug/start", new JsonObject { ["configuration"] = new JsonObject { ["type"] = "cppvsdbg", ["request"] = "launch" } })).StatusCode);
        Assert.Empty((await web.GetJsonAsync("/api/debug/sessions")).AsArray());
        await TrustAsync(web);

        // No PATH here: nothing is found but what config.json names.
        var missing = await web.PostAsync("/api/debug/start", new JsonObject { ["configuration"] = new JsonObject { ["type"] = "go", ["request"] = "launch", ["program"] = "." } });
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        var refusal = JsonNode.Parse(await missing.Content.ReadAsStringAsync())!;
        Assert.Equal("no_adapter", refusal["status"]!.GetValue<string>());
        Assert.Equal("dlv", refusal["adapter"]!.GetValue<string>());
        Assert.Contains("go install github.com/go-delve/delve/cmd/dlv@latest", refusal["install"]!.GetValue<string>());

        var windows = await web.PostAsync("/api/debug/start", new JsonObject { ["configuration"] = new JsonObject { ["type"] = "cppvsdbg", ["request"] = "launch" } });
        Assert.Equal(HttpStatusCode.BadRequest, windows.StatusCode);
        Assert.Contains("Visual Studio", await windows.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/debug/start", new JsonObject())).StatusCode);
        foreach (var port in new JsonNode[] { 0, 70_000, "x" })
        {
            var badPort = Launch.DeepClone().AsObject();
            badPort["debugServer"] = port;
            using var refused = await web.PostAsync("/api/debug/start", new JsonObject { ["configuration"] = badPort });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("bad_config", await refused.Content.ReadAsStringAsync());
        }
        Assert.Empty(_adapter.Received);

        var adapters =(await web.GetJsonAsync("/api/debug/adapters")).AsArray();
        Assert.Equal(["gdb", "lldb-dap", "netcoredbg", "debugpy", "dlv", "js-debug"], adapters.Select(a => a!["id"]!.GetValue<string>()));
        var gdb = adapters[0]!;
        Assert.True(gdb["found"]!.GetValue<bool>());
        Assert.Equal($"127.0.0.1:{_adapter.Port}", gdb["path"]!.GetValue<string>());
        var dlv = adapters[4]!;
        Assert.False(dlv["found"]!.GetValue<bool>());
        Assert.NotNull(dlv["reason"]);
        Assert.NotNull(dlv["install"]);
    }

    [Fact]
    public async Task A_cancel_and_a_progress_name_the_request_by_the_number_of_the_page_that_sent_it()
    {
        using var h = Fake();
        await using var web = await WebRun.StartAsync(h);
        var id = (await StartAsync(web))["id"]!.GetValue<string>();
        using var a = await DebugSocket.ConnectAsync(web, id);
        using var b = await DebugSocket.ConnectAsync(web, id);
        await a.RequestAsync("initialize");
        await b.RequestAsync("threads");
        // a's 2 is the adapter's 3.
        var slow = await a.SendAsync("slow");
        Assert.Equal(2, slow);
        Assert.Equal(slow, (await a.EventAsync("progressStart"))["body"]!["requestId"]!.GetValue<int>());
        Assert.Null((await b.EventAsync("progressStart"))["body"]!["requestId"]);

        // b has no request 2 waiting: its cancel names none (it would have been a's).
        await b.RequestAsync("cancel", new JsonObject { ["requestId"] = 2 });
        Assert.Null(_adapter.Received.Last()["arguments"]!["requestId"]);
        Assert.DoesNotContain(a.Seen, m => DebugSocket.Is(m, "response") && m["command"]!.GetValue<string>() == "slow");

        await a.RequestAsync("cancel", new JsonObject { ["requestId"] = slow });
        var cancelled = await a.UntilAsync(m => DebugSocket.Is(m, "response") && m["command"]!.GetValue<string>() == "slow", "the slow request's answer");
        Assert.Equal(slow, cancelled["request_seq"]!.GetValue<int>());
        Assert.False(cancelled["success"]!.GetValue<bool>());
        // What the adapter got is numbered in the order it got it.
        var seqs = _adapter.Received.Select(r => r["seq"]!.GetValue<long>()).ToList();
        Assert.Equal(seqs.Order(), seqs);
        Assert.Equal(seqs.Count, seqs.Distinct().Count());
    }

    [Fact]
    public async Task A_step_answered_after_the_stop_it_led_to_leaves_the_session_stopped()
    {
        using var h = Fake();
        await using var web = await WebRun.StartAsync(h);
        var id = (await StartAsync(web))["id"]!.GetValue<string>();
        using var page = await DebugSocket.ConnectAsync(web, id);
        await page.RequestAsync("initialize");
        await page.RequestAsync("configurationDone");
        await page.EventAsync("stopped");

        await page.RequestAsync("next", new JsonObject { ["threadId"] = 1 });
        await page.EventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "step");
        // One more round trip: what came after the step's answer has come too.
        await page.RequestAsync("threads");
        Assert.Equal("stopped", (await web.GetJsonAsync("/api/debug/sessions"))[0]!["state"]!.GetValue<string>());
        var last = page.Seen.Last(m => m["event"]?.GetValue<string>() == "codeArena.state");
        Assert.Equal("stopped", last["body"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public void A_page_that_falls_too_far_behind_in_bytes_is_let_go()
    {
        var page = new DebugPage(maxBytes: 100);
        var sixty = new SocketMessage(new byte[60], Text: true);
        Assert.True(page.TryWrite(sixty));
        Assert.False(page.TryWrite(sixty));
        Assert.True(page.Out.Reader.TryRead(out var first));
        page.Taken(first);
        Assert.True(page.TryWrite(sixty));
    }

    [Fact]
    public async Task A_gdb_named_by_its_name_alone_is_the_one_on_PATH_not_a_program_in_the_folder()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = Fake();
        var marker = Path.Combine(h.Root, "which-gdb");
        void Script(string path, string says)
        {
            File.WriteAllText(path, $"#!/bin/sh\necho {says} > '{marker}'\nexec sleep 600\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var bin = Directory.CreateDirectory(Path.Combine(h.Root, "bin")).FullName;
        Script(Path.Combine(bin, "my-gdb"), "path");
        Script(Path.Combine(h.Work, "my-gdb"), "folder");
        h.Env["PATH"] = bin;
        await using var web = await WebRun.StartAsync(h);
        JsonObject Cppdbg(string gdb) => new() { ["type"] = "cppdbg", ["request"] = "launch", ["program"] = "/bin/true", ["MIMode"] = "gdb", ["miDebuggerPath"] = gdb };

        var id = (await StartAsync(web, Cppdbg("my-gdb")))["id"]!.GetValue<string>();
        for (var waited = 0; !File.Exists(marker) || File.ReadAllText(marker).Trim().Length == 0; waited += 50)
        {
            Assert.True(waited < 10_000, "The gdb named did not start.");
            await Task.Delay(50);
        }
        Assert.Equal("path", File.ReadAllText(marker).Trim());
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/debug/stop", new JsonObject { ["id"] = id })).StatusCode);

        using var missing = await web.PostAsync("/api/debug/start", new JsonObject { ["configuration"] = Cppdbg("no-such-gdb") });
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Contains("not on PATH", await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_debugger_calls_take_only_this_runs_key_from_its_own_page()
    {
        using var h = Fake();
        await using var web = await WebRun.StartAsync(h);
        (string Method, string Path, JsonObject? Body)[] calls =
        [
            ("POST", "/api/debug/start", new() { ["configuration"] = Launch.DeepClone() }),
            ("GET", "/api/debug/sessions", null),
            ("GET", "/api/debug/socket?id=1", null),
            ("POST", "/api/debug/stop", new() { ["id"] = "1" }),
            ("GET", "/api/debug/source?id=1&path=%2Fetc%2Fhostname", null),
            ("GET", "/api/debug/adapters", null),
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
            await Expect(HttpStatusCode.Forbidden, "from another site", web.Http, r => r.Headers.Add("Origin", "https://evil.example"));
            await Expect(HttpStatusCode.Forbidden, "said by the browser to come from elsewhere", web.Http, r => r.Headers.Add("Sec-Fetch-Site", "cross-site"));
            if (body is not null)
            {
                await Expect(HttpStatusCode.UnsupportedMediaType, "as a form", web.Http, r => r.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "text/plain"));
            }
        }
        Assert.Empty(wrong);
        Assert.Empty(_adapter.Received);
        Assert.Empty((await web.GetJsonAsync("/api/debug/sessions")).AsArray());

        // The socket: only with the key and from this page; none for a session that is not there.
        var id = (await StartAsync(web))["id"]!.GetValue<string>();
        var host = ("Host", $"127.0.0.1:{web.Port}");
        var key = ("Authorization", $"Bearer {web.Token}");
        var origin = ("Origin", $"http://127.0.0.1:{web.Port}");
        var socket = $"/api/debug/socket?id={id}";
        Assert.Equal(401, await UpgradeAsync(web.Port, socket, host, origin));
        Assert.Equal(403, await UpgradeAsync(web.Port, socket, host, key, ("Origin", "https://evil.example")));
        Assert.Equal(403, await UpgradeAsync(web.Port, socket, host, key, ("Origin", $"http://localhost:{web.Port + 1}")));
        Assert.Equal(404, await UpgradeAsync(web.Port, "/api/debug/socket?id=99", host, key, origin));
        Assert.Equal(101, await UpgradeAsync(web.Port, socket, host, key, origin));
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Http.GetAsync(socket)).StatusCode);
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
        _adapter.Dispose();
        _gateway.Dispose();
        _mcp.Dispose();
    }
}
