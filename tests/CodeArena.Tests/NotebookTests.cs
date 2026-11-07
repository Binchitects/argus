using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>A notebook's kernel in the IDE: one Python per notebook, its state kept between cells, Jupyter's outputs.</summary>
public sealed class NotebookTests : IDisposable
{
    private readonly FakeGateway _gateway = new();

    public void Dispose() => _gateway.Dispose();

    /// <summary>A cell run to its end: its outputs, read as the page reads them (each answer once something is new).</summary>
    private static async Task<JsonNode> RunAsync(WebRun web, string notebook, string code)
    {
        var started = await web.PostAsync("/api/notebook/execute", new JsonObject { ["path"] = notebook, ["code"] = code });
        var said = await started.Content.ReadAsStringAsync();
        Assert.True(started.StatusCode == HttpStatusCode.OK, said);
        var id = JsonNode.Parse(said)!["run"]!.GetValue<string>();
        var since = 0;
        for (var i = 0; i < 200; i++)
        {
            var read = await web.GetJsonAsync($"/api/notebook/run?id={id}&since={since}");
            if (read["done"]!.GetValue<bool>())
            {
                return read;
            }
            since = read["version"]!.GetValue<int>();
        }
        throw new TimeoutException("The cell did not end.");
    }

    private static List<JsonNode> Outputs(JsonNode run) => [.. run["outputs"]!.AsArray().Select(o => o!)];

