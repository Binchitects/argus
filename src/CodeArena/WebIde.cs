using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CodeArena;

/// <summary>
/// The IDE's calls, beside the chat's: the working directory's files (listed,
/// read, saved, made, renamed, deleted), text search, the agent's changes (to
/// diff, accept or revert) and the terminals, whose output and keys travel over
/// a WebSocket on the same server. All behind this run's key, as the chat is:
/// saving a file or typing in a terminal is as powerful as the person's own
/// shell.
/// </summary>
internal sealed partial class WebApp
{
    /// <summary>The most a terminal's page sends in one message.</summary>
    private const int MaxTerminalMessage = 1024 * 1024;

    private IdeFiles _files = null!;
    private AgentChanges _changes = null!;
    private Terminals _terminals = null!;
    private PagePreferences _preferences = null!;

    private void StartIde()
    {
        _files = new IdeFiles(_rt.Workspace);
        _changes = new AgentChanges(_rt.Workspace);
        _terminals = new Terminals(_rt.Workspace, _rt.Config, _rt.Env.Env);
        _preferences = new PagePreferences(Path.Combine(_rt.Env.Paths.DataDir, PagePreferences.FileName));
    }

    /// <summary>One of the IDE's calls; false when the path is none of them.</summary>
    private async Task<bool> IdeAsync(HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        try
        {
            switch (req.Method, req.Path)
            {
                case ("GET", "/api/files"):
                    await res.JsonAsync(200, _files.List(Query(req, "path")), ct);
                    return true;
                case ("POST", "/api/complete"):
                    await CompleteAsync(Body(req), res, ct);
                    return true;
                case ("GET", "/api/files/all"):
                    await res.JsonAsync(200, await Task.Run(() => _files.All(ct), ct), ct);
                    return true;
                case ("GET", "/api/file"):
                    await res.JsonAsync(200, _files.Read(Query(req, "path")), ct);
                    return true;
                case ("POST", "/api/file"):
                {
                    var body = Body(req);
                    if (body["text"] is not JsonValue value || !value.TryGetValue<string>(out var text))
                    {
                        throw new IdeError(400, "invalid", "Send the file's text.");
                    }
                    await res.JsonAsync(200, _files.Write(body.Str("path"), text, body.Str("version")), ct);
                    return true;
                }
                case ("POST", "/api/files/new"):
                {
                    var body = Body(req);
                    await res.JsonAsync(200, _files.Create(body.Str("path"), body.Str("kind") == "dir"), ct);
                    return true;
                }
                case ("POST", "/api/files/rename"):
                {
                    var body = Body(req);
                    var (from, to) = _files.Rename(body.Str("from"), body.Str("to"));
                    _changes.Moved(from, to);
                    await res.JsonAsync(200, new JsonObject { ["from"] = _files.Show(from), ["to"] = _files.Show(to) }, ct);
                    return true;
                }
                case ("POST", "/api/files/delete"):
                    _files.Delete(Body(req).Str("path"));
                    await res.NoContentAsync(ct);
                    return true;
                case ("GET", "/api/search"):
                {
                    var q = new SearchQuery(Query(req, "q") ?? "", Flag(req, "regex"), Flag(req, "case"), Flag(req, "word"), Query(req, "include"), Query(req, "exclude"));
                    await res.JsonAsync(200, await Task.Run(() => _files.Search(q, ct), ct), ct);
                    return true;
                }
                case ("GET", "/api/changes"):
                    await res.JsonAsync(200, _changes.List(), ct);
                    return true;
                case ("GET", "/api/changes/diff"):
                    await res.JsonAsync(200, _changes.Texts(_files.Resolve(Query(req, "path"))), ct);
                    return true;
                case ("POST", "/api/changes/accept"):
                {
                    var path = Body(req).Str("path");
                    _changes.Accept(path is null ? null : _files.Resolve(path));
                    await res.JsonAsync(200, _changes.List(), ct);
                    return true;
                }
                case ("POST", "/api/changes/revert"):
                {
                    var path = Body(req).Str("path") ?? throw new IdeError(400, "invalid", "Say which file to revert.");
                    _changes.Revert(_files.Resolve(path));
                    await res.JsonAsync(200, _changes.List(), ct);
                    return true;
                }
                case ("GET", "/api/terminals"):
                    await res.JsonAsync(200, new JsonArray([.. _terminals.All.Select(t => (JsonNode)t.Json())]), ct);
                    return true;
                case ("POST", "/api/terminals"):
                {
                    var body = Body(req);
                    var terminal = await Task.Run(() => _terminals.Open(body.Int("cols") ?? 80, body.Int("rows") ?? 24), ct);
                    await res.JsonAsync(200, terminal.Json(), ct);
                    return true;
                }
                case ("POST", "/api/terminals/close"):
                    if (!_terminals.Close(Body(req).Str("id")))
                    {
                        throw new IdeError(404, "not_found", "There is no such terminal.");
                    }
                    await res.NoContentAsync(ct);
                    return true;
                case ("GET", "/api/terminals/socket"):
                    await TerminalSocketAsync(req, res, ct);
                    return true;
                case ("GET", "/api/preferences"):
                    await res.JsonAsync(200, _preferences.Read(), ct);
                    return true;
                case ("POST", "/api/preferences"):
                    await res.JsonAsync(200, _preferences.Save(Body(req)), ct);
                    return true;
            }
        }
        catch (IdeError e) when (!res.Started)
        {
            await res.ErrorAsync(e.Status, e.Code, e.Message, ct);
            return true;
        }
        catch (Exception e) when (!res.Started && e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            await res.ErrorAsync(e is UnauthorizedAccessException ? 403 : 409, "failed", e.Message, ct);
            return true;
        }
        return false;
    }

