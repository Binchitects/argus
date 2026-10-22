using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Gateway;
using Llm.Api.Media;
using Llm.Api.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>The media studio's videos: a storyboard from the person's words, changed, made shot by shot with narration and music, cut into one MP4.</summary>
[Collection(nameof(AppCollection))]
public sealed class MediaStoryTests(AppFixture app)
{
    /// <summary>The picture and video servers: each job done at the first look (a 1×1 picture, a few bytes of video).</summary>
    private sealed class FakeServers : HttpMessageHandler
    {
        public readonly ConcurrentQueue<(string Path, JsonObject Body)> Asked = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? [] : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject ?? [];
            JsonNode answer = path switch
            {
                "/v1/models" => new JsonObject { ["data"] = new JsonArray() },
                "/health" => new JsonObject { ["models"] = new JsonObject { ["cutout"] = true, ["voice"] = true, ["music"] = true }, ["free_mb"] = 20000, ["loaded"] = new JsonArray() },
                "/sdcpp/v1/img_gen" => new JsonObject { ["poll_url"] = "/sdcpp/v1/jobs/img" },
                "/sdcpp/v1/vid_gen" => new JsonObject { ["poll_url"] = "/sdcpp/v1/jobs/vid" },
                "/sdcpp/v1/jobs/img" => new JsonObject
                {
                    ["status"] = "completed",
                    ["result"] = new JsonObject { ["images"] = new JsonArray(new JsonObject { ["b64_json"] = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==" }) },
                },
                "/sdcpp/v1/jobs/vid" => new JsonObject
                {
                    ["status"] = "completed", ["result"] = new JsonObject { ["b64_json"] = Convert.ToBase64String([0x1A, 0x45, 0xDF, 0xA3]), ["mime_type"] = "video/webm" },
                },
                _ => new JsonObject(),
            };
            if (request.Method == HttpMethod.Post)
            {
                Asked.Enqueue((path, body));
            }
            if (path is "/v1/music" or "/v1/speak")
            {
                var sound = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xF3, 0x44, 0xC4]) };
                sound.Headers.Add("X-Seconds", path == "/v1/music" ? "14.00" : "2.00");
                return sound;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task A_storyboard_is_written_changed_made_shot_by_shot_with_narration_and_music_and_cut_and_a_shot_made_again_alone()
    {
        var servers = new FakeServers();
        var audio = new FakeAudio();
        await using var sandbox = new FakeSandbox();
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel(MediaModels.TextToSpeech, null, null, false, false, false, null, null, null, Mode: "audio_speech"));
        await using var f = app.Create(app.ConnectionStringFor("story_" + Guid.NewGuid().ToString("N")[..8]), gateway,
            new Dictionary<string, string?>
            {
                ["Modules:imagegen"] = "true", ["Modules:videogen"] = "true", ["Modules:studio"] = "true", ["Modules:audio"] = "true", ["Sandbox:Dir"] = sandbox.Dir,
            },
            s => s.AddHttpClient(SdServer.Client).ConfigurePrimaryHttpMessageHandler(() => servers)
                .Services.AddHttpClient(StudioServer.Client).ConfigurePrimaryHttpMessageHandler(() => servers)
                .Services.AddHttpClient(MediaControl.Client).ConfigurePrimaryHttpMessageHandler(() => servers)
                .Services.AddHttpClient(VoiceCatalog.Client).ConfigurePrimaryHttpMessageHandler(() => audio));
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "st" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var b = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        var runs = f.Services.GetRequiredService<StoryRuns>();

        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/media/stories", new { brief = "" }));
        var written = await b.JsonAsync(await b.PostAsync("/api/media/stories", new { brief = "A fox's winter, for children", shots = 3 }));
        var id = written.GetProperty("id").GetGuid();
        Assert.Equal(("The fox in winter", "calm piano, soft strings", "draft"), (written.GetProperty("title").GetString(), written.GetProperty("music").GetString(),
            written.GetProperty("status").GetString()));
        var shots = written.GetProperty("shots").EnumerateArray().ToList();
        Assert.Equal(3, shots.Count);
        Assert.Equal(("Line 2 of the story.", 3), (shots[1].GetProperty("narration").GetString(), shots[1].GetProperty("seconds").GetInt32()));

        // Changed by the person: a narrator's sample without their word is refused.
        object[] Shots(string first) => [.. shots.Select((s, i) => new { visual = s.GetProperty("visual").GetString(), narration = i == 0 ? first : s.GetProperty("narration").GetString(), seconds = 3 })];
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.Http.PutAsJsonAsync(new Uri($"/api/media/stories/{id}", UriKind.Relative),
            new { title = "Fox", shots = Shots("Snow fell."), voiceId = Guid.NewGuid(), consent = false, music = "piano" }));
        await StatusAssert.Is(HttpStatusCode.OK, await b.Http.PutAsJsonAsync(new Uri($"/api/media/stories/{id}", UriKind.Relative),
            new { title = "The fox in winter", shots = Shots("Snow fell all night."), consent = false, music = "calm piano" }));

