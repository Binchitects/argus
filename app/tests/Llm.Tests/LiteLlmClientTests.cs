using System.Net;
using System.Text.Json;
using Llm.Api.Gateway;

namespace Llm.Tests;

/// <summary>The exact requests the app sends to LiteLLM.</summary>
public sealed class LiteLlmClientTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public List<(string Path, JsonElement Body)> Calls { get; } = [];

        /// <summary>What /end_user/info answers: null = 404 (no such end user).</summary>
        public string? EndUser { get; set; } = """{"user_id":"p@example.test","budget_id":"b-1"}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? default : JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement;
            Calls.Add((request.RequestUri!.PathAndQuery, body));
            if (request.RequestUri.AbsolutePath == "/end_user/info" && EndUser is null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":{"message":"does not exist"}}""") };
            }
            var answer = request.RequestUri.AbsolutePath switch
            {
                "/key/generate" => """{"key":"sk-new"}""",
                "/end_user/info" => EndUser!,
                "/budget/new" => """{"budget_id":"b-new"}""",
                _ => "{}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer) };
        }
    }

    private static (LiteLlmClient Client, Recorder Calls) Create()
    {
        var recorder = new Recorder();
        return (new LiteLlmClient(new HttpClient(recorder) { BaseAddress = new Uri("http://litellm:4000") }), recorder);
    }

    [Fact]
    public async Task Creating_a_gateway_user_never_mints_a_hidden_key()
    {
        var (client, recorder) = Create();
        await client.EnsureUserAsync("p@example.test");
        var (path, body) = Assert.Single(recorder.Calls);
        Assert.Equal("/user/new", path);
        Assert.False(body.GetProperty("auto_create_key").GetBoolean());
        Assert.Equal("p@example.test", body.GetProperty("user_id").GetString());
    }

    private static List<string> Paths(Recorder r) => [.. r.Calls.Select(c => c.Path.Split('?')[0])];

    [Fact]
    public async Task A_budget_changes_the_end_users_budget_where_it_is_kept()
    {
        var (client, recorder) = Create();
        await client.SetBudgetAsync("p@example.test", 0m);
        Assert.Equal(["/user/update", "/end_user/info", "/budget/update"], Paths(recorder));
        var update = recorder.Calls.Last().Body;
        Assert.Equal(("b-1", 0m), (update.GetProperty("budget_id").GetString(), update.GetProperty("max_budget").GetDecimal()));
    }

    [Fact]
    public async Task A_person_unknown_to_the_chat_path_is_created_with_the_budget()
    {
        var (client, recorder) = Create();
        recorder.EndUser = null;
        await client.SetBudgetAsync("p@example.test", 7.5m);
        Assert.Equal(["/user/update", "/end_user/info", "/end_user/new"], Paths(recorder));
        Assert.Equal(7.5m, recorder.Calls.Last().Body.GetProperty("max_budget").GetDecimal());
    }

    [Fact]
    public async Task A_person_without_a_budget_gets_one_linked()
    {
        var (client, recorder) = Create();
        recorder.EndUser = """{"user_id":"p@example.test","budget_id":null}""";
        await client.SetBudgetAsync("p@example.test", 3m);
        Assert.Equal(["/user/update", "/end_user/info", "/budget/new", "/end_user/update"], Paths(recorder));
        Assert.Equal("b-new", recorder.Calls.Last().Body.GetProperty("budget_id").GetString());
    }

    [Fact]
    public async Task Unlimited_clears_the_limit_and_creates_nothing_new()
    {
        var (client, recorder) = Create();
        await client.SetBudgetAsync("p@example.test", null);
        Assert.Equal(JsonValueKind.Null, recorder.Calls.Last().Body.GetProperty("max_budget").ValueKind);
        recorder.Calls.Clear();
        recorder.EndUser = """{"user_id":"p@example.test","budget_id":null}""";
        await client.SetBudgetAsync("p@example.test", null);
        Assert.Equal(["/user/update", "/end_user/info"], Paths(recorder));
    }

    [Fact]
    public async Task A_generated_key_carries_the_persons_alias()
    {
        var (client, recorder) = Create();
        Assert.Equal("sk-new", await client.GenerateKeyAsync("p@example.test", "app-p"));
        Assert.Equal("app-p", recorder.Calls.Single().Body.GetProperty("key_alias").GetString());
    }

    private sealed class Hangs : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task A_gateway_that_does_not_answer_is_reported_as_down()
    {
        var client = new LiteLlmClient(new HttpClient(new Hangs()) { BaseAddress = new Uri("http://litellm:4000"), Timeout = TimeSpan.FromMilliseconds(200) });
        var ex = await Assert.ThrowsAsync<GatewayException>(() => client.KeysAsync("p@example.test"));
        Assert.Contains("did not answer in time", ex.Message, StringComparison.Ordinal);
    }
}
