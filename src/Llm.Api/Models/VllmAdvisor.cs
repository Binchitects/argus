using System.Globalization;
using System.Text.Json.Nodes;
using Llm.Core.Models;

namespace Llm.Api.Models;

/// <summary>
/// What a Hugging Face snapshot is, for vLLM, from its config.json and its safetensors index: the weights' size, the layers
/// of each attention kind (full, sliding window, linear, compressed), the heads that decide which tensor parallel sizes
/// work, the token cache each token takes and the state each answer keeps.
/// </summary>
/// <param name="KvPerToken">The token cache a token takes in every layer that keeps one, at one byte an element (fp8).</param>
/// <param name="StatePerSequence">What each answer keeps whatever its length, in bytes: sliding windows (at fp8) and linear layers' state (float32).</param>
/// <param name="Approximate">An attention vLLM sizes in its own way (compressed, sparse): read vLLM's reported capacity once it runs.</param>
public sealed record VllmProfile(
    string? Architecture, string? ModelType, int Layers, int FullLayers, int SlidingLayers, int LinearLayers, int CompressedLayers,
    int AttentionHeads, int KvHeads, int HeadDim, int? SlidingWindow, int TrainedContext, long WeightBytes, long KvPerToken, long StatePerSequence,
    int? Experts, int MtpLayers, bool Vision, string? Quantization, bool Approximate);

/// <summary>
/// The vLLM counterpart of <see cref="ModelAdvisor"/>: which GPUs and tensor parallel size a model takes on this machine
/// (proposed for whatever GPUs it has, the admin may choose others), whether its weights fit them at its share of each,
/// what room is left for the token cache, and from it the longest context and how many answers at once; and the RAM its
/// sleep and its CPU tier take. It answers in the same shapes (ModelAdvice, MemoryEstimate) so Admin → Models shows the
/// same cards, and the same checks refuse a setting on save.
/// </summary>
public static class VllmAdvisor
{
    /// <summary>What vLLM keeps on each GPU beside weights and cache: activations, CUDA graphs, the framework.</summary>
    public const long Overhead = 3L << 30;

    /// <summary>
    /// The least token cache worth starting a copy with: an eighth of its share of its GPUs, and 2 GiB at least. With less,
    /// long prompts and answers at once preempt each other; a proposal takes more GPUs instead (DeepSeek-V4-Flash's 160 GB
    /// fit two 96 GB GPUs by their weights, with too little left: it is proposed four).
    /// </summary>
    public static long MinCache(long share) => Math.Max(2L << 30, share / 8);

    /// <summary>The share of each GPU when none is set (vLLM's own default).</summary>
    public const double DefaultUtilization = 0.90;

    /// <summary>Tensor parallel sizes: powers of two (3 cannot divide the heads of any model here).</summary>
    public static readonly int[] TpSizes = [1, 2, 4, 8, 16];

