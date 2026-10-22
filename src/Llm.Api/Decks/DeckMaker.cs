using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Api.Endpoints;
using Llm.Api.Insights;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Decks;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Decks;

/// <summary>What the model made of a deck: its title and slides, or why not.</summary>
public sealed record MadeDeck(string Title, List<DeckSlide> Slides, IResult? Problem);

/// <summary>
/// Decks with the model, as the person (their model, credit and safeguards): a deck from their words, a deck changed as
/// they ask, its charts' numbers asked of the databases they may ask, its pictures from the picture model.
/// </summary>
public sealed partial class DeckMaker(OneQuestion one, DataQuestions data, ImageTool images, AppDbContext db, TimeProvider clock)
{
    private const string Shape = """
        {"title": "the deck's title", "slides": [
          {"layout": "title", "title": "…", "subtitle": "…", "notes": "what to say"},
          {"layout": "bullets", "title": "…", "bullets": ["short point", "…"], "picture": {"prompt": "only when a picture helps"}, "notes": "…"},
          {"layout": "two", "title": "…", "left": ["…"], "right": ["…"], "notes": "…"},
          {"layout": "chart", "title": "…", "chart": {"type": "bar or line", "ask": {"source": "a database below", "question": "what to count, in words"}}, "bullets": ["the takeaway"], "notes": "…"},
          {"layout": "chart", "title": "…", "chart": {"type": "bar", "categories": ["A", "B"], "series": [{"name": "…", "values": [1, 2]}]}, "notes": "…"},
          {"layout": "picture", "title": "…", "picture": {"prompt": "a concrete, visual description in English"}, "subtitle": "a caption", "notes": "…"},
          {"layout": "section", "title": "…", "subtitle": "…"},
          {"layout": "quote", "quote": "…", "by": "…"}
        ]}
        """;

    private async Task<string> SourcesAsync(AppUser me, CancellationToken ct)
    {
        var sources = await data.SourcesForAsync(me, ct);
        if (sources.Count == 0)
        {
            return "There is no database to ask: give a chart numbers only when the person gave them.";
        }
        var w = new StringBuilder("The databases a chart may ask (chart.ask.source), and their tables:\n");
        foreach (var s in sources)
        {
            w.Append("- \"").Append(s.Name).Append('"').Append(s.Description is { Length: > 0 } d ? $" ({d})" : "").Append(": ");
            w.AppendJoin(", ", DataQuestions.Open(s).Select(t => t.Name)).AppendLine();
        }
        return w.ToString();
    }

    private string Rules(string sources) => $"""
        Today is {clock.GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.
        Answer with JSON only, no other words, in this shape (the layouts: title, section, bullets, two, chart, picture, quote):
        {Shape}
        The first slide is a title slide. Short points: at most 6 on a slide, each under 12 words. Speaker notes on every slide: what to say, two to four sentences.
        At most 3 pictures in a deck, unless the person asks for more; never a picture of text, a chart or a logo.
        Never make up numbers: a chart's numbers come from the person's words, or from a database it asks.
        {sources}
        Write in the language the person writes in.
        """;

    /// <summary>A new deck about what the person says.</summary>
    public async Task<MadeDeck> MakeAsync(AppUser me, string about, CancellationToken ct)
    {
        var system = "You make slide decks for presentations at a company: clear, short, one idea a slide, usually 6 to 10 slides.\n" + Rules(await SourcesAsync(me, ct));
        var made = await AskForDeckAsync(me, about, system, ct);
        DropMadeUp(made.Slides, about);
        return made;
    }

    public const int MostDecks = 200;

    /// <summary>A new deck about what the person says, its charts' numbers asked, kept as its first version (in the default template, or the one chosen).</summary>
    public async Task<(Deck? Deck, List<DeckSlide> Slides, IResult? Problem)> CreateAsync(AppUser me, string about, Guid? templateId, CancellationToken ct)
    {
        about = about.Trim();
        if (about.Length is 0 or > 20_000)
        {
            return (null, [], AuthEndpoints.Problem(400, "about", "Say what the deck is about, up to 20,000 characters."));
        }
        if (await db.Decks.CountAsync(d => d.OwnerId == me.Id, ct) >= MostDecks)
        {
            return (null, [], AuthEndpoints.Problem(409, "decks", $"You have {MostDecks} decks, the most: remove one first."));
        }
        var template = templateId ?? await db.DeckTemplates.Where(t => t.IsDefault).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
        if (template is { } t && t != Guid.Empty && !await db.DeckTemplates.AnyAsync(x => x.Id == t, ct))
        {
            return (null, [], AuthEndpoints.Problem(400, "template", "That template is not here."));
        }
        var made = await MakeAsync(me, about, ct);
        if (made.Problem is not null)
        {
            return (null, [], made.Problem);
        }
        await CheckFilesAsync(made.Slides, me, ct);
        await FillChartsAsync(me, made.Slides, false, ct);
        var deck = new Deck { OwnerId = me.Id, Title = made.Title, Slides = DeckModel.Write(made.Slides), TemplateId = template == Guid.Empty ? null : template };
        db.Decks.Add(deck);
        db.DeckVersions.Add(new DeckVersion { DeckId = deck.Id, Version = 1, Title = deck.Title, Slides = deck.Slides, Note = about.Length > 500 ? about[..499] + "…" : about });
        await db.SaveChangesAsync(ct);
        return (deck, made.Slides, null);
    }

