using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// The IDE's Review view: a reviewer with the reading tools reports its findings through report_review; they are
/// checked against the files, listed worst first, marked fixed or dismissed, and shown as Problems while open.
/// </summary>
public sealed class ReviewTests : IDisposable
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

    private static bool Reviewing(JsonObject request) =>
        request["tools"] is JsonArray tools && tools.Any(t => t!["function"]!["name"]!.GetValue<string>() == "report_review");

    private static int Results(JsonObject request) => request["messages"]!.AsArray().Count(m => m!["role"]!.GetValue<string>() == "tool");

    private static async Task<JsonNode> DoneAsync(WebRun web)
    {
        for (var waited = 0; waited < 20_000; waited += 50)
        {
            var review = (await web.GetJsonAsync("/api/review"))["review"]!;
            if (!review["running"]!.GetValue<bool>())
            {
                return review;
            }
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException("The review did not end.");
    }

    [Fact]
    public async Task A_review_reports_findings_on_lines_with_fixes_that_fit_and_they_are_problems_while_open()
    {
        using var h = new Harness(_gateway, _mcp, c => c["autoCommit"] = false);
        Git(h.Work, "init", "-q", "-b", "main");
        Git(h.Work, "config", "user.name", "Pat Person");
        Git(h.Work, "config", "user.email", "pat@example.test");
        h.Write("src/cart.ts", "export function total(items) {\n  return items.reduce((s, i) => s + i.price, 0)\n}\n");
        Git(h.Work, "add", "-A");
        Git(h.Work, "commit", "-q", "-m", "First");
        h.Write("src/cart.ts", "export function total(items) {\n  return items.reduce((s, i) => s + i.price * i.qty)\n}\n");
        var findings = new JsonArray(
            new JsonObject
            {
                ["path"] = "src/cart.ts", ["line"] = 2, ["severity"] = "major", ["title"] = "reduce has no start value",
                ["why"] = "An empty cart throws.", ["fix"] = "Start the sum at 0.",
                ["old"] = "  return items.reduce((s, i) => s + i.price * i.qty)\n", ["new"] = "  return items.reduce((s, i) => s + i.price * i.qty, 0)\n",
            },
            new JsonObject { ["path"] = "src/cart.ts", ["line"] = 99, ["severity"] = "nit", ["title"] = "Name", ["why"] = "w", ["fix"] = "f", ["old"] = "not in the file", ["new"] = "x" },
            new JsonObject { ["path"] = "../outside.ts", ["line"] = 1, ["severity"] = "critical", ["title"] = "Outside", ["why"] = "w", ["fix"] = "f" },
            new JsonObject { ["path"] = "src/gone.ts", ["line"] = 1, ["severity"] = "critical", ["title"] = "Gone", ["why"] = "w", ["fix"] = "f" });
        _gateway.Answer = request => !Reviewing(request) ? Reply.Say("Hello.") : Results(request) switch
        {
            0 => Reply.Call(("git", """{"args":["diff","HEAD"]}"""), ("read_file", """{"path":"src/cart.ts"}""")),
            2 => Reply.Call(("report_review", new JsonObject { ["verdict"] = "changes needed", ["summary"] = "Counts quantities; breaks empty carts.", ["findings"] = findings }.ToJsonString())),
            _ => Reply.Say("Reviewed."),
        };
        await using var web = await WebRun.StartAsync(h);
        Assert.Null((await web.GetJsonAsync("/api/review"))["review"]);

        var started = await web.PostAsync("/api/review", new JsonObject());
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var review = await DoneAsync(web);
        Assert.Null(review["error"]);
        Assert.Null(review["base"]);
        Assert.Equal(("changes needed", "Counts quantities; breaks empty carts."), (review["verdict"]!.GetValue<string>(), review["summary"]!.GetValue<string>()));
        Assert.Equal(["git diff HEAD", "read_file src/cart.ts"], review["steps"]!.AsArray().Select(s => s!.GetValue<string>()));
        // The review read the uncommitted changes, with the reading tools only.
        var asked = _gateway.Requests.First(Reviewing);
        Assert.Contains("git diff HEAD", asked["messages"]!.AsArray()[1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        var tools = asked["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("edit_file", tools);
        Assert.DoesNotContain("run_shell", tools);

        // Only the findings on files of the folder; worst first; a line past the end at the last; a fix kept only where it fits.
        var listed = review["findings"]!.AsArray();
        Assert.Equal(["reduce has no start value", "Name"], listed.Select(f => f!["title"]!.GetValue<string>()));
        Assert.Equal((2, "major", "open"), (listed[0]!["line"]!.GetValue<int>(), listed[0]!["severity"]!.GetValue<string>(), listed[0]!["state"]!.GetValue<string>()));
        Assert.Equal("  return items.reduce((s, i) => s + i.price * i.qty, 0)\n", listed[0]!["new"]!.GetValue<string>());
        Assert.Equal(4, listed[1]!["line"]!.GetValue<int>());
        Assert.Null(listed[1]!["old"]);

        // Problems list the open ones, as the editor marks them.
        var problems = (await web.GetJsonAsync("/api/problems"))["problems"]!.AsArray().Where(p => p!["source"]!.GetValue<string>() == "review").ToList();
        Assert.Equal([("src/cart.ts", 2, "error"), ("src/cart.ts", 4, "info")], problems.Select(p => (p!["path"]!.GetValue<string>(), p["line"]!.GetValue<int>(), p["severity"]!.GetValue<string>())));
        var marked = JsonNode.Parse(await (await web.PostAsync("/api/review/finding", new JsonObject { ["index"] = 0, ["state"] = "fixed" })).Content.ReadAsStringAsync())!;
        Assert.Equal("fixed", marked["review"]!["findings"]![0]!["state"]!.GetValue<string>());
        await web.PostAsync("/api/review/finding", new JsonObject { ["index"] = 1, ["state"] = "dismissed" });
        Assert.DoesNotContain((await web.GetJsonAsync("/api/problems"))["problems"]!.AsArray(), p => p!["source"]!.GetValue<string>() == "review");
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/review/finding", new JsonObject { ["index"] = 9, ["state"] = "fixed" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/review/finding", new JsonObject { ["index"] = 0, ["state"] = "gone" })).StatusCode);
        // Nothing changed in the folder.
        Assert.Contains("i.qty)\n", File.ReadAllText(Path.Combine(h.Work, "src/cart.ts")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_branch_is_reviewed_over_its_base_one_review_at_a_time_and_a_reviewer_that_reports_nothing_says_so()
    {
        using var h = new Harness(_gateway, _mcp, c => c["autoCommit"] = false);
        Git(h.Work, "init", "-q", "-b", "main");
        _gateway.Hang = true;
        await using var web = await WebRun.StartAsync(h);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/review", new JsonObject { ["base"] = "--upload-pack=x" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/review", new JsonObject { ["base"] = "main" })).StatusCode);
        await _gateway.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains("git diff main...HEAD", _gateway.Requests.First(Reviewing)["messages"]!.AsArray()[1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsync("/api/review", new JsonObject())).StatusCode);
        await web.PostAsync("/api/review/stop", new JsonObject());
        var stopped = await DoneAsync(web);
        Assert.Equal(("main", "Stopped."), (stopped["base"]!.GetValue<string>(), stopped["error"]!.GetValue<string>()));

        _gateway.Hang = false;
        _gateway.Answer = _ => Reply.Say("All fine, I think.");
        await web.PostAsync("/api/review", new JsonObject());
        var silent = await DoneAsync(web);
        Assert.Equal("The reviewer ended without reporting its findings: All fine, I think.", silent["error"]!.GetValue<string>());
        Assert.Empty(silent["findings"]!.AsArray());
    }
}
