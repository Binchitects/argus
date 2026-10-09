using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Models;
using Llm.Core.Chat;
using Llm.Core.Data;

namespace Llm.Api.Chat.Tools;

/// <summary>Which text to speech model and voice say a text, and the text as it is said (no Markdown, no code).</summary>
public static partial class Voices
{
    public const int MaxChars = 4000;

    /// <summary>Persian in the Persian voice, anything else in Kokoro's.</summary>
    public static (string Model, string Voice) For(string text) =>
        Persian(text) ? (MediaModels.TextToSpeechPersian, "amir") : (MediaModels.TextToSpeech, "af_heart");

    private static bool Persian(string text) =>
        text.Count(c => c is >= '؀' and <= 'ۿ') > text.Count(c => c is >= 'A' and <= 'z');

    /// <summary>What is read aloud of an answer: its prose, without code, links' addresses or Markdown's marks.</summary>
    public static string Plain(string markdown)
    {
        var text = CodeBlock().Replace(markdown, " ");
        text = Link().Replace(text, "$1");
        text = Url().Replace(text, " ");
        text = Marks().Replace(text, " ");
        text = Space().Replace(text, " ").Trim();
        return text.Length > MaxChars ? text[..MaxChars] : text;
    }

    [GeneratedRegex(@"```[\s\S]*?(```|$)|<think>[\s\S]*?</think>")]
    private static partial Regex CodeBlock();

    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex Url();

    [GeneratedRegex(@"[#*_`>|~]+|\[\d+\]")]
    private static partial Regex Marks();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();
}

/// <summary>Short videos from the stack's video server (Wan2.2 TI2V 5B), kept as the person's files.</summary>
/// <remarks>The video server is not behind the gateway: each clip is booked in the gateway's request log by the app, at its price per second.</remarks>
public sealed partial class VideoTool(IHttpClientFactory http, Operations.Modules modules, AppDbContext db, Safeguards.Safeguards safeguards, MediaControl media,
    Gateway.PriceBook prices, Gateway.SpendLog spendLog, ChatKey chatKey, Storage.StorageQuotas quotas) : IChatTool
{
    public const string Client = "videogen";
    private const int Fps = 16;

    public string Id => "video";
    public string Title => "Video generation";
    public string Description => "Makes short video clips from a description, with the stack's video model.";
    public string Icon => "video";

    public async Task<string?> UnavailableAsync(CancellationToken ct) =>
        !await modules.HasAsync("videogen", ct) ? "The video server does not run here (the videogen module)."
        : !(await media.StateAsync(MediaModels.VideoModel, ct)).Enabled ? "The video model is turned off (Admin -> Models)." : null;

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [Schema.Function("generate_video", "Makes a short video clip (1 to 5 seconds, no sound) from a description and shows it to the person. " +
            "Write the prompt in English, concrete and visual: subject, action, setting, camera movement, style, lighting.",
            new JsonObject
            {
                ["prompt"] = Schema.Text("What happens in the clip, in detail"),
                ["seconds"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5, ["description"] = "length; default 3" },
            }, "prompt")],
        "When the person asks for a video or an animation, call generate_video. It takes a few minutes; the clip is shown to them " +
        "under your answer: do not repeat it or link it; say in a sentence what you made.",
        async (_, args, token) =>
        {
            var prompt = (Schema.Str(args, "prompt") ?? "").Trim();
            if (prompt.Length == 0)
            {
                return new ToolResult("Say what the clip shows in 'prompt'.", IsError: true);
            }
            var seconds = Math.Clamp(args["seconds"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 3, 1, 5);
            if (await quotas.ToolRefusalAsync(context.User.Id, "video", token) is { } full)
            {
                return new ToolResult(full, IsError: true);
            }
            if (await safeguards.TakeImageAsync(context.User.Id, token) is { } limit)
            {
                return new ToolResult(limit, IsError: true);
            }
            // Loaded when it is not: a few minutes the first time.
            if (await media.ReadyAsync(MediaModels.VideoModel, token) is { } notReady)
            {
                return new ToolResult(notReady, IsError: true);
            }
            byte[] video;
            string type;
            var started = DateTimeOffset.UtcNow;
            try
            {
                (video, type) = await GenerateAsync(prompt, seconds, token);
                media.Touch(MediaModels.VideoModel);
            }
            catch (Exception ex) when (ex is HttpRequestException or VideoException or System.Text.Json.JsonException)
            {
                return new ToolResult($"The video could not be made: {ex.Message}", IsError: true);
            }
            var cost = prices.Video(seconds);
            await spendLog.BookVideoAsync(context.Email, Gateway.AnswerCache.HashOf(await chatKey.GetAsync(token)), seconds, cost, started, DateTimeOffset.UtcNow, token);
            var file = new ChatAttachment
            {
                UserId = context.User.Id, FileName = Path.ChangeExtension(ImageTool.FileName(prompt), type == "video/webm" ? ".webm" : ".avi"),
                ContentType = type, Size = video.Length, Kind = "video", Data = video, Text = "", Seconds = seconds,
            };
            db.ChatAttachments.Add(file);
            await db.SaveChangesAsync(token);
            return new ToolResult(new JsonObject
            {
                ["shown_to_the_person"] = true, ["file"] = file.FileName, ["seconds"] = seconds, ["model"] = MediaModels.VideoModel,
            }.ToJsonString(Mcp.Plain), Files: [file]) { Cost = cost };
        }));

    private sealed class VideoException(string message) : Exception(message);

    /// <summary>One clip: a job at the server, asked about every few seconds until it is done (or the answer stops, which cancels it).</summary>
    private async Task<(byte[] Video, string Type)> GenerateAsync(string prompt, int seconds, CancellationToken ct)
    {
        var client = http.CreateClient(Client);
        // Wan takes 4n + 1 frames.
        var frames = seconds * Fps / 4 * 4 + 1;
        var body = new JsonObject
        {
            ["prompt"] = prompt, ["negative_prompt"] = "blurry, distorted, low quality, watermark, text", ["width"] = 640, ["height"] = 352, ["seed"] = -1,
            ["video_frames"] = frames, ["fps"] = Fps, ["output_format"] = "webm",
            ["sample_params"] = new JsonObject
            {
                ["sample_method"] = "euler", ["sample_steps"] = 20, ["flow_shift"] = 5.0, ["guidance"] = new JsonObject { ["txt_cfg"] = 5.0 },
            },
        };
        using var submitted = await client.PostAsJsonAsync(MediaModels.VideoUrl + "/sdcpp/v1/vid_gen", body, ct);
        var job = await submitted.Content.ReadFromJsonAsync<JsonObject>(ct);
        if (!submitted.IsSuccessStatusCode || job?["poll_url"]?.GetValue<string>() is not { } poll)
        {
            throw new VideoException(job?["error"]?["message"]?.GetValue<string>() ?? $"the server answered HTTP {(int)submitted.StatusCode}");
        }
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                // In use while it works: not unloaded as idle under a long job.
                media.Touch(MediaModels.VideoModel);
                var now = await client.GetFromJsonAsync<JsonObject>(MediaModels.VideoUrl + poll, ct);
                switch (now?["status"]?.GetValue<string>())
                {
                    case "completed":
                        var result = now["result"]!;
                        return (Convert.FromBase64String(result["b64_json"]!.GetValue<string>()), result["mime_type"]?.GetValue<string>() ?? "video/webm");
                    case "failed" or "cancelled":
                        throw new VideoException(now["error"]?["message"]?.GetValue<string>() ?? "the server gave up");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            using var _ = await client.PostAsync(MediaModels.VideoUrl + poll + "/cancel", null, CancellationToken.None);
            throw;
        }
    }
}

