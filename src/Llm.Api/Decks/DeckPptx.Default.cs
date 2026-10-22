using System.Globalization;
using System.Text;

namespace Llm.Api.Decks;

public static partial class DeckPptx
{
    private const long W = 12192000;
    private const long H = 6858000;

    /// <summary>The built-in look, as a template like any other: white, the app's blue, Calibri; a title, content, title-only, two-column and section layout.</summary>
    private static Dictionary<string, byte[]> Default()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["[Content_Types].xml"] = ContentTypes,
            ["_rels/.rels"] = Rels(("rId1", "officeDocument", "ppt/presentation.xml"), ("rId2", "extended-properties", "docProps/app.xml")),
            ["docProps/app.xml"] = """<Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"><Application>Argus Arena</Application></Properties>""",
            ["ppt/presentation.xml"] = Presentation,
            ["ppt/_rels/presentation.xml.rels"] = Rels(("rId1", "slideMaster", "slideMasters/slideMaster1.xml"), ("rId2", "notesMaster", "notesMasters/notesMaster1.xml"),
                ("rId3", "theme", "theme/theme1.xml"), ("rId4", "presProps", "presProps.xml"), ("rId5", "viewProps", "viewProps.xml"), ("rId6", "tableStyles", "tableStyles.xml")),
            ["ppt/presProps.xml"] = $"""<p:presentationPr xmlns:a="{A}" xmlns:r="{R}" xmlns:p="{P}"/>""",
            ["ppt/viewProps.xml"] = $"""<p:viewPr xmlns:a="{A}" xmlns:r="{R}" xmlns:p="{P}"><p:normalViewPr><p:restoredLeft sz="15620"/><p:restoredTop sz="94660"/></p:normalViewPr><p:gridSpacing cx="76200" cy="76200"/></p:viewPr>""",
            ["ppt/tableStyles.xml"] = $$"""<a:tblStyleLst xmlns:a="{{A}}" def="{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}"/>""",
            ["ppt/theme/theme1.xml"] = ThemeXml("Argus Arena"),
            ["ppt/theme/theme2.xml"] = ThemeXml("Argus Arena notes"),
            ["ppt/slideMasters/slideMaster1.xml"] = Master,
            ["ppt/slideMasters/_rels/slideMaster1.xml.rels"] = Rels(("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"), ("rId2", "slideLayout", "../slideLayouts/slideLayout2.xml"),
                ("rId3", "slideLayout", "../slideLayouts/slideLayout3.xml"), ("rId4", "slideLayout", "../slideLayouts/slideLayout4.xml"), ("rId5", "slideLayout", "../slideLayouts/slideLayout5.xml"),
                ("rId6", "theme", "../theme/theme1.xml")),
            ["ppt/notesMasters/notesMaster1.xml"] = NotesMaster,
            ["ppt/notesMasters/_rels/notesMaster1.xml.rels"] = Rels(("rId1", "theme", "../theme/theme2.xml")),
        };
        var layouts = new[]
        {
            LayoutXml("title", "Title Slide",
                Ph(2, "<p:ph type=\"ctrTitle\"/>", 731520, 2057400, 10728960, 1645920, "b", "<a:lvl1pPr><a:defRPr sz=\"4800\"/></a:lvl1pPr>")
                + Ph(3, "<p:ph type=\"subTitle\" idx=\"1\"/>", 731520, 3840480, 10728960, 1097280, "t",
                    "<a:lvl1pPr marL=\"0\" indent=\"0\"><a:buNone/><a:defRPr sz=\"2400\"><a:solidFill><a:schemeClr val=\"tx2\"/></a:solidFill></a:defRPr></a:lvl1pPr>")
                + Bar(731520, 3749040, 1463040, 45720)),
            LayoutXml("obj", "Title and Content", Ph(2, "<p:ph type=\"title\"/>", 731520, 342900, 10728960, 1097280, "b", null) + Ph(3, "<p:ph idx=\"1\"/>", 731520, 1714500, 10728960, 4663440, "t", null)),
            LayoutXml("titleOnly", "Title Only", Ph(2, "<p:ph type=\"title\"/>", 731520, 342900, 10728960, 1097280, "b", null)),
            LayoutXml("twoObj", "Two Content", Ph(2, "<p:ph type=\"title\"/>", 731520, 342900, 10728960, 1097280, "b", null)
                + Ph(3, "<p:ph sz=\"half\" idx=\"1\"/>", 731520, 1714500, 5273040, 4663440, "t", null) + Ph(4, "<p:ph sz=\"half\" idx=\"2\"/>", 6187440, 1714500, 5273040, 4663440, "t", null)),
            LayoutXml("secHead", "Section Header", Ph(2, "<p:ph type=\"title\"/>", 731520, 2286000, 10728960, 1371600, "b", "<a:lvl1pPr><a:defRPr sz=\"4400\"/></a:lvl1pPr>")
                + Ph(3, "<p:ph type=\"body\" idx=\"1\"/>", 731520, 3840480, 10728960, 914400, "t",
                    "<a:lvl1pPr marL=\"0\" indent=\"0\"><a:buNone/><a:defRPr sz=\"2000\"><a:solidFill><a:schemeClr val=\"tx2\"/></a:solidFill></a:defRPr></a:lvl1pPr>")
                + Bar(731520, 3749040, 1463040, 45720)),
        };
        for (var i = 0; i < layouts.Length; i++)
        {
            files[$"ppt/slideLayouts/slideLayout{i + 1}.xml"] = layouts[i];
            files[$"ppt/slideLayouts/_rels/slideLayout{i + 1}.xml.rels"] = Rels(("rId1", "slideMaster", "../slideMasters/slideMaster1.xml"));
        }
        return files.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value.Trim()), StringComparer.Ordinal);
    }

    private static string Rels(params (string Id, string Type, string Target)[] rels)
    {
        var w = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        foreach (var (id, type, target) in rels)
        {
            w.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"{id}\" Type=\"{RelBase}{type}\" Target=\"{target}\"/>");
        }
        return w.Append("</Relationships>").ToString();
    }

    private static readonly string ContentTypes = $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/ppt/presentation.xml" ContentType="{CtBase}presentationml.presentation.main+xml"/><Override PartName="/ppt/slideMasters/slideMaster1.xml" ContentType="{CtBase}presentationml.slideMaster+xml"/>{string.Concat(Enumerable.Range(1, 5).Select(i => $"<Override PartName=\"/ppt/slideLayouts/slideLayout{i}.xml\" ContentType=\"{CtBase}presentationml.slideLayout+xml\"/>"))}<Override PartName="/ppt/notesMasters/notesMaster1.xml" ContentType="{CtBase}presentationml.notesMaster+xml"/><Override PartName="/ppt/theme/theme1.xml" ContentType="{CtBase}theme+xml"/><Override PartName="/ppt/theme/theme2.xml" ContentType="{CtBase}theme+xml"/><Override PartName="/ppt/presProps.xml" ContentType="{CtBase}presentationml.presProps+xml"/><Override PartName="/ppt/viewProps.xml" ContentType="{CtBase}presentationml.viewProps+xml"/><Override PartName="/ppt/tableStyles.xml" ContentType="{CtBase}presentationml.tableStyles+xml"/><Override PartName="/docProps/app.xml" ContentType="{CtBase}extended-properties+xml"/></Types>
        """;

    private static readonly string Presentation = $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <p:presentation xmlns:a="{A}" xmlns:r="{R}" xmlns:p="{P}" saveSubsetFonts="1"><p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rId1"/></p:sldMasterIdLst><p:notesMasterIdLst><p:notesMasterId r:id="rId2"/></p:notesMasterIdLst><p:sldSz cx="{W}" cy="{H}"/><p:notesSz cx="6858000" cy="9144000"/></p:presentation>
        """;

    private static string Group => "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    private static string Ph(int id, string ph, long x, long y, long cx, long cy, string anchor, string? levels) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Placeholder {id}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr>{ph}</p:nvPr></p:nvSpPr>" +
        $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>" +
        $"<p:txBody><a:bodyPr vert=\"horz\" lIns=\"0\" rIns=\"0\" anchor=\"{anchor}\"><a:normAutofit/></a:bodyPr><a:lstStyle>{levels}</a:lstStyle><a:p><a:endParaRPr lang=\"en-US\"/></a:p></p:txBody></p:sp>";

    /// <summary>A short bar in the accent colour: the built-in look's one ornament.</summary>
    private static string Bar(long x, long y, long cx, long cy) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"9\" name=\"Accent\"/><p:cNvSpPr/><p:nvPr userDrawn=\"1\"/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm>" +
        "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:solidFill><a:schemeClr val=\"accent1\"/></a:solidFill><a:ln><a:noFill/></a:ln></p:spPr></p:sp>";

    private static string LayoutXml(string type, string name, string shapes) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <p:sldLayout xmlns:a="{A}" xmlns:r="{R}" xmlns:p="{P}" type="{type}" preserve="1"><p:cSld name="{name}"><p:spTree>{Group}{shapes}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>
        """;

    private const string ClrMap = "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>";

    private static string Level(int n, long left, int size) =>
        $"<a:lvl{n}pPr marL=\"{left}\" indent=\"-285750\" algn=\"l\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\"><a:lnSpc><a:spcPct val=\"100000\"/></a:lnSpc><a:spcBef><a:spcPts val=\"1000\"/></a:spcBef>" +
        $"<a:buClr><a:schemeClr val=\"accent1\"/></a:buClr><a:buFont typeface=\"Arial\"/><a:buChar char=\"{(n % 2 == 1 ? "•" : "–")}\"/><a:defRPr sz=\"{size}\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl{n}pPr>";

    private static readonly string Master = $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <p:sldMaster xmlns:a="{A}" xmlns:r="{R}" xmlns:p="{P}"><p:cSld><p:bg><p:bgRef idx="1001"><a:schemeClr val="bg1"/></p:bgRef></p:bg><p:spTree>{Group}{Ph(2, "<p:ph type=\"title\"/>", 731520, 342900, 10728960, 1097280, "b", null)}{Ph(3, "<p:ph type=\"body\" idx=\"1\"/>", 731520, 1714500, 10728960, 4663440, "t", null)}<p:sp><p:nvSpPr><p:cNvPr id="7" name="Accent"/><p:cNvSpPr/><p:nvPr userDrawn="1"/></p:nvSpPr><p:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{W}" cy="73152"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:solidFill><a:schemeClr val="accent1"/></a:solidFill><a:ln><a:noFill/></a:ln></p:spPr></p:sp></p:spTree></p:cSld>{ClrMap}<p:sldLayoutIdLst>{string.Concat(Enumerable.Range(1, 5).Select(i => $"<p:sldLayoutId id=\"{2147483648L + i}\" r:id=\"rId{i}\"/>"))}</p:sldLayoutIdLst><p:txStyles><p:titleStyle><a:lvl1pPr algn="l" rtl="0" eaLnBrk="1" latinLnBrk="0" hangingPunct="1"><a:lnSpc><a:spcPct val="90000"/></a:lnSpc><a:spcBef><a:spcPct val="0"/></a:spcBef><a:buNone/><a:defRPr sz="3600" b="1" kern="1200"><a:solidFill><a:schemeClr val="tx2"/></a:solidFill><a:latin typeface="+mj-lt"/><a:ea typeface="+mj-ea"/><a:cs typeface="+mj-cs"/></a:defRPr></a:lvl1pPr></p:titleStyle><p:bodyStyle>{Level(1, 285750, 2200)}{Level(2, 685800, 2000)}{Level(3, 1085850, 1800)}</p:bodyStyle><p:otherStyle><a:defPPr><a:defRPr lang="en-US"/></a:defPPr><a:lvl1pPr marL="0" algn="l" defTabSz="914400" rtl="0" eaLnBrk="1" latinLnBrk="0" hangingPunct="1"><a:defRPr sz="1800" kern="1200"><a:solidFill><a:schemeClr val="tx1"/></a:solidFill><a:latin typeface="+mn-lt"/><a:ea typeface="+mn-ea"/><a:cs typeface="+mn-cs"/></a:defRPr></a:lvl1pPr></p:otherStyle></p:txStyles></p:sldMaster>
        """;

    private static readonly string NotesMaster = $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <p:notesMaster xmlns:a="{A}" xmlns:r="{R}" xmlns:p="{P}"><p:cSld><p:bg><p:bgRef idx="1001"><a:schemeClr val="bg1"/></p:bgRef></p:bg><p:spTree>{Group}<p:sp><p:nvSpPr><p:cNvPr id="2" name="Slide Image"/><p:cNvSpPr><a:spLocks noGrp="1" noRot="1" noChangeAspect="1"/></p:cNvSpPr><p:nvPr><p:ph type="sldImg" idx="2"/></p:nvPr></p:nvSpPr><p:spPr><a:xfrm><a:off x="685800" y="1143000"/><a:ext cx="5486400" cy="3086100"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:noFill/><a:ln w="12700"><a:solidFill><a:prstClr val="black"/></a:solidFill></a:ln></p:spPr></p:sp><p:sp><p:nvSpPr><p:cNvPr id="3" name="Notes"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr><p:ph type="body" sz="quarter" idx="1"/></p:nvPr></p:nvSpPr><p:spPr><a:xfrm><a:off x="685800" y="4400550"/><a:ext cx="5486400" cy="3600450"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr><p:txBody><a:bodyPr vert="horz" lIns="91440" tIns="45720" rIns="91440" bIns="45720"/><a:lstStyle/><a:p><a:endParaRPr lang="en-US"/></a:p></p:txBody></p:sp></p:spTree></p:cSld>{ClrMap}<p:notesStyle><a:lvl1pPr marL="0" algn="l" defTabSz="914400" rtl="0" eaLnBrk="1" latinLnBrk="0" hangingPunct="1"><a:defRPr sz="1200" kern="1200"><a:solidFill><a:schemeClr val="tx1"/></a:solidFill><a:latin typeface="+mn-lt"/><a:ea typeface="+mn-ea"/><a:cs typeface="+mn-cs"/></a:defRPr></a:lvl1pPr></p:notesStyle></p:notesMaster>
        """;

    private static string Fill(string color) => $"<a:solidFill><a:schemeClr val=\"{color}\"/></a:solidFill>";

    private static string ThemeXml(string name)
    {
        static string C(string slot, string hex) => $"<a:{slot}><a:srgbClr val=\"{hex}\"/></a:{slot}>";
        var line = new StringBuilder();
        foreach (var w in new[] { 6350, 12700, 19050 })
        {
            line.Append(CultureInfo.InvariantCulture, $"<a:ln w=\"{w.ToString(CultureInfo.InvariantCulture)}\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\">{Fill("phClr")}<a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln>");
        }
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <a:theme xmlns:a="{A}" name="{name}"><a:themeElements><a:clrScheme name="Argus Arena"><a:dk1><a:srgbClr val="1B1C1F"/></a:dk1><a:lt1><a:srgbClr val="FFFFFF"/></a:lt1>{C("dk2", "1E3A5F")}{C("lt2", "EEF1F5")}{C("accent1", "2A78D6")}{C("accent2", "EB6834")}{C("accent3", "1BAF7A")}{C("accent4", "EDA100")}{C("accent5", "E87BA4")}{C("accent6", "4A3AA7")}{C("hlink", "2A78D6")}{C("folHlink", "4A3AA7")}</a:clrScheme><a:fontScheme name="Argus Arena"><a:majorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont><a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont></a:fontScheme><a:fmtScheme name="Argus Arena"><a:fillStyleLst>{Fill("phClr")}{Fill("phClr")}{Fill("phClr")}</a:fillStyleLst><a:lnStyleLst>{line}</a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst>{Fill("phClr")}{Fill("phClr")}{Fill("phClr")}</a:bgFillStyleLst></a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>
            """;
    }
}
