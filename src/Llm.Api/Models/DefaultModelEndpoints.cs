using System.Security.Cryptography;
using System.Text;
using Llm.Api.Endpoints;
using Llm.Api.Gateway;
using Llm.Api.Identity;
using Llm.Api.Operations;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>
/// The default models (<see cref="Provisioning"/>): which are there, and their download, started only when asked: by an admin
/// (Admin → Models → Default models), or by the installer's "download the default models" option, which calls the internal
/// path with the gateway's master key from inside the network (Traefik routes no /internal path).
/// </summary>
public static class DefaultModelEndpoints
{
    public const string InternalPath = "/internal/models/defaults";

    public static void MapDefaultModels(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/models/defaults").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("", async (Provisioning setup, CancellationToken ct) => Results.Ok(await setup.ListAsync(ct)));
        g.MapPost("", async (Provisioning setup, Audit audit, CancellationToken ct) =>
        {
            var started = await StartAsync(setup, ct);
            await audit.WriteAsync("model.defaults", null, detail: started.Count == 0 ? "nothing missing" : string.Join("; ", started));
            return Results.Ok(new { started, models = await setup.ListAsync(ct) });
        });
        app.MapGet(InternalPath, async (HttpContext http, IOptions<LiteLlmOptions> gateway, Provisioning setup, CancellationToken ct) =>
            Allowed(http, gateway) ? Results.Ok(await setup.ListAsync(ct)) : Results.Unauthorized()).AllowAnonymous();
        app.MapPost(InternalPath, async (HttpContext http, IOptions<LiteLlmOptions> gateway, Provisioning setup, Audit audit, CancellationToken ct) =>
        {
            if (!Allowed(http, gateway))
            {
                return Results.Unauthorized();
            }
            var started = await StartAsync(setup, ct);
            await audit.WriteAsync("model.defaults", "installer", detail: started.Count == 0 ? "nothing missing" : string.Join("; ", started));
            return Results.Ok(new { started, models = await setup.ListAsync(ct) });
        }).AllowAnonymous();
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

    private static bool Allowed(HttpContext http, IOptions<LiteLlmOptions> gateway)
    {
        var expected = gateway.Value.MasterKey;
        var given = http.Request.Headers["x-api-key"].ToString();
        return !string.IsNullOrEmpty(expected) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
    }
}
