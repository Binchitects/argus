using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Llm.Api.Vector;

/// <summary>Why an SVG could not be taken, in words for the person or the model that wrote it.</summary>
public sealed class SvgException(string message) : Exception(message);

/// <summary>An SVG made safe: its markup, its size (its viewBox's), what was changed, how many elements, and whether it moves.</summary>
public sealed record SafeSvg(string Markup, double Width, double Height, IReadOnlyList<string> Changed, int Elements, bool Animated);

/// <summary>
/// SVG made safe to keep, show and render, and tidy: the markup a model or a person wrote (in a code fence or not, with or
/// without its namespace) read as XML with no document type (no entities from outside), and only what draws kept — shapes,
/// paths, text, gradients, patterns, masks, filters, markers, symbols, styles and animations (SMIL and CSS). Never script,
/// event handlers, foreign HTML, or anything that reaches outside: a reference is to an element of its own (#id) or a
/// picture inside it (data:). Its root gets its namespace and a viewBox; long decimals are cut to three places.
/// </summary>
public static partial class SvgSafe
{
    /// <summary>How a drawing is served: opened on its own it runs nothing and reaches nothing (a sandboxing content policy).</summary>
    public const string Policy = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; sandbox";

    public const int MostBytes = 4 * 1024 * 1024;
    public const int MostElements = 60_000;
    public const int MostDepth = 64;

