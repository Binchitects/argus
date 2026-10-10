using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena.Tests;

/// <summary>code-arena web against the fake gateway and Arena: the real command, a real socket, the page's API.</summary>
public sealed partial class WebTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    [Fact]
    public async Task Web_listens_on_127_0_0_1_only_and_refuses_requests_without_this_runs_token()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);

        Assert.Matches(@"^http://127\.0\.0\.1:\d+/\?token=[A-Za-z0-9_-]{43}$", web.Address);
        Assert.Contains("Only this machine can open it", web.Out);
        // Not on the machine's other addresses.
        foreach (var address in OtherAddresses())
        {
            using var other = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(() => other.ConnectAsync(address, web.Port));
        }

        using var anonymous = WebRun.Client(web.Port, token: null);
        var page = await anonymous.GetAsync("/");
        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
        Assert.Contains("Open the address code-arena printed", await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/assets/app.js")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/?token=" + new string('x', 43))).StatusCode);

        // The token in the address once: it becomes a cookie, and leaves the address bar. Not by a redirect, which a browser
        // that came from another site (the launcher file) follows without the SameSite=Strict cookie: by a page of the
        // server's own that goes on to /, which the browser sends it to.
        var opened = await anonymous.GetAsync("/?token=" + web.Token);
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Null(opened.Headers.Location);
        Assert.Contains("""<meta http-equiv="refresh" content="0;url=/">""", await opened.Content.ReadAsStringAsync());
        Assert.Contains("frame-ancestors 'none'", opened.Headers.GetValues("Content-Security-Policy").Single());
        var cookie = Assert.Single(opened.Headers.GetValues("Set-Cookie"));
        Assert.Equal($"code_arena_{web.Port}={web.Token}; Path=/; HttpOnly; SameSite=Strict", cookie);

        using var withCookie = WebRun.Client(web.Port, token: null);
        withCookie.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        var shown = await withCookie.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.Contains("<title>Code Arena</title>", await shown.Content.ReadAsStringAsync());
        Assert.Contains("frame-ancestors 'none'", shown.Headers.GetValues("Content-Security-Policy").Single());
        // The terminals' sockets are this server's, named: no other address.
        Assert.Contains($"connect-src 'self' ws://127.0.0.1:{web.Port} ws://localhost:{web.Port};", shown.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", shown.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("text/javascript; charset=utf-8", (await withCookie.GetAsync("/assets/app.js")).Content.Headers.ContentType!.ToString());
        var state = await Json(await withCookie.GetAsync("/api/state"));
        Assert.Equal("model-a", state["model"]!.GetValue<string>());
        Assert.Equal("ask", state["mode"]!.GetValue<string>());
        Assert.Equal(h.Work, state["folder"]!.GetValue<string>());
        // The about box: the version, its licence and its source.
        Assert.Equal(Cli.Version, state["version"]!.GetValue<string>());
        Assert.Equal("AGPL-3.0-only", state["license"]!.GetValue<string>());
        Assert.Equal("https://github.com/Binchitects/argus", state["source"]!.GetValue<string>());
        // The help's link to the manual: the Arena's own, at the address signed in to.
        Assert.Equal(_mcp.BaseUrl + "/help/code-arena", state["manual"]!.GetValue<string>());
        // The key works as a bearer token too (scripts, these tests); another key does not.
        Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync("/api/state")).StatusCode);
        using var wrong = WebRun.Client(web.Port, token: new string('y', 43));
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task Other_hosts_other_sites_and_non_JSON_posts_are_refused()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);

        // A name pointed at 127.0.0.1 (DNS rebinding) carries its own name: refused, key or not.
        foreach (var host in new[] { $"evil.example:{web.Port}", $"127.0.0.1:{web.Port + 1}", "127.0.0.1" })
        {
            using var rebound = new HttpRequestMessage(HttpMethod.Get, "/api/state");
            rebound.Headers.Host = host;
            Assert.Equal(HttpStatusCode.Forbidden, (await web.Http.SendAsync(rebound)).StatusCode);
        }
        using var localhost = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        localhost.Headers.Host = $"localhost:{web.Port}";
        Assert.Equal(HttpStatusCode.OK, (await web.Http.SendAsync(localhost)).StatusCode);

        // Another site's page: its Origin, or the browser's own word for it.
        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/settings", new JsonObject { ["mode"] = "yolo" }, ("Origin", "https://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/settings", new JsonObject { ["mode"] = "yolo" }, ("Origin", $"http://127.0.0.1:{web.Port + 1}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/settings", new JsonObject { ["mode"] = "yolo" }, ("Sec-Fetch-Site", "cross-site"))).StatusCode);
        using var form = new HttpRequestMessage(HttpMethod.Post, "/api/settings") { Content = new StringContent("mode=yolo", Encoding.UTF8, "text/plain") };
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await web.Http.SendAsync(form)).StatusCode);
        Assert.Equal("ask", (await web.GetJsonAsync("/api/state"))["mode"]!.GetValue<string>());

        // Its own page may.
        var own = await web.PostAsync("/api/settings", new JsonObject { ["mode"] = "auto-edit" }, ("Origin", $"http://127.0.0.1:{web.Port}"), ("Sec-Fetch-Site", "same-origin"));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal("auto-edit", (await Json(own))["mode"]!.GetValue<string>());

        // The diagram runner is public code in a sandbox of its own: no key, never framed by another site.
        using var anonymous = WebRun.Client(web.Port, token: null);
        var runner = await anonymous.GetAsync("/preview.html");
        Assert.Equal(HttpStatusCode.OK, runner.StatusCode);
        var csp = runner.Headers.GetValues("Content-Security-Policy").Single();
        Assert.StartsWith("sandbox allow-scripts", csp);
        Assert.Contains("connect-src 'none'", csp);
        Assert.Contains("frame-ancestors 'self'", csp);
    }

    [Fact]
    public async Task Lines_chosen_in_the_editor_reach_the_model_with_the_message_and_the_page_shows_what_was_typed()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write("src/app.ts", "const a = 1\nconst b = 2\nconst c = 3\n");
        _gateway.Answer = req => Reply.Say("Seen.");
        await using var web = await WebRun.StartAsync(h);

        var context = new JsonArray(new JsonObject { ["path"] = "src/app.ts", ["startLine"] = 2, ["endLine"] = 3 },
            new JsonObject { ["path"] = "src/app.ts", ["startLine"] = 1, ["endLine"] = 1, ["text"] = "const a = 9" });
        using (var stream = await web.StreamAsync(HttpMethod.Post, "/api/messages", new JsonObject { ["text"] = "Why these? @src/app.ts:3", ["context"] = context }))
        {
            var events = await stream.RestAsync();
            var attached = events.Single(e => e["type"]!.GetValue<string>() == "attached");
            Assert.Equal("m0", attached["id"]!.GetValue<string>());
            Assert.Equal(["src/app.ts:2-3", "src/app.ts:1", "src/app.ts:3"], attached["files"]!.AsArray().Select(f => f!.GetValue<string>()));
        }
        var asked = _gateway.Requests[^1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>();
        Assert.StartsWith("Why these? @src/app.ts:3" + Mentions.Marker, asked);
        Assert.Contains("<selection path=\"src/app.ts\" lines=\"2-3\">\n     2\tconst b = 2\n     3\tconst c = 3\n</selection>", asked);
        Assert.Contains("<selection path=\"src/app.ts\" lines=\"1-1\" unsaved=\"true\">\n     1\tconst a = 9\n</selection>", asked);
        Assert.Contains("<file path=\"src/app.ts\" lines=\"3-3\">", asked);

        // Read back: the question as typed, and what went with it apart.
        var question = (await web.GetJsonAsync("/api/session"))["messages"]!.AsArray()[0]!;
        Assert.Equal("Why these? @src/app.ts:3", question["content"]!.GetValue<string>());
        Assert.Equal(["src/app.ts:2-3", "src/app.ts:1", "src/app.ts:3"], question["files"]!.AsArray().Select(f => f!.GetValue<string>()));

        // Queued, a message keeps its lines until it runs.
        Assert.Equal(HttpStatusCode.Accepted, (await web.PostAsync("/api/queue", new JsonObject { ["text"] = "And this?", ["context"] = new JsonArray(new JsonObject { ["path"] = "src/app.ts", ["startLine"] = 1, ["endLine"] = 1 }) })).StatusCode);
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!_gateway.Requests.Any(r => r["messages"]!.ToJsonString().Contains("And this?", StringComparison.Ordinal)))
        {
            Assert.True(DateTime.UtcNow < until, "The queued message never reached the model.");
            await Task.Delay(50);
        }
        var queued = _gateway.Requests.Last(r => r["messages"]!.ToJsonString().Contains("And this?", StringComparison.Ordinal))["messages"]!.AsArray()
            .Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>();
        Assert.Contains("<selection path=\"src/app.ts\" lines=\"1-1\">\n     1\tconst a = 1\n</selection>", queued);
    }

    [Fact]
    public async Task A_turn_streams_its_thinking_text_tool_calls_and_tokens_as_events_and_is_saved()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write("notes.txt", "The build uses make.\n");
        _gateway.Answer = req => FakeGateway.HasToolResults(req)
            ? Reply.Say("It is built with **make**.")
            : new Reply("", [("read_file", """{"path":"notes.txt"}""")], "Looking at the notes.");
        await using var web = await WebRun.StartAsync(h);

        using var stream = await web.SendAsync("How is it built?");
        var events = await stream.RestAsync();
        var types = events.Select(e => e["type"]!.GetValue<string>()).ToList();
        Assert.Equal(["question", "assistant", "reasoning", "thought", "usage", "tool_call", "tool_result", "assistant", "content", "content", "usage", "done"], types);
        Assert.Equal("m0", events[0]["id"]!.GetValue<string>());
        Assert.Equal("m1", events[1]["id"]!.GetValue<string>());
        Assert.Equal("model-a", events[1]["model"]!.GetValue<string>());
        Assert.Equal("Looking at the notes.", events[2]["text"]!.GetValue<string>());
        Assert.True(events[4]["prompt"]!.GetValue<long>() > 0);
        Assert.True(events[4]["cached"]!.GetValue<long>() > 0);
        var call = events[5];
        Assert.Equal("read_file", call["name"]!.GetValue<string>());
        Assert.Equal("local", call["tool"]!.GetValue<string>());
        var result = events[6];
        Assert.Equal(call["id"]!.GetValue<string>(), result["id"]!.GetValue<string>());
        Assert.Equal("m2", result["messageId"]!.GetValue<string>());
        Assert.Contains("The build uses make.", result["text"]!.GetValue<string>());
        Assert.False(result["isError"]!.GetValue<bool>());
        Assert.Equal("m3", events[7]["id"]!.GetValue<string>());
        Assert.Equal("It is built with **make**.", string.Concat(events.Where(e => e["type"]!.GetValue<string>() == "content").Select(e => e["text"]!.GetValue<string>())));
        Assert.Equal(2, events[^1]["usage"]!["requests"]!.GetValue<int>());
        // What the agent does is logged in the terminal.
        Assert.Contains("● read_file notes.txt", web.Err);

        // The same turn, read back as the page shows a saved session.
        var session = await web.GetJsonAsync("/api/session");
        var messages = session["messages"]!.AsArray();
        Assert.Equal(["m0", "m1", "m2", "m3"], messages.Select(m => m!["id"]!.GetValue<string>()));
        Assert.Equal(["user", "assistant", "tool", "assistant"], messages.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("m2", messages[3]!["parentId"]!.GetValue<string>());
        Assert.Equal("read_file", messages[1]!["toolCalls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("read_file", messages[2]!["toolName"]!.GetValue<string>());
        Assert.Equal("model-a", messages[3]!["model"]!.GetValue<string>());
        Assert.True(messages[3]!["promptTokens"]!.GetValue<long>() > 0);
        Assert.True(messages[3]!["cachedTokens"]!.GetValue<long>() > 0);
        Assert.Equal(7, messages[3]!["completionTokens"]!.GetValue<long>());
        var listed = Assert.Single((await web.GetJsonAsync("/api/sessions")).AsArray());
        Assert.Equal("How is it built?", listed!["title"]!.GetValue<string>());
        Assert.Equal(session["id"]!.GetValue<string>(), listed["id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await web.Http.GetAsync("/api/turn")).StatusCode);
    }

    [Fact]
    public async Task An_edit_waits_for_the_pages_approval_and_always_lets_the_next_edits_through()
    {
        using var h = new Harness(_gateway, _mcp);
        var file = h.Write("a.txt", "one\ntwo\nthree\n");
        _gateway.Answer = req => FakeGateway.Last(req) switch
        {
            "edit it" => Reply.Call(("edit_file", """{"path":"a.txt","old_string":"two","new_string":"2"}""")),
            "edit again" => Reply.Call(("edit_file", """{"path":"a.txt","old_string":"three","new_string":"3"}""")),
            "clean up" => Reply.Call(("run_shell", """{"command":"rm -rf build"}""")),
            _ => Reply.Say("Result: " + FakeGateway.Last(req)),
        };
        await using var web = await WebRun.StartAsync(h);

        using (var stream = await web.SendAsync("edit it"))
        {
            var question = await stream.UntilAsync("approval");
            Assert.Equal("call_0_edit_file", question["id"]!.GetValue<string>());
            Assert.Equal("edit_file", question["name"]!.GetValue<string>());
            Assert.Equal("for file edits", question["always"]!.GetValue<string>());
            Assert.Contains("\"old_string\":\"two\"", question["arguments"]!.GetValue<string>());
            Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(file));
            Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/approvals", new JsonObject { ["id"] = "call_0_edit_file", ["answer"] = "maybe" })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/approvals", new JsonObject { ["id"] = "call_0_edit_file", ["answer"] = "always" })).StatusCode);
            var result = await stream.UntilAsync("tool_result");
            Assert.False(result["declined"]!.GetValue<bool>());
            var diff = result["diff"]!;
            Assert.Equal("a.txt", diff["path"]!.GetValue<string>());
            Assert.Equal(1, diff["added"]!.GetValue<int>());
            Assert.Equal(1, diff["removed"]!.GetValue<int>());
            var lines = diff["lines"]!.AsArray().Select(l => l!.ToJsonString(CodeArena.Json.Relaxed)).ToList();
            Assert.Contains("""["-",2,0,"two"]""", lines);
            Assert.Contains("""["+",0,2,"2"]""", lines);
            Assert.Contains("""[" ",1,1,"one"]""", lines);
            await stream.RestAsync();
        }
        Assert.Equal("one\n2\nthree\n", File.ReadAllText(file));
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/approvals", new JsonObject { ["id"] = "call_0_edit_file", ["answer"] = "allow" })).StatusCode);

        // "Always for this session": the next edit asks no more.
        using (var stream = await web.SendAsync("edit again"))
        {
            var events = await stream.RestAsync();
            Assert.DoesNotContain(events, e => e["type"]!.GetValue<string>() == "approval");
        }
        Assert.Equal("one\n2\n3\n", File.ReadAllText(file));

        // A command still asks; Deny tells the model, and nothing runs.
        Directory.CreateDirectory(Path.Combine(h.Work, "build"));
        using (var stream = await web.SendAsync("clean up"))
        {
            var question = await stream.UntilAsync("approval");
            Assert.Equal("for `rm …`", question["always"]!.GetValue<string>());
            Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/approvals", new JsonObject { ["id"] = question["id"]!.DeepClone(), ["answer"] = "deny" })).StatusCode);
            var result = await stream.UntilAsync("tool_result");
            Assert.True(result["declined"]!.GetValue<bool>());
            Assert.StartsWith("The person declined", result["text"]!.GetValue<string>());
            await stream.RestAsync();
        }
        Assert.True(Directory.Exists(Path.Combine(h.Work, "build")));

        // Read back in this run: the edits' diffs as they were shown, whole-file and numbered, and the refused call.
        static string[] Lines(JsonNode session, string message) => [.. session["diffs"]![message]!["lines"]!.AsArray().Select(l => l!.ToJsonString(CodeArena.Json.Relaxed))];
        var session = await web.GetJsonAsync("/api/session");
        Assert.Contains("""["-",2,0,"two"]""", Lines(session, "m2"));
        Assert.Contains("""["-",3,0,"three"]""", Lines(session, "m6"));
        Assert.Contains(session["messages"]!.AsArray(), m => m!["role"]!.GetValue<string>() == "tool" && m["status"]!.GetValue<string>() == "declined");

        // In a later run the session file has only each edit's old and new text: the diff of those.
        await web.DisposeAsync();
        await using var later = await WebRun.StartAsync(h, "--continue");
        var resumed = await later.GetJsonAsync("/api/session");
        Assert.Equal(session["id"]!.GetValue<string>(), resumed["id"]!.GetValue<string>());
        Assert.Equal(["""["-",0,0,"two"]""", """["+",0,0,"2"]"""], Lines(resumed, "m2"));
        Assert.Equal(["""["-",0,0,"three"]""", """["+",0,0,"3"]"""], Lines(resumed, "m6"));
    }

    [Fact]
    public async Task Stop_ends_the_turn_and_keeps_what_was_written_and_a_page_that_comes_back_replays_the_turn()
    {
        _gateway.Hang = true;
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);

        using var stream = await web.SendAsync("think hard");
        Assert.Equal("Let me think", (await stream.UntilAsync("content"))["text"]!.GetValue<string>());
        Assert.True((await web.GetJsonAsync("/api/state"))["busy"]!.GetValue<bool>());
        // One turn at a time, and no switching sessions under it.
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/messages", new JsonObject { ["text"] = "another" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/sessions/new", new JsonObject())).StatusCode);

        // A page that was reloaded watches the turn from its start.
        using (var again = await web.StreamAsync(HttpMethod.Get, "/api/turn", null))
        {
            Assert.Equal("m0", (await again.UntilAsync("question"))["id"]!.GetValue<string>());
            Assert.Equal("Let me think", (await again.UntilAsync("content"))["text"]!.GetValue<string>());
        }

        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/stop", new JsonObject())).StatusCode);
        var stopped = await stream.UntilAsync("stopped");
        Assert.Equal("m1", stopped["id"]!.GetValue<string>());
        Assert.Equal("done", (await stream.RestAsync())[^1]["type"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/stop", new JsonObject())).StatusCode);

        var messages = (await web.GetJsonAsync("/api/session"))["messages"]!.AsArray();
        Assert.Equal("stopped", messages[1]!["status"]!.GetValue<string>());
        Assert.Equal("Let me think", messages[1]!["content"]!.GetValue<string>());
        Assert.False((await web.GetJsonAsync("/api/state"))["busy"]!.GetValue<bool>());
    }

    [Fact]
    public async Task This_folders_sessions_are_listed_newest_first_and_resumed_with_their_history()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => Reply.Say($"answer to {FakeGateway.Last(req)}");
        await using var web = await WebRun.StartAsync(h);

        await (await web.SendAsync("first question")).RestAsync();
        var first = (await web.GetJsonAsync("/api/session"))["id"]!.GetValue<string>();
        var fresh = await Json(await web.PostAsync("/api/sessions/new", new JsonObject()));
        Assert.NotEqual(first, fresh["id"]!.GetValue<string>());
        Assert.Empty(fresh["messages"]!.AsArray());
        // An empty session is not made twice.
        Assert.Equal(fresh["id"]!.GetValue<string>(), (await Json(await web.PostAsync("/api/sessions/new", new JsonObject())))["id"]!.GetValue<string>());
        await Task.Delay(50);
        await (await web.SendAsync("second question")).RestAsync();

        var sessions = (await web.GetJsonAsync("/api/sessions")).AsArray();
        Assert.Equal(["second question", "first question"], sessions.Select(s => s!["title"]!.GetValue<string>()));

        var resumed = await Json(await web.PostAsync("/api/sessions/resume", new JsonObject { ["id"] = first }));
        Assert.Equal(first, resumed["id"]!.GetValue<string>());
        Assert.Equal(["first question", "answer to first question"], resumed["messages"]!.AsArray().Select(m => m!["content"]!.GetValue<string>()));
        Assert.Equal(first, (await web.GetJsonAsync("/api/state"))["session"]!.GetValue<string>());

        await (await web.SendAsync("third")).RestAsync();
        var sent = _gateway.Requests[^1]["messages"]!.AsArray().Skip(1).Select(m => m!["content"]!.GetValue<string>()).ToList();
        Assert.Equal(["first question", "answer to first question", "third"], sent);
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/sessions/resume", new JsonObject { ["id"] = "../../config/config" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/sessions/resume", new JsonObject { ["id"] = "20000101-000000" })).StatusCode);
    }

    [Fact]
    public async Task The_chats_history_is_what_this_folder_sent_in_the_terminal_and_the_page_newest_first()
    {
        using var h = new Harness(_gateway, _mcp);
        _gateway.Answer = req => Reply.Say($"answer to {FakeGateway.Last(req)}");
        Assert.Equal(0, await h.Run("from the terminal\n/help\n/clear\n/exit\n", "chat"));
        await using var web = await WebRun.StartAsync(h);
        await (await web.SendAsync("from the page")).RestAsync();
        await (await web.SendAsync("from the terminal")).RestAsync();

        // The terminal's own commands are left out: the page cannot run them. /clear it can.
        var history = (await web.GetJsonAsync("/api/history")).AsArray().Select(e => e!["text"]!.GetValue<string>());
        Assert.Equal(["from the terminal", "from the page", "/clear"], history);
        Assert.Equal(["from the terminal", "/help", "/clear", "from the page", "from the terminal"], new InputHistory(h.Paths.HistoryDir, h.Work).Entries());
    }

    [Fact]
    public async Task Mode_model_and_thinking_changes_reach_the_next_request()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);

        var config = await web.GetJsonAsync("/api/chat/config");
        Assert.Equal(["model-a", "model-b"], config["models"]!.AsArray().Select(m => m!["name"]!.GetValue<string>()));
        Assert.Equal(32768, config["models"]![0]!["context"]!.GetValue<int>());
        Assert.Equal(["off", "low", "medium", "high", "xhigh"], config["presets"]!.AsArray().Select(p => p!["level"]!.GetValue<string>()));

        var state = await Json(await web.PostAsync("/api/settings", new JsonObject { ["mode"] = "plan", ["model"] = "model-b", ["thinking"] = "off" }));
        Assert.Equal("plan", state["mode"]!.GetValue<string>());
        Assert.Equal("model-b", state["model"]!.GetValue<string>());
        Assert.Equal("off", state["thinking"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/settings", new JsonObject { ["mode"] = "reckless" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/settings", new JsonObject { ["model"] = "no-such-model" })).StatusCode);

        await (await web.SendAsync("plan it")).RestAsync();
        var request = _gateway.Requests[^1];
        Assert.Equal("model-b", request["model"]!.GetValue<string>());
        Assert.False(request["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        var tools = FakeGateway.ToolNames(request);
        Assert.Contains("read_file", tools);
        Assert.DoesNotContain("edit_file", tools);
        Assert.Contains("Mode: plan", FakeGateway.System(request));

        Assert.Null((await Json(await web.PostAsync("/api/settings", new JsonObject { ["thinking"] = null })))["thinking"]);
    }

    [Fact]
    public async Task The_bare_command_is_the_IDE_and_says_how_to_talk_in_the_terminal_instead()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h, bare: true);
        Assert.Contains($"the IDE for {h.Work}", web.Out);
        Assert.Contains("The agent in this terminal instead: code-arena chat", web.Out);
        Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync("/api/files")).StatusCode);
    }

    [Fact]
    public async Task The_browser_is_opened_with_a_file_of_the_persons_own_never_with_the_key_on_its_command_line()
    {
        using var h = new Harness(_gateway, _mcp);
        var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var web = await WebRun.StartAsync(h, bare: false, browse: url => opened.TrySetResult(url), TimeSpan.FromSeconds(2));
        string file;
        try
        {
            // A command line is open to every user of the machine (ps, /proc): the browser's holds a file's name, not the key.
            var url = await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.StartsWith("file://", url);
            Assert.DoesNotContain(web.Token, url);
            file = new Uri(url).LocalPath;
            Assert.StartsWith(h.Paths.DataDir, file);
            // The file sends the browser on to the address, which is printed for the person as before.
            Assert.Contains($"url={web.Address}", File.ReadAllText(file));
            Assert.Contains(web.Address, web.Out);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(file)!));
            }
            // Followed, as a browser does from a file: the request comes from another site, so the answer cannot be a redirect,
            // which the browser would follow without the SameSite=Strict cookie (Chrome showed the page that asks for the key).
            // It is a page that sets the cookie and goes on to / itself; from it, the browser sends the cookie.
            using var browser = WebRun.Client(web.Port, token: null);
            using var entry = new HttpRequestMessage(HttpMethod.Get, web.Address);
            entry.Headers.Add("Sec-Fetch-Site", "cross-site");
            var handOff = await browser.SendAsync(entry);
            Assert.Equal(HttpStatusCode.OK, handOff.StatusCode);
            Assert.Contains("""<meta http-equiv="refresh" content="0;url=/">""", await handOff.Content.ReadAsStringAsync());
            using var page = new HttpRequestMessage(HttpMethod.Get, "/");
            page.Headers.Add("Sec-Fetch-Site", "same-origin");
            page.Headers.Add("Cookie", handOff.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
            Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(page)).StatusCode);
            // The browser has the IDE: code-arena does not tell the person to open the address.
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.DoesNotContain("No browser has opened the IDE yet", web.Out);
        }
        finally
        {
            await web.DisposeAsync();
        }
        // And it goes with the run.
        Assert.False(File.Exists(file));

        // A browser that came with the key but never had the page with its cookie (a hand-off that failed) has not opened
        // the IDE: the person is told to open the address.
        await using var lost = await WebRun.StartAsync(h, bare: false, browse: _ => true, TimeSpan.FromSeconds(1));
        using var anonymous = WebRun.Client(lost.Port, token: null);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(lost.Address)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/")).StatusCode);
        for (var waited = 0; !lost.Out.Contains("No browser has opened the IDE yet: open the address above in yours.") && waited < 10_000; waited += 50)
        {
            await Task.Delay(50);
        }
        Assert.Contains("No browser has opened the IDE yet: open the address above in yours.", lost.Out);
    }

    [Fact]
    public async Task Without_its_page_built_in_web_says_so_plainly_and_the_bare_command_talks_in_the_terminal()
    {
        using var h = new Harness(_gateway, _mcp);
        var output = new StringWriter();
        var env = new CliEnv
        {
            In = new StringReader("/exit\n"), Out = output, Err = output, Env = _ => null, Cwd = h.Work, Paths = h.Paths,
            Web = new WebAssets([]),
        };
        Assert.Equal(1, await Cli.RunAsync(["web", "--no-open"], env));
        Assert.Contains("This code-arena has no IDE: its page was not built into it", output.ToString());
        Assert.Contains("tools/publish-code-arena.sh", output.ToString());
        Assert.Equal(2, await Cli.RunAsync(["web", "do", "something"], env));
        Assert.Contains("web takes no prompt", output.ToString());

        // code-arena alone opens the IDE; without one, the conversation runs here.
        Assert.Equal(0, await Cli.RunAsync(["--no-open"], env));
        Assert.Contains("This code-arena has no IDE built into it: the conversation runs in this terminal.", output.ToString());
        Assert.Contains("Code Arena", output.ToString());
    }

    private static IEnumerable<IPAddress> OtherAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Take(2);

    private static async Task<JsonObject> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }
}
