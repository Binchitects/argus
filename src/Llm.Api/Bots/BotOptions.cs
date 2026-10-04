namespace Llm.Api.Bots;

/// <summary>Configuration section "Bots": the chat platforms' bots and email in (Settings → Chat bots).</summary>
public sealed class BotOptions
{
    /// <summary>What every bot's chats are told, after the app's own instructions.</summary>
    public string? Instructions { get; set; } = DefaultInstructions;

    public SlackOptions Slack { get; set; } = new();

    public MattermostOptions Mattermost { get; set; } = new();

    public TeamsOptions Teams { get; set; } = new();

    public MailInOptions Email { get; set; } = new();

    public const string DefaultInstructions =
        "You answer in a team chat or by email, where the answer is read as a message: keep it short, put the answer first, and use plain Markdown (no wide tables).";
}

/// <summary>What one platform's bot answers with (its model and tools), and where (its channels).</summary>
public abstract class PlatformOptions
{
    /// <summary>The model its chats use; empty: the one a new chat of the person would use.</summary>
    public string? Model { get; set; }

    /// <summary>Tool ids, comma separated; empty: those on in new chats; "none": no tools.</summary>
    public string? Tools { get; set; }

    /// <summary>Channels it answers in, comma separated (ids or names); empty: every channel it is in. Direct messages always.</summary>
    public string? Channels { get; set; }

    /// <summary>The tools of a new chat: null for those on in new chats.</summary>
    public List<string>? ToolList() => Tools?.Trim() switch
    {
        null or "" => null,
        "none" => [],
        var t => [.. t.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)],
    };

    /// <summary>Whether the bot answers in this channel (any of its names: id, name).</summary>
    public bool Allows(params string?[] names)
    {
        var allowed = (Channels ?? "").Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Length == 0 || allowed.Any(a => names.Any(n => n is { Length: > 0 } && string.Equals(a.TrimStart('#', '~'), n.TrimStart('#', '~'), StringComparison.OrdinalIgnoreCase)));
    }
}

public sealed class SlackOptions : PlatformOptions
{
    /// <summary>The app's signing secret (Basic Information): every event Slack sends is signed with it.</summary>
    public string? SigningSecret { get; set; }

    /// <summary>The bot's token, xoxb-… (scopes app_mentions:read, chat:write, im:history, users:read, users:read.email).</summary>
    public string? BotToken { get; set; }
}

public sealed class MattermostOptions : PlatformOptions
{
    /// <summary>The Mattermost server, https://chat.example.com.</summary>
    public string? Url { get; set; }

    /// <summary>The bot account's access token: reads who asked (their email) and posts the answers.</summary>
    public string? BotToken { get; set; }

    /// <summary>The tokens of the outgoing webhooks and slash commands that call the app, comma separated.</summary>
    public string? Tokens { get; set; }
}

public sealed class TeamsOptions : PlatformOptions
{
    /// <summary>The bot's Microsoft App ID (Azure Bot): the audience of the Bot Connector's tokens.</summary>
    public string? AppId { get; set; }

    /// <summary>The bot's client secret: it signs in to post the answers.</summary>
    public string? AppPassword { get; set; }

    /// <summary>The bot's tenant, for a single-tenant bot; empty: botframework.com (multi-tenant).</summary>
    public string? TenantId { get; set; }
}

public sealed class MailInOptions : PlatformOptions
{
    /// <summary>The secret the mail gateway sends with each email (X-Mail-Secret, a bearer token or a basic-auth password).</summary>
    public string? Secret { get; set; }

    public MailInOptions() => Tools = "none";
}
