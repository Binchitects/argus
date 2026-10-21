using System.Text.Json;
using Llm.Api.Chat.Tools;
using Llm.Core.Access;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Safeguards;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Plugins;

/// <summary>One thing a plugin asks for, as the admin grants it at install: its key, and a sentence that says it.</summary>
public sealed record PluginGrant(string Key, string Text);

/// <summary>
/// What a plugin brings besides its tools, kept in step with its version: its assistants (for everyone), its safeguard rules
/// (added in monitor mode, for an admin to enforce) and its toolsets; all of it goes when the plugin does. And the
/// permissions it asks for, from what it brings.
/// </summary>
public static class PluginContent
{
    /// <summary>What installing the package grants it, each said in a sentence.</summary>
    public static List<PluginGrant> Permissions(PluginPackage package, string? url = null)
    {
        var m = package.Manifest;
        var list = new List<PluginGrant>();
        var host = Uri.TryCreate(url ?? m.Url, UriKind.Absolute, out var u) ? u.Host : null;
        list.Add(new("tools", m.Mcp is not null ? $"Adds an MCP server's tools to the chat{(host is null || host.Contains('{') ? "" : $", at {host}")}." : "Adds tools to the chat, from its API document."));
        if (host is not null)
        {
            list.Add(new($"network:{host}", $"Sends what the chat asks of it to {host}."));
        }
        if (m.PersonAuth is not null)
        {
            list.Add(new("accounts", m.PersonAuth == "oauth2" ? "Each person signs in to its service, and it acts as them." : "Each person gives it their own API key, and it acts as them."));
        }
        if (m.Writes.Length > 0)
        {
            list.Add(new("writes", $"Can change things: {string.Join(", ", m.Writes)} (each call asks the person first)."));
        }
        if (package.Prompts is { Count: > 0 } prompts)
        {
            list.Add(new("prompts", $"Adds {prompts.Count} prompt{(prompts.Count > 1 ? "s" : "")} to the library: {string.Join(", ", prompts.Select(p => "/" + p.Name))}."));
        }
        if (package.Assistants.Count > 0)
        {
            list.Add(new("assistants", $"Adds {package.Assistants.Count} assistant{(package.Assistants.Count > 1 ? "s" : "")} for everyone: {string.Join(", ", package.Assistants.Select(a => a.Name))}."));
        }
        if (package.Rules.Count > 0)
        {
            list.Add(new("safeguards", $"Adds {package.Rules.Count} safeguard rule{(package.Rules.Count > 1 ? "s" : "")}, in monitor mode until an admin enforces them: {string.Join(", ", package.Rules.Select(r => r.Name))}."));
        }
        if (m.Toolsets.Length > 0)
        {
            list.Add(new("toolsets", $"Adds toolsets: {string.Join(", ", m.Toolsets.Select(t => t.Name))}."));
        }
        return list;
    }

    /// <summary>Permissions that need the admin's yes beyond adding tools: everything it brings to people or to the rules.</summary>
    public static IEnumerable<string> NeedingYes(IEnumerable<PluginGrant> permissions) =>
        permissions.Select(p => p.Key).Where(k => k is "assistants" or "safeguards" or "toolsets" or "accounts" or "writes" || k.StartsWith("network:", StringComparison.Ordinal));

