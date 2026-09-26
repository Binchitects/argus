namespace Llm.Api;

/// <summary>
/// `dotnet Llm.Api.dll healthcheck`: the container health check. The runtime
/// image has no shell or curl, so the app probes itself.
/// </summary>
public static class HealthProbe
{
    public static async Task<int> RunAsync()
    {
        var port = (Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080").Split(';', ',')[0];
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        try
        {
            using var res = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/readyz"));
            return res.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }
}
