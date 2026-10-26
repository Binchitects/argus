using System.Globalization;
using System.Text.RegularExpressions;
using Llm.Api.Chat;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Models;

/// <summary>
/// How each chat model is given its tools (Admin → Models → Tools): the admins' choice, else the automatic one. Small models
/// write wrong programs more often than they make a wrong call, so they call tools directly; the others get both, run_code
/// for many calls and a direct call for one or two. The chat and Code Arena (its config) follow it.
/// </summary>
public sealed partial class ToolModeBook(AppDbContext db, SmallModel small)
{
    /// <summary>Models of this many billion parameters or fewer (by their names: Qwen3-4B, llama-3.1-8b) are small.</summary>
    public const double SmallBillions = 8;

    /// <summary>The automatic choice: direct for the model for small steps and the small ones by their names, both for the rest.</summary>
    public static string Automatic(string model, string? smallModel) =>
        model == smallModel || Billions(model) is <= SmallBillions ? ToolModes.Direct : ToolModes.Both;

    /// <summary>The size a model's name says, in billions of parameters (the largest it says; 8x7B is 56, 135M is 0.135), or null when it says none.</summary>
    public static double? Billions(string model)
    {
        double? most = null;
        foreach (Match m in Size().Matches(model))
        {
            var size = double.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture) * (m.Groups["experts"].Success ? int.Parse(m.Groups["experts"].Value, CultureInfo.InvariantCulture) : 1)
                / (m.Groups["unit"].Value is "m" or "M" ? 1000 : 1);
            most = Math.Max(most ?? 0, size);
        }
        return most;
    }

    /// <summary>The admins' choices, by model.</summary>
    public async Task<Dictionary<string, string>> SetAsync(CancellationToken ct = default) =>
        await db.ModelToolModes.AsNoTracking().ToDictionaryAsync(m => m.Model, m => m.Mode, StringComparer.Ordinal, ct);

    /// <summary>Each model's way, the admins' choices read once: for a list of models.</summary>
    public async Task<Func<string, string>> ResolverAsync(CancellationToken ct = default)
    {
        var set = await SetAsync(ct);
        var smallName = small.Name;
        return model => set.TryGetValue(model, out var mode) ? mode : Automatic(model, smallName);
    }

    /// <summary>One model's way.</summary>
    public async Task<string> OfAsync(string model, CancellationToken ct = default) =>
        await db.ModelToolModes.AsNoTracking().Where(m => m.Model == model).Select(m => m.Mode).FirstOrDefaultAsync(ct) ?? Automatic(model, small.Name);

    /// <summary>Sets a model's way, or (null) gives it the automatic one again.</summary>
    public async Task SetAsync(string model, string? mode, CancellationToken ct = default)
    {
        var row = await db.ModelToolModes.FirstOrDefaultAsync(m => m.Model == model, ct);
        if (mode is null)
        {
            if (row is not null)
            {
                db.ModelToolModes.Remove(row);
            }
        }
        else if (row is null)
        {
            db.ModelToolModes.Add(new ModelToolMode { Model = model, Mode = mode });
        }
        else
        {
            row.Mode = mode;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    [GeneratedRegex(@"(?<![a-z0-9.])(?:(?<experts>\d+)x)?(?<size>\d+(?:\.\d+)?)(?<unit>[bm])(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Size();
}
