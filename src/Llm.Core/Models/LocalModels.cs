using Llm.Core.Access;

namespace Llm.Core.Models;

/// <summary>
/// A model one of this stack's engines can load: a file or folder in the model library and how to run it. A llama.cpp
/// model (a GGUF file) is written to that engine's presets; a vLLM model (a Hugging Face snapshot folder) to the vLLM
/// supervisor's desired state. Each is registered at the gateway.
/// </summary>
public sealed class LocalModel
{
    /// <summary>Its name at the engine, the gateway and in the chat.</summary>
    public required string Name { get; set; }
    /// <summary>
    /// Which kind of engine runs it: "llamacpp" (GGUF, any backend, can keep layers in RAM) or "vllm" (safetensors,
    /// tensor parallel over GPUs, sleep). Engines of other machines that the app does not manage are remote servers.
    /// </summary>
    public string EngineKind { get; set; } = EngineKinds.LlamaCpp;
    /// <summary>The engine of that kind it runs on, by name, when there are several (machines); null: the kind's first.</summary>
    public string? Engine { get; set; }
    /// <summary>
    /// What it is, relative to the library: a GGUF file (for a split model, its first part) for llama.cpp; a Hugging Face
    /// snapshot folder (hf/ORG/REPO@REVISION) for vLLM.
    /// </summary>
    public required string File { get; set; }
    /// <summary>A vision projector (mmproj) in the library, for a model that can see.</summary>
    public string? Projector { get; set; }
    /// <summary>The context, shared by the answers in parallel (one cache): the most one conversation can use.</summary>
    public int Context { get; set; } = 32768;
    public int? MaxOutput { get; set; }
    /// <summary>"auto": llama.cpp fits the model to the GPU when it loads (layers or experts to RAM as needed); "manual": <see cref="GpuLayers"/> and <see cref="CpuMoe"/>.</summary>
    public string Placement { get; set; } = "auto";
    public int GpuLayers { get; set; } = 99;
    /// <summary>
    /// The GPUs it runs on, by index as their vendor's tool numbers them ("0,1"); null: all of them (llama.cpp), or the
    /// ones the advisor proposes (vLLM). For vLLM the count is its tensor parallel size times <see cref="Replicas"/>.
    /// </summary>
    public string? Devices { get; set; }
    /// <summary>vLLM: copies of it, each on its share of <see cref="Devices"/> (two TP=2 copies on four GPUs); the chat keeps a conversation on one.</summary>
    public int Replicas { get; set; } = 1;
    /// <summary>vLLM: the share of each of its GPUs it takes (--gpu-memory-utilization); null: 0.90.</summary>
    public double? GpuMemoryUtilization { get; set; }
    /// <summary>vLLM: the weights' quantisation (--quantization: fp8, modelopt_fp4...); null: what its config says.</summary>
    public string? Quantization { get; set; }
    /// <summary>vLLM: speculative decoding, "mtp" (the model's own layers) or "eagle3" (with <see cref="DraftHead"/>, a folder); null: none.</summary>
    public string? SpecDecode { get; set; }
    /// <summary>vLLM: RAM for a CPU tier of its token cache (the OffloadingConnector), in GB; null or 0: none.</summary>
    public int? KvCpuGb { get; set; }
    /// <summary>vLLM: it may sleep (its weights to RAM) when its GPUs are lent to a picture or video job, rather than unload.</summary>
    public bool Sleep { get; set; } = true;
    /// <summary>The model that answers for it while it cannot (it restarts, or failed): the gateway's fallback, and the chat's.</summary>
    public string? Fallback { get; set; }
    /// <summary>Mixture-of-experts layers whose experts stay in RAM (llama.cpp --n-cpu-moe).</summary>
    public int CpuMoe { get; set; }
    /// <summary>The token cache's type: llama.cpp's (q8_0, f16...), or vLLM's --kv-cache-dtype (fp8, auto).</summary>
    public string KvType { get; set; } = "q8_0";
    /// <summary>Answers at once: llama.cpp's slots, vLLM's --max-num-seqs (per copy).</summary>
    public int Parallel { get; set; } = 1;
    /// <summary>Tokens of a prompt read in one step (llama.cpp --ubatch-size); null: the engine's 512.</summary>
    public int? Ubatch { get; set; }
    /// <summary>Drafting tokens with multi-token prediction: the model's own layer, or <see cref="DraftHead"/>.</summary>
    public bool Mtp { get; set; }
    /// <summary>A draft head in the library for <see cref="Mtp"/>, for a model without its own prediction layer.</summary>
    public string? DraftHead { get; set; }
    public int DraftMax { get; set; } = 3;
    /// <summary>A context past the one it was trained for, stretched with YaRN.</summary>
    public bool Yarn { get; set; }
    /// <summary>Sampling for requests that set none; null: the model's own recommendation (its file), else the engine's.</summary>
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? TopK { get; set; }
    public double? MinP { get; set; }
    public double? PresencePenalty { get; set; }
    /// <summary>More preset lines ("key = value", llama-server's long option names), for what the form does not cover.</summary>
    public string? ExtraPreset { get; set; }
    public bool Thinking { get; set; } = true;
    public bool Tools { get; set; } = true;
    /// <summary>Per million tokens; null: the default price (Settings → Prices).</summary>
    public decimal? InputPerMtok { get; set; }
    /// <summary>Per million prompt tokens the engine read from its cache; null: the default, never above <see cref="InputPerMtok"/>.</summary>
    public decimal? CachedInputPerMtok { get; set; }
    public decimal? OutputPerMtok { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Who may use a model (any model at the gateway, local or not). A model without a row: everyone.</summary>
public sealed class ModelAccess
{
    public required string Model { get; set; }
    public Audience Audience { get; set; }
    public List<Guid> Groups { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
