using System.Globalization;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>Why a canvas cannot be made or changed so, in words for the person or the model.</summary>
public sealed class CanvasException(string message) : Exception(message);

/// <summary>A change to make: exact text to find (once) and what replaces it.</summary>
public sealed record CanvasEdit(string Find, string Replace);

/// <summary>
/// A chat's canvases, and the rules the person (the page) and the model (CanvasTool)
/// both change them by. Every change is a version, with who made it and a short
/// summary; a change made over an older version than the latest is refused, so neither
/// overwrites the other's work unseen.
/// </summary>
public sealed class Canvases(AppDbContext db)
{
    /// <summary>The longest a canvas may be, in characters.</summary>
    public const int MaxChars = 200_000;
    public const int MaxPerChat = 50;
    public const int MaxTitle = 200;
    public const int MaxSummary = 300;

    public const string Person = "person";
    public const string Model = "model";

    public static readonly string[] Kinds = ["document", "code"];

    /// <summary>A chat's canvases, oldest first, without their text.</summary>
    public Task<List<Canvas>> ListAsync(Guid conversationId, CancellationToken ct) =>
        db.Canvases.AsNoTracking().Where(c => c.ConversationId == conversationId).OrderBy(c => c.CreatedAt).ToListAsync(ct);

    /// <summary>One of a chat's canvases as it is now (another request may have changed it since this context last read it).</summary>
    public async Task<Canvas?> FindAsync(Guid conversationId, Guid id, CancellationToken ct)
    {
        if (db.Canvases.Local.FirstOrDefault(c => c.Id == id) is { } known)
        {
            await db.Entry(known).ReloadAsync(ct);
            return known.ConversationId == conversationId ? known : null;
        }
        return await db.Canvases.SingleOrDefaultAsync(c => c.Id == id && c.ConversationId == conversationId, ct);
    }

    public async Task<Canvas> CreateAsync(Guid conversationId, string? title, string? kind, string? language, string? content, string author, CancellationToken ct)
    {
        kind = string.IsNullOrWhiteSpace(kind) ? "document" : kind.Trim().ToLowerInvariant();
        if (!Kinds.Contains(kind))
        {
            throw new CanvasException("A canvas is a \"document\" (Markdown) or \"code\".");
        }
        var text = Text(content ?? "");
        if (await db.Canvases.CountAsync(c => c.ConversationId == conversationId, ct) >= MaxPerChat)
        {
            throw new CanvasException($"This chat has {MaxPerChat} canvases, the most it may have. Change one of them instead.");
        }
        var canvas = new Canvas
        {
            ConversationId = conversationId, Title = Title(title), Kind = kind, Language = kind == "code" ? Language(language) : null, Content = text,
        };
        db.Canvases.Add(canvas);
        db.CanvasVersions.Add(new CanvasVersion { CanvasId = canvas.Id, Number = 1, Title = canvas.Title, Content = text, Author = author, Summary = "Created" });
        await db.SaveChangesAsync(ct);
        return canvas;
    }