    /// <summary>Only the person's own pictures stay on their slides (a file id from the model, or sent by hand, may be anyone's).</summary>
    public async Task CheckFilesAsync(List<DeckSlide> slides, AppUser me, CancellationToken ct)
    {
        var ids = slides.Select(s => s.Picture?.FileId).OfType<Guid>().Distinct().ToList();
        var mine = ids.Count == 0 ? [] : await db.ChatAttachments.AsNoTracking().Where(a => ids.Contains(a.Id) && a.UserId == me.Id && a.Kind == "image").Select(a => a.Id).ToListAsync(ct);
        foreach (var picture in slides.Select(s => s.Picture).OfType<DeckPicture>().Where(x => x.FileId is { } id && !mine.Contains(id)))
        {
            picture.FileId = null;
        }
    }

    /// <summary>The deck changed as the person asks; what they did not ask about stays as it was.</summary>
    public async Task<MadeDeck> ChangeAsync(AppUser me, string title, List<DeckSlide> slides, string change, int? looking, CancellationToken ct)
    {
        var deck = new JsonObject { ["title"] = title, ["slides"] = JsonNode.Parse(DeckModel.Write(slides)) };
        var system = $"""
            You change slide decks as the person asks. Here is the deck, as JSON:
            {deck.ToJsonString(DeckModel.Json)}
            {(looking is { } n ? $"They are looking at slide {n + 1}: \"this slide\" is that one." : "")}
            Answer with the whole deck after the change. Keep everything they did not ask to change exactly as it is: the same words, the
            same order, each picture with its prompt and fileId, each chart with its numbers and ask. Put a new slide exactly where they say.
            {Rules(await SourcesAsync(me, ct))}
            """;
        var made = await AskForDeckAsync(me, change, system, ct);
        DropMadeUp(made.Slides, change, slides);
        return made;
    }

    private async Task<MadeDeck> AskForDeckAsync(AppUser me, string words, string system, CancellationToken ct)
    {
        var asked = await one.AskAsync(me, words, system, null, 8000, "deck", ct);
        if (asked.Problem is not null)
        {
            return new MadeDeck("", [], asked.Problem);
        }
        if (Read(asked.Text ?? "") is { } made)
        {
            return made;
        }
        // Not JSON the first time: told so once.
        var again = await one.AskAsync(me, words, system, null, 8000, "deck", ct,
        [
            new JsonObject { ["role"] = "assistant", ["content"] = asked.Text },
            new JsonObject { ["role"] = "user", ["content"] = "That was not the deck as JSON. Answer with the JSON only, in the shape asked." },
        ]);
        return again.Problem is not null ? new MadeDeck("", [], again.Problem)
            : Read(again.Text ?? "") ?? new MadeDeck("", [], AuthEndpoints.Problem(502, "deck", "The model did not answer with a deck. Ask again, perhaps in other words."));
    }

