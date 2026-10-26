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

/// <summary>What a memory is beside its words: whose (its scope, and project), its kind, where it came from, pinned or not.</summary>
public sealed record MemoryFacts(string Scope = Memory.Person, string Kind = Memory.Fact, string? ProjectKey = null, string Source = Memory.Typed,
    Guid? SourceId = null, bool Pinned = false, bool Proposed = false, Guid? Replaces = null);

/// <summary>A memory found for a question, and how near it is to it (0 to 1).</summary>
public sealed record Recalled(Memory Memory, double Score);

/// <summary>
/// The memories of the chat and Code Arena: kept (a memory saying what one kept says already is that one, said again),
/// listed, and given to the model in two parts. The prompt's block holds the pinned ones, the company's and the newest,
/// and changes only when they do (the start of the prompt stays cached); before each answer, those that matter to its
/// question are found by meaning (the knowledge embedder; by words without it) and go with the question. A person's
/// memories never leave them: no admin page, audit entry or log shows one.
/// </summary>
public sealed partial class Memories(AppDbContext db, IOptionsMonitor<MemoryOptions> options, Knowledge.Embedder embedder, ILogger<Memories> logger)
{
    /// <summary>One memory's longest text: one or two short sentences.</summary>
    public const int MaxChars = 500;

    /// <summary>Memories one person may keep (theirs and their projects').</summary>
    public const int PerPerson = 1000;

    /// <summary>The company's memories at most.</summary>
    public const int CompanyMost = 300;

    /// <summary>What the prompt's block may take (about 400 tokens): past it, older memories are left to recall.</summary>
    public const int PromptChars = 1_500;

    /// <summary>What the memories found for a question may take (about 500 tokens).</summary>
    public const int RecallChars = 2_000;

    /// <summary>Two memories this near (cosine) say the same: the newer words replace the older.</summary>
    public const double Same = 0.92;

    /// <summary>A memory at least this near the question may be given with it (by meaning; by words, <see cref="WordsNear"/>).</summary>
    public const double Near = 0.35;

    public const double WordsNear = 0.2;

    /// <summary>
    /// What matters stands out: a memory is given with a question when it is this much nearer than the question's typical
    /// memory (their median: an embedder finds any two sentences somewhat alike, unrelated ones about 0.45 to 0.6), and
    /// within <see cref="Spread"/> of the nearest. Fewer than five to compare: <see cref="Near"/> alone.
    /// </summary>
    public const double Stand = 0.12, Spread = 0.1;

    /// <summary>The first line of the system prompt's block.</summary>
    public const string Heading =
        "What you remember about the person, from their earlier chats (use it where it helps; do not mention it unless it matters):";

    /// <summary>The first line of what goes with a question.</summary>
    public const string RecallHeading = "(Memories that may matter to this question; use them where they help:";

    /// <summary>Memory is on for everyone (Settings → Chat → Memory).</summary>
    public bool Enabled => options.CurrentValue.Enabled;

    /// <summary>Memory is on for this person: on for everyone, and not turned off by them.</summary>
    /// <summary>Whether memory works for them: on for the company, not turned off by them, and their processing not restricted (GDPR 18, 21).</summary>
    public bool On(AppUser user) => Enabled && !user.MemoryOff && user.ProcessingRestrictedAt == null;

    /// <summary>What this person's answers may read: their own (and their project's, when one is given) and the company's; none replaced.</summary>
    public IQueryable<Memory> Readable(Guid userId, string? project) =>
        db.Memories.Where(m => m.ReplacedBy == null && !m.Proposed &&
            ((m.UserId == userId && (m.Scope == Memory.Person || (project != null && m.Scope == Memory.Project && m.ProjectKey == project))) || m.Scope == Memory.Company));

    /// <summary>The person's memories (every scope and project of theirs, and the company's), the newest first; those replaced too when asked.</summary>
    public Task<List<Memory>> ListAsync(Guid userId, CancellationToken ct, bool replaced = false) =>
        db.Memories.AsNoTracking().Where(m => (m.UserId == userId || m.Scope == Memory.Company) && (replaced || m.ReplacedBy == null))
            .OrderByDescending(m => m.Pinned).ThenByDescending(m => m.UpdatedAt).ThenByDescending(m => m.CreatedAt).ToListAsync(ct);

