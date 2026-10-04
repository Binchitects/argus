using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Plugins;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>A prompt a plugin brings: one of its Markdown files, with a front matter that names it.</summary>
public sealed record PluginPrompt(string Name, string Title, string Text);

/// <summary>
/// The prompt library's rules: a slash name, a title, a text whose {{variables}} the composer asks
/// for; and the prompts plugins bring, read from their files and kept with their tool row.
/// </summary>
public static partial class PromptLibrary
{
    public const int MaxName = 40;
    public const int MaxTitle = 100;
    public const int MaxText = 20_000;

    /// <summary>The variables of a prompt's text, in the order they first appear: "{{file}}" is "file".</summary>
    public static List<string> Variables(string text) =>
        [.. Variable().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    /// <summary>The text with its variables filled in from <paramref name="values"/>; a variable not given is left empty.</summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string> values) =>
        Variable().Replace(text, m => values.GetValueOrDefault(m.Groups[1].Value, ""));

    /// <summary>Why a slash name will not do, or null.</summary>
    public static string? NameProblem(string name) => SlashName().IsMatch(name)
        ? null
        : $"\"/{name}\" is not a slash name: lowercase letters, digits, - and _, up to {MaxName} (like /review).";

    /// <summary>
    /// A plugin's prompt file: a front matter with its name (and a title), then its text.
    /// <code>
    /// ---
    /// name: triage
    /// title: Triage an issue
    /// ---
    /// Triage issue #{{issue}} in {{project}}…
    /// </code>
    /// </summary>
    public static PluginPrompt FromFile(string file, string content)
    {
        var text = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new PluginCatalog.PluginException($"The prompt {file} has no front matter (a --- line, its name and title, and --- again).");
        }
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new PluginCatalog.PluginException($"The front matter of {file} has no end (a --- line).");
        }
        JsonObject front;
        try
        {
            front = Tools.OpenApi.Parse(text[4..end]);
        }
        catch (Tools.OpenApi.SpecException)
        {
            throw new PluginCatalog.PluginException($"The front matter of {file} is not YAML.");
        }
        var body = text[(end + 4)..].TrimStart('-').Trim();
        var name = (front["name"] as JsonValue)?.ToString().Trim() ?? throw new PluginCatalog.PluginException($"The prompt {file} has no name.");
        if (NameProblem(name) is { } problem)
        {
            throw new PluginCatalog.PluginException($"{file}: {problem}");
        }
        var title = (front["title"] as JsonValue)?.ToString().Trim() is { Length: > 0 } t ? t : name;
        if (body.Length is 0 or > MaxText || title.Length > MaxTitle)
        {
            throw new PluginCatalog.PluginException($"The prompt {file} needs a text (up to {MaxText:N0} characters) and a title of up to {MaxTitle}.");
        }
        return new PluginPrompt(name, title, body);
    }

    /// <summary>A plugin installed or updated: its prompts are the package's now, for everyone who may use its tool.</summary>
    public static async Task SyncAsync(AppDbContext db, Core.Chat.McpServer server, PluginPackage package, CancellationToken ct)
    {
        await db.Prompts.Where(p => p.ServerId == server.Id).ExecuteDeleteAsync(ct);
        db.Prompts.AddRange((package.Prompts ?? []).Select(p => new SavedPrompt
        {
            Name = p.Name, Title = p.Title, Text = p.Text, Sharing = PromptSharing.Company, ServerId = server.Id,
        }));
        await db.SaveChangesAsync(ct);
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9_-]{0,39})\s*\}\}")]
    private static partial Regex Variable();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$")]
    private static partial Regex SlashName();
}
