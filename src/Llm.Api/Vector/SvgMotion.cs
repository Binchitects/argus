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
    /// <summary>Where a slide comes from or goes to: left, right, top, bottom.</summary>
    public string? From { get; init; }
    /// <summary>How far (drawing units), how much (degrees for turns, a fraction for scale), by kind.</summary>
    public double? Amount { get; init; }
    /// <summary>What it turns and scales about: center, top, bottom, left, right, top left..., or "x y" in the drawing's units.</summary>
    public string? Origin { get; init; }
    public string? Color { get; init; }
    public IReadOnlyList<MotionKey>? Keys { get; init; }
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
        ("float", "rises and sinks gently (amount: how high)"), ("drift", "drifts slowly from side to side, as a cloud or waves (amount: how far)"), ("bounce", "bounces up and down"), ("sway", "rocks from side to side (amount: degrees)"),
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
    public static Animated Apply(string markup, IReadOnlyList<Motion> motions)
    {
        var root = Still(XElement.Parse(SvgSafe.Clean(markup).Markup));
        var box = ViewBox(root);
        var problems = new List<string>();
        var css = new StringBuilder();
        double once = 0, cycle = 0;
        var loops = false;
        var ids = root.DescendantsAndSelf().Where(e => e.Attribute("id") is not null).GroupBy(e => (string)e.Attribute("id")!).ToDictionary(g => g.Key, g => g.First());
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
            var targets = m.Stagger is > 0
                ? element.Elements().Where(c => Movable.Contains(c.Name.LocalName)).ToList()
                : [element];
            if (targets.Count == 0)
            {
                problems.Add($"motion {n + 1}: \"{target}\" has no parts of its own to go one after another");
                continue;
            }
            var t = Timing(kind, m);
            var name = $"k{n + 1}";
            if (Frames(kind, m, box, element) is not { } frames)
            {
                problems.Add($"motion {n + 1}: keyframes need at least one key (at 0 to 1)");
                continue;
            }
            css.Append(CultureInfo.InvariantCulture, $"@keyframes {name}{{{frames}}}");
            for (var i = 0; i < targets.Count; i++)
            {
                var delay = m.Delay + i * (m.Stagger ?? 0);
                var animation = $"{name} {N(t.Duration)}s {t.Easing} {N(delay)}s {(t.Times is { } k ? N(k) : "infinite")} {(t.Alternate ? "alternate" : "normal")} both";
                var cls = $"m{n + 1}{(targets.Count > 1 ? $"-{i + 1}" : "")}";
                if (kind is "draw" or "color")
                {
                    foreach (var shape in targets[i].DescendantsAndSelf().Where(e => Shapes.Contains(e.Name.LocalName) || (kind == "color" && e.Name.LocalName is "text")))
                    {
                        Classed(shape, cls);
                        if (kind == "draw")
                        {
                            Drawable(shape, box);
                        }
                    }
                    css.Append(CultureInfo.InvariantCulture, $".{cls}{{animation:{animation}}}");
                }
                else
                {
                    var wrap = new XElement(S + "g", new XAttribute(Mark, "wrap"), new XAttribute("class", cls));
                    targets[i].ReplaceWith(wrap);
                    wrap.Add(targets[i]);
                    var (boxKind, origin) = OriginOf(kind, m.Origin);
                    css.Append(CultureInfo.InvariantCulture, $".{cls}{{animation:{animation};transform-box:{boxKind};transform-origin:{origin}}}");
                }
                var length = delay + t.Duration * (t.Times ?? 1);
                once = Math.Max(once, length);
                if (t.Times is null)
                {
                    loops = true;
                    cycle = Math.Max(cycle, t.Duration * (t.Alternate ? 2 : 1));
                }
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
        var o = (origin ?? (kind switch { "swing" => "top", "wave" or "breathe" => "bottom", _ => "center" })).Trim().ToLowerInvariant();
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

    private static string? Frames(string kind, Motion m, (double X, double Y, double W, double H) box, XElement element)
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
            "bounce" => $"0%,40%,70%,100%{{{T(0, 0)};animation-timing-function:ease-out}}20%{{{T(0, -(a ?? box.H * 0.08))};animation-timing-function:ease-in}}55%{{{T(0, -(a ?? box.H * 0.08) * 0.35)};animation-timing-function:ease-in}}",
            "sway" or "swing" => $"from{{transform:rotate({N(-(a ?? 8))}deg)}}to{{transform:rotate({N(a ?? 8)}deg)}}",
            "wave" => $"0%,100%{{transform:rotate(0deg)}}20%{{transform:rotate({N(a ?? 20)}deg)}}40%{{transform:rotate({N(-(a ?? 20) * 0.3)}deg)}}60%{{transform:rotate({N(a ?? 20)}deg)}}80%{{transform:rotate(0deg)}}",
            "pulse" => $"0%,100%{{transform:scale(1)}}50%{{transform:scale({N(1 + (a ?? 0.06))})}}",
            "breathe" => $"0%,100%{{transform:scale(1,1)}}50%{{transform:scale({N(1 + (a ?? 0.03) * 0.4)},{N(1 + (a ?? 0.03))})}}",
            "twinkle" => $"0%,100%{{opacity:1;transform:scale(1)}}50%{{opacity:{N(Math.Clamp(1 - (a ?? 0.6), 0, 1))};transform:scale(.78)}}",
            "glow" => $"0%,100%{{filter:drop-shadow(0 0 0px {Color(m.Color) ?? "#fff1a8"})}}50%{{filter:drop-shadow(0 0 {N(a ?? box.W * 0.025)}px {Color(m.Color) ?? "#fff1a8"}) brightness(1.12)}}",
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

    [System.Text.RegularExpressions.GeneratedRegex(@"^m\d+(-\d+)?$")]
    private static partial System.Text.RegularExpressions.Regex Ours();
}
