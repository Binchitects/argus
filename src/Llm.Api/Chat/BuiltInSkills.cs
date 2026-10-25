using System.Reflection;
using System.Text.Json.Nodes;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>
/// The skills that come with Arena (the repository's skills/ folder, built into the app): the company's skills, written
/// as Arena has them at each start — review code, write tests, debug, plan a change, summarize, meeting notes, and the
/// rest. A person's, a group's or the company's own skill of the same name wins over one; an admin turns one off (it
/// stays off), never edits it: one of the company's of the same name is theirs to write.
/// </summary>
public sealed partial class BuiltInSkills(IServiceScopeFactory scopes, IConfiguration config, ILogger<BuiltInSkills> logger) : IHostedService
{
    /// <summary>A skill as it is built in: its slash name, title, when it is for, instructions and parameters (JSON).</summary>
    public sealed record Built(string Name, string Title, string Description, string Instructions, string Parameters);

    /// <summary>The skills built into the app, by name.</summary>
    public static IReadOnlyList<Built> All => _all ??= Read();
    private static List<Built>? _all;

    private static List<Built> Read()
    {
        var assembly = typeof(BuiltInSkills).Assembly;
        var found = new List<Built>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(r => r.StartsWith("skills/", StringComparison.Ordinal) && r.EndsWith("/SKILL.md", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var folder = resource["skills/".Length..^"/SKILL.md".Length];
            var front = Front(text);
            var name = (front["name"] as JsonValue)?.ToString() ?? folder;
            var description = (front["description"] as JsonValue)?.ToString() ?? "";
            if (SkillLibrary.FromSkill(folder, text, name, description) is { } skill)
            {
                found.Add(new Built(skill.Name, skill.Title, skill.Description, skill.Instructions, SkillLibrary.Write(skill.Parameters)));
            }
        }
        return found;
    }

    /// <summary>A SKILL.md's front matter, as JSON (empty when it has none, or one that does not read).</summary>
    private static JsonObject Front(string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal) || text.IndexOf("\n---", 3, StringComparison.Ordinal) is not (var end and > 0))
        {
            return [];
        }
        try
        {
            return Tools.OpenApi.Parse(text[4..end]);
        }
        catch (Tools.OpenApi.SpecException)
        {
            return [];
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SyncAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            // Another replica wrote them at the same moment, or the database is not there yet: the next start writes them.
            LogFailed(logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Skills:BuiltIn (on unless set false): Arena's own skills are there.</summary>
    public bool On => !bool.TryParse(config["Skills:BuiltIn"], out var on) || on;

    /// <summary>
    /// The built-in skills as Arena has them now: added, written anew, and those no longer built in removed; an admin's off
    /// and auto-use kept. With Skills:BuiltIn false, there are none.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var kept = await db.Skills.Where(s => s.BuiltIn).ToDictionaryAsync(s => s.Name, StringComparer.Ordinal, ct);
        foreach (var b in On ? All : [])
        {
            if (kept.Remove(b.Name, out var skill))
            {
                if ((skill.Title, skill.Description, skill.Instructions, skill.Parameters) == (b.Title, b.Description, b.Instructions, b.Parameters))
                {
                    continue;
                }
                (skill.Title, skill.Description, skill.Instructions, skill.Parameters, skill.UpdatedAt) = (b.Title, b.Description, b.Instructions, b.Parameters, DateTimeOffset.UtcNow);
                continue;
            }
            db.Skills.Add(new Skill
            {
                Name = b.Name, Title = b.Title, Description = b.Description, Instructions = b.Instructions, Parameters = b.Parameters, Sharing = SkillSharing.Company, BuiltIn = true,
            });
        }
        db.Skills.RemoveRange(kept.Values);
        await db.SaveChangesAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The built-in skills were not written now")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