    [PythonFact]
    public async Task Cells_run_in_one_python_per_notebook_with_jupyters_outputs_and_errors()
    {
        using var h = new Harness(_gateway);
        h.Write("nb/a.ipynb", "{}");
        h.Write("nb/data.txt", "from the notebook's folder\n");
        h.Write("b.txt", "not a notebook");
        await using var web = await WebRun.StartAsync(h);

        // Not in an untrusted folder; only a notebook has a kernel.
        var refused = await web.PostAsync("/api/notebook/execute", new JsonObject { ["path"] = "nb/a.ipynb", ["code"] = "1" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("not trusted", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/workspace/trust", new JsonObject { ["trusted"] = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/notebook/execute", new JsonObject { ["path"] = "b.txt", ["code"] = "1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.PostAsync("/api/notebook/execute", new JsonObject { ["path"] = "../x.ipynb", ["code"] = "1" })).StatusCode);
        Assert.False((await web.GetJsonAsync("/api/notebook/kernel?path=nb%2Fa.ipynb"))["running"]!.GetValue<bool>());

        // What it prints, then its last line's value, counted; in the notebook's folder.
        var first = await RunAsync(web, "nb/a.ipynb", "x = 2\nprint('hi')\nprint(open('data.txt').read().strip())\nx * 21");
        Assert.True(first["ok"]!.GetValue<bool>());
        Assert.Equal(1, first["count"]!.GetValue<int>());
        var outs = Outputs(first);
        Assert.Equal(("stream", "stdout", "hi\nfrom the notebook's folder\n"), (outs[0]["output_type"]!.GetValue<string>(), outs[0]["name"]!.GetValue<string>(), outs[0]["text"]!.GetValue<string>()));
        Assert.Equal(("execute_result", 1, "42"), (outs[1]["output_type"]!.GetValue<string>(), outs[1]["execution_count"]!.GetValue<int>(), outs[1]["data"]!["text/plain"]!.GetValue<string>()));

        // Its variables stay; a ; at the end shows nothing; display() and an object's richest form; what a program
        // writes itself; stderr apart.
        var second = await RunAsync(web, "nb/a.ipynb", """
            import os, sys
            class Table:
                def _repr_html_(self):
                    return "<table><tr><td>1</td></tr></table>"
            display(Table())
            os.system("echo raw")
            print("careful", file=sys.stderr)
            x + 1;
            """);
        var shown = Outputs(second);
        Assert.Equal(2, second["count"]!.GetValue<int>());
        var display = shown.Single(o => o["output_type"]!.GetValue<string>() == "display_data");
        Assert.Equal("<table><tr><td>1</td></tr></table>", display["data"]!["text/html"]!.GetValue<string>());
        Assert.StartsWith("<", display["data"]!["text/plain"]!.GetValue<string>());
        Assert.Contains(shown, o => o["output_type"]!.GetValue<string>() == "stream" && o["name"]!.GetValue<string>() == "stdout" && o["text"]!.GetValue<string>().Contains("raw\n", StringComparison.Ordinal));
        Assert.Contains(shown, o => o["output_type"]!.GetValue<string>() == "stream" && o["name"]!.GetValue<string>() == "stderr" && o["text"]!.GetValue<string>() == "careful\n");
        Assert.DoesNotContain(shown, o => o["output_type"]!.GetValue<string>() == "execute_result");
        Assert.Equal("3", (await RunAsync(web, "nb/a.ipynb", "x + 1"))["outputs"]![0]!["data"]!["text/plain"]!.GetValue<string>());

        // An error: its name, value and traceback, the cell's own lines in it.
        var failed = await RunAsync(web, "nb/a.ipynb", "def f():\n    return 1 / 0\nf()");
        Assert.False(failed["ok"]!.GetValue<bool>());
        var error = Outputs(failed).Single();
        Assert.Equal(("error", "ZeroDivisionError", "division by zero"), (error["output_type"]!.GetValue<string>(), error["ename"]!.GetValue<string>(), error["evalue"]!.GetValue<string>()));
        var traceback = string.Join("", error["traceback"]!.AsArray().Select(l => l!.GetValue<string>()));
        Assert.Contains("return 1 / 0", traceback);
        Assert.DoesNotContain("kernel.py", traceback);

        var kernel = await web.GetJsonAsync("/api/notebook/kernel?path=nb%2Fa.ipynb");
        Assert.True(kernel["running"]!.GetValue<bool>());
        Assert.False(kernel["busy"]!.GetValue<bool>());

        // Restart: a new Python, nothing kept, counting from 1 again.
        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/notebook/restart", new JsonObject { ["path"] = "nb/a.ipynb" })).StatusCode);
        var fresh = await RunAsync(web, "nb/a.ipynb", "x");
        Assert.Equal(("NameError", 1), (Outputs(fresh).Single()["ename"]!.GetValue<string>(), fresh["count"]!.GetValue<int>()));
    }

    [PythonFact]
    public async Task Interrupt_stops_the_cell_running_and_the_kernel_carries_on()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var h = new Harness(_gateway);
        h.Write("a.ipynb", "{}");
        await using var web = await WebRun.StartAsync(h);
        await web.PostAsync("/api/workspace/trust", new JsonObject { ["trusted"] = true });
        await RunAsync(web, "a.ipynb", "kept = 'yes'");

        var started = await web.PostAsync("/api/notebook/execute", new JsonObject { ["path"] = "a.ipynb", ["code"] = "import time\nprint('going', flush=True)\nwhile True:\n    time.sleep(0.05)" });
        var id = JsonNode.Parse(await started.Content.ReadAsStringAsync())!["run"]!.GetValue<string>();
        // Once it prints, it is in the loop: then interrupted.
        JsonNode read = await web.GetJsonAsync($"/api/notebook/run?id={id}&since=0");
        for (var i = 0; i < 100 && !Outputs(read).Any(o => o["output_type"]!.GetValue<string>() == "stream"); i++)
        {
            read = await web.GetJsonAsync($"/api/notebook/run?id={id}&since={read["version"]!.GetValue<int>()}");
        }
        Assert.True((await web.GetJsonAsync("/api/notebook/kernel?path=a.ipynb"))["busy"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await web.PostAsync("/api/notebook/interrupt", new JsonObject { ["path"] = "a.ipynb" })).StatusCode);
        while (!read["done"]!.GetValue<bool>())
        {
            read = await web.GetJsonAsync($"/api/notebook/run?id={id}&since={read["version"]!.GetValue<int>()}");
        }
        Assert.Equal("KeyboardInterrupt", Outputs(read).Single(o => o["output_type"]!.GetValue<string>() == "error")["ename"]!.GetValue<string>());
        Assert.Equal("'yes'", (await RunAsync(web, "a.ipynb", "kept"))["outputs"]![0]!["data"]!["text/plain"]!.GetValue<string>());
    }
}
