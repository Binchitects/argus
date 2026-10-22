using System.Globalization;
using System.Text;
using Llm.Api.Chat;

namespace Llm.Api.Decks;

public static partial class DeckPptx
{
    /// <summary>One slide's XML and its relationships: shapes placed on the slide's size, words in the layout's placeholders.</summary>
    private sealed class SlideWriter(Package package, int number, long width, long height)
    {
        private readonly StringBuilder _shapes = new();
        private int _shape = 1;
        public List<(string Id, string Type, string Target)> Relations { get; } = [];

        public string Relate(string type, string target)
        {
            var id = $"rId{Relations.Count + 1}";
            Relations.Add((id, type, target));
            return id;
        }

        private long X(double f) => (long)(width * f);
        private long Y(double f) => (long)(height * f);

        private static string Xfrm(long x, long y, long cx, long cy, string prefix = "a") =>
            $"<{prefix}:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></{prefix}:xfrm>";

        /// <summary>A placeholder of the layout (title, ctrTitle, subTitle, body…) at a place of ours, with its lines.</summary>
        private void Placeholder(string ph, IEnumerable<string> lines, double x, double y, double w, double h)
        {
            var id = ++_shape;
            var paragraphs = string.Concat(lines.Select(l => Para(l, null)));
            _shapes.Append(CultureInfo.InvariantCulture, $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Placeholder {id}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr>{ph}</p:nvPr></p:nvSpPr>")
                .Append(CultureInfo.InvariantCulture, $"<p:spPr>{Xfrm(X(x), Y(y), X(w), Y(h))}</p:spPr>")
                .Append(CultureInfo.InvariantCulture, $"<p:txBody><a:bodyPr><a:normAutofit/></a:bodyPr><a:lstStyle/>{(paragraphs.Length > 0 ? paragraphs : Para("", null))}</p:txBody></p:sp>");
        }

        /// <summary>A text box of our own: a quote, a caption, a takeaway.</summary>
        private void TextBox(IEnumerable<string> lines, double x, double y, double w, double h, int size, bool italic = false, bool bold = false, string align = "l",
            string color = "tx1", string anchor = "t")
        {
            var id = ++_shape;
            var rPr = $"<a:rPr lang=\"en-US\" sz=\"{size * 100}\"{(italic ? " i=\"1\"" : "")}{(bold ? " b=\"1\"" : "")} dirty=\"0\"><a:solidFill><a:schemeClr val=\"{color}\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:cs typeface=\"+mn-cs\"/></a:rPr>";
            var paragraphs = string.Concat(lines.Select(l => Para(l, rPr, align)));
            _shapes.Append(CultureInfo.InvariantCulture, $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Text {id}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr>")
                .Append(CultureInfo.InvariantCulture, $"<p:spPr>{Xfrm(X(x), Y(y), X(w), Y(h))}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></p:spPr>")
                .Append(CultureInfo.InvariantCulture, $"<p:txBody><a:bodyPr wrap=\"square\" anchor=\"{anchor}\"><a:normAutofit/></a:bodyPr><a:lstStyle/>{paragraphs}</p:txBody></p:sp>");
        }

        /// <summary>A picture fitted inside the box (its shape kept), centred.</summary>
        private void Picture(DeckImage image, double x, double y, double w, double h)
        {
            var path = package.AddImage(image);
            var rel = Relate("image", "../media/" + Path.GetFileName(path));
            var (pw, ph) = Size(image.Data) ?? (4, 3);
            double boxW = X(w), boxH = Y(h);
            var scale = Math.Min(boxW / pw, boxH / ph);
            long cx = (long)(pw * scale), cy = (long)(ph * scale);
            long ox = X(x) + (long)((boxW - cx) / 2), oy = Y(y) + (long)((boxH - cy) / 2);
            var id = ++_shape;
            _shapes.Append(CultureInfo.InvariantCulture, $"<p:pic><p:nvPicPr><p:cNvPr id=\"{id}\" name=\"Picture {id}\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>")
                .Append(CultureInfo.InvariantCulture, $"<p:blipFill><a:blip r:embed=\"{rel}\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>")
                .Append(CultureInfo.InvariantCulture, $"<p:spPr>{Xfrm(ox, oy, cx, cy)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>");
        }

        /// <summary>A chart of PowerPoint's own, its numbers in it.</summary>
        private void Chart(DeckChart chart, double x, double y, double w, double h)
        {
            var path = package.AddChart(ChartXml(chart));
            var rel = Relate("chart", "../charts/" + Path.GetFileName(path));
            var id = ++_shape;
            _shapes.Append(CultureInfo.InvariantCulture, $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id}\" name=\"Chart {id}\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>")
                .Append(Xfrm(X(x), Y(y), X(w), Y(h), "p"))
                .Append(CultureInfo.InvariantCulture, $"<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"{rel}\"/></a:graphicData></a:graphic></p:graphicFrame>");
        }