    /// <summary>The system prompt's block for the person (pinned, the company's, then the newest), and the memories in it; null when there are none.</summary>
    public async Task<(string? Note, List<Guid> Ids)> NoteAsync(AppUser user, CancellationToken ct, string? project = null)
    {
        if (!On(user))
        {
            return (null, []);
        }
        var all = await Readable(user.Id, project).AsNoTracking()
            .OrderByDescending(m => m.Pinned).ThenByDescending(m => m.Scope == Memory.Company).ThenByDescending(m => m.UpdatedAt).Take(200).ToListAsync(ct);
        var block = new StringBuilder(Heading);
        var ids = new List<Guid>();
        foreach (var m in all)
        {
            var line = "\n- " + Line(m);
            // Pinned ones are always there; the rest while they fit.
            if (!m.Pinned && block.Length + line.Length > Heading.Length + PromptChars)
            {
                break;
            }
            block.Append(line);
            ids.Add(m.Id);
        }
        return ids.Count > 0 ? (block.ToString(), ids) : (null, []);
    }

    /// <summary>A memory as the model reads it: its words, with its kind when not a plain fact, and whose when not the person's.</summary>
    public static string Line(Memory m)
    {
        var tags = new List<string>();
        if (m.Kind != Memory.Fact)
        {
            tags.Add(m.Kind == Memory.HowTo ? "how to" : m.Kind);
        }
        if (m.Scope == Memory.Company)
        {
            tags.Add("the company's");
        }
        else if (m.Scope == Memory.Project)
        {
            tags.Add("this project's");
        }
        return tags.Count > 0 ? $"{m.Text} ({string.Join(", ", tags)})" : m.Text;
    }

    /// <summary>
    /// The memories that matter to a question, nearest first, within <paramref name="budget"/> characters: those not in
    /// the prompt's block already (<paramref name="besides"/>), by meaning when the embedder answers, else by words.
    /// </summary>
    public async Task<List<Recalled>> RecallAsync(AppUser user, string question, CancellationToken ct, string? project = null, IReadOnlyCollection<Guid>? besides = null,
        int budget = RecallChars, int most = 12)
    {
        if (!On(user) || string.IsNullOrWhiteSpace(question))
        {
            return [];
        }
        var skip = besides ?? [];
        var candidates = await Readable(user.Id, project).AsNoTracking().Where(m => !skip.Contains(m.Id)).ToListAsync(ct);
        if (candidates.Count == 0)
        {
            return [];
        }
        var vector = await QueryVectorAsync(question, ct);
        var words = Words(question);
        // Each memory's nearness: by meaning when both have a vector of the same embedder, else by the words they share.
        var raw = candidates.Select(m => vector is not null && m.Embedding is { } e && e.Length == vector.Length
                ? (Memory: m, Score: Dot(vector, e), Floor: Near)
                : (Memory: m, Score: WordScore(words, Words(m.Text)), Floor: WordsNear)).ToList();
        // What stands out from this question's typical memory (their median), and comes near the nearest.
        var ordered = raw.Select(r => r.Score).Order().ToList();
        var typical = ordered.Count >= 5 ? (ordered[(ordered.Count - 1) / 2] + ordered[ordered.Count / 2]) / 2 : 0;
        var top = ordered[^1];
        var bar = Math.Max(typical + Stand, top - Spread);
        var scored = raw.Where(r => r.Score >= r.Floor && r.Score >= bar).Select(r => new Recalled(r.Memory, r.Score))
            .OrderByDescending(r => r.Score).ThenByDescending(r => r.Memory.UpdatedAt).ToList();
        var found = new List<Recalled>();
        var used = 0;
        foreach (var r in scored)
        {
            var size = Line(r.Memory).Length + 3;
            if (found.Count >= most || used + size > budget)
            {
                break;
            }
            found.Add(r);
            used += size;
        }
        return found;
    }

    /// <summary>What goes with the question: the memories found for it.</summary>
    public static string QuestionNote(IEnumerable<Recalled> found) =>
        RecallHeading + string.Concat(found.Select(r => "\n- " + Line(r.Memory))) + ")";

