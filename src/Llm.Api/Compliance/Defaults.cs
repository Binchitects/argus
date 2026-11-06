using Llm.Api.Identity;
using Llm.Api.Settings;
using Llm.Core.Access;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Compliance;

/// <summary>A step Admin → Compliance offers beside a check that does not hold: done in one click, audited.</summary>
/// <param name="Key">The check's id: POST /api/admin/compliance/fixes/{key} takes the step.</param>
/// <param name="Label">The button's words.</param>
/// <param name="Does">What it changes, said before the click.</param>
public sealed record ComplianceFix(string Key, string Label, string Does);

public sealed class ComplianceFixException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Compliance's defaults. The starter, which a new installation gets at its first start (Compliance:Starter): the safeguard rules
/// that keep personal and health data from models on other providers, the policy templates as drafts for a person to approve,
/// chats kept a year, and the services Arena connects to in the supplier register. And the steps Admin → Compliance offers
/// beside each check that does not hold, each in one click: what a person decides stays theirs (approving a policy, assessing
/// a risk); a step that changes a setting or a rule needs the permission of what it changes.
/// </summary>
public sealed class ComplianceDefaults(AppDbContext db, SettingsService settings, Policies policies, Suppliers suppliers, Risks risks, IServiceProvider services,
    AccessReviews reviews, Access.AccessService access, Audit audit, IOptionsMonitor<AuthOptions> auth, IOptionsMonitor<Retention.RetentionOptions> retention,
    IOptionsMonitor<NoticeOptions> notice)
{
    /// <summary>The safeguard templates that keep personal and health data from models on other providers (the personal-data check).</summary>
    public static readonly string[] Rules = ["Personal data to cloud models", "Health and identity data to cloud models"];

    /// <summary>Days chats are kept when the company has not said: a year.</summary>
    public const int ChatDays = 365;

    /// <summary>The sign-in notice to start from (docs/compliance/templates/sign-in-notice.md): acceptable use and privacy, in short.</summary>
    public static readonly Lazy<string> NoticeText = new(() =>
    {
        using var stream = typeof(ComplianceDefaults).Assembly.GetManifestResourceStream("compliance-notice.md")
            ?? throw new InvalidOperationException("The notice template is not built into the app.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    });

    /// <summary>The security objectives to start from: a check and the share of days it should hold.</summary>
    public static readonly IReadOnlyList<ObjectiveInput> Objectives =
    [
        new("Backups end well every day", "backups", 95, null),
        new("The audit log stays whole", "audit-chain", 100, null),
        new("Admins sign in with two factors", "two-factor", 100, null),
        new("Personal and health data stay from cloud models", "personal-data", 100, null),
        new("Known vulnerabilities are fixed in time", "vulnerabilities", 90, null),
    ];

    /// <summary>The risks most companies running Arena have, to assess: each with the controls that treat it.</summary>
    public static readonly IReadOnlyList<RiskInput> CommonRisks =
    [
        new("Personal or health data reaches a model it should not", "Someone puts customers' or patients' data into a chat with a model on another provider.", "privacy",
            ["chats", "people's files"], "Careless or uninformed use", 3, 4, null, Llm.Core.Compliance.Risk.Reduce, ["DAT-04", "DAT-08", "GOV-02"], null, null, null),
        new("An admin account is taken over", "A guessed, phished or reused password gives someone the whole service.", "security",
            ["admin accounts"], "Phishing, password reuse", 2, 5, null, Llm.Core.Compliance.Risk.Reduce, ["IAM-01", "IAM-03", "IAM-07"], null, null, null),
        new("Instructions hidden in a document or a page steer an agent", "A tool result, a web page or an attached file carries instructions the model follows.", "ai",
            ["agents", "chats"], "Prompt injection", 3, 3, null, Llm.Core.Compliance.Risk.Reduce, ["DAT-04", "OPS-02"], null, null, null),
        new("A wrong answer is acted on", "People take what a model says as fact, in a decision about someone or in code that ships.", "ai",
            ["answers"], "Model error", 3, 3, null, Llm.Core.Compliance.Risk.Reduce, ["GOV-09", "GOV-10"], null, null, null),
        new("The host or its disks are lost", "A failed disk, a fire or ransomware takes the service and its data.", "continuity",
            ["the host", "the database"], "Hardware failure, ransomware", 2, 5, null, Llm.Core.Compliance.Risk.Reduce, ["OPS-01"], null, null, null),
        new("A known vulnerability is exploited", "A release's dependency or the company's own code has a published vulnerability someone uses.", "security",
            ["the stack", "repositories"], "Known vulnerabilities", 2, 4, null, Llm.Core.Compliance.Risk.Reduce, ["GOV-03", "VUL-01"], null, null, null),
        new("Someone changes the audit log to hide what they did", "An insider with database access removes or edits entries.", "security",
            ["the audit log"], "Insider", 1, 4, null, Llm.Core.Compliance.Risk.Reduce, ["LOG-03", "LOG-04"], null, null, null),
    ];

    /// <summary>The step offered beside a check that does not hold, when there is one to take here.</summary>
    public async Task<ComplianceFix?> OfferAsync(string check, CancellationToken ct) => check switch
    {
        "two-factor" when auth.CurrentValue.TwoFactorRequired == TwoFactorPolicy.Off =>
            new(check, "Require it of admins", "Settings → Sign-in → Require two-factor sign-in becomes admins: each admin without a second factor sets one up at their next step in the app, you first."),
        "chat-retention" when retention.CurrentValue.Days is not > 0 =>
            new(check, "Keep chats a year", $"Settings → Data retention → Keep chats for becomes {ChatDays} days: a chat and its files are deleted a year after its last message (people on legal hold keep everything). Change it there if the company keeps them for another time."),
        "notice" when !notice.CurrentValue.On =>
            new(check, "Use the default notice", "Settings → Sign-in notice gets a short acceptable use and privacy notice to start from: everyone accepts it at their next step in the app. Change its words there, and its version when they change in substance."),
        "personal-data" when await MissingRulesAsync(ct) is { Count: > 0 } =>
            new(check, "Add the two rules", $"Adds the safeguard rules {string.Join(" and ", Rules.Select(r => $"\"{r}\""))}, enforced: questions to models on other providers have personal data masked and health and identity numbers refused. The company's own models are not affected."),
        "policies" when await TemplatesLeftAsync(ct) is { Count: > 0 } and var left =>
            new(check, "Adopt the templates as drafts", $"Adds {left.Count} polic{(left.Count == 1 ? "y" : "ies")} from the templates as drafts: {string.Join(", ", left.Select(t => t.Title))}. Each is in force once a person fills in its brackets and approves it."),
        "suppliers" when (await suppliers.SuggestAsync(ct)).Count > 0 =>
            new(check, "Add them to the register", "Adds each service Arena connects to and is not in the register yet, with what Arena knows of it; say what data each gets and its agreements, and review it in a year."),
        "risks" when !await db.Risks.AnyAsync(r => r.ClosedAt == null, ct) =>
            new(check, "Start from the common risks", $"Adds {CommonRisks.Count} risks most companies running Arena have, each with the controls that treat it and you as its owner; assess each (likelihood, impact, what is left) and review it."),
        "management" when !await db.SecurityObjectives.AnyAsync(ct) =>
            new(check, "Set the default objectives", $"Sets {Objectives.Count} security objectives, each a check and the share of days it should hold: {string.Join("; ", Objectives.Select(o => $"{o.Title} ({o.Target}%)"))}."),
        "access-review" when !await db.AccessReviews.AnyAsync(r => r.ClosedAt == null, ct) =>
            new(check, "Start a review", "Starts an access review of who holds privileged access and which accounts went unused, for a reviewer to keep or revoke each by its due date."),
        _ => null,
    };

    /// <summary>Takes the step offered for this check, as this person: refused when none is offered now, or they may not change what it changes.</summary>
    public async Task<string> ApplyAsync(string check, AppUser by, CancellationToken ct)
    {
        var fix = await OfferAsync(check, ct) ?? throw new ComplianceFixException("Nothing to do here for that check now.", 404);
        var member = await access.MembershipAsync(by, ct);
        void Needs(string permission)
        {
            if (!member.Has(permission))
            {
                throw new ComplianceFixException($"It needs {permission}.", 403);
            }
        }
        var name = by.UserName ?? "";
        string done;
        switch (check)
        {
            case "two-factor":
                Needs(SettingsCatalog.PermissionOf(SettingsCatalog.ByKey["Auth:TwoFactorRequired"]));
                await settings.SaveAsync([new("Auth:TwoFactorRequired", TwoFactorPolicy.Admins)], ct);
                done = "two-factor sign-in required of admins";
                break;
            case "chat-retention":
                Needs(SettingsCatalog.PermissionOf(SettingsCatalog.ByKey["Retention:Days"]));
                await settings.SaveAsync([new("Retention:Days", ChatDays.ToString(System.Globalization.CultureInfo.InvariantCulture))], ct);
                done = $"chats kept {ChatDays} days";
                break;
            case "notice":
                Needs(SettingsCatalog.PermissionOf(SettingsCatalog.ByKey["Notice:Text"]));
                await settings.SaveAsync([new("Notice:Text", NoticeText.Value)], ct);
                done = $"the default notice, version {notice.CurrentValue.Version}";
                break;
            case "personal-data":
                Needs(Permissions.SafeguardsManage);
                done = $"rules added: {string.Join(", ", await Safeguards.SafeguardEndpoints.AddTemplatesAsync(db, audit, by, Rules, ct))}";
                break;
            case "policies":
                Needs(Permissions.ComplianceManage);
                done = $"drafts: {string.Join(", ", await AdoptAsync(name, ct))}";
                break;
            case "suppliers":
                Needs(Permissions.ComplianceManage);
                done = $"in the register: {string.Join(", ", await RegisterAsync(name, ct))}";
                break;
            case "risks":
                Needs(Permissions.ComplianceManage);
                foreach (var r in CommonRisks)
                {
                    await risks.CreateAsync(r with { Owner = name }, name, ct);
                }
                done = $"{CommonRisks.Count} risks added";
                break;
            case "management":
                Needs(Permissions.ComplianceManage);
                foreach (var o in Objectives)
                {
                    // Governance reads the checks (its inputs), which offer these steps: taken from the scope, not given.
                    await services.GetRequiredService<Governance>().SaveObjectiveAsync(null, o, name, ct);
                }
                done = $"{Objectives.Count} objectives set";
                break;
            case "access-review":
                Needs(Permissions.ComplianceManage);
                done = (await reviews.StartAsync(name, ct)).Title;
                break;
            default:
                throw new ComplianceFixException("Nothing to do here for that check now.", 404);
        }
        await audit.WriteAsync("compliance.fix", check, detail: $"{fix.Label}: {done}", actor: by);
        return done;
    }

    /// <summary>The starter, once, on a new installation's first start (nothing of it is done twice).</summary>
    public async Task StarterAsync(CancellationToken ct)
    {
        await Safeguards.SafeguardEndpoints.AddTemplatesAsync(db, audit, null, Rules, ct);
        await AdoptAsync("Arena's defaults", ct);
        if (retention.CurrentValue.Days is not > 0)
        {
            await settings.SaveAsync([new("Retention:Days", ChatDays.ToString(System.Globalization.CultureInfo.InvariantCulture))], ct);
        }
        await RegisterAsync("Arena's defaults", ct);
        await audit.WriteAsync("compliance.starter", detail: "the default safeguard rules, the policy templates as drafts, chats kept a year, the supplier register");
    }

    private async Task<List<string>> MissingRulesAsync(CancellationToken ct)
    {
        var have = await db.SafeguardRules.AsNoTracking().Select(r => r.Name).ToListAsync(ct);
        return [.. Rules.Except(have, StringComparer.Ordinal)];
    }

    private async Task<List<PolicyTemplate>> TemplatesLeftAsync(CancellationToken ct)
    {
        var keys = (await db.Policies.AsNoTracking().Select(p => p.Key).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        return [.. Policies.TemplateList.Where(t => !keys.Contains(t.Key))];
    }

    private async Task<List<string>> AdoptAsync(string by, CancellationToken ct)
    {
        var titles = new List<string>();
        foreach (var t in await TemplatesLeftAsync(ct))
        {
            titles.Add((await policies.CreateAsync(new PolicyCreate(t.Key, null, null), by, ct)).Title);
        }
        return titles;
    }

    private async Task<List<string>> RegisterAsync(string by, CancellationToken ct)
    {
        var names = new List<string>();
        foreach (var s in await suppliers.SuggestAsync(ct))
        {
            await suppliers.SaveAsync(null, new SupplierInput(s.Name, s.Kind, s.Service, [], null, null, null, "medium", null,
                "Added by Arena from what it connects to: say what data it gets and its agreements.", s.Source), by, ct);
            names.Add(s.Name);
        }
        return names;
    }
}
