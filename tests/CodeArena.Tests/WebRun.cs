using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena.Tests;

/// <summary>code-arena web running in the harness's folder, its address read from what it printed, until disposed (Ctrl+C).</summary>
internal sealed partial class WebRun : IAsyncDisposable
{
    private readonly CliEnv _env;
    private readonly StringWriter _out;
    private readonly StringWriter _err;
    private readonly Task<int> _exit;

    private WebRun(CliEnv env, StringWriter output, StringWriter error, Task<int> exit)
    {
        _env = env;
        _out = output;
        _err = error;
        _exit = exit;
    }

    // TextWriter.Synchronized locks the wrapper it returns: reading under the same lock.

    public string Address { get; private set; } = "";
    public int Port { get; private set; }
    public string Token { get; private set; } = "";
    public HttpClient Http { get; private set; } = null!;

    public string Out
    {
        get
        {
            lock (_env.Out)
            {
                return _out.ToString();
            }
        }
    }

    public string Err
    {
        get
        {
            lock (_env.Err)
            {
                return _err.ToString();
            }
        }
    }

    public static Task<WebRun> StartAsync(Harness h, params string[] args) => StartAsync(h, bare: false, args);

    /// <summary>code-arena web, or (bare) code-arena with no command and no prompt, which is the IDE too.</summary>
    public static async Task<WebRun> StartAsync(Harness h, bool bare, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var env = new CliEnv
        {
            In = new StringReader(""),
            Out = TextWriter.Synchronized(output),
            Err = TextWriter.Synchronized(error),
            Env = k => h.Env.GetValueOrDefault(k),
            Cwd = h.Work,
            Paths = h.Paths,
            Web = WebAssets.Of(
                ("code-arena.html", "<!doctype html><title>Code Arena</title><script type=\"module\" src=\"/assets/app.js\"></script>"),
                ("assets/app.js", "console.log('code arena')"),
                ("preview.html", "<!doctype html><p>runner</p>")),
        };
        var exit = Task.Run(() => Cli.RunAsync([.. bare ? Array.Empty<string>() : ["web"], "--no-open", .. args], env));
        var run = new WebRun(env, output, error, exit);
        for (var waited = 0; ; waited += 50)
        {
            if (Link().Match(run.Out) is { Success: true } m)
            {
                run.Address = m.Value;
                run.Port = int.Parse(m.Groups["port"].Value);
                run.Token = m.Groups["token"].Value;
                break;
            }
            if (exit.IsCompleted || waited > 20_000)
            {
                throw new Xunit.Sdk.XunitException($"code-arena web did not start: {run.Out} {run.Err}");
            }
            await Task.Delay(50);
        }
        run.Http = Client(run.Port, run.Token);
        return run;
    }

    [GeneratedRegex(@"http://127\.0\.0\.1:(?<port>\d+)/\?token=(?<token>[A-Za-z0-9_-]+)")]
    private static partial Regex Link();

    /// <summary>A client of the server: no cookies of its own, no redirects followed, the key as a bearer token when given.</summary>
    public static HttpClient Client(int port, string? token)
    {
        var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        if (token is not null)
        {
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
        return http;
    }

    public async Task<JsonNode> GetJsonAsync(string path)
    {
        var response = await Http.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {text}");
        return JsonNode.Parse(text)!;
    }

    public async Task<HttpResponseMessage> PostAsync(string path, JsonNode body, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            request.Headers.Add(name, value);
        }
        var response = await Http.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    /// <summary>A message, answered as a stream of events.</summary>
    public Task<EventStream> SendAsync(string text) => StreamAsync(HttpMethod.Post, "/api/messages", new JsonObject { ["text"] = text });

    public async Task<EventStream> StreamAsync(HttpMethod method, string path, JsonNode? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        return new EventStream(response);
    }

    public async ValueTask DisposeAsync()
    {
        if (_exit.IsCompleted)
        {
            return;
        }
        Http?.Dispose();
        Assert.True(_env.Cancel.Press());
        Assert.Equal(0, await _exit.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains("Stopped", Err);
    }
}

/// <summary>The events of one stream, read as the test needs them.</summary>
internal sealed class EventStream(HttpResponseMessage response) : IDisposable
{
    private StreamReader? _reader;

    public List<JsonObject> Seen { get; } = [];

    public async Task<JsonObject?> NextAsync()
    {
        _reader ??= new StreamReader(await response.Content.ReadAsStreamAsync());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await _reader.ReadLineAsync(deadline.Token) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                var e = JsonNode.Parse(line[6..])!.AsObject();
                Seen.Add(e);
                return e;
            }
        }
        return null;
    }

    /// <summary>Reads on to the next event of this type.</summary>
    public async Task<JsonObject> UntilAsync(string type)
    {
        while (await NextAsync() is { } e)
        {
            if (e["type"]!.GetValue<string>() == type)
            {
                return e;
            }
        }
        throw new Xunit.Sdk.XunitException($"The stream ended before a {type} event: {string.Join(", ", Seen.Select(e => e["type"]))}");
    }

    /// <summary>Reads to the end; every event seen.</summary>
    public async Task<List<JsonObject>> RestAsync()
    {
        while (await NextAsync() is not null)
        {
        }
        return Seen;
    }

    public void Dispose()
    {
        _reader?.Dispose();
        response.Dispose();
    }
}
