namespace Llm.Core.Models;

/// <summary>What a media model does, as the gateway names it.</summary>
public static class MediaModes
{
    public const string Image = "image_generation", Video = "video_generation", Transcription = "audio_transcription", Speech = "audio_speech";

    public static readonly IReadOnlyList<string> All = [Image, Video, Transcription, Speech];

    /// <summary>The server that runs a model of this mode.</summary>
    public static string Server(string mode) => mode switch
    {
        Image => "imagegen",
        Video => "videogen",
        _ => "audio",
    };
}

/// <summary>A file a picture or video model reads: its role (the server's flag for it) and its path in the library.</summary>
public sealed class MediaModelFile
{
    /// <summary>diffusion, model, llm, llm_vision, t5xxl, clip_l, clip_g, clip_vision, vae, taesd or high_noise.</summary>
    public required string Role { get; set; }
    public required string Path { get; set; }
}

/// <summary>
/// A picture, video or speech model, managed as chat models are: its files (or the speech server's id for it), how it
/// runs and what it makes by default, the GPU it uses, how long it stays loaded unused, whether it is kept loaded, and
/// its price. The app writes its server's control file from it (pictures and video) or loads it at the speech server.
/// </summary>
public sealed class MediaModel
{
    /// <summary>Its name at the gateway and in the chat.</summary>
    public required string Name { get; set; }
    /// <summary><see cref="MediaModes"/>.</summary>
    public required string Mode { get; set; }
    /// <summary>Pictures and video: its files, by role, in the library.</summary>
    public List<MediaModelFile> Files { get; set; } = [];
    /// <summary>Speech: the speech server's id for it (a Hugging Face repository).</summary>
    public string? SpeechId { get; set; }

    // What a picture or video is made with, unless a request says otherwise.
    public int? Steps { get; set; }
    public double? CfgScale { get; set; }
    public double? Guidance { get; set; }
    public double? FlowShift { get; set; }
    public string? Sampler { get; set; }
    public string? Scheduler { get; set; }
    public string? NegativePrompt { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    /// <summary>Video: frames a second, and the longest clip in seconds.</summary>
    public int? Fps { get; set; }
    public int? MaxSeconds { get; set; }

    // Speech.
    /// <summary>Text to speech: the voices offered (empty: all the model has), the default one, and its speed.</summary>
    public List<string> Voices { get; set; } = [];
    public string? DefaultVoice { get; set; }
    public double? Speed { get; set; }
    /// <summary>The language it speaks or hears, when it is one (fa, en); null: what it is asked for.</summary>
    public string? Language { get; set; }

    // The GPU (pictures and video).
    /// <summary>The GPU it runs on, as nvidia-smi numbers them; null: every GPU its server sees.</summary>
    public int? Gpu { get; set; }
    /// <summary>The most GPU memory its weights take (stable-diffusion.cpp --max-vram), in GB; null: what is free.</summary>
    public decimal? MaxVramGb { get; set; }
    /// <summary>Where it decodes: "auto" (on the GPU when it has <see cref="VaeGpuGb"/> free as it loads, else on the CPU), "gpu" or "cpu".</summary>
    public string Vae { get; set; } = "gpu";
    /// <summary>"auto": the GPU memory decoding there needs, and the budget it gets then.</summary>
    public decimal? VaeGpuGb { get; set; }
    /// <summary>Its weights in RAM until each is needed (--offload-to-cpu).</summary>
    public bool OffloadToCpu { get; set; }
    /// <summary>Decoding in tiles, for less memory (--vae-tiling).</summary>
    public bool VaeTiling { get; set; }
    /// <summary>Flash attention in its diffusion model (--diffusion-fa).</summary>
    public bool FlashAttention { get; set; } = true;
    /// <summary>More of its server's options, one "--flag value" a line.</summary>
    public string? ExtraArgs { get; set; }

    /// <summary>Loaded at start and kept loaded, as a chat model can be.</summary>
    public bool Kept { get; set; }
    /// <summary>Unloaded after this many minutes unused (not kept loaded); null: 10.</summary>
    public int? IdleMinutes { get; set; }
    /// <summary>Its price: per picture, per second of video, per minute of sound heard, per 1,000 characters read aloud; null: Settings → Prices.</summary>
    public decimal? UnitPrice { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string Server => MediaModes.Server(Mode);
}
