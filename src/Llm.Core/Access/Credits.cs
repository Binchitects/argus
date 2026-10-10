using Llm.Core.Identity;

namespace Llm.Core.Access;

/// <summary>
/// What a credit is for. Each has its own credit per person and per group, a calendar month's spend held to it:
/// the chat's answers, the API keys' text requests, and the pictures, videos and speech made, whichever way.
/// </summary>
public enum CreditKind
{
    Chat,
    Api,
    Pictures,
    Video,
    Speech,
}

/// <summary>The credits of a person or a group, kind by kind (null: no limit of that kind).</summary>
public static class Credits
{
    public static readonly IReadOnlyList<CreditKind> Kinds = Enum.GetValues<CreditKind>();

    /// <summary>The kind's name in the API and the pages: chat, api, pictures, video, speech.</summary>
    public static string Name(CreditKind kind) => kind.ToString().ToLowerInvariant();

    public static CreditKind? Parse(string? name)
    {
        var wanted = name?.Trim().ToLowerInvariant();
        foreach (var k in Kinds)
        {
            if (Name(k) == wanted)
            {
                return k;
            }
        }
        return null;
    }

    /// <summary>The kind in a sentence: "chat", "API", "picture", "video", "speech".</summary>
    public static string Label(CreditKind kind) => kind switch
    {
        CreditKind.Chat => "chat",
        CreditKind.Api => "API",
        CreditKind.Pictures => "picture",
        CreditKind.Video => "video",
        _ => "speech",
    };

    public static decimal? Of(AppUser u, CreditKind kind) => kind switch
    {
        CreditKind.Chat => u.ChatCredit,
        CreditKind.Api => u.ApiCredit,
        CreditKind.Pictures => u.PictureCredit,
        CreditKind.Video => u.VideoCredit,
        _ => u.SpeechCredit,
    };

    public static void Set(AppUser u, CreditKind kind, decimal? value)
    {
        switch (kind)
        {
            case CreditKind.Chat: u.ChatCredit = value; break;
            case CreditKind.Api: u.ApiCredit = value; break;
            case CreditKind.Pictures: u.PictureCredit = value; break;
            case CreditKind.Video: u.VideoCredit = value; break;
            default: u.SpeechCredit = value; break;
        }
    }

    public static decimal? Of(Group g, CreditKind kind) => kind switch
    {
        CreditKind.Chat => g.ChatCredit,
        CreditKind.Api => g.ApiCredit,
        CreditKind.Pictures => g.PictureCredit,
        CreditKind.Video => g.VideoCredit,
        _ => g.SpeechCredit,
    };

    public static void Set(Group g, CreditKind kind, decimal? value)
    {
        switch (kind)
        {
            case CreditKind.Chat: g.ChatCredit = value; break;
            case CreditKind.Api: g.ApiCredit = value; break;
            case CreditKind.Pictures: g.PictureCredit = value; break;
            case CreditKind.Video: g.VideoCredit = value; break;
            default: g.SpeechCredit = value; break;
        }
    }

    /// <summary>Whether the group has a credit of any kind.</summary>
    public static bool Any(Group g) => Kinds.Any(k => Of(g, k) is not null);
}
