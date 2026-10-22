using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// A review's finding: where (a path of the folder, its first and last line), how bad (critical, major, minor, nit),
/// what is wrong and why it matters, the fix in words, and, when the model gave one that fits the file as it is, the
/// exact text to replace and what replaces it (the IDE applies it in the editor). Open until fixed or dismissed.
/// </summary>
internal sealed record ReviewFinding(int Index, string Path, int Line, int EndLine, string Severity, string Title, string Why, string Fix, string? Old, string? New)
{
    public string State { get; set; } = Reviews.Open;
}

/// <summary>One review: of what (the changes not yet committed, or the branch over a base), how it went, and what it found.</summary>
internal sealed class ReviewRun
{
    public required string Id { get; init; }
    /// <summary>The branch or commit the branch is compared with; null: the changes not yet committed.</summary>
    public string? Base { get; init; }
    public DateTimeOffset Started { get; init; }
    public DateTimeOffset? Ended { get; set; }
    public string? Verdict { get; set; }
    public string? Summary { get; set; }
    public string? Error { get; set; }
    public List<ReviewFinding> Findings { get; } = [];
    /// <summary>What the reviewer did, a line a step (the files it read, the searches, git): the IDE shows them as it goes.</summary>
    public List<string> Steps { get; } = [];
    public bool Running => Ended is null;
}

/// <summary>
/// The IDE's reviews (the Review view): a fresh agent with the reading tools (files, search, git, Arena's and Argus's
/// tools that change nothing) reviews the changes not yet committed, or what the branch has over a base, as the Code
/// page's reviews and /review do: each finding refuted first, nothing changed. It reports through report_review, so
/// each finding has its file and line; one whose file is not there is left out, and a fix whose text is not in the file
/// once is kept in words only. The findings are Problems too (source "review"), so the editor marks them.
/// One review at a time; the last is kept until the next starts.
/// </summary>
internal sealed partial class Reviews(Runtime rt, Problems problems)
{
    public const string Source = "review";
    public const string Open = "open";
    public const string Fixed = "fixed";
    public const string Dismissed = "dismissed";
    public const int MostFindings = 100;
    public static readonly string[] Severities = ["critical", "major", "minor", "nit"];
    public static readonly string[] Verdicts = ["looks good", "comments", "changes needed"];

    /// <summary>The longest a review runs.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(20);

    private readonly object _gate = new();
    private CancellationTokenSource? _stop;

    public ReviewRun? Last { get; private set; }

    /// <summary>A review started (of the changes not yet committed, or the branch over <paramref name="baseRef"/>); one at a time.</summary>
    public ReviewRun Start(string? baseRef)
    {
        baseRef = baseRef?.Trim() is { Length: > 0 } b ? b : null;
        if (baseRef is not null && !Ref().IsMatch(baseRef))
        {
            throw new IdeError(400, "invalid", "The base is a branch or a commit (main, origin/main, a hash).");
        }
        lock (_gate)
        {
            if (Last is { Running: true })
            {
                throw new IdeError(409, "running", "A review is running: wait for it, or stop it.");
            }
            var run = new ReviewRun { Id = Guid.NewGuid().ToString("N")[..12], Base = baseRef, Started = DateTimeOffset.UtcNow };
            Last = run;
            _stop = new CancellationTokenSource(Limit);
            problems.Found(Source, []);
            var stop = _stop.Token;
            _ = Task.Run(() => RunAsync(run, stop));
            return run;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stop?.Cancel();
        }
    }

    /// <summary>A finding marked fixed (the IDE applied its fix) or dismissed, or open again; Problems keep the open ones.</summary>
    public ReviewFinding Mark(int index, string state)
    {
        if (state is not (Open or Fixed or Dismissed))
        {
            throw new IdeError(400, "invalid", "A finding is open, fixed or dismissed.");
        }
        lock (_gate)
        {
            var finding = Last?.Findings.FirstOrDefault(f => f.Index == index) ?? throw new IdeError(404, "not_found", "There is no such finding in the last review.");
            finding.State = state;
            problems.Found(Source, AsProblems(Last!));
            return finding;
        }
    }

