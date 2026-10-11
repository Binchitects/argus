using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// The IDE's debugger calls: a debug session started for a launch configuration the page resolved, its WebSocket (the
/// Debug Adapter Protocol, which the page speaks itself), stopping it, the source files it names outside the folder,
/// and the debug adapters found on this computer. Behind this run's key, as the terminals are, and only in a trusted
/// folder: a debugger runs the folder's programs.
/// </summary>
internal sealed partial class WebApp
{
    /// <summary>The most a debug session's page sends in one message.</summary>
    private const int MaxDebugMessage = 1024 * 1024;

    private DebugAdapters _adapters = null!;
    private DebugSessions _debug = null!;

    private void StartDebug()
    {
        _adapters = new DebugAdapters(_rt.Config, _rt.Env.Paths.DataDir, _rt.Env.Env);
        _debug = new DebugSessions(_rt.Workspace, _adapters, () => _state.Trusted);
    }

    /// <summary>
    /// {configuration} to {id, name, adapter, adapterId, configuration, notes, root, rootReal, pathStyle}: the
    /// configuration in the adapter's words, for the page's launch request. 409 <c>no_adapter</c> {adapter, install}
    /// when the adapter it needs is not here, 409 <c>untrusted</c> until the folder is trusted.
    /// </summary>
    private async Task DebugStartAsync(JsonObject body, HttpResponse res, CancellationToken ct)
    {
        if (body["configuration"] is not JsonObject configuration)
        {
            throw new IdeError(400, "invalid", "Send the launch configuration: {\"configuration\": {\"type\", \"request\", …}}.");
        }
        DebugSession session;
        try
        {
            session = await _debug.StartAsync(configuration, ct);
        }
        catch (DebugAdapterMissing e)
        {
            var kind = DebugAdapters.Kind(e.Adapter);
            await res.JsonAsync(409, new JsonObject
            {
                ["status"] = "no_adapter",
                ["error"] = $"{kind?.Name ?? e.Adapter} is needed to debug this: {e.Message}",
                ["adapter"] = e.Adapter,
                ["install"] = _adapters.Install(e.Adapter),
            }, ct);
            return;
        }
        var root = _rt.Workspace.Root;
        await res.JsonAsync(200, new JsonObject
        {
            ["id"] = session.Id,
            ["name"] = session.Launch.Configuration.Str("name"),
            ["adapter"] = session.Launch.AdapterId,
            ["adapterId"] = session.Launch.Type,
            ["configuration"] = session.Launch.Configuration.Clone(),
            ["notes"] = new JsonArray([.. session.Launch.Notes.Select(n => (JsonNode)n)]),
            ["root"] = root,
            ["rootReal"] = Workspace.Physical(root) ?? root,
            ["pathStyle"] = OperatingSystem.IsWindows() ? "win" : "posix",
        }, ct);
    }

    /// <summary>
    /// A page attached to a debug session: the session's snapshot and the output kept, then every message from the
    /// adapter meant for it, as JSON text messages, one per message; the page's requests the same way back.
    /// </summary>
    private async Task DebugSocketAsync(HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        if (!req.IsWebSocket)
        {
            throw new IdeError(400, "invalid", "This address is a WebSocket: connect to it with one.");
        }
        if (_debug.Find(Query(req, "id")) is not { } session)
        {
            throw new IdeError(404, "not_found", "There is no such debug session (it was stopped).");
        }
        using var socket = await res.AcceptWebSocketAsync(req, ct);
        var (first, page) = session.Attach();
        try
        {
            await SocketRelay.RunAsync(socket, first, page.Out.Reader, m => page.Taken(m), (m, token) => session.FromPageAsync(page, m, token), MaxDebugMessage, "The debug session ended.", ct);
        }
        finally
        {
            session.Detach(page);
        }
    }

    /// <summary>
    /// A source file of a debug session: one in the folder (an absolute path inside it, or a relative one) as the file
    /// calls read it, or one outside that the session's adapter named (read-only), but never Code Arena's own (its
    /// config.json holds the key; a program's debug information can name any file). Anything else is 403 <c>outside</c>.
    /// </summary>
    private JsonObject DebugSource(string? id, string? path)
    {
        var session = _debug.Find(id) ?? throw new IdeError(404, "not_found", "There is no such debug session.");
        if (string.IsNullOrEmpty(path) || path.Contains('\0'))
        {
            throw new IdeError(400, "invalid", "Give the source's path.");
        }
        if (!Path.IsPathFullyQualified(path))
        {
            var inside = _files.Read(path);
            inside["readOnly"] = false;
            return inside;
        }
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (_rt.Workspace.InRoot(full) && _rt.Workspace.Allowed(full))
        {
            var inside = _files.ReadFull(full);
            inside["readOnly"] = false;
            return inside;
        }
        if (!session.Named(full))
        {
            throw new IdeError(403, "outside", $"{path} is outside the folder Code Arena works in ({_rt.Workspace.Root}), and this debug session's adapter has not named it.");
        }
        var leads = Workspace.Physical(full) ?? full;
        if (new[] { _rt.Env.Paths.ConfigDir, _rt.Env.Paths.DataDir }.Any(own => Workspace.Within(full, own) || Workspace.Within(leads, Workspace.Physical(own) ?? own)))
        {
            throw new IdeError(403, "outside", $"{path} is one of Code Arena's own files: a debug session does not show it.");
        }
        var named = _files.ReadFull(full);
        named.Remove("version");
        named["readOnly"] = true;
        return named;
    }
}
