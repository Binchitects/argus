using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Llm.Api.Models;

/// <summary>What kind of file a GGUF is, as far as the engine is concerned.</summary>
public static class ModelKind
{
    /// <summary>A language model: the only kind the engine serves from Admin → Models.</summary>
    public const string Language = "language";
    public const string Embedding = "embedding";
    public const string Reranker = "reranker";
    public const string Projector = "projector";
    public const string Draft = "draft";
    public const string Image = "image";
    public const string Adapter = "adapter";
    public const string Unknown = "unknown";
}

public sealed record ExpertInfo(int Count, int Used, int Shared, int Layers, long Bytes);

public sealed record SamplingDefaults(double? Temperature, double? TopP, int? TopK, double? MinP);

public sealed record RopeScaling(string Type, double? Factor, int? OriginalContext);

public sealed record ProjectorInfo(bool Vision, bool Audio, int? ProjectionDim, string? Type);

/// <summary>
/// A GGUF file read for what it is and what it needs: the kind (a dense or
/// mixture-of-experts language model, an embedding model, a vision projector,
/// a draft head, an image model...), its shape, what it was trained for, and
/// the memory each part takes. The Models page asks only for what fits the kind,
/// within the limits the file sets.
/// </summary>
public sealed record ModelProfile
{
    public required string Kind { get; init; }
    /// <summary>Why it cannot be added as a model, when it is not a language model.</summary>
    public string? Why { get; init; }
    /// <summary>Something to know, e.g. that the image generator uses it too.</summary>
    public string? Note { get; init; }
    public string? Architecture { get; init; }
    public string? Name { get; init; }
    public string? SizeLabel { get; init; }
    public string? Quant { get; init; }
    /// <summary>"dense" or "moe" (a language model).</summary>
    public string? Structure { get; init; }
    /// <summary>"full", "hybrid" (a cache in some layers only), "sliding" (a window in some layers) or "recurrent" (no cache, a state).</summary>
    public string? Attention { get; init; }
    public long? Parameters { get; init; }
    /// <summary>For a mixture of experts: the parameters one token uses.</summary>
    public long? ActiveParameters { get; init; }
    public double? BitsPerWeight { get; init; }
    public long WeightBytes { get; init; }
    public int? Layers { get; init; }
    /// <summary>Layers that keep a cache for every token of the context.</summary>
    public int? AttentionLayers { get; init; }
    public ExpertInfo? Experts { get; init; }
    public int? TrainedContext { get; init; }
    public int? SlidingWindow { get; init; }
    public int? EmbeddingLength { get; init; }
    public int? VocabSize { get; init; }
    /// <summary>The cache each context token takes, by cache type (only the types this model can use).</summary>
    public IReadOnlyDictionary<string, long> KvBytesPerToken { get; init; } = new Dictionary<string, long>();
    /// <summary>The recurrent state each parallel answer keeps (hybrid and recurrent models).</summary>
    public long RecurrentBytesPerSlot { get; init; }
    /// <summary>Built-in multi-token-prediction layers (draft tokens without a separate head).</summary>
    public int MtpLayers { get; init; }
    /// <summary>From its chat template; null when it has none to read.</summary>
    public bool? Thinking { get; init; }
    public bool? Tools { get; init; }
    /// <summary>The sampling its makers recommend, which the engine applies by itself.</summary>
    public SamplingDefaults? Sampling { get; init; }
    /// <summary>Scaling the file declares: its trained context already includes it.</summary>
    public RopeScaling? RopeScaling { get; init; }
    /// <summary>Whether the context can be stretched past its training with YaRN.</summary>
    public bool CanStretch { get; init; }
    public ProjectorInfo? Projector { get; init; }
    /// <summary>The memory estimate is rougher for this architecture (parts of its cache are not modelled).</summary>
    public bool Approximate { get; init; }

    [JsonIgnore]
    public ModelWeights Weights { get; init; } = ModelWeights.None;
}

/// <summary>Where a model's bytes are, layer by layer: what placing it on the GPU or in RAM moves.</summary>
public sealed record ModelWeights(
    long[] Layer, long[] Experts, long Output, long HostOnly, long TiedCopy, long Mtp,
    int[] KvHeads, bool[] Sliding, int KeyLength, int ValueLength, int? MlaWidth, int MaxKvHeads)
{
    public static readonly ModelWeights None = new([], [], 0, 0, 0, 0, [], [], 0, 0, null, 0);
}

