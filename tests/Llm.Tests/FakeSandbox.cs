using System.Text.Json.Nodes;

namespace Llm.Tests;

/// <summary>
/// The sandbox's runner, as the app sees it: the same files in the same places
/// (services/sandbox/runner.py), without running any Python. What a job does:
///   stdout lists the files it was given and echoes its first line;
///   "# chart" writes a picture, "# csv" a CSV, "# binary" a file that is neither;
///   "# fail" exits 1 with a traceback; "# slow" runs until it is stopped.
/// </summary>
public sealed class FakeSandbox : IAsyncDisposable
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public string Dir { get; } = Directory.CreateTempSubdirectory("llm-sandbox-").FullName;
    public List<JsonObject> Jobs { get; } = [];

    public FakeSandbox(bool alive = true)
    {
        foreach (var d in new[] { "in", "out", "cancel" })
        {
            Directory.CreateDirectory(Path.Combine(Dir, d));
        }
        _loop = alive ? Task.Run(LoopAsync) : Task.CompletedTask;
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            await File.WriteAllTextAsync(Path.Combine(Dir, "heartbeat"), new JsonObject { ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, ["slots"] = 2, ["busy"] = 0 }.ToJsonString());
            foreach (var job in Directory.GetDirectories(Path.Combine(Dir, "in")).Where(d => !Path.GetFileName(d).StartsWith('.')))
            {
                await RunAsync(job);
            }
            try
            {
                await Task.Delay(50, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunAsync(string job)
    {
        var id = Path.GetFileName(job);
        var spec = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(job, "job.json")))!.AsObject();
        lock (Jobs)
        {
            Jobs.Add(spec);
        }
        var code = spec["code"]!.GetValue<string>();
        var given = Directory.GetFiles(Path.Combine(job, "files")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        var draft = Path.Combine(Dir, "out", ".tmp-" + id);
        Directory.CreateDirectory(Path.Combine(draft, "files"));
        var cancelled = false;
        if (code.Contains("# slow", StringComparison.Ordinal))
        {
            for (var i = 0; i < 200 && !(cancelled = File.Exists(Path.Combine(Dir, "cancel", id))); i++)
            {
                await Task.Delay(50);
            }
        }
        if (code.Contains("# chart", StringComparison.Ordinal))
        {
            await File.WriteAllBytesAsync(Path.Combine(draft, "files", "figure-1.png"), Png);
        }
        if (code.Contains("# csv", StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(Path.Combine(draft, "files", "summary.csv"), "month,total\n1,42\n");
        }
        if (code.Contains("# binary", StringComparison.Ordinal))
        {
            await File.WriteAllBytesAsync(Path.Combine(draft, "files", "data.bin"), [0, 1, 2, 3, 0, 255]);
        }
        var fail = code.Contains("# fail", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(draft, "result.json"), new JsonObject
        {
            ["exit_code"] = fail ? 1 : cancelled ? -9 : 0, ["timed_out"] = false, ["cancelled"] = cancelled,
            ["stdout"] = $"given: {string.Join(",", given)}\nfirst line: {code.Split('\n')[0]}\n", ["stdout_cut"] = false,
            ["stderr"] = fail ? "Traceback (most recent call last):\n  File \"main.py\", line 1\nValueError: bad\n" : "", ["stderr_cut"] = false,
            ["killed"] = null, ["error"] = null, ["skipped_files"] = new JsonArray(), ["duration_ms"] = 12,
            ["files"] = new JsonArray([.. Directory.GetFiles(Path.Combine(draft, "files")).Select(f => (JsonNode)Path.GetFileName(f))]),
        }.ToJsonString());
        Directory.Delete(job, recursive: true);
        Directory.Move(draft, Path.Combine(Dir, "out", id));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }
        _stop.Dispose();
        Directory.Delete(Dir, recursive: true);
    }
}
