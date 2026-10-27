using Llm.Core.Access;
using Llm.Core.Compliance;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Compliance;

public sealed class PolicyException(string message) : Exception(message);

public sealed record PolicyTemplate(string Key, string Title);

public sealed record PolicyCreate(string? Template, string? Title, string? Key);

public sealed record PolicySettings(string? Title, string? Owner, Audience Audience, Guid[]? Groups, int ReviewMonths);

public sealed record PolicyDraft(string? Text, string? Changes);

/// <summary>A policy as Admin → Compliance → Policies lists it: the version in force, a draft if any, the review and the acknowledgements.</summary>
public sealed record PolicyView(Guid Id, string Key, string Title, string? Owner, Audience Audience, Guid[] Groups, int ReviewMonths, int? Version,
    DateTimeOffset? ApprovedAt, string? ApprovedBy, DateTimeOffset? ReviewDue, bool ReviewOverdue, int? Draft, int Acknowledged, int Readers);

public sealed record PolicyVersionView(Guid Id, int Version, string? Changes, string CreatedBy, DateTimeOffset CreatedAt, string? ApprovedBy,
    DateTimeOffset? ApprovedAt, DateTimeOffset? ReviewDue, string? ReviewedBy, DateTimeOffset? ReviewedAt);

/// <summary>A policy a person must read: its version in force, and whether they said they did.</summary>
public sealed record PolicyToRead(Guid VersionId, string Key, string Title, int Version, DateTimeOffset ApprovedAt, DateTimeOffset? AcknowledgedAt);

