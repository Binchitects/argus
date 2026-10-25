using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>A skill proposed from a chat: its slash name, title, when to use it, and its instructions.</summary>
public sealed record LearnedSkill(string Name, string Title, string Description, string Instructions);

/// <summary>
/// Skills that learn: from a chat or a Code Arena session that worked out a way of doing a recurring task, a skill is
/// learned; when one used there went wrong or the person corrected it, a better version is proposed. Each waits for the
/// person (Skills → Learned from your work), unless they keep what is learned on their own; each applied one keeps what the
/// skill was, so Undo puts it back. One the person may not edit (the company's, Arena's own) gets better as theirs: a skill
/// of theirs with its name, which wins over it.
/// </summary>
public static partial class SkillLearning
{
    /// <summary>The longest instructions a learned skill or a better version may have.</summary>
    public const int MostInstructions = 8_000;

    [GeneratedRegex("^[a-z][a-z0-9-]{1,39}$")]
    private static partial Regex SlashName();

    /// <summary>A skill as a change keeps it: what Undo puts back.</summary>
    public static string Snapshot(Skill s) => new JsonObject
    {
        ["title"] = s.Title, ["description"] = s.Description, ["instructions"] = s.Instructions, ["parameters"] = s.Parameters,
    }.ToJsonString();

    private static void Restore(Skill s, string snapshot)
    {
        var j = JsonNode.Parse(snapshot)!;
        s.Title = j["title"]!.GetValue<string>();
        s.Description = j["description"]!.GetValue<string>();
        s.Instructions = j["instructions"]!.GetValue<string>();
        s.Parameters = j["parameters"]?.GetValue<string>() ?? "[]";
    }

    /// <summary>Why a proposed skill cannot be one, or null.</summary>
    public static string? Problem(LearnedSkill s) =>
        !SlashName().IsMatch(s.Name) ? "a slash name of lowercase letters, digits and dashes"
        : s.Title.Trim().Length is 0 or > 100 ? "a title of at most 100 characters"
        : s.Instructions.Trim().Length is < 40 or > MostInstructions ? $"instructions of 40 to {MostInstructions:N0} characters"
        : null;

    /// <summary>
    /// A skill learned from a chat: the person's, waiting for them unless <paramref name="keep"/>. Null when it is not one, or
    /// the person has a skill of that name already.
    /// </summary>
    public static async Task<Skill?> LearnAsync(AppDbContext db, Guid userId, LearnedSkill learned, Guid? chat, string reason, bool keep, CancellationToken ct)
    {
        if (Problem(learned) is not null || await db.Skills.AnyAsync(x => x.UserId == userId && x.Name == learned.Name, ct))
        {
            return null;
        }
        var now = DateTimeOffset.UtcNow;
        var skill = new Skill
        {
            UserId = userId, Name = learned.Name, Title = learned.Title.Trim(), Description = learned.Description.Trim()[..Math.Min(1000, learned.Description.Trim().Length)],
            Instructions = learned.Instructions.Trim(), Source = Skill.Learned, SourceId = chat, Proposed = !keep, AutoUse = true,
        };
        db.Skills.Add(skill);
        db.SkillChanges.Add(new SkillChange
        {
            SkillId = skill.Id, UserId = userId, Action = SkillChange.LearnedAction, Reason = reason, After = Snapshot(skill), SourceId = chat, Proposed = !keep,
            AppliedAt = keep ? now : null,
        });
        return skill;
    }

