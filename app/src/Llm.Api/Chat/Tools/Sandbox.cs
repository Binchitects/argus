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
    [property: JsonPropertyName("duration_ms")] int DurationMs)
{
    [JsonIgnore]
    public IReadOnlyList<SandboxFile> Files { get; init; } = [];
}

/// <summary>
/// Runs code in the sandbox container: the job goes into the shared volume, the
/// result comes back beside it. No Docker socket and no port: the sandbox has no
/// network at all (deploy/sandbox/runner.py has the protocol).
/// </summary>
public sealed class SandboxClient(IOptionsMonitor<SandboxOptions> options, TimeProvider clock)
{
    /// <summary>Longest a job may wait for a free slot, beyond its own time.</summary>
    private static readonly TimeSpan QueueAllowance = TimeSpan.FromMinutes(3);

    private string Dir => options.CurrentValue.Dir;

    /// <summary>Why the sandbox cannot run code now, or null.</summary>
    public string? Unavailable()
    {
        var heartbeat = Path.Combine(Dir, "heartbeat");
        try
        {
            if (!File.Exists(heartbeat))
            {
                return "The Python sandbox is not running. Turn on the sandbox profile (Settings → Deployment) and apply it.";
            }
            var at = JsonDocument.Parse(File.ReadAllText(heartbeat)).RootElement.GetProperty("at").GetDouble();
            return clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0 - at > 20 ? "The Python sandbox stopped answering." : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "The Python sandbox cannot be reached.";
        }
    }

    public async Task<SandboxResult> RunAsync(string code, IReadOnlyList<SandboxFile> files, int timeoutSeconds, CancellationToken ct)
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
            new JsonObject { ["code"] = code, ["timeout"] = timeoutSeconds, ["files"] = new JsonArray([.. names.Select(n => (JsonNode)n)]) }.ToJsonString(), ct);
        var queued = Path.Combine(Dir, "in", id);
        Directory.Move(draft, queued);

        var done = Path.Combine(Dir, "out", id);
        var deadline = clock.GetUtcNow() + TimeSpan.FromSeconds(timeoutSeconds) + QueueAllowance;
        try
        {
            while (!Directory.Exists(done))
            {
                if (clock.GetUtcNow() > deadline)
                {
                    TryDelete(queued);
                    throw new SandboxException("The sandbox did not answer in time: it may be busy. Try again in a minute.");
                }
                await Task.Delay(100, ct);
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
            $"Runs a Python 3.13 program and returns what it prints. Installed: numpy, pandas, matplotlib, scipy, sympy, openpyxl, pillow. " +
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
                result = await sandbox.RunAsync(code, await InputsAsync(context.Conversation.Id, token), options.CurrentValue.TimeoutSeconds, token);
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
        var lists = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.AttachmentsJson != null)
            .OrderBy(m => m.Sequence).Select(m => m.AttachmentsJson).ToListAsync(ct);
        var ids = lists.SelectMany(ChatService.ParseIds).Distinct().ToList();
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
            return new ChatAttachment { UserId = userId, FileName = f.Name, ContentType = image, Size = f.Bytes.Length, Kind = "image", Data = f.Bytes, Text = "" };
        }
        try
        {
            var (text, truncated, converted) = Attachments.Extract(f.Name, "", f.Bytes, 1_000_000);
            return new ChatAttachment
            {
                UserId = userId, FileName = f.Name, ContentType = converted ? "application/octet-stream" : "text/plain", Size = f.Bytes.Length,
                Kind = "text", Text = text, Truncated = truncated, Data = converted ? f.Bytes : null,
            };
        }
        catch (AttachmentException)
        {
            return new ChatAttachment { UserId = userId, FileName = f.Name, ContentType = "application/octet-stream", Size = f.Bytes.Length, Kind = "file", Data = f.Bytes, Text = "" };
        }
    }
}
