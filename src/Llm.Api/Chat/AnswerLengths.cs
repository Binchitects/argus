namespace Llm.Api.Chat;

/// <summary>
/// How long a person wants answers (Account → Answers), said to the model in the system prompt;
/// and "Shorter" or "Longer" on one answer, said with the question for that answer only.
/// </summary>
public static class AnswerLengths
{
    public const string Brief = "short";
    public const string Normal = "normal";
    public const string Thorough = "thorough";
    public static readonly string[] All = [Brief, Normal, Thorough];

    /// <summary>The system prompt's line for a person's choice; none for normal (the model judges).</summary>
    public static string? Note(string? length) => length switch
    {
        Brief => "The person prefers short answers: the answer first, usually in one to three sentences or a short list, without headings, " +
            "preamble, recap or offers of more. Write more only when the task needs it (code, a document, steps to follow) or they ask.",
        Thorough => "The person prefers thorough answers: explain the reasoning, cover the cases that matter, and give examples.",
        _ => null,
    };

    /// <summary>For answering again shorter or longer than an answer of so many words.</summary>
    public static string? Again(string? direction, int words) => direction switch
    {
        "shorter" => $"(Answer again, shorter: the last answer to this was about {words} words; this time about {Math.Max(words / 2, 15)}, keeping what matters most.)",
        "longer" => $"(Answer again, longer: the last answer to this was about {words} words; this time about {Math.Max(words * 2, 100)}, with more detail, reasons and examples.)",
        _ => null,
    };

    public static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
