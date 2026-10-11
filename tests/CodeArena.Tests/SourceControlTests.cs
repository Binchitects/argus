using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>The IDE's Source Control: the folder's git as the person works it.</summary>
public sealed class SourceControlTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {p.StandardError.ReadToEnd()}");
    }

    /// <summary>What git printed, trimmed.</summary>
    private static string GitOut(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {p.StandardError.ReadToEnd()}");
        return output.Trim();
    }

    /// <summary>A new repository on main, committing as Pat Person.</summary>
    private static void Init(string dir)
    {
        Git(dir, "init", "-q", "-b", "main");
        Git(dir, "config", "user.name", "Pat Person");
        Git(dir, "config", "user.email", "pat@example.test");
    }

    private static void Commit(string dir, string message)
    {
        Git(dir, "add", "-A");
        Git(dir, "commit", "-q", "-m", message);
    }

    private static Task<HttpResponseMessage> Get(WebRun web, string path, params (string Name, string Value)[] query) =>
        web.Http.GetAsync(path + "?" + string.Join('&', query.Select(q => $"{q.Name}={Uri.EscapeDataString(q.Value)}")));

    private static async Task<JsonNode> Show(WebRun web, string path, string rev)
    {
        var response = await Get(web, "/api/git/show", ("path", path), ("rev", rev));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} at {rev}: {(int)response.StatusCode} {text}");
        return JsonNode.Parse(text)!;
    }

    private static string? Text(JsonNode shown) => shown["text"]?.GetValue<string>();

    private static async Task<JsonNode> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    private static Dictionary<string, (string? Staged, string? Changed)> Files(JsonNode status) =>
        status["files"]!.AsArray().ToDictionary(f => f!["path"]!.GetValue<string>(), f => (f!["staged"]?.GetValue<string>(), f["changed"]?.GetValue<string>()));

    [Fact]
    public async Task What_changed_since_the_last_commit_is_staged_unstaged_discarded_and_committed()
    {
        using var h = new Harness(_gateway, _mcp, c => c["autoCommit"] = false);
        Git(h.Work, "init", "-q", "-b", "main");
        Git(h.Work, "config", "user.name", "Pat Person");
        Git(h.Work, "config", "user.email", "pat@example.test");
        h.Write("src/app.ts", "const total = 1\n");
        h.Write("README.md", "# Shop\n");
        Git(h.Work, "add", "-A");
        Git(h.Work, "commit", "-q", "-m", "First");
        h.Write("src/app.ts", "const total = 2\n");
        h.Write("notes.txt", "new\n");
        await using var web = await WebRun.StartAsync(h);

        var status = await web.GetJsonAsync("/api/git/status");
        Assert.Equal("main", status["branch"]!.GetValue<string>());
        Assert.Equal(new Dictionary<string, (string?, string?)> { ["src/app.ts"] = (null, "M"), ["notes.txt"] = (null, "?") }, Files(status));

        // Before and now, for the diff tab.
        var texts = await web.GetJsonAsync("/api/git/diff?path=src%2Fapp.ts");
        Assert.Equal("const total = 1\n", texts["original"]!.GetValue<string>());
        Assert.Equal("const total = 2\n", texts["modified"]!.GetValue<string>());
        Assert.Null((await web.GetJsonAsync("/api/git/diff?path=notes.txt"))["original"]);

        // Staged, then not.
        var staged = JsonNode.Parse(await (await web.PostAsync("/api/git/stage", new JsonObject { ["paths"] = new JsonArray("src/app.ts") })).Content.ReadAsStringAsync())!;
        Assert.Equal(("M", (string?)null), Files(staged)["src/app.ts"]);
        var unstaged = JsonNode.Parse(await (await web.PostAsync("/api/git/unstage", new JsonObject { ["paths"] = new JsonArray("src/app.ts") })).Content.ReadAsStringAsync())!;
        Assert.Equal(((string?)null, "M"), Files(unstaged)["src/app.ts"]);

        // Discarded: a file git does not know is deleted.
        await web.PostAsync("/api/git/discard", new JsonObject { ["paths"] = new JsonArray("notes.txt") });
        Assert.False(File.Exists(Path.Combine(h.Work, "notes.txt")));

        // Nothing staged: said so; all of it: committed, by the person.
        var refused = await web.PostAsync("/api/git/commit", new JsonObject { ["message"] = "Two" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var committed = await web.PostAsync("/api/git/commit", new JsonObject { ["message"] = "Total is two", ["all"] = true });
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        Assert.Empty(JsonNode.Parse(await committed.Content.ReadAsStringAsync())!["status"]!["files"]!.AsArray());
        var log = (await web.GetJsonAsync("/api/git/log")).AsArray();
        Assert.Equal(["Total is two", "First"], log.Select(c => c!["subject"]!.GetValue<string>()));
        Assert.Equal("Pat Person", log[0]!["author"]!.GetValue<string>());

        // Branches, and switching.
        Git(h.Work, "branch", "feature");
        Assert.Equal(["main", "feature"], (await web.GetJsonAsync("/api/git/branches")).AsArray().Select(b => b!["name"]!.GetValue<string>()));
        var switched = JsonNode.Parse(await (await web.PostAsync("/api/git/switch", new JsonObject { ["branch"] = "feature" })).Content.ReadAsStringAsync())!;
        Assert.Equal("feature", switched["branch"]!.GetValue<string>());

        // Outside the folder: refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/git/stage", new JsonObject { ["paths"] = new JsonArray("../x") })).StatusCode);
    }

    [Fact]
    public async Task A_folder_in_no_repository_says_so()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Write("a.txt", "a\n");
        await using var web = await WebRun.StartAsync(h);
        Assert.False((await web.GetJsonAsync("/api/git/status"))["repository"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/git/commit", new JsonObject { ["message"] = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(web, "/api/git/show", ("path", "a.txt"), ("rev", "HEAD"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(web, "/api/git/commit", ("hash", "HEAD"))).StatusCode);
        Assert.Empty((await web.GetJsonAsync("/api/git/log?path=a.txt")).AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/git/stage-text", new JsonObject { ["path"] = "a.txt", ["text"] = "b\n" })).StatusCode);
    }

    [Fact]
    public async Task A_file_is_shown_as_HEAD_the_index_a_commit_or_a_branch_has_it()
    {
        using var h = new Harness(_gateway, _mcp, c => c["autoCommit"] = false);
        Init(h.Work);
        h.Write("app.ts", "one\n");
        Commit(h.Work, "One");
        var first = GitOut(h.Work, "rev-parse", "HEAD");
        Git(h.Work, "branch", "old");
        h.Write("app.ts", "two\n");
        File.WriteAllBytes(Path.Combine(h.Work, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);
        File.WriteAllBytes(Path.Combine(h.Work, "latin1.txt"), [0x63, 0x61, 0x66, 0xE9, 0x0A]);
        h.Write("big.txt", new string('x', CodeArena.IdeFiles.MaxTextBytes + 1));
        h.Write("two words.txt", "spaced\n");
        h.Write("-n.txt", "dashed\n");
        h.Write("cr.txt", "plain\n");
        Commit(h.Work, "Two");
        var second = GitOut(h.Work, "rev-parse", "HEAD");
        h.Write("app.ts", "three\n");
        h.Write("staged.txt", "new\n");
        Git(h.Work, "add", "app.ts", "staged.txt");
        h.Write("app.ts", "four\n");
        await using var web = await WebRun.StartAsync(h);

        // HEAD, the index, a hash (whole or short), a branch and an ancestor.
        var head = await Show(web, "app.ts", "HEAD");
        Assert.Equal(("two\n", true, second), (Text(head), head["exists"]!.GetValue<bool>(), head["commit"]!.GetValue<string>()));
        var index = await Show(web, "app.ts", ":");
        Assert.Equal(("three\n", (string?)null), (Text(index), index["commit"]?.GetValue<string>()));
        Assert.Equal("one\n", Text(await Show(web, "app.ts", first)));
        Assert.Equal("one\n", Text(await Show(web, "app.ts", first[..7])));
        Assert.Equal("one\n", Text(await Show(web, "app.ts", "old")));
        Assert.Equal("one\n", Text(await Show(web, "app.ts", "HEAD~1")));
        Assert.Equal("four\n", File.ReadAllText(Path.Combine(h.Work, "app.ts")));

        // A name is only a name: with a space, or a dash first. One with a carriage return is refused (git would read x\r as x).
        Assert.Equal(("spaced\n", "spaced\n"), (Text(await Show(web, "two words.txt", "HEAD")), Text(await Show(web, "two words.txt", ":"))));
        Assert.Equal("dashed\n", Text(await Show(web, "-n.txt", "HEAD")));
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(web, "/api/git/show", ("path", "cr.txt\r"), ("rev", "HEAD"))).StatusCode);

        // Staged only: not at HEAD, in the index.
        var notYet = await Show(web, "staged.txt", "HEAD");
        Assert.Equal((false, (string?)null), (notYet["exists"]!.GetValue<bool>(), Text(notYet)));
        Assert.Equal("new\n", Text(await Show(web, "staged.txt", ":")));
        Assert.False((await Show(web, "staged.txt", first))["exists"]!.GetValue<bool>());

        // Binary, not UTF-8, larger than the editor opens: no text, and why.
        foreach (var file in (string[])["logo.png", "latin1.txt"])
        {
            var shown = await Show(web, file, "HEAD");
            Assert.Equal((true, true, (string?)null), (shown["exists"]!.GetValue<bool>(), shown["binary"]!.GetValue<bool>(), Text(shown)));
        }
        var big = await Show(web, "big.txt", "HEAD");
        Assert.Equal((true, false, (long)CodeArena.IdeFiles.MaxTextBytes + 1, (string?)null),
            (big["tooLarge"]!.GetValue<bool>(), big["binary"]!.GetValue<bool>(), big["size"]!.GetValue<long>(), Text(big)));

        // What could be read as an option, or more than a revision, is refused before git sees it.
        foreach (var rev in (string[])["-p", "--output=out.txt", "HEAD --output=out.txt", "HEAD out.txt", "HEAD:app.ts", "@{-1}", "main\n", new string('a', 201)])
        {
            var refused = await Get(web, "/api/git/show", ("path", "app.ts"), ("rev", rev));
            Assert.True(refused.StatusCode == HttpStatusCode.BadRequest, $"{rev}: {(int)refused.StatusCode}");
        }
        Assert.False(File.Exists(Path.Combine(h.Work, "out.txt")));
        foreach (var rev in (string[])["nosuch", "HEAD~5", "deadbeef"])
        {
            var unknown = await Get(web, "/api/git/show", ("path", "app.ts"), ("rev", rev));
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Contains("no_revision", await unknown.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(web, "/api/git/show", ("path", "../outside.txt"), ("rev", "HEAD"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(web, "/api/git/show", ("rev", "HEAD"))).StatusCode);
    }

    [Fact]
    public async Task Before_the_first_commit_HEAD_has_no_files_and_the_index_has_what_is_staged()
    {
        using var h = new Harness(_gateway, _mcp, c => c["autoCommit"] = false);
        Init(h.Work);
        h.Write("a.txt", "staged\n");
        Git(h.Work, "add", "-A");
        await using var web = await WebRun.StartAsync(h);

        var head = await Show(web, "a.txt", "HEAD");
        Assert.Equal((false, (string?)null, (string?)null), (head["exists"]!.GetValue<bool>(), head["commit"]?.GetValue<string>(), Text(head)));
        Assert.Equal("staged\n", Text(await Show(web, "a.txt", ":")));
        Assert.Equal(HttpStatusCode.NotFound, (await Get(web, "/api/git/show", ("path", "a.txt"), ("rev", "main"))).StatusCode);
        Assert.Empty((await web.GetJsonAsync("/api/git/log")).AsArray());
        Assert.Empty((await web.GetJsonAsync("/api/git/log?path=a.txt")).AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await Get(web, "/api/git/commit", ("hash", "HEAD"))).StatusCode);
    }

    [Fact]
    public async Task A_files_history_follows_it_across_a_rename_and_a_commit_lists_the_folders_files()
    {
        // The folder is part of a bigger repository: lib/ is outside it.
        using var h = new Harness(_gateway, _mcp, c => c["autoCommit"] = false);
        Init(h.Work);
        h.Cwd = Path.Combine(h.Work, "app");
        h.Write("app/old.txt", "one\ntwo\nthree\nfour\n");
        h.Write("lib/other.txt", "other\n");
        Commit(h.Work, "Start");
        var start = GitOut(h.Work, "rev-parse", "HEAD");
        Git(h.Work, "mv", "app/old.txt", "app/new.txt");
        Commit(h.Work, "Rename it");
        var rename = GitOut(h.Work, "rev-parse", "HEAD");
        h.Write("app/else.txt", "else\n");
        Commit(h.Work, "Something else");
        h.Write("app/new.txt", "one\ntwo\nthree\nfour\nfive\n");
        h.Write("lib/other.txt", "changed\n");
        Commit(h.Work, "Grow it");
        await using var web = await WebRun.StartAsync(h);

        string Entry(JsonNode? c) => $"{c!["subject"]}: {c["status"]} {c["pathAt"]}{(c["from"] is { } from ? $" from {from}" : "")}";
        var history = (await web.GetJsonAsync("/api/git/log?path=new.txt")).AsArray();
        Assert.Equal(["Grow it: M new.txt", "Rename it: R new.txt from old.txt", "Start: A old.txt"], history.Select(Entry));
        Assert.Equal(start[..7], history[2]!["hash"]!.GetValue<string>()[..7]);
        Assert.Equal("Pat Person", history[0]!["author"]!.GetValue<string>());
        Assert.Equal(["Grow it", "Rename it"], (await web.GetJsonAsync("/api/git/log?path=new.txt&limit=2")).AsArray().Select(c => c!["subject"]!.GetValue<string>()));
        // Without a file: the last commits, as before.
        var log = (await web.GetJsonAsync("/api/git/log")).AsArray();
        Assert.Equal(["Grow it", "Something else", "Rename it", "Start"], log.Select(c => c!["subject"]!.GetValue<string>()));
        Assert.Equal(["hash", "subject", "author", "at"], log[0]!.AsObject().Select(p => p.Key));
        Assert.Single((await web.GetJsonAsync("/api/git/log?limit=1")).AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(web, "/api/git/log", ("limit", "many"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(web, "/api/git/log", ("limit", "0"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(web, "/api/git/log", ("path", "../lib/other.txt"))).StatusCode);

        // Each side of a commit's change, by the folder's names.
        Assert.Equal("one\ntwo\nthree\nfour\n", Text(await Show(web, "old.txt", start)));
        Assert.False((await Show(web, "new.txt", start))["exists"]!.GetValue<bool>());
        Assert.Equal("one\ntwo\nthree\nfour\n", Text(await Show(web, "new.txt", rename)));

        // A commit's files: the root commit adds them; a rename says from where; files outside the folder are left out.
        string Changed(JsonNode commit) => string.Join(", ", commit["files"]!.AsArray().Select(f => $"{f!["status"]} {f["path"]}{(f["from"] is { } from ? $" from {from}" : "")}"));
        var root = await web.GetJsonAsync($"/api/git/commit?hash={start[..7]}");
        Assert.Equal((start, 0, "Start", "A old.txt"), (root["hash"]!.GetValue<string>(), root["parents"]!.AsArray().Count, root["subject"]!.GetValue<string>(), Changed(root)));
        var renamed = await web.GetJsonAsync($"/api/git/commit?hash={rename}");
        Assert.Equal((start, "R new.txt from old.txt"), (renamed["parents"]![0]!.GetValue<string>(), Changed(renamed)));
        var last = await web.GetJsonAsync("/api/git/commit?hash=HEAD");
        Assert.Equal(("Grow it", "Pat Person", "M new.txt", false), (last["subject"]!.GetValue<string>(), last["author"]!.GetValue<string>(), Changed(last), last["truncated"]!.GetValue<bool>()));
        Assert.True(DateTimeOffset.TryParse(last["at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(web, "/api/git/commit", ("hash", "--all"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(web, "/api/git/commit")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(web, "/api/git/commit", ("hash", "nosuch"))).StatusCode);
    }

    [Fact]
    public async Task Staging_a_text_changes_the_index_only_and_keeps_the_files_mode()
    {
        using var h = new Harness(_gateway, _mcp, c => c["autoCommit"] = false);
        Init(h.Work);
        h.Write("app.ts", "one\ntwo\nthree\n");
        var script = h.Write("run.sh", "#!/bin/sh\necho one\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        File.WriteAllBytes(Path.Combine(h.Work, "bom.txt"), [0xEF, 0xBB, 0xBF, (byte)'x', (byte)'\n']);
        h.Write("src/a.ts", "a\n");
        File.CreateSymbolicLink(Path.Combine(h.Work, "link.ts"), "app.ts");
        Git(h.Work, "add", "-A");
        Git(h.Work, "update-index", "--chmod=+x", "run.sh");
        Git(h.Work, "commit", "-q", "-m", "Start");
        h.Write("app.ts", "ONE\ntwo\nTHREE\n");
        h.Write("run.sh", "#!/bin/sh\necho two\n");
        File.WriteAllBytes(Path.Combine(h.Work, "bom.txt"), [0xEF, 0xBB, 0xBF, (byte)'y', (byte)'\n']);
        h.Write("new.ts", "first\nsecond\n");
        h.Write("-my notes.txt", "first\nsecond\n");
        await using var web = await WebRun.StartAsync(h);
        Task<HttpResponseMessage> StageText(JsonObject body) => web.PostAsync("/api/git/stage-text", body);
        async Task<Dictionary<string, (string? Staged, string? Changed)>> Staged(string path, string text) =>
            Files(await Json(await StageText(new JsonObject { ["path"] = path, ["text"] = text })));

        // One of the two changes staged: the index has it, the file keeps both.
        Assert.Equal(("M", "M"), (await Staged("app.ts", "ONE\ntwo\nthree\n"))["app.ts"]);
        Assert.Equal("ONE\ntwo\nthree\n", Text(await Show(web, "app.ts", ":")));
        Assert.Equal("ONE\ntwo\nTHREE\n", File.ReadAllText(Path.Combine(h.Work, "app.ts")));
        // Unstaged again: the index has HEAD's text.
        Assert.Equal(((string?)null, "M"), (await Staged("app.ts", "one\ntwo\nthree\n"))["app.ts"]);

        // An executable stays one, a new file is 100644, and a byte-order mark stays.
        Assert.Equal(("M", (string?)null), (await Staged("run.sh", "#!/bin/sh\necho two\n"))["run.sh"]);
        Assert.StartsWith("100755 ", GitOut(h.Work, "ls-files", "-s", "run.sh"));
        Assert.Equal(("A", "M"), (await Staged("new.ts", "first\n"))["new.ts"]);
        Assert.StartsWith("100644 ", GitOut(h.Work, "ls-files", "-s", "new.ts"));
        Assert.Equal("first\nsecond\n", File.ReadAllText(Path.Combine(h.Work, "new.ts")));
        Assert.Equal(("A", "M"), (await Staged("-my notes.txt", "first\n"))["-my notes.txt"]);
        Assert.Equal("first\n", Text(await Show(web, "-my notes.txt", ":")));
        Assert.Equal(("M", (string?)null), (await Staged("bom.txt", "y\n"))["bom.txt"]);

        // Outside the folder, no text, a folder, a link.
        Assert.Equal(HttpStatusCode.Forbidden, (await StageText(new JsonObject { ["path"] = "../x.txt", ["text"] = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await StageText(new JsonObject { ["path"] = "app.ts" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await StageText(new JsonObject { ["text"] = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await StageText(new JsonObject { ["path"] = "src", ["text"] = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await StageText(new JsonObject { ["path"] = "link.ts", ["text"] = "x" })).StatusCode);
        Assert.StartsWith("120000 ", GitOut(h.Work, "ls-files", "-s", "link.ts"));
    }

    [Fact]
    public async Task A_merge_lists_what_it_brought_and_a_staged_text_settles_a_conflict()
    {
        using var h = new Harness(_gateway, _mcp, c => c["autoCommit"] = false);
        Init(h.Work);
        h.Write("a.txt", "a\n");
        h.Write("b.txt", "b\n");
        h.Write("c.txt", "c\n");
        h.Write("docs/d.txt", "d\n");
        Commit(h.Work, "Start");
        Git(h.Work, "switch", "-q", "-c", "side");
        h.Write("b.txt", "b on the side\n");
        h.Write("c.txt", "c on the side\n");
        Commit(h.Work, "Side");
        Git(h.Work, "switch", "-q", "main");
        h.Write("a.txt", "a on main\n");
        Commit(h.Work, "Main");
        Git(h.Work, "merge", "-q", "--no-ff", "-m", "Merge side", "side");
        var merge = GitOut(h.Work, "rev-parse", "HEAD");
        // c.txt changed on both sides now: merging leaves it in conflict (stages 1, 2 and 3, none 0).
        Git(h.Work, "switch", "-q", "-c", "clash");
        h.Write("c.txt", "c on clash\n");
        Commit(h.Work, "Clash");
        Git(h.Work, "switch", "-q", "main");
        h.Write("c.txt", "c on main\n");
        Commit(h.Work, "Main again");
        var psi = new ProcessStartInfo("git") { WorkingDirectory = h.Work, RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "merge", "-q", "clash" } };
        using (var merging = Process.Start(psi)!)
        {
            merging.WaitForExit();
            Assert.NotEqual(0, merging.ExitCode);
        }
        Assert.Equal(3, GitOut(h.Work, "ls-files", "-s", "c.txt").Split('\n').Length);
        await using var web = await WebRun.StartAsync(h);

        // Against its first parent: what the side brought in.
        var merged = await web.GetJsonAsync($"/api/git/commit?hash={merge}");
        Assert.Equal((2, "M b.txt, M c.txt"), (merged["parents"]!.AsArray().Count, string.Join(", ", merged["files"]!.AsArray().Select(f => $"{f!["status"]} {f["path"]}"))));

        // A folder has no history of its own here: its files do.
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(web, "/api/git/log", ("path", "docs"))).StatusCode);
        Assert.Equal(["Start"], (await web.GetJsonAsync("/api/git/log?path=docs%2Fd.txt")).AsArray().Select(c => c!["subject"]!.GetValue<string>()));

        // In conflict, the index has no text of the file's own; staging one settles it, the file left with its markers.
        Assert.False((await Show(web, "c.txt", ":"))["exists"]!.GetValue<bool>());
        var settled = Files(await Json(await web.PostAsync("/api/git/stage-text", new JsonObject { ["path"] = "c.txt", ["text"] = "c settled\n" })));
        Assert.Equal(("M", "M"), settled["c.txt"]);
        Assert.Matches(@"^100644 [0-9a-f]+ 0\tc\.txt$", GitOut(h.Work, "ls-files", "-s", "c.txt"));
        Assert.Equal("c settled\n", Text(await Show(web, "c.txt", ":")));
        Assert.Contains("<<<<<<<", File.ReadAllText(Path.Combine(h.Work, "c.txt")));
    }

    [Fact]
    public async Task Part_of_an_agents_change_is_accepted_and_the_rest_stays_to_accept_or_revert()
    {
        using var h = new Harness(_gateway, _mcp);
        var a = h.Write("a.txt", "one\ntwo\nthree\n");
        var b = h.Write("b.txt", "four\n");
        _gateway.Answer = req => FakeGateway.Last(req) switch
        {
            "change them" => Reply.Call(("write_file", """{"path":"a.txt","content":"ONE\ntwo\nTHREE\n"}"""), ("write_file", """{"path":"b.txt","content":"FOUR\n"}""")),
            _ => Reply.Say("?"),
        };
        await using var web = await WebRun.StartAsync(h, "--mode", "auto-edit");
        await (await web.SendAsync("change them")).RestAsync();
        string Listed(JsonNode changes) => string.Join(", ", changes.AsArray().Select(c => $"{c!["path"]} +{c["added"]} -{c["removed"]}"));
        Assert.Equal("a.txt +2 -2, b.txt +1 -1", Listed(await web.GetJsonAsync("/api/changes")));
        // A before that is not a text is refused, not taken as the whole change.
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/changes/accept", new JsonObject { ["path"] = "a.txt", ["before"] = 5 })).StatusCode);
        Assert.Equal("a.txt +2 -2, b.txt +1 -1", Listed(await web.GetJsonAsync("/api/changes")));

        // The first line's change accepted: the third's is left, against the text with the first.
        var left = await Json(await web.PostAsync("/api/changes/accept", new JsonObject { ["path"] = "a.txt", ["before"] = "ONE\ntwo\nthree\n" }));
        Assert.Equal("a.txt +1 -1, b.txt +1 -1", Listed(left));
        var diff = await web.GetJsonAsync("/api/changes/diff?path=a.txt");
        Assert.Equal(("ONE\ntwo\nthree\n", "ONE\ntwo\nTHREE\n"), (diff["original"]!.GetValue<string>(), diff["modified"]!.GetValue<string>()));
        // Reverting the rest keeps the part accepted.
        await Json(await web.PostAsync("/api/changes/revert", new JsonObject { ["path"] = "a.txt" }));
        Assert.Equal("ONE\ntwo\nthree\n", File.ReadAllText(a));

        // Accepting up to the text the file has now accepts all of it.
        Assert.Empty((await Json(await web.PostAsync("/api/changes/accept", new JsonObject { ["path"] = "b.txt", ["before"] = "FOUR\n" }))).AsArray());
        Assert.Equal("FOUR\n", File.ReadAllText(b));
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/changes/accept", new JsonObject { ["path"] = "b.txt", ["before"] = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/changes/accept", new JsonObject { ["before"] = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/changes/accept", new JsonObject { ["path"] = "../x.txt", ["before"] = "x" })).StatusCode);
    }

    [Fact]
    public async Task The_compare_calls_take_only_this_runs_key_from_its_own_page()
    {
        using var h = new Harness(_gateway, _mcp, c => c["autoCommit"] = false);
        Init(h.Work);
        h.Write("inside.txt", "in\n");
        Commit(h.Work, "In");
        await using var web = await WebRun.StartAsync(h);
        (string Method, string Path, JsonObject? Body)[] calls =
        [
            ("GET", "/api/git/show?path=inside.txt&rev=HEAD", null),
            ("GET", "/api/git/log?path=inside.txt", null),
            ("GET", "/api/git/commit?hash=HEAD", null),
            ("POST", "/api/git/stage-text", new() { ["path"] = "inside.txt", ["text"] = "owned" }),
            ("POST", "/api/changes/accept", new() { ["path"] = "inside.txt", ["before"] = "owned" }),
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
                    request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
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
            if (body is not null)
            {
                await Expect(HttpStatusCode.UnsupportedMediaType, "as a form", web.Http, r => r.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "text/plain"));
            }
        }
        Assert.Empty(wrong);
        // None of it happened.
        Assert.Equal("in\n", Text(await Show(web, "inside.txt", ":")));
    }
}
