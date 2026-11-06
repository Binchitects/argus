using System.Text.Json.Nodes;
using Llm.Api.Models;
using Llm.Core.Models;

namespace Llm.Tests;

/// <summary>
/// VllmAdvisor against the real config.json of the models the design weighs (Resources/vllm: Qwen/Qwen3.8-Flash-Next-FP8,
/// nvidia/Qwen3.8-Flash-Next-NVFP4, nvidia/Qwen3.5-397B-A17B-NVFP4-V2, Qwen/Qwen3-235B-A22B-Instruct-2507-FP8,
/// openai/gpt-oss-120b, deepseek-ai/DeepSeek-V4-Flash, as Hugging Face had them on 2026-10-16, with their safetensors
/// index's total size and no weights): the cache a token takes, the tensor parallel sizes that split their heads, what
/// fits four 96 GB cards (the numbers of docs/multi-gpu-server.md §2), and sane proposals on every other machine.
/// </summary>
public sealed class VllmAdvisorTests
{
    private const long GiB = 1L << 30;

    private static VllmProfile Load(string name) =>
        VllmAdvisor.Read(Path.Combine(AppContext.BaseDirectory, "Resources", "vllm", name)) ?? throw new FileNotFoundException(name);

    private static LocalModel Model(string name, int context = 32768, string? devices = null, int replicas = 1, int parallel = 16) =>
        new() { Name = name, File = "hf/x/" + name + "@r", EngineKind = EngineKinds.Vllm, KvType = "fp8", Context = context, Devices = devices, Replicas = replicas, Parallel = parallel };

    [Fact]
    public void Each_config_says_its_layers_heads_and_cache_per_token()
    {
        var flash = Load("Qwen3.8-Flash-Next-FP8");
        Assert.Equal((48, 12, 36), (flash.Layers, flash.FullLayers, flash.LinearLayers));
        // 12 full-attention layers × 2 KV heads × 256 × K and V, one byte each: 12 KB a token.
        Assert.Equal(12 * 2 * 256 * 2, flash.KvPerToken);
        Assert.Equal(185502232570, flash.WeightBytes);
        Assert.True(flash.Vision);
        Assert.Equal(1, flash.MtpLayers);
        Assert.Equal(262144, flash.TrainedContext);
        Assert.Equal("fp8", flash.Quantization);

        var big = Load("Qwen3.5-397B-A17B-NVFP4-V2");
        Assert.Equal(15 * 2 * 256 * 2, big.KvPerToken);
        Assert.Equal("modelopt_mixed", big.Quantization);
        // 45 linear layers keep a state per answer: 64 value heads of 128 × 128 in float32, and the convolution's inputs.
        Assert.InRange(big.StatePerSequence, 190_000_000, 195_000_000);

        var qwen3 = Load("Qwen3-235B-A22B-Instruct-2507-FP8");
        Assert.Equal(94 * 4 * 128 * 2, qwen3.KvPerToken);
        Assert.Equal((0, 0), (qwen3.SlidingLayers, qwen3.StatePerSequence > 0 ? 1 : 0));

        var oss = Load("gpt-oss-120b");
        Assert.Equal((18, 18, 128), (oss.FullLayers, oss.SlidingLayers, oss.SlidingWindow));
        // 36 KB a token in bf16: 18 full layers × 8 × 64 × 2.
        Assert.Equal(36864, oss.KvPerToken * VllmAdvisor.CacheBytes("auto"));
        Assert.Equal("mxfp4", oss.Quantization);

        var ds = Load("DeepSeek-V4-Flash");
        Assert.True(ds.Approximate);
        Assert.Equal(1_048_576, ds.TrainedContext);
        // Compressed: a few KB a token at most.
        Assert.InRange(ds.KvPerToken, 1, 8192);
    }

    [Fact]
    public void Tensor_parallel_sizes_must_split_the_heads()
    {
        var oss = Load("gpt-oss-120b");
        Assert.Null(VllmAdvisor.TpProblem(oss, 8));
        Assert.Contains("1, 2, 4, 8, 16", VllmAdvisor.TpProblem(oss, 3), StringComparison.Ordinal);
        var odd = VllmAdvisor.Profile(JsonNode.Parse("""{"num_hidden_layers":4,"num_attention_heads":12,"num_key_value_heads":3,"head_dim":64}""")!.AsObject(), GiB);
        Assert.Contains("12 attention heads", VllmAdvisor.TpProblem(odd, 8), StringComparison.Ordinal);
        Assert.Contains("3 key/value heads", VllmAdvisor.TpProblem(odd, 4), StringComparison.Ordinal);
    }

