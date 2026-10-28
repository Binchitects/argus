namespace Llm.Core.Identity;

/// <summary>
/// An API key a person made for themselves (Account → API keys): its name, and the limits they set on it, which hold under
/// those an admin set on them (the tighter of the two reaches the gateway). The key itself is never kept, only the hash the
/// gateway knows it by.
/// </summary>
public sealed class OwnKey
{
    /// <summary>The key as the gateway keeps it: its SHA-256, in hex.</summary>
    public required string Token { get; set; }
    public Guid UserId { get; set; }
    public required string Name { get; set; }
    /// <summary>Its requests a minute; null: as many as the person may.</summary>
    public int? RequestsPerMinute { get; set; }
    /// <summary>Its tokens a minute; null: as many as the person may.</summary>
    public int? TokensPerMinute { get; set; }
    /// <summary>Its requests at once; null: as many as anyone's key may.</summary>
    public int? Parallel { get; set; }
    /// <summary>What it may spend in 30 days, in dollars; null: what the person may.</summary>
    public decimal? MonthlyBudget { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
