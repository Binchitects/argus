using System.Net;
using System.Net.Http.Json;
using Llm.Api.Oidc;

namespace Llm.Tests;

/// <summary>Settings → Network access: the allow and block lists Traefik asks about before every request, and no list that shuts out its own saver.</summary>
[Collection(nameof(AppCollection))]
public sealed class AddressFilterTests(AppFixture app)
{
    [Fact]
    public void A_list_takes_addresses_and_ranges_of_both_families_with_comments_and_says_the_first_that_is_not_one()
    {
        var (ranges, wrong) = AddressFilter.Parse("10.0.0.0/8\n192.168.1.20, 2001:db8::/32  # the office\n\n# a whole line\n");
        Assert.Null(wrong);
        Assert.Equal(3, ranges.Count);
        Assert.Equal("intranet", AddressFilter.Parse("10.0.0.0/8\nintranet\n").Wrong);
        Assert.Equal("10.0.0.0/33", AddressFilter.Parse("10.0.0.0/33").Wrong);

        IPAddress Ip(string s) => IPAddress.Parse(s);
        var allow = AddressFilter.Parse("10.0.0.0/8").Ranges;
        var block = AddressFilter.Parse("10.9.9.9").Ranges;
        Assert.Null(AddressFilter.Refusal(Ip("10.1.2.3"), allow, block));
        Assert.Equal("10.9.9.9 is blocked", AddressFilter.Refusal(Ip("10.9.9.9"), allow, block));
        Assert.Equal("172.16.0.1 is not on the allow list", AddressFilter.Refusal(Ip("172.16.0.1"), allow, block));
        // An IPv4 address as an IPv6 socket gives it, and no allow list at all.
        Assert.Equal("10.9.9.9 is blocked", AddressFilter.Refusal(Ip("::ffff:10.9.9.9"), [], block));
        Assert.Null(AddressFilter.Refusal(Ip("203.0.113.7"), [], block));
    }

    [Fact]
    public async Task Traefik_is_told_which_addresses_may_come_in_and_a_list_that_would_shut_out_its_saver_is_refused()
    {
        await using var f = app.Create(app.ConnectionStringFor("addresses_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        // The admin's address, as Traefik passes it on.
        admin.Http.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2.10");
        var traefik = f.CreateClient();
        async Task<HttpStatusCode> Ask(string address)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, AddressFilter.Path);
            req.Headers.Add("X-Forwarded-For", address);
            return (await traefik.SendAsync(req)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.NoContent, await Ask("203.0.113.7"));

        // A list without the saver's address is refused, and nothing is saved.
        var refused = await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = AddressFilter.AllowKey, value = "10.0.0.0/8" } } });
        await StatusAssert.Is(HttpStatusCode.BadRequest, refused);
        Assert.Contains("your own address would be refused", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var wrong = await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = AddressFilter.BlockKey, value = "not-an-address" } } });
        await StatusAssert.Is(HttpStatusCode.BadRequest, wrong);
        Assert.Contains("not-an-address", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A block list without the saver: saved, and Traefik refuses those addresses at once.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = AddressFilter.BlockKey, value = "203.0.113.0/24  # a scanner\n198.51.100.9" } } }));
        Assert.Equal(HttpStatusCode.Forbidden, await Ask("203.0.113.7"));
        Assert.Equal(HttpStatusCode.Forbidden, await Ask("198.51.100.9"));
        Assert.Equal(HttpStatusCode.NoContent, await Ask("192.0.2.1"));
    }
}