    /// <summary>The cache tokens one copy holds on GPUs <paramref name="gpus"/> of the four 96 GB cards.</summary>
    private static long Tokens(VllmProfile p, params int[] gpus)
    {
        var devices = GpuPlan.DevicesOf(FakeHardware.Four96()).Where(d => gpus.Contains(d.Index)).ToList();
        return VllmAdvisor.CacheRoom(p, devices, VllmAdvisor.DefaultUtilization) / p.KvPerToken;
    }

    [Fact]
    public void On_four_96_gb_cards_the_numbers_are_the_designs()
    {
        var hw = FakeHardware.Four96();
        // Flash-Next FP8: not at TP=2, at TP=4 about 145 GB of cache, about 12M tokens.
        var flash = Load("Qwen3.8-Flash-Next-FP8");
        Assert.Equal(0, Tokens(flash, 0, 1));
        Assert.InRange(Tokens(flash, 0, 1, 2, 3), 11_000_000, 15_000_000);
        Assert.Equal([0, 1, 2, 3], VllmAdvisor.Propose(flash, hw, 1, 0.90, 262144, "fp8"));
        // Its NVFP4 checkpoint: TP=2, about 30 GB of cache (2.5M tokens), and two copies on four GPUs.
        var nvfp4 = Load("Qwen3.8-Flash-Next-NVFP4");
        Assert.InRange(Tokens(nvfp4, 0, 1), 2_500_000, 4_000_000);
        Assert.Equal([0, 1], VllmAdvisor.Propose(nvfp4, hw, 1, 0.90, 262144, "fp8"));
        Assert.Equal([0, 1, 2, 3], VllmAdvisor.Propose(nvfp4, hw, 2, 0.90, 262144, "fp8"));
        // Qwen3.5-397B NVFP4 at TP=4: about 6M tokens, about 25 people at once with 262K each.
        var big = Load("Qwen3.5-397B-A17B-NVFP4-V2");
        Assert.InRange(Tokens(big, 0, 1, 2, 3), 5_500_000, 8_000_000);
        var bigAdvice = VllmAdvisor.Advise(Model("big", 262144, "0,1,2,3"), big, hw);
        Assert.Null(bigAdvice.FirstError);
        Assert.InRange(bigAdvice.Recommended!.Parallel, 20, 31);
        Assert.Equal(262144, bigAdvice.Recommended.Context);
        // More people than that: a warning that says how many the cache holds at the full window.
        var held = bigAdvice.Recommended.Parallel;
        Assert.DoesNotContain(VllmAdvisor.Advise(Model("big", 262144, "0,1,2,3", parallel: held), big, hw).Problems, p => p.Field == "parallel");
        var crowded = Assert.Single(VllmAdvisor.Advise(Model("big", 262144, "0,1,2,3", parallel: 40), big, hw).Problems, p => p.Field == "parallel");
        Assert.False(crowded.Error);
        Assert.Contains($"40 people at once at 262,144 tokens each need ", crowded.Message, StringComparison.Ordinal);
        Assert.Contains($"It holds {held} at the full window at fp8: vLLM queues or preempts the rest.", crowded.Message, StringComparison.Ordinal);
        // Qwen3-235B FP8 at TP=4: about 1M tokens, about 4 people at once with 262K each.
        var qwen3 = Load("Qwen3-235B-A22B-Instruct-2507-FP8");
        Assert.InRange(Tokens(qwen3, 0, 1, 2, 3), 1_000_000, 1_400_000);
        Assert.InRange(VllmAdvisor.Advise(Model("q3", devices: "0,1,2,3"), qwen3, hw).Recommended!.Parallel, 3, 6);
        // gpt-oss-120b on one GPU, about 18 GB of cache.
        var oss = Load("gpt-oss-120b");
        Assert.Equal([0], VllmAdvisor.Propose(oss, hw, 1, 0.90, 131072, "fp8"));
        Assert.InRange(VllmAdvisor.CacheRoom(oss, [GpuPlan.DevicesOf(hw)[0]], 0.90), 18 * GiB, 26 * GiB);
        // DeepSeek-V4-Flash: TP=2 holds its weights with too little left; TP=4, about 170 GB.
        var ds = Load("DeepSeek-V4-Flash");
        Assert.Equal([0, 1, 2, 3], VllmAdvisor.Propose(ds, hw, 1, 0.90, 1_048_576, "fp8"));
        Assert.InRange(VllmAdvisor.CacheRoom(ds, GpuPlan.DevicesOf(hw), 0.90), 160 * GiB, 190 * GiB);
        Assert.Contains(VllmAdvisor.Advise(Model("ds", 1_048_576, "0,1,2,3"), ds, hw).Problems, p => !p.Error && p.Message.Contains("rough", StringComparison.Ordinal));
    }

