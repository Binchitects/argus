using System.Globalization;
using System.Text.Json.Nodes;

namespace Llm.Tests;

/// <summary>
/// The sandbox's runner, as the app sees it: the same files in the same places
/// (services/sandbox/runner.py), without running any Python. What a job does:
///   stdout lists the files it was given and echoes its first line;
///   "# chart" writes a picture, "# csv" a CSV, "# binary" a file that is neither;
///   "# fail" exits 1 with a traceback; "# slow" runs until it is stopped;
///   "# pages" draws the pages it asks for ("first, last = 1, 20") of a document of <see cref="DocumentPages"/> pages (page-01.jpg...);
///   the media script (it runs ffprobe) makes sound.mp3, and for a video two frames, 4 seconds long;
///   "# canvas pdf" turns canvas.docx into canvas.pdf (a PDF header, then the Word file's size);
///   the media studio's cut makes story.mp4, three seconds a shot.
/// </summary>
public sealed class FakeSandbox : IAsyncDisposable
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public string Dir { get; } = Directory.CreateTempSubdirectory("llm-sandbox-").FullName;
    public List<JsonObject> Jobs { get; } = [];

    /// <summary>How many pages a document drawn by "# pages" has.</summary>
    public int DocumentPages { get; set; } = 3;

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
        if (code.Contains("\nTURNTABLE = True\n", StringComparison.Ordinal))
        {
            // As the 3D sandbox leaves the turntable and the print files.
            await File.WriteAllBytesAsync(Path.Combine(draft, "files", "turntable.mp4"), [0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70]);
            await File.WriteAllBytesAsync(Path.Combine(draft, "files", "model.3mf"), [0x50, 0x4B, 3, 4]);
        }
        if (code.Contains("# glb", StringComparison.Ordinal))
        {
            // As the 3D sandbox leaves a model: its glTF and a picture of it.
            await File.WriteAllBytesAsync(Path.Combine(draft, "files", "model.glb"), [0x67, 0x6C, 0x54, 0x46, 2, 0, 0, 0]);
            await File.WriteAllBytesAsync(Path.Combine(draft, "files", "view-front.png"), Png);
        }
        var pages = code.Contains("# pages", StringComparison.Ordinal);
        if (pages)
        {
            var range = System.Text.RegularExpressions.Regex.Match(code, @"first, last = (\d+), (\d+)");
            var (first, last) = (int.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture));
            for (var n = first; n <= Math.Min(last, DocumentPages); n++)
            {
                await File.WriteAllBytesAsync(Path.Combine(draft, "files", $"page-{n:D2}.jpg"), [0xFF, 0xD8, 0xFF, (byte)n]);
            }
        }
        var media = code.Contains("ffprobe", StringComparison.Ordinal) && !code.StartsWith("# The media studio's video", StringComparison.Ordinal);
        if (media)
        {
            await File.WriteAllBytesAsync(Path.Combine(draft, "files", "sound.mp3"), [0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0, 0, 1, 2]);
            if ((await File.ReadAllTextAsync(Path.Combine(job, "files", "kind"))).Trim() == "video")
            {
                await File.WriteAllBytesAsync(Path.Combine(draft, "files", "frame-01.jpg"), [0xFF, 0xD8, 0xFF, 1]);
                await File.WriteAllBytesAsync(Path.Combine(draft, "files", "frame-02.jpg"), [0xFF, 0xD8, 0xFF, 2]);
            }
        }
        if (code.Contains("# canvas pdf", StringComparison.Ordinal) && File.Exists(Path.Combine(job, "files", "canvas.docx")))
        {
            await File.WriteAllTextAsync(Path.Combine(draft, "files", "canvas.pdf"), $"%PDF-1.7\n% from {new FileInfo(Path.Combine(job, "files", "canvas.docx")).Length} bytes\n%%EOF\n");
        }
        // Vector work: a traced picture (a background and two shapes, made parts: the things named with a map each, else
        // one part), a drawing drawn as a PNG, frames made a video.
        if (code.Contains("vtracer.convert_raw_image_to_svg", StringComparison.Ordinal))
        {
            var traceJob = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(job, "files", "job.json")))!;
            var ids = (traceJob["ids"] as JsonArray ?? []).Select(i => i!.GetValue<string>()).ToList();
            var named = ids.Count > 0 && Enumerable.Range(0, ids.Count).All(i => File.Exists(Path.Combine(job, "files", $"mask-{i}.png")));
            const string Red = """<path d="M200 200 H800 V800 H200Z" fill="#E33322" stroke="#E33322" stroke-width="1.46"/>""";
            const string Dark = """<path d="M400 400 H600 V600 H400Z" fill="#222222" stroke="#222222" stroke-width="1.46"/>""";
            var parts = named && ids.Count > 1 ? $"""<g id="{ids[0]}">{Red}</g><g id="{ids[1]}">{Dark}</g>""" : $"""<g id="{(named ? ids[0] : "part")}">{Red}{Dark}</g>""";
            await File.WriteAllTextAsync(Path.Combine(draft, "files", "traced.svg"),
                $"""<svg xmlns="http://www.w3.org/2000/svg" width="1024" height="1024" viewBox="0 0 1024 1024"><rect id="background" width="1024" height="1024" fill="#FFFFFF"/>{parts}</svg>""");
        }
        else if (code.Contains("resvg_py.svg_to_bytes", StringComparison.Ordinal))
        {
            var size = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(job, "files", "job.json")))!;
            await File.WriteAllBytesAsync(Path.Combine(draft, "files", "drawing.png"), PngOf(size["width"]!.GetValue<int>(), size["height"]!.GetValue<int>()));
        }
        if (code.Contains("palettegen", StringComparison.Ordinal))
        {
            var format = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(job, "files", "job.json")))!["format"]!.GetValue<string>();
            await File.WriteAllBytesAsync(Path.Combine(draft, "files", $"drawing.{format}"), format == "gif" ? [0x47, 0x49, 0x46, 0x38, 0x39, 0x61] : [0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70]);
        }
        // The media studio's cut: a video, and its length (three seconds a shot).
        var cut = code.StartsWith("# The media studio's video", StringComparison.Ordinal);
        string? cutReport = null;
        if (cut)
        {
            var shots = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(job, "files", "story.json")))!["shots"]!.AsArray().Count;
            await File.WriteAllBytesAsync(Path.Combine(draft, "files", "story.mp4"), [0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D]);
            cutReport = $"{{\"seconds\": {shots * 3}.0, \"starts\": [], \"error\": null}}\n";
        }
        var served = await ServeMcpAsync(code, job) ?? await LabAsync(code, job) ?? await CallToolsAsync(spec, code, id, given);
        var fail = code.Contains("# fail", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(draft, "result.json"), new JsonObject
        {
            ["exit_code"] = fail ? 1 : cancelled ? -9 : 0, ["timed_out"] = false, ["cancelled"] = cancelled,
            ["stdout"] = served ?? ($"given: {string.Join(",", given)}\nfirst line: {code.Split('\n')[0]}\n" + (pages ? $"{{\"total\": {DocumentPages}, \"error\": null}}\n" : "")
                + (media ? "{\"seconds\": 4.0, \"sound\": true, \"frames\": 2}\n" : "") + cutReport), ["stdout_cut"] = false,
            ["stderr"] = fail ? "Traceback (most recent call last):\n  File \"main.py\", line 1\nValueError: bad\n" : "", ["stderr_cut"] = false,
            ["killed"] = null, ["error"] = null, ["skipped_files"] = new JsonArray(), ["duration_ms"] = 12,
            ["files"] = new JsonArray([.. Directory.GetFiles(Path.Combine(draft, "files")).Select(f => (JsonNode)Path.GetFileName(f))]),
        }.ToJsonString());
        Directory.Delete(job, recursive: true);
        Directory.Move(draft, Path.Combine(Dir, "out", id));
    }

    /// <summary>
    /// A run_code program (a job with calls): each "# call NAME {json}" line is a call of the chat's tools, relayed as the
    /// runner does (calls/&lt;id&gt;/&lt;n&gt;.json, answered in &lt;n&gt;.out.json); what it prints is each answer, a line each,
    /// after the functions it was given (.arena-tools.json). Null for any other job.
    /// </summary>
    private async Task<string?> CallToolsAsync(JsonObject spec, string code, string id, List<string?> given)
    {
        if (spec["calls"]?.GetValue<bool>() != true)
        {
            return null;
        }
        var job = Path.Combine(Dir, "in", id);
        var names = given.Contains(".arena-tools.json")
            ? JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(job, "files", ".arena-tools.json")))!.AsArray().Select(d => d!["name"]!.GetValue<string>()).ToList()
            : [];
        var calls = Path.Combine(Dir, "calls", id);
        Directory.CreateDirectory(calls);
        var said = new System.Text.StringBuilder($"functions: {string.Join(",", names)}\n");
        var n = 0;
        foreach (var line in code.Split('\n').Where(l => l.StartsWith("# call ", StringComparison.Ordinal)))
        {
            var rest = line["# call ".Length..];
            var at = rest.IndexOf(' ');
            var (name, args) = at < 0 ? (rest, "{}") : (rest[..at], rest[(at + 1)..]);
            n++;
            await File.WriteAllTextAsync(Path.Combine(calls, $"{n}.json"), new JsonObject { ["name"] = name, ["args"] = JsonNode.Parse(args) }.ToJsonString());
            var answer = Path.Combine(calls, $"{n}.out.json");
            for (var i = 0; i < 400 && !File.Exists(answer); i++)
            {
                await Task.Delay(25);
            }
            said.Append(File.Exists(answer) ? await File.ReadAllTextAsync(answer) : "{\"ok\":false,\"error\":\"not answered\"}").Append('\n');
        }
        Directory.Delete(calls, recursive: true);
        return said.ToString();
    }

    /// <summary>A grey PNG of this size (RGB, unfiltered), as resvg would draw one.</summary>
    private static byte[] PngOf(int width, int height)
    {
        var px = new byte[width * height * 4];
        Array.Fill(px, (byte)200);
        return Llm.Api.Media.Png.Encode(new Llm.Api.Media.Png.Image(width, height, px));
    }

    /// <summary>The settings.json each plugin server's job was given.</summary>
    public List<string> McpSettings { get; } = [];

    /// <summary>
    /// A plugin's own MCP server's job (Chat/Tools/SandboxServerTool): answered as its server.py would, a units converter
    /// (sandbox_tools/mcp_server.py's example): initialize, tools/list, tools/call of convert (boom fails). Null for any other job.
    /// </summary>
    /// <summary>The science lab's calls (lab.py with lab-call.json beside it), as they reached the sandbox.</summary>
    public List<JsonObject> LabCalls { get; } = [];

    /// <summary>
    /// The science lab's script: its call read from lab-call.json and answered as lab.py would, the call's op and arguments
    /// given back (math_check of x and x is proved; chem_molecule is C2H6O, drawn; an op named "bad" is refused). Null for any other job.
    /// </summary>
    private async Task<string?> LabAsync(string code, string job)
    {
        if (!code.Contains("lab-call.json", StringComparison.Ordinal))
        {
            return null;
        }
        var call = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(job, "files", "lab-call.json")))!.AsObject();
        lock (LabCalls)
        {
            LabCalls.Add(call);
        }
        var op = call["op"]!.GetValue<string>();
        var args = call["args"]!.AsObject();
        var answer = op == "math_check"
            ? new JsonObject { ["ok"] = true, ["result"] = new JsonObject { ["verdict"] = args["left"]?.ToString() == args["right"]?.ToString() ? "proved" : "refuted" } }
            : op == "bad" ? new JsonObject { ["ok"] = false, ["error"] = "There is no lab tool bad." }
            : op == "chem_molecule" ? new JsonObject { ["ok"] = true, ["result"] = new JsonObject { ["formula"] = "C2H6O" }, ["show"] = new JsonObject { ["svg"] = "<svg>ethanol</svg>" } }
            : new JsonObject { ["ok"] = true, ["result"] = new JsonObject { ["op"] = op, ["args"] = args.DeepClone() } };
        return answer.ToJsonString() + "\n";
    }

    private async Task<string?> ServeMcpAsync(string code, string job)
    {
        var input = System.Text.RegularExpressions.Regex.Match(code, @"sys\.stdin = io\.StringIO\((""(?:[^""\\]|\\.)*"")\)");
        if (!code.Contains("runpy.run_path('server.py'", StringComparison.Ordinal) || !input.Success || !File.Exists(Path.Combine(job, "files", "server.py")))
        {
            return null;
        }
        lock (McpSettings)
        {
            McpSettings.Add(File.ReadAllText(Path.Combine(job, "files", "settings.json")));
        }
        var lines = new System.Text.StringBuilder();
        foreach (var line in System.Text.Json.JsonSerializer.Deserialize<string>(input.Groups[1].Value)!.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = JsonNode.Parse(line)!.AsObject();
            if (m["id"] is not { } id)
            {
                continue;
            }
            JsonNode result = m["method"]!.GetValue<string>() switch
            {
                "initialize" => new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["instructions"] = "Lengths between metres and feet." },
                "tools/list" => new JsonObject
                {
                    ["tools"] = new JsonArray(
                        new JsonObject
                        {
                            ["name"] = "convert", ["description"] = "Converts a length.",
                            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "number" }, ["unit"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("value", "unit") },
                        },
                        new JsonObject { ["name"] = "boom", ["description"] = "Fails.", ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } }),
                },
                "tools/call" when m["params"]!["name"]!.GetValue<string>() == "convert" => new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"{m["params"]!["arguments"]!["value"]!.GetValue<double>() * 3.28084:0.00} ft" }), ["isError"] = false,
                },
                _ => new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "ValueError: boom" }), ["isError"] = true },
            };
            lines.Append(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToJsonString()).Append('\n');
        }
        await Task.Yield();
        return lines.ToString();
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
