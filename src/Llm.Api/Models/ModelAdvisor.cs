using System.Globalization;
using Llm.Core.Models;

namespace Llm.Api.Models;

/// <summary>
/// The machine the engines run on, whatever it has: no GPU at all (CPU only), one, or many, of one vendor or several,
/// of different sizes. Read from Prometheus or from an engine's hardware file (<see cref="HardwareProbe"/>).
/// </summary>
/// <param name="GpuReserved">What the drivers keep for themselves.</param>
/// <param name="RamReserve">RAM kept for everything else (LLAMACPP_RAM_RESERVE_GB).</param>
/// <param name="PowerLimit">The GPUs' lowest power cap in watts (GPU_POWER_LIMIT_W), and <paramref name="PowerDefault"/> their own.</param>
/// <param name="Devices">Each GPU, by its index as its vendor's tool numbers it (the engine's CUDA0, ROCm0... in the same order).</param>
/// <param name="Reserves">
/// What the servers beside the chat models keep on each GPU, by index: the picture server (on the GPU it runs on, not
/// always the first), the embedder when it runs on a GPU. <see cref="GpuPlan"/> counts them as residents.
/// </param>
public sealed record Hardware(string? GpuName, int Gpus, long GpuTotal, long GpuReserved, long RamTotal, long RamReserve,
    double? PowerLimit = null, double? PowerDefault = null, IReadOnlyList<GpuDevice>? Devices = null, IReadOnlyList<GpuReserve>? Reserves = null)
{
    /// <summary>What llama.cpp's fit leaves free on each GPU (its --fit-target, 1024 MiB).</summary>
    public const long FitMargin = 1024L << 20;

    /// <summary>What the other servers keep on these GPUs (on all of them when <see cref="Devices"/> is not known).</summary>
    public long ReservedForOthers => Reserves is not { Count: > 0 } r ? 0
        : Devices is { Count: > 0 } d ? r.Where(x => d.Any(g => g.Index == x.Gpu)).Sum(x => x.Bytes) : r.Sum(x => x.Bytes);

    public long GpuForModels => Gpus == 0 ? 0 : Math.Max(0, GpuTotal - GpuReserved - ReservedForOthers - (FitMargin * Gpus));
    public long RamForModels => Math.Max(0, RamTotal - RamReserve);

    /// <summary>A machine with no GPU: everything runs from RAM.</summary>
    public static Hardware CpuOnly(long ramTotal, long ramReserve) => new(null, 0, 0, 0, ramTotal, ramReserve, Devices: []);

    /// <summary>
    /// The machine as a model placed on these GPUs sees it (null or empty: all): their memory, and the reserves of only
    /// the other servers that run on them.
    /// </summary>
    public Hardware On(IReadOnlyCollection<int>? indices)
    {
        if (indices is not { Count: > 0 } || Devices is not { Count: > 0 } all)
        {
            return this;
        }
        var chosen = all.Where(d => indices.Contains(d.Index)).ToList();
        if (chosen.Count == 0)
        {
            return this;
        }
        return this with
        {
            GpuName = chosen[0].Name, Gpus = chosen.Count, GpuTotal = chosen.Sum(d => d.Total), GpuReserved = chosen.Sum(d => d.Reserved), Devices = chosen,
            Reserves = Reserves?.Where(r => chosen.Any(d => d.Index == r.Gpu)).ToList(),
        };
    }

    /// <summary>
    /// llama.cpp's name for a GPU of this backend (its --device list: CUDA0, ROCm0, Vulkan0, SYCL0, MTL0); null for
    /// the CPU backend, which places nothing on a GPU.
    /// </summary>
    public static string? DevicePrefix(string? backend) => (backend ?? "cuda").ToLowerInvariant() switch
    {
        "rocm" or "hip" => "ROCm",
        "vulkan" => "Vulkan",
        "sycl" => "SYCL",
        "metal" => "MTL",
        "cpu" => null,
        _ => "CUDA",
    };

    /// <summary>A model's GPUs from its setting ("0,1"); null: all (or a setting that is not a list of indices).</summary>
    public static IReadOnlyList<int>? ParseDevices(string? devices)
    {
        var list = new List<int>();
        foreach (var d in (devices ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out var i))
            {
                return null;
            }
            list.Add(i);
        }
        return list.Count == 0 ? null : [.. list.Distinct().Order()];
    }
}

/// <summary>What a server beside the chat models keeps on a GPU: the picture server, the embedder.</summary>
public sealed record GpuReserve(int Gpu, string Name, long Bytes);

