using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Llm.Api.Chat;

namespace Llm.Api.Decks;

/// <summary>A picture to put on a slide: its bytes and type (image/png, image/jpeg or image/gif).</summary>
public sealed record DeckImage(byte[] Data, string ContentType);

/// <summary>Why a template cannot be used.</summary>
public sealed class DeckTemplateException(string message) : Exception(message);

/// <summary>
/// A deck as a PowerPoint file, built here (the OOXML parts zipped, no library): in the company's template, or the
/// built-in one. The template's own slides go; each of ours is made on its layout of that kind (a title slide on its title
/// layout, points on "Title and Content"), its words in the layout's placeholders so they take the template's fonts and
/// colours; charts are PowerPoint's own (their numbers in them), pictures fitted, speaker notes kept when the template
/// has a notes master.
/// </summary>
public static partial class DeckPptx
{
    internal static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    internal static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    internal static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string RelBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private const string CtBase = "application/vnd.openxmlformats-officedocument.";

    public static byte[] Build(string title, List<DeckSlide> slides, IReadOnlyDictionary<Guid, DeckImage> images, byte[]? template, DateTimeOffset at)
    {
        var package = new Package(template is null ? Default() : Unzip(template));
        package.RemoveSlides();
        var layouts = package.Layouts();
        var (width, height) = package.SlideSize();
        var notesMaster = package.Target("ppt/presentation.xml", "notesMaster");
        var number = 0;
        foreach (var slide in slides)
        {
            number++;
            var layout = Choose(layouts, slide.Layout);
            var writer = new SlideWriter(package, number, width, height);
            var xml = writer.Slide(slide, images);
            var path = $"ppt/slides/slide{number}.xml";
            package.Set(path, xml);
            package.Override(path, CtBase + "presentationml.slide+xml");
            writer.Relate("slideLayout", "../slideLayouts/" + Path.GetFileName(layout.Path));
            if (notesMaster is not null && slide.Notes is { Length: > 0 } notes)
            {
                var notesPath = $"ppt/notesSlides/notesSlide{number}.xml";
                package.Set(notesPath, Notes(notes));
                package.Override(notesPath, CtBase + "presentationml.notesSlide+xml");
                package.SetRels(notesPath, [("rId1", "notesMaster", "../notesMasters/" + Path.GetFileName(notesMaster)), ("rId2", "slide", $"../slides/slide{number}.xml")]);
                writer.Relate("notesSlide", $"../notesSlides/notesSlide{number}.xml");
            }
            package.SetRels(path, writer.Relations);
            package.AddSlide($"slides/slide{number}.xml");
        }
        package.SetTitle(title, at);
        return package.Zip();
    }

    /// <summary>The template's theme for the browser: its colours and fonts, and the slide's size.</summary>
    public static string Theme(byte[] template)
    {
        var package = new Package(Unzip(template));
        var master = package.Target("ppt/presentation.xml", "slideMaster") ?? throw new DeckTemplateException("The file has no slide master: is it a PowerPoint template?");
        var themePath = package.Target(master, "theme");
        var colors = new System.Text.Json.Nodes.JsonObject();
        var fonts = new System.Text.Json.Nodes.JsonObject();
        if (themePath is not null && package.Xml(themePath) is { } theme)
        {
            foreach (var c in theme.Descendants(A + "clrScheme").FirstOrDefault()?.Elements() ?? [])
            {
                var value = c.Element(A + "srgbClr")?.Attribute("val")?.Value ?? c.Element(A + "sysClr")?.Attribute("lastClr")?.Value;
                if (value is { Length: 6 })
                {
                    colors[c.Name.LocalName] = "#" + value;
                }
            }
            fonts["major"] = theme.Descendants(A + "majorFont").FirstOrDefault()?.Element(A + "latin")?.Attribute("typeface")?.Value;
            fonts["minor"] = theme.Descendants(A + "minorFont").FirstOrDefault()?.Element(A + "latin")?.Attribute("typeface")?.Value;
        }
        var (w, h) = package.SlideSize();
        return new System.Text.Json.Nodes.JsonObject { ["colors"] = colors, ["fonts"] = fonts, ["width"] = w, ["height"] = h, ["layouts"] = package.Layouts().Count }.ToJsonString();
    }