        private const double M = 0.06;
        private const double Wide = 1 - 2 * M;

        public string Slide(DeckSlide s, IReadOnlyDictionary<Guid, DeckImage> images)
        {
            DeckImage? picture = s.Picture?.FileId is { } file && images.TryGetValue(file, out var found) ? found : null;
            var title = s.Title ?? "";
            switch (s.Layout)
            {
                case "title":
                    Placeholder("<p:ph type=\"ctrTitle\"/>", [title], M, 0.30, Wide, 0.24);
                    Placeholder("<p:ph type=\"subTitle\" idx=\"1\"/>", [s.Subtitle ?? ""], M, 0.56, Wide, 0.16);
                    break;
                case "section":
                    Placeholder("<p:ph type=\"title\"/>", [title], M, 0.34, Wide, 0.2);
                    Placeholder("<p:ph type=\"body\" idx=\"1\"/>", [s.Subtitle ?? ""], M, 0.56, Wide, 0.14);
                    break;
                case "two":
                    Placeholder("<p:ph type=\"title\"/>", [title], M, 0.05, Wide, 0.16);
                    Placeholder("<p:ph sz=\"half\" idx=\"1\"/>", s.Left ?? [], M, 0.25, Wide / 2 - 0.02, 0.68);
                    Placeholder("<p:ph sz=\"half\" idx=\"2\"/>", s.Right ?? [], M + Wide / 2 + 0.02, 0.25, Wide / 2 - 0.02, 0.68);
                    break;
                case "chart":
                    Placeholder("<p:ph type=\"title\"/>", [title], M, 0.05, Wide, 0.16);
                    var takeaway = s.Bullets ?? [];
                    var chartWidth = takeaway.Count > 0 ? Wide * 0.66 : Wide;
                    if (s.Chart is { Categories.Count: > 0, Series.Count: > 0 } chart)
                    {
                        Chart(chart, M, 0.24, chartWidth, 0.7);
                    }
                    else
                    {
                        TextBox([s.Chart?.Note ?? "No numbers yet."], M, 0.4, chartWidth, 0.2, 18, italic: true, color: "tx2", align: "ctr", anchor: "ctr");
                    }
                    if (takeaway.Count > 0)
                    {
                        TextBox(takeaway, M + chartWidth + 0.02, 0.28, Wide - chartWidth - 0.02, 0.6, 18, anchor: "ctr");
                    }
                    break;
                case "picture":
                    Placeholder("<p:ph type=\"title\"/>", [title], M, 0.05, Wide, 0.16);
                    if (picture is not null)
                    {
                        Picture(picture, M, 0.24, Wide, s.Subtitle is null ? 0.7 : 0.6);
                    }
                    if (s.Subtitle is { } caption)
                    {
                        TextBox([caption], M, 0.86, Wide, 0.08, 14, color: "tx2", align: "ctr");
                    }
                    break;
                case "quote":
                    TextBox([$"“{s.Quote ?? title}”"], M + 0.04, 0.2, Wide - 0.08, 0.48, 32, italic: true, align: "ctr", anchor: "ctr");
                    if (s.By is { } by)
                    {
                        TextBox([$"— {by}"], M + 0.04, 0.7, Wide - 0.08, 0.1, 18, color: "tx2", align: "ctr");
                    }
                    break;
                default:
                    Placeholder("<p:ph type=\"title\"/>", [title], M, 0.05, Wide, 0.16);
                    var textWidth = picture is null ? Wide : Wide * 0.56;
                    Placeholder("<p:ph idx=\"1\"/>", s.Bullets ?? [], M, 0.25, textWidth, 0.68);
                    if (picture is not null)
                    {
                        Picture(picture, M + textWidth + 0.03, 0.25, Wide - textWidth - 0.03, 0.68);
                    }
                    break;
            }
            return $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:sld xmlns:a="{A}" xmlns:r="{R}" xmlns:p="{P}"><p:cSld name="Slide {number}"><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>{_shapes}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>
                """;
        }
    }

    /// <summary>A picture's width and height from its header (PNG, JPEG, GIF), or null.</summary>
    public static (double W, double H)? Size(byte[] b)
    {
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
        {
            return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
        }
        if (b.Length > 10 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F')
        {
            return (b[6] | (b[7] << 8), b[8] | (b[9] << 8));
        }
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < b.Length && b[i] == 0xFF)
            {
                var marker = b[i + 1];
                var length = (b[i + 2] << 8) | b[i + 3];
                // A start of frame (not DHT, JPG or DAC): its height then its width.
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                }
                i += 2 + length;
            }
        }
        return null;
    }

    /// <summary>A bar or line chart, its numbers as literals (no workbook): the theme's accent colours, a legend for more than one series.</summary>
    internal static string ChartXml(DeckChart chart)
    {
        static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        var line = chart.Type == "line";
        var categories = new StringBuilder($"<c:cat><c:strLit><c:ptCount val=\"{chart.Categories.Count}\"/>");
        for (var i = 0; i < chart.Categories.Count; i++)
        {
            categories.Append(CultureInfo.InvariantCulture, $"<c:pt idx=\"{i}\"><c:v>{CanvasDocx.Esc(chart.Categories[i])}</c:v></c:pt>");
        }
        categories.Append("</c:strLit></c:cat>");
        var series = new StringBuilder();
        for (var s = 0; s < chart.Series.Count; s++)
        {
            var ser = chart.Series[s];
            var values = new StringBuilder($"<c:val><c:numLit><c:formatCode>General</c:formatCode><c:ptCount val=\"{chart.Categories.Count}\"/>");
            for (var i = 0; i < ser.Values.Count && i < chart.Categories.Count; i++)
            {
                if (ser.Values[i] is { } v)
                {
                    values.Append(CultureInfo.InvariantCulture, $"<c:pt idx=\"{i}\"><c:v>{N(v)}</c:v></c:pt>");
                }
            }
            values.Append("</c:numLit></c:val>");
            var color = $"<a:schemeClr val=\"accent{s % 6 + 1}\"/>";
            series.Append(CultureInfo.InvariantCulture, $"<c:ser><c:idx val=\"{s}\"/><c:order val=\"{s}\"/><c:tx><c:v>{CanvasDocx.Esc(ser.Name)}</c:v></c:tx>")
                .Append(line
                    ? $"<c:spPr><a:ln w=\"28575\" cap=\"rnd\"><a:solidFill>{color}</a:solidFill><a:round/></a:ln></c:spPr><c:marker><c:symbol val=\"none\"/></c:marker>"
                    : $"<c:spPr><a:solidFill>{color}</a:solidFill></c:spPr><c:invertIfNegative val=\"0\"/>")
                .Append(categories).Append(values)
                .Append(line ? "<c:smooth val=\"0\"/></c:ser>" : "</c:ser>");
        }
        var plot = line
            ? $"<c:lineChart><c:grouping val=\"standard\"/><c:varyColors val=\"0\"/>{series}<c:marker val=\"1\"/><c:axId val=\"1001\"/><c:axId val=\"1002\"/></c:lineChart>"
            : $"<c:barChart><c:barDir val=\"col\"/><c:grouping val=\"clustered\"/><c:varyColors val=\"0\"/>{series}<c:gapWidth val=\"80\"/><c:axId val=\"1001\"/><c:axId val=\"1002\"/></c:barChart>";
        const string Gray = "<c:spPr><a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"tx1\"><a:lumMod val=\"15000\"/><a:lumOff val=\"85000\"/></a:schemeClr></a:solidFill></a:ln></c:spPr>";
        var legend = chart.Series.Count > 1 ? "<c:legend><c:legendPos val=\"b\"/><c:overlay val=\"0\"/></c:legend>" : "";
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <c:chartSpace xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart" xmlns:a="{A}" xmlns:r="{R}"><c:roundedCorners val="0"/><c:chart><c:autoTitleDeleted val="1"/><c:plotArea><c:layout/>{plot}<c:catAx><c:axId val="1001"/><c:scaling><c:orientation val="minMax"/></c:scaling><c:delete val="0"/><c:axPos val="b"/><c:numFmt formatCode="General" sourceLinked="0"/><c:majorTickMark val="none"/><c:minorTickMark val="none"/><c:tickLblPos val="nextTo"/>{Gray}<c:crossAx val="1002"/><c:crosses val="autoZero"/><c:auto val="1"/><c:lblAlgn val="ctr"/><c:lblOffset val="100"/><c:noMultiLvlLbl val="0"/></c:catAx><c:valAx><c:axId val="1002"/><c:scaling><c:orientation val="minMax"/></c:scaling><c:delete val="0"/><c:axPos val="l"/><c:majorGridlines>{Gray}</c:majorGridlines><c:numFmt formatCode="General" sourceLinked="0"/><c:majorTickMark val="none"/><c:minorTickMark val="none"/><c:tickLblPos val="nextTo"/><c:spPr><a:ln><a:noFill/></a:ln></c:spPr><c:crossAx val="1001"/><c:crosses val="autoZero"/><c:crossBetween val="between"/></c:valAx></c:plotArea>{legend}<c:plotVisOnly val="1"/><c:dispBlanksAs val="gap"/></c:chart><c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz="1400"><a:solidFill><a:schemeClr val="tx1"><a:lumMod val="75000"/><a:lumOff val="25000"/></a:schemeClr></a:solidFill><a:latin typeface="+mn-lt"/></a:defRPr></a:pPr><a:endParaRPr lang="en-US"/></a:p></c:txPr></c:chartSpace>
            """;
    }
}
