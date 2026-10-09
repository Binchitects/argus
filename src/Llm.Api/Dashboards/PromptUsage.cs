using System.Globalization;
using System.Security.Claims;
using Llm.Api.Endpoints;
using Llm.Api.Gateway;
using Llm.Api.Quality;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Dashboards;

/// <summary>Who a prompt was for, in the admins' list.</summary>
public sealed record PromptPerson(Guid? Id, string Email, string? Name);

/// <summary>
/// One prompt and what it cost: a chat answer (its rounds, tool calls and sub-agents together), or
/// one request made with an API key. <see cref="Unpriced"/>: parts of it ran before costs were kept.
/// </summary>
/// <param name="Source">"chat" or "api".</param>
/// <param name="Kind">"chat", "picture", "speech", "transcription" or "video".</param>
/// <param name="Model">"Model A" or "Model B" for an answer of a comparison the viewer has not voted on yet.</param>
/// <param name="Cost">Null for such an answer: the prices would tell its model.</param>
public sealed record PromptRow(string Id, DateTimeOffset At, string Source, string Kind, string? Model, long Prompt, long Cached, long Completion, decimal? Cost,
    bool Unpriced, Guid? ChatId = null, string? Title = null, string? Key = null, PromptPerson? Person = null);

/// <summary>Every prompt the filter matches, not only the rows sent.</summary>
/// <param name="Cost">Without the answers of comparisons the viewer has not voted on yet.</param>
/// <param name="Blind">Those answers: their cost is counted once the viewer votes.</param>
public sealed record PromptTotals(long Prompts, long Prompt, long Cached, long Completion, decimal Cost, long Unpriced, long Blind = 0);

/// <param name="Capped">More prompts matched than were sent: the newest are.</param>
/// <param name="Models">The models of the range, to filter by.</param>
/// <param name="Problem">Why the API keys' requests are missing (the gateway's log cannot be read).</param>
public sealed record PromptList(IReadOnlyList<PromptRow> Rows, PromptTotals Totals, bool Capped, IReadOnlyList<string> Models, string? Problem);

/// <param name="People">The people whose chats count; null: everyone.</param>
/// <param name="Emails">The same people by email (lower case), for the gateway's log.</param>
/// <param name="Titles">The chats' titles too: a person's own list only.</param>
/// <param name="Viewer">Who looks: the answers of their comparisons not voted on yet stay blind, as in the chat.</param>
public sealed record PromptFilter(DateTimeOffset From, DateTimeOffset To, string Source = "all", string? Model = null, IReadOnlyCollection<Guid>? People = null,
    IReadOnlyCollection<string>? Emails = null, bool Titles = false, Guid? Viewer = null);

/// <summary>
/// Usage → prompts: each prompt with its tokens (in, cached, out) and cost, newest first. Chat
/// answers come from the chats' own records, with the cost kept as each part ran; requests with
/// API keys from the gateway's request log (the chat's own requests there are its answers, and
/// the small steps around them: titles, summaries, checks, which are in the usage totals only).
/// </summary>
public sealed class PromptUsage(AppDbContext db, SqlDatasource sql)
{
    public const int DefaultLimit = 1000;
    public const int MaxLimit = 5000;

    public async Task<PromptList> ListAsync(PromptFilter f, int limit, CancellationToken ct = default)
    {
        var rows = new List<PromptRow>();
        var totals = new PromptTotals(0, 0, 0, 0, 0, 0);
        var models = new SortedSet<string>(StringComparer.Ordinal);
        var capped = false;
        string? problem = null;
        if (f.Source != "api")
        {
            var (chat, chatTotals, chatModels) = await ChatAsync(f, limit, ct);
            rows.AddRange(chat);
            totals = chatTotals;
            capped |= chatTotals.Prompts > chat.Count;
            models.UnionWith(chatModels);
        }
        if (f.Source != "chat")
        {
            try
            {
                var (api, apiTotals, apiModels) = await ApiAsync(f, limit, ct);
                rows.AddRange(api);
                totals = totals with
                {
                    Prompts = totals.Prompts + apiTotals.Prompts, Prompt = totals.Prompt + apiTotals.Prompt, Cached = totals.Cached + apiTotals.Cached,
                    Completion = totals.Completion + apiTotals.Completion, Cost = totals.Cost + apiTotals.Cost,
                };
                capped |= apiTotals.Prompts > api.Count;
                models.UnionWith(apiModels);
            }
            catch (Npgsql.NpgsqlException ex)
            {
                problem = "The API keys' requests cannot be read right now: " + ex.Message;
            }
        }
        var newest = rows.OrderByDescending(r => r.At).ToList();
        capped |= newest.Count > limit;
        return new PromptList([.. newest.Take(limit)], totals, capped, [.. models], problem);
    }

