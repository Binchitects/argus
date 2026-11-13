using System.Globalization;
using Llm.Api.Gateway;
using Llm.Api.Reports;
using Llm.Core.Data;
using Llm.Core.Projects;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Projects;

/// <param name="AgentSeconds">The time the models were answering: requests at once counted once.</param>
/// <param name="ActiveSeconds">The people's own time: reading the answers and writing back (from an answer's end to their next message; a longer break than <see cref="ProjectReport.Idle"/> a minute).</param>
/// <param name="WaitingSeconds">The time the people waited for the answers: from each question to the end of its answer.</param>
public sealed record ProjectTotals(long Requests, long Input, long Cached, long Output, decimal Cost, double AgentSeconds, double ActiveSeconds, int People, int Messages, double WaitingSeconds = 0);

public sealed record ProjectBucket(DateTimeOffset At, long Input, long Output, decimal Cost);

/// <summary>A person's share: their requests and what they cost, the model's time on them (agent) and their own time in the chats (active).</summary>
/// <param name="Input">Input not read from the prompt cache; <paramref name="Cached"/> read from it; <paramref name="Output"/> the answers' (adding up to <paramref name="Tokens"/>).</param>
public sealed record ProjectPerson(Guid? Id, string Name, string? Email, long Requests, long Tokens, decimal Cost, double AgentSeconds, double ActiveSeconds, int Messages,
    long Input = 0, long Cached = 0, long Output = 0, double WaitingSeconds = 0);

public sealed record ProjectUse(string Name, long Requests, long Tokens, decimal Cost);

public sealed record ProjectProgress(int Goals, int GoalsDone, int Tasks, int TasksDone, int TasksDoneInRange, int Steps, int StepsDone, int Chats, int Rooms, int Sessions);

/// <summary>
/// The work done in a time against doing it by hand: the hours a person would have taken (the estimates of the tasks done,
/// and of the goals done with no estimated task), the hours people spent (their active time in the project's chats) and
/// the model's, and both at a person's hour (Settings → Costs) with the project's spend added to the done way.
/// </summary>
public sealed record ProjectHuman(int Estimated, int NotEstimated, double ByHandHours, double PeopleHours, double ModelHours, decimal HourlyRate, decimal ByHandCost, decimal ActualCost)
{
    /// <summary>How many times faster than by hand; null with nothing estimated or no one's time.</summary>
    public double? Faster => ByHandHours > 0 && PeopleHours > 0 ? Math.Round(ByHandHours / PeopleHours, 1) : null;

    public double SavedHours => Math.Round(ByHandHours - PeopleHours, 1);

    public decimal SavedCost => ByHandCost - ActualCost;
}

/// <summary>What a project used and did over a time: every number its page and its exports show.</summary>
public sealed record ProjectNumbers(DateTimeOffset From, DateTimeOffset To, TimeSpan Interval, ProjectTotals Totals, List<ProjectBucket> Series, List<ProjectPerson> People,
    List<ProjectUse> Models, List<ProjectUse> Surfaces, ProjectProgress Progress, decimal SpentThisMonth, ProjectHuman Human);

/// <summary>
/// A project's accounting over a time, from the ledger (gateway.requests tagged with it: tokens in and out, cost, requests,
/// the model's time, people, models, where they came from) and from its chats (the people's active time: their messages a
/// stretch at a time, a gap of more than <see cref="Idle"/> ending one); and its progress (goals, tasks, its chats' plans'
/// steps). Refusals never reached a model and are left out, as the usage pages leave them.
/// </summary>
public static class ProjectReport
{
    /// <summary>Messages closer than this are one stretch of work; each stretch counts a minute more for its first message.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(15);