    /// <summary>
    /// A better version of a skill used in a chat: of the person's own, a change to it (applied at once with
    /// <paramref name="keep"/>); of one they may not edit, a skill of theirs with its name. Null when nothing changes.
    /// </summary>
    public static async Task<SkillChange?> ImproveAsync(AppDbContext db, Guid userId, Skill used, string instructions, string why, Guid? chat, bool keep, CancellationToken ct)
    {
        instructions = instructions.Trim();
        if (instructions.Length is < 40 or > MostInstructions || instructions == used.Instructions.Trim())
        {
            return null;
        }
        if (used.UserId != userId || used.BuiltIn || used.ServerId is not null)
        {
            var mine = await LearnAsync(db, userId, new LearnedSkill(used.Name, used.Title, used.Description, instructions), chat, $"A better version of /{used.Name}, yours: {why}", keep, ct);
            return mine is null ? null : db.SkillChanges.Local.Single(c => c.SkillId == mine.Id);
        }
        // One proposal at a time for a skill: a newer one takes the older one's place.
        db.SkillChanges.RemoveRange(await db.SkillChanges.Where(c => c.SkillId == used.Id && c.Proposed).ToListAsync(ct));
        var tracked = await db.Skills.SingleAsync(x => x.Id == used.Id, ct);
        var after = new Skill { Name = tracked.Name, Title = tracked.Title, Description = tracked.Description, Instructions = instructions, Parameters = tracked.Parameters };
        var change = new SkillChange
        {
            SkillId = tracked.Id, UserId = userId, Action = SkillChange.Improved, Reason = why, Before = Snapshot(tracked), After = Snapshot(after), SourceId = chat, Proposed = !keep,
        };
        db.SkillChanges.Add(change);
        if (keep)
        {
            Apply(tracked, change);
        }
        return change;
    }

    private static void Apply(Skill skill, SkillChange change)
    {
        if (change.Action == SkillChange.Improved)
        {
            Restore(skill, change.After);
            skill.Version++;
        }
        skill.Proposed = false;
        skill.UpdatedAt = DateTimeOffset.UtcNow;
        (change.Proposed, change.AppliedAt) = (false, DateTimeOffset.UtcNow);
    }

    /// <summary>The person keeps what was proposed: the skill learned, or the better version; false when there is no such proposal of theirs.</summary>
    public static async Task<bool> ApplyAsync(AppDbContext db, Guid userId, Guid changeId, CancellationToken ct)
    {
        if (await db.SkillChanges.SingleOrDefaultAsync(c => c.Id == changeId && c.UserId == userId && c.Proposed, ct) is not { } change
            || await db.Skills.SingleOrDefaultAsync(s => s.Id == change.SkillId, ct) is not { } skill)
        {
            return false;
        }
        Apply(skill, change);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Not now: a skill learned goes; a better version is forgotten. False when there is no such proposal of theirs.</summary>
    public static async Task<bool> DismissAsync(AppDbContext db, Guid userId, Guid changeId, CancellationToken ct)
    {
        if (await db.SkillChanges.SingleOrDefaultAsync(c => c.Id == changeId && c.UserId == userId && c.Proposed, ct) is not { } change)
        {
            return false;
        }
        if (change.Action == SkillChange.LearnedAction)
        {
            await db.Skills.Where(s => s.Id == change.SkillId && s.Proposed).ExecuteDeleteAsync(ct);
        }
        else
        {
            db.SkillChanges.Remove(change);
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    /// <summary>Puts a skill back as it was before a better version was applied (a version more); false when there is no such change of theirs.</summary>
    public static async Task<bool> UndoAsync(AppDbContext db, Guid userId, Guid changeId, CancellationToken ct)
    {
        if (await db.SkillChanges.SingleOrDefaultAsync(c => c.Id == changeId && c.UserId == userId && c.Action == SkillChange.Improved && c.AppliedAt != null && c.UndoneAt == null, ct)
                is not { Before: { } before } change
            || await db.Skills.SingleOrDefaultAsync(s => s.Id == change.SkillId, ct) is not { } skill)
        {
            return false;
        }
        Restore(skill, before);
        skill.Version++;
        skill.UpdatedAt = DateTimeOffset.UtcNow;
        change.UndoneAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>A change as the Skills page shows it: what it did and why, the skill before and after, and its state.</summary>
    public static object View(SkillChange c) => new
    {
        c.Id, c.SkillId, c.Action, c.Reason, c.Proposed, c.At, c.AppliedAt, c.UndoneAt, c.SourceId,
        before = c.Before is null ? null : JsonNode.Parse(c.Before)?["instructions"]?.GetValue<string>(),
        after = JsonNode.Parse(c.After)?["instructions"]?.GetValue<string>(),
    };
}
