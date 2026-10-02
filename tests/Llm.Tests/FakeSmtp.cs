using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Llm.Tests;

/// <summary>An SMTP server on a free local port that keeps what it is sent (no TLS, no sign-in).</summary>
public sealed class FakeSmtp : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public sealed record Mail(string From, IReadOnlyList<string> To, string Data);

    public List<Mail> Received { get; } = [];

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public FakeSmtp()
    {
        _listener.Start();
        _ = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("220 fake ESMTP");
        string from = "";
        var to = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            var verb = line.Split(' ', 2)[0].ToUpperInvariant();
            switch (verb)
            {
                case "EHLO" or "HELO":
                    await writer.WriteLineAsync("250 fake");
                    break;
                case "MAIL":
                    from = line[(line.IndexOf(':') + 1)..].Trim();
                    await writer.WriteLineAsync("250 OK");
                    break;
                case "RCPT":
                    to.Add(line[(line.IndexOf(':') + 1)..].Trim());
                    await writer.WriteLineAsync("250 OK");
                    break;
                case "DATA":
                    await writer.WriteLineAsync("354 go on");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } body && body != ".")
                    {
                        data.AppendLine(body);
                    }
                    lock (Received)
                    {
                        Received.Add(new Mail(from, [.. to], data.ToString()));
                    }
                    await writer.WriteLineAsync("250 queued");
                    break;
                case "QUIT":
                    await writer.WriteLineAsync("221 bye");
                    return;
                default:
                    await writer.WriteLineAsync("250 OK");
                    break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        _stop.Dispose();
    }
}

/// <summary>Where webhooks are posted: keeps each post, and answers as told.</summary>
public sealed class FakeWebhook : HttpMessageHandler
{
    public List<(Uri Url, string Body)> Posts { get; } = [];

    public HttpStatusCode Answer { get; set; } = HttpStatusCode.OK;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        lock (Posts)
        {
            Posts.Add((request.RequestUri!, body));
        }
        return new HttpResponseMessage(Answer);
    }
}