    /// <summary>
    /// A new version: this text (and title), by the person or the model. Unchanged, nothing
    /// is saved. <paramref name="baseVersion"/>: the version the change was made on; when the
    /// canvas has moved on since, it is refused with <see cref="CanvasConflictException"/>.
    /// </summary>
    public async Task<bool> ChangeAsync(Canvas canvas, string content, string? title, string author, string? summary, int? baseVersion, CancellationToken ct)
    {
        if (baseVersion is { } seen && seen != canvas.Version)
        {
            throw new CanvasConflictException(canvas.Version);
        }
        var text = Text(content);
        var name = title is null ? canvas.Title : Title(title);
        if (text == canvas.Content && name == canvas.Title)
        {
            return false;
        }
        var said = string.IsNullOrWhiteSpace(summary) ? Changed(canvas.Content, text, canvas.Title != name) : summary.Trim();
        var version = new CanvasVersion
        {
            CanvasId = canvas.Id, Number = canvas.Version + 1, Title = name, Content = text, Author = author, Summary = said.Length > MaxSummary ? said[..MaxSummary] : said,
        };
        canvas.Content = text;
        canvas.Title = name;
        canvas.Version = version.Number;
        canvas.UpdatedAt = DateTimeOffset.UtcNow;
        db.CanvasVersions.Add(version);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Changed meanwhile by someone else: this change is not kept, and the canvas is as it is now again.
            db.Entry(version).State = EntityState.Detached;
            await db.Entry(canvas).ReloadAsync(ct);
            throw new CanvasConflictException(canvas.Version);
        }
        return true;
    }

    /// <summary>
    /// The model's (or anyone's) changes by find and replace, all or none. When the canvas
    /// changed in the moment between reading and saving, they are tried again on the new text.
    /// </summary>
    public async Task<int> EditAsync(Canvas canvas, IReadOnlyList<CanvasEdit> edits, string author, string? summary, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var text = Apply(canvas.Content, edits);
            try
            {
                await ChangeAsync(canvas, text, null, author, summary, null, ct);
                return edits.Count;
            }
            catch (CanvasConflictException) when (attempt == 0)
            {
                // The canvas was reloaded: the same edits, on what it is now.
            }
        }
    }

    /// <summary>A new version with an old one's text and title, by the person.</summary>
    public async Task<CanvasVersion?> RestoreAsync(Canvas canvas, int number, CancellationToken ct)
    {
        var old = await db.CanvasVersions.AsNoTracking().SingleOrDefaultAsync(v => v.CanvasId == canvas.Id && v.Number == number, ct);
        if (old is not null)
        {
            await ChangeAsync(canvas, old.Content, old.Title, Person, $"Restored version {number}", null, ct);
        }
        return old;
    }

    /// <summary>
    /// The edits made in turn on the text: each finds its text exactly once in the text as the
    /// edits before it left it. Any that does not, and nothing is changed: the reason says which and why.
    /// </summary>
    public static string Apply(string content, IReadOnlyList<CanvasEdit> edits)
    {
        if (edits.Count == 0)
        {
            throw new CanvasException("Give at least one edit: {\"find\": exact text in the canvas, \"replace\": what replaces it}.");
        }
        var text = content;
        for (var i = 0; i < edits.Count; i++)
        {
            var which = edits.Count == 1 ? "The edit" : $"Edit {i + 1}";
            var find = Lines(edits[i].Find);
            var replace = Lines(edits[i].Replace);
            if (find.Length == 0)
            {
                throw new CanvasException($"{which} has nothing in \"find\". Give the exact text to replace. Nothing was changed.");
            }
            var at = text.IndexOf(find, StringComparison.Ordinal);
            if (at < 0)
            {
                throw new CanvasException($"{which}: its \"find\" text is not in the canvas. It must match exactly, spaces and line breaks too.{Near(text, find)} " +
                    "Read the canvas again with canvas_read. Nothing was changed.");
            }
            var again = text.IndexOf(find, at + 1, StringComparison.Ordinal);
            if (again >= 0)
            {
                var places = new List<int>();
                for (var p = at; p >= 0 && places.Count < 6; p = text.IndexOf(find, p + 1, StringComparison.Ordinal))
                {
                    places.Add(LineAt(text, p));
                }
                throw new CanvasException($"{which}: its \"find\" text is in the canvas more than once (lines {string.Join(", ", places.Distinct())}). " +
                    "Add more of the text around it, so it matches one place. Nothing was changed.");
            }
            text = string.Concat(text.AsSpan(0, at), replace, text.AsSpan(at + find.Length));
        }
        return Text(text);
    }

    /// <summary>"Changed lines 4–9": where the text differs, for a version made without a summary.</summary>
    public static string Changed(string before, string after, bool renamed = false)
    {
        var a = Split(before);
        var b = Split(after);
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start])
        {
            start++;
        }
        var (endA, endB) = (a.Length, b.Length);
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1])
        {
            (endA, endB) = (endA - 1, endB - 1);
        }
        string Range(int from, int to) => to - from == 1
            ? $"line {(from + 1).ToString(CultureInfo.InvariantCulture)}"
            : $"lines {(from + 1).ToString(CultureInfo.InvariantCulture)}–{to.ToString(CultureInfo.InvariantCulture)}";
        var change = start == endA && start == endB ? null
            : endB == start ? $"Removed {Range(start, endA)}"
            : endA == start ? $"Added {Range(start, endB)}"
            : $"Changed {Range(start, endB)}";
        return (renamed, change) switch
        {
            (true, null) => "Renamed",
            (true, _) => $"Renamed; {char.ToLowerInvariant(change[0])}{change[1..]}",
            (false, null) => "No change",
            _ => change,
        };
    }

    /// <summary>Its lines; a last line break ends the last line, it does not start another.</summary>
    public static string[] Split(string text) => text.Length == 0 ? [] : (text.EndsWith('\n') ? text[..^1] : text).Split('\n');

    /// <summary>The line (from 1) a character is on.</summary>
    public static int LineAt(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static string Near(string text, string find)
    {
        var first = find.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length >= 4);
        if (first is null)
        {
            return "";
        }
        var lines = Split(text);
        var line = Array.FindIndex(lines, l => l.Contains(first, StringComparison.Ordinal));
        if (line < 0)
        {
            line = Array.FindIndex(lines, l => l.Contains(first, StringComparison.OrdinalIgnoreCase));
        }
        return line < 0 ? "" : $" Its first line looks like line {(line + 1).ToString(CultureInfo.InvariantCulture)}: \"{Cut(lines[line].Trim(), 120)}\".";
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Line breaks as \n, and within the size a canvas may have.</summary>
    private static string Text(string text)
    {
        text = Lines(text);
        return text.Length > MaxChars
            ? throw new CanvasException($"A canvas holds at most {MaxChars:N0} characters; this would be {text.Length:N0}. Split it into more than one canvas.")
            : text;
    }

    private static string Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string Title(string? title)
    {
        var t = string.Join(' ', (title ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length == 0 ? "Untitled" : t.Length > MaxTitle ? t[..MaxTitle] : t;
    }

    private static string? Language(string? language)
    {
        var l = (language ?? "").Trim().ToLowerInvariant();
        return l.Length == 0 ? null : l.Length > 40 ? l[..40] : l;
    }

    /// <summary>A code canvas's language as given (for a change of language by the person).</summary>
    public static string? LanguageOf(string? language) => Language(language);
}

/// <summary>The canvas changed since the version a change was made on: <see cref="Version"/> is what it is now.</summary>
public sealed class CanvasConflictException(int version) : Exception($"The canvas changed meanwhile: it is at version {version} now.")
{
    public int Version { get; } = version;
}
