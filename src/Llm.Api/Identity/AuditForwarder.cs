using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Identity;

/// <summary>Where the audit log is sent on to, as Settings → Audit log sets it.</summary>
public sealed class AuditForwardOptions
{
    /// <summary>A syslog collector: udp://host:514, tcp://host:514 or tls://host:6514 (RFC 5424, octet-counted over TCP and TLS).</summary>
    public string? Syslog { get; set; }

    /// <summary>An https:// address the entries are posted to as JSON, in batches, signed with <see cref="WebhookSecret"/>.</summary>
    public string? Webhook { get; set; }

    /// <summary>The key of the webhook's signature: X-Arena-Signature: sha256=HMAC-SHA256(key, body), hex.</summary>
    public string? WebhookSecret { get; set; }
}

/// <summary>How far the audit log was sent on, for Admin → Compliance.</summary>
public sealed record AuditForwardState(long Sent, long Newest, DateTimeOffset? SentAt, string? Problem);

/// <summary>
/// Sends the audit log on to the company's SIEM as it is written: every sealed entry once, in order, to syslog and to a webhook (each
/// when set). How far it got is kept in the database, so a restart or another replica goes on from there; one replica sends at a time
/// (an advisory lock); a collector that does not answer is tried again, later each time, and nothing is skipped.
/// </summary>
public sealed partial class AuditForwarder(IServiceScopeFactory scopes, IOptionsMonitor<AuditForwardOptions> options, IHttpClientFactory http, TimeProvider clock,
    ILogger<AuditForwarder> logger) : BackgroundService
{
    /// <summary>The settings row holding the id of the last entry sent.</summary>
    public const string Mark = "audit.forwarded";

    private const long ForwardLock = AuditChain.Lock + 1;
    private const int Batch = 200;

    public string? Problem { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    public bool On => !string.IsNullOrWhiteSpace(options.CurrentValue.Syslog) || !string.IsNullOrWhiteSpace(options.CurrentValue.Webhook);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wait = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (On)
                {
                    while (await SendAsync(stoppingToken) == Batch)
                    {
                    }
                }
                Problem = null;
                wait = TimeSpan.FromSeconds(2);
            }
            catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or AuthenticationException or TaskCanceledException
                or InvalidOperationException && !stoppingToken.IsCancellationRequested)
            {
                Problem = ex.Message;
                LogNotSent(logger, ex.Message);
                // Later each time, up to five minutes; nothing is skipped meanwhile.
                wait = TimeSpan.FromSeconds(Math.Min(300, wait.TotalSeconds * 2));
            }
            try
            {
                await Task.Delay(wait, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Sends the next entries not sent yet (at most a batch); returns how many. Nothing when another replica is sending.</summary>
    public async Task<int> SendAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({ForwardLock}) AS \"Value\"").SingleAsync(ct))
        {
            return 0;
        }
        var mark = await db.Settings.SingleOrDefaultAsync(s => s.Key == Mark, ct);
        var sent = long.TryParse(mark?.Value, CultureInfo.InvariantCulture, out var n) ? n : 0;
        var entries = await db.AuditEvents.AsNoTracking().Where(e => e.Id > sent && e.Hash != null).OrderBy(e => e.Id).Take(Batch).ToListAsync(ct);
        if (entries.Count == 0)
        {
            return 0;
        }
        var o = options.CurrentValue;
        if (!string.IsNullOrWhiteSpace(o.Syslog))
        {
            await SyslogAsync(o.Syslog.Trim(), entries, ct);
        }
        if (!string.IsNullOrWhiteSpace(o.Webhook))
        {
            await WebhookAsync(o.Webhook.Trim(), o.WebhookSecret, entries, ct);
        }
        var last = entries[^1].Id.ToString(CultureInfo.InvariantCulture);
        if (mark is null)
        {
            db.Settings.Add(new Setting { Key = Mark, Value = last, UpdatedAt = clock.GetUtcNow() });
        }
        else
        {
            mark.Value = last;
            mark.UpdatedAt = clock.GetUtcNow();
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        SentAt = clock.GetUtcNow();
        return entries.Count;
    }

    /// <summary>How far it got: the last entry sent, the newest written, when, and why not further.</summary>
    public async Task<AuditForwardState> StateAsync(AppDbContext db, CancellationToken ct)
    {
        var mark = await db.Settings.AsNoTracking().Where(s => s.Key == Mark).Select(s => s.Value).SingleOrDefaultAsync(ct);
        var newest = await db.AuditEvents.AsNoTracking().MaxAsync(e => (long?)e.Id, ct) ?? 0;
        return new AuditForwardState(long.TryParse(mark, CultureInfo.InvariantCulture, out var n) ? n : 0, newest, SentAt, Problem);
    }

    /// <summary>An entry as it is sent: every field, and its hash, so the SIEM can walk the chain too.</summary>
    public static string Json(AuditEvent e) => JsonSerializer.Serialize(new
    {
        id = e.Id, at = e.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture), actorId = e.ActorId, actor = e.Actor,
        action = e.Action, target = e.Target, success = e.Success, ip = e.Ip, detail = e.Detail, hash = e.Hash,
    });

    /// <summary>RFC 5424: facility log audit (13), informational or, for a failure, warning; the entry as JSON.</summary>
    public static string SyslogLine(AuditEvent e, string host) =>
        $"<{13 * 8 + (e.Success ? 6 : 4)}>1 {e.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)} {host} argus-arena - audit - {Json(e)}";

    private static async Task SyslogAsync(string address, IReadOnlyList<AuditEvent> entries, CancellationToken ct)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var u) || u.Scheme is not ("udp" or "tcp" or "tls") || u.Port <= 0)
        {
            throw new InvalidOperationException($"The syslog address is udp://host:port, tcp://host:port or tls://host:port, not {address}.");
        }
        var host = Environment.MachineName;
        if (u.Scheme == "udp")
        {
            using var udp = new UdpClient();
            foreach (var e in entries)
            {
                await udp.SendAsync(Encoding.UTF8.GetBytes(SyslogLine(e, host)), u.Host, u.Port, ct);
            }
            return;
        }
        using var tcp = new TcpClient();
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(TimeSpan.FromSeconds(10));
        await tcp.ConnectAsync(u.Host, u.Port, connect.Token);
        Stream stream = tcp.GetStream();
        if (u.Scheme == "tls")
        {
            var tls = new SslStream(stream);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = u.Host }, ct);
            stream = tls;
        }
        await using (stream)
        {
            foreach (var e in entries)
            {
                // RFC 6587 octet counting: the length, a space, the message.
                var message = Encoding.UTF8.GetBytes(SyslogLine(e, host));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"{message.Length} "), ct);
                await stream.WriteAsync(message, ct);
            }
            await stream.FlushAsync(ct);
        }
    }

    private async Task WebhookAsync(string url, string? secret, IReadOnlyList<AuditEvent> entries, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("The audit webhook is an https:// address.");
        }
        var body = "[" + string.Join(",", entries.Select(Json)) + "]";
        using var req = new HttpRequestMessage(HttpMethod.Post, u) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (!string.IsNullOrEmpty(secret))
        {
            req.Headers.Add("X-Arena-Signature", "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))));
        }
        using var res = await http.CreateClient(Schedules.Webhooks.Client).SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"the audit webhook answered HTTP {(int)res.StatusCode}");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The audit log could not be sent on: {Reason}; trying again later")]
    private static partial void LogNotSent(ILogger logger, string reason);
}
