using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>The audit log sent on to the company's SIEM as it is written: by webhook and by syslog, each entry once and in order.</summary>
[Collection(nameof(AppCollection))]
public sealed class AuditForwardTests(AppFixture app)
{
    private static async Task<List<long>> IdsAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> f)
    {
        await using var scope = f.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditEvents.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Id).ToListAsync();
    }

    private static async Task<JsonElement> CheckAsync(TestBrowser admin) =>
        (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "audit-forwarding");

    /// <summary>Waits for a condition the forwarder makes true on its own (it sends every two seconds, later after a failure).</summary>
    private static async Task UntilAsync(Func<Task<bool>> done, string what)
    {
        for (var i = 0; i < 300; i++)
        {
            if (await done())
            {
                return;
            }
            await Task.Delay(100);
        }
        Assert.Fail($"Not in 30 s: {what}");
    }

    [Fact]
    [Trait("Control", "LOG-04")]
    public async Task Each_entry_is_posted_once_in_order_signed_and_a_webhook_that_fails_gets_them_all_later()
    {
        var host = $"siem-{Guid.NewGuid():N}.test";
        await using var f = app.Create(app.ConnectionStringFor("fwd_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Audit:Webhook"] = $"https://{host}/collect",
            ["Audit:WebhookSecret"] = "a-signing-key-for-tests",
        });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await admin.PostAsync("/api/admin/people", new { userName = "fw" + Guid.NewGuid().ToString("N")[..6], email = $"fw{Guid.NewGuid().ToString("N")[..6]}@example.test" });

        List<(Uri Url, string Body)> Posts()
        {
            lock (app.Webhook.Posts)
            {
                return [.. app.Webhook.Posts.Where(p => p.Url.Host == host)];
            }
        }
        List<long> Sent() => [.. Posts().SelectMany(p => JsonDocument.Parse(p.Body).RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetInt64())).Distinct()];
        await UntilAsync(async () => Sent().SequenceEqual(await IdsAsync(f)), "every entry posted");
        var first = JsonDocument.Parse(Posts()[0].Body).RootElement[0];
        Assert.Matches("^[0-9a-f]{64}$", first.GetProperty("hash").GetString()!);
        Assert.True(first.TryGetProperty("action", out _));
        Assert.Equal("ok", (await CheckAsync(admin)).GetProperty("state").GetString());

        // The collector fails: nothing is skipped, the check says why, and all of it arrives once it answers again.
        app.Webhook.Answer = HttpStatusCode.ServiceUnavailable;
        try
        {
            await admin.PostAsync("/api/admin/people", new { userName = "fx" + Guid.NewGuid().ToString("N")[..6], email = $"fx{Guid.NewGuid().ToString("N")[..6]}@example.test" });
            await UntilAsync(async () => (await CheckAsync(admin)).GetProperty("state").GetString() == "fail", "the check failing");
            Assert.Contains("HTTP 503", (await CheckAsync(admin)).GetProperty("detail").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            app.Webhook.Answer = HttpStatusCode.OK;
        }
        await UntilAsync(async () => Sent().SequenceEqual(await IdsAsync(f)), "every entry posted after the failure");

        // Each post is signed with the key, over its body as sent.
        foreach (var (url, body) in Posts())
        {
            Assert.Equal($"https://{host}/collect", url.ToString());
            string signature;
            lock (app.Webhook.Posts)
            {
                signature = app.Webhook.Signatures[body];
            }
            Assert.Equal("sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("a-signing-key-for-tests"), Encoding.UTF8.GetBytes(body))), signature);
        }
    }

    [Fact]
    [Trait("Control", "LOG-04")]
    public async Task Syslog_gets_each_entry_octet_counted_as_RFC_5424_with_failures_as_warnings()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var frames = new List<string>();
        using var stop = new CancellationTokenSource();
        var reading = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                var stream = client.GetStream();
                var bytes = new MemoryStream();
                await stream.CopyToAsync(bytes, stop.Token);
                var data = bytes.ToArray();
                // Octet counting: "LEN MSG", one after another; LEN counts the message's bytes.
                for (var at = 0; at < data.Length;)
                {
                    var space = Array.IndexOf(data, (byte)' ', at);
                    var length = int.Parse(Encoding.ASCII.GetString(data, at, space - at), System.Globalization.CultureInfo.InvariantCulture);
                    lock (frames)
                    {
                        frames.Add(Encoding.UTF8.GetString(data, space + 1, length));
                    }
                    at = space + 1 + length;
                }
            }
        });
        try
        {
            await using var f = app.Create(app.ConnectionStringFor("sys_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
            {
                ["Audit:Syslog"] = $"tcp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",
            });
            var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
            await new TestBrowser(f).LoginAsync("admin", "not the password");
            List<long> Got()
            {
                lock (frames)
                {
                    return [.. frames.Select(m => JsonDocument.Parse(m[m.IndexOf('{')..]).RootElement.GetProperty("id").GetInt64())];
                }
            }
            await UntilAsync(async () => Got().SequenceEqual(await IdsAsync(f)), "every entry sent by syslog");
            List<string> all;
            lock (frames)
            {
                all = [.. frames];
            }
            Assert.All(all, m => Assert.Matches(@"^<1(10|08)>1 \d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{6}Z \S+ argus-arena - audit - \{", m));
            Assert.Contains(all, m => m.StartsWith("<108>", StringComparison.Ordinal) && m.Contains("\"success\":false", StringComparison.Ordinal));
            Assert.Equal("ok", (await CheckAsync(admin)).GetProperty("state").GetString());
        }
        finally
        {
            await stop.CancelAsync();
            listener.Stop();
            await Task.WhenAny(reading, Task.Delay(1000));
        }
    }
}