    /// <summary>The context a conversation is counted at when answers at once are recommended.</summary>
    private const int TypicalContext = 32768;

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
        return c is null ? null : Profile(c, WeightBytes(dir));
    }

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
            weightBytes, perToken, state, experts > 0 ? experts : null, mtp, config["vision_config"] is JsonObject, q, approximate);
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
        return Math.Max(0, (each - (p.WeightBytes / group.Count) - Overhead) * group.Count);
    }

    /// <summary>
    /// GPUs for <paramref name="replicas"/> copies of this model on this machine, for whatever it has: the smallest tensor
    /// parallel size whose copies fit with room for <see cref="MinCache"/> of cache and a whole context each, on GPUs of a
    /// backend vLLM runs on, the ones with the most room first (beside <paramref name="others"/>), alike in size where
    /// possible. Null when no set of its GPUs holds it.
    /// </summary>
    public static IReadOnlyList<int>? Propose(VllmProfile p, Hardware hw, int replicas, double utilization, int context, string kvType,
        IReadOnlyDictionary<int, long>? others = null)
    {
        var usable = GpuPlan.DevicesOf(hw).Where(d => EngineKinds.VllmCapabilities.Backends.Contains(d.Backend)).ToList();
        foreach (var tp in TpSizes.Where(tp => TpProblem(p, tp) is null && tp * replicas <= usable.Count))
        {
            // Alike first (the group goes at its smallest GPU's pace), then the most room.
            var chosen = usable.GroupBy(d => (d.Vendor, d.Total)).Where(g => g.Count() >= tp * replicas)
                .Select(g => g.OrderByDescending(d => d.Total - (others?.GetValueOrDefault(d.Index) ?? 0)).ThenBy(d => d.Index).Take(tp * replicas).ToList())
                .Append(usable.OrderByDescending(d => d.Total - (others?.GetValueOrDefault(d.Index) ?? 0)).ThenBy(d => d.Index).Take(tp * replicas).ToList())
                .FirstOrDefault(set => Enumerable.Range(0, replicas).All(r => Fits(p, [.. set.Skip(r * tp).Take(tp)], utilization, context, kvType, others)));
            if (chosen is not null)
            {
                return [.. chosen.Select(d => d.Index).Order()];
            }
        }
        return null;
    }

    private static bool Fits(VllmProfile p, IReadOnlyList<GpuDevice> group, double utilization, int context, string kvType, IReadOnlyDictionary<int, long>? others)
    {
        var room = CacheRoom(p, group, utilization, others);
        var cache = PerToken(p, kvType);
        return room >= MinCache(group.Sum(d => (long)(d.Total * utilization))) && room >= ((long)Math.Min(context, p.TrainedContext) * cache) + p.StatePerSequence;
    }

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
    /// is kept on each GPU (other vLLM groups, the reserves), as GpuPlan counts it.
    /// </summary>
    public static ModelAdvice Advise(LocalModel m, VllmProfile p, Hardware? hw, IReadOnlyDictionary<int, long>? others = null)
    {
        var problems = new List<ModelProblem>();
        var inv = CultureInfo.InvariantCulture;
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
        var limits = new ModelLimits(new Bounds(ctxMin, trained), new Bounds(Math.Min(ModelAdvisor.MinOutput, context / 2), Math.Max(ModelAdvisor.MinOutput, context - ModelAdvisor.PromptRoom)),
            new Bounds(1, 1024), new Bounds(0, 0), null, new Bounds(1, 8), ModelAdvisor.VllmCacheTypes, [], p.MtpLayers > 0, false, [], []);
        if (m.Context < limits.Context.Min || m.Context > limits.Context.Max)
        {
            problems.Add(new("context", string.Create(inv, $"The context is {limits.Context.Min:N0} to {limits.Context.Max:N0} tokens for this model (its config's longest)."), true));
        }
        if (m.SpecDecode == "mtp" && p.MtpLayers == 0)
        {
            problems.Add(new("specDecode", "This model has no prediction layers of its own: choose eagle3 with a draft model, or none.", true));
        }
        if (p.Approximate)
        {
            problems.Add(new("memory", "Its attention is compressed and sparse: the cache figures here are rough. vLLM says its real capacity in its log as it starts.", false));
        }
        if (hw is null)
        {
            return new ModelAdvice(profile, limits, null, null, problems, hw);
        }
        var devices = GpuPlan.DevicesOf(hw);
        if (devices.Count == 0)
        {
            problems.Add(new("devices", "vLLM needs GPUs, and this machine's engines have none: run this model's GGUF on llama.cpp instead, or on an engine with GPUs.", true));
            return new ModelAdvice(profile, limits, null, null, problems, hw);
        }
        var proposed = Propose(p, hw, replicas, utilization, context, m.KvType, others);
        var gpus = Hardware.ParseDevices(m.Devices) ?? proposed;
        var recommended = Recommend(p, hw, replicas, utilization, others, proposed, m.KvType);
        if (gpus is null)
        {
            problems.Add(new("devices", $"Its weights ({G(p.WeightBytes)}) with room for a context do not fit {replicas} cop{(replicas == 1 ? "y" : "ies")} on this machine's GPUs at {utilization:0.00} of each"
                + (others is { Count: > 0 } ? ", beside what is kept there" : "") + ". Choose a smaller quantisation, fewer copies, or a larger share.", true));
            return new ModelAdvice(profile, limits, recommended, null, problems, hw);
        }
        var group = devices.Where(d => gpus.Contains(d.Index)).ToList();
        if (group.Count != gpus.Count)
        {
            problems.Add(new("devices", $"This machine has GPUs {string.Join(", ", devices.Select(d => d.Index))}.", true));
            return new ModelAdvice(profile, limits, recommended, null, problems, hw);
        }
        if (group.FirstOrDefault(d => !EngineKinds.VllmCapabilities.Backends.Contains(d.Backend)) is { } other)
        {
            problems.Add(new("devices", $"GPU {other.Index} ({other.Name}) runs on {other.Backend}, which vLLM does not: it takes {string.Join(" or ", EngineKinds.VllmCapabilities.Backends)} GPUs.", true));
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
        var fit = room <= 0 ? "none" : "gpu";
        var estimate = new MemoryEstimate(p.WeightBytes * replicas, room * replicas, Overhead * gpus.Count, share, budget,
            0, 0, ram, hw.RamForModels, p.Layers + 1, p.Layers, 0, p.Experts is > 0 ? p.Layers : 0, fit, p.Approximate);
        if (room <= 0)
        {
            problems.Add(new("memory", $"Its weights take {G(p.WeightBytes / tp)} of each of its {tp} GPU{(tp == 1 ? "" : "s")}, more than the {utilization:0.00} share of each less {G(Overhead)} for vLLM itself: it would not start. "
                + "Give it more GPUs, a larger share, or a smaller quantisation.", true));
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
                problems.Add(new("context", string.Create(inv, $"One answer at {context:N0} tokens needs {G(need)} of cache, and a copy has {G(room)}: at most {Math.Max(0, (room - state) / perToken):N0} tokens fit."), true));
            }
            else if (((long)Math.Min(context, TypicalContext) * perToken + state) * m.Parallel > room)
            {
                problems.Add(new("parallel", string.Create(inv, $"{m.Parallel} answers at once of {Math.Min(context, TypicalContext):N0} tokens need more than the {G(room)} of cache: vLLM queues or preempts the rest."), false));
            }
        }
        if (others is { Count: > 0 } && group.Any(d => (long)(d.Total * utilization) > d.Total - d.Reserved - Hardware.FitMargin - others.GetValueOrDefault(d.Index)))
        {
            problems.Add(new("gpuMemoryUtilization", $"At {utilization:0.00} of each GPU it would take memory that is kept for others there: lower its share, or choose other GPUs.", true));
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
    /// Settings to start from on this machine: the proposed GPUs, the longest context the config allows that fits, and answers
    /// at once from the cache at a typical conversation (32K tokens), at least one and at most 256.
    /// </summary>
    private static ModelRecommendation? Recommend(VllmProfile p, Hardware hw, int replicas, double utilization, IReadOnlyDictionary<int, long>? others,
        IReadOnlyList<int>? proposed, string kvType)
    {
        if (proposed is null)
        {
            return null;
        }
        var devices = GpuPlan.DevicesOf(hw);
        var room = Groups(proposed, replicas).Min(g => CacheRoom(p, [.. devices.Where(d => g.Contains(d.Index))], utilization, others));
        var perToken = PerToken(p, kvType);
        var state = p.StatePerSequence;
        var context = (int)Math.Min(p.TrainedContext, Math.Max(0, (room - state) / perToken));
        var parallel = (int)Math.Clamp(room / Math.Max(1, ((long)Math.Min(context, TypicalContext) * perToken) + state), 1, 256);
        var maxOutput = Math.Max(ModelAdvisor.MinOutput, Math.Min(32768, context / 2 / 1024 * 1024));
        return new ModelRecommendation(context, maxOutput, parallel, kvType.StartsWith("fp8", StringComparison.Ordinal) ? kvType : "fp8", null, p.MtpLayers > 0, null, 3, null, true, true)
        {
            Devices = string.Join(',', proposed),
        };
    }
}
