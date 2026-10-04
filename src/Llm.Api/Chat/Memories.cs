using System.Text;
using System.Text.RegularExpressions;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>Configuration section "Memory".</summary>
public sealed class MemoryOptions
{
    /// <summary>Memory for everyone. Off: no answer reads or offers memories (people's own stay, to see and delete).</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// A person's memories: kept, listed, and given to each of their answers as a short block of
/// the system prompt, the newest first, within a few hundred tokens. The block comes after the
/// app's fixed notes and the tools', so a new memory leaves the cached start of the prompt as
/// it was. A memory never leaves its person: no admin page, audit entry or log shows one.
/// </summary>
public sealed partial class Memories(AppDbContext db, IOptionsMonitor<MemoryOptions> options)
{
    /// <summary>One memory's longest text: a short sentence.</summary>
    public const int MaxChars = 300;

    /// <summary>Memories one person may keep.</summary>
    public const int PerPerson = 100;

    /// <summary>What the block may take of the prompt (about 400 tokens): older memories past it are left out.</summary>
    public const int PromptChars = 1_500;

    /// <summary>The first line of the system prompt's block.</summary>
    public const string Heading =
        "What you remember about the person, from their earlier chats (use it where it helps; do not mention it unless it matters):";

    /// <summary>Memory is on for everyone (Settings → Chat → Memory).</summary>
    public bool Enabled => options.CurrentValue.Enabled;

    /// <summary>Memory is on for this person: on for everyone, and not turned off by them.</summary>
    public bool On(AppUser user) => Enabled && !user.MemoryOff;

    /// <summary>The person's memories, the newest first.</summary>
    public Task<List<Memory>> ListAsync(Guid userId, CancellationToken ct) =>
        db.Memories.AsNoTracking().Where(m => m.UserId == userId).OrderByDescending(m => m.UpdatedAt).ThenByDescending(m => m.CreatedAt).ToListAsync(ct);

    /// <summary>The system prompt's block for the person, or null (memory off, or nothing remembered).</summary>
    public async Task<string?> NoteAsync(AppUser user, CancellationToken ct) =>
        On(user) ? Note((await ListAsync(user.Id, ct)).Select(m => m.Text)) : null;

    /// <summary>The block: a line per memory, the newest first, as many as fit in <see cref="PromptChars"/>.</summary>
    public static string? Note(IEnumerable<string> newestFirst)
    {
        var block = new StringBuilder(Heading);
        var any = false;
        foreach (var text in newestFirst)
        {
            var line = "\n- " + text;
            if (block.Length + line.Length > Heading.Length + PromptChars)
            {
                break;
            }
            block.Append(line);
            any = true;
        }
        return any ? block.ToString() : null;
    }

    /// <summary>A memory as written, on one line and trimmed; and why it cannot be kept, if so.</summary>
    public static (string Text, string? Problem) Check(string? text)
    {
        var tidy = Spaces().Replace(text ?? "", " ").Trim();
        return tidy.Length switch
        {
            0 => (tidy, "Say what to remember."),
            > MaxChars => (tidy, $"A memory is at most {MaxChars} characters: one short sentence."),
            _ => (tidy, null),
        };
    }

    /// <summary>The person's memory with these words already, if any (case and a final full stop aside).</summary>
    public async Task<Memory?> FindAsync(Guid userId, string text, CancellationToken ct)
    {
        var key = Key(text);
        return (await db.Memories.Where(m => m.UserId == userId).ToListAsync(ct)).FirstOrDefault(m => Key(m.Text) == key);
    }

    /// <summary>
    /// Keeps a memory: a new one, or the same words said again (that one comes first again).
    /// Null when the person has <see cref="PerPerson"/> already.
    /// </summary>
    public async Task<Memory?> KeepAsync(Guid userId, string text, CancellationToken ct)
    {
        if (await FindAsync(userId, text, ct) is { } same)
        {
            same.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return same;
        }
        if (await db.Memories.CountAsync(m => m.UserId == userId, ct) >= PerPerson)
        {
            return null;
        }
        var made = new Memory { UserId = userId, Text = text };
        db.Memories.Add(made);
        await db.SaveChangesAsync(ct);
        return made;
    }

    private static string Key(string text) => text.Trim().TrimEnd('.').ToUpperInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
