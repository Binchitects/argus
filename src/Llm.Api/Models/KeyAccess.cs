using Llm.Api.Chat;
using Llm.Api.Gateway;
using Llm.Api.Operations;
using Llm.Core.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>
/// Who may use which model holds for API keys too: a request of a key is let in only for a model its person may use
/// (<see cref="Gateway.Admission"/>, 403 for the rest), pictures, video and speech only with their features. Fair use
/// holds for keys too: <see cref="AtOnce"/> of a key's requests are answered at once, the rest wait their turn.
/// </summary>
public sealed class KeyAccess(ModelPolicy policy, ChatModels models, IOptionsMonitor<ChatOptions> chat, Access.AccessService access)
{
    /// <summary>A key's requests answered at once (Chat:ApiRequestsPerKey; the rest wait their turn); null: no limit.</summary>
    public int? AtOnce => chat.CurrentValue.ApiRequestsPerKey > 0 ? chat.CurrentValue.ApiRequestsPerKey : null;

    /// <summary>The word for "no model" in a key's list: an empty list means every model.</summary>
    public const string NoModels = "no-default-models";

    /// <summary>The models a person's keys may call: empty (every model) when nothing is kept from them.</summary>
    public async Task<IReadOnlyList<string>> ListForAsync(AppUser user, CancellationToken ct = default)
    {
        var every = await models.AllAsync(ct);
        var all = every.Select(m => m.Name).ToList();
        var allowed = await policy.AllowedAsync(user, all, ct);
        // Pictures, video and speech are features of their own (Admin → Access): their models go with them.
        var member = await access.MembershipAsync(user, ct);
        allowed.ExceptWith(every.Where(m => FeatureOf(m.Mode) is { } feature && !member.Has(feature)).Select(m => m.Name));
        return allowed.Count == all.Count ? [] : allowed.Count == 0 ? [NoModels] : [.. all.Where(allowed.Contains)];
    }

    /// <summary>The feature permission a model of this mode needs; null: chat's own.</summary>
    public static string? FeatureOf(string mode) => mode switch
    {
        "image_generation" => Core.Access.Permissions.Pictures,
        "video_generation" => Core.Access.Permissions.Video,
        "audio_speech" or "audio_transcription" => Core.Access.Permissions.Speech,
        _ => null,
    };
}

/// <summary>
/// What a key may do changed (a model's access, a group, a person's rules or limits, Chat:ApiRequestsPerKey, the company's
/// rate limits): whose a key is and what it may call is read again at once, on every replica (<see cref="KeyBook"/>).
/// </summary>
public sealed class KeyAccessWatcher : IDisposable
{
    private const string WakeTopic = "keys:wake";
    private readonly Replicas replicas;
    private readonly KeyBook book;
    private readonly IDisposable? _onChange;
    private readonly IDisposable? _onRates;

    public KeyAccessWatcher(Replicas replicas, KeyBook book, IOptionsMonitor<ChatOptions> chat, IOptionsMonitor<Gateway.RateLimitOptions> rates)
    {
        this.replicas = replicas;
        this.book = book;
        replicas.On(WakeTopic, _ =>
        {
            book.Changed();
            return true;
        });
        var perKey = chat.CurrentValue.ApiRequestsPerKey;
        _onChange = chat.OnChange(o =>
        {
            if (o.ApiRequestsPerKey != perKey)
            {
                perKey = o.ApiRequestsPerKey;
                Wake();
            }
        });
        var limits = (rates.CurrentValue.RequestsPerMinute, rates.CurrentValue.TokensPerMinute);
        _onRates = rates.OnChange(o =>
        {
            if ((o.RequestsPerMinute, o.TokensPerMinute) != limits)
            {
                limits = (o.RequestsPerMinute, o.TokensPerMinute);
                Wake();
            }
        });
    }

    /// <summary>Every replica reads its keys again.</summary>
    public void Wake()
    {
        book.Changed();
        replicas.Tell(WakeTopic);
    }

    public void Dispose()
    {
        _onChange?.Dispose();
        _onRates?.Dispose();
    }
}
