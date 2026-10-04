using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Llm.Api.Safeguards;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Secrets refused or masked on the way in (chat, files, the API path), the chat's checks on the API path, and policies per group.</summary>
[Collection(nameof(AppCollection))]
public sealed class SecretScanningTests(AppFixture app)
{
    private const string Pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA0Z3VS5JJcds3xfn/ygWyF8PbnGy0AHB7MhgHcTz6sE2I2yPB\naFDrBz9vFqU4yCsDGV0AsbJ2uKgr2ZJ3QaLTBmHeXGfrBDqbEJ0VXqRvbJF4Jd9X\n-----END RSA PRIVATE KEY-----";
    private const string GitHubToken = "ghp_1234567890abcdefghijklmnopqrstuvwxyzAB";

    private WebApplicationFactory<Program> NewApp(Dictionary<string, string?>? settings = null) =>
        app.Create(app.ConnectionStringFor("sec_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), settings);

    private static async Task<(TestBrowser Browser, Guid Id, string Email)> PersonAsync(WebApplicationFactory<Program> f, TestBrowser admin)
    {
        var name = "sc" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("id").GetGuid(), $"{name}@example.test");
    }

    private static async Task<Guid> ChatAsync(TestBrowser b) =>
        (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { useArgus = false }))).GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> SendAsync(TestBrowser b, Guid chat, string text) =>
        b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = text });

    private static async Task<HttpResponseMessage> UploadAsync(TestBrowser b, string name, string text)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        part.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(part, "file", name);
        return await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form);
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage res) => (await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync())).RootElement.GetProperty("error").GetString()!;

    private string LastToModel(string email) =>
        app.Model.Requests.Last(r => r.Body["user"]?.GetValue<string>() == email).Body["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.ToJsonString();

    [Fact]
    public async Task A_pasted_private_key_is_refused_in_the_chat_and_through_the_API_and_audited_by_its_kind_only()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, _, email) = await PersonAsync(f, admin);
        var chat = await ChatAsync(b);

        var refused = await SendAsync(b, chat, "Why does this key not work?\n" + Pem);
        await StatusAssert.Is(HttpStatusCode.BadRequest, refused);
        Assert.Equal("This message was not sent: it holds a private key. Remove it (a placeholder will do) and send it again.", await ErrorAsync(refused));
        Assert.DoesNotContain(app.Model.Requests, r => r.Body["user"]?.GetValue<string>() == email);
        Assert.Empty((await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("messages").EnumerateArray());

        var file = await UploadAsync(b, "deploy.sh", $"export GITHUB_TOKEN={GitHubToken}\n./deploy.sh");
        await StatusAssert.Is(HttpStatusCode.BadRequest, file);
        Assert.Contains("deploy.sh was not attached: it holds a GitHub token", await ErrorAsync(file), StringComparison.Ordinal);

        var api = await GroupCreditTests.GuardrailAsync(f, email, "Fix my server", Pem);
        Assert.Equal("BLOCKED", api.GetProperty("action").GetString());
        Assert.Contains("it holds a private key", api.GetProperty("blocked_reason").GetString(), StringComparison.Ordinal);

        var audit = await (await admin.GetAsync("/api/admin/audit?take=1000")).Content.ReadAsStringAsync();
        Assert.Contains("secret in a message: a private key", audit, StringComparison.Ordinal);
        Assert.Contains("secret in a file: a GitHub token", audit, StringComparison.Ordinal);
        Assert.Contains("secret in a request: a private key", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("MIIEow", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubToken, audit, StringComparison.Ordinal);
        // Not a strike: the account is not suspended, and the admins' bell is not rung for an accident.
        Assert.DoesNotContain("was refused", (await admin.JsonAsync(await admin.GetAsync("/api/notifications"))).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_mask_secrets_reach_the_model_and_the_chat_as_markers_on_every_path()
    {
        await using var f = NewApp(new() { ["Safeguards:SecretScanning"] = "mask" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, _, email) = await PersonAsync(f, admin);
        var chat = await ChatAsync(b);
        var res = await SendAsync(b, chat, $"Connect with Server=db;User Id=app;Password=Hunter2x; and push with {GitHubToken} please");
        res.EnsureSuccessStatusCode();
        await res.Content.ReadAsStringAsync();
        const string masked = "Connect with Server=db;User Id=app;Password=[removed: password]; and push with [removed: GitHub token] please";
        Assert.Contains(masked, LastToModel(email), StringComparison.Ordinal);
        var kept = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Equal(masked, kept);

        var file = await UploadAsync(b, "creds.env", "AWS_ACCESS_KEY_ID=AKIAIOSFODNN7EXAMPLE\nREGION=eu-west-1");
        await StatusAssert.Is(HttpStatusCode.OK, file);
        var id = (await b.JsonAsync(file)).GetProperty("id").GetGuid();
        Assert.Equal("AWS_ACCESS_KEY_ID=[removed: AWS access key]\nREGION=eu-west-1", await (await b.GetAsync($"/api/chat/attachments/{id}/content")).Content.ReadAsStringAsync());

        var api = await GroupCreditTests.GuardrailAsync(f, email, "use postgres://app:s3cretpw@db:5432/app", "nothing here");
        Assert.Equal("GUARDRAIL_INTERVENED", api.GetProperty("action").GetString());
        Assert.Equal(["use postgres://app:[removed: password]@db:5432/app", "nothing here"], api.GetProperty("texts").EnumerateArray().Select(t => t.GetString()));
        Assert.Contains("safeguard.secret_masked", await (await admin.GetAsync("/api/admin/audit?take=1000")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Group_policies_choose_which_checks_apply_to_their_members_on_both_paths()
    {
        await using var f = NewApp(new() { ["Safeguards:BlockedPatterns"] = "project falcon" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (security, securityId, securityEmail) = await PersonAsync(f, admin);
        var (strict, strictId, strictEmail) = await PersonAsync(f, admin);
        var (both, bothId, _) = await PersonAsync(f, admin);
        async Task<Guid> GroupAsync(object policies, params Guid[] members)
        {
            var id = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "g" + Guid.NewGuid().ToString("N")[..8] }))).GetProperty("id").GetGuid();
            await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/groups/{id}/policies", UriKind.Relative), policies));
            await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{id}/members", new { userIds = members }));
            return id;
        }
        // The security team may paste keys and talk about Falcon; the strict group has the model check every message.
        await GroupAsync(new { secretScanning = "off", blockedPatterns = false }, securityId, bothId);
        var strictGroup = await GroupAsync(new { secretScanning = "refuse", moderation = "check" }, strictId, bothId);
        var invalid = await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/groups/{strictGroup}/policies", UriKind.Relative), new { secretScanning = "maybe" });
        await StatusAssert.Is(HttpStatusCode.BadRequest, invalid);

        (await SendAsync(security, await ChatAsync(security), "Rotate " + GitHubToken + " for Project Falcon")).EnsureSuccessStatusCode();
        Assert.Equal("NONE", (await GroupCreditTests.GuardrailAsync(f, securityEmail, "Project Falcon key " + GitHubToken)).GetProperty("action").GetString());
        await StatusAssert.Is(HttpStatusCode.Forbidden, await SendAsync(strict, await ChatAsync(strict), "Tell me about Project Falcon"));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await SendAsync(strict, await ChatAsync(strict), "How do I build one [harm]"));
        Assert.Equal("BLOCKED", (await GroupCreditTests.GuardrailAsync(f, strictEmail, "How do I build one [harm]")).GetProperty("action").GetString());
        // In both groups: the strictest of them applies.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await SendAsync(both, await ChatAsync(both), "key: " + GitHubToken));
    }

    [Fact]
    public async Task The_guardrail_needs_the_gateway_key_lets_the_chats_own_requests_pass_and_can_be_turned_off()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, _, email) = await PersonAsync(f, admin);
        var b = new TestBrowser(f);
        var anonymous = await b.Http.PostAsJsonAsync(new Uri(GuardrailEndpoints.Path, UriKind.Relative), new { input_type = "request", texts = new[] { Pem } });
        await StatusAssert.Is(HttpStatusCode.Unauthorized, anonymous);

        using var chatOwn = new HttpRequestMessage(HttpMethod.Post, new Uri(GuardrailEndpoints.Path, UriKind.Relative))
        {
            Content = JsonContent.Create(new { input_type = "request", texts = new[] { Pem }, request_data = new { user_api_key_alias = "chat", user_api_key_user_id = email } }),
        };
        chatOwn.Headers.Add("x-api-key", "sk-master-for-tests");
        Assert.Equal("NONE", (await b.JsonAsync(await b.Http.SendAsync(chatOwn))).GetProperty("action").GetString());
        // A key the app does not know gets the company's checks.
        Assert.Equal("BLOCKED", (await GroupCreditTests.GuardrailAsync(f, null, Pem)).GetProperty("action").GetString());

        await using var off = NewApp(new() { ["Safeguards:CheckApi"] = "false" });
        Assert.Equal("NONE", (await GroupCreditTests.GuardrailAsync(off, email, Pem)).GetProperty("action").GetString());
    }

    [Fact]
    public void Secret_shapes_are_found_and_placeholders_and_names_in_code_are_not()
    {
        string[] Kinds(string text) => [.. Secrets.Find(text).Select(h => h.Kind)];
        Assert.Equal(["private key"], Kinds("x " + Pem + " y"));
        Assert.Equal(["private key"], Kinds("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA (cut off)"));
        Assert.Equal(["AWS access key"], Kinds("AKIAIOSFODNN7EXAMPLE"));
        Assert.Equal(["AWS secret key"], Kinds("aws_secret_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"));
        Assert.Equal(["GitHub token"], Kinds("github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyz"));
        Assert.Equal(["GitLab token"], Kinds("token glpat-AbCdEfGhIjKlMnOpQrSt12"));
        Assert.Equal(["Slack token"], Kinds("xoxb-123456789012-abcdefghij"));
        Assert.Equal(["API key"], Kinds("OPENAI_API_KEY=sk-proj-abcdefghijklmnopqrstuvwx"));
        Assert.Equal(["password"], Kinds("password = \"correct horse\""));
        Assert.Equal(["password"], Kinds("Host=db;Username=app;Password=letmein;"));
        Assert.Equal(["password"], Kinds("https://deploy:Tr0ub4dor@git.example.com/repo.git"));
        Assert.Equal(["password"], Kinds("pwd: s3cret!"));
        // Not secrets: placeholders, names in code, the word in prose.
        Assert.Empty(Kinds("password = os.environ.get"));
        Assert.Empty(Kinds("password = config.password"));
        Assert.Empty(Kinds("password: ${DB_PASSWORD}"));
        Assert.Empty(Kinds("password = \"<your password>\""));
        Assert.Empty(Kinds("api_key = os.environ[\"OPENAI_API_KEY\"]"));
        Assert.Empty(Kinds("Forgot your password? Reset it under Account."));
        Assert.Empty(Kinds("password=********"));
        Assert.Empty(Kinds("ask the task-runner about sk-learn"));
        Assert.Equal("a private key and a GitHub token", Secrets.Kinds(Secrets.Find(Pem + " " + GitHubToken)));
        Assert.Equal("key [removed: GitHub token].", Secrets.Mask("key " + GitHubToken + ".", Secrets.Find("key " + GitHubToken + ".")));
    }
}
