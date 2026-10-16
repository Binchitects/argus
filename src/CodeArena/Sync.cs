using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>A chat of the person's in Arena, as Code Arena lists them to continue one.</summary>
internal sealed record WebChat(string Id, string Title, string? Origin, string? Place, DateTimeOffset Updated, int Messages);

/// <summary>
/// What came in from the web chat: messages added after the session's (<see cref="Added"/>), or the model's whole history
/// built again (<see cref="History"/>: the web's conversation changed, or its messages go before some of the session's), and
/// what to say of it.
/// </summary>
internal sealed record Taken(IReadOnlyList<JsonObject> Added, List<JsonObject>? History = null, string? Said = null)
{
    public static readonly Taken None = new([]);

    /// <summary>Anything changed in what the model reads.</summary>
    public bool Any => Added.Count > 0 || History is not null;
}

/// <summary>When the Arena account's chats compact, in percent of the window (0: only when a request would not fit); <paramref name="Own"/>: the person chose it, else it is Arena's default.</summary>
internal sealed record AccountCompaction(int At, bool Own);

/// <summary>
/// The session kept in step with a chat in Arena (/api/code-arena), both ways: each message the session adds is sent
/// in the background, in order, again until Arena has it (a lost connection, Arena restarting); what the person added
/// in the web chat comes in at the next turn's start, and while the session waits (the terminal's prompt, the IDE)
/// within seconds: the web chat is asked every <see cref="WatchEvery"/> (<see cref="OnNews"/>). Every message goes with
/// Arena's id for it: this end reads what follows the last message both have, and adds after it. When the web edited a
/// question, answered again or shows another branch, the session follows the web's conversation from where they part;
/// when the session went back to before a turn (/rewind), what it adds goes on a branch of its own there. While a turn
/// runs here the chat is this end's (its turn, renewed as it goes): the web queues the person's messages meanwhile.
/// </summary>
internal sealed class ChatSync : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _key;
    private readonly SessionStore _store;
    private readonly string _place;
    private readonly Func<string> _model;
    private readonly Func<bool> _vision;
    private readonly Action<bool, string> _notice;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly Task _watch;
    /// <summary>The web chat's branch as last read in step with this end: asked with it, Arena says only "unchanged".</summary>
    private string? _stamp;
    /// <summary>Arena has messages this end has not taken in: sending waits for the next turn's start.</summary>
    private bool _behind;
    /// <summary>"The web chat is answering" was said: not again until a read gets through.</summary>
    private bool _answeringSaid;
    private string? _problem;
    /// <summary>A turn runs here: the chat is this end's (<see cref="_lease"/>) while it does.</summary>
    private bool _turn;
    /// <summary>Arena holds the chat for this end's turn, renewed at <see cref="_leaseAt"/>.</summary>
    private bool _lease;
    private DateTime _leaseAt;

    /// <summary>The most messages one request sends.</summary>
    public const int Batch = 200;

    /// <summary>The longest one request to Arena may take: a slow or half-open connection never holds a turn.</summary>
    public static readonly TimeSpan RequestLimit = TimeSpan.FromSeconds(20);

    /// <summary>How long a turn's start waits for a send under way before it goes on without the web's news (it comes next turn).</summary>
    public static readonly TimeSpan TakeInWait = TimeSpan.FromSeconds(3);

    /// <summary>How long a turn's start waits for Arena to start saying what the web chat added (all of it then has <see cref="RequestLimit"/>).</summary>
    public static readonly TimeSpan TakeInLimit = TimeSpan.FromSeconds(5);

    /// <summary>How often the web chat is asked, while someone listens (<see cref="OnNews"/>), whether it has messages this end lacks.</summary>
    public static TimeSpan WatchEvery { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a turn renews its hold on the chat (Arena lets it go after 90 seconds without).</summary>
    public static TimeSpan TurnRenew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a turn's start waits for the web chat's answer to end before it goes on without it.</summary>
    public static TimeSpan WebAnswerWait { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Told when the web chat has messages this session has not taken in, asked every <see cref="WatchEvery"/>: the
    /// terminal at its prompt, or the IDE with nothing running, takes them in then. Null: nobody listens (a one-shot run),
    /// and the web chat is not asked; its news comes at the next turn's start.
    /// </summary>
    public Action? OnNews { get; set; }

    private ChatSync(HttpClient http, string arenaUrl, string key, SessionStore store, string place, Func<string> model, Action<bool, string> notice, Func<bool>? vision)
    {
        _http = http;
        _base = arenaUrl.TrimEnd('/');
        _key = key;
        _store = store;
        _place = place;
        _model = model;
        _notice = notice;
        _vision = vision ?? (() => false);
        store.Added += Added;
        _loop = Task.Run(LoopAsync);
        _watch = Task.Run(WatchAsync);
        if (store.Read(d => d.Waiting > 0 || d.Summary is not null))
        {
            Wake();
        }
    }

    /// <summary>
    /// Keeps this session in step with its chat in Arena (made at its first message); null when there is no Arena to keep it
    /// with. <paramref name="vision"/>: whether the model sees pictures (those attached on the web then come as pictures).
    /// </summary>
    public static ChatSync? Start(Config config, HttpClient http, SessionStore store, string place, Func<string> model, Action<bool, string> notice,
        Func<bool>? vision = null) =>
        config.SyncChats && config.Url is { Length: > 0 } url && config.ApiKey is { Length: > 0 } key
            ? new ChatSync(http, url, key, store, place, model, notice, vision)
            : null;

    /// <summary>The Arena chat this session is kept with, once made.</summary>
    public string? Conversation => _store.Read(d => d.Unlinked ? null : d.Conversation);

    /// <summary>Where it stands, in words (/sync).</summary>
    public string Describe()
    {
        var (conversation, unlinked, waiting) = _store.Read(d => (d.Conversation, d.Unlinked, d.Waiting));
        lock (_gate)
        {
            if (unlinked)
            {
                return "Not kept in step: its chat in Arena was deleted.";
            }
            var where = conversation is null ? "Its chat in Arena is made with the first message." : $"Kept in step with the chat {_base}/chat/{conversation}.";
            return where + (waiting > 0 ? $" {waiting} message{(waiting == 1 ? "" : "s")} still to send{(_problem is null ? "" : $" ({_problem})")}." : " Arena has every message.")
                + (_behind ? " The web chat has new messages: they come in at the next turn." : "");
        }
    }

    /// <summary>Links a new session to a chat already in Arena (continued here): its messages come in at the next turn.</summary>
    public void Link(string conversation)
    {
        lock (_gate)
        {
            _store.Link(conversation);
            _stamp = null;
            _behind = true;
        }
    }

    private void Added(JsonObject message) => Wake();

    private void Wake()
    {
        lock (_wake)
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
    }

    private async Task LoopAsync()
    {
        var wait = TimeSpan.Zero;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                // While a turn holds the chat, it is renewed every so often, whatever else there is to do.
                var renew = Holding() ? TurnRenew : TimeSpan.Zero;
                var next = wait > TimeSpan.Zero && renew > TimeSpan.Zero ? (wait < renew ? wait : renew) : wait > TimeSpan.Zero ? wait : renew;
                if (next > TimeSpan.Zero)
                {
                    await Task.WhenAny(Task.Delay(next, _stop.Token), _wake.WaitAsync(_stop.Token));
                }
                else
                {
                    await _wake.WaitAsync(_stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                wait = await PushAsync(_stop.Token);
                await TurnHoldAsync(_stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // Arena out of reach, or restarting (or the session's file not writable): again in a while, longer each time (at most a minute).
                wait = wait == TimeSpan.Zero ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(Math.Min(60, wait.TotalSeconds * 2));
                Problem($"Arena cannot be reached ({Fmt.OneLine(e.Message, 120)}): the session is sent when it can be");
            }
        }
    }

    /// <summary>A turn runs, or the chat is still held for one that ended (its last messages on their way).</summary>
    private bool Holding()
    {
        lock (_gate)
        {
            return _turn || _lease;
        }
    }

    /// <summary>Sends what Arena lacks; how long to wait before trying again (zero: nothing left, or nothing to try).</summary>
    private async Task<TimeSpan> PushAsync(CancellationToken ct)
    {
        await _store.Busy.WaitAsync(ct);
        try
        {
            while (true)
            {
                var plan = _store.Read(d =>
                {
                    var first = d.FirstUnsent;
                    var summary = d.Summary is { } s && s.UpTo < first && d.Stream[s.UpTo].Id is { Length: > 0 } upTo ? new JsonObject { ["upTo"] = upTo, ["text"] = s.Text } : null;
                    var batch = d.Stream.Skip(first).Take(Batch).ToList();
                    // Each tool result with its tool's name (the call it answers, maybe sent before).
                    var names = batch.Select((e, i) => e.Message.Str("role") == "tool" ? SyncEntry.ToolName(d.Stream, first + i) : null).ToList();
                    return (d.Conversation, d.Unlinked, Parent: d.LastSent, Unknown: d.Stream.Take(first).Any(e => e.Id == ""), d.Branch, First: first,
                        Batch: batch, Names: names, Summary: summary);
                });
                if (plan.Unlinked || (plan.Batch.Count == 0 && plan.Summary is null))
                {
                    return TimeSpan.Zero;
                }
                lock (_gate)
                {
                    // What the web added is taken in first; only a call's results still go at once (they belong straight after it).
                    if (_behind && !Insertable(plan.Batch))
                    {
                        return TimeSpan.Zero;
                    }
                }
                if (plan.Unknown)
                {
                    // A session from before Arena's ids: the chat is read again from its start (at the next turn) before anything is added.
                    lock (_gate)
                    {
                        _behind = true;
                    }
                    return TimeSpan.Zero;
                }
                if (plan.Conversation is null)
                {
                    var made = await SendAsync(HttpMethod.Post, "/api/code-arena/chats", new JsonObject
                    {
                        ["ref"] = _store.Id, ["place"] = _place, ["model"] = _model(), ["title"] = null,
                    }, ct);
                    if (made.Status != HttpStatusCode.OK || made.Body?.Str("id") is not { } id)
                    {
                        return Refused(made);
                    }
                    _store.Link(id);
                    continue;
                }
                var body = new JsonObject
                {
                    ["parent"] = plan.Parent,
                    ["branch"] = plan.Branch,
                    ["messages"] = new JsonArray([.. plan.Batch.Select((e, i) => (JsonNode)Out(e, _store.Id, plan.Names[i]))]),
                };
                if (plan.Summary is not null)
                {
                    body["summary"] = plan.Summary;
                }
                var sent = await SendAsync(HttpMethod.Post, $"/api/code-arena/chats/{plan.Conversation}/messages", body, ct);
                if (sent.Status == HttpStatusCode.OK)
                {
                    _store.Sent(Ids(sent.Body, plan.Batch), plan.Summary is not null);
                    lock (_gate)
                    {
                        _stamp = sent.Body?.Str("stamp");
                        _problem = null;
                        if (_lease)
                        {
                            // A send renews the turn's hold.
                            _leaseAt = DateTime.UtcNow;
                        }
                    }
                    continue;
                }
                if (sent.Status == HttpStatusCode.Conflict && sent.Body?.Str("status") == "behind")
                {
                    // Those Arena had already (sent before, the answer lost), or took (a call's results, put straight after it).
                    _store.Sent(Ids(sent.Body, plan.Batch));
                    if (sent.Body?.Bool("fork") == true && _store.Read(d => d.Waiting) > 0 && !plan.Branch)
                    {
                        // The web shows another branch now: what this session still has to send goes on a branch of its own, shown from then on.
                        _store.Branch();
                        _notice(false, "The web chat moved to another branch while this session's messages were on their way: they go on a branch of their own there.");
                        continue;
                    }
                    lock (_gate)
                    {
                        _behind = true;
                    }
                    _notice(false, "The web chat has new messages: they come into this session at the next turn.");
                    return TimeSpan.Zero;
                }
                return Refused(sent);
            }
        }
        finally
        {
            _store.Busy.Release();
        }
    }

    /// <summary>The first messages still to send are a call's results: they go now, straight after it, whatever the web added since.</summary>
    private bool Insertable(List<SyncEntry> batch) =>
        batch.Count > 0 && batch[0].Message.Str("role") == "tool" && _store.Read(d =>
            d.FirstUnsent > 0 && d.Stream[d.FirstUnsent - 1].Message["tool_calls"] is JsonArray { Count: > 0 } calls
            && calls.OfType<JsonObject>().Any(c => c.Str("id") == batch[0].Message.Str("tool_call_id")));

    /// <summary>The ids Arena gave the messages sent, in their order: each with its message's number.</summary>
    private static List<(int Key, string Id)> Ids(JsonObject? body, List<SyncEntry> batch) =>
        [.. (body?["ids"] as JsonArray ?? []).Select(i => i?.GetValue<string>()).OfType<string>().Zip(batch, (id, e) => (e.Key, id))];

    /// <summary>
    /// The chat is held while a turn runs here, renewed every <see cref="TurnRenew"/>; let go once the turn ended and Arena has
    /// its messages (or they cannot go now).
    /// </summary>
    private async Task TurnHoldAsync(CancellationToken ct)
    {
        bool take;
        bool give;
        string? conversation;
        lock (_gate)
        {
            conversation = _store.Read(d => d.Unlinked ? null : d.Conversation);
            var waiting = _store.Read(d => d.Waiting);
            take = _turn && !_asking && conversation is not null && (!_lease || DateTime.UtcNow - _leaseAt >= TurnRenew);
            give = !_turn && _lease && conversation is not null && (waiting == 0 || _behind || _problem is not null);
            if (give)
            {
                // Let go once: a session closing meanwhile does not let it go again.
                _lease = false;
            }
        }
        if (conversation is null || (!take && !give))
        {
            return;
        }
        if (take)
        {
            await HoldAsync(conversation, ct);
            return;
        }
        // Not let go (Arena out of reach): Arena lets it go when the hold lapses.
        await SendAsync(HttpMethod.Delete, $"/api/code-arena/chats/{conversation}/turn", null, ct);
    }

    /// <summary>The chat is being asked for now: not asked again meanwhile.</summary>
    private bool _asking;

    /// <summary>Asks Arena for the chat for this turn; false while the web chat answers.</summary>
    private async Task<bool> HoldAsync(string conversation, CancellationToken ct)
    {
        lock (_gate)
        {
            _asking = true;
        }
        try
        {
            var held = await SendAsync(HttpMethod.Post, $"/api/code-arena/chats/{conversation}/turn", null, ct, TakeInLimit);
            if (held.Status == HttpStatusCode.OK)
            {
                lock (_gate)
                {
                    // A turn that ended meanwhile lets it go at once (the loop does).
                    _lease = true;
                    _leaseAt = DateTime.UtcNow;
                }
                if (!_turn)
                {
                    Wake();
                }
                return true;
            }
            if (held.Status == HttpStatusCode.NotFound && held.Body?.Str("status") == "gone")
            {
                Refused(held);
            }
            return false;
        }
        finally
        {
            lock (_gate)
            {
                _asking = false;
            }
        }
    }

    /// <summary>
    /// A turn starts here: the chat is this end's until it ends (the web queues the person's messages meanwhile). While the
    /// web chat is answering, the turn waits for its answer (at most <see cref="WebAnswerWait"/>; Stop goes on without it),
    /// so the two never answer at once.
    /// </summary>
    public async Task BeginTurnAsync(CancellationToken ct)
    {
        string? conversation;
        lock (_gate)
        {
            _turn = true;
            conversation = _store.Read(d => d.Unlinked ? null : d.Conversation);
        }
        if (conversation is null)
        {
            // A chat made with this turn's first message: nobody has it on the web yet.
            return;
        }
        var until = DateTime.UtcNow + WebAnswerWait;
        var said = false;
        try
        {
            while (!await HoldAsync(conversation, ct))
            {
                if (DateTime.UtcNow >= until || !await AnsweringAsync(conversation, ct))
                {
                    if (said)
                    {
                        _notice(false, "The web chat is still answering: this turn goes on, and its answer comes in when it ends.");
                    }
                    return;
                }
                if (!said)
                {
                    said = true;
                    _notice(false, "The web chat is answering: this turn starts when its answer ends (Stop goes on without it).");
                }
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            // Arena out of reach: the turn goes on, and the chat is asked for again as it does.
        }
    }

    /// <summary>Whether the web chat is answering now (a read says so).</summary>
    private async Task<bool> AnsweringAsync(string conversation, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Get, $"/api/code-arena/chats/{conversation}?limit=0", null, ct, TakeInLimit)) is { Status: HttpStatusCode.Conflict } read
        && read.Body?.Str("status") == "answering";

    /// <summary>The turn ended here: the chat is let go once Arena has its messages.</summary>
    public void EndTurn()
    {
        lock (_gate)
        {
            if (!_turn)
            {
                return;
            }
            _turn = false;
        }
        Wake();
    }

    /// <summary>What Arena refused: a chat deleted there ends the sync; one answering on the web is tried again soon; the rest is said.</summary>
    private TimeSpan Refused((HttpStatusCode Status, JsonObject? Body) answer)
    {
        var why = answer.Body?.Str("error") ?? answer.Body?.Str("message") ?? $"HTTP {(int)answer.Status}";
        switch (answer.Status)
        {
            case HttpStatusCode.NotFound when answer.Body?.Str("status") == "gone":
                if (_store.Read(d => d.Unlinked))
                {
                    return TimeSpan.Zero;
                }
                _store.Link(null);
                lock (_gate)
                {
                    _lease = false;
                }
                _notice(true, "The session's chat in Arena was deleted: it is not kept in step any more.");
                return TimeSpan.Zero;
            case HttpStatusCode.Conflict:
                return TimeSpan.FromSeconds(10);
            case HttpStatusCode.NotFound:
                Problem("this Arena does not keep Code Arena's chats (an older version)");
                return TimeSpan.FromMinutes(10);
            default:
                Problem($"Arena refused it: {Fmt.OneLine(why, 200)}");
                return TimeSpan.FromMinutes(1);
        }
    }

    private void Problem(string text)
    {
        bool fresh;
        lock (_gate)
        {
            fresh = _problem != text;
            _problem = text;
        }
        if (fresh)
        {
            _notice(true, $"Chats are not in step with Arena now: {text}.");
        }
    }

    /// <summary>
    /// What the person added in the web chat since this end last had it, written into the session: the next turn's
    /// model hears it. Nothing when there is nothing new, or Arena cannot say now (said once).
    /// </summary>
    public async Task<Taken> TakeInAsync(CancellationToken ct)
    {
        if (_store.Read(d => d.Conversation is null || d.Unlinked))
        {
            return Taken.None;
        }
        // A send under way (Arena slow to answer) never holds the turn: past a few seconds the turn goes on, and the web's news comes next turn.
        if (!await _store.Busy.WaitAsync(TakeInWait, ct))
        {
            // Not read, which says nothing of whether the web added anything: the sends go on as they were.
            _notice(false, "The web chat is not read now (Arena is slow to answer): what was added there comes in at the next turn.");
            return Taken.None;
        }
        try
        {
            // Read where this end stands only now, past any send or take-in that was under way: what they took is not read twice.
            var (conversation, since) = _store.Read(d => (d.Unlinked ? null : d.Conversation, d.LastSent));
            string? stamp;
            lock (_gate)
            {
                // Known to have news: read all of it.
                stamp = _behind ? null : _stamp;
            }
            if (conversation is null)
            {
                return Taken.None;
            }
            (HttpStatusCode Status, JsonObject? Body) read;
            var messages = new List<JsonObject>();
            try
            {
                // A turn's start waits a few seconds at most: what the web added comes in at the next turn otherwise.
                read = await SendAsync(HttpMethod.Get, ReadPath(conversation, since, stamp), null, ct, TakeInLimit);
                if (read.Status == HttpStatusCode.OK && read.Body?.Bool("unchanged") != true)
                {
                    messages.AddRange((read.Body?["messages"] as JsonArray ?? []).OfType<JsonObject>());
                    // A long chat comes a page at a time: each goes on after the last message of the one before.
                    var page = read.Body;
                    while (page?.Bool("more") == true && messages.Count > 0 && messages[^1].Str("id") is { } last)
                    {
                        var next = await SendAsync(HttpMethod.Get, ReadPath(conversation, last, null), null, ct);
                        if (next.Status != HttpStatusCode.OK || next.Body is null || next.Body.Bool("fork") == true)
                        {
                            // Changed meanwhile: what came so far is taken in, the rest at the next read.
                            break;
                        }
                        page = next.Body;
                        messages.AddRange((page["messages"] as JsonArray ?? []).OfType<JsonObject>());
                        read.Body!["stamp"] = page.Str("stamp");
                        read.Body["summary"] = page["summary"]?.DeepClone();
                    }
                    read.Body!["more"] = page?.Bool("more") == true;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
            {
                Problem($"Arena cannot be reached ({Fmt.OneLine(e.Message, 120)}): what was added on the web comes later");
                return Taken.None;
            }
            // The pictures that went with the web's messages, for a model that sees them.
            var images = await ImagesAsync(messages, ct);
            try
            {
                return Take(read, messages, images);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The session's file cannot be written: the web's news is not taken in now (it is still there next turn).
                Problem($"the session's file cannot be written ({Fmt.OneLine(e.Message, 120)})");
                return Taken.None;
            }
        }
        finally
        {
            _store.Busy.Release();
        }
    }

    /// <summary>The web chat's messages after <paramref name="since"/> (from the start: null); with a stamp, only "unchanged" when nothing moved since it.</summary>
    private static string ReadPath(string conversation, string? since, string? stamp, int? limit = null) =>
        $"/api/code-arena/chats/{conversation}?" + string.Join('&', new[]
        {
            since is null ? null : $"since={Uri.EscapeDataString(since)}",
            stamp is null ? null : $"stamp={Uri.EscapeDataString(stamp)}",
            limit is null ? null : $"limit={limit}",
        }.OfType<string>());

    /// <summary>Asks the web chat every <see cref="WatchEvery"/> whether it has news, while someone listens, and tells them.</summary>
    private async Task WatchAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(WatchEvery, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (OnNews is null)
            {
                continue;
            }
            bool news;
            try
            {
                news = await LookAsync(_stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // Arena out of reach: asked again in a while (the sends say so when they fail).
                continue;
            }
            if (news)
            {
                OnNews?.Invoke();
            }
        }
    }

    /// <summary>
    /// Whether the web chat has messages this end has not taken in: asked of Arena without taking them in (that is the
    /// listener's to do, between turns), and without its messages. False while this end is busy with Arena (a send, a
    /// take-in): asked next time.
    /// </summary>
    internal async Task<bool> LookAsync(CancellationToken ct)
    {
        if (!await _store.Busy.WaitAsync(0, ct))
        {
            return false;
        }
        try
        {
            var (conversation, since) = _store.Read(d => (d.Unlinked ? null : d.Conversation, d.LastSent));
            string? stamp;
            lock (_gate)
            {
                // Known to have news: asked only whether it can be read now (not while the web chat answers).
                stamp = _behind ? null : _stamp;
            }
            if (conversation is null)
            {
                return false;
            }
            var read = await SendAsync(HttpMethod.Get, ReadPath(conversation, since, stamp, limit: 0), null, ct, TakeInLimit);
            if (read.Status == HttpStatusCode.NotFound && read.Body?.Str("status") == "gone")
            {
                Refused(read);
                return false;
            }
            // Answering on the web (its messages come when it ends), or refused: asked again next time.
            if (read.Status != HttpStatusCode.OK || read.Body?.Bool("unchanged") == true)
            {
                return false;
            }
            var news = read.Body?.Bool("more") == true || read.Body?.Bool("fork") == true
                || (read.Body?["summary"] is JsonObject summary && summary.Str("upTo") is { } upTo && upTo != _store.Read(d => d.WebSummary) && _store.Read(d => d.Stream.Any(e => e.Id == upTo)));
            lock (_gate)
            {
                if (_store.Read(d => d.LastSent) != since)
                {
                    return false;
                }
                if (!news && !_behind)
                {
                    // Nothing new as of this branch: the next asks need not read the chat while it stays so.
                    _stamp = read.Body?.Str("stamp");
                    return false;
                }
                // Sends wait for it to be taken in (Arena would refuse them as behind).
                _behind = true;
                return true;
            }
        }
        finally
        {
            _store.Busy.Release();
        }
    }

    /// <summary>What a read of the web chat brought: written into the session, the session's own messages known and left out.</summary>
    private Taken Take((HttpStatusCode Status, JsonObject? Body) read, List<JsonObject> messages, IReadOnlyDictionary<string, string>? images = null)
    {
        if (read.Status != HttpStatusCode.OK)
        {
            if (read.Status == HttpStatusCode.Conflict)
            {
                bool fresh;
                lock (_gate)
                {
                    fresh = !_answeringSaid;
                    _answeringSaid = true;
                }
                if (fresh)
                {
                    _notice(false, "The web chat is answering: what it adds comes in when the answer ends.");
                }
            }
            else
            {
                Refused(read);
            }
            return Taken.None;
        }
        if (read.Body?.Bool("unchanged") == true)
        {
            lock (_gate)
            {
                _behind = false;
                _answeringSaid = false;
            }
            Wake();
            return Taken.None;
        }
        var body = read.Body!;
        var fork = body.Bool("fork") == true;
        var at = body.Str("base");
        var d = _store.Read(s => (Stream: s.Stream.ToList(), First: s.FirstUnsent, s.Branch));
        var unknown = d.Stream.Any(e => e.Id == "");
        var start = 0;
        if (!fork && !unknown)
        {
            // The session's own messages, which Arena had though its answer was lost: not taken in twice.
            var own = new List<(int Key, string Id)>();
            while (start < messages.Count && d.First + own.Count < d.Stream.Count && d.Stream[d.First + own.Count] is var mine
                   && Own(messages[start].Str("ref")) == mine.Key && messages[start].Str("id") is { } id)
            {
                own.Add((mine.Key, id));
                start++;
            }
            _store.Sent(own);
            d = _store.Read(s => (Stream: s.Stream.ToList(), First: s.FirstUnsent, s.Branch));
        }
        var web = messages.Skip(start).Select(m => Entry(m, images)).ToList();
        var unsent = d.Stream.Skip(d.First).ToList();
        Taken taken;
        if (unknown)
        {
            // A session from before Arena's ids: its conversation is the web chat's as it is now, then what it has still to send.
            taken = new Taken([], _store.Rewrite(0, [.. web, .. unsent]));
        }
        else if (fork && unsent.Count > 0)
        {
            // The web moved elsewhere while this session's messages were on their way: they go on a branch of their own there.
            if (!d.Branch)
            {
                _store.Branch();
            }
            taken = Taken.None;
        }
        else if (fork)
        {
            var keep = at is null ? 0 : d.Stream.FindIndex(e => e.Id == at) + 1;
            taken = new Taken(web.Select(e => e.Message).ToList(), _store.Rewrite(keep, web),
                $"The web chat's conversation changed (a question edited, an answer written again, or another branch shown): this session follows it from its message {keep + 1}.");
        }
        else if (web.Count == 0)
        {
            taken = Taken.None;
        }
        else if (unsent.Count == 0)
        {
            foreach (var e in web)
            {
                _store.Remote(e);
            }
            taken = new Taken(web.Select(e => e.Message).ToList());
        }
        else if (Insertable(unsent))
        {
            // A call's results still to send go first, straight after it: then what the web added comes in.
            Wake();
            return Taken.None;
        }
        else
        {
            // Added on the web while this session's last messages were on their way: they come before them here, as there.
            taken = new Taken(web.Select(e => e.Message).ToList(), _store.Rewrite(d.First, [.. web, .. unsent]),
                $"Taken in from the web chat: {web.Count} message{(web.Count == 1 ? "" : "s")} added there while this session's last ones were on their way; they come before them, as there.");
        }
        taken = WebSummary(body, taken);
        lock (_gate)
        {
            _stamp = body.Bool("more") == true ? null : body.Str("stamp");
            _behind = body.Bool("more") == true;
            _answeringSaid = false;
        }
        Wake();
        return taken;
    }

    /// <summary>
    /// The web's summary of its chat, when it covers more than the model reads word for word here: the model reads it
    /// instead of what it covers, as the web's own model does.
    /// </summary>
    private Taken WebSummary(JsonObject body, Taken taken)
    {
        if (body["summary"] is not JsonObject summary || summary.Str("upTo") is not { } upTo || summary.Str("text") is not { Length: > 0 } text)
        {
            return taken;
        }
        var (index, from, known, count) = _store.Read(d => (d.Stream.FindIndex(e => e.Id == upTo), d.From, d.WebSummary, d.Stream.Count));
        if (known == upTo || index < 0 || index < from)
        {
            return taken;
        }
        var history = new List<JsonObject>
        {
            new() { ["role"] = "user", ["content"] = Agent.SummaryHead + "\n\n" + text },
            new() { ["role"] = "assistant", ["content"] = Agent.Understood },
        };
        history.AddRange(_store.Read(d => d.Stream.Skip(index + 1).Select(e => e.Message.Clone()).ToList()));
        _store.Compacted(history, count - index - 1, upTo);
        return taken with { History = history, Said = (taken.Said is null ? "" : taken.Said + " ") + "The web chat's summary of its older messages is read here instead of them." };
    }

    /// <summary>The number in this session of a message it sent (its ref, session:number); null for any other.</summary>
    private int? Own(string? reference) =>
        reference is { } r && r.StartsWith(_store.Id + ":", StringComparison.Ordinal) && int.TryParse(r.AsSpan(_store.Id.Length + 1), out var index) ? index : null;

    /// <summary>
    /// Goes back to the first <paramref name="messages"/> of the model's history (/rewind), once no send is under way: what
    /// Arena has past there stays on the web, and what the session adds next goes on a branch of its own. The history now.
    /// </summary>
    public async Task<List<JsonObject>> RewindAsync(int messages, CancellationToken ct)
    {
        var held = await _store.Busy.WaitAsync(RequestLimit, ct);
        try
        {
            var history = _store.Rewind(messages);
            if (_store.Read(d => d.Branch && !d.Unlinked && d.Conversation is not null))
            {
                _notice(false, "The chat in Arena goes on from there on a branch of its own: the web shows the turns gone back from as another branch.");
            }
            return history;
        }
        finally
        {
            if (held)
            {
                _store.Busy.Release();
            }
            Wake();
        }
    }

    /// <summary>
    /// Whose chats the key keeps sessions with ("Ada Lovelace (ada@example.com)"): a key of another account syncs with that
    /// account's chats, not the person's. Null when Arena does not say (an older one) or cannot be reached.
    /// </summary>
    public static async Task<string?> WhoAsync(Config config, HttpClient http, CancellationToken ct)
    {
        try
        {
            using var req = Request(HttpMethod.Get, config.Url!.TrimEnd('/') + "/api/code-arena/chats?limit=1", config.ApiKey!, null);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(RequestLimit);
            using var res = await http.SendAsync(req, limit.Token);
            if (!res.IsSuccessStatusCode || Json.ParseObject(await res.Content.ReadAsStringAsync(limit.Token))?["me"] is not JsonObject me)
            {
                return null;
            }
            var (name, email) = (me.Str("name"), me.Str("email"));
            return name is { Length: > 0 } && email is { Length: > 0 } && name != email ? $"{name} ({email})" : email ?? name;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// When the person's chats compact, as their Arena account says (Your account → Answers): their own choice (Own), else
    /// Arena's default; 0 compacts only when a request would not fit. Null when Arena does not say (an older one) or cannot
    /// be reached within <paramref name="limit"/>.
    /// </summary>
    public static async Task<AccountCompaction?> CompactAtAsync(Config config, HttpClient http, TimeSpan limit, CancellationToken ct)
    {
        if (await AccountReadAsync(config, http, "/api/code-arena/preferences", limit, ct) is not { } prefs)
        {
            return null;
        }
        var own = prefs.Int("compactAt");
        return (own ?? prefs.Int("compactAtDefault")) is { } at ? new AccountCompaction(at, own is not null) : null;
    }

    /// <summary>
    /// What the web chat offers the person, as Arena's /api/chat/config says it (read with the key): each model loaded or
    /// not and seeing pictures or not, the thinking levels and their default, the address of GitLab for Argus's links. Null
    /// when Arena does not say (an older one) or cannot be reached within <paramref name="limit"/>.
    /// </summary>
    public static Task<JsonObject?> ChatConfigAsync(Config config, HttpClient http, TimeSpan limit, CancellationToken ct) =>
        AccountReadAsync(config, http, "/api/code-arena/config", limit, ct);

    /// <summary>A JSON object of the account's from Arena, read with the key; null when there is no Arena, it refuses, or it does not answer within <paramref name="limit"/>.</summary>
    private static async Task<JsonObject?> AccountReadAsync(Config config, HttpClient http, string path, TimeSpan limit, CancellationToken ct)
    {
        if (config.Url is not { Length: > 0 } url || config.ApiKey is not { Length: > 0 } key)
        {
            return null;
        }
        try
        {
            using var req = Request(HttpMethod.Get, url.TrimEnd('/') + path, key, null);
            using var within = CancellationTokenSource.CreateLinkedTokenSource(ct);
            within.CancelAfter(limit);
            using var res = await http.SendAsync(req, within.Token);
            return res.IsSuccessStatusCode ? Json.ParseObject(await res.Content.ReadAsStringAsync(within.Token)) : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>How many chats in Arena are listed at most (/web all, the IDE): the newest.</summary>
    public const int MostListed = 5000;

    /// <summary>
    /// The person's chats in Arena, newest first, to continue one here: the newest <paramref name="most"/>, read a page
    /// of 200 at a time.
    /// </summary>
    public static async Task<List<WebChat>> ListAsync(Config config, HttpClient http, CancellationToken ct, int most = 30)
    {
        var chats = new List<WebChat>();
        string? before = null;
        while (chats.Count < most)
        {
            var url = config.Url!.TrimEnd('/') + $"/api/code-arena/chats?limit={Math.Min(200, most - chats.Count)}" + (before is null ? "" : $"&before={Uri.EscapeDataString(before)}");
            using var req = Request(HttpMethod.Get, url, config.ApiKey!, null);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(RequestLimit);
            using var res = await http.SendAsync(req, limit.Token);
            var body = Json.ParseObject(await res.Content.ReadAsStringAsync(limit.Token));
            if (!res.IsSuccessStatusCode)
            {
                throw new HttpRequestException(body?.Str("error") ?? $"Arena answered HTTP {(int)res.StatusCode}");
            }
            var page = (body?["chats"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            chats.AddRange(page.Select(c => new WebChat(
                c.Str("id") ?? "", c.Str("title") ?? "", c.Str("origin"), c.Str("originPlace"),
                DateTimeOffset.TryParse(c.Str("updatedAt"), out var at) ? at : DateTimeOffset.MinValue, (int)(c.Long("messages") ?? 0))));
            // The next page: those changed before the last one listed (exactly as Arena wrote its time).
            if (body?.Bool("more") != true || page.Count == 0 || page[^1].Str("updatedAt") is not { } last)
            {
                break;
            }
            before = last;
        }
        return chats;
    }

    /// <summary>
    /// Where the sync stands, as the IDE shows it: this session's chat (null: none yet), how many of its messages Arena lacks,
    /// what stopped them (null: nothing), and whether the web chat has news not taken in yet.
    /// </summary>
    public JsonObject State()
    {
        var (conversation, waiting) = _store.Read(d => (d.Unlinked ? null : d.Conversation, d.Waiting));
        lock (_gate)
        {
            return new JsonObject { ["chat"] = conversation, ["waiting"] = waiting, ["trouble"] = _problem, ["behind"] = _behind };
        }
    }

    /// <summary>
    /// The person continues a chat of theirs here: in Arena, an archived one comes back to the list, once (what this end
    /// adds later leaves it as the person left it). False when Arena cannot say.
    /// </summary>
    public static async Task<bool> ContinuedAsync(Config config, HttpClient http, string conversation, CancellationToken ct)
    {
        try
        {
            using var req = Request(HttpMethod.Post, config.Url!.TrimEnd('/') + $"/api/code-arena/chats/{Uri.EscapeDataString(conversation)}/continued", config.ApiKey!, null);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(RequestLimit);
            using var res = await http.SendAsync(req, limit.Token);
            return res.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Arena has every message of this session (it was sent, or there was none to send).</summary>
    public bool InStep => _store.Read(d => !d.Unlinked && d.Conversation is not null && d.Waiting == 0 && d.Summary is null);

    /// <summary>What stopped the sends (Arena out of reach, a refusal); null: nothing.</summary>
    public string? Trouble
    {
        get
        {
            lock (_gate)
            {
                return _problem;
            }
        }
    }

    /// <summary>
    /// Waits a little for what is still being sent (the session is closing, or is sent from /sync all, with no turn to
    /// come): not past a problem, unless <paramref name="patient"/> (/sync all), when the sends are tried again until
    /// <paramref name="most"/> is up. What the web added meanwhile is taken into the session's file first, before what
    /// is still to send, so that can go.
    /// </summary>
    public async Task FlushAsync(TimeSpan most, bool patient = false, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + most;
        var tries = 0;
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            if (_store.Read(d => d.Unlinked || (d.Waiting == 0 && d.Summary is null)))
            {
                return;
            }
            bool behind;
            lock (_gate)
            {
                behind = _behind;
                if (_problem is not null && !patient)
                {
                    return;
                }
            }
            if (behind)
            {
                if (++tries > 3)
                {
                    return;
                }
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    limit.CancelAfter(until - DateTime.UtcNow is { TotalMilliseconds: > 0 } left ? left : TimeSpan.Zero);
                    await TakeInAsync(limit.Token);
                }
                catch (OperationCanceledException)
                {
                    // It comes when the session is next opened.
                    return;
                }
                continue;
            }
            try
            {
                await Task.Delay(100, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Whether Arena has this chat of the person's (before a session is made for it): false when it has not (deleted,
    /// someone else's, a mistyped id). Throws when Arena cannot say.
    /// </summary>
    public static async Task<bool> ExistsAsync(Config config, HttpClient http, string conversation, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, config.Url!.TrimEnd('/') + $"/api/code-arena/chats/{Uri.EscapeDataString(conversation)}?limit=0", config.ApiKey!, null);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(RequestLimit);
        using var res = await http.SendAsync(req, limit.Token);
        var body = Json.ParseObject(await res.Content.ReadAsStringAsync(limit.Token));
        return res.StatusCode switch
        {
            HttpStatusCode.OK or HttpStatusCode.Conflict => true,
            HttpStatusCode.NotFound when body?.Str("status") == "gone" => false,
            _ => throw new HttpRequestException(body?.Str("error") ?? $"Arena answered HTTP {(int)res.StatusCode}"),
        };
    }

    public async ValueTask DisposeAsync()
    {
        _store.Added -= Added;
        EndTurn();
        await FlushAsync(TimeSpan.FromSeconds(5));
        await LetGoAsync();
        await _stop.CancelAsync();
        try
        {
            await Task.WhenAll(_loop, _watch);
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        _stop.Dispose();
    }

    /// <summary>The chat held for a turn is let go as the session closes (else Arena lets it go when its hold lapses).</summary>
    private async Task LetGoAsync()
    {
        string? conversation;
        lock (_gate)
        {
            conversation = _lease ? _store.Read(d => d.Conversation) : null;
            _lease = false;
        }
        if (conversation is null)
        {
            return;
        }
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await SendAsync(HttpMethod.Delete, $"/api/code-arena/chats/{conversation}/turn", null, limit.Token);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            // Arena lets it go when the hold lapses.
        }
    }

    /// <summary>
    /// A message as Arena keeps it: the model's own shape, with its ref (session:number), when it was written, and what the
    /// web shows of it: an answer's model, tokens and thinking, and whether the person stopped it; a tool result's tool; a
    /// note of Code Arena's own (not the person's words).
    /// </summary>
    internal static JsonObject Out(SyncEntry e, string session, string? toolName = null)
    {
        var m = e.Message;
        var role = m.Str("role") ?? "user";
        var o = new JsonObject { ["role"] = role, ["content"] = m["content"]?.DeepClone() ?? "", ["at"] = e.At.ToString("o"), ["ref"] = $"{session}:{e.Key}" };
        if (m.Str("reasoning_content") is { Length: > 0 } reasoning)
        {
            o["reasoning"] = reasoning;
        }
        if (m["tool_calls"] is JsonArray calls)
        {
            o["toolCalls"] = calls.DeepClone();
        }
        if (m.Str("tool_call_id") is { } callId)
        {
            o["toolCallId"] = callId;
        }
        if ((toolName ?? m.Str("name")) is { } name && role == "tool")
        {
            o["name"] = name;
        }
        if (role == "assistant")
        {
            if (e.Model is not null)
            {
                o["model"] = e.Model;
            }
            if (e.Usage is { } usage)
            {
                o["promptTokens"] = usage.Prompt;
                o["cachedTokens"] = usage.Cached;
                o["completionTokens"] = usage.Completion;
            }
            // Stopped by the person here: the web shows it as stopped, not the mark the model reads.
            if (m.Str("content") is { } text && text.EndsWith(Agent.StoppedMark, StringComparison.Ordinal))
            {
                o["content"] = text[..^Agent.StoppedMark.Length].TrimEnd();
                o["status"] = "stopped";
            }
        }
        // A question that quotes part of an answer: Arena keeps the quote as its own field, apart from the question.
        if (role == "user" && m["content"] is JsonValue said && said.TryGetValue<string>(out var asked) && Quotes.Split(asked) is { Quote: { } quote } split)
        {
            o["content"] = split.Text;
            o["quote"] = quote;
        }
        if (e.Kind is not null)
        {
            o["kind"] = e.Kind;
        }
        return o;
    }

    /// <summary>The most of one image a message takes in (a picture larger than that is named instead).</summary>
    public const int MostImageBytes = 5 * 1024 * 1024;

    /// <summary>
    /// A message from the web chat as the session keeps it, in the model's own shape: its text files are in its words already
    /// (Arena put them there, as the web's model reads them), its pictures go as pictures (<paramref name="images"/>: those
    /// fetched, as data URLs) or by name, an answer stopped there says so, and one with no words and no calls (it failed, or
    /// was stopped before it said anything) says that: some models refuse an empty answer in what they read.
    /// </summary>
    internal static SyncEntry Entry(JsonObject m, IReadOnlyDictionary<string, string>? images = null)
    {
        var role = m.Str("role") ?? "user";
        var status = m.Str("status") is { } s && s != "complete" ? s : null;
        // A question from the web that quotes part of an answer: the session keeps what the model reads, the quote first.
        var o = new JsonObject { ["role"] = role, ["content"] = role == "user" ? Quotes.Compose(Quotes.Clean(m.Str("quote")), m.Str("content") ?? "") : m.Str("content") ?? "" };
        if (m["toolCalls"] is JsonArray calls && calls.Count > 0)
        {
            o["tool_calls"] = calls.DeepClone();
        }
        if (role == "assistant")
        {
            var text = o.Str("content") ?? "";
            if (status == "stopped")
            {
                o["content"] = text.Length > 0 ? $"{text}\n\n{Agent.StoppedMark}" : Agent.StoppedMark;
            }
            else if (text.Length == 0 && o["tool_calls"] is null)
            {
                o["content"] = "(no answer)";
            }
            if (m.Str("reasoning") is { Length: > 0 } reasoning)
            {
                o["reasoning_content"] = reasoning;
            }
        }
        if (m.Str("toolCallId") is { } callId)
        {
            o["tool_call_id"] = callId;
        }
        if (role == "user" && (m["attachments"] as JsonArray ?? []).OfType<JsonObject>().Where(a => a.Str("kind") == "image").ToList() is { Count: > 0 } pictures)
        {
            var text = new StringBuilder(o.Str("content"));
            var parts = new JsonArray();
            foreach (var picture in pictures)
            {
                if (picture.Str("id") is { } id && images?.GetValueOrDefault(id) is { } url)
                {
                    parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } });
                }
                else
                {
                    text.Append("\n\n[image attached: ").Append(picture.Str("name") ?? "an image").Append(']');
                }
            }
            o["content"] = parts.Count == 0 ? text.ToString() : new JsonArray([new JsonObject { ["type"] = "text", ["text"] = text.ToString() }, .. parts.Select(p => p!.DeepClone())]);
        }
        return new SyncEntry
        {
            Message = o,
            Id = m.Str("id") ?? "",
            Web = true,
            At = DateTimeOffset.TryParse(m.Str("at"), out var when) ? when : DateTimeOffset.UtcNow,
            Model = m.Str("model"),
            Usage = m.Long("promptTokens") is { } prompt ? new TokenUsage(prompt, m.Long("cachedTokens") ?? 0, m.Long("completionTokens") ?? 0) : null,
            Kind = m.Str("kind"),
            Status = status,
        };
    }

    /// <summary>
    /// The pictures attached on the web to these messages, as data URLs, fetched with the key (Arena gives them to their
    /// owner only); none when the model cannot see. One that cannot be fetched, or is too large, is named instead.
    /// </summary>
    private async Task<Dictionary<string, string>> ImagesAsync(List<JsonObject> messages, CancellationToken ct)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!_vision())
        {
            return found;
        }
        foreach (var picture in messages.Where(m => m.Str("role") == "user").SelectMany(m => (m["attachments"] as JsonArray ?? []).OfType<JsonObject>())
                     .Where(a => a.Str("kind") == "image"))
        {
            if (picture.Str("id") is not { } id || found.ContainsKey(id))
            {
                continue;
            }
            try
            {
                using var req = Request(HttpMethod.Get, $"{_base}/api/code-arena/attachments/{Uri.EscapeDataString(id)}", _key, null);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(RequestLimit);
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token);
                if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > MostImageBytes)
                {
                    continue;
                }
                var bytes = await res.Content.ReadAsByteArrayAsync(limit.Token);
                if (bytes.Length <= MostImageBytes)
                {
                    found[id] = $"data:{res.Content.Headers.ContentType?.MediaType ?? picture.Str("contentType") ?? "image/png"};base64,{Convert.ToBase64String(bytes)}";
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
            {
                // Named instead: the model reads that a picture went with it.
            }
        }
        return found;
    }

    /// <param name="answerWithin">How long Arena has to start answering (its headers), when shorter than the request's limit:
    /// a long answer then has the whole limit to arrive.</param>
    private async Task<(HttpStatusCode Status, JsonObject? Body)> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct, TimeSpan? answerWithin = null)
    {
        using var req = Request(method, _base + path, _key, body);
        // A deadline of its own: Arena slow to answer, or a connection gone half-open, is retried later rather than waited on.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(RequestLimit);
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
        headers.CancelAfter(answerWithin ?? RequestLimit);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headers.Token);
        var text = await res.Content.ReadAsStringAsync(limit.Token);
        return (res.StatusCode, text.Length > 0 && text.TrimStart().StartsWith('{') ? Json.ParseObject(text) : null);
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string key, JsonObject? body)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        // Arena's guard for calls that change something.
        req.Headers.Add("X-Requested-With", "code-arena");
        if (body is not null)
        {
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        return req;
    }
}