    private static Dictionary<string, byte[]> Unzip(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long total = 0;
            foreach (var e in zip.Entries.Where(e => e.FullName.Length > 0 && !e.FullName.EndsWith('/')))
            {
                total += e.Length;
                // A zip that unpacks to far more than a template does is refused (a zip bomb).
                if (total > 200L * 1024 * 1024)
                {
                    throw new DeckTemplateException("The template unpacks to more than 200 MB.");
                }
                using var s = e.Open();
                using var m = new MemoryStream();
                s.CopyTo(m);
                parts[e.FullName.TrimStart('/')] = m.ToArray();
            }
            if (!parts.ContainsKey("[Content_Types].xml") || !parts.ContainsKey("ppt/presentation.xml"))
            {
                throw new DeckTemplateException("That is not a PowerPoint file (.pptx or .potx).");
            }
            return parts;
        }
        catch (InvalidDataException)
        {
            throw new DeckTemplateException("That is not a PowerPoint file (.pptx or .potx): it does not open as one.");
        }
    }

    /// <summary>A layout of the template: its part and its kind (title, obj, titleOnly, twoObj, secHead, blank…).</summary>
    private sealed record Layout(string Path, string Type, string Name);

    /// <summary>The template's layout for a slide of ours: the kind that fits, else "Title and Content", else its first.</summary>
    private static Layout Choose(List<Layout> layouts, string layout)
    {
        string[] wanted = layout switch
        {
            "title" => ["title"],
            "section" => ["secHead", "title"],
            "two" => ["twoObj", "twoTxTwoObj", "obj"],
            "chart" or "picture" => ["titleOnly", "obj"],
            "quote" => ["titleOnly", "blank", "obj"],
            _ => ["obj", "tx"],
        };
        foreach (var w in wanted)
        {
            if (layouts.FirstOrDefault(l => l.Type == w) is { } found)
            {
                return found;
            }
        }
        return layouts.FirstOrDefault(l => l.Type == "obj") ?? layouts[0];
    }

    private static string Notes(string notes)
    {
        var paragraphs = string.Concat(notes.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => Para(l.Trim(), null)));
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:notes xmlns:a="{A}" xmlns:r="{R}" xmlns:p="{P}"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>
            <p:sp><p:nvSpPr><p:cNvPr id="2" name="Slide Image"/><p:cNvSpPr><a:spLocks noGrp="1" noRot="1" noChangeAspect="1"/></p:cNvSpPr><p:nvPr><p:ph type="sldImg"/></p:nvPr></p:nvSpPr><p:spPr/></p:sp>
            <p:sp><p:nvSpPr><p:cNvPr id="3" name="Notes"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>{paragraphs}</p:txBody></p:sp>
            </p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>
            """;
    }

    /// <summary>Whether a line reads right to left: its first letter Hebrew, Arabic or Persian.</summary>
    private static bool RightToLeft(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= 'ﻼ')
            {
                return true;
            }
            if (char.IsLetter(c))
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>A paragraph of one run, aligned as asked (l, ctr, r); right to left when it reads so.</summary>
    internal static string Para(string text, string? runProperties, string? alignment = null)
    {
        var rtl = RightToLeft(text);
        var attributes = (rtl ? " rtl=\"1\"" : "") + (alignment is not null ? $" algn=\"{alignment}\"" : rtl ? " algn=\"r\"" : "");
        var pPr = attributes.Length > 0 ? $"<a:pPr{attributes}/>" : "";
        var rPr = runProperties ?? "<a:rPr lang=\"en-US\" dirty=\"0\"/>";
        return text.Length == 0 ? $"<a:p>{pPr}<a:endParaRPr lang=\"en-US\" dirty=\"0\"/></a:p>" : $"<a:p>{pPr}<a:r>{rPr}<a:t>{CanvasDocx.Esc(text)}</a:t></a:r></a:p>";
    }

    /// <summary>The parts of a PowerPoint file, and what relates them.</summary>
    private sealed class Package(Dictionary<string, byte[]> parts)
    {
        private readonly Dictionary<string, byte[]> _parts = parts;
        private readonly Dictionary<string, XDocument> _xml = new(StringComparer.Ordinal);

        public XDocument? Xml(string path)
        {
            if (_xml.TryGetValue(path, out var x))
            {
                return x;
            }
            if (!_parts.TryGetValue(path, out var bytes))
            {
                return null;
            }
            x = XDocument.Load(new MemoryStream(bytes));
            _xml[path] = x;
            return x;
        }

        public void Set(string path, string xml)
        {
            _xml.Remove(path);
            _parts[path] = Encoding.UTF8.GetBytes(xml.TrimStart());
        }

        public void SetBytes(string path, byte[] data)
        {
            _xml.Remove(path);
            _parts[path] = data;
        }

        public bool Has(string path) => _parts.ContainsKey(path);

        private void Remove(string path)
        {
            _parts.Remove(path);
            _xml.Remove(path);
            _parts.Remove(RelsOf(path));
            _xml.Remove(RelsOf(path));
            Types.Root!.Elements(Ct + "Override").Where(o => (string?)o.Attribute("PartName") == "/" + path).Remove();
        }

        private static string RelsOf(string path) => $"{Path.GetDirectoryName(path)!.Replace('\\', '/')}/_rels/{Path.GetFileName(path)}.rels";

        private XDocument Types => Xml("[Content_Types].xml")!;

        public void Override(string path, string type)
        {
            Types.Root!.Elements(Ct + "Override").Where(o => (string?)o.Attribute("PartName") == "/" + path).Remove();
            Types.Root.Add(new XElement(Ct + "Override", new XAttribute("PartName", "/" + path), new XAttribute("ContentType", type)));
        }

        public void Default(string extension, string type)
        {
            if (!Types.Root!.Elements(Ct + "Default").Any(d => string.Equals((string?)d.Attribute("Extension"), extension, StringComparison.OrdinalIgnoreCase)))
            {
                Types.Root.AddFirst(new XElement(Ct + "Default", new XAttribute("Extension", extension), new XAttribute("ContentType", type)));
            }
        }

        /// <summary>A part's relationships: id, type (its last word: slide, slideLayout…) and the part it points at.</summary>
        public List<(string Id, string Type, string Target)> Rels(string path)
        {
            var rels = Xml(RelsOf(path));
            return rels?.Root?.Elements(Rel + "Relationship")
                .Where(r => (string?)r.Attribute("TargetMode") != "External")
                .Select(r => ((string)r.Attribute("Id")!, ((string)r.Attribute("Type")!).Split('/')[^1], Resolve(path, (string)r.Attribute("Target")!)))
                .ToList() ?? [];
        }

        public void SetRels(string path, IEnumerable<(string Id, string Type, string Target)> rels) =>
            Set(RelsOf(path), new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(Rel + "Relationships",
                rels.Select(r => new XElement(Rel + "Relationship", new XAttribute("Id", r.Id), new XAttribute("Type", RelBase + r.Type), new XAttribute("Target", r.Target))))).ToString(SaveOptions.DisableFormatting));

        /// <summary>The first part this one relates to by that type, or null.</summary>
        public string? Target(string path, string type) => Rels(path).FirstOrDefault(r => r.Type == type).Target;

        private static string Resolve(string from, string target)
        {
            if (target.StartsWith('/'))
            {
                return target.TrimStart('/');
            }
            var folder = (Path.GetDirectoryName(from) ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach (var part in target.Split('/'))
            {
                if (part == "..")
                {
                    if (folder.Count > 0)
                    {
                        folder.RemoveAt(folder.Count - 1);
                    }
                }
                else if (part is not ("." or ""))
                {
                    folder.Add(part);
                }
            }
            return string.Join('/', folder);
        }

        /// <summary>The template's own slides out: their parts, notes and charts, their place in the presentation, custom shows and sections.</summary>
        public void RemoveSlides()
        {
            var presentation = Xml("ppt/presentation.xml")!;
            var rels = Xml(RelsOf("ppt/presentation.xml"));
            foreach (var (id, _, target) in Rels("ppt/presentation.xml").Where(r => r.Type == "slide"))
            {
                foreach (var part in Rels(target).Where(r => r.Type is "notesSlide" or "chart").Select(r => r.Target))
                {
                    foreach (var embedded in Rels(part).Where(r => r.Type is "package" or "chartUserShapes" or "chartStyle" or "chartColorStyle").Select(r => r.Target))
                    {
                        Remove(embedded);
                    }
                    Remove(part);
                }
                Remove(target);
                rels?.Root?.Elements(Rel + "Relationship").Where(r => (string?)r.Attribute("Id") == id).Remove();
            }
            presentation.Root!.Element(P + "sldIdLst")?.Remove();
            presentation.Root.Element(P + "custShowLst")?.Remove();
            presentation.Root.Element(P + "extLst")?.Elements().Where(e => e.Elements().Any(x => x.Name.LocalName == "sectionLst")).Remove();
        }

        public List<Layout> Layouts()
        {
            var master = Target("ppt/presentation.xml", "slideMaster") ?? throw new DeckTemplateException("The file has no slide master: is it a PowerPoint template?");
            var layouts = new List<Layout>();
            foreach (var path in Rels(master).Where(r => r.Type == "slideLayout").Select(r => r.Target).Where(Has))
            {
                var root = Xml(path)!.Root!;
                layouts.Add(new Layout(path, (string?)root.Attribute("type") ?? "cust", (string?)root.Element(P + "cSld")?.Attribute("name") ?? ""));
            }
            return layouts.Count > 0 ? layouts : throw new DeckTemplateException("The template's slide master has no layouts.");
        }

        public (long Width, long Height) SlideSize()
        {
            var size = Xml("ppt/presentation.xml")!.Root!.Element(P + "sldSz");
            return (long.TryParse((string?)size?.Attribute("cx"), out var cx) ? cx : 12192000, long.TryParse((string?)size?.Attribute("cy"), out var cy) ? cy : 6858000);
        }

        private int _nextRel = 1;

        /// <summary>A slide of ours into the presentation's list, after its masters and before its size.</summary>
        public void AddSlide(string target)
        {
            var presentation = Xml("ppt/presentation.xml")!;
            var relsPath = RelsOf("ppt/presentation.xml");
            var rels = Xml(relsPath)!;
            string id;
            do
            {
                id = $"rIdDeck{_nextRel++}";
            }
            while (rels.Root!.Elements(Rel + "Relationship").Any(r => (string?)r.Attribute("Id") == id));
            rels.Root.Add(new XElement(Rel + "Relationship", new XAttribute("Id", id), new XAttribute("Type", RelBase + "slide"), new XAttribute("Target", target)));
            var list = presentation.Root!.Element(P + "sldIdLst");
            if (list is null)
            {
                list = new XElement(P + "sldIdLst");
                var after = presentation.Root.Element(P + "handoutMasterIdLst") ?? presentation.Root.Element(P + "notesMasterIdLst") ?? presentation.Root.Element(P + "sldMasterIdLst");
                if (after is not null)
                {
                    after.AddAfterSelf(list);
                }
                else
                {
                    presentation.Root.AddFirst(list);
                }
            }
            var next = list.Elements(P + "sldId").Select(e => (long)e.Attribute("id")!).DefaultIfEmpty(255).Max() + 1;
            list.Add(new XElement(P + "sldId", new XAttribute("id", next), new XAttribute(R + "id", id)));
        }

        public void SetTitle(string title, DateTimeOffset at)
        {
            var at8601 = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            Set("docProps/core.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><dc:title>{CanvasDocx.Esc(title)}</dc:title><dc:creator>Argus Arena</dc:creator><dcterms:created xsi:type="dcterms:W3CDTF">{at8601}</dcterms:created><dcterms:modified xsi:type="dcterms:W3CDTF">{at8601}</dcterms:modified></cp:coreProperties>
                """);
            Override("docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml");
            var root = Xml("_rels/.rels")!;
            if (!root.Root!.Elements(Rel + "Relationship").Any(r => ((string?)r.Attribute("Type"))?.EndsWith("/core-properties", StringComparison.Ordinal) == true))
            {
                root.Root.Add(new XElement(Rel + "Relationship", new XAttribute("Id", "rIdDeckCore"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties"), new XAttribute("Target", "docProps/core.xml")));
            }
        }

        public byte[] Zip()
        {
            foreach (var (path, x) in _xml)
            {
                using var m = new MemoryStream();
                using (var w = System.Xml.XmlWriter.Create(m, new System.Xml.XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
                {
                    x.Save(w);
                }
                _parts[path] = m.ToArray();
            }
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                // The content types first, as readers expect.
                foreach (var (path, bytes) in _parts.OrderBy(p => p.Key == "[Content_Types].xml" ? 0 : 1).ThenBy(p => p.Key, StringComparer.Ordinal))
                {
                    var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                    using var s = entry.Open();
                    s.Write(bytes);
                }
            }
            return stream.ToArray();
        }

        private int _media;
        private int _charts;

        public string AddImage(DeckImage image)
        {
            var extension = image.ContentType switch { "image/jpeg" => "jpeg", "image/gif" => "gif", _ => "png" };
            Default(extension, image.ContentType);
            string path;
            do
            {
                path = $"ppt/media/deck{++_media}.{extension}";
            }
            while (Has(path));
            SetBytes(path, image.Data);
            return path;
        }

        public string AddChart(string xml)
        {
            string path;
            do
            {
                path = $"ppt/charts/deckChart{++_charts}.xml";
            }
            while (Has(path));
            Set(path, xml);
            Override(path, CtBase + "drawingml.chart+xml");
            return path;
        }
    }
}
