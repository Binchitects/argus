using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// API keys through the app's own gateway (Gateway:Mode own): a request let in is answered by the engine and booked in the
/// ledger as the key's; one refused is refused before anything is sent, with the words the clients read (an unknown key,
/// an unknown model, the requests a minute with Retry-After and the rate-limit headers, the key's own budget).
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class AdmissionTests(AppFixture app)
{
    private static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Res)> AskAsync(WebApplicationFactory<Program> f, string? key, string text, string model = "Qwen3.8-Flash-Next")
    {
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/chat/completions", UriKind.Relative))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model, stream = true, messages = new object[] { new { role = "user", content = text } } }), Encoding.UTF8, "application/json"),
        };
        if (key is not null)
        {
            req.Headers.Authorization = new("Bearer", key);
        }
        var res = await client.SendAsync(req);
        return (res.StatusCode, await res.Content.ReadAsStringAsync(), res);
    }

    [Fact]
    public async Task A_key_let_in_is_answered_by_the_engine_and_booked_as_its_own_and_refusals_say_what_the_clients_read()
    {
        var cs = app.ConnectionStringFor("admit_" + Guid.NewGuid().ToString("N")[..8]);
        await using var f = app.Create(cs, new FakeGateway(), new Dictionary<string, string?> { ["Gateway:Mode"] = "own", ["Gateway:KeyCacheSeconds"] = "0" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "ad" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var key = made.GetProperty("apiKey").GetString()!;
        var id = made.GetProperty("id").GetGuid();

        // Let in: the engine answers, nothing of the app's own fields reaches it.
        var before = app.Model.Requests.Count;
        var ok = await AskAsync(f, key, "Name a prime number.");
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Contains("Answer to:", ok.Body, StringComparison.Ordinal);
        Assert.Contains(app.Model.Requests.Skip(before), r => r.Body["messages"]!.ToJsonString().Contains("prime", StringComparison.Ordinal) && r.Body["metadata"] is null);

        // Booked as the key's request, with its tokens.
        var gateway = f.Services.GetRequiredService<Llm.Api.Gateway.ModelGateway>();
        Llm.Core.Gateway.GatewayRequest? row = null;
        for (var i = 0; i < 100 && row is null; i++)
        {
            await gateway.FlushAsync(default);
            await using var scope = f.Services.CreateAsyncScope();
            row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().GatewayRequests.AsNoTracking()
                .FirstOrDefaultAsync(r => r.UserId == id && r.Status == Llm.Core.Gateway.GatewayRequest.Done);
            if (row is null)
            {
                await Task.Delay(50);
            }
        }
        Assert.NotNull(row);
        Assert.Equal(("api", "api", 100), (row.Surface, row.CreditKind, row.PromptTokens));
        Assert.Equal(64, row.KeyHash!.Trim().Length);

        // Refused before anything is sent.
        var unknown = await AskAsync(f, "sk-not-a-key", "hello");
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.Status);
        Assert.Contains("invalid_api_key", unknown.Body, StringComparison.Ordinal);
        var noModel = await AskAsync(f, key, "hello", model: "no-such-model");
        Assert.Equal(HttpStatusCode.NotFound, noModel.Status);
        Assert.Contains("model_not_found", noModel.Body, StringComparison.Ordinal);

        // A request a minute: the second is refused with when to ask again, as Code Arena and the OpenAI clients read it.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{id}/limits", UriKind.Relative), new { requestsPerMinute = 1, tokensPerMinute = (int?)null }));
        var first = await AskAsync(f, key, "one");
        var second = await AskAsync(f, key, "two");
        // The minute may have turned between them: then the first of the next minute passed, and a third is refused.
        var limited = second.Status == HttpStatusCode.TooManyRequests ? second : await AskAsync(f, key, "three");
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Contains("Limit type: requests. Current limit: 1", limited.Body, StringComparison.Ordinal);
        Assert.NotNull(limited.Res.Headers.RetryAfter);
        Assert.Equal("1", limited.Res.Headers.GetValues("x-ratelimit-limit-requests").Single());
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{id}/limits", UriKind.Relative), new { requestsPerMinute = (int?)null, tokensPerMinute = (int?)null }));

        // The refusals are in the ledger as refused, not as requests.
        await gateway.FlushAsync(default);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var reasons = await scope.ServiceProvider.GetRequiredService<AppDbContext>().GatewayRequests.AsNoTracking()
                .Where(r => r.UserId == id && r.Status == Llm.Core.Gateway.GatewayRequest.Refused).Select(r => r.Refusal).ToListAsync();
            Assert.Contains("unknown_model", reasons);
            Assert.Contains("rate_limit", reasons);
        }

        // An own key with a budget spent: refused with LiteLLM's sentence, which the chat and Code Arena read.
        var person = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        var own = await person.JsonAsync(await person.PostAsync("/api/account/keys/own", new { name = "CI", monthlyBudget = 0.01m }));
        var ownKey = own.GetProperty("apiKey").GetString()!;
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.GatewayRequests.Add(new Llm.Core.Gateway.GatewayRequest
            {
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5), EndedAt = DateTimeOffset.UtcNow.AddMinutes(-5), Surface = "api", Call = "chat", CreditKind = "api", UserId = id,
                Person = $"{name}@example.test", KeyHash = Llm.Api.Gateway.AnswerCache.HashOf(ownKey), Model = "Qwen3.8-Flash-Next", Cost = 1m, Status = "done", HttpStatus = 200,
            });
            await db.SaveChangesAsync();
        }
        var spent = await AskAsync(f, ownKey, "over budget");
        Assert.Equal(HttpStatusCode.BadRequest, spent.Status);
        Assert.Contains("Budget has been exceeded", spent.Body, StringComparison.Ordinal);
        // The person's main key is not held to it.
        Assert.Equal(HttpStatusCode.OK, (await AskAsync(f, key, "still fine")).Status);
    }
}
