using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Llm.Api.Models;

namespace Llm.Api.Media;

/// <summary>
/// The picture and video servers' own API (stable-diffusion.cpp's sdcpp/v1): a job sent, then asked about every few seconds
/// until it is done (the answer stopping cancels it); pictures from words, from a picture (changed by words, or a little
/// different), clips that start from a picture, and pictures enlarged by its upscaler.
/// </summary>
public sealed class SdServer(IHttpClientFactory http, MediaControl media)
{
    public const string Client = "sd";

    public sealed class SdException(string message) : Exception(message);

    private static string UrlOf(MediaControl.Model model) => model.Mode == Llm.Core.Models.MediaModes.Video ? MediaModels.VideoUrl : MediaModels.ImageUrl;

    /// <summary>A picture as the server takes it: a data URL.</summary>
    public static string DataUrl(byte[] image, string type = "image/png") => $"data:{type};base64,{Convert.ToBase64String(image)}";

    /// <summary>
    /// Pictures from the picture model: from words; from <paramref name="start"/> a little different (<paramref name="strength"/>,
    /// 0 to 1: how far from it); or <paramref name="references"/> changed as the words say.
    /// </summary>
    public async Task<IReadOnlyList<byte[]>> PicturesAsync(MediaControl.Model model, string prompt, int width, int height, int count, CancellationToken ct,
        string? start = null, double? strength = null, IReadOnlyList<string>? references = null)
    {
        var m = model.Row;
        var body = new JsonObject
        {
            ["prompt"] = prompt, ["width"] = width, ["height"] = height, ["seed"] = -1, ["batch_count"] = Math.Clamp(count, 1, 4), ["output_format"] = "png",
            // The model's own settings, else the server's (its arguments say them).
            ["sample_params"] = SampleParams(m, m.Steps, m.CfgScale, m.FlowShift, m.Sampler),
        };
        if (m.NegativePrompt is { Length: > 0 } negative)
        {
            body["negative_prompt"] = negative;
        }
        if (start is not null)
        {
            body["init_image"] = start;
            body["strength"] = Math.Clamp(strength ?? 0.6, 0.05, 1.0);
        }
        if (references is { Count: > 0 })
        {
            body["ref_images"] = new JsonArray([.. references.Select(r => (JsonNode)r)]);
        }
        var result = await JobAsync(model, "img_gen", body, ct);
        var images = (result["images"] as JsonArray ?? []).Select(i => i?["b64_json"]?.GetValue<string>()).OfType<string>().Select(Convert.FromBase64String).ToList();
        return images.Count == 0 ? throw new SdException("the picture server sent no picture") : images;
    }

    /// <summary>A clip of <paramref name="seconds"/> from the video model; from <paramref name="start"/> (its first frame) when given.</summary>
    public async Task<(byte[] Video, string Type)> VideoAsync(MediaControl.Model model, string prompt, int seconds, CancellationToken ct, string? start = null,
        (int Width, int Height)? size = null)
    {
        var m = model.Row;
        var fps = m.Fps ?? 16;
        // Wan takes 4n + 1 frames.
        var frames = seconds * fps / 4 * 4 + 1;
        var (width, height) = size ?? (m.Width ?? 640, m.Height ?? 352);
        var body = new JsonObject
        {
            ["prompt"] = prompt, ["negative_prompt"] = m.NegativePrompt ?? "blurry, distorted, low quality, watermark, text", ["width"] = width,
            ["height"] = height, ["seed"] = -1, ["video_frames"] = frames, ["fps"] = fps, ["output_format"] = "webm",
            ["sample_params"] = SampleParams(m, m.Steps ?? 20, m.CfgScale ?? 5.0, m.FlowShift ?? 5.0, m.Sampler ?? "euler"),
        };
        if (start is not null)
        {
            body["init_image"] = start;
        }
        var result = await JobAsync(model, "vid_gen", body, ct);
        return result["b64_json"]?.GetValue<string>() is { } b64
            ? (Convert.FromBase64String(b64), result["mime_type"]?.GetValue<string>() ?? "video/webm")
            : throw new SdException("the video server sent no clip");
    }

