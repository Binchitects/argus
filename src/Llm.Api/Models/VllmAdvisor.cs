using System.Globalization;
using System.Text.Json.Nodes;
using Llm.Core.Models;

namespace Llm.Api.Models;

/// <summary>
/// What a model is, for vLLM and SGLang: a Hugging Face snapshot from its config.json and its safetensors index, or a GGUF
/// file from its header. The weights' size, the layers of each attention kind (full, sliding window, linear, compressed),
/// the heads that decide which tensor parallel sizes work, the token cache each token takes and the state each answer keeps.
/// </summary>
/// <param name="KvPerToken">The token cache a token takes in every layer that keeps one, at one byte an element (fp8).</param>
/// <param name="StatePerSequence">What each answer keeps whatever its length, in bytes: sliding windows (at fp8) and linear layers' state (float32).</param>
/// <param name="Approximate">An attention vLLM sizes in its own way (compressed, sparse): read vLLM's reported capacity once it runs.</param>
/// <param name="GgufParts">A GGUF file's parts (1: one file); 0: a snapshot.</param>
/// <param name="Checkpoint">The precision a snapshot's weights are stored at (NVFP4, MXFP4, FP8, BF16...); null for a GGUF file.</param>
public sealed record VllmProfile(
    string? Architecture, string? ModelType, int Layers, int FullLayers, int SlidingLayers, int LinearLayers, int CompressedLayers,
    int AttentionHeads, int KvHeads, int HeadDim, int? SlidingWindow, int TrainedContext, long WeightBytes, long KvPerToken, long StatePerSequence,
    int? Experts, int MtpLayers, bool Vision, string? Quantization, bool Approximate, int GgufParts = 0, CheckpointPrecision? Checkpoint = null);

/// <summary>
/// The vLLM counterpart of <see cref="ModelAdvisor"/>: which GPUs and tensor parallel size a model takes on this machine
/// (proposed for whatever GPUs it has, the admin may choose others), whether its weights fit them at its share of each,
/// what room is left for the token cache, and from it the longest context and how many answers at once; and the RAM its
/// sleep and its CPU tier take. It answers in the same shapes (ModelAdvice, MemoryEstimate) so Admin → Models shows the
/// same cards, and the same checks refuse a setting on save.
/// </summary>
public static class VllmAdvisor
{
    /// <summary>The most vLLM keeps on a GPU beside weights and cache (<see cref="OverheadOf"/>): a large model's activations, CUDA graphs, the framework.</summary>
    public const long MaxOverhead = 3L << 30;

    /// <summary>
    /// What vLLM keeps on each GPU of a copy on <paramref name="tp"/> GPUs beside weights and cache: CUDA and the framework
    /// (half a GiB), and activations and CUDA graphs that grow with the model (a quarter of its weights' slice), 3 GiB at most.
    /// A 135M model takes about 0.5 GiB (vLLM ran SmolLM2's 88 MB at 0.03 of a 24 GB card), a 27B one the whole 3 GiB.
    /// </summary>
    public static long OverheadOf(VllmProfile p, int tp = 1) => Math.Min(MaxOverhead, (512L << 20) + (p.WeightBytes / Math.Max(1, tp) / 4));

    /// <summary>
    /// The least token cache worth starting a copy with: an eighth of its share of its GPUs. With less, long prompts and
    /// answers at once preempt each other; a proposal takes more GPUs instead (DeepSeek-V4-Flash's 160 GB fit two 96 GB
    /// GPUs by their weights, with too little left: it is proposed four).
    /// </summary>
    public static long MinCache(long share) => share / 8;

    /// <summary>The share of each GPU when none is set (vLLM's own default).</summary>
    public const double DefaultUtilization = 0.90;

    /// <summary>Answers at once a model starts from on vLLM or SGLang (--max-num-seqs, --max-running-requests), as the dialog does.</summary>
    public const int DefaultParallel = 16;

    /// <summary>The shares of each GPU a model may take (--gpu-memory-utilization, --mem-fraction-static), in hundredths.</summary>
    public const double MinUtilization = 0.01;
    public const double MaxUtilization = 0.98;

    /// <summary>Tensor parallel sizes: powers of two (3 cannot divide the heads of any model here).</summary>
    public static readonly int[] TpSizes = [1, 2, 4, 8, 16];

