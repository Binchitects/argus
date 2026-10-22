using System.Text.Json;
using System.Text.Json.Serialization;

namespace Llm.Api.Decks;

/// <summary>A chart's numbers: from the person's words, or asked of a database (<see cref="Ask"/>) and filled in here.</summary>
public sealed class DeckChart
{
    /// <summary>bar or line.</summary>
    public string Type { get; set; } = "bar";
    public List<string> Categories { get; set; } = [];
    public List<DeckSeries> Series { get; set; } = [];
    /// <summary>The question asked of a database for its numbers, run again with Refresh.</summary>
    public DeckAsk? Ask { get; set; }
    /// <summary>Where its numbers came from, or why there are none.</summary>
    public string? Note { get; set; }
}

public sealed class DeckSeries
{
    public string Name { get; set; } = "";
    public List<double?> Values { get; set; } = [];
}

public sealed class DeckAsk
{
    /// <summary>The database's name, as Data lists it.</summary>
    public string Source { get; set; } = "";
    public string Question { get; set; } = "";
}

/// <summary>A picture: made by the picture model from its prompt, or a file of the person's.</summary>
public sealed class DeckPicture
{
    public string Prompt { get; set; } = "";
    public Guid? FileId { get; set; }
}

/// <summary>One slide: its layout and what it holds.</summary>
public sealed class DeckSlide
{
    /// <summary>title, section, bullets, two, chart, picture or quote.</summary>
    public string Layout { get; set; } = "bullets";
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public List<string>? Bullets { get; set; }
    public List<string>? Left { get; set; }
    public List<string>? Right { get; set; }
    public string? Quote { get; set; }
    public string? By { get; set; }
    public DeckChart? Chart { get; set; }
    public DeckPicture? Picture { get; set; }
    public string? Notes { get; set; }
}

/// <summary>The slides as kept and sent, and the model's JSON made safe: known layouts, words cut to size, at most 30 slides.</summary>
public static class DeckModel
{
    public const int MostSlides = 30;
    public static readonly string[] Layouts = ["title", "section", "bullets", "two", "chart", "picture", "quote"];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static string Write(List<DeckSlide> slides) => JsonSerializer.Serialize(slides, Json);

    public static List<DeckSlide> Read(string json) => JsonSerializer.Deserialize<List<DeckSlide>>(json, Json) ?? [];

    private static string? Cut(string? s, int most)
    {
        var t = s?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > most ? t[..most] : t;
    }

    private static List<string>? Lines(List<string>? lines, int most = 8) =>
        lines?.Select(l => Cut(l, 300)).OfType<string>().Take(most).ToList() is { Count: > 0 } kept ? kept : null;

    /// <summary>A slide as it may be kept: a known layout, what that layout shows, nothing too long.</summary>
    public static DeckSlide Clean(DeckSlide s)
    {
        var layout = Layouts.Contains(s.Layout?.Trim().ToLowerInvariant()) ? s.Layout!.Trim().ToLowerInvariant() : "bullets";
        var chart = s.Chart is { } c ? CleanChart(c) : null;
        var picture = s.Picture is { } p && (Cut(p.Prompt, 1000) is not null || p.FileId is not null) ? new DeckPicture { Prompt = Cut(p.Prompt, 1000) ?? "", FileId = p.FileId } : null;
        // A chart slide with nothing to draw, or a picture slide with nothing to show, says its words instead.
        if (layout == "chart" && chart is null)
        {
            layout = "bullets";
        }
        if (layout == "picture" && picture is null)
        {
            layout = "bullets";
        }
        return new DeckSlide
        {
            Layout = layout,
            Title = Cut(s.Title, 200),
            Subtitle = Cut(s.Subtitle, 300),
            Bullets = Lines(s.Bullets),
            Left = Lines(s.Left),
            Right = Lines(s.Right),
            Quote = Cut(s.Quote, 500),
            By = Cut(s.By, 200),
            Chart = chart,
            Picture = picture,
            Notes = Cut(s.Notes, 4000),
        };
    }

    private static DeckChart? CleanChart(DeckChart c)
    {
        var ask = c.Ask is { } a && Cut(a.Question, 1000) is { } q ? new DeckAsk { Source = Cut(a.Source, 100) ?? "", Question = q } : null;
        var categories = c.Categories.Select(x => Cut(x, 100) ?? "").Take(60).ToList();
        var series = c.Series.Where(x => x.Values.Count > 0).Take(6)
            .Select(x => new DeckSeries { Name = Cut(x.Name, 100) ?? "Values", Values = [.. x.Values.Take(categories.Count).Select(v => v is { } d && double.IsFinite(d) ? d : (double?)null)] })
            .ToList();
        if (ask is null && (categories.Count == 0 || series.Count == 0))
        {
            return null;
        }
        return new DeckChart { Type = c.Type is "line" ? "line" : "bar", Categories = categories, Series = series, Ask = ask, Note = Cut(c.Note, 1000) };
    }

    public static List<DeckSlide> Clean(IEnumerable<DeckSlide> slides) => [.. slides.Take(MostSlides).Select(Clean)];
}
