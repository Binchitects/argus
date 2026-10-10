using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Operations;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>
/// The picture, video and speech models, managed as the engine's are: on or off, kept loaded or not,
/// loaded and unloaded by hand, and who may use them (the models' access rules). The picture and video
/// servers run while their control file says "on" (services/sd-serve.sh reads it); the speech server
/// loads and unloads its models through its own API. One not kept loaded loads when asked for, and
/// unloads after ten minutes unused. With several replicas, the one that leads writes the control
/// files and loads the speech models; a model asked for on another is told to it.
/// </summary>
public sealed partial class MediaControl : BackgroundService
{
    public const string Client = "media";
    private const string SettingPrefix = "media.";
    private const string AskedTopic = "media:asked";
    private const string ForgetTopic = "media:forget";
    private const string WakeTopic = "media:wake";
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);

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

    public MediaControl(IServiceScopeFactory scopes, Modules modules, IHttpClientFactory http, IOptions<EngineOptions> engine, TimeProvider clock, Replicas replicas,
        ILogger<MediaControl> logger)
    {
        (this.scopes, this.modules, this.http, this.engine, this.clock, this.replicas, this.logger) = (scopes, modules, http, engine, clock, replicas, logger);
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
    }

    /// <summary>Asked for now (loaded, or used): here, and on the replica that leads; the control file is written at once.</summary>
    private void Asked(string name)
    {
        _asked[name] = clock.GetUtcNow();
        replicas.Tell(AskedTopic, name);
        WakeHere();
    }

    private void WakeHere()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    /// <summary>A media model: what it does, the server it runs on, and its id there (the speech server's).</summary>
    public sealed record Model(string Name, string Mode, string Server, string? Id, bool KeptByDefault);

    public static readonly IReadOnlyList<Model> All =
    [
        new(MediaModels.ImageModel, "image_generation", "imagegen", null, true),
        new(MediaModels.VideoModel, "video_generation", "videogen", null, false),
        .. MediaModels.Speech.Select(s => new Model(s.Name, s.Mode, "audio", s.Id, s.Mode == "audio_transcription")),
    ];

    public static Model? Find(string name) => All.FirstOrDefault(m => m.Name == name);

    public sealed record State(bool Enabled, bool Kept);

    /// <summary>
    /// What a model is doing: loaded, loading, unloading (its server still answers as it stops), unloaded, failed (its server
    /// did not answer within ten minutes of being turned on; it is still tried), waiting (for its files), or off (its server
    /// does not run).
    /// </summary>
    public sealed record Status(Model Model, State State, string Now);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _asked = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _now = new(StringComparer.Ordinal);

    /// <summary>Each picture and video server's control file as last written: "on" since when (Environment.TickCount64), or off (absent).</summary>
    private readonly ConcurrentDictionary<string, long> _onSince = new(StringComparer.Ordinal);

    /// <summary>Since when each model has been loading (by the app's clock): past <see cref="LoadLimit"/> it has failed.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _loadingSince = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wake = new(0);

    public async Task<IReadOnlyList<Status>> ListAsync(CancellationToken ct)
    {
        var states = await StatesAsync(ct);
        return [.. All.Select(m => new Status(m, states[m.Name], _now.GetValueOrDefault(m.Name, "unloaded")))];
    }

    public async Task<State> StateAsync(string name, CancellationToken ct) => (await StatesAsync(ct))[name];

    public async Task SetAsync(string name, bool? enabled = null, bool? kept = null, CancellationToken ct = default)
    {
        var now = await StateAsync(name, ct);
        var next = new State(enabled ?? now.Enabled, kept ?? now.Kept);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = SettingPrefix + name;
        var value = JsonSerializer.Serialize(next);
        if (await db.Settings.SingleOrDefaultAsync(s => s.Key == key, ct) is { } row)
        {
            (row.Value, row.UpdatedAt) = (value, clock.GetUtcNow());
        }
        else
        {
            db.Settings.Add(new Setting { Key = key, Value = value });
        }
        await db.SaveChangesAsync(ct);
        WakeHere();
        replicas.Tell(WakeTopic);
    }

    /// <summary>Loads it now; one not kept loaded unloads again after ten minutes unused.</summary>
    public void Load(string name)
    {
        Asked(name);
        _now[name] = "loading";
        WakeHere();
    }

    /// <summary>Unloads it now (and stops keeping it loaded, as with the engine's models).</summary>
    public async Task UnloadAsync(string name, CancellationToken ct)
    {
        _asked.TryRemove(name, out _);
        replicas.Tell(ForgetTopic, name);
        if ((await StateAsync(name, ct)).Kept)
        {
            await SetAsync(name, kept: false, ct: ct);
        }
        if (Find(name) is { Id: { } id })
        {
            using var _ = await http.CreateClient(Client).DeleteAsync($"{MediaModels.AudioUrl}/api/ps/{id}", ct);
        }
        WakeHere();
    }

    /// <summary>Used now: it stays loaded another ten minutes.</summary>
    public void Touch(string name)
    {
        if (_asked.ContainsKey(name))
        {
            Asked(name);
        }
    }

    /// <summary>
    /// For a tool that needs it: loaded when it is not, waiting until it answers (a few minutes at most).
    /// Null when it is ready, else why not.
    /// </summary>
    public async Task<string?> ReadyAsync(string name, CancellationToken ct)
    {
        var model = Find(name)!;
        if (!(await StateAsync(name, ct)).Enabled)
        {
            return $"{name} is turned off (Admin -> Models).";
        }
        // Not this stack's server: the gateway answers for it from wherever it is, or says why not.
        if (!await modules.HasAsync(model.Server, ct))
        {
            return null;
        }
        Asked(name);
        if (model.Id is not null || (On(model.Server) && await UpAsync(model, ct)))
        {
            return null;
        }
        // Off, or turned off a moment ago (ten minutes unused): its server answering now may be on its way out. On again first,
        // and its answer counts once the script has read that.
        Load(name);
        var until = clock.GetUtcNow() + TimeSpan.FromMinutes(5);
        while (clock.GetUtcNow() < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            if (On(model.Server) && await UpAsync(model, ct))
            {
                _now[name] = "loaded";
                return null;
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
        var states = await StatesAsync(ct);
        var now = clock.GetUtcNow();
        foreach (var (name, at) in _asked)
        {
            // Only that ask: one made meanwhile (a picture asked for at this moment) keeps it loaded.
            if (now - at > Idle && !states[name].Kept && _asked.TryRemove(new KeyValuePair<string, DateTimeOffset>(name, at)))
            {
                LogIdle(logger, name);
            }
        }
        IReadOnlyList<string>? speechLoaded = null;
        foreach (var m in All)
        {
            var s = states[m.Name];
            var want = s.Enabled && (s.Kept || _asked.ContainsKey(m.Name));
            if (!await modules.HasAsync(m.Server, ct))
            {
                _now[m.Name] = "off";
                continue;
            }
            if (m.Id is null)
            {
                if (lead)
                {
                    Write(m.Server, want ? "on\n" : "off\n");
                }
                if (want)
                {
                    _onSince.TryAdd(m.Server, Environment.TickCount64);
                }
                else
                {
                    _onSince.TryRemove(m.Server, out _);
                }
                var files = MediaModels.Servers.First(x => x.Server == m.Server).Files;
                var ready = files.All(f => File.Exists(Path.Combine(engine.Value.LibraryDir, f.Dir, f.Name)));
                var up = ready && await UpAsync(m, ct);
                _now[m.Name] = !ready ? "waiting" : up ? (want ? "loaded" : "unloading") : want ? Loading(m.Name, now) : "unloaded";
                if (up || !want || !ready)
                {
                    _loadingSince.TryRemove(m.Name, out _);
                }
                continue;
            }
            speechLoaded ??= await SpeechLoadedAsync(ct);
            var loaded = speechLoaded.Contains(m.Id);
            // The replica that leads loads and unloads; the others only read what the speech server has.
            if (lead && want && !loaded)
            {
                using var res = await http.CreateClient(Client).PostAsync($"{MediaModels.AudioUrl}/api/ps/{m.Id}", null, ct);
                loaded = res.IsSuccessStatusCode;
            }
            else if (lead && !s.Enabled && loaded)
            {
                using var _ = await http.CreateClient(Client).DeleteAsync($"{MediaModels.AudioUrl}/api/ps/{m.Id}", ct);
                loaded = false;
            }
            _now[m.Name] = loaded ? "loaded" : "unloaded";
        }
    }

    /// <summary>Whether a server's control file has said "on" long enough for its answer to count (<see cref="OnSettle"/>).</summary>
    private bool On(string server) => _onSince.TryGetValue(server, out var since) && Environment.TickCount64 - since >= OnSettle.TotalMilliseconds;

    /// <summary>"loading", or "failed" once it has loaded for longer than <see cref="LoadLimit"/> (it is still tried: its script starts it again).</summary>
    private string Loading(string name, DateTimeOffset now)
    {
        var since = _loadingSince.GetOrAdd(name, now);
        if (now - since < LoadLimit)
        {
            return "loading";
        }
        if (_now.GetValueOrDefault(name) != "failed")
        {
            LogNotLoaded(logger, name, (int)LoadLimit.TotalMinutes);
        }
        return "failed";
    }

    private async Task<Dictionary<string, State>> StatesAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith(SettingPrefix)).ToDictionaryAsync(s => s.Key[SettingPrefix.Length..], s => s.Value, ct);
        return All.ToDictionary(m => m.Name, m => rows.TryGetValue(m.Name, out var v) && JsonSerializer.Deserialize<State>(v) is { } s ? s : new State(true, m.KeptByDefault));
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
        var path = Path.Combine(engine.Value.ConfigDir, server);
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == text)
            {
                return;
            }
            Directory.CreateDirectory(engine.Value.ConfigDir);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Media models: {Model} unused for ten minutes, unloading")]
    private static partial void LogIdle(ILogger logger, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Media models: {Model}'s server has not answered for {Minutes} minutes since it was turned on (its log says why: often no GPU memory left beside the chat models); it is still tried")]
    private static partial void LogNotLoaded(ILogger logger, string model, int minutes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Media models: cannot write {Path}: {Error}")]
    private static partial void LogControl(ILogger logger, string path, string error);
}
