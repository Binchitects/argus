namespace Llm.Core.Models;

/// <summary>
/// What a kind of engine can do. The app offers a model only the settings its engine's kind has: tensor parallelism over
/// GPUs, sleeping to lend its GPUs, a prefix cache shared by every request, slots a conversation keeps, layers in RAM, the
/// token cache's tiers beyond the GPU, and the GPU backends and model formats it runs.
/// </summary>
/// <param name="Managed">The app starts and stops its models (else it only registers them and checks they answer).</param>
/// <param name="TensorParallel">One model over several GPUs, each holding a slice of every layer.</param>
/// <param name="Sleep">Weights to RAM and back in seconds, so its GPUs can be lent without a reload.</param>
/// <param name="PrefixCache">Any request reuses any cached prefix: no slot pinning is needed.</param>
/// <param name="SlotPinning">Each conversation keeps the slot that holds its start (llama.cpp's slots).</param>
/// <param name="SpillToRam">Layers or experts go to RAM when the GPUs are short (slower, not refused).</param>
/// <param name="Replicas">Several copies of one model, each on its own GPUs.</param>
/// <param name="KvTiers">Tiers of the token cache beyond the GPU: "cpu", "lmcache".</param>
/// <param name="Backends">The GPU backends it runs on: cuda, rocm, vulkan, sycl, metal, cpu.</param>
/// <param name="Formats">The model formats it loads: gguf, safetensors.</param>
public sealed record EngineCapabilities(string Kind, string Label, bool Managed, bool TensorParallel, bool Sleep, bool PrefixCache, bool SlotPinning,
    bool SpillToRam, bool Replicas, IReadOnlyList<string> KvTiers, IReadOnlyList<string> Backends, IReadOnlyList<string> Formats);

/// <summary>
/// The kinds of engine. A new one is a new entry here, a backend in Llm.Api/Models (IEngineBackend: list, load, unload,
/// sleep, capacity, health), and its advisor; nothing else asks which kind a model is beyond these. vLLM and SGLang are
/// one server a model, run by the same supervisor (<see cref="ServerPerModel"/>): only their arguments and metrics differ.
/// </summary>
public static class EngineKinds
{
    public const string LlamaCpp = "llamacpp";
    public const string Vllm = "vllm";
    public const string Sglang = "sglang";
    /// <summary>An OpenAI-compatible server the app does not manage (another vLLM, SGLang, TGI, Ollama, a cloud): Admin → Models → Other GPU servers.</summary>
    public const string External = "external";

    public static readonly EngineCapabilities LlamaCppCapabilities = new(LlamaCpp, "llama.cpp", Managed: true, TensorParallel: false, Sleep: false,
        PrefixCache: false, SlotPinning: true, SpillToRam: true, Replicas: false, KvTiers: [], Backends: ["cuda", "rocm", "vulkan", "sycl", "metal", "cpu"], Formats: ["gguf"]);

    public static readonly EngineCapabilities VllmCapabilities = new(Vllm, "vLLM", Managed: true, TensorParallel: true, Sleep: true,
        PrefixCache: true, SlotPinning: false, SpillToRam: false, Replicas: true, KvTiers: ["cpu"], Backends: ["cuda", "rocm"], Formats: ["safetensors"]);

    /// <summary>SGLang: its radix cache shares every prefix, its memory saver sleeps a model, its hierarchical cache keeps tokens in RAM.</summary>
    public static readonly EngineCapabilities SglangCapabilities = new(Sglang, "SGLang", Managed: true, TensorParallel: true, Sleep: true,
        PrefixCache: true, SlotPinning: false, SpillToRam: false, Replicas: true, KvTiers: ["cpu"], Backends: ["cuda", "rocm"], Formats: ["safetensors"]);

    public static readonly EngineCapabilities ExternalCapabilities = new(External, "Another server (OpenAI-compatible)", Managed: false, TensorParallel: false,
        Sleep: false, PrefixCache: false, SlotPinning: false, SpillToRam: false, Replicas: false, KvTiers: [], Backends: [], Formats: []);

    public static IReadOnlyList<EngineCapabilities> All { get; } = [LlamaCppCapabilities, VllmCapabilities, SglangCapabilities, ExternalCapabilities];

    /// <summary>A kind's capabilities; null for a kind there is not.</summary>
    public static EngineCapabilities? Of(string? kind) => All.FirstOrDefault(k => k.Kind == kind);

    /// <summary>The kinds a model added here can have (the app runs them).</summary>
    public static bool Managed(string? kind) => Of(kind) is { Managed: true };

    /// <summary>
    /// vLLM and SGLang: one server a model (and a copy), from a Hugging Face snapshot, on its own GPUs, run by the supervisor
    /// (services/vllm/supervisor.sh) as the app's desired state says, with its answers at once in each copy and no slots.
    /// </summary>
    public static bool ServerPerModel(string? kind) => kind is Vllm or Sglang;
}
