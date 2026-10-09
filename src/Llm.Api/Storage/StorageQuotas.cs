using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Storage;

/// <summary>
/// Each person's room for files: their own when an admin set one (0: no limit), else the company's
/// (Settings → Storage → Room for each person's files). Past it, uploads and the pictures and videos
/// the tools make are refused; what Python makes and long tool results still go (small, and the
/// answer needs them).
/// </summary>
public sealed class StorageQuotas(AppDbContext db, StorageFiles files, IOptionsMonitor<StorageOptions> options)
{
    /// <summary>The person's room in bytes; null: no limit.</summary>
    public async Task<long?> LimitAsync(Guid userId, CancellationToken ct = default)
    {
        var own = await db.StorageQuotas.AsNoTracking().Where(q => q.UserId == userId).Select(q => (int?)q.Megabytes).FirstOrDefaultAsync(ct);
        var megabytes = own ?? options.CurrentValue.PersonMegabytes;
        return megabytes is > 0 ? megabytes.Value * 1024L * 1024 : null;
    }

    /// <summary>What the person's files take, and their room (null: no limit).</summary>
    public async Task<(long Used, long? Limit)> OfAsync(Guid userId, CancellationToken ct = default) =>
        (await files.UsedByAsync(userId, ct), await LimitAsync(userId, ct));

    /// <summary>Why <paramref name="adding"/> more bytes do not fit the person's room, or null when they do.</summary>
    public async Task<string?> RefusalAsync(Guid userId, long adding, CancellationToken ct = default)
    {
        if (await LimitAsync(userId, ct) is not { } limit)
        {
            return null;
        }
        var used = await files.UsedByAsync(userId, ct);
        if (adding > 0 ? used + adding <= limit : used < limit)
        {
            return null;
        }
        var what = $"Your files take {StorageDisks.Size(used)} of your {StorageDisks.Size(limit)}";
        return adding > 0
            ? $"{what}: this one ({StorageDisks.Size(adding)}) does not fit. Delete chats you no longer need, or ask an admin for more room."
            : $"{what}. Delete chats you no longer need, or ask an admin for more room.";
    }

    /// <summary>What a tool says to the model when the person's room is full: nothing was made, and why.</summary>
    public async Task<string?> ToolRefusalAsync(Guid userId, string what, CancellationToken ct = default) =>
        await RefusalAsync(userId, 0, ct) is { } why
            ? $"No {what} was made: the person's files are full. Tell them: {why}"
            : null;

    /// <summary>Gives a person their own room (megabytes; 0: no limit), or takes it away (null: the company's).</summary>
    public async Task SetAsync(Guid userId, int? megabytes, CancellationToken ct = default)
    {
        await db.StorageQuotas.Where(q => q.UserId == userId).ExecuteDeleteAsync(ct);
        if (megabytes is { } mb)
        {
            db.StorageQuotas.Add(new StorageQuota { UserId = userId, Megabytes = mb });
            await db.SaveChangesAsync(ct);
        }
    }
}
