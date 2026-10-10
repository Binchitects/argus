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
/// in the web chat comes in at the next turn's start. Arena counts the chat's messages: a send that does not follow on
/// from its count is refused with what this end lacks, which is taken in first.
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
    private string? _conversation;
    private int _server;
    private int _pushed;
    private bool _unlinked;
    /// <summary>Arena has messages this end has not taken in: sending waits for the next turn's start.</summary>
    private bool _behind;
    private string? _problem;

    /// <summary>The most messages one request sends.</summary>
    public const int Batch = 200;

    private ChatSync(HttpClient http, string arenaUrl, string key, SessionStore store, string place, Func<string> model, Action<bool, string> notice, SessionData? data)
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
            _local.AddRange(data.Local.Select(m => (m, data.Model ?? model(), DateTimeOffset.UtcNow)));
        }
        store.Added += Added;
        _loop = Task.Run(LoopAsync);
        if (_pushed < _local.Count)
        {
            Wake();
        }
    }

    /// <summary>Keeps this session in step with its chat in Arena (made at its first message); null when there is no Arena to keep it with.</summary>
    public static ChatSync? Start(Config config, HttpClient http, SessionStore store, string place, Func<string> model, Action<bool, string> notice, SessionData? data = null) =>
        config.SyncChats && config.Url is { Length: > 0 } url && config.ApiKey is { Length: > 0 } key
            ? new ChatSync(http, url, key, store, place, model, notice, data)
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
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
            {
                // Arena out of reach, or restarting: again in a while, longer each time (at most a minute).
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
        string? conversation;
        int after;
        lock (_gate)
        {
            conversation = _conversation;
            after = _server;
            if (conversation is null || _unlinked)
            {
                return [];
            }
        }
        await _busy.WaitAsync(ct);
        try
        {
            (HttpStatusCode Status, JsonObject? Body) read;
            try
            {
                read = await SendAsync(HttpMethod.Get, $"/api/code-arena/chats/{conversation}?after={after}", null, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
            {
                Problem($"Arena cannot be reached ({Fmt.OneLine(e.Message, 120)}): what was added on the web comes later");
                return [];
            }
            if (read.Status != HttpStatusCode.OK)
            {
                if (read.Status == HttpStatusCode.Conflict)
                {
                    _notice(false, "The web chat is answering: what it adds comes in at the next turn.");
                }
                else
                {
                    Refused(read);
                }
                return [];
            }
            var taken = new List<JsonObject>();
            lock (_gate)
            {
                foreach (var m in (read.Body?["messages"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    // One of this session's own, sent before its count was written here (a lost answer, a stop): not taken in twice.
                    if (Own(m.Str("ref")) is { } index)
                    {
                        _pushed = Math.Max(_pushed, Math.Min(index + 1, _local.Count));
                        continue;
                    }
                    var message = In(m);
                    _store.Remote(message);
                    taken.Add(message);
                }
                _server = (int)(read.Body?.Long("count") ?? after + taken.Count);
                _behind = false;
                _store.Sync(_conversation, _server, _pushed);
            }
            Wake();
            return taken;
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>The place in this session of a message it sent (its ref, session:index); null for any other.</summary>
    private int? Own(string? reference) =>
        reference is { } r && r.StartsWith(_store.Id + ":", StringComparison.Ordinal) && int.TryParse(r.AsSpan(_store.Id.Length + 1), out var index) ? index : null;

    /// <summary>The person's recent chats in Arena, newest first, to continue one here.</summary>
    public static async Task<List<WebChat>> ListAsync(Config config, HttpClient http, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, config.Url!.TrimEnd('/') + "/api/code-arena/chats?limit=30", config.ApiKey!, null);
        using var res = await http.SendAsync(req, ct);
        var body = Json.ParseObject(await res.Content.ReadAsStringAsync(ct));
        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException(body?.Str("error") ?? $"Arena answered HTTP {(int)res.StatusCode}");
        }
        return [.. (body?["chats"] as JsonArray ?? []).OfType<JsonObject>().Select(c => new WebChat(
            c.Str("id") ?? "", c.Str("title") ?? "", c.Str("origin"), c.Str("originPlace"),
            DateTimeOffset.TryParse(c.Str("updatedAt"), out var at) ? at : DateTimeOffset.MinValue, (int)(c.Long("messages") ?? 0)))];
    }

    /// <summary>Waits a little for what is still being sent (the session is closing).</summary>
    public async Task FlushAsync(TimeSpan most)
    {
        var until = DateTime.UtcNow + most;
        while (DateTime.UtcNow < until)
        {
            lock (_gate)
            {
                if (_unlinked || _behind || _pushed >= _local.Count || _problem is not null)
                {
                    return;
                }
            }
            await Task.Delay(100);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _store.Added -= Added;
        await FlushAsync(TimeSpan.FromSeconds(5));
        await _stop.CancelAsync();
        try
        {
            await _loop;
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

    private async Task<(HttpStatusCode Status, JsonObject? Body)> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var req = Request(method, _base + path, _key, body);
        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
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
