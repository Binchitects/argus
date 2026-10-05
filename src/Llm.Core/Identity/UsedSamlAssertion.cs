namespace Llm.Core.Identity;

/// <summary>A SAML assertion that has signed someone in, kept until it expires: the same one never signs anyone in again.</summary>
public sealed class UsedSamlAssertion
{
    /// <summary>The assertion's ID, as the identity provider made it.</summary>
    public required string Id { get; set; }
    /// <summary>When the assertion stops being acceptable anyway (with the clock skew); the row may go then.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
