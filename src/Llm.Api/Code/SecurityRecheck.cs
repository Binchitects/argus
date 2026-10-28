using System.Globalization;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Operations;
using Llm.Core.Code;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Code;

/// <summary>
/// A reviewed repository's packages checked again when the advisories are renewed, so a vulnerability published after the
/// review is found in the code it read without a push: each repository's latest security review (of the last
/// <see cref="RecentDays"/> days), checked against advisories newer than its own, at the commit it read, as the person who
/// ran it (Argus's deps_check, within their GitLab rights; no model). What is new joins that review, found now, and the
/// person is told.
/// </summary>
public sealed partial class SecurityRecheck(IServiceScopeFactory scopes, ILogger<SecurityRecheck> logger) : BackgroundService
{
    /// <summary>How often the advisories' day is asked of Argus.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);
    /// <summary>Reviews older than this are not checked again: review the repository again instead.</summary>
    public const int RecentDays = 90;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Every, stoppingToken);
                await RecheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is ArgusException or HttpRequestException or DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
            {
                LogFailed(logger, ex);
            }
        }
    }

    /// <summary>The day of the advisories Argus has installed (its pack's version), or null when it has none.</summary>
    public static DateOnly? AdvisoriesDay(JsonNode? packs) =>
        (packs?["packs"] as JsonArray ?? []).OfType<JsonObject>().Where(p => p["name"]?.ToString() == "advisories")
            .Select(p => DateOnly.TryParseExact(p["version"]?.ToString(), "yyyy.MM.dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null)
            .FirstOrDefault(d => d is not null);

    /// <summary>Checks again every review its advisories are older than; returns how many new findings were made.</summary>
    public async Task<int> RecheckAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var argusAdmin = services.GetRequiredService<ArgusAdmin>();
        var argus = services.GetRequiredService<ArgusMcp>();
        if (!argusAdmin.Enabled || !argus.Enabled || AdvisoriesDay(await argusAdmin.GetAsync("packs", ct)) is not { } day)
        {
            return 0;
        }
        var db = services.GetRequiredService<AppDbContext>();
        var since = DateTimeOffset.UtcNow.AddDays(-RecentDays);
        var done = await db.SecurityScans.AsNoTracking().Where(s => s.Status == SecurityScan.Done && s.FinishedAt >= since)
            .Select(s => new { s.Id, s.Project, s.Ref, s.FinishedAt, s.AdvisoriesAsOf }).ToListAsync(ct);
        // Each repository and ref's latest review only: an older one's packages are the newer one's to say.
        var due = done.GroupBy(s => (s.Project, s.Ref)).Select(g => g.OrderByDescending(s => s.FinishedAt).First())
            .Where(s => s.AdvisoriesAsOf is null || s.AdvisoriesAsOf < day).Select(s => s.Id).ToList();
        var made = 0;
        foreach (var id in due)
        {
            made += await RecheckOneAsync(services, db, argus, id, ct);
        }
        return made;
    }

    private static async Task<int> RecheckOneAsync(IServiceProvider services, AppDbContext db, ArgusMcp argus, Guid id, CancellationToken ct)
    {
        var scan = await db.SecurityScans.Include(s => s.Findings).SingleAsync(s => s.Id == id, ct);
        var user = await services.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(scan.UserId.ToString());
        if (user is null || user.IsDisabled || user.Email is null)
        {
            return 0;
        }
        JsonObject check;
        try
        {
            var session = await argus.ConnectAsync(user.Id, user.Email, ct);
            var args = new JsonObject { ["project"] = scan.Project, ["ref"] = scan.Commit ?? scan.Ref };
            if (args["ref"] is null)
            {
                args.Remove("ref");
            }
            var (text, isError) = await session.CallAsync("deps_check", args, null, ct);
            // The person may no longer read it, or GitLab cannot say: the review stays as it was, and is tried again next time.
            if (isError || JsonNode.Parse(text) is not JsonObject answer)
            {
                return 0;
            }
            check = answer;
        }
        catch (Exception ex) when (ex is McpException or System.Text.Json.JsonException)
        {
            return 0;
        }
        var asOf = (check["advisories"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(a => DateOnly.TryParse(a["as_of"]?.ToString(), CultureInfo.InvariantCulture, out var d) ? d : (DateOnly?)null).FirstOrDefault(d => d is not null);
        static (string, string) Key(SecurityFinding f) => (f.Source?.Split(',')[0].Trim() ?? "", f.SinkCode);
        var known = scan.Findings.Where(f => f.Kind == SecurityFinding.Dependency).Select(Key).ToHashSet();
        var fresh = (check["findings"] as JsonArray ?? []).OfType<JsonObject>().Select(SecurityReview.PackageFinding).Where(f => !known.Contains(Key(f))).ToList();
        foreach (var f in fresh)
        {
            f.ScanId = scan.Id;
            scan.Findings.Add(f);
            db.SecurityFindings.Add(f);
        }
        scan.AdvisoriesAsOf = asOf ?? scan.AdvisoriesAsOf;
        scan.RecheckedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (fresh.Count > 0)
        {
            var exploited = fresh.Count(f => f.Exploited);
            await services.GetRequiredService<Notifications.Notifier>().SendAsync(scan.UserId, new Notifications.News("review",
                $"New known vulnerabilities in {scan.Project}",
                $"{fresh.Count} package version(s) affected by advisories published since the review{(exploited > 0 ? $", {exploited} known to be exploited" : "")}",
                $"/code/security/{scan.Id}"), ct);
            await SecurityOverview.AlertAsync(services, scan, fresh, ct);
        }
        return fresh.Count;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The security reviews' packages could not be checked again")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
