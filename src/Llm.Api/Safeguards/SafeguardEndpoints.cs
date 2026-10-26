using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Notifications;
using Llm.Core.Access;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Safeguards;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Safeguards;

/// <summary>A rule as the admin's form sends it.</summary>
public sealed record RuleRequest(
    string Name, string Detector, List<string> Stages, string Action,
    string? Description = null, bool Enabled = true, bool Monitor = false, List<Guid>? Groups = null, List<Guid>? People = null, List<string>? Models = null,
    string? Message = null, string? RerouteModel = null, bool Strike = false, List<string>? Kinds = null, string? Patterns = null, string? Topics = null,
    List<string>? Categories = null, List<string>? Domains = null, bool DomainsAllowed = false, List<string>? Extensions = null, int? MaxFileMb = null, bool ModelCheck = false);

/// <summary>A text for the test bench: where it is, whose (a person's groups decide which rules apply) and for which model.</summary>
public sealed record TrialRequest(string Text, string Stage = "question", Guid? Person = null, string? Model = null, string? FileName = null, int? FileMb = null);

public sealed record ReviewAnswer(bool Allow, string? Note = null);

public sealed record ReviewRequest(string Reason);

/// <summary>
/// Admin → Safeguards: the rules (made, changed, ordered, removed), a test bench, what the rules did (events, with counts
/// by day and by rule), and people's requests to review a refusal. People ask for a review of their own refusals.
/// </summary>
public static class SafeguardEndpoints
{
    public static void MapSafeguards(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/safeguards").RequirePermissions(Permissions.SafeguardsRead, Permissions.SafeguardsManage, new Dictionary<string, string>
        {
            ["POST /api/admin/safeguards/test"] = Permissions.SafeguardsRead,
            ["POST /api/admin/safeguards/events/{id:guid}/decide"] = Permissions.SafeguardsReview,
        });
        g.MapGet("/rules", RulesAsync);
        g.MapPost("/rules", CreateAsync);
        g.MapPut("/rules/{id:guid}", UpdateAsync);
        g.MapDelete("/rules/{id:guid}", DeleteAsync);
        g.MapPost("/rules/order", OrderAsync);
        g.MapGet("/templates", () => Results.Ok(Templates));
        g.MapPost("/test", TestAsync);
        g.MapGet("/events", EventsAsync);
        g.MapGet("/stats", StatsAsync);
        g.MapPost("/events/{id:guid}/decide", DecideAsync);

