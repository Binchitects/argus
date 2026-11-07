using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// The IDE's multi-root workspace: a .code-workspace file opened (Open Workspace from File), made (Save Workspace As)
/// or closed, and read and written by the page (its folders as Add Folder changes them, its settings as the Settings
/// editor's Workspace tab sets them). Whenever it is read, its folders are what this run has added: the others are
/// taken out.
/// </summary>
internal sealed partial class WebApp
{
    /// <summary>The workspace file this folder was opened with, while it is there.</summary>
    private string? WorkspaceFile => _state.Read().Str(CodeWorkspace.Key) is { Length: > 0 } file && File.Exists(file) ? file : null;

    /// <summary>
    /// The file's folders made this run's: those besides the folder open (and not inside it) added, those it no longer
    /// names taken out. Its folders, settings and problems back, with its text.
    /// </summary>
    private (List<CodeWorkspace.Folder> Folders, JsonObject Settings, List<string> Problems, string Text) SyncWorkspaceFile(string file)
    {
        var text = CodeWorkspace.Text(file);
        var (folders, settings, problems) = CodeWorkspace.Read(file, text);
        var root = _rt.Workspace.Root;
        var wanted = folders.Where(f => f.Exists && !CodeWorkspace.Same(f.Full, root) && !_rt.Workspace.InRoot(f.Full)).Select(f => f.Full).ToList();
        var before = Folders.Added(_state);
        foreach (var gone in before.Where(b => !wanted.Any(w => CodeWorkspace.Same(w, b))))
        {
            _rt.Workspace.Disallow(gone);
        }
        foreach (var folder in wanted)
        {
            _rt.Workspace.Allow(folder);
        }
        if (!before.SequenceEqual(wanted, StringComparer.Ordinal))
        {
            Folders.SetAdded(_state, wanted);
        }
        return (folders, settings, problems, text);
    }

    /// <summary>The workspace file as the page shows it: its folders (each with its name and whether it is the folder open), its settings and text.</summary>
    private JsonObject WorkspaceFileJson()
    {
        if (WorkspaceFile is not { } file)
        {
            return new JsonObject { ["path"] = null };
        }
        var (folders, settings, problems, text) = SyncWorkspaceFile(file);
        var root = _rt.Workspace.Root;
        return new JsonObject
        {
            ["path"] = file,
            ["name"] = Path.GetFileNameWithoutExtension(file),
            ["dir"] = Path.GetDirectoryName(file),
            ["text"] = text,
            ["version"] = IdeFiles.Version(file),
            ["settings"] = settings.DeepClone(),
            ["folders"] = new JsonArray([.. folders.Select(f => (JsonNode)new JsonObject
            {
                ["path"] = CodeWorkspace.Same(f.Full, root) ? "." : _rt.Workspace.Show(f.Full),
                ["full"] = f.Full,
                ["name"] = f.Name ?? Path.GetFileName(f.Full),
                ["exists"] = f.Exists,
                ["root"] = CodeWorkspace.Same(f.Full, root),
            })]),
            ["rootInIt"] = folders.Any(f => CodeWorkspace.Same(f.Full, root)),
            ["problems"] = new JsonArray([.. problems.Select(p => (JsonNode)p)]),
        };
    }

