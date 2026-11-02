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
    /// Which kind of engine runs it: "llamacpp" (GGUF, any backend, can keep layers in RAM), "vllm" or "sglang" (safetensors,
    /// tensor parallel over GPUs, sleep). Engines of other machines that the app does not manage are remote servers.
    /// </summary>
    public string EngineKind { get; set; } = EngineKinds.LlamaCpp;
    /// <summary>The engine of that kind it runs on, by name, when there are several (machines); null: the kind's first.</summary>
    public string? Engine { get; set; }
    /// <summary>
    /// What it is, relative to the library: a GGUF file (for a split model, its first part) for llama.cpp; a Hugging Face
    /// snapshot folder (hf/ORG/REPO@REVISION) for vLLM and SGLang.
    /// </summary>
    public required string File { get; set; }
    /// <summary>A vision projector (mmproj) in the library, for a model that can see.</summary>
    public string? Projector { get; set; }
    /// <summary>
    /// The context (llama.cpp's ctx-size, vLLM's --max-model-len, SGLang's --context-length). On llama.cpp the answers in
    /// parallel share it, each slot held to its share; on vLLM and SGLang each request may hold all of it (ModelCatalog.Window).
    /// </summary>
    public int Context { get; set; } = 32768;
    /// <summary>The longest answer, in tokens, within what one request holds (its prompt needs room too); null: half of that.</summary>
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
    /// <summary>vLLM: RAM for a CPU tier of its token cache, in GB a copy; null or 0: none.</summary>
    public int? KvCpuGb { get; set; }
    /// <summary>
    /// vLLM: what keeps its token cache beyond the GPU: "cpu" (vLLM's own OffloadingConnector, <see cref="KvCpuGb"/> of RAM)
    /// or "lmcache" (LMCache: <see cref="KvCpuGb"/> of RAM, <see cref="KvDiskGb"/> of local disk that survives a restart,
    /// and the stack's remote store when it has one); null: "cpu" when it has a CPU tier, else none.
    /// </summary>
    public string? KvTier { get; set; }
    /// <summary>vLLM with LMCache: local disk for its token cache, in GB a copy (it survives a restart); null or 0: none.</summary>
    public int? KvDiskGb { get; set; }
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
    /// <summary>Its move to another engine, underway or failed (<see cref="ModelMove"/>); null: none.</summary>
    public ModelMove? Move { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A model moving to another engine at runtime (Admin → Models → Edit, another engine). Its row has its new settings from
/// the start, so new requests go to its new engine and wait there until it is ready. Its old engine keeps it
/// (<see cref="From"/>) until its answers in flight there have ended, then lets it go. It loads on its new engine beside
/// the old one when they share no GPU (or the old one did not have it loaded), else once the old one has let it go.
/// Should it fail to start there, its row is put back as it was, on its old engine (loaded again if it was), and
/// <see cref="Error"/> says why.
/// </summary>
public sealed class ModelMove
{
    /// <summary>Its answers on its old engine end first, on the same GPUs: new requests wait.</summary>
    public const string Draining = "draining";
    /// <summary>Loading on its new engine.</summary>
    public const string Loading = "loading";
    /// <summary>Ready on its new engine; its last answers on the old one still ending.</summary>
    public const string Ready = "ready";
    /// <summary>It could not start on its new engine: back on its old one, <see cref="Error"/> says why.</summary>
    public const string Failed = "failed";

    /// <summary>Its old engine has it, answering (until its answers end), stopping, or no more.</summary>
    public const string Serving = "serving";
    public const string Stopping = "stopping";
    public const string Gone = "gone";

    /// <summary>Its settings on the engine it leaves (a LocalModel as JSON); null once it failed (its row has them again).</summary>
    public string? From { get; set; }
    public string FromKind { get; set; } = "";
    /// <summary>The engine instance it leaves, by name: llamacpp, vllm, sglang, another machine's.</summary>
    public string FromEngine { get; set; } = "";
    public string ToKind { get; set; } = "";
    public string ToEngine { get; set; } = "";
    public string Phase { get; set; } = Draining;
    /// <summary>Whether its old engine still has it: <see cref="Serving"/>, <see cref="Stopping"/> or <see cref="Gone"/>.</summary>
    public string Old { get; set; } = Gone;
    /// <summary>It was loaded on its old engine as the move began: loaded there again should it fail.</summary>
    public bool WasLoaded { get; set; }
    public string? Error { get; set; }
    /// <summary>Who moved it.</summary>
    public string? By { get; set; }
    public DateTimeOffset Since { get; set; }
    /// <summary>When its phase began.</summary>
    public DateTimeOffset At { get; set; }
    /// <summary>When its new engine was asked to load it; null: not yet.</summary>
    public DateTimeOffset? LoadAt { get; set; }
    /// <summary>When its old engine was asked to let it go.</summary>
    public DateTimeOffset? OldAt { get; set; }
}
