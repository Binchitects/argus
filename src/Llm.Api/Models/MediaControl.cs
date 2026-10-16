using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Operations;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>
/// The picture, video and speech models (rows of media_models), managed as the engine's are: kept loaded
/// or not, loaded and unloaded by hand, and who may use them (the models' access rules). Each picture and
/// video server runs one model at a time: the one kept loaded, else the one asked for last; the app writes
/// its model, files and flags in the server's control file (services/sd-serve.sh reads it, and says what
/// it does in its status file). The speech server loads and unloads its models through its own API (its
/// own unloading is off). One not kept loaded loads when asked for or used, and unloads after its idle
/// minutes (ten by default). With several replicas, the one that leads writes the control files and
/// loads the speech models; a model asked for on another is told to it.
/// </summary>
/// <summary>Configuration section "Media": the models the chat uses unless a person names another (Settings → Model).</summary>
public sealed class MediaOptions
{
    public string? PictureModel { get; set; }
    public string? VideoModel { get; set; }
    public string? TranscriptionModel { get; set; }
}

public sealed partial class MediaControl : BackgroundService
{
    public const string Client = "media";
    private const string AskedTopic = "media:asked";
    private const string ChangedTopic = "media:changed";
    private const string ForgetTopic = "media:forget";
    private const string WakeTopic = "media:wake";
    private const string HeldTopic = "media:held";
    private const string RestartTopic = "media:restart";
    private const int IdleMinutes = 10;

    /// <summary>The picture and video servers, each running one model at a time.</summary>
    public static readonly IReadOnlyList<string> FileServers = ["imagegen", "videogen"];

    /// <summary>A server that has not answered this long after it was turned on has failed to load (Admin → Models says so).</summary>
    private static readonly TimeSpan LoadLimit = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a server must have been wanted on before its answer counts: services/sd-serve.sh reads the control file every
    /// 3 seconds, so for a moment after "off" was written and "on" again it may still stop the one that answers.
    /// </summary>
    private static readonly TimeSpan OnSettle = TimeSpan.FromSeconds(4);
    private readonly IServiceScopeFactory scopes;
    private readonly Modules modules;
    private readonly IHttpClientFactory http;
    private readonly IOptions<EngineOptions> engine;
    private readonly TimeProvider clock;
    private readonly Replicas replicas;
    private readonly ILogger<MediaControl> logger;
    private readonly IOptionsMonitor<MediaOptions> choices;

    public MediaControl(IServiceScopeFactory scopes, Modules modules, IHttpClientFactory http, IOptions<EngineOptions> engine, TimeProvider clock, Replicas replicas,
        ILogger<MediaControl> logger, IOptionsMonitor<MediaOptions> choices)
    {
        (this.scopes, this.modules, this.http, this.engine, this.clock, this.replicas, this.logger, this.choices) = (scopes, modules, http, engine, clock, replicas, logger, choices);
        replicas.On(AskedTopic, name =>
        {
            if (Find(name) is not null)
            {
                _asked[name] = clock.GetUtcNow();
                WakeHere();
            }
            return true;
        });
        replicas.On(ForgetTopic, name =>
        {
            _asked.TryRemove(name, out _);
            WakeHere();
            return true;
        });
        replicas.On(WakeTopic, _ =>
        {
            WakeHere();
            return true;
        });
        replicas.On(ChangedTopic, _ =>
        {
            _models = null;
            WakeHere();
            return true;
        });
        replicas.On(HeldTopic, name =>
        {
            if (name.StartsWith('-'))
            {
                UnholdHere(name[1..]);
            }
            else
            {
                HoldHere(name);
            }
            WakeHere();
            return true;
        });
        replicas.On(RestartTopic, server =>
        {
            RestartHere(server);
            WakeHere();
            return true;
        });
    }

    /// <summary>A model added, changed or removed: read again here and on every replica.</summary>
    public async Task ChangedAsync(CancellationToken ct)
    {
        await RefreshAsync(ct);
        replicas.Tell(ChangedTopic);
        WakeHere();
    }

