using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Code;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Code.Fixes;

/// <summary>What to fix: a review finding, a security review's finding, a GitLab issue, a failed pipeline, or words; in a project, from a branch.</summary>
public sealed record NewFix(Guid? FindingId = null, string? Project = null, string? Branch = null, string? Ask = null, int? Issue = null, long? Pipeline = null,
    Guid? SecurityFindingId = null);

public sealed record MoreFix(string? Ask);

public sealed record FixToken(string? Token);

public sealed record MergeRequestFix(string? Title = null);

/// <summary>
/// Fix it: an agent changes the code for a finding, an issue, a failed pipeline or the person's words, runs the tests it can,
/// and shows the diff; the merge request is opened as the person, with their own GitLab token (scope api), linked here.
/// </summary>
public static partial class FixEndpoints
{
    public const int MostWorking = 2;

    public static void MapFixes(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/fixes").PersonOnly().RequirePermission(Core.Access.Permissions.FixIt);
        g.MapGet("/token", TokenAsync);
        g.MapPut("/token", LinkAsync);
        g.MapDelete("/token", UnlinkAsync);
        g.MapGet("", ListAsync);
        g.MapPost("", CreateAsync);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPost("/{id:guid}/more", MoreAsync);
        g.MapPost("/{id:guid}/stop", StopAsync);
        g.MapPost("/{id:guid}/merge-request", MergeRequestAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static async Task<IResult> TokenAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, GitLabAsPerson gitlab, CancellationToken ct)
    {
        var me = await Me(p, users);
        var saved = await db.PersonCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.UserId == me.Id && c.ToolId == GitLabAsPerson.ToolId, ct);
        return Results.Ok(new { linked = saved is not null, account = saved?.Account, linkedAt = saved?.CreatedAt, gitlab = gitlab.BaseUrl });
    }

    /// <summary>The person's GitLab token for Fix it: checked with GitLab now (who it is), kept encrypted, never shown again.</summary>
    private static async Task<IResult> LinkAsync(FixToken body, ClaimsPrincipal p, UserManager<AppUser> users, GitLabAsPerson gitlab, Plugins.PersonCredentials credentials,
        Microsoft.Extensions.Options.IOptions<AuthOptions> auth, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        var token = body.Token?.Trim() ?? "";
        if (token.Length is < 8 or > 500 || token.Any(char.IsWhiteSpace))
        {
            return AuthEndpoints.Problem(400, "token", "Paste a GitLab personal access token with the api scope.");
        }
        if (auth.Value.DataKey is not { Length: > 0 })
        {
            return AuthEndpoints.Problem(400, "data_key", "APP_DATA_KEY is not set, so a token (a secret) cannot be stored encrypted.");
        }
        string login;
        try
        {
            login = (await gitlab.GetAsync(token, "/user", ct))["username"]?.GetValue<string>() ?? "";
        }
        catch (GitLabException ex)
        {
            return AuthEndpoints.Problem(400, "token", $"GitLab did not take it: {ex.Message}");
        }
        await credentials.SaveAsync(me.Id, GitLabAsPerson.ToolId, token, null, null, login, ct);
        await audit.WriteAsync("fixit.link", login);
        return Results.Ok(new { linked = true, account = login });
    }

