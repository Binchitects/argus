namespace Llm.Api.Models;

/// <summary>The default picture and video models: their files (as Provisioning fetches them) and how their servers run them.</summary>
public static class MediaRecipes
{
    private static string Image(string file) => $"{MediaModels.ImageDir}/{file}";
    private static string Video(string file) => $"{MediaModels.VideoDir}/{file}";

    public static readonly SdModel Flux = new(MediaModels.ImageModel, "imagegen",
        [new("diffusion", Image("flux-2-klein-4b-Q4_0.gguf")), new("llm", Image("Qwen3-4B-Q4_K_M.gguf")), new("vae", Image("flux2-vae.safetensors"))],
        ["--max-vram", "2", "--diffusion-fa", "--cfg-scale", "1.0", "--steps", "4"]);

    /// <summary>
    /// Decodes on the GPU when it has 13 GB free as the server loads, with a 12 GB budget then (the decode alone needs about
    /// 11) and the weights in RAM until each is needed; else on the CPU within its usual 3 GB.
    /// </summary>
    public static readonly SdModel Wan = new(MediaModels.VideoModel, "videogen",
        [new("diffusion", Video("Wan2.2-TI2V-5B-Q4_K_M.gguf")), new("t5xxl", Video("umt5-xxl-encoder-Q4_K_M.gguf")), new("vae", Video("wan2.2_vae.safetensors"))],
        ["--max-vram", "3", "--diffusion-fa", "--vae-tiling", "--cfg-scale", "5.0", "--flow-shift", "5.0", "--sampling-method", "euler"],
        VaeGpuMb: 13312, VaeGpuArgs: ["--max-vram", "12", "--offload-to-cpu"], VaeCpuArgs: ["--vae-on-cpu"]);

    public static readonly IReadOnlyList<SdModel> All = [Flux, Wan];

    public static SdModel? Of(string name) => All.FirstOrDefault(m => m.Name == name);
}