    /// <summary>Asked for now (loaded, or used): here, and on the replica that leads; the control file is written at once.</summary>
    private void Asked(string name)
    {
        _asked[name] = clock.GetUtcNow();
        replicas.Tell(AskedTopic, name);
        WakeHere();
    }

    /// <summary>Looks at every model again now.</summary>
    public void Wake() => WakeHere();

    private void WakeHere()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    /// <summary>A media model: what it does, the server it runs on, its id there (the speech server's), and its row.</summary>
    public sealed record Model(string Name, string Mode, string Server, string? Id, MediaModel Row);

    private volatile IReadOnlyList<Model>? _models;

    /// <summary>Every picture, video and speech model, as last read.</summary>
    public IReadOnlyList<Model> All => _models ?? LoadNow();

    public Model? Find(string name) => All.FirstOrDefault(m => m.Name == name);

    /// <summary>
    /// The model of a kind to use: the one named (a person's choice), else the one Settings names for the chat, else the
    /// first; only among <paramref name="allowed"/> when given (the models' access rules), and null when none is.
    /// </summary>
    public Model? Pick(string mode, string? named = null, IReadOnlySet<string>? allowed = null)
    {
        var o = choices.CurrentValue;
        var preferred = mode switch
        {
            MediaModes.Image => o.PictureModel,
            MediaModes.Video => o.VideoModel,
            MediaModes.Transcription => o.TranscriptionModel,
            _ => null,
        };
        var of = All.Where(m => m.Mode == mode && (allowed is null || allowed.Contains(m.Name))).ToList();
        return of.FirstOrDefault(m => m.Name == named) ?? of.FirstOrDefault(m => m.Name == preferred) ?? (of.Count > 0 ? of[0] : null);
    }

    private static IReadOnlyList<Model> Of(IEnumerable<MediaModel> rows) =>
        [.. rows.OrderBy(r => r.Name, StringComparer.Ordinal).Select(r => new Model(r.Name, r.Mode, r.Server, r.SpeechId, r))];

