using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Models;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Picture, video and speech models as rows (media_models), managed as chat models are: today's five on every stack, each
/// with its files or the speech server's id; its own price at the gateway; removed like a chat model, its files kept.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class MediaModelsTests(AppFixture app)
{
    private async Task<(WebApplicationFactory<Program> App, FakeGateway Gateway, TestBrowser Admin)> NewAsync()
    {
        var gateway = new FakeGateway();
        var f = app.Create(app.ConnectionStringFor("mmodels_" + Guid.NewGuid().ToString("N")[..8]), gateway, new Dictionary<string, string?>
        {
            ["Modules:imagegen"] = "true", ["Modules:audio"] = "true", ["Modules:videogen"] = "true",
        });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        return (f, gateway, admin);
    }

    private static async Task<List<JsonElement>> MediaRowsAsync(TestBrowser admin) =>
        [.. (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("models").EnumerateArray().Where(m => m.GetProperty("source").GetString() == "media")];

    [Fact]
    public async Task Every_stack_has_todays_five_with_their_files_kept_as_before_and_its_own_prices()
    {
        var (f, gateway, admin) = await NewAsync();
        await using var _ = f;
        var rows = await MediaRowsAsync(admin);
        Assert.Equal(["FLUX.2-klein-4B", "Wan2.2-TI2V-5B", "kokoro", "piper-fa", "whisper-large-v3-turbo"], rows.Select(r => r.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
        var flux = rows.Single(r => r.GetProperty("name").GetString() == MediaModels.ImageModel);
        Assert.True(flux.GetProperty("kept").GetBoolean());
        Assert.Equal(["diffusion", "llm", "vae"], flux.GetProperty("files").EnumerateArray().Select(x => x.GetProperty("role").GetString()));
        Assert.Equal("deepdml/faster-whisper-large-v3-turbo-ct2", rows.Single(r => r.GetProperty("name").GetString() == MediaModels.SpeechToText).GetProperty("speechId").GetString());
        Assert.False(rows.Single(r => r.GetProperty("name").GetString() == MediaModels.TextToSpeech).GetProperty("kept").GetBoolean());

        // Its own price, at the gateway and on the page; the others keep Settings -> Prices.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.MediaModels.Where(m => m.Name == MediaModels.ImageModel).ExecuteUpdateAsync(m => m.SetProperty(x => x.UnitPrice, 0.03m));
            await scope.ServiceProvider.GetRequiredService<MediaControl>().ChangedAsync(CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<ModelCatalog>().SyncGatewayAsync();
        }
        Assert.Equal(0.03m, gateway.Managed.Values.Single(m => m.Name == MediaModels.ImageModel).Params["input_cost_per_image"]!.GetValue<decimal>());
        Assert.Equal(0.015m / 1000, gateway.Managed.Values.Single(m => m.Name == MediaModels.TextToSpeech).Params["input_cost_per_character"]!.GetValue<decimal>());
        var price = (await MediaRowsAsync(admin)).Single(r => r.GetProperty("name").GetString() == MediaModels.ImageModel).GetProperty("unitPrice");
        Assert.Equal((0.03m, true), (price.GetProperty("amount").GetDecimal(), price.GetProperty("own").GetBoolean()));
        // Videos are made by the app, not the gateway.
        Assert.DoesNotContain(gateway.Managed.Values, m => m.Name == MediaModels.VideoModel);
    }

    [Fact]
    public async Task A_media_model_is_removed_as_a_chat_model_is_and_leaves_the_gateway()
    {
        var (f, gateway, admin) = await NewAsync();
        await using var _ = f;
        using (var scope = f.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ModelCatalog>().SyncGatewayAsync();
        }
        Assert.Contains(gateway.Managed.Values, m => m.Name == MediaModels.TextToSpeechPersian);
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.DeleteAsync(new Uri($"/api/admin/models/{MediaModels.TextToSpeechPersian}", UriKind.Relative)));
        Assert.DoesNotContain(await MediaRowsAsync(admin), r => r.GetProperty("name").GetString() == MediaModels.TextToSpeechPersian);
        Assert.DoesNotContain(gateway.Managed.Values, m => m.Name == MediaModels.TextToSpeechPersian);
        Assert.Contains(gateway.Managed.Values, m => m.Name == MediaModels.TextToSpeech);
        // Turning a model off is gone: removing it is how.
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/models/{MediaModels.TextToSpeech}/enabled", UriKind.Relative), new { enabled = false }));
    }

    [Fact]
    public void Its_row_runs_as_the_servers_ran_it_before()
    {
        var flux = new MediaModel
        {
            Name = "FLUX.2-klein-4B", Mode = MediaModes.Image, MaxVramGb = 2, FlashAttention = true, Steps = 4, CfgScale = 1.0,
            Files = [new() { Role = "diffusion", Path = "image/f/flux.gguf" }, new() { Role = "llm", Path = "image/f/qwen.gguf" }, new() { Role = "vae", Path = "image/f/vae.safetensors" }],
        };
        var sd = SdArgs.Of(flux);
        Assert.Equal(["--max-vram", "2", "--diffusion-fa", "--steps", "4", "--cfg-scale", "1"], sd.Args);
        Assert.Null(sd.VaeGpuMb);
        var conf = SdArgs.Conf(sd, 7, true);
        Assert.StartsWith("gen=7\nwant=on\nmodel=FLUX.2-klein-4B\n", conf, StringComparison.Ordinal);
        Assert.Contains("arg=--diffusion-model\narg=image/f/flux.gguf\narg=--llm\narg=image/f/qwen.gguf\narg=--vae\narg=image/f/vae.safetensors\n", conf, StringComparison.Ordinal);

        var wan = new MediaModel
        {
            Name = "Wan2.2-TI2V-5B", Mode = MediaModes.Video, MaxVramGb = 3, FlashAttention = true, VaeTiling = true, CfgScale = 5, FlowShift = 5,
            Sampler = "euler", Vae = "auto", VaeGpuGb = 12, ExtraArgs = "--seed 1\n--negative-prompt blurry, text",
            Files = [new() { Role = "diffusion", Path = "video/w/wan.gguf" }, new() { Role = "t5xxl", Path = "video/w/t5.gguf" }],
        };
        var video = SdArgs.Of(wan);
        Assert.Equal(["--max-vram", "3", "--diffusion-fa", "--vae-tiling", "--cfg-scale", "5", "--flow-shift", "5", "--sampling-method", "euler",
            "--seed", "1", "--negative-prompt", "blurry, text"], video.Args);
        // On the GPU when 13 GB is free as it loads (today's VAE_GPU_MB), with 12 GB and its weights in RAM; else on the CPU.
        Assert.Equal(13312, video.VaeGpuMb);
        Assert.Equal(["--max-vram", "12", "--offload-to-cpu"], video.VaeGpuArgs);
        Assert.Equal(["--vae-on-cpu"], video.VaeCpuArgs);
        Assert.Equal(["--vae-on-cpu"], SdArgs.Of(new MediaModel { Name = "x", Mode = MediaModes.Image, Vae = "cpu", FlashAttention = false }).Args);
    }

    [Fact]
    public async Task An_admin_adds_and_changes_picture_and_speech_models_and_bad_settings_are_refused()
    {
        var (f, gateway, admin) = await NewAsync();
        await using var _ = f;
        Task<HttpResponseMessage> Add(object body) => admin.Http.PostAsJsonAsync(new Uri("/api/admin/models/media", UriKind.Relative), body);
        Task<HttpResponseMessage> Edit(string name, object body) => admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/models/media/{name}", UriKind.Relative), body);
        var files = new[]
        {
            new { role = "diffusion", path = "image/flux2-klein-4b/flux-2-klein-4b-Q4_0.gguf" }, new { role = "llm", path = "image/flux2-klein-4b/Qwen3-4B-Q4_K_M.gguf" },
            new { role = "vae", path = "image/flux2-klein-4b/flux2-vae.safetensors" },
        };

        var added = await Add(new { name = "FLUX-8", mode = "image_generation", files, steps = 8, maxVramGb = 2, unitPrice = 0.02m, idleMinutes = 5 });
        await StatusAssert.Is(HttpStatusCode.Created, added);
        // Its files are not in this test's library: it waits for them, and says so.
        Assert.Contains("Not in the library yet", await added.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var row = (await MediaRowsAsync(admin)).Single(r => r.GetProperty("name").GetString() == "FLUX-8");
        Assert.Equal((8, 5), (row.GetProperty("settings").GetProperty("steps").GetInt32(), row.GetProperty("idleMinutes").GetInt32()));
        Assert.Equal(0.02m, gateway.Managed.Values.Single(m => m.Name == "FLUX-8").Params["input_cost_per_image"]!.GetValue<decimal>());

        async Task Refused(Task<HttpResponseMessage> call, string field)
        {
            var res = await call;
            Assert.True(res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict, $"{field}: {res.StatusCode}");
            Assert.Contains(field, await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        await Refused(Add(new { name = "x1", mode = "image_generation", files, extraArgs = "--listen-port 9" }), "extraArgs");
        await Refused(Add(new { name = "x2", mode = "image_generation", files, extraArgs = "--steps 3" }), "Steps");
        await Refused(Add(new { name = "x3", mode = "image_generation", files = new[] { new { role = "diffusion", path = "../etc/passwd.gguf" } } }), "files");
        await Refused(Add(new { name = "x4", mode = "image_generation", files = new[] { new { role = "bogus", path = "a/b.gguf" } } }), "files");
        await Refused(Add(new { name = "x5", mode = "image_generation", files = new[] { new { role = "vae", path = "a/vae.safetensors" } } }), "files");
        await Refused(Add(new { name = "kokoro", mode = "audio_speech", speechId = "speaches-ai/Kokoro-82M-v1.0-ONNX" }), "exists");
        await Refused(Add(new { name = "x6", mode = "audio_speech" }), "speechId");
        await Refused(Add(new { name = "x7", mode = "image_generation", files, unitPrice = -1 }), "unitPrice");
        await Refused(Add(new { name = "x8", mode = "painting", files }), "mode");

        // Changed: what is left out stays, what is cleared goes; decoding where there is room needs how much it takes there.
        await Refused(Edit("FLUX-8", new { vae = "auto" }), "vaeGpuGb");
        await StatusAssert.Is(HttpStatusCode.OK, await Edit("FLUX-8", new { vae = "auto", vaeGpuGb = 6, clear = new[] { "steps" } }));
        var changed = (await MediaRowsAsync(admin)).Single(r => r.GetProperty("name").GetString() == "FLUX-8").GetProperty("settings");
        Assert.Equal(JsonValueKind.Null, changed.GetProperty("steps").ValueKind);
        Assert.Equal(("auto", 6m, 2m), (changed.GetProperty("vae").GetString(), changed.GetProperty("vaeGpuGb").GetDecimal(), changed.GetProperty("maxVramGb").GetDecimal()));

        // A second text to speech model on the speech server's Kokoro, with one voice.
        await StatusAssert.Is(HttpStatusCode.Created, await Add(new { name = "kokoro-heart", mode = "audio_speech", speechId = "speaches-ai/Kokoro-82M-v1.0-ONNX", voices = new[] { "af_heart" } }));
        Assert.Equal(["af_heart"], (await MediaRowsAsync(admin)).Single(r => r.GetProperty("name").GetString() == "kokoro-heart").GetProperty("settings").GetProperty("voices").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public async Task The_chat_uses_the_picture_model_settings_name_then_the_first_and_only_those_a_person_may_use()
    {
        var gateway = new FakeGateway();
        await using var f = app.Create(app.ConnectionStringFor("mpick_" + Guid.NewGuid().ToString("N")[..8]), gateway, new Dictionary<string, string?>
        {
            ["Modules:imagegen"] = "true", ["Media:PictureModel"] = "FLUX-8",
        });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Created, await admin.Http.PostAsJsonAsync(new Uri("/api/admin/models/media", UriKind.Relative), new
        {
            name = "FLUX-8", mode = "image_generation", files = new[] { new { role = "diffusion", path = "image/flux2-klein-4b/flux-2-klein-4b-Q4_0.gguf" } }, steps = 8,
        }));
        var media = f.Services.GetRequiredService<MediaControl>();
        await media.RefreshAsync(CancellationToken.None);
        // Settings names FLUX-8; a person may name another; one not allowed is never picked.
        Assert.Equal("FLUX-8", media.Pick(MediaModes.Image)!.Name);
        Assert.Equal(MediaModels.ImageModel, media.Pick(MediaModes.Image, MediaModels.ImageModel)!.Name);
        Assert.Equal(MediaModels.ImageModel, media.Pick(MediaModes.Image, null, new HashSet<string> { MediaModels.ImageModel })!.Name);
        Assert.Null(media.Pick(MediaModes.Image, null, new HashSet<string>()));
        Assert.Equal(MediaModels.SpeechToText, media.Pick(MediaModes.Transcription)!.Name);
    }
}
