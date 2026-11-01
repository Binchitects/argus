using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Identity;

namespace Llm.Api.Operations;

/// <summary>
/// Admin → Monitoring → Engines and sandboxes: each with its state, and Reload, Restart or Reset (<see cref="Runtimes"/>).
/// Loading an engine's models again is an operator's duty with loading them (models.load); a restart or a reset stops
/// what runs there (runtimes.restart). Each is audited: runtime.reload, runtime.restart, runtime.reset.
/// </summary>
public static class RuntimeEndpoints
{
    public static void MapRuntimes(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/runtimes");
        g.MapGet("", async (Runtimes runtimes, CancellationToken ct) => Results.Ok(await runtimes.ListAsync(ct)))
            .RequirePermission(Core.Access.Permissions.MonitoringRead);
        g.MapPost("/{id}/reload", (string id, HttpContext http, Runtimes runtimes, Audit audit, CancellationToken ct) => AskAsync(id, Runtimes.Reload, http, runtimes, audit, ct))
            .RequirePermission(Core.Access.Permissions.ModelsLoad);
        g.MapPost("/{id}/restart", (string id, HttpContext http, Runtimes runtimes, Audit audit, CancellationToken ct) => AskAsync(id, Runtimes.Restart, http, runtimes, audit, ct))
            .RequirePermission(Core.Access.Permissions.RuntimesRestart);
        g.MapPost("/{id}/reset", (string id, HttpContext http, Runtimes runtimes, Audit audit, CancellationToken ct) => AskAsync(id, Runtimes.Reset, http, runtimes, audit, ct))
            .RequirePermission(Core.Access.Permissions.RuntimesRestart);
    }

    private static async Task<IResult> AskAsync(string id, string action, HttpContext http, Runtimes runtimes, Audit audit, CancellationToken ct)
    {
        var who = http.User.Identity?.Name is { Length: > 0 } name ? name : "an admin";
        var (detail, refusal) = await runtimes.AskAsync(id, action, who, ct);
        if (refusal is { } r)
        {
            return AuthEndpoints.Problem(r.Status, r.Code, r.Message);
        }
        await audit.WriteAsync($"runtime.{action}", id, detail: detail);
        return Results.Accepted(value: new { status = action switch { Runtimes.Reload => "reloading", Runtimes.Restart => "restarting", _ => "resetting" } });
    }
}