        var mine = app.MapGroup("/api/safeguards").PersonOnly();
        mine.MapGet("/mine", MineAsync);
        mine.MapPost("/events/{id:guid}/review", AskReviewAsync);
    }

    /// <summary>Rules to start from: the form opens filled with one.</summary>
    public static readonly IReadOnlyList<object> Templates =
    [
        Template("Secrets", "Private keys, tokens and passwords in what people send: refused, never stored.", "secrets", ["question", "file", "api"], "block"),
        Template("Secrets in answers", "A secret the model repeats (from a file or a tool) is masked in its answer.", "secrets", ["answer", "tool_result"], "mask"),
        Template("Personal data to cloud models", "Personal data reaches models on other providers masked; the company's own models see it.", "pii", ["question", "api"], "mask", models: ["@external"]),
        Template("Personal data in answers", "E-mail addresses, phone and card numbers in answers masked.", "pii", ["answer"], "mask"),
        Template("Health and identity data to cloud models", "Dates of birth, medical record, NHS, national, social security and Medicare numbers never reach models on other providers: the question is refused (HIPAA, the GDPR's special categories).",
            "pii", ["question", "file", "api"], "block", models: ["@external"], kinds: Pii.HealthKinds),
        Template("Health and identity data masked", "The same identifiers reach the company's own models masked, and are masked in answers.", "pii", ["question", "file", "api", "answer", "tool_result"], "mask",
            kinds: Pii.HealthKinds),
        Template("Classification labels", "Documents marked confidential do not go to models on other providers.", "words", ["question", "file", "api"], "block", models: ["@external"],
            patterns: "CONFIDENTIAL; STRICTLY CONFIDENTIAL; INTERNAL ONLY; TOP SECRET; re:\\bRESTRICTED\\b"),
        Template("Harmful requests", "The model reads each message and refuses one asking for harm.", "topic", ["question", "api"], "block", strike: true, categories: [.. Safeguards.Categories.Keys]),
        Template("Legal and medical advice", "People are reminded the assistant is not a lawyer or a doctor, and say why they ask.", "topic", ["question"], "justify",
            topics: "legal advice: what someone should do in their own legal matter\nmedical advice: diagnosis or treatment of someone's own health"),
        Template("Jailbreaks", "Messages trying to get round the assistant's rules.", "injection", ["question", "api"], "block", modelCheck: true),
        Template("Prompt injection in what tools bring", "Instructions hidden in web pages, documents and tool results, removed before the model reads them.", "injection", ["tool_result"], "mask"),
        Template("File-sharing sites", "Links to file-sharing and paste sites refused in messages, tool calls and answers.", "links", ["question", "tool_call", "answer"], "block",
            domains: ["wetransfer.com", "dropbox.com", "mega.nz", "pastebin.com", "file.io", "transfer.sh"]),
        Template("Programs", "Programs and scripts are not attached.", "files", ["file"], "block", extensions: [".exe", ".dll", ".msi", ".bat", ".cmd", ".ps1", ".scr", ".jar", ".sh"]),
        Template("Competitors", "People are asked to confirm before naming a competitor.", "words", ["question"], "warn", patterns: "Competitor One; Competitor Two"),
    ];

    private static object Template(string name, string description, string detector, string[] stages, string action, string[]? models = null, string? patterns = null,
        bool strike = false, string[]? categories = null, string? topics = null, bool modelCheck = false, string[]? domains = null, string[]? extensions = null, string[]? kinds = null) =>
        new { name, description, detector, stages, action, models = models ?? [], patterns, strike, categories = categories ?? [], topics, modelCheck, domains = domains ?? [], extensions = extensions ?? [], kinds = kinds ?? [] };

    private static object Shape(SafeguardRule r) => new
    {
        r.Id, r.Name, r.Description, r.Order, r.Enabled, r.Monitor, r.Detector, r.Stages, r.Groups, r.People, r.Models, r.Action, r.Message, r.RerouteModel,
        r.Strike, r.Kinds, r.Patterns, r.Topics, r.Categories, r.Domains, r.DomainsAllowed, r.Extensions, r.MaxFileMb, r.ModelCheck, r.UpdatedAt,
    };

    private static async Task<IResult> RulesAsync(AppDbContext db, CancellationToken ct)
    {
        var rules = await db.SafeguardRules.AsNoTracking().OrderBy(r => r.Order).ThenBy(r => r.CreatedAt).ToListAsync(ct);
        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var counts = await db.SafeguardEvents.AsNoTracking().Where(e => e.At > since && e.RuleId != null).GroupBy(e => e.RuleId)
            .Select(x => new { Id = x.Key!.Value, Count = x.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        return Results.Ok(new
        {
            rules = rules.Select(r => new { rule = Shape(r), week = counts.GetValueOrDefault(r.Id) }),
            detectors = SafeguardRule.AllDetectors, stages = SafeguardRule.AllStages, actions = SafeguardRule.AllActions,
            categories = Safeguards.Categories.Select(c => new { key = c.Key, description = c.Value }),
            secretKinds = Secrets.AllKinds, piiKinds = Pii.AllKinds,
        });
    }

    /// <summary>What is wrong with a rule, for its form; null when it can be saved.</summary>
    public static string? Invalid(RuleRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 200)
        {
            return "A rule needs a name of up to 200 characters.";
        }
        if (!SafeguardRule.AllDetectors.Contains(r.Detector))
        {
            return $"The detector is one of {string.Join(", ", SafeguardRule.AllDetectors)}.";
        }
        if (!SafeguardRule.AllActions.Contains(r.Action))
        {
            return $"The action is one of {string.Join(", ", SafeguardRule.AllActions)}.";
        }
        if (r.Stages is not { Count: > 0 } || r.Stages.Any(s => !SafeguardRule.AllStages.Contains(s)))
        {
            return $"Say where it looks: {string.Join(", ", SafeguardRule.AllStages)}.";
        }
        if (r.Action == "mask" && r.Detector is "topic" or "files")
        {
            return "A topic or a file has nothing to mask: block, warn, ask for a reason or log it.";
        }
        if (r.Action is "warn" or "justify" && r.Stages.Any(s => s is not ("question" or "file")))
        {
            return "Only a person's message can wait for them to confirm or give a reason: warn and justify are for questions.";
        }
        if (r.Action == "reroute" && (string.IsNullOrWhiteSpace(r.RerouteModel) || r.Stages.Any(s => s != "question")))
        {
            return "Rerouting sends a question to another model: say which, and look only at questions.";
        }
        if (r.Detector == "files" && r.Stages.Any(s => s != "file"))
        {
            return "The files detector looks at attached files only.";
        }
        if (r.Detector == "words" && (Words.Patterns(r.Patterns).Any() is false || Words.Invalid(r.Patterns) is { } wrong) && r.Action != "allow")
        {
            return Words.Invalid(r.Patterns) is { } bad ? $"A pattern does not compile: {bad}" : "Give the words, phrases or patterns.";
        }
        if (r.Detector == "topic" && r.Action != "allow" && (r.Categories ?? []).Count == 0 && string.IsNullOrWhiteSpace(r.Topics))
        {
            return "Choose harms, or describe the topics (one a line: name: what it covers).";
        }
        if ((r.Categories ?? []).FirstOrDefault(c => !Safeguards.Categories.ContainsKey(c)) is { } unknown)
        {
            return $"{unknown} is not one of the harms: {string.Join(", ", Safeguards.Categories.Keys)}.";
        }
        if (r.Detector == "links" && r.Action != "allow" && (r.Domains ?? []).Count == 0)
        {
            return "Give the domains.";
        }
        if (r.Detector == "files" && r.Action != "allow" && (r.Extensions ?? []).Count == 0 && r.MaxFileMb is not > 0)
        {
            return "Give the extensions refused, or the largest size.";
        }
        if (r.Detector == "pii" && (r.Kinds ?? []).FirstOrDefault(k => !Pii.AllKinds.Contains(k)) is { } kind)
        {
            return $"{kind} is not one of {string.Join(", ", Pii.AllKinds)}.";
        }
        if (r.Detector == "secrets" && (r.Kinds ?? []).FirstOrDefault(k => !Secrets.AllKinds.Contains(k)) is { } secret)
        {
            return $"{secret} is not one of {string.Join(", ", Secrets.AllKinds)}.";
        }
        return r.Message?.Length > 2000 ? "The message is at most 2,000 characters." : null;
    }

    public static void Apply(SafeguardRule rule, RuleRequest r)
    {
        rule.Name = r.Name.Trim();
        rule.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        rule.Enabled = r.Enabled;
        rule.Monitor = r.Monitor;
        rule.Detector = r.Detector;
        rule.Stages = [.. r.Stages.Distinct()];
        rule.Groups = [.. (r.Groups ?? []).Distinct()];
        rule.People = [.. (r.People ?? []).Distinct()];
        rule.Models = [.. (r.Models ?? []).Select(m => m.Trim()).Where(m => m.Length > 0).Distinct()];
        rule.Action = r.Action;
        rule.Message = string.IsNullOrWhiteSpace(r.Message) ? null : r.Message.Trim();
        rule.RerouteModel = r.Action == "reroute" ? r.RerouteModel?.Trim() : null;
        rule.Strike = r.Action == "block" && r.Strike;
        rule.Kinds = [.. (r.Kinds ?? []).Distinct()];
        rule.Patterns = r.Detector == "words" ? r.Patterns?.Trim() : null;
        rule.Topics = r.Detector == "topic" ? r.Topics?.Trim() : null;
        rule.Categories = r.Detector == "topic" ? [.. (r.Categories ?? []).Distinct()] : [];
        rule.Domains = r.Detector == "links" ? [.. (r.Domains ?? []).Select(d => d.Trim().ToLowerInvariant()).Where(d => d.Length > 0).Distinct()] : [];
        rule.DomainsAllowed = r.Detector == "links" && r.DomainsAllowed;
        rule.Extensions = r.Detector == "files" ? [.. (r.Extensions ?? []).Select(e => "." + e.Trim().TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 1).Distinct()] : [];
        rule.MaxFileMb = r.Detector == "files" && r.MaxFileMb is > 0 ? r.MaxFileMb : null;
        rule.ModelCheck = r.Detector == "injection" && r.ModelCheck;
        rule.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static async Task<IResult> CreateAsync(RuleRequest body, AppDbContext db, UserManager<AppUser> users, HttpContext http, Audit audit, CancellationToken ct)
    {
        if (Invalid(body) is { } wrong)
        {
            return AuthEndpoints.Problem(400, "rule", wrong);
        }
        var me = await users.GetUserAsync(http.User);
        var rule = new SafeguardRule { Name = body.Name, Detector = body.Detector, Action = body.Action, CreatedById = me?.Id };
        Apply(rule, body);
        rule.Order = (await db.SafeguardRules.MaxAsync(r => (int?)r.Order, ct) ?? 0) + 10;
        db.SafeguardRules.Add(rule);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("safeguard.rule_added", rule.Name, detail: Describe(rule), actor: me);
        return Results.Created($"/api/admin/safeguards/rules/{rule.Id}", Shape(rule));
    }

    private static async Task<IResult> UpdateAsync(Guid id, RuleRequest body, AppDbContext db, UserManager<AppUser> users, HttpContext http, Audit audit, CancellationToken ct)
    {
        if (await db.SafeguardRules.FindAsync([id], ct) is not { } rule)
        {
            return Results.NotFound();
        }
        if (Invalid(body) is { } wrong)
        {
            return AuthEndpoints.Problem(400, "rule", wrong);
        }
        Apply(rule, body);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("safeguard.rule_changed", rule.Name, detail: Describe(rule), actor: await users.GetUserAsync(http.User));
        return Results.Ok(Shape(rule));
    }

    private static async Task<IResult> DeleteAsync(Guid id, AppDbContext db, UserManager<AppUser> users, HttpContext http, Audit audit, CancellationToken ct)
    {
        if (await db.SafeguardRules.FindAsync([id], ct) is not { } rule)
        {
            return Results.NotFound();
        }
        db.SafeguardRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("safeguard.rule_removed", rule.Name, actor: await users.GetUserAsync(http.User));
        return Results.NoContent();
    }

    public sealed record OrderRequest(List<Guid> Ids);

    /// <summary>The rules in this order (each 10 apart); rules not named keep their place after them.</summary>
    private static async Task<IResult> OrderAsync(OrderRequest body, AppDbContext db, UserManager<AppUser> users, HttpContext http, Audit audit, CancellationToken ct)
    {
        var rules = await db.SafeguardRules.OrderBy(r => r.Order).ThenBy(r => r.CreatedAt).ToListAsync(ct);
        var ordered = body.Ids.Distinct().Select(id => rules.FirstOrDefault(r => r.Id == id)).OfType<SafeguardRule>().Concat(rules.Where(r => !body.Ids.Contains(r.Id))).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Order = (i + 1) * 10;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("safeguard.rules_ordered", string.Join(", ", ordered.Select(r => r.Name)), actor: await users.GetUserAsync(http.User));
        return Results.NoContent();
    }

    private static string Describe(SafeguardRule r) =>
        $"{(r.Enabled ? "" : "off; ")}{(r.Monitor ? "monitor; " : "")}{r.Detector} on {string.Join(", ", r.Stages)}: {r.Action}" +
        (r.Groups.Count + r.People.Count > 0 ? $"; for {r.Groups.Count} groups and {r.People.Count} people" : "") +
        (r.Models.Count > 0 ? $"; models {string.Join(", ", r.Models)}" : "");

    /// <summary>The test bench: a text through the rules as they stand, nothing recorded; monitor rules show what they would do.</summary>
    private static async Task<IResult> TestAsync(TrialRequest body, Safeguards safeguards, AppDbContext db, CancellationToken ct)
    {
        if (!SafeguardRule.AllStages.Contains(body.Stage))
        {
            return AuthEndpoints.Problem(400, "stage", $"The stage is one of {string.Join(", ", SafeguardRule.AllStages)}.");
        }
        if (body.Text.Length > 100_000)
        {
            return AuthEndpoints.Problem(400, "text", "Try at most 100,000 characters.");
        }
        var person = body.Person is { } pid ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == pid, ct) : null;
        var result = await safeguards.RunAsync(new RuleRun(body.Stage, body.Text)
        {
            User = person, Model = string.IsNullOrWhiteSpace(body.Model) ? null : body.Model.Trim(), Trial = true, What = body.FileName ?? "message",
            FileName = body.FileName, FileBytes = (body.FileMb ?? 0) * 1024L * 1024,
        }, ct);
        return Results.Ok(new
        {
            outcome = result.Blocked ? "blocked" : result.Pending.Count > 0 ? "asks" : result.Text != body.Text ? "masked" : "passes",
            text = result.Text, reason = result.Reason ?? (result.Pending.Count > 0 ? string.Join(" ", result.Pending.Select(p => p.Message)) : null),
            reroute = result.Reroute,
            hits = result.Hits,
        });
    }

    private static async Task<IResult> EventsAsync(AppDbContext db, Guid? rule, Guid? person, string? outcome, string? stage, string? review, bool? monitor,
        DateTimeOffset? before, int? take, CancellationToken ct)
    {
        var q = db.SafeguardEvents.AsNoTracking();
        q = rule is { } r ? q.Where(e => e.RuleId == r) : q;
        q = person is { } p ? q.Where(e => e.UserId == p) : q;
        q = string.IsNullOrEmpty(outcome) ? q : q.Where(e => e.Outcome == outcome);
        q = string.IsNullOrEmpty(stage) ? q : q.Where(e => e.Stage == stage);
        q = review switch { null or "" => q, "any" => q.Where(e => e.Review != null), _ => q.Where(e => e.Review == review) };
        q = monitor is { } m ? q.Where(e => e.Monitor == m) : q;
        q = before is { } b ? q.Where(e => e.At < b) : q;
        var events = await q.OrderByDescending(e => e.At).Take(Math.Clamp(take ?? 100, 1, 500)).ToListAsync(ct);
        var ids = events.SelectMany(e => new[] { e.UserId, e.ReviewedById }).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName : u.DisplayName, ct);
        return Results.Ok(events.Select(e => new
        {
            e.Id, e.RuleId, e.RuleName, e.Stage, e.Detector, e.Outcome, e.Monitor, e.Detail, e.Excerpt, e.Justification, e.Model, e.ConversationId, e.At,
            person = e.UserId is { } u ? new { id = u, name = names.GetValueOrDefault(u) } : null,
            e.Review, e.ReviewReason, e.ReviewNote, e.ReviewedAt, reviewedBy = e.ReviewedById is { } rb ? names.GetValueOrDefault(rb) : null, e.AllowedUntil,
        }));
    }

    /// <summary>What the rules did, day by day (UTC) and rule by rule, over the last days.</summary>
    private static async Task<IResult> StatsAsync(AppDbContext db, int? days, CancellationToken ct)
    {
        var span = Math.Clamp(days ?? 30, 1, 365);
        var since = DateTimeOffset.UtcNow.Date.AddDays(1 - span);
        var rows = await db.SafeguardEvents.AsNoTracking().Where(e => e.At >= since)
            .Select(e => new { e.At, e.Outcome, e.Monitor, e.RuleId, e.RuleName, e.UserId }).ToListAsync(ct);
        return Results.Ok(new
        {
            days = Enumerable.Range(0, span).Select(i => since.AddDays(i)).Select(d => new
            {
                day = d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                outcomes = rows.Where(r => r.At.UtcDateTime.Date == d.Date && !r.Monitor).GroupBy(r => r.Outcome).ToDictionary(x => x.Key, x => x.Count()),
                monitor = rows.Count(r => r.At.UtcDateTime.Date == d.Date && r.Monitor),
            }),
            rules = rows.GroupBy(r => (r.RuleId, r.RuleName)).Select(x => new
            {
                id = x.Key.RuleId, name = x.Key.RuleName, count = x.Count(), blocked = x.Count(r => r.Outcome == "blocked" && !r.Monitor), monitor = x.Count(r => r.Monitor),
            }).OrderByDescending(x => x.count),
            people = rows.Where(r => r.UserId is not null && r.Outcome == "blocked" && !r.Monitor).Select(r => r.UserId!.Value).Distinct().Count(),
            waiting = await db.SafeguardEvents.CountAsync(e => e.Review == SafeguardEvent.Asked, ct),
        });
    }

    /// <summary>A review answered: allowed (the same text passes that rule for a day) or confirmed; the person hears either way.</summary>
    private static async Task<IResult> DecideAsync(Guid id, ReviewAnswer body, AppDbContext db, UserManager<AppUser> users, HttpContext http, Notifier notifier, Audit audit, CancellationToken ct)
    {
        if (await db.SafeguardEvents.FindAsync([id], ct) is not { } ev)
        {
            return Results.NotFound();
        }
        if (ev.Review != SafeguardEvent.Asked)
        {
            return AuthEndpoints.Problem(409, "review", "No one asked for this one to be reviewed, or it was answered.");
        }
        var me = await users.GetUserAsync(http.User);
        ev.Review = body.Allow ? SafeguardEvent.Allowed : SafeguardEvent.Confirmed;
        ev.ReviewedById = me?.Id;
        ev.ReviewNote = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim()[..Math.Min(body.Note.Trim().Length, 2000)];
        ev.ReviewedAt = DateTimeOffset.UtcNow;
        ev.AllowedUntil = body.Allow ? ev.ReviewedAt.Value.AddDays(1) : null;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(body.Allow ? "safeguard.review_allowed" : "safeguard.review_confirmed", ev.RuleName, detail: ev.ReviewNote, actor: me);
        if (ev.UserId is { } person)
        {
            var link = ev.ConversationId is { } c ? $"/chat/{c}" : "/account";
            await notifier.SendAsync(person, body.Allow
                ? new News("safeguards", "Your message may be sent", $"An admin reviewed it: send the same message again within a day.{(ev.ReviewNote is { } n ? " " + n : "")}", link)
                : new News("safeguards", "Your message stays refused", ev.ReviewNote ?? $"An admin reviewed it and the rule \"{ev.RuleName}\" stands.", link), ct);
        }
        return Results.NoContent();
    }

    /// <summary>The person's own refusals of the last month, with their reviews.</summary>
    private static async Task<IResult> MineAsync(AppDbContext db, UserManager<AppUser> users, HttpContext http, CancellationToken ct)
    {
        var me = await users.GetUserAsync(http.User);
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        return Results.Ok(await db.SafeguardEvents.AsNoTracking().Where(e => e.UserId == me!.Id && e.At > since && e.Outcome == "blocked" && !e.Monitor)
            .OrderByDescending(e => e.At).Take(100)
            .Select(e => new { e.Id, e.RuleName, e.Stage, e.Detail, e.At, e.ConversationId, e.Review, e.ReviewNote, e.AllowedUntil }).ToListAsync(ct));
    }

    /// <summary>A person asks for one of their refusals to be reviewed, with why; those who answer reviews hear of it.</summary>
    private static async Task<IResult> AskReviewAsync(Guid id, ReviewRequest body, AppDbContext db, UserManager<AppUser> users, HttpContext http, AccessService access,
        Notifier notifier, Audit audit, CancellationToken ct)
    {
        var me = await users.GetUserAsync(http.User);
        if (await db.SafeguardEvents.FirstOrDefaultAsync(e => e.Id == id && e.UserId == me!.Id, ct) is not { } ev || ev.Outcome != "blocked" || ev.Monitor)
        {
            return Results.NotFound();
        }
        if (ev.Review is not null)
        {
            return AuthEndpoints.Problem(409, "review", "A review was already asked for.");
        }
        var reason = body.Reason?.Trim() ?? "";
        if (reason.Length is 0 or > 2000)
        {
            return AuthEndpoints.Problem(400, "reason", "Say why it should be allowed (up to 2,000 characters).");
        }
        ev.Review = SafeguardEvent.Asked;
        ev.ReviewReason = reason;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("safeguard.review_asked", ev.RuleName, detail: reason, actor: me);
        var people = await db.Users.AsNoTracking().Where(u => !u.IsDisabled).ToListAsync(ct);
        foreach (var (person, m) in await access.MembershipsAsync(people, ct))
        {
            if (person != me!.Id && m.Has(Permissions.SafeguardsReview))
            {
                await notifier.SendAsync(person, new News("safeguards", $"{(string.IsNullOrWhiteSpace(me.DisplayName) ? me.UserName : me.DisplayName)} asks for a refusal to be reviewed",
                    $"{ev.RuleName}: {reason}", "/admin/safeguards?tab=reviews"), ct);
            }
        }
        return Results.NoContent();
    }
}