/// <summary>
/// The company's policies, governed (Admin → Compliance → Policies): each from one of the templates in docs/compliance or
/// written here, with an owner, the people who must read it, versions approved by a person, a review date its period
/// sets, and each reader's acknowledgement of the version in force. Every step is audited.
/// </summary>
public sealed class Policies(AppDbContext db, Access.AccessService access, Identity.Audit audit, TimeProvider clock)
{
    private static readonly Lazy<IReadOnlyList<(PolicyTemplate Template, string Text)>> Templates = new(() =>
    {
        var asm = typeof(Policies).Assembly;
        return [.. asm.GetManifestResourceNames().Where(n => n.StartsWith("policy-templates/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Select(n =>
        {
            using var reader = new StreamReader(asm.GetManifestResourceStream(n)!);
            var text = reader.ReadToEnd();
            var title = text.Split('\n').FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim() ?? n;
            return (new PolicyTemplate(Path.GetFileNameWithoutExtension(n), title), text);
        })];
    });

    /// <summary>The policy templates of docs/compliance, built into the app.</summary>
    public static IReadOnlyList<PolicyTemplate> TemplateList => [.. Templates.Value.Select(t => t.Template)];

    public async Task<IReadOnlyList<PolicyView>> ListAsync(CancellationToken ct)
    {
        var policies = await db.Policies.AsNoTracking().OrderBy(p => p.Title).ToListAsync(ct);
        var versions = (await db.PolicyVersions.AsNoTracking().Select(v => new { v.Id, v.PolicyId, v.Version, v.ApprovedAt, v.ApprovedBy, v.ReviewDue }).ToListAsync(ct))
            .ToLookup(v => v.PolicyId);
        var readers = await ReadersAsync(policies, ct);
        var current = policies.Select(p => versions[p.Id].Where(v => v.ApprovedAt is not null).MaxBy(v => v.Version)?.Id).OfType<Guid>().ToList();
        var acks = await db.PolicyAcknowledgements.AsNoTracking().Where(a => current.Contains(a.PolicyVersionId))
            .GroupBy(a => a.PolicyVersionId).Select(g => new { g.Key, Users = g.Select(a => a.UserId).ToList() }).ToDictionaryAsync(g => g.Key, g => g.Users, ct);
        var now = clock.GetUtcNow();
        return [.. policies.Select(p =>
        {
            var inForce = versions[p.Id].Where(v => v.ApprovedAt is not null).MaxBy(v => v.Version);
            var draft = versions[p.Id].Where(v => v.ApprovedAt is null).MaxBy(v => v.Version);
            var who = readers[p.Id];
            var read = inForce is null ? 0 : (acks.GetValueOrDefault(inForce.Id) ?? []).Count(who.Contains);
            return new PolicyView(p.Id, p.Key, p.Title, p.Owner, p.Audience, p.Groups, p.ReviewMonths, inForce?.Version, inForce?.ApprovedAt, inForce?.ApprovedBy,
                inForce?.ReviewDue, inForce?.ReviewDue < now, draft?.Version, read, who.Count);
        })];
    }

    public async Task<(PolicyView Policy, List<PolicyVersionView> Versions, List<string> Pending)> GetAsync(Guid id, CancellationToken ct)
    {
        var view = (await ListAsync(ct)).SingleOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException();
        var versions = await db.PolicyVersions.AsNoTracking().Where(v => v.PolicyId == id).OrderByDescending(v => v.Version)
            .Select(v => new PolicyVersionView(v.Id, v.Version, v.Changes, v.CreatedBy, v.CreatedAt, v.ApprovedBy, v.ApprovedAt, v.ReviewDue, v.ReviewedBy, v.ReviewedAt))
            .ToListAsync(ct);
        var pending = new List<string>();
        if (versions.FirstOrDefault(v => v.ApprovedAt is not null) is { } inForce)
        {
            var policy = await db.Policies.AsNoTracking().SingleAsync(p => p.Id == id, ct);
            var who = (await ReadersAsync([policy], ct))[id];
            var read = await db.PolicyAcknowledgements.AsNoTracking().Where(a => a.PolicyVersionId == inForce.Id).Select(a => a.UserId).ToListAsync(ct);
            var waiting = who.Except(read).ToList();
            pending = await db.Users.AsNoTracking().Where(u => waiting.Contains(u.Id)).OrderBy(u => u.UserName).Select(u => u.UserName!).Take(500).ToListAsync(ct);
        }
        return (view, versions, pending);
    }

    public async Task<string> TextAsync(Guid id, int version, CancellationToken ct) =>
        await db.PolicyVersions.AsNoTracking().Where(v => v.PolicyId == id && v.Version == version).Select(v => v.Text).SingleOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException();

    /// <summary>A new policy, from a template (its text as the first draft) or blank.</summary>
    public async Task<PolicyView> CreateAsync(PolicyCreate input, string by, CancellationToken ct)
    {
        var template = input.Template is { Length: > 0 } t ? Templates.Value.FirstOrDefault(x => x.Template.Key == t) : default;
        if (input.Template is { Length: > 0 } && template.Text is null)
        {
            throw new PolicyException($"No template {input.Template}.");
        }
        var title = Text(input.Title, 200) ?? template.Template?.Title ?? throw new PolicyException("Name the policy.");
        var key = (input.Key ?? template.Template?.Key ?? Slug(title)).Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z0-9][a-z0-9-]{0,63}$"))
        {
            throw new PolicyException("Its short name is lowercase letters, digits and -.");
        }
        if (await db.Policies.AnyAsync(p => p.Key == key, ct))
        {
            throw new PolicyException($"There is a policy {key} already.");
        }
        var now = clock.GetUtcNow();
        var policy = new Policy { Key = key, Title = title, CreatedBy = by, CreatedAt = now };
        db.Policies.Add(policy);
        db.PolicyVersions.Add(new PolicyVersion
        {
            PolicyId = policy.Id, Version = 1, Text = template.Text ?? $"# {title}\n", Changes = template.Text is null ? null : "From the template.", CreatedBy = by, CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.policy.create", key, detail: template.Text is null ? title : $"{title}, from the template");
        return (await ListAsync(ct)).Single(p => p.Id == policy.Id);
    }

    public async Task<PolicyView> SetAsync(Guid id, PolicySettings input, string by, CancellationToken ct)
    {
        var policy = await db.Policies.SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new KeyNotFoundException();
        policy.Title = Text(input.Title, 200) ?? throw new PolicyException("Name the policy.");
        policy.Owner = Text(input.Owner, 256);
        policy.ReviewMonths = input.ReviewMonths is >= 1 and <= 36 ? input.ReviewMonths : throw new PolicyException("It is reviewed every 1 to 36 months.");
        var groups = (input.Groups ?? []).Distinct().ToArray();
        if (input.Audience == Audience.Groups && groups.Length == 0)
        {
            throw new PolicyException("Name the groups whose members must read it.");
        }
        if (groups.Length > 0 && await db.Groups.CountAsync(g => groups.Contains(g.Id), ct) != groups.Length)
        {
            throw new PolicyException("A group named is not there.");
        }
        (policy.Audience, policy.Groups) = (input.Audience, input.Audience == Audience.Groups ? groups : []);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.policy.settings", policy.Key, detail: $"owner {policy.Owner ?? "-"}, read by {policy.Audience}, reviewed every {policy.ReviewMonths} months");
        return (await ListAsync(ct)).Single(p => p.Id == id);
    }

    /// <summary>The draft's text: the draft there, or a new one from the version in force.</summary>
    public async Task<PolicyVersionView> DraftAsync(Guid id, PolicyDraft input, string by, CancellationToken ct)
    {
        var policy = await db.Policies.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new KeyNotFoundException();
        var text = input.Text?.Trim() is { Length: > 0 } s ? s : throw new PolicyException("A policy has its text.");
        if (text.Length > 200_000)
        {
            throw new PolicyException("200,000 characters at most.");
        }
        var latest = await db.PolicyVersions.Where(v => v.PolicyId == id).OrderByDescending(v => v.Version).FirstAsync(ct);
        var draft = latest.ApprovedAt is null ? latest : null;
        if (draft is null)
        {
            draft = new PolicyVersion { PolicyId = id, Version = latest.Version + 1, Text = text, CreatedBy = by, CreatedAt = clock.GetUtcNow() };
            db.PolicyVersions.Add(draft);
        }
        (draft.Text, draft.Changes) = (text, Text(input.Changes, 2000));
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.policy.draft", policy.Key, detail: $"version {draft.Version}{(draft.Changes is null ? "" : $": {draft.Changes}")}");
        return View(draft);
    }

    /// <summary>The draft approved, in force from now: its review date set, and its readers told.</summary>
    public async Task<PolicyVersionView> ApproveAsync(Guid id, string by, Guid byId, Notifications.Notifier notifier, CancellationToken ct)
    {
        var policy = await db.Policies.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new KeyNotFoundException();
        var draft = await db.PolicyVersions.Where(v => v.PolicyId == id && v.ApprovedAt == null).OrderByDescending(v => v.Version).FirstOrDefaultAsync(ct)
            ?? throw new PolicyException("There is no draft to approve: write one first.");
        if (draft.Version > 1 && draft.Changes is null)
        {
            throw new PolicyException("Say what changed from the version before, in the draft.");
        }
        var now = clock.GetUtcNow();
        (draft.ApprovedBy, draft.ApprovedAt, draft.ReviewDue) = (by, now, now.AddMonths(policy.ReviewMonths));
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.policy.approve", policy.Key, detail: $"version {draft.Version}, review by {draft.ReviewDue:yyyy-MM-dd}");
        await notifier.SendManyAsync([.. (await ReadersAsync([policy], ct))[id].Where(u => u != byId)], new Notifications.News("review", $"A policy to read: {policy.Title}",
            $"Version {draft.Version} is in force. Read it and say so under Your account.", "/account#policies", $"policy:{draft.Id}"), ct);
        return View(draft);
    }

    /// <summary>Reviewed and kept as it is: its next review set from today.</summary>
    public async Task<PolicyVersionView> ReviewAsync(Guid id, string by, CancellationToken ct)
    {
        var policy = await db.Policies.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new KeyNotFoundException();
        var inForce = await db.PolicyVersions.Where(v => v.PolicyId == id && v.ApprovedAt != null).OrderByDescending(v => v.Version).FirstOrDefaultAsync(ct)
            ?? throw new PolicyException("No version is in force yet: approve one first.");
        var now = clock.GetUtcNow();
        (inForce.ReviewedBy, inForce.ReviewedAt, inForce.ReviewDue) = (by, now, now.AddMonths(policy.ReviewMonths));
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.policy.review", policy.Key, detail: $"version {inForce.Version} kept, next review {inForce.ReviewDue:yyyy-MM-dd}");
        return View(inForce);
    }

    /// <summary>The policies a person must read: each one's version in force, and when they said they read it.</summary>
    public async Task<IReadOnlyList<PolicyToRead>> ForAsync(AppUser user, CancellationToken ct)
    {
        var policies = await db.Policies.AsNoTracking().ToListAsync(ct);
        var mine = await ReadersAsync(policies, ct, user);
        var ids = policies.Where(p => mine[p.Id].Contains(user.Id)).Select(p => p.Id).ToList();
        var inForce = (await db.PolicyVersions.AsNoTracking().Where(v => ids.Contains(v.PolicyId) && v.ApprovedAt != null).ToListAsync(ct))
            .GroupBy(v => v.PolicyId).Select(g => g.MaxBy(v => v.Version)!).ToList();
        var versionIds = inForce.Select(v => v.Id).ToList();
        var acks = await db.PolicyAcknowledgements.AsNoTracking().Where(a => a.UserId == user.Id && versionIds.Contains(a.PolicyVersionId)).ToDictionaryAsync(a => a.PolicyVersionId, a => a.At, ct);
        return [.. inForce.Join(policies, v => v.PolicyId, p => p.Id, (v, p) => new PolicyToRead(v.Id, p.Key, p.Title, v.Version, v.ApprovedAt!.Value, acks.TryGetValue(v.Id, out var at) ? at : null))
            .OrderBy(p => p.AcknowledgedAt is not null).ThenBy(p => p.Title)];
    }

    public async Task<string> ReadTextAsync(AppUser user, Guid versionId, CancellationToken ct) =>
        (await ForAsync(user, ct)).Any(p => p.VersionId == versionId)
            ? await db.PolicyVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => v.Text).SingleAsync(ct)
            : throw new KeyNotFoundException();

    public async Task<PolicyToRead> AcknowledgeAsync(AppUser user, Guid versionId, CancellationToken ct)
    {
        var mine = (await ForAsync(user, ct)).SingleOrDefault(p => p.VersionId == versionId) ?? throw new KeyNotFoundException();
        if (mine.AcknowledgedAt is not null)
        {
            return mine;
        }
        var at = clock.GetUtcNow();
        db.PolicyAcknowledgements.Add(new PolicyAcknowledgement { PolicyVersionId = versionId, UserId = user.Id, At = at });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Said twice at once: once is enough.
        }
        await audit.WriteAsync("compliance.policy.acknowledge", mine.Key, detail: $"version {mine.Version}");
        return mine with { AcknowledgedAt = at };
    }

