using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Media;
using Llm.Api.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>The media studio's vector drawings: drawn by the model and fixed, animated and changed as asked, by hand, versions, uploads, exports, tracing.</summary>
[Collection(nameof(AppCollection))]
public sealed class VectorTests(AppFixture app)
{
    /// <summary>The picture server: a red square picture of the size asked, done at the first look.</summary>
    private sealed class FakePictures : HttpMessageHandler
    {
        public readonly ConcurrentQueue<JsonObject> Asked = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            JsonNode answer = new JsonObject { ["data"] = new JsonArray() };
            if (path == "/sdcpp/v1/img_gen")
            {
                Asked.Enqueue(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
                answer = new JsonObject { ["poll_url"] = "/sdcpp/v1/jobs/1" };
            }
            else if (path.StartsWith("/sdcpp/v1/jobs/", StringComparison.Ordinal))
            {
                var px = new byte[64 * 64 * 4];
                Array.Fill(px, (byte)255);
                var png = Convert.ToBase64String(Png.Encode(new Png.Image(64, 64, px)));
                answer = new JsonObject { ["status"] = "completed", ["result"] = new JsonObject { ["images"] = new JsonArray(new JsonObject { ["b64_json"] = png }) } };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>The studio server's segmenter: a map for each thing asked (or, failing, a 503), what was asked kept.</summary>
    private sealed class FakeSegmenter : HttpMessageHandler
    {
        public readonly ConcurrentQueue<string[]> Asked = new();
        public bool Fail { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            JsonNode answer = new JsonObject { ["models"] = new JsonObject { ["segment"] = true } };
            if (request.RequestUri!.AbsolutePath == "/v1/segment")
            {
                var labels = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!["labels"]!.AsArray().Select(l => l!.GetValue<string>()).ToArray();
                Asked.Enqueue(labels);
                if (Fail)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("""{"error": "The segmenting model is not in the library yet."}""") };
                }
                var map = Convert.ToBase64String(Png.Encode(new Png.Image(1, 1, [255, 255, 255, 255])));
                answer = new JsonObject { ["masks"] = new JsonArray([.. labels.Select(_ => (JsonNode)map)]), ["strength"] = new JsonArray([.. labels.Select(_ => (JsonNode)1.5)]) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<JsonElement> DoneAsync(WebApplicationFactory<Program> f, TestBrowser b, object request, string status = "done")
    {
        var res = await b.PostAsync("/api/media/jobs", request);
        Assert.True(res.StatusCode == HttpStatusCode.Accepted, await res.Content.ReadAsStringAsync());
        var id = (await b.JsonAsync(res)).GetProperty("id").GetGuid();
        await f.Services.GetRequiredService<MediaJobs>().Finished(id).WaitAsync(TimeSpan.FromSeconds(60));
        var job = (await b.JsonAsync(await b.GetAsync("/api/media/jobs"))).EnumerateArray().Single(j => j.GetProperty("id").GetGuid() == id);
        Assert.True(job.GetProperty("status").GetString() == status, job.GetProperty("error").ToString());
        return job;
    }

    private static Guid First(JsonElement job) => job.GetProperty("items")[0].GetGuid();

    [Fact]
    public async Task A_drawing_is_drawn_fixed_animated_changed_by_hand_and_by_words_exported_and_kept_in_versions()
    {
        await using var sandbox = new FakeSandbox();
        var pictures = new FakePictures();
        var segmenter = new FakeSegmenter();
        await using var f = app.Create(app.ConnectionStringFor("vector_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Sandbox:Dir"] = sandbox.Dir, ["Modules:imagegen"] = "true" },
            s => s.AddHttpClient(SdServer.Client).ConfigurePrimaryHttpMessageHandler(() => pictures)
                .Services.AddHttpClient(MediaControl.Client).ConfigurePrimaryHttpMessageHandler(() => pictures)
                .Services.AddHttpClient(StudioServer.Client).ConfigurePrimaryHttpMessageHandler(() => segmenter));
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "vd" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var b = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        var asked = (string tag) => app.Model.Requests.Count(r => r.Body["metadata"]?["tags"]?[0]?.GetValue<string>() == tag && r.Body["user"]?.GetValue<string>() == $"{name}@example.test");

        var config = await b.JsonAsync(await b.GetAsync("/api/media/vectors/config"));
        Assert.Contains(config.GetProperty("styles").EnumerateArray(), s => s.GetProperty("name").GetString() == "isometric");
        Assert.Contains(config.GetProperty("kinds").EnumerateArray(), k => k.GetProperty("kind").GetString() == "draw");
        Assert.False(config.GetProperty("moves").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/media/jobs", new { action = "draw" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/media/jobs", new { action = "draw", prompt = "x", style = "baroque" }));

        // Drawn: its first answer not XML, then one without names: each sent back to fix.
        var broken = First(await DoneAsync(f, b, new { action = "draw", prompt = "[broken] a sun over hills", style = "flat", animate = "never" }));
        Assert.Equal(2, asked("svg-draw"));
        var id = First(await DoneAsync(f, b, new { action = "draw", prompt = "[noids] a sun over hills", style = "cute", width = 512, height = 512, animate = "never" }));
        Assert.Equal(4, asked("svg-draw"));
        var fixedUp = app.Model.Requests.Last(r => r.Body["metadata"]?["tags"]?[0]?.GetValue<string>() == "svg-draw").Body["messages"]!.AsArray();
        Assert.True(fixedUp.Last()!["content"]!.ToString().Contains("no part has an id", StringComparison.Ordinal), fixedUp.ToJsonString());
        var drawing = await b.JsonAsync(await b.GetAsync($"/api/media/vectors/{id}"));
        Assert.Contains("id=\"sun\"", drawing.GetProperty("still").GetString(), StringComparison.Ordinal);
        Assert.Equal(("drawn", "cute", 1), (drawing.GetProperty("source").GetString(), drawing.GetProperty("style").GetString(), drawing.GetProperty("version").GetInt32()));
        var item = (await b.JsonAsync(await b.GetAsync("/api/media/items?kind=vector"))).GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetGuid() == id);
        Assert.Equal(("vector", "image/svg+xml", true), (item.GetProperty("kind").GetString(), item.GetProperty("contentType").GetString(), item.GetProperty("thumb").GetBoolean()));
        var file = await b.GetAsync($"/api/media/items/{id}/svg");
        Assert.Equal("image/svg+xml", file.Content.Headers.ContentType?.MediaType);
        Assert.Contains("sandbox", file.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);

        // Drawn and animated in one go: the words' picture drawn, then moving as they say (its first version still).
        var moving = First(await DoneAsync(f, b, new { action = "draw", prompt = "a sun over hills, the sun spins" }));
        var moved = await b.JsonAsync(await b.GetAsync($"/api/media/vectors/{moving}"));
        Assert.Equal((2, "sun", "spin"), (moved.GetProperty("version").GetInt32(), moved.GetProperty("motions")[0].GetProperty("target").GetString(),
            moved.GetProperty("motions")[0].GetProperty("kind").GetString()));
        Assert.Contains("the sun spins", app.Model.Requests.Last(r => r.Body["metadata"]?["tags"]?[0]?.GetValue<string>() == "svg-animate").Body.ToJsonString(), StringComparison.Ordinal);

        // Animated as asked: a part it named is not there, so it plans again.
        await DoneAsync(f, b, new { action = "animate", sourceId = id, prompt = "[badpart] the sun turns, the hills appear" });
        Assert.Equal(3, asked("svg-animate"));
        drawing = await b.JsonAsync(await b.GetAsync($"/api/media/vectors/{id}"));
        Assert.Equal(["sun", "hills"], drawing.GetProperty("motions").EnumerateArray().Select(m => m.GetProperty("target").GetString()));
        Assert.Contains("@keyframes", drawing.GetProperty("markup").GetString(), StringComparison.Ordinal);
        Assert.Equal((4, true), (drawing.GetProperty("seconds").GetDouble(), drawing.GetProperty("loops").GetBoolean()));

        // Changed as asked: the sun red; its motions kept (their parts are still there).
        await DoneAsync(f, b, new { action = "reshape", sourceId = id, prompt = "make the sun red" });
        drawing = await b.JsonAsync(await b.GetAsync($"/api/media/vectors/{id}"));
        Assert.Contains("fill=\"#e33\"", drawing.GetProperty("still").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, drawing.GetProperty("motions").GetArrayLength());

        // By hand: code that is no SVG refused; motions changed; versions kept and one put back.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.Http.PutAsJsonAsync(new Uri($"/api/media/vectors/{id}", UriKind.Relative), new { still = "<div/>" }));
        var byHand = await b.JsonAsync(await b.Http.PutAsJsonAsync(new Uri($"/api/media/vectors/{id}", UriKind.Relative),
            new { motions = new object[] { new { target = "sun", kind = "pulse" }, new { target = "nowhere", kind = "spin" } } }));
        Assert.Contains("no part with the id \"nowhere\"", byHand.GetProperty("problems")[0].GetString(), StringComparison.Ordinal);
        var versions = (await b.JsonAsync(await b.GetAsync($"/api/media/vectors/{id}/versions"))).EnumerateArray().Select(v => (v.GetProperty("version").GetInt32(), v.GetProperty("change").GetString())).ToList();
        Assert.Equal([(4, "its motions by hand"), (3, "asked: make the sun red"), (2, "animated: [badpart] the sun turns, the hills appear"), (1, "drawn")], versions);
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.PostAsync($"/api/media/vectors/{id}/versions/1/restore"));
        drawing = await b.JsonAsync(await b.GetAsync($"/api/media/vectors/{id}"));
        Assert.Equal((5, 0), (drawing.GetProperty("version").GetInt32(), drawing.GetProperty("motions").GetArrayLength()));

        // Made a picture in the sandbox; a GIF needs the browser, which does not run here.
        var png = First(await DoneAsync(f, b, new { action = "export", sourceId = id, format = "png", side = 1024 }));
        var exported = (await b.JsonAsync(await b.GetAsync("/api/media/items"))).GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetGuid() == png);
        Assert.Equal(("picture", "export", id, 1024), (exported.GetProperty("kind").GetString(), exported.GetProperty("action").GetString(), exported.GetProperty("parentId").GetGuid(), exported.GetProperty("width").GetInt32()));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/media/jobs", new { action = "export", sourceId = id, format = "avi" }));
        var gif = await DoneAsync(f, b, new { action = "export", sourceId = id, format = "gif" }, "failed");
        Assert.Contains("does not move", gif.GetProperty("error").GetString(), StringComparison.Ordinal);
        await b.Http.PutAsJsonAsync(new Uri($"/api/media/vectors/{id}", UriKind.Relative), new { motions = new object[] { new { target = "sun", kind = "spin" } } });
        gif = await DoneAsync(f, b, new { action = "export", sourceId = id, format = "gif" }, "failed");
        Assert.Contains("browser module", gif.GetProperty("error").GetString(), StringComparison.Ordinal);

