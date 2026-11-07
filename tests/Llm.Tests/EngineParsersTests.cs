using Llm.Api.Models;
using Llm.Core.Models;

namespace Llm.Tests;

/// <summary>
/// Each engine's tool-call and reasoning parsers (EngineParsers): the family a model is of, from its name and its config; the
/// flags its server is started with on vLLM, SGLang and LMDeploy (none on Strata, which reads its own); an admin's choice over
/// the automatic one; names refused that the engine has not; and the advisor's word when an engine reads no calls of a family.
/// </summary>
public sealed class EngineParsersTests
{
    private static LocalModel Model(string repo, string kind = EngineKinds.Vllm) =>
        new() { Name = repo.ToLowerInvariant(), File = $"hf/org/{repo}@abc", EngineKind = kind, KvType = "fp8", Context = 32768, Parallel = 16, Sleep = false };

    private static VllmProfile Load(string name) =>
        VllmAdvisor.Read(Path.Combine(AppContext.BaseDirectory, "Resources", "vllm", name)) ?? throw new FileNotFoundException(name);

    private static string? After(IReadOnlyList<string> args, string flag) => args.ToList().IndexOf(flag) is >= 0 and var i ? args[i + 1] : null;

    [Theory]
    [InlineData("Qwen2.5-1.5B-Instruct", null, null, "qwen2.5")]
    [InlineData("Qwen2.5-Coder-32B-Instruct", null, null, "qwen2.5")]
    [InlineData("Qwen3-0.6B", "Qwen3ForCausalLM", "qwen3", "qwen3")]
    [InlineData("Qwen3-30B-A3B-Thinking-2507", null, "qwen3_moe", "qwen3-thinking")]
    [InlineData("Qwen3-235B-A22B-Instruct-2507-FP8", null, "qwen3_moe", "qwen3-instruct")]
    [InlineData("Qwen3-Coder-30B-A3B-Instruct", null, "qwen3_moe", "qwen3-coder")]
    [InlineData("Qwen3.6-35B-A3B", null, null, "qwen3.5")]
    [InlineData("Qwen3.8-27B", null, null, "qwen3.5")]
    [InlineData("flash", "Qwen4ExpForConditionalGeneration", "qwen4_exp_text", "qwen3.5")]
    [InlineData("big", "Qwen3_5MoeForConditionalGeneration", "qwen3_5_moe_text", "qwen3.5")]
    [InlineData("QwQ-32B", "Qwen2ForCausalLM", "qwen2", "qwq")]
    [InlineData("DeepSeek-R1-Distill-Qwen-7B", "Qwen2ForCausalLM", "qwen2", "deepseek-r1-distill")]
    [InlineData("Llama-3.1-8B-Instruct", "LlamaForCausalLM", "llama", "llama3")]
    [InlineData("Meta-Llama-3-70B-Instruct", null, "llama", "llama3")]
    [InlineData("Llama-4-Scout-17B-16E-Instruct", null, "llama4", "llama4")]
    [InlineData("Hermes-3-Llama-3.1-8B", null, "llama", "hermes")]
    [InlineData("gpt-oss-20b", "GptOssForCausalLM", "gpt_oss", "gpt-oss")]
    [InlineData("oss", "GptOssForCausalLM", null, "gpt-oss")]
    [InlineData("DeepSeek-R1-0528", "DeepseekV3ForCausalLM", "deepseek_v3", "deepseek-r1")]
    [InlineData("DeepSeek-V3-0324", "DeepseekV3ForCausalLM", "deepseek_v3", "deepseek-v3")]
    [InlineData("DeepSeek-V3.1-Terminus", "DeepseekV3ForCausalLM", "deepseek_v3", "deepseek-v3.1")]
    [InlineData("DeepSeek-V3.2-Exp", "DeepseekV32ForCausalLM", "deepseek_v32", "deepseek-v3.2")]
    [InlineData("DeepSeek-V4-Flash", "DeepseekV4ForCausalLM", "deepseek_v4", "deepseek-v4")]
    [InlineData("Kimi-K2-Instruct", "DeepseekV3ForCausalLM", "kimi_k2", "kimi-k2")]
    [InlineData("Kimi-K2-Thinking", "DeepseekV3ForCausalLM", "kimi_k2", "kimi-k2-thinking")]
    [InlineData("GLM-4.7", "Glm4MoeForCausalLM", "glm4_moe", "glm-4.7")]
    [InlineData("GLM-4.6", "Glm4MoeForCausalLM", "glm4_moe", "glm-4.5")]
    [InlineData("Mistral-Small-3.2-24B-Instruct-2506", "Mistral3ForConditionalGeneration", "mistral3", "mistral")]
    [InlineData("Magistral-Small-2509", null, "mistral3", "magistral")]
    [InlineData("internlm3-8b-instruct", "InternLM3ForCausalLM", "internlm3", "internlm")]
    [InlineData("MiniMax-M2", "MiniMaxM2ForCausalLM", "minimax_m2", "minimax-m2")]
    [InlineData("SmolLM2-135M", "LlamaForCausalLM", "llama", null)]
    [InlineData("deepseek-coder-v2-lite", "DeepseekV2ForCausalLM", "deepseek_v2", null)]
    public void Its_family_comes_from_its_name_and_its_config(string repo, string? architecture, string? modelType, string? family) =>
        Assert.Equal(family, EngineParsers.FamilyOf("m", $"hf/org/{repo}@abc", architecture, modelType)?.Id);

