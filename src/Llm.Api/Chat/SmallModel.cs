using Llm.Api.Gateway;
using Llm.Api.Models;
using Llm.Api.Settings;
using Llm.Core.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>
/// The model for sub-agents and small steps (Chat:SmallModel): a small, fast model beside the
/// big one, for the many short calls around an answer (sub-agents, the chat's title,
/// compaction summaries, the safeguards' check, Auto's choice), so they do not wait on the
/// big model's slow writing. It never thinks for them. Not set, not at the gateway, or not
/// one the person may use now: each step uses the answer's own model.
/// </summary>
public sealed class SmallModel(IOptionsMonitor<ChatOptions> chat, ChatModels models, ModelPolicy policy, ModelCatalog catalog, IOptions<EngineOptions> engine)
    : ISettingWarning
{
    /// <summary>What a chat that chose Auto has as its model: the small model answers the easy questions and hands the rest on.</summary>
    public const string Auto = "auto";

    /// <summary>The setting's model name, or null when it is empty.</summary>
    public string? Name => chat.CurrentValue.SmallModel?.Trim() is { Length: > 0 } name ? name : null;

    /// <summary>The small model, when it is set, at the gateway, and this person may use it (Auto is offered to them); else null.</summary>
    public async Task<GatewayModel?> OfferedAsync(AppUser user, CancellationToken ct = default)
    {
        if (Name is not { } name || (await models.ListAsync(ct)).FirstOrDefault(m => m.Name == name) is not { } model)
        {
            return null;
        }
        return (await policy.AllowedAsync(user, [name], ct)).Contains(name) ? model : null;
    }

    /// <summary>The small model for this person now: offered to them, and loaded or loading when asked; else null (the step uses the answer's own model).</summary>
    public async Task<GatewayModel?> ForAsync(AppUser user, CancellationToken ct = default) =>
        await OfferedAsync(user, ct) is { } model && await policy.RefusalAsync(user, model.Name, ct) is null ? model : null;

    /// <summary>"Thinking off" for a model that thinks; nothing for one that does not (its template may not know the switch).</summary>
    public static string? NoThinking(GatewayModel model) => model.Thinking ? "off" : null;

    string ISettingWarning.Key => "Chat:SmallModel";

    /// <summary>
    /// Why the small model cannot do its work well now: it is not at the gateway, or it is the
    /// engine's and the engine cannot hold it beside the big model (one model at once, not kept
    /// loaded, or every place kept for others). Null when all is well, or nothing is set.
    /// </summary>
    public async Task<string?> WarningAsync(CancellationToken ct = default)
    {
        if (Name is not { } name)
        {
            return null;
        }
        if ((await models.ListAsync(ct)).All(m => m.Name != name))
        {
            return $"{name} is not a chat model at the gateway: each small step uses the answer's own model until it is.";
        }
        // Another GPU server's model, or a cloud one: it is always there.
        if (!(await policy.OnEngineAsync(ct)).Contains(name))
        {
            return null;
        }
        var max = engine.Value.ModelsMax;
        var kept = catalog.Kept();
        if (max < 2)
        {
            return $"The engine holds one model at once: {name} and the big model would take turns, each loading again for every step. " +
                "Raise \"Models loaded at once\" to 2 and keep both loaded (Admin → Models).";
        }
        if (kept.Contains(name))
        {
            return null;
        }
        return kept.Count >= max
            ? $"{name} is not kept loaded, and every place in the engine keeps another model, so it cannot load: each small step uses the answer's own model. " +
              $"Keep it loaded instead of one of them, or raise \"Models loaded at once\" to {kept.Count + 1} (Admin → Models)."
            : $"{name} is not kept loaded: each small step waits for it to load, and pushes another model out. Keep it loaded beside the big model (Admin → Models).";
    }
}
