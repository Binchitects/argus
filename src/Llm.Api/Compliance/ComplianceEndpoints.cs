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

/// <summary>How much of a framework Arena's controls serve: its clauses, and those a control serves.</summary>
public sealed record FrameworkCoverage(string Title, int Clauses, int Served);

/// <param name="Coverage">Each framework's clauses (its catalog) and how many a control serves.</param>
/// <param name="Retention">How long each kind of data and record is kept, and what removes it.</param>
public sealed record ComplianceView(IReadOnlyDictionary<string, string> Frameworks, IReadOnlyList<ControlView> Controls, IReadOnlyList<CheckResult> Checks,
    DateTimeOffset CheckedAt, IReadOnlyDictionary<string, FrameworkCoverage> Coverage, IReadOnlyList<Storage.KeepRule>? Retention = null);

public sealed record AttestRequest(string? Note);

/// <param name="UserId">For an admin taking one in: whose.</param>
public sealed record TakeRequest(string? Kind, string? Details, Guid UserId = default);

public sealed record OutcomeRequest(string? Outcome);

public sealed record AcceptRequest(string? Version);

public sealed record OpenIncidentRequest(string? Title, string? Summary, string? Severity, bool PersonalData = false, bool HealthData = false, Guid? AlertId = null,
    DateTimeOffset? DetectedAt = null);

public sealed record StepRequest(string? Kind, string? Text);

public sealed record RiskReviewRequest(string? Note, DateTimeOffset? Next);

public sealed record RiskAcceptRequest(string? Why, DateTimeOffset? Until);

public sealed record RiskCloseRequest(string? Why);

public sealed record RiskActionRequest(string? Text, string? Owner, DateTimeOffset? Due);

public sealed record SoaApproveRequest(string? Note);

public sealed record AuditReportRequest(string? Conclusion);

public sealed record FindingVerifyRequest(bool Worked, string? Note);

public sealed record SupplierReviewRequest(string? Note);

