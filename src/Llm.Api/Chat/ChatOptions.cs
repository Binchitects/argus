namespace Llm.Api.Chat;

/// <summary>Configuration section "Chat" (most of it defaults from the Stack and Argus sections).</summary>
public sealed class ChatOptions
{
    /// <summary>LiteLLM, reached inside the network. The master key comes from Gateway:MasterKey.</summary>
    public string GatewayUrl { get; set; } = "http://litellm:4000";

    public int MaxToolRounds { get; set; } = 8;

    /// <summary>Tool definitions past this many characters go on demand (OnDemandTools); 0: always whole.</summary>
    public int ToolTextChars { get; set; } = 6000;

    /// <summary>A tool result past this many characters: the model reads its start, the whole is a file in the chat; 0: no limit.</summary>
    public int ToolResultChars { get; set; } = 24_000;

    /// <summary>
    /// A chat is compacted (its older messages summarized) before an answer when it
    /// fills more than this share of the model's context, in percent. 0: never; the
    /// oldest messages are left out instead.
    /// </summary>
    public int AutoCompactPercent { get; set; } = 80;
    /// <summary>Text kept per attachment (what the Files panel shows and read_file reads).</summary>
    public int MaxAttachmentChars { get; set; } = 1_000_000;
    /// <summary>What of one attachment goes into the question itself; the rest is read in parts.</summary>
    public int InlineAttachmentChars { get; set; } = 30_000;
    public long MaxUploadBytes { get; set; } = 20 * 1024 * 1024;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Sub-agents of one answer that run at once (the Sub-agents tool); the others wait their turn.</summary>
    public int AgentsAtOnce { get; set; } = 3;

    /// <summary>Longest one tool call (Argus, an MCP server) may take; a server can have its own.</summary>
    public TimeSpan ToolCallTimeout { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>Answers one person may have running at once, across their chats; more wait their turn.</summary>
    public int AnswersPerPerson { get; set; } = 1;

    /// <summary>Answers running at once in the whole chat, all models together; 0: no limit but each model's own (its slots, AnswerGate).</summary>
    public int AnswersAtOnce { get; set; }

    /// <summary>The model new chats use ("auto": Auto, while <see cref="SmallModel"/> is set); empty: the first kept loaded (Admin -> Models).</summary>
    public string? DefaultModel { get; set; }

    /// <summary>The model for sub-agents and small steps (titles, compaction, the safeguards' check, Auto); empty: each step uses the answer's own model.</summary>
    public string? SmallModel { get; set; }

    /// <summary>The thinking levels a chat offers: level:Label pairs, comma-separated.</summary>
    public string ThinkingPresets { get; set; } = "xhigh:Deep think,medium:Balanced,low:Quick,off:No thinking";

    /// <summary>How hard the model thinks when a chat does not choose.</summary>
    public string DefaultThinking { get; set; } = "medium";

    /// <summary>Longest an answer waits in line before it gives up.</summary>
    public TimeSpan QueueTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Requests one API key may have at the gateway at once (LiteLLM refuses more with 429); 0: no limit.</summary>
    public int ApiRequestsPerKey { get; set; } = 2;

    /// <summary>GitLab as people's browsers reach it, for links from Argus's answers; empty: ARGUS_GITLAB_URL.</summary>
    public string? GitlabLinkUrl { get; set; }

    /// <summary>ARGUS_CHAT_CLIENT_TOKEN: the credential Argus accepts for the chat, with the person's email beside it.</summary>
    public string? ArgusChatToken { get; set; }
}

public sealed record ThinkingPreset(string Level, string Label);

public static class ThinkingPresets
{
    /// <summary>"xhigh:Deep think,medium:Balanced,low:Quick,off:No thinking" (THINKING_PRESETS).</summary>
    public static IReadOnlyList<ThinkingPreset> Parse(string? raw) =>
        [.. (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(p => p[0].Length > 0)
            .Select(p => new ThinkingPreset(p[0].ToLowerInvariant(), p.Length > 1 && p[1].Length > 0 ? p[1] : p[0]))];

    /// <summary>
    /// What reaches the model's chat template. A top-level reasoning_effort is dropped
    /// by LiteLLM for this backend (measured); chat_template_kwargs arrives. "off" is
    /// the switch that really turns thinking off.
    /// </summary>
    public static System.Text.Json.Nodes.JsonObject? TemplateKwargs(string? level) => level switch
    {
        null or "" => null,
        "off" => new() { ["enable_thinking"] = false },
        _ => new() { ["reasoning_effort"] = level },
    };
}