        // An SVG of one's own: made safe, and told what went.
        var upload = await b.JsonAsync(await b.PostAsync("/api/media/vectors", new { name = "logo.svg", svg = "<svg viewBox=\"0 0 10 10\"><script>alert(1)</script><circle r=\"4\" onclick=\"x()\"/></svg>" }));
        Assert.Contains(upload.GetProperty("changed").EnumerateArray(), c => c.GetString()!.Contains("script", StringComparison.Ordinal));
        var mine = await b.GetAsync($"/api/media/items/{upload.GetProperty("id").GetGuid()}/svg");
        Assert.DoesNotContain("alert", await mine.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Traced: one the picture model draws flat from words (kept beside it), made of the things the words name (the model
        // names them, the segmenter finds them: not the background), then the person's picture, its parts its islands when
        // the segmenter fails.
        var traced = First(await DoneAsync(f, b, new { action = "trace", prompt = "a red badge", trace = "logo" }));
        Assert.Contains("flat vector illustration", pictures.Asked.Single()["prompt"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(["a red round badge", "a dark star"], segmenter.Asked.Single());
        var tracedDrawing = await b.JsonAsync(await b.GetAsync($"/api/media/vectors/{traced}"));
        var still = tracedDrawing.GetProperty("still").GetString()!;
        Assert.Contains("<g id=\"badge\">", still, StringComparison.Ordinal);
        Assert.Contains("<g id=\"star\">", still, StringComparison.Ordinal);
        Assert.Equal(("traced", "logo"), (tracedDrawing.GetProperty("source").GetString(), tracedDrawing.GetProperty("style").GetString()));
        var parent = (await b.JsonAsync(await b.GetAsync("/api/media/items"))).GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetGuid() == traced).GetProperty("parentId").GetGuid();
        segmenter.Fail = true;
        var islands = First(await DoneAsync(f, b, new { action = "trace", sourceId = parent }));
        Assert.Contains("<g id=\"part\">", (await b.JsonAsync(await b.GetAsync($"/api/media/vectors/{islands}"))).GetProperty("still").GetString(), StringComparison.Ordinal);

        // In the chat: drawn and animated by the tool as the studio does it (kept there), shown as an SVG that runs nothing.
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            var person = await db.Users.SingleAsync(u => u.UserName == name);
            var run = await scope.ServiceProvider.GetRequiredService<Llm.Api.Chat.Tools.VectorTool>()
                .StartAsync(new Llm.Api.Chat.Tools.ToolContext(person, person.Email!, new Llm.Core.Chat.Conversation { UserId = person.Id }), CancellationToken.None);
            Assert.Contains(run.Functions, fn => fn!["function"]!["name"]!.GetValue<string>() == "animate_svg");
            var drawn = await run.CallAsync("draw_svg", new JsonObject { ["description"] = "a sun over hills", ["kind"] = "drawing", ["style"] = "cute" }, CancellationToken.None);
            Assert.False(drawn.IsError, drawn.Text);
            var answer = JsonNode.Parse(drawn.Text)!;
            Assert.Equal(["sky", "sun", "hills"], answer["parts"]!.AsArray().Select(p => p!.GetValue<string>()));
            var shownFile = drawn.Files!.Single();
            Assert.Equal(("image", "image/svg+xml"), (shownFile.Kind, shownFile.ContentType));
            var shown = await b.GetAsync($"/api/chat/attachments/{shownFile.Id}/content");
            Assert.Contains("sandbox", shown.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
            var chatDrawing = answer["drawing"]!.GetValue<string>();
            Assert.Contains((await b.JsonAsync(await b.GetAsync("/api/media/items"))).GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == chatDrawing);
            var animated = await run.CallAsync("animate_svg", new JsonObject { ["drawing"] = chatDrawing, ["motion"] = "the sun spins" }, CancellationToken.None);
            Assert.False(animated.IsError, animated.Text);
            Assert.Contains("sun: spin", JsonNode.Parse(animated.Text)!["motions"]!.ToJsonString(), StringComparison.Ordinal);
            Assert.True((await run.CallAsync("animate_svg", new JsonObject { ["drawing"] = id.ToString(), ["motion"] = "x" }, CancellationToken.None)).IsError is false);
            Assert.True((await run.CallAsync("animate_svg", new JsonObject { ["drawing"] = Guid.NewGuid().ToString(), ["motion"] = "spin" }, CancellationToken.None)).IsError);
        }

        // Another person sees none of it.
        var other = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/media/vectors/{id}"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/media/items/{id}/svg"));
        Assert.NotEqual(broken, id);
    }
}
