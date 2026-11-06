using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace CodeArena.Tests;

/// <summary>
/// The Python tool (run_code) on the tasks a model gives it: state kept from one call to the next, the project's own
/// virtual environment, a value shown as a notebook shows it, an error said back, a plot seen. Each call's result and
/// time printed for docs/measurements/code-arena-tools.md.
/// </summary>
public sealed class CodePythonTests(ITestOutputHelper output) : IDisposable
{
    private readonly FakeGateway _gateway = new();

    public void Dispose() => _gateway.Dispose();

    /// <summary>The model's calls, one a round, then its answer: each call's result and how long it took.</summary>
    private async Task<List<(string Result, TimeSpan Took)>> Calls(Harness h, params string[] programs)
    {
        var said = new List<DateTime>();
        _gateway.Answer = req =>
        {
            said.Add(DateTime.UtcNow);
            var done = FakeGateway.History(req).Count(m => m["role"]!.GetValue<string>() == "tool");
            return done < programs.Length
                ? Reply.Call(("run_code", programs[done].StartsWith('{') ? programs[done] : new JsonObject { ["code"] = programs[done] }.ToJsonString()))
                : Reply.Say("done");
        };
        Assert.Equal(0, await h.Run("go\n/exit\n", "chat", "--mode", "yolo"));
        var results = FakeGateway.History(_gateway.Requests[^1]).Where(m => m["role"]!.GetValue<string>() == "tool").Select(m => m["content"]!.ToString()).ToList();
        var runs = results.Select((r, i) => (r, said[i + 1] - said[i])).ToList();
        foreach (var (program, (result, took)) in programs.Zip(runs))
        {
            output.WriteLine($"| {Fmt.OneLine(program, 50)} | {took.TotalMilliseconds:0} ms | {Fmt.OneLine(result, 90)} |");
        }
        return runs;
    }

    [PythonFact]
    public async Task Variables_stay_from_one_call_to_the_next_a_value_shows_and_an_error_says_its_line()
    {
        using var h = new Harness(_gateway);
        var runs = await Calls(h,
            "import json\nrows = [{'n': i, 'sq': i * i} for i in range(5)]\ntotal = sum(r['sq'] for r in rows)",
            "total + 0.5",
            "print(rows[-1])\nmissing_name + 1",
            "print('still here', total)");
        Assert.Contains("30.5", runs[1].Result);
        Assert.Contains("{'n': 4, 'sq': 16}", runs[2].Result);
        Assert.Contains("NameError: name 'missing_name' is not defined", runs[2].Result);
        Assert.Contains("line 2", runs[2].Result);
        Assert.Contains("still here 30", runs[3].Result);
    }

