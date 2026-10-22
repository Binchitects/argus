using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Llm.Api.Decks;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>
/// Decks: made from a person's words by the model, changed by asking (pictures kept), versions, pictures from the picture
/// model, and the PowerPoint file, in the built-in look or a company template.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class DeckTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp(FakeGateway? gateway = null) =>
        app.Create(app.ConnectionStringFor("decks_" + Guid.NewGuid().ToString("N")[..8]), gateway ?? new FakeGateway(), new Dictionary<string, string?>());

    private static async Task<TestBrowser> PersonAsync(TestBrowser admin, WebApplicationFactory<Program> f)
    {
        var name = "k" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", displayName = "Kim" }));
        return await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
    }

    /// <summary>The PowerPoint file's parts, each XML one read (so it is well-formed).</summary>
    private static Dictionary<string, XDocument> Parts(byte[] pptx)
    {
        using var zip = new ZipArchive(new MemoryStream(pptx));
        return zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal) || e.FullName.EndsWith(".rels", StringComparison.Ordinal))
            .ToDictionary(e => e.FullName, e => XDocument.Load(e.Open()));
    }

    [Fact]
    public async Task A_deck_is_made_from_words_changed_by_asking_with_its_pictures_kept_and_put_back_to_a_version()
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel("FLUX.2-klein-4B", null, null, false, false, false, null, null, null, Mode: "image_generation"));
        await using var f = NewApp(gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var kim = await PersonAsync(admin, f);
        var other = await PersonAsync(admin, f);

        Assert.True((await kim.JsonAsync(await kim.GetAsync("/api/decks/config"))).GetProperty("pictures").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await kim.PostAsync("/api/decks", new { about = "" }));
        // Not JSON the first time: asked once more.
        var made = await kim.JsonAsync(await kim.PostAsync("/api/decks", new { about = "Our quarter for the board [nojson]" }));
        var id = made.GetProperty("id").GetGuid();
        Assert.Equal(("Our quarter", 1), (made.GetProperty("title").GetString(), made.GetProperty("version").GetInt32()));
        var slides = made.GetProperty("slides").EnumerateArray().ToList();
        Assert.Equal(["title", "bullets", "chart", "bullets"], slides.Select(s => s.GetProperty("layout").GetString()));
        // Empty points dropped; an unknown layout is points.
        Assert.Equal(["Sales grew", "Two new customers"], slides[1].GetProperty("bullets").EnumerateArray().Select(b => b.GetString()));
        Assert.Equal([120d, 80d], slides[2].GetProperty("chart").GetProperty("series")[0].GetProperty("values").EnumerateArray().Select(v => v.GetDouble()));

        // Its picture, made from its prompt as Kim, once.
        var before = app.Model.ImageRequests.Count;
        var drawn = await kim.JsonAsync(await kim.PostAsync($"/api/decks/{id}/slides/1/picture"));
        var file = drawn.GetProperty("slides")[1].GetProperty("picture").GetProperty("fileId").GetGuid();
        Assert.Equal("A rising arrow over a city", app.Model.ImageRequests.Skip(before).Single()["prompt"]!.GetValue<string>());
        await kim.PostAsync($"/api/decks/{id}/slides/1/picture");
        Assert.Equal(before + 1, app.Model.ImageRequests.Count);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await kim.PostAsync($"/api/decks/{id}/slides/0/picture"));

        // Changed by asking: a slide added; the picture stays though the model dropped its file.
        var changed = await kim.JsonAsync(await kim.PostAsync($"/api/decks/{id}/ask", new { change = "Add a slide on costs at the end", slide = 1 }));
        Assert.Equal(2, changed.GetProperty("version").GetInt32());
        Assert.Equal("Costs", changed.GetProperty("slides")[4].GetProperty("title").GetString());
        Assert.Equal(file, changed.GetProperty("slides")[1].GetProperty("picture").GetProperty("fileId").GetGuid());
        var request = app.Model.Requests.Last(r => r.Body["messages"]![0]!["content"]!.GetValue<string>().StartsWith("You change slide decks", StringComparison.Ordinal));
        Assert.Contains("They are looking at slide 2", request.Body["messages"]![0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("deck", request.Body["metadata"]!["tags"]![0]!.GetValue<string>());

        // By hand: someone else's picture does not stay on a slide.
        var otherFile = (await other.JsonAsync(await other.PostAsync($"/api/decks", new { about = "Theirs" }))).GetProperty("id").GetGuid();
        _ = otherFile;
        var edited = await kim.JsonAsync(await kim.Http.PutAsJsonAsync(new Uri($"/api/decks/{id}", UriKind.Relative), new
        {
            title = "Q3", slides = new object[] { new { layout = "title", title = "Q3" }, new { layout = "picture", title = "Not mine", picture = new { prompt = "x", fileId = Guid.NewGuid() } } },
        }));
        Assert.Equal(("Q3", 3), (edited.GetProperty("title").GetString(), edited.GetProperty("version").GetInt32()));
        Assert.Equal(JsonValueKind.Null, edited.GetProperty("slides")[1].GetProperty("picture").GetProperty("fileId").ValueKind);

        // Every version kept; one put back is the newest.
        var versions = (await kim.JsonAsync(await kim.GetAsync($"/api/decks/{id}/versions"))).EnumerateArray().Select(v => (v.GetProperty("version").GetInt32(), v.GetProperty("note").GetString())).ToList();
        Assert.Equal([(3, "Edited"), (2, "Add a slide on costs at the end"), (1, "Our quarter for the board [nojson]")], versions);
        var restored = await kim.JsonAsync(await kim.PostAsync($"/api/decks/{id}/versions/2/restore"));
        Assert.Equal((4, 5), (restored.GetProperty("version").GetInt32(), restored.GetProperty("slides").GetArrayLength()));

        // Each person's own.
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/decks/{id}"));
        Assert.Single((await other.JsonAsync(await other.GetAsync("/api/decks"))).EnumerateArray());
        await admin.PostAsync("/api/admin/access/bulk", new { people = Array.Empty<Guid>(), steps = Array.Empty<object>(), dryRun = true });
    }

    [Fact]
    public async Task The_PowerPoint_file_has_a_slide_of_each_kind_on_its_layout_with_its_chart_picture_and_notes()
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel("FLUX.2-klein-4B", null, null, false, false, false, null, null, null, Mode: "image_generation"));
        await using var f = NewApp(gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var kim = await PersonAsync(admin, f);
        var id = (await kim.JsonAsync(await kim.PostAsync("/api/decks", new { about = "Our quarter" }))).GetProperty("id").GetGuid();
        await kim.PostAsync($"/api/decks/{id}/slides/1/picture");

        var response = await kim.GetAsync($"/api/decks/{id}/pptx");
        await StatusAssert.Is(HttpStatusCode.OK, response);
        Assert.Equal("Our quarter.pptx", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var pptx = await response.Content.ReadAsByteArrayAsync();
        var parts = Parts(pptx);
        XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        Assert.Equal(4, parts["ppt/presentation.xml"].Root!.Element(p + "sldIdLst")!.Elements().Count());
        // Each slide on the layout of its kind, its title in the title placeholder.
        string LayoutOf(int n) => parts[$"ppt/slideLayouts/{Path.GetFileName(parts[$"ppt/slides/_rels/slide{n}.xml.rels"].Root!.Elements().Single(e => ((string)e.Attribute("Type")!).EndsWith("/slideLayout", StringComparison.Ordinal)).Attribute("Target")!.Value)}"].Root!.Attribute("type")!.Value;
        Assert.Equal(["title", "obj", "titleOnly", "obj"], Enumerable.Range(1, 4).Select(LayoutOf));
        Assert.Contains("Our quarter", parts["ppt/slides/slide1.xml"].ToString(), StringComparison.Ordinal);
        // The chart is PowerPoint's own, with its numbers; the picture is in the file; the notes are kept.
        var chartRel = parts["ppt/slides/_rels/slide3.xml.rels"].Root!.Elements().Single(e => ((string)e.Attribute("Type")!).EndsWith("/chart", StringComparison.Ordinal));
        var chart = parts["ppt/charts/" + Path.GetFileName(chartRel.Attribute("Target")!.Value)].ToString();
        Assert.Contains("<c:v>North</c:v>", chart, StringComparison.Ordinal);
        Assert.Contains("<c:v>120</c:v>", chart, StringComparison.Ordinal);
        using (var zip = new ZipArchive(new MemoryStream(pptx)))
        {
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("ppt/media/", StringComparison.Ordinal) && e.FullName.EndsWith(".png", StringComparison.Ordinal));
        }
        Assert.Contains("Welcome everyone.", parts["ppt/notesSlides/notesSlide1.xml"].ToString(), StringComparison.Ordinal);
        // Every part a relationship points at is in the file, and has its content type.
        var types = parts["[Content_Types].xml"].Root!;
        foreach (var (path, rels) in parts.Where(x => x.Key.EndsWith(".rels", StringComparison.Ordinal)))
        {
            var folder = path.Replace("_rels/", "", StringComparison.Ordinal)[..^".rels".Length];
            foreach (var rel in rels.Root!.Elements().Where(e => (string?)e.Attribute("TargetMode") != "External"))
            {
                var target = rel.Attribute("Target")!.Value;
                var full = target.StartsWith('/') ? target[1..] : Path.GetFullPath(Path.Combine("/" + Path.GetDirectoryName(folder), target)).TrimStart('/');
                using var zip = new ZipArchive(new MemoryStream(pptx));
                Assert.True(zip.GetEntry(full) is not null, $"{path} points at {full}, which is not in the file");
                var extension = Path.GetExtension(full).TrimStart('.');
                Assert.True(types.Elements().Any(t => (string?)t.Attribute("PartName") == "/" + full || string.Equals((string?)t.Attribute("Extension"), extension, StringComparison.OrdinalIgnoreCase)),
                    $"{full} has no content type");
            }
        }
        _ = r;
        // Kept beside the test's output, to open by hand (soffice --convert-to pdf).
        await File.WriteAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "deck-sample.pptx"), pptx);
    }

    [Fact]
    public async Task A_company_template_carries_its_look_its_own_slides_left_out()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var kim = await PersonAsync(admin, f);
        // A template: a PowerPoint file with slides of its own, made by the built-in writer here.
        var template = DeckPptx.Build("Brand", [new DeckSlide { Layout = "title", Title = "Brand" }, new DeckSlide { Layout = "bullets", Title = "Old", Bullets = ["Old slide"] }],
            new Dictionary<Guid, DeckImage>(), null, DateTimeOffset.UtcNow);

        using (var form = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "broken.pptx" } })
        {
            await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PostAsync(new Uri("/api/admin/deck-templates", UriKind.Relative), form));
        }
        JsonElement uploaded;
        using (var form = new MultipartFormDataContent { { new ByteArrayContent(template), "file", "brand.pptx" }, { new StringContent("Our brand"), "name" } })
        {
            uploaded = await admin.JsonAsync(await admin.Http.PostAsync(new Uri("/api/admin/deck-templates", UriKind.Relative), form));
        }
        Assert.Equal(("Our brand", true, "#2A78D6"), (uploaded.GetProperty("name").GetString(), uploaded.GetProperty("isDefault").GetBoolean(),
            uploaded.GetProperty("theme").GetProperty("colors").GetProperty("accent1").GetString()));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await kim.Http.PostAsync(new Uri("/api/admin/deck-templates", UriKind.Relative), new MultipartFormDataContent()));

        // New decks are made in the default template; its own slides are not in the file.
        var deck = await kim.JsonAsync(await kim.PostAsync("/api/decks", new { about = "Our quarter" }));
        Assert.Equal(uploaded.GetProperty("id").GetGuid(), deck.GetProperty("templateId").GetGuid());
        var parts = Parts(await (await kim.GetAsync($"/api/decks/{deck.GetProperty("id").GetGuid()}/pptx")).Content.ReadAsByteArrayAsync());
        Assert.Equal(4, parts.Keys.Count(k => k.StartsWith("ppt/slides/slide", StringComparison.Ordinal)));
        Assert.DoesNotContain(parts.Values, x => x.ToString().Contains("Old slide", StringComparison.Ordinal));
        // The built-in look, chosen instead.
        var built = await kim.JsonAsync(await kim.Http.PutAsJsonAsync(new Uri($"/api/decks/{deck.GetProperty("id").GetGuid()}", UriKind.Relative), new { templateId = Guid.Empty }));
        Assert.Equal(JsonValueKind.Null, built.GetProperty("templateId").ValueKind);
    }

    [Fact]
    public async Task In_a_chat_the_Decks_tool_makes_a_deck_and_answers_with_its_link()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var kim = await PersonAsync(admin, f);
        var chat = (await kim.JsonAsync(await kim.PostAsync("/api/chat/conversations", new { useArgus = false, tools = new[] { "decks" } }))).GetProperty("id").GetGuid();
        var text = await (await kim.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = """Slides please: [call make_deck {"about":"Our quarter for the board"}]""" })).Content.ReadAsStringAsync();
        var result = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)
            .First(e => e.GetProperty("type").GetString() == "tool_result");
        var said = JsonDocument.Parse(result.GetProperty("text").GetString()!).RootElement;
        var deck = (await kim.JsonAsync(await kim.GetAsync("/api/decks"))).EnumerateArray().Single();
        Assert.Equal(($"/decks/{deck.GetProperty("id").GetGuid()}", 4), (said.GetProperty("link").GetString(), said.GetProperty("slides").GetInt32()));
        Assert.Equal("Our quarter", deck.GetProperty("title").GetString());
    }

    [Fact]
    public void The_models_deck_is_read_from_a_fence_and_cleaned()
    {
        var made = DeckMaker.Read("Sure!\n```json\n{\"title\": \"T\", \"slides\": [{\"layout\": \"chart\", \"title\": \"No numbers\"}, {\"layout\": \"picture\"}, {\"layout\": \"quote\", \"quote\": \"Less is more\"}]}\n```")!;
        Assert.Equal("T", made.Title);
        // A chart with nothing to draw and a picture with nothing to show are points instead.
        Assert.Equal(["bullets", "bullets", "quote"], made.Slides.Select(s => s.Layout));
        Assert.Null(DeckMaker.Read("No deck here."));
        Assert.Equal((1d, 1d), DeckPptx.Size(Llm.Tests.FakeModel.Png));
        var chart = new DeckChart { Type = "bar" };
        // As the database gives them: an int, a decimal (numeric), a time.
        DeckMaker.Fill(chart, new Llm.Api.Insights.DataResult([new("Month", "time"), new("Orders", "number"), new("Spent", "number")],
            [new() { "2026-10-01T00:00:00.0000000Z", 3, 120.5m }, new() { "2026-11-01T00:00:00.0000000Z", 5, null }], false, 3));
        Assert.Equal(["2026-10-01", "2026-11-01"], chart.Categories);
        Assert.Equal("Orders", chart.Series[0].Name);
        Assert.Equal([3d, 5d], chart.Series[0].Values);
        Assert.Equal([120.5, null], chart.Series[1].Values);
    }
}
