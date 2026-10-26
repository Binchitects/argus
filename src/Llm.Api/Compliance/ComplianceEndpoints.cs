using System.Globalization;
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Access;
using Llm.Core.Compliance;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Compliance;

/// <summary>The newest attestation of a control's operator part; <paramref name="Due"/> a year after it.</summary>
public sealed record AttestationView(string Actor, DateTimeOffset At, string Note, DateTimeOffset Due);

/// <summary>A control as Admin → Compliance shows it: as controls.yaml has it, with its state from its app checks.</summary>
/// <param name="State">The worst of its app checks; null when it has none (its live checks run from outside).</param>
/// <param name="Attested">The newest attestation of the operator's part, if any.</param>
public sealed record ControlView(string Id, string Title, string Meets, IReadOnlyDictionary<string, List<string>> Frameworks, IReadOnlyList<string> Docs,
    IReadOnlyList<string> Tests, IReadOnlyList<string> Checks, string? Operator, string? State, AttestationView? Attested);

/// <param name="Retention">How long each kind of data and record is kept, and what removes it.</param>
public sealed record ComplianceView(IReadOnlyDictionary<string, string> Frameworks, IReadOnlyList<ControlView> Controls, IReadOnlyList<CheckResult> Checks,
    DateTimeOffset CheckedAt, IReadOnlyList<Storage.KeepRule>? Retention = null);

public sealed record AttestRequest(string? Note);

/// <param name="UserId">For an admin taking one in: whose.</param>
public sealed record TakeRequest(string? Kind, string? Details, Guid UserId = default);

public sealed record OutcomeRequest(string? Outcome);

public sealed record RestrictionRequest(bool Restricted, string? Why);

public sealed record DataRequestView(Guid Id, Guid? UserId, string Person, string? Email, string Kind, string? Details, string Status, DateTimeOffset ReceivedAt,
    DateTimeOffset DueAt, string ReceivedBy, DateTimeOffset? DoneAt, string? DoneBy, string? Outcome, bool Certificate);

/// <param name="Decision">keep or revoke.</param>
public sealed record DecideRequest(string? Decision, string? Note);

public sealed record ReviewSummary(Guid Id, string Title, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset DueAt, DateTimeOffset? ClosedAt, string? ClosedBy,
    int Items, int Kept, int Revoked);

public static class ComplianceEndpoints
{
    /// <summary>An attestation counts for this long; after it, the operator's part is due again.</summary>
    public static readonly TimeSpan AttestationLasts = TimeSpan.FromDays(365);

    public static void AddCompliance(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<ComplianceChecks>();
        services.Configure<ComplianceOptions>(config.GetSection("Compliance"));
        services.AddScoped<AccessReviews>();
        services.AddScoped<DataRequests>();
        services.AddHostedService<AccessReviewSchedule>();
    }

