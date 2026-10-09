using System.Globalization;
using Llm.Api.Chat;
using Llm.Core.Models;

namespace Llm.Api.Models;

/// <summary>
/// What a model's token cache keeps and costs (Admin → Models, on its card). Its slots (parallel) each
/// hold a conversation's prompt, so the next turn reads only what is new (the app sends each turn back to
/// its slot, SlotTable); from 3 slots, side requests go to the last first. An idle slot keeps what it
/// holds (no-cache-idle-slots). A hybrid or recurrent model also keeps checkpoints of its state in RAM,
/// to go back to a shorter prompt (the turn before an edit, a retry): each is about its recurrent state.
/// </summary>
/// <param name="Slots">Its slots: the answers and side requests it serves at once.</param>
/// <param name="SideSlot">The slot side requests go to first (conversations take it last), or null (fewer than 3 slots, or the gateway picks among copies).</param>
/// <param name="Pinned">Whether conversations keep their slot: on this engine alone, with 2 slots or more.</param>
/// <param name="KeepsIdle">Whether an idle slot keeps its prompt (else llama.cpp moves it to RAM and empties it at the next request).</param>
/// <param name="Checkpoints">Checkpoints a slot keeps (hybrid and recurrent models); null for others.</param>
/// <param name="CheckpointBytes">The RAM one checkpoint takes, from the file's profile; null when unknown.</param>
/// <param name="RamBytes">The RAM every slot's checkpoints take at most, together.</param>
public sealed record TokenCache(int Slots, int? SideSlot, bool Pinned, bool KeepsIdle, int? Checkpoints, long? CheckpointBytes, long? RamBytes)
{
    /// <summary>Checkpoints a slot keeps unless the model's extra lines say otherwise (llama.cpp's own is 32).</summary>
    public const int DefaultCheckpoints = 4;

    /// <summary>Whether the model keeps checkpoints of a state: hybrid and recurrent attention.</summary>
    public static bool Checkpointed(ModelProfile? profile) => profile?.Attention is "hybrid" or "recurrent";

    /// <summary>The cache of a model added here; <paramref name="pooled"/>: it has copies on other GPU servers.</summary>
    public static TokenCache Of(LocalModel m, ModelProfile? profile, bool pooled)
    {
        var slots = Math.Max(1, m.Parallel);
        var own = ModelCatalog.ExtraValues(m.ExtraPreset);
        static bool On(string? v) => v?.Trim().ToLowerInvariant() is "true" or "on" or "1" or "enabled";
        var keepsIdle = !(own.TryGetValue("cache-idle-slots", out var cache) && On(cache))
            && !(own.TryGetValue("no-cache-idle-slots", out var no) && !On(no));
        int? checkpoints = Checkpointed(profile)
            ? (int.TryParse(own.GetValueOrDefault("ctx-checkpoints") ?? own.GetValueOrDefault("swa-checkpoints"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? Math.Max(0, n) : DefaultCheckpoints)
            : null;
        long? each = checkpoints is not null && profile!.RecurrentBytesPerSlot > 0 ? profile.RecurrentBytesPerSlot : null;
        return new TokenCache(slots, pooled ? null : SlotTable.SideSlot(slots), !pooled && slots >= 2, keepsIdle, checkpoints, each,
            each is { } b ? b * checkpoints!.Value * slots : null);
    }
}
