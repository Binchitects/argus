using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Core.Code;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Code;

/// <summary>
/// Code review as a tool: the chat (and, through Arena MCP, coding agents and Code Arena) reviews a merge request or a
/// branch over another, as the person (ReviewEngine), waits for it, and reads the findings; or reads a review made
/// before. The review is also on the Code page, with its findings to resolve.
/// </summary>
public sealed class ReviewTool(AppDbContext db, ReviewEngine engine, ArgusMcp argus, TimeProvider clock) : IChatTool
{
    public const string ToolId = "reviews";

    /// <summary>The longest the tool waits for a review to end before saying where to read it.</summary>
    public static TimeSpan Wait { get; set; } = TimeSpan.FromMinutes(15);

    public string Id => ToolId;
    public string Title => "Code review";
    public string Description => "Reviews merge requests and branches with the model, as you (your GitLab access), and reads reviews made before.";
    public string Icon => "search-code";

    public Task<string?> UnavailableAsync(CancellationToken ct) =>
        Task.FromResult(argus.Enabled ? null : "Argus is not set up here: it reads the code for reviews.");

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [
            Schema.Function("review_merge_request", "Reviews a GitLab merge request with the model as the person: findings on lines, each checked by a second pass, and a summary with a verdict. Takes a few minutes; the review is also on the Code page.",
                new JsonObject { ["project"] = Schema.Text("The project's path, group/project"), ["iid"] = new JsonObject { ["type"] = "integer", ["description"] = "The merge request's number" } },
                "project", "iid"),
            Schema.Function("review_branch", "Reviews what one branch (to) has over another (from) in a GitLab project, with no merge request.",
                new JsonObject { ["project"] = Schema.Text("group/project"), ["from"] = Schema.Text("What it goes into, e.g. main"), ["to"] = Schema.Text("The branch") },
                "project", "from", "to"),
            Schema.Function("review_get", "A review made before, by its id: its summary, verdict and findings.",
                new JsonObject { ["id"] = Schema.Text("The review's id") }, "id"),
        ],
        "Use review_merge_request when the person asks to review a merge request (by its link or number); say the verdict and the main findings, " +
        "with their files and lines, and that the review is on the Code page. Nothing is posted to GitLab.",
        async (name, args, token) =>
        {
            if (name == "review_get")
            {
                return Guid.TryParse(Schema.Str(args, "id"), out var rid) && await db.CodeReviews.AsNoTracking().Include(r => r.Findings)
                        .SingleOrDefaultAsync(r => r.Id == rid && r.UserId == context.User.Id, token) is { } found
                    ? new ToolResult(Shape(found).ToJsonString(Mcp.Plain))
                    : new ToolResult("There is no such review of yours.", IsError: true);
            }
            var project = (Schema.Str(args, "project") ?? "").Trim().Trim('/');
            var iid = args["iid"] is JsonValue v && (v.TryGetValue<int>(out var n) || (v.TryGetValue<string>(out var t) && int.TryParse(t.TrimStart('!'), out n))) ? n : (int?)null;
            if (project.Length == 0 || (name == "review_merge_request" && iid is null) || (name == "review_branch" && (string.IsNullOrWhiteSpace(Schema.Str(args, "from")) || string.IsNullOrWhiteSpace(Schema.Str(args, "to")))))
            {
                return new ToolResult("Give the project (group/project) and the merge request's number, or the two branches.", IsError: true);
            }
            var review = new CodeReview
            {
                UserId = context.User.Id, Project = project, Iid = name == "review_merge_request" ? iid : null,
                FromRef = Schema.Str(args, "from")?.Trim(), ToRef = Schema.Str(args, "to")?.Trim(), Title = iid is { } i ? $"!{i}" : $"{Schema.Str(args, "to")} over {Schema.Str(args, "from")}",
            };
            db.CodeReviews.Add(review);
            await db.SaveChangesAsync(token);
            engine.Start(review.Id);
            var until = clock.GetUtcNow() + Wait;
            string? said = null;
            while (clock.GetUtcNow() < until)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                var now = await db.CodeReviews.AsNoTracking().Include(r => r.Findings).SingleAsync(r => r.Id == review.Id, token);
                if (now.Status is CodeReview.Done or CodeReview.Failed)
                {
                    return now.Status == CodeReview.Failed ? new ToolResult($"The review failed: {now.Error}", IsError: true) : new ToolResult(Shape(now).ToJsonString(Mcp.Plain));
                }
                if (now.Step is { } step && step != said && context.Progress is { } progress)
                {
                    said = step;
                    await progress.ReportAsync(new McpProgress(0, null, step));
                }
            }
            return new ToolResult($"The review is still running: read it with review_get (id {review.Id}), or on the Code page.");
        }));

    private static JsonObject Shape(CodeReview r) => new()
    {
        ["id"] = r.Id.ToString(), ["project"] = r.Project, ["title"] = r.Title, ["url"] = r.WebUrl, ["verdict"] = r.Verdict, ["risk"] = r.Risk, ["ci"] = r.Ci,
        ["summary"] = r.Summary,
        ["findings"] = new JsonArray([.. r.Findings.Where(f => f.Verified != false).OrderBy(f => f.Severity switch { "critical" => 0, "major" => 1, "minor" => 2, _ => 3 })
            .Select(f => (JsonNode)new JsonObject
            {
                ["severity"] = f.Severity, ["path"] = f.Path, ["line"] = f.Line, ["title"] = f.Title, ["body"] = f.Body, ["suggestion"] = f.Suggestion, ["state"] = f.State,
            })]),
        ["doubted"] = r.Findings.Count(f => f.Verified == false),
        ["page"] = $"/code/reviews/{r.Id}",
    };
}