    /// <summary>
    /// The answers that started in the range, each with its parts added up. An answer of a comparison
    /// the viewer has not voted on yet stays blind, as in the chat and its search: "Model A" or "Model B"
    /// for its model, no cost (the prices would tell the two apart), and never found by its model.
    /// </summary>
    private async Task<(List<PromptRow>, PromptTotals, List<string>)> ChatAsync(PromptFilter f, int limit, CancellationToken ct)
    {
        var blind = f.Viewer is { } viewer
            ? await ChatQuality.BlindAsync(db, viewer, await db.ArenaMatches.AsNoTracking()
                .Where(m => m.UserId == viewer && m.Vote == null && m.ConversationId != null).Select(m => m.ConversationId!.Value).ToListAsync(ct), ct)
            : [];
        var hidden = blind.Keys.ToList();
        var answers = from a in db.ChatMessages.AsNoTracking()
                      join c in db.Conversations.AsNoTracking() on a.ConversationId equals c.Id
                      where a.AnswerId == (Guid?)a.Id && a.CreatedAt >= f.From && a.CreatedAt < f.To
                      select new { a.Id, a.CreatedAt, a.Model, c.UserId, Chat = c.Id, c.Title };
        if (f.People is { } people)
        {
            answers = answers.Where(x => people.Contains(x.UserId));
        }
        var inRange = answers;
        if (f.Model is { Length: > 0 } model)
        {
            answers = answers.Where(x => x.Model == model && !hidden.Contains(x.Id));
        }
        var parts = from x in answers
                    join m in db.ChatMessages.AsNoTracking() on (Guid?)x.Id equals m.AnswerId
                    select new { Answer = x, m.PromptTokens, m.CachedTokens, m.CompletionTokens, m.Cost };
        var found = await (from p in parts
                           group p by p.Answer into g
                           orderby g.Key.CreatedAt descending
                           select new
                           {
                               g.Key.Id, g.Key.CreatedAt, g.Key.Model, g.Key.UserId, g.Key.Chat, g.Key.Title,
                               Prompt = g.Sum(p => (long)(p.PromptTokens ?? 0)), Cached = g.Sum(p => (long)(p.CachedTokens ?? 0)),
                               Completion = g.Sum(p => (long)(p.CompletionTokens ?? 0)), Cost = g.Sum(p => p.Cost ?? 0),
                               Unpriced = g.Count(p => p.Cost == null && p.PromptTokens != null),
                           }).Take(limit + 1).ToListAsync(ct);
        var sums = await parts.GroupBy(_ => 1).Select(g => new
        {
            Prompt = g.Sum(p => (long)(p.PromptTokens ?? 0)), Cached = g.Sum(p => (long)(p.CachedTokens ?? 0)),
            Completion = g.Sum(p => (long)(p.CompletionTokens ?? 0)),
        }).SingleOrDefaultAsync(ct);
        var seen = parts.Where(p => !hidden.Contains(p.Answer.Id));
        var cost = await seen.SumAsync(p => p.Cost ?? 0, ct);
        var count = await answers.LongCountAsync(ct);
        var unpriced = await seen.Where(p => p.Cost == null && p.PromptTokens != null).Select(p => p.Answer.Id).Distinct().LongCountAsync(ct);
        var blinded = hidden.Count == 0 ? 0 : await answers.LongCountAsync(x => hidden.Contains(x.Id), ct);
        var who = f.Titles ? [] : await PeopleAsync(found.Select(r => r.UserId).Distinct().ToList(), ct);
        var rows = found.Select(r =>
        {
            var side = blind.GetValueOrDefault(r.Id);
            return new PromptRow(r.Id.ToString(), r.CreatedAt, "chat", "chat", side is null ? r.Model : Arena.Label(side), r.Prompt, r.Cached, r.Completion,
                side is null ? r.Cost : null, side is null && r.Unpriced > 0, ChatId: f.Titles ? r.Chat : null, Title: f.Titles ? r.Title : null,
                Person: who.GetValueOrDefault(r.UserId));
        }).ToList();
        var totals = new PromptTotals(count, sums?.Prompt ?? 0, sums?.Cached ?? 0, sums?.Completion ?? 0, cost, unpriced, blinded);
        var models = await inRange.Where(x => x.Model != null && !hidden.Contains(x.Id)).Select(x => x.Model!).Distinct().ToListAsync(ct);
        return (rows, totals, models);
    }