    [Fact]
    public void A_gguf_file_says_its_family_by_its_name_and_its_header()
    {
        Assert.Equal("qwen3", EngineParsers.FamilyOf("m", "Qwen3-8B-Q4_K_M.gguf", "qwen3", "qwen3")?.Id);
        Assert.Equal("qwen3.5", EngineParsers.FamilyOf("m", "flash/Qwen3.8-Flash-Next-UD-Q4_K_XL-00001-of-00003.gguf", "qwen4exp", "qwen4exp")?.Id);
        Assert.Equal("llama3", EngineParsers.FamilyOf("m", "Llama-3.2-3B-Instruct-Q8_0.gguf", "llama", "llama")?.Id);
    }

    [Fact]
    public void Every_automatic_parser_is_one_its_engine_has()
    {
        foreach (var family in EngineParsers.Families)
        {
            foreach (var (kind, pair) in family.Engines)
            {
                Assert.True(EngineParsers.Known(EngineParsers.ToolNames, kind, pair.Tool), $"{family.Id} on {kind}: no tool parser {pair.Tool}");
                Assert.True(EngineParsers.Known(EngineParsers.ReasoningNames, kind, pair.Reasoning), $"{family.Id} on {kind}: no reasoning parser {pair.Reasoning}");
            }
        }
    }

