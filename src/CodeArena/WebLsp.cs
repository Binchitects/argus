namespace CodeArena;

/// <summary>
/// The IDE's language servers: the ones found on this computer, a server's WebSocket (the Language Server Protocol,
/// which the page speaks itself; the server started as the page opens it), and its log. Behind this run's key, as the
/// terminals are, and only in a trusted folder: a language server runs the folder's code.
/// </summary>
internal sealed partial class WebApp
{
    private LanguageServers _lsp = null!;

    private void StartLsp() =>
        _lsp = new LanguageServers(_rt.Workspace, _rt.Config, _rt.Env.Paths.DataDir, _rt.Env.Env, () => _rt.Sandbox, () => _state.Trusted);

    /// <summary>
    /// A page attached to a language server it starts (in place of the one of that id another page had): each JSON-RPC
    /// message one text message, both ways, until either side goes; then the server is stopped (shutdown, exit, killed
    /// 2 s later). Refused before the socket opens: 403 <c>not_trusted</c>, 404 <c>missing</c>, 409 <c>too_many</c>.
    /// </summary>
    private async Task LspSocketAsync(HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        if (!req.IsWebSocket)
        {
            throw new IdeError(400, "invalid", "This address is a WebSocket: connect to it with one.");
        }
        var run = await _lsp.StartAsync(Query(req, "server"), ct);
        try
        {
            using var socket = await res.AcceptWebSocketAsync(req, ct);
            await SocketRelay.RunAsync(socket, [], run.Out.Reader, m => m, run.FromPageAsync, LanguageServers.MaxPageMessage, "The language server ended.", ct);
        }
        finally
        {
            await run.StopAsync(null);
        }
    }
}
