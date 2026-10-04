namespace Llm.Core.Chat;

/// <summary>
/// Something a person asked the chat to remember ("I deploy with Podman"), or kept when the
/// model offered it: given to every answer of theirs in a short block of the system prompt,
/// and to nobody else. They list, edit and delete them in Your account → Memory.
/// </summary>
public sealed class Memory
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    /// <summary>One short sentence about the person.</summary>
    public required string Text { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Written, edited or said again: the newest come first in the prompt.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
