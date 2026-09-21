namespace Llm.Api.Endpoints;

public sealed record LoginRequest(string UserName, string Password, bool Remember = false, string? Redirect = null);
public sealed record TwoFactorRequest(string Code, bool Recovery = false, bool Remember = false, string? Redirect = null);
public sealed record ChangePasswordRequest(string Current, string Next);
public sealed record CodeRequest(string Code);
public sealed record CreatePersonRequest(string UserName, string Email, string? DisplayName, bool Admin = false, decimal? Budget = null);
public sealed record UpdatePersonRequest(bool? Admin = null, bool? Disabled = null, string? DisplayName = null);
public sealed record BudgetRequest(decimal? Budget);
