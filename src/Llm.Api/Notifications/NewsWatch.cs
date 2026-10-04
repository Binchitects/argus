using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Llm.Api.Dashboards;
using Llm.Api.Gateway;
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
public sealed partial class NewsWatch(IServiceScopeFactory scopes, IConfiguration config, TimeProvider clock, ILogger<NewsWatch> logger) : BackgroundService
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
                    $"alert:{a.Name}:{Fingerprint(a.Labels)}:{a.StartsAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}"), ct);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DatasourceException or System.Text.Json.JsonException or DbUpdateException or Npgsql.NpgsqlException)
        {
            LogSkipped(logger, "alerts", ex.Message);
        }
    }

    /// <summary>People at 80% of their credit, or past it.</summary>
    public async Task CheckCreditAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var spending = await scope.ServiceProvider.GetRequiredService<Ledger>().ReadAsync(ct);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var news = scope.ServiceProvider.GetRequiredService<NewsDelivery>();
            foreach (var u in await db.Users.AsNoTracking().Where(u => !u.IsDisabled && u.Email != null).ToListAsync(ct))
            {
                if (!spending.People.TryGetValue(u.Email!, out var g) || g.Budget is not > 0)
                {
                    continue;
                }
                var limit = g.Budget!.Value;
                var of = $"{Money(g.Spend)} of {Money(limit)}";
                var key = limit.ToString("0.####", CultureInfo.InvariantCulture);
                if (g.Spend >= limit)
                {
                    if (await news.SendAsync(u, new News("usage", "Your credit is used up",
                        $"You have spent {of}. The chat and your API keys are refused until an admin adds credit.", "/", $"credit:100:{key}"), ct))
                    {
                        await news.ToAdminsAsync(new News("usage", $"{u.DisplayName ?? u.UserName} has used up their credit",
                            $"{of}. Their chat and API keys are refused until you add credit (People).", "/admin/people", $"credit-of:{u.Id}:100:{key}"), ct);
                    }
                }
                else if (g.Spend >= limit * 0.8m)
                {
                    await news.SendAsync(u, new News("usage", $"{Math.Floor(100 * g.Spend / limit)}% of your credit is used",
                        $"You have spent {of}. Ask an admin for more before it runs out.", "/", $"credit:80:{key}"), ct);
                }
            }
        }
        catch (Exception ex) when (ex is GatewayException or HttpRequestException or TaskCanceledException or DbUpdateException or Npgsql.NpgsqlException)
        {
            LogSkipped(logger, "credit", ex.Message);
        }
    }

    private static string Money(decimal v) => "$" + v.ToString(v is > 0 and < 0.01m ? "0.####" : "0.00", CultureInfo.InvariantCulture);

    private static string Fingerprint(IReadOnlyDictionary<string, string> labels) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}={l.Value}")))))[..12];

    [LoggerMessage(Level = LogLevel.Debug, Message = "News watch: {What} not checked now: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string what, string reason);
}