    /// <summary>The requests made with API keys in the range: every key but the chat's, the master key's too (attributed to nobody).</summary>
    private async Task<(List<PromptRow>, PromptTotals, List<string>)> ApiAsync(PromptFilter f, int limit, CancellationToken ct)
    {
        var p = new Dictionary<string, object>
        {
            ["from"] = f.From.UtcDateTime, ["to"] = f.To.UtcDateTime, ["chat"] = Chat.ChatKey.Alias, ["limit"] = limit + 1,
        };
        var key = """coalesce(s.metadata->>'user_api_key_alias', v.key_alias, '(master key)')""";
        var where = $"""
             where s."startTime" >= @from and s."startTime" < @to and s.call_type <> '' and coalesce(s.status, 'success') <> 'failure'
               and {key} <> @chat
            """;
        if (f.Emails is { } emails)
        {
            where += $" and lower({UsageEndpoints.Person}) = any(@people)";
            p["people"] = emails.ToArray();
        }
        var inRange = where;
        if (f.Model is { Length: > 0 } model)
        {
            where += $" and {UsageEndpoints.Model} = @model";
            p["model"] = model;
        }
        var found = await sql.QueryAsync($"""
            select s.request_id, s."startTime", {UsageEndpoints.Model}, s.call_type, {key}, lower({UsageEndpoints.Person}),
                   coalesce(s.prompt_tokens, 0), {UsageEndpoints.Cached}, coalesce(s.completion_tokens, 0), coalesce(s.spend, 0)
            {UsageEndpoints.From}{where}
            order by s."startTime" desc limit @limit
            """, p, ct);
        var sums = (await sql.QueryAsync($"""
            select count(*), coalesce(sum(s.prompt_tokens), 0), coalesce(sum({UsageEndpoints.Cached}), 0), coalesce(sum(s.completion_tokens), 0), coalesce(sum(s.spend), 0)
            {UsageEndpoints.From}{where}
            """, p, ct)).Rows[0];
        var models = (await sql.QueryAsync($"select distinct {UsageEndpoints.Model} {UsageEndpoints.From}{inRange}", p, ct)).Rows
            .Select(r => r[0] as string).OfType<string>().Where(m => m.Length > 0).ToList();
        var byEmail = f.Titles ? [] : await PeopleByEmailAsync(found.Rows.Select(r => (string)r[5]!).Distinct().ToList(), ct);
        var rows = found.Rows.Select(r => new PromptRow((string)r[0]!, new DateTimeOffset((DateTime)r[1]!, TimeSpan.Zero), "api", Kind((string)r[3]!), r[2] as string,
            Long(r[6]), Long(r[7]), Long(r[8]), Dec(r[9]), false, Key: (string)r[4]!,
            Person: f.Titles ? null : byEmail.GetValueOrDefault((string)r[5]!) ?? new PromptPerson(null, (string)r[5]!, null))).ToList();
        return (rows, new PromptTotals(Long(sums[0]), Long(sums[1]), Long(sums[2]), Long(sums[3]), Dec(sums[4]), 0), models);
    }

    /// <summary>What a request of the gateway's log made, by its call.</summary>
    public static string Kind(string callType) => callType switch
    {
        "aimage_generation" or "image_generation" => "picture",
        "aspeech" or "speech" => "speech",
        "atranscription" or "transcription" => "transcription",
        SpendLog.VideoCall => "video",
        _ => "chat",
    };

    private async Task<Dictionary<Guid, PromptPerson>> PeopleAsync(List<Guid> ids, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new PromptPerson(u.Id, u.Email ?? "", u.DisplayName ?? u.UserName), ct);

    private async Task<Dictionary<string, PromptPerson>> PeopleByEmailAsync(List<string> emails, CancellationToken ct)
    {
        var normalized = emails.Select(e => e.ToUpperInvariant()).ToList();
        return (await db.Users.AsNoTracking().Where(u => u.NormalizedEmail != null && normalized.Contains(u.NormalizedEmail)).ToListAsync(ct))
            .GroupBy(u => u.Email!.ToLowerInvariant()).ToDictionary(g => g.Key, g => new PromptPerson(g.First().Id, g.Key, g.First().DisplayName ?? g.First().UserName));
    }