    private static string? Query(HttpRequest req, string name) => req.Query.GetValueOrDefault(name);

    private static bool Flag(HttpRequest req, string name) => Query(req, name) is "1" or "true";

    private static JsonObject Body(HttpRequest req) => req.Json() ?? throw new IdeError(400, "invalid", "Send a JSON object.");

    /// <summary>
    /// A page attached to a terminal: first the output kept so far, then each
    /// output as it comes (binary messages) and {"type":"exit","code"} when the
    /// shell ends. From the page: {"type":"input","data"} and
    /// {"type":"resize","cols","rows"} (text), or keys as binary messages.
    /// </summary>
    private async Task TerminalSocketAsync(HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        if (!req.IsWebSocket)
        {
            throw new IdeError(400, "invalid", "This address is a WebSocket: connect to it with one.");
        }
        if (_terminals.Find(Query(req, "id")) is not { } terminal)
        {
            throw new IdeError(404, "not_found", "There is no such terminal (it was closed).");
        }
        using var socket = await res.AcceptWebSocketAsync(req, ct);
        var (backlog, frames) = terminal.Attach();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sending = SendFramesAsync(socket, backlog, frames, stop.Token);
        try
        {
            await ReceiveKeysAsync(socket, terminal, stop.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
        {
            // The page went away, or the server is stopping.
        }
        finally
        {
            terminal.Detach(frames);
            await stop.CancelAsync();
            await sending;
        }
        if (socket.State == WebSocketState.CloseReceived)
        {
            try
            {
                using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, bye.Token);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
            {
            }
        }
    }

    /// <summary>The only one sending on the socket: the backlog, the output, and at the end the exit and the close.</summary>
    private static async Task SendFramesAsync(WebSocket socket, byte[] backlog, Channel<TerminalFrame> frames, CancellationToken ct)
    {
        try
        {
            if (backlog.Length > 0)
            {
                await socket.SendAsync(backlog, WebSocketMessageType.Binary, true, ct);
            }
            await foreach (var frame in frames.Reader.ReadAllAsync(ct))
            {
                if (frame.Output is { } output)
                {
                    await socket.SendAsync(output, WebSocketMessageType.Binary, true, ct);
                }
                else if (frame.Exit is { } code)
                {
                    await socket.SendAsync(Encoding.UTF8.GetBytes(Json.Line(new JsonObject { ["type"] = "exit", ["code"] = code })), WebSocketMessageType.Text, true, ct);
                }
            }
            // The shell ended (the channel completes only then).
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "The shell ended.", ct);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The page went away first.
        }
    }

    private static async Task ReceiveKeysAsync(WebSocket socket, Terminal terminal, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var r = await socket.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
            if (message.Length + r.Count > MaxTerminalMessage)
            {
                return;
            }
            message.Write(buffer, 0, r.Count);
            if (!r.EndOfMessage)
            {
                continue;
            }
            var data = message.ToArray();
            message.SetLength(0);
            if (r.MessageType == WebSocketMessageType.Binary)
            {
                terminal.Input(data);
                continue;
            }
            var json = Json.ParseObject(Encoding.UTF8.GetString(data));
            switch (json.Str("type"))
            {
                case "input":
                    terminal.Input(Encoding.UTF8.GetBytes(json.Str("data") ?? ""));
                    break;
                case "resize":
                    terminal.Resize(json.Int("cols") ?? terminal.Cols, json.Int("rows") ?? terminal.Rows);
                    break;
            }
        }
    }

