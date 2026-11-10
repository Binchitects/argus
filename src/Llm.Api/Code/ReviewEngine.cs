using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Chat;
using Llm.Api.Models;
using Llm.Core.Code;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Branding;
namespace Llm.Api.Code;

/// <summary>
/// Reviews a merge request (or what one ref has over another) with the model, as the person who asked: code-intel reads the
/// change with their read-only access (mr_get, mr_diff, ci_status, compare_refs); each file (or a part of a large one) is
/// reviewed on its own, its lines numbered as in the new file; every finding is then put to a second pass that tries to
/// refute it; and a summary gives a walkthrough, a risk and a verdict. The model's requests are the person's, tagged
/// "review". Nothing is posted back to the code host.
/// </summary>
public sealed partial class ReviewEngine(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ILogger<ReviewEngine> logger) : IHostedService
{
    /// <summary>The most text of a diff one review pass reads.</summary>
    public const int UnitChars = 24_000;
    /// <summary>The most findings put to the second pass (the most severe first).</summary>
    public const int MostVerified = 25;
    /// <summary>The most files a review reads.</summary>
    public const int MostFiles = 80;
    public const string Tag = "review";

    /// <summary>Starts a queued review in the background.</summary>
    public void Start(Guid reviewId) => _ = Task.Run(() => RunAsync(reviewId, lifetime.ApplicationStopping));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Reviews cut short by a restart say so.
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // An automatic review still waiting its turn waits on; the rule's next look starts it.
            await db.CodeReviews.Where(r => r.Status == CodeReview.Running || (r.Status == CodeReview.Queued && r.RuleId == null))
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, CodeReview.Failed).SetProperty(r => r.Error, "The app restarted while it ran: review it again."), cancellationToken);
            await db.CodeReviewBatches.Where(b => b.Status == CodeReview.Running || b.Status == CodeReview.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, CodeReview.Failed).SetProperty(b => b.Step, (string?)null)
                    .SetProperty(b => b.Error, "The app restarted while it ran: review it again."), cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            LogFailed(logger, Guid.Empty, ex);
        }
    }

    /// <summary>What runs stops with the host's own stopping token: nothing of its own to stop (or to be disposed before it is).</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>One file's part to review: its path and its diff with new line numbers.</summary>
    public sealed record Unit(string Path, string Text);

    private async Task RunAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        if (await db.CodeReviews.SingleOrDefaultAsync(r => r.Id == id, ct) is not { } review)
        {
            return;
        }
        try
        {
            var user = await services.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(review.UserId.ToString())
                ?? throw new ReviewException("Its person is gone.");
            review.Status = CodeReview.Running;
            await StepAsync(db, review, "Reading the change", ct);
            var codeIntel = services.GetRequiredService<CodeIntelMcp>();
            var session = await codeIntel.ConnectAsync(user.Id, user.Email!, ct);
            if (review.Scope == CodeReview.Repository)
            {
                await ReviewRepositoryAsync(services, db, session, review, user, ct);
                await FinishAsync(db, review, services, ct);
                return;
            }
            var previous = review.PreviousId is { } p ? await db.CodeReviews.AsNoTracking().SingleOrDefaultAsync(r => r.Id == p, ct) : null;
            var (description, files, ci) = await ReadAsync(session, review, previous, ct);
            review.Ci = ci;
            review.Files = files.Count;
            if (files.Count == 0 && review.RuleId is not null && previous is not null)
            {
                // An automatic look at something that has not changed since: nothing to say, nothing kept.
                db.CodeReviews.Remove(review);
                await db.SaveChangesAsync(ct);
                return;
            }
            if (files.Count == 0)
            {
                review.Verdict = "looks_good";
                review.Risk = "low";
                review.Summary = previous is null ? "The change has no files to review." : "Nothing changed since the last review.";
                await FinishAsync(db, review, services, ct);
                return;
            }
            var model = await ModelAsync(services, user, ct);
            review.Model = model;
            var units = Units(files);
            var found = new List<ReviewFinding>();
            for (var i = 0; i < units.Count; i++)
            {
                await StepAsync(db, review, $"Reviewing {units[i].Path} ({i + 1} of {units.Count})", ct);
                found.AddRange(await ReviewUnitAsync(services, review, user.Email!, model, description, units[i], ct));
            }
            await StepAsync(db, review, "Checking what it adds to the repository's security", ct);
            var (packages, scanned) = await SecurityDeltaAsync(session, review, files, ct);
            // A scanner's match is a pattern: the second pass puts it to the test as it does the model's own.
            found.AddRange(scanned);
            // The second pass: each finding, the most severe first, put to a reviewer told to refute it.
            var ordered = found.OrderBy(f => Rank(f.Severity)).ToList();
            for (var i = 0; i < ordered.Count && i < MostVerified; i++)
            {
                await StepAsync(db, review, $"Checking finding {i + 1} of {Math.Min(ordered.Count, MostVerified)}", ct);
                var unit = units.FirstOrDefault(u => u.Path == ordered[i].Path);
                await VerifyAsync(services, review, user.Email!, model, ordered[i], unit, ct);
            }
            // A package an advisory affects is a fact deps_check matched: no second pass.
            ordered = [.. packages.OrderBy(f => Rank(f.Severity)), .. ordered];
            review.Findings.AddRange(ordered);
            db.ReviewFindings.AddRange(ordered.Select(f => { f.ReviewId = review.Id; return f; }));
            await StepAsync(db, review, "Writing the summary", ct);
            await SummarizeAsync(services, review, user.Email!, model, description, files, ordered, ct);
            await FinishAsync(db, review, services, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogFailed(logger, id, ex);
            review.Status = CodeReview.Failed;
            review.Error = ex is ReviewException or ChatGatewayException ? ex.Message : "The review failed on the server. Try again, and tell an admin if it keeps failing.";
            review.Step = null;
            review.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await NotifyAsync(services, review, CancellationToken.None);
        }
    }

    // ------------------------------------------------------------------ a whole repository --

    /// <summary>The most files a review of a whole repository reads, one model call each.</summary>
    public const int MostRepositoryFiles = 30;

    /// <summary>Tests, examples, vendored, generated and built code: not what a review of the product reads.</summary>
    [GeneratedRegex(@"(^|/)(tests?|spec|specs|__tests__|testdata|fixtures?|examples?|samples?|node_modules|vendor|third_party|dist|build|out|bin|obj|e2e|migrations|generated|\.git[^/]*)/|\.(test|spec)\.[a-z]+$|_test\.go$|Tests?\.cs$|test_[^/]*\.py$|\.min\.(js|css)$|\.g\.cs$|\.Designer\.cs$|\.lock$|-lock\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex NotReviewed();

    /// <summary>The languages a review reads as code; the rest (docs, data, configuration) is left to the summary's layout.</summary>
    private static readonly HashSet<string> CodeLanguages = new(StringComparer.Ordinal)
    {
        "csharp", "python", "javascript", "typescript", "tsx", "go", "java", "kotlin", "rust", "c", "cpp", "ruby", "php", "swift", "scala", "fsharp", "vb", "dart", "lua", "shell", "sql", "elixir", "erlang", "haskell", "ocaml", "r", "julia", "perl", "groovy", "zig", "vue", "svelte",
    };

    [GeneratedRegex(@"(^|/)(program|main|app|server|startup|index|cli|api|routes?|handlers?|controllers?)\.[a-z]+$", RegexOptions.IgnoreCase)]
    private static partial Regex EntryPoint();

    /// <summary>
    /// What a review of a whole repository reads: its product's code files (no tests, vendored, generated or built code),
    /// between 200 bytes and 200 KB, the entry points first, then the largest (where most of the logic is), at most
    /// <paramref name="most"/>.
    /// </summary>
    public static List<string> ChooseFiles(IEnumerable<(string Path, long Size, string? Language)> files, int most = MostRepositoryFiles) =>
        [.. files.Where(f => f.Language is { } l && CodeLanguages.Contains(l) && !NotReviewed().IsMatch(f.Path) && f.Size is >= 200 and <= 200_000)
            .OrderByDescending(f => EntryPoint().IsMatch(f.Path)).ThenByDescending(f => f.Size).ThenBy(f => f.Path, StringComparer.Ordinal)
            .Take(most).Select(f => f.Path)];

    /// <summary>A file's lines numbered as a diff's new lines are ("   12 code"), so a finding is anchored the same way.</summary>
    public static string NumberedFile(string text) =>
        string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select((l, i) => $"{i + 1,5} {l}"));

    public const string RepositoryPrompt =
        "You review a repository's code like a careful senior engineer, one whole file at a time; each line is numbered on the left. " +
        "Find real problems: bugs, security holes, data loss, race conditions, wrong error handling, broken edge cases, resource leaks, " +
        "performance traps, missing tests for risky logic, and design that will cause bugs (a god class, hidden coupling, duplicated logic " +
        "that has already drifted). Skip taste and formatting unless it hides a bug. At most eight findings, the worst first; each needs " +
        "the line it is about.\n\nAnswer with JSON only, no prose around it: {\"findings\": [{\"line\": 123, \"severity\": \"critical|major|minor|nit\", " +
        "\"category\": \"bug|security|performance|design|tests|style\", \"title\": \"short\", \"body\": \"what is wrong, why it matters, and how to fix it\", " +
        "\"suggestion\": \"the replacement code, or null\"}]}. No findings: {\"findings\": []}. Do not invent problems.";

    public const string RepositoryVerifyPrompt =
        "You check another reviewer's finding on a repository's file, as a skeptic: try to REFUTE it. It is real only if the file shows the " +
        "problem and nothing in it (a check elsewhere in the file, a guarantee of the language, the code's evident intent) makes it harmless. " +
        "When unsure, it is not real. Answer with JSON only: {\"real\": true|false, \"why\": \"one or two sentences\"}.";

    public const string RepositorySummaryPrompt =
        "You write the summary of a review of a whole repository for its maintainers. Given its README, its layout and languages, the files " +
        "read and the findings that survived checking, write Markdown: what the repository is and how it is built (two or three sentences); " +
        "its health, area by area (one line each: what is solid, what is risky); then what to fix first, and what to test that is not. Then, " +
        "on the last line alone, JSON: {\"verdict\": \"looks_good|comments|changes\", \"risk\": \"low|medium|high\"}.";

    private static JsonObject AtRef(CodeReview review, JsonObject args)
    {
        args["project"] = review.Project;
        if (review.ToRef is { Length: > 0 } r)
        {
            args["ref"] = r;
        }
        return args;
    }

    /// <summary>
    /// A review of a whole repository at a ref: its files listed (files_at), the product's code chosen (<see cref="ChooseFiles"/>),
    /// each read whole and reviewed, each finding put to a skeptic, then a summary of its health with a verdict.
    /// </summary>
    private static async Task ReviewRepositoryAsync(IServiceProvider services, AppDbContext db, McpSession session, CodeReview review, AppUser user, CancellationToken ct)
    {
        await StepAsync(db, review, "Listing its files", ct);
        var listing = await CallAsync(session, "files_at", AtRef(review, new JsonObject { ["limit"] = 5000 }), ct);
        var label = listing["ref"]?.ToString();
        review.HeadSha = listing["sha"]?.ToString();
        review.Title = $"{review.Project} @ {review.ToRef ?? label ?? "the default branch"}";
        var all = (listing["files"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(f => (Path: f["path"]?.ToString() ?? "", Size: f["size"] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0, Language: f["language"]?.ToString())).ToList();
        var chosen = ChooseFiles(all);
        review.Files = chosen.Count;
        // What it is: its README, its top-level layout and its languages.
        var readme = all.Select(f => f.Path).FirstOrDefault(f => Regex.IsMatch(f, @"^readme(\.[a-z]+)?$", RegexOptions.IgnoreCase));
        var about = new StringBuilder();
        if (readme is not null)
        {
            var text = (await CallAsync(session, "file_at", AtRef(review, new JsonObject { ["path"] = readme, ["end_line"] = 120 }), ct))["content"]?.ToString() ?? "";
            about.Append("README:\n").Append(Cut(text, 4_000)).Append("\n\n");
        }
        about.Append("Layout (top-level, files):\n");
        foreach (var g in all.GroupBy(f => f.Path.Contains('/') ? f.Path[..f.Path.IndexOf('/')] + "/" : f.Path).OrderByDescending(g => g.Count()).Take(30))
        {
            about.Append("- ").Append(g.Key).Append(' ').Append(g.Count()).Append('\n');
        }
        if (listing["languages"] is JsonObject languages)
        {
            about.Append("Languages: ").Append(string.Join(", ", languages.Select(l => $"{l.Key} {l.Value?["files"]}"))).Append('\n');
        }
        if (chosen.Count == 0)
        {
            review.Verdict = "comments";
            review.Risk = "low";
            review.Summary = "No code of the product to read here: the repository has only tests, docs, configuration or files of languages a review does not read.";
            return;
        }
        var model = await ModelAsync(services, user, ct);
        review.Model = model;
        var units = new List<Unit>();
        var found = new List<ReviewFinding>();
        for (var i = 0; i < chosen.Count; i++)
        {
            await StepAsync(db, review, $"Reviewing {chosen[i]} ({i + 1} of {chosen.Count})", ct);
            var file = await CallAsync(session, "file_at", AtRef(review, new JsonObject { ["path"] = chosen[i] }), ct);
            if (file["content"]?.ToString() is not { Length: > 0 } content)
            {
                continue;
            }
            var unit = new Unit(chosen[i], Cut(NumberedFile(content.TrimEnd('\n')), UnitChars));
            units.Add(unit);
            var ask = $"The repository:\n{Cut(about.ToString(), 3_000)}\n\nFile: {unit.Path}\n```\n{unit.Text}\n```";
            found.AddRange(Findings(await AskAsync(services, review, user.Email!, model, RepositoryPrompt, ask, 2_000, ct), unit, 8));
        }
        var ordered = found.OrderBy(f => Rank(f.Severity)).ToList();
        for (var i = 0; i < ordered.Count && i < MostVerified; i++)
        {
            await StepAsync(db, review, $"Checking finding {i + 1} of {Math.Min(ordered.Count, MostVerified)}", ct);
            var unit = units.FirstOrDefault(u => u.Path == ordered[i].Path);
            var ask = $"File: {ordered[i].Path}\n```\n{Cut(unit?.Text ?? "", UnitChars)}\n```\n\nThe finding (line {ordered[i].Line?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}, {ordered[i].Severity}): {ordered[i].Title}\n{ordered[i].Body}";
            if (Json(await AskAsync(services, review, user.Email!, model, RepositoryVerifyPrompt, ask, 400, ct)) is { } verdict && verdict["real"] is JsonValue real && real.TryGetValue<bool>(out var isReal))
            {
                ordered[i].Verified = isReal;
                ordered[i].VerifyNote = Cut(verdict["why"]?.ToString() ?? "", 1_000);
            }
        }
        review.Findings.AddRange(ordered);
        db.ReviewFindings.AddRange(ordered.Select(f => { f.ReviewId = review.Id; return f; }));
        await StepAsync(db, review, "Writing the summary", ct);
        var kept = ordered.Where(f => f.Verified != false).ToList();
        var summary = new StringBuilder(about.ToString()).Append("\nFiles read:\n");
        foreach (var path in chosen)
        {
            summary.Append("- ").Append(path).Append('\n');
        }
        summary.Append(CultureInfo.InvariantCulture, $"({all.Count} files in all)\n\nFindings:\n");
        foreach (var f in kept)
        {
            summary.Append("- [").Append(f.Severity).Append("] ").Append(f.Path).Append(f.Line is { } l ? $":{l}" : "").Append(' ').Append(f.Title).Append('\n');
        }
        if (kept.Count == 0)
        {
            summary.Append("(none)\n");
        }
        Verdict(review, (await AskAsync(services, review, user.Email!, model, RepositorySummaryPrompt, summary.ToString(), 1_500, ct)).Trim(), kept);
    }

    /// <summary>The findings of a model's answer for a unit: each anchored to a line the unit shows, at most <paramref name="most"/>.</summary>
    private static List<ReviewFinding> Findings(string text, Unit unit, int most)
    {
        var found = new List<ReviewFinding>();
        if (Json(text)?["findings"] is not JsonArray list)
        {
            return found;
        }
        var numbered = NumberedLines(unit.Text);
        foreach (var f in list.OfType<JsonObject>().Take(most))
        {
            var title = f["title"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }
            int? line = f["line"] is JsonValue v && v.TryGetValue<int>(out var n) && numbered.Contains(n) ? n : null;
            found.Add(new ReviewFinding
            {
                Path = unit.Path, Line = line,
                Severity = f["severity"]?.ToString() is "critical" or "major" or "minor" or "nit" ? f["severity"]!.ToString() : "minor",
                Category = Cut(f["category"]?.ToString() ?? "", 32) is { Length: > 0 } c ? c : null,
                Title = Cut(title, 300), Body = Cut(f["body"]?.ToString() ?? "", 4_000),
                Suggestion = f["suggestion"] is JsonValue s && s.ToString() is { Length: > 0 } code && code != "null" ? Cut(code, 4_000) : null,
            });
        }
        return found;
    }

    /// <summary>The summary and the verdict line the model ends it with; without one, the verdict from the findings kept.</summary>
    private static void Verdict(CodeReview review, string text, List<ReviewFinding> kept)
    {
        var lastBrace = text.LastIndexOf('{');
        var verdict = lastBrace >= 0 ? Json(text[lastBrace..]) : null;
        review.Summary = (verdict is null ? text : text[..lastBrace]).Trim().TrimEnd('`').Trim();
        review.Verdict = verdict?["verdict"]?.ToString() is "looks_good" or "comments" or "changes" ? verdict["verdict"]!.ToString()
            : kept.Any(f => f.Severity is "critical" or "major") ? "changes" : kept.Count > 0 ? "comments" : "looks_good";
        review.Risk = verdict?["risk"]?.ToString() is "low" or "medium" or "high" ? verdict["risk"]!.ToString()
            : kept.Any(f => f.Severity == "critical") ? "high" : kept.Any(f => f.Severity == "major") ? "medium" : "low";
    }

    // ------------------------------------------------------------- several repositories --

    /// <summary>A review of several whole repositories: each one's in turn, then the look across them.</summary>
    public void StartBatch(Guid batchId) => _ = Task.Run(() => RunBatchAsync(batchId, lifetime.ApplicationStopping));

    public const string CrossPrompt =
        """
        You review several repositories of one organisation together, as a principal engineer, from each one's review and from how they depend on each other. Write in Markdown, plainly, with these sections:
        ## How they fit together
        Two to four sentences: what each one is for, and how they depend on each other (from the links given; say when none were found).
        ## Between the repositories
        Problems at their boundaries the reviews support: a contract one side breaks or may break (a changed type, an endpoint, a message), a shared package at different versions, the same logic done twice and drifting, a dependency the wrong way. Name the repositories and files. If nothing crosses, say so in one sentence.
        ## Alike and different
        Where they do the same thing differently (errors, logging, configuration, tests), and which way to settle on.
        ## Fix first
        A numbered list, at most ten: the repository, the finding (file:line), and why it comes first.
        Never invent a finding, a file or a line that is not in what you are given.
        """;

    private async Task RunBatchAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        if (await db.CodeReviewBatches.SingleOrDefaultAsync(b => b.Id == id, ct) is not { } batch)
        {
            return;
        }
        try
        {
            batch.Status = CodeReview.Running;
            await db.SaveChangesAsync(ct);
            var ids = await db.CodeReviews.AsNoTracking().Where(r => r.BatchId == id).OrderBy(r => r.CreatedAt).Select(r => new { r.Id, r.Project }).ToListAsync(ct);
            for (var i = 0; i < ids.Count; i++)
            {
                batch.Step = Cut($"Reviewing {ids[i].Project} ({i + 1} of {ids.Count})", 200);
                await db.SaveChangesAsync(ct);
                await RunAsync(ids[i].Id, ct);
            }
            var reviews = await db.CodeReviews.AsNoTracking().Include(r => r.Findings).Where(r => r.BatchId == id && r.Status == CodeReview.Done).OrderBy(r => r.CreatedAt).ToListAsync(ct);
            if (batch.Cross && reviews.Count >= 2)
            {
                batch.Step = "Looking across the repositories";
                await db.SaveChangesAsync(ct);
                var user = await services.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(batch.UserId.ToString()) ?? throw new ReviewException("Its person is gone.");
                var session = await services.GetRequiredService<CodeIntelMcp>().ConnectAsync(user.Id, user.Email!, ct);
                batch.Links = await LinksAsync(session, [.. reviews.Select(r => r.Project)], ct);
                batch.Model = await ModelAsync(services, user, ct);
                batch.Report = (await AskModelAsync(services, batch.Model, user.Email!, CrossPrompt, CrossQuestion(reviews, batch.Links), 2_000,
                    (p, c) => (batch.PromptTokens, batch.CompletionTokens) = (batch.PromptTokens + p, batch.CompletionTokens + c), ct)).Trim();
            }
            batch.Status = reviews.Count == 0 ? CodeReview.Failed : CodeReview.Done;
            batch.Error = reviews.Count == 0 ? "No repository's review ended: see each one for why." : null;
            batch.Step = null;
            batch.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogFailed(logger, id, ex);
            batch.Status = CodeReview.Failed;
            batch.Error = ex is ReviewException or ChatGatewayException ? ex.Message : "The look across the repositories failed on the server. Each repository's review is kept.";
            batch.Step = null;
            batch.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>The most pairs of repositories whose links are asked for.</summary>
    public const int MostPairs = 15;

    /// <summary>How the repositories depend on each other, pair by pair, as code-intel's dependency_path resolves it: one line a link found.</summary>
    private static async Task<string> LinksAsync(McpSession session, List<string> projects, CancellationToken ct)
    {
        var lines = new List<string>();
        var pairs = 0;
        for (var i = 0; i < projects.Count; i++)
        {
            for (var j = i + 1; j < projects.Count && pairs < MostPairs; j++, pairs++)
            {
                JsonObject answer;
                try
                {
                    answer = await CallAsync(session, "dependency_path", new JsonObject { ["from"] = projects[i], ["to"] = projects[j] }, ct);
                }
                catch (ReviewException)
                {
                    continue;
                }
                if (answer["paths"] is not JsonArray { Count: > 0 } paths || paths[0] is not JsonObject best)
                {
                    continue;
                }
                var steps = (best["steps"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(st => $"{st["from"]} -> {st["to"]} ({Cut(st["by"]?.ToJsonString() ?? "", 160)})");
                lines.Add($"{answer["direction"] ?? $"{projects[i]} and {projects[j]}"}: {string.Join("; ", steps)}, confidence {best["confidence"]}{(answer["weak"] is not null ? ", weak" : "")}");
            }
        }
        return lines.Count == 0 ? $"No dependency between them that {Brand.CodeIntel} resolved (they may still talk at run time: an API, a queue)." : string.Join('\n', lines);
    }

    private static string CrossQuestion(List<CodeReview> reviews, string links)
    {
        var sb = new StringBuilder();
        foreach (var r in reviews)
        {
            sb.Append("# ").Append(r.Project).Append(r.ToRef is { Length: > 0 } at ? "@" + at : "").Append('\n');
            sb.Append(Cut(r.Summary ?? "", 1_500)).Append('\n');
            foreach (var f in r.Findings.Where(f => f.Verified != false).OrderBy(f => Rank(f.Severity)).Take(12))
            {
                sb.Append("- [").Append(f.Severity).Append("] ").Append(f.Path).Append(f.Line is { } l ? $":{l}" : "").Append(' ').Append(f.Title).Append('\n');
            }
            sb.Append('\n');
        }
        sb.Append("# How they depend on each other\n").Append(links).Append('\n');
        return sb.ToString();
    }

    private static async Task StepAsync(AppDbContext db, CodeReview review, string step, CancellationToken ct)
    {
        review.Step = step;
        await db.SaveChangesAsync(ct);
    }

    private static async Task FinishAsync(AppDbContext db, CodeReview review, IServiceProvider services, CancellationToken ct)
    {
        review.Status = CodeReview.Done;
        review.Step = null;
        review.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await NotifyAsync(services, review, ct);
        if (review.RuleId is not null && review.RoomId is { } room)
        {
            await services.GetRequiredService<AutoReviews>().PostAsync(db, review, room, ct);
        }
    }

    private static async Task NotifyAsync(IServiceProvider services, CodeReview review, CancellationToken ct) =>
        await services.GetRequiredService<Notifications.Notifier>().SendAsync(review.UserId, new Notifications.News("review",
            review.Status == CodeReview.Done ? $"Review ready: {review.Title}" : $"Review failed: {review.Title}",
            review.Status == CodeReview.Done ? $"{review.Findings.Count(f => f.Verified != false)} findings · {VerdictText(review.Verdict)}" : review.Error,
            $"/code/reviews/{review.Id}"), ct);

    public static string VerdictText(string? verdict) => verdict switch
    {
        "looks_good" => "looks good",
        "changes" => "changes suggested",
        _ => "comments",
    };

    private static int Rank(string severity) => severity switch { "critical" => 0, "major" => 1, "minor" => 2, _ => 3 };

    /// <summary>The model the person's chats use by default, if they may use it.</summary>
    private static async Task<string> ModelAsync(IServiceProvider services, AppUser user, CancellationToken ct)
    {
        var policy = services.GetRequiredService<ModelPolicy>();
        var models = services.GetRequiredService<ChatModels>();
        var name = (await policy.ForAsync(user, await models.ListAsync(ct), ct)).Default?.Name ?? throw new ReviewException("No model is there for you to review with.");
        if (await policy.RefusalAsync(user, name, ct) is { } refused)
        {
            throw new ReviewException(refused);
        }
        return name;
    }

    /// <summary>What the review reads: the description, the files changed (since the last review's head, for a review again) and CI.</summary>
    private static async Task<(string Description, List<(string Path, string Diff)> Files, string? Ci)> ReadAsync(McpSession session, CodeReview review, CodeReview? previous,
        CancellationToken ct)
    {
        string description;
        JsonObject changes;
        string? ci = null;
        if (review.Iid is { } iid)
        {
            var mr = await CallAsync(session, "mr_get", new JsonObject { ["project"] = review.Project, ["iid"] = iid }, ct);
            review.Title = $"!{iid} {mr["title"]?.ToString()}";
            review.WebUrl = mr["web_url"]?.ToString();
            review.FromRef = mr["target_branch"]?.ToString();
            review.ToRef = mr["source_branch"]?.ToString();
            review.HeadSha = mr["diff_refs"]?["head_sha"]?.ToString() ?? mr["sha"]?.ToString();
            description = $"Title: {mr["title"]}\nAuthor: {mr["author"]}\nFrom {review.ToRef} into {review.FromRef}\n\n{mr["description"]}";
            var args = new JsonObject { ["project"] = review.Project, ["iid"] = iid };
            if (previous?.HeadSha is { Length: > 0 } since && since != review.HeadSha)
            {
                args["since_sha"] = since;
            }
            else if (previous?.HeadSha == review.HeadSha && previous is not null)
            {
                return (description, [], null);
            }
            changes = await CallAsync(session, "mr_diff", args, ct);
            try
            {
                var status = await CallAsync(session, "ci_status", new JsonObject { ["project"] = review.Project, ["iid"] = iid }, ct);
                ci = status["pipeline"] is JsonObject pipeline
                    ? $"Pipeline {pipeline["status"]}" + (status["failed_jobs"] is JsonArray { Count: > 0 } failed ? $"; failed: {string.Join(", ", failed.Select(j => j?["name"]?.ToString()))}" : "")
                    : status["note"]?.ToString();
            }
            catch (ReviewException)
            {
                ci = null;
            }
        }
        else
        {
            changes = await CallAsync(session, "compare_refs", new JsonObject { ["project"] = review.Project, ["from"] = review.FromRef, ["to"] = review.ToRef }, ct);
            review.Title = $"{review.ToRef} over {review.FromRef}";
            // Its head is the last commit: a scheduled review again of a branch that has not moved has nothing new to read.
            review.HeadSha = (changes["commits"] as JsonArray)?.LastOrDefault()?["sha"]?.ToString();
            if (previous is not null && previous.HeadSha is { Length: > 0 } head && head == review.HeadSha)
            {
                return ($"What {review.ToRef} has over {review.FromRef}.", [], null);
            }
            description = $"What {review.ToRef} has over {review.FromRef}.\nCommits:\n" +
                string.Join('\n', (changes["commits"] as JsonArray ?? []).Select(c => "- " + c?["title"]));
        }
        var files = new List<(string Path, string Diff)>();
        foreach (var f in (changes["files"] as JsonArray ?? []).OfType<JsonObject>().Take(MostFiles))
        {
            var path = f["new_path"]?.ToString() ?? f["old_path"]?.ToString() ?? "";
            if (f["deleted_file"]?.GetValue<bool>() == true || f["binary"]?.GetValue<bool>() == true)
            {
                continue;
            }
            var diff = f["diff"]?.ToString();
            if (diff is null && f["omitted"] is not null && review.Iid is { } number)
            {
                // Over the answer's size: asked for alone.
                var alone = await CallAsync(session, "mr_diff", new JsonObject { ["project"] = review.Project, ["iid"] = number, ["path"] = path }, ct);
                diff = (alone["files"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["diff"]?.ToString();
            }
            if (!string.IsNullOrWhiteSpace(diff))
            {
                files.Add((path, diff));
            }
        }
        return (description, files, ci);
    }

    private static async Task<JsonObject> CallAsync(McpSession session, string tool, JsonObject args, CancellationToken ct)
    {
        var (text, isError) = await session.CallAsync(tool, args, null, ct);
        if (isError)
        {
            throw new ReviewException(text.Length > 500 ? text[..500] : text);
        }
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? throw new ReviewException($"{Brand.CodeIntel}'s {tool} answered something else.");
        }
        catch (JsonException)
        {
            throw new ReviewException($"{Brand.CodeIntel}'s {tool} answered something else.");
        }
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex Hunk();

    /// <summary>A unified diff with each line's number in the new file ("123 +added", "124  kept", "    -removed").</summary>
    public static string Numbered(string diff)
    {
        var sb = new StringBuilder();
        var line = 0;
        foreach (var raw in diff.Split('\n'))
        {
            if (Hunk().Match(raw) is { Success: true } m)
            {
                line = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                sb.Append(raw).Append('\n');
            }
            else if (raw.StartsWith('-'))
            {
                sb.Append("     ").Append(raw).Append('\n');
            }
            else if (raw.StartsWith('+') || raw.StartsWith(' '))
            {
                sb.Append(line.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(5)).Append(raw).Append('\n');
                line++;
            }
            else if (raw.Length > 0)
            {
                sb.Append(raw).Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>A file that says which packages a project uses (a lock file or a manifest): a change to one is checked against the advisories.</summary>
    [GeneratedRegex(@"(^|/)(package-lock\.json|npm-shrinkwrap\.json|yarn\.lock|pnpm-lock\.yaml|package\.json|packages\.lock\.json|[^/]+\.(cs|fs|vb)proj|Directory\.Packages\.props|requirements[^/]*\.txt|poetry\.lock|pyproject\.toml|uv\.lock|Pipfile\.lock|go\.mod|go\.sum|Cargo\.lock|Cargo\.toml|pom\.xml|build\.gradle(\.kts)?|gradle\.lockfile|Gemfile\.lock|composer\.lock)$")]
    private static partial Regex Manifest();

    /// <summary>The lines each file's diff adds, by their number in the new file.</summary>
    public static Dictionary<string, HashSet<int>> AddedLines(IEnumerable<(string Path, string Diff)> files)
    {
        var added = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var (path, diff) in files)
        {
            var line = 0;
            var lines = new HashSet<int>();
            foreach (var raw in diff.Split('\n'))
            {
                if (Hunk().Match(raw) is { Success: true } m)
                {
                    line = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
                else if (raw.StartsWith('+') && !raw.StartsWith("+++", StringComparison.Ordinal))
                {
                    lines.Add(line++);
                }
                else if (raw.StartsWith(' '))
                {
                    line++;
                }
            }
            if (lines.Count > 0)
            {
                added[path] = lines;
            }
        }
        return added;
    }

    private static string SeverityOf(string security, bool exploited) => exploited || security == "critical" ? "critical" : security == "high" ? "major" : security == "medium" ? "minor" : "nit";

    private static IEnumerable<JsonObject> Rows(JsonNode? node) => (node as JsonArray ?? (node as JsonObject)?["result"] as JsonArray ?? []).OfType<JsonObject>();

    /// <summary>
    /// What the change adds to the repository's security, with no model: each package version an advisory affects that the
    /// target did not have (deps_check at both ends, when a lock file or a manifest changed), and what the scanners find on
    /// the lines it adds (scan_at on its files, where the scanners are installed). Neither is there without its code-intel tool,
    /// and the review goes on without it.
    /// </summary>
    private static async Task<(List<ReviewFinding> Packages, List<ReviewFinding> Scanned)> SecurityDeltaAsync(McpSession session, CodeReview review,
        List<(string Path, string Diff)> files, CancellationToken ct)
    {
        JsonObject AtHead(JsonObject args)
        {
            args["project"] = review.Project;
            if (review.Iid is { } iid)
            {
                args["iid"] = iid;
            }
            else
            {
                args["ref"] = review.ToRef;
            }
            return args;
        }
        var packages = new List<ReviewFinding>();
        if (files.Any(f => Manifest().IsMatch(f.Path)) && review.FromRef is { Length: > 0 } target)
        {
            try
            {
                var head = await CallAsync(session, "deps_check", AtHead([]), ct);
                var before = await CallAsync(session, "deps_check", new JsonObject { ["project"] = review.Project, ["ref"] = target }, ct);
                var had = Rows(before["findings"]).Select(f => $"{f["advisory"]}|{f["package"]}|{f["version"]}").ToHashSet(StringComparer.Ordinal);
                foreach (var f in Rows(head["findings"]).Where(f => !had.Contains($"{f["advisory"]}|{f["package"]}|{f["version"]}")))
                {
                    var exploited = f["exploited"] is JsonValue e && e.TryGetValue<bool>(out var x) && x;
                    var upgrade = f["upgrade_to"]?.ToString();
                    var how = f["direct"] is JsonValue d && d.TryGetValue<bool>(out var direct) && direct ? "asked for directly" : $"brought in by {f["via"]?.ToString() ?? "another package"}";
                    packages.Add(new ReviewFinding
                    {
                        Path = Cut(f["file"]?.ToString() ?? "", 1_000), Line = f["line"] is JsonValue l && l.TryGetValue<int>(out var line) && line > 0 ? line : null,
                        Severity = SeverityOf(f["severity"]?.ToString() ?? "", exploited), Category = "security",
                        Title = Cut($"Adds {f["package"]} {f["version"]}, affected by {f["advisory"]} ({f["severity"]})", 300),
                        Body = Cut($"{f["summary"]} The package is {how}." + (exploited ? " It is on CISA's list of known exploited vulnerabilities." : "") +
                            (upgrade is not null ? $" {upgrade} fixes it." : " No fixed version is known.") + $" {f["url"]}", 4_000),
                        Suggestion = upgrade is not null ? $"{f["package"]} {upgrade}" : null,
                        Verified = true, VerifyNote = "Matched against the advisories by deps_check, with no model.",
                    });
                }
            }
            catch (Exception ex) when (ex is ReviewException or McpException)
            {
                // No advisories pack, lock files code-intel cannot read, or an code-intel without the tool: the review goes on with the code.
            }
        }
        var scanned = new List<ReviewFinding>();
        var added = AddedLines(files);
        if (added.Count > 0)
        {
            try
            {
                var result = await CallAsync(session, "scan_at", AtHead(new JsonObject { ["history"] = false, ["paths"] = string.Join('\n', added.Keys.Take(500)) }), ct);
                foreach (var f in Rows(result["findings"]))
                {
                    var path = f["path"]?.ToString() ?? "";
                    var line = f["line"] is JsonValue l && l.TryGetValue<int>(out var n) ? n : 0;
                    if (!added.TryGetValue(path, out var lines) || !lines.Contains(line))
                    {
                        continue;
                    }
                    var secret = f["kind"]?.ToString() == "secret";
                    var tools = string.Join(", ", (f["tools"] as JsonArray ?? []).Select(t => t?.ToString()));
                    var rules = string.Join(", ", (f["rules"] as JsonArray ?? []).Select(t => t?.ToString()));
                    scanned.Add(new ReviewFinding
                    {
                        Path = Cut(path, 1_000), Line = line, Severity = secret ? "critical" : SeverityOf(f["severity"]?.ToString() ?? "", false), Category = "security",
                        Title = Cut(f["title"]?.ToString() ?? rules, 300),
                        Body = Cut($"{f["message"]} ({(f["cwe"]?.ToString() is { Length: > 0 } cwe ? cwe + ", " : "")}{tools}: {rules})", 4_000),
                    });
                }
            }
            catch (Exception ex) when (ex is ReviewException or McpException)
            {
                // No scanners here: the model's review stands alone.
            }
        }
        return (packages, scanned);
    }

    /// <summary>The parts reviewed one by one: a file each, a large file in parts at its hunks.</summary>
    public static List<Unit> Units(IEnumerable<(string Path, string Diff)> files)
    {
        var units = new List<Unit>();
        foreach (var (path, diff) in files)
        {
            var text = Numbered(diff);
            if (text.Length <= UnitChars)
            {
                units.Add(new Unit(path, text));
                continue;
            }
            var part = new StringBuilder();
            foreach (var hunk in Regex.Split(text, @"(?=^@@ )", RegexOptions.Multiline))
            {
                if (part.Length > 0 && part.Length + hunk.Length > UnitChars)
                {
                    units.Add(new Unit(path, part.ToString()));
                    part.Clear();
                }
                part.Append(hunk.Length > UnitChars ? hunk[..UnitChars] + "\n[cut to fit]\n" : hunk);
            }
            if (part.Length > 0)
            {
                units.Add(new Unit(path, part.ToString()));
            }
        }
        return units;
    }

    public const string ReviewPrompt =
        "You review code changes like a careful senior engineer. You read one file's diff of a change; each line of the new file is " +
        "numbered on the left (removed lines have no number). Find real problems in the CHANGED lines: bugs, security holes, data loss, " +
        "race conditions, wrong error handling, broken edge cases, performance traps, missing tests for risky logic, and unclear design " +
        "that will cause bugs. Skip taste and formatting unless it hides a bug. Each finding needs the new line number it is about.\n\n" +
        "Answer with JSON only, no prose around it: {\"findings\": [{\"line\": 123, \"severity\": \"critical|major|minor|nit\", " +
        "\"category\": \"bug|security|performance|design|tests|style\", \"title\": \"short\", \"body\": \"what is wrong, why it matters, and how to fix it\", " +
        "\"suggestion\": \"the replacement code, or null\"}]}. No findings: {\"findings\": []}. Do not invent problems.";

    public const string VerifyPrompt =
        "You check another reviewer's finding on a code change, as a skeptic: try to REFUTE it. It is real only if the diff shows the " +
        "problem and nothing in it (a check elsewhere in the shown code, a guarantee of the language, the change's evident intent) makes " +
        "it harmless. When unsure, it is not real. Answer with JSON only: {\"real\": true|false, \"why\": \"one or two sentences\"}.";

    public const string SummaryPrompt =
        "You write the summary of a code review for the change's author and reviewers. Given the change's description, its files and the " +
        "findings that survived checking, write Markdown: a short walkthrough of what the change does, file by file (one line each); then " +
        "what to look at first. Then, on the last line alone, JSON: {\"verdict\": \"looks_good|comments|changes\", \"risk\": \"low|medium|high\"}.";

    private static async Task<List<ReviewFinding>> ReviewUnitAsync(IServiceProvider services, CodeReview review, string email, string model, string description, Unit unit,
        CancellationToken ct)
    {
        var ask = $"The change:\n{Cut(description, 4_000)}\n\nFile: {unit.Path}\n```diff\n{unit.Text}\n```";
        var text = await AskAsync(services, review, email, model, ReviewPrompt, ask, 2_000, ct);
        var found = new List<ReviewFinding>();
        if (Json(text)?["findings"] is not JsonArray list)
        {
            return found;
        }
        var numbered = NumberedLines(unit.Text);
        foreach (var f in list.OfType<JsonObject>().Take(20))
        {
            var title = f["title"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }
            int? line = f["line"] is JsonValue v && v.TryGetValue<int>(out var n) && numbered.Contains(n) ? n : null;
            found.Add(new ReviewFinding
            {
                Path = unit.Path,
                Line = line,
                Severity = f["severity"]?.ToString() is "critical" or "major" or "minor" or "nit" ? f["severity"]!.ToString() : "minor",
                Category = Cut(f["category"]?.ToString() ?? "", 32) is { Length: > 0 } c ? c : null,
                Title = Cut(title, 300),
                Body = Cut(f["body"]?.ToString() ?? "", 4_000),
                Suggestion = f["suggestion"] is JsonValue s && s.ToString() is { Length: > 0 } code && code != "null" ? Cut(code, 4_000) : null,
            });
        }
        return found;
    }

    /// <summary>The new-file line numbers a numbered diff shows: where a finding may be anchored.</summary>
    public static HashSet<int> NumberedLines(string numbered) =>
        [.. numbered.Split('\n').Select(l => l.Length > 5 && int.TryParse(l[..5].Trim(), out var n) ? n : 0).Where(n => n > 0)];

    private static async Task VerifyAsync(IServiceProvider services, CodeReview review, string email, string model, ReviewFinding finding, Unit? unit, CancellationToken ct)
    {
        var ask = $"File: {finding.Path}\n```diff\n{Cut(unit?.Text ?? "", UnitChars)}\n```\n\nThe finding (line {finding.Line?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}, {finding.Severity}): {finding.Title}\n{finding.Body}";
        var text = await AskAsync(services, review, email, model, VerifyPrompt, ask, 400, ct);
        if (Json(text) is { } verdict && verdict["real"] is JsonValue real && real.TryGetValue<bool>(out var isReal))
        {
            finding.Verified = isReal;
            finding.VerifyNote = Cut(verdict["why"]?.ToString() ?? "", 1_000);
        }
    }

    private static async Task SummarizeAsync(IServiceProvider services, CodeReview review, string email, string model, string description,
        List<(string Path, string Diff)> files, List<ReviewFinding> findings, CancellationToken ct)
    {
        var kept = findings.Where(f => f.Verified != false).ToList();
        var ask = new StringBuilder($"The change:\n{Cut(description, 6_000)}\n\nFiles:\n");
        foreach (var (path, diff) in files)
        {
            ask.Append("- ").Append(path).Append(" (").Append(diff.Split('\n').Count(l => l.StartsWith('+'))).Append(" lines added)\n");
        }
        ask.Append("\nCI: ").Append(review.Ci ?? "unknown").Append("\n\nFindings:\n");
        foreach (var f in kept)
        {
            ask.Append("- [").Append(f.Severity).Append("] ").Append(f.Path).Append(f.Line is { } l ? $":{l}" : "").Append(' ').Append(f.Title).Append('\n');
        }
        if (kept.Count == 0)
        {
            ask.Append("(none)\n");
        }
        Verdict(review, (await AskAsync(services, review, email, model, SummaryPrompt, ask.ToString(), 1_500, ct)).Trim(), kept);
    }

    /// <summary>One request to the model, as the person, tagged "review"; its tokens counted on the review.</summary>
    private static Task<string> AskAsync(IServiceProvider services, CodeReview review, string email, string model, string system, string ask, int maxTokens,
        CancellationToken ct) =>
        AskModelAsync(services, model, email, system, ask, maxTokens, (p, c) => (review.PromptTokens, review.CompletionTokens) = (review.PromptTokens + p, review.CompletionTokens + c), ct);

    /// <summary>One request to the model, as the person, tagged "review", thinking off; its tokens to <paramref name="used"/>.</summary>
    private static async Task<string> AskModelAsync(IServiceProvider services, string model, string email, string system, string ask, int maxTokens, Action<int, int> used,
        CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system }, new JsonObject { ["role"] = "user", ["content"] = ask }),
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.1,
            ["user"] = email,
            ["metadata"] = new JsonObject { ["tags"] = new JsonArray(Tag) },
            ["chat_template_kwargs"] = ThinkingPresets.TemplateKwargs("off"),
        };
        var text = new StringBuilder();
        await foreach (var e in services.GetRequiredService<GatewayChat>().StreamAsync(request, email, ct))
        {
            if (e is ContentDelta c)
            {
                text.Append(c.Text);
            }
            else if (e is UsageReport u)
            {
                used(u.Prompt, u.Completion);
            }
        }
        return text.ToString();
    }

    /// <summary>The first JSON object in a model's answer (it may wrap it in a code block or words).</summary>
    public static JsonObject? Json(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(text[start..(end + 1)]) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review {Id} failed")]
    private static partial void LogFailed(ILogger logger, Guid id, Exception ex);
}

/// <summary>A review that cannot go on, with words for the person.</summary>
public sealed class ReviewException(string message) : Exception(message);
