using System.Net;
using Llm.Api.Models;
using Llm.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>
/// Each model's engine kind (llama.cpp or vLLM): the settings each kind has and refuses, llama.cpp's presets holding its
/// models alone and unchanged, a vLLM model from a snapshot folder, and a fallback that must be a model the gateway has.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class EngineKindTests(AppFixture app) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("llm-kinds-").FullName;

    private string Config => Path.Combine(_dir, "config");
    private string Library => Path.Combine(_dir, "library");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Each_kind_has_only_its_own_settings()
    {
        Assert.Empty(ModelAdvisor.KindProblems(new LocalModel { Name = "l", File = "l.gguf" }));
        Assert.Contains(ModelAdvisor.KindProblems(new LocalModel { Name = "l", File = "l.gguf", Replicas = 2 }), p => p.Field == "replicas" && p.Error);
        Assert.Contains(ModelAdvisor.KindProblems(new LocalModel { Name = "l", File = "l.gguf", GpuMemoryUtilization = 0.9 }), p => p.Field == "gpuMemoryUtilization");
        Assert.Contains(ModelAdvisor.KindProblems(new LocalModel { Name = "l", File = "l.gguf", SpecDecode = "eagle3" }), p => p.Field == "specDecode");

        LocalModel V() => new() { Name = "v", File = "hf/a/b@c", EngineKind = EngineKinds.Vllm, KvType = "fp8" };
        Assert.Empty(ModelAdvisor.KindProblems(V()));
        var two = V();
        two.Replicas = 2;
        two.GpuMemoryUtilization = 0.85;
        two.SpecDecode = "mtp";
        Assert.Empty(ModelAdvisor.KindProblems(two));
        foreach (var (change, field) in new (Action<LocalModel>, string)[]
        {
            (m => m.Placement = "manual", "placement"), (m => m.CpuMoe = 4, "cpuMoe"), (m => m.Ubatch = 1024, "ubatch"), (m => m.ExtraPreset = "x = 1", "extra"),
            (m => m.KvType = "q8_0", "kvType"), (m => m.GpuMemoryUtilization = 0.99, "gpuMemoryUtilization"), (m => m.SpecDecode = "eagle3", "draftHead"),
            (m => m.SpecDecode = "ngram", "specDecode"), (m => m.Replicas = 9, "replicas"), (m => m.Mtp = true, "mtp"), (m => m.Projector = "p.gguf", "projector"),
        })
        {
            var m = V();
            change(m);
            Assert.Contains(ModelAdvisor.KindProblems(m), p => p.Field == field && p.Error);
        }
        Assert.Contains(ModelAdvisor.KindProblems(new LocalModel { Name = "x", File = "x", EngineKind = "sglang" }), p => p.Field == "engineKind");
        // What each kind can do, for the form.
        Assert.True(EngineKinds.VllmCapabilities is { TensorParallel: true, Sleep: true, PrefixCache: true, SlotPinning: false });
        Assert.True(EngineKinds.LlamaCppCapabilities is { TensorParallel: false, SpillToRam: true, SlotPinning: true });
        Assert.Contains("rocm", EngineKinds.LlamaCppCapabilities.Backends);
        Assert.False(EngineKinds.Managed(EngineKinds.External));
    }

    private WebApplicationFactory<Program> NewApp()
    {
        GgufFile.Language("qwen3", name: "Tiny", context: 40960).Write(Path.Combine(Library, "tiny", "Tiny-4B-Q4_K_M.gguf"));
        Directory.CreateDirectory(Path.Combine(Library, "hf", "acme", "Flash@abc123"));
        File.WriteAllText(Path.Combine(Library, "hf", "acme", "Flash@abc123", "config.json"), """{"architectures":["Qwen3ForCausalLM"]}""");
        Directory.CreateDirectory(Path.Combine(Library, "hf", "acme", "Empty@abc123"));
        Directory.CreateDirectory(Config);
        app.Engine.Reset(Path.Combine(Config, "models.ini"));
        return app.Create(app.ConnectionStringFor("kinds_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = Config, ["Engine:LibraryDir"] = Library,
        });
    }

    [Fact]
    public async Task A_vllm_model_is_kept_out_of_llama_cpps_presets_which_stay_as_they_were()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", new { name = "tiny", file = "tiny/Tiny-4B-Q4_K_M.gguf", context = 8192 }));
        var before = await File.ReadAllTextAsync(Path.Combine(Config, "models.ini"));

        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", new
        {
            name = "flash", engineKind = "vllm", file = "hf/acme/Flash@abc123", context = 65536, parallel = 64, replicas = 2, gpuMemoryUtilization = 0.85, fallback = "tiny",
        }));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(Config, "models.ini")));
        Assert.Contains("[tiny]\nmodel = /library/tiny/Tiny-4B-Q4_K_M.gguf\nctx-size = 8192\nparallel = 1\n", before, StringComparison.Ordinal);
        var row = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "flash");
        Assert.Equal("vllm", row.GetProperty("engineKind").GetString());
        Assert.Equal("fp8", row.GetProperty("kvType").GetString());
        Assert.Equal(2, row.GetProperty("replicas").GetInt32());
        Assert.Equal("tiny", row.GetProperty("fallback").GetString());

        // What the kind lacks, a folder that is no snapshot, and a fallback that is not a model: refused.
        async Task Refused(object body, string field)
        {
            var res = await admin.PostAsync("/api/admin/models", body);
            await StatusAssert.Is(HttpStatusCode.BadRequest, res);
            Assert.Contains(field, await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        await Refused(new { name = "x1", engineKind = "vllm", file = "hf/acme/Empty@abc123" }, "file");
        await Refused(new { name = "x2", engineKind = "vllm", file = "tiny/Tiny-4B-Q4_K_M.gguf" }, "file");
        await Refused(new { name = "x3", engineKind = "vllm", file = "hf/acme/Flash@abc123", placement = "manual" }, "placement");
        await Refused(new { name = "x4", file = "tiny/Tiny-4B-Q4_K_M.gguf", replicas = 2 }, "replicas");
        await Refused(new { name = "x5", file = "tiny/Tiny-4B-Q4_K_M.gguf", fallback = "nothing-of-the-sort" }, "fallback");
        await Refused(new { name = "x6", engineKind = "external", file = "tiny/Tiny-4B-Q4_K_M.gguf" }, "engineKind");
        await Refused(new { name = "x7", engineKind = "vllm", file = "hf/acme/../../tiny" }, "file");
    }
}
