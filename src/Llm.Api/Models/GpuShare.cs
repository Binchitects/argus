using System.Collections.Concurrent;
using Llm.Api.Chat;
using Llm.Api.Operations;
using Llm.Core.Models;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>
/// A GPU shared by the chat engines and the picture and video servers: the one the media server runs on (its model's GPU,
/// else Engine:MediaGpu, else the first), never the others. Picture and video jobs take turns (one at a time, the
/// gateway's pictures too), and each holds its model on its server, which does not switch to another under it. Before a
/// job whose model is short of GPU memory (a video model that would decode on the CPU, a picture model whose budget is
/// not free), the GPU is lent to it: the idle chat models not kept loaded unload (the engine call an admin's Unload
/// uses), the job waits for their memory, and a server already running the model starts again so it decides anew where
/// it decodes (services/sd-serve.sh decides as it starts). While the GPU is lent no chat model loads: a request for one
/// waits (EngineRoute, the chat says why), the engine's watcher loads none, and an admin's Load is refused. When the job
/// ends the room goes back: the model unloads (unless kept loaded), and the chat models unloaded for it that are kept
/// loaded or new chats use load again. Admin → Models says what happened.
/// </summary>
public sealed partial class GpuShare : IDisposable
{
    private const string LentTopic = "gpu:lent";
    private const string ReturnedTopic = "gpu:returned";

    /// <summary>How long a lending or a hold lasts at most, should the replica that has it stop without giving it back.</summary>
    public static readonly TimeSpan LendLimit = TimeSpan.FromHours(1);

    /// <summary>How long a job waits for idle chat models to make room, then for their memory, and its server to stop after it.</summary>
    public static readonly TimeSpan RoomWait = TimeSpan.FromMinutes(1);

    /// <summary>What stable-diffusion.cpp needs on the GPU beside its weights' budget (its compute buffers), as SdArgs counts it.</summary>
    public const int ReserveMb = 1024;

    /// <summary>How many of the last things that happened Admin → Models shows.</summary>
    private const int Kept = 8;

    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly ConcurrentQueue<Happened> _happened = new();
    private readonly MediaControl media;
    private readonly EngineState state;
    private readonly EngineClient engine;
    private readonly EngineRoute route;
    private readonly IServiceScopeFactory scopes;
    private readonly IOptions<EngineOptions> options;
    private readonly Modules modules;
    private readonly Replicas replicas;
    private readonly TimeProvider clock;
    private readonly ILogger<GpuShare> logger;

    public GpuShare(MediaControl media, EngineState state, EngineClient engine, EngineRoute route, IServiceScopeFactory scopes, IOptions<EngineOptions> options, Modules modules,
        Replicas replicas, TimeProvider clock, ILogger<GpuShare> logger)
    {
        (this.media, this.state, this.engine, this.route, this.scopes, this.options, this.modules, this.replicas, this.clock, this.logger) =
            (media, state, engine, route, scopes, options, modules, replicas, clock, logger);
        // Lent on another replica: no chat model loads here either (the replica that leads runs the watcher).
        replicas.On(LentTopic, said =>
        {
            var parts = said.Split('\n');
            if (parts.Length is 2 or 3)
            {
                var now = clock.GetUtcNow();
                state.Lend(new EngineState.Lending(parts[0], parts[1], now, now + LendLimit, parts.Length == 3 ? Hardware.ParseDevices(parts[2]) : null));
            }
            return true;
        });
        replicas.On(ReturnedTopic, _ =>
        {
            state.Return();
            return true;
        });
    }

    /// <summary>Something that happened, for Admin → Models: when, and what ("made room for … by unloading …").</summary>
    public sealed record Happened(DateTimeOffset At, string Text);

    /// <summary>The last things that happened on this replica, the newest last.</summary>
    public IReadOnlyList<Happened> Recent => [.. _happened];

    private void Say(string text)
    {
        _happened.Enqueue(new Happened(clock.GetUtcNow(), text));
        while (_happened.Count > Kept && _happened.TryDequeue(out _))
        {
        }
        LogHappened(logger, text);
    }