    private static async Task<IResult> UnlinkAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.PersonCredentials.Where(c => c.UserId == me.Id && c.ToolId == GitLabAsPerson.ToolId).ExecuteDeleteAsync(ct) == 0)
        {
            return Results.NotFound();
        }
        await audit.WriteAsync("fixit.unlink", me.UserName ?? "");
        return Results.NoContent();
    }

    private static object Brief(CodeFix f) => new { f.Id, f.Project, f.Branch, f.Title, f.Source, f.Status, f.Step, f.MergeRequestUrl, f.Error, f.CreatedAt, f.UpdatedAt };

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        var fixes = await db.CodeFixes.AsNoTracking().Where(f => f.UserId == me.Id).OrderByDescending(f => f.CreatedAt).Take(200).ToListAsync(ct);
        return Results.Ok(fixes.Select(Brief));
    }

    private static string Cut(string s, int most) => s.Length > most ? s[..most] + "…" : s;

    /// <summary>A fix started: what it is for, read now (the finding, the issue, the failed jobs' logs), and the branch it starts from.</summary>
    private static async Task<IResult> CreateAsync(NewFix body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, GitLabAsPerson gitlab, FixRuns runs,
        AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await gitlab.TokenAsync(me.Id, ct) is not { } token)
        {
            return AuthEndpoints.Problem(409, "token", "Link your GitLab token for Fix it first: it reads the branch and opens the merge request as you.");
        }
        if (await db.CodeFixes.CountAsync(f => f.UserId == me.Id && f.Status == CodeFix.Working, ct) >= MostWorking)
        {
            return AuthEndpoints.Problem(429, "busy", $"{MostWorking} fixes of yours are at work: wait for one to end.");
        }
        string project, branch, title, ask, source;
        string? sourceId = null;
        try
        {
            if (body.SecurityFindingId is { } securityId)
            {
                var finding = await db.SecurityFindings.AsNoTracking().SingleOrDefaultAsync(f => f.Id == securityId, ct);
                var scan = finding is null ? null : await db.SecurityScans.AsNoTracking().SingleOrDefaultAsync(s => s.Id == finding.ScanId, ct);
                if (finding is null || scan is null || (scan.UserId != me.Id && !await access.MayAsync(me, Core.Access.Permissions.ReviewsAdmin, ct)))
                {
                    return Results.NotFound();
                }
                project = scan.Project;
                branch = scan.Ref ?? await DefaultBranchAsync(gitlab, token, project, ct);
                title = finding.Kind == SecurityFinding.Dependency ? $"Upgrade {finding.SinkCode.Split(' ')[0]}: {finding.Title}" : $"{finding.Cwe} {finding.Title}".Trim();
                ask = SecurityAsk(finding, scan) + (body.Ask is { Length: > 0 } more ? $"\n\n{more}" : "");
                (source, sourceId) = ("security", finding.Id.ToString());
            }
            else if (body.FindingId is { } findingId)
            {
                var finding = await db.ReviewFindings.AsNoTracking().SingleOrDefaultAsync(f => f.Id == findingId, ct);
                var review = finding is null ? null : await db.CodeReviews.AsNoTracking().SingleOrDefaultAsync(r => r.Id == finding.ReviewId && r.UserId == me.Id, ct);
                if (finding is null || review is null)
                {
                    return Results.NotFound();
                }
                project = review.Project;
                branch = review.ToRef ?? await DefaultBranchAsync(gitlab, token, project, ct);
                title = finding.Title;
                ask = $"Fix this review finding in {finding.Path}{(finding.Line is { } l ? $" at line {l}" : "")} ({finding.Severity}{(finding.Category is { } c ? $", {c}" : "")}): {finding.Title}\n\n{finding.Body}"
                    + (finding.Suggestion is { Length: > 0 } s ? $"\n\nThe review suggests:\n```\n{s}\n```" : "");
                (source, sourceId) = ("finding", finding.Id.ToString());
            }
            else
            {
                project = body.Project?.Trim().Trim('/') ?? "";
                if (!ProjectPath().IsMatch(project))
                {
                    return AuthEndpoints.Problem(400, "project", "Name the project as its path: group/project.");
                }
                branch = body.Branch?.Trim() is { Length: > 0 } b ? b : await DefaultBranchAsync(gitlab, token, project, ct);
                if (body.Issue is { } iid)
                {
                    var issue = await gitlab.GetAsync(token, $"{GitLabAsPerson.Project(project)}/issues/{iid}", ct);
                    title = issue["title"]?.GetValue<string>() ?? $"Issue #{iid}";
                    ask = $"Fix issue #{iid}: {title}\n\n{Cut(issue["description"]?.GetValue<string>() ?? "", 8000)}" + (body.Ask is { Length: > 0 } more ? $"\n\n{more}" : "");
                    (source, sourceId) = ("issue", iid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else if (body.Pipeline is { } pipelineId)
                {
                    var pipeline = await gitlab.GetAsync(token, $"{GitLabAsPerson.Project(project)}/pipelines/{pipelineId}", ct);
                    branch = body.Branch?.Trim() is { Length: > 0 } chosen ? chosen : pipeline["ref"]?.GetValue<string>() ?? branch;
                    var jobs = await gitlab.GetAsync(token, $"{GitLabAsPerson.Project(project)}/pipelines/{pipelineId}/jobs?scope[]=failed&per_page=5", ct) as JsonArray ?? [];
                    var logs = new StringBuilder();
                    foreach (var job in jobs.OfType<JsonObject>().Take(3))
                    {
                        var trace = await gitlab.TextAsync(token, $"{GitLabAsPerson.Project(project)}/jobs/{job["id"]}/trace", ct);
                        logs.Append(System.Globalization.CultureInfo.InvariantCulture, $"\n\nJob {job["name"]} (stage {job["stage"]}) failed; the end of its log:\n```\n{(trace.Length > 4000 ? trace[^4000..] : trace)}\n```");
                    }
                    title = $"Pipeline {pipelineId} on {branch}";
                    ask = $"Make the failed pipeline {pipelineId} pass: change the code so its failed jobs succeed.{logs}" + (body.Ask is { Length: > 0 } more ? $"\n\n{more}" : "");
                    (source, sourceId) = ("pipeline", pipelineId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    ask = body.Ask?.Trim() ?? "";
                    if (ask.Length is 0 or > 20_000)
                    {
                        return AuthEndpoints.Problem(400, "ask", "Say what to fix, up to 20,000 characters.");
                    }
                    title = ask.Split('\n')[0] is var first && first.Length > 100 ? first[..100] + "…" : first;
                    source = "ask";
                }
            }
        }
        catch (GitLabException ex)
        {
            return AuthEndpoints.Problem(400, "gitlab", ex.Message);
        }
        var fix = new CodeFix { UserId = me.Id, Project = project, Branch = branch, Title = Cut(title, 200), Ask = Cut(ask, 30_000), Source = source, SourceId = sourceId, Step = "Starting" };
        db.CodeFixes.Add(fix);
        await db.SaveChangesAsync(ct);
        runs.Go(fix.Id);
        return Results.Accepted($"/api/fixes/{fix.Id}", Brief(fix));
    }

    /// <summary>
    /// What the agent is asked for a security review's finding: the weakness as the review found it (where, the data's path,
    /// the references), a test that shows it first where one can, the change the references describe, and the tests run; a
    /// vulnerable package upgraded the smallest step that fixes it; a secret taken out of the code (and revoked by the person).
    /// </summary>
    public static string SecurityAsk(SecurityFinding f, SecurityScan scan)
    {
        var pages = (JsonNode.Parse(f.Pages) as JsonArray ?? []).OfType<JsonObject>()
            .Select(p => $"- {p["title"]}{(p["url"]?.ToString() is { Length: > 0 } url ? $": {url}" : "")}").ToList();
        var references = pages.Count > 0 ? $"\n\nWhat it rests on (read them):\n{string.Join('\n', pages)}" : "";
        var reviewed = scan.Commit is { } c ? $" The security review read commit {c[..Math.Min(12, c.Length)]}; the branch may have moved since: if the weakness is gone, say so and change nothing." : "";
        var where = $"{f.Path}{(f.Line > 0 ? $" at line {f.Line}" : "")}";
        return f.Kind switch
        {
            SecurityFinding.Dependency =>
                $"A package this project uses has a known vulnerability: {f.SinkCode}, in {where}. {f.Title} ({f.Severity}, {f.Source}). {f.Flow}." +
                (f.Exploited ? " It is on CISA's list of known exploited vulnerabilities." : "") + $"\n\n{f.Fix}\n\n" +
                "Change the version where the project asks for it (its manifest). Update the lock file too only where it can be done exactly by hand; " +
                "when the package manager must regenerate it, say so. When the package comes in through another, upgrade that one, or pin a safe " +
                "version the way the ecosystem allows (npm overrides, Yarn resolutions, a NuGet PackageReference, a Maven dependencyManagement entry). " +
                "Run the tests that can run here." + reviewed + references,
            SecurityFinding.Secret =>
                $"A secret ({f.Rule ?? "a credential"}) is in {where}{(f.Commit is { } at ? $", in the history at commit {at[..Math.Min(12, at.Length)]}" : "")}. " +
                "Take it out of the code: read it from the environment or the project's secret store instead, leaving a placeholder or an example " +
                "with no real value, and never write the secret anywhere, not in a test, a comment or the summary. Say in the summary that the person " +
                "must revoke it and make a new one: whoever could read the repository has it, and taking it out of the code does not take it back." + reviewed,
            SecurityFinding.Config =>
                $"Fix this configuration finding in {where} ({f.Rule}): {f.Title}.\n\n{f.Fix}\n\nKeep what the configuration is for working, and say what changes for whoever runs it." + reviewed,
            _ =>
                $"Fix this security weakness in {where}: {f.Cwe} ({f.CweTitle}), {f.Severity}. {f.Title}\n\n" +
                (f.SinkCode.Length > 0 ? $"The line, as the review read it:\n```\n{f.SinkCode}\n```\n" : "") +
                (f.Source is { Length: > 0 } src ? $"Where outside data comes in: {src}\n" : "") +
                (f.Flow.Length > 0 ? $"Its path to the sink:\n{f.Flow}\n" : "") +
                (f.Exploit.Length > 0 ? $"How it could be exploited: {f.Exploit}\n" : "") +
                (f.Fix.Length > 0 ? $"The fix the review suggests: {f.Fix}\n" : "") +
                "\nFirst write a test that shows the weakness where one can be written (a request that injects, a path that escapes its folder, a " +
                "redirect that leaves the site), and see it fail. Then make the change the references describe: a parameterised query rather than an " +
                "escaped string, an allow-list rather than a deny-list, encoding where the data is written. Run the tests, the new one with them." +
                reviewed + references,
        };
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+(/[A-Za-z0-9_.\-]+)+$")]
    private static partial Regex ProjectPath();

    private static async Task<string> DefaultBranchAsync(GitLabAsPerson gitlab, string token, string project, CancellationToken ct) =>
        (await gitlab.GetAsync(token, GitLabAsPerson.Project(project), ct))["default_branch"]?.GetValue<string>() ?? throw new GitLabException("The project has no default branch.");

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.CodeFixes.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id && f.UserId == me.Id, ct) is not { } f)
        {
            return Results.NotFound();
        }
        var changes = JsonSerializer.Deserialize<Dictionary<string, string?>>(f.Changes) ?? [];
        return Results.Ok(new
        {
            f.Id, f.Project, f.Branch, f.BaseSha, f.Title, f.Ask, f.Source, f.SourceId, f.Status, f.Step, f.Summary, f.Diff, f.Model, f.MergeRequestUrl, f.Error, f.CreatedAt, f.UpdatedAt,
            log = JsonNode.Parse(f.Log), tests = f.Tests is { } t ? JsonNode.Parse(t) : null, checks = f.Checks is { } c ? JsonNode.Parse(c) : null,
            files = changes.Select(c => new { path = c.Key, deleted = c.Value is null }),
        });
    }

    /// <summary>Another round: more to do on the same change.</summary>
    private static async Task<IResult> MoreAsync(Guid id, MoreFix body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, FixRuns runs, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.CodeFixes.SingleOrDefaultAsync(f => f.Id == id && f.UserId == me.Id, ct) is not { } f)
        {
            return Results.NotFound();
        }
        var more = body.Ask?.Trim() ?? "";
        if (more.Length is 0 or > 10_000)
        {
            return AuthEndpoints.Problem(400, "ask", "Say what more to do, up to 10,000 characters.");
        }
        if (f.Status is CodeFix.Working or CodeFix.Opened || runs.IsRunning(id))
        {
            return AuthEndpoints.Problem(409, "status", f.Status == CodeFix.Opened ? "Its merge request is open: change it there, or start a new fix." : "It is at work.");
        }
        f.Status = CodeFix.Working;
        f.Step = "Starting";
        f.Error = null;
        f.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        runs.Go(id, more);
        return Results.Accepted($"/api/fixes/{id}", Brief(f));
    }

    private static async Task<IResult> StopAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, FixRuns runs, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.CodeFixes.SingleOrDefaultAsync(f => f.Id == id && f.UserId == me.Id, ct) is not { } f)
        {
            return Results.NotFound();
        }
        if (!runs.Stop(id) && f.Status == CodeFix.Working)
        {
            f.Status = CodeFix.Stopped;
            f.Error = "Stopped.";
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    /// <summary>The change committed to a new branch and a merge request opened into the fix's branch, as the person (their token).</summary>
    private static async Task<IResult> MergeRequestAsync(Guid id, MergeRequestFix body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, GitLabAsPerson gitlab,
        Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.CodeFixes.SingleOrDefaultAsync(f => f.Id == id && f.UserId == me.Id, ct) is not { } f)
        {
            return Results.NotFound();
        }
        if (f.Status != CodeFix.Ready)
        {
            return AuthEndpoints.Problem(409, "status", f.Status == CodeFix.Opened ? "Its merge request is open already." : "It is not ready.");
        }
        if (await gitlab.TokenAsync(me.Id, ct) is not { } token)
        {
            return AuthEndpoints.Problem(409, "token", "Link your GitLab token for Fix it first.");
        }
        var changes = JsonSerializer.Deserialize<Dictionary<string, string?>>(f.Changes) ?? [];
        var slug = Slug().Replace(f.Title.ToLowerInvariant(), "-").Trim('-');
        var branch = $"fix/{(slug.Length > 40 ? slug[..40].Trim('-') : slug)}-{f.Id.ToString("N")[^6..]}";
        var title = body.Title?.Trim() is { Length: > 0 } t ? Cut(t, 200) : $"Fix: {f.Title}";
        try
        {
            var actions = new JsonArray();
            foreach (var (path, content) in changes)
            {
                var exists = await ExistsAsync(gitlab, token, f.Project, path, f.BaseSha ?? f.Branch, ct);
                actions.Add(content is null
                    ? new JsonObject { ["action"] = "delete", ["file_path"] = path }
                    : new JsonObject { ["action"] = exists ? "update" : "create", ["file_path"] = path, ["content"] = content });
            }
            await gitlab.PostAsync(token, $"{GitLabAsPerson.Project(f.Project)}/repository/commits", new JsonObject
            {
                ["branch"] = branch, ["start_sha"] = f.BaseSha, ["commit_message"] = $"{title}\n\nMade with Fix it (Argus Arena) for {me.DisplayName}.", ["actions"] = actions,
            }, ct);
            var mr = await gitlab.PostAsync(token, $"{GitLabAsPerson.Project(f.Project)}/merge_requests", new JsonObject
            {
                ["source_branch"] = branch, ["target_branch"] = f.Branch, ["title"] = title, ["remove_source_branch"] = true,
                ["description"] = $"{f.Summary}" + (FixChecks.Describe(f.Checks is { } checks ? JsonNode.Parse(checks) as JsonObject : null) is { Length: > 0 } said ? $"\n\n**Checked after the change:** {said}" : "") +
                    $"\n\n---\n*Made with Fix it in Argus Arena, opened by {me.DisplayName}.*",
            }, ct);
            f.MergeRequestUrl = mr["web_url"]?.GetValue<string>();
        }
        catch (GitLabException ex)
        {
            return AuthEndpoints.Problem(400, "gitlab", ex.Message);
        }
        f.Status = CodeFix.Opened;
        f.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("fixit.merge_request", f.Project, detail: f.MergeRequestUrl);
        return Results.Ok(new { url = f.MergeRequestUrl, branch });
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex Slug();

    private static async Task<bool> ExistsAsync(GitLabAsPerson gitlab, string token, string project, string path, string @ref, CancellationToken ct)
    {
        try
        {
            await gitlab.GetAsync(token, $"{GitLabAsPerson.Project(project)}/repository/files/{Uri.EscapeDataString(path)}?ref={Uri.EscapeDataString(@ref)}", ct);
            return true;
        }
        catch (GitLabException ex) when (ex.Message.Contains("404", StringComparison.Ordinal))
        {
            return false;
        }
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, FixRuns runs, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.CodeFixes.SingleOrDefaultAsync(f => f.Id == id && f.UserId == me.Id, ct) is not { } f)
        {
            return Results.NotFound();
        }
        runs.Stop(id);
        db.CodeFixes.Remove(f);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
