using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Chat.Tools;
using Microsoft.Extensions.Options;

namespace Llm.Api.Schedules;

/// <summary>Configuration section "Schedules".</summary>
public sealed class ScheduleOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Scheduled tasks one person may have.</summary>
    public int PerPerson { get; set; } = 10;
    /// <summary>The shortest time between two runs of a task: a schedule may not run more often.</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromMinutes(15);
    /// <summary>Hosts a task's answer may be posted to, comma separated; *.example.com for a domain and its subdomains.</summary>
    public string WebhookHosts { get; set; } = "hooks.slack.com, *.webhook.office.com, *.logic.azure.com, discord.com";
}

/// <summary>Configuration section "Mail": the SMTP server the app sends email through.</summary>
public sealed class MailOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    /// <summary>Upgrade to TLS (STARTTLS) before signing in.</summary>
    public bool StartTls { get; set; } = true;
    public string? User { get; set; }
    public string? Password { get; set; }
    /// <summary>The sender, e.g. "Argus Arena &lt;llm@example.com&gt;".</summary>
    public string? From { get; set; }
}

/// <summary>Sends email through the SMTP server an admin set (Settings → Email).</summary>
public sealed class Mailer(IOptionsMonitor<MailOptions> options)
{
    public bool Configured => options.CurrentValue is { Host.Length: > 0, From.Length: > 0 };

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!Configured)
        {
            throw new InvalidOperationException("Email is not set up (Settings → Email).");
        }
        using var client = new SmtpClient(o.Host, o.Port) { EnableSsl = o.StartTls, DeliveryMethod = SmtpDeliveryMethod.Network, Timeout = 30_000 };
        if (o.User is { Length: > 0 } user)
        {
            client.Credentials = new NetworkCredential(user, o.Password);
        }
        using var message = new MailMessage(new MailAddress(o.From!), new MailAddress(to))
        {
            Subject = subject, Body = body, SubjectEncoding = Encoding.UTF8, BodyEncoding = Encoding.UTF8, IsBodyHtml = false,
        };
        await client.SendMailAsync(message, ct);
    }
}

/// <summary>
/// Posts a task's answer to a webhook (Slack, Teams, Mattermost, Discord): JSON with "text"
/// (what those show) and "content" (Discord's), and the task, status and link beside them.
/// Only to hosts an admin allowed (Schedules:WebhookHosts), so a task cannot be made to
/// reach into the network.
/// </summary>
public sealed class Webhooks(IHttpClientFactory http, IOptionsMonitor<ScheduleOptions> options)
{
    public const string Client = "webhook";

    /// <summary>Why this URL may not be used, or null.</summary>
    public string? Refusal(string url) => Refusal(url, options.CurrentValue.WebhookHosts);

    /// <summary>Why this URL may not be used with these hosts allowed (Schedules:WebhookHosts), or null.</summary>
    public static string? Refusal(string url, string? hosts)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
        {
            return "A webhook is an https:// URL, as Slack, Teams or Mattermost give it.";
        }
        return WebGuard.Allowed(u.Host, hosts) ? null : $"{u.Host} is not a webhook host an admin allowed ({hosts}).";
    }

    public async Task PostAsync(string url, string text, JsonObject details, CancellationToken ct)
    {
        if (Refusal(url) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }
        var body = new JsonObject { ["text"] = text, ["content"] = text.Length > 1900 ? text[..1900] + "…" : text };
        foreach (var (k, v) in details)
        {
            body[k] = v?.DeepClone();
        }
        using var res = await http.CreateClient(Client).PostAsync(url, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)res.StatusCode}");
        }
    }
}
