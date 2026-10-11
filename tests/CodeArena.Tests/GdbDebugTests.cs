using System.Diagnostics;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>A test that needs gdb 14 or later (its own debug adapter, gdb -i dap) and a C compiler on PATH: skipped where either is missing.</summary>
public sealed class GdbFactAttribute : FactAttribute
{
    public GdbFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "gdb's debug adapter is checked on Linux and macOS.";
        }
        else if (Run("cc", "--version") is null)
        {
            Skip = "No C compiler (cc) on PATH.";
        }
        else if (Run("gdb", "--version") is not { } version || !int.TryParse(System.Text.RegularExpressions.Regex.Match(version.Split('\n')[0], @"(\d+)\.\d+[^ ]*$").Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major) || major < 14)
        {
            Skip = "No gdb 14 or later (with its debug adapter, gdb -i dap) on PATH.";
        }
    }

    /// <summary>What the program printed, or null when it is not there or failed.</summary>
    internal static string? Run(string program, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            return p.WaitForExit(10_000) && p.ExitCode == 0 ? output.Result : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>A real C program debugged through the real server with gdb's own debug adapter, the page's side spoken by the test.</summary>
public sealed class GdbDebugTests : IDisposable
{
    private const string Source = """
        #include <stdio.h>

        int add(int a, int b) {
            int sum = a + b;
            return sum;
        }

        int main(void) {
            printf("sum=%d\n", add(2, 3));
            return 0;
        }

        """;

    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    [GdbFact]
    public async Task A_breakpoint_stops_the_program_where_its_stack_and_values_can_be_read_then_it_runs_to_its_end()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Env["PATH"] = Environment.GetEnvironmentVariable("PATH");
        h.Write("main.c", Source);
        var cc = Process.Start(new ProcessStartInfo("cc", ["-g", "-O0", "-o", "app", "main.c"]) { WorkingDirectory = h.Work })!;
        await cc.WaitForExitAsync();
        Assert.Equal(0, cc.ExitCode);
        await using var web = await WebRun.StartAsync(h);

        var started = await Json(await web.PostAsync("/api/debug/start", new JsonObject
        {
            ["configuration"] = new JsonObject { ["name"] = "app", ["type"] = "cppdbg", ["request"] = "launch", ["program"] = Path.Combine(h.Work, "app"), ["MIMode"] = "gdb" },
        }));
        Assert.Equal("gdb", started["adapter"]!.GetValue<string>());
        var root = started["rootReal"]!.GetValue<string>();
        using var page = await DebugSocket.ConnectAsync(web, started["id"]!.GetValue<string>());

        // The handshake as the page does it: the launch is not waited for (gdb answers it after configurationDone).
        var initialize = await page.RequestAsync("initialize", new JsonObject
        {
            ["clientID"] = "code-arena", ["adapterID"] = started["adapterId"]!.DeepClone(), ["linesStartAt1"] = true, ["columnsStartAt1"] = true, ["pathFormat"] = "path",
        });
        Assert.True(initialize["success"]!.GetValue<bool>(), initialize.ToJsonString());
        var launch = await page.SendAsync("launch", started["configuration"]!.DeepClone().AsObject());
        await page.EventAsync("initialized");
        var breakpoints = await page.RequestAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = Path.Combine(h.Work, "main.c") },
            ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = 4 }),
        });
        Assert.True(breakpoints["success"]!.GetValue<bool>(), breakpoints.ToJsonString());
        await page.RequestAsync("configurationDone");
        await page.UntilAsync(m => DebugSocket.Is(m, "response") && m["request_seq"]!.GetValue<int>() == launch, "the launch's answer");

        var stopped = await page.EventAsync("stopped");
        Assert.Equal("breakpoint", stopped["body"]!["reason"]!.GetValue<string>());
        await page.EventAsync("codeArena.state", e => e["body"]!["state"]!.GetValue<string>() == "stopped");
        var thread = stopped["body"]!["threadId"]!.GetValue<int>();

        var stack = await page.RequestAsync("stackTrace", new JsonObject { ["threadId"] = thread, ["startFrame"] = 0, ["levels"] = 20 });
        var top = stack["body"]!["stackFrames"]![0]!;
        Assert.Equal(4, top["line"]!.GetValue<int>());
        Assert.StartsWith("add", top["name"]!.GetValue<string>());
        // The page shows it as the folder's main.c.
        Assert.Equal("main.c", Path.GetRelativePath(root, top["source"]!["path"]!.GetValue<string>()));
        Assert.False((await web.GetJsonAsync($"/api/debug/source?id={started["id"]}&path={Uri.EscapeDataString(top["source"]!["path"]!.GetValue<string>())}"))["readOnly"]!.GetValue<bool>());

        var sum = await page.RequestAsync("evaluate", new JsonObject { ["expression"] = "a+b", ["frameId"] = top["id"]!.DeepClone(), ["context"] = "watch" });
        Assert.Equal("5", sum["body"]!["result"]!.GetValue<string>());

        await page.RequestAsync("continue", new JsonObject { ["threadId"] = thread });
        await page.EventAsync("output", e => e["body"]!["output"]!.GetValue<string>().Contains("sum=5", StringComparison.Ordinal));
        Assert.Equal(0, (await page.EventAsync("exited"))["body"]!["exitCode"]!.GetValue<int>());
        await page.EventAsync("terminated");
        await page.RequestAsync("disconnect", new JsonObject { ["terminateDebuggee"] = true });
        await page.Closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var ended = await page.EventAsync("codeArena.state", e => e["body"]!["state"]!.GetValue<string>() == "ended");
        Assert.Null(ended["body"]!["error"]);
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
}
