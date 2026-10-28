using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Chat;
using Llm.Api.Operations;
using Llm.Core.Code;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Code.Fixes;

/// <summary>
/// A security fix's change checked before its merge request is opened. The scanners are run on the files it changed and
/// the finding's, before and after: is the finding's hit gone, and did the change bring new ones? The security review's
/// tracing question is asked again where the weakness was (the sink's line, or the call that took its place): does it
/// still read as vulnerable? A package's fix is checked again when its merge request is reviewed, against the advisories.
/// </summary>
public static partial class FixChecks
{
    /// <summary>How far from where it was the finding's hit, or its sink, is looked for in the changed file.</summary>
    public const int Near = 10;
    public const int SinkNear = 40;
    /// <summary>The most files a check sends to the scanners (as Argus takes them).</summary>
    public const int MostFiles = 200;

    public static async Task<JsonObject?> SecurityAsync(IServiceProvider services, CodeFix fix, Workspace w, AppUser me, CancellationToken ct)
    {
        if (fix.Source != "security" || !Guid.TryParse(fix.SourceId, out var id))
        {
            return null;
        }
        var db = services.GetRequiredService<AppDbContext>();
        if (await db.SecurityFindings.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct) is not { } finding)
        {
            return null;
        }
        if (finding.Kind == SecurityFinding.Dependency)
        {
            return new JsonObject { ["note"] = "A package's fix is checked against the advisories when its merge request is reviewed." };
        }
        var checks = new JsonObject { ["scanners"] = await ScannersAsync(services, finding, w, ct) };
        if (finding.Kind == SecurityFinding.Code && fix.Model is { } model)
        {
            checks["review"] = await ReviewAsync(services, finding, w, model, me, ct);
        }
        return checks;
    }

    private sealed record Hit(string Path, int Line, string Rule, string Cwe, string Title, string Code);

    private static List<Hit> Hits(JsonNode? answer) =>
        [.. (answer?["findings"] as JsonArray ?? []).OfType<JsonObject>().Select(f => new Hit(f["path"]?.ToString() ?? "",
            f["line"] is JsonValue l && l.TryGetValue<int>(out var line) ? line : 0,
            string.Join(", ", (f["rules"] as JsonArray ?? []).Select(r => r?.ToString())), SecurityReview.Cwe(f["cwe"]?.ToString()) ?? "",
            f["title"]?.ToString() ?? "", Squash(f["code"]?.ToString() ?? "")))];

    /// <summary>The scanners on the change's files: the finding's hit still there or not, and the hits the change brought.</summary>
    private static async Task<JsonObject> ScannersAsync(IServiceProvider services, SecurityFinding finding, Workspace w, CancellationToken ct)
    {
        var argus = services.GetRequiredService<ArgusAdmin>();
        var tools = finding.Kind switch { SecurityFinding.Secret => "gitleaks", SecurityFinding.Config => "trivy", _ => "opengrep,bandit" };
        var paths = w.Changes.Where(c => c.Value is not null).Select(c => c.Key).Prepend(finding.Path).Distinct(StringComparer.Ordinal).Where(w.Exists).Take(MostFiles).ToList();
        if (!argus.Enabled || paths.Count == 0)
        {
            return new JsonObject { ["error"] = argus.Enabled ? "The change leaves no file to scan." : "Argus is not set up here, so the scanners cannot be asked." };
        }
        List<Hit> before, after;
        try
        {
            var original = paths.Select(p => (p, w.Original(p))).Where(x => x.Item2 is not null).ToList();
            before = original.Count == 0 ? [] : Hits(await argus.PostAsync("scan-files", Files(original, tools), ct));
            after = Hits(await argus.PostAsync("scan-files", Files([.. paths.Select(p => (p, (string?)w.Read(p)))], tools), ct));
        }
        catch (ArgusException ex)
        {
            return new JsonObject { ["error"] = ex.Message };
        }
        catch (WorkspaceException ex)
        {
            return new JsonObject { ["error"] = ex.Message };
        }
        var at = w.Exists(finding.Path) ? Locate(finding, w.Read(finding.Path).Split('\n')) : 0;
        var where = at > 0 ? at : finding.Line;
        bool IsFinding(Hit h) => h.Path == finding.Path && Math.Abs(h.Line - where) <= Near &&
            (finding.Rule is { Length: > 0 } rule ? rule.Split(", ").Intersect(h.Rule.Split(", ")).Any() : h.Cwe == finding.Cwe);
        var known = before.Select(h => (h.Path, h.Rule, h.Code)).ToHashSet();
        var changed = w.Changes.Keys.ToHashSet(StringComparer.Ordinal);
        return new JsonObject
        {
            ["tools"] = tools,
            ["still"] = new JsonArray([.. after.Where(IsFinding).Select(View)]),
            ["new"] = new JsonArray([.. after.Where(h => changed.Contains(h.Path) && !IsFinding(h) && !known.Contains((h.Path, h.Rule, h.Code))).Take(20).Select(View)]),
        };
    }

    private static JsonNode View(Hit h) => new JsonObject { ["path"] = h.Path, ["line"] = h.Line, ["rule"] = h.Rule, ["cwe"] = h.Cwe, ["title"] = h.Title };

    private static JsonObject Files(IEnumerable<(string Path, string? Text)> files, string tools) =>
        new() { ["files"] = new JsonObject(files.Select(f => KeyValuePair.Create(f.Path, (JsonNode?)f.Text))), ["tools"] = tools };

    /// <summary>The finding's sink in the file as it is now: its line found by its text, nearest where it was; 0 when it is gone.</summary>
    public static int Locate(SecurityFinding finding, IReadOnlyList<string> lines)
    {
        var sink = Squash(finding.SinkCode);
        if (sink.Length < 6)
        {
            return 0;
        }
        return Enumerable.Range(1, lines.Count).Where(n => Squash(lines[n - 1]).Contains(sink, StringComparison.Ordinal))
            .OrderBy(n => Math.Abs(n - finding.Line)).FirstOrDefault();
    }

    /// <summary>
    /// The review's tracing question asked again where the weakness was: its sink's line as the change left it, or a call of
    /// the same kind near where it was (a query made another way). None there: the call it was about is gone.
    /// </summary>
    private static async Task<JsonObject> ReviewAsync(IServiceProvider services, SecurityFinding finding, Workspace w, string model, AppUser me, CancellationToken ct)
    {
        if (!w.Exists(finding.Path))
        {
            return new JsonObject { ["verdict"] = "gone", ["why"] = $"{finding.Path} is gone." };
        }
        var lines = w.Read(finding.Path).Split('\n');
        var sinks = SecurityReview.Sinks.Where(s => s.Cwe == finding.Cwe && s.Globs.Any(g => Glob(g, finding.Path))).ToList();
        var line = Locate(finding, lines);
        var sink = sinks.FirstOrDefault();
        if (line == 0)
        {
            var (from, to) = (Math.Max(1, finding.Line - SinkNear), Math.Min(lines.Length, finding.Line + SinkNear));
            (line, sink) = Enumerable.Range(from, Math.Max(0, to - from + 1))
                .SelectMany(n => sinks.Where(s => Regex.IsMatch(lines[n - 1], s.Pattern)).Select(s => (n, (Sink?)s)))
                .OrderBy(x => Math.Abs(x.n - finding.Line)).FirstOrDefault();
        }
        if (line == 0)
        {
            return new JsonObject { ["verdict"] = "gone", ["why"] = "The call the finding was about is not there any more, nor another of its kind near where it was." };
        }
        var what = sink ?? new Sink("code", finding.Cwe, finding.CweTitle, [], "");
        var start = Math.Max(1, line - 60);
        var context = new StringBuilder();
        for (var n = start; n <= Math.Min(lines.Length, line + 15); n++)
        {
            context.Append(n.ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append(n == line ? " >" : "  ").Append(lines[n - 1]).Append('\n');
        }
        var question = SecurityReview.Question(new SecurityReview.Candidate(finding.Path, line, lines[line - 1], what), context.ToString(),
            Callers(w, finding.Path, lines, line, start), []);
        var request = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = SecurityReview.AnalyzePrompt }, new JsonObject { ["role"] = "user", ["content"] = question }),
            ["stream"] = true, ["max_tokens"] = 1_500, ["temperature"] = 0.1, ["user"] = me.Email,
            ["metadata"] = new JsonObject { ["tags"] = new JsonArray(SecurityReview.Tag) },
            ["chat_template_kwargs"] = ThinkingPresets.TemplateKwargs("off"),
        };
        var text = new StringBuilder();
        try
        {
            await foreach (var e in services.GetRequiredService<GatewayChat>().StreamAsync(request, me.Email!, ct))
            {
                if (e is ContentDelta d)
                {
                    text.Append(d.Text);
                }
            }
        }
        catch (ChatGatewayException ex)
        {
            return new JsonObject { ["error"] = ex.Message };
        }
        if (ReviewEngine.Json(text.ToString()) is not { } answer || answer["vulnerable"] is not JsonValue v || !v.TryGetValue<bool>(out var vulnerable))
        {
            return new JsonObject { ["error"] = "The model's answer could not be read." };
        }
        var flow = answer["flow"] is JsonArray steps ? string.Join(" → ", steps.Select(s => s?.ToString())) : answer["flow"]?.ToString() ?? "";
        return new JsonObject
        {
            ["verdict"] = vulnerable ? "still" : "fixed", ["line"] = line,
            ["why"] = Cut(vulnerable ? $"{answer["title"]}{(flow.Length > 0 ? $": {flow}" : "")}" : answer["check"]?.ToString() ?? "", 1_000),
        };
    }

    /// <summary>Who calls the sink's function, in the change's files and the rest: up to four places, each with lines around it.</summary>
    private static string Callers(Workspace w, string path, string[] lines, int line, int shownFrom)
    {
        if (SecurityReview.EnclosingFunction([.. lines.Take(line)]) is not { } name)
        {
            return "";
        }
        var sb = new StringBuilder($"The sink is in `{name}`. Its callers:\n");
        var n = 0;
        foreach (var hit in w.Search(name + "(", null, 40))
        {
            var parts = hit.Split(':', 3);
            if (n >= 4 || parts.Length < 3 || !int.TryParse(parts[1], out var at) || (parts[0] == path && at >= shownFrom && at <= line + 15)
                || Regex.IsMatch(parts[2], $@"\b(def|func|function|void|public|private|protected|internal|static)\b[^=]*\b{Regex.Escape(name)}\s*\("))
            {
                continue;
            }
            var around = w.Read(parts[0]).Split('\n');
            sb.Append(CultureInfo.InvariantCulture, $"\n{parts[0]}:\n");
            for (var k = Math.Max(1, at - 12); k <= Math.Min(around.Length, at + 6); k++)
            {
                sb.Append(k.ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append(k == at ? " >" : "  ").Append(around[k - 1]).Append('\n');
            }
            n++;
        }
        return n == 0 ? $"The sink is in `{name}`; no caller was found outside it (it may be an entry point itself, or called by name from elsewhere)." : sb.ToString();
    }

    /// <summary>What the checks found, in words for the merge request's description and the fix's page.</summary>
    public static string Describe(JsonObject? checks)
    {
        if (checks is null)
        {
            return "";
        }
        if (checks["note"]?.ToString() is { } note)
        {
            return note;
        }
        var said = new List<string>();
        if (checks["scanners"] is JsonObject s)
        {
            said.Add(s["error"]?.ToString() is { } error ? $"The scanners could not check it: {error}"
                : (s["still"] as JsonArray)?.Count is > 0 and var still ? $"The scanners ({s["tools"]}) still find it ({still} hit(s) where it was)."
                : $"The scanners ({s["tools"]}) no longer find it.");
            if ((s["new"] as JsonArray)?.Count is > 0 and var added)
            {
                said.Add($"The change brings {added} new scanner finding(s): " +
                    string.Join("; ", (s["new"] as JsonArray)!.OfType<JsonObject>().Take(5).Select(h => $"{h["path"]}:{h["line"]} {h["title"]}")) + ".");
            }
        }
        if (checks["review"] is JsonObject r)
        {
            said.Add(r["error"]?.ToString() is { } error ? $"The review could not read it again: {error}" : r["verdict"]?.ToString() switch
            {
                "still" => $"The security review still reads it as vulnerable at line {r["line"]}: {r["why"]}",
                "fixed" => $"The security review reads line {r["line"]} as no longer vulnerable.",
                _ => $"The security review finds the call gone: {r["why"]}",
            });
        }
        return string.Join(" ", said);
    }

    private static bool Glob(string glob, string path) =>
        Regex.IsMatch(Path.GetFileName(path), "^" + Regex.Escape(glob).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\[", "[", StringComparison.Ordinal).Replace(@"\]", "]", StringComparison.Ordinal) + "$");

    private static string Squash(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Cut(string s, int most) => s.Length <= most ? s : s[..most];
}