    /// <summary>
    /// Never made-up numbers: a chart that does not ask a database keeps its numbers only when each was given, in the person's
    /// words or on the deck before; else it loses them and says why.
    /// </summary>
    public static void DropMadeUp(List<DeckSlide> slides, string words, IEnumerable<DeckSlide>? before = null)
    {
        var given = Numbers().Matches(words).Select(m => double.TryParse(m.Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN)
            .Where(double.IsFinite).ToHashSet();
        foreach (var v in (before ?? []).Select(s => s.Chart).OfType<DeckChart>().SelectMany(c => c.Series).SelectMany(x => x.Values).OfType<double>())
        {
            given.Add(v);
        }
        foreach (var chart in slides.Select(s => s.Chart).OfType<DeckChart>().Where(c => c.Ask is null && c.Series.Count > 0))
        {
            if (chart.Series.SelectMany(x => x.Values).OfType<double>().All(v => given.Any(g => Math.Abs(g - v) <= 1e-9 * Math.Max(1, Math.Abs(v)))))
            {
                continue;
            }
            chart.Categories = [];
            chart.Series = [];
            chart.Note = "The model gave this chart numbers no one gave it, so they are left out: give them, or let it ask a database.";
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"-?\d[\d,]*(\.\d+)?")]
    private static partial System.Text.RegularExpressions.Regex Numbers();

    /// <summary>The model's deck JSON (in a code fence or not), its slides cleaned; null when it is not one.</summary>
    public static MadeDeck? Read(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }
        try
        {
            // Read as small models write it: a trailing comma or a comment is no reason to lose the deck.
            var o = JsonNode.Parse(text[start..(end + 1)], null, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
            if (o?["slides"] is not JsonArray array || array.Count == 0)
            {
                return null;
            }
            var slides = DeckModel.Clean(array.Deserialize<List<DeckSlide>>(DeckModel.Json) ?? []);
            var title = (o["title"] is JsonValue v && v.TryGetValue<string>(out var t) ? t.Trim() : "") is { Length: > 0 } named ? named : slides[0].Title ?? "A deck";
            return new MadeDeck(title.Length > 200 ? title[..200] : title, slides, null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Asks the databases for the numbers of the charts that ask one and have none yet (or all of them, to refresh).</summary>
    public async Task FillChartsAsync(AppUser me, List<DeckSlide> slides, bool refresh, CancellationToken ct)
    {
        List<Llm.Core.Insights.DataSource>? sources = null;
        foreach (var chart in slides.Select(s => s.Chart).OfType<DeckChart>().Where(c => c.Ask is not null && (refresh || c.Categories.Count == 0)))
        {
            sources ??= await data.SourcesForAsync(me, ct);
            var ask = chart.Ask!;
            if (sources.FirstOrDefault(s => string.Equals(s.Name, ask.Source, StringComparison.OrdinalIgnoreCase)) is not { } source)
            {
                chart.Note = $"There is no database \"{ask.Source}\" you may ask.";
                continue;
            }
            var (answer, problem) = await data.AskAsync(source, me, ask.Question, null, "deck", ct);
            if (problem is not null || answer?.Result is not { } result)
            {
                chart.Note = answer?.Error ?? answer?.Explanation ?? "The database could not answer that.";
                continue;
            }
            Fill(chart, result);
            chart.Note = $"From {source.Name}: {answer.Sql}";
        }
    }

    /// <summary>A result as a chart's numbers: the first column that is not a number names the bars, the numbers are its series.</summary>
    public static void Fill(DeckChart chart, DataResult result)
    {
        var x = result.Columns.FindIndex(c => c.Type != "number");
        var numbers = result.Columns.Select((c, i) => (c, i)).Where(p => p.c.Type == "number" && p.i != x).Take(6).ToList();
        var rows = result.Rows.Take(chart.Type == "line" ? 60 : 20).ToList();
        chart.Categories = [.. rows.Select((r, n) => x >= 0 ? Text(r[x]) : (n + 1).ToString(CultureInfo.InvariantCulture))];
        chart.Series = [.. numbers.Select(p => new DeckSeries { Name = p.c.Name, Values = [.. rows.Select(r => Number(r[p.i]))] })];
    }

    private static string Text(JsonNode? v) => v is JsonValue jv && jv.TryGetValue<string>(out var s)
        ? (s.Length >= 10 && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? s[..10] : s)
        : v?.ToJsonString() ?? "";

    /// <summary>A cell's number, whatever it holds (an int, a long, a decimal from numeric, a double).</summary>
    private static double? Number(JsonNode? v) =>
        v is JsonValue jv && jv.GetValueKind() == JsonValueKind.Number && double.TryParse(jv.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>A slide's picture from its prompt, by the picture model, as the person (their credit and limits); kept as their file.</summary>
    public async Task<(Guid? FileId, string? Problem)> PictureAsync(AppUser me, string prompt, CancellationToken ct)
    {
        IToolRun run;
        try
        {
            run = await images.StartAsync(new ToolContext(me, me.Email!, new Conversation { UserId = me.Id, Title = "Deck" }), ct);
        }
        catch (McpException ex)
        {
            return (null, ex.Message);
        }
        var made = await run.CallAsync("generate_image", new JsonObject { ["prompt"] = prompt, ["size"] = "1024x768" }, ct);
        return made.IsError || made.Files is not [var file, ..] ? (null, made.Text) : (file.Id, null);
    }
}