public sealed record AiAssessRequest(string? Assessment);

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
        services.Configure<NoticeOptions>(config.GetSection("Notice"));
        services.AddScoped<Notice>();
        services.Configure<SecurityOptions>(config.GetSection("Security"));
        services.AddSingleton<SecurityWatch>();
        services.AddHostedService(sp => sp.GetRequiredService<SecurityWatch>());
        services.AddScoped<Incidents>();
        services.AddScoped<Risks>();
        services.AddScoped<Soa>();
        services.AddScoped<Policies>();
        services.AddScoped<Audits>();
        services.AddScoped<Suppliers>();
        services.AddScoped<AiSystems>();
        services.AddScoped<Readiness>();
        services.AddSingleton<ComplianceSnapshots>();
        services.AddHostedService(sp => sp.GetRequiredService<ComplianceSnapshots>());
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

        // Security alerts and the incident register.
        // The risk register: risks, their treatment's actions and history (GOV-04).
        g.MapGet("/risks", async (Risks risks, CancellationToken ct) => Results.Ok(await risks.ListAsync(ct)));
        g.MapGet("/risks/assets", async (Risks risks, CancellationToken ct) => Results.Ok(await risks.AssetsAsync(ct)));
        g.MapPost("/risks", async (RiskInput body, ClaimsPrincipal p, UserManager<AppUser> users, Risks risks, CancellationToken ct) =>
            await RiskAsync(async () => await risks.CreateAsync(body, await Who(p, users), ct)));
        g.MapGet("/risks/{id:guid}", async (Guid id, Risks risks, CancellationToken ct) => await RiskAsync(async () =>
        {
            var (risk, actions, events) = await risks.GetAsync(id, ct);
            return new { risk, actions, events };
        }));
        g.MapPut("/risks/{id:guid}", async (Guid id, RiskInput body, ClaimsPrincipal p, UserManager<AppUser> users, Risks risks, CancellationToken ct) =>
            await RiskAsync(async () => await risks.UpdateAsync(id, body, await Who(p, users), ct)));
        g.MapPost("/risks/{id:guid}/review", async (Guid id, RiskReviewRequest body, ClaimsPrincipal p, UserManager<AppUser> users, Risks risks, CancellationToken ct) =>
            await RiskAsync(async () => await risks.ReviewAsync(id, body.Note, body.Next, await Who(p, users), ct)));
        g.MapPost("/risks/{id:guid}/accept", async (Guid id, RiskAcceptRequest body, ClaimsPrincipal p, UserManager<AppUser> users, Risks risks, CancellationToken ct) =>
            await RiskAsync(async () => await risks.AcceptAsync(id, body.Why, body.Until, await Who(p, users), ct)));
        g.MapPost("/risks/{id:guid}/close", async (Guid id, RiskCloseRequest body, ClaimsPrincipal p, UserManager<AppUser> users, Risks risks, CancellationToken ct) =>
            await RiskAsync(async () => await risks.CloseAsync(id, true, body.Why, await Who(p, users), ct)));
        g.MapPost("/risks/{id:guid}/reopen", async (Guid id, RiskCloseRequest body, ClaimsPrincipal p, UserManager<AppUser> users, Risks risks, CancellationToken ct) =>
            await RiskAsync(async () => await risks.CloseAsync(id, false, body.Why, await Who(p, users), ct)));
        g.MapPost("/risks/{id:guid}/actions", async (Guid id, RiskActionRequest body, ClaimsPrincipal p, UserManager<AppUser> users, Risks risks, CancellationToken ct) =>
            await RiskAsync(async () => await risks.AddActionAsync(id, body.Text, body.Owner, body.Due, await Who(p, users), ct)));
        g.MapPost("/risks/{id:guid}/actions/{action:guid}/done", async (Guid id, Guid action, ClaimsPrincipal p, UserManager<AppUser> users, Risks risks, CancellationToken ct) =>
            await RiskAsync(async () => await risks.CompleteActionAsync(id, action, await Who(p, users), ct)));

        // The Statements of Applicability, ISO 27001's and ISO 42001's (GOV-05).
        g.MapGet("/soa/{framework}", async (string framework, Soa soa, CancellationToken ct) => await SoaAsync(() => soa.GetAsync(framework, ct)));
        g.MapPut("/soa/{framework}/{clause}", async (string framework, string clause, SoaUpdate body, ClaimsPrincipal p, UserManager<AppUser> users, Soa soa, CancellationToken ct) =>
            await SoaAsync(async () => await soa.SetAsync(framework, clause, body, await Who(p, users), ct)));
        g.MapPost("/soa/{framework}/approve", async (string framework, SoaApproveRequest body, ClaimsPrincipal p, UserManager<AppUser> users, Soa soa, CancellationToken ct) =>
            await SoaAsync(async () => await soa.ApproveAsync(framework, body.Note, await Who(p, users), ct)));
        g.MapGet("/soa/{framework}/csv", async (string framework, int? version, Soa soa, AppDbContext db, CancellationToken ct) =>
        {
            if (!Soa.Frameworks.Contains(framework))
            {
                return Results.NotFound();
            }
            // A version as it was approved, or the statement as it stands.
            var text = version is { } v
                ? await db.SoaApprovals.AsNoTracking().Where(a => a.Framework == framework && a.Version == v).Select(a => a.Snapshot).SingleOrDefaultAsync(ct)
                : Soa.Csv(await soa.GetAsync(framework, ct));
            return text is null ? Results.NotFound()
                : Results.File(Encoding.UTF8.GetBytes(text), "text/csv", $"statement-of-applicability-{framework}{(version is { } n ? $"-v{n}" : "")}.csv");
        });

        // The company's policies, governed (GOV-06).
        g.MapGet("/policies", async (Policies policies, CancellationToken ct) => Results.Ok(await policies.ListAsync(ct)));
        g.MapGet("/policies/templates", () => Results.Ok(Policies.TemplateList));
        g.MapPost("/policies", async (PolicyCreate body, ClaimsPrincipal p, UserManager<AppUser> users, Policies policies, CancellationToken ct) =>
            await PolicyAsync(async () => await policies.CreateAsync(body, await Who(p, users), ct)));
        g.MapGet("/policies/{id:guid}", async (Guid id, Policies policies, CancellationToken ct) => await PolicyAsync(async () =>
        {
            var (policy, versions, pending) = await policies.GetAsync(id, ct);
            return new { policy, versions, pending };
        }));
        g.MapGet("/policies/{id:guid}/versions/{version:int}", async (Guid id, int version, Policies policies, CancellationToken ct) =>
            await PolicyAsync(async () => new { text = await policies.TextAsync(id, version, ct) }));
        g.MapPut("/policies/{id:guid}", async (Guid id, PolicySettings body, ClaimsPrincipal p, UserManager<AppUser> users, Policies policies, CancellationToken ct) =>
            await PolicyAsync(async () => await policies.SetAsync(id, body, await Who(p, users), ct)));
        g.MapPut("/policies/{id:guid}/draft", async (Guid id, PolicyDraft body, ClaimsPrincipal p, UserManager<AppUser> users, Policies policies, CancellationToken ct) =>
            await PolicyAsync(async () => await policies.DraftAsync(id, body, await Who(p, users), ct)));
        g.MapPost("/policies/{id:guid}/approve", async (Guid id, ClaimsPrincipal p, UserManager<AppUser> users, Policies policies, Notifications.Notifier notifier, CancellationToken ct) =>
            await PolicyAsync(async () =>
            {
                var me = (await users.GetUserAsync(p))!;
                return await policies.ApproveAsync(id, me.UserName!, me.Id, notifier, ct);
            }));
        g.MapPost("/policies/{id:guid}/review", async (Guid id, ClaimsPrincipal p, UserManager<AppUser> users, Policies policies, CancellationToken ct) =>
            await PolicyAsync(async () => await policies.ReviewAsync(id, await Who(p, users), ct)));

        // Audits, findings, certificates and waivers (GOV-07).
        g.MapGet("/audits", async (Audits audits, CancellationToken ct) => Results.Ok(await audits.AllAsync(ct)));
        g.MapPost("/audits", async (AuditInput body, ClaimsPrincipal p, UserManager<AppUser> users, Audits audits, CancellationToken ct) =>
            await AuditAsync(async () => await audits.SaveAuditAsync(null, body, await Who(p, users), ct)));
        g.MapPut("/audits/{id:guid}", async (Guid id, AuditInput body, ClaimsPrincipal p, UserManager<AppUser> users, Audits audits, CancellationToken ct) =>
            await AuditAsync(async () => await audits.SaveAuditAsync(id, body, await Who(p, users), ct)));
        g.MapPost("/audits/{id:guid}/report", async (Guid id, AuditReportRequest body, ClaimsPrincipal p, UserManager<AppUser> users, Audits audits, CancellationToken ct) =>
            await AuditAsync(async () => await audits.ReportAsync(id, body.Conclusion, await Who(p, users), ct)));
        g.MapPost("/findings", async (FindingInput body, ClaimsPrincipal p, UserManager<AppUser> users, Audits audits, CancellationToken ct) =>
            await AuditAsync(async () => await audits.SaveFindingAsync(null, body, await Who(p, users), ct)));
        g.MapPut("/findings/{id:guid}", async (Guid id, FindingInput body, ClaimsPrincipal p, UserManager<AppUser> users, Audits audits, CancellationToken ct) =>
            await AuditAsync(async () => await audits.SaveFindingAsync(id, body, await Who(p, users), ct)));
        g.MapPost("/findings/{id:guid}/done", async (Guid id, ClaimsPrincipal p, UserManager<AppUser> users, Audits audits, CancellationToken ct) =>
            await AuditAsync(async () => await audits.DoneAsync(id, await Who(p, users), ct)));
        g.MapPost("/findings/{id:guid}/verify", async (Guid id, FindingVerifyRequest body, ClaimsPrincipal p, UserManager<AppUser> users, Audits audits, CancellationToken ct) =>
            await AuditAsync(async () => await audits.VerifyAsync(id, body.Worked, body.Note, await Who(p, users), ct)));
        g.MapPost("/certificates", async (CertificateInput body, ClaimsPrincipal p, UserManager<AppUser> users, Audits audits, CancellationToken ct) =>
            await AuditAsync(async () => await audits.SaveCertificateAsync(null, body, await Who(p, users), ct)));
        g.MapPut("/certificates/{id:guid}", async (Guid id, CertificateInput body, ClaimsPrincipal p, UserManager<AppUser> users, Audits audits, CancellationToken ct) =>
            await AuditAsync(async () => await audits.SaveCertificateAsync(id, body, await Who(p, users), ct)));
        g.MapPost("/waivers", async (WaiverInput body, ClaimsPrincipal p, UserManager<AppUser> users, Audits audits, CancellationToken ct) =>
            await AuditAsync(async () => await audits.WaiveAsync(body, await Who(p, users), ct)));
        g.MapPost("/waivers/{id:guid}/withdraw", async (Guid id, ClaimsPrincipal p, UserManager<AppUser> users, Audits audits, CancellationToken ct) =>
            await AuditAsync(async () => await audits.WithdrawAsync(id, await Who(p, users), ct)));

        // The supplier register (GOV-08).
        g.MapGet("/suppliers", async (Suppliers suppliers, CancellationToken ct) =>
            Results.Ok(new { suppliers = await suppliers.ListAsync(ct), suggested = await suppliers.SuggestAsync(ct) }));
        g.MapPost("/suppliers", async (SupplierInput body, ClaimsPrincipal p, UserManager<AppUser> users, Suppliers suppliers, CancellationToken ct) =>
            await SupplierAsync(async () => await suppliers.SaveAsync(null, body, await Who(p, users), ct)));
        g.MapPut("/suppliers/{id:guid}", async (Guid id, SupplierInput body, ClaimsPrincipal p, UserManager<AppUser> users, Suppliers suppliers, CancellationToken ct) =>
            await SupplierAsync(async () => await suppliers.SaveAsync(id, body, await Who(p, users), ct)));
        g.MapPost("/suppliers/{id:guid}/review", async (Guid id, SupplierReviewRequest body, ClaimsPrincipal p, UserManager<AppUser> users, Suppliers suppliers, CancellationToken ct) =>
            await SupplierAsync(async () => await suppliers.ReviewAsync(id, body.Note, await Who(p, users), ct)));
        g.MapPost("/suppliers/{id:guid}/retire", async (Guid id, ClaimsPrincipal p, UserManager<AppUser> users, Suppliers suppliers, CancellationToken ct) =>
            await SupplierAsync(async () => await suppliers.RetireAsync(id, await Who(p, users), ct)));

        // The AI systems register (GOV-09).
        g.MapGet("/ai-systems", async (AiSystems ai, CancellationToken ct) => Results.Ok(new { systems = await ai.ListAsync(ct), suggested = await ai.SuggestAsync(ct) }));
        g.MapPost("/ai-systems", async (AiSystemInput body, ClaimsPrincipal p, UserManager<AppUser> users, AiSystems ai, CancellationToken ct) =>
            await AiAsync(async () => await ai.SaveAsync(null, body, await Who(p, users), ct)));
        g.MapPut("/ai-systems/{id:guid}", async (Guid id, AiSystemInput body, ClaimsPrincipal p, UserManager<AppUser> users, AiSystems ai, CancellationToken ct) =>
            await AiAsync(async () => await ai.SaveAsync(id, body, await Who(p, users), ct)));
        g.MapPost("/ai-systems/{id:guid}/assess", async (Guid id, AiAssessRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AiSystems ai, CancellationToken ct) =>
            await AiAsync(async () => await ai.AssessAsync(id, body.Assessment, await Who(p, users), ct)));
        g.MapPost("/ai-systems/{id:guid}/in-use", async (Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AiSystems ai, CancellationToken ct) =>
            await AiAsync(async () => await ai.PutInUseAsync(id, await Who(p, users), ct)));
        g.MapPost("/ai-systems/{id:guid}/review", async (Guid id, SupplierReviewRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AiSystems ai, CancellationToken ct) =>
            await AiAsync(async () => await ai.ReviewAsync(id, body.Note, await Who(p, users), ct)));
        g.MapPost("/ai-systems/{id:guid}/retire", async (Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AiSystems ai, CancellationToken ct) =>
            await AiAsync(async () => await ai.RetireAsync(id, await Who(p, users), ct)));

        // How ready for each framework, and what its auditor reads (GOV-11).
        g.MapGet("/readiness", async (Readiness readiness, CancellationToken ct) => Results.Ok(await readiness.AllAsync(ct)));
        g.MapGet("/readiness/{framework}/package", async (string framework, DateOnly? from, DateOnly? to, ClaimsPrincipal p, UserManager<AppUser> users, Readiness readiness,
            Audit audit, TimeProvider clock, CancellationToken ct) =>
        {
            if (!ComplianceControls.Catalogs.ContainsKey(framework))
            {
                return Results.NotFound();
            }
            var zip = await readiness.PackageAsync(framework, from, to, await Who(p, users), ct);
            await audit.WriteAsync("compliance.package", framework, detail: $"{from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "a year"} to {to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "today"}");
            return Results.File(zip, "application/zip", $"{framework}-for-the-auditor-{clock.GetUtcNow():yyyy-MM-dd}.zip");
        });

        g.MapGet("/incidents", async (AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var since = clock.GetUtcNow().AddDays(-60);
            var incidents = await db.Incidents.AsNoTracking().OrderBy(i => i.Status == Incident.Closed ? 1 : 0).ThenByDescending(i => i.DetectedAt).Take(100).ToListAsync(ct);
            return Results.Ok(new
            {
                alerts = await db.SecurityAlerts.AsNoTracking().Where(a => a.At >= since).OrderByDescending(a => a.At).Take(200).ToListAsync(ct),
                incidents = incidents.Select(IncidentShape),
            });
        });
        g.MapPost("/incidents", async (OpenIncidentRequest body, ClaimsPrincipal p, UserManager<AppUser> users, Incidents incidents, CancellationToken ct) =>
        {
            try
            {
                var made = await incidents.OpenAsync(body.Title ?? "", body.Summary, body.Severity ?? "medium", body.PersonalData, body.HealthData, body.AlertId, body.DetectedAt,
                    (await users.GetUserAsync(p))!.UserName!, ct);
                return Results.Created($"/api/admin/compliance/incidents/{made.Id}", IncidentShape(made));
            }
            catch (IncidentException ex)
            {
                return AuthEndpoints.Problem(400, "incident", ex.Message);
            }
        });
        g.MapGet("/incidents/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.Incidents.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct) is { } incident
                ? Results.Ok(new { incident = IncidentShape(incident), events = await db.IncidentEvents.AsNoTracking().Where(e => e.IncidentId == id).OrderBy(e => e.At).ToListAsync(ct),
                    alerts = await db.SecurityAlerts.AsNoTracking().Where(a => a.IncidentId == id).ToListAsync(ct) })
                : Results.NotFound());
        g.MapPost("/incidents/{id:guid}/steps", async (Guid id, StepRequest body, ClaimsPrincipal p, UserManager<AppUser> users, Incidents incidents, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(IncidentShape(await incidents.StepAsync(id, body.Kind ?? "", body.Text, (await users.GetUserAsync(p))!.UserName!, ct)));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (IncidentException ex)
            {
                return AuthEndpoints.Problem(400, "incident", ex.Message);
            }
        });

        // The sign-in notice: what it says, whether they accepted this version, and accepting it.
        var notice = app.MapGroup("/api/account/notice").PersonOnly();
        notice.MapGet("", async (ClaimsPrincipal p, UserManager<AppUser> users, Notice n, CancellationToken ct) =>
        {
            var me = (await users.GetUserAsync(p))!;
            var o = n.Current;
            var accepted = await n.AcceptedAsync(me.Id, ct);
            return Results.Ok(new { on = o.On, o.Title, o.Text, o.Version, acceptedVersion = accepted?.Version, acceptedAt = accepted?.At });
        });
        notice.MapPost("/accept", async (AcceptRequest body, ClaimsPrincipal p, UserManager<AppUser> users, Notice n, HttpContext http, CancellationToken ct) =>
        {
            var me = (await users.GetUserAsync(p))!;
            return await n.AcceptAsync(me.Id, me.UserName!, body.Version ?? "", http.Connection.RemoteIpAddress?.ToString(), ct)
                ? Results.NoContent()
                : AuthEndpoints.Problem(409, "changed", "The notice changed while you read it: read the new one.");
        });

        // The person's own: what they asked, and asking.
        // The policies a person must read, and their acknowledgement.
        var toRead = app.MapGroup("/api/account/policies").PersonOnly();
        toRead.MapGet("", async (ClaimsPrincipal p, UserManager<AppUser> users, Policies policies, CancellationToken ct) =>
            Results.Ok(await policies.ForAsync((await users.GetUserAsync(p))!, ct)));
        toRead.MapGet("/{version:guid}", async (Guid version, ClaimsPrincipal p, UserManager<AppUser> users, Policies policies, CancellationToken ct) =>
            await PolicyAsync(async () => new { text = await policies.ReadTextAsync((await users.GetUserAsync(p))!, version, ct) }));
        toRead.MapPost("/{version:guid}/acknowledge", async (Guid version, ClaimsPrincipal p, UserManager<AppUser> users, Policies policies, CancellationToken ct) =>
            await PolicyAsync(async () => await policies.AcknowledgeAsync((await users.GetUserAsync(p))!, version, ct)));

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

    /// <summary>An incident with the law's clocks: when the authority and the people must be told by (null: not asked).</summary>
    private static object IncidentShape(Incident i)
    {
        var (authority, people) = Incidents.Clocks(i);
        return new
        {
            i.Id, i.Title, i.Summary, i.Severity, i.Status, i.DetectedAt, i.OpenedBy, i.PersonalData, i.HealthData, i.AuthorityDecidedAt, i.AuthorityNotified,
            i.PeopleNotifiedAt, i.ClosedAt, i.Lessons, authorityDue = authority, peopleDue = people,
        };
    }

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

    /// <summary>The signed-in person's name, as the records keep it.</summary>
    private static async Task<string> Who(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!.UserName!;

    private static async Task<IResult> AiAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return Results.Ok(await work());
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (AiSystemException ex)
        {
            return AuthEndpoints.Problem(400, "ai-system", ex.Message);
        }
    }

    private static async Task<IResult> SupplierAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return Results.Ok(await work());
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (SupplierException ex)
        {
            return AuthEndpoints.Problem(400, "supplier", ex.Message);
        }
    }

    private static async Task<IResult> AuditAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return Results.Ok(await work());
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (AuditException ex)
        {
            return AuthEndpoints.Problem(400, "audit", ex.Message);
        }
    }

    private static async Task<IResult> PolicyAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return Results.Ok(await work());
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (PolicyException ex)
        {
            return AuthEndpoints.Problem(400, "policy", ex.Message);
        }
    }

    private static async Task<IResult> SoaAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return Results.Ok(await work());
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (SoaException ex)
        {
            return AuthEndpoints.Problem(400, "soa", ex.Message);
        }
    }

    private static async Task<IResult> RiskAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return Results.Ok(await work());
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (RiskException ex)
        {
            return AuthEndpoints.Problem(400, "risk", ex.Message);
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
        return new ComplianceView(file.Frameworks, controls, results, at, Coverage(file));
    }

    /// <summary>For each framework: its catalog's clauses, and those at least one control serves.</summary>
    public static IReadOnlyDictionary<string, FrameworkCoverage> Coverage(ControlsFile file) =>
        ComplianceControls.Catalogs.ToDictionary(kv => kv.Key, kv => new FrameworkCoverage(kv.Value.Title, kv.Value.Clauses.Count,
            file.Controls.SelectMany(c => c.Frameworks.GetValueOrDefault(kv.Key) ?? []).Select(kv.Value.Entry).OfType<string>().Distinct().Count()));

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
                notice.csv            who accepted which version of the sign-in notice, when and from where
                incidents.json        the incident register: each incident with its clocks and every step, and the security alerts
                risks.csv             the risk register: each risk, its levels before and after treatment, owner, treatment, controls, review
                risk-history.csv      each risk's every change, review, acceptance and action, by whom and when
                soa-iso27001.csv      ISO 27001's Statement of Applicability as it stands: each Annex A control, whether it applies and why, how far
                soa-iso42001.csv      the same for ISO 42001's Annex A
                soa-approvals.csv     every approved version of either, by whom and when
                policies.csv          each policy's every version: approved by whom and when, its review, who must read it and how many did
                policy-acknowledgements.csv  who read which version of which policy, and when
                policies/             the text of each policy's version in force
                audits.csv            the audit programme: each audit, its kind, scope, auditor, dates and conclusion
                findings.csv          every finding: its kind, clause, correction, root cause, corrective action, and its check
                certificates.csv      the certificates and reports the company holds, their scope and when they end
                waivers.csv           each control waived for a time: why, what makes up for it, by whom, until when
                suppliers.csv         each supplier: the data it gets, where, its certifications and agreements, its risk and review
                ai-systems.csv        each AI system: purpose, intended use, whom it affects, AI Act class and role, oversight, evaluation, review
                ai-impact/            each AI system's impact assessment, as written

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
            var accepted = await db.NoticeAcceptances.AsNoTracking().Join(db.Users.AsNoTracking(), a => a.UserId, u => u.Id, (a, u) => new { u.UserName, a.Version, a.At, a.Ip })
                .OrderBy(a => a.At).ToListAsync(ct);
            Text("notice.csv", Csv([["person", "version", "at", "from"], .. accepted.Select(a => new object?[] { a.UserName, a.Version, a.At.ToString("u", CultureInfo.InvariantCulture), a.Ip })]));
            var allIncidents = await db.Incidents.AsNoTracking().OrderBy(i => i.DetectedAt).ToListAsync(ct);
            var allSteps = await db.IncidentEvents.AsNoTracking().OrderBy(e => e.At).ToListAsync(ct);
            Text("incidents.json", JsonSerializer.Serialize(new
            {
                incidents = allIncidents.Select(i => new { incident = IncidentShape(i), steps = allSteps.Where(e => e.IncidentId == i.Id) }),
                alerts = await db.SecurityAlerts.AsNoTracking().OrderBy(a => a.At).ToListAsync(ct),
            }, json));
            var riskViews = await http.RequestServices.GetRequiredService<Risks>().ListAsync(ct);
            Text("risks.csv", Csv([["id", "title", "category", "assets", "threat", "likelihood", "impact", "level", "owner", "treatment", "controls",
                "left likelihood", "left impact", "left level", "accepted by", "accepted until", "why", "review due", "closed", "open actions"], .. riskViews.Select(r => new object?[]
            {
                r.Id, r.Title, r.Category, string.Join("; ", r.Assets), r.Threat, r.Likelihood, r.Impact, r.Level, r.Owner, r.Treatment, string.Join(" ", r.Controls),
                r.ResidualLikelihood, r.ResidualImpact, r.ResidualLevel, r.AcceptedBy, r.AcceptedUntil?.ToString("u", CultureInfo.InvariantCulture), r.AcceptedWhy,
                r.ReviewDue.ToString("u", CultureInfo.InvariantCulture), r.ClosedAt?.ToString("u", CultureInfo.InvariantCulture), r.OpenActions,
            })]));
            var riskEvents = await db.RiskEvents.AsNoTracking().OrderBy(e => e.RiskId).ThenBy(e => e.At).ToListAsync(ct);
            Text("risk-history.csv", Csv([["risk", "at", "by", "what", "text"], .. riskEvents.Select(e => new object?[] { e.RiskId, e.At.ToString("u", CultureInfo.InvariantCulture), e.By, e.Kind, e.Text })]));
            var soa = http.RequestServices.GetRequiredService<Soa>();
            foreach (var fw in Soa.Frameworks)
            {
                Text($"soa-{fw}.csv", Soa.Csv(await soa.GetAsync(fw, ct)));
            }
            var approvals = await db.SoaApprovals.AsNoTracking().OrderBy(a => a.Framework).ThenBy(a => a.Version).ToListAsync(ct);
            Text("soa-approvals.csv", Csv([["framework", "version", "approved by", "at", "note"],
                .. approvals.Select(a => new object?[] { a.Framework, a.Version, a.ApprovedBy, a.ApprovedAt.ToString("u", CultureInfo.InvariantCulture), a.Note })]));
            var policyList = await http.RequestServices.GetRequiredService<Policies>().ListAsync(ct);
            var policyVersions = await db.PolicyVersions.AsNoTracking().OrderBy(v => v.PolicyId).ThenBy(v => v.Version).ToListAsync(ct);
            Text("policies.csv", Csv([["policy", "title", "version", "changes", "approved by", "approved at", "review due", "reviewed by", "reviewed at", "read by", "acknowledged", "must read"],
                .. policyVersions.Join(policyList, v => v.PolicyId, pl => pl.Id, (v, pl) => new object?[]
                {
                    pl.Key, pl.Title, v.Version, v.Changes, v.ApprovedBy, v.ApprovedAt?.ToString("u", CultureInfo.InvariantCulture), v.ReviewDue?.ToString("u", CultureInfo.InvariantCulture),
                    v.ReviewedBy, v.ReviewedAt?.ToString("u", CultureInfo.InvariantCulture), pl.Audience, pl.Version == v.Version ? pl.Acknowledged : null, pl.Version == v.Version ? pl.Readers : null,
                })]));
            var acknowledged = await db.PolicyAcknowledgements.AsNoTracking().Join(db.PolicyVersions.AsNoTracking(), a => a.PolicyVersionId, v => v.Id, (a, v) => new { a.UserId, a.At, v.PolicyId, v.Version })
                .Join(db.Users.AsNoTracking(), x => x.UserId, u => u.Id, (x, u) => new { u.UserName, x.At, x.PolicyId, x.Version }).OrderBy(x => x.At).ToListAsync(ct);
            var keys = policyList.ToDictionary(pl => pl.Id, pl => pl.Key);
            Text("policy-acknowledgements.csv", Csv([["policy", "version", "person", "at"],
                .. acknowledged.Select(x => new object?[] { keys.GetValueOrDefault(x.PolicyId), x.Version, x.UserName, x.At.ToString("u", CultureInfo.InvariantCulture) })]));
            foreach (var pl in policyList.Where(pl => pl.Version is not null))
            {
                Text($"policies/{pl.Key}-v{pl.Version}.md", policyVersions.Single(v => v.PolicyId == pl.Id && v.Version == pl.Version).Text);
            }
            var auditsView = await http.RequestServices.GetRequiredService<Audits>().AllAsync(ct);
            static string? U(DateTimeOffset? d) => d?.ToString("u", CultureInfo.InvariantCulture);
            Text("audits.csv", Csv([["id", "title", "kind", "framework", "scope", "auditor", "starts", "ends", "reported", "conclusion", "findings", "open"],
                .. auditsView.Audits.Select(a => new object?[] { a.Id, a.Title, a.Kind, a.Framework, a.Scope, a.Auditor, U(a.Starts), U(a.Ends), U(a.ReportedAt), a.Conclusion, a.Findings, a.OpenFindings })]));
            Text("findings.csv", Csv([["id", "audit", "title", "kind", "clause", "control", "owner", "due", "status", "correction", "root cause", "corrective action", "done by", "done at", "verified by", "verified at", "verification"],
                .. auditsView.Findings.Select(f => new object?[] { f.Id, f.Audit, f.Title, f.Kind, f.Clause, f.Control, f.Owner, U(f.Due), f.Status, f.Correction, f.RootCause, f.CorrectiveAction, f.DoneBy, U(f.DoneAt), f.VerifiedBy, U(f.VerifiedAt), f.Verification })]));
            Text("certificates.csv", Csv([["framework", "body", "number", "scope", "issued", "expires"],
                .. auditsView.Certificates.Select(c => new object?[] { c.Framework, c.Body, c.Number, c.Scope, U(c.Issued), U(c.Expires) })]));
            Text("waivers.csv", Csv([["control", "reason", "compensating", "approved by", "approved at", "until", "withdrawn"],
                .. auditsView.Waivers.Select(w => new object?[] { w.Control, w.Reason, w.Compensating, w.ApprovedBy, U(w.ApprovedAt), U(w.Until), U(w.WithdrawnAt) })]));
            var supplierList = await http.RequestServices.GetRequiredService<Suppliers>().ListAsync(ct);
            Text("suppliers.csv", Csv([["name", "kind", "service", "data", "location", "certifications", "agreements", "risk", "owner", "review due", "reviewed by", "reviewed at", "retired"],
                .. supplierList.Select(s => new object?[] { s.Name, s.Kind, s.Service, string.Join(" ", s.Data), s.Location, s.Certifications, s.Agreements, s.Risk, s.Owner, U(s.ReviewDue), s.ReviewedBy, U(s.ReviewedAt), U(s.RetiredAt) })]));
            var aiList = await http.RequestServices.GetRequiredService<AiSystems>().ListAsync(ct);
            Text("ai-systems.csv", Csv([["name", "kind", "purpose", "intended use", "affects", "class", "why", "annex III area", "role", "owner", "oversight", "evaluation", "assessed by", "assessed at", "in use since", "review due", "retired"],
                .. aiList.Select(s => new object?[] { s.Name, s.Kind, s.Purpose, s.IntendedUse, s.Affected, s.Class, s.ClassWhy, s.HighRiskArea, s.Role, s.Owner, s.Oversight, s.Evaluation, s.ImpactAssessedBy, U(s.ImpactAssessedAt), U(s.InUseSince), U(s.ReviewDue), U(s.RetiredAt) })]));
            foreach (var s in aiList.Where(s => s.ImpactAssessment is not null))
            {
                Text($"ai-impact/{s.Id}.md", $"# {s.Name}: impact assessment\n\nBy {s.ImpactAssessedBy}, {U(s.ImpactAssessedAt)}.\n\n{s.ImpactAssessment}\n");
            }
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
