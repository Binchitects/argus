using Microsoft.AspNetCore.Diagnostics;

namespace Llm.Api.Gateway;

/// <summary>
/// Any request that needed the gateway while it was down answers 502 with a
/// sentence a person can act on, wherever it happened -- not a bare 500.
/// </summary>
public sealed class GatewayExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not GatewayException gateway)
        {
            return false;
        }
        httpContext.Response.StatusCode = StatusCodes.Status502BadGateway;
        await httpContext.Response.WriteAsJsonAsync(new
        {
            status = "gateway",
            error = "The model gateway is not reachable right now, so keys and credit cannot be shown or changed. " + gateway.Message,
        }, cancellationToken);
        return true;
    }
}
