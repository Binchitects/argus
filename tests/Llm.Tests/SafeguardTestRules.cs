using Llm.Core.Data;
using Llm.Core.Safeguards;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>A test's safeguard rules, changed in its own app's database: the rules the app starts with, by name.</summary>
internal static class SafeguardTestRules
{
    public const string Secrets = "Secrets in what people send";
    public const string PersonalData = "Personal data to the model";
    public const string BlockedWords = "Blocked words";
    public const string Harmful = "Harmful requests";

    public static async Task ChangeAsync(WebApplicationFactory<Program> f, string name, Action<SafeguardRule> change)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rule = await db.SafeguardRules.SingleAsync(r => r.Name == name);
        change(rule);
        await db.SaveChangesAsync();
    }

    /// <summary>The model reads every message for harm (the rule the app starts with, turned on).</summary>
    public static Task CheckHarmAsync(WebApplicationFactory<Program> f) => ChangeAsync(f, Harmful, r => r.Enabled = true);
}