    private async Task RunAsync(ReviewRun run, CancellationToken ct)
    {
        JsonObject? reported = null;
        var report = new ToolDef
        {
            Name = "report_review",
            Kind = ToolKind.Read,
            Description = "Report the review, once, when you are done: the verdict, a summary of the change in two or three sentences, and every finding you kept.",
            Parameters = LocalTools.Schema("""
                {"type":"object","properties":{
                  "verdict":{"type":"string","enum":["looks good","comments","changes needed"]},
                  "summary":{"type":"string","description":"What the change does and how it looks, in two or three sentences."},
                  "findings":{"type":"array","items":{"type":"object","properties":{
                    "path":{"type":"string","description":"The file, relative to the working directory."},
                    "line":{"type":"integer","description":"The line in the file as it is now, from 1."},
                    "end_line":{"type":"integer","description":"The last line it is about, when more than one."},
                    "severity":{"type":"string","enum":["critical","major","minor","nit"]},
                    "title":{"type":"string","description":"What is wrong, in one line."},
                    "why":{"type":"string","description":"Why it matters: what goes wrong, and when."},
                    "fix":{"type":"string","description":"The fix, in words."},
                    "old":{"type":"string","description":"For a small fix: the exact text in the file now (whole lines, as they are, unique in the file)."},
                    "new":{"type":"string","description":"For a small fix: what replaces old."}},
                   "required":["path","line","severity","title","why","fix"]}}},
                 "required":["verdict","summary","findings"]}
                """),
            Summary = a => $"{(a["findings"] as JsonArray)?.Count ?? 0} findings, {a.Str("verdict")}",
            Run = (args, _, _) =>
            {
                reported = args;
                return Task.FromResult(new ToolResult("Reported. Your review is done: answer with one line."));
            },
        };
        try
        {
            var said = await rt.ReviewAsync(Prompt(run.Base), report, new Steps(run), ct);
            if (reported is null)
            {
                lock (_gate)
                {
                    run.Error = "The reviewer ended without reporting its findings" + (said.Trim().Length > 0 ? $": {Fmt.OneLine(said, 300)}" : ".");
                }
                return;
            }
            Take(run, reported);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                run.Error = run.Started + Limit <= DateTimeOffset.UtcNow ? $"Stopped after {Limit.TotalMinutes:0} minutes." : "Stopped.";
            }
        }
        catch (Exception e) when (e is GatewayException or ToolError or IOException or InvalidOperationException)
        {
            lock (_gate)
            {
                run.Error = e.Message;
            }
        }
        finally
        {
            lock (_gate)
            {
                run.Ended = DateTimeOffset.UtcNow;
                if (ReferenceEquals(Last, run))
                {
                    problems.Found(Source, AsProblems(run));
                }
            }
        }
    }

    /// <summary>What the reviewer reported, checked against the files: findings on files that are there, lines within them, fixes that fit.</summary>
    private void Take(ReviewRun run, JsonObject reported)
    {
        var findings = new List<ReviewFinding>();
        foreach (var f in (reported["findings"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (findings.Count == MostFindings)
            {
                break;
            }
            string full;
            try
            {
                full = rt.Workspace.Resolve(f.Str("path"));
            }
            catch (ToolError)
            {
                continue;
            }
            if (!File.Exists(full))
            {
                continue;
            }
            var text = File.ReadAllText(full);
            var lines = text.Split('\n').Length;
            var line = Math.Clamp(f.Int("line") ?? 1, 1, lines);
            var end = Math.Clamp(f.Int("end_line") ?? line, line, lines);
            var (old, @new) = (f.Str("old"), f.Str("new"));
            // A fix applies only where its text is in the file once (as the editor would find it).
            if (old is not { Length: > 0 } || @new is null || Count(text.Replace("\r\n", "\n", StringComparison.Ordinal), old.Replace("\r\n", "\n", StringComparison.Ordinal)) != 1)
            {
                (old, @new) = (null, null);
            }
            var severity = f.Str("severity")?.Trim().ToLowerInvariant() is { } s && Severities.Contains(s) ? s : "minor";
            findings.Add(new ReviewFinding(findings.Count, Path.GetRelativePath(rt.Workspace.Root, full).Replace('\\', '/'), line, end, severity,
                Cut(f.Str("title"), 200) is { Length: > 0 } title ? title : "A finding", Cut(f.Str("why"), 2000), Cut(f.Str("fix"), 2000), old, @new));
        }
        lock (_gate)
        {
            run.Verdict = reported.Str("verdict")?.Trim().ToLowerInvariant() is { } v && Verdicts.Contains(v) ? v : findings.Count == 0 ? "looks good" : "comments";
            run.Summary = Cut(reported.Str("summary"), 2000);
            // Worst first, then by file and line.
            run.Findings.AddRange(findings.OrderBy(f => Array.IndexOf(Severities, f.Severity)).ThenBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Line)
                .Select((f, i) => f with { Index = i }));
        }
    }

    /// <summary>The open findings as Problems: critical and major ones errors, minor warnings, nits notes.</summary>
    private static List<Problem> AsProblems(ReviewRun run) =>
        [.. run.Findings.Where(f => f.State == Open).Select(f => new Problem(f.Path, f.Line, 1, f.Severity switch { "critical" or "major" => "error", "minor" => "warning", _ => "info" },
            f.Severity, f.Title, Source))];

    /// <summary>The review's turn: /review's, its findings reported with report_review.</summary>
    public static string Prompt(string? baseRef) =>
        Repl.ReviewPrompt(baseRef ?? "", reportTool: true);

    public JsonObject Json()
    {
        lock (_gate)
        {
            if (Last is not { } run)
            {
                return new JsonObject { ["review"] = null };
            }
            return new JsonObject
            {
                ["review"] = new JsonObject
                {
                    ["id"] = run.Id, ["base"] = run.Base, ["started"] = run.Started.ToString("o"), ["ended"] = run.Ended?.ToString("o"), ["running"] = run.Running,
                    ["verdict"] = run.Verdict, ["summary"] = run.Summary, ["error"] = run.Error,
                    ["steps"] = new JsonArray([.. StepsOf(run).Select(s => (JsonNode)s)]),
                    ["findings"] = new JsonArray([.. run.Findings.Select(f => (JsonNode)new JsonObject
                    {
                        ["index"] = f.Index, ["path"] = f.Path, ["line"] = f.Line, ["endLine"] = f.EndLine, ["severity"] = f.Severity, ["title"] = f.Title,
                        ["why"] = f.Why, ["fix"] = f.Fix, ["old"] = f.Old, ["new"] = f.New, ["state"] = f.State,
                    })]),
                },
            };
        }
    }

    private static List<string> StepsOf(ReviewRun run)
    {
        lock (run.Steps)
        {
            return [.. run.Steps.TakeLast(200)];
        }
    }

    private static string Cut(string? text, int most) => (text ?? "").Trim() is var t && t.Length > most ? t[..most] : (text ?? "").Trim();

    private static int Count(string text, string piece)
    {
        var count = 0;
        for (var at = text.IndexOf(piece, StringComparison.Ordinal); at >= 0 && count < 2; at = text.IndexOf(piece, at + 1, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/~^@{}-]{0,199}$")]
    private static partial Regex Ref();

    /// <summary>The reviewer's steps, a line each: what it read, searched and asked.</summary>
    private sealed class Steps(ReviewRun run) : IAgentEvents
    {
        public void Step()
        {
        }

        public void Reasoning(string text)
        {
        }

        public void Text(string text)
        {
        }

        public void StepDone(TokenUsage? usage)
        {
        }

        public void ToolCall(string id, string name, ToolDef? tool, string arguments)
        {
            if (name == "report_review")
            {
                return;
            }
            string what;
            try
            {
                what = JsonNode.Parse(arguments) is JsonObject args && tool?.Summary is { } summary ? summary(args) : "";
            }
            catch (System.Text.Json.JsonException)
            {
                what = "";
            }
            lock (run.Steps)
            {
                run.Steps.Add(Fmt.OneLine($"{name} {what}".Trim(), 200));
            }
        }

        public void ToolResult(string id, string name, int index, ToolResult result, TimeSpan took, bool declined)
        {
        }

        public void Compacted(string notice)
        {
        }

        public void Notice(string text)
        {
            lock (run.Steps)
            {
                run.Steps.Add(Fmt.OneLine(text, 200));
            }
        }
    }
}
