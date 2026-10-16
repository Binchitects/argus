using System.Text;
using System.Text.Json.Nodes;

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
    private Problems _problems = null!;
    private IdeGit _git = null!;
    private UserFiles _userFiles = null!;
    private WorkspaceState _state = null!;
    private Formatters _formatters = null!;
    private Tasks _tasks = null!;
    private RunConfigs _runs = null!;
    private EditorExtensions _extensions = null!;

    /// <summary>The one call whose body is not JSON: a VS Code extension's .vsix, sent whole.</summary>
    private const string InstallPath = "/api/extensions/install";

    private void StartIde()
    {
        _files = new IdeFiles(_rt.Workspace);
        _changes = new AgentChanges(_rt.Workspace);
        _terminals = new Terminals(_rt.Workspace, _rt.Config, _rt.Env.Env);
        _state = new WorkspaceState(_rt.Env.Paths.DataDir, _rt.Workspace.Root);
        _problems = new Problems(_rt.Workspace, _rt.Config, _state);
        _git = new IdeGit(_rt.Workspace);
        _preferences = new PagePreferences(Path.Combine(_rt.Env.Paths.DataDir, PagePreferences.FileName));
        _userFiles = new UserFiles(_rt.Env.Paths.ConfigDir);
        _formatters = new Formatters(_files, _rt.Config, _state, () => _rt.Sandbox, _rt.Env.Env);
        _tasks = new Tasks(_rt.Workspace, _files, _terminals, _problems, _state, _rt.Env.Env);
        _runs = new RunConfigs(_rt.Workspace, _terminals, _tasks, _state, _rt.Env.Env);
        _extensions = new EditorExtensions(_rt.Env.Paths.DataDir, _rt.Env.Env);
        StartDebug();
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
                case ("GET", "/api/git/status"):
                    await res.JsonAsync(200, await _git.StatusAsync(ct), ct);
                    return true;
                case ("GET", "/api/git/log"):
                    await res.JsonAsync(200, await _git.LogAsync(GitFile(req), Query(req, "limit"), ct), ct);
                    return true;
                case ("GET", "/api/git/show"):
                    await res.JsonAsync(200, await _git.ShowAsync(GitFile(req), Query(req, "rev"), ct), ct);
                    return true;
                case ("GET", "/api/git/commit"):
                    await res.JsonAsync(200, await _git.CommitFilesAsync(Query(req, "hash"), ct), ct);
                    return true;
                case ("GET", "/api/git/branches"):
                    await res.JsonAsync(200, await _git.BranchesAsync(ct), ct);
                    return true;
                case ("POST", "/api/git/stage"):
                    await _git.StageAsync(Body(req), ct);
                    await res.JsonAsync(200, await _git.StatusAsync(ct), ct);
                    return true;
                case ("POST", "/api/git/stage-text"):
                {
                    var body = Body(req);
                    if (body["text"] is not JsonValue value || !value.TryGetValue<string>(out var text))
                    {
                        throw new IdeError(400, "invalid", "Send the text to stage.");
                    }
                    await _git.StageTextAsync(body.Str("path") is { Length: > 0 } path ? _files.Resolve(path) : null, text, ct);
                    await res.JsonAsync(200, await _git.StatusAsync(ct), ct);
                    return true;
                }
                case ("POST", "/api/git/unstage"):
                    await _git.UnstageAsync(Body(req), ct);
                    await res.JsonAsync(200, await _git.StatusAsync(ct), ct);
                    return true;
                case ("POST", "/api/git/discard"):
                    await _git.DiscardAsync(Body(req), ct);
                    await res.JsonAsync(200, await _git.StatusAsync(ct), ct);
                    return true;
                case ("POST", "/api/git/commit"):
                    var hash = await _git.CommitAsync(Body(req), ct);
                    await res.JsonAsync(200, new JsonObject { ["hash"] = hash, ["status"] = await _git.StatusAsync(ct) }, ct);
                    return true;
                case ("POST", "/api/git/switch"):
                    await _git.SwitchAsync(Body(req), ct);
                    await res.JsonAsync(200, await _git.StatusAsync(ct), ct);
                    return true;
                case ("GET", "/api/problems"):
                    await res.JsonAsync(200, ProblemsJson(_problems.Last), ct);
                    return true;
                case ("POST", "/api/problems/run"):
                    await res.JsonAsync(200, ProblemsJson(await _problems.RunAsync()), ct);
                    return true;
                case ("GET", "/api/formatters"):
                    await res.JsonAsync(200, await Task.Run(() => _formatters.List(Query(req, "path"), Query(req, "language")), ct), ct);
                    return true;
                case ("POST", "/api/format"):
                    await FormatAsync(Body(req), res, ct);
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
                case ("POST", "/api/replace"):
                {
                    var body = Body(req);
                    var q = new SearchQuery(body.Str("q") ?? "", body.Bool("regex") == true, body.Bool("case") == true, body.Bool("word") == true, body.Str("include"), body.Str("exclude"));
                    var paths = body["paths"] is JsonArray only ? only.Select(p => p is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToHashSet(StringComparer.Ordinal) : null;
                    var replacement = body.Str("replacement") ?? "";
                    await res.JsonAsync(200, await Task.Run(() => _files.Replace(q, replacement, paths, ct), ct), ct);
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
                    var body = Body(req);
                    var path = body.Str("path");
                    var before = body.Str("before");
                    if (before is null && body["before"] is not null)
                    {
                        // Not taken as no before at all: that would accept the whole change, which could no longer be reverted.
                        throw new IdeError(400, "invalid", "before is the file's text with the part accepted.");
                    }
                    if (before is not null && path is null)
                    {
                        throw new IdeError(400, "invalid", "Say which file's change is accepted in part.");
                    }
                    _changes.Accept(path is null ? null : _files.Resolve(path), before);
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
                    var start = body["program"] is null ? null : DebuggeeStart(body);
                    var terminal = await Task.Run(() => _terminals.Open(body.Int("cols") ?? 80, body.Int("rows") ?? 24, start), ct);
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
                case ("GET", "/api/user-files"):
                    await res.JsonAsync(200, _userFiles.Read(Query(req, "name")), ct);
                    return true;
                case ("POST", "/api/user-files"):
                {
                    var body = Body(req);
                    if (body["text"] is not JsonValue value || !value.TryGetValue<string>(out var text))
                    {
                        throw new IdeError(400, "invalid", "Send the file's text.");
                    }
                    await res.JsonAsync(200, _userFiles.Write(body.Str("name"), text, body.Str("version")), ct);
                    return true;
                }
                case ("GET", "/api/user-files/vscode"):
                    await res.JsonAsync(200, _userFiles.FromVsCode(Query(req, "name"), _rt.Env.Env), ct);
                    return true;
                case ("GET", "/api/workspace/state"):
                    await res.JsonAsync(200, _state.Read(), ct);
                    return true;
                case ("POST", "/api/workspace/state"):
                    await res.JsonAsync(200, _state.Merge(Body(req)), ct);
                    return true;
                case ("POST", "/api/workspace/trust"):
                    await res.JsonAsync(200, _state.SetTrust(Body(req).Bool("trusted")), ct);
                    return true;
                case ("GET", "/api/tasks"):
                    await res.JsonAsync(200, await Task.Run(_tasks.List, ct), ct);
                    return true;
                case ("POST", "/api/tasks/run"):
                {
                    var body = Body(req);
                    await res.JsonAsync(200, await Task.Run(() => _tasks.RunAsync(body), ct), ct);
                    return true;
                }
                case ("POST", "/api/tasks/stop"):
                    await res.JsonAsync(200, await _tasks.StopAsync(Body(req)), ct);
                    return true;
                case ("GET", "/api/run/configs"):
                    await res.JsonAsync(200, await Task.Run(_runs.List, ct), ct);
                    return true;
                case ("POST", "/api/run/start"):
                {
                    var body = Body(req);
                    if (body.Bool("noDebug") == true)
                    {
                        await res.JsonAsync(200, await Task.Run(() => _runs.StartAsync(body), ct), ct);
                    }
                    else
                    {
                        // With its debugger: the configuration made ready here, then a debug session as /api/debug/start starts one.
                        var configuration = await Task.Run(() => _runs.DebugConfigurationAsync(body), ct);
                        await DebugStartAsync(new JsonObject { ["configuration"] = configuration }, res, ct);
                    }
                    return true;
                }
                case ("POST", "/api/run/stop"):
                    await res.JsonAsync(200, await _runs.StopAsync(Body(req)), ct);
                    return true;
                case ("POST", "/api/debug/start"):
                    await DebugStartAsync(Body(req), res, ct);
                    return true;
                case ("GET", "/api/debug/sessions"):
                    await res.JsonAsync(200, new JsonArray([.. _debug.All.Select(s => (JsonNode)s.Json())]), ct);
                    return true;
                case ("GET", "/api/debug/socket"):
                    await DebugSocketAsync(req, res, ct);
                    return true;
                case ("POST", "/api/debug/stop"):
                    if (!await _debug.StopAsync(Body(req).Str("id")))
                    {
                        throw new IdeError(404, "not_found", "There is no such debug session.");
                    }
                    await res.NoContentAsync(ct);
                    return true;
                case ("GET", "/api/debug/source"):
                    await res.JsonAsync(200, DebugSource(Query(req, "id"), Query(req, "path")), ct);
                    return true;
                case ("GET", "/api/debug/adapters"):
                    await res.JsonAsync(200, await _adapters.ListAsync(ct), ct);
                    return true;
                case ("GET", "/api/extensions"):
                    await res.JsonAsync(200, _extensions.List(), ct);
                    return true;
                case ("GET", "/api/extensions/contributions"):
                    await res.JsonAsync(200, _extensions.Contributions(), ct);
                    return true;
                case ("GET", "/api/extensions/file"):
                {
                    var (bytes, type) = _extensions.File(Query(req, "id"), Query(req, "path"));
                    // Never a page of this origin's: an SVG opened by its address runs nothing, and is never read as another type.
                    res.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
                    res.Headers["X-Content-Type-Options"] = "nosniff";
                    // The page asks with the version in the address: an icon theme's icons are read once, not at each row drawn.
                    res.Headers["Cache-Control"] = "private, max-age=3600";
                    await res.SendAsync(200, bytes, type, ct);
                    return true;
                }
                case ("POST", InstallPath):
                    await res.JsonAsync(200, await Task.Run(() => _extensions.Install(req.Body), ct), ct);
                    return true;
                case ("POST", "/api/extensions/import"):
                {
                    var id = Body(req).Str("id");
                    await res.JsonAsync(200, await Task.Run(() => _extensions.Import(id), ct), ct);
                    return true;
                }
                case ("POST", "/api/extensions/uninstall"):
                    _extensions.Uninstall(Body(req).Str("id"));
                    await res.NoContentAsync(ct);
                    return true;
                case ("POST", "/api/extensions/enable"):
                {
                    var body = Body(req);
                    await res.JsonAsync(200, _extensions.Enable(body.Str("id"), body.Bool("enabled")), ct);
                    return true;
                }
                case ("GET", "/api/extensions/local"):
                    await res.JsonAsync(200, await Task.Run(_extensions.Local, ct), ct);
                    return true;
                case ("GET", "/api/extensions/recommended"):
                    await res.JsonAsync(200, await Task.Run(() => _extensions.Recommended(_files), ct), ct);
                    return true;
            }
        }
        catch (IdeError e) when (!res.Started)
        {
            await res.JsonAsync(e.Status, e.Json(), ct);
            return true;
        }
        catch (Exception e) when (!res.Started && e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            await res.ErrorAsync(e is UnauthorizedAccessException ? 403 : 409, "failed", e.Message, ct);
            return true;
        }
        return false;
    }

    /// <summary>
    /// A debug adapter's runInTerminal, as the page passes it on ({program, args, cwd, env, title}): the program in a
    /// terminal of its own (a debuggee's), in the folder or one inside it, without ARENA_API_KEY.
    /// </summary>
    private TerminalStart DebuggeeStart(JsonObject body)
    {
        var program = body.Str("program") is { Length: > 0 } p ? p : throw new IdeError(400, "invalid", "Say which program to run.");
        var args = body["args"] is JsonArray list ? list.Select(a => a is JsonValue v && v.TryGetValue<string>(out var s) ? s : a?.ToJsonString() ?? "").ToList() : [];
        string? cwd = null;
        if (body.Str("cwd") is { Length: > 0 } dir)
        {
            var full = Path.GetFullPath(dir, _rt.Workspace.Root);
            cwd = _rt.Workspace.Inside(full) && Directory.Exists(full) ? full : throw new IdeError(403, "outside", "A debuggee runs in the folder, or in a folder inside it.");
        }
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in body["env"] as JsonObject ?? [])
        {
            if (!string.Equals(key, "ARENA_API_KEY", StringComparison.OrdinalIgnoreCase))
            {
                env[key] = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            }
        }
        return new TerminalStart(program, args, cwd, env, body.Str("title") ?? Path.GetFileName(program), TerminalKind.Debuggee);
    }

    private static string? Query(HttpRequest req, string name) => req.Query.GetValueOrDefault(name);

    private static bool Flag(HttpRequest req, string name) => Query(req, name) is "1" or "true";

    /// <summary>The file a git call names (<c>path</c>), checked as every path of the page's is (403 outside the folder); null when none is named.</summary>
    private string? GitFile(HttpRequest req) => Query(req, "path") is { Length: > 0 } path ? _files.Resolve(path) : null;

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
        try
        {
            await SocketRelay.RunAsync(
                socket,
                backlog.Length > 0 ? [new SocketMessage(backlog, Text: false)] : [],
                frames.Reader,
                frame => frame.Output is { } output ? new SocketMessage(output, Text: false)
                    : frame.Exit is { } code ? SocketMessage.Of(new JsonObject { ["type"] = "exit", ["code"] = code })
                    : null,
                (message, _) =>
                {
                    Keys(terminal, message);
                    return ValueTask.CompletedTask;
                },
                MaxTerminalMessage,
                "The shell ended.",
                ct);
        }
        finally
        {
            terminal.Detach(frames);
        }
    }

    /// <summary>What the page sent a terminal: keys as bytes, or {"type":"input"} and {"type":"resize"} as text.</summary>
    private static void Keys(Terminal terminal, SocketMessage message)
    {
        if (!message.Text)
        {
            terminal.Input(message.Data);
            return;
        }
        var json = Json.ParseObject(Encoding.UTF8.GetString(message.Data));
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

    /// <summary>The check's last run as the page shows it: its command (or the one it would run), how it ended, and the problems, then the tasks'.</summary>
    private JsonObject ProblemsJson(CheckRun? run) => new()
    {
        ["command"] = run?.Command ?? _problems.Command(),
        ["running"] = _problems.Running,
        ["ran"] = run?.Started.ToString("o"),
        ["exitCode"] = run?.ExitCode,
        ["said"] = run?.Said,
        ["problems"] = new JsonArray([.. (run?.Found ?? []).Concat(_problems.FromTasks()).Select(p => (JsonNode)new JsonObject
        {
            ["path"] = p.Path, ["line"] = p.Line, ["column"] = p.Column, ["severity"] = p.Severity, ["code"] = p.Code, ["message"] = p.Message, ["source"] = p.Source,
        })]),
    };

    /// <summary>
    /// The editor's text through a formatter: {path, text, formatter, range: {start, end}, options, language} to {text,
    /// formatter, source}. A formatter that failed is 422 <c>format_failed</c> with the line and column it named, when it did.
    /// </summary>
    private async Task FormatAsync(JsonObject body, HttpResponse res, CancellationToken ct)
    {
        if (body["text"] is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            throw new IdeError(400, "invalid", "Send the text to format.");
        }
        (int, int)? range = body["range"] is JsonObject r ? (r.Int("start") ?? -1, r.Int("end") ?? -1) : null;
        try
        {
            var (formatted, formatter) = await _formatters.FormatAsync(body.Str("path"), text, body.Str("formatter"), range, body["options"] as JsonObject, body.Str("language"), ct);
            await res.JsonAsync(200, new JsonObject { ["text"] = formatted, ["formatter"] = formatter.Id, ["source"] = formatter.Source }, ct);
        }
        catch (FormatFailed e)
        {
            await res.JsonAsync(422, new JsonObject { ["status"] = "format_failed", ["error"] = e.Message, ["line"] = e.Line, ["column"] = e.Column }, ct);
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
