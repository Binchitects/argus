using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>A chat of the person's in Arena, as Code Arena lists them to continue one.</summary>
internal sealed record WebChat(string Id, string Title, string? Origin, string? Place, DateTimeOffset Updated, int Messages);

/// <summary>
/// The session kept in step with a chat in Arena (/api/code-arena), both ways: each message the session adds is sent
/// in the background, in order, again until Arena has it (a lost connection, Arena restarting); what the person added
/// in the web chat comes in at the next turn's start, and while the session waits (the terminal's prompt, the IDE)
/// within seconds: the web chat is asked every <see cref="WatchEvery"/> (<see cref="OnNews"/>). Arena counts the
/// chat's messages: a send that does not follow on from its count is refused with what this end lacks, which is taken
/// in first.
/// </summary>
internal sealed class ChatSync : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _key;
    private readonly SessionStore _store;
    private readonly string _place;
    private readonly Func<string> _model;
    private readonly Action<bool, string> _notice;
    private readonly object _gate = new();
    private readonly List<(JsonObject Message, string Model, DateTimeOffset At)> _local = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly Task _watch;
    private string? _conversation;
    /// <summary>The web chat's branch as last read in step with <see cref="_server"/>: asked with it, Arena says only "unchanged".</summary>
    private string? _stamp;
    private int _server;
    private int _pushed;
    private bool _unlinked;
    /// <summary>Arena has messages this end has not taken in: sending waits for the next turn's start.</summary>
    private bool _behind;
    /// <summary>"The web chat is answering" was said: not again until a read gets through.</summary>
    private bool _answeringSaid;
    private string? _problem;

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

    /// <summary>
    /// Told when the web chat has messages this session has not taken in, asked every <see cref="WatchEvery"/>: the
    /// terminal at its prompt, or the IDE with nothing running, takes them in then. Null: nobody listens (a one-shot run),
    /// and the web chat is not asked; its news comes at the next turn's start.
    /// </summary>
    public Action? OnNews { get; set; }
    private ChatSync(HttpClient http, string arenaUrl, string key, SessionStore store, string place, Func<string> model, Action<bool, string> notice, SessionData? data, DateTimeOffset? written)
    {
        _http = http;
        _base = arenaUrl.TrimEnd('/');
        _key = key;
        _store = store;
        _place = place;
        _model = model;
        _notice = notice;
        if (data is not null)
        {
            _conversation = data.Conversation;
            _unlinked = data.Unlinked;
            _server = data.ServerCount;
            _pushed = Math.Min(data.Pushed, data.Local.Count);
            _local.AddRange(data.Local.Select(m => (m, data.Model ?? model(), written ?? DateTimeOffset.UtcNow)));
        }
        store.Added += Added;
        _loop = Task.Run(LoopAsync);
        _watch = Task.Run(WatchAsync);
        if (_pushed < _local.Count)
        {
            Wake();
        }
    }

    /// <summary>
    /// Keeps this session in step with its chat in Arena (made at its first message); null when there is no Arena to keep
    /// it with. <paramref name="written"/>: when the messages read back were written, as Arena is told (now when not given).
    /// </summary>
    public static ChatSync? Start(Config config, HttpClient http, SessionStore store, string place, Func<string> model, Action<bool, string> notice,
        SessionData? data = null, DateTimeOffset? written = null) =>
        config.SyncChats && config.Url is { Length: > 0 } url && config.ApiKey is { Length: > 0 } key
            ? new ChatSync(http, url, key, store, place, model, notice, data, written)
            : null;

    /// <summary>The Arena chat this session is kept with, once made.</summary>
    public string? Conversation
    {
        get
        {
            lock (_gate)
            {
                return _conversation;
            }
        }
    }

    /// <summary>Where it stands, in words (/sync).</summary>
    public string Describe()
    {
        lock (_gate)
        {
            if (_unlinked)
            {
                return "Not kept in step: its chat in Arena was deleted.";
            }
            var where = _conversation is null ? "Its chat in Arena is made with the first message." : $"Kept in step with the chat {_base}/chat/{_conversation}.";
            var waiting = _local.Count - _pushed;
            return where + (waiting > 0 ? $" {waiting} message{(waiting == 1 ? "" : "s")} still to send{(_problem is null ? "" : $" ({_problem})")}." : " Arena has every message.")
                + (_behind ? " The web chat has new messages: they come in at the next turn." : "");
        }
    }

    /// <summary>Links a new session to a chat already in Arena (continued here): its messages come in at the next turn.</summary>
    public void Link(string conversation)
    {
        lock (_gate)
        {
            _conversation = conversation;
            _server = 0;
            _stamp = null;
            _behind = true;
            _store.Sync(_conversation, _server, _pushed);
        }
    }

    private void Added(JsonObject message)
    {
        lock (_gate)
        {
            _local.Add((message.Clone(), _model(), DateTimeOffset.UtcNow));
        }
        Wake();
    }

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
                if (wait > TimeSpan.Zero)
                {
                    await Task.WhenAny(Task.Delay(wait, _stop.Token), _wake.WaitAsync(_stop.Token));
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

    /// <summary>Sends what Arena lacks; how long to wait before trying again (zero: nothing left, or nothing to try).</summary>
    private async Task<TimeSpan> PushAsync(CancellationToken ct)
    {
        await _busy.WaitAsync(ct);
        try
        {
            while (true)
            {
                string? conversation;
                int after;
                int pushed;
                List<(JsonObject Message, string Model, DateTimeOffset At)> batch;
                lock (_gate)
                {
                    if (_unlinked || _behind || _pushed >= _local.Count)
                    {
                        return TimeSpan.Zero;
                    }
                    conversation = _conversation;
                    after = _server;
                    pushed = _pushed;
                    batch = _local.Skip(_pushed).Take(Batch).ToList();
                }
                if (conversation is null)
                {
                    var made = await SendAsync(HttpMethod.Post, "/api/code-arena/chats", new JsonObject
                    {
                        ["ref"] = _store.Id, ["place"] = _place, ["model"] = _model(), ["title"] = null,
                    }, ct);
                    if (made.Status != HttpStatusCode.OK || made.Body?.Str("id") is not { } id)
                    {
                        return Refused(made);
                    }
                    lock (_gate)
                    {
                        _conversation = id;
                        _store.Sync(_conversation, _server, _pushed);
                    }
                    continue;
                }
                var sent = await SendAsync(HttpMethod.Post, $"/api/code-arena/chats/{conversation}/messages", new JsonObject
                {
                    ["after"] = after,
                    ["messages"] = new JsonArray([.. batch.Select((b, i) => (JsonNode)Out(b.Message, b.Model, b.At, $"{_store.Id}:{pushed + i}"))]),
                }, ct);
                if (sent.Status == HttpStatusCode.OK)
                {
                    lock (_gate)
                    {
                        _server = (int)(sent.Body?.Long("count") ?? after + batch.Count);
                        _stamp = sent.Body?.Str("stamp");
                        _pushed += batch.Count;
                        _problem = null;
                        _store.Sync(_conversation, _server, _pushed);
                    }
                    continue;
                }
                if (sent.Status == HttpStatusCode.Conflict && sent.Body?.Str("status") == "behind")
                {
                    var theirs = (sent.Body?["messages"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                    lock (_gate)
                    {
                        if (theirs.Count > 0 && theirs.All(m => Own(m.Str("ref")) is not null))
                        {
                            // Only this session's own, which Arena had though its answer was lost: carry on after them.
                            _pushed = Math.Max(_pushed, Math.Min(theirs.Max(m => Own(m.Str("ref"))!.Value) + 1, _local.Count));
                            _server = (int)(sent.Body?.Long("count") ?? after + theirs.Count);
                            _stamp = null;
                            _store.Sync(_conversation, _server, _pushed);
                            continue;
                        }
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
            _busy.Release();
        }
    }

    /// <summary>What Arena refused: a chat deleted there ends the sync; one answering on the web is tried again soon; the rest is said.</summary>
    private TimeSpan Refused((HttpStatusCode Status, JsonObject? Body) answer)
    {
        var why = answer.Body?.Str("error") ?? answer.Body?.Str("message") ?? $"HTTP {(int)answer.Status}";
        switch (answer.Status)
        {
            case HttpStatusCode.NotFound when answer.Body?.Str("status") == "gone":
                lock (_gate)
                {
                    _unlinked = true;
                    _conversation = null;
                    _store.Sync(null, _server, _pushed);
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
    /// model hears it. Empty when there is nothing new, or Arena cannot say now (said once).
    /// </summary>
    public async Task<IReadOnlyList<JsonObject>> TakeInAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_conversation is null || _unlinked)
            {
                return [];
            }
        }
        // A send under way (Arena slow to answer) never holds the turn: past a few seconds the turn goes on, and the web's news comes next turn.
        if (!await _busy.WaitAsync(TakeInWait, ct))
        {
            // Not read, which says nothing of whether the web added anything: the sends go on as they were.
            _notice(false, "The web chat is not read now (Arena is slow to answer): what was added there comes in at the next turn.");
            return [];
        }
        try
        {
            string? conversation;
            int after;
            string? stamp;
            // Read where this end stands only now, past any send or take-in that was under way: what they took is not read twice.
            lock (_gate)
            {
                conversation = _conversation;
                after = _server;
                // Known to have news: read all of it.
                stamp = _behind ? null : _stamp;
                if (conversation is null || _unlinked)
                {
                    return [];
                }
            }
            (HttpStatusCode Status, JsonObject? Body) read;
            try
            {
                // A turn's start waits a few seconds at most: what the web added comes in at the next turn otherwise.
                read = await SendAsync(HttpMethod.Get, ReadPath(conversation, after, stamp), null, ct, TakeInLimit);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
            {
                Problem($"Arena cannot be reached ({Fmt.OneLine(e.Message, 120)}): what was added on the web comes later");
                return [];
            }
            try
            {
                return Take(read, after);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The session's file cannot be written: the web's news is not taken in now (it is still there next turn).
                Problem($"the session's file cannot be written ({Fmt.OneLine(e.Message, 120)})");
                return [];
            }
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>The web chat's messages after <paramref name="after"/>; with a stamp, only "unchanged" when nothing moved since it.</summary>
    private static string ReadPath(string conversation, int after, string? stamp) =>
        $"/api/code-arena/chats/{conversation}?after={after}" + (stamp is null ? "" : $"&stamp={Uri.EscapeDataString(stamp)}");

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
    /// listener's to do, between turns). False while this end is busy with Arena (a send, a take-in): asked next time.
    /// </summary>
    internal async Task<bool> LookAsync(CancellationToken ct)
    {
        if (!await _busy.WaitAsync(0, ct))
        {
            return false;
        }
        try
        {
            string conversation;
            int after;
            string? stamp;
            lock (_gate)
            {
                if (_conversation is null || _unlinked)
                {
                    return false;
                }
                conversation = _conversation;
                after = _server;
                // Known to have news: asked only whether it can be read now (not while the web chat answers).
                stamp = _behind ? null : _stamp;
            }
            var read = await SendAsync(HttpMethod.Get, ReadPath(conversation, after, stamp), null, ct, TakeInLimit);
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
            var count = (int)(read.Body?.Long("count") ?? after);
            var messages = (read.Body?["messages"] as JsonArray ?? []).Count;
            lock (_gate)
            {
                if (_conversation != conversation || _server != after)
                {
                    return false;
                }
                if (count == after && messages == 0 && !_behind)
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
            _busy.Release();
        }
    }

    /// <summary>What a read of the web chat brought: written into the session, the model's own messages known and left out.</summary>
    private List<JsonObject> Take((HttpStatusCode Status, JsonObject? Body) read, int after)
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
                return [];
            }
            if (read.Body?.Bool("unchanged") == true)
            {
                lock (_gate)
                {
                    _behind = false;
                    _answeringSaid = false;
                }
                Wake();
                return [];
            }
            var taken = new List<JsonObject>();
            lock (_gate)
            {
                var done = 0;
                try
                {
                    foreach (var m in (read.Body?["messages"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        // One of this session's own, sent before its count was written here (a lost answer, a stop): not taken in twice.
                        if (Own(m.Str("ref")) is { } index)
                        {
                            _pushed = Math.Max(_pushed, Math.Min(index + 1, _local.Count));
                            done++;
                            continue;
                        }
                        var message = In(m);
                        _store.Remote(message);
                        taken.Add(message);
                        done++;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Those written so far are this session's now: the next read goes on after them, not again from the start.
                    _server = after + done;
                    TrySync();
                    throw;
                }
                _server = (int)(read.Body?.Long("count") ?? after + done);
                _stamp = read.Body?.Str("stamp");
                _behind = false;
                _answeringSaid = false;
                _store.Sync(_conversation, _server, _pushed);
            }
        Wake();
        return taken;
    }

    /// <summary>The counts written to the session, when its file can be written.</summary>
    private void TrySync()
    {
        try
        {
            _store.Sync(_conversation, _server, _pushed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Written with the next that can be.
        }
    }

    /// <summary>The place in this session of a message it sent (its ref, session:index); null for any other.</summary>
    private int? Own(string? reference) =>
        reference is { } r && r.StartsWith(_store.Id + ":", StringComparison.Ordinal) && int.TryParse(r.AsSpan(_store.Id.Length + 1), out var index) ? index : null;

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

    /// <summary>Arena has every message of this session (it was sent, or there was none to send).</summary>
    public bool InStep
    {
        get
        {
            lock (_gate)
            {
                return !_unlinked && _conversation is not null && _pushed >= _local.Count;
            }
        }
    }

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
    /// Waits a little for what is still being sent (the session is closing): not past a problem, unless
    /// <paramref name="patient"/> (/sync all), when the sends are tried again until <paramref name="most"/> is up.
    /// </summary>
    public async Task FlushAsync(TimeSpan most, bool patient = false)
    {
        var until = DateTime.UtcNow + most;
        while (DateTime.UtcNow < until)
        {
            lock (_gate)
            {
                if (_unlinked || _behind || _pushed >= _local.Count || (_problem is not null && !patient))
                {
                    return;
                }
            }
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// Whether Arena has this chat of the person's (before a session is made for it): false when it has not (deleted,
    /// someone else's, a mistyped id). Throws when Arena cannot say.
    /// </summary>
    public static async Task<bool> ExistsAsync(Config config, HttpClient http, string conversation, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, config.Url!.TrimEnd('/') + $"/api/code-arena/chats/{Uri.EscapeDataString(conversation)}?after={int.MaxValue}", config.ApiKey!, null);
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
        await FlushAsync(TimeSpan.FromSeconds(5));
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

    /// <summary>A message as Arena keeps it: the model's own shape, with the model that wrote it and when.</summary>
    internal static JsonObject Out(JsonObject m, string model, DateTimeOffset at, string reference)
    {
        var role = m.Str("role") ?? "user";
        var o = new JsonObject { ["role"] = role, ["content"] = m["content"]?.DeepClone() ?? "", ["at"] = at.ToString("o"), ["ref"] = reference };
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
        if (m.Str("name") is { } name)
        {
            o["name"] = name;
        }
        if (role == "assistant")
        {
            o["model"] = model;
        }
        return o;
    }

    /// <summary>A message from the web chat, in the model's own shape.</summary>
    internal static JsonObject In(JsonObject m)
    {
        var o = new JsonObject { ["role"] = m.Str("role") ?? "user", ["content"] = m.Str("content") ?? "" };
        if (m["toolCalls"] is JsonArray calls && calls.Count > 0)
        {
            o["tool_calls"] = calls.DeepClone();
        }
        if (m.Str("toolCallId") is { } callId)
        {
            o["tool_call_id"] = callId;
        }
        if (m.Str("name") is { } name && o.Str("role") == "tool")
        {
            o["name"] = name;
        }
        return o;
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
