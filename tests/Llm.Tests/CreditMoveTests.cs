using Llm.Api.Gateway;
using Llm.Api.Models;
using Llm.Core.Access;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>From the gateway's budgets to the app's credits, once; and keys blocked as their people are.</summary>
[Collection(nameof(AppCollection))]
public sealed class CreditMoveTests(AppFixture app)
{
    [Fact]
    public async Task The_gateways_budget_becomes_every_kinds_credit_once_and_the_gateway_holds_none()
    {
        var gateway = new FakeGateway();
        await using var f = app.Create(app.ConnectionStringFor("move_" + Guid.NewGuid().ToString("N")[..8]), gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        static string Person(string name) => $"{name}@example.test";
        var (had, own, none) = (Person("had"), Person("own"), Person("none"));
        foreach (var name in new[] { "had", "none", "own" })
        {
            await admin.PostAsync("/api/admin/people", new { userName = name + Guid.NewGuid().ToString("N")[..4], email = Person(name) });
        }
        // As v5.4 left them: budgets at the gateway (one person already has credits of their own here).
        gateway.Budgets[Person("had")] = 7.5m;
        gateway.Budgets[Person("own")] = 3m;
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Users.Where(u => u.Email == own).ExecuteUpdateAsync(u => u.SetProperty(x => x.ChatCredit, 1m));
            await db.Settings.Where(x => x.Key == CreditMove.Done).ExecuteDeleteAsync();
            Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<CreditMove>().RunAsync());
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<CreditMove>().RunAsync());
            // A budget the gateway gives later (an edited litellm.yaml's default) is cleared again, and moved nowhere.
            gateway.Budgets[had] = 50m;
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<CreditMove>().RunAsync());
        }
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hadUser = await db.Users.AsNoTracking().SingleAsync(u => u.Email == had);
            Assert.All(Credits.Kinds, k => Assert.Equal(7.5m, Credits.Of(hadUser, k)));
            var ownUser = await db.Users.AsNoTracking().SingleAsync(u => u.Email == own);
            Assert.Equal(1m, ownUser.ChatCredit);
            Assert.Null(ownUser.ApiCredit);
            var noneUser = await db.Users.AsNoTracking().SingleAsync(u => u.Email == none);
            Assert.All(Credits.Kinds, k => Assert.Null(Credits.Of(noneUser, k)));
        }
        // The gateway holds no budget any more.
        Assert.All(new[] { "had", "own" }, n => Assert.Null(gateway.Budgets[Person(n)]));
    }

    [Fact]
    public async Task Keys_are_blocked_while_their_person_has_no_API_access_whatever_failed_before()
    {
        var gateway = new FakeGateway();
        await using var f = app.Create(app.ConnectionStringFor("blocks_" + Guid.NewGuid().ToString("N")[..8]), gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "b" + Guid.NewGuid().ToString("N")[..8];
        await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" });
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Taken while the gateway could not be reached: the person's flag is set, their key is not blocked yet.
        await db.Users.Where(u => u.UserName == name).ExecuteUpdateAsync(u => u.SetProperty(x => x.ApiOff, true));
        var keys = scope.ServiceProvider.GetRequiredService<KeyAccess>();
        Assert.Equal(1, await keys.BlocksAsync());
        Assert.All(gateway.KeysOf($"{name}@example.test"), k => Assert.True(k.Blocked));
        Assert.Equal(0, await keys.BlocksAsync());
        await db.Users.Where(u => u.UserName == name).ExecuteUpdateAsync(u => u.SetProperty(x => x.ApiOff, false));
        Assert.Equal(1, await keys.BlocksAsync());
        Assert.All(gateway.KeysOf($"{name}@example.test"), k => Assert.False(k.Blocked));
    }
}
