using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Llm.Api.Operations;

namespace Llm.Api.Chat;

/// <summary>
/// What changed in a person's chats, as it happens: a message added (by them, an answer, Code Arena), a chat renamed, moved or
/// deleted. Their pages and their Code Arenas watch it (server-sent events) and read the chat again at once, instead of
/// asking every few seconds. Every replica hears it (Postgres NOTIFY through <see cref="Replicas"/>): a message saved by one
/// reaches the pages that watch another.
/// </summary>
public sealed class ChatWatch
{
    public const string Topic = "chat-changed";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, List<Channel<string>>> _people = new();
    private Replicas? _replicas;

    /// <summary>
    /// The other replicas heard from here on, and told (once the app has started: the database's context, which this serves,
    /// is made before the replicas are, and making them here would wait on itself).
    /// </summary>
    public void Attach(Replicas replicas)
    {
        _replicas = replicas;
        replicas.On(Topic, payload =>
        {
            var at = payload.IndexOf(' ', StringComparison.Ordinal);
            if (at > 0 && Guid.TryParse(payload[..at], out var person))
            {
                Local(person, payload[(at + 1)..]);
            }
            return true;
        });
    }

    /// <summary>The person's chat changed (what: "messages", "title", "project", "deleted"): their watchers are told, on every replica.</summary>
    public void Changed(Guid person, Guid chat, string what = "messages")
    {
        var line = JsonSerializer.Serialize(new { type = "chat", id = chat, what }, Json);
        Local(person, line);
        _replicas?.Tell(Topic, $"{person} {line}");
    }

    private void Local(Guid person, string line)
    {
        if (!_people.TryGetValue(person, out var watchers))
        {
            return;
        }
        lock (watchers)
        {
            foreach (var w in watchers)
            {
                w.Writer.TryWrite(line);
            }
        }
    }

    /// <summary>How many watch now (pages and Code Arenas), for the tests.</summary>
    public int Watching(Guid person) => _people.TryGetValue(person, out var w) ? w.Count : 0;

    /// <summary>The person's chats' changes as lines of JSON, and an empty line every <paramref name="heartbeat"/> (a proxy keeps the stream).</summary>
    public async IAsyncEnumerable<string> WatchAsync(Guid person, TimeSpan heartbeat, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var watchers = _people.GetOrAdd(person, _ => []);
        lock (watchers)
        {
            watchers.Add(channel);
        }
        try
        {
            // Said at once: the stream is open (a client then knows it hears every change from here on).
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
            lock (watchers)
            {
                watchers.Remove(channel);
            }
        }
    }

    /// <summary>The watch as server-sent events, for as long as the client keeps it open.</summary>
    public async Task StreamAsync(HttpContext http, Guid person)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        try
        {
            await foreach (var line in WatchAsync(person, TimeSpan.FromSeconds(15), http.RequestAborted))
            {
                await http.Response.WriteAsync(line.Length == 0 ? ": still here\n\n" : $"data: {line}\n\n", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // The client went.
        }
        catch (IOException) when (http.RequestAborted.IsCancellationRequested)
        {
            // The connection dropped.
        }
    }
}

/// <summary>Attaches the chats' watch to the replicas as the app starts.</summary>
public sealed class ChatWatchStart(ChatWatch watch, Replicas replicas) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        watch.Attach(replicas);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
