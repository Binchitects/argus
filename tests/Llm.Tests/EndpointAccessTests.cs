using System.Net;
using System.Text.RegularExpressions;
using Llm.Api.Access;
using Llm.Core.Access;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Every route says who may call it (a permission, any person signed in, or nobody, with the reason), and a
/// permission is held to: a person without it gets 403 on every route that needs it, an Owner never does.
/// Read from the routes themselves, so a new route is covered as soon as it is added.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed partial class EndpointAccessTests(AppFixture app)
{
    private sealed record Route(string Method, string Pattern, string? Permission);

    private List<Route> Routes()
    {
        var all = new List<Route>();
        var missing = new List<string>();
        foreach (var endpoint in app.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = "/" + (endpoint.RoutePattern.RawText ?? "").TrimStart('/');
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
            var permission = endpoint.Metadata.GetMetadata<PermissionMetadata>()?.Permission;
            if (permission is null && endpoint.Metadata.GetMetadata<PersonOnlyMetadata>() is null && endpoint.Metadata.GetMetadata<AnonymousMetadata>() is null)
            {
                missing.Add($"{string.Join(',', methods)} {pattern}");
            }
            all.AddRange(methods.Select(m => new Route(m, pattern, permission)));
        }
        Assert.True(missing.Count == 0, "Routes that do not say who may call them (RequirePermission, PersonOnly or AllowAnonymous with a reason):\n" + string.Join('\n', missing));
        return all;
    }

    [Fact]
    public void Every_route_says_who_may_call_it() => Assert.NotEmpty(Routes());

    [GeneratedRegex(@"\{(\*{0,2})([^}:]+)(:[^}]+)?\}")]
    private static partial Regex Parameter();

    /// <summary>A path for the pattern, with a value of the right shape in each parameter.</summary>
    private static string Path(string pattern) => Parameter().Replace(pattern, m => m.Groups[3].Value switch
    {
        ":guid" => Guid.NewGuid().ToString(),
        ":long" or ":int" => "1",
        _ => "x",
    });

    [Fact]
    public async Task A_person_without_a_permission_is_refused_everywhere_it_is_needed()
    {
        var routes = Routes().Where(r => r.Permission is not null && r.Method != "*").ToList();
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "ea" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var member = await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!);
        var wrong = new List<string>();
        foreach (var r in routes.Where(r => !Permissions.IsFeature(r.Permission!)))
        {
            using var request = new HttpRequestMessage(new HttpMethod(r.Method), Path(r.Pattern));
            if (r.Method is not ("GET" or "HEAD" or "DELETE"))
            {
                request.Content = JsonContent.Create(new { });
            }
            var res = await member.Http.SendAsync(request);
            if (res.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.NotFound))
            {
                wrong.Add($"{r.Method} {r.Pattern} ({r.Permission}): {(int)res.StatusCode}");
            }
        }
        Assert.True(wrong.Count == 0, "A member reached:\n" + string.Join('\n', wrong));

        // An Owner is never refused: the admin's reads (a read changes nothing, so every one is tried).
        foreach (var r in routes.Where(r => r.Method == "GET"))
        {
            var res = await admin.GetAsync(Path(r.Pattern));
            Assert.True(res.StatusCode != HttpStatusCode.Forbidden, $"The admin was refused GET {r.Pattern}");
        }
    }

    [Fact]
    public async Task A_role_reaches_the_reads_of_its_permissions_and_no_others()
    {
        var routes = Routes().Where(r => r.Method == "GET" && r.Permission is { } p && !Permissions.IsFeature(p) && !r.Pattern.Contains('{', StringComparison.Ordinal)).ToList();
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "au" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var id = made.GetProperty("id").GetGuid();
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var access = scope.ServiceProvider.GetRequiredService<AccessService>();
            await access.ChangeAsync(null, async edit =>
            {
                var auditor = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(edit.Db.AccessRoles, r => r.Key == BuiltInRoles.Auditor);
                edit.Add(new AccessAssignment { RoleId = auditor.Id, SubjectKind = SubjectKind.Person, SubjectId = id });
            });
        }
        var auditorBrowser = await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!);
        var reads = BuiltInRoles.Find(BuiltInRoles.Auditor)!.Permissions;
        foreach (var r in routes)
        {
            var res = await auditorBrowser.GetAsync(r.Pattern);
            if (reads.Contains(r.Permission!))
            {
                Assert.True(res.StatusCode != HttpStatusCode.Forbidden, $"The auditor was refused GET {r.Pattern} ({r.Permission})");
            }
            else
            {
                Assert.True(res.StatusCode == HttpStatusCode.Forbidden, $"The auditor reached GET {r.Pattern} ({r.Permission}): {(int)res.StatusCode}");
            }
        }
    }

    [Fact]
    public void Each_built_in_role_holds_exactly_its_permissions()
    {
        var roles = BuiltInRoles.All.ToDictionary(r => r.Key, r => new AccessRole { Key = r.Key, Name = r.Name, BuiltIn = true, Permissions = [.. r.Permissions] });
        var now = DateTimeOffset.UtcNow;
        foreach (var role in BuiltInRoles.All)
        {
            var person = Guid.NewGuid();
            // Without the everyone-holds-Member assignment, so each role is alone.
            var world = new AccessSnapshot(1, roles.Values, [new AccessAssignment { RoleId = roles[role.Key].Id, SubjectKind = SubjectKind.Person, SubjectId = person }], []);
            foreach (var p in Permissions.All)
            {
                Assert.True(role.Permissions.Contains(p.Key) == world.Check(Principal.Of(person), p.Key, now).Allowed, $"{role.Name} and {p.Key}");
            }
        }
        Assert.All(BuiltInRoles.Find(BuiltInRoles.Auditor)!.Permissions, p => Assert.EndsWith(".read", p, StringComparison.Ordinal));
        Assert.Equal(Permissions.All.Count, BuiltInRoles.Find(BuiltInRoles.Owner)!.Permissions.Count);
    }
}
