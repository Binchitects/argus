using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Knowledge;

/// <summary>
/// A GitLab project, or every project of a group, read with the GitLab bot's token (Settings → Scheduled tasks):
/// its wiki pages and its issues (with their comments; never confidential ones). Who may read them is the
/// project's members (Guest and up, inherited too, active), matched to people by username as Argus does:
/// a person's user name here is their GitLab username. A project whose last activity has not moved since
/// it was read is not read again, but once a day it is (GitLab notes activity at most hourly).
/// </summary>
public sealed class GitLabConnector(Schedules.GitLabBot bot, AppDbContext db, TimeProvider clock) : IKnowledgeConnector
{
    private const int PerPage = 100;
    /// <summary>Pages of one listing at most: 10,000 issues or members.</summary>
    private const int MaxPages = 100;
    private const int Guest = 10;
    private static readonly TimeSpan ReadAgain = TimeSpan.FromDays(1);

    public string Kind => "gitlab";

    public string? Check(KnowledgeSource source) =>
        !bot.Ready ? "Set the GitLab bot first: Settings → Scheduled tasks, its address and token (Reporter in the projects)."
        : string.IsNullOrWhiteSpace(source.Location) ? "Say which GitLab project or group, by its path (group/project)."
        : !source.Wiki && !source.Issues ? "Choose the wiki, the issues, or both."
        : null;

    private sealed record Project(long Id, string Path, string WebUrl, string? Activity, bool Wiki, bool Issues);

