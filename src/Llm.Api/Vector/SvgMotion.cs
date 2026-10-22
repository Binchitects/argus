using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Llm.Api.Vector;

/// <summary>A moment of a motion of one's own: where the part is at that point of it (0 to 1), and how it looks.</summary>
public sealed record MotionKey
{
    public double At { get; init; }
    /// <summary>A move, in the drawing's own units (its viewBox's).</summary>
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Scale { get; init; }
    /// <summary>A turn, in degrees.</summary>
    public double? Rotate { get; init; }
    public double? Opacity { get; init; }
    public string? Fill { get; init; }
}

/// <summary>
/// A motion of a part of a drawing (its id), or of all of it ("scene"): one of the kinds every motion works as (fades,
/// slides, pops, spins, floats, bounces, sways, pulses, wiggles, draws its lines on, changes colour...) or keyframes of
/// one's own, with its timing. <see cref="Stagger"/>: the part's own parts, one after another.
/// </summary>
public sealed record Motion
{
    public string Target { get; init; } = "scene";
    public string Kind { get; init; } = "fade-in";
    public double? Duration { get; init; }
    public double Delay { get; init; }
    public double? Stagger { get; init; }
    /// <summary>"once", "loop", or how many times.</summary>
    public string? Repeat { get; init; }
    /// <summary>There and back (each other time backwards).</summary>
    public bool? Alternate { get; init; }
    public string? Easing { get; init; }
    /// <summary>Where a slide comes from or goes to: left, right, top, bottom; where a travel starts: a part's id ("laptop"), else where it is.</summary>
    public string? From { get; init; }
    /// <summary>Where a travel goes: a part's id ("server"), or "x y" in the drawing's units.</summary>
    public string? To { get; init; }
    /// <summary>How far (drawing units), how much (degrees for turns, a fraction for scale), by kind.</summary>
    public double? Amount { get; init; }
    /// <summary>What it turns and scales about: center, top, bottom, left, right, top left..., or "x y" in the drawing's units.</summary>
    public string? Origin { get; init; }
    public string? Color { get; init; }
    public IReadOnlyList<MotionKey>? Keys { get; init; }
    /// <summary>
    /// On the story's beat: when in the cycle it happens (seconds). Motions with a start share one cycle and happen one
    /// after another, again each cycle; those without go on by themselves (things that just live: a float, a twinkle).
    /// </summary>
    public double? Start { get; init; }
    /// <summary>The story's cycle (seconds), as its plan says; else its last motion's end and a short rest.</summary>
    public double? Cycle { get; init; }
}

/// <summary>How long an animated drawing plays: once through, and whether it goes on (a loop) — what an export records.</summary>
public sealed record Timeline(double Seconds, bool Loops);

/// <summary>An animated drawing: its markup, how long it plays, and what of the motions asked for could not be done (and why).</summary>
public sealed record Animated(string Markup, Timeline Timeline, IReadOnlyList<string> Problems);

/// <summary>
/// Motions compiled into an SVG as CSS: each motion of a part wraps it in a group of its own that moves (so two motions
/// of one part add up, and the part's own transform stays as it was), turning about the part's own box; drawing lines on
/// and colour changes go on the shapes themselves. Compiled again from the drawing as it was each time (what an earlier
/// compile added is taken out first), so motions can be changed freely.
/// </summary>
public static partial class SvgMotion
{
    /// <summary>The kinds, each with what it is for (the model and the page read these).</summary>
    public static readonly IReadOnlyList<(string Kind, string About)> Kinds =
    [
        ("fade-in", "appears, from see-through"), ("fade-out", "disappears"), ("slide-in", "comes in from a side (from: left, right, top, bottom; amount: how far)"),
        ("slide-out", "leaves to a side"), ("rise", "comes up from below as it appears"), ("pop", "appears, growing past its size and back"),
        ("grow", "grows from nothing"), ("shrink", "shrinks to nothing"), ("zoom-in", "comes closer as it appears"),
        ("spin", "turns round and round (amount: turns a cycle)"), ("orbit", "goes round a point (origin: \"x y\")"),
        ("float", "rises and sinks gently (amount: how high)"), ("drift", "drifts slowly from side to side, as a cloud or waves (amount: how far)"),
        ("travel", "goes from one place to another, as a file sent from a laptop to a server or a car along a road (to: the part it goes to, or \"x y\"; from: the part it starts at, else where it is), appearing at the start and fading at the end each time"),
        ("flow", "dashes run along a line or an arrow, as data flowing through it (a link between parts)"),
        ("arc", "flies from one part to another along a curve, as a thrown ball or a paper plane (to, from as travel; amount: how high the curve rises)"),
        ("jump", "jumps: crouches, springs up stretched, lands squashed and settles (amount: how high)"),
        ("land", "drops in from above and lands with a squash and a little bounce"),
        ("flip", "flips round like a card, showing its back and front again"),
        ("camera", "the view goes close to a part and back (target scene; to: the part; amount: how close, 1.6)"),
        ("trail", "ghost copies follow the part's own move a moment behind, fading (after its travel, arc, float...; amount: how many, 4)"), ("bounce", "bounces up and down"), ("sway", "rocks from side to side (amount: degrees)"),
        ("swing", "swings from its top like a sign"), ("wave", "waves, as a hand (origin: where it turns, e.g. bottom)"),
        ("pulse", "grows a little and back"), ("heartbeat", "beats twice and rests"), ("wiggle", "wiggles"), ("shake", "shakes side to side"),
        ("breathe", "breathes: swells a little upward and back, slowly (amount: how much, 0.03)"), ("twinkle", "twinkles as a star: dims and shrinks a little and back"),
        ("glow", "glows: a soft light around it brightens and fades (color, amount: how wide)"),
        ("blink", "closes and opens quickly now and then, as an eye"), ("draw", "its lines are drawn on, then it fills in"),
        ("color", "its colour changes to another and back (color)"), ("keyframes", "moves as its keys say (at 0 to 1: x, y, scale, rotate, opacity, fill)"),
    ];

