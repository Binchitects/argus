using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Llm.Api.Access;
using Llm.Api.Operations;
using Llm.Core.Chat;
using Llm.Core.Gateway;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Llm.Api.Reports;

/// <summary>
/// Use as it is booked: a request in the ledger, a chat's message with its time. The pages that add use up (Accounting,
/// a project's metrics, Usage) watch it (server-sent events, /api/usage/live) and read their numbers again at once, instead
/// of at their next look. A beat says only that use changed, and in which projects (their ids), never whose or how much:
/// each page reads its numbers by its own permission. Beats are a second apart at most, on every replica (Postgres NOTIFY
/// through <see cref="Replicas"/>).
/// </summary>
public sealed class UsageWatch(TimeProvider clock)
{
    public const string Topic = "usage-changed";

    /// <summary>The most beats a second: bookings within it go as one.</summary>
    public static readonly TimeSpan Beat = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<Channel<string>> _watchers = [];
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _projects = [];
    private bool _due;
    private Replicas? _replicas;

    public void Attach(Replicas replicas)
    {
        _replicas = replicas;
        replicas.On(Topic, line =>
        {
            Local(line);
            return true;
        });
    }

    /// <summary>Use was booked (in these projects, if any): the watchers hear of it within <see cref="Beat"/>.</summary>
    public void Booked(IEnumerable<Guid?> projects)
    {
        lock (_gate)
        {
            foreach (var p in projects)
            {
                if (p is { } id)
                {
                    _projects.Add(id);
                }
            }
            if (_due)
            {
                return;
            }
            _due = true;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(Beat, clock);
            Guid[] changed;
            lock (_gate)
            {
                changed = [.. _projects];
                _projects.Clear();
                _due = false;
            }
            var line = JsonSerializer.Serialize(new { type = "usage", projects = changed }, Json);
            Local(line);
            _replicas?.Tell(Topic, line);
        });
    }

    private void Local(string line)
    {
        lock (_watchers)
        {
            foreach (var w in _watchers)
            {
                w.Writer.TryWrite(line);
            }
        }
    }

    /// <summary>How many pages watch now, for the tests.</summary>
    public int Watching
    {
        get
        {
            lock (_watchers)
            {
                return _watchers.Count;
            }
        }
    }

    /// <summary>The beats as lines of JSON, and an empty line every <paramref name="heartbeat"/> (a proxy keeps the stream).</summary>
    public async IAsyncEnumerable<string> WatchAsync(TimeSpan heartbeat, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(16) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        lock (_watchers)
        {
            _watchers.Add(channel);
        }
        try
        {
            yield return JsonSerializer.Serialize(new { type = "watching" }, Json);
            while (!ct.IsCancellationRequested)
            {
                string? line;
                using (var beat = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    beat.CancelAfter(heartbeat);
                    try
                    {
                        if (!await channel.Reader.WaitToReadAsync(beat.Token))
                        {
                            yield break;
                        }
                        channel.Reader.TryRead(out line);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        line = "";
                    }
                    catch (OperationCanceledException)
                    {
                        yield break;
                    }
                }
                if (line is not null)
                {
                    yield return line;
                }
            }
        }
        finally
        {
            lock (_watchers)
            {
                _watchers.Remove(channel);
            }
        }
    }

    public static void MapUsageWatch(IEndpointRouteBuilder app) =>
        app.MapGet("/api/usage/live", async (HttpContext http, UsageWatch watch) =>
        {
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            try
            {
                await foreach (var line in watch.WatchAsync(TimeSpan.FromSeconds(15), http.RequestAborted))
                {
                    await http.Response.WriteAsync(line.Length == 0 ? ": still here\n\n" : $"data: {line}\n\n", http.RequestAborted);
                    await http.Response.Body.FlushAsync(http.RequestAborted);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException && http.RequestAborted.IsCancellationRequested)
            {
                // The page went.
            }
        }).PersonOnly();
}

/// <summary>Whatever books use (the gateway's ledger, an answer's round or tool call, Code Arena's sync), the usage watchers hear of it once it is saved.</summary>
public sealed class UsageOnSave(UsageWatch watch) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, List<Guid?>> _pending = [];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Tell(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Tell(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } c)
        {
            _pending.Remove(c);
        }
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        SaveChangesFailed(eventData);
        return Task.CompletedTask;
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }
        var projects = context.ChangeTracker.Entries<GatewayRequest>().Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Select(e => e.Entity.ProjectId).ToList();
        // A message's time (an answer's round, a tool call, a question): the chats' time accounting changed.
        var timed = context.ChangeTracker.Entries<ChatMessage>().Any(e => e.State == EntityState.Added
            || e.State == EntityState.Modified && e.Property(nameof(ChatMessage.DurationMs)).IsModified);
        if (projects.Count > 0 || timed)
        {
            if (projects.Count == 0)
            {
                projects.Add(null);
            }
            _pending.AddOrUpdate(context, projects);
        }
    }

    private void Tell(DbContext? context)
    {
        if (context is not null && _pending.TryGetValue(context, out var projects))
        {
            _pending.Remove(context);
            watch.Booked(projects);
        }
    }
}

/// <summary>Attaches the usage watch to the replicas as the app starts.</summary>
public sealed class UsageWatchStart(UsageWatch watch, Replicas replicas) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        watch.Attach(replicas);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
