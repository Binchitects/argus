using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Operations;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>
/// What a new deployment needs before it answers, done by the app itself: the picture and video
/// servers' files into the library (they wait for them), the speech server told to fetch its models,
/// and the first chat model (MODEL in .env) fetched, added and kept loaded. At start, then every ten
/// minutes: a module turned on later is provisioned then. Nothing already there is fetched again.
/// With several replicas, the one that leads does it.
/// </summary>
public sealed partial class Provisioning(IServiceScopeFactory scopes, Modules modules, ModelDownloads downloads, IOptions<EngineOptions> engine,
    IHttpClientFactory http, Replicas replicas, ILogger<Provisioning> logger) : BackgroundService
{
    public const string Client = "provisioning";
    private const string By = "setup";
    private bool _speechAsked;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var lead = replicas.IsLeader;
            try
            {
                if (lead)
                {
                    await RunAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Tried again next round; a background failure never stops the app.
                LogFailed(logger, ex.Message);
            }
            try
            {
                // A replica that does not lead looks again soon: it may lead by then.
                await Task.Delay(lead ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hf = scope.ServiceProvider.GetRequiredService<HuggingFace>();
        foreach (var (server, files) in MediaModels.Servers)
        {
            if (await modules.HasAsync(server, ct))
            {
                await ServerFilesAsync(files, db, hf, ct);
            }
        }
        if (await modules.HasAsync("audio", ct))
        {
            await SpeechAsync(ct);
        }
        if (engine.Value.Enabled && engine.Value.FirstModel is { Length: > 0 } first && !await db.LocalModels.AnyAsync(ct))
        {
            await FirstModelAsync(first.Trim(), scope.ServiceProvider, db, hf, ct);
        }
    }

    /// <summary>The files a server reads that are not in the library: one download per repository.</summary>
    private async Task ServerFilesAsync(IReadOnlyList<MediaModels.ServerFile> files, AppDbContext db, HuggingFace hf, CancellationToken ct)
    {
        var missing = files.Where(f => !File.Exists(Path.Combine(downloads.Root, f.Dir, f.Name))).ToList();
        foreach (var repo in missing.GroupBy(f => (f.Repo, f.Dir)))
        {
            // One going (or paused by an admin) is left be; a failed one is tried again.
            if (await db.ModelDownloads.AnyAsync(d => d.Repo == repo.Key.Repo && d.Dir == repo.Key.Dir && (d.State == "queued" || d.State == "running" || d.State == "paused"), ct))
            {
                continue;
            }
            var (_, sha, tree) = await hf.TreeAsync(repo.Key.Repo, ct);
            var chosen = repo.Select(f => (f, tree.FirstOrDefault(t => t.Path == f.Path))).ToList();
            if (chosen.FirstOrDefault(c => c.Item2 is null) is { f: { } gone })
            {
                LogNoFile(logger, gone.Repo, gone.Path);
                continue;
            }
            Start(db, repo.Key.Repo, sha, repo.Key.Dir, chosen.Select(c => new DownloadFile { Path = c.f.Path, Target = c.f.Name, Size = c.Item2!.Size, Sha256 = c.Item2.Sha256 }));
            var names = string.Join(", ", repo.Select(f => f.Name));
            LogFetching(logger, names, repo.Key.Dir);
        }
        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            // Woken once they are saved: woken before, it finds none and sleeps a minute.
            downloads.Wake();
        }
    }

    /// <summary>
    /// Asks the speech server for its models; it fetches them from Hugging Face itself. Once a start for
    /// every one (a download cut short is listed as there: asking again finishes it, and a whole one
    /// answers at once), then only for those it does not list.
    /// </summary>
    private async Task SpeechAsync(CancellationToken ct)
    {
        var client = http.CreateClient(Client);
        var have = new HashSet<string>(StringComparer.Ordinal);
        var list = await client.GetFromJsonAsync<JsonObject>($"{MediaModels.AudioUrl}/v1/models", ct);
        foreach (var m in (list?["data"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (m["id"]?.GetValue<string>() is { } id)
            {
                have.Add(id);
            }
        }
        var all = !_speechAsked;
        foreach (var (name, id, _) in MediaModels.Speech.Where(s => all || !have.Contains(s.Id)))
        {
            LogSpeech(logger, name, id);
            using var res = await client.PostAsync($"{MediaModels.AudioUrl}/v1/models/{id}", null, ct);
            if (!res.IsSuccessStatusCode)
            {
                LogSpeechFailed(logger, id, (int)res.StatusCode);
            }
        }
        _speechAsked = true;
    }

    /// <summary>
    /// MODEL: a file in the library, or "owner/repo:QUANT" from Hugging Face (with the repository's
    /// vision projector, when it has one). Added once it is all there, with the settings recommended for
    /// this machine, and kept loaded.
    /// </summary>
    private async Task FirstModelAsync(string first, IServiceProvider services, AppDbContext db, HuggingFace hf, CancellationToken ct)
    {
        var library = services.GetRequiredService<ModelLibrary>();
        string file;
        string? projector = null;
        if (first.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            file = first.TrimStart('/');
        }
        else
        {
            var at = first.LastIndexOf(':');
            var (repoId, quant) = at > 0 ? (first[..at], first[(at + 1)..]) : (first, "Q4_K_M");
            var repo = await hf.RepoAsync(repoId, ct);
            if (repo.Models.FirstOrDefault(m => !m.Projector && string.Equals(m.Quant, quant, StringComparison.OrdinalIgnoreCase)) is not { } model)
            {
                LogNoQuant(logger, repoId, quant);
                return;
            }
            var mmproj = repo.Models.Where(m => m.Projector).OrderBy(m => m.Quant is "F16" or "BF16" ? 0 : 1).ThenBy(m => m.Size).FirstOrDefault();
            file = $"{repo.Id}/{model.Files[0].Path}";
            projector = mmproj is null ? null : $"{repo.Id}/{mmproj.Files[0].Path}";
            var wanted = model.Files.Concat(mmproj?.Files ?? []).ToList();
            if (wanted.Any(f => !File.Exists(Path.Combine(downloads.Root, repo.Id, f.Path))))
            {
                if (!await db.ModelDownloads.AnyAsync(d => d.Repo == repo.Id && (d.State == "queued" || d.State == "running" || d.State == "paused"), ct))
                {
                    Start(db, repo.Id, repo.Sha, repo.Id, wanted.Select(f => new DownloadFile { Path = f.Path, Size = f.Size, Sha256 = f.Sha256 }));
                    await db.SaveChangesAsync(ct);
                    downloads.Wake();
                    var fetching = Path.GetFileName(file);
                    LogFetching(logger, fetching, repo.Id);
                }
                return;
            }
        }
        if (library.Find(file) is not { } entry)
        {
            LogNotInLibrary(logger, file);
            return;
        }
        var name = SplitPart().Replace(Path.GetFileNameWithoutExtension(file), "");
        var draft = new LocalModel { Name = name, File = file, Projector = projector };
        if (ModelAdvisor.Advise(draft, entry, library.List(), await services.GetRequiredService<HardwareProbe>().GetAsync(ct)).Recommended is { } r)
        {
            (draft.Context, draft.MaxOutput, draft.Parallel, draft.KvType, draft.Ubatch) = (r.Context, r.MaxOutput, r.Parallel, r.KvType, r.Ubatch);
            (draft.Projector, draft.Thinking, draft.Tools) = (draft.Projector ?? r.Projector, r.Thinking, r.Tools);
        }
        db.LocalModels.Add(draft);
        await db.SaveChangesAsync(ct);
        var catalog = services.GetRequiredService<ModelCatalog>();
        if (catalog.Pinned().Count == 0)
        {
            catalog.SetKept([name]);
        }
        await catalog.WritePresetsAsync(ct);
        services.GetRequiredService<EngineWatcher>().Wake();
        LogAdded(logger, name, file);
    }

    private static void Start(AppDbContext db, string repo, string sha, string dir, IEnumerable<DownloadFile> files)
    {
        var d = new ModelDownload { Repo = repo, Revision = sha, Dir = dir, CreatedBy = By, Files = [.. files] };
        d.Total = d.Files.Sum(f => f.Size);
        db.ModelDownloads.Add(d);
    }

    [GeneratedRegex(@"-\d{5}-of-\d{5}$")]
    private static partial Regex SplitPart();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup: {Error}; trying again in ten minutes")]
    private static partial void LogFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Setup: fetching {Files} into the model library ({Dir})")]
    private static partial void LogFetching(ILogger logger, string files, string dir);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup: {Repo} has no {Path}")]
    private static partial void LogNoFile(ILogger logger, string repo, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Setup: the speech server fetches {Name} ({Id})")]
    private static partial void LogSpeech(ILogger logger, string name, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup: the speech server could not fetch {Id} (HTTP {Status})")]
    private static partial void LogSpeechFailed(ILogger logger, string id, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup: {Repo} has no {Quant} model (MODEL in .env)")]
    private static partial void LogNoQuant(ILogger logger, string repo, string quant);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup: {File} is not a model in the library (MODEL in .env)")]
    private static partial void LogNotInLibrary(ILogger logger, string file);

    [LoggerMessage(Level = LogLevel.Information, Message = "Setup: {Name} ({File}) added and kept loaded")]
    private static partial void LogAdded(ILogger logger, string name, string file);
}
