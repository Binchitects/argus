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
    private TurnFiles _turnFiles = null!;
    private Terminals _terminals = null!;
    private PagePreferences _preferences = null!;
    private Problems _problems = null!;
    private IdeGit _git = null!;
    private UserFiles _userFiles = null!;
    private Backups _backups = null!;
    private WorkspaceState _state = null!;
    private Formatters _formatters = null!;
    private Tasks _tasks = null!;
    private RunConfigs _runs = null!;
    private EditorExtensions _extensions = null!;
    private Reviews _reviews = null!;
    private SecurityProblems _security = null!;
    private DiskWatch _watch = null!;
    private NotebookKernels _kernels = null!;

    private readonly TaskCompletionSource<string> _folderOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether the page may open another folder here (not served for Code Studio, nor connected to it).</summary>
    public bool FoldersOpen { get; set; }

    /// <summary>The folder the page asked to open (Open Folder): the IDE is then started again on it.</summary>
    public Task<string> FolderOpened => _folderOpened.Task;

    /// <summary>The folders added to this workspace (Add Folder), as the page shows them: whole paths, / between parts.</summary>
    private JsonArray AddedJson() => new([.. Folders.Added(_state).Select(f => (JsonNode)_rt.Workspace.Show(f))]);

    /// <summary>A call whose body is not JSON: a VS Code extension's .vsix, sent whole.</summary>
    private const string InstallPath = "/api/extensions/install";

    /// <summary>The other: a file dropped from the computer onto the Explorer, its bytes as they are.</summary>
    private const string UploadPath = "/api/files/upload";

    private void StartIde()
    {
        // The project's skills too, when the folder is inside the repository (they are at its root).
        // Search goes through the folders added to the workspace too, as VS Code's does in a multi-root workspace.
        _files = new IdeFiles(_rt.Workspace) { AlsoEdits = full => IdeSkills.ProjectSkillFile(_rt, full), SearchAlso = () => [.. Folders.Added(_state).Where(_rt.Workspace.Allowed)] };
        _changes = new AgentChanges(_rt.Workspace);
        _turnFiles = new TurnFiles(_rt.Workspace);
        _terminals = new Terminals(_rt.Workspace, _rt.Config, _rt.Env.Env);
        _state = new WorkspaceState(_rt.Env.Paths.DataDir, _rt.Workspace.Root);
        _problems = new Problems(_rt.Workspace, _rt.Config, _state);
        _git = new IdeGit(_rt.Workspace);
        _preferences = new PagePreferences(Path.Combine(_rt.Env.Paths.DataDir, PagePreferences.FileName));
        _userFiles = new UserFiles(_rt.Env.Paths.ConfigDir);
        _backups = new Backups(_rt.Env.Paths.DataDir, _rt.Workspace.Root, path => _files.EditedEntry(path));
        _formatters = new Formatters(_files, _rt.Config, _state, () => _rt.Sandbox, _rt.Env.Env);
        _tasks = new Tasks(_rt.Workspace, _files, _terminals, _problems, _state, _rt.Env.Env);
        _runs = new RunConfigs(_rt.Workspace, _terminals, _tasks, _state, _rt.Env.Env);
        _extensions = new EditorExtensions(_rt.Env.Paths.DataDir, _rt.Env.Env);
        _reviews = new Reviews(_rt, _problems);
        _security = new SecurityProblems(_rt, _problems);
        _watch = new DiskWatch(_rt.Workspace.Root);
        _kernels = new NotebookKernels(_rt.Workspace, _rt.Config, _state, _rt.Env.Env, _rt.Env.Paths.DataDir);
        if (!_served)
        {
            Folders.Remember(_rt.Env.Paths.DataDir, _rt.Workspace.Root);
        }
        StartDebug();
        StartLsp();
    }

    /// <summary>One of the IDE's calls; false when the path is none of them.</summary>
    private async Task<bool> IdeAsync(HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        try
        {
            if (await AccountAsync(req, res, ct) || await WorkspaceFileAsync(req, res, ct))
            {
                return true;
            }
            switch (req.Method, req.Path)
            {
                case ("GET", "/api/files"):
                    await res.JsonAsync(200, _files.List(Query(req, "path")), ct);
                    return true;
                case ("GET", "/api/folders"):
                {
                    // A folder's folders for the picker (the one open now's parent first), with the recent ones and the folders
                    // added to this workspace; its files' names too for @ in the chat.
                    var listed = Folders.List(Folders.Resolve(Query(req, "path") is { Length: > 0 } at ? at : Path.GetDirectoryName(_rt.Workspace.Root) ?? _rt.Workspace.Root,
                        _rt.Workspace.Root, _rt.Env.Env), _rt.Workspace.Root, _rt.Env.Paths.DataDir, _rt.Env.Env, Query(req, "files") == "1");
                    listed["added"] = AddedJson();
                    await res.JsonAsync(200, listed, ct);
                    return true;
                }
                case ("POST", "/api/folders/add"):
                {
                    // Add Folder to Workspace: the tools may use it from now on, the Explorer shows it, and it stays added here.
                    var folder = Folders.Resolve(Body(req).Str("path"), _rt.Workspace.Root, _rt.Env.Env);
                    if (_rt.Workspace.Allowed(folder) && _rt.Workspace.InRoot(folder))
                    {
                        throw new IdeError(409, "inside", $"{folder} is in the folder open now already.");
                    }
                    _rt.Workspace.Allow(folder);
                    Folders.SetAdded(_state, Folders.Added(_state).Where(f => !string.Equals(f, folder, StringComparison.Ordinal)).Append(folder));
                    await res.JsonAsync(200, new JsonObject { ["folder"] = folder, ["added"] = AddedJson() }, ct);
                    return true;
                }
                case ("POST", "/api/folders/remove"):
                {
                    var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Body(req).Str("path") ?? throw new IdeError(400, "invalid", "Name the folder.")));
                    var kept = Folders.Added(_state);
                    if (!kept.Remove(folder))
                    {
                        throw new IdeError(404, "not_added", $"{folder} was not added to this workspace.");
                    }
                    _rt.Workspace.Disallow(folder);
                    Folders.SetAdded(_state, kept);
                    await res.JsonAsync(200, new JsonObject { ["added"] = AddedJson() }, ct);
                    return true;
                }
                case ("POST", "/api/folders/open"):
                {
                    if (!FoldersOpen)
                    {
                        throw new IdeError(409, "not_here", "This workspace is Code Studio's: open another folder there.");
                    }
                    var folder = Folders.Resolve(Body(req).Str("path"), _rt.Workspace.Root, _rt.Env.Env);
                    await res.JsonAsync(200, new JsonObject { ["folder"] = folder }, ct);
                    // Answered first: the IDE then stops here and starts again on it, on this port with this key.
                    _folderOpened.TrySetResult(folder);
                    return true;
                }
                case ("GET", "/api/fs/changes"):
                    // What changed on disk since the version the page has: answered at once, or within 25 seconds of waiting.
                    await res.JsonAsync(200, await _watch.SinceAsync(long.TryParse(Query(req, "since"), out var since) ? since : 0, TimeSpan.FromSeconds(25), ct), ct);
                    return true;
                case ("POST", "/api/complete"):
                    await CompleteAsync(Body(req), res, ct);
                    return true;
                case ("POST", "/api/git/repositories"):
                {
                    // Source Control's repositories, as git.autoRepositoryDetection, git.repositoryScanMaxDepth and
                    // git.repositoryScanIgnoredFolders say (the page sends them, and the files open in its editor).
                    var body = Body(req);
                    var detect = body["detect"] is JsonValue v && v.TryGetValue<bool>(out var on) ? (on ? "true" : "false") : body.Str("detect") ?? "true";
                    var depth = Math.Clamp(body.Int("depth") ?? 1, -1, 10);
                    var ignored = Strings(body["ignored"]).ToHashSet(StringComparer.Ordinal);
                    var open = detect is "true" or "openEditors"
                        ? Strings(body["open"]).Take(200).Select(p =>
                        {
                            try
                            {
                                return _files.Resolve(p);
                            }
                            catch (IdeError)
                            {
                                return null;
                            }
                        }).OfType<string>().ToList()
                        : [];
                    // -1, as VS Code's: no limit; here 10 levels at most.
                    var list = await IdeGit.RepositoriesAsync(_rt.Workspace, GitRoots(), detect is "true" or "subFolders", depth < 0 ? 10 : depth, ignored, open, ct);
                    await res.JsonAsync(200, new JsonObject { ["repositories"] = list }, ct);
                    return true;
                }
                case ("GET", "/api/git/status"):
                    await res.JsonAsync(200, await GitOf(Query(req, "repo")).StatusAsync(ct), ct);
                    return true;
                case ("GET", "/api/git/log"):
                {
                    var file = GitFile(req);
                    await res.JsonAsync(200, await (await GitForAsync(Query(req, "repo"), file, ct)).LogAsync(file, Query(req, "limit"), ct), ct);
                    return true;
                }
                case ("GET", "/api/git/show"):
                {
                    var file = GitFile(req);
                    await res.JsonAsync(200, await (await GitForAsync(Query(req, "repo"), file, ct)).ShowAsync(file, Query(req, "rev"), ct), ct);
                    return true;
                }
                case ("GET", "/api/git/merge"):
                {
                    // The merge editor's file: unmerged or not, what stopped on it, and its two sides' names and commits.
                    var file = GitFile(req) ?? throw new IdeError(400, "invalid", "Say which file.");
                    await res.JsonAsync(200, await (await GitForAsync(Query(req, "repo"), file, ct)).MergeAsync(file, ct), ct);
                    return true;
                }
                case ("POST", "/api/git/merge/complete"):
                {
                    var body = Body(req);
                    var file = body.Str("path") is { Length: > 0 } path ? _files.Resolve(path) : throw new IdeError(400, "invalid", "Say which file.");
                    var git = await GitForAsync(body.Str("repo"), file, ct);
                    await git.CompleteMergeAsync(file, body.Bool("anyway") == true, ct);
                    await res.JsonAsync(200, await git.StatusAsync(ct), ct);
                    return true;
                }
                case ("GET", "/api/git/commit"):
                    await res.JsonAsync(200, await GitOf(Query(req, "repo")).CommitFilesAsync(Query(req, "hash"), ct), ct);
                    return true;
                case ("GET", "/api/git/branches"):
                    await res.JsonAsync(200, await GitOf(Query(req, "repo")).BranchesAsync(ct, Flag(req, "all")), ct);
                    return true;
                case ("POST", "/api/git/stage"):
                {
                    var git = GitOf(Body(req).Str("repo"));
                    await git.StageAsync(Body(req), ct);
                    await res.JsonAsync(200, await git.StatusAsync(ct), ct);
                    return true;
                }
                case ("POST", "/api/git/stage-text"):
                {
                    var body = Body(req);
                    if (body["text"] is not JsonValue value || !value.TryGetValue<string>(out var text))
                    {
                        throw new IdeError(400, "invalid", "Send the text to stage.");
                    }
                    var file = body.Str("path") is { Length: > 0 } path ? _files.Resolve(path) : null;
                    var git = await GitForAsync(body.Str("repo"), file, ct);
                    await git.StageTextAsync(file, text, ct);
                    await res.JsonAsync(200, await git.StatusAsync(ct), ct);
                    return true;
                }
                case ("POST", "/api/git/unstage"):
                {
                    var git = GitOf(Body(req).Str("repo"));
                    await git.UnstageAsync(Body(req), ct);
                    await res.JsonAsync(200, await git.StatusAsync(ct), ct);
                    return true;
                }
                case ("POST", "/api/git/discard"):
                {
                    var git = GitOf(Body(req).Str("repo"));
                    await git.DiscardAsync(Body(req), ct);
                    await res.JsonAsync(200, await git.StatusAsync(ct), ct);
                    return true;
                }
                case ("POST", "/api/git/commit"):
                {
                    var git = GitOf(Body(req).Str("repo"));
                    var hash = await git.CommitAsync(Body(req), ct);
                    await res.JsonAsync(200, new JsonObject { ["hash"] = hash, ["status"] = await git.StatusAsync(ct) }, ct);
                    return true;
                }
                case ("POST", "/api/git/switch"):
                {
                    var git = GitOf(Body(req).Str("repo"));
                    await git.SwitchAsync(Body(req), ct);
                    await res.JsonAsync(200, await git.StatusAsync(ct), ct);
                    return true;
                }
                case ("POST", "/api/git/fetch"):
                case ("POST", "/api/git/pull"):
                case ("POST", "/api/git/push"):
                case ("POST", "/api/git/sync"):
                case ("POST", "/api/git/branch/create"):
                case ("POST", "/api/git/branch/rename"):
                case ("POST", "/api/git/branch/delete"):
                case ("POST", "/api/git/undo-commit"):
                {
                    var body = Body(req);
                    var git = GitOf(body.Str("repo"));
                    await (req.Path switch
                    {
                        "/api/git/fetch" => git.FetchAsync(ct),
                        "/api/git/pull" => git.PullAsync(body, ct),
                        "/api/git/push" => git.PushAsync(body, ct),
                        "/api/git/sync" => git.SyncAsync(body, ct),
                        "/api/git/branch/create" => git.CreateBranchAsync(body, ct),
                        "/api/git/branch/rename" => git.RenameBranchAsync(body, ct),
                        "/api/git/branch/delete" => git.DeleteBranchAsync(body, ct),
                        _ => git.UndoCommitAsync(ct),
                    });
                    await res.JsonAsync(200, await git.StatusAsync(ct), ct);
                    return true;
                }
                case ("GET", "/api/problems"):
                    await res.JsonAsync(200, ProblemsJson(_problems.Last), ct);
                    return true;
                case ("POST", "/api/problems/run"):
                    await res.JsonAsync(200, ProblemsJson(await _problems.RunAsync()), ct);
                    return true;
                case ("GET", "/api/problems/security"):
                    await res.JsonAsync(200, await _security.CurrentAsync(ct), ct);
                    return true;
                case ("POST", "/api/problems/security"):
                    await res.JsonAsync(200, await _security.RefreshAsync(ct), ct);
                    return true;
                case ("GET", "/api/review"):
                    await res.JsonAsync(200, _reviews.Json(), ct);
                    return true;
                case ("POST", "/api/review"):
                    _reviews.Start(Body(req).Str("base"));
                    await res.JsonAsync(200, _reviews.Json(), ct);
                    return true;
                case ("POST", "/api/review/stop"):
                    _reviews.Stop();
                    await res.JsonAsync(200, _reviews.Json(), ct);
                    return true;
                case ("POST", "/api/review/finding"):
                {
                    var body = Body(req);
                    _reviews.Mark(body.Int("index") ?? -1, body.Str("state") ?? "");
                    await res.JsonAsync(200, _reviews.Json(), ct);
                    return true;
                }
                case ("GET", "/api/formatters"):
                    await res.JsonAsync(200, await Task.Run(() => _formatters.List(Query(req, "path"), Query(req, "language")), ct), ct);
                    return true;
                case ("POST", "/api/format"):
                    await FormatAsync(Body(req), res, ct);
                    return true;
                case ("GET", "/api/symbols"):
                {
                    // Go to Symbol: by name across the folder, or one file's, from the code index (made on the first ask).
                    var index = await _rt.IndexForIde().GetAsync(TimeSpan.FromSeconds(3), ct);
                    var path = Query(req, "path") is { Length: > 0 } p ? _rt.Workspace.Show(_rt.Workspace.Resolve(p)).Replace('\\', '/') : null;
                    var found = index is null ? [] : await Task.Run(() => index.Search().Symbols(Query(req, "q") ?? "", path, Math.Clamp(int.TryParse(Query(req, "limit"), out var n) ? n : 100, 1, 500)), ct);
                    var list = new JsonArray([.. found.Select(s => (JsonNode)new JsonObject
                    {
                        ["name"] = s.Name, ["kind"] = s.Kind, ["container"] = s.Container, ["path"] = s.Path, ["line"] = s.Line, ["end"] = s.End,
                    })]);
                    await res.JsonAsync(200, new JsonObject { ["symbols"] = list, ["building"] = index is null }, ct);
                    return true;
                }
                case ("POST", "/api/outline"):
                {
                    // The Outline: the symbols of the text the editor holds (unsaved edits too), or of the file on disk.
                    var body = Body(req);
                    var path = body.Str("path") ?? throw new ToolError("Which file?");
                    var text = body.Str("text") ?? _files.Read(path).Str("text") ?? "";
                    if (text.Length > CodeFiles.MostSymbolBytes)
                    {
                        await res.JsonAsync(200, new JsonObject { ["symbols"] = new JsonArray(), ["language"] = null }, ct);
                        return true;
                    }
                    var lang = CodeFiles.LanguageOf(path);
                    var symbols = new JsonArray([.. CodeIndex.Outline(lang, text).Select(s => (JsonNode)new JsonObject
                    {
                        ["name"] = s.Name, ["kind"] = s.Kind, ["qualified"] = s.Qualified, ["parent"] = s.Parent, ["start"] = s.Start, ["end"] = s.End, ["signature"] = s.Signature,
                    })]);
                    await res.JsonAsync(200, new JsonObject { ["symbols"] = symbols, ["language"] = lang }, ct);
                    return true;
                }
                case ("GET", "/api/files/all"):
                    await res.JsonAsync(200, await Task.Run(() => _files.All(ct), ct), ct);
                    return true;
                case ("GET", "/api/file"):
                    await res.JsonAsync(200, _files.Read(Query(req, "path")), ct);
                    return true;
                case ("GET", "/api/media"):
                {
                    // A picture, sound or video as it is, for an img, audio or video element of the page: shown, never run.
                    var (bytes, type) = _files.Media(Query(req, "path"));
                    res.Headers["X-Content-Type-Options"] = "nosniff";
                    res.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
                    res.Headers["Content-Disposition"] = "inline";
                    res.Headers["Cache-Control"] = "no-store";
                    await res.SendAsync(200, bytes, type, ct);
                    return true;
                }
                case ("POST", "/api/file"):
                {
                    var body = Body(req);
                    if (body["text"] is not JsonValue value || !value.TryGetValue<string>(out var text))
                    {
                        throw new IdeError(400, "invalid", "Send the file's text.");
                    }
                    PersonTouched(body.Str("path"));
                    await res.JsonAsync(200, _files.Write(body.Str("path"), text, body.Str("version")), ct);
                    return true;
                }
                case ("POST", "/api/files/new"):
                {
                    var body = Body(req);
                    PersonTouched(body.Str("path"));
                    await res.JsonAsync(200, _files.Create(body.Str("path"), body.Str("kind") == "dir"), ct);
                    return true;
                }
                case ("POST", "/api/files/rename"):
                {
                    var body = Body(req);
                    var (from, to) = _files.Rename(body.Str("from"), body.Str("to"));
                    _changes.Moved(from, to);
                    _turnFiles.PersonTouched(from);
                    _turnFiles.PersonTouched(to);
                    await res.JsonAsync(200, new JsonObject { ["from"] = _files.Show(from), ["to"] = _files.Show(to) }, ct);
                    return true;
                }
                case ("POST", "/api/files/copy"):
                {
                    var body = Body(req);
                    var (from, to) = await Task.Run(() => _files.Copy(body.Str("from"), body.Str("to")), ct);
                    _turnFiles.PersonTouched(to);
                    await res.JsonAsync(200, new JsonObject { ["from"] = _files.Show(from), ["to"] = _files.Show(to) }, ct);
                    return true;
                }
                case ("POST", UploadPath):
                    // The file's bytes as they are (Content-Type: application/octet-stream, which no form sends).
                    PersonTouched(Query(req, "path"));
                    await res.JsonAsync(200, await Task.Run(() => _files.Upload(Query(req, "path"), req.Body, Flag(req, "overwrite")), ct), ct);
                    return true;
                case ("POST", "/api/files/delete"):
                {
                    var gone = Body(req).Str("path");
                    PersonTouched(gone);
                    _files.Delete(gone);
                    await res.NoContentAsync(ct);
                    return true;
                }
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
                    // What commands the agent ran changed since, too (a background one may still be writing).
                    if (Busy() || _rt.Jobs.Running.Count > 0)
                    {
                        await Task.Run(() => FoundChanges(ct), ct);
                    }
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
                    // A shell in a folder of the folder's (Open in Integrated Terminal).
                    var shellIn = start is null && body.Str("cwd") is { Length: > 0 } dir ? FolderInside(dir, "A terminal opens in the folder, or in a folder inside it.") : null;
                    var terminal = await Task.Run(() => _terminals.Open(body.Int("cols") ?? 80, body.Int("rows") ?? 24, start, shellIn), ct);
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
                {
                    // One of the person's skills in Arena (none of that name on this computer): as Arena keeps it.
                    var name = Query(req, "name");
                    await res.JsonAsync(200, IdeSkills.InArena(_rt, name) is { } kept ? IdeSkills.ReadArena(name!, kept) : _userFiles.Read(name), ct);
                    return true;
                }
                case ("POST", "/api/user-files"):
                {
                    var body = Body(req);
                    if (body["text"] is not JsonValue value || !value.TryGetValue<string>(out var text))
                    {
                        throw new IdeError(400, "invalid", "Send the file's text.");
                    }
                    var name = body.Str("name");
                    await res.JsonAsync(200, IdeSkills.InArena(_rt, name) is not null
                        ? await IdeSkills.SaveArenaAsync(_rt, name!, text, body.Str("version"), ct)
                        : _userFiles.Write(name, text, body.Str("version")), ct);
                    return true;
                }
                case ("GET", "/api/notebook/kernel"):
                    // A notebook's kernel: the Python it runs, whether it runs or is busy, its count.
                    await res.JsonAsync(200, _kernels.Info(Notebook(Query(req, "path"))), ct);
                    return true;
                case ("POST", "/api/notebook/execute"):
                {
                    // A cell run in the notebook's kernel (started at the first); its outputs are read from /api/notebook/run.
                    var body = Body(req);
                    var notebook = Notebook(body.Str("path"));
                    var code = body["code"] is JsonValue v && v.TryGetValue<string>(out var c) ? c : throw new IdeError(400, "invalid", "Send the cell's code.");
                    await res.JsonAsync(200, new JsonObject { ["run"] = _kernels.Execute(notebook, code) }, ct);
                    return true;
                }
                case ("GET", "/api/notebook/run"):
                    await res.JsonAsync(200, await _kernels.OutputAsync(Query(req, "id"), int.TryParse(Query(req, "since"), out var seen) ? seen : 0, ct), ct);
                    return true;
                case ("POST", "/api/notebook/interrupt"):
                case ("POST", "/api/notebook/restart"):
                {
                    var notebook = Notebook(Body(req).Str("path"));
                    await (req.Path == "/api/notebook/interrupt" ? _kernels.InterruptAsync(notebook) : _kernels.StopAsync(notebook));
                    await res.JsonAsync(200, _kernels.Info(notebook), ct);
                    return true;
                }
                case ("GET", "/api/snippets"):
                    // The project's .vscode/*.code-snippets (the folder's and each added folder's) and the person's own.
                    await res.JsonAsync(200, IdeSnippets.List([_rt.Workspace.Root, .. Folders.Added(_state).Where(_rt.Workspace.Allowed)], _files.Show, _rt.Env.Paths.ConfigDir), ct);
                    return true;
                case ("GET", "/api/user-files/vscode"):
                    await res.JsonAsync(200, _userFiles.FromVsCode(Query(req, "name"), _rt.Env.Env), ct);
                    return true;
                case ("GET", "/api/skills"):
                    await res.JsonAsync(200, await IdeSkills.ListAsync(_rt, Query(req, "fresh") == "1", ct), ct);
                    return true;
                case ("POST", "/api/skills"):
                    await res.JsonAsync(200, await IdeSkills.CreateAsync(_rt, Body(req), ct), ct);
                    return true;
                case ("GET", "/api/tools/settings"):
                    await res.JsonAsync(200, ToolsApi.List(_rt), ct);
                    return true;
                case ("POST", "/api/tools/settings"):
                    await res.JsonAsync(200, ToolsApi.Save(_rt, Body(req)), ct);
                    return true;
                case ("POST", "/api/tools/enable"):
                    await res.JsonAsync(200, ToolsApi.Enable(_rt, Body(req)), ct);
                    return true;
                case ("GET", "/api/skills/export"):
                {
                    // Any skill the agent reads, as files: a .zip of their folders, or one alone as its SKILL.md.
                    var skills = await SkillTransfer.NamedAsync(_rt, (Query(req, "names") ?? "").Split(','), ct);
                    res.Headers["Cache-Control"] = "no-store";
                    if (Query(req, "format") == "md")
                    {
                        if (skills is not [var one] || SkillTransfer.FolderOf(one).Count > 1)
                        {
                            throw new IdeError(400, "invalid", "Only one skill with no other files goes out as its SKILL.md: export it as a .zip.");
                        }
                        res.Headers["Content-Disposition"] = "attachment; filename=\"SKILL.md\"";
                        await res.SendAsync(200, Encoding.UTF8.GetBytes(SkillTransfer.FolderOf(one)[SkillTransfer.SkillMd]), "text/markdown; charset=utf-8", ct);
                        return true;
                    }
                    res.Headers["Content-Disposition"] = $"attachment; filename=\"{(skills is [var only] ? only.Name : "skills")}.zip\"";
                    await res.SendAsync(200, SkillTransfer.Zip(skills), "application/zip", ct);
                    return true;
                }
                case ("POST", "/api/skills/import/preview"):
                {
                    var body = Body(req);
                    var (folders, leftOut) = SkillTransfer.Read(ImportUploads(body));
                    await res.JsonAsync(200, await SkillTransfer.PreviewAsync(_rt, folders, leftOut, body.Str("scope") ?? "project", ct), ct);
                    return true;
                }
                case ("POST", "/api/skills/import"):
                {
                    var body = Body(req);
                    var (folders, _) = SkillTransfer.Read(ImportUploads(body));
                    await res.JsonAsync(200, await SkillTransfer.ImportAsync(_rt, folders, body.Str("scope") ?? "project", body["choices"] as JsonArray ?? [], ct), ct);
                    return true;
                }
                case ("GET", "/api/processes"):
                    await res.JsonAsync(200, await Task.Run(OwnProcesses.List, ct), ct);
                    return true;
                case ("GET", "/api/backups"):
                    await res.JsonAsync(200, new JsonObject { ["backups"] = _backups.List() }, ct);
                    return true;
                case ("POST", "/api/backups"):
                {
                    var body = Body(req);
                    _backups.Keep(body.Str("path"), body.Str("text"), body.Str("version"));
                    await res.NoContentAsync(ct);
                    return true;
                }
                case ("GET", "/api/workspace/state"):
                    await res.JsonAsync(200, _state.Read(), ct);
                    return true;
                case ("POST", "/api/workspace/state"):
                    await res.JsonAsync(200, _state.Merge(Body(req)), ct);
                    return true;
                case ("POST", "/api/workspace/trust"):
                    await res.JsonAsync(200, _state.SetTrust(Body(req).Bool("trusted")), ct);
                    if (!_state.Trusted)
                    {
                        // Its language servers run its code: they stop with the trust.
                        _ = _lsp.StopAllAsync("The folder is no longer trusted.");
                    }
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
                case ("GET", "/api/lsp"):
                    await res.JsonAsync(200, await _lsp.ListAsync(ct), ct);
                    return true;
                case ("GET", "/api/lsp/socket"):
                    await LspSocketAsync(req, res, ct);
                    return true;
                case ("GET", "/api/lsp/log"):
                    await res.JsonAsync(200, _lsp.Log(Query(req, "server")), ct);
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
        var cwd = body.Str("cwd") is { Length: > 0 } dir ? FolderInside(dir, "A debuggee runs in the folder, or in a folder inside it.") : null;
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

    /// <summary>A folder of the folder's (or of one added to it), as a full path; 403 for any other, or one not there.</summary>
    private string FolderInside(string dir, string refusal)
    {
        var full = Path.GetFullPath(dir, _rt.Workspace.Root);
        return _rt.Workspace.Inside(full) && Directory.Exists(full) ? full : throw new IdeError(403, "outside", refusal);
    }

    private static string? Query(HttpRequest req, string name) => req.Query.GetValueOrDefault(name);

    /// <summary>A notebook of the folder's (or of one added to it), as a full path: a .ipynb file, checked as every path of the page's is.</summary>
    private string Notebook(string? path)
    {
        var full = _files.Resolve(path);
        return full.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase) ? full : throw new IdeError(400, "invalid", "Only a notebook (.ipynb) has a kernel.");
    }

    private static bool Flag(HttpRequest req, string name) => Query(req, name) is "1" or "true";

    /// <summary>The file a git call names (<c>path</c>), checked as every path of the page's is (403 outside the folder); null when none is named.</summary>
    private string? GitFile(HttpRequest req) => Query(req, "path") is { Length: > 0 } path ? _files.Resolve(path) : null;

    /// <summary>The strings of a JSON array (none when it is not one).</summary>
    private static IEnumerable<string> Strings(JsonNode? node) =>
        (node as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue<string>(out var x) ? x : null).OfType<string>().Where(x => x.Length > 0);

    /// <summary>The workspace's folders whose repositories Source Control shows: the folder open, then those added.</summary>
    private List<string> GitRoots() => [_rt.Workspace.Root, .. Folders.Added(_state).Where(_rt.Workspace.Allowed)];

    /// <summary>
    /// The repository a call names (<c>repo</c>: its folder as Source Control lists it), the folder's own when none is.
    /// It is a folder of the workspace's: the folder open, or one added.
    /// </summary>
    private IdeGit GitOf(string? repo)
    {
        if (string.IsNullOrEmpty(repo) || repo == ".")
        {
            return _git;
        }
        var full = _files.Resolve(repo);
        return Directory.Exists(full) ? new IdeGit(_rt.Workspace, full) : throw new IdeError(404, "no_repository", $"There is no folder {repo} for a repository.");
    }

    /// <summary>A file's repository when the call names none: the one it is in (a repository nested in the folder, or an added folder's).</summary>
    private async Task<IdeGit> GitForAsync(string? repo, string? file, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(repo) || file is null)
        {
            return GitOf(repo);
        }
        var dir = await IdeGit.RepositoryOfAsync(GitRoots(), file, ct);
        return dir is null || string.Equals(dir, _rt.Workspace.Root, StringComparison.Ordinal) ? _git : new IdeGit(_rt.Workspace, dir);
    }

    /// <summary>A path the person acts on in the IDE: its change is theirs, not the agent's.</summary>
    private void PersonTouched(string? path)
    {
        if (path is { Length: > 0 })
        {
            try
            {
                _turnFiles.PersonTouched(_files.Resolve(path));
            }
            catch (IdeError)
            {
                // Not a path of the folder: the call says so itself.
            }
        }
    }

    /// <summary>The agent's changes made by any means since the turn began (commands, code it ran): kept as its changes. The paths.</summary>
    private List<string> FoundChanges(CancellationToken ct)
    {
        var found = new List<string>();
        try
        {
            foreach (var (full, before, now) in _turnFiles.Changed(ct))
            {
                _changes.RecordFound(full, before, now);
                found.Add(_files.Show(full));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Looked at again at the next step.
        }
        return found;
    }

    private static JsonObject Body(HttpRequest req) => req.Json() ?? throw new IdeError(400, "invalid", "Send a JSON object.");

    /// <summary>The files an import sends: each its path (in the folder chosen) and its bytes in base64.</summary>
    private static List<(string Path, byte[] Bytes)> ImportUploads(JsonObject body)
    {
        var list = new List<(string, byte[])>();
        foreach (var u in body["uploads"] as JsonArray ?? [])
        {
            if (u.Str("path") is not { Length: > 0 } path || u.Str("data") is not { } data)
            {
                throw new IdeError(400, "invalid", "Each file of an import is { path, data } (its bytes in base64).");
            }
            try
            {
                list.Add((path, Convert.FromBase64String(data)));
            }
            catch (FormatException)
            {
                throw new IdeError(400, "invalid", $"{path}: its data is not base64.");
            }
        }
        return list;
    }

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
            ["ask"] = p.Ask,
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
