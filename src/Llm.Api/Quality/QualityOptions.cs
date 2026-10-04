namespace Llm.Api.Quality;

/// <summary>Configuration section "Quality": feedback on answers and the arena.</summary>
public sealed class QualityOptions
{
    /// <summary>Everyone sees the arena's leaderboard; off: admins only (Admin → Quality).</summary>
    public bool PublicLeaderboard { get; set; } = true;
}