/// <summary>
/// One GPU: its index as its vendor's tool numbers it (nvidia-smi, rocm-smi), its name, its memory, who made it
/// ("nvidia", "amd", "intel", "apple", or another), the backend engines drive it with ("cuda", "rocm", "vulkan",
/// "sycl", "metal"), and its free memory when the source says it.
/// </summary>
public sealed record GpuDevice(int Index, string Name, long Total, long Reserved, string Vendor = "nvidia", string Backend = "cuda", long? Free = null);

public sealed record Bounds(int Min, int Max);

/// <summary>What this model allows: every value the form offers is inside these.</summary>
public sealed record ModelLimits(
    Bounds Context, Bounds MaxOutput, Bounds Parallel, Bounds GpuLayers, Bounds? CpuMoe, Bounds DraftMax,
    IReadOnlyList<string> CacheTypes, IReadOnlyList<int> Ubatch, bool Mtp, bool Yarn,
    IReadOnlyList<string> Projectors, IReadOnlyList<string> DraftHeads);

/// <summary>Settings to start from: the fastest that fit this machine, as far as the estimate tells.</summary>
public sealed record ModelRecommendation(
    int Context, int MaxOutput, int Parallel, string KvType, int? Ubatch, bool Mtp, string? DraftHead, int DraftMax, string? Projector, bool Thinking, bool Tools)
{
    /// <summary>The GPUs proposed for it on this machine ("0,1"); null: llama.cpp's own choice (all of them).</summary>
    public string? Devices { get; init; }
}

/// <summary>
/// Where the model's bytes go with these settings. <see cref="Fit"/>: "gpu" (all of it on the GPU),
/// "experts" (some experts in RAM), "layers" (some layers in RAM), "cpu" (all in RAM), "over"
/// (placed by hand, more than the GPU has), "none" (it cannot load), or "unknown" (no figures for this machine).
/// </summary>
public sealed record MemoryEstimate(
    long GpuWeights, long GpuCache, long GpuCompute, long GpuTotal, long? GpuBudget,
    long RamWeights, long RamCache, long RamTotal, long? RamBudget,
    int GpuLayers, int Layers, int ExpertLayersInRam, int MoeLayers, string Fit, bool Approximate);

/// <summary>
/// What one GPU would hold (<see cref="GpuPlan"/>): its room (its memory less the driver's share and llama.cpp's margin),
/// what its residents need of it, and which they are: chat models, vLLM groups, the picture server, the embedder.
/// </summary>
public sealed record GpuUse(int Index, string Name, long Budget, long Need, IReadOnlyList<string> Models, string Vendor = "nvidia", long Total = 0);

/// <summary>Whether the models kept loaded fit together, and how full each GPU would be.</summary>
public sealed record KeptPlan(IReadOnlyList<ModelProblem> Problems, IReadOnlyList<GpuUse> Gpus)
{
    public ModelProblem? FirstError => Problems.FirstOrDefault(p => p.Error);
}

/// <summary>A setting that cannot be used (an error), or one to know about (a warning).</summary>
public sealed record ModelProblem(string Field, string Message, bool Error);

public sealed record ModelAdvice(
    ModelProfile Profile, ModelLimits? Limits, ModelRecommendation? Recommended, MemoryEstimate? Estimate, IReadOnlyList<ModelProblem> Problems, Hardware? Hardware)
{
    public ModelProblem? FirstError => Problems.FirstOrDefault(p => p.Error);
}

/// <summary>
/// Limits, recommendations and a memory estimate for a model and its settings,
/// from its file and the machine. The form shows them as it is filled in, and the
/// same checks refuse a setting on save: what the page offers and what the API
/// takes cannot drift apart.
/// </summary>
public static class ModelAdvisor
{
    public const int MaxContext = 1_048_576;
    public const int MinOutput = 256;
    /// <summary>What a prompt needs at least beside the longest answer.</summary>
    public const int PromptRoom = 1024;
    public const int MaxParallel = 32;
    public const int StretchFactor = 4;
    public static readonly int[] UbatchSizes = [256, 512, 1024, 2048, 4096];
    private static readonly int[] ContextSteps = [8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576];
    private static readonly string[] CacheOrder = ["q8_0", "f16", "bf16", "q5_1", "q5_0", "q4_1", "q4_0", "iq4_nl"];
    /// <summary>CUDA's context and libraries, measured beside llama.cpp's own estimate.</summary>
    private const long GpuOverhead = 256L << 20;
    private const long ProjectorCompute = 256L << 20;

    private sealed record Inputs(int Context, int Parallel, double CacheBytes, int Ubatch, bool Mtp, long DraftBytes, long ProjectorBytes);

