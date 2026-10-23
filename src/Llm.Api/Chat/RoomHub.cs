using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace Llm.Api.Chat;

/// <summary>
/// A room's live events, for the pages its members have open (GET /api/rooms/{id}/events): a message written, an
/// answer started and ended (and whose), who is typing, who is here, the members changed; and its call: who is in it,
/// what each says (live captions) and the calls' own signals between two pages. In memory, on this replica; the
/// answer's own words still come from the chat's stream, which the page opens on "answering".
/// </summary>
public sealed class RoomHub
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="Page">The page's own id (it chooses one), by which the call reaches it: null for a page not in calls.</param>
    private sealed record Watcher(Guid Person, Guid? Page, Channel<string> Channel);

    /// <summary>A page in a room's call: whose, and the language it reads captions in.</summary>
    public sealed record Seat(Guid Page, Guid Person, string? Language, bool Muted);

    private readonly ConcurrentDictionary<Guid, List<Watcher>> _rooms = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Seat>> _calls = new();
    private readonly ConcurrentDictionary<(Guid Room, Guid Person), DateTimeOffset> _typing = new();
    private readonly TimeProvider _clock;

    public RoomHub(TimeProvider clock) => _clock = clock;

    /// <summary>How long a page's seat in the call waits for its events to be opened again (a proxy closed them) before it goes.</summary>
    public static readonly TimeSpan SeatHeldFor = TimeSpan.FromSeconds(15);

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

    /// <summary>An event for one page only (a call's signal): nothing when the page is not open.</summary>
    public void PublishTo(Guid room, Guid page, object e)
    {
        if (!_rooms.TryGetValue(room, out var watchers))
        {
            return;
        }
        var line = JsonSerializer.Serialize(e, Json);
        lock (watchers)
        {
            foreach (var w in watchers.Where(w => w.Page == page))
            {
                w.Channel.Writer.TryWrite(line);
            }
        }
    }

    /// <summary>Whether this page of this person has the room open now (a call's seat is only for a page that watches).</summary>
    public bool Watching(Guid room, Guid person, Guid page)
    {
        if (!_rooms.TryGetValue(room, out var watchers))
        {
            return false;
        }
        lock (watchers)
        {
            return watchers.Any(w => w.Person == person && w.Page == page);
        }
    }

    /// <summary>Who is in the room's call: each page, whose, and the language it reads in.</summary>
    public IReadOnlyList<Seat> InCall(Guid room) => _calls.TryGetValue(room, out var seats) ? [.. seats.Values.OrderBy(s => s.Person).ThenBy(s => s.Page)] : [];

    /// <summary>A page takes its seat in the call (or changes its language or mute); false when the call is full.</summary>
    public bool Sit(Guid room, Seat seat, int most)
    {
        var seats = _calls.GetOrAdd(room, _ => new());
        lock (seats)
        {
            if (!seats.ContainsKey(seat.Page) && seats.Count >= most)
            {
                return false;
            }
            seats[seat.Page] = seat;
        }
        PublishCall(room);
        return true;
    }

    /// <summary>A page leaves the call (it left, or closed).</summary>
    public void Stand(Guid room, Guid page)
    {
        if (_calls.TryGetValue(room, out var seats) && seats.TryRemove(page, out _))
        {
            PublishCall(room);
        }
    }

    private void Stand(Guid room, Func<Seat, bool> which)
    {
        if (!_calls.TryGetValue(room, out var seats))
        {
            return;
        }
        var gone = seats.Values.Where(which).ToList();
        foreach (var s in gone)
        {
            seats.TryRemove(s.Page, out _);
        }
        if (gone.Count > 0)
        {
            PublishCall(room);
        }
    }

    private void PublishCall(Guid room) =>
        Publish(room, new { type = "call", seats = InCall(room).Select(s => new { page = s.Page, person = s.Person, muted = s.Muted }) });

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
        Stand(room, s => s.Person == person);
        Publish(room, new { type = "presence", here = Here(room) });
    }

    /// <summary>Each event as it comes, an empty line now and then while nothing does, until <paramref name="ct"/> fires or the person is removed.</summary>
    public async IAsyncEnumerable<string> WatchAsync(Guid room, Guid person, TimeSpan heartbeat, [EnumeratorCancellation] CancellationToken ct, Guid? page = null)
    {
        var watcher = new Watcher(person, page, Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true }));
        var watchers = _rooms.GetOrAdd(room, _ => []);
        lock (watchers)
        {
            watchers.Add(watcher);
        }
        Publish(room, new { type = "presence", here = Here(room) });
        if (InCall(room) is { Count: > 0 } seats)
        {
            // Who is in the call, for a page just opened.
            watcher.Channel.Writer.TryWrite(JsonSerializer.Serialize(new { type = "call", seats = seats.Select(s => new { page = s.Page, person = s.Person, muted = s.Muted }) }, Json));
        }
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
            // The page closed, not only its stream (opened again in a few seconds): its seat in the call goes too.
            if (page is { } left && _calls.TryGetValue(room, out var held) && held.ContainsKey(left))
            {
                _ = Task.Delay(SeatHeldFor, _clock).ContinueWith(_ =>
                {
                    if (!Watching(room, person, left))
                    {
                        Stand(room, left);
                    }
                }, TaskScheduler.Default);
            }
            Publish(room, new { type = "presence", here = Here(room) });
        }
    }
}
