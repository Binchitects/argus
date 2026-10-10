using System.Globalization;
using Llm.Core.Access;
using Llm.Core.Identity;

namespace Llm.Api.Gateway;

/// <summary>A credit per kind, in dollars a calendar month; null: no limit of that kind.</summary>
public sealed record CreditsRequest(decimal? Chat = null, decimal? Api = null, decimal? Pictures = null, decimal? Video = null, decimal? Speech = null)
{
    public const decimal Most = 1_000_000_000;

    public decimal? Of(CreditKind kind) => kind switch
    {
        CreditKind.Chat => Chat,
        CreditKind.Api => Api,
        CreditKind.Pictures => Pictures,
        CreditKind.Video => Video,
        _ => Speech,
    };

    /// <summary>Why these cannot be credits, or null.</summary>
    public string? Problem() => Credits.Kinds.Any(k => Of(k) is < 0 or > Most)
        ? "A credit is a number of dollars from 0, or empty for no limit of that kind."
        : null;

    public void Apply(AppUser u)
    {
        foreach (var k in Credits.Kinds)
        {
            Credits.Set(u, k, Of(k));
        }
    }

    public void Apply(Group g)
    {
        foreach (var k in Credits.Kinds)
        {
            Credits.Set(g, k, Of(k));
        }
    }

    public static CreditsRequest Of(AppUser u) => new(u.ChatCredit, u.ApiCredit, u.PictureCredit, u.VideoCredit, u.SpeechCredit);

    public static CreditsRequest Of(Group g) => new(g.ChatCredit, g.ApiCredit, g.PictureCredit, g.VideoCredit, g.SpeechCredit);

    /// <summary>Each kind in a line, for the audit log: "chat $20.00; API no limit; …".</summary>
    public string Describe() => string.Join("; ", Credits.Kinds.Select(k =>
        $"{Credits.Label(k)} {(Of(k) is { } c ? c.ToString("$0.00", CultureInfo.InvariantCulture) : "no limit")}"));
}
