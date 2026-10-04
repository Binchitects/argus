namespace Llm.Api.Safeguards;

/// <summary>Configuration section "Safeguards": what keeps the chat from being abused, or used to harm. Every part can be turned off.</summary>
public sealed class SafeguardOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Longest message a person may send, in characters; 0: no limit.</summary>
    public int MaxMessageChars { get; set; } = 100_000;

    /// <summary>Files one message may carry; 0: no limit.</summary>
    public int MaxAttachmentsPerMessage { get; set; } = 20;

    /// <summary>Messages one person may send in a minute, and in a day; 0: no limit.</summary>
    public int MessagesPerMinute { get; set; } = 20;
    public int MessagesPerDay { get; set; }

    /// <summary>Pictures one person may have drawn in a day, and deep research answers in a day; 0: no limit.</summary>
    public int ImagesPerDay { get; set; } = 100;
    public int ResearchPerDay { get; set; } = 20;

    /// <summary>
    /// Words, phrases or patterns a message may not contain, separated by ";" or new lines: a phrase matches
    /// as whole words, ignoring case; "re:" starts a regular expression.
    /// </summary>
    public string? BlockedPatterns { get; set; }

    /// <summary>off, or check: the model reads each message first and refuses one asking for harm in <see cref="ModerationCategories"/>.</summary>
    public string Moderation { get; set; } = "off";

    public string ModerationCategories { get; set; } = "violence,self-harm,sexual-minors,weapons,malware,hate";

    /// <summary>off, or mask: e-mail addresses, phone and card numbers, IBANs in messages reach the model masked (the chat keeps them).</summary>
    public string RedactPii { get; set; } = "off";

    /// <summary>
    /// Secrets in messages and files (private keys, cloud and service tokens, passwords): refuse (the message
    /// or file is not taken), mask (it goes with each secret replaced by a marker), or off.
    /// </summary>
    public string SecretScanning { get; set; } = "refuse";

    /// <summary>API keys' requests pass the same checks (secrets, blocked words, the model's check, personal data): the gateway asks the app before each one.</summary>
    public bool CheckApi { get; set; } = true;

    /// <summary>What the web gives the model is marked as data, never instructions to follow (against prompt injection).</summary>
    public bool UntrustedToolResults { get; set; } = true;

    /// <summary>Refused messages in a day after which the account is suspended (an admin turns it back on); 0: never.</summary>
    public int StrikesToSuspend { get; set; }

    /// <summary>Admins hear of each refused message in their bell.</summary>
    public bool NotifyAdmins { get; set; } = true;

    public string RefusalMessage { get; set; } = "This message was not sent: it goes against your organisation's rules for the assistant. Ask an admin if you think it should be allowed.";
}
