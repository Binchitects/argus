namespace Llm.Api.Models;

/// <summary>
/// The models of the picture, video, embedding and speech servers. The file servers read files
/// from the library and wait for them; the app fetches those on the first start (<see cref="Provisioning"/>).
/// The speech server fetches its own when the app asks. Paths match docker-compose.yml.
/// </summary>
public static class MediaModels
{
    public const string ImageDir = "image/flux2-klein-4b";
    public const string ImageTextEncoder = "Qwen3-4B-Q4_K_M.gguf";
    public const string VideoDir = "video/wan2.2-ti2v-5b";
    public const string EmbedDir = "embed";

    /// <summary>What the gateway and the chat call them.</summary>
    public const string ImageModel = "FLUX.2-klein-4B";
    public const string VideoModel = "Wan2.2-TI2V-5B";
    public const string SpeechToText = "whisper-large-v3-turbo";
    public const string TextToSpeech = "kokoro";
    public const string TextToSpeechPersian = "piper-fa";

    public const string ImageUrl = "http://imagegen:1234";
    public const string VideoUrl = "http://videogen:1234";
    public const string AudioUrl = "http://audio:8000";

    /// <summary>A file a server reads: from a Hugging Face repository into a folder of the library, by its own name.</summary>
    public sealed record ServerFile(string Repo, string Path, string Dir)
    {
        public string Name => System.IO.Path.GetFileName(Path);
    }

    /// <summary>Each file server by its name on the network, and its files.</summary>
    public static readonly IReadOnlyList<(string Server, IReadOnlyList<ServerFile> Files)> Servers =
    [
        ("imagegen",
        [
            new("leejet/FLUX.2-klein-4B-GGUF", "flux-2-klein-4b-Q4_0.gguf", ImageDir),
            new("unsloth/Qwen3-4B-GGUF", "Qwen3-4B-Q4_K_M.gguf", ImageDir),
            new("Comfy-Org/flux2-klein-4B", "split_files/vae/flux2-vae.safetensors", ImageDir),
        ]),
        ("embed",
        [
            new("nomic-ai/nomic-embed-text-v1.5-GGUF", "nomic-embed-text-v1.5.f16.gguf", EmbedDir),
        ]),
        ("videogen",
        [
            new("unsloth/Wan2.2-TI2V-5B-GGUF", "Wan2.2-TI2V-5B-Q4_K_M.gguf", VideoDir),
            new("city96/umt5-xxl-encoder-gguf", "umt5-xxl-encoder-Q4_K_M.gguf", VideoDir),
            new("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/vae/wan2.2_vae.safetensors", VideoDir),
        ]),
    ];

    /// <summary>The speech server's models (its own ids), by the name the gateway gives them.</summary>
    public static readonly IReadOnlyList<(string Name, string Id, string Mode)> Speech =
    [
        (SpeechToText, "deepdml/faster-whisper-large-v3-turbo-ct2", "audio_transcription"),
        (TextToSpeech, "speaches-ai/Kokoro-82M-v1.0-ONNX", "audio_speech"),
        // gyro over amir: Whisper wrote gyro's Persian back with a 3.8% character error rate, amir's with 14.4%.
        (TextToSpeechPersian, "speaches-ai/piper-fa_IR-gyro-medium", "audio_speech"),
    ];
}
