using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Llm.Api.Operations;

/// <summary>Configuration section "Replicas": several app replicas on one database.</summary>
public sealed class ReplicaOptions
{
    /// <summary>Off: this replica always leads and hears no other (one replica behind a pooler that keeps no sessions).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often a replica tries for the lead (or checks it still has it) and counts the others.</summary>
    public TimeSpan Renew { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a question to the other replicas waits for the one it is about.</summary>
    public TimeSpan AskTimeout { get; set; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// The app's replicas, when several run on one database (behind Traefik, or a Helm release's
/// pods). One leads, and it alone does the background work that must happen once: the scheduled
/// tasks' clock, the engine's models, the downloads, the directory check. The lead is a Postgres
/// advisory lock held by a connection of its own: a replica that stops or dies lets go of it, and
/// another takes it within <see cref="ReplicaOptions.Renew"/>. The same connection hears the other
/// replicas (Postgres LISTEN/NOTIFY): a yes to a tool call, a stop or "answer now" posted to one
/// reaches the one writing the answer, and a saved setting applies on all.
/// </summary>
public sealed partial class Replicas(IConfiguration config, IOptions<ReplicaOptions> options, IServiceProvider services, ILogger<Replicas> logger) : BackgroundService
{
    private const string Channel = "arena_replicas";
    private const string AppName = "arena-replica:";
    /// <summary>The lead's lock: "ArenaLea", one per database.</summary>
    private const long LeaderKey = 0x4172656E614C6561;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, Func<string, bool>> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _asks = new(StringComparer.Ordinal);
    private NpgsqlConnection? _connection;
    private string? _connectionString;
    private volatile bool _leader;
    private volatile bool _listening;

    /// <summary>A message between replicas: who sent it, about what, and for a question its id (an answer names who asked).</summary>
    private sealed record Message(string From, string Topic, string Payload, string? Ask = null, string? To = null);

    /// <summary>This replica, as the others and Postgres (application_name) know it.</summary>
    public string Id { get; } = $"{(Environment.MachineName.Length > 40 ? Environment.MachineName[..40] : Environment.MachineName)}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";

    /// <summary>Whether this replica leads now: only then does it run the once-only background work.</summary>
    public bool IsLeader => !options.Value.Enabled || _leader;

    /// <summary>Replicas connected to the database now, this one included (as of the last check).</summary>
    public int Count { get; private set; } = 1;

    /// <summary>What a replica does when another tells it <paramref name="topic"/>: true when it had what the message is about.</summary>
    public void On(string topic, Func<string, bool> handler) => _handlers[topic] = handler;

    /// <summary>Tells the other replicas (not this one); never throws: a replica that misses it catches up on its own.</summary>
    public void Tell(string topic, string payload = "")
    {
        // Even when it counts no other: one that just started may not be counted yet.
        if (!options.Value.Enabled)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await NotifyAsync(new Message(Id, topic, payload), CancellationToken.None);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                LogTellFailed(logger, topic, ex.Message);
            }
        });
    }

