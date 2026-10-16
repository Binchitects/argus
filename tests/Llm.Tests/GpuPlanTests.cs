using Llm.Api.Models;
using Llm.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Llm.Tests;

/// <summary>
/// GpuPlan, the one owner of placement, on every kind of machine: what each GPU holds (chat models, vLLM groups, the picture
/// server and the embedder on their own GPUs), the design's layouts on four 96 GB cards, over-commit refused, a GPU the
/// machine lacks, the hardware file of any vendor, and llama.cpp's device names for each backend. Today's one-3090 machine
/// sees exactly what it saw before.
/// </summary>
public sealed class GpuPlanTests : IDisposable
{
    private const long GiB = FakeHardware.GiB;
    private const long MiB = 1L << 20;
    private readonly string _dir = Directory.CreateTempSubdirectory("llm-gpuplan-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_one_3090_machine_has_the_room_it_had_with_the_picture_server_on_its_gpu()
    {
        var hw = FakeHardware.One24;
        // As before: the card less the picture server's 2 GiB and llama.cpp's 1 GiB margin.
        Assert.Equal((24 * GiB) - FakeHardware.Picture - Hardware.FitMargin, hw.GpuForModels);
        Assert.Equal(hw.GpuForModels, hw.On([0]).GpuForModels);
        var reserve = Assert.Single(hw.Reserves!);
        Assert.Equal((0, "the picture server"), (reserve.Gpu, reserve.Name));
        // And its presets name the GPU as before.
        Assert.Contains("device = CUDA0\n", ModelCatalog.Preset(new LocalModel { Name = "m", File = "m.gguf", Devices = "0" }, "/library"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_picture_server_and_the_embedder_are_charged_to_the_gpu_each_runs_on()
    {
        var bare = FakeHardware.Four96(null) with { Reserves = null };
        var o = new EngineOptions();
        Assert.Equal(0, HardwareProbe.WithReserves(bare, o, null)!.Reserves!.Single().Gpu);
        Assert.Equal(3, HardwareProbe.WithReserves(bare, o, 3)!.Reserves!.Single().Gpu);
        Assert.Equal(2, HardwareProbe.WithReserves(bare, new EngineOptions { MediaGpu = 2 }, null)!.Reserves!.Single().Gpu);
        var both = HardwareProbe.WithReserves(bare, new EngineOptions { EmbedGpu = 1 }, 3)!;
        Assert.Equal([(3, "the picture server"), (1, "the embedder")], both.Reserves!.Select(r => (r.Gpu, r.Name)));
        // A GPU the machine lacks is charged nothing; a machine without GPUs keeps everything in RAM.
        Assert.Empty(HardwareProbe.WithReserves(bare, new EngineOptions { MediaGpu = 9 }, null)!.Reserves!);
        Assert.Null(HardwareProbe.WithReserves(FakeHardware.CpuOnly, o, null)!.Reserves);
        // A model on GPUs 0 and 1 gives up nothing for a picture server on GPU 3.
        var hw = FakeHardware.Four96(3);
        Assert.Equal(hw.On([0]).GpuForModels, hw.On([3]).GpuForModels + FakeHardware.Picture);
    }

    /// <summary>A vLLM group on these GPUs at this share of each.</summary>
    private static Resident Vllm(string name, Hardware hw, double share, params int[] gpus) => GpuPlan.Share(name, hw, gpus, share);

    [Fact]
    public void The_designs_layouts_on_four_96_gb_cards_fit_and_an_over_commit_is_refused()
    {
        var hw = FakeHardware.Four96(3);
        // A: one flagship at TP=4 at 0.80, the 27B GGUF (17 GiB) on GPUs 2 and 3, the picture server on 3.
        var a = GpuPlan.Of(hw, [Vllm("Qwen3.5-397B", hw, 0.80, 0, 1, 2, 3), GpuPlan.Spread("Qwen3.8-27B", "llamacpp", hw, [2, 3], 17 * GiB), .. GpuPlan.Reserved(hw)]);
        Assert.Null(a.FirstError);
        Assert.Empty(a.Problems);
        Assert.Equal(["Qwen3.5-397B", "Qwen3.8-27B", "the picture server"], a.Gpus[3].Models);
        // B: Flash-Next at TP=2 on 0-1, gpt-oss on 2, the picture server, a small model and the embedder on 3.
        var b = GpuPlan.Of(hw, [Vllm("Flash-Next", hw, 0.90, 0, 1), Vllm("gpt-oss-120b", hw, 0.90, 2), GpuPlan.Spread("small", "llamacpp", hw, [3], 8 * GiB),
            .. GpuPlan.Reserved(HardwareProbe.WithReserves(hw, new EngineOptions { EmbedGpu = 3 }, 3)!)]);
        Assert.Empty(b.Problems);
        Assert.True(b.FreeOn(3) > 70 * GiB);
        // C: the coder at TP=4, its share leaving room for the picture server on 3.
        var c = GpuPlan.Of(hw, [Vllm("Qwen3-Coder-480B", hw, 0.90, 0, 1, 2, 3), .. GpuPlan.Reserved(hw)]);
        Assert.Empty(c.Problems);

        // Over-commit: a second TP=2 group on GPUs 0-1 beside A's flagship would not start (vLLM cannot move to RAM).
        var over = GpuPlan.Of(hw, [Vllm("Qwen3.5-397B", hw, 0.80, 0, 1, 2, 3), Vllm("gpt-oss-120b", hw, 0.30, 1)]);
        var error = Assert.IsType<ModelProblem>(over.FirstError);
        Assert.Contains("GPU 1", error.Message, StringComparison.Ordinal);
        Assert.Contains("cannot move to RAM", error.Message, StringComparison.Ordinal);
        // Two llama.cpp models on one GPU that holds one: slower, not refused.
        var spill = GpuPlan.Of(hw, [GpuPlan.Spread("a", "llamacpp", hw, [0], 60 * GiB), GpuPlan.Spread("b", "llamacpp", hw, [0], 60 * GiB)]);
        Assert.Null(spill.FirstError);
        Assert.Single(spill.Problems);
    }

    public static IEnumerable<object[]> Machines => FakeHardware.All;

    [Theory]
    [MemberData(nameof(Machines))]
    public void Every_machine_places_a_model_on_its_own_gpus_and_refuses_a_gpu_it_lacks(string what, Hardware hw)
    {
        var devices = GpuPlan.DevicesOf(hw);
        var spread = GpuPlan.Spread("m", "llamacpp", hw, null, 10 * GiB);
        var plan = GpuPlan.Of(hw, [spread, .. GpuPlan.Reserved(hw)]);
        Assert.Equal(devices.Count, plan.Gpus.Count);
        if (devices.Count == 0)
        {
            Assert.Equal(0, hw.GpuForModels);
            Assert.Empty(spread.PerGpu);
            Assert.Contains("no GPU", GpuPlan.Of(hw, [GpuPlan.Spread("m", "llamacpp", FakeHardware.One24, [0], GiB)]).FirstError!.Message, StringComparison.Ordinal);
            return;
        }
        // Spread by what each GPU has: the sum is the model, a bigger GPU takes more, and the picture server's GPU less.
        Assert.InRange(spread.PerGpu.Values.Sum(), (10 * GiB) - devices.Count, 10 * GiB);
        var room = devices.ToDictionary(d => d.Index, d => d.Total - hw.Reserves!.Where(r => r.Gpu == d.Index).Sum(r => r.Bytes));
        foreach (var x in devices)
        {
            foreach (var y in devices.Where(y => room[y.Index] < room[x.Index]))
            {
                Assert.True(spread.PerGpu[x.Index] > spread.PerGpu[y.Index], $"{what}: GPU {x.Index} has more room than GPU {y.Index}");
            }
        }
        Assert.Empty(plan.Problems);
        var missing = devices.Max(d => d.Index) + 1;
        Assert.Contains($"GPU {missing}", GpuPlan.Of(hw, [GpuPlan.Spread("m", "llamacpp", hw, null, GiB) with { PerGpu = new Dictionary<int, long> { [missing] = GiB } }])
            .FirstError!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cpu_only_machine_runs_a_model_from_ram()
    {
        var file = Path.Combine(_dir, "dense", "Small-Q8_0.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        GgufFile.Language("llama", layers: 8, layerBytes: 64 * MiB).Write(file);
        var all = new ModelLibrary(Options.Create(new EngineOptions { LibraryDir = _dir })).List();
        var advice = ModelAdvisor.Advise(new LocalModel { Name = "s", File = all[0].File.Path, Context = 8192 }, all[0], all, FakeHardware.CpuOnly);
        Assert.Equal("cpu", advice.Estimate!.Fit);
        Assert.Equal(0, advice.Estimate.GpuTotal);
        Assert.DoesNotContain(advice.Problems, p => p.Error);
        Assert.Empty(ModelAdvisor.PlanKept([(new LocalModel { Name = "s", File = all[0].File.Path, Context = 8192 }, all[0])], all, FakeHardware.CpuOnly).Gpus);
    }

    [Fact]
    public void An_empty_gpu_setting_from_compose_is_none()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Engine:MediaGpu"] = "", ["Engine:EmbedGpu"] = "2", ["Engine:Backend"] = "rocm" }).Build();
        var o = new EngineOptions();
        config.GetSection("Engine").Bind(o);
        Assert.Equal((null, 2, "rocm"), (o.MediaGpu, o.EmbedGpu, o.Backend));
    }

    [Fact]
    public void Llama_cpp_names_the_gpus_of_its_backend_and_none_on_the_cpu()
    {
        var m = new LocalModel { Name = "g", File = "g/G.gguf", Devices = "0,1" };
        Assert.Contains("device = ROCm0,ROCm1\n", ModelCatalog.Preset(m, "/library", backend: "rocm"), StringComparison.Ordinal);
        Assert.Contains("device = Vulkan0,Vulkan1\n", ModelCatalog.Preset(m, "/library", backend: "vulkan"), StringComparison.Ordinal);
        Assert.Contains("device = SYCL0,SYCL1\n", ModelCatalog.Preset(m, "/library", backend: "sycl"), StringComparison.Ordinal);
        Assert.Contains("device = MTL0,MTL1\n", ModelCatalog.Preset(m, "/library", backend: "metal"), StringComparison.Ordinal);
        Assert.Contains("device = CUDA0,CUDA1\n", ModelCatalog.Preset(m, "/library", backend: "cuda"), StringComparison.Ordinal);
        Assert.DoesNotContain("device", ModelCatalog.Preset(m, "/library", backend: "cpu"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_hardware_file_of_any_vendor_is_read_and_a_bad_one_is_not()
    {
        var o = new EngineOptions();
        var amd = HardwareProbe.FromFile("""
            {"ram_bytes": 1649267441664, "source": "rocm-smi", "gpus": [
              {"index": 1, "name": "AMD Instinct MI300X", "vendor": "amd", "backend": "rocm", "total_bytes": 206158430208, "free_bytes": 200000000000, "reserved_bytes": 0},
              {"index": 0, "name": "AMD Instinct MI300X", "vendor": "amd", "backend": "rocm", "total_bytes": 206158430208, "free_bytes": 206158430208, "reserved_bytes": 0}]}
            """, o)!;
        Assert.Equal(2, amd.Gpus);
        Assert.Equal([0, 1], amd.Devices!.Select(d => d.Index));
        Assert.All(amd.Devices!, d => Assert.Equal(("amd", "rocm"), (d.Vendor, d.Backend)));
        Assert.Equal(200000000000, amd.Devices![1].Free);
        Assert.Equal(1649267441664, amd.RamTotal);
        var none = HardwareProbe.FromFile("""{"ram_bytes": 68719476736, "source": "none", "gpus": []}""", o)!;
        Assert.Equal((0, 0L), (none.Gpus, none.GpuForModels));
        Assert.Null(HardwareProbe.FromFile("not json", o));
        Assert.Null(HardwareProbe.FromFile("[]", o));
    }
}