    /// <summary>The mode of the media studio's own models' work (the studio server), which borrows the GPU as pictures do.</summary>
    public const string StudioMode = "studio";

    /// <summary>What a picture or video model makes: "video" or "picture"; the studio server's work, "studio piece".</summary>
    private static string KindOf(MediaControl.Model model) => model.Mode switch { MediaModes.Video => "video", StudioMode => "studio piece", _ => "picture" };

    /// <summary>
    /// The GPU memory a picture or video model needs, in MB: to decode on the GPU when it may ("auto": its budget there and
    /// 1 GB more, as services/sd-serve.sh compares), else its weights' budget and the compute buffers; null: it says none.
    /// </summary>
    public static int? NeedMb(MediaModel row) =>
        SdArgs.Of(row).VaeGpuMb ?? (row.MaxVramGb is { } gb ? (int)(gb * 1024) + ReserveMb : null);

    /// <summary>
    /// How much the job lacks, by its server's status file (the GPU's free memory, every 2 s): null when it has what it
    /// needs or nothing says. A server running the model already holds its share: it lacks when it decodes on the CPU
    /// though it may on the GPU (it started while the chat models held the memory), or when a chat model loaded since has
    /// left it less than it computes with (its weights may wait in system memory, brought in a block at a time: a block
    /// with nowhere to go fails the picture).
    /// </summary>
    private int? Short(MediaControl.Model model)
    {
        if (NeedMb(model.Row) is not { } need || media.StatusOf(model.Server) is not { FreeMb: { } free } status)
        {
            return null;
        }
        if (status is { Phase: "running" } && status.Model == model.Name)
        {
            return (model.Row.Vae == "auto" && status.Vae == "cpu") || free < RunningMb ? need : null;
        }
        return free < need ? need : null;
    }

    /// <summary>The GPU memory a server running its model needs free to compute as it goes, in MB.</summary>
    public const int RunningMb = 2 * ReserveMb;

