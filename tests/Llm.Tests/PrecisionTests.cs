using System.Text.Json.Nodes;
using Llm.Api.Models;
using Llm.Core.Models;

namespace Llm.Tests;

/// <summary>
/// FP4 and FP8 checkpoints: what precision each config says its weights are stored at (Resources/vllm's real configs:
/// NVIDIA's NVFP4 ones are mixed with FP8, gpt-oss is MXFP4; compressed-tensors' and ModelOpt's own file), and how each runs
/// on each GPU generation and engine: weight-only FP4 Marlin on Hopper and Ampere, natively on Blackwell, refused where the
/// engine has no kernels, with the memory said in Admin → Models.
/// </summary>
public sealed class PrecisionTests
{
    private const long GiB = 1L << 30;

    private static readonly GpuDevice H200 = new(0, "NVIDIA H200 NVL", 141 * GiB, GiB);
    private static readonly GpuDevice Rtx3090 = new(0, "NVIDIA GeForce RTX 3090", 24 * GiB, GiB / 2, Compute: "8.6");
    private static readonly GpuDevice B200 = new(0, "NVIDIA B200", 180 * GiB, GiB);
    private static readonly GpuDevice Mi300 = new(0, "AMD Instinct MI300X", 192 * GiB, GiB, "amd", "rocm");

    private static VllmProfile Load(string name) =>
        VllmAdvisor.Read(Path.Combine(AppContext.BaseDirectory, "Resources", "vllm", name)) ?? throw new FileNotFoundException(name);

    private static JsonObject Config(string quantization) =>
        new() { ["architectures"] = new JsonArray("LlamaForCausalLM"), ["quantization_config"] = JsonNode.Parse(quantization) };

    [Fact]
    public void Each_real_config_says_the_precision_its_weights_are_stored_at()
    {
        var nvfp4 = Load("Qwen3.8-Flash-Next-NVFP4").Checkpoint!;
        Assert.Equal((Precision.Nvfp4, "NVFP4 + FP8", "modelopt"), (nvfp4.Format, nvfp4.Label, nvfp4.Method));
        Assert.True(nvfp4.Fp4);
        Assert.Equal((Precision.Nvfp4, "modelopt_mixed"), (Load("Qwen3.5-397B-A17B-NVFP4-V2").Checkpoint!.Format, Load("Qwen3.5-397B-A17B-NVFP4-V2").Checkpoint!.Method));
        var oss = Load("gpt-oss-120b").Checkpoint!;
        Assert.Equal((Precision.Mxfp4, "MXFP4", 4.25, true), (oss.Format, oss.Label, oss.Bits, oss.WeightsOnly));
        Assert.Equal(Precision.Fp8, Load("Qwen3.8-Flash-Next-FP8").Checkpoint!.Format);
        Assert.Equal(Precision.Fp8, Load("DeepSeek-V4-Flash").Checkpoint!.Format);
        // The small copies fp4-bench.py measures (Qwen/Qwen3-0.6B-FP8, mgoin/Qwen3-0.6B-NVFP4: compressed-tensors, W4A4).
        Assert.Equal((Precision.Fp8, "fp8"), (Load("Qwen3-0.6B-FP8").Checkpoint!.Format, Load("Qwen3-0.6B-FP8").Checkpoint!.Method));
        var small = Load("Qwen3-0.6B-NVFP4").Checkpoint!;
        Assert.Equal((Precision.Nvfp4, "compressed-tensors", false), (small.Format, small.Method, small.WeightsOnly));
        Assert.Equal("W4A16", Precision.On(small, EngineKinds.Vllm, [Rtx3090])!.Runs);
    }