    public async IAsyncEnumerable<FoundDocument> ReadAsync(KnowledgeSource source, SyncPass pass, [EnumeratorCancellation] CancellationToken ct)
    {
        var projects = await ProjectsAsync(source.Location.Trim().Trim('/'), ct);
        var rows = await db.KnowledgeReaders.Where(r => r.SourceId == source.Id).ToDictionaryAsync(r => r.Key, ct);
        foreach (var p in projects)
        {
            var key = Readers.GitLab(p.Id);
            if (!rows.TryGetValue(key, out var row))
            {
                row = new KnowledgeReaders { SourceId = source.Id, Key = key };
                db.KnowledgeReaders.Add(row);
                rows[key] = row;
            }
            row.Name = p.Path;
            if (!await RefreshAsync(row, ct))
            {
                pass.Problems.Add($"{p.Path}: its members could not be read, so who may read it is as it was");
            }
            await db.SaveChangesAsync(ct);
            if (row.Activity == p.Activity && row.ReadAt > clock.GetUtcNow() - ReadAgain)
            {
                pass.Unchanged.Add($"{p.Id}/");
                continue;
            }
            var whole = true;
            string[] readers = [key];
            if (source.Wiki && p.Wiki)
            {
                var (pages, status) = await ListAsync($"/projects/{p.Id}/wikis?with_content=1", ct);
                if (pages is null)
                {
                    whole = false;
                    pass.Unchanged.Add($"{p.Id}/wiki/");
                    pass.Problems.Add($"{p.Path}: its wiki could not be read (GitLab answered {Said(status)})");
                }
                foreach (var page in pages ?? [])
                {
                    var slug = Str(page, "slug");
                    var content = Str(page, "content");
                    if (slug.Length == 0)
                    {
                        continue;
                    }
                    yield return new FoundDocument($"{p.Id}/wiki/{slug}", $"{Str(page, "title")} ({p.Path} wiki)", $"{p.WebUrl}/-/wikis/{slug}", Readers.Hash(content), readers,
                        _ => Task.FromResult<string?>($"# {Str(page, "title")}\n\n{content}"));
                }
            }
            if (source.Issues && p.Issues)
            {
                for (var n = 1; n <= MaxPages; n++)
                {
                    var (status, body) = await bot.ReadAsync($"/projects/{p.Id}/issues?state=all&order_by=updated_at&sort=desc&per_page={PerPage}&page={n}", ct);
                    if (status != 200 || Array(body) is not { } issues)
                    {
                        whole = false;
                        pass.Unchanged.Add($"{p.Id}/issues/");
                        pass.Problems.Add($"{p.Path}: its issues could not be read (GitLab answered {Said(status)})");
                        break;
                    }
                    foreach (var issue in issues.Where(i => i["confidential"]?.GetValue<bool>() != true))
                    {
                        var iid = issue["iid"]?.GetValue<long>() ?? 0;
                        yield return new FoundDocument($"{p.Id}/issues/{iid}", $"#{iid} {Str(issue, "title")} ({p.Path})", Str(issue, "web_url"), Str(issue, "updated_at"), readers,
                            token => IssueAsync(p.Id, issue, token));
                    }
                    if (issues.Count < PerPage)
                    {
                        break;
                    }
                }
            }
            if (whole)
            {
                row.Activity = p.Activity;
                row.ReadAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
            }
        }
        // A project that left the group: who may read it goes (its documents go, as they were not seen).
        var ids = projects.Select(p => Readers.GitLab(p.Id)).ToHashSet();
        db.KnowledgeReaders.RemoveRange(rows.Values.Where(r => !ids.Contains(r.Key)));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Reads a project's members again; false when GitLab did not answer (the list stays as it was).</summary>
    public async Task<bool> RefreshAsync(KnowledgeReaders row, CancellationToken ct)
    {
        var project = row.Key["gitlab:".Length..];
        var (members, _) = await ListAsync($"/projects/{project}/members/all", ct);
        if (members is null)
        {
            return false;
        }
        row.People = [.. members
            .Where(m => (m["state"]?.GetValue<string>() ?? "active") == "active" && (m["access_level"]?.GetValue<int>() ?? 0) >= Guest)
            .Select(m => Str(m, "username").ToLowerInvariant()).Where(u => u.Length > 0).Distinct().Order(StringComparer.Ordinal)];
        row.FetchedAt = clock.GetUtcNow();
        return true;
    }

    /// <summary>The project at that path, or every project of the group at that path (subgroups too, not archived ones).</summary>
    private async Task<List<Project>> ProjectsAsync(string location, CancellationToken ct)
    {
        var path = Uri.EscapeDataString(location);
        var (status, body) = await bot.ReadAsync($"/projects/{path}", ct);
        if (status == 200 && Parse(body) is JsonObject one)
        {
            return [ProjectOf(one)];
        }
        if (status is not (403 or 404))
        {
            throw new KnowledgeException(status == 0 ? "GitLab could not be reached." : $"GitLab answered {status} for {location}.");
        }
        var (projects, groupStatus) = await ListAsync($"/groups/{path}/projects?include_subgroups=true&archived=false", ct);
        if (projects is null)
        {
            throw new KnowledgeException(groupStatus is 403 or 404
                ? $"GitLab has no project or group {location} that the bot can read: add the bot to it as a Reporter."
                : $"GitLab could not list the projects of {location} ({Said(groupStatus)}).");
        }
        return [.. projects.Select(ProjectOf)];
    }

    private static Project ProjectOf(JsonObject p) => new(
        p["id"]?.GetValue<long>() ?? 0, Str(p, "path_with_namespace"), Str(p, "web_url").TrimEnd('/'), p["last_activity_at"]?.GetValue<string>(),
        Enabled(p, "wiki"), Enabled(p, "issues"));

    /// <summary>A feature that is on: by its access level (GitLab 13+), or the older flag.</summary>
    private static bool Enabled(JsonObject p, string feature) =>
        p[$"{feature}_access_level"]?.GetValue<string>() is { } level ? level != "disabled" : p[$"{feature}_enabled"]?.GetValue<bool>() ?? true;

    /// <summary>An issue as a document: its title, state and labels, its description, then its comments (not GitLab's own notes).</summary>
    private async Task<string?> IssueAsync(long project, JsonObject issue, CancellationToken ct)
    {
        var (notes, _) = await ListAsync($"/projects/{project}/issues/{issue["iid"]}/notes?sort=asc&order_by=created_at", ct);
        if (notes is null)
        {
            return null;
        }
        var labels = (issue["labels"] as JsonArray ?? []).Select(l => l?.ToString()).Where(l => l is { Length: > 0 }).ToList();
        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"# #{issue["iid"]} {Str(issue, "title")}\n\n")
            .Append(CultureInfo.InvariantCulture, $"State: {Str(issue, "state")}. Opened by {issue["author"]?["username"]} on {Day(Str(issue, "created_at"))}.")
            .Append(labels.Count > 0 ? $" Labels: {string.Join(", ", labels)}." : "")
            .Append("\n\n").Append(Str(issue, "description"));
        foreach (var note in notes.Where(x => x["system"]?.GetValue<bool>() != true && x["confidential"]?.GetValue<bool>() != true && x["internal"]?.GetValue<bool>() != true))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n\n## Comment by {note["author"]?["username"]} on {Day(Str(note, "created_at"))}\n\n").Append(Str(note, "body"));
        }
        return text.ToString();
    }

    /// <summary>Every item of a listing, page by page; null (with GitLab's status) when a page could not be read.</summary>
    private async Task<(List<JsonObject>? Items, int Status)> ListAsync(string path, CancellationToken ct)
    {
        var items = new List<JsonObject>();
        for (var n = 1; n <= MaxPages; n++)
        {
            var (status, body) = await bot.ReadAsync($"{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}per_page={PerPage}&page={n}", ct);
            if (status != 200 || Array(body) is not { } page)
            {
                return (null, status);
            }
            items.AddRange(page);
            if (page.Count < PerPage)
            {
                break;
            }
        }
        return (items, 200);
    }

    private static JsonNode? Parse(string? body)
    {
        try
        {
            return body is null ? null : JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<JsonObject>? Array(string? body) => Parse(body) is JsonArray a ? [.. a.OfType<JsonObject>()] : null;

    private static string Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static string Day(string iso) => iso.Length >= 10 ? iso[..10] : iso;

    private static string Said(int status) => status == 0 ? "nothing: it could not be reached" : status.ToString(CultureInfo.InvariantCulture);
}