    private static long Long(object? v) => v is null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);

    private static decimal Dec(object? v) => v is null ? 0 : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}

/// <summary>Past costs to work out again: the time range, only those booked as free or every one, and whether to change them (else they are only counted).</summary>
public sealed record RecalculateRequest(DateTimeOffset From, DateTimeOffset To, bool OnlyFree = true, bool Apply = false);

/// <summary>
/// Usage → prompts: a person's own (everyone), everyone's (admins: by person, group, model, date; no
/// chat titles, only who, when, which model, tokens and cost). And the admins' recalculation of past costs.
/// </summary>
public static class PromptEndpoints
{
    public static void MapPrompts(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/usage/prompts", MineAsync).RequireAuthorization();
        var admin = app.MapGroup("/api/admin/usage").RequireAuthorization(AdminEndpoints.Policy);
        admin.MapGet("/prompts", EveryoneAsync);
        admin.MapPost("/recalculate", RecalculateAsync);
    }

    private static string? Check(DateTimeOffset from, DateTimeOffset to, string? source) =>
        to <= from || to - from > DashboardEndpoints.MaxRange ? "The time range must be positive and at most 400 days."
        : source is not (null or "all" or "chat" or "api") ? "The source is all, chat or api."
        : null;

    private static async Task<IResult> MineAsync(DateTimeOffset from, DateTimeOffset to, string? source, string? model, int? limit, ClaimsPrincipal principal,
        UserManager<AppUser> users, PromptUsage prompts, CancellationToken ct)
    {
        if (Check(from, to, source) is { } bad)
        {
            return AuthEndpoints.Problem(400, "range", bad);
        }
        var me = (await users.GetUserAsync(principal))!;
        return Results.Ok(await prompts.ListAsync(new PromptFilter(from, to, source ?? "all", model, [me.Id], [me.Email!.ToLowerInvariant()], Titles: true, Viewer: me.Id),
            Math.Clamp(limit ?? PromptUsage.DefaultLimit, 1, PromptUsage.MaxLimit), ct));
    }

    private static async Task<IResult> EveryoneAsync(DateTimeOffset from, DateTimeOffset to, string? source, string? model, Guid? person, Guid? group, int? limit,
        ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, Credit credit, PromptUsage prompts, CancellationToken ct)
    {
        if (Check(from, to, source) is { } bad)
        {
            return AuthEndpoints.Problem(400, "range", bad);
        }
        List<Guid>? ids = null;
        List<string>? emails = null;
        if (group is { } g)
        {
            if (await db.Groups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == g, ct) is not { } found)
            {
                return AuthEndpoints.Problem(404, "group", "There is no such group.");
            }
            emails = await credit.MembersAsync(found, ct);
            var upper = emails.Select(e => e.ToUpperInvariant()).ToList();
            ids = await db.Users.AsNoTracking().Where(u => u.NormalizedEmail != null && upper.Contains(u.NormalizedEmail)).Select(u => u.Id).ToListAsync(ct);
        }
        if (person is { } p)
        {
            if (await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == p, ct) is not { Email: { } email })
            {
                return AuthEndpoints.Problem(404, "person", "There is no such person.");
            }
            // Within the group, when both are given.
            ids = ids is null || ids.Contains(p) ? [p] : [];
            emails = emails is null || emails.Contains(email.ToLowerInvariant()) ? [email.ToLowerInvariant()] : [];
        }
        // The admin's own comparisons not voted on yet stay blind to them here too.
        var me = (await users.GetUserAsync(principal))!;
        return Results.Ok(await prompts.ListAsync(new PromptFilter(from, to, source ?? "all", model, ids, emails, Viewer: me.Id),
            Math.Clamp(limit ?? PromptUsage.DefaultLimit, 1, PromptUsage.MaxLimit), ct));
    }

    /// <summary>Counts what would change; with apply, changes it (audited).</summary>
    private static async Task<IResult> RecalculateAsync(RecalculateRequest body, CostRecalculation recalculation, CancellationToken ct)
    {
        if (Check(body.From, body.To, null) is { } bad)
        {
            return AuthEndpoints.Problem(400, "range", bad);
        }
        try
        {
            return Results.Ok(await recalculation.RunAsync(body.From, body.To, body.OnlyFree, body.Apply, ct));
        }
        catch (Npgsql.NpgsqlException ex)
        {
            return AuthEndpoints.Problem(503, "log", "The gateway's request log cannot be changed right now: " + ex.Message);
        }
    }
}
