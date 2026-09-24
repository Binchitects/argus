namespace Llm.Api.Chat;

/// <summary>Configuration section "Chat" (most of it defaults from the Stack and Argus sections).</summary>
public sealed class ChatOptions
{
    /// <summary>LiteLLM, reached inside the network. The master key comes from Gateway:MasterKey.</summary>
    public string GatewayUrl { get; set; } = "http://litellm:4000";

    public int MaxToolRounds { get; set; } = 8;
    public int MaxAttachmentChars { get; set; } = 200_000;
    public long MaxUploadBytes { get; set; } = 20 * 1024 * 1024;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(15);

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