    [Theory]
    // vLLM: --enable-auto-tool-choice with its parser; Qwen2.5 has no thinking to read.
    [InlineData(EngineKinds.Vllm, "Qwen2.5-1.5B-Instruct", "hermes", null)]
    [InlineData(EngineKinds.Vllm, "Qwen3-0.6B", "hermes", "qwen3")]
    [InlineData(EngineKinds.Vllm, "Qwen3.8-27B", "qwen3_coder", "qwen3")]
    [InlineData(EngineKinds.Vllm, "Qwen3-Coder-30B-A3B-Instruct", "qwen3_coder", null)]
    [InlineData(EngineKinds.Vllm, "Llama-3.1-8B-Instruct", "llama3_json", null)]
    [InlineData(EngineKinds.Vllm, "gpt-oss-120b", "openai", "openai_gptoss")]
    [InlineData(EngineKinds.Vllm, "DeepSeek-R1-0528", "deepseek_v3", "deepseek_r1")]
    [InlineData(EngineKinds.Vllm, "GLM-4.7", "glm47", "glm47")]
    [InlineData(EngineKinds.Vllm, "Kimi-K2-Instruct", "kimi_k2", null)]
    [InlineData(EngineKinds.Vllm, "Mistral-Small-3.2-24B-Instruct-2506", "mistral", null)]
    [InlineData(EngineKinds.Vllm, "internlm3-8b-instruct", "internlm", null)]
    [InlineData(EngineKinds.Vllm, "SmolLM2-135M", null, null)]
    // SGLang: its own names.
    [InlineData(EngineKinds.Sglang, "Qwen2.5-1.5B-Instruct", "qwen", null)]
    [InlineData(EngineKinds.Sglang, "Qwen3-0.6B", "qwen", "qwen3")]
    [InlineData(EngineKinds.Sglang, "Qwen3-30B-A3B-Thinking-2507", "qwen", "qwen3-thinking")]
    [InlineData(EngineKinds.Sglang, "Qwen3.8-27B", "qwen3_coder", "qwen3")]
    [InlineData(EngineKinds.Sglang, "Llama-3.1-8B-Instruct", "llama3", null)]
    [InlineData(EngineKinds.Sglang, "gpt-oss-120b", "gpt-oss", "gpt-oss")]
    [InlineData(EngineKinds.Sglang, "DeepSeek-V3.1-Terminus", "deepseekv31", "deepseek-v3")]
    [InlineData(EngineKinds.Sglang, "GLM-4.6", "glm45", "glm45")]
    [InlineData(EngineKinds.Sglang, "Kimi-K2-Thinking", "kimi_k2", "kimi_k2")]
    // LMDeploy: its own names, its default reasoning parser only for a family that thinks, and gpt-oss read by its architecture.
    [InlineData(EngineKinds.Lmdeploy, "Qwen2.5-1.5B-Instruct", "qwen2d5", null)]
    [InlineData(EngineKinds.Lmdeploy, "Qwen3-0.6B", "qwen3", "default")]
    [InlineData(EngineKinds.Lmdeploy, "Qwen3-235B-A22B-Instruct-2507-FP8", "qwen3", null)]
    [InlineData(EngineKinds.Lmdeploy, "Qwen3.8-27B", "qwen3coder", "default")]
    [InlineData(EngineKinds.Lmdeploy, "Llama-3.1-8B-Instruct", "llama3", null)]
    [InlineData(EngineKinds.Lmdeploy, "internlm3-8b-instruct", "internlm", null)]
    [InlineData(EngineKinds.Lmdeploy, "GLM-4.7", "glm47", "default")]
    [InlineData(EngineKinds.Lmdeploy, "gpt-oss-120b", null, null)]
    [InlineData(EngineKinds.Lmdeploy, "Mistral-Small-3.2-24B-Instruct-2506", null, null)]
    public void Its_server_is_started_with_its_familys_parsers(string kind, string repo, string? tool, string? reasoning)
    {
        var args = ModelCatalog.ServerArgs(Model(repo, kind), "/library", 1);
        Assert.Equal((tool, reasoning), (After(args, "--tool-call-parser"), After(args, "--reasoning-parser")));
        // vLLM answers tool_calls only with --enable-auto-tool-choice beside its parser; SGLang and LMDeploy have no such flag.
        Assert.Equal(kind == EngineKinds.Vllm && tool is not null, args.Contains("--enable-auto-tool-choice"));
    }

    [Fact]
    public void Its_familys_parsers_come_from_its_config_when_its_name_says_none()
    {
        // As the app writes the presets: the profile read from the library (config.json's architectures and model_type).
        var m = Model("flash");
        m.File = "hf/org/flash@abc";
        var family = EngineParsers.FamilyOf(m, Load("Qwen3.8-Flash-Next-FP8"));
        var args = ModelCatalog.VllmArgs(m, "/library", 1, family);
        Assert.Equal(("qwen3_coder", "qwen3"), (After(args, "--tool-call-parser"), After(args, "--reasoning-parser")));
        var oss = EngineParsers.FamilyOf(Model("big"), Load("gpt-oss-120b"));
        Assert.Equal("gpt-oss", oss?.Id);
    }

    [Fact]
    public void An_admins_choice_is_used_over_the_automatic_one_and_none_turns_one_off()
    {
        var m = Model("Qwen3-0.6B");
        m.ToolParser = "qwen3_xml";
        m.ReasoningParser = EngineParsers.None;
        var args = ModelCatalog.VllmArgs(m, "/library", 1);
        Assert.Equal("qwen3_xml", After(args, "--tool-call-parser"));
        Assert.DoesNotContain("--reasoning-parser", args);
        m.ToolParser = EngineParsers.None;
        args = ModelCatalog.VllmArgs(m, "/library", 1);
        Assert.DoesNotContain("--tool-call-parser", args);
        Assert.DoesNotContain("--enable-auto-tool-choice", args);
        // A model of no family the app knows: what the admin chose.
        var smol = Model("SmolLM2-135M", EngineKinds.Sglang);
        smol.ToolParser = "pythonic";
        Assert.Equal("pythonic", After(ModelCatalog.SglangArgs(smol, "/library", 1), "--tool-call-parser"));
    }

