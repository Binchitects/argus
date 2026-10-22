using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Media;
using Llm.Api.Vector;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat.Tools;

/// <summary>
/// Vector drawings in the chat, made as the media studio makes them (its work, its limits, its gallery): an illustration from
/// words (drawn flat by the picture model, traced into named parts) or SVG drawn by the model (icons, diagrams, logos), and a
/// drawing animated as asked. Each is shown in the chat as an SVG that plays its motions, and kept in the studio to change
/// and export.
/// </summary>
public sealed partial class VectorTool(AppDbContext db, MediaJobs jobs, Operations.Modules modules, SandboxClient sandbox, Storage.StorageQuotas quotas) : IChatTool
{
    public const string ToolId = "vector";

    public string Id => ToolId;
    public string Title => "Vector drawings";
    public string Description => "Draws SVG illustrations, icons and diagrams from words, and animates them as asked; each is kept in the media studio.";
    public string Icon => "image";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public async Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct)
    {
        // Illustrations are drawn by the picture server and traced in the sandbox; without them, the model draws the SVG.
        var traces = sandbox.Unavailable() is null && await modules.HasAsync("imagegen", ct);
        var kinds = traces ? new JsonArray("illustration", "drawing") : new JsonArray("drawing");
        return new LocalRun(
            [
                Schema.Function("draw_svg", "Makes a vector drawing (SVG) from a description and shows it to the person. " +
                    (traces ? "\"illustration\" (the default): a rich flat illustration, its things named as parts (a fox, the moon, stars) ready to animate; " : "") +
                    "\"drawing\": SVG written by the model, best for icons, diagrams, logos and simple shapes. Write the description in English, concrete and visual.",
                    new JsonObject
                    {
                        ["description"] = Schema.Text("What the drawing shows: its subject, the things in it, colours"),
                        ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = kinds, ["description"] = traces ? "illustration (default) or drawing" : "drawing" },
                        ["style"] = new JsonObject
                        {
                            ["type"] = "string", ["enum"] = new JsonArray([.. SvgDraw.Styles.Select(s => (JsonNode)s.Name)]), ["description"] = "for a drawing: its style (default flat)",
                        },
                    }, "description"),
                Schema.Function("animate_svg", "Gives a drawing made by draw_svg motions as asked (its parts twinkle, glow, breathe, drift, sway, spin, fade in...) " +
                    "and shows it playing. Say what moves and how; the parts are the ones draw_svg named.",
                    new JsonObject
                    {
                        ["drawing"] = Schema.Text("The drawing's id, as draw_svg gave it"),
                        ["motion"] = Schema.Text("What moves and how, in words: the stars twinkle one after another, the boat rocks"),
                    }, "drawing", "motion"),
            ],
            "When the person asks for an SVG, a vector illustration, an icon or a logo, call draw_svg; to make it move (an animated SVG), " +
            "call animate_svg with its id. Each is shown to them under your answer: do not repeat or link it; say in a sentence what you made, " +
            "and that it can be changed and exported (GIF, MP4) in the media studio.",
            (name, args, token) => name == "animate_svg" ? AnimateAsync(context, args, token) : DrawAsync(context, args, traces, token));
    }

    private async Task<ToolResult> DrawAsync(ToolContext context, JsonObject args, bool traces, CancellationToken ct)
    {
        var description = (Schema.Str(args, "description") ?? "").Trim();
        if (description.Length == 0)
        {
            return new ToolResult("Say what the drawing shows in 'description'.", IsError: true);
        }
        var trace = traces && Schema.Str(args, "kind") != "drawing";
        var style = Schema.Str(args, "style") is { } s && SvgDraw.Styles.Any(x => x.Name == s) ? s : "flat";
        var options = trace ? new JsonObject { ["trace"] = "illustration" } : new JsonObject { ["style"] = style, ["width"] = 512, ["height"] = 512 };
        return await RunAsync(context, trace ? MediaJob.Trace : MediaJob.Draw, description, null, options, ct);
    }

    private async Task<ToolResult> AnimateAsync(ToolContext context, JsonObject args, CancellationToken ct)
    {
        var motion = (Schema.Str(args, "motion") ?? "").Trim();
        if (!Guid.TryParse(Schema.Str(args, "drawing"), out var drawing)
            || !await db.MediaItems.AnyAsync(i => i.Id == drawing && i.OwnerId == context.User.Id && i.Kind == MediaItem.Vector, ct))
        {
            return new ToolResult("There is no drawing of the person's with that id: use the id draw_svg gave.", IsError: true);
        }
        if (motion.Length == 0)
        {
            return new ToolResult("Say what moves and how in 'motion'.", IsError: true);
        }
        return await RunAsync(context, MediaJob.Animate, motion, drawing, new JsonObject(), ct);
    }

    /// <summary>The studio's work done as the person, followed until it is (its steps said as it goes), and the drawing shown.</summary>
    private async Task<ToolResult> RunAsync(ToolContext context, string action, string prompt, Guid? source, JsonObject options, CancellationToken ct)
    {
        if (await quotas.ToolRefusalAsync(context.User.Id, "drawing", ct) is { } full)
        {
            return new ToolResult(full, IsError: true);
        }
        var since = DateTimeOffset.UtcNow - MediaJobs.Lost;
        if (await db.MediaJobs.CountAsync(j => j.OwnerId == context.User.Id && j.Status == MediaJob.Working && j.CreatedAt > since, ct) >= MediaJobs.MostGoing)
        {
            return new ToolResult($"The person has {MediaJobs.MostGoing} pieces of studio work going: try again when one is done.", IsError: true);
        }
        var job = new MediaJob
        {
            OwnerId = context.User.Id, Action = action, Prompt = prompt[..Math.Min(2000, prompt.Length)], SourceId = source, Options = options.ToJsonString(), Phase = "Starting",
        };
        db.MediaJobs.Add(job);
        await db.SaveChangesAsync(ct);
        jobs.Go(job.Id);
        var finished = jobs.Finished(job.Id);
        var said = "";
        while (!finished.IsCompleted)
        {
            await Task.WhenAny(finished, Task.Delay(1000, ct));
            var phase = await db.MediaJobs.AsNoTracking().Where(j => j.Id == job.Id).Select(j => j.Phase).SingleAsync(ct);
            if (context.Progress is { } progress && phase is { Length: > 0 } && phase != said)
            {
                said = phase;
                await progress.ReportAsync(new McpProgress(0, null, phase));
            }
        }
        var done = await db.MediaJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, ct);
        if (done.Status != MediaJob.Done)
        {
            return new ToolResult($"The drawing could not be made: {done.Error}", IsError: true);
        }
        var id = action == MediaJob.Animate ? source!.Value : JsonSerializer.Deserialize<Guid[]>(done.Items ?? "[]")!.First();
        var item = await db.MediaItems.AsNoTracking().SingleAsync(i => i.Id == id, ct);
        var vector = await db.MediaVectors.AsNoTracking().SingleAsync(v => v.ItemId == id, ct);
        var made = await db.ChatAttachments.AsNoTracking().SingleAsync(a => a.Id == item.AttachmentId, ct);
        // Shown as a picture that plays its motions: a copy, so the chat keeps what it showed while the studio's changes on.
        var file = new ChatAttachment
        {
            UserId = context.User.Id, FileName = made.FileName, ContentType = "image/svg+xml", Size = made.Size, Kind = "image", Data = made.Data, Text = "", Origin = "picture",
        };
        db.ChatAttachments.Add(file);
        await db.SaveChangesAsync(ct);
        var answer = new JsonObject
        {
            ["shown_to_the_person"] = true, ["drawing"] = id.ToString(), ["file"] = file.FileName,
            ["parts"] = new JsonArray([.. Parts(vector.Still).Select(p => (JsonNode)p)]),
        };
        if (action == MediaJob.Animate)
        {
            answer["motions"] = new JsonArray([.. VectorStore.MotionsOf(vector.Motions).Select(m => (JsonNode)$"{m.Target}: {m.Kind}")]);
            answer["seconds"] = Math.Round(vector.Seconds, 1);
        }
        return new ToolResult(answer.ToJsonString(Mcp.Plain), Files: [file]);
    }

    /// <summary>A drawing's named parts (its ids, but the shapes a tracing numbered), for animating it.</summary>
    public static IReadOnlyList<string> Parts(string still) =>
        [.. Ids().Matches(still).Select(m => m.Groups[1].Value).Where(id => !Numbered().IsMatch(id)).Distinct().Take(40)];

    [GeneratedRegex(@"\sid=""([^""]+)""")]
    private static partial Regex Ids();

    [GeneratedRegex(@"^(shape|part|specks)-\d+$|-\d+$")]
    private static partial Regex Numbered();
}