    [Fact]
    public void Compressed_tensors_and_modelopts_own_file_are_read_too()
    {
        var nvfp4 = Precision.Of(Config("""{"quant_method": "compressed-tensors", "format": "nvfp4-pack-quantized", "config_groups": {"g": {"weights": {"num_bits": 4, "type": "float", "group_size": 16}, "input_activations": null}}}"""));
        Assert.Equal((Precision.Nvfp4, true), (nvfp4.Format, nvfp4.WeightsOnly));
        var mxfp4 = Precision.Of(Config("""{"quant_method": "compressed-tensors", "config_groups": {"g": {"weights": {"num_bits": 4, "type": "float", "group_size": 32}, "input_activations": {"num_bits": 4, "type": "float"}}}}"""));
        Assert.Equal(Precision.Mxfp4, mxfp4.Format);
        var awq = Precision.Of(Config("""{"quant_method": "awq", "bits": 4}"""));
        Assert.Equal((Precision.Int4Bits, "INT4 (AWQ)"), (awq.Format, awq.Label));
        Assert.Equal(Precision.Bf16, Precision.Of(new JsonObject { ["torch_dtype"] = "bfloat16" }).Format);

        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "hf_quant_config.json"), """{"producer": {"name": "modelopt"}, "quantization": {"quant_algo": "NVFP4", "group_size": 16}}""");
            var own = Precision.Of(new JsonObject { ["torch_dtype"] = "bfloat16" }, dir);
            Assert.Equal((Precision.Nvfp4, "modelopt_fp4", false), (own.Format, own.Method, own.WeightsOnly));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Fp4_is_weight_only_on_hopper_and_ampere_and_native_on_blackwell()
    {
        var nvfp4 = Load("Qwen3.8-Flash-Next-NVFP4").Checkpoint!;
        var hopper = Precision.On(nvfp4, EngineKinds.Vllm, [H200])!;
        Assert.Equal(("W4A16", false, false), (hopper.Runs, hopper.Native, hopper.Refused));
        Assert.Contains("FP4 Marlin", hopper.Kernel);
        Assert.Contains("Hopper, sm_90", hopper.Gpu);
        Assert.Contains("no FP4 tensor cores", hopper.Summary);
        Assert.Equal(("W4A16", false), (Precision.On(nvfp4, EngineKinds.Sglang, [H200])!.Runs, Precision.On(nvfp4, EngineKinds.Sglang, [H200])!.Refused));
        Assert.Equal("W4A16", Precision.On(nvfp4, EngineKinds.Vllm, [Rtx3090])!.Runs);
        var blackwell = Precision.On(nvfp4, EngineKinds.Vllm, [B200])!;
        Assert.Equal(("W4A4", true), (blackwell.Runs, blackwell.Native));

        // gpt-oss' MXFP4 on Hopper: Triton's kernels for its experts on vLLM.
        var oss = Precision.On(Load("gpt-oss-120b").Checkpoint!, EngineKinds.Vllm, [H200])!;
        Assert.Equal("W4A16", oss.Runs);
        Assert.Contains("Triton", oss.Kernel);
    }

    [Fact]
    public void Fp8_is_native_from_ada_and_widened_on_ampere()
    {
        var fp8 = Load("Qwen3.8-Flash-Next-FP8").Checkpoint!;
        Assert.Equal(("W8A8", true), (Precision.On(fp8, EngineKinds.Vllm, [H200])!.Runs, Precision.On(fp8, EngineKinds.Vllm, [H200])!.Native));
        var ampere = Precision.On(fp8, EngineKinds.Vllm, [Rtx3090])!;
        Assert.Equal(("W8A16", false), (ampere.Runs, ampere.Refused));
        Assert.Contains("FP8 Marlin", ampere.Kernel);
        // SGLang takes ModelOpt's FP8 from sm_89 only.
        var modelopt = Precision.Of(Config("""{"quant_method": "modelopt", "quant_algo": "FP8"}"""));
        Assert.True(Precision.On(modelopt, EngineKinds.Sglang, [Rtx3090])!.Refused);
        Assert.False(Precision.On(modelopt, EngineKinds.Sglang, [H200])!.Refused);
    }

    [Fact]
    public void Fp4_is_refused_where_the_engine_has_no_kernels_for_it_saying_what_runs()
    {
        var nvfp4 = Load("Qwen3.8-Flash-Next-NVFP4").Checkpoint!;
        var amd = Precision.On(nvfp4, EngineKinds.Vllm, [Mi300])!;
        Assert.True(amd.Refused);
        Assert.Contains("FP8 or BF16 copy", amd.Summary);
        var turing = Precision.On(Load("gpt-oss-120b").Checkpoint!, EngineKinds.Sglang, [new GpuDevice(0, "Tesla T4", 16 * GiB, GiB)])!;
        Assert.True(turing.Refused);
        Assert.Contains("sm_80", turing.Summary);
        // A copy over GPUs of two generations goes at the older one's pace.
        Assert.Equal("W4A16", Precision.On(nvfp4, EngineKinds.Vllm, [B200, H200 with { Index = 1 }])!.Runs);
        Assert.Null(Precision.On(nvfp4, EngineKinds.LlamaCpp, [H200]));
    }

    [Fact]
    public void A_gpus_compute_capability_is_its_probes_else_known_by_its_name()
    {
        Assert.Equal((8, 6), Precision.ComputeOf(Rtx3090));
        Assert.Equal((9, 0), Precision.ComputeOf(H200));
        Assert.Equal((10, 0), Precision.ComputeOf(B200));
        Assert.Equal((12, 0), Precision.ComputeOf(new GpuDevice(0, "NVIDIA RTX PRO 6000 Blackwell", 96 * GiB, GiB)));
        Assert.Null(Precision.ComputeOf(Mi300));
        Assert.Null(Precision.ComputeOf(new GpuDevice(0, "Some GPU", GiB, 0)));
    }

    [Fact]
    public void The_advisor_says_how_a_snapshot_runs_on_its_gpus_with_its_memory_and_refuses_another_method_on_it()
    {
        var p = Load("Qwen3.8-Flash-Next-NVFP4");
        var hw = new Hardware("NVIDIA H200 NVL", 3, 3 * 141 * GiB, 3 * GiB, 512 * GiB, 32 * GiB,
            Devices: [H200, H200 with { Index = 1 }, H200 with { Index = 2 }]);
        var m = new LocalModel { Name = "flash", File = "hf/nvidia/flash@r", EngineKind = EngineKinds.Vllm, KvType = "fp8", Context = 32768, Parallel = 16 };
        var advice = VllmAdvisor.Advise(m, p, hw);
        Assert.NotNull(advice.Precision);
        Assert.Equal("W4A16", advice.Precision!.Runs);
        Assert.Equal(p.WeightBytes, advice.Precision.WeightBytes);
        Assert.True(advice.Precision.Bf16Bytes > 3 * p.WeightBytes);
        Assert.DoesNotContain(advice.Problems, x => x.Field == "file" && x.Error);

        var wrong = VllmAdvisor.Advise(new LocalModel { Name = "flash", File = m.File, EngineKind = EngineKinds.Vllm, KvType = "fp8", Context = 32768, Parallel = 16, Quantization = "fp8" }, p, hw);
        Assert.Contains(wrong.Problems, x => x.Field == "quantization" && x.Error && x.Message.Contains("NVFP4", StringComparison.Ordinal));

        var amd = new Hardware("AMD Instinct MI300X", 1, 192 * GiB, GiB, 512 * GiB, 32 * GiB, Devices: [Mi300]);
        Assert.Contains(VllmAdvisor.Advise(m, p, amd).Problems, x => x.Field == "file" && x.Error && x.Message.Contains("ROCm", StringComparison.Ordinal));
    }
}