/// <summary>A text read aloud into a sound file, by the gateway's text to speech.</summary>
public sealed class SpeechTool(ChatModels models, GatewayChat gateway, AppDbContext db, Gateway.PriceBook prices) : IChatTool
{
    public string Id => "speech";
    public string Title => "Speech";
    public string Description => "Reads a text aloud into a sound file (MP3), in English or Persian.";
    public string Icon => "audio-lines";

    public async Task<string?> UnavailableAsync(CancellationToken ct) =>
        await models.OfModeAsync("audio_speech", null, ct) is null ? "The gateway has no text to speech model (the audio module)." : null;

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [Schema.Function("speak", "Reads a text aloud into an MP3 file and gives it to the person: a voice-over, an announcement, a pronunciation.",
            new JsonObject
            {
                ["text"] = Schema.Text($"What to say, as it should be spoken (at most {Voices.MaxChars} characters)"),
                ["name"] = Schema.Text("A short file name, without the extension"),
            }, "text")],
        "When the person asks to hear something or for a sound file of a text, call speak. The file is shown to them under your answer.",
        async (_, args, token) =>
        {
            var text = Voices.Plain(Schema.Str(args, "text") ?? "");
            if (text.Length == 0)
            {
                return new ToolResult("Give the words to say in 'text'.", IsError: true);
            }
            var (model, voice) = Voices.For(text);
            byte[] mp3;
            try
            {
                mp3 = await gateway.SpeakAsync(model, text, voice, context.Email, token);
            }
            catch (ChatGatewayException ex)
            {
                return new ToolResult(ex.Message, IsError: true);
            }
            var name = Schema.Str(args, "name") is { Length: > 0 } given ? given : text;
            var file = new ChatAttachment
            {
                UserId = context.User.Id, FileName = Path.ChangeExtension(ImageTool.FileName(name), ".mp3"), ContentType = "audio/mpeg",
                Size = mp3.Length, Kind = "audio", Data = mp3, Text = text,
            };
            db.ChatAttachments.Add(file);
            await db.SaveChangesAsync(token);
            return new ToolResult(new JsonObject
            {
                ["shown_to_the_person"] = true, ["file"] = file.FileName, ["voice"] = string.Create(CultureInfo.InvariantCulture, $"{model}/{voice}"),
            }.ToJsonString(Mcp.Plain), Files: [file]) { Cost = prices.Speech(text.Length) };
        }));
}