    private static readonly HashSet<string> KindNames = [.. Kinds.Select(k => k.Kind)];

    /// <summary>
    /// The motions without those a part's pieces repeat of it (the clouds drift, and each cloud drifts too: twice as far):
    /// a motion of a part inside another part that moves the same way goes.
    /// </summary>
    public static List<Motion> Unrepeated(XElement root, IReadOnlyList<Motion> motions)
    {
        var ids = root.Descendants().Where(e => e.Attribute("id") is not null).GroupBy(e => (string)e.Attribute("id")!).ToDictionary(g => g.Key, g => g.First());
        bool Inside(string inner, string outer) =>
            ids.TryGetValue(inner, out var i) && (outer == "scene" ? inner != "scene" : ids.TryGetValue(outer, out var o) && i.Ancestors().Contains(o));
        return [.. motions.Where(m => !motions.Any(other => other != m && other.Kind == m.Kind && Inside(m.Target, other.Target)))];
    }

    /// <summary>What can move: what is drawn, and groups of it (not gradients' stops, a path inside a clip...).</summary>
    private static readonly HashSet<string> Movable = new(StringComparer.Ordinal) { "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text", "use", "image", "svg" };

    private static readonly HashSet<string> Shapes = new(StringComparer.Ordinal) { "path", "rect", "circle", "ellipse", "line", "polyline", "polygon" };

    /// <summary>Elements a compile added (its groups, its style), and the class its shapes were given.</summary>
    public const string Mark = "data-motion";

