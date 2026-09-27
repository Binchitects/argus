using Llm.Api.Models;
using Llm.Core.Models;
using Microsoft.Extensions.Options;

namespace Llm.Tests;

/// <summary>The advisor's memory arithmetic and verdicts, against a machine of known size.</summary>
public sealed class ModelAdvisorTests : IDisposable
{
    private const long MiB = 1L << 20;
    private const long GiB = 1L << 30;
    private readonly string _dir = Directory.CreateTempSubdirectory("llm-advisor-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (LibraryEntry Model, IReadOnlyList<LibraryEntry> All) Library(string file, GgufFile gguf)
    {
        gguf.Write(Path.Combine(_dir, file));
        var all = new ModelLibrary(Options.Create(new EngineOptions { LibraryDir = _dir })).List();
        return (all.Single(e => e.File.Path == file), all);
    }

    /// <summary>A GPU with <paramref name="forModels"/> free for models after llama.cpp's own margin; RAM likewise.</summary>
    private static Hardware Machine(long forModels, long ram = 64 * GiB) => new("Test GPU", 1, forModels + Hardware.FitMargin, 0, 0, ram, 0);

    [Fact]
    public void A_mixture_of_experts_too_big_for_the_gpu_keeps_experts_in_ram_and_its_layers_on_the_gpu()
    {
        // 8 layers of 64 MiB with 512 MiB of experts each: 4.5 GiB of weights.
        var (model, all) = Library("moe/Moe-Q4_K_M.gguf", GgufFile.Language("qwen3moe", layers: 8, experts: 64, used: 4, layerBytes: 64 * MiB, expertBytes: 512 * MiB));
        var m = new LocalModel { Name = "m", File = model.File.Path, Context = 32768, Parallel = 1 };

        var roomy = ModelAdvisor.Advise(m, model, all, Machine(8 * GiB));
        Assert.Equal("gpu", roomy.Estimate!.Fit);
        Assert.Empty(roomy.Problems);

        var tight = ModelAdvisor.Advise(m, model, all, Machine(3 * GiB));
        Assert.Equal("experts", tight.Estimate!.Fit);
        Assert.InRange(tight.Estimate.ExpertLayersInRam, 1, 7);
        Assert.Equal(9, tight.Estimate.GpuLayers);
        Assert.True(tight.Estimate.GpuTotal <= 3 * GiB);
        Assert.DoesNotContain(tight.Problems, p => p.Error);

        // By hand, all on the GPU: it would not load, and says so.
        var hand = ModelAdvisor.Advise(new LocalModel { Name = "m", File = model.File.Path, Context = 32768, Placement = "manual", GpuLayers = 99 }, model, all, Machine(3 * GiB));
        Assert.Equal("over", hand.Estimate!.Fit);
        Assert.Contains(hand.Problems, p => p.Field == "memory" && p.Error);

        // Its recommendation fits: the largest context with the experts' layers placed.
        Assert.NotNull(tight.Recommended);
        Assert.Equal(1024, tight.Recommended.Ubatch);
    }

    /// <summary>Two GPUs of <paramref name="each"/> for models (the first also runs the image server's <paramref name="image"/>).</summary>
    private static Hardware TwoGpus(long each, long image = 0, long ram = 64 * GiB) =>
        new("GPU A", 2, 2 * (each + Hardware.FitMargin) + image, 0, image, ram, 0,
            Devices: [new GpuDevice(0, "GPU A", each + Hardware.FitMargin + image, 0), new GpuDevice(1, "GPU B", each + Hardware.FitMargin, 0)]);

    [Fact]
    public void A_model_on_some_gpus_sees_only_those_and_the_image_server_only_on_the_first()
    {
        var hw = TwoGpus(10 * GiB, image: 2 * GiB);
        Assert.Equal(20 * GiB, hw.GpuForModels);
        var second = hw.On([1]);
        Assert.Equal(1, second.Gpus);
        Assert.Equal("GPU B", second.GpuName);
        Assert.Equal(10 * GiB, second.GpuForModels);
        Assert.Equal(0, second.ImageReserve);
        Assert.Equal(10 * GiB, hw.On([0]).GpuForModels);
        Assert.Same(hw, hw.On(null));
        Assert.Equal([0, 1], Hardware.ParseDevices(" 1, 0,1"));
        Assert.Null(Hardware.ParseDevices("CUDA0"));

        // The estimate follows: 3 GiB of layers fit one GPU of 10, not a machine with 2 left.
        var (model, all) = Library("dense/Gpu-Q8_0.gguf", GgufFile.Language("llama", layers: 12, layerBytes: 256 * MiB));
        var m = new LocalModel { Name = "g", File = model.File.Path, Context = 8192, Devices = "1" };
        Assert.Equal("gpu", ModelAdvisor.Advise(m, model, all, hw).Estimate!.Fit);
        Assert.Equal(10 * GiB, ModelAdvisor.Advise(m, model, all, hw).Estimate!.GpuBudget);
    }

    [Fact]
    public void Kept_models_on_one_gpu_that_holds_one_warn_and_on_two_gpus_fit()
    {
        var (model, all) = Library("dense/Kept-Q8_0.gguf", GgufFile.Language("llama", layers: 24, layerBytes: 256 * MiB));
        var hw = TwoGpus(8 * GiB);
        LocalModel On(string name, string? devices) => new() { Name = name, File = model.File.Path, Context = 8192, Devices = devices };

        var shared = ModelAdvisor.PlanKept([(On("a", "0"), model), (On("b", "0"), model)], all, hw);
        var warning = Assert.Single(shared.Problems);
        Assert.False(warning.Error);
        Assert.Contains("On GPU 0 (GPU A)", warning.Message, StringComparison.Ordinal);
        Assert.Equal(["a", "b"], shared.Gpus[0].Models);
        Assert.Empty(shared.Gpus[1].Models);

        var apart = ModelAdvisor.PlanKept([(On("a", "0"), model), (On("b", "1"), model)], all, hw);
        Assert.Empty(apart.Problems);
        Assert.All(apart.Gpus, g => Assert.True(g.Need <= g.Budget));

        // Across both GPUs, a model counts on each by what each has.
        var split = ModelAdvisor.PlanKept([(On("c", null), model)], all, hw);
        Assert.InRange(split.Gpus[0].Need, split.Gpus[1].Need - MiB, split.Gpus[1].Need + MiB);

        // Caches and buffers that cannot fit the GPUs and RAM together: one would not load.
        var tiny = new Hardware("small", 1, Hardware.FitMargin + (16 * MiB), 0, 0, 16 * MiB, 0);
        Assert.NotNull(ModelAdvisor.PlanKept([(On("h1", null), model), (On("h2", null), model)], all, tiny).FirstError);

        // A model whose file is outside the library is said, not guessed.
        Assert.Contains(ModelAdvisor.PlanKept([(On("x", null), null)], all, hw).Problems, p => p.Message.Contains("outside the model library", StringComparison.Ordinal));
    }

    [Fact]
    public void A_model_on_chosen_gpus_names_them_as_the_engine_numbers_them()
    {
        var m = new LocalModel { Name = "g", File = "g/G.gguf", Devices = "0,2" };
        Assert.Contains("device = CUDA0,CUDA2\n", ModelCatalog.Preset(m, "/library"), StringComparison.Ordinal);
        Assert.DoesNotContain("device", ModelCatalog.Preset(new LocalModel { Name = "g", File = "g/G.gguf" }, "/library"), StringComparison.Ordinal);
        Assert.Contains("is set by the form", ModelCatalog.CheckExtra("device = CUDA1"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_dense_model_too_big_for_the_gpu_runs_layers_from_ram_with_a_warning()
    {
        var (model, all) = Library("dense/Dense-Q8_0.gguf", GgufFile.Language("llama", layers: 16, layerBytes: 256 * MiB));
        var advice = ModelAdvisor.Advise(new LocalModel { Name = "d", File = model.File.Path, Context = 8192 }, model, all, Machine(2 * GiB));
        Assert.Equal("layers", advice.Estimate!.Fit);
        Assert.InRange(advice.Estimate.GpuLayers, 1, 16);
        var warning = Assert.Single(advice.Problems);
        Assert.False(warning.Error);
        Assert.Contains("run from RAM", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cache_that_fits_neither_the_gpu_nor_ram_cannot_load()
    {
        // 64 layers of 8 heads of 256+256: 256 KiB of cache per token at f16; a million tokens need 256 GiB.
        var (model, all) = Library("wide/Wide-F16.gguf", GgufFile.Language("llama", layers: 64, kvHeads: 8, headSize: 256, context: 1_048_576));
        var advice = ModelAdvisor.Advise(new LocalModel { Name = "w", File = model.File.Path, Context = 1_048_576, KvType = "f16" }, model, all, Machine(4 * GiB, ram: 32 * GiB));
        Assert.Equal("none", advice.Estimate!.Fit);
        Assert.Contains(advice.Problems, p => p.Field == "memory" && p.Error);
        // A context that fits is recommended instead.
        Assert.True(advice.Recommended!.Context < 1_048_576);
    }

    [Fact]
    public void A_low_gpu_power_cap_is_named_for_a_model_all_on_the_gpu_not_for_experts_in_ram()
    {
        var (dense, all) = Library("dense/Small-Q8_0.gguf", GgufFile.Language("llama", layers: 4, layerBytes: 64 * MiB));
        var capped = Machine(8 * GiB) with { PowerLimit = 150, PowerDefault = 350 };
        var gpu = ModelAdvisor.Advise(new LocalModel { Name = "s", File = dense.File.Path, Context = 8192 }, dense, all, capped);
        Assert.Equal("gpu", gpu.Estimate!.Fit);
        Assert.Contains(gpu.Problems, p => p.Field == "power" && !p.Error && p.Message.Contains("150 W of its 350 W", StringComparison.Ordinal));
        Assert.DoesNotContain(ModelAdvisor.Advise(new LocalModel { Name = "s", File = dense.File.Path, Context = 8192 }, dense, all, Machine(8 * GiB) with { PowerLimit = 300, PowerDefault = 350 }).Problems, p => p.Field == "power");

        var (moe, all2) = Library("moe/Big-Q4_K_M.gguf", GgufFile.Language("qwen3moe", layers: 8, experts: 64, used: 4, layerBytes: 64 * MiB, expertBytes: 512 * MiB));
        var experts = ModelAdvisor.Advise(new LocalModel { Name = "m", File = moe.File.Path, Context = 8192 }, moe, all2, Machine(3 * GiB) with { PowerLimit = 150, PowerDefault = 350 });
        Assert.Equal("experts", experts.Estimate!.Fit);
        Assert.DoesNotContain(experts.Problems, p => p.Field == "power");
    }

    [Fact]
    public void Drafting_with_the_models_own_layer_adds_its_weights_cache_and_buffers()
    {
        var (model, all) = Library("mtp/Mtp-Q4_K_M.gguf", GgufFile.Language("qwen35", layers: 8, nextn: 1, layerBytes: 64 * MiB));
        Assert.Equal(1, model.Profile.MtpLayers);
        var off = ModelAdvisor.Advise(new LocalModel { Name = "t", File = model.File.Path, Context = 32768 }, model, all, Machine(8 * GiB)).Estimate!;
        var on = ModelAdvisor.Advise(new LocalModel { Name = "t", File = model.File.Path, Context = 32768, Mtp = true }, model, all, Machine(8 * GiB)).Estimate!;
        Assert.Equal(64 * MiB, on.GpuWeights - off.GpuWeights);
        Assert.True(on.GpuCache > off.GpuCache);
        Assert.True(on.GpuCompute > 3 * off.GpuCompute);
    }

    [Fact]
    public void Hybrid_models_keep_a_cache_in_their_attention_layers_only_and_a_state_per_answer()
    {
        var gguf = GgufFile.Language("qwen35", layers: 8, interval: 4, nextn: 1)
            .U32("qwen35.ssm.state_size", 128).U32("qwen35.ssm.inner_size", 1024).U32("qwen35.ssm.group_count", 4).U32("qwen35.ssm.conv_kernel", 4);
        var (model, _) = Library("hybrid/Hybrid-Q4_K_M.gguf", gguf);
        var p = model.Profile;
        Assert.Equal("hybrid", p.Attention);
        Assert.Equal(8, p.Layers);
        Assert.Equal(2, p.AttentionLayers);
        Assert.Equal(1, p.MtpLayers);
        Assert.Equal(2 * 2 * 256 * 2, p.KvBytesPerToken["f16"]);
        // 6 recurrent layers: a convolution of 3 x (1024 + 2 x 4 x 128) and a 1024 x 128 state, in f32.
        Assert.Equal(6 * ((3 * (1024 + (2 * 4 * 128))) + (1024 * 128)) * 4, p.RecurrentBytesPerSlot);
        Assert.True(p.CanStretch);
    }
}
