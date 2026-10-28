using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Llm.Api.Chat;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>API requests wait in the answers' line with the chat: a person's own requests at once hold their keys too, and a line as long as it may be refuses the next at once.</summary>
[Collection(nameof(AppCollection))]
public sealed class ApiLineTests(AppFixture app)
{
    [Fact]
    public async Task A_persons_requests_at_once_hold_their_API_requests_and_past_the_longest_line_one_is_refused_as_busy()
    {
        await using var f = app.Create(app.ConnectionStringFor("apiline_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?> { ["Chat:QueueLength"] = "1" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "al" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var (id, key) = (made.GetProperty("id").GetGuid(), made.GetProperty("apiKey").GetString()!);
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{id}/limits", UriKind.Relative), new { answersAtOnce = 1 }));
        var person = await admin.JsonAsync(await admin.GetAsync($"/api/admin/people/{id}"));
        Assert.Equal((1, "person"), (person.GetProperty("atOnce").GetProperty("value").GetInt32(), person.GetProperty("atOnce").GetProperty("from").GetString()));

        Task<HttpResponseMessage> Ask(string text)
        {
            var client = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
            var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/chat/completions", UriKind.Relative))
            {
                Content = new StringContent(JsonSerializer.Serialize(new { model = "Qwen3.8-Flash-Next", messages = new[] { new { role = "user", content = text } }, stream = true }), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new("Bearer", key);
            return client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        }
        var gate = f.Services.GetRequiredService<AnswerGate>();
        // The first is answered slowly: the second waits for it, as the chat's answers would.
        using var first = await Ask("one [slow]");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = Ask("two");
        for (var waited = 0; gate.Now().Waiting == 0 && waited < 10_000; waited += 50)
        {
            await Task.Delay(50);
        }
        Assert.Equal((1, 1), gate.Now());
        // Admin → Line: whose request runs and whose waits, in order, chat or API.
        var line = await admin.JsonAsync(await admin.GetAsync("/api/admin/line"));
        var running = Assert.Single(line.GetProperty("running").EnumerateArray());
        var waiting = Assert.Single(line.GetProperty("waiting").EnumerateArray());
        Assert.Equal((name, "api"), (running.GetProperty("person").GetProperty("userName").GetString(), running.GetProperty("kind").GetString()));
        Assert.Equal((name, 1), (waiting.GetProperty("person").GetProperty("userName").GetString(), waiting.GetProperty("position").GetInt32()));
        Assert.Equal(1, line.GetProperty("queueLength").GetInt32());
        // Someone without usage.read does not see it.
        var nosy = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        Assert.Equal(HttpStatusCode.Forbidden, (await nosy.GetAsync("/api/admin/line")).StatusCode);
        // The line holds one: a third is refused at once, as busy, with Retry-After.
        using var third = await Ask("three");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter);
        Assert.Contains("busy", await third.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // Prometheus reads the line on the stack's network; what came through Traefik does not.
        var plain = f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        Assert.Contains("arena_line_waiting", await plain.GetStringAsync(new Uri("/metrics", UriKind.Relative)), StringComparison.Ordinal);
        using var forwarded = new HttpRequestMessage(HttpMethod.Get, new Uri("/metrics", UriKind.Relative));
        forwarded.Headers.Add("X-Forwarded-For", "203.0.113.9");
        Assert.Equal(HttpStatusCode.NotFound, (await plain.SendAsync(forwarded)).StatusCode);
        await first.Content.ReadAsStringAsync();
        first.Dispose();
        using var answered = await second.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
    }
}