    private static readonly XNamespace S = SvgSafe.Svg;

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The drawing as it was before any motion was compiled into it.</summary>
    public static XElement Still(XElement root)
    {
        var copy = new XElement(root);
        copy.Descendants(S + "style").Where(s => s.Attribute(Mark) is not null).Remove();
        copy.Descendants(S + "g").Where(g => (string?)g.Attribute(Mark) == "echo").Remove();
        foreach (var wrap in copy.Descendants(S + "g").Where(g => (string?)g.Attribute(Mark) == "wrap").Reverse().ToList())
        {
            wrap.ReplaceWith(wrap.Nodes());
        }
        foreach (var e in copy.Descendants().Where(e => e.Attribute(Mark) is not null).ToList())
        {
            e.Attribute(Mark)!.Remove();
            if ((string?)e.Attribute("data-motion-path") == "1")
            {
                e.Attribute("pathLength")?.Remove();
                e.Attribute("data-motion-path")!.Remove();
            }
            // Its own class and style as they were.
            var classes = ((string?)e.Attribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(c => !Ours().IsMatch(c)).ToList();
            e.SetAttributeValue("class", classes.Count > 0 ? string.Join(' ', classes) : null);
            if (e.Attribute("data-motion-style") is { } style)
            {
                e.SetAttributeValue("style", style.Value.Length > 0 ? style.Value : null);
                style.Remove();
            }
        }
        // The scene group a compile made, when nothing else used it.
        if (copy.Elements(S + "g").FirstOrDefault(g => (string?)g.Attribute("id") == "scene" && (string?)g.Attribute("data-scene") == "1") is { } scene)
        {
            scene.ReplaceWith(scene.Nodes());
        }
        return copy;
    }

    /// <summary>The motions compiled into the drawing (made safe first), and how long it plays.</summary>
    /// <param name="centers">Where each part's middle is, in the drawing's units (SvgRender.PartsAsync): travels go between parts by them.</param>
    public static Animated Apply(string markup, IReadOnlyList<Motion> motions, IReadOnlyDictionary<string, (double X, double Y)>? centers = null)
    {
        var root = Still(XElement.Parse(SvgSafe.Clean(markup).Markup));
        var box = ViewBox(root);
        var problems = new List<string>();
        var css = new StringBuilder();
        double once = 0, cycle = 0;
        var loops = false;
        var ids = root.DescendantsAndSelf().Where(e => e.Attribute("id") is not null).GroupBy(e => (string)e.Attribute("id")!).ToDictionary(g => g.Key, g => g.First());
        // The story: the motions with a start, in one cycle they share (as long as its plan says, or to its last end and a rest).
        var story = motions.Where(x => x.Start is not null && KindNames.Contains(Kind(x)) && Kind(x) != "trail").ToList();
        var ends = story.Count > 0 ? story.Max(x => Math.Max(0, x.Start!.Value) + Timing(Kind(x), x).Duration) : 0;
        var asked = story.Count > 0 ? story.Max(x => x.Cycle ?? 0) : 0;
        var beat = story.Count > 0 ? Math.Clamp(asked > 0 ? Math.Max(asked, ends) : ends + 0.8, 0.5, 60) : 0;
        var storyLoops = story.Any(x => !string.Equals((x.Repeat ?? "loop").Trim(), "once", StringComparison.OrdinalIgnoreCase));
        var moved = new List<(int Index, string Target, string Kind, XElement Wrap, Played Play, string BoxKind, string Origin)>();
        var trails = new List<(int Index, Motion Motion, string Target)>();
        for (var n = 0; n < motions.Count; n++)
        {
            var m = motions[n];
            var kind = (m.Kind ?? "").Trim().ToLowerInvariant();
            if (!KindNames.Contains(kind))
            {
                problems.Add($"motion {n + 1}: there is no kind \"{m.Kind}\" (one of {string.Join(", ", KindNames)})");
                continue;
            }
            var target = (m.Target ?? "scene").Trim().TrimStart('#');
            XElement? element;
            if (target is "scene" or "all" or "*" or "")
            {
                element = Scene(root, ids);
            }
            else if (!ids.TryGetValue(target, out element))
            {
                problems.Add($"motion {n + 1}: there is no part with the id \"{target}\" (the parts: {string.Join(", ", ids.Keys.Take(40))})");
                continue;
            }
            if (!Movable.Contains(element.Name.LocalName) || element.Ancestors().Any(a => a.Name.LocalName is "defs" or "clipPath" or "mask" or "pattern" or "symbol" or "marker"))
            {
                problems.Add($"motion {n + 1}: \"{target}\" is a <{element.Name.LocalName}> that is not drawn where it is: give the motion to what draws it");
                continue;
            }
            // One after another: its pieces (groups, or parts with names); a part of a few unnamed shapes (three birds), each of
            // them; a part of many unnamed shapes is one thing (a traced fox): it moves whole.
            var children = element.Elements().Where(c => Movable.Contains(c.Name.LocalName)).ToList();
            var pieces = children.Where(c => c.Name.LocalName == "g" || c.Attribute("id") is not null).ToList();
            var targets = m.Stagger is > 0 ? (pieces.Count > 0 ? pieces : children.Count <= 6 ? children : [element]) : [element];
            if (targets.Count == 0)
            {
                problems.Add($"motion {n + 1}: \"{target}\" has no parts of its own to go one after another");
                continue;
            }
            var t = Timing(kind, m);
            var name = $"k{n + 1}";
            (double X0, double Y0, double X1, double Y1)? way = null;
            if (kind is "travel" or "arc")
            {
                if (Way(target, m, centers) is not { } found)
                {
                    problems.Add($"motion {n + 1}: a {kind} goes to a part (to: its id, one of {string.Join(", ", centers?.Keys.Take(40) ?? [])}) or a point (\"x y\"), from a part or where \"{target}\" is");
                    continue;
                }
                way = found;
            }
            else if (kind == "camera")
            {
                // The view: the part's middle to the drawing's, that much closer.
                if (centers is null || !centers.TryGetValue((m.To ?? "").Trim().TrimStart('#'), out var focus))
                {
                    problems.Add($"motion {n + 1}: the camera goes close to a part (to: its id, one of {string.Join(", ", centers?.Keys.Take(40) ?? [])})");
                    continue;
                }
                var zoom = Math.Clamp(m.Amount ?? 1.6, 1.05, 4);
                way = (box.X + box.W / 2 - zoom * focus.X, box.Y + box.H / 2 - zoom * focus.Y, zoom, 0);
            }
            else if (kind == "trail")
            {
                // Made once the move it follows is.
                trails.Add((n, m, target));
                continue;
            }
            if (Frames(kind, m, box, element, way) is not { } frames)
            {
                problems.Add($"motion {n + 1}: keyframes need at least one key (at 0 to 1)");
                continue;
            }
            var onBeat = m.Start is { } begins && beat > 0;
            if (!onBeat)
            {
                css.Append(CultureInfo.InvariantCulture, $"@keyframes {name}{{{frames}}}");
            }
            for (var i = 0; i < targets.Count; i++)
            {
                Played play;
                if (onBeat)
                {
                    // Its part of the story's cycle: still before it, its move from its start, as it ended after.
                    var from = m.Start!.Value + i * (m.Stagger ?? 0);
                    var piece = targets.Count > 1 ? $"{name}-{i + 1}" : name;
                    css.Append(CultureInfo.InvariantCulture, $"@keyframes {piece}{{{Remap(frames, Math.Min(99, from / beat * 100), Math.Min(100, (from + t.Duration) / beat * 100))}}}");
                    play = new Played(piece, beat, t.Easing, 0, storyLoops ? null : 1, false);
                    once = Math.Max(once, beat);
                    if (storyLoops)
                    {
                        loops = true;
                        cycle = Math.Max(cycle, beat);
                    }
                }
                else
                {
                    var delay = m.Delay + i * (m.Stagger ?? 0);
                    play = new Played(name, t.Duration, t.Easing, delay, t.Times, t.Alternate);
                    once = Math.Max(once, delay + t.Duration * (t.Times ?? 1));
                    if (t.Times is null)
                    {
                        loops = true;
                        cycle = Math.Max(cycle, t.Duration * (t.Alternate ? 2 : 1));
                    }
                }
                var cls = $"m{n + 1}{(targets.Count > 1 ? $"-{i + 1}" : "")}";
                if (kind is "draw" or "color" or "flow")
                {
                    foreach (var shape in targets[i].DescendantsAndSelf().Where(e => (kind == "flow" ? e.Name.LocalName is "path" or "line" or "polyline" : Shapes.Contains(e.Name.LocalName))
                        || (kind == "color" && e.Name.LocalName is "text")))
                    {
                        Classed(shape, cls);
                        if (kind == "draw")
                        {
                            Drawable(shape, box);
                        }
                    }
                    css.Append(CultureInfo.InvariantCulture, $".{cls}{{animation:{play.Css()}}}");
                }
                else
                {
                    var wrap = new XElement(S + "g", new XAttribute(Mark, "wrap"), new XAttribute("class", cls));
                    targets[i].ReplaceWith(wrap);
                    wrap.Add(targets[i]);
                    var (boxKind, origin) = OriginOf(kind, m.Origin);
                    css.Append(CultureInfo.InvariantCulture, $".{cls}{{animation:{play.Css()};transform-box:{boxKind};transform-origin:{origin}}}");
                    moved.Add((n, target, kind, wrap, play, boxKind, origin));
                }
            }
        }
        // Trails: ghost copies of a part (a use of it, each fainter) that make its move a moment behind it.
        foreach (var (n, m, target) in trails)
        {
            var after = moved.Where(x => x.Index < n && x.Target == target && Moving.Contains(x.Kind)).ToList();
            if (target is "scene" or "all" or "*" or "" || after.Count == 0)
            {
                problems.Add($"motion {n + 1}: a trail follows a part's own move (a travel, arc, float...) given before it: \"{target}\" has none");
                continue;
            }
            var lead = after[^1];
            var count = (int)Math.Clamp(Math.Round(m.Amount ?? 4), 1, 8);
            for (var k = 1; k <= count; k++)
            {
                var cls = $"m{n + 1}-t{k}";
                var echo = new XElement(S + "g", new XAttribute(Mark, "echo"), new XAttribute("class", cls),
                    new XElement(S + "g", new XAttribute("opacity", N(0.45 * Math.Pow(1 - (k - 1.0) / count, 1.5))), new XElement(S + "use", new XAttribute("href", "#" + target))));
                lead.Wrap.AddBeforeSelf(echo);
                // Close behind each other: a smear of its way, not a row of copies.
                var play = lead.Play with { Delay = lead.Play.Delay + k * 0.04 };
                css.Append(CultureInfo.InvariantCulture, $".{cls}{{animation:{play.Css()};transform-box:{lead.BoxKind};transform-origin:{lead.Origin}}}");
            }
        }
        if (css.Length > 0)
        {
            root.Add(new XElement(S + "style", new XAttribute(Mark, "1"), css.ToString()));
        }
        // A loop plays through once, then each loop once more (an alternating one there and back): an export loops seamlessly.
        var seconds = Math.Round(Math.Min(60, loops ? Math.Max(once, cycle) : once), 3);
        return new Animated(root.ToString(SaveOptions.DisableFormatting), new Timeline(seconds, loops), problems);
    }

    private sealed record Time(double Duration, string Easing, double? Times, bool Alternate);

    /// <summary>How a part plays its keyframes: its CSS animation (null times: for ever).</summary>
    private sealed record Played(string Name, double Duration, string Easing, double Delay, double? Times, bool Alternate)
    {
        public string Css() => $"{Name} {N(Duration)}s {Easing} {N(Delay)}s {(Times is { } k ? N(k) : "infinite")} {(Alternate ? "alternate" : "normal")} both";
    }

    private static string Kind(Motion m) => (m.Kind ?? "").Trim().ToLowerInvariant();

    /// <summary>The kinds that go between parts, by where they are (measured on the drawing).</summary>
    public static readonly HashSet<string> Measured = new(StringComparer.OrdinalIgnoreCase) { "travel", "arc", "camera" };

    /// <summary>The motions that take a part somewhere, which a trail can follow.</summary>
    private static readonly HashSet<string> Moving = new(StringComparer.Ordinal)
    {
        "travel", "arc", "float", "drift", "bounce", "jump", "land", "slide-in", "slide-out", "rise", "sway", "swing", "orbit", "spin", "wave", "shake", "wiggle", "keyframes",
    };

    /// <summary>
    /// Keyframes moved into a part of a longer cycle: what they did from 0% to 100% done from <paramref name="from"/>% to
    /// <paramref name="to"/>%, as at their start before it and as at their end after it.
    /// </summary>
    public static string Remap(string frames, double from, double to)
    {
        var blocks = new List<(List<double> At, string Body)>();
        var i = 0;
        while (frames.IndexOf('{', i) is var open and >= 0 && frames.IndexOf('}', open) is var close and >= 0)
        {
            var at = frames[i..open].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x switch { "from" => 0d, "to" => 100d, _ => double.Parse(x.TrimEnd('%'), CultureInfo.InvariantCulture) }).ToList();
            blocks.Add((at, frames[(open + 1)..close]));
            i = close + 1;
        }
        if (blocks.Count == 0)
        {
            return frames;
        }
        var first = blocks.MinBy(b => b.At.Min()).Body;
        var last = blocks.MaxBy(b => b.At.Max()).Body;
        var o = new StringBuilder();
        if (from > 0)
        {
            o.Append(CultureInfo.InvariantCulture, $"0%{{{first}}}");
        }
        foreach (var (at, body) in blocks)
        {
            o.Append(string.Join(',', at.Select(p => N(from + p * (to - from) / 100) + "%"))).Append('{').Append(body).Append('}');
        }
        if (to < 100)
        {
            o.Append(CultureInfo.InvariantCulture, $"100%{{{last}}}");
        }
        return o.ToString();
    }