        // Made: three pictures (wide), three clips from them, the narration in the person's voice settings, music, the cut.
        await StatusAssert.Is(HttpStatusCode.Accepted, await b.PostAsync($"/api/media/stories/{id}/make"));
        await runs.Finished(id).WaitAsync(TimeSpan.FromSeconds(60));
        var story = await b.JsonAsync(await b.GetAsync($"/api/media/stories/{id}"));
        Assert.True(story.GetProperty("status").GetString() == "done", story.GetProperty("error").ToString());
        Assert.All(story.GetProperty("shots").EnumerateArray(), s => Assert.NotNull(s.GetProperty("clip").GetString()));
        Assert.Equal(3, servers.Asked.Count(a => a.Path == "/sdcpp/v1/img_gen"));
        Assert.All(servers.Asked.Where(a => a.Path == "/sdcpp/v1/img_gen"), a => Assert.Equal((1024, 576), (a.Body["width"]!.GetValue<int>(), a.Body["height"]!.GetValue<int>())));
        Assert.All(servers.Asked.Where(a => a.Path == "/sdcpp/v1/vid_gen"), a => Assert.StartsWith("data:image/png;base64,", a.Body["init_image"]!.GetValue<string>(), StringComparison.Ordinal));
        Assert.Contains(app.Model.SpeechRequests, r => r["input"]?.GetValue<string>() == "Snow fell all night.");
        var music = Assert.Single(servers.Asked, a => a.Path == "/v1/music").Body;
        Assert.Equal(("calm piano", "[instrumental]"), (music["prompt"]!.GetValue<string>(), music["lyrics"]!.GetValue<string>()));
        var cut = sandbox.Jobs.Last(j => j["code"]!.GetValue<string>().StartsWith("# The media studio's video", StringComparison.Ordinal));
        var video = story.GetProperty("videoItemId").GetGuid();
        var items = (await b.JsonAsync(await b.GetAsync("/api/media/items?kind=video"))).GetProperty("items").EnumerateArray().ToList();
        var item = Assert.Single(items);
        Assert.Equal((video, "story", 9.0, "video/mp4"), (item.GetProperty("id").GetGuid(), item.GetProperty("action").GetString(), item.GetProperty("seconds").GetDouble(),
            item.GetProperty("contentType").GetString()));

        // Shot 2 again: only its picture and clip are made again (the narration and the music kept); the video is cut again.
        var spoken = app.Model.SpeechRequests.Count;
        await StatusAssert.Is(HttpStatusCode.Accepted, await b.PostAsync($"/api/media/stories/{id}/shots/1/again"));
        await runs.Finished(id).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(4, servers.Asked.Count(a => a.Path == "/sdcpp/v1/img_gen"));
        Assert.Equal(4, servers.Asked.Count(a => a.Path == "/sdcpp/v1/vid_gen"));
        Assert.Equal(spoken, app.Model.SpeechRequests.Count);
        Assert.Single(servers.Asked, a => a.Path == "/v1/music");
        var again = await b.JsonAsync(await b.GetAsync($"/api/media/stories/{id}"));
        Assert.NotEqual(video, again.GetProperty("videoItemId").GetGuid());
        Assert.Single((await b.JsonAsync(await b.GetAsync("/api/media/items?kind=video"))).GetProperty("items").EnumerateArray());

        // One video of a person's in the making at a time.
        var second = (await b.JsonAsync(await b.PostAsync("/api/media/stories", new { brief = "Another one", shots = 2 }))).GetProperty("id").GetGuid();
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            var s = db.MediaStories.Single(x => x.Id == id);
            (s.Status, s.UpdatedAt) = (Llm.Core.Media.MediaStory.Making, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        await StatusAssert.Is(HttpStatusCode.TooManyRequests, await b.PostAsync($"/api/media/stories/{second}/make"));
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            db.MediaStories.Single(x => x.Id == id).Status = Llm.Core.Media.MediaStory.Done;
            await db.SaveChangesAsync();
        }

        // Another person sees none of it; deleted, its files go and the video stays.
        var other = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/media/stories/{id}"));
        var clip = again.GetProperty("shots")[0].GetProperty("clip").GetString()!;
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/media/stories/{id}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await b.GetAsync(clip));
        Assert.Single((await b.JsonAsync(await b.GetAsync("/api/media/items?kind=video"))).GetProperty("items").EnumerateArray());
        Assert.Contains("story.json", cut["code"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_storyboard_is_read_as_small_models_write_it()
    {
        var s = StoryModel.Parse("""Sure! {"title": "", "shots": [{"visual": "A cat", "narration": "Hi.", "seconds": "9"}, {"visual": ""}, {"visual": "A dog", "seconds": 1.4},], } Done.""");
        Assert.NotNull(s);
        Assert.Equal(("A story", null), (s.Title, s.Music));
        Assert.Equal([("A cat", 5), ("A dog", 2)], s.Shots.Select(x => (x.Visual, x.Seconds)));
        Assert.Null(StoryModel.Parse("no json here"));
        Assert.Null(StoryModel.Parse("""{"shots": []}"""));
        Assert.True(StoryModel.RightToLeft("برف بارید"));
        Assert.False(StoryModel.RightToLeft("Snow fell"));

        var shot = new StoryShot { Visual = "A", Narration = "B", Seconds = 3, Picture = Guid.NewGuid(), Clip = Guid.NewGuid(), Speech = Guid.NewGuid() };
        Assert.Equal((shot.Picture, shot.Clip, (Guid?)null), (shot.Changed("A", "C", 3).Picture, shot.Changed("A", "C", 3).Clip, shot.Changed("A", "C", 3).Speech));
        Assert.Equal((shot.Picture, (Guid?)null, shot.Speech), (shot.Changed("A", "B", 4).Picture, shot.Changed("A", "B", 4).Clip, shot.Changed("A", "B", 4).Speech));
        Assert.Equal(((Guid?)null, (Guid?)null), (shot.Changed("X", "B", 3).Picture, shot.Changed("X", "B", 3).Clip));
    }
}