    public static void MapCompliance(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/compliance").RequirePermissions(Permissions.ComplianceRead, Permissions.ComplianceManage);
        g.MapGet("", async (ComplianceChecks checks, AppDbContext db, Storage.StorageReport storage, TimeProvider clock, CancellationToken ct) =>
            Results.Ok(View(await checks.RunAsync(ct), await LatestAsync(db, ct), clock.GetUtcNow()) with { Retention = await storage.RulesAsync(ct) }));
        g.MapPost("/controls/{id}/attest", AttestAsync);
        // Reading the evidence changes nothing, yet it lists who holds what: it is audited.
        g.MapGet("/evidence", EvidenceAsync);
        // The audit log's hash chain walked now, with its anchors; and an anchor made now (before an audit, say).
        g.MapGet("/audit-chain", async (AuditWatch watch, IOptionsMonitor<AuditOptions> o, CancellationToken ct) =>
            Results.Ok(new { report = await watch.WalkAsync(anchor: false, ct), anchors = AnchorsOf(o.CurrentValue).TakeLast(30) }));
        // Access reviews: started by the schedule or by hand, each item kept or revoked, closed when all are decided.
        g.MapGet("/reviews", async (AppDbContext db, CancellationToken ct) => Results.Ok(await ReviewListAsync(db, ct)));
        g.MapPost("/reviews", async (ClaimsPrincipal p, UserManager<AppUser> users, AccessReviews reviews, CancellationToken ct) =>
        {
            var review = await reviews.StartAsync((await users.GetUserAsync(p))!.UserName!, ct);
            return Results.Created($"/api/admin/compliance/reviews/{review.Id}", new { review.Id });
        });
        g.MapGet("/reviews/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.AccessReviews.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct) is { } review
                ? Results.Ok(new { review, items = await db.AccessReviewItems.AsNoTracking().Where(i => i.ReviewId == id).OrderBy(i => i.Kind).ThenBy(i => i.Subject).ToListAsync(ct) })
                : Results.NotFound());
        g.MapPost("/reviews/{id:guid}/items/{item:guid}", async (Guid id, Guid item, DecideRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AccessReviews reviews,
            CancellationToken ct) => await ReviewAsync(async () => await reviews.DecideAsync(id, item, body.Decision ?? "", body.Note, (await users.GetUserAsync(p))!, ct)));
        g.MapPost("/reviews/{id:guid}/close", async (Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AccessReviews reviews, CancellationToken ct) =>
            await ReviewAsync(async () => await reviews.CloseAsync(id, (await users.GetUserAsync(p))!, ct)));
        // People's data requests: taken in, answered within a month, each step audited.
        var privacy = app.MapGroup("/api/admin/compliance/data-requests").RequirePermissions(Permissions.ComplianceRead, Permissions.PrivacyManage);
        privacy.MapGet("", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.DataRequests.AsNoTracking().OrderBy(r => r.Status == DataRequest.Open ? 0 : 1).ThenBy(r => r.DueAt).Select(Shaped).ToListAsync(ct)));
        privacy.MapPost("", async (TakeRequest body, ClaimsPrincipal p, UserManager<AppUser> users, DataRequests requests, CancellationToken ct) =>
            await users.FindByIdAsync(body.UserId.ToString()) is not { } person ? Results.NotFound()
                : await PrivacyAsync(async () => Shape(await requests.TakeAsync(person, body.Kind ?? "", body.Details, (await users.GetUserAsync(p))!.UserName!, ct))));
        privacy.MapPost("/{id:guid}/complete", async (Guid id, OutcomeRequest body, ClaimsPrincipal p, UserManager<AppUser> users, DataRequests requests, CancellationToken ct) =>
            await PrivacyAsync(async () => Shape(await requests.CompleteAsync(id, body.Outcome, (await users.GetUserAsync(p))!, ct))));
        privacy.MapPost("/{id:guid}/refuse", async (Guid id, OutcomeRequest body, ClaimsPrincipal p, UserManager<AppUser> users, DataRequests requests, CancellationToken ct) =>
            await PrivacyAsync(async () => Shape(await requests.RefuseAsync(id, body.Outcome, (await users.GetUserAsync(p))!, ct))));
        privacy.MapPost("/{id:guid}/restrict", async (Guid id, ClaimsPrincipal p, UserManager<AppUser> users, DataRequests requests, CancellationToken ct) =>
            await PrivacyAsync(async () => Shape(await requests.RestrictAsync(id, (await users.GetUserAsync(p))!, ct))));
        privacy.MapPost("/{id:guid}/erase", async (Guid id, ClaimsPrincipal p, UserManager<AppUser> users, DataRequests requests, CancellationToken ct) =>
            await PrivacyAsync(async () => Shape(await requests.EraseAsync(id, (await users.GetUserAsync(p))!, ct))));
        privacy.MapGet("/{id:guid}/certificate", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.DataRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct) is { Certificate: { } c }
                ? Results.Text(DataRequests.CertificateText(JsonSerializer.Deserialize<ErasureCertificate>(c, JsonSerializerOptions.Web)!), "text/markdown; charset=utf-8")
                : Results.NotFound());
        // Lifting a restriction (or restricting without a request): on the person, audited.
        privacy.MapPut("/restriction/{userId:guid}", async (Guid userId, RestrictionRequest body, UserManager<AppUser> users, DataRequests requests, CancellationToken ct) =>
        {
            if (await users.FindByIdAsync(userId.ToString()) is not { } person)
            {
                return Results.NotFound();
            }
            await requests.SetRestrictedAsync(person, body.Restricted, string.IsNullOrWhiteSpace(body.Why) ? "an admin" : body.Why.Trim(), ct);
            return Results.Ok(new { restrictedAt = person.ProcessingRestrictedAt });
        });

        // The person's own: what they asked, and asking.
        var mine = app.MapGroup("/api/account/data-requests").PersonOnly();
        mine.MapGet("", async (ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct) =>
        {
            var me = (await users.GetUserAsync(p))!;
            return Results.Ok(new
            {
                restrictedAt = me.ProcessingRestrictedAt,
                requests = await db.DataRequests.AsNoTracking().Where(r => r.UserId == me.Id).OrderByDescending(r => r.ReceivedAt).Select(Shaped).ToListAsync(ct),
            });
        });
        mine.MapPost("", async (TakeRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, DataRequests requests, CancellationToken ct) =>
        {
            var me = (await users.GetUserAsync(p))!;
            // A few at a time: each one is answered by people.
            if (await db.DataRequests.CountAsync(r => r.UserId == me.Id && r.Status == DataRequest.Open, ct) >= 5)
            {
                return AuthEndpoints.Problem(429, "too_many", "You have five requests waiting already: they are answered within a month.");
            }
            return await PrivacyAsync(async () => Shape(await requests.TakeAsync(me, body.Kind ?? "", body.Details, "themselves", ct)));
        });
        g.MapPost("/audit-chain/anchor", async (AuditWatch watch, AuditChainState state, Audit audit, CancellationToken ct) =>
        {
            var report = await watch.WalkAsync(anchor: true, ct);
            if (report.Holds)
            {
                await audit.WriteAsync("audit.anchor", detail: $"entry {state.LastAnchor?.Id}");
            }
            return Results.Ok(new { report, anchor = report.Holds ? state.LastAnchor : null });
        });
    }

    private static readonly System.Linq.Expressions.Expression<Func<DataRequest, DataRequestView>> Shaped = r =>
        new DataRequestView(r.Id, r.UserId, r.Person, r.Email, r.Kind, r.Details, r.Status, r.ReceivedAt, r.DueAt, r.ReceivedBy, r.DoneAt, r.DoneBy, r.Outcome, r.Certificate != null);

    private static readonly Func<DataRequest, DataRequestView> ShapeOne = Shaped.Compile();

    private static DataRequestView Shape(DataRequest r) => ShapeOne(r);

    private static async Task<IResult> PrivacyAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return Results.Ok(await work());
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (Exception ex) when (ex is DataRequestException or PeopleException)
        {
            return AuthEndpoints.Problem(400, "request", ex.Message);
        }
    }

    private static async Task<IResult> ReviewAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return Results.Ok(await work());
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (AccessReviewException ex)
        {
            return AuthEndpoints.Problem(400, "review", ex.Message);
        }
    }

    /// <summary>Every review, newest first, with how many of its items are decided, kept and revoked.</summary>
    public static async Task<List<ReviewSummary>> ReviewListAsync(AppDbContext db, CancellationToken ct)
    {
        var reviews = await db.AccessReviews.AsNoTracking().OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        var counts = await db.AccessReviewItems.AsNoTracking().GroupBy(i => new { i.ReviewId, i.Decision }).Select(g => new { g.Key.ReviewId, g.Key.Decision, n = g.Count() }).ToListAsync(ct);
        return [.. reviews.Select(r =>
        {
            int Of(string? d) => counts.Where(c => c.ReviewId == r.Id && c.Decision == d).Sum(c => c.n);
            return new ReviewSummary(r.Id, r.Title, r.CreatedAt, r.CreatedBy, r.DueAt, r.ClosedAt, r.ClosedBy,
                Of(null) + Of(AccessReviewItem.Keep) + Of(AccessReviewItem.Revoke), Of(AccessReviewItem.Keep), Of(AccessReviewItem.Revoke));
        })];
    }

    private static IReadOnlyList<AuditAnchor> AnchorsOf(AuditOptions o) => AuditChain.ReadAnchors(string.IsNullOrWhiteSpace(o.AnchorDir) ? null : o.AnchorDir);

    private static readonly string[] Worst = [ComplianceChecks.Fail, ComplianceChecks.Warn, ComplianceChecks.Unknown, ComplianceChecks.Ok];

    private static async Task<Dictionary<string, ComplianceAttestation>> LatestAsync(AppDbContext db, CancellationToken ct) =>
        (await db.ComplianceAttestations.AsNoTracking().ToListAsync(ct)).GroupBy(a => a.ControlId).ToDictionary(g => g.Key, g => g.MaxBy(a => a.At)!);

    public static ComplianceView View(IReadOnlyList<CheckResult> results, IReadOnlyDictionary<string, ComplianceAttestation> attested, DateTimeOffset at)
    {
        var file = ComplianceControls.All;
        var byId = results.ToDictionary(r => "app:" + r.Id);
        var controls = file.Controls.Select(c =>
        {
            var states = c.Checks.Where(byId.ContainsKey).Select(k => byId[k].State).ToList();
            var a = attested.GetValueOrDefault(c.Id);
            return new ControlView(c.Id, c.Title, c.Meets, c.Frameworks, c.Docs, c.Tests, c.Checks, c.Operator,
                states.Count == 0 ? null : Worst.First(states.Contains), a is null ? null : new AttestationView(a.Actor, a.At, a.Note, a.At + AttestationLasts));
        }).ToList();
        return new ComplianceView(file.Frameworks, controls, results, at);
    }

    private static async Task<IResult> AttestAsync(string id, AttestRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Audit audit,
        TimeProvider clock, CancellationToken ct)
    {
        if (ComplianceControls.All.Controls.FirstOrDefault(c => c.Id == id) is not { } control)
        {
            return Results.NotFound();
        }
        var note = body.Note?.Trim() ?? "";
        if (note.Length is < 3 or > 2000)
        {
            return AuthEndpoints.Problem(400, "note", "Say what was done and where its proof is (up to 2000 characters).");
        }
        var me = (await users.GetUserAsync(p))!;
        var made = new ComplianceAttestation { ControlId = control.Id, ActorId = me.Id, Actor = me.UserName ?? "", Note = note, At = clock.GetUtcNow() };
        db.ComplianceAttestations.Add(made);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("compliance.attest", control.Id, detail: note.Length > 200 ? note[..200] + "…" : note);
        return Results.Ok(new AttestationView(made.Actor, made.At, made.Note, made.At + AttestationLasts));
    }

    /// <summary>
    /// What an auditor asks for, in one zip: the controls, the checks' results now, every attestation, the settings (no secret),
    /// who holds an admin permission, and the audit log's last 90 days counted by action.
    /// </summary>
    private static async Task EvidenceAsync(HttpContext http, ClaimsPrincipal p, ComplianceChecks checks, AppDbContext db, AccessService access,
        Settings.SettingsService settings, Audit audit, AuditWatch watch, IOptionsMonitor<AuditOptions> auditOptions, TimeProvider clock)
    {
        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        var results = await checks.RunAsync(ct);
        var attestations = await db.ComplianceAttestations.AsNoTracking().OrderBy(a => a.ControlId).ThenBy(a => a.At).ToListAsync(ct);
        var people = await db.Users.AsNoTracking().ToListAsync(ct);
        var memberships = await access.MembershipsAsync(people, ct);
        var adminKeys = Permissions.All.Where(x => x.Admin).Select(x => x.Key).ToList();
        var since = now.AddDays(-90);
        var actions = await db.AuditEvents.AsNoTracking().Where(e => e.At >= since)
            .GroupBy(e => e.Action).Select(g => new { Action = g.Key, Count = g.Count(), Failed = g.Count(e => !e.Success) }).OrderBy(a => a.Action).ToListAsync(ct);
        var view = View(results, attestations.GroupBy(a => a.ControlId).ToDictionary(g => g.Key, g => g.MaxBy(a => a.At)!), now);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

        await audit.WriteAsync("compliance.evidence", detail: $"{view.Controls.Count} controls, {results.Count} checks, {attestations.Count} attestations");
        http.Response.ContentType = "application/zip";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"compliance-evidence-{now:yyyy-MM-dd}.zip\"";
        http.Response.Headers.CacheControl = "no-store";
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Text(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
            string Csv(IEnumerable<IEnumerable<object?>> rows) => string.Join("\n", rows.Select(r => string.Join(",", r.Select(Cell)))) + "\n";

            Text("README.txt", $"""
                Compliance evidence from Argus Arena {AppInfo.Current.Version}, made {now:yyyy-MM-dd HH:mm} UTC by {p.Identity?.Name}.

                controls.yaml         each control: the clauses it serves, its documents, tests and checks (docs/compliance/)
                compliance.json       the controls with their state now and their newest attestation, and every check's result
                attestations.csv      every attestation of the company's part, oldest first
                settings.json         every setting with its value and where it comes from (secrets only as set or not)
                privileged-access.csv who holds an administrative permission, how they sign in, whether they have a second factor
                audit-actions.csv     the audit log's last 90 days, counted by action
                audit-chain.json      the audit log's hash chain walked as this was made: whether it holds, and where not
                audit-anchors.jsonl   the chain's daily anchors, kept apart from the database (each: when, the entry, its hash)
                access-reviews.csv    every access review's every item: who, what they held, the decision, by whom, when, what came of it

                """);
            Text("controls.yaml", ComplianceControls.Text);
            Text("compliance.json", JsonSerializer.Serialize(view, json));
            Text("attestations.csv", Csv([["control", "at", "by", "note"], .. attestations.Select(a => new object?[] { a.ControlId, a.At.ToString("u", CultureInfo.InvariantCulture), a.Actor, a.Note })]));
            Text("settings.json", JsonSerializer.Serialize(await settings.ViewAsync(ct), json));
            Text("privileged-access.csv", Csv([["person", "email", "signs in with", "second factor", "disabled", "last sign-in", "administrative permissions"],
                .. people.Where(u => adminKeys.Any(memberships[u.Id].Has)).OrderBy(u => u.UserName, StringComparer.Ordinal).Select(u => new object?[]
                {
                    u.UserName, u.Email, u.Source switch { UserSource.Ldap => "the directory", UserSource.Oidc => "company sign-in", _ => "a password here" },
                    u.Source == UserSource.Oidc ? "the provider's" : u.TwoFactorEnabled ? "yes" : "no", u.IsDisabled ? "yes" : "no",
                    u.LastSignInAt?.ToString("u", CultureInfo.InvariantCulture), string.Join(" ", adminKeys.Where(memberships[u.Id].Has)),
                })]));
            Text("audit-actions.csv", Csv([["action", "count", "failed"], .. actions.Select(a => new object?[] { a.Action, a.Count, a.Failed })]));
            var reviewTitles = await db.AccessReviews.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Title, ct);
            var reviewItems = await db.AccessReviewItems.AsNoTracking().OrderBy(i => i.ReviewId).ThenBy(i => i.Subject).ToListAsync(ct);
            Text("access-reviews.csv", Csv([["review", "kind", "who", "what", "decision", "by", "at", "note", "outcome"], .. reviewItems.Select(i => new object?[]
            {
                reviewTitles.GetValueOrDefault(i.ReviewId), i.Kind, i.Subject, i.What, i.Decision, i.DecidedBy, i.DecidedAt?.ToString("u", CultureInfo.InvariantCulture), i.Note, i.Outcome,
            })]));
            Text("audit-chain.json", JsonSerializer.Serialize(await watch.WalkAsync(anchor: false, ct), json));
            Text("audit-anchors.jsonl", string.Concat(AnchorsOf(auditOptions.CurrentValue).Select(a => JsonSerializer.Serialize(a, JsonSerializerOptions.Web) + "\n")));
        }
        buffer.Position = 0;
        await buffer.CopyToAsync(http.Response.Body, ct);
    }

    private static string Cell(object? v)
    {
        var s = v switch { null => "", IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => v.ToString() ?? "" };
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
    }
}
