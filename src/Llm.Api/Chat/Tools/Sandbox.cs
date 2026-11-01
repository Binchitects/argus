using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat.Tools;

/// <summary>Configuration section "Sandbox": the Python sandbox (compose profile sandbox).</summary>
public sealed class SandboxOptions
{
    /// <summary>The sandbox-jobs volume, as the app mounts it.</summary>
    public string Dir { get; set; } = "/sandbox";

    /// <summary>How long one run may take.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Of each of stdout and stderr, what the model reads.</summary>
    public int MaxOutputChars { get; set; } = 20_000;
}

public sealed class SandboxException(string message) : Exception(message);

public sealed record SandboxFile(string Name, byte[] Bytes);

public sealed record SandboxResult(
    [property: JsonPropertyName("exit_code")] int? ExitCode,
    [property: JsonPropertyName("timed_out")] bool TimedOut,
    [property: JsonPropertyName("cancelled")] bool Cancelled,
    [property: JsonPropertyName("stdout")] string Stdout,
    [property: JsonPropertyName("stdout_cut")] bool StdoutCut,
    [property: JsonPropertyName("stderr")] string Stderr,
    [property: JsonPropertyName("stderr_cut")] bool StderrCut,
    [property: JsonPropertyName("killed")] string? Killed,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("skipped_files")] IReadOnlyList<string> Skipped,
    [property: JsonPropertyName("duration_ms")] int DurationMs,
    [property: JsonPropertyName("reset")] string? Reset = null)
{
    [JsonIgnore]
    public IReadOnlyList<SandboxFile> Files { get; init; } = [];
}

/// <summary>
/// Runs code in the sandbox container: the job goes into the shared volume, the
/// result comes back beside it. No Docker socket and no port: the sandbox has no
/// network at all (services/sandbox/runner.py has the protocol).
/// </summary>
public sealed class SandboxClient(IOptionsMonitor<SandboxOptions> options, TimeProvider clock)
    : JobRunner(() => options.CurrentValue.Dir, "The Python sandbox", "Turn on the sandbox profile (Settings → Deployment) and apply it.", clock);

/// <summary>
/// A sandbox's jobs through its folder (services/sandbox/runner.py): written in, the result read back. The Python
/// sandbox's and the 3D sandbox's (Blender) alike.
/// </summary>
public abstract class JobRunner(Func<string> dir, string name, string howToStart, TimeProvider clock)
{
    /// <summary>
    /// Longest a job waits in line for a free slot while its sandbox answers (many people at once, a slow 3D render
    /// ahead): its own time counts from when it starts.
    /// </summary>
    public static readonly TimeSpan QueueLimit = TimeSpan.FromMinutes(15);

    /// <summary>Beyond a job's own time, from when it starts, before it is given up (the runner stops it at its time).</summary>
    private static readonly TimeSpan RunGrace = TimeSpan.FromSeconds(30);

    /// <summary>A runner silent this long while it has a job has stopped: the job is not waited for any more.</summary>
    public static readonly TimeSpan SilentLimit = TimeSpan.FromSeconds(30);

    /// <summary>An admin's reset (Admin → Monitoring), in its folder: there until the runner has done it.</summary>
    public const string ResetFile = "reset";

    /// <summary>Its name in a list ("Python sandbox", "3D sandbox (Blender)").</summary>
    public string Label => name.StartsWith("The ", StringComparison.Ordinal) ? char.ToUpperInvariant(name[4]) + name[5..] : name;

    /// <summary>Where the app asks for a reset, and the runner removes the ask once it is done.</summary>
    public string ResetPath => Path.Combine(Dir, ResetFile);