    [Fact]
    public void A_copy_that_does_not_fit_or_splits_badly_is_refused_and_what_is_kept_beside_it_counts()
    {
        var hw = FakeHardware.Four96();
        var flash = Load("Qwen3.8-Flash-Next-FP8");
        Assert.Contains(VllmAdvisor.Advise(Model("f", devices: "0,1"), flash, hw).Problems, p => p.Error && p.Field == "memory" && p.Message.Contains("would not start", StringComparison.Ordinal));
        Assert.Contains(VllmAdvisor.Advise(Model("f", devices: "0,1,2"), flash, hw).Problems, p => p.Error && p.Field == "devices");
        Assert.Contains(VllmAdvisor.Advise(Model("f", devices: "0,1,2", replicas: 2), flash, hw).Problems, p => p.Error && p.Field == "replicas");
        Assert.Contains(VllmAdvisor.Advise(Model("f", devices: "0,7"), flash, hw).Problems, p => p.Error && p.Field == "devices");
        Assert.Contains(VllmAdvisor.Advise(Model("f", context: 2_000_000, devices: "0,1,2,3"), flash, hw).Problems, p => p.Error && p.Field == "context");

        // Layout B made by the advisor: Flash-Next NVFP4 kept on GPUs 0-1 at 0.90, gpt-oss goes to GPU 2 (GPU 3 has the picture server).
        var nvfp4 = GpuPlan.Share("flash", hw, [0, 1], 0.90);
        var others = nvfp4.PerGpu.Concat(GpuPlan.Reserved(hw).SelectMany(r => r.PerGpu)).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Sum(x => x.Value));
        var oss = Load("gpt-oss-120b");
        Assert.Equal([2], VllmAdvisor.Propose(oss, hw, 1, 0.90, 131072, "fp8", others));
        // Asked onto GPU 0 beside it: refused, it would take memory kept for another.
        var onZero = VllmAdvisor.Advise(Model("oss", devices: "0"), oss, hw, others);
        Assert.NotNull(onZero.FirstError);
        // Sleep sends its weights to RAM: a machine without that much says so.
        var small = FakeHardware.Four96() with { RamTotal = 40 * GiB };
        Assert.Contains(VllmAdvisor.Advise(Model("oss", devices: "2"), oss, small).Problems, p => !p.Error && p.Message.Contains("asleep", StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> Machines => FakeHardware.All;

    [Theory]
    [MemberData(nameof(Machines))]
    public void Every_machine_gets_a_sane_proposal_or_a_reason(string what, Hardware hw)
    {
        var oss = Load("gpt-oss-120b");
        var advice = VllmAdvisor.Advise(Model("oss", 32768), oss, hw);
        var gpus = advice.Recommended?.Devices is { } d ? Hardware.ParseDevices(d) : null;
        var devices = GpuPlan.DevicesOf(hw);
        if (devices.Count == 0)
        {
            Assert.Contains("needs GPUs", advice.FirstError!.Message, StringComparison.Ordinal);
            return;
        }
        if (devices.Sum(x => x.Total) * 0.9 < oss.WeightBytes)
        {
            // 1 × 24 GB, 24 + 12 GB: it cannot fit, and says so rather than guessing.
            Assert.Null(gpus);
            Assert.Equal("devices", advice.FirstError!.Field);
            return;
        }
        Assert.NotNull(gpus);
        Assert.Null(advice.FirstError);
        // The fewest GPUs that hold it, of the machine's own, alike.
        Assert.All(gpus, g => Assert.Contains(devices, x => x.Index == g));
        Assert.Single(gpus.Select(g => devices.First(x => x.Index == g).Total).Distinct());
        // One GPU where it leaves a working cache (an eighth of its share), two on an 80 GB card, where one leaves 8 GB.
        Assert.True(gpus.Count <= 2, $"{what}: {string.Join(',', gpus)}");
        // And the 397B on the AMD machine: two of its 192 GB GPUs.
        if (what.StartsWith("AMD", StringComparison.Ordinal))
        {
            Assert.Equal([0, 1], VllmAdvisor.Propose(Load("Qwen3.5-397B-A17B-NVFP4-V2"), hw, 1, 0.90, 262144, "fp8"));
        }
    }

    [Fact]
    public void A_gguf_files_header_is_its_profile_and_what_its_engine_cannot_read_of_it_is_refused()
    {
        var dir = Directory.CreateTempSubdirectory("llm-vllm-gguf-").FullName;
        try
        {
            var path = Path.Combine(dir, "Tiny-4B-Q4_K_M.gguf");
            GgufFile.Language("qwen3", layers: 4, context: 40960, kvHeads: 2, headSize: 128, layerBytes: 1L << 28).Write(path);
            var profile = ModelProfiler.Profile(Gguf.Read(path)!, "Tiny-4B-Q4_K_M.gguf");
            var p = VllmAdvisor.FromGguf(profile);
            // 4 layers of 2 KV heads × (128 + 128), a byte each (fp8); 8 heads for tensor parallelism; its weights quantised as the file has them.
            Assert.Equal((4, 4, 0, 8, 2, 128), (p.Layers, p.FullLayers, p.LinearLayers, p.AttentionHeads, p.KvHeads, p.HeadDim));
            Assert.Equal(4 * 2 * 256, p.KvPerToken);
            Assert.Equal((40960, profile.WeightBytes, "Q4_K_M", 1, false, 0), (p.TrainedContext, p.WeightBytes, p.Quantization, p.GgufParts, p.Vision, p.MtpLayers));
            Assert.True(p.WeightBytes > 1L << 30);

            LocalModel M(string kind = EngineKinds.Vllm) => new()
            {
                Name = "tiny", File = "tiny/Tiny-4B-Q4_K_M.gguf", EngineKind = kind, KvType = "fp8", Context = 32768, Parallel = 4,
            };
            var advice = VllmAdvisor.Advise(M(), p, FakeHardware.One24);
            Assert.Null(advice.FirstError);
            Assert.Equal(40960, advice.Limits!.Context.Max);
            Assert.Equal(("Q4_K_M", "qwen3"), (advice.Profile.Quant, advice.Profile.Architecture));
            Assert.Equal("0", advice.Recommended!.Devices);
            Assert.Equal(p.WeightBytes, advice.Estimate!.GpuWeights);
            Assert.Null(VllmAdvisor.Advise(M(EngineKinds.Sglang), p, FakeHardware.One24).FirstError);

            ModelProblem Refused(LocalModel m, VllmProfile of) => VllmAdvisor.Advise(m, of, FakeHardware.One24).FirstError!;
            // Quantised already, and its own prediction layers are a snapshot's.
            var quantised = M();
            quantised.Quantization = "fp8";
            Assert.Equal("quantization", Refused(quantised, p).Field);
            var mtp = M();
            mtp.SpecDecode = "mtp";
            Assert.Contains("from its snapshot, not from a GGUF file", Refused(mtp, p).Message, StringComparison.Ordinal);
            // Split in parts: vLLM's plugin reads each, SGLang one file only, and says how to merge them.
            var split = p with { GgufParts = 3 };
            Assert.Null(VllmAdvisor.Advise(M(), split, FakeHardware.One24).FirstError);
            var one = Refused(M(EngineKinds.Sglang), split);
            Assert.Equal("file", one.Field);
            Assert.Contains("split in 3 parts", one.Message, StringComparison.Ordinal);
            Assert.Contains("llama-gguf-split --merge", one.Message, StringComparison.Ordinal);
            // An architecture its engine cannot read: refused, with those it can (phi3 on vLLM, not on SGLang; qwen35 on neither).
            var qwen35 = Refused(M(), p with { Architecture = "qwen35" });
            Assert.Equal("file", qwen35.Field);
            Assert.Contains("This one is qwen35", qwen35.Message, StringComparison.Ordinal);
            Assert.Contains("run it on llama.cpp", qwen35.Message, StringComparison.Ordinal);
            Assert.Null(VllmAdvisor.Advise(M(), p with { Architecture = "phi3" }, FakeHardware.One24).FirstError);
            Assert.Equal("file", Refused(M(EngineKinds.Sglang), p with { Architecture = "phi3" }).Field);
            // On AMD GPUs vLLM is its own ROCm image (rocm.yml), with no GGUF plugin: refused, where a snapshot is not.
            var amd = VllmAdvisor.Advise(M(), p, FakeHardware.Rocm2x192).FirstError!;
            Assert.Equal("file", amd.Field);
            Assert.Contains("vLLM's own ROCm image", amd.Message, StringComparison.Ordinal);
            Assert.Contains("run it on llama.cpp", amd.Message, StringComparison.Ordinal);
            Assert.Null(VllmAdvisor.Advise(Model("gpt-oss-120b"), Load("gpt-oss-120b"), FakeHardware.Rocm2x192).FirstError);
            // A snapshot is no GGUF file: none of it applies.
            Assert.Equal(0, Load("gpt-oss-120b").GgufParts);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_vllm_models_longest_answer_leaves_its_prompt_room_in_the_whole_context()
    {
        var flash = Load("Qwen3.8-Flash-Next-FP8");
        var m = Model("flash", context: 32768);
        m.MaxOutput = 32768;
        var advice = VllmAdvisor.Advise(m, flash, null);
        // Each of its 16 at once may hold the whole context: the answer is bounded by it, not by a sixteenth of it.
        Assert.Equal(new Bounds(256, 24576), advice.Limits!.MaxOutput);
        Assert.True(Assert.Single(advice.Problems, p => p.Field == "maxOutput").Error);
        m.MaxOutput = 31744;
        Assert.True(Assert.Single(VllmAdvisor.Advise(m, flash, null).Problems, p => p.Field == "maxOutput").Error);
        m.MaxOutput = 24576;
        Assert.DoesNotContain(VllmAdvisor.Advise(m, flash, null).Problems, p => p.Field == "maxOutput");
    }

    /// <summary>SmolLM2-135M-Instruct-Q2_K's GGUF file as vLLM reads it: 30 layers of 3 KV heads × 64, 88 MB of weights, 8,192 tokens.</summary>
    private static readonly VllmProfile Smol = new("llama", "llama", 30, 30, 0, 0, 0, 9, 3, 64, null, 8192, 88L << 20, 30 * 3 * 128, 0, null, 0, false, "Q2_K", false, 1);

    [Fact]
    public void A_tiny_model_on_a_mostly_full_gpu_takes_a_small_share_and_is_told_which_shares_fit()
    {
        var hw = FakeHardware.One24;
        LocalModel M(double? share) => new()
        {
            Name = "smol", File = "smol/SmolLM2-135M-Instruct-Q2_K.gguf", EngineKind = EngineKinds.Vllm, KvType = "fp8", Context = 8192, Parallel = 16, GpuMemoryUtilization = share,
        };
        var reserves = GpuPlan.Reserved(hw).SelectMany(r => r.PerGpu).ToDictionary(x => x.Key, x => x.Value);
        // Its 88 MB and a whole context fit 0.03 of the card (737 MB), as vLLM ran it: not refused for the 3 GiB a large model needs beside its weights.
        Assert.Null(VllmAdvisor.Advise(M(0.03), Smol, hw, reserves).FirstError);
        Assert.DoesNotContain(ModelAdvisor.KindProblems(M(0.03)), p => p.Error);
        Assert.NotNull(VllmAdvisor.Advise(M(0.02), Smol, hw, reserves).FirstError);
        // The default 0.90 takes what the picture server keeps: refused, saying which shares fit here.
        var held = VllmAdvisor.Advise(M(null), Smol, hw, reserves);
        Assert.Equal("gpuMemoryUtilization", held.FirstError!.Field);
        Assert.Contains("0.03 to 0.87 fits here", held.FirstError.Message, StringComparison.Ordinal);
        Assert.Equal((0.03, 0.87), (held.Recommended!.ShareMin, held.Recommended.ShareMax));
        // Beside a model kept on 0.75 of the card, 3 GiB is left: from 0.03 to 0.12, and nothing larger is offered.
        var others = GpuPlan.Share("big", hw, [0], 0.75).PerGpu.Concat(reserves).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Sum(x => x.Value));
        var beside = VllmAdvisor.Advise(M(0.22), Smol, hw, others);
        Assert.Contains("0.03 to 0.12 fits here", beside.FirstError!.Message, StringComparison.Ordinal);
        Assert.Equal((0.03, 0.12), (beside.Recommended!.ShareMin, beside.Recommended.ShareMax));
        Assert.Equal("0", beside.Recommended.Devices);
        Assert.Null(VllmAdvisor.Advise(M(0.12), Smol, hw, others).FirstError);
        // Too little left for even a whole context: no share is offered.
        var full = GpuPlan.Share("big", hw, [0], 0.86).PerGpu.Concat(reserves).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Sum(x => x.Value));
        var none = VllmAdvisor.Advise(M(0.03), Smol, hw, full);
        Assert.NotNull(none.FirstError);
        Assert.Null(none.Recommended?.ShareMin);
    }
}
