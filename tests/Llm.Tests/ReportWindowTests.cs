using System.Net;
using System.Text.Json;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>The reports that take a time window (from and to) read only what happened in it: the audit log, feedback, the leaderboard.</summary>
[Collection(nameof(AppCollection))]
public sealed class ReportWindowTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() => app.Create(app.ConnectionStringFor("win_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());

    private static readonly DateTimeOffset October = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static string Window(DateTimeOffset from, DateTimeOffset to) => $"from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

    [Fact]
    public async Task The_audit_log_feedback_and_the_leaderboard_read_only_their_window()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var person = db.Users.First().Id;
            foreach (var (day, what) in new[] { (2, "window.before"), (10, "window.in"), (20, "window.after") })
            {
                db.AuditEvents.Add(new AuditEvent { Action = what, At = October.AddDays(day) });
                db.ProductFeedback.Add(new ProductFeedback { UserId = person, Kind = "idea", Text = what, CreatedAt = October.AddDays(day) });
            }
            db.ArenaMatches.Add(new ArenaMatch { UserId = person, QuestionId = Guid.NewGuid(), ModelA = "old-a", ModelB = "old-b", Vote = "a", VotedAt = October.AddDays(2) });
            db.ArenaMatches.Add(new ArenaMatch { UserId = person, QuestionId = Guid.NewGuid(), ModelA = "new-a", ModelB = "new-b", Vote = "b", VotedAt = October.AddDays(10) });
            await db.SaveChangesAsync();
        }
        var window = Window(October.AddDays(5), October.AddDays(15));

        var audit = await admin.JsonAsync(await admin.GetAsync($"/api/admin/audit?{window}"));
        Assert.Equal(["window.in"], audit.EnumerateArray().Select(e => e.GetProperty("action").GetString()).Where(a => a!.StartsWith("window.", StringComparison.Ordinal)));
        Assert.Equal(3, (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray().Count(e => e.GetProperty("action").GetString()!.StartsWith("window.", StringComparison.Ordinal)));

        var feedback = await admin.JsonAsync(await admin.GetAsync($"/api/admin/feedback?status=all&{window}"));
        Assert.Equal(["window.in"], feedback.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()));
        // The counts are the window's too.
        Assert.Equal(1, feedback.GetProperty("counts").GetProperty("new").GetInt32());

        var board = (await admin.JsonAsync(await admin.GetAsync($"/api/arena/leaderboard?{window}"))).GetProperty("board");
        Assert.Equal(1, board.GetProperty("votes").GetInt32());
        Assert.Equal(2, (await admin.JsonAsync(await admin.GetAsync("/api/arena/leaderboard"))).GetProperty("board").GetProperty("votes").GetInt32());

        // A window that ends before it starts is refused, not read as none.
        var backwards = Window(October.AddDays(15), October.AddDays(5));
        foreach (var path in new[] { "/api/admin/audit", "/api/admin/feedback", "/api/arena/leaderboard" })
        {
            await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.GetAsync($"{path}?{backwards}"));
        }
    }
}
