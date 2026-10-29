using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Llm.Api.Insights;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Data files as a data source: CSV, Excel and JSON uploaded, each a table of the source's own (typed from its values, its
/// header kept), asked like PostgreSQL with the same checks; replaced, removed, and gone with the source.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class DataFilesTests(AppFixture app)
{
    /// <summary>A workbook of one sheet: a header, a date column (formatted as a date), numbers and text.</summary>
    internal static byte[] Workbook()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
            const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Add("xl/workbook.xml", $"""<workbook xmlns="{Main}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Q4" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Add("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
            Add("xl/styles.xml", $"""<styleSheet xmlns="{Main}"><numFmts count="1"><numFmt numFmtId="164" formatCode="dd/mm/yyyy"/></numFmts><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="164"/></cellXfs></styleSheet>""");
            Add("xl/sharedStrings.xml", $"""<sst xmlns="{Main}"><si><t>Region</t></si><si><t>Due</t></si><si><t>Target</t></si><si><t>North</t></si><si><t>South</t></si></sst>""");
            // 46296 is 2026-10-01 as Excel counts days.
            Add("xl/worksheets/sheet1.xml", $"""
                <worksheet xmlns="{Main}"><sheetData>
                <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c></row>
                <row r="2"><c r="A2" t="s"><v>3</v></c><c r="B2" s="1"><v>46296</v></c><c r="C2"><v>1500</v></c></row>
                <row r="3"><c r="A3" t="s"><v>4</v></c><c r="B3" s="1"><v>46297</v></c><c r="C3"><v>1000.25</v></c></row>
                </sheetData></worksheet>
                """);
        }
        return ms.ToArray();
    }

    private static MultipartFormDataContent Files(params (string Name, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, bytes) in files)
        {
            form.Add(new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "files", name);
        }
        return form;
    }

    [Fact]
    public async Task Files_become_tables_people_ask_and_go_with_their_source()
    {
        await using var f = DataEngineTests.NewApp(app);
        var admin = await KnowledgeTests.AdminAsync(f);
        var (ana, _) = await KnowledgeTests.PersonAsync(f, admin, "filesana" + Guid.NewGuid().ToString("N")[..4]);
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/data-sources", new { name = "Sales files", kind = "files", reach = "company" }));
        var id = made.GetProperty("id").GetGuid();
        var schema = FilesData.SchemaOf(id);
        Assert.Equal(("files", "uploaded files", 0), (made.GetProperty("kind").GetString(), made.GetProperty("where").GetString(), made.GetProperty("tables").GetArrayLength()));

        var csv = "Region;Month;Revenue (EUR);Closed;Zip\nNorth;2026-09-01;1200.50;yes;01234\n\"South; East\";2026-10-01;980;no;04567\n"u8.ToArray();
        var json = """{"data": [{"id": 1, "name": "Ana", "tags": ["a", "b"]}, {"id": 2, "name": "Ben"}]}"""u8.ToArray();
        var upload = await admin.Http.PostAsync(new Uri($"/api/admin/data-sources/{id}/files", UriKind.Relative), Files(("sales.csv", csv), ("targets.xlsx", Workbook()), ("people.json", json)));
        Assert.True(upload.IsSuccessStatusCode, await upload.Content.ReadAsStringAsync());
        var uploaded = await admin.JsonAsync(upload);
        Assert.Equal([("people", 2), ("sales", 2), ("targets", 2)], uploaded.GetProperty("imported").EnumerateArray().Select(i => (i.GetProperty("table").GetString(), i.GetProperty("rows").GetInt32())).Order());
        var tables = uploaded.GetProperty("source").GetProperty("schema").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t);
        Assert.Equal([$"{schema}.people", $"{schema}.sales", $"{schema}.targets"], tables.Keys.Order(StringComparer.Ordinal));
        var sales = tables[$"{schema}.sales"];
        Assert.Equal("sales.csv", sales.GetProperty("comment").GetString());
        Assert.Equal([("region", "text"), ("month", "date"), ("revenue_eur", "numeric"), ("closed", "boolean"), ("zip", "text")],
            sales.GetProperty("columns").EnumerateArray().Select(c => (c.GetProperty("name").GetString(), c.GetProperty("type").GetString())));
        Assert.Contains(sales.GetProperty("columns").EnumerateArray(), c => c.GetProperty("comment").GetString() == "Revenue (EUR)");
        Assert.Equal([("region", "text"), ("due", "date"), ("target", "numeric")],
            tables[$"{schema}.targets"].GetProperty("columns").EnumerateArray().Select(c => (c.GetProperty("name").GetString(), c.GetProperty("type").GetString())));

        async Task<HttpResponseMessage> Run(string sql) => await ana.PostAsync("/api/data/run", new { sourceId = id, sql });
        var joined = await ana.JsonAsync(await Run($"""select s.region, s.revenue_eur, t.target from {schema}.sales s left join {schema}.targets t on t.region = s.region order by 1"""));
        Assert.Equal([("North", 1200.50m, 1500m), ("South; East", 980m, (decimal?)null)],
            joined.GetProperty("rows").EnumerateArray().Select(r => (r[0].GetString(), r[1].GetDecimal(), r[2].ValueKind == JsonValueKind.Null ? (decimal?)null : r[2].GetDecimal())));
        var due = await ana.JsonAsync(await Run($"select due, zip from {schema}.targets, {schema}.sales where targets.region = 'North' and sales.region = 'North'"));
        Assert.Equal(("2026-10-01", "01234"), (due.GetProperty("rows")[0][0].GetString(), due.GetProperty("rows")[0][1].GetString()));
        var people = await ana.JsonAsync(await Run($"select name, tags from {schema}.people order by id"));
        Assert.Equal("[\"a\", \"b\"]", people.GetProperty("rows")[0][1].GetString());
        // The same checks as any PostgreSQL database: nothing but its tables.
        var settings = await Run("select count(*) from public.settings");
        await StatusAssert.Is(HttpStatusCode.BadRequest, settings);
        Assert.Contains("settings", await settings.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A file uploaded again replaces its table; a table removed goes.
        await admin.Http.PostAsync(new Uri($"/api/admin/data-sources/{id}/files", UriKind.Relative), Files(("sales.csv", "Region,Revenue\nWest,10\n"u8.ToArray())));
        Assert.Equal(1, (await ana.JsonAsync(await Run($"select count(*) from {schema}.sales"))).GetProperty("rows")[0][0].GetInt32());
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/data-sources/{id}/files/people", UriKind.Relative)));
        var source = (await admin.JsonAsync(await admin.GetAsync("/api/admin/data-sources"))).EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == id);
        Assert.Equal([$"{schema}.sales", $"{schema}.targets"], source.GetProperty("tables").EnumerateArray().Select(t => t.GetString()!).Order(StringComparer.Ordinal));
        Assert.Contains("save it as .xlsx or .csv", await (await admin.Http.PostAsync(new Uri($"/api/admin/data-sources/{id}/files", UriKind.Relative), Files(("old.xls", [1, 2, 3])))).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The model is told PostgreSQL, with the files' tables.
        await ana.JsonAsync(await ana.PostAsync("/api/data/ask", new { sourceId = id, question = $"Revenue? [sql:select sum(revenue) from {schema}.sales]" }));
        var system = app.Model.Requests.Last(r => r.Body["messages"]![0]!["content"]!.GetValue<string>().Contains(schema, StringComparison.Ordinal)).Body["messages"]![0]!["content"]!.GetValue<string>();
        Assert.StartsWith("You answer questions about a PostgreSQL database", system, StringComparison.Ordinal);

        // Removing the source removes its schema and its role.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/data-sources/{id}", UriKind.Relative)));
        await using var conn = new NpgsqlConnection(app.ConnectionStringFor("postgres"));
        await conn.OpenAsync();
        await using var gone = new NpgsqlCommand($"select (select count(*) from pg_roles where rolname = '{schema}')", conn);
        Assert.Equal(0L, (long)(await gone.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData("a,b\n1,2\n", "a|b", "1|2")]
    [InlineData("a\tb\n\"x\ty\"\t2\n", "a|b", "x\ty|2")]
    [InlineData("a;b\r\n\"say \"\"hi\"\"\";\r\n", "a|b", "say \"hi\"|")]
    public void Delimited_files_are_read_by_their_own_delimiter(string text, string header, string row)
    {
        var sheet = Assert.Single(TabularFiles.Read("x.csv", Encoding.UTF8.GetBytes(text)));
        Assert.Equal(header, string.Join('|', sheet.Header));
        Assert.Equal(row, string.Join('|', Assert.Single(sheet.Rows).Select(v => v as string ?? "")));
    }

    [Fact]
    public void A_workbook_keeps_its_dates_and_numbers()
    {
        var sheet = Assert.Single(TabularFiles.Read("targets.xlsx", Workbook()));
        Assert.Equal(["Region", "Due", "Target"], sheet.Header);
        Assert.Equal(new object?[] { "North", new DateTime(2026, 10, 1), 1500d }, sheet.Rows[0]);
    }

    [Theory]
    [InlineData("Revenue (EUR)", "revenue_eur")]
    [InlineData("2026 total", "n_2026_total")]
    [InlineData("فروش ماهانه", "فروش_ماهانه")]
    [InlineData("  ", "column_1")]
    public void Names_are_ones_PostgreSQL_takes(string header, string name) => Assert.Equal(name, FilesData.Safe(header, "column_1"));
}
