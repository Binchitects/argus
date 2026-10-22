using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Llm.Api.Chat;
using Llm.Core.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.Vector;

/// <summary>A drawing the model made: the SVG (made safe), the model, and what was fixed on the way.</summary>
public sealed record Drawn(SafeSvg Svg, string? Model, IReadOnlyList<string> Notes);

/// <summary>A thing in a picture that moves on its own: its id in the drawing, and how it looks (what is looked for in the picture).</summary>
public sealed record Thing(string Id, string Looks);

/// <summary>
/// SVG by the model: drawn from words in a style, changed as asked, and given motions as asked. Each answer is made safe
/// and checked — not XML, references to nothing, parts with no names, parts off the canvas — and what is wrong goes back
/// to the model to fix; a model that sees is shown its own drawing, rendered, and fixes what looks wrong.
/// </summary>
public sealed partial class SvgDraw(OneQuestion one, ChatModels models, SvgRender render, IOptionsMonitor<ChatOptions> chat)
{
    /// <summary>The styles a drawing is made in, each with what it asks of the drawing.</summary>
    public static readonly IReadOnlyList<(string Name, string About)> Styles =
    [
        ("flat", "flat colours and clean geometric shapes, no outlines, a few soft shadows as darker shapes"),
        ("line", "line art: strokes only (stroke-linecap and stroke-linejoin round), one or two colours, no fills but the background"),
        ("isometric", "an isometric view: each block's top, left and right faces in three tones of its colour"),
        ("gradient", "soft linear and radial gradients, gentle shadows and highlights, glossy and modern"),
        ("icon", "a simple, bold icon: few shapes, thick strokes or solid fills, readable at 32 pixels"),
        ("cute", "rounded and friendly: soft corners, pastel colours, big eyes when it has a face"),
        ("geometric", "an abstract composition of circles, triangles and arcs in a bold palette"),
    ];

    public static string StyleOf(string? name) => Styles.FirstOrDefault(s => s.Name == name).About ?? Styles[0].About;

    /// <summary>The longest still drawing the model is asked to change by words (its markup, in characters).</summary>
    public const int MostToChange = 30_000;

    private string? Model => chat.CurrentValue.SvgModel is { Length: > 0 } m ? m : null;

    private static string Rules(string style, int width, int height) => $$"""
        You are an illustrator who draws in SVG. Answer with one complete SVG and nothing else: no words, no code fence.
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {{width}} {{height}}"> … </svg>
        Draw well:
        - Style: {{style}}.
        - Everything inside the viewBox, the subject centred and large, with a margin of about a twentieth of the width.
        - Name every meaningful part with an id in lowercase words joined by hyphens (sun, cloud-1, wheel-left, arm-right), and put a part's pieces in a <g id="…">: parts are moved and changed by their ids.
        - Back to front: the background first (a full rect, unless asked for none), then far things, then near ones.
        - Curves with C, Q and A for living shapes; rect with rx for rounded ones; polygons for crisp facets. Close shapes with Z.
        - A harmonious palette of four to six colours, one accent; strong contrast between the subject and the background. Gradients go in <defs> and are used by fill="url(#id)".
        - Depth without noise: a soft shadow under things (an ellipse of a darker tone, or black at opacity 0.15), a highlight here and there.
        - No text unless asked; no images; no script; no filters unless they are needed; at most 400 elements.
        """;

    private static string Changing(int width, int height) => $$"""
        You change SVG drawings as asked. Answer with the whole changed SVG and nothing else: no words, no code fence.
        Keep everything that is not asked to change as it is: the viewBox (0 0 {{width}} {{height}}), the parts, their ids and their order.
        Give new parts ids in lowercase words joined by hyphens. No text unless asked; no images; no script.
        """;

    private async Task<bool> SeesAsync(string? model, CancellationToken ct) =>
        model is not null && (await models.ListAsync(ct)).FirstOrDefault(m => m.Name == model) is { Vision: true };

    /// <summary>A drawing from words, in a style, at this size of canvas.</summary>
    public Task<(Drawn? Drawing, string? Problem)> DrawAsync(AppUser me, string prompt, string style, int width, int height, Func<string, Task> phase, CancellationToken ct) =>
        AskAsync(me, $"Draw: {prompt}", Rules(StyleOf(style), width, height), prompt, phase, "svg-draw", ct);

