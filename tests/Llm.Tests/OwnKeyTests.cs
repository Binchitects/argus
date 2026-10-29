using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// A person's own API keys (Account → API keys): named, each with limits of its own under those an admin set on them,
/// each changed or revoked on its own, and kept so by the gateway's sync.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class OwnKeyTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp(FakeGateway gateway) =>
        app.Create(app.ConnectionStringFor("ownkeys_" + Guid.NewGuid().ToString("N")[..8]), gateway);

    private static async Task<(Guid Id, string Email, TestBrowser Person)> PersonAsync(WebApplicationFactory<Program> f, TestBrowser admin)
    {
        var name = "ok" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var person = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        return (made.GetProperty("id").GetGuid(), $"{name}@example.test", person);
    }

    [Fact]
    public async Task Keys_of_ones_own_are_named_limited_under_ones_limits_changed_and_revoked_one_by_one()
    {
        var gateway = new FakeGateway();
        await using var f = NewApp(gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (id, email, person) = await PersonAsync(f, admin);
        // The admin holds them to 60 requests a minute.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{id}/limits", UriKind.Relative), new { requestsPerMinute = 60, tokensPerMinute = (int?)null }));
        var main = gateway.KeysOf(email).Single();

        // A key for CI: 100 requests a minute asked, 60 held (theirs is tighter); 2 at once; $5 in 30 days.
        var made = await person.JsonAsync(await person.PostAsync("/api/account/keys/own", new { name = "CI", requestsPerMinute = 100, tokensPerMinute = 5000, parallel = 2, monthlyBudget = 5m }));
        var secret = made.GetProperty("apiKey").GetString()!;
        var ci = gateway.Keys[AnswerCache.HashOf(secret)];
        Assert.Equal(new KeyRate(60, 5000), ci.Rate);
        Assert.Equal(2, ci.MaxParallel);
        Assert.Equal(5m, ci.MaxBudget);
        var view = made.GetProperty("key");
        Assert.Equal(("CI", "own"), (view.GetProperty("name").GetString(), view.GetProperty("kind").GetString()));
        Assert.Equal(100, view.GetProperty("limits").GetProperty("requestsPerMinute").GetInt32());
        Assert.Equal(60, view.GetProperty("effective").GetProperty("requestsPerMinute").GetInt32());

        // A second, a name taken is refused, and a limit of 0 too.
        var laptop = (await person.JsonAsync(await person.PostAsync("/api/account/keys/own", new { name = "laptop", requestsPerMinute = 10 }))).GetProperty("apiKey").GetString()!;
        Assert.Equal(new KeyRate(10, null), gateway.Keys[AnswerCache.HashOf(laptop)].Rate);
        var taken = await person.PostAsync("/api/account/keys/own", new { name = "CI" });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains("named CI already", await taken.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await person.PostAsync("/api/account/keys/own", new { name = "zero", parallel = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await person.PostAsync("/api/account/keys/own", new { name = " " })).StatusCode);

        // Listed: the main key and the two of their own.
        var listed = (await person.JsonAsync(await person.GetAsync("/api/account/keys/own"))).GetProperty("keys").EnumerateArray().ToList();
        Assert.Equal(["Your key:main", "CI:own", "laptop:own"], listed.Select(k => $"{k.GetProperty("name").GetString()}:{k.GetProperty("kind").GetString()}"));

        // Changed: CI to 30 a minute, no budget; the gateway holds it so.
        var changed = await person.JsonAsync(await person.Http.PutAsJsonAsync(new Uri($"/api/account/keys/own/{ci.Token}", UriKind.Relative),
            new { name = "CI runners", requestsPerMinute = 30, tokensPerMinute = 5000, parallel = 2 }));
        Assert.Equal("CI runners", changed.GetProperty("name").GetString());
        Assert.Equal(new KeyRate(30, 5000), ci.Rate);
        Assert.Null(ci.MaxBudget);

        // The sync keeps each key's own: the person's limit lifted, CI stays at its 30, the main key has none.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{id}/limits", UriKind.Relative), new { requestsPerMinute = (int?)0, tokensPerMinute = (int?)null }));
        await using (var scope = f.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RateLimits>().SyncAsync();
            await scope.ServiceProvider.GetRequiredService<Llm.Api.Models.KeyAccess>().SyncAsync();
        }
        Assert.Equal(new KeyRate(30, 5000), ci.Rate);
        Assert.Equal(2, ci.MaxParallel);
        Assert.Equal(KeyRate.None, main.Rate);

        // Rotate replaces the main key only; revoking CI leaves laptop.
        var rotated = (await person.JsonAsync(await person.PostAsync("/api/account/keys/rotate"))).GetProperty("apiKey").GetString()!;
        Assert.True(gateway.Keys.ContainsKey(ci.Token));
        Assert.False(gateway.Keys.ContainsKey(main.Token));
        Assert.True(gateway.Keys.ContainsKey(AnswerCache.HashOf(rotated)));
        await StatusAssert.Is(HttpStatusCode.NoContent, await person.Http.DeleteAsync(new Uri($"/api/account/keys/own/{ci.Token}", UriKind.Relative)));
        Assert.False(gateway.Keys.ContainsKey(ci.Token));
        Assert.True(gateway.Keys.ContainsKey(AnswerCache.HashOf(laptop)));
        // Another person's key, or one not there, is not theirs to change.
        Assert.Equal(HttpStatusCode.NotFound, (await person.Http.DeleteAsync(new Uri($"/api/account/keys/own/{ci.Token}", UriKind.Relative))).StatusCode);
        var (_, _, other) = await PersonAsync(f, admin);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.DeleteAsync(new Uri($"/api/account/keys/own/{AnswerCache.HashOf(laptop)}", UriKind.Relative))).StatusCode);
        Assert.True(gateway.Keys.ContainsKey(AnswerCache.HashOf(laptop)));
    }

    [Fact]
    public async Task A_key_made_or_changed_while_a_sync_writes_the_persons_keys_waits_for_it_so_keeps_its_own_limits()
    {
        var gateway = new FakeGateway();
        await using var f = NewApp(gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (id, email, person) = await PersonAsync(f, admin);
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{id}/limits", UriKind.Relative), new { requestsPerMinute = 60, tokensPerMinute = (int?)null }));
        async Task SyncAsync()
        {
            await using var scope = f.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<RateLimits>().SyncAsync();
        }

        // Only a sync in the person's turn writes their keys' limits. One is held as it puts their main key right (the gateway
        // lost its limits): a key they make meanwhile waits for it (before, a sync that listed their keys after the new one was
        // made, but read their own keys' limits before it was saved, gave it their limits alone, not its own).
        var main = gateway.KeysOf(email).Single().Token;
        var fixing = new Hold();
        gateway.BeforeRate = fixing.For(main);
        gateway.Keys[main].Rate = KeyRate.None;
        var sync = SyncAsync();
        Task<HttpResponseMessage> making;
        try
        {
            await fixing.Held;
            making = person.PostAsync("/api/account/keys/own", new { name = "CI", requestsPerMinute = 100, tokensPerMinute = 1000 });
            await Task.WhenAny(making, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.False(making.IsCompleted);
        }
        finally
        {
            fixing.Release();
        }
        await sync;
        var token = AnswerCache.HashOf((await person.JsonAsync(await making)).GetProperty("apiKey").GetString()!);
        Assert.Equal(new KeyRate(60, 1000), gateway.Keys[token].Rate);

        // One is held as it puts the new key right (lost at the gateway too): a change meanwhile waits for it, then stays.
        var writing = new Hold();
        gateway.BeforeRate = writing.For(token);
        gateway.Keys[token].Rate = KeyRate.None;
        sync = SyncAsync();
        Task<HttpResponseMessage> changing;
        try
        {
            await writing.Held;
            changing = person.Http.PutAsJsonAsync(new Uri($"/api/account/keys/own/{token}", UriKind.Relative), new { requestsPerMinute = 20, tokensPerMinute = 2000 });
            await Task.WhenAny(changing, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.False(changing.IsCompleted);
        }
        finally
        {
            writing.Release();
        }
        await sync;
        await StatusAssert.Is(HttpStatusCode.OK, await changing);
        Assert.Equal(new KeyRate(20, 2000), gateway.Keys[token].Rate);
    }

    [Fact]
    public async Task A_keys_own_requests_at_once_hold_in_the_apps_line()
    {
        var gateway = new FakeGateway();
        await using var f = NewApp(gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, _, person) = await PersonAsync(f, admin);
        var one = (await person.JsonAsync(await person.PostAsync("/api/account/keys/own", new { name = "one at a time", parallel = 1 }))).GetProperty("apiKey").GetString()!;
        var free = (await person.JsonAsync(await person.PostAsync("/api/account/keys/own", new { name = "free" }))).GetProperty("apiKey").GetString()!;
        var line = f.Services.GetRequiredService<KeyLine>();
        using var first = await line.EnterAsync(one, CancellationToken.None);
        Assert.NotNull(first);
        // The second waits for the first (it would be refused only past the line's timeout).
        var second = line.EnterAsync(one, CancellationToken.None);
        await Task.Delay(200);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, line.PlacesFor(one));
        first!.Dispose();
        using (await second)
        {
            // The other key has everyone's places.
            using var a = await line.EnterAsync(free, CancellationToken.None);
            using var b = await line.EnterAsync(free, CancellationToken.None);
            Assert.NotNull(b);
        }
    }

    [Fact]
    public async Task Code_arena_says_whose_its_key_is_and_signing_out_revokes_a_key_it_made_not_the_main_one()
    {
        var gateway = new FakeGateway();
        await using var f = NewApp(gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "ca" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var person = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        var mainKey = made.GetProperty("apiKey").GetString()!;
        // As Code Arena's sign-in makes it.
        var machine = (await person.JsonAsync(await person.PostAsync("/api/account/keys/own", new { name = "Code Arena on laptop" }))).GetProperty("apiKey").GetString()!;
        HttpClient Arena(string key)
        {
            var http = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), AllowAutoRedirect = false, HandleCookies = false });
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            http.DefaultRequestHeaders.Add("X-Requested-With", "code-arena");
            return http;
        }

        var me = JsonDocument.Parse(await (await Arena(machine).GetAsync(new Uri("/api/code-arena/me", UriKind.Relative))).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal((name, false), (me.GetProperty("userName").GetString(), me.GetProperty("managesPeople").GetBoolean()));
        var adminKey = (await admin.JsonAsync(await admin.PostAsync("/api/account/keys/rotate"))).GetProperty("apiKey").GetString()!;
        var adminMe = JsonDocument.Parse(await (await Arena(adminKey).GetAsync(new Uri("/api/code-arena/me", UriKind.Relative))).Content.ReadAsStringAsync()).RootElement;
        Assert.True(adminMe.GetProperty("managesPeople").GetBoolean());

        // The main key is not Code Arena's to revoke; the machine's is.
        Assert.Equal(HttpStatusCode.Conflict, (await Arena(mainKey).PostAsync(new Uri("/api/code-arena/sign-out", UriKind.Relative), null)).StatusCode);
        Assert.True(gateway.Keys.ContainsKey(AnswerCache.HashOf(mainKey)));
        Assert.Equal(HttpStatusCode.NoContent, (await Arena(machine).PostAsync(new Uri("/api/code-arena/sign-out", UriKind.Relative), null)).StatusCode);
        Assert.False(gateway.Keys.ContainsKey(AnswerCache.HashOf(machine)));
    }

    [Fact]
    public async Task Making_keys_counts_with_rotating_them_an_hour()
    {
        var gateway = new FakeGateway();
        await using var f = NewApp(gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, _, person) = await PersonAsync(f, admin);
        for (var i = 0; i < Llm.Api.Identity.PeopleService.OwnKeysPerHour; i++)
        {
            await StatusAssert.Is(HttpStatusCode.OK, await person.PostAsync("/api/account/keys/own", new { name = $"k{i}" }));
        }
        var refused = await person.PostAsync("/api/account/keys/own", new { name = "one more" });
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await person.PostAsync("/api/account/keys/rotate")).StatusCode);
        Assert.Equal("too_many_keys", JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());
    }
}
