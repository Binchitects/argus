using System.Globalization;
using System.Text.Json.Nodes;
using Llm.Core.Models;

namespace Llm.Api.Models;

/// <summary>
/// The precision a checkpoint's weights are stored at, as its config says (quantization_config, or ModelOpt's
/// hf_quant_config.json beside it): NVFP4, MXFP4, FP8, INT4 or INT8, or none (BF16/FP16).
/// </summary>
/// <param name="Format">nvfp4, mxfp4, fp8, int4, int8 or bf16.</param>
/// <param name="Label">What Admin → Models says: "NVFP4", "MXFP4", "FP8", "INT4 (AWQ)", "BF16".</param>
/// <param name="Bits">Bits a quantised weight takes with its scales: NVFP4 4.5 (an FP8 scale per 16), MXFP4 4.25 (an E8M0 scale per 32).</param>
/// <param name="Method">The quantisation method the engines name it by (modelopt_fp4, mxfp4, compressed-tensors, fp8...); null: none.</param>
/// <param name="WeightsOnly">Its activations stay 16-bit by the checkpoint (W4A16, W8A16): no input scales were made for them.</param>
public sealed record CheckpointPrecision(string Format, string Label, double Bits, string? Method, bool WeightsOnly = false)
{
    public bool Fp4 => Format is Precision.Nvfp4 or Precision.Mxfp4;
}

/// <summary>
/// How a checkpoint's precision runs on a set of GPUs and an engine: the weights as stored, the activations the math is done at,
/// the kernels, whether the GPU has tensor cores for it (native) or the weights are widened as they are read (weight-only), and
/// what the admin is told, or why it does not run there.
/// </summary>
/// <param name="Runs">"W4A4", "W4A16", "W8A8", "W8A16", "W16A16".</param>
/// <param name="Kernel">The kernels the engine takes there (FP4 Marlin, CUTLASS FP4, Triton...).</param>
/// <param name="Gpu">The GPUs as said: "H200 NVL (Hopper, sm_90)".</param>
/// <param name="Refused">It cannot run there: <see cref="Summary"/> says why and what does.</param>
/// <param name="WeightBytes">Its weights as stored, what each copy keeps on its GPUs.</param>
/// <param name="Bf16Bytes">About what the same weights take at BF16, for comparing.</param>
public sealed record PrecisionRun(string Weights, string Runs, string Kernel, bool Native, string Gpu, string Summary, bool Refused, long WeightBytes = 0, long Bf16Bytes = 0);

/// <summary>
/// What the engines in this stack's images do with FP4 and FP8 checkpoints on each GPU generation, read from their sources
/// (vLLM 0.31.0, SGLang 0.5.21; LMDeploy 0.18 by its documentation):
/// <list type="bullet">
/// <item>NVFP4 (ModelOpt's or compressed-tensors' nvfp4-pack-quantized): native W4A4 only on Blackwell (sm_100, sm_120: FP4
/// tensor cores). On Hopper and Ampere vLLM and SGLang keep the 4-bit weights in memory and widen them in the GEMM: FP4
/// Marlin, W4A16 (vLLM from sm_75, SGLang from sm_80, both chosen by themselves: SGLang's fp4 GEMM backend "auto" and its MoE
/// runner take Marlin below sm_100).</item>
/// <item>MXFP4 (gpt-oss' quant_method mxfp4): vLLM runs it from sm_80 (Triton's kernels or Marlin on Hopper, Marlin on
/// Ampere; dense layers through Marlin), SGLang for MoE layers only, from sm_80; Blackwell runs it natively.</item>
/// <item>FP8: native W8A8 from sm_89 (Ada, Hopper, Blackwell); on Ampere vLLM and SGLang widen FP8 weights with FP8 Marlin (W8A16).</item>
/// </list>
/// On AMD's MI300 (CDNA 3) gpt-oss' MXFP4 runs weight-only (AITER, Triton); NVFP4 does not run on AMD GPUs.
/// So on an H200 an FP4 model saves memory (about 3.5 times less than BF16, 1.8 times less than FP8) and is about as fast as FP8
/// or faster while decoding is memory-bound (few people at once), and slower than FP8 when many at once make it compute-bound.
/// </summary>
public static class Precision
{
    public const string Nvfp4 = "nvfp4";
    public const string Mxfp4 = "mxfp4";
    public const string Fp8 = "fp8";
    /// <summary>Integer weights (AWQ, GPTQ, compressed-tensors' pack-quantized), 4 or 8 bits.</summary>
    public const string Int4Bits = "int4";
    public const string Int8Bits = "int8";
    public const string Bf16 = "bf16";

