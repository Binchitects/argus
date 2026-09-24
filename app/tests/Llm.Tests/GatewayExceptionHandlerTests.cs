using System.Text.Json;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Http;

namespace Llm.Tests;

/// <summary>How a request that needed the gateway ends when the gateway is down.</summary>
public sealed class GatewayExceptionHandlerTests
{
    [Fact]
    public async Task A_waiting_browser_gets_a_502_with_a_sentence()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var handled = await new GatewayExceptionHandler().TryHandleAsync(context, new GatewayException("The gateway is unreachable."), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(502, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("gateway", body.RootElement.GetProperty("status").GetString());
        Assert.Contains("not reachable", body.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_browser_that_left_gets_nothing_and_nothing_throws()
    {
        // Measured: a reload while the keys were loading made the error handler
        // itself throw on the closed connection, logging two errors per reload.
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();
        var context = new DefaultHttpContext { RequestAborted = gone.Token };
        context.Response.Body = new MemoryStream();
        var handled = await new GatewayExceptionHandler().TryHandleAsync(context, new GatewayException("The gateway is unreachable."), gone.Token);

        Assert.True(handled);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task Other_errors_are_left_to_the_default_handling()
    {
        var context = new DefaultHttpContext();
        Assert.False(await new GatewayExceptionHandler().TryHandleAsync(context, new InvalidOperationException("x"), CancellationToken.None));
    }
}