    /// <summary>The video model's size for a picture of this shape: its own, turned to stand when the picture is taller than wide.</summary>
    public static (int Width, int Height) VideoSize(MediaControl.Model model, int pictureWidth, int pictureHeight)
    {
        var (w, h) = (model.Row.Width ?? 640, model.Row.Height ?? 352);
        var (wide, tall) = (Math.Max(w, h), Math.Min(w, h));
        return pictureHeight > pictureWidth * 1.1 ? (tall, wide) : pictureWidth > pictureHeight * 1.1 ? (wide, tall) : (tall + (wide - tall) / 3 / 16 * 16, tall + (wide - tall) / 3 / 16 * 16);
    }

    /// <summary>Whether the picture server has an upscaler (a model in its upscalers' folder).</summary>
    public async Task<bool> CanEnlargeAsync(CancellationToken ct)
    {
        try
        {
            var c = await http.CreateClient(Client).GetFromJsonAsync<JsonObject>(MediaModels.ImageUrl + "/sdcpp/v1/capabilities", ct);
            return c?["features"]?["upscale"]?.GetValue<bool>() == true
                || (c?["upscalers"] as JsonArray ?? []).Any(u => u?["image_upscale"]?.GetValue<bool>() == true);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>A picture four times as large, by the picture server's upscaler (no generation: quick, answered at once).</summary>
    public async Task<(byte[] Png, int Width, int Height)> EnlargeAsync(MediaControl.Model model, byte[] image, CancellationToken ct)
    {
        media.Touch(model.Name);
        using var res = await http.CreateClient(Client).PostAsJsonAsync(MediaModels.ImageUrl + "/sdcpp/v1/upscale",
            new JsonObject { ["image"] = DataUrl(image), ["repeats"] = 1, ["output_format"] = "png" }, ct);
        var body = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
        if (!res.IsSuccessStatusCode || body?["images"]?[0]?["b64_json"]?.GetValue<string>() is not { } b64)
        {
            throw new SdException(ErrorOf(body) ?? $"the picture server answered HTTP {(int)res.StatusCode}");
        }
        return (Convert.FromBase64String(b64), body["width"]?.GetValue<int>() ?? 0, body["height"]?.GetValue<int>() ?? 0);
    }

    private static JsonObject SampleParams(Llm.Core.Models.MediaModel m, int? steps, double? cfg, double? shift, string? sampler)
    {
        var guidance = new JsonObject();
        var p = new JsonObject { ["guidance"] = guidance };
        if (steps is { } n)
        {
            p["sample_steps"] = n;
        }
        if (cfg is { } c)
        {
            guidance["txt_cfg"] = c;
        }
        if (m.Guidance is { } g)
        {
            guidance["distilled_guidance"] = g;
        }
        if (sampler is { Length: > 0 })
        {
            p["sample_method"] = sampler;
        }
        if (m.Scheduler is { Length: > 0 } scheduler)
        {
            p["scheduler"] = scheduler;
        }
        if (shift is { } s)
        {
            p["flow_shift"] = s;
        }
        return p;
    }

    private static string? ErrorOf(JsonNode? body) => body?["error"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonObject o => o["message"]?.GetValue<string>(),
        _ => null,
    };

    /// <summary>One job at the server, asked about every few seconds until it is done; its result.</summary>
    private async Task<JsonNode> JobAsync(MediaControl.Model model, string kind, JsonObject body, CancellationToken ct)
    {
        var client = http.CreateClient(Client);
        var url = UrlOf(model);
        using var submitted = await client.PostAsJsonAsync($"{url}/sdcpp/v1/{kind}", body, ct);
        var job = await submitted.Content.ReadFromJsonAsync<JsonObject>(ct);
        if (!submitted.IsSuccessStatusCode || job?["poll_url"]?.GetValue<string>() is not { } poll)
        {
            throw new SdException(ErrorOf(job) ?? $"the server answered HTTP {(int)submitted.StatusCode}");
        }
        try
        {
            while (true)
            {
                await Task.Delay(kind == "vid_gen" ? TimeSpan.FromSeconds(3) : TimeSpan.FromMilliseconds(700), ct);
                // In use while it works: not unloaded as idle under a long job.
                media.Touch(model.Name);
                var now = await client.GetFromJsonAsync<JsonObject>(url + poll, ct);
                switch (now?["status"]?.GetValue<string>())
                {
                    case "completed":
                        return now["result"] ?? throw new SdException("the server sent no result");
                    case "failed" or "cancelled":
                        throw new SdException(ErrorOf(now) ?? "the server gave up");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            using var _ = await client.PostAsync(url + poll + "/cancel", null, CancellationToken.None);
            throw;
        }
    }
}