    /// <summary>One of the workspace file's calls; false when the path is none of them.</summary>
    private async Task<bool> WorkspaceFileAsync(HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        switch (req.Method, req.Path)
        {
            case ("GET", "/api/workspace-file"):
                await res.JsonAsync(200, WorkspaceFileJson(), ct);
                return true;
            case ("POST", "/api/workspace-file"):
            {
                // The page's edit of it (folders added or removed, a setting): written, then its folders applied.
                var body = Body(req);
                var file = WorkspaceFile ?? throw new IdeError(404, "no_workspace", "No workspace file is open: Save Workspace As… makes one.");
                var text = body["text"] is JsonValue v && v.TryGetValue<string>(out var t) ? t : throw new IdeError(400, "invalid", "Send the file's text.");
                // Checked before it is written: a file that is no workspace file is refused, the one there kept.
                CodeWorkspace.Read(file, text);
                CodeWorkspace.Write(file, text, body.Str("version"));
                await res.JsonAsync(200, WorkspaceFileJson(), ct);
                return true;
            }
            case ("POST", "/api/workspace-file/open"):
            {
                // Open Workspace from File: the folder open stays when the file names it; else the IDE starts again on its
                // first folder there, the others added.
                var file = CodeWorkspace.Resolve(Body(req).Str("path"), _rt.Workspace.Root);
                var (folders, _, _) = CodeWorkspace.Read(file, CodeWorkspace.Text(file));
                var root = _rt.Workspace.Root;
                if (folders.Any(f => CodeWorkspace.Same(f.Full, root)))
                {
                    _state.Merge(new JsonObject { [CodeWorkspace.Key] = file });
                    Folders.Remember(_rt.Env.Paths.DataDir, file);
                    await res.JsonAsync(200, new JsonObject { ["folder"] = root, ["restart"] = false, ["workspace"] = WorkspaceFileJson() }, ct);
                    return true;
                }
                if (!FoldersOpen)
                {
                    throw new IdeError(409, "not_here", "This workspace is Code Studio's: open another folder there.");
                }
                var first = folders.FirstOrDefault(f => f.Exists) ?? throw new IdeError(409, "no_folder", $"None of {Path.GetFileName(file)}'s folders is there.");
                var next = new WorkspaceState(_rt.Env.Paths.DataDir, first.Full);
                Folders.SetAdded(next, folders.Where(f => f.Exists && f != first).Select(f => f.Full));
                next.Merge(new JsonObject { [CodeWorkspace.Key] = file });
                Folders.Remember(_rt.Env.Paths.DataDir, file);
                await res.JsonAsync(200, new JsonObject { ["folder"] = first.Full, ["restart"] = true }, ct);
                _folderOpened.TrySetResult(first.Full);
                return true;
            }
            case ("POST", "/api/workspace-file/save-as"):
            {
                // Save Workspace As: the folder open and those added, in a new file (or one there, when asked to replace it).
                var body = Body(req);
                var path = body.Str("path");
                if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
                {
                    throw new IdeError(400, "invalid", "Give the workspace file's path.");
                }
                var file = Path.GetFullPath(path.EndsWith(CodeWorkspace.Extension, StringComparison.OrdinalIgnoreCase) ? path : path + CodeWorkspace.Extension, _rt.Workspace.Root);
                if (!Directory.Exists(Path.GetDirectoryName(file)))
                {
                    throw new IdeError(404, "not_found", $"There is no folder {Path.GetDirectoryName(file)}.");
                }
                if (File.Exists(file) && body.Bool("replace") != true)
                {
                    throw new IdeError(409, "exists", $"{file} exists already.");
                }
                CodeWorkspace.Write(file, CodeWorkspace.Make(file, [_rt.Workspace.Root, .. Folders.Added(_state)]), null);
                _state.Merge(new JsonObject { [CodeWorkspace.Key] = file });
                Folders.Remember(_rt.Env.Paths.DataDir, file);
                await res.JsonAsync(200, WorkspaceFileJson(), ct);
                return true;
            }
            case ("POST", "/api/workspace-file/close"):
            {
                // Close Workspace: the folder open alone again, as before the workspace file was opened.
                foreach (var folder in Folders.Added(_state))
                {
                    _rt.Workspace.Disallow(folder);
                }
                Folders.SetAdded(_state, []);
                _state.Merge(new JsonObject { [CodeWorkspace.Key] = null });
                await res.JsonAsync(200, new JsonObject { ["path"] = null }, ct);
                return true;
            }
        }
        return false;
    }
}
