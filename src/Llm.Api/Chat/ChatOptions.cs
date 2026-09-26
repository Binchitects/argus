namespace Llm.Api.Chat;

/// <summary>Configuration section "Chat" (most of it defaults from the Stack and Argus sections).</summary>
public sealed class ChatOptions
{
    /// <summary>LiteLLM, reached inside the network. The master key comes from Gateway:MasterKey.</summary>
    public string GatewayUrl { get; set; } = "http://litellm:4000";

    public int MaxToolRounds { get; set; } = 8;
    /// <summary>Text kept per attachment (what the Files panel shows and read_file reads).</summary>
    public int MaxAttachmentChars { get; set; } = 1_000_000;
    /// <summary>What of one attachment goes into the question itself; the rest is read in parts.</summary>
    public int InlineAttachmentChars { get; set; } = 30_000;
    public long MaxUploadBytes { get; set; } = 20 * 1024 * 1024;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Answers one person may have running at once, across their chats; more wait their turn.</summary>
    public int AnswersPerPerson { get; set; } = 1;

    /// <summary>Answers running at once in the whole chat; 0: as many as the engine serves at once (<see cref="EngineSlots"/>).</summary>
    public int AnswersAtOnce { get; set; }

    /// <summary>LLAMACPP_PARALLEL: what the engine serves at once (set from the stack's .env).</summary>
    public int EngineSlots { get; set; }

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
