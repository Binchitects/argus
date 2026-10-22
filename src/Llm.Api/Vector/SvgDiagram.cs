using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Llm.Api.Chat;
using Llm.Core.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.Vector;

/// <summary>A thing in a diagram: its id, its icon (Lucide's name), its label, where its middle is (0 to 1 of the canvas), how big, its colour and its shape.</summary>
public sealed record DiagramPart
{
    public string Id { get; init; } = "";
    public string Icon { get; init; } = "";
    public string? Label { get; init; }
    public double X { get; init; } = 0.5;
    public double Y { get; init; } = 0.5;
    /// <summary>Its tile's side, a fraction of the canvas's shorter side.</summary>
    public double Size { get; init; } = 0.2;
    public string? Color { get; init; }
    /// <summary>"tile" (a rounded square), "circle", or "icon" (the icon alone, in its colour).</summary>
    public string? Shape { get; init; }
}

/// <summary>An arrow between two things: dashed or solid, with a label of its own.</summary>
public sealed record DiagramLink
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string? Label { get; init; }
    public string? Style { get; init; }
}

public sealed record DiagramLayout
{
    public string? Background { get; init; }
    public IReadOnlyList<DiagramPart> Parts { get; init; } = [];
    public IReadOnlyList<DiagramLink> Links { get; init; } = [];
}

/// <summary>
/// Diagrams that explain (a laptop sending a file to a server, a pipeline, a system): the model lays out its things — each an
/// icon of Lucide's (ISC licence, its 1,900 icons in Icons/lucide.json, found by their names and tags), a label, a place —
/// and the arrows between them; this draws them clean and alike: tiles with a soft shadow and a light gradient, white
/// strokes, labels under them, curved arrows. Every thing is a part of its own (its id), every arrow too ("link-…"),
/// so each moves on its own.
/// </summary>
public sealed partial class SvgDiagram(OneQuestion one, IOptionsMonitor<ChatOptions> chat)
{
    private static readonly XNamespace S = SvgSafe.Svg;

    /// <summary>Each icon: its tags (what it shows), and its drawing (Lucide's elements, on a 24 by 24 grid, stroked).</summary>
    public static readonly Lazy<IReadOnlyDictionary<string, (string[] Tags, IReadOnlyList<(string Tag, IReadOnlyDictionary<string, string> Attributes)> Nodes)>> Icons = new(() =>
    {
        using var stream = typeof(SvgDiagram).Assembly.GetManifestResourceStream("lucide.json") ?? throw new InvalidOperationException("lucide.json is not in the app");
        var all = JsonNode.Parse(stream)!.AsObject();
        return all.ToDictionary(kv => kv.Key, kv => (
            (kv.Value!["tags"] as JsonArray ?? []).Select(t => t!.GetValue<string>()).ToArray(),
            (IReadOnlyList<(string, IReadOnlyDictionary<string, string>)>)[.. (kv.Value!["nodes"] as JsonArray ?? []).OfType<JsonArray>()
                .Select(n => (n[0]!.GetValue<string>(), (IReadOnlyDictionary<string, string>)n[1]!.AsObject().ToDictionary(a => a.Key, a => a.Value!.ToString())))]), StringComparer.Ordinal);
    });

    /// <summary>The palette a diagram's things take, in turn, when the model gives none.</summary>
    private static readonly string[] Palette = ["#6366F1", "#0EA5E9", "#10B981", "#F59E0B", "#EF4444", "#8B5CF6", "#EC4899", "#14B8A6"];

    private string? Model => chat.CurrentValue.SvgModel is { Length: > 0 } m ? m : null;