    private IReadOnlyList<Model> LoadNow()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return _models = Of(db.MediaModels.AsNoTracking().ToList());
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _models = Of(await db.MediaModels.AsNoTracking().ToListAsync(ct));
    }

    /// <summary>
    /// What a model is doing: loaded, loading, unloading (its server still answers as it stops), unloaded, failed (its server
    /// did not answer within ten minutes of being turned on; it is still tried), waiting (for its files), or off (its server
    /// does not run).
    /// </summary>
    public sealed record Status(Model Model, bool Kept, string Now, string? Why = null, DateTimeOffset? Since = null, MediaFailure? Failure = null);

    /// <summary>Why each model loads or unloads, and since when (Admin -> Models says so, as for chat models).</summary>
    private readonly ConcurrentDictionary<string, (string Why, DateTimeOffset Since)> _why = new(StringComparer.Ordinal);

    /// <summary>Each model's failure to load, while it lasts (gone once it loads).</summary>
    private readonly ConcurrentDictionary<string, MediaFailure> _failures = new(StringComparer.Ordinal);

    /// <summary>Since when each server has run its generation (by the app's clock), to tell an exit at once from a crash.</summary>
    private readonly ConcurrentDictionary<string, (string Gen, DateTimeOffset Since)> _running = new(StringComparer.Ordinal);

    private void Because(string name, string why)
    {
        if (!_why.TryGetValue(name, out var now) || now.Why != why)
        {
            _why[name] = (why, clock.GetUtcNow());
        }
    }

    /// <summary>
    /// Why it loads, unless something said why already (an admin's Load, a job that made room for it): only in place of
    /// none, or of why it unloaded last.
    /// </summary>
    private void BecauseUnlessSaid(string name, string why)
    {
        if (!_why.TryGetValue(name, out var said) || said.Why.StartsWith("unused", StringComparison.Ordinal) || said.Why.StartsWith("unloaded", StringComparison.Ordinal))
        {
            Because(name, why);
        }
    }

    /// <summary>Why it cannot load now: it failed lately and its wait is not over (or it waits to be changed); null: it may.</summary>
    private string? NotYet(string name)
    {
        if (_failures.GetValueOrDefault(name) is not { } f || f.RetryAt is { } at && clock.GetUtcNow() >= at)
        {
            return null;
        }
        var when = f.RetryAt is { } retry ? $"it is tried again from {retry:HH:mm} UTC" : "it is not tried again until an admin changes or loads it";
        return $"{name} could not load ({f.Class}: {f.Detail}); {when} (Admin -> Models).";
    }

    /// <summary>Its failure, while it lasts.</summary>
    public MediaFailure? FailureOf(string name) => _failures.GetValueOrDefault(name);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _asked = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _now = new(StringComparer.Ordinal);

    /// <summary>Each picture and video server's control file as last written: "on" since when (Environment.TickCount64), or off (absent).</summary>
    private readonly ConcurrentDictionary<string, long> _onSince = new(StringComparer.Ordinal);

    /// <summary>Since when each model has been loading (by the app's clock): past <see cref="LoadLimit"/> it has failed.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _loadingSince = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wake = new(0);

    public async Task<IReadOnlyList<Status>> ListAsync(CancellationToken ct)
    {
        await RefreshAsync(ct);
        return [.. All.Select(m =>
        {
            var now = _now.GetValueOrDefault(m.Name, "unloaded");
            var why = now is "loading" or "unloading" or "loaded" or "failed" ? _why.GetValueOrDefault(m.Name) : default;
            return new Status(m, m.Row.Kept, now, why.Why, why.Why is null ? null : why.Since, _failures.GetValueOrDefault(m.Name));
        })];
    }

    /// <summary>Kept loaded or not (at most one of a picture or video server's models is: keeping one stops keeping the others).</summary>
    public async Task SetKeptAsync(string name, bool kept, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.MediaModels.SingleOrDefaultAsync(m => m.Name == name, ct) is not { } row)
        {
            return;
        }
        row.Kept = kept;
        row.UpdatedAt = clock.GetUtcNow();
        if (kept && FileServers.Contains(row.Server))
        {
            foreach (var other in await db.MediaModels.Where(m => m.Name != name && m.Kept).ToListAsync(ct))
            {
                if (other.Server == row.Server)
                {
                    other.Kept = false;
                }
            }
        }
        await db.SaveChangesAsync(ct);
        await ChangedAsync(ct);
    }

    /// <summary>Loads it now; one not kept loaded unloads again after ten minutes unused.</summary>
    public void Load(string name)
    {
        // By hand: at once, whatever failed before (a new generation of its server's control file).
        if (_failures.TryRemove(name, out _) && Find(name) is { Id: null } m)
        {
            _gen[m.Server] = Gen(m.Server) + 1;
        }
        Because(name, "loaded by an admin");
        Asked(name);
        _now[name] = "loading";
        WakeHere();
    }

    /// <summary>Changed by an admin: whatever failed before is tried again at once, with its new settings.</summary>
    public void Forgive(string name)
    {
        if (_failures.TryRemove(name, out _) && Find(name) is { Id: null } m)
        {
            _gen[m.Server] = Gen(m.Server) + 1;
        }
        WakeHere();
    }

    /// <summary>Unloads it now (and stops keeping it loaded, as with the engine's models).</summary>
    public async Task UnloadAsync(string name, CancellationToken ct)
    {
        Because(name, "unloaded by an admin");
        _failures.TryRemove(name, out _);
        _asked.TryRemove(name, out _);
        replicas.Tell(ForgetTopic, name);
        if (Find(name) is { Row.Kept: true })
        {
            await SetKeptAsync(name, false, ct);
        }
        if (Find(name) is { Id: { } id })
        {
            try
            {
                using var _ = await http.CreateClient(Client).DeleteAsync($"{MediaModels.AudioUrl}/api/ps/{id}", ct);
            }
            catch (HttpRequestException)
            {
                // The speech server is down: it has nothing loaded then.
            }
        }
        WakeHere();
    }

    /// <summary>
    /// The model each picture and video server runs for a job now (<see cref="GpuShare"/>), and until when at most (should
    /// the replica that holds it stop without letting go): its server does not switch to another meanwhile.
    /// </summary>
    private readonly ConcurrentDictionary<string, (string Name, DateTimeOffset Until)> _held = new(StringComparer.Ordinal);

    /// <summary>A job holds <paramref name="name"/> on its server (here and on the replica that leads), until <see cref="Unhold"/>.</summary>
    public void Hold(string name)
    {
        HoldHere(name);
        replicas.Tell(HeldTopic, name);
        Asked(name);
    }

    /// <summary>The job that held <paramref name="name"/> ended.</summary>
    public void Unhold(string name)
    {
        UnholdHere(name);
        replicas.Tell(HeldTopic, "-" + name);
        WakeHere();
    }

    private void HoldHere(string name)
    {
        if (Find(name) is { Id: null } m)
        {
            _held[m.Server] = (name, clock.GetUtcNow() + GpuShare.LendLimit);
        }
    }

    private void UnholdHere(string name)
    {
        if (Find(name) is { } m && _held.TryGetValue(m.Server, out var held) && held.Name == name)
        {
            _held.TryRemove(new KeyValuePair<string, (string, DateTimeOffset)>(m.Server, held));
        }
    }

    /// <summary>
    /// Starts <paramref name="server"/> again (a new generation of its control file, on every replica): its script decides
    /// anew where the model decodes, by the GPU memory free then. <paramref name="why"/> is what Admin → Models says.
    /// </summary>
    public void Restart(string server, string why)
    {
        RestartHere(server);
        replicas.Tell(RestartTopic, server);
        if (_target.GetValueOrDefault(server) is { } name)
        {
            Because(name, why);
            _now[name] = "loading";
        }
        WakeHere();
    }

    private void RestartHere(string server)
    {
        _gen[server] = Gen(server) + 1;
        if (_target.GetValueOrDefault(server) is { } name)
        {
            _loadingSince.TryRemove(name, out _);
        }
    }

    /// <summary>Why <paramref name="name"/> loads now, for Admin → Models (a job made room for it).</summary>
    public void Explain(string name, string why) => Because(name, why);

    /// <summary>A job is done with <paramref name="name"/> and the room it had goes back: unloaded now unless kept loaded, as after its idle minutes.</summary>
    public void Done(string name, string why)
    {
        if (Find(name) is not { Row.Kept: false })
        {
            return;
        }
        Because(name, why);
        _asked.TryRemove(name, out _);
        replicas.Tell(ForgetTopic, name);
        WakeHere();
    }

    /// <summary>Used now: loaded if it is not (a speech model loads at its first request), and it stays another ten minutes.</summary>
    public void Touch(string name)
    {
        if (Find(name) is not null)
        {
            Asked(name);
        }
    }

    /// <summary>Every model of a kind used now (a request that names its model in a form the app does not read).</summary>
    public void TouchMode(string mode)
    {
        foreach (var m in All.Where(m => m.Mode == mode))
        {
            Asked(m.Name);
        }
    }

    /// <summary>
    /// For a tool that needs it: loaded when it is not, waiting until it answers (a few minutes at most).
    /// Null when it is ready, else why not.
    /// </summary>
    public async Task<string?> ReadyAsync(string name, CancellationToken ct)
    {
        if (Find(name) is not { } model)
        {
            return $"There is no picture, video or speech model named {name} (Admin -> Models).";
        }
        // Not this stack's server: the gateway answers for it from wherever it is, or says why not.
        if (!await modules.HasAsync(model.Server, ct))
        {
            return null;
        }
        Asked(name);
        if (model.Id is not null || (On(model) && await UpAsync(model, ct)))
        {
            return null;
        }
        // It failed to load lately: not tried again until its wait is over (as a chat model), and the person is told why.
        if (NotYet(name) is { } failed)
        {
            return failed;
        }
        // Off, or turned off a moment ago (ten minutes unused): its server answering now may be on its way out. On again first,
        // and its answer counts once the script has read that.
        BecauseUnlessSaid(name, "for a request");
        _now[name] = "loading";
        WakeHere();
        var until = clock.GetUtcNow() + TimeSpan.FromMinutes(5);
        while (clock.GetUtcNow() < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            if (On(model) && await UpAsync(model, ct))
            {
                _now[name] = "loaded";
                return null;
            }
            if (NotYet(name) is { } now)
            {
                return now;
            }
        }
        return $"{name} did not load within five minutes (its server's log says why).";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogFailed(logger, ex.Message);
            }
            try
            {
                await _wake.WaitAsync(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Each model as it should be: the control files written, kept speech models loaded (by the replica that leads), and what each is doing now.</summary>
    private async Task TickAsync(CancellationToken ct)
    {
        var lead = replicas.IsLeader;
        await RefreshAsync(ct);
        var models = All;
        var now = clock.GetUtcNow();
        foreach (var (name, at) in _asked)
        {
            var model = models.FirstOrDefault(m => m.Name == name);
            var idle = TimeSpan.FromMinutes(model?.Row.IdleMinutes ?? IdleMinutes);
            // Only that ask: one made meanwhile (a picture asked for at this moment) keeps it loaded.
            if ((model is null || now - at > idle && !model.Row.Kept) && _asked.TryRemove(new KeyValuePair<string, DateTimeOffset>(name, at)))
            {
                if (model is not null)
                {
                    Because(name, $"unused for {idle.TotalMinutes:0} minutes");
                    LogIdle(logger, name, (int)idle.TotalMinutes);
                }
            }
        }
        foreach (var server in FileServers)
        {
            await ServerAsync(server, [.. models.Where(m => m.Server == server)], lead, now, ct);
        }
        IReadOnlyList<string>? speechLoaded = null;
        foreach (var m in models.Where(x => x.Id is not null))
        {
            var want = m.Row.Kept || _asked.ContainsKey(m.Name);
            if (!await modules.HasAsync(m.Server, ct))
            {
                _now[m.Name] = "off";
                continue;
            }
            speechLoaded ??= await SpeechLoadedAsync(ct);
            var loaded = speechLoaded.Contains(m.Id!);
            // The replica that leads loads and unloads; the others only read what the speech server has. Why it loads, unless an
            // admin's Load said so (as for the picture and video servers' models).
            if (want)
            {
                BecauseUnlessSaid(m.Name, m.Row.Kept ? "kept loaded" : "for a request");
            }
            var waiting = _failures.TryGetValue(m.Name, out var refused) && refused.RetryAt is { } next && now < next;
            if (lead && want && !loaded && !waiting)
            {
                using var res = await http.CreateClient(Client).PostAsync($"{MediaModels.AudioUrl}/api/ps/{m.Id}", null, ct);
                loaded = res.IsSuccessStatusCode;
                if (!loaded)
                {
                    var said = await res.Content.ReadAsStringAsync(ct);
                    Failed(m.Name, "refused", $"HTTP {(int)res.StatusCode}: {said[..Math.Min(said.Length, 300)]}", "", now);
                }
            }
            if (loaded && want)
            {
                _failures.TryRemove(m.Name, out _);
            }
            else if (lead && !want && loaded)
            {
                // Unused for its idle minutes and not kept loaded: the speech server keeps it until told (its TTL is off).
                using var _ = await http.CreateClient(Client).DeleteAsync($"{MediaModels.AudioUrl}/api/ps/{m.Id}", ct);
                loaded = false;
            }
            _now[m.Name] = loaded ? "loaded" : want && _failures.ContainsKey(m.Name) ? "failed" : "unloaded";
        }
        if (lead && speechLoaded is not null)
        {
            await IdleVadAsync(models, speechLoaded, now, ct);
        }
    }

    /// <summary>
    /// The speech server's voice activity detector (silero_vad_*), which it loads by itself for a transcription and which has
    /// no row: unloaded once no speech to text model has been asked for within the idle minutes, as a model not kept loaded is
    /// (the speech server's own unloading is off).
    /// </summary>
    private async Task IdleVadAsync(IReadOnlyList<Model> models, IReadOnlyList<string> loaded, DateTimeOffset now, CancellationToken ct)
    {
        var heard = models.Where(m => m.Mode == MediaModes.Transcription).Select(m => _asked.TryGetValue(m.Name, out var at) ? at : default).DefaultIfEmpty().Max();
        if (heard != default && now - heard <= TimeSpan.FromMinutes(IdleMinutes))
        {
            return;
        }
        foreach (var id in loaded.Where(i => i.StartsWith(VadPrefix, StringComparison.Ordinal) && models.All(m => m.Id != i)))
        {
            LogVad(logger, id, IdleMinutes);
            using var _ = await http.CreateClient(Client).DeleteAsync($"{MediaModels.AudioUrl}/api/ps/{id}", ct);
        }
    }

    /// <summary>The speech server's voice activity detector's id starts so (silero_vad_v5).</summary>
    public const string VadPrefix = "silero_vad";

    /// <summary>The model a picture or video server runs, and what each of its models is doing.</summary>
    private async Task ServerAsync(string server, IReadOnlyList<Model> rows, bool lead, DateTimeOffset now, CancellationToken ct)
    {
        if (!await modules.HasAsync(server, ct))
        {
            foreach (var x in rows)
            {
                _now[x.Name] = "off";
            }
            return;
        }
        // The one a job holds (it never switches under a running job), else the one kept loaded, else the one asked for last;
        // else none (the server is off, its last model named).
        var wanted = rows.FirstOrDefault(x => _held.TryGetValue(server, out var held) && held.Until > now && x.Name == held.Name)
            ?? rows.FirstOrDefault(x => x.Row.Kept)
            ?? rows.Where(x => _asked.ContainsKey(x.Name)).OrderByDescending(x => _asked.GetValueOrDefault(x.Name)).FirstOrDefault();
        var target = wanted ?? rows.FirstOrDefault(x => x.Name == _target.GetValueOrDefault(server)) ?? (rows.Count > 0 ? rows[0] : null);
        if (target is null)
        {
            if (lead)
            {
                Write(server, $"gen={Gen(server).ToString(CultureInfo.InvariantCulture)}\nwant=off\n");
            }
            return;
        }
        _target[server] = target.Name;
        var want = wanted is not null;
        var m = target;
        var sd = SdArgs.Of(m.Row);
        // Another model, or its settings changed: a new generation, which its script starts.
        var body = SdArgs.Conf(sd, 0, true).Split('\n', 3)[2];
        var was = _body.GetOrAdd(server, s => Body(s) ?? body);
        if (was != body)
        {
            _gen[server] = Gen(server) + 1;
            _body[server] = body;
            _loadingSince.TryRemove(m.Name, out _);
        }
        var status = StatusOf(server);
        var gen = Gen(server).ToString(CultureInfo.InvariantCulture);
        if (want)
        {
            BecauseUnlessSaid(m.Name, m.Row.Kept ? "kept loaded" : "for a request");
        }
        if (status is { Phase: "running" } && status.Gen == gen && (!_running.TryGetValue(server, out var r) || r.Gen != gen))
        {
            _running[server] = (gen, now);
        }
        // Its server exited while wanted (no GPU memory, flags it refused, a crash): why, once for this generation.
        if (want && status is { Phase: "exited" } && status.Gen == gen && _failures.GetValueOrDefault(m.Name)?.Gen != gen)
        {
            var ran = _running.TryGetValue(server, out var since) && since.Gen == gen ? (now - since.Since).TotalSeconds : 0;
            var (kind, detail) = MediaFailure.Classify(LogTail(server), status.Exit, ran);
            Failed(m.Name, kind, detail, gen, now);
        }
        // Tried again after its wait (1, 2, 4… minutes), as a new generation; flags it refused, not until it is changed or loaded.
        if (want && _failures.TryGetValue(m.Name, out var failed) && failed.Gen == gen && failed.RetryAt is { } retry && now >= retry)
        {
            LogRetry(logger, m.Name, failed.Class, failed.Count);
            _gen[server] = Gen(server) + 1;
            gen = Gen(server).ToString(CultureInfo.InvariantCulture);
            _loadingSince.TryRemove(m.Name, out _);
        }
        // While it waits to be tried again, its server is off.
        var holding = _failures.GetValueOrDefault(m.Name)?.Gen == gen;
        if (lead)
        {
            Write(server, SdArgs.Conf(sd, Gen(server), want && !holding));
        }
        if (want && !holding)
        {
            _onSince.TryAdd(server, Environment.TickCount64);
        }
        else
        {
            _onSince.TryRemove(server, out _);
        }
        var ready = status is not null ? status.Phase != "waiting" : sd.Files.All(f => File.Exists(Path.Combine(engine.Value.LibraryDir, f.Path)));
        var up = ready && !holding && (status is null || status.Phase is "running" or "stopping") && await UpAsync(m, ct);
        if (up && want)
        {
            _failures.TryRemove(m.Name, out _);
        }
        _now[m.Name] = !ready ? "waiting"
            : holding ? "failed"
            : status is { Phase: "stopping" } ? "unloading"
            : up ? (want ? "loaded" : "unloading")
            : want ? Loading(m.Name, now, gen) : "unloaded";
        if (up || !want || !ready || holding)
        {
            _loadingSince.TryRemove(m.Name, out _);
        }
        // Its other models: not running (waiting, when their files are not all there).
        foreach (var other in rows.Where(o => o.Name != m.Name))
        {
            _now[other.Name] = SdArgs.Of(other.Row).Files.All(f => File.Exists(Path.Combine(engine.Value.LibraryDir, f.Path))) ? "unloaded" : "waiting";
            _loadingSince.TryRemove(other.Name, out _);
        }
    }

    /// <summary>The model each picture and video server runs (or ran last), and its control file without its generation and want.</summary>
    private readonly ConcurrentDictionary<string, string> _target = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _body = new(StringComparer.Ordinal);

    /// <summary>The control file on disk without its generation and want (what the last run wrote), or null.</summary>
    private string? Body(string server)
    {
        try
        {
            var path = Path.Combine(engine.Value.ConfigDir, server + ".conf");
            return File.Exists(path)
                ? string.Concat(File.ReadAllLines(path).Where(l => !l.StartsWith("gen=", StringComparison.Ordinal) && !l.StartsWith("want=", StringComparison.Ordinal)).Select(l => l + "\n"))
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a server's answer counts as this model's: its status says it runs this model, at the generation the app
    /// wrote last; or, before its first status, its control file has named it on long enough (<see cref="OnSettle"/>).
    /// </summary>
    private bool On(Model model)
    {
        if (_target.GetValueOrDefault(model.Server) != model.Name)
        {
            return false;
        }
        if (StatusOf(model.Server) is { } status)
        {
            return status.Phase == "running" && status.Model == model.Name && status.Gen == Gen(model.Server).ToString(CultureInfo.InvariantCulture)
                && _onSince.ContainsKey(model.Server);
        }
        return _onSince.TryGetValue(model.Server, out var since) && Environment.TickCount64 - since >= OnSettle.TotalMilliseconds;
    }

    /// <summary>What a picture or video server says it does (services/sd-serve.sh's status file).</summary>
    public sealed record ServerStatus(string Gen, string Phase, DateTimeOffset Since, DateTimeOffset At, long? FreeMb, int? Exit, IReadOnlyList<string> Missing, string Vae, string Model);

    /// <summary>Its status, when it wrote one in the last 15 s (null: none, or its script has stopped).</summary>
    public ServerStatus? StatusOf(string server)
    {
        var path = Path.Combine(engine.Value.MediaStatusDir, server + ".json");
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject j)
            {
                return null;
            }
            var at = DateTimeOffset.FromUnixTimeSeconds(j["at"]?.GetValue<long>() ?? 0);
            if (DateTimeOffset.UtcNow - at > TimeSpan.FromSeconds(15))
            {
                return null;
            }
            return new ServerStatus(j["gen"]?.ToString() ?? "", j["phase"]?.ToString() ?? "off",
                DateTimeOffset.FromUnixTimeSeconds(j["since"]?.GetValue<long>() ?? 0), at,
                j["free_mb"] is JsonValue f && f.TryGetValue<long>(out var mb) ? mb : null,
                j["exit"] is JsonValue e && e.TryGetValue<int>(out var code) ? code : null,
                [.. (j["missing"] as JsonArray ?? []).Select(x => x?.ToString() ?? "")], j["vae"]?.ToString() ?? "", j["model"]?.ToString() ?? "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The generation of a server's control file: raised to start it again; carried on from the file the last run wrote.</summary>
    private long Gen(string server) => _gen.GetOrAdd(server, s =>
    {
        try
        {
            var path = Path.Combine(engine.Value.ConfigDir, s + ".conf");
            var line = File.Exists(path) ? File.ReadLines(path).FirstOrDefault(l => l.StartsWith("gen=", StringComparison.Ordinal)) : null;
            return line is not null && long.TryParse(line[4..], out var g) ? g : 1;
        }
        catch (IOException)
        {
            return 1;
        }
    });

    private readonly ConcurrentDictionary<string, long> _gen = new(StringComparer.Ordinal);

    /// <summary>"loading", or past <see cref="LoadLimit"/> a timeout: failed, its server stopped, and tried again after its wait.</summary>
    private string Loading(string name, DateTimeOffset now, string gen)
    {
        var since = _loadingSince.GetOrAdd(name, now);
        if (now - since < LoadLimit)
        {
            return "loading";
        }
        LogNotLoaded(logger, name, (int)LoadLimit.TotalMinutes);
        Failed(name, "timeout", $"its server did not answer within {(int)LoadLimit.TotalMinutes} minutes", gen, now);
        return "failed";
    }

    /// <summary>A failure to load: one more in a row (its wait doubles), since now.</summary>
    private void Failed(string name, string kind, string detail, string gen, DateTimeOffset now)
    {
        var count = (_failures.GetValueOrDefault(name)?.Count ?? 0) + 1;
        _failures[name] = new MediaFailure(kind, detail, count, now, gen);
        LogFailedLoad(logger, name, kind, detail);
    }

    /// <summary>The end of a server's output (its status folder's log), for why it exited.</summary>
    private string LogTail(string server)
    {
        try
        {
            var path = Path.Combine(engine.Value.MediaStatusDir, server + ".log");
            if (!File.Exists(path))
            {
                return "";
            }
            using var f = File.OpenRead(path);
            f.Seek(Math.Max(0, f.Length - 4096), SeekOrigin.Begin);
            using var reader = new StreamReader(f);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    private async Task<bool> UpAsync(Model m, CancellationToken ct)
    {
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(2));
            using var res = await http.CreateClient(Client).GetAsync($"http://{m.Server}:1234/v1/models", wait.Token);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<string>> SpeechLoadedAsync(CancellationToken ct)
    {
        try
        {
            var ps = await http.CreateClient(Client).GetFromJsonAsync<JsonObject>($"{MediaModels.AudioUrl}/api/ps", ct);
            return [.. (ps?["models"] as JsonArray ?? []).Select(x => x?.GetValue<string>() ?? "")];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return [];
        }
    }

    /// <summary>The control file a server's script reads, only when it changes.</summary>
    private void Write(string server, string text)
    {
        var path = Path.Combine(engine.Value.ConfigDir, server + ".conf");
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == text)
            {
                return;
            }
            Directory.CreateDirectory(engine.Value.ConfigDir);
            // Before v5.6 the file said only "on" or "off".
            File.Delete(Path.Combine(engine.Value.ConfigDir, server));
            File.WriteAllText(path + ".tmp", text);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogControl(logger, path, ex.Message);
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Media models: {Error}")]
    private static partial void LogFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Media models: {Model} unused for {Minutes} minutes, unloading")]
    private static partial void LogIdle(ILogger logger, string model, int minutes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Media models: {Model}'s server has not answered for {Minutes} minutes since it was turned on (its log says why: often no GPU memory left beside the chat models); it is still tried")]
    private static partial void LogNotLoaded(ILogger logger, string model, int minutes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Media models: {Model} failed to load ({Kind}): {Detail}")]
    private static partial void LogFailedLoad(ILogger logger, string model, string kind, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Media models: {Model} is tried again ({Kind}, {Count} in a row)")]
    private static partial void LogRetry(ILogger logger, string model, string kind, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Media models: the speech server's {Id} unused for {Minutes} minutes (no speech to text), unloading")]
    private static partial void LogVad(ILogger logger, string id, int minutes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Media models: cannot write {Path}: {Error}")]
    private static partial void LogControl(ILogger logger, string path, string error);
}
