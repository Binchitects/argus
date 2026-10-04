using System.Globalization;
using System.Text.Json.Nodes;
using Llm.Api.Identity;
using Llm.Api.Schedules;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.Notifications;

/// <summary>Configuration section "Notifications": where news goes besides the bell.</summary>
public sealed class NotificationOptions
{
    /// <summary>Credit and alert news by email too, when email is set up (Settings → Email).</summary>
    public bool Email { get; set; } = true;

    /// <summary>The admins' news (alerts firing, credit used up) is posted here too: a Slack, Teams or Mattermost webhook.</summary>
    public string? AlertsWebhook { get; set; }
}

/// <summary>
/// News that matters beyond the page: a person's credit running out, a system alert
/// firing. It goes in the bell as any news and, the first time it is said, by email to
/// each person told (when email is set up), and the admins' news once to the alerts
/// webhook (only to a host an admin allowed, as a task's). A failed email or post is
/// logged; the bell has the news anyway.
/// </summary>
public sealed partial class NewsDelivery(Notifier notifier, UserManager<AppUser> users, Mailer mailer, Webhooks webhooks,
    IOptionsMonitor<NotificationOptions> options, IOptions<AuthOptions> auth, TimeProvider clock, ILogger<NewsDelivery> logger)
{
    /// <returns>False when this person had this news already (same key): nothing is sent again.</returns>
    public async Task<bool> SendAsync(AppUser person, News news, CancellationToken ct)
    {
        if (!await notifier.SendAsync(person.Id, news, ct))
        {
            return false;
        }
        if (options.CurrentValue.Email && mailer.Configured && person.Email is { Length: > 0 } email)
        {
            try
            {
                await mailer.SendAsync(email, news.Title,
                    $"{news.Body ?? news.Title}\n\n--\n{(news.Link is { } link ? $"Open it: {auth.Value.Origin}{link}\n" : "")}This is in your notifications too (the bell).", ct);
            }
            catch (Exception ex) when (ex is System.Net.Mail.SmtpException or InvalidOperationException or IOException or FormatException)
            {
                LogFailed(logger, "email", news.Kind, ex.Message);
            }
        }
        return true;
    }

    /// <summary>To every admin who can sign in, each by email the first time; and, when any of them had not heard it, to the alerts webhook.</summary>
    public async Task ToAdminsAsync(News news, CancellationToken ct)
    {
        var told = false;
        foreach (var admin in (await users.GetUsersInRoleAsync(Roles.Admin)).Where(a => !a.IsDisabled))
        {
            told |= await SendAsync(admin, news, ct);
        }
        if (told && options.CurrentValue.AlertsWebhook is { Length: > 0 } url)
        {
            var link = news.Link is { } l ? auth.Value.Origin + l : null;
            try
            {
                await webhooks.PostAsync(url, $"{news.Title}{(news.Body is { Length: > 0 } body ? $"\n\n{body}" : "")}{(link is null ? "" : $"\n\nOpen it: {link}")}", new JsonObject
                {
                    ["kind"] = news.Kind, ["title"] = news.Title, ["url"] = link, ["at"] = clock.GetUtcNow().ToString("o", CultureInfo.InvariantCulture),
                }, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                LogFailed(logger, "webhook", news.Kind, ex.Message);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "News ({Kind}): the {Channel} could not be sent: {Error}")]
    private static partial void LogFailed(ILogger logger, string channel, string kind, string error);
}
