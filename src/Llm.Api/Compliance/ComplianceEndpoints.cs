using Llm.Api.Access;
using Llm.Core.Access;

namespace Llm.Api.Compliance;

/// <summary>A control as Admin → Compliance shows it: as controls.yaml has it, with its state from its app checks.</summary>
/// <param name="State">The worst of its app checks; null when it has none (its live checks run from outside).</param>
public sealed record ControlView(string Id, string Title, string Meets, IReadOnlyDictionary<string, List<string>> Frameworks, IReadOnlyList<string> Docs,
    IReadOnlyList<string> Tests, IReadOnlyList<string> Checks, string? Operator, string? State);

public sealed record ComplianceView(IReadOnlyDictionary<string, string> Frameworks, IReadOnlyList<ControlView> Controls, IReadOnlyList<CheckResult> Checks,
    DateTimeOffset CheckedAt);

public static class ComplianceEndpoints
{
    public static void AddCompliance(IServiceCollection services) => services.AddScoped<ComplianceChecks>();

    public static void MapCompliance(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/compliance").RequirePermissions(Permissions.ComplianceRead, Permissions.ComplianceManage);
        g.MapGet("", async (ComplianceChecks checks, TimeProvider clock, CancellationToken ct) => Results.Ok(View(await checks.RunAsync(ct), clock.GetUtcNow())));
    }

    private static readonly string[] Worst = [ComplianceChecks.Fail, ComplianceChecks.Warn, ComplianceChecks.Unknown, ComplianceChecks.Ok];

    public static ComplianceView View(IReadOnlyList<CheckResult> results, DateTimeOffset at)
    {
        var file = ComplianceControls.All;
        var byId = results.ToDictionary(r => "app:" + r.Id);
        var controls = file.Controls.Select(c =>
        {
            var states = c.Checks.Where(byId.ContainsKey).Select(k => byId[k].State).ToList();
            return new ControlView(c.Id, c.Title, c.Meets, c.Frameworks, c.Docs, c.Tests, c.Checks, c.Operator,
                states.Count == 0 ? null : Worst.First(states.Contains));
        }).ToList();
        return new ComplianceView(file.Frameworks, controls, results, at);
    }
}