    /// <summary>
    /// The GGUF architectures (general.architecture) each engine reads, as its image has it: vLLM 0.31.0 through transformers 5.17
    /// and vllm-gguf-plugin 0.0.5 (services/vllm.Dockerfile), SGLang 0.5.21 through its own loader. Tried on the library's files
    /// (llama, qwen2 and qwen3 on both; llama with llama.cpp's rotary layout, which services/vllm-gguf-rope.py and SGLang's
    /// launcher set) and, for vLLM, on each architecture's own config; the others fail as they load (qwen35, qwen3vlmoe and
    /// gemma4 among them: transformers or the plugin cannot map them, nor MoE ones such as qwen3moe).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> GgufArchitectures = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [EngineKinds.Vllm] = ["falcon", "gemma3", "gpt2", "llama", "mamba", "minimax-m2", "phi3", "qwen2", "qwen3", "stablelm", "starcoder2"],
        [EngineKinds.Sglang] = ["llama", "qwen2", "qwen3"],
        // Strata 0.2.11's families: Qwen3.8-Flash-Next (qwen4exp) and Qwen3.6-35B-A3B (qwen35moe), with their packs made from the file.
        [EngineKinds.Strata] = ["qwen4exp", "qwen35moe"],
        [EngineKinds.Lmdeploy] = [],
    };

    private static string G(long b) => (b / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GiB";

    private static int Int(JsonNode? n, int fallback = 0) => n is JsonValue v && v.TryGetValue<long>(out var l) ? (int)l : n is JsonValue d && d.TryGetValue<double>(out var x) ? (int)x : fallback;

    /// <summary>Reads a snapshot folder; null when it has no config.json that can be read.</summary>
    public static VllmProfile? Read(string dir)
    {
        var config = Path.Combine(dir, "config.json");
        if (!File.Exists(config))
        {
            return null;
        }
        JsonObject? c;
        try
        {
            c = JsonNode.Parse(File.ReadAllText(config)) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        return c is null ? null : Profile(c, WeightBytes(dir)) with { Checkpoint = Precision.Of(c, dir) };
    }

    /// <summary>
    /// A vLLM or SGLang model's file in the library: a snapshot's profile (<see cref="Read"/>), or a GGUF file's from its header
    /// (<see cref="FromGguf"/>); null when it is neither, or a GGUF file that is not a language model (or cannot be read).
    /// </summary>
    public static VllmProfile? Of(ModelLibrary library, string file)
    {
        if (library.Snapshot(file) is { } snapshot)
        {
            return Read(library.FullPath(snapshot.Path));
        }
        return file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) && library.Find(file) is { Profile.Kind: ModelKind.Language } entry
            ? FromGguf(entry.Profile, entry.File.Parts)
            : null;
    }

    /// <summary>
    /// A GGUF file's profile for vLLM (through its GGUF plugin) and SGLang, from what its header says (<see cref="ModelProfiler"/>):
    /// its layers and heads, the cache a token takes and the state each answer keeps, its context, and its weights, which stay
    /// on the GPU quantised as the file has them. Its prediction layers and a projector are llama.cpp's to run: neither is read.
    /// </summary>
    public static VllmProfile FromGguf(ModelProfile p, int parts = 1)
    {
        var w = p.Weights;
        bool Sliding(int i) => i < w.Sliding.Length && w.Sliding[i];
        var full = w.KvHeads.Where((k, i) => k > 0 && !Sliding(i)).Count();
        var sliding = w.KvHeads.Where((k, i) => k > 0 && Sliding(i)).Count();
        var perToken = ModelProfiler.KvElementsPerToken(w.KvHeads, w.Sliding, w.KeyLength, w.ValueLength, w.MlaWidth, fullOnly: true);
        var windowed = ModelProfiler.KvElementsPerToken(w.KvHeads, w.Sliding, w.KeyLength, w.ValueLength, w.MlaWidth, fullOnly: false) - perToken;
        return new VllmProfile(p.Architecture, p.Architecture, p.Layers ?? w.KvHeads.Length, full, sliding, w.KvHeads.Count(k => k == 0), 0,
            Math.Max(1, w.Heads), Math.Max(1, w.MaxKvHeads), w.KeyLength, p.SlidingWindow, p.TrainedContext ?? 4096, p.WeightBytes, perToken,
            (windowed * (p.SlidingWindow ?? 0)) + p.RecurrentBytesPerSlot, p.Experts?.Count, 0, false, p.Quant, p.Approximate, Math.Max(1, parts));
    }

    /// <summary>A vLLM or SGLang model's weights in bytes: its snapshot's, or its GGUF file's; 0 when it is neither.</summary>
    public static long WeightsOf(ModelLibrary library, string file) =>
        library.Snapshot(file) is { } snapshot ? WeightBytes(library.FullPath(snapshot.Path)) : library.Find(file)?.Profile.WeightBytes ?? 0;

    /// <summary>The weights' bytes: what the safetensors index says, else the safetensors files' sizes.</summary>
    public static long WeightBytes(string dir)
    {
        foreach (var index in Directory.EnumerateFiles(dir, "*.safetensors.index.json"))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(index))?["metadata"]?["total_size"] is JsonValue v && v.TryGetValue<long>(out var total) && total > 0)
                {
                    return total;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // the files' sizes below
            }
        }
        return Directory.EnumerateFiles(dir, "*.safetensors", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
    }

    /// <summary>A model's profile from its config (its text part, for a model that also sees) and its weights' size.</summary>
    public static VllmProfile Profile(JsonObject config, long weightBytes)
    {
        var t = config["text_config"] as JsonObject ?? config;
        int Get(string key, int fallback = 0) => Int(t[key] ?? config[key], fallback);
        var layers = Get("num_hidden_layers");
        var heads = Get("num_attention_heads", 1);
        var kvHeads = Get("num_key_value_heads", heads);
        var headDim = Get("head_dim", Get("hidden_size") / Math.Max(1, heads));
        // A sliding window the config turns off (use_sliding_window: false, as Qwen3's) is none.
        var slidingOn = (t["use_sliding_window"] ?? config["use_sliding_window"]) is not JsonValue on || on.GetValueKind() != System.Text.Json.JsonValueKind.False;
        var window = slidingOn && Get("sliding_window") is > 0 and var w ? w : 0;
        var types = (t["layer_types"] ?? config["layer_types"]) as JsonArray;
        int Count(string kind) => types?.Count(x => x?.GetValue<string>() == kind) ?? 0;
        int full, sliding, linear = Count("linear_attention"), compressed = 0;
        var approximate = false;
        var ratios = (t["compress_ratios"] ?? config["compress_ratios"]) as JsonArray;
        long perToken, state = 0;
        // Multi-head latent attention (DeepSeek V2/V3): one latent and its rotary part per layer, no K and V of their own.
        var mla = Get("kv_lora_rank") is > 0 and var rank ? rank + Get("qk_rope_head_dim") : 0;
        if (ratios is { Count: > 0 })
        {
            // Compressed sparse attention (DeepSeek V4): a layer with a ratio keeps one entry of its latent per that many tokens
            // (and its indexer's keys), one without keeps a sliding window only.
            var width = (long)kvHeads * headDim;
            var index = Get("index_head_dim");
            var rs = ratios.Select(r => Int(r)).ToList();
            compressed = rs.Count(r => r > 0);
            sliding = rs.Count(r => r == 0);
            full = 0;
            perToken = rs.Where(r => r > 0).Sum(r => (width + index) / r);
            state = sliding * (long)Math.Max(window, 1) * width;
            approximate = true;
        }
        else
        {
            if (types is { Count: > 0 })
            {
                full = Count("full_attention");
                sliding = Count("sliding_attention");
            }
            else if (Get("full_attention_interval") is > 0 and var interval)
            {
                full = layers / interval;
                linear = layers - full;
                sliding = 0;
            }
            else
            {
                full = window > 0 ? 0 : layers;
                sliding = window > 0 ? layers : 0;
            }
            var each = mla > 0 ? mla : 2L * kvHeads * headDim;
            perToken = full * each;
            state = sliding * (long)Math.Max(window, 0) * each;
        }
        if (linear > 0)
        {
            // Gated delta net: a state of value heads × key dim × value dim (float32), and a convolution's last few inputs.
            var vHeads = Get("linear_num_value_heads");
            var kHeads = Get("linear_num_key_heads");
            var kDim = Get("linear_key_head_dim");
            var vDim = Get("linear_value_head_dim");
            var conv = Math.Max(0, Get("linear_conv_kernel_dim", 4) - 1);
            state += linear * (((long)vHeads * kDim * vDim * 4) + ((long)conv * ((2L * kHeads * kDim) + ((long)vHeads * vDim)) * 2));
        }
        var experts = Int(t["num_experts"] ?? t["num_local_experts"] ?? t["n_routed_experts"] ?? config["num_experts"]);
        var mtp = Get("mtp_num_hidden_layers", Get("num_nextn_predict_layers"));
        var q = (config["quantization_config"] ?? t["quantization_config"])?["quant_method"]?.GetValue<string>();
        return new VllmProfile((config["architectures"] as JsonArray)?.FirstOrDefault()?.GetValue<string>(), (t["model_type"] ?? config["model_type"])?.GetValue<string>(),
            layers, full, sliding, linear, compressed, heads, kvHeads, headDim, window > 0 ? window : null, Get("max_position_embeddings", 32768),
            weightBytes, perToken, state, experts > 0 ? experts : null, mtp, config["vision_config"] is JsonObject, q, approximate, Checkpoint: Precision.Of(config));
    }

    /// <summary>Bytes an element of the cache takes for vLLM's --kv-cache-dtype.</summary>
    public static int CacheBytes(string kvType) => kvType.StartsWith("fp8", StringComparison.Ordinal) ? 1 : 2;

    /// <summary>Why a tensor parallel size cannot split this model's heads, or null.</summary>
    public static string? TpProblem(VllmProfile p, int tp)
    {
        if (!TpSizes.Contains(tp))
        {
            return $"A copy runs on {string.Join(", ", TpSizes)} GPUs (tensor parallel), not {tp}.";
        }
        if (p.AttentionHeads % tp != 0)
        {
            return $"Its {p.AttentionHeads} attention heads cannot be split over {tp} GPUs.";
        }
        if (p.KvHeads % tp != 0 && tp % p.KvHeads != 0)
        {
            return $"Its {p.KvHeads} key/value heads cannot be split over {tp} GPUs.";
        }
        return null;
    }

    /// <summary>
    /// The token cache one copy has on these GPUs at this share of each: each GPU's share less the weights' slice, the
    /// overhead and what else is there (<paramref name="others"/>, by GPU), the smallest GPU setting the pace (vLLM gives
    /// every rank the same cache).
    /// </summary>
    public static long CacheRoom(VllmProfile p, IReadOnlyList<GpuDevice> group, double utilization, IReadOnlyDictionary<int, long>? others = null)
    {
        if (group.Count == 0)
        {
            return 0;
        }
        var each = group.Min(d => Math.Min((long)(d.Total * utilization), d.Total - d.Reserved - Hardware.FitMargin - (others?.GetValueOrDefault(d.Index) ?? 0)));
        return Math.Max(0, (each - (p.WeightBytes / group.Count) - OverheadOf(p, group.Count)) * group.Count);
    }

    /// <summary>
    /// The shares of each GPU that hold a copy on each of these groups, in hundredths: from the smallest that leaves room for
    /// a whole context and <see cref="MinCache"/> to the largest that fits beside what is kept there (<paramref name="others"/>);
    /// null when none does.
    /// </summary>
    public static (double Min, double Max)? Shares(VllmProfile p, IReadOnlyList<IReadOnlyList<GpuDevice>> copies, int context, string kvType,
        IReadOnlyDictionary<int, long>? others = null, int people = 1)
    {
        if (copies.Count == 0 || copies.Any(c => c.Count == 0))
        {
            return null;
        }
        var most = (int)Math.Min(Math.Round(MaxUtilization * 100), copies.SelectMany(c => c)
            .Min(d => (d.Total - d.Reserved - Hardware.FitMargin - (others?.GetValueOrDefault(d.Index) ?? 0)) * 100 / Math.Max(1, d.Total)));
        for (var h = (int)Math.Round(MinUtilization * 100); h <= most; h++)
        {
            if (copies.All(c => Fits(p, c, h / 100.0, context, kvType, others, people)))
            {
                return (h / 100.0, most / 100.0);
            }
        }
        return null;
    }

    /// <summary>Which shares fit, said for an admin ("A share of 0.03 to 0.87 fits here."); empty when none does.</summary>
    private static string SharesFit((double Min, double Max)? shares) =>
        shares is var (min, max) ? string.Create(CultureInfo.InvariantCulture, $" A share of {min:0.00} to {max:0.00} fits here.") : "";

    /// <summary>
    /// GPUs for <paramref name="replicas"/> copies of this model on this machine, for whatever it has: the smallest tensor
    /// parallel size whose copies fit with room for <see cref="MinCache"/> of cache and a whole context each, on GPUs of a
    /// backend vLLM runs on, alike in size where possible, the least placed first (<paramref name="placed"/>: what the models
    /// that name their GPUs and the media servers keep there, kept loaded or not), so a new model goes where the others are
    /// not; else the ones with the most room (beside <paramref name="others"/>, what is kept loaded). Null when no set of its
    /// GPUs holds it.
    /// </summary>
    public static IReadOnlyList<int>? Propose(VllmProfile p, Hardware hw, int replicas, double utilization, int context, string kvType,
        IReadOnlyDictionary<int, long>? others = null, IReadOnlyDictionary<int, long>? placed = null)
    {
        var usable = GpuPlan.DevicesOf(hw).Where(d => EngineKinds.VllmCapabilities.Backends.Contains(d.Backend)).ToList();
        IEnumerable<GpuDevice> Roomiest(IEnumerable<GpuDevice> gpus) => gpus.OrderByDescending(d => d.Total - (others?.GetValueOrDefault(d.Index) ?? 0)).ThenBy(d => d.Index);
        foreach (var tp in TpSizes.Where(tp => TpProblem(p, tp) is null && tp * replicas <= usable.Count))
        {
            // Alike first (the group goes at its smallest GPU's pace), the least placed; then the most room.
            var alike = usable.GroupBy(d => (d.Vendor, d.Total)).Where(g => g.Count() >= tp * replicas).ToList();
            var chosen = alike.Select(g => GpuPlan.Least(g, placed ?? others).Take(tp * replicas).ToList())
                .Append([.. GpuPlan.Least(usable, placed ?? others).Take(tp * replicas)])
                .Concat(alike.Select(g => Roomiest(g).Take(tp * replicas).ToList()))
                .Append([.. Roomiest(usable).Take(tp * replicas)])
                .FirstOrDefault(set => Enumerable.Range(0, replicas).All(r => Fits(p, [.. set.Skip(r * tp).Take(tp)], utilization, context, kvType, others)));
            if (chosen is not null)
            {
                return [.. chosen.Select(d => d.Index).Order()];
            }
        }
        return null;
    }

    private static bool Fits(VllmProfile p, IReadOnlyList<GpuDevice> group, double utilization, int context, string kvType, IReadOnlyDictionary<int, long>? others,
        int people = 1)
    {
        var room = CacheRoom(p, group, utilization, others);
        return room >= MinCache(group.Sum(d => (long)(d.Total * utilization))) && room >= PersonCache(p, context, kvType) * people;
    }

    /// <summary>The token cache one person's whole context takes, and the state their answer keeps.</summary>
    public static long PersonCache(VllmProfile p, int context, string kvType) => ((long)Math.Min(context, p.TrainedContext) * PerToken(p, kvType)) + p.StatePerSequence;

    /// <summary>How many people at once a copy's cache of <paramref name="room"/> bytes holds, each with the whole context.</summary>
    public static int PeopleThatFit(VllmProfile p, long room, int context, string kvType) => (int)Math.Clamp(room / Math.Max(1, PersonCache(p, context, kvType)), 0, 1024);

    private static long PerToken(VllmProfile p, string kvType) => Math.Max(1, p.KvPerToken * CacheBytes(kvType));

    /// <summary>The copies' GPU groups: <paramref name="gpus"/> cut into <paramref name="replicas"/> equal runs, in order.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> Groups(IReadOnlyList<int> gpus, int replicas)
    {
        var tp = gpus.Count / Math.Max(1, replicas);
        return [.. Enumerable.Range(0, Math.Max(1, replicas)).Select(r => (IReadOnlyList<int>)[.. gpus.Skip(r * tp).Take(tp)])];
    }

    /// <summary>
    /// Advice for a vLLM model: its GPUs (its own, else proposed), whether each copy fits them, its cache, the longest
    /// context and answers at once that fit, and the RAM for sleep and the CPU tier. <paramref name="others"/>: what else
    /// is kept on each GPU (other vLLM groups, the reserves), as GpuPlan counts it; <paramref name="placed"/>: what is placed
    /// on each, kept loaded or not, which the GPUs proposed avoid.
    /// </summary>
    public static ModelAdvice Advise(LocalModel m, VllmProfile p, Hardware? hw, IReadOnlyDictionary<int, long>? others = null,
        IReadOnlyDictionary<int, long>? placed = null)
    {
        if (m.EngineKind == EngineKinds.Strata && hw is not null && p.GgufParts > 0)
        {
            return Spilled(m, p, hw, others, placed);
        }
        var advice = AdviseFit(m, p, hw, others, placed);
        if (hw is null || p.GgufParts > 0)
        {
            return advice;
        }
        // How its weights run on its GPUs (those it names, else every one its engine runs on: a proposal is some of them).
        var usable = GpuPlan.DevicesOf(hw).Where(d => EngineKinds.Of(m.EngineKind)?.Backends.Contains(d.Backend) ?? false).ToList();
        var named = Hardware.ParseDevices(m.Devices) is { Count: > 0 } ids ? usable.Where(d => ids.Contains(d.Index)).ToList() : [];
        var checkpoint = p.Checkpoint ?? Precision.None;
        if (Precision.On(checkpoint, m.EngineKind, named.Count > 0 ? named : usable) is not { } run)
        {
            return advice;
        }
        run = run with { WeightBytes = p.WeightBytes, Bf16Bytes = checkpoint.Bits < 16 ? (long)(p.WeightBytes * 16 / checkpoint.Bits) : p.WeightBytes };
        var problems = advice.Problems.ToList();
        if (run.Refused)
        {
            problems.Add(new("file", run.Summary, true));
        }
        // A checkpoint stored quantised runs as it is stored: another method on top of it fails as it loads.
        if (checkpoint.Method is { } method && m.Quantization is { Length: > 0 } q && !string.Equals(q, method, StringComparison.OrdinalIgnoreCase)
            && !(checkpoint.Fp4 && q is "modelopt_fp4" or "mxfp4" or "compressed-tensors"))
        {
            problems.Add(new("quantization", $"This snapshot is stored as {checkpoint.Label} ({method}): leave Quantisation empty, and the engine reads it as it is.", true));
        }
        return advice with { Problems = problems, Precision = run };
    }

    /// <summary>
    /// Strata's advice: what of its weights its GPUs hold at its share (beside its overhead and a whole context for each person
    /// at once) stays there, and the rest of its experts in RAM, read as each token needs them: slower than all on the GPUs,
    /// refused only when RAM cannot hold the rest either.
    /// </summary>
    private static ModelAdvice Spilled(LocalModel m, VllmProfile p, Hardware hw, IReadOnlyDictionary<int, long>? others, IReadOnlyDictionary<int, long>? placed)
    {
        var usable = GpuPlan.DevicesOf(hw).Where(d => EngineKinds.StrataCapabilities.Backends.Contains(d.Backend)).ToList();
        var on = Hardware.ParseDevices(m.Devices) is { Count: > 0 } ids ? usable.Where(d => ids.Contains(d.Index)).ToList() : usable;
        var utilization = m.GpuMemoryUtilization ?? DefaultUtilization;
        var context = Math.Clamp(m.Context, Math.Min(4096, p.TrainedContext), p.TrainedContext);
        var held = on.Sum(d => Math.Min((long)(d.Total * utilization), d.Total - d.Reserved - Hardware.FitMargin - (others?.GetValueOrDefault(d.Index) ?? 0)))
            - (on.Count * OverheadOf(p)) - (PersonCache(p, context, m.KvType) * Math.Max(1, m.Parallel)) - (MinCache(on.Sum(d => (long)(d.Total * utilization))));
        if (on.Count == 0 || held >= p.WeightBytes || held <= 0)
        {
            return AdviseFit(m, p, hw, others, placed);
        }
        var advice = AdviseFit(m, p with { WeightBytes = held }, hw, others, placed);
        var rest = p.WeightBytes - held;
        var problems = advice.Problems.ToList();
        problems.Add(rest > hw.RamForModels
            ? new("memory", $"Its weights ({G(p.WeightBytes)}) are more than its GPUs hold, and the {G(rest)} of experts Strata would keep in RAM are more than the {G(hw.RamForModels)} there for models.", true)
            : new("memory", $"Its weights ({G(p.WeightBytes)}) are more than its GPUs hold: Strata keeps {G(held)} on them and {G(rest)} of experts in RAM, read as each token needs them (slower than all on the GPUs).", false));
        return advice with
        {
            Problems = problems,
            Estimate = advice.Estimate is { } e ? e with { RamWeights = rest, RamTotal = e.RamTotal + rest } : null,
        };
    }

    private static ModelAdvice AdviseFit(LocalModel m, VllmProfile p, Hardware? hw, IReadOnlyDictionary<int, long>? others, IReadOnlyDictionary<int, long>? placed)
    {
        var problems = new List<ModelProblem>();
        var inv = CultureInfo.InvariantCulture;
        // One server a model on its GPUs, whichever of its kinds.
        var kind = EngineKinds.ServerPerModel(m.EngineKind) ? EngineKinds.Of(m.EngineKind)! : EngineKinds.VllmCapabilities;
        var label = kind.Label;
        var utilization = m.GpuMemoryUtilization ?? DefaultUtilization;
        var replicas = Math.Max(1, m.Replicas);
        var trained = p.TrainedContext;
        var ctxMin = Math.Min(4096, trained);
        var context = Math.Clamp(m.Context, ctxMin, trained);
        var profile = new ModelProfile
        {
            Kind = ModelKind.Language, Architecture = p.Architecture, Name = Path.GetFileName(m.File), Quant = p.Quantization, Structure = p.Experts is > 0 ? "moe" : "dense",
            Attention = p.LinearLayers > 0 ? "hybrid" : p.SlidingLayers > 0 || p.CompressedLayers > 0 ? "sliding" : "full", WeightBytes = p.WeightBytes, Layers = p.Layers,
            AttentionLayers = p.FullLayers + p.CompressedLayers, TrainedContext = trained, SlidingWindow = p.SlidingWindow, MtpLayers = p.MtpLayers,
            KvBytesPerToken = new Dictionary<string, long> { ["fp8"] = p.KvPerToken, ["auto"] = p.KvPerToken * 2 }, RecurrentBytesPerSlot = p.StatePerSequence,
            Approximate = p.Approximate, Thinking = true, Tools = true,
        };
        // Each request may hold the whole context (--max-model-len), its answer included: the answers at once share the cache, not the window.
        var limits = new ModelLimits(new Bounds(ctxMin, trained), ModelAdvisor.OutputBounds(context),
            new Bounds(1, 1024), new Bounds(0, 0), null, new Bounds(1, 8), ModelAdvisor.VllmCacheTypes, [], p.MtpLayers > 0, false, [], []);
        if (m.Context < limits.Context.Min || m.Context > limits.Context.Max)
        {
            problems.Add(new("context", string.Create(inv, $"The context is {limits.Context.Min:N0} to {limits.Context.Max:N0} tokens for this model (its config's longest)."), true));
        }
        if (m.MaxOutput is { } o && (o < limits.MaxOutput.Min || o > limits.MaxOutput.Max))
        {
            problems.Add(new("maxOutput", string.Create(inv, $"The longest answer is {limits.MaxOutput.Min:N0} to {limits.MaxOutput.Max:N0} tokens: the context less room for the prompt."), true));
        }
        if (m.SpecDecode == "mtp" && p.MtpLayers == 0)
        {
            problems.Add(new("specDecode", p.GgufParts > 0
                ? $"{label} reads a model's own prediction layers from its snapshot, not from a GGUF file: choose eagle3 with a draft model, or none."
                : "This model has no prediction layers of its own: choose eagle3 with a draft model, or none.", true));
        }
        if (p.GgufParts == 0 && !kind.Formats.Contains("safetensors"))
        {
            problems.Add(new("file", $"{label} runs GGUF files ({string.Join(", ", GgufArchitectures[kind.Kind])}), not a Hugging Face snapshot: choose the model's GGUF file, "
                + "or run the snapshot on vLLM or SGLang.", true));
        }
        if (p.GgufParts > 0 && !kind.Formats.Contains("gguf"))
        {
            problems.Add(new("file", $"{label} runs Hugging Face snapshots, not GGUF files: choose the model's snapshot, or run the GGUF file on llama.cpp or vLLM.", true));
        }
        else if (p.GgufParts > 0)
        {
            // A GGUF file: quantised as it is, read by vLLM's GGUF plugin (with all its parts), by SGLang (one file only), or by Strata.
            var archs = GgufArchitectures[kind.Kind];
            if (!archs.Contains(p.Architecture))
            {
                problems.Add(new("file", $"{label} reads GGUF files of these architectures: {string.Join(", ", archs)}. This one is {p.Architecture ?? "not said in its header"}, "
                    + $"which would fail as it loads: run it on llama.cpp, or on {label} from its Hugging Face snapshot.", true));
            }
            if (kind.Kind == EngineKinds.Sglang && p.GgufParts > 1)
            {
                problems.Add(new("file", $"SGLang reads one GGUF file, and this one is split in {p.GgufParts} parts: merge them into one in the library (llama.cpp's "
                    + "llama-gguf-split --merge FIRST-PART MERGED.gguf), or run it on vLLM, which reads every part, or on llama.cpp.", true));
            }
            if (m.Quantization is { Length: > 0 })
            {
                problems.Add(new("quantization", $"A GGUF file is quantised already ({p.Quantization ?? "as its file says"}): leave Quantisation empty.", true));
            }
        }
        if (p.Approximate)
        {
            problems.Add(new("memory", $"Its attention is compressed and sparse: the cache figures here are rough. {label} says its real capacity in its log as it starts.", false));
        }
        if (hw is null)
        {
            return new ModelAdvice(profile, limits, null, null, problems, hw);
        }
        var devices = GpuPlan.DevicesOf(hw);
        if (devices.Count == 0)
        {
            problems.Add(new("devices", $"{label} needs GPUs, and this machine's engines have none: run this model's GGUF on llama.cpp instead, or on an engine with GPUs.", true));
            return new ModelAdvice(profile, limits, null, null, problems, hw);
        }
        // GPUs that hold it at its share, else at the most that is free on them (its share is then refused, saying which fits).
        var proposed = Propose(p, hw, replicas, utilization, context, m.KvType, others, placed) ?? Propose(p, hw, replicas, MaxUtilization, context, m.KvType, others, placed);
        var gpus = Hardware.ParseDevices(m.Devices) ?? proposed;
        var shares = gpus is { Count: > 0 } && gpus.Count % replicas == 0 && gpus.All(i => devices.Any(d => d.Index == i))
            ? Shares(p, [.. Groups(gpus, replicas).Select(g => (IReadOnlyList<GpuDevice>)[.. devices.Where(d => g.Contains(d.Index))])], context, m.KvType, others)
            : null;
        var recommended = Recommend(p, hw, replicas, utilization, others, proposed, m.KvType, shares);
        if (gpus is null)
        {
            problems.Add(new("devices", $"Its weights ({G(p.WeightBytes)}) with room for a context do not fit {replicas} cop{(replicas == 1 ? "y" : "ies")} on this machine's GPUs at any share of each"
                + (others is { Count: > 0 } ? ", beside what is kept there" : "") + ". Choose a smaller quantisation or fewer copies.", true));
            return new ModelAdvice(profile, limits, recommended, null, problems, hw);
        }
        var group = devices.Where(d => gpus.Contains(d.Index)).ToList();
        if (group.Count != gpus.Count)
        {
            problems.Add(new("devices", $"This machine has GPUs {string.Join(", ", devices.Select(d => d.Index))}.", true));
            return new ModelAdvice(profile, limits, recommended, null, problems, hw);
        }
        if (group.FirstOrDefault(d => !kind.Backends.Contains(d.Backend)) is { } other)
        {
            problems.Add(new("devices", $"GPU {other.Index} ({other.Name}) runs on {other.Backend}, which {label} does not: it takes {string.Join(" or ", kind.Backends)} GPUs.", true));
        }
        // On AMD GPUs vLLM is its own ROCm image (rocm.yml), not arena-vllm: its GGUF plugin is built for CUDA.
        if (p.GgufParts > 0 && kind.Kind == EngineKinds.Vllm && group.FirstOrDefault(d => d.Backend == "rocm") is { } amd)
        {
            problems.Add(new("file", $"GPU {amd.Index} ({amd.Name}) runs vLLM's own ROCm image, which has no GGUF plugin (arena-vllm's is built for CUDA): "
                + "run it on llama.cpp, or on vLLM from its Hugging Face snapshot.", true));
        }
        if (group.Select(d => d.Vendor).Distinct().Count() > 1)
        {
            problems.Add(new("devices", "Its GPUs are of different vendors: one copy runs on one vendor's GPUs.", true));
        }
        if (gpus.Count % replicas != 0)
        {
            problems.Add(new("replicas", $"{gpus.Count} GPUs cannot be shared equally by {replicas} copies.", true));
            return new ModelAdvice(profile, limits, recommended, null, problems, hw);
        }
        var tp = gpus.Count / replicas;
        if (TpProblem(p, tp) is { } split)
        {
            problems.Add(new("devices", split, true));
        }
        if (group.Select(d => d.Total).Distinct().Count() > 1)
        {
            problems.Add(new("devices", "Its GPUs differ in size: each copy uses as much of every GPU as its smallest has.", false));
        }
        var copies = Groups(gpus, replicas).Select(g => group.Where(d => g.Contains(d.Index)).ToList()).ToList();
        var room = copies.Min(c => CacheRoom(p, c, utilization, others));
        var perToken = PerToken(p, m.KvType);
        var state = p.StatePerSequence;
        var share = copies.Sum(c => c.Sum(d => (long)(d.Total * utilization)));
        var budget = group.Sum(d => GpuPlan.BudgetOf(d) - (others?.GetValueOrDefault(d.Index) ?? 0));
        var ram = (m.Sleep ? p.WeightBytes * replicas : 0) + ((long)(m.KvCpuGb ?? 0) << 30) * replicas;
        if (m.KvDiskGb is > 0 and var disk)
        {
            problems.Add(new("kvDiskGb", $"Its disk tier takes up to {disk * replicas} GB of the engine's /kvcache (KVCACHE_DIR): keep it on an NVMe drive with that room.", false));
        }
        var fit = room <= 0 ? "none" : "gpu";
        var estimate = new MemoryEstimate(p.WeightBytes * replicas, room * replicas, OverheadOf(p, tp) * gpus.Count, share, budget,
            0, 0, ram, hw.RamForModels, p.Layers + 1, p.Layers, 0, p.Experts is > 0 ? p.Layers : 0, fit, p.Approximate);
        if (room <= 0)
        {
            problems.Add(new("memory", $"Its weights take {G(p.WeightBytes / tp)} of each of its {tp} GPU{(tp == 1 ? "" : "s")}, more than the {utilization:0.00} share of each less {G(OverheadOf(p, tp))} for {label} itself: it would not start. "
                + "Give it more GPUs, a larger share, or a smaller quantisation." + SharesFit(shares), true));
        }
        else
        {
            if (room < MinCache(copies.Min(c => c.Sum(d => (long)(d.Total * utilization)))))
            {
                problems.Add(new("memory", $"Only {G(room)} is left for its token cache: few answers fit at once. Give it more GPUs or a larger share.", false));
            }
            var need = ((long)context * perToken) + state;
            if (need > room)
            {
                problems.Add(new("context", string.Create(inv, $"One answer at {context:N0} tokens needs {G(need)} of cache, and a copy has {G(room)}: at most {Math.Max(0, (room - state) / perToken):N0} tokens fit.") + SharesFit(shares), true));
            }
            else if (need * m.Parallel > room)
            {
                // Each person at once may hold the whole context: the cache is sized for all of them, or the rest wait.
                var held = PeopleThatFit(p, room, context, m.KvType);
                var also = m.KvType.StartsWith("fp8", StringComparison.Ordinal) ? "" : string.Create(inv, $", {PeopleThatFit(p, room, context, "fp8")} at fp8");
                var fitting = gpus.Count % replicas == 0
                    ? Shares(p, [.. copies.Select(c => (IReadOnlyList<GpuDevice>)c)], context, m.KvType, others, m.Parallel)
                    : null;
                problems.Add(new("parallel", string.Create(inv,
                    $"{m.Parallel} people at once at {context:N0} tokens each need {G(need * m.Parallel)} of token cache, and a copy has {G(room)}. It holds {held} at the full window at {m.KvType}{also}: {label} queues or preempts the rest. ")
                    + (fitting is var (least, _) ? string.Create(inv, $"A share of {least:0.00} of each GPU holds all {m.Parallel}.") : string.Create(inv, $"Choose at most {held} people at once a copy, a shorter context per person, more GPUs or copies.")), false));
            }
        }
        if (others is { Count: > 0 } && group.Any(d => (long)(d.Total * utilization) > d.Total - d.Reserved - Hardware.FitMargin - others.GetValueOrDefault(d.Index)))
        {
            problems.Add(new("gpuMemoryUtilization", $"At {utilization:0.00} of each GPU it would take memory that is kept for others there: lower its share, or choose other GPUs." + SharesFit(shares), true));
        }
        if (ram > hw.RamForModels)
        {
            problems.Add(new("memory", m.KvCpuGb is > 0
                ? $"Its CPU tier ({m.KvCpuGb} GB a copy) and its weights while asleep need {G(ram)} of RAM, and {G(hw.RamForModels)} is there for models."
                : $"While asleep its weights ({G(ram)}) go to RAM, and {G(hw.RamForModels)} is there for models: turn sleep off (it unloads instead) or add RAM.", m.KvCpuGb is > 0));
        }
        return new ModelAdvice(profile, limits, recommended, estimate, problems, hw);
    }

    /// <summary>
    /// Settings to start from on this machine: the proposed GPUs, the longest context per person the config allows that fits,
    /// and the people at once the cache holds with all of it each, at least one and at most 256, at its share (at the smallest
    /// that fits when its own does not); and the shares that fit its GPUs.
    /// </summary>
    private static ModelRecommendation? Recommend(VllmProfile p, Hardware hw, int replicas, double utilization, IReadOnlyDictionary<int, long>? others,
        IReadOnlyList<int>? proposed, string kvType, (double Min, double Max)? shares)
    {
        if (proposed is null)
        {
            return null;
        }
        var devices = GpuPlan.DevicesOf(hw);
        var at = shares is var (min, max) && (utilization < min || utilization > max) ? min : utilization;
        var room = Groups(proposed, replicas).Min(g => CacheRoom(p, [.. devices.Where(d => g.Contains(d.Index))], at, others));
        var kv = kvType.StartsWith("fp8", StringComparison.Ordinal) ? kvType : "fp8";
        var perToken = PerToken(p, kv);
        var state = p.StatePerSequence;
        var context = (int)Math.Min(p.TrainedContext, Math.Max(0, (room - state) / perToken));
        // As many people at once as the cache holds with the whole context each.
        var parallel = Math.Clamp(PeopleThatFit(p, room, context, kv), 1, 256);
        var maxOutput = ModelCatalog.DefaultMaxOutput(context);
        return new ModelRecommendation(context, maxOutput, parallel, kv, null, p.MtpLayers > 0, null, 3, null, true, true)
        {
            Devices = string.Join(',', proposed), ShareMin = shares?.Min, ShareMax = shares?.Max,
        };
    }
}