    private static readonly JsonSerializerOptions Lenient = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>The icons whose names or tags these words name (the best first), at most <paramref name="most"/>.</summary>
    public static IReadOnlyList<string> Candidates(string words, int most = 80)
    {
        var asked = Words().Matches(words.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length > 2).Select(w => w.TrimEnd('s')).Distinct().ToList();
        return [.. Icons.Value.Select(kv =>
            {
                var parts = kv.Key.Split('-');
                var score = asked.Sum(w => (parts.Contains(w) ? 3 : 0) + (parts[0] == w ? 2 : 0) + (kv.Value.Tags.Any(t => t == w || t.TrimEnd('s') == w) ? 1 : 0));
                return (Name: kv.Key, Score: score - parts.Length * 0.1);
            })
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(most).Select(x => x.Name)];
    }

    [GeneratedRegex("[a-z][a-z0-9]*")]
    private static partial Regex Words();

    private const string Laying = """
        You lay out explanatory diagrams: flat, clean, modern (as a product's landing page explains how it works).
        Answer with JSON only:
        {"background": "#hex (light)", "parts": [{"id": "laptop", "icon": "laptop", "label": "Client", "x": 0.78, "y": 0.5, "size": 0.24, "color": "#6366F1", "shape": "tile"}],
         "links": [{"from": "laptop", "to": "server", "label": "", "style": "dashed"}]}
        - parts: each thing the request names, its id (lowercase words joined by hyphens), an icon from the list given (its exact name), a short label (one or two words, or "" for none).
        - x, y: where its middle is, 0 to 1 across and down the canvas; things well apart (at least 0.25), none nearer an edge than 0.12, the whole balanced and centred.
        - size: 0.18 to 0.3 for the main things; 0.08 to 0.12 for what goes between them (a file, a message, a packet), placed between them.
        - shape: "tile" for the main things, "circle" for people and small moving things, "icon" for a small thing alone.
        - color: a harmonious palette, each thing its own colour, what goes between them an accent.
        - links: the arrows of the flow, from and to parts' ids; dashed for data that flows, solid for a fixed link; none when there is no flow.
        """;

    /// <summary>A diagram of these words laid out by the model: its things, places and arrows; null and why when it cannot.</summary>
    public async Task<(DiagramLayout? Layout, string? Problem)> LayOutAsync(AppUser me, string scene, string? motion, CancellationToken ct)
    {
        var icons = Candidates($"{scene} {motion}");
        var listed = string.Join(", ", icons.Select(n => Icons.Value[n].Tags.Length > 0 ? $"{n} ({string.Join(' ', Icons.Value[n].Tags.Take(4))})" : n));
        var question = $"The request: {scene}{(motion is { Length: > 0 } ? $"\nIt will move so: {motion}" : "")}\n\nIcons that may fit (name and what it shows): {listed}\n"
            + "Other icons of Lucide by their exact names are allowed (circle, square, user, users, server, laptop, file, file-text, database, cloud, globe, lock, mail, smartphone, monitor, cpu, wifi, shield, key, bell, image, package, truck, rocket, star, heart, check, x).";
        var asked = await one.AskAsync(me, question, Laying, Model, 1500, "svg-diagram", ct);
        if (asked.Problem is not null)
        {
            return (null, "The model could not be asked now.");
        }
        var text = asked.Text ?? "";
        var (start, end) = (text.IndexOf('{'), text.LastIndexOf('}'));
        try
        {
            var layout = start >= 0 && end > start
                ? JsonSerializer.Deserialize<DiagramLayout>(text[start..(end + 1)], Lenient)
                : null;
            return layout is { Parts.Count: > 0 } ? (layout, null) : (null, "The model laid out nothing.");
        }
        catch (JsonException)
        {
            return (null, "The model did not answer with a layout.");
        }
    }

