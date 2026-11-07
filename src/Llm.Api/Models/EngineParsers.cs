using System.Text.RegularExpressions;
using Llm.Core.Models;

namespace Llm.Api.Models;

/// <summary>
/// One engine's parsers for one family of models: the one that reads its tool calls into tool_calls, the one that reads its
/// thinking into the answer's reasoning; null: that engine has none for it. <paramref name="BuiltIn"/>: the engine reads both
/// by itself, with no flag (LMDeploy for gpt-oss, by its architecture; Strata always).
/// </summary>
public sealed record EngineParserPair(string? Tool, string? Reasoning, bool BuiltIn = false);

/// <summary>A family of models as the engines' parsers know them: by its name, its config's architecture and model type.</summary>
public sealed record ParserFamily(string Id, string Label, IReadOnlyDictionary<string, EngineParserPair> Engines);

/// <summary>A parser as the form shows it: the one chosen (null: automatic; "none": off), the automatic one, and the engine's names.</summary>
public sealed record ParserChoice(string? Chosen, string? Automatic, IReadOnlyList<string> Names);

/// <summary>What Admin → Models → Edit shows under Advanced: the model's family, its two parsers, and whether its engine reads them itself.</summary>
public sealed record ParserAdvice(string? Family, string? FamilyLabel, bool BuiltIn, ParserChoice Tool, ParserChoice Reasoning);

/// <summary>
/// The tool-call and reasoning parsers each engine is started with for a model (vLLM's --enable-auto-tool-choice
/// --tool-call-parser and --reasoning-parser, SGLang's and LMDeploy's --tool-call-parser and --reasoning-parser). Without one,
/// an engine answers a request with tools with an error (vLLM, LMDeploy) or with the call as text (SGLang), and puts the
/// thinking in the answer's text. The names are each engine's own, as their images have them (vLLM 0.31.0's
/// ToolParserManager and ReasoningParserManager, SGLang 0.5.21's --help, LMDeploy 0.18.0's --help); the automatic choice is by
/// the model's family, from its name and its config.json (architectures, model_type). Strata reads both by itself.
/// </summary>
public static partial class EngineParsers
{
    /// <summary>A parser turned off: no flag, whatever its family.</summary>
    public const string None = "none";