    /// <summary>The plugin's assistants, rules and toolsets as the package has them: added, changed, or removed when it no longer does.</summary>
    public static async Task SyncAsync(AppDbContext db, McpServer server, PluginPackage package, Guid? adminId, CancellationToken ct)
    {
        var plugin = server.Plugin!;
        var self = McpServerTool.Prefix + server.Id;
        string[] Tools(IEnumerable<string> names) => [.. names.Select(n => n == "self" ? self : n).Distinct()];

        var assistants = await db.Assistants.Where(a => a.Plugin == plugin).ToListAsync(ct);
        foreach (var a in package.Assistants)
        {
            var row = assistants.FirstOrDefault(x => x.Name == a.Name);
            if (row is null)
            {
                row = new Assistant { Name = a.Name, UserId = adminId ?? Guid.Empty, Plugin = plugin, Reach = Reach.Company };
                if (adminId is null)
                {
                    continue;
                }
                db.Assistants.Add(row);
            }
            row.Description = a.Description;
            row.Instructions = a.Instructions;
            row.Starters = [.. a.Starters];
            row.Tools = a.Tools.Length > 0 ? [.. Tools(a.Tools)] : [self];
            row.Model = a.Model;
            row.Icon = a.Icon ?? row.Icon;
            row.Color = a.Color ?? row.Color;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        db.Assistants.RemoveRange(assistants.Where(x => package.Assistants.All(a => a.Name != x.Name)));

        var rules = await db.SafeguardRules.Where(r => r.Plugin == plugin).ToListAsync(ct);
        var order = (await db.SafeguardRules.MaxAsync(r => (int?)r.Order, ct) ?? 0) + 10;
        foreach (var r in package.Rules)
        {
            var name = $"{server.Name}: {r.Name}";
            var row = rules.FirstOrDefault(x => x.Name == name);
            var adding = row is null;
            row ??= new SafeguardRule { Name = name, Detector = r.Detector, Action = r.Action, Plugin = plugin, Order = order += 10, CreatedById = adminId };
            var monitor = adding || row.Monitor;
            var enabled = adding ? r.Enabled : row.Enabled;
            Safeguards.SafeguardEndpoints.Apply(row, r with { Name = name });
            // A plugin's rule watches first: an admin makes it bite (Admin → Safeguards).
            (row.Monitor, row.Enabled) = (monitor, enabled);
            if (adding)
            {
                db.SafeguardRules.Add(row);
            }
        }
        db.SafeguardRules.RemoveRange(rules.Where(x => package.Rules.All(r => $"{server.Name}: {r.Name}" != x.Name)));

        var sets = await db.Toolsets.Where(t => t.Plugin == plugin).ToListAsync(ct);
        foreach (var t in package.Manifest.Toolsets)
        {
            var row = sets.FirstOrDefault(x => x.Name == t.Name);
            if (row is null)
            {
                if (await db.Toolsets.AnyAsync(x => x.Name == t.Name, ct))
                {
                    // An admin's own toolset has the name: theirs stays.
                    continue;
                }
                row = new Toolset { Name = t.Name, Plugin = plugin };
                db.Toolsets.Add(row);
            }
            row.Description = t.Description;
            row.Tools = [.. Tools(t.Tools.Length > 0 ? t.Tools : ["self"])];
        }
        db.Toolsets.RemoveRange(sets.Where(x => package.Manifest.Toolsets.All(t => t.Name != x.Name)));

        // The scheduled tasks it offers: people add them to their own (Tasks → From plugins).
        var templates = await db.TaskTemplates.Where(t => t.Plugin == plugin).ToListAsync(ct);
        foreach (var t in package.Manifest.Tasks)
        {
            var row = templates.FirstOrDefault(x => x.Name == t.Name);
            if (row is null)
            {
                row = new TaskTemplate { Plugin = plugin, ServerId = server.Id, Name = t.Name, Prompt = t.Prompt, Cron = t.Cron };
                db.TaskTemplates.Add(row);
            }
            row.ServerId = server.Id;
            row.Description = t.Description;
            row.Prompt = t.Prompt;
            row.Cron = t.Cron;
            row.Tools = [.. Tools(t.Tools.Length > 0 ? t.Tools : ["self"])];
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        db.TaskTemplates.RemoveRange(templates.Where(x => package.Manifest.Tasks.All(t => t.Name != x.Name)));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The Code Arena skills of the plugins this person may use (their tool allowed to them), from each one's version
    /// installed: a name two plugins use is the first's by plugin name.
    /// </summary>
    public static async Task<List<(PluginSkill Skill, string Plugin, string Version)>> SkillsAsync(AppDbContext db, IReadOnlyCollection<string> usable, CancellationToken ct)
    {
        var servers = await db.McpServers.AsNoTracking().Where(s => s.Plugin != null).OrderBy(s => s.Plugin).ToListAsync(ct);
        var found = new List<(PluginSkill, string, string)>();
        foreach (var server in servers.Where(s => usable.Contains(McpServerTool.Prefix + s.Id)))
        {
            if (server.Manifest is null || PluginManifest.Parse(server.Manifest).Skills.Length == 0)
            {
                continue;
            }
            var release = await db.PluginReleases.AsNoTracking().Where(r => r.ServerId == server.Id).OrderByDescending(r => r.InstalledAt).FirstOrDefaultAsync(ct);
            if (release is null || JsonSerializer.Deserialize<Dictionary<string, string>>(release.FilesJson) is not { } files)
            {
                continue;
            }
            try
            {
                foreach (var skill in PluginCatalog.FromFiles(files).Skills.Where(k => found.All(f => f.Item1.Name != k.Name)))
                {
                    found.Add((skill, server.Plugin!, release.Version));
                }
            }
            catch (Exception ex) when (ex is PluginCatalog.PluginException or PluginManifest.ManifestException)
            {
                continue;
            }
        }
        return found;
    }

    /// <summary>Everything the plugin brought, gone with it.</summary>
    public static async Task RemoveAsync(AppDbContext db, string plugin, CancellationToken ct)
    {
        await db.Assistants.Where(a => a.Plugin == plugin).ExecuteDeleteAsync(ct);
        await db.SafeguardRules.Where(r => r.Plugin == plugin).ExecuteDeleteAsync(ct);
        await db.Toolsets.Where(t => t.Plugin == plugin).ExecuteDeleteAsync(ct);
        await db.TaskTemplates.Where(t => t.Plugin == plugin).ExecuteDeleteAsync(ct);
    }

    /// <summary>The version installed, kept so it can be put back.</summary>
    public static PluginRelease Release(McpServer server, PluginPackage package, Guid? adminId) => new()
    {
        ServerId = server.Id, Version = package.Manifest.Version, FilesJson = JsonSerializer.Serialize(package.Files),
        Permissions = [.. Permissions(package).Select(p => p.Key)], InstalledById = adminId,
    };
}