    /// <summary>What the runner says it does: its slots, how many are busy, and the jobs waiting in line; null when it says nothing.</summary>
    public (int Slots, int Busy, int Waiting)? Load()
    {
        try
        {
            var beat = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "heartbeat"))).RootElement;
            var waiting = Directory.GetDirectories(Path.Combine(Dir, "in")).Count(d => !Path.GetFileName(d).StartsWith('.'));
            return (beat.GetProperty("slots").GetInt32(), beat.GetProperty("busy").GetInt32(), waiting);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The runner's heartbeat: when it last wrote it, and when it started (null from a runner before that).</summary>
    private (double At, double? Started)? Heartbeat()
    {
        try
        {
            var beat = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "heartbeat"))).RootElement;
            return (beat.GetProperty("at").GetDouble(), beat.TryGetProperty("started", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private string Dir => dir();

    /// <summary>Why the sandbox cannot run code now, or null.</summary>
    public string? Unavailable()
    {
        var heartbeat = Path.Combine(Dir, "heartbeat");
        try
        {
            if (!File.Exists(heartbeat))
            {
                return $"{name} is not running. {howToStart}";
            }
            if (File.Exists(ResetPath))
            {
                return $"{name} is being reset by an admin: try again in a minute.";
            }
            var at = JsonDocument.Parse(File.ReadAllText(heartbeat)).RootElement.GetProperty("at").GetDouble();
            return clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0 - at > 20 ? $"{name} stopped answering." : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return $"{name} cannot be reached.";
        }
    }

    /// <summary>The program's calls the runner left, from the one after <paramref name="answered"/>, each answered in turn; how many are now.</summary>
    private async Task<int> AnswerCallsAsync(string id, int answered, Func<string, JsonObject, CancellationToken, Task<JsonObject>> calls, CancellationToken ct)
    {
        var dir = Path.Combine(Dir, "calls", id);
        for (var n = answered + 1; File.Exists(Path.Combine(dir, $"{n}.json")); n++)
        {
            JsonObject answer;
            try
            {
                var asked = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dir, $"{n}.json"), ct))!.AsObject();
                // The arguments on their own, out of the file's object: a tool may put them in a request of its own (an MCP server's).
                answer = await calls(asked["name"]?.GetValue<string>() ?? "", asked["args"] is JsonObject args ? (JsonObject)args.DeepClone() : [], ct);
            }
            catch (JsonException)
            {
                answer = new JsonObject { ["ok"] = false, ["error"] = "The call was not JSON." };
            }
            var tmp = Path.Combine(dir, $".tmp-{n}.out.json");
            await File.WriteAllTextAsync(tmp, answer.ToJsonString(), ct);
            File.Move(tmp, Path.Combine(dir, $"{n}.out.json"), overwrite: true);
            answered = n;
        }
        return answered;
    }

    /// <summary>
    /// Runs a job. With <paramref name="calls"/>, its program may call the chat's tools (arena, run_code): each call the
    /// runner relays (calls/&lt;id&gt;/&lt;n&gt;.json) is answered here, one at a time, while the job runs.
    /// </summary>
    /// <param name="waiting">Told how many jobs are ahead while this one waits in line (the chat shows it beside the call), then null once it runs.</param>
    public async Task<SandboxResult> RunAsync(string code, IReadOnlyList<SandboxFile> files, int timeoutSeconds, CancellationToken ct,
        Func<string, JsonObject, CancellationToken, Task<JsonObject>>? calls = null, Func<int?, Task>? waiting = null)
    {
        var id = Guid.CreateVersion7().ToString("N");
        var draft = Path.Combine(Dir, "in", ".tmp-" + id);
        Directory.CreateDirectory(Path.Combine(draft, "files"));
        var names = new List<string>();
        foreach (var f in files)
        {
            await File.WriteAllBytesAsync(Path.Combine(draft, "files", f.Name), f.Bytes, ct);
            names.Add(f.Name);
        }
        await File.WriteAllTextAsync(Path.Combine(draft, "job.json"),
            new JsonObject { ["code"] = code, ["timeout"] = timeoutSeconds, ["files"] = new JsonArray([.. names.Select(n => (JsonNode)n)]), ["calls"] = calls is null ? null : true }.ToJsonString(), ct);
        var queued = Path.Combine(Dir, "in", id);
        Directory.Move(draft, queued);

        var done = Path.Combine(Dir, "out", id);
        // In line while the job is in in/: as long as the sandbox answers, up to the queue's limit; once taken, its own time.
        var inLineUntil = clock.GetUtcNow() + QueueLimit;
        DateTimeOffset? deadline = null;
        int? ahead = -1;
        // The runner is watched while it has the job: one that stops answering, or starts again (and so drops what it was
        // running), is not waited for until the deadline, which reads as a tool that never answers.
        var startedAt = Heartbeat()?.Started;
        DateTimeOffset? silentSince = null;
        var looked = Environment.TickCount64;
        try
        {
            var answered = 0;
            while (!Directory.Exists(done))
            {
                var now = clock.GetUtcNow();
                if (deadline is null && !Directory.Exists(queued))
                {
                    deadline = now + TimeSpan.FromSeconds(timeoutSeconds) + RunGrace;
                }
                if (deadline is null && now > inLineUntil)
                {
                    TryDelete(queued);
                    throw new SandboxException($"{name} is busy: this job waited {QueueLimit.TotalMinutes:0} minutes in line behind others and was not run. Try again later; "
                        + "an admin can give it more room (its jobs at once, in .env).");
                }
                if (deadline is { } end && now > end)
                {
                    throw new SandboxException("The sandbox did not answer in time: it may be busy. Try again in a minute.");
                }
                if (Environment.TickCount64 - looked >= 1000)
                {
                    looked = Environment.TickCount64;
                    if (waiting is not null)
                    {
                        // The jobs the runner takes before this one (it takes them in their ids' order, which is their time's).
                        int? now2 = deadline is null ? Ahead(id) : null;
                        if (now2 != ahead)
                        {
                            ahead = now2;
                            await waiting(now2);
                        }
                    }
                    var beat = Heartbeat();
                    if (beat is null || now.ToUnixTimeMilliseconds() / 1000.0 - beat.Value.At > 20)
                    {
                        silentSince ??= now;
                        if (now - silentSince > SilentLimit)
                        {
                            TryDelete(queued);
                            throw new SandboxException($"{name} stopped answering while it had this job, so nothing came back: run it again. If it keeps happening, an admin finds why in Admin → Logs.");
                        }
                    }
                    else
                    {
                        silentSince = null;
                        // Its result first: a runner an admin reset answers the job, then starts again.
                        if (startedAt is not null && beat.Value.Started is { } started && started != startedAt && !Directory.Exists(queued) && !Directory.Exists(done))
                        {
                            throw new SandboxException($"{name} started again while it ran this job, which was lost with it: run it again.");
                        }
                    }
                }
                if (calls is not null)
                {
                    answered = await AnswerCallsAsync(id, answered, calls, ct);
                }
                await Task.Delay(calls is null ? 100 : 20, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The person pressed stop: a waiting job goes, a running one is stopped.
            TryDelete(queued);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(Dir, "cancel", id), "", CancellationToken.None);
            }
            catch (IOException)
            {
            }
            throw;
        }

        try
        {
            var result = JsonSerializer.Deserialize<SandboxResult>(await File.ReadAllTextAsync(Path.Combine(done, "result.json"), ct))
                ?? throw new SandboxException("The sandbox gave no result.");
            if (result.Reset is { } reset)
            {
                throw new SandboxException($"An admin reset {Short} while this job {(reset == "waiting" ? "waited in line" : "was running")}: run it again.");
            }
            var made = new List<SandboxFile>();
            var dir = Path.Combine(done, "files");
            if (Directory.Exists(dir))
            {
                foreach (var path in Directory.GetFiles(dir).Order(StringComparer.Ordinal))
                {
                    made.Add(new SandboxFile(Path.GetFileName(path), await File.ReadAllBytesAsync(path, ct)));
                }
            }
            return result with { Files = made };
        }
        finally
        {
            TryDelete(done);
        }
    }

    /// <summary>What the chat shows beside a call whose job waits in line: how many are ahead, then nothing once it runs.</summary>
    public Func<int?, Task>? InLine(ToolContext context) => context.Progress is not { } progress ? null : ahead => progress.ReportAsync(new McpProgress(0, null,
        ahead switch
        {
            null => null,
            0 => $"Waiting for {Short}: next in line",
            1 => $"Waiting for {Short}: 1 job ahead",
            var n => $"Waiting for {Short}: {n} jobs ahead",
        }));

    /// <summary>Its name in a line ("the 3D sandbox"), from its name ("The 3D sandbox (Blender)").</summary>
    private string Short => (name.IndexOf(" (", StringComparison.Ordinal) is var at and > 0 ? name[..at] : name) is var s && s.StartsWith("The ", StringComparison.Ordinal) ? "the " + s[4..] : s;

    /// <summary>How many jobs wait in line before this one.</summary>
    private int Ahead(string id)
    {
        try
        {
            return Directory.GetDirectories(Path.Combine(Dir, "in")).Select(Path.GetFileName).Count(n => n is { } x && !x.StartsWith('.') && string.CompareOrdinal(x, id) < 0);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Python, run in the sandbox: calculations, data analysis, charts, and reading
/// or writing files. The chat's files are in its working directory; files it
/// writes come back as the person's (pictures in the answer, the rest in Files).
/// </summary>
public sealed class PythonTool(SandboxClient sandbox, AppDbContext db, IOptionsMonitor<SandboxOptions> options) : IChatTool
{
    /// <summary>Of the chat's files, what goes into the working directory at most.</summary>
    private const long MaxInputBytes = 100L * 1024 * 1024;

    public string Id => "python";
    public string Title => "Python";
    public string Description => "Runs Python in a sandbox with no network: calculations, data analysis, charts, and reading or writing spreadsheets and other files.";
    public string Icon => "terminal";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult(sandbox.Unavailable());

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [Schema.Function("run_python",
            "Runs a Python 3.13 program and returns what it prints. Installed: " +
            "data: numpy, pandas, polars, pyarrow, duckdb, scipy, statsmodels, scikit-learn, xgboost, lightgbm, numba, sympy, networkx; " +
            "charts: matplotlib, seaborn, plotly (fig.write_html; no image export); " +
            "images: pillow (with HEIC), opencv (cv2), scikit-image, imageio, pytesseract (OCR in English, Arabic, Persian and Chinese: lang 'eng', 'ara', 'fas', 'chi_sim', 'chi_tra', or joined with +); " +
            "fonts: Noto for most scripts, Noto Sans CJK SC/TC/JP/KR for Chinese, Japanese and Korean (matplotlib: set font.family to one); " +
            "maps: geopandas, shapely, pyproj; " +
            "files: openpyxl, xlsxwriter, python-docx, python-pptx, reportlab, pypdf, pdfplumber, lxml, beautifulsoup4, pyyaml; " +
            "commands: ffmpeg, imagemagick, pandoc, libreoffice, graphviz, tesseract. " +
            $"No network. At most {options.CurrentValue.TimeoutSeconds} seconds a run. Each run starts afresh: nothing is kept between runs but the files. " +
            "The chat's files are in the working directory under their names. Files the program writes there are given to the person.",
            new JsonObject { ["code"] = Schema.Text("The whole program. print() what you need to read.") }, "code")],
        "Use run_python for anything that needs exact computation, data, or a file: analysing an attached spreadsheet or CSV, " +
        "statistics, charts (matplotlib; save with plt.savefig or just plt.show()), converting or writing files. Print results you need; " +
        "the person sees the charts and files the program writes, so do not repeat their content, say what they show. If it fails, fix the code and run it again.",
        async (_, args, token) =>
        {
            var code = Schema.Str(args, "code") ?? "";
            if (code.Trim().Length == 0)
            {
                return new ToolResult("Give the program in 'code'.", IsError: true);
            }
            SandboxResult result;
            try
            {
                result = await sandbox.RunAsync(code, await InputsAsync(context.Conversation.Id, token), options.CurrentValue.TimeoutSeconds, token, waiting: sandbox.InLine(context));
            }
            catch (SandboxException ex)
            {
                return new ToolResult(ex.Message, IsError: true);
            }
            var made = new List<ChatAttachment>();
            foreach (var f in result.Files)
            {
                made.Add(AsAttachment(context.User.Id, f));
            }
            db.ChatAttachments.AddRange(made);
            await db.SaveChangesAsync(token);
            var max = options.CurrentValue.MaxOutputChars;
            var failed = result.Error is not null || result.TimedOut || result.Killed is not null || result.ExitCode is not 0;
            return new ToolResult(new JsonObject
            {
                ["exit_code"] = result.ExitCode,
                ["stdout"] = Cut(result.Stdout, max, result.StdoutCut),
                ["stderr"] = result.Stderr.Length > 0 ? Cut(result.Stderr, max, result.StderrCut) : null,
                ["timed_out"] = result.TimedOut ? $"Stopped after {options.CurrentValue.TimeoutSeconds} seconds." : null,
                ["problem"] = result.Error ?? result.Killed,
                ["files_given_to_the_person"] = made.Count > 0 ? new JsonArray([.. made.Select(a => (JsonNode)$"{a.FileName} ({(a.Kind == "image" ? "shown as a picture" : "in their Files panel")})")]) : null,
                ["files_not_kept"] = result.Skipped.Count > 0 ? $"{string.Join(", ", result.Skipped)}: too many or too large" : null,
                ["seconds"] = Math.Round(result.DurationMs / 1000.0, 1),
            }.ToJsonString(Mcp.Plain), failed, made);
        }));

    private static string Cut(string text, int max, bool alreadyCut) =>
        text.Length > max ? text[..max] + $"\n[… {text.Length - max:N0} more characters not shown]" : alreadyCut ? text + "\n[… more output not shown]" : text;

    /// <summary>The chat's files, as bytes: the original of a document, the text of a text file, the picture itself.</summary>
    public async Task<IReadOnlyList<SandboxFile>> InputsAsync(Guid conversationId, CancellationToken ct)
    {
        var ids = await ChatService.FileIdsAsync(db, conversationId, ct);
        var found = await db.ChatAttachments.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var byName = new Dictionary<string, SandboxFile>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var a in ids.Where(found.ContainsKey).Select(i => found[i]))
        {
            var bytes = a.Data ?? Encoding.UTF8.GetBytes(a.Text);
            var name = SafeName(a.FileName);
            if (total + bytes.Length > MaxInputBytes)
            {
                break;
            }
            // A later file of the same name (a newer version) is the one the code sees.
            if (byName.TryGetValue(name, out var older))
            {
                total -= older.Bytes.Length;
            }
            byName[name] = new SandboxFile(name, bytes);
            total += bytes.Length;
        }
        return [.. byName.Values];
    }

    private static string SafeName(string name)
    {
        var clean = new string([.. Path.GetFileName(name).Select(c => char.IsControl(c) || c is '/' or '\\' or ':' ? '_' : c)]).Trim().TrimStart('.');
        return clean.Length == 0 ? "file" : clean.Length > 120 ? clean[..120] : clean;
    }

    /// <summary>A picture is shown; text and documents are read as text (the model can read them on); anything else is a file to download.</summary>
    public static ChatAttachment AsAttachment(Guid userId, SandboxFile f)
    {
        if (Attachments.ImageType(f.Name, f.Bytes) is { } image)
        {
            return new ChatAttachment { UserId = userId, FileName = f.Name, ContentType = image, Size = f.Bytes.Length, Kind = "image", Data = f.Bytes, Text = "", Origin = "tool" };
        }
        try
        {
            var (text, truncated, converted) = Attachments.Extract(f.Name, "", f.Bytes, 1_000_000);
            // A page or picture keeps its bytes too: the chat previews it whole (a plotly chart carries
            // its library, past what the model reads), and it downloads as it was written.
            var page = Path.GetExtension(f.Name).ToLowerInvariant() is ".html" or ".htm" or ".svg";
            return new ChatAttachment
            {
                UserId = userId, FileName = f.Name, ContentType = converted ? "application/octet-stream" : "text/plain", Size = f.Bytes.Length,
                Kind = "text", Text = text, Truncated = truncated, Data = converted || page ? f.Bytes : null, Origin = "tool",
            };
        }
        catch (AttachmentException)
        {
            return new ChatAttachment { UserId = userId, FileName = f.Name, ContentType = "application/octet-stream", Size = f.Bytes.Length, Kind = "file", Data = f.Bytes, Text = "", Origin = "tool" };
        }
    }
}
