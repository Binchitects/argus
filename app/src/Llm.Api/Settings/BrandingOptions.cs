namespace Llm.Api.Settings;

/// <summary>Configuration section "Branding": what people see the service called (Settings page, applies at once).</summary>
public sealed class BrandingOptions
{
    public string ProductName { get; set; } = "LLM Service";
    public string? SignInHeadline { get; set; } = "Your organisation's model, code search and usage, in one place.";
    public string? SupportContact { get; set; }
}
