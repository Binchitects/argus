using Llm.Core.Access;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Llm.Api.Dashboards;
using Llm.Api.Gateway;
using Llm.Api.Operations;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Notifications;

/// <summary>
/// News nobody asks for, looked for now and then: a system alert that starts firing
/// (to every admin), and a person's credit at 80% and used up (to them; used up, to
/// the admins too). Each is said once: per alert firing, per credit threshold and
/// budget (a raised budget says it again when reached); by email and the alerts
/// webhook too (NewsDelivery).
/// </summary>
/// <remarks>Notifications:Watch=false turns the looking off (tests, which call the checks themselves).</remarks>
public sealed partial class NewsWatch(IServiceScopeFactory scopes, IConfiguration config, TimeProvider clock, Replicas replicas, ILogger<NewsWatch> logger) : BackgroundService
{
    public static readonly TimeSpan AlertsEvery = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CreditEvery = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Notifications:Watch", true))
        {
            return;
        }
        try
        {
            // The database and the gateway first, and many app starts at once (tests) not all asking at once.
            await Task.Delay(TimeSpan.FromSeconds(45), clock, stoppingToken);
            var credit = DateTimeOffset.MinValue;
            using var timer = new PeriodicTimer(AlertsEvery, clock);
            do
            {
                // With several replicas, the one that leads looks.
                if (!replicas.IsLeader)
                {
                    continue;
                }
                await CheckAlertsAsync(stoppingToken);
                if (clock.GetUtcNow() - credit >= CreditEvery)
                {
                    await CheckCreditAsync(stoppingToken);
                    credit = clock.GetUtcNow();
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Alerts firing now that the admins were not told of yet.</summary>
    public async Task CheckAlertsAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var alerts = await scope.ServiceProvider.GetRequiredService<AlertmanagerClient>().AlertsAsync(ct);
            var news = scope.ServiceProvider.GetRequiredService<NewsDelivery>();
            foreach (var a in alerts.Where(a => a.State == "active"))
            {
                var severity = a.Severity is { Length: > 0 } s ? char.ToUpperInvariant(s[0]) + s[1..] : "Alert";
                await news.ToAdminsAsync(new News("alert", $"{severity}: {a.Summary ?? a.Name}", a.Description ?? a.Summary, "/admin/alerts",
                    AlertKey(a.Name, a.Labels, a.StartsAt)), ct);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DatasourceException or System.Text.Json.JsonException or DbUpdateException or Npgsql.NpgsqlException)
        {
            LogSkipped(logger, "alerts", ex.Message);
        }
    }

    /// <summary>
    /// People at 80% of a credit of their own, or past it, kind by kind: each said once a month for each credit (a raised
    /// credit says it again when reached).
    /// </summary>
    public async Task CheckCreditAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var spending = await scope.ServiceProvider.GetRequiredService<Ledger>().ReadAsync(ct);
            if (spending.Month is not { } month)
            {
                return;
            }
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var news = scope.ServiceProvider.GetRequiredService<NewsDelivery>();
            var when = month.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            foreach (var u in await db.Users.AsNoTracking().Where(u => !u.IsDisabled && u.Email != null).ToListAsync(ct))
            {
                foreach (var kind in Credits.Kinds)
                {
                    if (Credits.Of(u, kind) is not > 0 || Credits.Of(u, kind) is not { } limit)
                    {
                        continue;
                    }
                    var spent = spending.Of(u.Email, kind);
                    var what = Credits.Label(kind);
                    var of = $"{Money(spent)} of {Money(limit)}";
                    var key = $"{when}:{Credits.Name(kind)}:{limit.ToString("0.####", CultureInfo.InvariantCulture)}";
                    if (spent >= limit)
                    {
                        if (await news.SendAsync(u, new News("usage", $"Your {what} credit is used up",
                            $"You have spent {of} this month. {Refused(kind)} until an admin adds credit, or the month ends.", "/", $"credit:100:{key}"), ct))
                        {
                            await news.ToAdminsAsync(new News("usage", $"{u.DisplayName ?? u.UserName} has used up their {what} credit",
                                $"{of} this month. {Refused(kind)} until you add credit (People).", "/admin/people", $"credit-of:{u.Id}:100:{key}"), ct);
                        }
                    }
                    else if (spent >= limit * 0.8m)
                    {
                        await news.SendAsync(u, new News("usage", $"{Math.Floor(100 * spent / limit)}% of your {what} credit is used",
                            $"You have spent {of} this month. Ask an admin for more before it runs out.", "/", $"credit:80:{key}"), ct);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is GatewayException or HttpRequestException or TaskCanceledException or DbUpdateException or Npgsql.NpgsqlException)
        {
            LogSkipped(logger, "credit", ex.Message);
        }
    }

    /// <summary>What a used-up credit of this kind refuses.</summary>
    private static string Refused(CreditKind kind) => kind switch
    {
        CreditKind.Chat => "The chat's answers are refused",
        CreditKind.Api => "API keys are refused",
        CreditKind.Pictures => "Pictures are refused",
        CreditKind.Video => "Videos are refused",
        _ => "Speech (read aloud, sound turned into text) is refused",
    };

    private static string Money(decimal v) => "$" + v.ToString(v is > 0 and < 0.01m ? "0.####" : "0.00", CultureInfo.InvariantCulture);

    /// <summary>What an alert's news is said once by: its name, labels and start. The app's own alerts told without Alertmanager use it too.</summary>
    public static string AlertKey(string name, IReadOnlyDictionary<string, string> labels, DateTimeOffset startsAt) =>
        $"alert:{name}:{Fingerprint(labels)}:{startsAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}";

    private static string Fingerprint(IReadOnlyDictionary<string, string> labels) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}={l.Value}")))))[..12];

    [LoggerMessage(Level = LogLevel.Debug, Message = "News watch: {What} not checked now: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string what, string reason);
}
