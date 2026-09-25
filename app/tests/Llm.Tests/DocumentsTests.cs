using System.IO.Compression;
using System.Text;
using Llm.Api.Chat;

namespace Llm.Tests;

/// <summary>Office documents read as text: what the model gets from each kind, and what is refused.</summary>
public sealed class DocumentsTests
{
    private static byte[] Zip(params (string Path, string Content)[] parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in parts)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    private const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";

    private static string Extract(string name, byte[] bytes) => Attachments.Extract(name, "application/octet-stream", bytes, 200_000).Text;

    [Fact]
    public void A_word_document_keeps_headings_lists_tables_and_footnotes()
    {
        var docx = Zip(
            ("[Content_Types].xml", "<Types/>"),
            ("word/document.xml", $"""
                <w:document {W}><w:body>
                  <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Quarterly plan</w:t></w:r></w:p>
                  <w:p><w:r><w:t xml:space="preserve">Revenue grew </w:t></w:r><w:r><w:t>12%</w:t></w:r><w:del><w:r><w:delText>deleted words</w:delText></w:r></w:del></w:p>
                  <w:p><w:pPr><w:numPr><w:ilvl w:val="0"/></w:numPr></w:pPr><w:r><w:t>Hire two engineers</w:t></w:r></w:p>
                  <w:tbl>
                    <w:tr><w:tc><w:p><w:r><w:t>Team</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Budget</w:t></w:r></w:p></w:tc></w:tr>
                    <w:tr><w:tc><w:p><w:r><w:t>Platform</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>$1.2M</w:t></w:r></w:p></w:tc></w:tr>
                  </w:tbl>
                  <w:p><w:r><w:t>Line one</w:t><w:br/><w:t>line two</w:t><w:tab/><w:t>tabbed</w:t></w:r></w:p>
                </w:body></w:document>
                """),
            ("word/footnotes.xml", $"<w:footnotes {W}><w:footnote><w:p><w:r><w:t>Source: finance</w:t></w:r></w:p></w:footnote></w:footnotes>"));
        var text = Extract("plan.docx", docx);
        Assert.Contains("# Quarterly plan", text, StringComparison.Ordinal);
        Assert.Contains("Revenue grew 12%", text, StringComparison.Ordinal);
        Assert.DoesNotContain("deleted words", text, StringComparison.Ordinal);
        Assert.Contains("- Hire two engineers", text, StringComparison.Ordinal);
        Assert.Contains("| Team | Budget |\n| Platform | $1.2M |", text, StringComparison.Ordinal);
        Assert.Contains("Line one\nline two\ttabbed", text, StringComparison.Ordinal);
        Assert.Contains("--- footnotes ---\nSource: finance", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_workbook_is_csv_per_sheet_with_shared_strings_and_gaps()
    {
        const string S = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        var xlsx = Zip(
            ("xl/workbook.xml", $"<workbook {S}><sheets><sheet name=\"Sales\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Notes\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Target=\"/xl/worksheets/sheet2.xml\"/></Relationships>"),
            ("xl/sharedStrings.xml", $"<sst {S}><si><t>Region</t></si><si><t>Total</t></si><si><r><t>North, </t></r><r><t>East</t></r><rPh><t>ignored</t></rPh></si></sst>"),
            ("xl/worksheets/sheet1.xml", $"""
                <worksheet {S}><sheetData>
                  <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>
                  <row r="2"><c r="A2" t="s"><v>2</v></c><c r="C2"><f>SUM(1,2)</f><v>1500.5</v></c><c r="D2" t="b"><v>1</v></c></row>
                </sheetData></worksheet>
                """),
            ("xl/worksheets/sheet2.xml", $"<worksheet {S}><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Checked by \"Ann\"</t></is></c></row></sheetData></worksheet>"));
        var text = Extract("sales.xlsx", xlsx);
        Assert.Contains("## Sheet: Sales\nRegion,Total\n\"North, East\",,1500.5,TRUE", text, StringComparison.Ordinal);
        Assert.Contains("## Sheet: Notes\n\"Checked by \"\"Ann\"\"\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SUM(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ignored", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_presentation_is_its_slides_in_order()
    {
        const string A = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
        static string Slide(string a, params string[] lines) => $"<p:sld xmlns:p=\"p\" {a}><a:txBody>{string.Concat(lines.Select(l => $"<a:p><a:r><a:t>{l}</a:t></a:r></a:p>"))}</a:txBody></p:sld>";
        var pptx = Zip(("ppt/presentation.xml", "<p/>"), ("ppt/slides/slide10.xml", Slide(A, "Ten")), ("ppt/slides/slide2.xml", Slide(A, "Roadmap", "Q3: ship")), ("ppt/slides/slide1.xml", Slide(A, "Welcome")));
        Assert.Equal("--- slide 1 ---\nWelcome\n--- slide 2 ---\nRoadmap\nQ3: ship\n--- slide 3 ---\nTen", Extract("deck.pptx", pptx));
    }

    [Fact]
    public void Open_document_text_and_spreadsheets_read_too()
    {
        const string O = "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\"";
        var odt = Zip(("mimetype", "application/vnd.oasis.opendocument.text"), ("content.xml", $"""
            <doc {O}><text:h text:outline-level="2">Minutes</text:h><text:p>Agreed<text:s text:c="2"/>to <text:span>ship</text:span>.</text:p>
            <table:table><table:table-row><table:table-cell><text:p>Owner</text:p></table:table-cell><table:table-cell><text:p>Ann</text:p></table:table-cell></table:table-row></table:table></doc>
            """));
        Assert.Equal("## Minutes\nAgreed  to ship.\n| Owner | Ann |", Extract("minutes.odt", odt));

        var ods = Zip(("mimetype", "application/vnd.oasis.opendocument.spreadsheet"), ("content.xml", $"""
            <doc {O}><table:table table:name="Stock">
              <table:table-row><table:table-cell><text:p>Item</text:p></table:table-cell><table:table-cell><text:p>Qty</text:p></table:table-cell><table:table-cell table:number-columns-repeated="1020"/></table:table-row>
              <table:table-row table:number-rows-repeated="2"><table:table-cell><text:p>Bolt</text:p></table:table-cell><table:table-cell><text:p>7</text:p></table:table-cell></table:table-row>
              <table:table-row table:number-rows-repeated="1048000"><table:table-cell table:number-columns-repeated="1024"/></table:table-row>
            </table:table></doc>
            """));
        Assert.Equal("## Sheet: Stock\nItem,Qty\nBolt,7\nBolt,7", Extract("stock.ods", ods));
    }

    [Fact]
    public void Rtf_keeps_its_words_and_drops_its_formatting()
    {
        var rtf = Encoding.Latin1.GetBytes(@"{\rtf1\ansi{\fonttbl{\f0 Arial;}}{\colortbl;\red0\green0\blue0;}{\*\generator Word;}\f0\fs24 Caf\'e9 menu\par {\b Soup}\tab 4\u8364?\par}");
        Assert.Equal("Café menu\nSoup\t4€", Extract("menu.rtf", rtf));
    }

    [Fact]
    public void Old_office_files_other_archives_and_damaged_documents_are_refused_with_a_reason()
    {
        byte[] ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0];
        Assert.Contains("Save it as .docx", Assert.Throws<AttachmentException>(() => Extract("old.doc", ole)).Message, StringComparison.Ordinal);
        Assert.Contains("not a file the chat can read", Assert.Throws<AttachmentException>(() => Extract("photos.zip", Zip(("a.bin", "\0\0")))).Message, StringComparison.Ordinal);
        Assert.Contains("damaged", Assert.Throws<AttachmentException>(() => Extract("broken.docx", Zip(("word/document.xml", "<w:document")))).Message, StringComparison.Ordinal);
        Assert.Contains("no text", Assert.Throws<AttachmentException>(() => Extract("empty.docx", Zip(("word/document.xml", $"<w:document {W}/>")))).Message, StringComparison.Ordinal);
        // A DTD (the way into entity expansion) is not processed.
        Assert.Contains("damaged", Assert.Throws<AttachmentException>(() => Extract("dtd.docx", Zip(("word/document.xml", "<!DOCTYPE d [<!ENTITY a \"aaaa\">]><d>&a;</d>")))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_document_is_cut_and_says_so()
    {
        var body = string.Concat(Enumerable.Range(0, 5000).Select(i => $"<w:p><w:r><w:t>Paragraph {i} with some words in it.</w:t></w:r></w:p>"));
        var (text, truncated, converted) = Attachments.Extract("long.docx", "", Zip(("word/document.xml", $"<w:document {W}><w:body>{body}</w:body></w:document>")), 10_000);
        Assert.True(truncated);
        Assert.True(converted);
        Assert.Equal(10_000, text.Length);
        Assert.False(Attachments.Extract("notes.txt", "text/plain", "plain"u8.ToArray(), 100).Converted);
    }
}