    /// <summary>These memories were read by an answer: counted, and when.</summary>
    public Task UsedAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return Task.CompletedTask;
        }
        var now = DateTimeOffset.UtcNow;
        return db.Memories.Where(m => ids.Contains(m.Id)).ExecuteUpdateAsync(s => s.SetProperty(m => m.Uses, m => m.Uses + 1).SetProperty(m => m.UsedAt, now), ct);
    }

    /// <summary>A memory as written, on one line and trimmed; and why it cannot be kept, if so.</summary>
    public static (string Text, string? Problem) Check(string? text)
    {
        var tidy = Spaces().Replace(text ?? "", " ").Trim();
        return tidy.Length switch
        {
            0 => (tidy, "Say what to remember."),
            > MaxChars => (tidy, $"A memory is at most {MaxChars} characters: one or two short sentences."),
            _ => (tidy, null),
        };
    }

    /// <summary>Why these facts cannot be, if so: an unknown scope or kind, a project's without its project.</summary>
    public static string? Problem(MemoryFacts facts) =>
        !Memory.Scopes.Contains(facts.Scope) ? $"The scope is one of: {string.Join(", ", Memory.Scopes)}."
        : !Memory.Kinds.Contains(facts.Kind) ? $"The kind is one of: {string.Join(", ", Memory.Kinds)}."
        : facts.Scope == Memory.Project && string.IsNullOrWhiteSpace(facts.ProjectKey) ? "A project's memory says its project."
        : facts.ProjectKey is { Length: > 300 } ? "A project's key is at most 300 characters."
        : null;

    /// <summary>The person's memory of this scope (and project) with these words already, if any (case and a final full stop aside).</summary>
    public async Task<Memory?> FindAsync(Guid userId, string text, CancellationToken ct, MemoryFacts? facts = null)
    {
        var f = facts ?? new MemoryFacts();
        var key = Key(text);
        return (await Of(userId, f).ToListAsync(ct)).FirstOrDefault(m => Key(m.Text) == key);
    }

    /// <summary>Memories of this scope and project: the person's own, or the company's.</summary>
    private IQueryable<Memory> Of(Guid userId, MemoryFacts f) =>
        db.Memories.Where(m => m.ReplacedBy == null && !m.Proposed && m.Scope == f.Scope && (f.Scope == Memory.Company || (m.UserId == userId && m.ProjectKey == f.ProjectKey)));

    public Task<Memory?> KeepAsync(Guid userId, string text, CancellationToken ct) => KeepAsync(userId, text, new MemoryFacts(), ct);

    /// <summary>
    /// Keeps a memory: a new one; or the same words said again, or words that mean the same as one kept (that one, in the
    /// newer words, comes first again). Null when there is no room (<see cref="PerPerson"/>, <see cref="CompanyMost"/>).
    /// </summary>
    public async Task<Memory?> KeepAsync(Guid userId, string text, MemoryFacts facts, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (await FindAsync(userId, text, ct, facts) is { } same)
        {
            if (!facts.Proposed)
            {
                Touch(same, facts, now);
                await db.SaveChangesAsync(ct);
            }
            return same;
        }
        var vector = await DocumentVectorAsync(text, ct);
        if (vector is not null)
        {
            var near = (await Of(userId, facts).Where(m => m.Embedding != null).ToListAsync(ct))
                .Where(m => m.Embedding!.Length == vector.Length).Select(m => (Memory: m, Score: Dot(vector, m.Embedding!))).Where(x => x.Score >= Same)
                .OrderByDescending(x => x.Score).FirstOrDefault();
            if (near.Memory is { } alike)
            {
                // Said again in other words: the newer words stand (a proposal changes nothing the person kept).
                if (!facts.Proposed)
                {
                    (alike.Text, alike.Embedding) = (text, vector);
                    Touch(alike, facts, now);
                    await db.SaveChangesAsync(ct);
                }
                return alike;
            }
        }
        if (facts.Proposed && (await db.Memories.Where(m => m.UserId == userId && m.Proposed).ToListAsync(ct)).FirstOrDefault(m => Key(m.Text) == Key(text)) is { } waiting)
        {
            // Proposed already, and still waiting.
            return waiting;
        }
        var full = facts.Scope == Memory.Company
            ? await db.Memories.CountAsync(m => m.Scope == Memory.Company && m.ReplacedBy == null, ct) >= CompanyMost
            : await db.Memories.CountAsync(m => m.UserId == userId && m.Scope != Memory.Company && m.ReplacedBy == null, ct) >= PerPerson;
        if (full)
        {
            return null;
        }
        var made = new Memory
        {
            UserId = userId, Text = text, Scope = facts.Scope, Kind = facts.Kind, ProjectKey = facts.Scope == Memory.Project ? facts.ProjectKey : null,
            Source = facts.Source, SourceId = facts.SourceId, Pinned = facts.Pinned, Embedding = vector, CreatedAt = now, UpdatedAt = now,
            Proposed = facts.Proposed, Replaces = facts.Proposed ? facts.Replaces : null,
        };
        db.Memories.Add(made);
        if (!facts.Proposed && facts.Replaces is { } old)
        {
            await ReplaceAsync(made, old, ct);
        }
        await db.SaveChangesAsync(ct);
        return made;
    }

    /// <summary><paramref name="newer"/> says otherwise than the person's memory <paramref name="old"/>: that one is kept, replaced (no longer read).</summary>
    public async Task ReplaceAsync(Memory newer, Guid old, CancellationToken ct)
    {
        if (old != newer.Id && await db.Memories.SingleOrDefaultAsync(m => m.Id == old && m.UserId == newer.UserId && m.ReplacedBy == null, ct) is { } replaced)
        {
            replaced.ReplacedBy = newer.Id;
        }
    }

    /// <summary>A proposal kept: read from now on, and what it replaces replaced. False when it was not one of the person's proposals.</summary>
    public async Task<Memory?> AcceptAsync(Guid userId, Guid id, CancellationToken ct)
    {
        if (await db.Memories.SingleOrDefaultAsync(m => m.Id == id && m.UserId == userId && m.Proposed, ct) is not { } proposal)
        {
            return null;
        }
        (proposal.Proposed, proposal.UpdatedAt) = (false, DateTimeOffset.UtcNow);
        if (proposal.Replaces is { } old)
        {
            await ReplaceAsync(proposal, old, ct);
        }
        await db.SaveChangesAsync(ct);
        return proposal;
    }

    private static void Touch(Memory m, MemoryFacts facts, DateTimeOffset now)
    {
        m.UpdatedAt = now;
        m.Pinned |= facts.Pinned;
        // A kind said now (not the default) is the newer word on it.
        if (facts.Kind != Memory.Fact)
        {
            m.Kind = facts.Kind;
        }
    }

    /// <summary>The words changed: a new vector for them (none when the embedder does not answer: recall reads its words then).</summary>
    public async Task ReembedAsync(Memory m, CancellationToken ct) => m.Embedding = await DocumentVectorAsync(m.Text, ct);

    /// <summary>Memories with no vector yet (kept while the embedder was away, or before there was one), given one; how many.</summary>
    public async Task<int> EmbedMissingAsync(int most, CancellationToken ct)
    {
        if (!await embedder.ReadyAsync(ct))
        {
            return 0;
        }
        var missing = await db.Memories.Where(m => m.Embedding == null && m.ReplacedBy == null).OrderBy(m => m.CreatedAt).Take(most).ToListAsync(ct);
        if (missing.Count == 0)
        {
            return 0;
        }
        try
        {
            var vectors = await embedder.DocumentsAsync([.. missing.Select(m => m.Text)], ct);
            for (var i = 0; i < missing.Count; i++)
            {
                missing[i].Embedding = Unit(vectors[i]);
            }
            await db.SaveChangesAsync(ct);
            return missing.Count;
        }
        catch (Exception ex) when (ex is Knowledge.EmbedderException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogEmbed(logger, ex.Message);
            return 0;
        }
    }

    internal async Task<float[]?> DocumentVectorAsync(string text, CancellationToken ct)
    {
        try
        {
            return await embedder.ReadyAsync(ct) ? Unit((await embedder.DocumentsAsync([text], ct))[0]) : null;
        }
        catch (Exception ex) when (ex is Knowledge.EmbedderException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogEmbed(logger, ex.Message);
            return null;
        }
    }

    internal async Task<float[]?> QueryVectorAsync(string text, CancellationToken ct)
    {
        try
        {
            return await embedder.ReadyAsync(ct) ? Unit(await embedder.QueryAsync(text.Length > 2_000 ? text[^2_000..] : text, ct)) : null;
        }
        catch (Exception ex) when (ex is Knowledge.EmbedderException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogEmbed(logger, ex.Message);
            return null;
        }
    }

    private static float[] Unit(float[] v)
    {
        var length = Math.Sqrt(v.Sum(x => (double)x * x));
        return length is 0 or double.NaN ? v : [.. v.Select(x => (float)(x / length))];
    }

    internal static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += (double)a[i] * b[i];
        }
        return sum;
    }

    /// <summary>The words that carry meaning (three letters or more, the commonest left out), in lower case.</summary>
    internal static HashSet<string> Words(string text) =>
        [.. WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length >= 3 && !Common.Contains(w))];

    /// <summary>How alike two sets of words are: the shared ones over the geometric mean of their sizes.</summary>
    internal static double WordScore(HashSet<string> a, HashSet<string> b) =>
        a.Count == 0 || b.Count == 0 ? 0 : a.Count(b.Contains) / Math.Sqrt((double)a.Count * b.Count);

    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "from", "what", "which", "how", "are", "was", "were", "has", "have", "its", "into", "about",
        "when", "where", "who", "why", "does", "did", "can", "not", "but", "you", "your", "they", "their", "there", "our", "all", "use", "uses",
        "please", "want", "would", "could", "should", "will", "just", "any", "some", "one",
    };

    private static string Key(string text) => text.Trim().TrimEnd('.').ToUpperInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[\p{L}\p{N}_-]+")]
    private static partial Regex WordPattern();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Memory: the embedder did not answer ({Reason}); memories are compared by their words meanwhile")]
    private static partial void LogEmbed(ILogger logger, string reason);
}
