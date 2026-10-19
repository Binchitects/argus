using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace Llm.Api.Chat;

/// <summary>
/// A room's live events, for the pages its members have open (GET /api/rooms/{id}/events): a message written, an
/// answer started and ended (and whose), who is typing, who is here, the members changed. In memory, on this
/// replica; the answer's own words still come from the chat's stream, which the page opens on "answering".
/// </summary>
public sealed class RoomHub
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Watcher(Guid Person, Channel<string> Channel);

    private readonly ConcurrentDictionary<Guid, List<Watcher>> _rooms = new();
    private readonly ConcurrentDictionary<(Guid Room, Guid Person), DateTimeOffset> _typing = new();
    private readonly TimeProvider _clock;

    public RoomHub(TimeProvider clock) => _clock = clock;

    /// <summary>How long "typing" holds after the page last said so.</summary>
    public static readonly TimeSpan TypingFor = TimeSpan.FromSeconds(6);

    public void Publish(Guid room, object e)
    {
        if (!_rooms.TryGetValue(room, out var watchers))
        {
            return;
        }
        var line = JsonSerializer.Serialize(e, Json);
        lock (watchers)
        {
            foreach (var w in watchers)
            {
                w.Channel.Writer.TryWrite(line);
            }
        }
    }

    /// <summary>A message saved (a question, or one for the people only).</summary>
    public void Said(Guid room, Guid message) => Publish(room, new { type = "message", id = message });

    public void Answering(Guid room, Guid person) => Publish(room, new { type = "answering", by = person });

    public void Answered(Guid room) => Publish(room, new { type = "answered" });

    public void Changed(Guid room, string what) => Publish(room, new { type = what });

    public void Typing(Guid room, Guid person)
    {
        _typing[(room, person)] = _clock.GetUtcNow() + TypingFor;
        Publish(room, new { type = "typing", by = person });
    }

    /// <summary>Who has the room open now.</summary>
    public IReadOnlyList<Guid> Here(Guid room)
    {
        if (!_rooms.TryGetValue(room, out var watchers))
        {
            return [];
        }
        lock (watchers)
        {
            return [.. watchers.Select(w => w.Person).Distinct()];
        }
    }

    /// <summary>Who is typing now.</summary>
    public IReadOnlyList<Guid> TypingNow(Guid room)
    {
        var now = _clock.GetUtcNow();
        return [.. _typing.Where(t => t.Key.Room == room && t.Value > now).Select(t => t.Key.Person)];
    }

    /// <summary>A person removed (or who left): their open pages of the room close at once.</summary>
    public void Close(Guid room, Guid person)
    {
        if (!_rooms.TryGetValue(room, out var watchers))
        {
            return;
        }
        lock (watchers)
        {
            foreach (var w in watchers.Where(w => w.Person == person).ToList())
            {
                w.Channel.Writer.TryComplete();
                watchers.Remove(w);
            }
        }
        Publish(room, new { type = "presence", here = Here(room) });
    }

    /// <summary>Each event as it comes, an empty line now and then while nothing does, until <paramref name="ct"/> fires or the person is removed.</summary>
    public async IAsyncEnumerable<string> WatchAsync(Guid room, Guid person, TimeSpan heartbeat, [EnumeratorCancellation] CancellationToken ct)
    {
        var watcher = new Watcher(person, Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true }));
        var watchers = _rooms.GetOrAdd(room, _ => []);
        lock (watchers)
        {
            watchers.Add(watcher);
        }
        Publish(room, new { type = "presence", here = Here(room) });
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line;
                using (var beat = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    beat.CancelAfter(heartbeat);
                    try
                    {
                        if (!await watcher.Channel.Reader.WaitToReadAsync(beat.Token))
                        {
                            yield break;
                        }
                        watcher.Channel.Reader.TryRead(out line);
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
                watchers.Remove(watcher);
            }
            Publish(room, new { type = "presence", here = Here(room) });
        }
    }
}