    private static Time Timing(string kind, Motion m)
    {
        var (duration, easing, loop, alternate) = kind switch
        {
            "fade-in" or "fade-out" => (0.8, "ease-out", false, false),
            "slide-in" or "rise" or "zoom-in" => (0.9, "cubic-bezier(.2,.9,.1,1)", false, false),
            "slide-out" => (0.8, "ease-in", false, false),
            "pop" => (0.6, "cubic-bezier(.34,1.56,.64,1)", false, false),
            "grow" or "shrink" => (0.7, "ease-out", false, false),
            "spin" => (3.0, "linear", true, false),
            "orbit" => (6.0, "linear", true, false),
            "float" => (3.0, "ease-in-out", true, false),
            "drift" => (6.0, "ease-in-out", true, false),
            "travel" => (2.6, "cubic-bezier(.45,0,.55,1)", true, false),
            "flow" => (0.9, "linear", true, false),
            "arc" => (2.2, "linear", true, false),
            "jump" => (1.1, "ease-in-out", true, false),
            "land" => (0.9, "ease-in", false, false),
            "flip" => (1.2, "ease-in-out", false, false),
            "camera" => (4.0, "cubic-bezier(.45,0,.55,1)", false, false),
            "bounce" => (1.4, "ease-in-out", true, false),
            "sway" or "swing" => (1.2, "ease-in-out", true, true),
            "wave" => (1.2, "ease-in-out", true, false),
            "pulse" => (1.6, "ease-in-out", true, false),
            "breathe" => (3.6, "ease-in-out", true, false),
            "twinkle" => (1.6, "ease-in-out", true, false),
            "glow" => (2.6, "ease-in-out", true, false),
            "heartbeat" => (1.3, "ease-in-out", true, false),
            "wiggle" or "shake" => (0.8, "ease-in-out", false, false),
            "blink" => (4.0, "linear", true, false),
            "draw" => (2.0, "ease-in-out", false, false),
            "color" => (2.0, "ease-in-out", true, true),
            _ => (1.5, "ease-in-out", false, false),
        };
        double? times = (m.Repeat ?? "").Trim().ToLowerInvariant() switch
        {
            "loop" or "infinite" or "forever" => null,
            "once" => 1,
            var r when double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var k) && k > 0 => Math.Min(k, 1000),
            _ => loop ? null : 1,
        };
        return new Time(Math.Clamp(m.Duration ?? duration, 0.05, 120), Easing(m.Easing) ?? easing, times, m.Alternate ?? alternate);
    }

    private static string? Easing(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "" => null,
        "linear" or "ease" or "ease-in" or "ease-out" or "ease-in-out" => name!.Trim().ToLowerInvariant(),
        "spring" or "overshoot" or "back" => "cubic-bezier(.34,1.56,.64,1)",
        "smooth" => "cubic-bezier(.45,0,.55,1)",
        "snappy" => "cubic-bezier(.2,.9,.1,1)",
        "gentle" => "cubic-bezier(.4,0,.2,1)",
        var s when s.StartsWith("steps", StringComparison.Ordinal) && int.TryParse(new string([.. s.Where(char.IsDigit)]), out var k) && k is > 0 and <= 100 => $"steps({k})",
        _ => null,
    };

    private static (string Box, string Origin) OriginOf(string kind, string? origin)
    {
        if (kind == "camera")
        {
            // The view moves in the drawing's own units: from its corner.
            return ("view-box", "0px 0px");
        }
        var o = (origin ?? (kind switch { "swing" => "top", "wave" or "breathe" or "jump" or "land" => "bottom", _ => "center" })).Trim().ToLowerInvariant();
        var parts = o.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            // A point of the drawing: about it, in the drawing's own units.
            return ("view-box", $"{parts[0]}px {parts[1]}px");
        }
        string X(string w) => w switch { "left" => "0%", "right" => "100%", _ => "50%" };
        string Y(string w) => w switch { "top" => "0%", "bottom" => "100%", _ => "50%" };
        var x = parts.FirstOrDefault(p => p is "left" or "right") ?? "center";
        var y = parts.FirstOrDefault(p => p is "top" or "bottom") ?? "center";
        return ("fill-box", $"{X(x)} {Y(y)}");
    }

    /// <summary>
    /// A travel's start and end, as moves from where the part is: from its own middle (or the middle of the part it starts
    /// at) to the middle of the part it goes to, or to a point. Null when where they are is not known.
    /// </summary>
    private static (double X0, double Y0, double X1, double Y1)? Way(string target, Motion m, IReadOnlyDictionary<string, (double X, double Y)>? centers)
    {
        if (centers is null || !centers.TryGetValue(target, out var self))
        {
            return null;
        }
        (double X, double Y)? Point(string? where)
        {
            var w = (where ?? "").Trim().TrimStart('#');
            if (w.Length == 0)
            {
                return null;
            }
            if (centers.TryGetValue(w, out var c))
            {
                return c;
            }
            var xy = w.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
            return xy.Length == 2 && double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ? (x, y) : null;
        }
        if (Point(m.To) is not { } to)
        {
            return null;
        }
        var from = Point(m.From) ?? self;
        return (from.X - self.X, from.Y - self.Y, to.X - self.X, to.Y - self.Y);
    }

    private static string? Frames(string kind, Motion m, (double X, double Y, double W, double H) box, XElement element, (double X0, double Y0, double X1, double Y1)? way = null)
    {
        var a = m.Amount;
        var side = (m.From ?? "left").Trim().ToLowerInvariant();
        var far = a ?? (side is "top" or "bottom" ? box.H : box.W) * 0.3;
        var (dx, dy) = side switch { "right" => (far, 0d), "top" => (0d, -far), "bottom" => (0d, far), _ => (-far, 0d) };
        string T(double x, double y) => $"transform:translate({N(x)}px,{N(y)}px)";
        return kind switch
        {
            "fade-in" => "from{opacity:0}to{opacity:1}",
            "fade-out" => "from{opacity:1}to{opacity:0}",
            "slide-in" => $"from{{{T(dx, dy)};opacity:0}}to{{{T(0, 0)};opacity:1}}",
            "slide-out" => $"from{{{T(0, 0)};opacity:1}}to{{{T(dx, dy)};opacity:0}}",
            "rise" => $"from{{{T(0, a ?? box.H * 0.15)};opacity:0}}to{{{T(0, 0)};opacity:1}}",
            "pop" => $"0%{{transform:scale(0);opacity:0}}60%{{transform:scale({N(1 + (a ?? 0.12))});opacity:1}}100%{{transform:scale(1);opacity:1}}",
            "grow" => "from{transform:scale(0)}to{transform:scale(1)}",
            "shrink" => "from{transform:scale(1)}to{transform:scale(0)}",
            "zoom-in" => $"from{{transform:scale({N(1 - (a ?? 0.4))});opacity:0}}to{{transform:scale(1);opacity:1}}",
            "spin" or "orbit" => $"from{{transform:rotate(0deg)}}to{{transform:rotate({N(360 * (a ?? 1))}deg)}}",
            "float" => $"0%,100%{{{T(0, 0)}}}50%{{{T(0, -(a ?? box.H * 0.03))}}}",
            "drift" => $"0%,100%{{{T(-(a ?? box.W * 0.03), 0)}}}50%{{{T(a ?? box.W * 0.03, 0)}}}",
            "arc" when way is { } w => Arc(w, a),
            // Dashes and gaps as long as a fiftieth and a sixtieth of the drawing, one dash and gap along each round.
            "flow" => $"from{{stroke-dasharray:{N(box.W * 0.02)} {N(box.W * 0.016)};stroke-dashoffset:{N(box.W * 0.036)}}}to{{stroke-dasharray:{N(box.W * 0.02)} {N(box.W * 0.016)};stroke-dashoffset:0}}",
            "jump" => Jump(a ?? box.H * 0.12),
            "land" => $"0%{{transform:translate(0px,{N(-(a ?? box.H * 0.3))}px) scale(1,1);opacity:0}}55%{{transform:translate(0px,0px) scale(1.15,.85);opacity:1;animation-timing-function:ease-out}}"
                + $"75%{{transform:translate(0px,{N(-(a ?? box.H * 0.3) * 0.08)}px) scale(.96,1.04);opacity:1}}100%{{transform:translate(0px,0px) scale(1,1);opacity:1}}",
            "flip" => "0%,100%{transform:scale(1,1)}25%,75%{transform:scale(0,1)}50%{transform:scale(-1,1)}",
            "camera" when way is { } w => $"0%,100%{{transform:translate(0px,0px) scale(1)}}30%,70%{{transform:translate({N(w.X0)}px,{N(w.Y0)}px) scale({N(w.X1)})}}",
            // Appears where it starts, goes, and fades where it arrives: each time round a new one on its way.
            "travel" when way is { } w => $"0%{{{T(w.X0, w.Y0)};opacity:0}}12%{{{T(w.X0, w.Y0)};opacity:1}}82%{{{T(w.X1, w.Y1)};opacity:1}}100%{{{T(w.X1, w.Y1)};opacity:0}}",
            "bounce" => $"0%,40%,70%,100%{{{T(0, 0)};animation-timing-function:ease-out}}20%{{{T(0, -(a ?? box.H * 0.08))};animation-timing-function:ease-in}}55%{{{T(0, -(a ?? box.H * 0.08) * 0.35)};animation-timing-function:ease-in}}",
            "sway" or "swing" => $"from{{transform:rotate({N(-(a ?? 8))}deg)}}to{{transform:rotate({N(a ?? 8)}deg)}}",
            "wave" => $"0%,100%{{transform:rotate(0deg)}}20%{{transform:rotate({N(a ?? 20)}deg)}}40%{{transform:rotate({N(-(a ?? 20) * 0.3)}deg)}}60%{{transform:rotate({N(a ?? 20)}deg)}}80%{{transform:rotate(0deg)}}",
            "pulse" => $"0%,100%{{transform:scale(1)}}50%{{transform:scale({N(1 + (a ?? 0.06))})}}",
            "breathe" => $"0%,100%{{transform:scale(1,1)}}50%{{transform:scale({N(1 + (a ?? 0.03) * 0.4)},{N(1 + (a ?? 0.03))})}}",
            "twinkle" => $"0%,100%{{opacity:1;transform:scale(1)}}50%{{opacity:{N(Math.Clamp(1 - (a ?? 0.6), 0, 1))};transform:scale(.78)}}",
            // A light at least a hundredth of the drawing wide (a plan's "amount" may be meant as a fraction).
            "glow" => $"0%,100%{{filter:drop-shadow(0 0 0px {Color(m.Color) ?? "#fff1a8"})}}50%{{filter:drop-shadow(0 0 {N(Math.Max(a ?? box.W * 0.025, box.W * 0.01))}px {Color(m.Color) ?? "#fff1a8"}) brightness(1.12)}}",
            "heartbeat" => $"0%,28%,70%,100%{{transform:scale(1)}}14%{{transform:scale({N(1 + (a ?? 0.15))})}}42%{{transform:scale({N(1 + (a ?? 0.15) * 0.8)})}}",
            "wiggle" => $"0%,100%{{transform:rotate(0deg)}}20%,60%{{transform:rotate({N(-(a ?? 6))}deg)}}40%,80%{{transform:rotate({N(a ?? 6)}deg)}}",
            "shake" => $"0%,100%{{{T(0, 0)}}}20%,60%{{{T(-(a ?? box.W * 0.02), 0)}}}40%,80%{{{T(a ?? box.W * 0.02, 0)}}}",
            "blink" => "0%,92%,100%{transform:scale(1,1)}96%{transform:scale(1,.1)}",
            "draw" => "0%{stroke-dashoffset:1;fill-opacity:0}70%{stroke-dashoffset:0;fill-opacity:0}100%{stroke-dashoffset:0;fill-opacity:1}",
            // To the colour from each shape's own: no from, so each starts as it is.
            "color" => $"to{{fill:{Color(m.Color) ?? "#e85d75"}}}",
            "keyframes" => Keys(m.Keys),
            _ => null,
        };
    }

    /// <summary>
    /// A flight along a curve: from its start up over the middle (as high as <paramref name="height"/>, else a third of the
    /// way) down to its end, appearing at the start and fading at the end; sampled, so its speed follows the curve.
    /// </summary>
    private static string Arc((double X0, double Y0, double X1, double Y1) w, double? height)
    {
        var length = Math.Sqrt(Math.Pow(w.X1 - w.X0, 2) + Math.Pow(w.Y1 - w.Y0, 2));
        var (cx, cy) = ((w.X0 + w.X1) / 2, Math.Min(w.Y0, w.Y1) - (height ?? Math.Max(30, length * 0.35)));
        string At(double t, double opacity, double scale)
        {
            var (x, y) = ((1 - t) * (1 - t) * w.X0 + 2 * (1 - t) * t * cx + t * t * w.X1, (1 - t) * (1 - t) * w.Y0 + 2 * (1 - t) * t * cy + t * t * w.Y1);
            return $"transform:translate({N(x)}px,{N(y)}px) scale({N(scale)});opacity:{N(opacity)}";
        }
        var o = new StringBuilder();
        o.Append(CultureInfo.InvariantCulture, $"0%{{{At(0, 0, 0.8)}}}8%{{{At(0, 1, 1)}}}");
        for (var k = 1; k <= 10; k++)
        {
            o.Append(CultureInfo.InvariantCulture, $"{N(8 + 80 * k / 10.0)}%{{{At(k / 10.0, 1, 1)}}}");
        }
        o.Append(CultureInfo.InvariantCulture, $"100%{{{At(1, 0, 0.8)}}}");
        return o.ToString();
    }

    /// <summary>A jump as a cartoon makes it: a crouch, up stretched, down squashed, settled (its origin its feet).</summary>
    private static string Jump(double height) =>
        $"0%,100%{{transform:translate(0px,0px) scale(1,1)}}15%{{transform:translate(0px,0px) scale(1.12,.86);animation-timing-function:ease-out}}"
        + $"45%{{transform:translate(0px,{N(-height)}px) scale(.92,1.1);animation-timing-function:ease-in}}70%{{transform:translate(0px,0px) scale(1.14,.84)}}"
        + "85%{transform:translate(0px,0px) scale(.97,1.03)}";

    private static string? Keys(IReadOnlyList<MotionKey>? keys)
    {
        if (keys is not { Count: > 0 })
        {
            return null;
        }
        var b = new StringBuilder();
        foreach (var k in keys.OrderBy(k => k.At))
        {
            var parts = new List<string>();
            var transform = new List<string>();
            if (k.X is not null || k.Y is not null)
            {
                transform.Add($"translate({N(k.X ?? 0)}px,{N(k.Y ?? 0)}px)");
            }
            if (k.Rotate is { } r)
            {
                transform.Add($"rotate({N(r)}deg)");
            }
            if (k.Scale is { } s)
            {
                transform.Add($"scale({N(s)})");
            }
            if (transform.Count > 0)
            {
                parts.Add("transform:" + string.Join(' ', transform));
            }
            if (k.Opacity is { } o)
            {
                parts.Add($"opacity:{N(Math.Clamp(o, 0, 1))}");
            }
            if (Color(k.Fill) is { } fill)
            {
                parts.Add($"fill:{fill}");
            }
            b.Append(CultureInfo.InvariantCulture, $"{N(Math.Clamp(k.At, 0, 1) * 100)}%{{{string.Join(';', parts)}}}");
        }
        return b.ToString();
    }

    /// <summary>A colour as CSS takes it (a name, #hex, rgb()/hsl()), else null: nothing else reaches the style.</summary>
    private static string? Color(string? c)
    {
        var v = (c ?? "").Trim();
        return v.Length is > 0 and <= 40 && v.All(ch => char.IsLetterOrDigit(ch) || ch is '#' or '(' or ')' or ',' or '.' or '%' or ' ' or '-') ? v : null;
    }

    private static void Classed(XElement e, string cls)
    {
        var had = (string?)e.Attribute("class");
        e.SetAttributeValue("class", had is null ? cls : $"{had} {cls}");
        e.SetAttributeValue(Mark, "1");
    }

    /// <summary>A shape whose lines are drawn on: measured as 1 long, dashed so; one without a line gets one in its fill's colour.</summary>
    private static void Drawable(XElement shape, (double X, double Y, double W, double H) box)
    {
        if (shape.Attribute("pathLength") is null)
        {
            shape.SetAttributeValue("pathLength", "1");
            shape.SetAttributeValue("data-motion-path", "1");
        }
        var style = (string?)shape.Attribute("style") ?? "";
        if (shape.Attribute("data-motion-style") is null)
        {
            shape.SetAttributeValue("data-motion-style", style);
        }
        else
        {
            style = (string)shape.Attribute("data-motion-style")!;
        }
        var stroke = (string?)shape.Attribute("stroke") ?? "";
        var extra = "stroke-dasharray:1;";
        if (stroke is "" or "none" && !style.Contains("stroke:", StringComparison.Ordinal))
        {
            var line = (string?)shape.Attribute("fill") is { } f and not "none" ? f : "currentColor";
            extra += $"stroke:{line};stroke-width:{N(Math.Max(box.W, box.H) / 300)};";
        }
        shape.SetAttributeValue("style", extra + style);
    }

    /// <summary>All that is drawn at the top, in one group with the id "scene" (made when there is none), to move it all.</summary>
    private static XElement Scene(XElement root, Dictionary<string, XElement> ids)
    {
        if (ids.TryGetValue("scene", out var scene))
        {
            return scene;
        }
        var g = new XElement(S + "g", new XAttribute("id", "scene"), new XAttribute("data-scene", "1"));
        var drawn = root.Elements().Where(e => e.Name.LocalName is not ("defs" or "style" or "title" or "desc" or "metadata")).ToList();
        drawn.Remove();
        g.Add(drawn);
        root.Add(g);
        ids["scene"] = g;
        return g;
    }

    public static (double X, double Y, double W, double H) ViewBox(XElement root)
    {
        var p = ((string?)root.Attribute("viewBox") ?? "0 0 512 512").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0).ToArray();
        return p.Length == 4 ? (p[0], p[1], p[2], p[3]) : (0, 0, 512, 512);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^m\d+(-\d+)?(-t\d+)?$")]
    private static partial System.Text.RegularExpressions.Regex Ours();
}