    private static string N(double v) => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);

    /// <summary>An icon by a name the model gave: as given, else the nearest by its words, else a circle.</summary>
    public static string IconOf(string? name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant().Replace(' ', '-');
        if (Icons.Value.ContainsKey(n))
        {
            return n;
        }
        return Candidates(n.Replace('-', ' '), 1) is [var best, ..] ? best : "circle";
    }

    /// <summary>The layout drawn: a background, the arrows (behind), then each thing — a tile, its icon, its label.</summary>
    public static string Render(DiagramLayout layout, int width, int height)
    {
        var (w, h) = (Math.Clamp(width, 320, 4096), Math.Clamp(height, 240, 4096));
        var side = Math.Min(w, h);
        var root = new XElement(S + "svg", new XAttribute("viewBox", $"0 0 {w} {h}"), new XAttribute("width", w), new XAttribute("height", h));
        var defs = new XElement(S + "defs",
            new XElement(S + "filter", new XAttribute("id", "soft-shadow"), new XAttribute("x", "-30%"), new XAttribute("y", "-30%"), new XAttribute("width", "160%"), new XAttribute("height", "160%"),
                new XElement(S + "feDropShadow", new XAttribute("dx", "0"), new XAttribute("dy", N(side * 0.012)), new XAttribute("stdDeviation", N(side * 0.014)),
                    new XAttribute("flood-color", "#0F172A"), new XAttribute("flood-opacity", "0.18"))));
        root.Add(defs);
        root.Add(new XElement(S + "rect", new XAttribute("id", "background"), new XAttribute("width", w), new XAttribute("height", h),
            new XAttribute("fill", Color(layout.Background) ?? "#F8FAFC")));

        // Each thing once, its id its own (made so), its place kept off the edges.
        var taken = new HashSet<string>(["background", "scene"], StringComparer.Ordinal);
        var parts = new List<(DiagramPart Part, string Id, double X, double Y, double Size, string Color)>();
        foreach (var (p, i) in layout.Parts.Take(24).Select((p, i) => (p, i)))
        {
            var id = SvgDraw.ThingId(p.Id) is { Length: > 0 } t ? t : $"part-{i + 1}";
            while (!taken.Add(id))
            {
                id += "-b";
            }
            var size = Math.Clamp(p.Size, 0.05, 0.4) * side;
            // A colour too light to see on the light background (white on white) takes the palette's.
            var color = Color(p.Color) is { } c && Lightness(c) < 0.8 ? c : Palette[i % Palette.Length];
            parts.Add((p, id, Math.Clamp(p.X, 0.06, 0.94) * w, Math.Clamp(p.Y, 0.06, 0.94) * h, size, color));
        }
        // Things apart: any two nearer than their halves and a gap are pushed away from each other along the line between
        // them (a monitor laid on a server), a few rounds, kept inside the canvas.
        for (var round = 0; round < 40; round++)
        {
            var moved = false;
            for (var i = 0; i < parts.Count; i++)
            {
                for (var j = i + 1; j < parts.Count; j++)
                {
                    var (a, b) = (parts[i], parts[j]);
                    var need = (a.Size + b.Size) / 2 + side * 0.06;
                    var (dx, dy) = (b.X - a.X, b.Y - a.Y);
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    if (d >= need)
                    {
                        continue;
                    }
                    (dx, dy) = d < 1e-3 ? (1.0, 0.0) : (dx / d, dy / d);
                    var push = (need - d) / 2;
                    parts[i] = a with { X = Math.Clamp(a.X - dx * push, a.Size / 2 + side * 0.04, w - a.Size / 2 - side * 0.04), Y = Math.Clamp(a.Y - dy * push, a.Size / 2 + side * 0.04, h - a.Size / 2 - side * 0.1) };
                    parts[j] = b with { X = Math.Clamp(b.X + dx * push, b.Size / 2 + side * 0.04, w - b.Size / 2 - side * 0.04), Y = Math.Clamp(b.Y + dy * push, b.Size / 2 + side * 0.04, h - b.Size / 2 - side * 0.1) };
                    moved = true;
                }
            }
            if (!moved)
            {
                break;
            }
        }
        var byId = parts.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var linkNo = 0;
        foreach (var link in layout.Links.Take(24))
        {
            if (!byId.TryGetValue(SvgDraw.ThingId(link.From), out var a) || !byId.TryGetValue(SvgDraw.ThingId(link.To), out var b) || a.Id == b.Id)
            {
                continue;
            }
            root.Add(Arrow($"link-{a.Id}-{b.Id}" + (taken.Add($"link-{a.Id}-{b.Id}") ? "" : $"-{++linkNo}"), a, b, link, side));
        }
        foreach (var p in parts)
        {
            root.Add(Thing(p.Id, p.Part, p.X, p.Y, p.Size, p.Color, side, defs));
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement Thing(string id, DiagramPart part, double x, double y, double size, string color, double side, XElement defs)
    {
        var shape = (part.Shape ?? "tile").Trim().ToLowerInvariant();
        var g = new XElement(S + "g", new XAttribute("id", id));
        var gradient = $"fill-{id}";
        defs.Add(new XElement(S + "linearGradient", new XAttribute("id", gradient), new XAttribute("x1", "0"), new XAttribute("y1", "0"), new XAttribute("x2", "0"), new XAttribute("y2", "1"),
            new XElement(S + "stop", new XAttribute("offset", "0"), new XAttribute("stop-color", Lighter(color, 0.18))),
            new XElement(S + "stop", new XAttribute("offset", "1"), new XAttribute("stop-color", color))));
        var stroke = "#FFFFFF";
        if (shape == "circle")
        {
            g.Add(new XElement(S + "circle", new XAttribute("cx", N(x)), new XAttribute("cy", N(y)), new XAttribute("r", N(size / 2)),
                new XAttribute("fill", $"url(#{gradient})"), new XAttribute("filter", "url(#soft-shadow)")));
        }
        else if (shape == "icon")
        {
            // Alone, on a disc of its own colour's tint: seen on any background, as it moves.
            stroke = color;
            g.Add(new XElement(S + "circle", new XAttribute("cx", N(x)), new XAttribute("cy", N(y)), new XAttribute("r", N(size * 0.62)),
                new XAttribute("fill", Lighter(color, 0.82)), new XAttribute("stroke", Lighter(color, 0.5)), new XAttribute("stroke-width", N(Math.Max(1, side * 0.003)))));
        }
        else
        {
            g.Add(new XElement(S + "rect", new XAttribute("x", N(x - size / 2)), new XAttribute("y", N(y - size / 2)), new XAttribute("width", N(size)), new XAttribute("height", N(size)),
                new XAttribute("rx", N(size * 0.22)), new XAttribute("fill", $"url(#{gradient})"), new XAttribute("filter", "url(#soft-shadow)")));
        }
        // The icon on its 24 grid, as large as the tile leaves room for (all of it alone).
        var icon = shape == "icon" ? size * 0.8 : size * 0.56;
        var scale = icon / 24;
        var drawn = new XElement(S + "g", new XAttribute("transform", $"translate({N(x - icon / 2)} {N(y - icon / 2)}) scale({N(scale)})"),
            new XAttribute("fill", "none"), new XAttribute("stroke", stroke), new XAttribute("stroke-width", N(shape == "icon" ? 2.2 : 1.9)),
            new XAttribute("stroke-linecap", "round"), new XAttribute("stroke-linejoin", "round"));
        foreach (var (tag, attributes) in Icons.Value[IconOf(part.Icon)].Nodes)
        {
            drawn.Add(new XElement(S + tag, attributes.Select(a => new XAttribute(a.Key, a.Value))));
        }
        g.Add(drawn);
        if (part.Label is { Length: > 0 } label)
        {
            g.Add(new XElement(S + "text", new XAttribute("x", N(x)), new XAttribute("y", N(y + size / 2 + side * 0.055)), new XAttribute("text-anchor", "middle"),
                new XAttribute("font-family", "Inter, 'Segoe UI', system-ui, sans-serif"), new XAttribute("font-size", N(Math.Max(12, side * 0.04))), new XAttribute("font-weight", "600"),
                new XAttribute("fill", "#334155"), label.Length > 40 ? label[..40] : label));
        }
        return g;
    }

    /// <summary>An arrow from one thing's edge to the other's, bowed a little, its head at the end, its label over its middle.</summary>
    private static XElement Arrow(string id, (DiagramPart Part, string Id, double X, double Y, double Size, string Color) a,
        (DiagramPart Part, string Id, double X, double Y, double Size, string Color) b, DiagramLink link, double side)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        var (ux, uy) = (dx / length, dy / length);
        var (x0, y0) = (a.X + ux * (a.Size / 2 + side * 0.03), a.Y + uy * (a.Size / 2 + side * 0.03));
        var (x1, y1) = (b.X - ux * (b.Size / 2 + side * 0.03), b.Y - uy * (b.Size / 2 + side * 0.03));
        // Bowed up a little, to one side of the straight line.
        var bow = Math.Min(length * 0.12, side * 0.06);
        var (cx, cy) = ((x0 + x1) / 2 + uy * bow, (y0 + y1) / 2 - ux * bow);
        var color = "#94A3B8";
        var dashed = !string.Equals((link.Style ?? "dashed").Trim(), "solid", StringComparison.OrdinalIgnoreCase);
        var g = new XElement(S + "g", new XAttribute("id", id));
        g.Add(new XElement(S + "path", new XAttribute("d", $"M{N(x0)} {N(y0)} Q{N(cx)} {N(cy)} {N(x1)} {N(y1)}"), new XAttribute("fill", "none"), new XAttribute("stroke", color),
            new XAttribute("stroke-width", N(Math.Max(2, side * 0.006))), new XAttribute("stroke-linecap", "round"),
            dashed ? new XAttribute("stroke-dasharray", $"{N(side * 0.02)} {N(side * 0.016)}") : null));
        // The head, along the curve's last direction.
        var (hx, hy) = (x1 - cx, y1 - cy);
        var hl = Math.Max(1, Math.Sqrt(hx * hx + hy * hy));
        (hx, hy) = (hx / hl, hy / hl);
        var head = side * 0.03;
        g.Add(new XElement(S + "path", new XAttribute("d",
            $"M{N(x1)} {N(y1)} L{N(x1 - hx * head - hy * head * 0.6)} {N(y1 - hy * head + hx * head * 0.6)} L{N(x1 - hx * head + hy * head * 0.6)} {N(y1 - hy * head - hx * head * 0.6)} Z"),
            new XAttribute("fill", color)));
        if (link.Label is { Length: > 0 } label)
        {
            g.Add(new XElement(S + "text", new XAttribute("x", N(cx)), new XAttribute("y", N(cy - side * 0.025)), new XAttribute("text-anchor", "middle"),
                new XAttribute("font-family", "Inter, 'Segoe UI', system-ui, sans-serif"), new XAttribute("font-size", N(Math.Max(11, side * 0.032))), new XAttribute("fill", "#64748B"),
                label.Length > 40 ? label[..40] : label));
        }
        return g;
    }

    private static string? Color(string? c)
    {
        var v = (c ?? "").Trim();
        return HexColor().IsMatch(v) ? v : null;
    }

    [GeneratedRegex("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexColor();

    /// <summary>How light a colour is, 0 (black) to 1 (white), as the eye sees it.</summary>
    private static double Lightness(string hex)
    {
        var v = hex.TrimStart('#');
        if (v.Length == 3)
        {
            v = string.Concat(v.Select(ch => $"{ch}{ch}"));
        }
        var rgb = Enumerable.Range(0, 3).Select(i => int.Parse(v.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0).ToArray();
        return 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2];
    }

    /// <summary>A colour this much nearer white.</summary>
    private static string Lighter(string hex, double by)
    {
        var v = hex.TrimStart('#');
        if (v.Length == 3)
        {
            v = string.Concat(v.Select(ch => $"{ch}{ch}"));
        }
        var rgb = Enumerable.Range(0, 3).Select(i => int.Parse(v.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .Select(c => (int)Math.Round(c + (255 - c) * by)).ToArray();
        return $"#{rgb[0]:X2}{rgb[1]:X2}{rgb[2]:X2}";
    }
}