    /// <summary>
    /// The GPU for a job of <paramref name="model"/> (a picture or video model): its turn, then room when it is short. Dispose
    /// it when the job ends (the next job's turn, and the room given back). A speech model, or one whose server is not this
    /// stack's (the gateway answers for it from wherever it is), takes no turn.
    /// </summary>
    public async Task<Hold> HoldAsync(MediaControl.Model model, CancellationToken ct)
    {
        if (!MediaControl.FileServers.Contains(model.Server) || !await modules.HasAsync(model.Server, ct))
        {
            return new Hold(null, model);
        }
        await _one.WaitAsync(ct);
        var hold = new Hold(this, model);
        try
        {
            media.Hold(model.Name);
            if (options.Value.Enabled && Short(model) is { } need)
            {
                await MakeRoomAsync(hold, need, ct);
            }
            return hold;
        }
        catch
        {
            await hold.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The GPU for the studio server's work (<paramref name="name"/>: its model): its turn (pictures and videos take theirs
    /// from the same), then room when the studio server says it has less free than <paramref name="needMb"/>. Ended, the
    /// room goes back once <paramref name="release"/> (the studio server unloading its model) is done.
    /// </summary>
    public async Task<Hold> HoldStudioAsync(string name, int needMb, Func<CancellationToken, Task<long?>> freeMb, Func<Task> release, CancellationToken ct)
    {
        var model = new MediaControl.Model(name, StudioMode, StudioMode, null, new MediaModel { Name = name, Mode = StudioMode });
        await _one.WaitAsync(ct);
        var hold = new Hold(this, model) { Free = freeMb, Release = release };
        try
        {
            if (options.Value.Enabled && needMb > 0 && await freeMb(ct) is { } free && free < needMb)
            {
                await MakeRoomAsync(hold, needMb, ct);
            }
            return hold;
        }
        catch
        {
            await hold.DisposeAsync();
            throw;
        }
    }

    private async Task MakeRoomAsync(Hold hold, int need, CancellationToken ct)
    {
        var model = hold.Model;
        var kind = KindOf(model);
        var now = clock.GetUtcNow();
        var gpus = GpusOf(model);
        // Lent first: no chat model on its GPU loads from now on, so the room made stays made.
        state.Lend(new EngineState.Lending(model.Name, kind, now, now + LendLimit, gpus));
        replicas.Tell(LentTopic, $"{model.Name}\n{kind}" + (gpus is { Count: > 0 } ? "\n" + string.Join(',', gpus) : ""));
        hold.Lent = true;
        IReadOnlyList<string> kept;
        await using (var scope = scopes.CreateAsyncScope())
        {
            kept = scope.ServiceProvider.GetRequiredService<ModelCatalog>().Kept();
        }
        await route.MakeRoomAsync(model.Name, kind, kept, RoomWait, hold.Made, ct, gpus, hold.Asleep);
        if (hold.Unloaded.Count == 0 && hold.Asleep.Count == 0)
        {
            // Nothing could make room (every chat model is kept loaded, or in use all this minute): the job runs as it is.
            Say($"{model.Name} had less GPU memory than it needs ({need:N0} MB) for a {kind}, and no idle chat model could make room: it runs as it is.");
            Give();
            hold.Lent = false;
            return;
        }
        var unloaded = string.Join(", ", hold.Unloaded.Concat(hold.Asleep.Select(n => n + " (asleep)")));
        Say($"Made room for {model.Name} (a {kind}){(gpus is { Count: > 0 } ? $" on GPU {string.Join(", ", gpus)}" : "")}: "
            + (hold.Unloaded.Count > 0 ? "unloaded " : "put to sleep ") + $"{unloaded}.");
        // Their memory, as the server's script reads it every 2 s (or the studio server says it; the engine stops a model within 10 s).
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while ((hold.Free is { } said ? await said(ct) : media.StatusOf(model.Server)?.FreeMb) is { } free && free < need && waited.Elapsed < RoomWait)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        if (hold.Free is not null)
        {
            return;
        }
        var why = $"for a {kind}, with room made by unloading {unloaded}";
        if (media.StatusOf(model.Server) is { Phase: "running" } status && status.Model == model.Name && status.Vae == "cpu" && model.Row.Vae == "auto")
        {
            // It decodes where it decided as it started: started again, it decides anew, on the GPU now.
            media.Restart(model.Server, $"started again on the GPU {why}");
        }
        else
        {
            media.Explain(model.Name, why);
        }
    }

    /// <summary>The GPU is the chat models' again, on every replica.</summary>
    private void Give()
    {
        state.Return();
        replicas.Tell(ReturnedTopic);
    }

    /// <summary>The job ended: the room back to the chat models (when it was lent), and the next job's turn.</summary>
    private async Task EndAsync(Hold hold)
    {
        var model = hold.Model;
        try
        {
            if (hold.Lent)
            {
                // Borrowed: the model goes (unless kept loaded), and its server's memory is free before the chat models load
                // again (llama.cpp fits a model to the memory free as it loads).
                if (hold.Release is { } release)
                {
                    await release();
                }
                media.Unhold(model.Name);
                media.Done(model.Name, "unloaded: the room went back to the chat models");
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (media.Find(model.Name) is { Row.Kept: false } && media.StatusOf(model.Server) is { Phase: not ("off" or "exited" or "waiting") }
                    && waited.Elapsed < RoomWait)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
                // Returned first: the chat's requests waiting for it go on (and load what they ask for).
                Give();
                hold.Lent = false;
                await GiveBackAsync(hold);
            }
        }
        catch (Exception ex) when (ex is EngineException or HttpRequestException or InvalidOperationException)
        {
            LogGiveBackFailed(logger, model.Name, ex.Message);
        }
        finally
        {
            if (hold.Lent)
            {
                Give();
            }
            media.Unhold(model.Name);
            _one.Release();
        }
    }

    /// <summary>
    /// The GPUs a media model's job is lent: its server's (the model's GPU, else Engine:MediaGpu, else the machine's first);
    /// null when the machine's GPUs are not known (then every GPU is lent, as before).
    /// </summary>
    public IReadOnlyCollection<int>? GpusOf(MediaControl.Model model)
    {
        var devices = state.Hardware is { } hw ? GpuPlan.DevicesOf(hw) : [];
        var gpu = model.Row.Gpu ?? options.Value.MediaGpu ?? (devices.Count > 0 ? devices[0].Index : null);
        return gpu is { } g && devices.Any(d => d.Index == g) ? [g] : null;
    }

    /// <summary>
    /// The chat models put to sleep for the job, woken; those unloaded for it that are kept loaded or new chats use, loaded
    /// again (each through the app's room-making).
    /// </summary>
    private async Task GiveBackAsync(Hold hold)
    {
        var woken = new List<string>();
        foreach (var name in hold.Asleep)
        {
            try
            {
                await engine.SleepAsync(name, false, CancellationToken.None);
                woken.Add(name);
            }
            catch (EngineException ex)
            {
                LogGiveBackFailed(logger, name, ex.Message);
            }
        }
        if (woken.Count > 0)
        {
            Say($"Gave the room back after {hold.Model.Name}: woke {string.Join(", ", woken)}.");
        }
        if (hold.Unloaded.Count == 0)
        {
            return;
        }
        IReadOnlyList<string> kept;
        await using (var scope = scopes.CreateAsyncScope())
        {
            kept = scope.ServiceProvider.GetRequiredService<ModelCatalog>().Kept();
        }
        var back = new List<string>();
        foreach (var name in hold.Unloaded.Where(n => n == state.Default || kept.Contains(n)))
        {
            if (!await route.RoomForAsync(name, TimeSpan.Zero, CancellationToken.None))
            {
                continue;
            }
            state.Loading(name, $"given back after {hold.Model.Name}'s {KindOf(hold.Model)}");
            try
            {
                await engine.LoadAsync(name, CancellationToken.None);
            }
            catch (EngineException ex) when (ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
            {
                // Loaded or loading already: what it was asked for.
            }
            back.Add(name);
        }
        Say(back.Count > 0
            ? $"Gave the room back after {hold.Model.Name}: loaded {string.Join(", ", back)} again."
            : $"Gave the room back after {hold.Model.Name}: the chat models load again when asked for.");
    }

    /// <summary>A job's turn on the GPU, and the room made for it: dispose it when the job ends.</summary>
    public sealed class Hold(GpuShare? share, MediaControl.Model model) : IAsyncDisposable
    {
        private int _ended;

        public MediaControl.Model Model { get; } = model;

        /// <summary>Whether the GPU was lent to it (chat models unloaded for it).</summary>
        internal bool Lent { get; set; }

        /// <summary>The chat models unloaded for it, each as it was (a job stopped while room was made gives back those).</summary>
        internal List<string> Made { get; } = [];

        /// <summary>The chat models unloaded for it.</summary>
        public IReadOnlyList<string> Unloaded => Made;

        /// <summary>The vLLM models put to sleep for it (woken when it ends).</summary>
        internal List<string> Asleep { get; } = [];

        /// <summary>The chat models put to sleep for it.</summary>
        public IReadOnlyList<string> Slept => Asleep;

        /// <summary>A server that says its own free memory (the studio server), instead of a status file.</summary>
        internal Func<CancellationToken, Task<long?>>? Free { get; init; }

        /// <summary>What frees its server's memory when the room goes back (the studio server unloading its model).</summary>
        internal Func<Task>? Release { get; init; }

        public async ValueTask DisposeAsync()
        {
            if (share is not null && Interlocked.Exchange(ref _ended, 1) == 0)
            {
                await share.EndAsync(this);
            }
        }
    }

    public void Dispose() => _one.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "GPU: {Text}")]
    private static partial void LogHappened(ILogger logger, string text);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GPU: the room {Model} had could not all be given back: {Error}")]
    private static partial void LogGiveBackFailed(ILogger logger, string model, string error);
}