    /// <summary>The buckets a time is cut into: hours up to two days, days up to 92, else weeks.</summary>
    public static TimeSpan IntervalFor(DateTimeOffset from, DateTimeOffset to) =>
        to - from <= TimeSpan.FromDays(2) ? TimeSpan.FromHours(1) : to - from <= TimeSpan.FromDays(92) ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);

    public static async Task<ProjectNumbers> ReadAsync(LedgerSql sql, AppDbContext db, Project project, DateTimeOffset from, DateTimeOffset to, TimeSpan interval, decimal hourly, CancellationToken ct)
    {
        var p = new Dictionary<string, object>
        {
            ["p"] = project.Id,
            ["from"] = from.ToUniversalTime(),
            ["to"] = to.ToUniversalTime(),
            ["secs"] = interval.TotalSeconds,
        };
        const string Where = " from gateway.requests r where r.project_id = @p and r.started_at >= @from and r.started_at < @to and r.status <> 'refused' ";
        var totals = (await sql.QueryAsync($"""
            select count(*), coalesce(sum(greatest(r.prompt_tokens - r.cached_tokens, 0)),0), coalesce(sum(r.cached_tokens),0), coalesce(sum(r.completion_tokens),0),
                   coalesce(sum(r.cost),0), coalesce(sum(r.duration_ms),0)
            {Where}
            """, p, ct)).Rows[0];
        var byTime = await sql.QueryAsync($"""
            select floor(extract(epoch from r.started_at)/@secs)*@secs, coalesce(sum(r.prompt_tokens),0), coalesce(sum(r.completion_tokens),0), coalesce(sum(r.cost),0)
            {Where} group by 1 order by 1
            """, p, ct);
        var byPerson = await sql.QueryAsync($"""
            select r.user_id, max(r.person), count(*), coalesce(sum(r.total_tokens),0), coalesce(sum(r.cost),0), coalesce(sum(r.duration_ms),0),
                   coalesce(sum(greatest(r.prompt_tokens - r.cached_tokens, 0)),0), coalesce(sum(r.cached_tokens),0), coalesce(sum(r.completion_tokens),0)
            {Where} group by 1
            """, p, ct);
        var byModel = await sql.QueryAsync($"select r.model, count(*), coalesce(sum(r.total_tokens),0), coalesce(sum(r.cost),0) {Where} group by 1 order by 4 desc, 2 desc", p, ct);
        var bySurface = await sql.QueryAsync($"select r.surface, count(*), coalesce(sum(r.total_tokens),0), coalesce(sum(r.cost),0) {Where} group by 1 order by 4 desc, 2 desc", p, ct);
        // The models' time: each request from its start to its end, those at once (sub-agents, steps side by side) counted
        // once: the islands of overlapping requests, per person and for the whole project.
        static string Busy(string who) => $"""
            with r as (select {who} as who, r.started_at as s, greatest(r.ended_at, r.started_at) as e {Where}),
            o as (select who, s, e, max(e) over (partition by who order by s, e rows between unbounded preceding and 1 preceding) as prev from r),
            i as (select who, s, e, sum(case when prev is null or s > prev then 1 else 0 end) over (partition by who order by s, e) as island from o)
            select who, coalesce(sum(extract(epoch from l - f)), 0) from (select who, island, min(s) as f, max(e) as l from i group by who, island) x group by who
            """;
        var busyBy = (await sql.QueryAsync(Busy("r.user_id::text"), p, ct)).Rows.Where(r => r[0] is string).ToDictionary(r => Guid.Parse((string)r[0]!), r => Dbl(r[1]));
        var busyAll = (await sql.QueryAsync(Busy("1"), p, ct)).Rows.Select(r => Dbl(r[1])).FirstOrDefault();
        // The people's own time and their waiting, from the project's chats (Code Arena's sessions are among them): each
        // question's time since the answer before it ended (reading, then writing; a break past the idle time is a minute),
        // as the time accounting counts it (gateway.chat_times, Chat.TimeSplit), and from it to the end of its answer
        // (waiting). Code Arena's messages are put down as they end; the web's start then and run their time.
        var active = await sql.QueryAsync("""
            with ev as (
              select m."Id" as id, c."Id" as conv, coalesce(m."AuthorId", c."UserId") as who, m."Role" as role, m."CreatedAt" as at,
                     m."CreatedAt" + make_interval(secs => case when m."SyncRef" is null then coalesce(m."DurationMs", 0) else 0 end / 1000.0) as ends
              from chat_messages m join conversations c on c."Id" = m."ConversationId"
              where c."WorkProjectId" = @p and c."DeletedAt" is null and m."Role" in ('user', 'assistant') and m."CreatedAt" >= @from and m."CreatedAt" < @to
            ), q as (
              select u.id, u.who, u.at,
                     (select max(a.ends) from ev a where a.conv = u.conv and a.role = 'assistant' and a.at < u.at) as before_end,
                     (select max(a.ends) from ev a where a.conv = u.conv and a.role = 'assistant' and a.at >= u.at
                        and a.at < coalesce((select min(n.at) from ev n where n.conv = u.conv and n.role = 'user' and n.at > u.at), 'infinity'::timestamptz)) as answered
              from ev u where u.role = 'user'
            )
            select q.who, count(*), coalesce(sum(t.ms), 0) / 1000.0, coalesce(sum(greatest(extract(epoch from q.answered - q.at), 0)), 0)
            from q left join gateway.chat_times t on t.message = q.id
            group by q.who
            """, p, ct);

        var seconds = active.Rows.Where(r => r[0] is string).ToDictionary(r => Guid.Parse((string)r[0]!), r => (Messages: (int)Num(r[1]), Seconds: Dbl(r[2]), Waiting: Dbl(r[3])));
        var ids = byPerson.Rows.Select(r => Id(r[0])).OfType<Guid>().Concat(seconds.Keys).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, Name = u.DisplayName == null || u.DisplayName == "" ? u.UserName : u.DisplayName, u.Email }).ToDictionaryAsync(u => u.Id, ct);
        var people = new List<ProjectPerson>();
        foreach (var r in byPerson.Rows)
        {
            var id = Id(r[0]);
            var (messages, secs, waiting) = id is { } i && seconds.TryGetValue(i, out var s) ? s : (0, 0d, 0d);
            var known = id is { } k && names.TryGetValue(k, out var n) ? n : null;
            var busy = id is { } b && busyBy.TryGetValue(b, out var bs) ? bs : 0;
            people.Add(new ProjectPerson(id, known?.Name ?? (r[1] as string) ?? "(no one)", known?.Email ?? r[1] as string, Num(r[2]), Num(r[3]), Dec(r[4]), busy, secs, messages,
                Num(r[6]), Num(r[7]), Num(r[8]), waiting));
        }
        foreach (var (id, (messages, secs, waiting)) in seconds.Where(s => people.All(x => x.Id != s.Key)))
        {
            var known = names.GetValueOrDefault(id);
            people.Add(new ProjectPerson(id, known?.Name ?? "(a former member)", known?.Email, 0, 0, 0, 0, secs, messages, WaitingSeconds: waiting));
        }
        people = [.. people.OrderByDescending(x => x.Cost).ThenByDescending(x => x.ActiveSeconds).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)];

        var points = byTime.Rows.ToDictionary(r => (long)Dbl(r[0]), r => (Input: Num(r[1]), Output: Num(r[2]), Cost: Dec(r[3])));
        var series = new List<ProjectBucket>();
        var step = (long)interval.TotalSeconds;
        for (var at = (long)Math.Floor(from.ToUnixTimeSeconds() / (double)step) * step; at < to.ToUnixTimeSeconds() && series.Count < 2000; at += step)
        {
            var v = points.GetValueOrDefault(at);
            series.Add(new ProjectBucket(DateTimeOffset.FromUnixTimeSeconds(at), v.Input, v.Output, v.Cost));
        }

        var goalRows = await db.ProjectGoals.AsNoTracking().Where(g => g.ProjectId == project.Id).Select(g => new { g.Id, g.Status, g.DoneAt, g.HumanHours }).ToListAsync(ct);
        var goals = goalRows.Select(g => g.Status).ToList();
        var tasks = await db.ProjectTasks.AsNoTracking().Where(t => t.ProjectId == project.Id).Select(t => new { t.GoalId, t.Done, t.DoneAt, t.HumanHours }).ToListAsync(ct);
        var chats = await db.Conversations.AsNoTracking().Where(c => c.WorkProjectId == project.Id).Select(c => new { c.Id, c.Kind, c.Origin }).ToListAsync(ct);
        var chatIds = chats.Select(c => c.Id).ToList();
        var steps = await db.PlanSteps.AsNoTracking().Where(s => db.Plans.Any(pl => pl.Id == s.PlanId && pl.ConversationId != null && chatIds.Contains(pl.ConversationId.Value)))
            .Select(s => s.Status).ToListAsync(ct);
        var progress = new ProjectProgress(goals.Count(g => g != ProjectGoal.Dropped), goals.Count(g => g == ProjectGoal.Done), tasks.Count, tasks.Count(t => t.Done),
            tasks.Count(t => t.Done && t.DoneAt >= from && t.DoneAt < to), steps.Count, steps.Count(s => s == Core.Plans.PlanStep.Done),
            chats.Count(c => c.Kind == null && c.Origin != Chat.CodeArenaSync.Origin), chats.Count(c => c.Kind == Core.Chat.RoomMember.Room), chats.Count(c => c.Origin == Chat.CodeArenaSync.Origin));

        var month = (await ProjectBudgets.SpentAsync(db, [project.Id], DateTimeOffset.UtcNow, ct)).GetValueOrDefault(project.Id);
        var total = new ProjectTotals(Num(totals[0]), Num(totals[1]), Num(totals[2]), Num(totals[3]), Dec(totals[4]), busyAll,
            seconds.Values.Sum(s => s.Seconds), people.Count(x => x.Requests > 0 || x.Messages > 0), seconds.Values.Sum(s => s.Messages), seconds.Values.Sum(s => s.Waiting));
        // The work done in the time, by hand: each task's estimate; a goal's own only when none of its tasks has one.
        bool InRange(DateTimeOffset? at) => at >= from && at < to;
        var estimatedGoals = tasks.Where(t => t.HumanHours is not null && t.GoalId is not null).Select(t => t.GoalId!.Value).ToHashSet();
        var doneTasks = tasks.Where(t => t.Done && InRange(t.DoneAt)).ToList();
        var doneGoals = goalRows.Where(g => g.Status == ProjectGoal.Done && InRange(g.DoneAt) && !estimatedGoals.Contains(g.Id)).ToList();
        var byHand = (double)(doneTasks.Sum(t => t.HumanHours ?? 0) + doneGoals.Sum(g => g.HumanHours ?? 0));
        var peopleHours = total.ActiveSeconds / 3600;
        var human = new ProjectHuman(doneTasks.Count(t => t.HumanHours is not null) + doneGoals.Count(g => g.HumanHours is not null),
            doneTasks.Count(t => t.HumanHours is null) + doneGoals.Count(g => g.HumanHours is null),
            Math.Round(byHand, 2), Math.Round(peopleHours, 2), Math.Round(total.AgentSeconds / 3600, 2), hourly,
            Math.Round((decimal)byHand * hourly, 2), Math.Round(total.Cost + (decimal)peopleHours * hourly, 2));
        return new ProjectNumbers(from, to, interval, total, series, people,
            [.. byModel.Rows.Select(r => new ProjectUse((string?)r[0] ?? "", Num(r[1]), Num(r[2]), Dec(r[3])))],
            [.. bySurface.Rows.Select(r => new ProjectUse(Surface((string?)r[0]), Num(r[1]), Num(r[2]), Dec(r[3])))],
            progress, month, human);
    }

    /// <summary>Where requests came from, as people say it.</summary>
    public static string Surface(string? s) => s switch
    {
        "chat" => "Chat",
        "api" => "API keys and Code Arena",
        "app" => "The app's own work (titles, summaries)",
        "studio" => "Code Studio",
        "media" => "Pictures, video and speech",
        _ => s ?? "",
    };

    /// <summary>
    /// The project's report for its exports. <paramref name="merged"/>: its repositories' changes merged in the time (as the
    /// person who asked may read them). Each person's row only when <paramref name="everyone"/> (an owner asks); else their own.
    /// </summary>
    public static ReportDocument Document(Project project, ProjectNumbers n, IReadOnlyList<ProjectGoal> goals, IReadOnlyList<(string Repository, int Iid, string Title, string? Author, DateTimeOffset? MergedAt)> merged,
        bool everyone, Guid me, string madeFor, DateTimeOffset made)
    {
        var t = n.Totals;
        var progress = n.Progress;
        var figures = new List<ReportFigure>
        {
            new("Cost", ReportFiles.Money(t.Cost), project.MonthlyBudget is { } b ? $"This month {ReportFiles.Money(n.SpentThisMonth)} of {ReportFiles.Money(b)}" : null),
            new("Tokens", ReportFiles.Compact(t.Input + t.Cached + t.Output), $"{ReportFiles.Compact(t.Input + t.Cached)} in ({ReportFiles.Compact(t.Cached)} cached), {ReportFiles.Compact(t.Output)} out"),
            new("Requests", t.Requests.ToString("#,##0", CultureInfo.InvariantCulture), $"{n.Models.Count} model{(n.Models.Count == 1 ? "" : "s")}"),
            new("People", t.People.ToString(CultureInfo.InvariantCulture), $"{t.Messages:#,##0} messages"),
            new("Active time", ReportFiles.Hours(t.ActiveSeconds), "People's own time in the chats"),
            new("Agent time", ReportFiles.Hours(t.AgentSeconds), "The models' time answering, at once counted once"),
            new("Waiting", ReportFiles.Hours(t.WaitingSeconds), "People's time waiting for the answers"),
            new("Goals done", $"{progress.GoalsDone} of {progress.Goals}", progress.Goals == 0 ? "No goals yet" : $"{100 * progress.GoalsDone / progress.Goals}%"),
            new("Tasks done", $"{progress.TasksDone + progress.StepsDone} of {progress.Tasks + progress.Steps}", $"{progress.TasksDoneInRange} in this time; {merged.Count} change{(merged.Count == 1 ? "" : "s")} merged"),
        };
        if (n.Human.Estimated > 0)
        {
            var h = n.Human;
            figures.Add(new("By hand", ReportFiles.Hours(h.ByHandHours * 3600), $"{h.Estimated} estimated piece{(h.Estimated == 1 ? "" : "s")} of work done; {ReportFiles.Money(h.ByHandCost)} at {ReportFiles.Money(h.HourlyRate)} an hour"));
            figures.Add(new("Done in", ReportFiles.Hours(h.PeopleHours * 3600), h.Faster is { } x ? $"{x.ToString("0.0", CultureInfo.InvariantCulture)}× faster; {ReportFiles.Money(h.ActualCost)} with the models' cost" : $"{ReportFiles.Money(h.ActualCost)} with the models' cost"));
        }
        var charts = new List<ReportChart>
        {
            new("Tokens", ["Input", "Output"], [.. n.Series.Select(s => (s.At, new double[] { s.Input, s.Output }))]),
            new("Cost", ["Cost"], [.. n.Series.Select(s => (s.At, new double[] { (double)s.Cost }))], ReportKind.Money),
        };
        var shown = everyone ? n.People : [.. n.People.Where(x => x.Id == me)];
        var tables = new List<ReportTable>
        {
            new("People", [new("Person", Width: 2.2), new("Requests", ReportKind.Number), new("Tokens", ReportKind.Number), new("In", ReportKind.Number), new("Cached", ReportKind.Number),
                    new("Out", ReportKind.Number), new("Cost", ReportKind.Money), new("Agent time", ReportKind.Hours), new("Active time", ReportKind.Hours), new("Waiting", ReportKind.Hours), new("Messages", ReportKind.Number, 0.8)],
                [.. shown.Select(x => (object?[])[x.Name, x.Requests, x.Tokens, x.Input, x.Cached, x.Output, x.Cost, x.AgentSeconds, x.ActiveSeconds, x.WaitingSeconds, x.Messages])],
                everyone ? null : "Only your own row: each person's share is for the project's owners."),
            new("Models", [new("Model", Width: 2.4), new("Requests", ReportKind.Number), new("Tokens", ReportKind.Number), new("Cost", ReportKind.Money)],
                [.. n.Models.Select(m => (object?[])[m.Name, m.Requests, m.Tokens, m.Cost])]),
            new("Where it came from", [new("From", Width: 2.4), new("Requests", ReportKind.Number), new("Tokens", ReportKind.Number), new("Cost", ReportKind.Money)],
                [.. n.Surfaces.Select(m => (object?[])[m.Name, m.Requests, m.Tokens, m.Cost])]),
            new("Goals", [new("Goal", Width: 3), new("State"), new("Due"), new("Done", ReportKind.Time, 1.3)],
                [.. goals.Select(g => (object?[])[g.Title, g.Status, g.DueOn?.ToString("d MMM yyyy", CultureInfo.InvariantCulture), g.DoneAt])]),
            new("Merged changes", [new("Repository", Width: 1.6), new("Change", Width: 0.6), new("Title", Width: 3), new("By", Width: 1.2), new("Merged", ReportKind.Time, 1.3)],
                [.. merged.Select(m => (object?[])[m.Repository, "!" + m.Iid.ToString(CultureInfo.InvariantCulture), m.Title, m.Author, m.MergedAt])]),
            new("By time", [new("From", ReportKind.Time, 1.4), new("Input tokens", ReportKind.Number), new("Output tokens", ReportKind.Number), new("Cost", ReportKind.Money)],
                [.. n.Series.Where(s => s.Input + s.Output > 0 || s.Cost > 0).Select(s => (object?[])[s.At, s.Input, s.Output, s.Cost])]),
        };
        var subtitle = (project.Description.Length > 0 ? project.Description.Trim() + " " : "")
            + $"{progress.Chats} chat{(progress.Chats == 1 ? "" : "s")}, {progress.Rooms} room{(progress.Rooms == 1 ? "" : "s")} and {progress.Sessions} Code Arena session{(progress.Sessions == 1 ? "" : "s")}"
            + (project.DueOn is { } due ? $"; due {due.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}." : ".");
        return new ReportDocument(project.Name, subtitle, n.From, n.To, made, figures, charts, tables, madeFor);
    }

    private static Guid? Id(object? v) => v is string s && Guid.TryParse(s, out var g) ? g : null;

    private static long Num(object? v) => v is null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);

    private static double Dbl(object? v) => v is null ? 0 : Convert.ToDouble(v, CultureInfo.InvariantCulture);

    private static decimal Dec(object? v) => v is null ? 0 : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}
