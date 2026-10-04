using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Sound and video: what an upload becomes, what each kind of model gets, and reading answers aloud.</summary>
[Collection(nameof(AppCollection))]
public sealed class MediaTests(AppFixture app)
{
    private static readonly byte[] Mp3 = [0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0, 0, 9, 9, 9, 9];
    private static readonly byte[] Mp4 = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0];

    [Fact]
    public void Sounds_and_videos_are_known_by_their_first_bytes()
    {
        Assert.Equal(("audio", "audio/mpeg"), Media.Detect("a.mp3", null, Mp3));
        Assert.Equal(("video", "video/mp4"), Media.Detect("clip.mp4", "video/mp4", Mp4));
        // An MP4 container holding sound only: the browser's type or the name says so.
        Assert.Equal(("audio", "audio/mp4"), Media.Detect("voice.m4a", "audio/mp4", Mp4));
        byte[] webm = [0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3, 4, 5, 6, 7, 8];
        Assert.Equal(("audio", "audio/webm"), Media.Detect("Voice message.webm", "audio/webm;codecs=opus", webm));
        Assert.Equal(("video", "video/webm"), Media.Detect("screen.webm", "video/webm", webm));
        Assert.Equal(("audio", "audio/wav"), Media.Detect("a.wav", null, [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8]));
        Assert.Null(Media.Detect("notes.txt", "text/plain", "hello, world"u8.ToArray()));
    }

    [Fact]
    public void An_answer_is_read_without_its_code_or_marks_and_Persian_in_a_Persian_voice()
    {
        Assert.Equal("Hello there. See the docs.", Voices.Plain("**Hello** there.\n```python\nprint(1)\n```\nSee [the docs](https://example.com/docs)."));
        Assert.Equal((Llm.Api.Models.MediaModels.TextToSpeech, "af_heart"), Voices.For("Hello"));
        Assert.Equal((Llm.Api.Models.MediaModels.TextToSpeechPersian, "amir"), Voices.For("سلام، حال شما چطور است؟"));
    }

    /// <summary>
    /// services/sd-serve.sh's choice of where the video server decodes, run by /bin/sh with a fake
    /// nvidia-smi that reports <paramref name="free"/> MB (or fails, as when the GPU cannot be read).
    /// </summary>
    private static async Task<(string Vae, string Where)> PlaceVaeAsync(string? needs, string free, string? gpuFlags = null)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The servers' scripts run under /bin/sh.");
        }
        var script = Path.GetFullPath(Path.Combine(AppFixture.DashboardsPath, "..", "..", "..", "..", "deploy", "services", "sd-serve.sh"));
        var bin = Directory.CreateTempSubdirectory("fake-nvidia-");
        try
        {
            var smi = Path.Combine(bin.FullName, "nvidia-smi");
            // Only the question the script asks is answered; "fail" is a driver that cannot be reached.
            await File.WriteAllTextAsync(smi, $$"""
                #!/bin/sh
                [ "$*" = "--query-gpu=memory.free --format=csv,noheader,nounits" ] || exit 9
                [ "{{free}}" = fail ] && { echo "NVIDIA-SMI has failed" >&2; exit 9; }
                printf '%s\n' {{free}}
                """.Replace("\r", "", StringComparison.Ordinal));
            File.SetUnixFileMode(smi, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var run = new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", ". \"$0\"; place_vae; printf '%s|%s' \"$vae\" \"$where\"", script])
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            run.Environment["PATH"] = $"{bin.FullName}:/usr/bin:/bin";
            run.Environment["SD_SERVE_LIB"] = "1";
            run.Environment["NAME"] = "videogen";
            run.Environment.Remove("VAE_GPU_MB");
            run.Environment.Remove("VAE_GPU_FLAGS");
            if (needs is not null)
            {
                run.Environment["VAE_GPU_MB"] = needs;
            }
            if (gpuFlags is not null)
            {
                run.Environment["VAE_GPU_FLAGS"] = gpuFlags;
            }
            using var p = System.Diagnostics.Process.Start(run)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            Assert.True(p.ExitCode == 0, await p.StandardError.ReadToEndAsync());
            var parts = output.Split('|', 2);
            return (parts[0], parts[1]);
        }
        finally
        {
            bin.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Video_decodes_on_the_GPU_when_it_has_room_and_on_the_CPU_when_the_chat_model_holds_it()
    {
        // The chat model holds most of the GPU: decoding there would not fit.
        Assert.Equal(("--vae-on-cpu", " (decoding on the CPU: 2900 MB free of the 8192 it needs)"), await PlaceVaeAsync("8192", "2900"));
        // Room (on the first GPU, the one the server uses): on the GPU.
        Assert.Equal(("", " (decoding on the GPU: 12000 MB free)"), await PlaceVaeAsync("8192", "12000 24000"));
        Assert.Equal(("", " (decoding on the GPU: 8192 MB free)"), await PlaceVaeAsync("8192", "8192"));
        // On the GPU, the flags it needs there: a budget the decode fits in, the weights in RAM until needed.
        Assert.Equal(("--max-vram 12 --offload-to-cpu", " (decoding on the GPU: 20000 MB free)"), await PlaceVaeAsync("13312", "20000", "--max-vram 12 --offload-to-cpu"));
        Assert.Equal("--vae-on-cpu", (await PlaceVaeAsync("13312", "2187", "--max-vram 12 --offload-to-cpu")).Vae);
        // The GPU cannot be read: the CPU, which always fits.
        Assert.Equal(("--vae-on-cpu", " (decoding on the CPU: unknown MB free of the 8192 it needs)"), await PlaceVaeAsync("8192", "fail"));
        Assert.Equal("--vae-on-cpu", (await PlaceVaeAsync("8192", "[N/A]")).Vae);
        // The picture server sets no VAE_GPU_MB: its flags stay as they are.
        Assert.Equal(("", ""), await PlaceVaeAsync(null, "100"));
    }

    private async Task<(WebApplicationFactory<Program> App, TestBrowser Browser, FakeSandbox Sandbox)> NewAppAsync()
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel("Omni", 32768, 8192, Vision: true, Tools: true, Thinking: false, null, null, null, Audio: true));
        gateway.Models.Add(new GatewayModel(Llm.Api.Models.MediaModels.SpeechToText, null, null, false, false, false, null, null, null, Mode: "audio_transcription"));
        gateway.Models.Add(new GatewayModel(Llm.Api.Models.MediaModels.TextToSpeech, null, null, false, false, false, null, null, null, Mode: "audio_speech"));
        var sandbox = new FakeSandbox();
        var f = app.Create(app.ConnectionStringFor("media_" + Guid.NewGuid().ToString("N")[..8]), gateway, new Dictionary<string, string?> { ["Sandbox:Dir"] = sandbox.Dir });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "m" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (f, await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), sandbox);
    }

    private static async Task<JsonElement> UploadAsync(TestBrowser b, string name, string type, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", name);
        var res = await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form);
        await StatusAssert.Is(HttpStatusCode.OK, res);
        return await b.JsonAsync(res);
    }

    private async Task<JsonObject> AskAsync(TestBrowser b, string model, Guid attachment)
    {
        var chat = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { model, useArgus = false }))).GetProperty("id").GetGuid();
        var marker = "media-" + Guid.NewGuid().ToString("N")[..8];
        var res = await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = marker, attachments = new[] { attachment } });
        await res.Content.ReadAsStringAsync();
        return app.Model.Requests.Last(r => r.Body.ToJsonString().Contains(marker, StringComparison.Ordinal)).Body;
    }

    private static JsonArray LastUserParts(JsonObject request) =>
        request["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"] as JsonArray ?? [];

    [Fact]
    public async Task A_voice_message_is_heard_by_a_model_that_hears_and_read_as_a_transcript_by_one_that_does_not()
    {
        var (f, b, sandbox) = await NewAppAsync();
        await using var _f = f;
        await using var _s = sandbox;
        var voice = await UploadAsync(b, "Voice message 10.00.00.webm", "audio/webm", [0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Equal("audio", voice.GetProperty("kind").GetString());
        Assert.Equal("audio/mpeg", voice.GetProperty("contentType").GetString());
        Assert.Equal("Voice message 10.00.00.mp3", voice.GetProperty("fileName").GetString());
        var id = voice.GetProperty("id").GetGuid();

        // Played in place, with ranges for seeking.
        var played = await b.GetAsync($"/api/chat/attachments/{id}/content");
        Assert.Equal("audio/mpeg", played.Content.Headers.ContentType!.MediaType);
        Assert.Equal("bytes", played.Headers.AcceptRanges.Single());

        // A model that hears gets the sound itself.
        var heard = LastUserParts(await AskAsync(b, "Omni", id));
        Assert.Contains(heard, p => p!["type"]!.GetValue<string>() == "input_audio" && p["input_audio"]!["format"]!.GetValue<string>() == "mp3");

        // One that does not gets what was said, written down once.
        var before = app.Model.Transcriptions.Count;
        var request = await AskAsync(b, "Qwen3.8-Flash-Next", id);
        var text = request["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.ToJsonString();
        Assert.Contains("What is the capital of France?", text, StringComparison.Ordinal);
        Assert.DoesNotContain("input_audio", text, StringComparison.Ordinal);
        await AskAsync(b, "Qwen3.8-Flash-Next", id);
        Assert.Equal(before + 1, app.Model.Transcriptions.Count);
    }

    [Fact]
    public async Task A_video_goes_as_its_frames_to_a_model_that_sees_with_its_sound()
    {
        var (f, b, sandbox) = await NewAppAsync();
        await using var _f = f;
        await using var _s = sandbox;
        var video = await UploadAsync(b, "clip.mp4", "video/mp4", Mp4);
        Assert.Equal("video", video.GetProperty("kind").GetString());
        Assert.Equal(4.0, video.GetProperty("seconds").GetDouble());
        var parts = LastUserParts(await AskAsync(b, "Omni", video.GetProperty("id").GetGuid()));
        Assert.Equal(2, parts.Count(p => p!["type"]!.GetValue<string>() == "image_url"));
        Assert.Single(parts, p => p!["type"]!.GetValue<string>() == "input_audio");
        Assert.Contains("2 frames below, taken at 1 s, 3 s", parts[0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_is_read_aloud_in_the_persons_name()
    {
        var (f, b, sandbox) = await NewAppAsync();
        await using var _f = f;
        await using var _s = sandbox;
        var res = await b.PostAsync("/api/chat/speech", new { text = "**Paris** is the capital." });
        await StatusAssert.Is(HttpStatusCode.OK, res);
        Assert.Equal("audio/mpeg", res.Content.Headers.ContentType!.MediaType);
        var asked = app.Model.SpeechRequests.Last();
        Assert.Equal("Paris is the capital.", asked["input"]!.GetValue<string>());
        Assert.Equal("af_heart", asked["voice"]!.GetValue<string>());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/chat/speech", new { text = "```\ncode only\n```" }));
    }

    [Fact]
    public async Task Picture_video_and_speech_models_are_turned_on_and_off_and_kept_like_the_chat_models()
    {
        var (f, _, sandbox) = await NewAppAsync();
        await using var _f = f;
        await using var _s = sandbox;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        async Task<Dictionary<string, JsonElement>> MediaAsync() =>
            (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("models").EnumerateArray()
                .Where(m => m.GetProperty("source").GetString() == "media").ToDictionary(m => m.GetProperty("name").GetString()!);
        var rows = await MediaAsync();
        Assert.Equal(5, rows.Count);
        Assert.True(rows["FLUX.2-klein-4B"].GetProperty("kept").GetBoolean());
        Assert.False(rows["Wan2.2-TI2V-5B"].GetProperty("kept").GetBoolean());
        // No such server in tests: off.
        Assert.Equal("off", rows["Wan2.2-TI2V-5B"].GetProperty("status").GetString());

        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/models/Wan2.2-TI2V-5B/keep", UriKind.Relative), new { keep = true }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/models/FLUX.2-klein-4B/enabled", UriKind.Relative), new { enabled = false }));
        rows = await MediaAsync();
        Assert.True(rows["Wan2.2-TI2V-5B"].GetProperty("kept").GetBoolean());
        Assert.False(rows["FLUX.2-klein-4B"].GetProperty("enabled").GetBoolean());
        // Off, it does not load; and who may use it is set like any model's.
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync("/api/admin/models/FLUX.2-klein-4B/load", new { }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/models/kokoro/access", UriKind.Relative), new { audience = "Everyone", groups = Array.Empty<Guid>() }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/models/no-such/enabled", UriKind.Relative), new { enabled = true }));
    }

    [Fact]
    public async Task A_model_the_gateway_does_not_know_yet_or_still_loading_is_waited_for()
    {
        var (f, b, sandbox) = await NewAppAsync();
        await using var _f = f;
        await using var _s = sandbox;
        app.Model.UnknownOnce["Omni"] = true;
        app.Model.LoadingOnce["Omni"] = true;
        var chat = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { model = "Omni", useArgus = false }))).GetProperty("id").GetGuid();
        var answer = await (await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "hello after a first start" })).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Invalid model name", answer, StringComparison.Ordinal);
        Assert.Contains(app.Model.Requests, r => r.Body.ToJsonString().Contains("hello after a first start", StringComparison.Ordinal));
    }
}