    public static readonly CheckpointPrecision None = new(Bf16, "BF16", 16, null);

    /// <summary>A snapshot's precision from its config, and ModelOpt's hf_quant_config.json in <paramref name="dir"/> when it has one.</summary>
    public static CheckpointPrecision Of(JsonObject config, string? dir = null)
    {
        var t = config["text_config"] as JsonObject;
        var q = (config["quantization_config"] ?? t?["quantization_config"]) as JsonObject;
        if (q is null && dir is not null && File.Exists(Path.Combine(dir, "hf_quant_config.json")))
        {
            try
            {
                // ModelOpt's own file: {"quantization": {"quant_algo": "NVFP4", ...}}.
                q = (JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "hf_quant_config.json")))?["quantization"] as JsonObject) is { } m
                    ? new JsonObject { ["quant_method"] = "modelopt", ["quant_algo"] = m["quant_algo"]?.DeepClone() }
                    : null;
            }
            catch (System.Text.Json.JsonException)
            {
                q = null;
            }
        }
        if (q is null)
        {
            var dtype = (t?["torch_dtype"] ?? config["torch_dtype"] ?? t?["dtype"] ?? config["dtype"])?.ToString();
            return dtype is "float16" ? None with { Label = "FP16" } : None;
        }
        var method = Str(q["quant_method"])?.ToLowerInvariant();
        var algo = (Str(q["quant_algo"]) ?? Str(q["quantization"]?["quant_algo"]))?.ToUpperInvariant();
        switch (method)
        {
            case "mxfp4":
                return new(Mxfp4, "MXFP4", 4.25, "mxfp4", true);
            case "modelopt" or "modelopt_fp4" when algo is not null && algo.Contains("FP4", StringComparison.Ordinal):
                // NVFP4 is W4A4 by its scales; W4A16_NVFP4, or a checkpoint without input scales, weights only.
                return new(Nvfp4, "NVFP4", 4.5, "modelopt_fp4", algo.StartsWith("W4A16", StringComparison.Ordinal) || NoActivations(q));
            case "modelopt" when algo is "FP8" or "FP8_PB_WO" or "FP8_PER_CHANNEL_PER_TOKEN":
                return new(Fp8, "FP8", 8, "modelopt", algo == "FP8_PB_WO");
            case "modelopt" or "modelopt_mixed" when algo == "MIXED_PRECISION":
                return Mixed(q, method);
            case "fp8" or "fbgemm_fp8":
                return new(Fp8, "FP8", 8, method);
            case "compressed-tensors":
                return CompressedTensors(q);
            case "awq" or "gptq" or "auto-round" or "awq_marlin" or "gptq_marlin":
            {
                var bits = Int(q["bits"] ?? q["w_bit"], 4);
                return new(bits > 6 ? Int8Bits : Int4Bits, $"INT{bits} ({method.ToUpperInvariant()})", bits + 0.25, method, true);
            }
            case "bitsandbytes":
                return new(Int4Bits, "INT4 (bitsandbytes)", 4.5, method, true);
            default:
                return new(Bf16, method is null ? "BF16" : method.ToUpperInvariant(), 16, method);
        }
    }

    /// <summary>
    /// ModelOpt's mixed precision (NVIDIA's own NVFP4 checkpoints: the experts NVFP4, attention and the shared experts FP8):
    /// NVFP4 when any layer is, by its groups or its layers' algorithms, else FP8.
    /// </summary>
    private static CheckpointPrecision Mixed(JsonObject q, string method)
    {
        var groups = (q["config_groups"] as JsonObject)?.Select(g => g.Value).OfType<JsonObject>().ToList() ?? [];
        var layers = (q["quantized_layers"] as JsonObject)?.Select(l => Str(l.Value?["quant_algo"])?.ToUpperInvariant()).ToList() ?? [];
        var fp4 = layers.Any(a => a is not null && a.Contains("FP4", StringComparison.Ordinal))
            || groups.Any(g => g["weights"] is JsonObject w && Int(w["num_bits"], 16) == 4 && Str(w["type"]) == "float");
        var fp8 = layers.Any(a => a is not null && a.StartsWith("FP8", StringComparison.Ordinal))
            || groups.Any(g => g["weights"] is JsonObject w && Int(w["num_bits"], 16) == 8 && Str(w["type"]) == "float");
        return fp4
            ? new(Nvfp4, fp8 ? "NVFP4 + FP8" : "NVFP4", 4.5, method, NoActivations(q))
            : new(Fp8, "FP8", 8, method, NoActivations(q));
    }

    /// <summary>compressed-tensors' groups: nvfp4-pack-quantized and mxfp4-pack-quantized are FP4, float-quantized FP8, pack-quantized INT4.</summary>
    private static CheckpointPrecision CompressedTensors(JsonObject q)
    {
        var groups = (q["config_groups"] as JsonObject)?.Select(g => g.Value).OfType<JsonObject>().ToList() ?? [];
        var format = Str(q["format"]) ?? groups.Select(g => Str(g["format"])).FirstOrDefault(f => f is not null);
        var weights = groups.Select(g => g["weights"] as JsonObject).FirstOrDefault(w => w is not null);
        var bits = Int(weights?["num_bits"], 16);
        var type = Str(weights?["type"]);
        var weightsOnly = groups.Count > 0 && groups.All(g => g["input_activations"] is null);
        if (format == "nvfp4-pack-quantized" || (bits == 4 && type == "float" && Int(weights?["group_size"], 16) == 16))
        {
            return new(Nvfp4, "NVFP4", 4.5, "compressed-tensors", weightsOnly);
        }
        if (format == "mxfp4-pack-quantized" || (bits == 4 && type == "float"))
        {
            return new(Mxfp4, "MXFP4", 4.25, "compressed-tensors", weightsOnly);
        }
        if (format == "float-quantized" || (bits == 8 && type == "float"))
        {
            return new(Fp8, "FP8", 8, "compressed-tensors", weightsOnly);
        }
        if (bits is 4 or 8)
        {
            return new(bits == 4 ? Int4Bits : Int8Bits, $"INT{bits} (compressed-tensors)", bits + 0.25, "compressed-tensors", weightsOnly);
        }
        return new(Bf16, "BF16", 16, "compressed-tensors");
    }

    private static bool NoActivations(JsonObject q) =>
        q["config_groups"] is JsonObject groups && groups.Count > 0 && groups.All(g => g.Value is JsonObject o && o["input_activations"] is null);

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int Int(JsonNode? n, int fallback) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;

    /// <summary>
    /// A GPU's compute capability: what its probe says (nvidia-smi's compute_cap), else what its name is known as; null for a
    /// GPU that is not NVIDIA's or a name not known. 9.0 Hopper (H100, H200, H20, GH200), 10.0 Blackwell (B200, B300, GB200),
    /// 12.0 Blackwell workstation (RTX 50, RTX PRO 6000), 8.9 Ada, 8.6 and 8.0 Ampere, 7.5 Turing, 7.0 Volta.
    /// </summary>
    public static (int Major, int Minor)? ComputeOf(GpuDevice d)
    {
        if (d.Compute is { Length: > 0 } c && c.Split('.') is [var a, var b]
            && int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var major) && int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return (major, minor);
        }
        if (d.Backend != "cuda")
        {
            return null;
        }
        var n = d.Name.ToUpperInvariant();
        bool Has(params string[] parts) => parts.Any(p => n.Contains(p, StringComparison.Ordinal));
        return n switch
        {
            _ when Has("H100", "H200", "H800", "H20", "GH200") => (9, 0),
            _ when Has("B200", "B100", "B300", "GB200", "GB300") => (10, 0),
            _ when Has("RTX 50", "RTX PRO 6000", "RTX PRO 5000", "RTX PRO 4000", "BLACKWELL") => (12, 0),
            _ when Has("L40", "L4", "L20", "RTX 40", "ADA") => (8, 9),
            _ when Has("A100", "A800", "A30") => (8, 0),
            _ when Has("A10", "A40", "A16", "A2", "RTX 30", "RTX A6000", "RTX A5000", "RTX A4000") => (8, 6),
            _ when Has("T4", "RTX 20", "TITAN RTX", "QUADRO RTX") => (7, 5),
            _ when Has("V100", "TITAN V") => (7, 0),
            _ => null,
        };
    }

    /// <summary>A generation's name for a compute capability.</summary>
    public static string Generation((int Major, int Minor) cc) => cc switch
    {
        (>= 12, _) => "Blackwell",
        (10, _) or (11, _) => "Blackwell",
        (9, _) => "Hopper",
        (8, 9) => "Ada",
        (8, _) => "Ampere",
        (7, 5) => "Turing",
        (7, _) => "Volta",
        _ => "an older generation",
    };

    private static string Said(GpuDevice d, (int Major, int Minor)? cc) =>
        cc is { } c ? $"{d.Name} ({Generation(c)}, sm_{c.Major}{c.Minor})" : d.Name;

    /// <summary>
    /// How <paramref name="p"/> runs on <paramref name="engineKind"/> on these GPUs (the slowest of them, as a copy goes at its
    /// pace): its weights, the math, its kernels, and what it is told; null for an engine that reads no snapshot (llama.cpp)
    /// or no GPUs.
    /// </summary>
    public static PrecisionRun? On(CheckpointPrecision p, string engineKind, IReadOnlyList<GpuDevice> gpus)
    {
        if (gpus.Count == 0 || engineKind is EngineKinds.LlamaCpp)
        {
            return null;
        }
        var label = EngineKinds.Of(engineKind)?.Label ?? engineKind;
        var amd = gpus.FirstOrDefault(g => g.Backend == "rocm");
        var slowest = gpus.Select(g => (Gpu: g, Cc: ComputeOf(g))).OrderBy(x => x.Cc?.Major ?? 99).ThenBy(x => x.Cc?.Minor ?? 99).First();
        var gpu = amd is not null ? amd.Name : Said(slowest.Gpu, slowest.Cc);
        var cc = slowest.Cc;
        var at = cc is { } c0 ? (c0.Major * 10) + c0.Minor : (int?)null;
        string Weights() => $"{p.Label} weights ({p.Bits.ToString("0.##", CultureInfo.InvariantCulture)} bits each with their scales)";
        if (p.Format == Bf16 || p.Format is Int4Bits or Int8Bits)
        {
            return new(p.Label, p.Format == Bf16 ? "W16A16" : p.Format == Int4Bits ? "W4A16" : "W8A16", p.Format == Bf16 ? "BF16 GEMMs" : "Marlin (weight-only)", p.Format == Bf16, gpu,
                p.Format == Bf16 ? $"{p.Label} weights run as they are on {gpu}." : $"{Weights()} run weight-only (W{(p.Format == Int4Bits ? 4 : 8)}A16) on {gpu}.", false);
        }
        if (amd is not null)
        {
            // gpt-oss' MXFP4 runs weight-only through AITER's and Triton's kernels on MI300; NVFP4 is NVIDIA's alone.
            return p.Format == Mxfp4 && p.Method == "mxfp4"
                ? new(p.Label, "W4A16", "AITER / Triton MXFP4 (weight-only)", false, gpu, $"{Weights()} run weight-only (W4A16) on {amd.Name}, through AITER's and Triton's kernels.", false)
                : p.Fp4
                    ? new(p.Label, "-", "-", false, gpu, $"{p.Label} does not run on {amd.Name} with this stack's ROCm images (NVFP4 is NVIDIA's; MI300 has no FP4): "
                        + "run its FP8 or BF16 copy there.", true)
                    : new(p.Label, "W8A8", "FP8 GEMMs", true, gpu, $"{Weights()} run natively (W8A8) on {amd.Name}.", false);
        }
        if (at is null)
        {
            return new(p.Label, "?", "?", false, gpu, $"{Weights()}: what {gpu} runs them as is not known here (no compute capability). "
                + $"{label} takes FP4 tensor cores on Blackwell and widens the weights (W4A16) on Hopper and Ampere.", false);
        }
        if (p.Format == Fp8)
        {
            // SGLang takes ModelOpt's FP8 from sm_89 only (no FP8 Marlin for it); vLLM widens it below that.
            if (engineKind == EngineKinds.Sglang && p.Method == "modelopt" && at < 89)
            {
                return new(p.Label, "-", "-", false, gpu, $"SGLang runs ModelOpt's FP8 from sm_89 (Ada, Hopper), and {gpu} is older: run it on vLLM (FP8 Marlin, W8A16).", true);
            }
            return at >= 89
                ? new(p.Label, "W8A8", "FP8 tensor cores", true, gpu, $"{Weights()} run natively (W8A8) on {gpu}.", false)
                : at >= 80
                    ? new(p.Label, "W8A16", "FP8 Marlin (weight-only)", false, gpu, $"{Weights()} on {gpu}, which has no FP8 tensor cores: {label} widens them as it reads them "
                        + "(FP8 Marlin, W8A16). They take half the memory of BF16; the math is BF16.", false)
                    : new(p.Label, "-", "-", false, gpu, $"FP8 needs sm_80 or newer for {label}; {gpu} is older: run its BF16 or INT4 copy.", true);
        }
        // FP4: native on Blackwell; weight-only below it, where the engine has its kernels.
        if (at >= 100)
        {
            return new(p.Label, p.WeightsOnly ? "W4A16" : "W4A4", p.WeightsOnly ? "FP4 Marlin" : "FP4 tensor cores (CUTLASS / FlashInfer)", !p.WeightsOnly, gpu,
                p.WeightsOnly ? $"{Weights()} run weight-only (W4A16) on {gpu}: the checkpoint has no activation scales." : $"{Weights()} run natively (W4A4) on {gpu}'s FP4 tensor cores.", false);
        }
        var least = engineKind switch
        {
            EngineKinds.Sglang => 80,
            EngineKinds.Vllm => p.Format == Nvfp4 ? 75 : 80,
            // LMDeploy runs MXFP4 (gpt-oss) from sm_80; NVFP4 only on Blackwell.
            _ => p.Format == Mxfp4 ? 80 : 100,
        };
        if (engineKind == EngineKinds.Sglang && p.Format == Mxfp4 && p.Method == "compressed-tensors")
        {
            least = 999;
        }
        if (at < least)
        {
            var what = least == 100
                ? $"{label} runs {p.Label} only on Blackwell (sm_100 or newer)"
                : least == 999 ? $"{label} runs MXFP4 only as gpt-oss stores it (quant_method mxfp4)" : $"{label} runs {p.Label} from sm_{least}";
            var instead = engineKind is EngineKinds.Vllm ? "its FP8 or BF16 copy" : "it on vLLM (FP4 Marlin, W4A16), or its FP8 copy";
            return new(p.Label, "-", "-", false, gpu, $"{what}, and {gpu} is not: run {instead}.", true);
        }
        var kernel = p.Format == Mxfp4 && at == 90 && engineKind == EngineKinds.Vllm ? "Triton's MXFP4 kernels for experts, FP4 Marlin for the rest" : "FP4 Marlin";
        return new(p.Label, "W4A16", kernel, false, gpu,
            $"{Weights()} on {gpu}, which has no FP4 tensor cores: {label} keeps them 4-bit in memory and widens them as it reads them ({kernel}, W4A16; the math is BF16). "
            + "They take about a quarter of BF16's memory and half of FP8's; decoding few at once is about as fast as FP8 or faster, many at once slower.", false);
    }
}
