using System.Net;
using System.Net.Http.Json;
using Llm.Api.Operations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// A GitLab on the company's own CA: a person links their GitLab token (and with it Fix it, Code Studio, their apps)
/// over a real TLS connection once the CA is given, in Settings or at install, without a restart; code-intel is given the
/// same CA in the file it reads; and a certificate of another CA, or for another name, is still refused.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class CompanyCaTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp(string gitlabUrl, string directory, string? caFile = null) =>
        app.Create(app.ConnectionStringFor("companyca_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-company-ca-tests", ["GitLab:Url"] = gitlabUrl, ["Auth:DirectoryFile"] = Path.Combine(directory, "users.yml"),
            ["Tls:CompanyCaFile"] = caFile,
        // GitLab's own client as it runs, not the fixture's fake: the certificate is checked on a real connection.
        }, s => s.AddHttpClient(Llm.Api.Schedules.GitLabBot.Client).ConfigurePrimaryHttpMessageHandler(CompanyCa.Handler));

    private static async Task<TestBrowser> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await KnowledgeTests.AdminAsync(f);
        return (await KnowledgeTests.PersonAsync(f, admin, "ca" + Guid.NewGuid().ToString("N")[..8])).Browser;
    }

    private static Task<HttpResponseMessage> Link(TestBrowser b) => b.Http.PutAsJsonAsync(new Uri("/api/fixes/token", UriKind.Relative), new { token = "person-token" });

    [Fact]
    [Trait("Control", "NET-01")]
    public async Task A_person_links_their_GitLab_token_once_the_company_CA_is_given_in_Settings_and_CodeIntel_gets_it_too()
    {
        using var ca = TestCertificates.Ca("Test Company Root CA");
        using var certificate = TestCertificates.Server(ca, "localhost");
        await using var gitlab = await TlsServer.StartAsync(certificate, new FakeMcp(), "gitlab.test");
        var directory = Directory.CreateTempSubdirectory("company-ca-").FullName;
        await using var f = NewApp(gitlab.Url(""), directory);
        var admin = await KnowledgeTests.AdminAsync(f);
        var kim = await PersonAsync(f);

        // Before: the connection is refused, as people saw it, and they are told what to do.
        var refused = await Link(kim);
        await StatusAssert.Is(HttpStatusCode.BadRequest, refused);
        Assert.Contains("GitLab's certificate is not trusted", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, gitlab.Requests);

        // Something that is not a CA is refused, and nothing changes.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = "Tls:CompanyCa", value = "not a certificate" } } }));

        // The CA given in Settings: trusted at once, without a restart.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = "Tls:CompanyCa", value = TestCertificates.Pem(ca) } } }));
        var linked = await kim.JsonAsync(await Link(kim));
        Assert.Equal("kim", linked.GetProperty("account").GetString());

        // code-intel reads the same CA from the file beside the directory, written as the CA changed.
        var shared = await File.ReadAllTextAsync(Path.Combine(directory, CompanyCa.FileName));
        Assert.Contains(TestCertificates.Pem(ca).Trim().Split('\n')[1], shared, StringComparison.Ordinal);

        // A GitLab with another CA's certificate is still refused: the company CA is added, nothing is let through.
        using var other = TestCertificates.Ca("Someone Else's CA");
        using var impostor = TestCertificates.Server(other, "localhost");
        await using var fake = await TlsServer.StartAsync(impostor, new FakeMcp(), "gitlab.test");
        var client = f.Services.GetRequiredService<IHttpClientFactory>().CreateClient(Llm.Api.Schedules.GitLabBot.Client);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(new Uri(fake.Url("/api/v4/user"))));
        // Nor one for another name, though its CA is trusted.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(new Uri(gitlab.Url("/api/v4/user", "127.0.0.1"))));
    }

    [Fact]
    public async Task The_CA_given_at_install_is_trusted_without_Settings()
    {
        using var ca = TestCertificates.Ca("Install Root CA");
        using var certificate = TestCertificates.Server(ca, "localhost");
        await using var gitlab = await TlsServer.StartAsync(certificate, new FakeMcp(), "gitlab.test");
        var directory = Directory.CreateTempSubdirectory("company-ca-").FullName;
        var file = Path.Combine(directory, "install-ca.pem");
        await File.WriteAllTextAsync(file, TestCertificates.Pem(ca));
        await using var f = NewApp(gitlab.Url(""), directory, file);
        var kim = await PersonAsync(f);
        Assert.Equal("kim", (await kim.JsonAsync(await Link(kim))).GetProperty("account").GetString());
        // An empty install file (the stack mounts one when none is given) trusts nothing more.
        await File.WriteAllTextAsync(file, "");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
        Assert.Empty(f.Services.GetRequiredService<CompanyCa>().Cas());
    }
}