    private Completions? _completions;

    /// <summary>
    /// Code completion at the editor's cursor: {path, prefix, suffix} (the code before and after it) to {text}. 404
    /// <c>off</c> when the config turns it off; 409 <c>busy</c> while an answer is written (the key's requests at once are
    /// the agent's then); the gateway's refusals as 429 or 502, not tried again (the person types on).
    /// </summary>
    private async Task CompleteAsync(JsonObject body, HttpResponse res, CancellationToken ct)
    {
        if (!_rt.Config.Completion)
        {
            throw new IdeError(404, "off", "Code completion is off (\"completion\": false in config.json).");
        }
        if (Busy())
        {
            throw new IdeError(409, "busy", "The agent is working: code completion waits for it.");
        }
        var path = body.Str("path") ?? "";
        var prefix = body.Str("prefix") ?? "";
        var suffix = body.Str("suffix") ?? "";
        _completions ??= new Completions(_rt);
        try
        {
            await res.JsonAsync(200, new JsonObject { ["text"] = await _completions.CompleteAsync(path, prefix, suffix, ct) }, ct);
        }
        catch (GatewayException e)
        {
            throw new IdeError(e.Status == 429 ? 429 : 502, e.Status == 429 ? "limited" : "gateway", e.Message);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new IdeError(504, "slow", $"No completion: {Fmt.OneLine(e.Message, 200)}");
        }
    }
}

/// <summary>
/// The page's own preferences (the layout of its panels, the theme), kept in the
/// data folder: a browser keeps a page's storage per port, and each run takes a
/// free port, so the browser alone would forget them at every start. One file
/// for every project, as an editor keeps its layout; the page's keys merged into
/// it as it sends them (null forgets one), 16 KB at most.
/// </summary>
internal sealed class PagePreferences(string file)
{
    /// <summary>The file's name in the data folder.</summary>
    public const string FileName = "ide.json";

    /// <summary>The most the file holds.</summary>
    public const int MaxBytes = 16 * 1024;

    private readonly object _gate = new();

    /// <summary>What was saved; none when the file is missing or unreadable.</summary>
    public JsonObject Read()
    {
        lock (_gate)
        {
            return Load();
        }
    }

    /// <summary>The keys sent, over what was saved; the whole of it back.</summary>
    public JsonObject Save(JsonObject change)
    {
        lock (_gate)
        {
            var all = Load();
            foreach (var (key, value) in change)
            {
                if (value is null)
                {
                    all.Remove(key);
                }
                else
                {
                    all[key] = value.DeepClone();
                }
            }
            var text = all.ToJsonString();
            if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
            {
                throw new IdeError(413, "too_large", $"The page's preferences are kept up to {MaxBytes / 1024} KB.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            // Whole or not at all: another run of code-arena may read it meanwhile.
            var temp = $"{file}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, file, overwrite: true);
            return all;
        }
    }

    private JsonObject Load()
    {
        try
        {
            return File.Exists(file) ? Json.ParseObject(File.ReadAllText(file)) ?? [] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
