using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Gateway;
using Llm.Api.Media;
using Llm.Api.Models;
using Llm.Core.Data;
using Llm.Core.Media;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Llm.Tests;

/// <summary>The media studio: pictures made, changed (all or where painted), varied, enlarged and set moving, each a job in the background.</summary>
[Collection(nameof(AppCollection))]
public sealed class MediaStudioTests(AppFixture app)
{
    private static Png.Image Solid(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            (px[i], px[i + 1], px[i + 2], px[i + 3]) = (r, g, b, 255);
        }
        return new Png.Image(w, h, px);
    }

    private static string DataUrl(Png.Image image) => "data:image/png;base64," + Convert.ToBase64String(Png.Encode(image));

    [Fact]
    public void Pngs_read_back_what_was_written_blend_where_painted_and_thumbnails_are_jpegs()
    {
        var px = new byte[40 * 30 * 4];
        for (var i = 0; i < 40 * 30; i++)
        {
            (px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]) = ((byte)(i % 40 * 6), (byte)(i / 40 * 8), (byte)(i * 7 % 256), 255);
        }
        var gradient = new Png.Image(40, 30, px);
        var png = Png.Encode(gradient);
        Assert.Equal(px, Png.Decode(png).Rgba);
        Assert.Equal((40, 30), Png.SizeOf(png));

        // Painted on the left half: the change there, the original elsewhere.
        var mask = Solid(4, 2, 0, 0, 0);
        mask.Rgba[0] = mask.Rgba[4] = mask.Rgba[16] = mask.Rgba[20] = 255;
        var blended = Png.Blend(Solid(4, 2, 255, 0, 0), Solid(4, 2, 0, 0, 255), mask);
        Assert.Equal([0, 0, 255, 255, 0, 0, 255, 255, 255, 0, 0, 255, 255, 0, 0, 255], blended.Rgba[..16]);
        Assert.Throws<InvalidDataException>(() => Png.Blend(Solid(4, 2, 0, 0, 0), Solid(2, 2, 0, 0, 0), mask));
        Assert.Throws<InvalidDataException>(() => Png.Decode([1, 2, 3]));

        var thumb = Jpeg.Encode(Jpeg.Smaller(Solid(1000, 500, 30, 120, 200), 384));
        Assert.Equal((384, 192), Png.SizeOf(thumb));
        Assert.Equal([0xFF, 0xD8], thumb[..2]);
        Assert.Equal([0xFF, 0xD9], thumb[^2..]);
        Assert.True(thumb.Length < 8000, $"{thumb.Length} bytes");
        Assert.Equal((10, 7), Png.SizeOf([.. "GIF89a"u8, 10, 0, 7, 0]));
        Assert.Null(Png.SizeOf([1, 2, 3, 4]));

        // A painted change: the painting with some around it, 256 pixels a side at least where the picture allows.
        var painted = Solid(1000, 600, 0, 0, 0);
        for (var y = 100; y < 140; y++)
        {
            for (var x = 700; x < 800; x++)
            {
                painted.Rgba[(y * 1000 + x) * 4] = 255;
            }
        }
        Assert.Equal(new Png.Area(622, 0, 256, 256), Png.AreaAround(painted, 256));
        Assert.Null(Png.AreaAround(Solid(10, 10, 0, 0, 0), 256));
        Assert.Equal(new Png.Area(0, 0, 10, 10), Png.AreaAround(Solid(10, 10, 255, 0, 0), 256));
        var part = Png.Crop(gradient, new Png.Area(5, 4, 10, 6));
        var at = (4 * 40 + 5) * 4;
        Assert.Equal(gradient.Rgba[at], part.Rgba[0]);
        var back = Png.Paste(Solid(40, 30, 0, 0, 0), part, 5, 4);
        Assert.Equal(part.Rgba[..4], back.Rgba[at..(at + 4)]);
        Assert.Equal((20, 15), (Png.Resize(gradient, 20, 15).Width, Png.Resize(gradient, 20, 15).Height));
        Assert.Equal(Solid(7, 5, 9, 8, 7).Rgba, Png.Resize(Solid(3, 2, 9, 8, 7), 7, 5).Rgba);
        Assert.Equal((768, 384), MediaJobs.DrawSize(64, 32));
        Assert.Equal((1024, 576), MediaJobs.DrawSize(1024, 576));

        Assert.Equal((1024, 672), MediaJobs.Fit(1536, 1024));
        Assert.Equal((496, 288), MediaJobs.Fit(500, 300));
        Assert.Equal((64, 64), MediaJobs.Fit(10, 10));
    }

    /// <summary>The picture and video servers: each job done at the first look, its pictures blue and of the size asked.</summary>
    private sealed class FakeSd : HttpMessageHandler
    {
        public readonly ConcurrentQueue<(string Path, JsonObject Body)> Asked = new();
        private readonly ConcurrentDictionary<string, JsonObject> _jobs = new();
        private int _next;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? new JsonObject() : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject ?? [];
            if (request.Method == HttpMethod.Post)
            {
                Asked.Enqueue((path, body));
            }
            JsonNode answer;
            switch (path)
            {
                case "/v1/models":
                    answer = new JsonObject { ["data"] = new JsonArray() };
                    break;
                case "/sdcpp/v1/img_gen" or "/sdcpp/v1/vid_gen":
                    var id = Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    _jobs[id] = body;
                    answer = new JsonObject { ["poll_url"] = $"/sdcpp/v1/jobs/{id}" };
                    break;
                case "/sdcpp/v1/upscale":
                    var image = Png.Decode(Convert.FromBase64String(body["image"]!.GetValue<string>().Split(',')[1]));
                    Assert.Equal("jpeg", body["output_format"]!.GetValue<string>());
                    var big = Jpeg.Encode(Solid(image.Width * 4, image.Height * 4, 0, 255, 0));
                    answer = new JsonObject
                    {
                        ["images"] = new JsonArray(new JsonObject { ["b64_json"] = Convert.ToBase64String(big) }), ["width"] = image.Width * 4, ["height"] = image.Height * 4,
                    };
                    break;
                default:
                    var job = _jobs[path.Split('/')[^1]];
                    JsonObject result;
                    if (job.ContainsKey("video_frames"))
                    {
                        result = new JsonObject { ["b64_json"] = Convert.ToBase64String([0x1A, 0x45, 0xDF, 0xA3]), ["mime_type"] = "video/webm" };
                    }
                    else
                    {
                        var picture = Convert.ToBase64String(Png.Encode(Solid(job["width"]!.GetValue<int>(), job["height"]!.GetValue<int>(), 0, 0, 255)));
                        var count = job["batch_count"]?.GetValue<int>() ?? 1;
                        result = new JsonObject { ["images"] = new JsonArray([.. Enumerable.Range(0, count).Select(_ => (JsonNode)new JsonObject { ["b64_json"] = picture })]) };
                    }
                    answer = new JsonObject { ["status"] = "completed", ["result"] = result };
                    break;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private async Task<(WebApplicationFactory<Program> App, FakeSd Server, DirectoryInfo Library)> NewAppAsync()
    {
        var library = Directory.CreateTempSubdirectory("media-studio-");
        Directory.CreateDirectory(Path.Combine(library.FullName, MediaModels.UpscalerDir));
        await File.WriteAllTextAsync(Path.Combine(library.FullName, MediaModels.UpscalerDir, MediaModels.Upscaler), "");
        var server = new FakeSd();
        var f = app.Create(app.ConnectionStringFor("media_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Modules:imagegen"] = "true", ["Modules:videogen"] = "true", ["Engine:LibraryDir"] = library.FullName },
            services => services.AddHttpClient(SdServer.Client).ConfigurePrimaryHttpMessageHandler(() => server)
                .Services.AddHttpClient(MediaControl.Client).ConfigurePrimaryHttpMessageHandler(() => server));
        return (f, server, library);
    }

    private static async Task<(TestBrowser Browser, Guid Id, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "ms" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("id").GetGuid(), $"{name}@example.test");
    }

    private static async Task<JsonElement> DoneAsync(WebApplicationFactory<Program> f, TestBrowser b, object request)
    {
        var res = await b.PostAsync("/api/media/jobs", request);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var id = (await b.JsonAsync(res)).GetProperty("id").GetGuid();
        await f.Services.GetRequiredService<MediaJobs>().Finished(id).WaitAsync(TimeSpan.FromSeconds(60));
        var job = (await b.JsonAsync(await b.GetAsync("/api/media/jobs"))).EnumerateArray().Single(j => j.GetProperty("id").GetGuid() == id);
        Assert.True(job.GetProperty("status").GetString() == "done", job.GetProperty("error").ToString());
        return job;
    }

    private static async Task<JsonElement> ItemAsync(TestBrowser b, Guid id) =>
        (await b.JsonAsync(await b.GetAsync("/api/media/items"))).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id);

    private static async Task<Png.Image> PictureAsync(TestBrowser b, JsonElement item) =>
        Png.Decode(await (await b.GetAsync($"/api/chat/attachments/{item.GetProperty("attachmentId").GetGuid()}/content")).Content.ReadAsByteArrayAsync());

    [Fact]
    public async Task Pictures_are_made_changed_where_painted_varied_enlarged_and_set_moving_each_kept_with_where_it_came_from()
    {
        var (f, server, library) = await NewAppAsync();
        await using var _ = f;
        try
        {
            var (b, id, email) = await PersonAsync(f);
            var config = await b.JsonAsync(await b.GetAsync("/api/media"));
            Assert.Equal(MediaModels.ImageModel, config.GetProperty("picture").GetString());
            Assert.Equal(MediaModels.VideoModel, config.GetProperty("video").GetString());
            Assert.True(config.GetProperty("enlarge").GetBoolean());

            await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/media/jobs", new { action = "make" }));
            await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/media/jobs", new { action = "paint", prompt = "x" }));
            await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/media/jobs", new { action = "vary" }));

            // Two pictures from words, with thumbnails.
            var made = await DoneAsync(f, b, new { action = "make", prompt = "A red fox in the snow", count = 2, width = 768, height = 512 });
            var ids = made.GetProperty("items").EnumerateArray().Select(i => i.GetGuid()).ToList();
            Assert.Equal(2, ids.Count);
            var asked = server.Asked.Single(a => a.Path == "/sdcpp/v1/img_gen").Body;
            Assert.Equal("A red fox in the snow", asked["prompt"]!.GetValue<string>());
            Assert.Equal((768, 512, 2), (asked["width"]!.GetValue<int>(), asked["height"]!.GetValue<int>(), asked["batch_count"]!.GetValue<int>()));
            var first = await ItemAsync(b, ids[0]);
            Assert.Equal(("picture", "make", 768), (first.GetProperty("kind").GetString(), first.GetProperty("action").GetString(), first.GetProperty("width").GetInt32()));
            Assert.True(first.GetProperty("thumb").GetBoolean());
            var thumb = await b.GetAsync($"/api/media/items/{ids[0]}/thumb");
            Assert.Equal("image/jpeg", thumb.Content.Headers.ContentType?.MediaType);
            Assert.Equal((384, 256), Png.SizeOf(await thumb.Content.ReadAsByteArrayAsync()));

            // Changed where painted (the left half): the change there, the picture as it was elsewhere; from the first one.
            var red = Solid(64, 32, 255, 0, 0);
            var mask = Solid(64, 32, 0, 0, 0);
            for (var y = 0; y < 32; y++)
            {
                for (var x = 0; x < 32; x++)
                {
                    mask.Rgba[(y * 64 + x) * 4] = 255;
                }
            }
            await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/media/jobs", new { action = "edit", sourceId = ids[0], prompt = "Blue", picture = "data:image/jpeg;base64,AAAA", mask = DataUrl(mask) }));
            var edited = await DoneAsync(f, b, new { action = "edit", sourceId = ids[0], prompt = "Make it blue", picture = DataUrl(red), mask = DataUrl(mask) });
            var editId = edited.GetProperty("items")[0].GetGuid();
            var edit = await ItemAsync(b, editId);
            Assert.Equal(ids[0], edit.GetProperty("parentId").GetGuid());
            var pixels = await PictureAsync(b, edit);
            Assert.Equal((64, 32), (pixels.Width, pixels.Height));
            Assert.Equal([0, 0, 255], pixels.Rgba[..3]);
            Assert.Equal([255, 0, 0], pixels.Rgba[(63 * 4)..(63 * 4 + 3)]);
            var changed = server.Asked.Last(a => a.Path == "/sdcpp/v1/img_gen").Body;
            // The painted part with what is around it (here all of it, smaller than 256 pixels), drawn larger.
            Assert.Equal(DataUrl(red), changed["ref_images"]![0]!.GetValue<string>());
            Assert.Equal((768, 384), (changed["width"]!.GetValue<int>(), changed["height"]!.GetValue<int>()));

            // A little different, from the painted one; four times as large; and a clip that starts from it.
            await DoneAsync(f, b, new { action = "vary", sourceId = editId, strength = 0.4 });
            var varied = server.Asked.Last(a => a.Path == "/sdcpp/v1/img_gen").Body;
            Assert.Equal(0.4, varied["strength"]!.GetValue<double>());
            Assert.StartsWith("data:image/png;base64,", varied["init_image"]!.GetValue<string>(), StringComparison.Ordinal);
            var enlarged = await ItemAsync(b, (await DoneAsync(f, b, new { action = "enlarge", sourceId = editId })).GetProperty("items")[0].GetGuid());
            Assert.Equal((256, 128), (enlarged.GetProperty("width").GetInt32(), enlarged.GetProperty("height").GetInt32()));
            Assert.Equal(("image/jpeg", true), (enlarged.GetProperty("contentType").GetString(), enlarged.GetProperty("thumb").GetBoolean()));
            var clip = await ItemAsync(b, (await DoneAsync(f, b, new { action = "move", sourceId = editId, prompt = "The fox looks up", seconds = 2 })).GetProperty("items")[0].GetGuid());
            Assert.Equal(("video", 2.0, true), (clip.GetProperty("kind").GetString(), clip.GetProperty("seconds").GetDouble(), clip.GetProperty("thumb").GetBoolean()));
            var film = server.Asked.Single(a => a.Path == "/sdcpp/v1/vid_gen").Body;
            Assert.StartsWith("data:image/png;base64,", film["init_image"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.True(film["width"]!.GetValue<int>() > film["height"]!.GetValue<int>());
            await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/media/jobs", new { action = "vary", sourceId = clip.GetProperty("id").GetGuid() }));

            // Booked in the gateway's log as the chat's: four picture requests (two pictures in one), one clip.
            await using (var conn = new NpgsqlConnection(app.LitellmConnectionString))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand("""select call_type, count(*) from "LiteLLM_SpendLogs" where end_user = @email group by call_type order by call_type""", conn);
                cmd.Parameters.AddWithValue("email", email);
                await using var reader = await cmd.ExecuteReaderAsync();
                var rows = new List<(string, long)>();
                while (await reader.ReadAsync())
                {
                    rows.Add((reader.GetString(0), reader.GetInt64(1)));
                }
                Assert.Equal([(SpendLog.PictureCall, 4L), (SpendLog.VideoCall, 1L)], rows);
            }

            // Another person sees none of it; taken out, a made picture's file goes with it.
            var (other, _, _) = await PersonAsync(f);
            await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/media/items/{ids[0]}/thumb"));
            await StatusAssert.Is(HttpStatusCode.NotFound, await other.Http.DeleteAsync(new Uri($"/api/media/items/{ids[0]}", UriKind.Relative)));
            Assert.Empty((await other.JsonAsync(await other.GetAsync("/api/media/items"))).GetProperty("items").EnumerateArray());
            var second = (await ItemAsync(b, ids[1])).GetProperty("attachmentId").GetGuid();
            await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/media/items/{ids[1]}", UriKind.Relative)));
            await StatusAssert.Is(HttpStatusCode.NotFound, await b.GetAsync($"/api/chat/attachments/{second}/content"));

            // Three pieces of work going: a fourth waits.
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.MediaJobs.AddRange(Enumerable.Range(0, MediaJobs.MostGoing).Select(_ => new MediaJob { OwnerId = id, Action = MediaJob.Make, Prompt = "x" }));
                await db.SaveChangesAsync();
            }
            await StatusAssert.Is(HttpStatusCode.TooManyRequests, await b.PostAsync("/api/media/jobs", new { action = "make", prompt = "One more" }));
        }
        finally
        {
            library.Delete(true);
        }
    }

    /// <summary>The studio server: a cut-out half see-through, speech and music as a few bytes of "MP3" with their length.</summary>
    private sealed class FakeStudio : HttpMessageHandler
    {
        public readonly ConcurrentQueue<(string Path, JsonObject? Body)> Asked = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/health")
            {
                return Json(new JsonObject { ["models"] = new JsonObject { ["cutout"] = true, ["voice"] = true, ["music"] = true } });
            }
            if (path == "/v1/cutout")
            {
                var picture = Png.Decode(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
                Asked.Enqueue((path, null));
                for (var i = 3; i < picture.Rgba.Length / 2; i += 4)
                {
                    picture.Rgba[i] = 0;
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(RgbaPng(picture)) };
            }
            Asked.Enqueue((path, JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)) as JsonObject));
            var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xF3, 0x44, 0xC4]) };
            res.Headers.Add("X-Seconds", path == "/v1/music" ? "20.00" : "2.50");
            return res;
        }

        private static HttpResponseMessage Json(JsonNode body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
    }

    /// <summary>An RGBA PNG (Png.Encode writes RGB only), unfiltered.</summary>
    private static byte[] RgbaPng(Png.Image image)
    {
        using var raw = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(raw, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var y = 0; y < image.Height; y++)
            {
                z.WriteByte(0);
                z.Write(image.Rgba, y * image.Width * 4, image.Width * 4);
            }
        }
        // The RGB writer's file, its header and data swapped for these.
        var rgb = Png.Encode(new Png.Image(1, 1, [0, 0, 0, 255]));
        using var o = new MemoryStream();
        o.Write(rgb, 0, 8);
        void Chunk(string type, byte[] body)
        {
            var len = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, body.Length);
            o.Write(len);
            var named = System.Text.Encoding.ASCII.GetBytes(type);
            o.Write(named);
            o.Write(body);
            o.Write(new byte[4]);
        }
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        (header[8], header[9]) = (8, 6);
        Chunk("IHDR", header);
        Chunk("IDAT", raw.ToArray());
        Chunk("IEND", []);
        return o.ToArray();
    }

    [Fact]
    public async Task The_studio_server_takes_a_background_away_speaks_in_a_voice_the_person_may_use_and_makes_music()
    {
        var library = Directory.CreateTempSubdirectory("media-studio-");
        var server = new FakeSd();
        var studio = new FakeStudio();
        await using var f = app.Create(app.ConnectionStringFor("studio_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Modules:imagegen"] = "true", ["Modules:studio"] = "true", ["Engine:LibraryDir"] = library.FullName },
            services => services.AddHttpClient(SdServer.Client).ConfigurePrimaryHttpMessageHandler(() => server)
                .Services.AddHttpClient(MediaControl.Client).ConfigurePrimaryHttpMessageHandler(() => server)
                .Services.AddHttpClient(StudioServer.Client).ConfigurePrimaryHttpMessageHandler(() => studio));
        try
        {
            var (b, _, email) = await PersonAsync(f);
            var config = await b.JsonAsync(await b.GetAsync("/api/media"));
            Assert.True(config.GetProperty("cutout").GetBoolean() && config.GetProperty("voice").GetBoolean() && config.GetProperty("music").GetBoolean());

            var made = (await DoneAsync(f, b, new { action = "make", prompt = "A cup of tea", width = 64, height = 64 })).GetProperty("items")[0].GetGuid();
            var cut = await ItemAsync(b, (await DoneAsync(f, b, new { action = "cutout", sourceId = made })).GetProperty("items")[0].GetGuid());
            Assert.Equal(("picture", "cutout", made), (cut.GetProperty("kind").GetString(), cut.GetProperty("action").GetString(), cut.GetProperty("parentId").GetGuid()));
            Assert.Equal((64, 64), (cut.GetProperty("width").GetInt32(), cut.GetProperty("height").GetInt32()));
            var kept = Png.Decode(await (await b.GetAsync($"/api/chat/attachments/{cut.GetProperty("attachmentId").GetGuid()}/content")).Content.ReadAsByteArrayAsync());
            Assert.Equal(0, kept.Rgba[3]);
            Assert.Equal(255, kept.Rgba[^1]);
            Assert.Equal("image/jpeg", (await b.GetAsync($"/api/media/items/{cut.GetProperty("id").GetGuid()}/thumb")).Content.Headers.ContentType?.MediaType);

            // Its own voice; a person's sample only when they say they may use it.
            var spoken = await ItemAsync(b, (await DoneAsync(f, b, new { action = "speak", prompt = "Welcome to the lunch club." })).GetProperty("items")[0].GetGuid());
            Assert.Equal(("sound", "speak", 2.5, "audio/mpeg"), (spoken.GetProperty("kind").GetString(), spoken.GetProperty("action").GetString(),
                spoken.GetProperty("seconds").GetDouble(), spoken.GetProperty("contentType").GetString()));
            Assert.Null(studio.Asked.Single(a => a.Path == "/v1/speak").Body!["voice"]);
            Guid sample;
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var me = db.Users.Single(u => u.Email == email).Id;
                var a = new Llm.Core.Chat.ChatAttachment { UserId = me, FileName = "me.mp3", ContentType = "audio/mpeg", Kind = "audio", Data = [1, 2, 3], Text = "" };
                db.ChatAttachments.Add(a);
                await db.SaveChangesAsync();
                sample = a.Id;
            }
            await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/media/jobs", new { action = "speak", prompt = "Hello", voiceId = sample }));
            await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/media/jobs", new { action = "speak", prompt = "Hello", voiceId = Guid.NewGuid(), consent = true }));
            await DoneAsync(f, b, new { action = "speak", prompt = "Hello from me.", voiceId = sample, consent = true, exaggeration = 0.7 });
            var asked = studio.Asked.Last(a => a.Path == "/v1/speak").Body!;
            Assert.Equal((Convert.ToBase64String([1, 2, 3]), 0.7), (asked["voice"]!.GetValue<string>(), asked["exaggeration"]!.GetValue<double>()));

            var song = await ItemAsync(b, (await DoneAsync(f, b, new { action = "music", prompt = "lo-fi piano, rain, calm", lyrics = "[verse]\nTea in the morning", seconds = 20 })).GetProperty("items")[0].GetGuid());
            Assert.Equal(("sound", "music", 20.0), (song.GetProperty("kind").GetString(), song.GetProperty("action").GetString(), song.GetProperty("seconds").GetDouble()));
            var music = studio.Asked.Single(a => a.Path == "/v1/music").Body!;
            Assert.Equal(("lo-fi piano, rain, calm", "[verse]\nTea in the morning", 20.0), (music["prompt"]!.GetValue<string>(), music["lyrics"]!.GetValue<string>(), music["seconds"]!.GetValue<double>()));

            await using var conn = new NpgsqlConnection(app.LitellmConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""select call_type, count(*) from "LiteLLM_SpendLogs" where end_user = @email group by call_type order by call_type""", conn);
            cmd.Parameters.AddWithValue("email", email);
            await using var reader = await cmd.ExecuteReaderAsync();
            var rows = new List<(string, long)>();
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetString(0), reader.GetInt64(1)));
            }
            Assert.Equal([(SpendLog.PictureCall, 2L), (SpendLog.MusicCall, 1L), (SpendLog.SpeechCall, 2L)], rows);
        }
        finally
        {
            library.Delete(true);
        }
    }

    [Fact]
    public async Task An_upload_goes_into_the_studio_and_stays_the_persons_when_taken_out()
    {
        var (f, _, library) = await NewAppAsync();
        await using var _f = f;
        try
        {
            var (b, _, _) = await PersonAsync(f);
            using var form = new MultipartFormDataContent { { new ByteArrayContent(Png.Encode(Solid(100, 50, 9, 9, 9))) { Headers = { ContentType = new("image/png") } }, "file", "mine.png" } };
            var upload = await b.JsonAsync(await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form));
            var attachment = upload.GetProperty("id").GetGuid();
            var added = (await b.JsonAsync(await b.PostAsync("/api/media/items", new { attachmentId = attachment }))).GetProperty("id").GetGuid();
            Assert.Equal(added, (await b.JsonAsync(await b.PostAsync("/api/media/items", new { attachmentId = attachment }))).GetProperty("id").GetGuid());
            var item = await ItemAsync(b, added);
            Assert.Equal(("upload", 100, 50), (item.GetProperty("action").GetString(), item.GetProperty("width").GetInt32(), item.GetProperty("height").GetInt32()));
            await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/media/items/{added}", UriKind.Relative)));
            await StatusAssert.Is(HttpStatusCode.OK, await b.GetAsync($"/api/chat/attachments/{attachment}/content"));
            await StatusAssert.Is(HttpStatusCode.NotFound, await b.PostAsync("/api/media/items", new { attachmentId = Guid.NewGuid() }));
        }
        finally
        {
            library.Delete(true);
        }
    }
}
