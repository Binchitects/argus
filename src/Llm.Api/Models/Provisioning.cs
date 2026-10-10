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
/// The default models a deployment starts with: the first chat model (MODEL in .env), the picture and
/// video servers' files (they wait for them in the library) and the speech server's models. Never
/// fetched by the app on its own (a site may be offline, metered, or want to choose): the installer's
/// "download the default models" option asks for them (through /internal/models/defaults), or an admin
/// (Admin → Models → Default models), and only then are they downloaded (<see cref="RunAsync"/> with
/// download). On its own, at start and every ten minutes, it only adds the first chat model, and keeps
/// it loaded, once its files are in the library (copied there, or downloaded when asked). With several
/// replicas, the one that leads does it.
/// </summary>
/// <summary>A default model's file (or the speech server's model): what serves it, its place in the library, where it comes from, and present, downloading, missing or unknown.</summary>
public sealed record DefaultModel(string Server, string Name, string Source, string State);

public sealed partial class Provisioning(IServiceScopeFactory scopes, Modules modules, ModelDownloads downloads, IOptions<EngineOptions> engine,
    IHttpClientFactory http, Replicas replicas, ILogger<Provisioning> logger) : BackgroundService
{
    public const string Client = "provisioning";
    private const string By = "setup";
    private bool _speechAsked;
    /// <summary>MODEL's files were said to be missing (once, until they come).</summary>
    private bool _toldMissing;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var lead = replicas.IsLeader;
            try
            {
                if (lead)
                {
                    await RunAsync(download: await DownloadOnceAsync(stoppingToken), stoppingToken);
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

    /// <summary>
    /// The first chat model added once its files are in the library; with <paramref name="download"/> (asked by the
    /// installer or an admin), the default models' missing files downloaded first: the servers' files of the modules
    /// that run, the speech server's models, and MODEL's files. Returns what was started.
    /// </summary>
    public async Task<IReadOnlyList<string>> RunAsync(bool download, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hf = scope.ServiceProvider.GetRequiredService<HuggingFace>();
        var started = new List<string>();
        if (download)
        {
            foreach (var (server, files) in MediaModels.Servers)
            {
                if (await modules.HasAsync(server, ct))
                {
                    started.AddRange(await ServerFilesAsync(files, db, hf, ct));
                }
            }
            if (await modules.HasAsync("audio", ct))
            {
                started.AddRange(await SpeechAsync(ct));
            }
        }
        if (engine.Value.Enabled && engine.Value.FirstModel is { Length: > 0 } first && !await db.LocalModels.AnyAsync(ct))
        {
            started.AddRange(await FirstModelAsync(first.Trim(), download, scope.ServiceProvider, db, hf, ct));
        }
        return started;
    }

    /// <summary>The setting that says the installer's download was started: it is done once.</summary>
    public const string Downloaded = "setup.default_models_downloaded";

    /// <summary>
    /// Whether this round downloads the default models: the installer chose it (DEFAULT_MODELS=download), and it was not
    /// started before. Marked started at once: a download that fails is the downloads' own to try again.
    /// </summary>
    private async Task<bool> DownloadOnceAsync(CancellationToken ct)
    {
        if (!string.Equals(engine.Value.DefaultModels?.Trim(), "download", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.Settings.AnyAsync(s => s.Key == Downloaded, ct))
        {
            return false;
        }
        db.Settings.Add(new Setting { Key = Downloaded, Value = DateTimeOffset.UtcNow.ToString("O") });
        await db.SaveChangesAsync(ct);
        LogInstallerDownload(logger);
        return true;
    }

    /// <summary>The default models' files and whether each is in the library (or, for speech, at the speech server), for the page and the installer.</summary>
    public async Task<IReadOnlyList<DefaultModel>> ListAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var going = await db.ModelDownloads.AsNoTracking().Where(d => d.State == "queued" || d.State == "running" || d.State == "paused").Select(d => d.Dir).ToListAsync(ct);
        var list = new List<DefaultModel>();
        foreach (var (server, files) in MediaModels.Servers)
        {
            if (!await modules.HasAsync(server, ct))
            {
                continue;
            }
            foreach (var f in files)
            {
                var there = File.Exists(Path.Combine(downloads.Root, f.Dir, f.Name));
                list.Add(new DefaultModel(server, $"{f.Dir}/{f.Name}", f.Repo, there ? "present" : going.Contains(f.Dir) ? "downloading" : "missing"));
            }
        }
        if (await modules.HasAsync("audio", ct))
        {
            var have = await SpeechHasAsync(ct);
            list.AddRange(MediaModels.Speech.Select(m => new DefaultModel("audio", m.Name, m.Id, have is null ? "unknown" : have.Contains(m.Id) ? "present" : "missing")));
        }
        if (engine.Value.Enabled && engine.Value.FirstModel is { Length: > 0 } first)
        {
            var file = LocalFirst(first.Trim());
            list.Add(new DefaultModel("chat", first.Trim(), file?.File ?? first.Trim(), file is not null ? "present" : going.Any(d => first.StartsWith(d, StringComparison.Ordinal)) ? "downloading" : "missing"));
        }
        return list;
    }

    /// <summary>The files a server reads that are not in the library: one download per repository.</summary>
    private async Task<IReadOnlyList<string>> ServerFilesAsync(IReadOnlyList<MediaModels.ServerFile> files, AppDbContext db, HuggingFace hf, CancellationToken ct)
    {
        var started = new List<string>();
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
            started.Add($"{repo.Key.Dir}: {names}");
        }
        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            // Woken once they are saved: woken before, it finds none and sleeps a minute.
            downloads.Wake();
        }
        return started;
    }

    /// <summary>
    /// Asks the speech server for its models; it fetches them from Hugging Face itself. Once a start for
    /// every one (a download cut short is listed as there: asking again finishes it, and a whole one
    /// answers at once), then only for those it does not list.
    /// </summary>
    private async Task<IReadOnlyList<string>> SpeechAsync(CancellationToken ct)
    {
        var client = http.CreateClient(Client);
        var have = await SpeechHasAsync(ct) ?? [];
        var all = !_speechAsked;
        var started = new List<string>();
        foreach (var (name, id, _) in MediaModels.Speech.Where(s => all || !have.Contains(s.Id)))
        {
            LogSpeech(logger, name, id);
            using var res = await client.PostAsync($"{MediaModels.AudioUrl}/v1/models/{id}", null, ct);
            if (!res.IsSuccessStatusCode)
            {
                LogSpeechFailed(logger, id, (int)res.StatusCode);
            }
            else if (!have.Contains(id))
            {
                started.Add($"speech: {name}");
            }
        }
        _speechAsked = true;
        return started;
    }

    /// <summary>The models the speech server has; null when it cannot be asked.</summary>
    private async Task<HashSet<string>?> SpeechHasAsync(CancellationToken ct)
    {
        try
        {
            var list = await http.CreateClient(Client).GetFromJsonAsync<JsonObject>($"{MediaModels.AudioUrl}/v1/models", ct);
            return [.. (list?["data"] as JsonArray ?? []).OfType<JsonObject>().Select(m => m["id"]?.GetValue<string>()).OfType<string>()];
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// MODEL's files in the library, without asking Hugging Face: a .gguf named, or a repository's quant (its first part
    /// when split) and its projector, as a download (or a copy of one) leaves them, under the repository's folder.
    /// </summary>
    private (string File, string? Projector)? LocalFirst(string first)
    {
        if (first.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            return File.Exists(Path.Combine(downloads.Root, first.TrimStart('/'))) ? (first.TrimStart('/'), null) : null;
        }
        var at = first.LastIndexOf(':');
        var (repo, quant) = at > 0 ? (first[..at], first[(at + 1)..]) : (first, "Q4_K_M");
        var folder = Path.Combine(downloads.Root, repo);
        if (!Directory.Exists(folder))
        {
            return null;
        }
        var ggufs = Directory.EnumerateFiles(folder, "*.gguf", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(downloads.Root, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
        bool Projector(string f) => Path.GetFileName(f).Contains("mmproj", StringComparison.OrdinalIgnoreCase);
        var parts = ggufs.Where(f => !Projector(f) && Path.GetFileName(f).Contains(quant, StringComparison.OrdinalIgnoreCase)).ToList();
        if (parts.Count == 0)
        {
            return null;
        }
        // A split model: all its parts, or not there yet.
        var split = SplitPart().Match(Path.GetFileNameWithoutExtension(parts[0]));
        if (split.Success && parts.Count < int.Parse(split.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
        {
            return null;
        }
        var projector = ggufs.Where(Projector).OrderBy(f => f.Contains("F16", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
        return (parts[0], projector);
    }

    /// <summary>
    /// MODEL: a file in the library, or "owner/repo:QUANT" from Hugging Face (with the repository's
    /// vision projector, when it has one). Added once it is all there, with the settings recommended for
    /// this machine, and kept loaded.
    /// </summary>
    private async Task<IReadOnlyList<string>> FirstModelAsync(string first, bool download, IServiceProvider services, AppDbContext db, HuggingFace hf, CancellationToken ct)
    {
        var library = services.GetRequiredService<ModelLibrary>();
        string file;
        string? projector = null;
        if (LocalFirst(first) is { } local)
        {
            (file, projector) = local;
            _toldMissing = false;
        }
        else if (!download || first.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            if (!_toldMissing)
            {
                LogFirstMissing(logger, first);
                _toldMissing = true;
            }
            return [];
        }
        else
        {
            var at = first.LastIndexOf(':');
            var (repoId, quant) = at > 0 ? (first[..at], first[(at + 1)..]) : (first, "Q4_K_M");
            var repo = await hf.RepoAsync(repoId, ct);
            if (repo.Models.FirstOrDefault(m => !m.Projector && string.Equals(m.Quant, quant, StringComparison.OrdinalIgnoreCase)) is not { } model)
            {
                LogNoQuant(logger, repoId, quant);
                return [];
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
                    return [$"chat: {fetching}"];
                }
                return [];
            }
        }
        if (library.Find(file) is not { } entry)
        {
            LogNotInLibrary(logger, file);
            return [];
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
        return [];
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Setup: downloading the default models, as the installer was told (DEFAULT_MODELS=download); once")]
    private static partial void LogInstallerDownload(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup: MODEL ({Model}) is not in the model library, and the app fetches nothing on its own: download the default models (the installer, or Admin -> Models -> Default models), or copy its files into the library")]
    private static partial void LogFirstMissing(ILogger logger, string model);
}