    /// <summary>A drawing changed as asked (its still drawing: motions are kept apart).</summary>
    public Task<(Drawn? Drawing, string? Problem)> ChangeAsync(AppUser me, string still, string request, Func<string, Task> phase, CancellationToken ct)
    {
        if (still.Length > MostToChange)
        {
            return Task.FromResult<(Drawn?, string?)>((null, "This drawing is too detailed to change by words: change its code by hand, or give it motions."));
        }
        var box = SvgMotion.ViewBox(XElement.Parse(still));
        return AskAsync(me, $"The SVG:\n{still}\n\nThe change: {request}", Changing((int)box.W, (int)box.H), request, phase, "svg-change", ct);
    }

    private async Task<(Drawn? Drawing, string? Problem)> AskAsync(AppUser me, string question, string system, string want, Func<string, Task> phase, string tag, CancellationToken ct)
    {
        var notes = new List<string>();
        var after = new List<JsonObject>();
        string? model = null;
        SafeSvg? svg = null;
        // Up to three answers: the first, and one more for each kind of problem found (not SVG, then what the checks find).
        for (var round = 1; round <= 3; round++)
        {
            await phase(round == 1 ? "Drawing" : "Fixing what was wrong");
            var asked = await one.AskAsync(me, question, system, Model, 8000, tag, ct, after);
            if (asked.Problem is not null)
            {
                return svg is not null ? (new Drawn(svg, model, notes), null) : (null, "The model could not be asked now.");
            }
            model = asked.Model;
            var answer = asked.Text ?? "";
            try
            {
                var made = SvgSafe.Clean(answer);
                svg = made;
            }
            catch (SvgException ex)
            {
                notes.Add($"answer {round}: {ex.Message}");
                after = [Turn("assistant", answer), Turn("user", $"That SVG could not be used: {ex.Message} Answer with the whole corrected SVG only.")];
                continue;
            }
            var parts = await render.PartsAsync(svg.Markup, ct);
            var issues = Issues(XElement.Parse(svg.Markup), parts, svg.Width, svg.Height);
            if (issues.Count == 0 || round == 3)
            {
                break;
            }
            notes.AddRange(issues.Select(i => $"answer {round}: {i}"));
            after = [Turn("assistant", svg.Markup), Turn("user", "Fix these and answer with the whole corrected SVG only:\n- " + string.Join("\n- ", issues))];
        }
        if (svg is null)
        {
            return (null, "The model did not draw an SVG this could use. Try again, or say it more simply.");
        }
        // A model that sees: shown its drawing, once, to fix what looks wrong.
        if (await SeesAsync(model, ct))
        {
            await phase("Looking at the drawing");
            var png = await render.PngAsync(svg.Markup, 768, 0, ct);
            var look = await one.AskAsync(me, question, system, model, 8000, tag, ct, [
                Turn("assistant", svg.Markup),
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = $"This is how your SVG looks. Is it what was asked (\"{want}\")? If anything is wrong (a part missing, in the wrong place or overlapping wrongly, off-centre, too small, colours that clash or too little contrast), answer with the whole corrected SVG. If it is right, answer only: OK",
                        },
                        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(png) } }),
                },
            ]);
            if (look.Text is { } seen && seen.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    svg = SvgSafe.Clean(seen);
                    notes.Add("looked at and fixed");
                }
                catch (SvgException ex)
                {
                    notes.Add($"its fix after looking could not be used: {ex.Message}");
                }
            }
        }
        return (new Drawn(svg, model, notes), null);
    }

    private static JsonObject Turn(string role, string content) => new() { ["role"] = role, ["content"] = content };

    /// <summary>What is wrong with a drawing that can be said: references to nothing, parts with no names, parts off the canvas, a drawing lost in it.</summary>
    public static IReadOnlyList<string> Issues(XElement root, IReadOnlyList<PartBox> parts, double width, double height)
    {
        var issues = new List<string>();
        var ids = root.DescendantsAndSelf().Select(e => (string?)e.Attribute("id")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var named = root.DescendantsAndSelf().SelectMany(e => e.Attributes())
            .SelectMany(a => a.Name.LocalName == "href" && a.Value.StartsWith('#') ? [a.Value[1..]] : Reference().Matches(a.Value).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal);
        foreach (var missing in named.Where(n => !ids.Contains(n)).Take(5))
        {
            issues.Add($"\"#{missing}\" is used but no element has the id {missing}");
        }
        var drawn = root.Descendants().Count(e => e.Name.LocalName is "path" or "rect" or "circle" or "ellipse" or "polygon" or "polyline" or "line" or "text");
        if (drawn >= 3 && ids.Count == 0)
        {
            issues.Add("no part has an id: give the meaningful parts ids that name them (and group their pieces in <g id=\"…\">)");
        }
        foreach (var off in parts.Where(p => p.X > width || p.Y > height || p.X + p.Width < 0 || p.Y + p.Height < 0).Take(5))
        {
            issues.Add($"#{off.Id} is outside the canvas (at {off.X.ToString("0", CultureInfo.InvariantCulture)},{off.Y.ToString("0", CultureInfo.InvariantCulture)}; the canvas is {width}×{height})");
        }
        // A shape with no area (its points on a line) draws nothing a person sees.
        foreach (var flat in parts.Where(p => p.Tag is "polygon" or "path" or "rect" or "ellipse" && (p.Width < Math.Max(width, height) / 400 || p.Height < Math.Max(width, height) / 400)).Take(5))
        {
            issues.Add($"#{flat.Id} has no area (it is {flat.Width.ToString("0.#", CultureInfo.InvariantCulture)}×{flat.Height.ToString("0.#", CultureInfo.InvariantCulture)}): give it width and height, its points around an area");
        }
        // The subject without the background (a part covering most of the canvas): too small, it is lost in it.
        var subject = parts.Where(p => p.Width * p.Height < width * height * 0.85 && p.Tag != "g").ToList();
        if (subject.Count > 0)
        {
            var (x0, y0) = (Math.Max(0, subject.Min(p => p.X)), Math.Max(0, subject.Min(p => p.Y)));
            var (x1, y1) = (Math.Min(width, subject.Max(p => p.X + p.Width)), Math.Min(height, subject.Max(p => p.Y + p.Height)));
            if ((x1 - x0) * (y1 - y0) < width * height * 0.2)
            {
                issues.Add($"the drawing fills too little of the canvas (its parts span {(x1 - x0).ToString("0", CultureInfo.InvariantCulture)}×{(y1 - y0).ToString("0", CultureInfo.InvariantCulture)} of {width}×{height}): draw it larger, centred, and spread the parts over the canvas");
            }
        }
        return issues;
    }

    [GeneratedRegex(@"url\(\s*#([^)\s]+)\s*\)")]
    private static partial Regex Reference();

    private static string Motions() => string.Join('\n', SvgMotion.Kinds.Select(k => $"- {k.Kind}: {k.About}"));

    private static readonly JsonSerializerOptions Lenient = new(JsonSerializerDefaults.Web) { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };

    private const string Naming = """
        You name the separate things in a picture, from the words it was drawn from, so that each can be animated on its own.
        Answer with JSON only: {"things": [{"id": "fox", "looks": "an orange fox"}, …]}.
        - At most six things, the main subject first. Things drawn that could move apart (a fox, the moon, a boat, waves, clouds, stars, a tree, a person), not their pieces (no eyes, no tails, no sails), not the plain background or the sky.
        - id: one or two lowercase English words joined by a hyphen. Many of a kind together are one thing with a plural id (stars, clouds, birds).
        - looks: the thing alone in a few English words, with its colour and shape when the words give them (a blue crescent moon, small yellow stars, a small sailboat); never what it is on, under or with.
        """;

    /// <summary>The things a picture drawn from these words has that could each move on their own (at most six), named by the model; none when it cannot say.</summary>
    public async Task<IReadOnlyList<Thing>> ThingsAsync(AppUser me, string words, CancellationToken ct)
    {
        var asked = await one.AskAsync(me, $"The words: {words}", Naming, Model, 600, "svg-things", ct);
        var text = asked.Text ?? "";
        var (start, end) = (text.IndexOf('{'), text.LastIndexOf('}'));
        List<Thing>? found = null;
        try
        {
            found = start >= 0 && end > start
                ? JsonNode.Parse(text[start..(end + 1)], null, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })?["things"]
                    ?.Deserialize<List<Thing>>(Lenient)
                : null;
        }
        catch (JsonException)
        {
        }
        var seen = new HashSet<string>(["background", "scene"]);
        return [.. (found ?? []).Where(t => t is { Id.Length: > 0, Looks.Length: > 0 }).Select(t => new Thing(ThingId(t.Id), t.Looks.Trim()[..Math.Min(120, t.Looks.Trim().Length)]))
            .Where(t => t.Id.Length > 0 && seen.Add(t.Id)).Take(6)];
    }

    /// <summary>An id as the drawing has them: lowercase words joined by hyphens, beginning with a letter.</summary>
    public static string ThingId(string name)
    {
        var id = NotIdChars().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        id = id.Length > 32 ? id[..32].TrimEnd('-') : id;
        return id.Length > 0 && char.IsAsciiLetter(id[0]) ? id : id.Length > 0 ? $"p-{id}" : "";
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotIdChars();

    /// <summary>Motions for a drawing as asked: the model reads its parts (where each is) and plans them; what cannot be done goes back once.</summary>
    public async Task<(IReadOnlyList<Motion>? Motions, Animated? Result, string? Problem)> AnimateAsync(AppUser me, string still, IReadOnlyList<Motion> kept, string request,
        Func<string, Task> phase, CancellationToken ct)
    {
        await phase("Looking at its parts");
        var parts = await render.PartsAsync(still, ct);
        var root = XElement.Parse(still);
        var box = SvgMotion.ViewBox(root);
        var list = parts.Count > 0
            ? SvgRender.Describe(parts)
            : string.Join('\n', root.Descendants().Where(e => e.Attribute("id") is not null).Take(300).Select(e => $"#{(string)e.Attribute("id")!} {e.Name.LocalName}"));
        var system = $$"""
            You animate SVG drawings with motions. Answer with JSON only: {"motions": [ … ]}, each motion:
            {"target": "a part's id, or scene for the whole drawing", "kind": "…", "duration": seconds, "delay": seconds, "repeat": "once" | "loop" | a number,
             "easing": "linear" | "ease-in-out" | "ease-out" | "spring" | "smooth" | "snappy", "alternate": true | false, "stagger": seconds (its own parts one after another),
             "from": "left" | "right" | "top" | "bottom", "amount": number, "origin": "center" | "top" | "bottom" | "left" | "right" | "x y", "color": "#hex",
             "keys": [{"at": 0 to 1, "x": n, "y": n, "scale": n, "rotate": degrees, "opacity": 0 to 1, "fill": "#hex"}] for kind keyframes}.
            The kinds:
            {{Motions()}}
            Distances are in the drawing's units (its viewBox is {{box.W}}×{{box.H}}). Only the parts listed exist.
            Do what is asked, and only that: the parts named move as said, everything else stays still. No entrances unless asked (fade-in, slide-in, pop...).
            Choose kinds that look natural: stars twinkle (twinkle), lights, the sun and the moon glow (glow), creatures breathe (breathe), clouds and waves drift (drift), boats rock (sway with origin bottom and amount 5), leaves and flowers sway, wheels spin, eyes blink.
            A part whose own parts are numbered (stars-1, stars-2...) moves them one after another with "stagger" on the part itself (target "stars").
            When asked only to make it alive: entrances one after another first, then gentle loops; wheels spin, things float or sway, eyes blink, lines draw on.
            """;
        var question = $"The drawing's parts (id, kind, where, size, colour):\n{list}\n\nIts motions so far: {JsonSerializer.Serialize(kept, Lenient)}\n\nAsked: {request}\n\nAnswer with all its motions (those so far that stay, and the new ones).";
        var after = new List<JsonObject>();
        for (var round = 1; round <= 2; round++)
        {
            await phase(round == 1 ? "Planning the motions" : "Fixing the motions");
            var asked = await one.AskAsync(me, question, system, Model, 4000, "svg-animate", ct, after);
            if (asked.Problem is not null)
            {
                return (null, null, "The model could not be asked now.");
            }
            var text = asked.Text ?? "";
            var (start, end) = (text.IndexOf('{'), text.LastIndexOf('}'));
            List<Motion>? motions = null;
            try
            {
                motions = start >= 0 && end > start ? JsonNode.Parse(text[start..(end + 1)], null, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })?["motions"]
                    ?.Deserialize<List<Motion>>(Lenient) : null;
            }
            catch (JsonException)
            {
            }
            if (motions is not { Count: > 0 })
            {
                after = [Turn("assistant", text), Turn("user", "Answer with JSON only: {\"motions\": [ … ]}.")];
                continue;
            }
            motions = [.. motions.Take(60)];
            var result = SvgMotion.Apply(still, motions);
            if (result.Problems.Count == 0 || round == 2)
            {
                // What it could not do is left out: the rest plays.
                var good = motions.Where((_, i) => !result.Problems.Any(p => p.StartsWith($"motion {i + 1}:", StringComparison.Ordinal))).ToList();
                return (good, good.Count == motions.Count ? result : SvgMotion.Apply(still, good), null);
            }
            after = [Turn("assistant", text), Turn("user", "Fix these and answer with all the motions again, JSON only:\n- " + string.Join("\n- ", result.Problems))];
        }
        return (null, null, "The model did not plan motions this could use. Try again, or say it more simply.");
    }
}