    public static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    public static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    /// <summary>What may be drawn: SVG's shapes, text, paint servers, structure, filters and animations.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "svg", "g", "defs", "symbol", "use", "title", "desc", "switch",
        "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "image",
        "text", "tspan", "textPath",
        "linearGradient", "radialGradient", "stop", "pattern", "clipPath", "mask", "marker", "style",
        "filter", "feBlend", "feColorMatrix", "feComponentTransfer", "feComposite", "feConvolveMatrix", "feDiffuseLighting", "feDisplacementMap",
        "feDistantLight", "feDropShadow", "feFlood", "feFuncA", "feFuncB", "feFuncG", "feFuncR", "feGaussianBlur", "feMerge", "feMergeNode",
        "feMorphology", "feOffset", "fePointLight", "feSpecularLighting", "feSpotLight", "feTile", "feTurbulence",
        "animate", "animateTransform", "animateMotion", "mpath", "set",
    };

    /// <summary>Kept for what is inside them: a link's drawing is drawn, its link is not.</summary>
    private static readonly HashSet<string> Unwrapped = new(StringComparer.Ordinal) { "a" };

    /// <summary>Attributes whose values are numbers (and lists of them): their long decimals are cut.</summary>
    private static readonly HashSet<string> Numeric = new(StringComparer.Ordinal)
    {
        "d", "points", "transform", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry", "fx", "fy", "width", "height", "viewBox",
        "stroke-width", "offset", "opacity", "fill-opacity", "stroke-opacity", "gradientTransform", "patternTransform", "dx", "dy",
    };

    /// <summary>A few of HTML's named characters, as models write them; any other name is dropped.</summary>
    private static readonly Dictionary<string, string> Named = new(StringComparer.Ordinal)
    {
        ["nbsp"] = "&#160;", ["copy"] = "&#169;", ["reg"] = "&#174;", ["trade"] = "&#8482;", ["mdash"] = "&#8212;", ["ndash"] = "&#8211;",
        ["hellip"] = "&#8230;", ["laquo"] = "&#171;", ["raquo"] = "&#187;", ["deg"] = "&#176;", ["times"] = "&#215;", ["middot"] = "&#183;",
        ["bull"] = "&#8226;", ["rsquo"] = "&#8217;", ["lsquo"] = "&#8216;", ["rdquo"] = "&#8221;", ["ldquo"] = "&#8220;", ["euro"] = "&#8364;",
        ["amp"] = "&amp;", ["lt"] = "&lt;", ["gt"] = "&gt;", ["quot"] = "&quot;", ["apos"] = "&apos;",
    };

    /// <summary>The SVG in a text (a model's answer, a file), made safe; or why it cannot be.</summary>
    public static SafeSvg Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new SvgException("There is no SVG: write one <svg>…</svg>.");
        }
        if (text.Length > MostBytes)
        {
            throw new SvgException($"The SVG is larger than {MostBytes / 1024 / 1024} MB.");
        }
        var start = text.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        var end = text.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            throw new SvgException("There is no <svg> element in it.");
        }
        // An answer cut short has no closing tag: closed here, and what was open closes with it (XML says where).
        var markup = end > start ? text[start..(end + 6)] : text[start..] + "</svg>";
        markup = Entity().Replace(markup, m => Named.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value.StartsWith("&#", StringComparison.Ordinal) ? m.Value : "");
        markup = Declared(markup);
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(markup), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0, IgnoreProcessingInstructions = true,
                MaxCharactersInDocument = MostBytes * 2L,
            });
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new SvgException($"The SVG is not well-formed XML: {ex.Message}");
        }
        var changed = new List<string>();
        var root = doc.Root!;
        if (root.Name.LocalName != "svg")
        {
            throw new SvgException("Its outermost element is not <svg>.");
        }
        var count = 0;
        var animated = false;
        var kept = Walk(root, 0, changed, ref count, ref animated) ?? throw new SvgException("Nothing in it can be drawn.");
        Size(kept, changed, out var width, out var height);
        if (!kept.Descendants().Any(e => Drawn.Contains(e.Name.LocalName)))
        {
            throw new SvgException("It draws nothing: it has no shapes, paths, text or pictures.");
        }
        // Its namespaces declared once at the top: SVG's own, and XLink's when an attribute uses it.
        kept.Attributes().Where(a => a.IsNamespaceDeclaration).Remove();
        kept.SetAttributeValue("xmlns", Svg.NamespaceName);
        if (kept.DescendantsAndSelf().Attributes().Any(a => a.Name.Namespace == XLink))
        {
            kept.SetAttributeValue(XNamespace.Xmlns + "xlink", XLink.NamespaceName);
        }
        animated |= kept.Descendants(Svg + "style").Any(s => Keyframes().IsMatch(s.Value)) || kept.DescendantsAndSelf().Attributes("style").Any(a => a.Value.Contains("animation", StringComparison.OrdinalIgnoreCase));
        return new SafeSvg(kept.ToString(SaveOptions.DisableFormatting), width, height, changed.Distinct().ToList(), count, animated);
    }

    /// <summary>
    /// Prefixes used but never declared (xlink:href without its xmlns:xlink, an editor's inkscape:...), declared on the root so
    /// the markup reads as XML: XLink's as itself, the others as namespaces of their own, which are then left out.
    /// </summary>
    private static string Declared(string markup)
    {
        var used = Prefixes().Matches(markup).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .Where(p => p is not ("xml" or "xmlns") && !markup.Contains($"xmlns:{p}=", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        if (used.Count == 0)
        {
            return markup;
        }
        var declarations = string.Concat(used.Select(p => $" xmlns:{p}=\"{(p == "xlink" ? XLink.NamespaceName : "urn:x-undeclared:" + p)}\""));
        return markup.Insert(4, declarations);
    }

    /// <summary>What draws: an SVG with none of these draws nothing.</summary>
    private static readonly HashSet<string> Drawn = new(StringComparer.Ordinal) { "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text", "image", "use" };

    private static XElement? Walk(XElement e, int depth, List<string> changed, ref int count, ref bool animated)
    {
        var name = e.Name.LocalName;
        if (e.Name.Namespace != Svg && e.Name.Namespace != XNamespace.None)
        {
            changed.Add($"<{name}> of another namespace taken out");
            return null;
        }
        if (!Allowed.Contains(name) && !Unwrapped.Contains(name))
        {
            changed.Add(name is "script" or "foreignObject" or "iframe" or "object" or "embed" ? $"<{name}> taken out (no script or HTML in an SVG)" : $"<{name}> taken out");
            return null;
        }
        if (++count > MostElements)
        {
            throw new SvgException($"It has more than {MostElements:N0} elements.");
        }
        if (depth > MostDepth)
        {
            throw new SvgException($"Its elements are nested more than {MostDepth} deep.");
        }
        if (name is "animate" or "animateTransform" or "animateMotion" or "set")
        {
            animated = true;
            // An animation of a link or of a handler is script by another name.
            var target = (string?)e.Attribute("attributeName") ?? "";
            if (target.Contains("href", StringComparison.OrdinalIgnoreCase) || target.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                changed.Add($"an animation of {target} taken out");
                return null;
            }
        }
        var copy = new XElement(Svg + name);
        foreach (var a in e.Attributes())
        {
            if (a.IsNamespaceDeclaration)
            {
                continue;
            }
            if (Attribute(name, a, changed) is { } kept)
            {
                copy.Add(kept);
            }
        }
        foreach (var n in e.Nodes())
        {
            switch (n)
            {
                case XElement child:
                    if (Unwrapped.Contains(child.Name.LocalName) && (child.Name.Namespace == Svg || child.Name.Namespace == XNamespace.None))
                    {
                        changed.Add("<a> links taken out (their drawing kept)");
                        foreach (var inner in child.Elements())
                        {
                            if (Walk(inner, depth + 1, changed, ref count, ref animated) is { } k)
                            {
                                copy.Add(k);
                            }
                        }
                    }
                    else if (Walk(child, depth + 1, changed, ref count, ref animated) is { } k)
                    {
                        copy.Add(k);
                    }
                    break;
                case XCData data when name == "style":
                    copy.Add(new XText(Css(data.Value, changed)));
                    break;
                // Space between elements is no part of the drawing (inside text it is).
                case XText t when string.IsNullOrWhiteSpace(t.Value) && name is not ("text" or "tspan" or "textPath"):
                    break;
                case XText t:
                    copy.Add(new XText(name == "style" ? Css(t.Value, changed) : t.Value));
                    break;
            }
        }
        return copy;
    }

    private static XAttribute? Attribute(string element, XAttribute a, List<string> changed)
    {
        var ns = a.Name.Namespace;
        var local = a.Name.LocalName;
        if (ns != XNamespace.None && ns != XLink && ns != XNamespace.Xml)
        {
            // An editor's own (Inkscape, Illustrator...): of no use to draw it.
            return null;
        }
        if (local.StartsWith("on", StringComparison.OrdinalIgnoreCase))
        {
            changed.Add("event handlers taken out");
            return null;
        }
        var value = a.Value;
        if (Scheme().IsMatch(value))
        {
            changed.Add("a script address taken out");
            return null;
        }
        if (local == "href")
        {
            if (value.StartsWith('#'))
            {
                return new XAttribute(ns == XLink ? XLink + "href" : "href", value);
            }
            if (element == "image" && Picture().IsMatch(value))
            {
                return new XAttribute(ns == XLink ? XLink + "href" : "href", value);
            }
            changed.Add("references to outside the SVG taken out");
            return null;
        }
        if (local == "style")
        {
            var css = Css(value, changed);
            return css.Trim().Length == 0 ? null : new XAttribute("style", css);
        }
        if (value.Contains("url(", StringComparison.OrdinalIgnoreCase))
        {
            value = Urls().Replace(value, m => Inside(m.Groups[1].Value) ? m.Value : Changed(changed, "none"));
        }
        if (ns == XNamespace.Xml)
        {
            return local == "space" ? new XAttribute(XNamespace.Xml + "space", value) : null;
        }
        if (Numeric.Contains(local))
        {
            value = Decimals().Replace(value, m => Math.Round(double.Parse(m.Value, CultureInfo.InvariantCulture), 3).ToString("0.###", CultureInfo.InvariantCulture));
        }
        return new XAttribute(local, value);
    }

    private static string Changed(List<string> changed, string with)
    {
        changed.Add("references to outside the SVG taken out");
        return with;
    }

    /// <summary>A url() of its own: an element of the SVG, or a picture or font inside it.</summary>
    private static bool Inside(string url)
    {
        var u = url.Trim().Trim('"', '\'').Trim();
        return u.StartsWith('#') || DataUrl().IsMatch(u);
    }

    /// <summary>A style sheet or a style attribute, without what reaches outside or runs: @import, outside url()s, old IE's expressions and bindings.</summary>
    public static string Css(string css, List<string> changed)
    {
        var before = css;
        css = Import().Replace(css, "");
        css = Urls().Replace(css, m => Inside(m.Groups[1].Value) ? m.Value : "none");
        css = Expression().Replace(css, "");
        if (css != before)
        {
            changed.Add("references to outside the SVG taken out of its styles");
        }
        return css;
    }

    /// <summary>
    /// Its size: the viewBox's width and height. One it lacks is made from its width and height (in pixels, or plain
    /// numbers), and those set from it when they are missing or in percent, so it draws at its size and scales.
    /// </summary>
    private static void Size(XElement root, List<string> changed, out double width, out double height)
    {
        static double? Length(string? v) =>
            v is null || v.EndsWith('%') ? null : double.TryParse(Unit().Replace(v.Trim(), ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;
        var box = ((string?)root.Attribute("viewBox"))?.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
        if (box is not { Length: 4 } || box.Any(double.IsNaN) || box[2] <= 0 || box[3] <= 0)
        {
            var (w, h) = (Length((string?)root.Attribute("width")) ?? 512, Length((string?)root.Attribute("height")) ?? 512);
            root.SetAttributeValue("viewBox", string.Create(CultureInfo.InvariantCulture, $"0 0 {w:0.###} {h:0.###}"));
            changed.Add("a viewBox added");
            box = [0, 0, w, h];
        }
        (width, height) = (box[2], box[3]);
        if (Length((string?)root.Attribute("width")) is null || Length((string?)root.Attribute("height")) is null)
        {
            root.SetAttributeValue("width", width.ToString("0.###", CultureInfo.InvariantCulture));
            root.SetAttributeValue("height", height.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    [GeneratedRegex(@"</?([A-Za-z_][\w.-]*):[A-Za-z_]|[\s""']([A-Za-z_][\w.-]*):[A-Za-z_][\w.-]*\s*=")]
    private static partial Regex Prefixes();

    [GeneratedRegex(@"&([a-zA-Z][a-zA-Z0-9]*);|&#[0-9]+;|&#x[0-9a-fA-F]+;")]
    private static partial Regex Entity();

    [GeneratedRegex(@"^\s*(java|vb)\s*script\s*:|^\s*data\s*:\s*text/html", RegexOptions.IgnoreCase)]
    private static partial Regex Scheme();

    [GeneratedRegex(@"^data:image/(png|jpeg|jpg|gif|webp);base64,[A-Za-z0-9+/=\s]+$", RegexOptions.IgnoreCase)]
    private static partial Regex Picture();

    [GeneratedRegex(@"^data:(image/(png|jpeg|jpg|gif|webp)|font/(woff2?|ttf|otf)|application/(font-woff2?|x-font-ttf));base64,", RegexOptions.IgnoreCase)]
    private static partial Regex DataUrl();

    [GeneratedRegex(@"url\(\s*([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex Urls();

    [GeneratedRegex(@"@import[^;]*;?", RegexOptions.IgnoreCase)]
    private static partial Regex Import();

    [GeneratedRegex(@"expression\s*\(|behavior\s*:|-moz-binding\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex Expression();

    [GeneratedRegex(@"-?\d*\.\d{4,}")]
    private static partial Regex Decimals();

    [GeneratedRegex(@"px$", RegexOptions.IgnoreCase)]
    private static partial Regex Unit();

    [GeneratedRegex(@"@keyframes|animation\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex Keyframes();
}