    [PythonFact]
    public async Task The_projects_virtual_environment_is_the_python_that_runs()
    {
        using var h = new Harness(_gateway);
        var venv = Path.Combine(h.Work, ".venv");
        using (var make = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3", ["-m", "venv", "--without-pip", venv]) { UseShellExecute = false })!)
        {
            Assert.True(make.WaitForExit(60_000));
            Assert.Equal(0, make.ExitCode);
        }
        var site = Directory.EnumerateDirectories(Path.Combine(venv, OperatingSystem.IsWindows() ? "Lib" : "lib")).Select(d => OperatingSystem.IsWindows() ? d : Path.Combine(d, "site-packages")).First();
        Directory.CreateDirectory(Path.Combine(site, "shopkit"));
        File.WriteAllText(Path.Combine(site, "shopkit", "__init__.py"), "VERSION = '2.4.1'\n");
        var runs = await Calls(h, "import shopkit, sys\nprint(shopkit.VERSION, sys.prefix.endswith('.venv'))");
        Assert.Contains("2.4.1 True", runs[0].Result);
    }

    [PythonFact]
    public async Task A_plot_is_seen_by_the_model_and_kept_as_a_file()
    {
        using var h = new Harness(_gateway);
        // matplotlib's pyplot as far as the interpreter uses it, where there is no matplotlib (the project's folder is on its path).
        h.Write("matplotlib/__init__.py", "");
        h.Write("matplotlib/pyplot.py", """
            _figures = []
            class _Figure:
                def savefig(self, path, **kw):
                    open(path, "wb").write(bytes.fromhex("89504e470d0a1a0a"))
            def plot(*a, **k):
                _figures.append(_Figure())
            def title(*a): pass
            def show(): pass
            def get_fignums(): return list(range(1, len(_figures) + 1))
            def figure(num): return _figures[num - 1]
            def close(which=None): _figures.clear()
            """);
        var runs = await Calls(h, "import matplotlib.pyplot as plt\nplt.plot([1, 2, 3], [3, 1, 2])\nplt.title('sales')\nplt.show()", "plt.get_fignums()");
        Assert.Matches(@"\[plot kept as \.code-arena/plots/plot-\d+-1-1\.png: shown to you below\]", runs[0].Result);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(h.Work, ".code-arena", "plots"), "*.png"));
        // The picture follows the result (or, for a model that cannot see, a note that says so).
        Assert.Contains(_gateway.Requests[1]["messages"]!.AsArray(), m => m!["content"] is JsonArray parts && parts.Any(p => p!["type"]?.GetValue<string>() == "image_url")
            || m["content"]?.ToString().Contains("cannot see pictures", StringComparison.Ordinal) == true);
        // Saved figures are closed: the next call starts with none.
        Assert.StartsWith("[]", runs[1].Result);
    }

    [PythonFact]
    public async Task A_call_past_its_time_stops_the_interpreter_exit_ends_it_and_fresh_starts_anew()
    {
        using var h = new Harness(_gateway);
        var runs = await Calls(h,
            "kept = 1",
            """{"code":"import time\ntime.sleep(30)","timeout_seconds":1}""",
            "print(kept)",
            "kept = 2",
            "import sys\nsys.exit(3)",
            "print('kept' in dir())",
            "import os\nos._exit(4)",
            "print('kept' in dir())",
            "kept = 3",
            """{"code":"print('kept' in dir())","fresh":true}""");
        Assert.Contains("timed out after 1 s: the interpreter was stopped", runs[1].Result);
        Assert.True(runs[1].Took < TimeSpan.FromSeconds(10), runs[1].Took.ToString());
        Assert.Contains("NameError: name 'kept' is not defined", runs[2].Result);
        // sys.exit ends the program, not the interpreter (as in a notebook); os._exit ends the interpreter.
        Assert.Contains("[exit code 3 (sys.exit", runs[4].Result);
        Assert.Contains("True", runs[5].Result);
        Assert.Contains("the interpreter ended with exit code 4", runs[6].Result);
        Assert.Contains("False", runs[7].Result);
        Assert.Contains("False", runs[9].Result);
    }

    [Fact]
    public async Task On_tcp_the_bridge_answers_only_a_program_that_says_its_token()
    {
        await using var bridge = new ToolBridge(null, (name, args, _) => Task.FromResult(new JsonObject { ["ok"] = true, ["result"] = name }));
        Assert.StartsWith("tcp:127.0.0.1:", bridge.Path);
        var port = int.Parse(bridge.Path[(bridge.Path.LastIndexOf(':') + 1)..]);
        async Task<string?> Ask(string token)
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            await using var writer = new StreamWriter(stream) { AutoFlush = true };
            await writer.WriteLineAsync(token);
            await writer.WriteLineAsync("""{"name":"read_file","args":{}}""");
            return await reader.ReadLineAsync();
        }
        Assert.Null(await Ask("not-the-token"));
        Assert.Equal("""{"ok":true,"result":"read_file"}""", await Ask(bridge.Token!));
        Assert.Equal(["read_file"], bridge.Calls);
    }
}
