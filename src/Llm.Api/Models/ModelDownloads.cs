using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>
/// Downloads models from Hugging Face into the model library, one at a time, in turn.
/// Each file goes to "name.part" first and is renamed once whole and its SHA-256 checked
/// (Hugging Face's own, for files kept in LFS), so the library never lists half a model.
/// A download paused, stopped by a restart, or cut off goes on from where it got to
/// (an HTTP range request); one the app found running when it started is queued again.
/// </summary>
public sealed partial class ModelDownloads(IServiceScopeFactory scopes, IOptions<EngineOptions> engine, TimeProvider clock, ILogger<ModelDownloads> logger)
    : BackgroundService
{
    /// <summary>Room left on the disk after a download, besides its files.</summary>
    public const long SpareBytes = 2L * 1024 * 1024 * 1024;

    private readonly SemaphoreSlim _wake = new(0);
    private readonly ConcurrentDictionary<Guid, Live> _live = new();
    private CancellationTokenSource? _current;
    private Guid? _currentId;

    /// <summary>A download as it runs: bytes so far, and how fast (bytes a second, over the last few seconds).</summary>
    public sealed record Live(long Bytes, double Speed);

    public Live? Now(Guid id) => _live.GetValueOrDefault(id);

    public string Root => engine.Value.LibraryDir;

    /// <summary>Look for queued downloads now.</summary>
    public void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    /// <summary>Stops the download running, if it is this one (its row says what next: paused or removed).</summary>
    public void Stop(Guid id)
    {
        if (_currentId == id)
        {
            _current?.Cancel();
        }
    }

    /// <summary>Where a download's file goes, under the library; null when the path would leave it.</summary>
    public string? PathOf(ModelDownload d, DownloadFile f) => PathOf(d, f.Target ?? f.Path);

    public string? PathOf(ModelDownload d, string file)
    {
        var root = System.IO.Path.GetFullPath(Root);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, d.Dir, file));
        return full.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal) ? full : null;
    }

    /// <summary>Takes away what a download left half done (its .part files); whole files stay in the library.</summary>
    public void Discard(ModelDownload d)
    {
        foreach (var f in d.Files)
        {
            if (PathOf(d, f) is { } path && File.Exists(path + ".part"))
            {
                File.Delete(path + ".part");
            }
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // What was running when the app stopped goes on.
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.ModelDownloads.Where(d => d.State == "running").ExecuteUpdateAsync(s => s.SetProperty(d => d.State, "queued"), stoppingToken);
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Guid? next;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    next = await db.ModelDownloads.Where(d => d.State == "queued").OrderBy(d => d.CreatedAt).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(stoppingToken);
                }
                if (next is not { } id)
                {
                    await _wake.WaitAsync(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }
                await RunAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the app keeps running: a background failure must never stop it.
                LogFailed(logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task RunAsync(Guid id, CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hf = scope.ServiceProvider.GetRequiredService<HuggingFace>();
        var d = await db.ModelDownloads.SingleAsync(x => x.Id == id, stoppingToken);
        d.State = "running";
        d.Error = null;
        await db.SaveChangesAsync(stoppingToken);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        (_current, _currentId) = (run, id);
        var done = 0L;
        try
        {
            foreach (var f in d.Files)
            {
                var path = PathOf(d, f) ?? throw new IOException($"{f.Path} would land outside the model library.");
                if (File.Exists(path) && new FileInfo(path).Length == f.Size)
                {
                    done += f.Size;
                    continue;
                }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                await FetchAsync(hf, d, f, path, done, db, run.Token);
                done += f.Size;
            }
            d.State = "done";
            d.Bytes = d.Total;
            d.FinishedAt = clock.GetUtcNow();
            LogDone(logger, d.Repo, d.Total);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            // Paused or removed by an admin (its row says which), or the app is stopping (queued again at start).
            await db.Entry(d).ReloadAsync(CancellationToken.None);
            if (d.State == "running")
            {
                d.State = stoppingToken.IsCancellationRequested ? "queued" : "paused";
            }
        }
        catch (Exception ex) when (ex is HuggingFaceException or IOException or HttpRequestException or UnauthorizedAccessException)
        {
            d.State = "failed";
            d.Error = ex is UnauthorizedAccessException
                ? $"The app cannot write to the model library ({Root}): MODELS_DIR must be writable for the app's user (uid 1000)."
                : ex.Message;
            LogDownloadFailed(logger, d.Repo, ex.Message);
        }
        finally
        {
            (_current, _currentId) = (null, null);
            _live.TryRemove(id, out _);
            if (db.Entry(d).State != EntityState.Detached && await db.ModelDownloads.AnyAsync(x => x.Id == id, CancellationToken.None))
            {
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        // The admin who started it hears how it ended (it can take hours).
        if (d.State is "done" or "failed" && await scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Llm.Core.Identity.AppUser>>().FindByNameAsync(d.CreatedBy) is { } who)
        {
            var files = string.Join(", ", d.Files.Select(f => Path.GetFileName(f.Path)));
            await scope.ServiceProvider.GetRequiredService<Notifications.Notifier>().SendAsync(who.Id, d.State == "done"
                ? new Notifications.News("download", $"Downloaded {d.Repo}", $"{files} is in the model library: add it under Models.", "/admin/models", $"download:{d.Id}")
                : new Notifications.News("download", $"Download of {d.Repo} failed", d.Error, "/admin/models"), CancellationToken.None);
        }
    }

    /// <summary>One file: from where its .part got to, hashed as it comes, checked, then renamed into place.</summary>
    private async Task FetchAsync(HuggingFace hf, ModelDownload d, DownloadFile f, string path, long before, AppDbContext db, CancellationToken ct)
    {
        var part = path + ".part";
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > f.Size)
        {
            File.Delete(part);
            have = 0;
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (have > 0)
        {
            await using var existing = File.OpenRead(part);
            var buffer = new byte[1 << 20];
            int n;
            while ((n = await existing.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, n);
            }
        }
        if (have < f.Size)
        {
            using var res = await hf.OpenAsync(d.Repo, d.Revision, f.Path, have, ct);
            // A server that ignores the range sends the whole file: start again.
            if (have > 0 && res.StatusCode != HttpStatusCode.PartialContent)
            {
                have = 0;
                hash.GetHashAndReset();
            }
            await using var body = await res.Content.ReadAsStreamAsync(ct);
            await using var file = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            var buffer = new byte[1 << 20];
            var clockStart = Stopwatch.StartNew();
            var (savedAt, speedAt, speedBytes) = (TimeSpan.Zero, TimeSpan.Zero, have);
            var speed = 0.0;
            int n;
            while ((n = await ReadAsync(body, buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
                hash.AppendData(buffer, 0, n);
                have += n;
                var now = clockStart.Elapsed;
                if (now - speedAt >= TimeSpan.FromSeconds(2))
                {
                    speed = (have - speedBytes) / (now - speedAt).TotalSeconds;
                    (speedAt, speedBytes) = (now, have);
                }
                _live[d.Id] = new Live(before + have, speed);
                if (now - savedAt >= TimeSpan.FromSeconds(5))
                {
                    d.Bytes = before + have;
                    await db.SaveChangesAsync(CancellationToken.None);
                    savedAt = now;
                }
            }
        }
        if (have != f.Size)
        {
            throw new IOException($"{f.Path} came to {have:N0} bytes, not {f.Size:N0}: it is tried again from there when resumed.");
        }
        if (f.Sha256 is { Length: > 0 } expected && !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new IOException($"{f.Path} is not the file Hugging Face lists (its SHA-256 differs); it was removed. Start the download again.");
        }
        File.Move(part, path, overwrite: true);
    }

    /// <summary>The longest a download may receive nothing before it counts as cut off.</summary>
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(90);

    private static async Task<int> ReadAsync(Stream body, byte[] buffer, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(Stall);
        try
        {
            return await body.ReadAsync(buffer, stall.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"Hugging Face sent nothing for {Stall.TotalSeconds:0} seconds: resume to go on from here.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Model download from {Repo} done ({Bytes} bytes)")]
    private static partial void LogDone(ILogger logger, string repo, long bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Model download from {Repo} failed: {Reason}")]
    private static partial void LogDownloadFailed(ILogger logger, string repo, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "The model downloads failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