    [Fact]
    public void Strata_and_llama_cpp_take_no_parser_flags_they_read_calls_and_thinking_themselves()
    {
        var strata = new LocalModel { Name = "s", File = "Qwen3.8-Flash-Next.gguf", EngineKind = EngineKinds.Strata, KvType = "fp8", Context = 32768, Parallel = 2 };
        var args = ModelCatalog.ServerArgs(strata, "/library", 1);
        Assert.DoesNotContain(args, a => a.Contains("parser", StringComparison.Ordinal));
        Assert.True(EngineParsers.Advise(strata, EngineParsers.FamilyOf(strata, null)).BuiltIn);
        Assert.Empty(EngineParsers.FamilyProblems(strata, null));
        strata.ToolParser = "hermes";
        Assert.Contains(ModelAdvisor.KindProblems(strata), p => p.Field == "toolParser" && p.Error);
        var llama = new LocalModel { Name = "l", File = "l.gguf", ReasoningParser = "qwen3" };
        Assert.Contains(ModelAdvisor.KindProblems(llama), p => p.Field == "toolParser" && p.Error);
    }

    [Fact]
    public void A_name_its_engine_has_not_is_refused()
    {
        var m = Model("Qwen2.5-1.5B-Instruct", EngineKinds.Lmdeploy);
        Assert.Empty(ModelAdvisor.KindProblems(m));
        // vLLM's name for it, not LMDeploy's.
        m.ToolParser = "hermes";
        var problem = Assert.Single(ModelAdvisor.KindProblems(m), p => p.Field == "toolParser");
        Assert.True(problem.Error);
        Assert.Contains("qwen2d5", problem.Message, StringComparison.Ordinal);
        m.ToolParser = "qwen2d5";
        m.ReasoningParser = "qwen3";
        Assert.Contains(ModelAdvisor.KindProblems(m), p => p.Field == "reasoningParser" && p.Error);
        m.ReasoningParser = EngineParsers.None;
        Assert.Empty(ModelAdvisor.KindProblems(m));
    }

    [Fact]
    public void The_advisor_says_when_its_engine_reads_no_calls_of_its_family_and_what_the_chat_then_does()
    {
        var mistral = Model("Mistral-Small-3.2-24B-Instruct-2506", EngineKinds.Lmdeploy);
        var family = EngineParsers.FamilyOf(mistral, null);
        var word = Assert.Single(EngineParsers.FamilyProblems(mistral, family));
        Assert.False(word.Error);
        Assert.Contains("LMDeploy has no tool-call parser for Mistral", word.Message, StringComparison.Ordinal);
        Assert.Contains("refuses a request with tools", word.Message, StringComparison.Ordinal);
        Assert.Contains("no text protocol of its own", word.Message, StringComparison.Ordinal);
        Assert.Contains("vLLM or SGLang", word.Message, StringComparison.Ordinal);
        // With Tools off the chat sends it none: nothing to say.
        mistral.Tools = false;
        Assert.Empty(EngineParsers.FamilyProblems(mistral, family));

        // A family the app does not know: choose one, or Tools off. SGLang answers the call as text.
        var smol = Model("SmolLM2-135M", EngineKinds.Sglang);
        Assert.Contains("answers its calls as text", Assert.Single(EngineParsers.FamilyProblems(smol, null)).Message, StringComparison.Ordinal);
        Assert.Empty(EngineParsers.FamilyProblems(Model("Qwen3-0.6B"), EngineParsers.FamilyOf(Model("Qwen3-0.6B"), null)));
        // gpt-oss on LMDeploy: read by its architecture, nothing to say.
        var oss = Model("gpt-oss-120b", EngineKinds.Lmdeploy);
        Assert.Empty(EngineParsers.FamilyProblems(oss, EngineParsers.FamilyOf(oss, null)));
    }

    [Fact]
    public void The_advice_carries_the_parsers_for_the_form()
    {
        var m = Model("flash");
        var advice = VllmAdvisor.Advise(m, Load("Qwen3-0.6B-FP8"), null);
        Assert.NotNull(advice.Parsers);
        Assert.Equal(("qwen3", "hermes", "qwen3", false), (advice.Parsers.Family, advice.Parsers.Tool.Automatic, advice.Parsers.Reasoning.Automatic, advice.Parsers.BuiltIn));
        Assert.Contains("qwen3_coder", advice.Parsers.Tool.Names);
        Assert.Null(advice.Parsers.Tool.Chosen);
        m.EngineKind = EngineKinds.Lmdeploy;
        Assert.Equal("default", VllmAdvisor.Advise(m, Load("Qwen3-0.6B-FP8"), null).Parsers!.Reasoning.Automatic);
    }
}
