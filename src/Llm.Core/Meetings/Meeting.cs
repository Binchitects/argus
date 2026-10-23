namespace Llm.Core.Meetings;

/// <summary>
/// A meeting recorded in the browser or uploaded: its sound (an attachment of its owner's, as an MP3), what was said
/// and by whom (the transcript, each part with its speaker), the speakers' names, and its minutes — a summary, the
/// decisions and the action items (<see cref="MeetingAction"/>) — made by the model; and the chat that asks about it.
/// </summary>
public sealed class Meeting
{
    public const string Processing = "processing";
    public const string Ready = "ready";
    public const string Failed = "failed";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string Title { get; set; } = "";
    public string Status { get; set; } = Processing;
    /// <summary>What it is doing while it is made (for the page).</summary>
    public string? Step { get; set; }
    public string? Error { get; set; }
    /// <summary>Its sound: the attachment kept as an MP3 (mono, 16 kHz).</summary>
    public Guid? RecordingId { get; set; }
    public double? Seconds { get; set; }
    /// <summary>The language spoken, as speech to text heard it (en, fa).</summary>
    public string? Language { get; set; }
    /// <summary>What was said, as JSON: [{"start", "end", "speaker", "text"}], the speaker a number from 0.</summary>
    public string Transcript { get; set; } = "[]";
    /// <summary>Who the speakers are, as JSON: [{"speaker", "name", "personId"}], named by the model when the meeting says, or by its owner.</summary>
    public string Speakers { get; set; } = "[]";
    public string? Summary { get; set; }
    /// <summary>What was decided, as JSON: ["…"].</summary>
    public string Decisions { get; set; } = "[]";
    public string? Model { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    /// <summary>The plan its action items were made into, once they were.</summary>
    public Guid? PlanId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<MeetingAction> Actions { get; set; } = [];
}

/// <summary>
/// Something to be done after a meeting: what, whose (a speaker, and the person they are in the app when known), by when,
/// and whether it is done; its owner reminded once, on the day it is due.
/// </summary>
public sealed class MeetingAction
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid MeetingId { get; set; }
    public int Number { get; set; }
    public string Text { get; set; } = "";
    /// <summary>The speaker it falls to (a number from 0), when the meeting said whose it is.</summary>
    public int? Speaker { get; set; }
    /// <summary>Whose it is by name, when not a speaker's (someone not in the meeting), or as its owner set it.</summary>
    public string? Owner { get; set; }
    /// <summary>The person in the app it falls to: told now, and reminded on the day it is due.</summary>
    public Guid? PersonId { get; set; }
    public DateOnly? Due { get; set; }
    public bool Done { get; set; }
    public DateTimeOffset? RemindedAt { get; set; }
}