    /// <summary>Who must read each policy: the people who can sign in, the admins among them, or its groups' members.</summary>
    private async Task<Dictionary<Guid, HashSet<Guid>>> ReadersAsync(IReadOnlyCollection<Policy> policies, CancellationToken ct, AppUser? only = null)
    {
        var people = only is null ? await db.Users.AsNoTracking().Where(u => !u.IsDisabled).ToListAsync(ct) : [only];
        var all = people.Select(u => u.Id).ToHashSet();
        HashSet<Guid>? admins = null;
        if (policies.Any(p => p.Audience == Audience.Admins))
        {
            var memberships = await access.MembershipsAsync(people, ct);
            admins = [.. people.Where(u => memberships[u.Id] is var m && (m.IsOwner || Permissions.AdminKeys.Any(m.Has))).Select(u => u.Id)];
        }
        var groupIds = policies.SelectMany(p => p.Groups).Distinct().ToList();
        var members = (groupIds.Count == 0 ? [] : await db.GroupMembers.AsNoTracking().Where(m => groupIds.Contains(m.GroupId)).ToListAsync(ct))
            .ToLookup(m => m.GroupId, m => m.UserId);
        return policies.ToDictionary(p => p.Id, p => p.Audience switch
        {
            Audience.Admins => admins!,
            Audience.Groups => p.Groups.SelectMany(g => members[g]).Where(all.Contains).ToHashSet(),
            _ => all,
        });
    }

    private static PolicyVersionView View(PolicyVersion v) =>
        new(v.Id, v.Version, v.Changes, v.CreatedBy, v.CreatedAt, v.ApprovedBy, v.ApprovedAt, v.ReviewDue, v.ReviewedBy, v.ReviewedAt);

    private static string Slug(string title) =>
        System.Text.RegularExpressions.Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } s ? s[..Math.Min(s.Length, 64)].TrimEnd('-') : "policy";

    private static string? Text(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }
        s = s.Trim();
        return s.Length <= max ? s : throw new PolicyException($"{max} characters at most.");
    }
}