/// <summary>Reads a <see cref="ModelProfile"/> from a GGUF header.</summary>
public static partial class ModelProfiler
{
    /// <summary>Bytes per cache element, by the cache types llama.cpp offers.</summary>
    public static readonly IReadOnlyDictionary<string, double> CacheTypeBytes = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["f16"] = 2, ["bf16"] = 2, ["q8_0"] = 34 / 32.0, ["q5_1"] = 24 / 32.0, ["q5_0"] = 22 / 32.0, ["q4_1"] = 20 / 32.0, ["q4_0"] = 18 / 32.0, ["iq4_nl"] = 18 / 32.0,
    };

    /// <summary>Head sizes CUDA's flash attention covers, which a quantized cache needs.</summary>
    private static readonly int[] FlashHeads = [64, 80, 96, 112, 128, 256];

    private static readonly HashSet<string> Encoders = new(StringComparer.Ordinal)
    {
        "bert", "nomic-bert", "nomic-bert-moe", "jina-bert-v2", "jina-bert-v3", "modern-bert", "neo-bert", "eurobert", "t5encoder", "roberta", "xlm-roberta",
    };

    private static readonly HashSet<string> Diffusion = new(StringComparer.Ordinal)
    {
        "flux", "flux2", "sd1", "sd2", "sdxl", "sd3", "wan", "qwen_image", "chroma", "hidream", "ltxv", "lumina2", "z_image", "hunyuan-video", "cosmos",
    };

    private static readonly string[] DiffusionTensors =
        ["double_blocks.", "single_blocks.", "model.diffusion_model.", "diffusion_model.", "first_stage_model.", "joint_blocks.", "input_blocks.", "img_in.", "time_in.", "decoder.up_blocks.", "encoder.down_blocks."];

    /// <summary>Layers per full-attention layer in sliding-window models whose file does not say (llama.cpp's own values).</summary>
    private static readonly Dictionary<string, int> SlidingPattern = new(StringComparer.Ordinal)
    {
        ["gemma2"] = 2, ["gemma3"] = 6, ["gemma3n"] = 5, ["cohere2"] = 4, ["gpt-oss"] = 2, ["llama4"] = 4, ["exaone4"] = 4,
    };

    public static ModelProfile Unreadable() => new()
    {
        Kind = ModelKind.Unknown, Why = "Not a GGUF file this can read: damaged, incomplete, or from a newer format.",
    };

    public static ModelProfile Profile(GgufHeader h, string fileName)
    {
        var arch = h.Text("general.architecture");
        var basics = new ModelProfile
        {
            Kind = ModelKind.Unknown, Architecture = arch, Name = h.Text("general.name"), SizeLabel = h.Text("general.size_label"),
            Quant = Quant(fileName, h.Whole("general.file_type")), WeightBytes = h.Tensors.Sum(t => t.Bytes),
        };
        var type = h.Text("general.type");

        if (type == "adapter" || h.Values.ContainsKey("adapter.type"))
        {
            return basics with { Kind = ModelKind.Adapter, Why = "A LoRA adapter: not a model on its own." };
        }
        if (arch is "clip" or "mmproj" || type == "mmproj" || fileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
        {
            var dim = h.Whole("clip.vision.projection_dim") ?? h.Whole("clip.audio.projection_dim");
            return basics with
            {
                Kind = ModelKind.Projector, Why = "A vision projector: choose it as a model's vision projector.",
                Projector = new ProjectorInfo(h.Flag("clip.has_vision_encoder") ?? true, h.Flag("clip.has_audio_encoder") ?? false, dim is null ? null : (int)dim, h.Text("clip.projector_type")),
            };
        }
        if ((arch is not null && Diffusion.Contains(arch)) || (h.Tensors.Count > 0 && h.Tensors.Any(t => DiffusionTensors.Any(p => t.Name.StartsWith(p, StringComparison.Ordinal)))))
        {
            return basics with { Kind = ModelKind.Image, Why = "An image model: pictures are made by the image server (IMAGEGEN_MODEL_DIR), not by the engine." };
        }
        if (arch is null)
        {
            return basics with { Why = "It does not say what it is (no architecture): llama.cpp's server cannot serve it." };
        }

        var blocks = (int)(h.Whole(h.Arch("block_count")) ?? 0);
        var nextn = (int)Math.Clamp(h.Whole(h.Arch("nextn_predict_layers")) ?? 0, 0, blocks);
        var layers = blocks - nextn;
        var byLayer = new long[blocks];
        var experts = new long[blocks];
        long expertElements = 0, elements = 0, lookupElements = 0, output = 0, hostOnly = 0;
        var tied = true;
        var layerTensors = new bool[blocks];
        foreach (var t in h.Tensors)
        {
            var layer = LayerOf(t.Name);
            if (layer is { } l && l < blocks)
            {
                byLayer[l] += t.Bytes;
                layerTensors[l] = true;
                if (t.Name.Contains("exps.", StringComparison.Ordinal))
                {
                    experts[l] += t.Bytes;
                    if (l < layers)
                    {
                        expertElements += t.Elements;
                    }
                }
                if (l < layers)
                {
                    elements += t.Elements;
                }
                continue;
            }
            elements += t.Elements;
            if (t.Name is "token_embd.weight" || t.Name.StartsWith("per_layer_token_embd", StringComparison.Ordinal))
            {
                hostOnly += t.Bytes;
                lookupElements += t.Elements;
            }
            else
            {
                output += t.Bytes;
            }
            if (t.Name == "output.weight")
            {
                tied = false;
            }
        }

        // A draft head: only the prediction layers' tensors, to run beside the model it came from. By what it holds,
        // never by its name: a whole model may well be called "MTP-something".
        if (h.Flag(h.Arch("nextn_shared_target_tensors")) == true
            || (nextn > 0 && blocks > 0 && Enumerable.Range(0, layers).All(i => !layerTensors[i]) && Enumerable.Range(layers, nextn).Any(i => layerTensors[i])))
        {
            return basics with
            {
                Kind = ModelKind.Draft, Why = "A draft head for multi-token prediction: turn on drafting for its model and choose it there.",
                EmbeddingLength = (int?)h.Whole(h.Arch("embedding_length")), VocabSize = (int?)h.Array("tokenizer.ggml.tokens")?.Length,
            };
        }

        var pooling = h.Whole(h.Arch("pooling_type"));
        if (pooling == 4 || h.Tensors.Any(t => t.Name is "cls.output.weight" or "cls.weight"))
        {
            return basics with { Kind = ModelKind.Reranker, Why = "A reranker: it scores passages and cannot chat. The engine serves one model at a time, so it would take the chat's place." };
        }
        if (pooling is 1 or 2 or 3 || Encoders.Contains(arch) || h.Flag(h.Arch("attention.causal")) == false)
        {
            return basics with
            {
                Kind = ModelKind.Embedding, TrainedContext = (int?)h.Whole(h.Arch("context_length")),
                Why = "An embedding model: it turns text into vectors and cannot chat. The engine serves one model at a time, so it would take the chat's place; Argus's embeddings run on their own engine.",
            };
        }

        var vocab = h.Array("tokenizer.ggml.tokens")?.Length ?? 0;
        var embd = (int)(h.Whole(h.Arch("embedding_length")) ?? 0);
        if (layers <= 0 || vocab <= 0 || embd <= 0 || !h.Tensors.Any(t => t.Name is "token_embd.weight" or "output.weight"))
        {
            return basics with { Why = "Not a language model llama.cpp's server can chat with (no layers, vocabulary or embeddings)." };
        }

        // Attention: which layers keep a cache, with how many heads, and how wide.
        var heads = (int)(h.Whole(h.Arch("attention.head_count")) ?? 0);
        var kvHeads = new int[layers];
        if (h.Array(h.Arch("attention.head_count_kv")) is { Items: { } perLayer })
        {
            for (var i = 0; i < layers && i < perLayer.Length; i++)
            {
                kvHeads[i] = (int)Convert.ToInt64(perLayer[i], CultureInfo.InvariantCulture);
            }
        }
        else
        {
            var kv = (int)(h.Whole(h.Arch("attention.head_count_kv")) ?? heads);
            var interval = (int)(h.Whole(h.Arch("full_attention_interval")) ?? 1);
            var recurrentOnly = h.Values.ContainsKey(h.Arch("ssm.state_size")) && interval <= 1 && heads == 0;
            for (var i = 0; i < layers; i++)
            {
                kvHeads[i] = recurrentOnly ? 0 : interval > 1 ? ((i + 1) % interval == 0 ? kv : 0) : kv;
            }
        }
        var keyLength = (int)(h.Whole(h.Arch("attention.key_length")) ?? (heads > 0 ? embd / heads : 0));
        var valueLength = (int)(h.Whole(h.Arch("attention.value_length")) ?? keyLength);
        var mlaRank = h.Whole(h.Arch("attention.kv_lora_rank"));
        int? mla = mlaRank is > 0 ? (int)mlaRank + (int)(h.Whole(h.Arch("rope.dimension_count")) ?? 64) : null;
        var window = (int?)h.Whole(h.Arch("attention.sliding_window"));
        var sliding = new bool[layers];
        if (window is > 0)
        {
            if (h.Array(h.Arch("attention.sliding_window_pattern")) is { Items: { } pattern })
            {
                for (var i = 0; i < layers && i < pattern.Length; i++)
                {
                    sliding[i] = pattern[i] is true || (pattern[i] is long v && v != 0);
                }
            }
            else
            {
                var every = (int?)h.Whole(h.Arch("attention.sliding_window_pattern")) ?? SlidingPattern.GetValueOrDefault(arch, 1);
                for (var i = 0; i < layers; i++)
                {
                    sliding[i] = every > 1 && (i + 1) % every != 0;
                }
            }
        }
        var attentionLayers = kvHeads.Count(k => k > 0);
        var ssmLayers = h.Values.ContainsKey(h.Arch("ssm.state_size")) ? layers - attentionLayers : 0;
        long recurrent = 0;
        if (ssmLayers > 0)
        {
            var inner = h.Whole(h.Arch("ssm.inner_size")) ?? 2L * embd;
            var state = h.Whole(h.Arch("ssm.state_size")) ?? 16;
            var groups = h.Whole(h.Arch("ssm.group_count")) ?? 0;
            var conv = h.Whole(h.Arch("ssm.conv_kernel")) ?? 4;
            recurrent = ssmLayers * (((conv - 1) * (inner + 2 * groups * state)) + (inner * state)) * 4;
        }
        var quantizable = mla is not null || (keyLength == valueLength && FlashHeads.Contains(keyLength));
        var cacheTypes = quantizable ? CacheTypeBytes.Keys.ToList() : ["f16", "bf16"];
        var perToken = KvElementsPerToken(kvHeads, sliding, keyLength, valueLength, mla, fullOnly: true);
        var kvBytes = cacheTypes.ToDictionary(t => t, t => (long)Math.Ceiling(perToken * CacheTypeBytes[t]), StringComparer.Ordinal);

        var expertCount = (int)(h.Whole(h.Arch("expert_count")) ?? 0);
        var expertUsed = (int)(h.Whole(h.Arch("expert_used_count")) ?? 0);
        var moeLayers = experts.Take(layers).Count(e => e > 0);
        var isMoe = expertCount > 1 && moeLayers > 0;
        // Active per token, as model cards count them: without the embedding tables (a lookup, not a multiplication).
        long? active = isMoe && expertCount > 0 ? elements - lookupElements - expertElements + (expertElements * expertUsed / expertCount) : null;

        var template = h.Text("tokenizer.chat_template");
        var sampling = new SamplingDefaults(h.Number("general.sampling.temp"), h.Number("general.sampling.top_p"), (int?)h.Whole("general.sampling.top_k"), h.Number("general.sampling.min_p"));
        var scaling = h.Text(h.Arch("rope.scaling.type")) is { } st && st != "none"
            ? new RopeScaling(st, h.Number(h.Arch("rope.scaling.factor")), (int?)h.Whole(h.Arch("rope.scaling.original_context_length")))
            : null;
        var hasRope = h.Values.ContainsKey(h.Arch("rope.freq_base")) || h.Values.ContainsKey(h.Arch("rope.dimension_count"));
        var trained = (int?)h.Whole(h.Arch("context_length"));
        var weights = new ModelWeights(
            byLayer[..layers], experts[..layers], output, hostOnly, tied ? h.Tensors.Where(t => t.Name == "token_embd.weight").Sum(t => t.Bytes) : 0,
            byLayer[layers..].Sum(), kvHeads, sliding, keyLength, valueLength, mla, kvHeads.DefaultIfEmpty(0).Max());

        return basics with
        {
            Kind = ModelKind.Language,
            Structure = isMoe ? "moe" : "dense",
            Attention = attentionLayers == 0 ? "recurrent" : ssmLayers > 0 || attentionLayers < layers ? "hybrid" : sliding.Any(s => s) ? "sliding" : "full",
            Parameters = elements, ActiveParameters = active,
            BitsPerWeight = elements > 0 ? Math.Round((basics.WeightBytes - weights.Mtp) * 8.0 / elements, 2) : null,
            Layers = layers, AttentionLayers = attentionLayers,
            Experts = isMoe ? new ExpertInfo(expertCount, expertUsed, (int)(h.Whole(h.Arch("expert_shared_count")) ?? (h.Tensors.Any(t => t.Name.Contains("_shexp", StringComparison.Ordinal)) ? 1 : 0)), moeLayers, experts.Take(layers).Sum()) : null,
            TrainedContext = trained, SlidingWindow = window is > 0 ? window : null, EmbeddingLength = embd, VocabSize = (int)vocab,
            KvBytesPerToken = kvBytes, RecurrentBytesPerSlot = recurrent, MtpLayers = nextn > 0 && weights.Mtp > 0 ? nextn : 0,
            Thinking = template is null ? null : ThinkingMarks().IsMatch(template),
            Tools = template is null ? null : ToolMarks().IsMatch(template),
            Sampling = sampling is { Temperature: null, TopP: null, TopK: null, MinP: null } ? null : sampling,
            RopeScaling = scaling,
            CanStretch = hasRope && scaling is null && trained is > 0 && attentionLayers > 0,
            Approximate = h.Values.Keys.Any(k => k.Contains(".attention.indexer.", StringComparison.Ordinal) || k.EndsWith(".attention.compress_ratios", StringComparison.Ordinal) || k.Contains(".hyper_connection.", StringComparison.Ordinal))
                || arch.StartsWith("rwkv", StringComparison.Ordinal),
            Weights = weights,
        };
    }

    /// <summary>Cache elements per token of context: full-attention layers only, or with the sliding ones.</summary>
    public static long KvElementsPerToken(int[] kvHeads, bool[] sliding, int keyLength, int valueLength, int? mla, bool fullOnly)
    {
        long sum = 0;
        for (var i = 0; i < kvHeads.Length; i++)
        {
            if (kvHeads[i] == 0 || (fullOnly && i < sliding.Length && sliding[i]))
            {
                continue;
            }
            sum += mla ?? (long)kvHeads[i] * (keyLength + valueLength);
        }
        return sum;
    }

    private static int? LayerOf(string tensor)
    {
        if (!tensor.StartsWith("blk.", StringComparison.Ordinal))
        {
            return null;
        }
        var end = tensor.IndexOf('.', 4);
        return end > 4 && int.TryParse(tensor.AsSpan(4, end - 4), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>The quantization: the file name says it best (dynamic quants mix types); else the header's file type.</summary>
    private static string? Quant(string fileName, long? fileType)
    {
        var m = QuantInName().Match(fileName);
        if (m.Success)
        {
            return m.Groups[1].Value.ToUpperInvariant();
        }
        return fileType switch
        {
            0 => "F32", 1 => "F16", 2 => "Q4_0", 3 => "Q4_1", 7 => "Q8_0", 8 => "Q5_0", 9 => "Q5_1", 10 => "Q2_K", 11 => "Q3_K_S", 12 => "Q3_K_M", 13 => "Q3_K_L",
            14 => "Q4_K_S", 15 => "Q4_K_M", 16 => "Q5_K_S", 17 => "Q5_K_M", 18 => "Q6_K", 19 => "IQ2_XXS", 20 => "IQ2_XS", 21 => "Q2_K_S", 22 => "IQ3_XS",
            23 => "IQ3_XXS", 24 => "IQ1_S", 25 => "IQ4_NL", 26 => "IQ3_S", 27 => "IQ3_M", 28 => "IQ2_S", 29 => "IQ2_M", 30 => "IQ4_XS", 31 => "IQ1_M", 32 => "BF16",
            36 => "TQ1_0", 37 => "TQ2_0", 38 => "MXFP4",
            _ => null,
        };
    }

    [GeneratedRegex(@"[-_.]((?:UD-)?(?:I?Q\d(?:_[A-Z0-9]+)*|BF16|F16|F32|MXFP4(?:_MOE)?))(?:-\d{5}-of-\d{5})?\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex QuantInName();

    [GeneratedRegex(@"<think>|\[THINK\]|enable_thinking|reasoning_content|reasoning_effort|<\|channel\|>analysis|thinking", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkingMarks();

    [GeneratedRegex(@"tool_call|\[TOOL_CALLS\]|available_tools|<tools>|tools\s*is\s*defined|tools\s*%\}|\btools\b.*\bfunction\b", RegexOptions.Singleline)]
    private static partial Regex ToolMarks();
}
