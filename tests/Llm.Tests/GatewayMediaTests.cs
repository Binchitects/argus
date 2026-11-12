using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Gateway;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Pictures, speech and transcriptions through the app's own gateway: an API key's request let in,
/// sent to the server of its model, and booked at its price (per picture, per character, per second heard); a
/// transcription asked as verbose_json and given back as the caller asked.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class GatewayMediaTests(AppFixture app)
{
    private static Deployment Route(string name, string mode, string priceKey, decimal price) => new()
    {
        Name = name, Kind = Deployment.Audio, BaseUrl = "http://engine.test/v1", Model = name + "-there", Mode = mode, Info = new JsonObject { ["mode"] = mode },
        Params = new JsonObject { [priceKey] = price }, Fingerprint = "media:" + name,
    };

    private sealed class Given(params Deployment[] given) : IRoutesSource
    {
        public IEnumerable<Deployment> Routes() => given;
    }

    [Fact]
    public void A_transcription_is_given_back_in_the_format_asked()
    {
        const string verbose = """{"text":"Hello there.","duration":2.5,"segments":[{"start":0.0,"end":2.5,"text":" Hello there."}]}""";
        Assert.Equal("""{"text":"Hello there."}""", KeyMedia.Render(verbose, "json").Text);
        Assert.Equal("Hello there.", KeyMedia.Render(verbose, "text").Text);
        Assert.Equal("1\n00:00:00,000 --> 00:00:02,500\nHello there.\n\n", KeyMedia.Render(verbose, "srt").Text);
        Assert.Equal("WEBVTT\n\n00:00:00.000 --> 00:00:02.500\nHello there.\n\n", KeyMedia.Render(verbose, "vtt").Text);
        Assert.Equal(verbose, KeyMedia.Render(verbose, "verbose_json").Text);
    }

    [Fact]
    public async Task An_API_keys_picture_speech_and_transcription_are_answered_by_their_servers_and_booked_at_their_prices()
    {
        var cs = app.ConnectionStringFor("gwmedia_" + Guid.NewGuid().ToString("N")[..8]);
        await using var f = app.Create(cs, new FakeGateway(), new Dictionary<string, string?> { ["Gateway:KeyCacheSeconds"] = "0" },
            s => s.AddSingleton<IRoutesSource>(new Given(
                Route("test-picture", "image_generation", "input_cost_per_image", 0.04m),
                Route("test-voice", "audio_speech", "input_cost_per_character", 0.00002m),
                Route("test-ears", "audio_transcription", "input_cost_per_second", 0.0001m))));
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "gm" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var key = made.GetProperty("apiKey").GetString()!;
        var id = made.GetProperty("id").GetGuid();
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
        async Task<HttpResponseMessage> PostAsync(string path, HttpContent content)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return await client.SendAsync(req);
        }

        var picture = await PostAsync("/v1/images/generations", new StringContent("""{"model":"test-picture","prompt":"an apple","n":2}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Contains("b64_json", await picture.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains(app.Model.ImageRequests, r => r["model"]?.GetValue<string>() == "test-picture-there");

        var spoken = await PostAsync("/audio/speech", new StringContent("""{"model":"test-voice","input":"Hello world","voice":"af_bella"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, spoken.StatusCode);
        Assert.Equal("audio/mpeg", spoken.Content.Headers.ContentType!.MediaType);

        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("audio/mpeg") } }, "file", "q.mp3" },
            { new StringContent("test-ears"), "model" },
            { new StringContent("srt"), "response_format" },
        };
        var heard = await PostAsync("/v1/audio/transcriptions", form);
        Assert.Equal(HttpStatusCode.OK, heard.StatusCode);
        Assert.StartsWith("1\n00:00:00,000 --> 00:00:04,500\nWhat is the capital of France?", await heard.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("verbose_json", app.Model.Transcriptions.Last(t => t.Contains("test-ears-there", StringComparison.Ordinal)), StringComparison.Ordinal);

        // A browser's preflight, answered by the app itself.
        foreach (var path in new[] { "/v1/chat/completions", "/v1/audio/speech" })
        {
            using var preflight = new HttpRequestMessage(HttpMethod.Options, new Uri(path, UriKind.Relative));
            preflight.Headers.TryAddWithoutValidation("Origin", "https://app.example.test");
            preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "authorization, content-type");
            var answered = await client.SendAsync(preflight);
            Assert.Equal(HttpStatusCode.NoContent, answered.StatusCode);
            Assert.Equal("*", answered.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Equal("authorization, content-type", answered.Headers.GetValues("Access-Control-Allow-Headers").Single());
        }

        var gateway = f.Services.GetRequiredService<ModelGateway>();
        List<Llm.Core.Gateway.GatewayRequest> rows = [];
        for (var i = 0; i < 100 && rows.Count < 3; i++)
        {
            await gateway.FlushAsync(default);
            await using var scope = f.Services.CreateAsyncScope();
            rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().GatewayRequests.AsNoTracking().Where(r => r.UserId == id && r.Status == "done").ToListAsync();
            if (rows.Count < 3)
            {
                await Task.Delay(50);
            }
        }
        var byCall = rows.ToDictionary(r => r.Call);
        Assert.Equal(("pictures", 2, 0.08m), (byCall["image"].CreditKind, byCall["image"].Pictures!.Value, byCall["image"].Cost));
        Assert.Equal(("speech", 11, 0.00022m), (byCall["speech"].CreditKind, byCall["speech"].Characters!.Value, byCall["speech"].Cost));
        Assert.Equal(("speech", 4.5, 0.00045m), (byCall["transcription"].CreditKind, byCall["transcription"].Seconds!.Value, byCall["transcription"].Cost));
    }
}
