using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Llm.Api.Chat;
using Llm.Core.Identity;

namespace Llm.Api.Media;

/// <summary>
/// A shot of a story: what the video model shows (in English), what the narrator says over it, how long, and what was made
/// for it (files: the picture it starts from, its clip, the narration's sound), each made again when what it comes from changes.
/// </summary>
public sealed record StoryShot
{
    public string Visual { get; init; } = "";
    public string Narration { get; init; } = "";
    public int Seconds { get; init; } = 4;
    public Guid? Picture { get; init; }
    public Guid? Clip { get; init; }
    public Guid? Speech { get; init; }

    public const int Least = 2;
    public const int Most = 5;
    public const int MostVisual = 1000;
    public const int MostNarration = 400;

    /// <summary>The shot as the person changed it: what was made for it is kept only while what it came from holds.</summary>
    public StoryShot Changed(string visual, string narration, int seconds) => this with
    {
        Visual = visual, Narration = narration, Seconds = seconds,
        Picture = visual == Visual ? Picture : null,
        Clip = visual == Visual && seconds == Seconds ? Clip : null,
        Speech = narration == Narration ? Speech : null,
    };
}

/// <summary>A storyboard as the model wrote it.</summary>
public sealed record WrittenStory(string Title, string? Music, IReadOnlyList<StoryShot> Shots);

/// <summary>Stories' shots as kept, and storyboards written by the model from the person's words.</summary>
public static class StoryModel
{
    public const int LeastShots = 2;
    public const int MostShots = 8;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static List<StoryShot> Read(string json) => JsonSerializer.Deserialize<List<StoryShot>>(json, Json) ?? [];

    public static string Write(IEnumerable<StoryShot> shots) => JsonSerializer.Serialize(shots, Json);

    private static string System(int shots) => $$"""
        You write storyboards for short narrated videos. Answer with JSON only, in this shape:
        {"title": "a short title", "music": "the music under it: style, instruments, mood, tempo, as a few words separated by commas, in English",
         "shots": [{"visual": "...", "narration": "...", "seconds": 4}]}
        Write {{shots}} shots. In each:
        - visual: in English, a prompt for a video model that makes a few seconds from it: who or what is seen, one simple motion,
          where, the camera (still, a slow pan, a slow push in), the light and the style. Keep the same people, places and style from
          shot to shot, described the same way each time.
        - narration: what a narrator says over the shot, in the language of the request, at most 22 words, going on from the shot before.
        - seconds: 3 to 5.
        """;

    /// <summary>A storyboard of <paramref name="shots"/> shots from the person's words, by their default model (their credit and safeguards).</summary>
    public static async Task<(WrittenStory? Story, IResult? Problem)> WriteAsync(OneQuestion one, AppUser me, string brief, int shots, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var asked = await one.AskAsync(me, brief, System(shots), null, 3000, "story", ct);
            if (asked.Problem is not null)
            {
                return (null, asked.Problem);
            }
            if (Parse(asked.Text ?? "") is { } story)
            {
                return (story, null);
            }
        }
        return (null, Endpoints.AuthEndpoints.Problem(502, "brief", "The model did not write a storyboard this reads. Try again, or say it differently."));
    }

    /// <summary>A storyboard from the model's answer, read as small models write it (text around it, a trailing comma); null when it has no shots.</summary>
    public static WrittenStory? Parse(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }
        JsonObject? o;
        try
        {
            o = JsonNode.Parse(text[start..(end + 1)], null, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        var shots = (o?["shots"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(s => new StoryShot
            {
                Visual = Cut(Str(s["visual"]), StoryShot.MostVisual),
                Narration = Cut(Str(s["narration"]), StoryShot.MostNarration),
                Seconds = Math.Clamp(s["seconds"] is JsonValue v && double.TryParse(v.ToJsonString().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? (int)Math.Round(d) : 4, StoryShot.Least, StoryShot.Most),
            })
            .Where(s => s.Visual.Length > 0).Take(MostShots).ToList();
        if (shots.Count == 0)
        {
            return null;
        }
        var title = Cut(Str(o!["title"]), 200);
        var music = Cut(Str(o["music"]), 1000);
        return new WrittenStory(title.Length > 0 ? title : "A story", music.Length > 0 ? music : null, shots);
    }

    private static string Cut(string s, int most) => s.Length <= most ? s : s[..most];

    /// <summary>Whether a text is written right to left (Persian, Arabic, Hebrew): its captions take a font for it.</summary>
    public static bool RightToLeft(string text) => text.Any(c => c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= 'ﻼ');
}