    /// <summary>
    /// Asks the other replicas to do something only one of them can (the one writing an answer):
    /// true when one did, false when none had it within <see cref="ReplicaOptions.AskTimeout"/>.
    /// </summary>
    public async Task<bool> AskAsync(string topic, string payload, CancellationToken ct)
    {
        if (!options.Value.Enabled || Count <= 1 || !_listening)
        {
            return false;
        }
        var id = Guid.NewGuid().ToString("N");
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _asks[id] = answered;
        try
        {
            await NotifyAsync(new Message(Id, topic, payload, id), ct);
            await answered.Task.WaitAsync(options.Value.AskTimeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (NpgsqlException ex)
        {
            LogTellFailed(logger, topic, ex.Message);
            return false;
        }
        finally
        {
            _asks.TryRemove(id, out _);
        }
    }

    private async Task NotifyAsync(Message message, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(ConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", conn);
        cmd.Parameters.AddWithValue("channel", Channel);
        cmd.Parameters.AddWithValue("payload", JsonSerializer.Serialize(message, Json));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private string ConnectionString() => _connectionString ??= DatabaseSettings.ConnectionString(config);

    /// <summary>The first try for the lead before the other background services start: a lone replica leads from its first moment.</summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Settings saved on another replica: read again here (off the listening connection's thread).
        if (services.GetService<Settings.DatabaseConfigurationProvider>() is { } saved)
        {
            On(Settings.SettingsService.ReloadTopic, payload =>
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        saved.Reload();
                    }
                    catch (NpgsqlException ex)
                    {
                        LogTellFailed(logger, Settings.SettingsService.ReloadTopic, ex.Message);
                    }
                });
                return true;
            });
        }
        if (options.Value.Enabled)
        {
            try
            {
                _connection = await ConnectAsync(cancellationToken);
                await CheckAsync(_connection, cancellationToken);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                LogLost(logger, ex.Message);
                await DropAsync();
            }
        }
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _connection ??= await ConnectAsync(stoppingToken);
                // Notifications are read while it waits; then the lead is tried for again, or found still held.
                await _connection.WaitAsync(options.Value.Renew, stoppingToken);
                await CheckAsync(_connection, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException or TimeoutException)
            {
                // The connection is gone, and the lead with it (Postgres let go of the lock): connect again.
                LogLost(logger, ex.Message);
                await DropAsync();
                try
                {
                    await Task.Delay(options.Value.Renew, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        await DropAsync();
    }

    private async Task<NpgsqlConnection> ConnectAsync(CancellationToken ct)
    {
        // A connection of its own, never pooled: the lock lives as long as it does.
        var cs = new NpgsqlConnectionStringBuilder(ConnectionString()) { Pooling = false, ApplicationName = AppName + Id, TcpKeepAlive = true }.ConnectionString;
        var conn = new NpgsqlConnection(cs);
        try
        {
            await conn.OpenAsync(ct);
            conn.Notification += (_, e) => Heard(e.Payload);
            await using var listen = new NpgsqlCommand($"LISTEN {Channel}", conn);
            await listen.ExecuteNonQueryAsync(ct);
            _listening = true;
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>Tries for the lead unless it has it (its connection alive is the lock held), and counts the replicas.</summary>
    private async Task CheckAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT CASE WHEN @leading THEN true ELSE pg_try_advisory_lock(@key) END, " +
            "(SELECT count(*)::int FROM pg_stat_activity WHERE datname = current_database() AND application_name LIKE @app)", conn);
        cmd.Parameters.AddWithValue("leading", _leader);
        cmd.Parameters.AddWithValue("key", LeaderKey);
        cmd.Parameters.AddWithValue("app", AppName + "%");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var leading = reader.GetBoolean(0);
        Count = Math.Max(1, reader.GetInt32(1));
        if (leading && !_leader)
        {
            LogLeading(logger, Id, Count);
        }
        _leader = leading;
    }

    /// <summary>Closes the connection: Postgres lets go of the lock with it.</summary>
    private async Task DropAsync()
    {
        var was = _leader;
        (_leader, _listening) = (false, false);
        if (_connection is { } conn)
        {
            _connection = null;
            try
            {
                await conn.DisposeAsync();
            }
            catch (Exception ex) when (ex is NpgsqlException or IOException or InvalidOperationException)
            {
                // Already broken: closing it is all that was left.
            }
        }
        if (was)
        {
            LogStepDown(logger, Id);
        }
    }

    /// <summary>A message from another replica: done here when it is for this one, and a question answered when it was.</summary>
    private void Heard(string payload)
    {
        Message? message;
        try
        {
            message = JsonSerializer.Deserialize<Message>(payload, Json);
        }
        catch (JsonException)
        {
            return;
        }
        if (message is null || message.From == Id)
        {
            return;
        }
        if (message.Topic == "answered")
        {
            if (message.To == Id && _asks.TryGetValue(message.Payload, out var answered))
            {
                answered.TrySetResult();
            }
            return;
        }
        if (!_handlers.TryGetValue(message.Topic, out var handler))
        {
            return;
        }
        bool done;
        try
        {
            done = handler(message.Payload);
        }
        catch (Exception ex)
        {
            LogHandlerFailed(logger, message.Topic, ex);
            return;
        }
        if (done && message.Ask is { } ask)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await NotifyAsync(new Message(Id, "answered", ask, To: message.From), CancellationToken.None);
                }
                catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
                {
                    LogTellFailed(logger, "answered", ex.Message);
                }
            });
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Replica {Replica} leads now ({Count} replicas on the database): the once-only background work runs here")]
    private static partial void LogLeading(ILogger logger, string replica, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Replica {Replica} no longer leads")]
    private static partial void LogStepDown(ILogger logger, string replica);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Replicas: the connection for the lead and the signals was lost ({Reason}); connecting again")]
    private static partial void LogLost(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Replicas: \"{Topic}\" could not be told to the others: {Reason}")]
    private static partial void LogTellFailed(ILogger logger, string topic, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Replicas: handling \"{Topic}\" from another replica failed")]
    private static partial void LogHandlerFailed(ILogger logger, string topic, Exception ex);
}
