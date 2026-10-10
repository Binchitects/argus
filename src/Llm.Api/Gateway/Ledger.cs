using Llm.Core.Access;
using Llm.Core.Identity;

namespace Llm.Api.Gateway;

/// <summary>This calendar month's spend (UTC), by person and credit kind; null with <see cref="Problem"/> when the request log cannot be read.</summary>
public sealed record Spending(MonthBook? Month, string? Problem = null)
{
    /// <summary>Everything spent this month, by anyone (unattributed requests too).</summary>
    public decimal Total => Month?.Spend.Values.Sum(k => k.Values.Sum()) ?? 0m;

    public decimal Of(string? email) => email is null || Month is null ? 0m : Month.Total(email);

    public decimal Of(string? email, CreditKind kind) => email is null || Month is null ? 0m : Month.Of(email, kind);

    /// <summary>The kinds whose own credit the person has used up this month (a credit of $0, none of that kind at all, is not "used up").</summary>
    public IReadOnlyList<CreditKind> Over(AppUser u) =>
        [.. Credits.Kinds.Where(k => Credits.Of(u, k) is > 0 and var credit && Of(u.Email, k) >= credit)];

    /// <summary>For the pages: each kind's spend this month (null when the request log cannot be read) and the person's own credit (null: no limit).</summary>
    public object Kinds(AppUser u) => Credits.Kinds.ToDictionary(Credits.Name, k => new { spent = Month is null ? (decimal?)null : Of(u.Email, k), credit = Credits.Of(u, k) });
}

/// <summary>
/// What each person has spent this month, kind by kind, by the gateway's request log over every path (chat, API keys,
/// agents), by the same rule as the usage dashboards, so every page says what the dashboards say and what the credits
/// hold them to (<see cref="Credit"/>).
/// </summary>
public sealed class Ledger(CreditBook book)
{
    public async Task<Spending> ReadAsync(CancellationToken ct = default) =>
        await book.ReadAsync(ct, fresh: true) is { } month ? new Spending(month) : new Spending(null, "This month's spend is missing: the gateway's request log cannot be read.");
}