    /// <summary>Each engine's tool-call parsers, as its image lists them.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ToolNames = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [EngineKinds.Vllm] =
        [
            "apertus", "cohere_command3", "cohere_command4", "deepseek_v3", "deepseek_v31", "deepseek_v32", "deepseek_v4", "deepseek_v41", "dots", "ernie45",
            "functiongemma", "gemma4", "gigachat3", "glm45", "glm47", "granite", "granite-20b-fc", "granite4", "hermes", "hunyuan_a13b", "hy_v3", "hy_v4", "inkling",
            "internlm", "jamba", "k2_horizon", "kimi_k2", "kimi_k3", "lfm2", "ling3", "llama3_json", "llama4_json", "llama4_pythonic", "longcat", "mimo", "minicpm5",
            "minimax_m2", "minimax_m3", "mistral", "muse_glimmer", "olmo3", "openai", "phi4_mini_json", "poolside_v1", "pythonic", "qwen3_coder", "qwen3_xml",
            "seed_oss", "step3", "step3p5", "xlam",
        ],
        [EngineKinds.Sglang] =
        [
            "apertus2509", "cohere_command4", "deepseekv3", "deepseekv31", "deepseekv32", "deepseekv4", "deepseekv41", "dots", "gemma4", "gigachat3", "gigachat35",
            "glm", "glm45", "glm47", "gpt-oss", "hermes", "hunyuan", "inkling", "interns1", "iquest_q1", "k2_horizon", "kimi_k2", "kimi_k3", "lfm2", "ling3", "llama3",
            "mimo", "minicpm5", "minimax-m2", "minimax-m3", "mistral", "muse", "nanbeige", "poolside_v1", "pythonic", "qwen", "qwen25", "qwen3_coder", "spark25",
            "step3", "step3p5", "trinity",
        ],
        [EngineKinds.Lmdeploy] =
        [
            "deepseek-v32", "deepseek-v4", "glm47", "internlm", "intern-s1", "interns2-preview", "kimi_k2", "kimi-k2", "llama3", "qwen", "qwen2d5", "qwen3",
            "qwen3coder",
        ],
        [EngineKinds.Strata] = [],
    };

    /// <summary>Each engine's reasoning parsers, as its image lists them (LMDeploy's legacy qwen-qwq, intern-s1 and deepseek-r1 are its default).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ReasoningNames = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [EngineKinds.Vllm] =
        [
            "cohere_command3", "cohere_command4", "deepseek_r1", "deepseek_v3", "deepseek_v4", "deepseek_v41", "ernie45", "gemma4", "glm45", "glm47", "granite",
            "granite_thinking_parser", "holo2", "hunyuan_a13b", "hy_v3", "hy_v4", "inkling", "k2_horizon", "kimi_k2", "kimi_k3", "ling3", "mimo", "minimax_m2",
            "minimax_m2_append_think", "minimax_m3", "mistral", "muse_glimmer", "nemotron_v3", "olmo3", "openai_gptoss", "poolside_v1", "qwen3", "seed_oss", "step3",
            "step3p5",
        ],
        [EngineKinds.Sglang] =
        [
            "apertus2509", "cohere_command4", "deepseek-r1", "deepseek-v3", "deepseek-v4", "deepseek-v41", "dots", "gemma4", "gigachat35", "glm45", "gpt-oss",
            "granite_thinking_parser", "hunyuan", "inkling", "interns1", "iquest_q1", "k2_horizon", "kimi", "kimi_k2", "kimi_k3", "ling3", "mimo", "minimax",
            "minimax-append-think", "minimax-m3", "mistral", "muse", "nanbeige", "nemotron_3", "poolside_v1", "qwen3", "qwen3-thinking", "step3", "step3p5",
        ],
        [EngineKinds.Lmdeploy] = ["default", "deepseek-v3", "deepseek-v32", "deepseek-v3.2", "deepseek-v4"],
        [EngineKinds.Strata] = [],
    };

    private static ParserFamily F(string id, string label, EngineParserPair vllm, EngineParserPair sglang, EngineParserPair lmdeploy) =>
        new(id, label, new Dictionary<string, EngineParserPair>(StringComparer.Ordinal)
        {
            [EngineKinds.Vllm] = vllm, [EngineKinds.Sglang] = sglang, [EngineKinds.Lmdeploy] = lmdeploy,
            [EngineKinds.Strata] = new(null, null, BuiltIn: true),
        });

    private static EngineParserPair P(string? tool, string? reasoning) => new(tool, reasoning);

    /// <summary>
    /// The families the engines have parsers for, and each engine's pair. LMDeploy's default reasoning parser starts every answer in
    /// its thinking, and refuses a tokenizer without &lt;think&gt;: only a family that thinks gets it.
    /// </summary>
    public static readonly IReadOnlyList<ParserFamily> Families =
    [
        F("gpt-oss", "gpt-oss (harmony)", P("openai", "openai_gptoss"), P("gpt-oss", "gpt-oss"), new(null, null, BuiltIn: true)),
        F("qwen3-coder", "Qwen3-Coder", P("qwen3_coder", null), P("qwen3_coder", null), P("qwen3coder", null)),
        // Qwen3.5 and later (Qwen3.6, Qwen3.8): calls as XML (<function=NAME><parameter=...>), as Qwen3-Coder's; thinking opened by the prompt.
        F("qwen3.5", "Qwen3.5 and later", P("qwen3_coder", "qwen3"), P("qwen3_coder", "qwen3"), P("qwen3coder", "default")),
        F("qwen3-thinking", "Qwen3 Thinking", P("hermes", "qwen3"), P("qwen", "qwen3-thinking"), P("qwen3", "default")),
        F("qwen3-instruct", "Qwen3 Instruct (no thinking)", P("hermes", null), P("qwen", null), P("qwen3", null)),
        F("qwen3", "Qwen3", P("hermes", "qwen3"), P("qwen", "qwen3"), P("qwen3", "default")),
        F("qwq", "QwQ", P("hermes", "deepseek_r1"), P("qwen", "deepseek-r1"), P("qwen2d5", "default")),
        F("deepseek-r1-distill", "DeepSeek-R1 distilled", P(null, "deepseek_r1"), P(null, "deepseek-r1"), P(null, "default")),
        F("qwen2.5", "Qwen2.5", P("hermes", null), P("qwen", null), P("qwen2d5", null)),
        F("kimi-k3", "Kimi K3", P("kimi_k3", "kimi_k3"), P("kimi_k3", "kimi_k3"), P(null, null)),
        F("kimi-k2-thinking", "Kimi K2 Thinking", P("kimi_k2", "kimi_k2"), P("kimi_k2", "kimi_k2"), P("kimi_k2", "default")),
        F("kimi-k2", "Kimi K2", P("kimi_k2", null), P("kimi_k2", null), P("kimi_k2", null)),
        F("deepseek-v4.1", "DeepSeek-V4.1", P("deepseek_v41", "deepseek_v41"), P("deepseekv41", "deepseek-v41"), P(null, null)),
        F("deepseek-v4", "DeepSeek-V4", P("deepseek_v4", "deepseek_v4"), P("deepseekv4", "deepseek-v4"), P("deepseek-v4", "deepseek-v4")),
        F("deepseek-v3.2", "DeepSeek-V3.2", P("deepseek_v32", "deepseek_v3"), P("deepseekv32", "deepseek-v3"), P("deepseek-v32", "deepseek-v32")),
        F("deepseek-v3.1", "DeepSeek-V3.1", P("deepseek_v31", "deepseek_v3"), P("deepseekv31", "deepseek-v3"), P(null, "deepseek-v3")),
        F("deepseek-r1", "DeepSeek-R1", P("deepseek_v3", "deepseek_r1"), P("deepseekv3", "deepseek-r1"), P(null, "default")),
        F("deepseek-v3", "DeepSeek-V3", P("deepseek_v3", null), P("deepseekv3", null), P(null, null)),
        F("glm-4.7", "GLM-4.7", P("glm47", "glm47"), P("glm47", "glm45"), P("glm47", "default")),
        F("glm-4.5", "GLM-4.5 and 4.6", P("glm45", "glm45"), P("glm45", "glm45"), P(null, "default")),
        F("glm-4", "GLM-4", P(null, null), P("glm", null), P(null, null)),
        F("minimax-m2", "MiniMax-M2", P("minimax_m2", "minimax_m2"), P("minimax-m2", "minimax"), P(null, null)),
        F("magistral", "Magistral", P("mistral", "mistral"), P("mistral", "mistral"), P(null, null)),
        F("mistral", "Mistral", P("mistral", null), P("mistral", null), P(null, null)),
        F("hermes", "Hermes", P("hermes", null), P("hermes", null), P(null, null)),
        F("llama4", "Llama 4", P("llama4_pythonic", null), P("pythonic", null), P(null, null)),
        F("llama3", "Llama 3.x", P("llama3_json", null), P("llama3", null), P("llama3", null)),
        F("intern-s1", "Intern-S1", P(null, null), P("interns1", "interns1"), P("intern-s1", "default")),
        F("internlm", "InternLM", P("internlm", null), P(null, null), P("internlm", null)),
        F("gemma4", "Gemma 4", P("gemma4", "gemma4"), P("gemma4", "gemma4"), P(null, null)),
    ];

    /// <summary>The kinds whose parsers the app sets (Strata reads its own; llama.cpp reads calls by the model's template).</summary>
    public static bool Sets(string? kind) => kind is EngineKinds.Vllm or EngineKinds.Sglang or EngineKinds.Lmdeploy;

    /// <summary>
    /// A model's family, from its name and file (hf/ORG/REPO@REVISION, a GGUF file's name) and its config's architecture and model
    /// type (a GGUF file's general.architecture); null when no family's parsers are known for it.
    /// </summary>
    public static ParserFamily? FamilyOf(string? name, string? file, string? architecture, string? modelType)
    {
        var text = $"{name} {Repo(file)}".ToLowerInvariant();
        var arch = (architecture ?? "").ToLowerInvariant();
        var type = (modelType ?? "").ToLowerInvariant();
        bool Has(string s) => text.Contains(s, StringComparison.Ordinal);
        string? id = null;
        if (arch.StartsWith("gptoss", StringComparison.Ordinal) || type is "gpt_oss" or "gpt-oss" || Has("gpt-oss"))
        {
            id = "gpt-oss";
        }
        else if (Has("qwen3-coder") || Has("qwen3coder"))
        {
            id = "qwen3-coder";
        }
        else if (Qwen35().IsMatch(text) || arch.StartsWith("qwen3_5", StringComparison.Ordinal) || arch.StartsWith("qwen4exp", StringComparison.Ordinal)
            || type.StartsWith("qwen3_5", StringComparison.Ordinal) || type.StartsWith("qwen4_exp", StringComparison.Ordinal) || type is "qwen35" or "qwen35moe" or "qwen4exp")
        {
            id = "qwen3.5";
        }
        else if (Has("r1-distill"))
        {
            id = "deepseek-r1-distill";
        }
        else if (Has("qwq"))
        {
            id = "qwq";
        }
        else if (Has("qwen3") || type.StartsWith("qwen3", StringComparison.Ordinal) || arch.StartsWith("qwen3", StringComparison.Ordinal))
        {
            // Qwen3's first models think or not as asked; the 2507 ones and Qwen3-Next, Qwen3-VL are -Instruct (never) or -Thinking (always).
            id = Has("thinking") ? "qwen3-thinking" : Has("instruct") ? "qwen3-instruct" : "qwen3";
        }
        else if (Has("qwen2") || type.StartsWith("qwen2", StringComparison.Ordinal) || arch.StartsWith("qwen2", StringComparison.Ordinal))
        {
            id = "qwen2.5";
        }
        else if (Has("kimi-k3") || type == "kimi_k3")
        {
            id = "kimi-k3";
        }
        else if (Has("kimi-k2") || type == "kimi_k2")
        {
            id = Has("thinking") ? "kimi-k2-thinking" : "kimi-k2";
        }
        else if (Has("deepseek") || arch.StartsWith("deepseek", StringComparison.Ordinal) || type.StartsWith("deepseek", StringComparison.Ordinal))
        {
            id = Has("v4.1") || Has("v41") ? "deepseek-v4.1"
                : Has("v4") || type.StartsWith("deepseek_v4", StringComparison.Ordinal) ? "deepseek-v4"
                : Has("v3.2") || type == "deepseek_v32" || arch.StartsWith("deepseekv32", StringComparison.Ordinal) ? "deepseek-v3.2"
                : Has("v3.1") ? "deepseek-v3.1"
                : Has("r1") ? "deepseek-r1"
                // DeepSeek's older ones (V2, Coder-V2) write their calls otherwise.
                : Has("v3") || type == "deepseek_v3" || arch.StartsWith("deepseekv3", StringComparison.Ordinal) ? "deepseek-v3"
                : null;
        }
        else if (Has("glm-4.7") || Has("glm4.7"))
        {
            id = "glm-4.7";
        }
        else if (Has("glm-4.5") || Has("glm-4.6") || type == "glm4_moe")
        {
            id = "glm-4.5";
        }
        else if (Has("glm-4") || type == "glm4")
        {
            id = "glm-4";
        }
        else if (Has("minimax-m2") || type == "minimax_m2")
        {
            id = "minimax-m2";
        }
        else if (Has("magistral"))
        {
            id = "magistral";
        }
        else if (Has("mistral") || Has("devstral") || Has("ministral") || type.StartsWith("mistral", StringComparison.Ordinal) || arch.StartsWith("mistral", StringComparison.Ordinal))
        {
            id = "mistral";
        }
        else if (Has("hermes"))
        {
            id = "hermes";
        }
        else if (Has("llama-4") || Has("llama4") || type.StartsWith("llama4", StringComparison.Ordinal))
        {
            id = "llama4";
        }
        else if (Llama3().IsMatch(text))
        {
            id = "llama3";
        }
        else if (Has("intern-s1") || Has("interns1") || type == "interns1")
        {
            id = "intern-s1";
        }
        else if (Has("internlm") || type.StartsWith("internlm", StringComparison.Ordinal))
        {
            id = "internlm";
        }
        else if (Has("gemma-4") || Has("gemma4") || type.StartsWith("gemma4", StringComparison.Ordinal))
        {
            id = "gemma4";
        }
        return Families.FirstOrDefault(f => f.Id == id);
    }

    /// <summary>A model's family, from its library profile (its config's architecture and model type) and its name and file.</summary>
    public static ParserFamily? FamilyOf(LocalModel m, VllmProfile? p) => FamilyOf(m.Name, m.File, p?.Architecture, p?.ModelType);

    /// <summary>The automatic pair of a family on a kind of engine; empty for a family not known or a kind that sets none.</summary>
    public static EngineParserPair Automatic(string kind, ParserFamily? family) =>
        kind == EngineKinds.Strata ? new(null, null, BuiltIn: true) : family?.Engines.GetValueOrDefault(kind) ?? new(null, null);

    /// <summary>The parsers a model is started with: its own choice ("none": none), else its family's on its engine.</summary>
    public static (string? Tool, string? Reasoning) For(LocalModel m, ParserFamily? family)
    {
        if (!Sets(m.EngineKind))
        {
            return (null, null);
        }
        var auto = Automatic(m.EngineKind, family);
        static string? Pick(string? chosen, string? automatic) => chosen switch { null => automatic, None => null, _ => chosen };
        return (Pick(m.ToolParser, auto.Tool), Pick(m.ReasoningParser, auto.Reasoning));
    }

    /// <summary>Whether a parser's name is one its engine has (or none, or automatic).</summary>
    public static bool Known(IReadOnlyDictionary<string, string[]> names, string kind, string? chosen) =>
        chosen is null or None || (names.TryGetValue(kind, out var list) && list.Contains(chosen, StringComparer.Ordinal));

    /// <summary>What the form shows under Advanced for a model on its engine.</summary>
    public static ParserAdvice Advise(LocalModel m, ParserFamily? family)
    {
        var auto = Automatic(m.EngineKind, family);
        return new ParserAdvice(family?.Id, family?.Label, auto.BuiltIn,
            new ParserChoice(m.ToolParser, auto.Tool, ToolNames.GetValueOrDefault(m.EngineKind) ?? []),
            new ParserChoice(m.ReasoningParser, auto.Reasoning, ReasoningNames.GetValueOrDefault(m.EngineKind) ?? []));
    }

    /// <summary>A parser's name its engine does not have, refused on save; or one set for an engine whose parsers the app does not set.</summary>
    public static IReadOnlyList<ModelProblem> NameProblems(LocalModel m)
    {
        var problems = new List<ModelProblem>();
        var label = EngineKinds.Of(m.EngineKind)?.Label ?? m.EngineKind;
        if (!Sets(m.EngineKind))
        {
            if (m.ToolParser is not null || m.ReasoningParser is not null)
            {
                problems.Add(new("toolParser", m.EngineKind == EngineKinds.Strata
                    ? "Strata has no parsers to choose: it reads tool calls and thinking itself."
                    : $"{label} has no parsers to choose: it reads tool calls and thinking by the model's chat template.", true));
            }
            return problems;
        }
        if (!Known(ToolNames, m.EngineKind, m.ToolParser))
        {
            problems.Add(new("toolParser", $"{label} has no tool-call parser {m.ToolParser}: choose one of {string.Join(", ", ToolNames[m.EngineKind])}, or Automatic.", true));
        }
        if (!Known(ReasoningNames, m.EngineKind, m.ReasoningParser))
        {
            problems.Add(new("reasoningParser", $"{label} has no reasoning parser {m.ReasoningParser}: choose one of {string.Join(", ", ReasoningNames[m.EngineKind])}, or Automatic.", true));
        }
        return problems;
    }

    /// <summary>
    /// What the advisor says of a model with Tools on whose engine reads no calls of its family: a warning, since the engine then
    /// refuses a request with tools (vLLM, LMDeploy) or answers the call as text (SGLang), and the app has no text protocol of its
    /// own to read calls from an answer.
    /// </summary>
    public static IReadOnlyList<ModelProblem> FamilyProblems(LocalModel m, ParserFamily? family)
    {
        var problems = new List<ModelProblem>();
        var label = EngineKinds.Of(m.EngineKind)?.Label ?? m.EngineKind;
        if (!Sets(m.EngineKind))
        {
            return problems;
        }
        var (tool, _) = For(m, family);
        var auto = Automatic(m.EngineKind, family);
        if (m.Tools && tool is null && !auto.BuiltIn)
        {
            var why = m.ToolParser == None ? "Its tool-call parser is off"
                : family is null ? "The app does not know this model's family, so it chooses no tool-call parser"
                : $"{label} has no tool-call parser for {family.Label}";
            var engine = m.EngineKind == EngineKinds.Sglang ? "answers its calls as text" : "refuses a request with tools";
            // Where its family's calls are read: the other engines with a parser for it, and llama.cpp (by the model's chat template).
            var elsewhere = family?.Engines.Where(e => e.Key != m.EngineKind && (e.Value.Tool is not null || e.Value.BuiltIn) && e.Key != EngineKinds.Strata)
                .Select(e => EngineKinds.Of(e.Key)!.Label).Append("llama.cpp (its GGUF file)").ToList() ?? [];
            problems.Add(new("toolParser", $"{why}: {label} {engine}, and the app has no text protocol of its own to read calls from an answer. "
                + (family is null || m.ToolParser == None
                    ? "Choose its tool-call parser under Advanced, or turn Tools off (the chat then sends it none)."
                    : $"Turn Tools off for it (the chat then sends it none), or run it on {string.Join(" or ", elsewhere)}."), false));
        }
        return problems;
    }

    /// <summary>A file's repository name: hf/ORG/REPO@REVISION's REPO, a GGUF file's name.</summary>
    private static string Repo(string? file)
    {
        if (string.IsNullOrEmpty(file))
        {
            return "";
        }
        var last = file.TrimEnd('/').Split('/')[^1];
        var at = last.IndexOf('@', StringComparison.Ordinal);
        return at >= 0 ? last[..at] : last;
    }

    [GeneratedRegex(@"qwen3\.[5-9]|qwen3\.\d{2}")]
    private static partial Regex Qwen35();

    [GeneratedRegex(@"llama-?3|meta-llama-3")]
    private static partial Regex Llama3();
}
