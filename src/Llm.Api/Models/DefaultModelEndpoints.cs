using Llm.Api.Access;
using Llm.Api.Identity;

namespace Llm.Api.Models;

/// <summary>
/// The default models (<see cref="Provisioning"/>): which are there, and their download, started only when asked: by an admin
/// here (Admin → Models → Default models), or by the installer's "download the default models" option (DEFAULT_MODELS=download,
/// downloaded once at the first start).
/// </summary>
public static class DefaultModelEndpoints
{
    public static void MapDefaultModels(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/models/defaults").RequirePermissions(Llm.Core.Access.Permissions.ModelsRead, Llm.Core.Access.Permissions.ModelsManage, new Dictionary<string, string>
        {
            ["POST /api/admin/models/defaults"] = Llm.Core.Access.Permissions.ModelsDownload,
        });
        g.MapGet("", async (Provisioning setup, CancellationToken ct) => Results.Ok(await setup.ListAsync(ct)));
        g.MapPost("", async (Provisioning setup, Audit audit, CancellationToken ct) =>
        {
            var started = await StartAsync(setup, ct);
            await audit.WriteAsync("model.defaults", null, detail: started.Count == 0 ? "nothing missing" : string.Join("; ", started));
            return Results.Ok(new { started, models = await setup.ListAsync(ct) });
        });
    }

    /// <summary>The downloads started; a Hugging Face that cannot be reached is said as a 502 by the caller's error.</summary>
    private static async Task<IReadOnlyList<string>> StartAsync(Provisioning setup, CancellationToken ct)
    {
        try
        {
            return await setup.RunAsync(download: true, ct);
        }
        catch (HuggingFaceException ex)
        {
            throw new BadHttpRequestException($"Hugging Face could not be reached: {ex.Message}", StatusCodes.Status502BadGateway);
        }
    }
}
