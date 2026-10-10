namespace Llm.Api.Endpoints;

public sealed record LoginRequest(string UserName, string Password, bool Remember = false, string? Redirect = null);
public sealed record TwoFactorRequest(string Code, bool Recovery = false, bool Remember = false, string? Redirect = null);
public sealed record ChangePasswordRequest(string Current, string Next);
public sealed record CodeRequest(string Code);
public sealed record CreatePersonRequest(string UserName, string Email, string? DisplayName, bool Admin = false, Gateway.CreditsRequest? Credits = null);
public sealed record UpdatePersonRequest(bool? Admin = null, bool? Disabled = null, string? DisplayName = null);
/// <summary>One kind's credit in dollars a calendar month; null: no limit of that kind.</summary>
public sealed record CreditRequest(decimal? Credit);

/// <summary>API access on (their keys work) or off (blocked, none shown or made).</summary>
public sealed record ApiAccessRequest(bool On);
/// <summary>A person's own rate limits for their API keys, a minute: null takes their groups' or the company's, 0 is no limit.</summary>
public sealed record LimitsRequest(int? RequestsPerMinute = null, int? TokensPerMinute = null);

/// <summary>The people to move to directory sign-in.</summary>
public sealed record MoveRequest(List<Guid>? Ids);