    private static string G(long b) => (b / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GiB";

    public static ModelAdvice Advise(LocalModel m, LibraryEntry file, IReadOnlyList<LibraryEntry> library, Hardware? hw)
    {
        // The machine as this model sees it: the GPUs it may use.
        hw = hw?.On(Hardware.ParseDevices(m.Devices));
        var p = file.Profile;
        var problems = new List<ModelProblem>();
        if (p.Kind != ModelKind.Language)
        {
            problems.Add(new("file", p.Why ?? "Not a language model.", true));
            return new ModelAdvice(p, null, null, null, problems, hw);
        }
        var w = p.Weights;
        var layers = p.Layers ?? 0;
        var trained = p.TrainedContext is > 0 ? p.TrainedContext.Value : (int?)null;
        var stretch = m.Yarn && p.CanStretch;
        var ctxMax = Math.Min(MaxContext, trained is { } t ? (stretch ? t * StretchFactor : t) : MaxContext);
        var ctxMin = Math.Min(4096, ctxMax);
        var ctx = Math.Clamp(m.Context, ctxMin, ctxMax);
        var cacheTypes = CacheOrder.Where(p.KvBytesPerToken.ContainsKey).ToList();
        var dir = Path.GetDirectoryName(file.File.Path) ?? "";
        var projectors = library.Where(f => f.Profile.Kind == ModelKind.Projector && Compatible(p, f.Profile))
            .OrderBy(f => Path.GetDirectoryName(f.File.Path) == dir ? 0 : 1).ThenBy(f => f.File.Path, StringComparer.Ordinal).Select(f => f.File.Path).ToList();
        var drafts = library.Where(f => f.Profile.Kind == ModelKind.Draft && f.Profile.Architecture == p.Architecture
                && f.Profile.EmbeddingLength == p.EmbeddingLength && (f.Profile.VocabSize is null || f.Profile.VocabSize == p.VocabSize))
            .OrderBy(f => Path.GetDirectoryName(f.File.Path) == dir ? 0 : 1).ThenBy(f => f.File.Path, StringComparer.Ordinal).Select(f => f.File.Path).ToList();
        var moe = p.Structure == "moe";
        var limits = new ModelLimits(
            new Bounds(ctxMin, ctxMax), new Bounds(Math.Min(MinOutput, ctx / 2), Math.Max(MinOutput, ctx - PromptRoom)), new Bounds(1, MaxParallel),
            new Bounds(0, layers + 1), moe ? new Bounds(0, layers) : null, new Bounds(1, 8), cacheTypes, UbatchSizes,
            p.MtpLayers > 0 || drafts.Count > 0, p.CanStretch, projectors, drafts);

        Check(m, p, limits, trained, problems);

        var draft = m.Mtp && m.DraftHead is { } head ? library.FirstOrDefault(f => f.File.Path == head) : null;
        var projector = m.Projector is { } pp ? library.FirstOrDefault(f => f.File.Path == pp) : null;
        var cacheBytes = ModelProfiler.CacheTypeBytes.GetValueOrDefault(m.KvType, ModelProfiler.CacheTypeBytes["q8_0"]);
        var x = new Inputs(ctx, Math.Clamp(m.Parallel, 1, MaxParallel), cacheBytes, m.Ubatch ?? 512, m.Mtp && (p.MtpLayers > 0 || draft is not null),
            draft?.File.Size ?? 0, projector?.File.Size ?? 0);
        var estimate = m.Placement == "manual"
            ? Manual(p, x, Math.Clamp(m.GpuLayers, 0, layers + 1), moe ? Math.Clamp(m.CpuMoe, 0, layers) : 0, hw)
            : Auto(p, x, hw);
        MemoryProblems(estimate, m.Placement == "manual", problems);
        // All on the GPU, the cores do the work: a cap far below the card's own starves them (measured on a 3090 at
        // 150 W: 240 MHz of 2130, a 27B at 7 tokens/s). With experts in RAM the cap costs nothing (docs/deployment.md).
        if (estimate.Fit == "gpu" && hw is { PowerLimit: { } cap, PowerDefault: { } own } && cap < own * 0.6)
        {
            problems.Add(new("power", string.Create(CultureInfo.InvariantCulture,
                $"The GPU is capped at {cap:0} W of its {own:0} W (GPU_POWER_LIMIT_W). A model that runs all on the GPU is held back by that: on a 3090 at 150 W the cores ran at a tenth of their clock. Raise the cap while this model is loaded."), false));
        }

        var recommended = Recommend(p, library, file, hw, cacheTypes, trained, ctxMin, projectors);
        return new ModelAdvice(p, limits, recommended, estimate, problems, hw);
    }

    /// <summary>The token cache types vLLM takes (--kv-cache-dtype).</summary>
    public static readonly string[] VllmCacheTypes = ["fp8", "fp8_e4m3", "fp8_e5m2", "auto"];

    /// <summary>
    /// The settings a model has that its engine's kind does not (EngineKinds): llama.cpp runs one copy on its GPUs, without
    /// vLLM's share of each GPU, quantisation, speculative decoding by EAGLE, or a CPU tier; vLLM has none of llama.cpp's
    /// placement by layers, experts in RAM, prompt step, YaRN, extra preset lines or projector file.
    /// </summary>
    public static IReadOnlyList<ModelProblem> KindProblems(LocalModel m)
    {
        var problems = new List<ModelProblem>();
        var kind = EngineKinds.Of(m.EngineKind);
        if (kind is null)
        {
            problems.Add(new("engineKind", $"There is no engine kind {m.EngineKind}.", true));
            return problems;
        }
        void No(bool set, string field, string what)
        {
            if (set)
            {
                problems.Add(new(field, $"{kind.Label} has no {what}.", true));
            }
        }
        No(m.Replicas != 1 && !kind.Replicas, "replicas", "copies of one model (one copy runs on its GPUs)");
        No(m.Replicas < 1 || m.Replicas > 8, "replicas", "fewer than 1 or more than 8 copies");
        if (kind.Kind == EngineKinds.LlamaCpp)
        {
            No(m.GpuMemoryUtilization is not null, "gpuMemoryUtilization", "share of each GPU to set (it fits itself to what is free)");
            No(m.Quantization is not null, "quantization", "quantisation to choose (the GGUF file has its own)");
            No(m.SpecDecode is not null, "specDecode", "speculative decoding but its own MTP (Draft tokens)");
            No(m.KvCpuGb is > 0 || m.KvTier is not null || m.KvDiskGb is > 0, "kvCpuGb", "tiers for its token cache to set (it keeps conversations that lost their slot in RAM by itself)");
        }
        else
        {
            No(m.Placement == "manual", "placement", "placement by hand (it holds every layer on its GPUs)");
            No(m.CpuMoe > 0, "cpuMoe", "experts in RAM");
            No(m.Ubatch is not null, "ubatch", "prompt step to set");
            No(m.Yarn, "yarn", "YaRN switch here (its config says how far it stretches)");
            No(m.ExtraPreset is not null, "extra", "llama.cpp preset lines");
            No(m.Projector is not null, "projector", "projector file (a model that sees has it in its folder)");
            No(m.Mtp, "mtp", "llama.cpp's MTP switch: choose speculative decoding (mtp or eagle3) instead");
            if (!VllmCacheTypes.Contains(m.KvType))
            {
                problems.Add(new("kvType", $"vLLM's token cache is {string.Join(", ", VllmCacheTypes)}.", true));
            }
            if (m.GpuMemoryUtilization is < 0.1 or > 0.98)
            {
                problems.Add(new("gpuMemoryUtilization", "The share of each GPU is 0.10 to 0.98.", true));
            }
            if (m.SpecDecode is not (null or "mtp" or "eagle3"))
            {
                problems.Add(new("specDecode", "Speculative decoding is mtp (the model's own layers) or eagle3 (with a draft model).", true));
            }
            else if (m.SpecDecode == "eagle3" && m.DraftHead is null)
            {
                problems.Add(new("draftHead", "EAGLE-3 needs its draft model: a snapshot folder in the library.", true));
            }
            if (m.KvCpuGb is < 0 or > 4096)
            {
                problems.Add(new("kvCpuGb", "The CPU tier is 0 to 4,096 GB.", true));
            }
            if (m.KvTier is not (null or "cpu" or "lmcache"))
            {
                problems.Add(new("kvTier", "The cache's tier is cpu (vLLM's own) or lmcache.", true));
            }
            if (m.KvDiskGb is > 0 && m.KvTier != "lmcache")
            {
                problems.Add(new("kvDiskGb", "A disk tier needs LMCache (the cache's tier: lmcache).", true));
            }
            if (m.KvDiskGb is < 0 or > 65536)
            {
                problems.Add(new("kvDiskGb", "The disk tier is 0 to 65,536 GB.", true));
            }
            if (m.Parallel is < 1 or > 1024)
            {
                problems.Add(new("parallel", "Answers at once (max-num-seqs) are 1 to 1,024.", true));
            }
        }
        return problems;
    }

    private static bool Compatible(ModelProfile model, ModelProfile projector) =>
        projector.Projector?.ProjectionDim is not { } dim || model.EmbeddingLength is not { } embd || dim == embd;

    private static void Check(LocalModel m, ModelProfile p, ModelLimits limits, int? trained, List<ModelProblem> problems)
    {
        var inv = CultureInfo.InvariantCulture;
        if (m.Context < limits.Context.Min || m.Context > limits.Context.Max)
        {
            problems.Add(new("context", m.Context > limits.Context.Max && trained is { } t && !m.Yarn && p.CanStretch
                ? string.Create(inv, $"It was trained for {t:N0} tokens of context. Stretch it with YaRN (up to {t * StretchFactor:N0}), or choose at most {t:N0}.")
                : string.Create(inv, $"The context is {limits.Context.Min:N0} to {limits.Context.Max:N0} tokens for this model{(trained is not null ? $" (trained for {trained:N0})" : "")}."), true));
        }
        if (m.MaxOutput is { } o && (o < limits.MaxOutput.Min || o > limits.MaxOutput.Max))
        {
            problems.Add(new("maxOutput", string.Create(inv, $"The longest answer is {limits.MaxOutput.Min:N0} to {limits.MaxOutput.Max:N0} tokens: the context less room for the prompt."), true));
        }
        if (m.Parallel < 1 || m.Parallel > MaxParallel)
        {
            problems.Add(new("parallel", $"Answers at once are 1 to {MaxParallel}.", true));
        }
        if (!limits.CacheTypes.Contains(m.KvType))
        {
            problems.Add(new("kvType", ModelProfiler.CacheTypeBytes.ContainsKey(m.KvType)
                ? $"This model's attention heads ({p.Weights.KeyLength}/{p.Weights.ValueLength}) cannot use a quantized cache: choose {string.Join(" or ", limits.CacheTypes)}."
                : $"The cache type is one of {string.Join(", ", limits.CacheTypes)}.", true));
        }
        if (m.Placement is not ("auto" or "manual"))
        {
            problems.Add(new("placement", "Placement is automatic or manual.", true));
        }
        else if (m.Placement == "manual")
        {
            // More than there are means all of them (llama.cpp's own convention: 99, 999).
            if (m.GpuLayers is < 0 or > 999)
            {
                problems.Add(new("gpuLayers", $"Layers on the GPU are 0 to {limits.GpuLayers.Max} ({limits.GpuLayers.Max - 1} layers and the output).", true));
            }
            if (limits.CpuMoe is null && m.CpuMoe != 0)
            {
                problems.Add(new("cpuMoe", "A dense model has no experts to keep in RAM.", true));
            }
            else if (limits.CpuMoe is { } cm && (m.CpuMoe < cm.Min || m.CpuMoe > cm.Max))
            {
                problems.Add(new("cpuMoe", $"Layers with their experts in RAM are 0 to {cm.Max}.", true));
            }
        }
        if (m.Ubatch is { } u && !limits.Ubatch.Contains(u))
        {
            problems.Add(new("ubatch", $"The prompt step is one of {string.Join(", ", limits.Ubatch)} tokens.", true));
        }
        if (m.Mtp)
        {
            if (m.DraftHead is { } head && !limits.DraftHeads.Contains(head))
            {
                problems.Add(new("draftHead", "The draft head must be one made for this model (same architecture, width and vocabulary) in the library.", true));
            }
            else if (m.DraftHead is null && p.MtpLayers == 0)
            {
                problems.Add(new("mtp", limits.DraftHeads.Count > 0 ? "This model has no prediction layer of its own: choose a draft head." : "This model has no prediction layer, and the library has no draft head for it.", true));
            }
        }
        if (m.DraftMax < limits.DraftMax.Min || m.DraftMax > limits.DraftMax.Max)
        {
            problems.Add(new("draftMax", "Draft tokens are 1 to 8.", true));
        }
        if (m.Yarn && !p.CanStretch)
        {
            problems.Add(new("yarn", p.RopeScaling is not null ? "Its file already stretches the context: its trained context includes it." : "This model's context cannot be stretched with YaRN.", true));
        }
        if (m.Projector is { } proj && !limits.Projectors.Contains(proj))
        {
            problems.Add(new("projector", "The vision projector must be one in the library made for this model's width.", true));
        }
        if (m.Thinking && p.Thinking == false)
        {
            problems.Add(new("thinking", "Its chat template has no thinking: it answers without.", true));
        }
        if (m.Tools && p.Tools == false)
        {
            problems.Add(new("tools", "Its chat template has no tool calls.", true));
        }
        if (m.Temperature is < 0 or > 2 || m.TopP is < 0 or > 1 || m.TopK is < 0 or > 1000 || m.MinP is < 0 or > 1 || m.PresencePenalty is < -2 or > 2)
        {
            problems.Add(new("sampling", "Temperature is 0 to 2, top-p and min-p 0 to 1, top-k 0 to 1000, the presence penalty -2 to 2.", true));
        }
    }

    /// <summary>
    /// Whether the models kept loaded fit together, beside <paramref name="others"/> (vLLM groups, which take their share
    /// whatever they hold) and the servers' reserves, by <see cref="GpuPlan"/>. Each llama.cpp model's estimate (alone, on
    /// its GPUs) is spread over its GPUs by what each has for models. Their caches and buffers must fit the GPUs and RAM
    /// together, or one would not load; past that, a GPU asked for more than it has means the last to load puts layers in
    /// RAM, and RAM asked for more means weights page in from disk: slower, not broken.
    /// </summary>
    public static KeptPlan PlanKept(IReadOnlyList<(LocalModel Model, LibraryEntry? File)> kept, IReadOnlyList<LibraryEntry> library, Hardware? hw,
        IReadOnlyList<Resident>? others = null, long othersRam = 0)
    {
        var problems = new List<ModelProblem>();
        if (hw is null || (kept.Count == 0 && others is not { Count: > 0 }))
        {
            return new KeptPlan(problems, []);
        }
        var residents = new List<Resident>(others ?? []);
        long buffers = 0, ram = othersRam;
        foreach (var (m, file) in kept)
        {
            if (file is null)
            {
                problems.Add(new("memory", $"{m.Name}'s file is outside the model library, so its memory is not counted here.", false));
                continue;
            }
            if (Advise(m, file, library, hw).Estimate is not { Fit: not "unknown" } e)
            {
                continue;
            }
            residents.Add(GpuPlan.Spread(m.Name, "llamacpp", hw, Hardware.ParseDevices(m.Devices), e.GpuTotal));
            buffers += e.GpuCache + e.GpuCompute + e.RamCache;
            ram += e.RamTotal;
        }
        if (residents.Count > 0)
        {
            residents.AddRange(GpuPlan.Reserved(hw));
        }
        var plan = GpuPlan.Of(hw, residents);
        if (buffers > hw.GpuForModels + hw.RamForModels)
        {
            problems.Add(new("memory", $"Together their caches and buffers need {G(buffers)}, and the GPUs and RAM have {G(hw.GpuForModels + hw.RamForModels)} for models: one would not load. Keep fewer loaded, or give them smaller contexts.", true));
        }
        problems.AddRange(plan.Problems);
        if (ram > hw.RamForModels)
        {
            problems.Add(new("memory", $"Together they keep about {G(ram)} in RAM, which has {G(hw.RamForModels)} for models: weights page in from disk and answers slow down.", false));
        }
        return new KeptPlan(problems, plan.Gpus);
    }

    private static void MemoryProblems(MemoryEstimate e, bool manual, List<ModelProblem> problems)
    {

        if (e.GpuBudget is not { } budget || e.RamBudget is not { } ram)
        {
            return;
        }
        if (e.Fit == "none")
        {
            problems.Add(new("memory", $"It cannot load: its cache and buffers need {G(e.RamCache + e.GpuCache)} and there is not that much GPU memory and RAM. Lower the context, the answers at once, or the cache type.", true));
            return;
        }
        if (manual && e.GpuTotal > budget)
        {
            problems.Add(new("memory", $"It needs about {G(e.GpuTotal)} of GPU memory, and {G(budget)} is there for models: it would not load. Lower the context or the cache type, keep more of it in RAM, or place it automatically.", true));
        }
        if (e.Fit == "layers")
        {
            problems.Add(new("memory", $"{e.Layers - Math.Max(0, e.GpuLayers - 1)} of {e.Layers} layers run from RAM: answers are much slower. A smaller context or cache, or a smaller quantization, keeps it on the GPU.", false));
        }
        if (e.Fit == "cpu")
        {
            problems.Add(new("memory", "It runs entirely from RAM: answers are very slow.", false));
        }
        if (e.RamWeights > ram)
        {
            problems.Add(new("memory", $"The part in RAM ({G(e.RamWeights)}) is more than the {G(ram)} kept for models: it is read from disk as it runs, which is slow. Keep the model files on an NVMe drive, or add RAM.", false));
        }
    }

    private static ModelRecommendation Recommend(ModelProfile p, IReadOnlyList<LibraryEntry> library, LibraryEntry file, Hardware? hw,
        List<string> cacheTypes, int? trained, int ctxMin, List<string> projectors)
    {
        var kv = cacheTypes.Contains("q8_0") ? "q8_0" : cacheTypes[0];
        var bytes = ModelProfiler.CacheTypeBytes[kv];
        var moe = p.Structure == "moe";
        var small = (p.ActiveParameters ?? p.Parameters ?? long.MaxValue) <= 10_000_000_000;
        var projector = projectors.FirstOrDefault(pp => Path.GetDirectoryName(pp) == Path.GetDirectoryName(file.File.Path));
        var projectorBytes = projector is null ? 0 : library.First(f => f.File.Path == projector).File.Size;
        var ceiling = Math.Min(trained ?? 32768, MaxContext);
        var candidates = ContextSteps.Where(s => s <= ceiling && s >= ctxMin).Append(ceiling).Distinct().OrderDescending().ToList();
        var context = Math.Max(ctxMin, Math.Min(ceiling, 32768));
        var parallel = 2;
        var ubatch = (int?)null;
        var fit = "unknown";
        if (hw is not null)
        {
            context = candidates.Count > 0 ? candidates[^1] : ctxMin;
            foreach (var c in candidates)
            {
                var e = Auto(p, new Inputs(c, 2, bytes, moe ? 1024 : 512, false, 0, projectorBytes), hw);
                if (e.Fit == "gpu" || (moe && e.Fit == "experts"))
                {
                    (context, fit) = (c, e.Fit);
                    break;
                }
            }
            // Four at once for a small model all on the GPU; with experts in RAM the CPU is what answers, and two share it best.
            if (small && fit == "gpu" && Auto(p, new Inputs(context, 4, bytes, 512, false, 0, projectorBytes), hw).Fit == "gpu")
            {
                parallel = 4;
            }
            ubatch = moe && fit == "experts" ? 1024 : null;
        }
        var maxOutput = Math.Max(MinOutput, Math.Min(32768, context / 2 / 1024 * 1024));
        // Drafting is a win for a dense model on the GPU (measured +50% on a 27B), not with experts in RAM; and only if its draft context fits too.
        var mtp = p.MtpLayers > 0 && p.Structure == "dense" && fit == "gpu"
            && Auto(p, new Inputs(context, parallel, bytes, 512, true, 0, projectorBytes), hw).Fit == "gpu";
        return new ModelRecommendation(context, maxOutput, parallel, kv, ubatch, mtp, null, 3, projector, p.Thinking ?? true, p.Tools ?? true);
    }

    /// <summary>What llama.cpp's fit does: all on the GPU; else a mixture of experts keeps experts in RAM, layer by layer; else layers go to RAM.</summary>
    private static MemoryEstimate Auto(ModelProfile p, Inputs x, Hardware? hw)
    {
        var w = p.Weights;
        var layers = w.Layer.Length;
        var none = new bool[layers];
        var full = Measure(p, x, layers + 1, none, hw, "gpu");
        if (hw is null)
        {
            return full with { Fit = "unknown" };
        }
        var budget = hw.GpuForModels;
        if (full.GpuTotal <= budget)
        {
            return RamCheck(full, hw);
        }
        var moeLayers = Enumerable.Range(0, layers).Where(i => w.Experts[i] > 0).ToList();
        var inRam = new bool[layers];
        if (moeLayers.Count > 0)
        {
            // From the last layer back, as llama.cpp does.
            for (var k = 1; k <= moeLayers.Count; k++)
            {
                inRam[moeLayers[^k]] = true;
                var e = Measure(p, x, layers + 1, inRam, hw, "experts");
                if (e.GpuTotal <= budget)
                {
                    return RamCheck(e, hw);
                }
            }
        }
        for (var g = layers; g >= 1; g--)
        {
            var e = Measure(p, x, g, inRam, hw, "layers");
            if (e.GpuTotal <= budget)
            {
                return RamCheck(e, hw);
            }
        }
        return RamCheck(Measure(p, x, 0, inRam, hw, "cpu"), hw);
    }

    private static MemoryEstimate Manual(ModelProfile p, Inputs x, int gpuLayers, int cpuMoe, Hardware? hw)
    {
        var layers = p.Weights.Layer.Length;
        var inRam = Enumerable.Range(0, layers).Select(i => i < cpuMoe).ToArray();
        var fit = gpuLayers == 0 ? "cpu" : gpuLayers <= layers ? "layers" : inRam.Where((r, i) => r && p.Weights.Experts[i] > 0).Any() ? "experts" : "gpu";
        var e = Measure(p, x, gpuLayers, inRam, hw, fit);
        return hw is null ? e with { Fit = "unknown" } : e.GpuTotal > hw.GpuForModels ? e with { Fit = "over" } : RamCheck(e, hw);
    }

    /// <summary>The cache and buffers in RAM cannot page out: without room for them it cannot load.</summary>
    private static MemoryEstimate RamCheck(MemoryEstimate e, Hardware hw) => e.RamCache > hw.RamForModels ? e with { Fit = "none" } : e;

    /// <summary>
    /// The bytes on the GPU and in RAM with the last <paramref name="gpuLayers"/> layers on the GPU
    /// (the output counts as one, first) and the experts of <paramref name="expertsInRam"/> in RAM.
    /// Calibrated against llama.cpp's own estimate (llama fit-params) for dense, hybrid and MoE models.
    /// </summary>
    private static MemoryEstimate Measure(ModelProfile p, Inputs x, int gpuLayers, bool[] expertsInRam, Hardware? hw, string fit)
    {
        var w = p.Weights;
        var layers = w.Layer.Length;
        var start = Math.Max(0, layers + 1 - gpuLayers);
        var ssm = w.KvHeads.Count(k => k == 0);
        var recurrent = ssm > 0 && p.RecurrentBytesPerSlot > 0 ? p.RecurrentBytesPerSlot / ssm * x.Parallel : 0;
        var slidingCells = Math.Min(x.Context, ((long)(p.SlidingWindow ?? 0) * x.Parallel) + x.Ubatch);
        long gpuWeights = 0, gpuCache = 0, ramWeights = w.HostOnly, ramCache = 0, largestInRam = 0;
        var moved = 0;
        for (var i = 0; i < layers; i++)
        {
            long cache = w.KvHeads[i] > 0
                ? (long)((w.MlaWidth ?? (long)w.KvHeads[i] * (w.KeyLength + w.ValueLength)) * x.CacheBytes * (w.Sliding.Length > i && w.Sliding[i] ? slidingCells : x.Context))
                : recurrent;
            var experts = expertsInRam[i] ? w.Experts[i] : 0;
            if (i >= start)
            {
                gpuWeights += w.Layer[i] - experts;
                gpuCache += cache;
                ramWeights += experts;
                if (experts > 0)
                {
                    moved++;
                    largestInRam = Math.Max(largestInRam, experts);
                }
            }
            else
            {
                ramWeights += w.Layer[i];
                ramCache += cache;
                moved += w.Experts[i] > 0 ? 1 : 0;
            }
        }
        if (gpuLayers >= 1)
        {
            gpuWeights += w.Output + w.TiedCopy;
        }
        else
        {
            ramWeights += w.Output;
        }
        var draftCache = 0L;
        if (x.Mtp)
        {
            gpuWeights += x.DraftBytes > 0 ? x.DraftBytes : w.Mtp;
            draftCache = (long)((w.MlaWidth ?? (long)w.MaxKvHeads * (w.KeyLength + w.ValueLength)) * x.CacheBytes * x.Context);
        }
        var projectorCompute = 0L;
        if (x.ProjectorBytes > 0)
        {
            gpuWeights += x.ProjectorBytes;
            projectorCompute = ProjectorCompute;
        }
        var width = w.MlaWidth ?? (long)w.MaxKvHeads * (w.KeyLength + w.ValueLength);
        var embd = (long)(p.EmbeddingLength ?? 0);
        var logits = (long)(p.VocabSize ?? 0) * x.Ubatch * 4;
        var attention = (x.Context * ((width * 2) + (x.Ubatch * 2L))) + (80L << 20);
        var gpuCompute = Math.Max(logits, attention) + (embd * x.Ubatch * 8);
        // The draft context has its own cache and buffers: measured 1,628 MiB for a 27B's layer at 64K (qwen35).
        gpuCache += draftCache;
        gpuCompute += x.Mtp ? gpuCompute * 23 / 10 : 0;
        if (p.Approximate)
        {
            // Measured on qwen4exp (an indexer, compressed attention, hyper-connections) against llama.cpp's estimate and nvidia-smi.
            gpuCache = gpuCache * 112 / 100;
            ramCache = ramCache * 112 / 100;
            gpuCompute = gpuCompute * 175 / 100;
        }
        // Experts in RAM are copied to the GPU for a long prompt, a layer's worth at a time.
        gpuCompute += projectorCompute + (largestInRam > 0 && x.Ubatch >= 32 ? largestInRam : 0);
        var ramCompute = (x.Context * x.Ubatch * 2L) + (embd * x.Ubatch * 8);
        var gpuTotal = gpuLayers == 0 ? 0 : gpuWeights + gpuCache + gpuCompute + GpuOverhead;
        return new MemoryEstimate(
            gpuWeights, gpuCache, gpuLayers == 0 ? 0 : gpuCompute, gpuTotal, hw?.GpuForModels,
            ramWeights, ramCache + ramCompute, ramWeights + ramCache + ramCompute, hw?.RamForModels,
            Math.Min(gpuLayers, layers + 1), layers, moved, w.Experts.Count(e => e > 0), fit, p.Approximate);
    }
}
