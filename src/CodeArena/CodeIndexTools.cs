using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// A session's code index: opened in the background as the session starts (read from disk, then synced), brought in step
/// with the folder before each question (by size and time, at most once a second), written back after changes (at most
/// every ten seconds, and when the session ends).
/// </summary>
internal sealed class CodeIndexHost : IDisposable
{
    /// <summary>How long a question waits for the first build before it is told to use grep meanwhile.</summary>
    public static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(15);

    private readonly string _dataDir;
    private readonly Workspace _workspace;
    private readonly object _gate = new();
    private Task<CodeIndex> _opened;
    private DateTime _synced;
    private DateTime _saved = DateTime.UtcNow;
    private bool _dirty;

    public CodeIndexHost(string dataDir, Workspace workspace)
    {
        _dataDir = dataDir;
        _workspace = workspace;
        _opened = Open(rebuild: false);
    }

    /// <summary>Off for this session (/index off): the tools say so, and grep is used.</summary>
    public bool Off { get; set; }

    /// <summary>The index once the first build is done; null before.</summary>
    public CodeIndex? Ready => _opened.IsCompletedSuccessfully ? _opened.Result : null;

    /// <summary>What the last sync did (the first build's, until a question syncs again).</summary>
    public IndexSync? LastSync { get; private set; }

    /// <summary>Whether the last opening built the index whole (none was kept, or it could not be read).</summary>
    public bool Built { get; private set; }

    private Task<CodeIndex> Open(bool rebuild) => Task.Run(() =>
    {
        var (index, sync, built) = CodeIndex.Open(_dataDir, _workspace, CancellationToken.None, rebuild);
        lock (_gate)
        {
            _synced = DateTime.UtcNow;
            LastSync = sync;
            Built = built;
        }
        return index;
    });

    /// <summary>The index in step with the folder; null when the first build is not done within <paramref name="wait"/>.</summary>
    public async Task<CodeIndex?> GetAsync(TimeSpan wait, CancellationToken ct)
    {
        try
        {
            await _opened.WaitAsync(wait, ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
        var index = _opened.Result;
        lock (_gate)
        {
            if (DateTime.UtcNow - _synced >= TimeSpan.FromSeconds(1))
            {
                var sync = index.Sync(_workspace, _workspace.Roots, ct);
                _synced = DateTime.UtcNow;
                LastSync = sync;
                _dirty |= sync.Read > 0 || sync.Removed > 0;
            }
            if (_dirty && DateTime.UtcNow - _saved > TimeSpan.FromSeconds(10))
            {
                index.Save();
                _saved = DateTime.UtcNow;
                _dirty = false;
            }
        }
        return index;
    }

    /// <summary>Built again from nothing (/index rebuild).</summary>
    public async Task<CodeIndex> RebuildAsync()
    {
        var next = Open(rebuild: true);
        lock (_gate)
        {
            _opened = next;
            _dirty = false;
        }
        return await next;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_dirty && Ready is { } index)
            {
                index.Save();
            }
        }
    }
}

/// <summary>The agent's tools over the code index: code_search, code_symbol and code_outline (Config.CodeIndex).</summary>
internal static class CodeIndexTools
{
    public static readonly string[] Names = ["code_search", "code_symbol", "code_outline"];

    /// <summary>The tools, or those of them named (codeIndexTools).</summary>
    public static List<ToolDef> All(IReadOnlyCollection<string>? only = null) =>
        [.. new[] { Search(), Symbol(), Outline() }.Where(t => only is null || only.Contains(t.Name))];

    private static ToolDef Search() => new()
    {
        Name = "code_search",
        Kind = ToolKind.Read,
        Description = "Find code by what it does or by its name, from this folder's code index: the places that matter most, each cited as path:start-end with its symbol and its lines (numbered as read_file numbers them), up to a budget. " +
                      "A name gives its definitions and every use in code, grouped by the function each is in. Ranked, not every match, unless it says complete: for every occurrence of a pattern, a regex, or text in comments and strings, use grep.",
        Parameters = LocalTools.Schema("""
            {"type":"object","properties":{
              "query":{"type":"string","description":"What to find: what the code does (where big tool results are cut), a name (ToolDef, KeptResults.Keep, run_tools), or both."},
              "path":{"type":"string","description":"Only under this folder or file, or matching this glob (src/**/*.cs)."},
              "tests":{"type":"boolean","description":"Rank tests like other code (default: below it, unless the query says test)."},
              "budget":{"type":"integer","description":"Tokens the answer may take (default 2000, 300-4000)."}},
             "required":["query"]}
            """),
        Summary = a => a.Str("query") + (a.Str("path") is { } p ? $" in {p}" : ""),
        Run = (a, c, ct) => Answer(c, a, 2000, ct, (search, budget, cpt, may) =>
        {
            if (a.Str("query") is not { Length: > 0 } query)
            {
                return null;
            }
            var path = a.Str("path") is { Length: > 0 } p ? Within(c, p) : null;
            return search.Answer(new CodeQuery(query) { Path = path, Tests = a.Bool("tests") == true, Budget = budget, CharsPerToken = cpt, MayRead = may, Shown = c.Shown });
        }),
    };

    private static ToolDef Symbol() => new()
    {
        Name = "code_symbol",
        Kind = ToolKind.Read,
        Description = "One symbol from the code index: its source, its members and who calls it (show: source, the default), its callers, its callees, or its impact (what reaches it, steps out, and the tests among them). " +
                      "Each use says how it was tied: scope (the code's own context), type (a declared type), name (the one definition), guess (one of two or three). A name of several symbols gives them as path#Qualified: name one.",
        Parameters = LocalTools.Schema("""
            {"type":"object","properties":{
              "name":{"type":"string","description":"A symbol: Name, Type.Member, or path#Type.Member as code_search cites it."},
              "show":{"type":"string","enum":["source","callers","callees","impact"],"description":"source (the default: its code, its members and who calls it), callers, callees, or impact (what reaches it, steps out, and the tests among them)."},
              "depth":{"type":"integer","description":"impact: steps out (1-3, default 2)."},
              "budget":{"type":"integer","description":"Tokens the answer may take (default 1500, 300-4000)."}},
             "required":["name"]}
            """),
        Summary = a => a.Str("name") + (a.Str("show") is { } s and not "source" ? $" ({s})" : ""),
        Run = (a, c, ct) => Answer(c, a, 1500, ct, (search, budget, cpt, may) => a.Str("name") is { Length: > 0 } name
            ? search.Symbol(new SymbolQuery(name) { Show = a.Str("show") ?? "source", Depth = a.Int("depth") ?? 2, Budget = budget, CharsPerToken = cpt, MayRead = may, Shown = c.Shown })
            : null),
    };

    private static ToolDef Outline() => new()
    {
        Name = "code_outline",
        Kind = ToolKind.Read,
        Description = "The map of a file or a folder from the code index. A file: its symbols, nested, each with its lines and its first line. A folder: its folders, then the files most used from elsewhere, each with its main types and functions.",
        Parameters = LocalTools.Schema("""
            {"type":"object","properties":{
              "path":{"type":"string","description":"A file or folder (default: the working directory)."},
              "budget":{"type":"integer","description":"Tokens (default 1200, 300-4000)."}}}
            """),
        Summary = a => a.Str("path") ?? ".",
        Run = (a, c, ct) => Answer(c, a, 1200, ct, (search, budget, cpt, may) =>
            search.Outline(a.Str("path") is { Length: > 0 } p ? Within(c, p) : null, budget, cpt, may)),
    };

    /// <summary>A path the agent gave, as the index has it (relative to the working directory); a glob as it is.</summary>
    private static string Within(ToolContext c, string path) =>
        path.IndexOfAny(['*', '?', '[', '{']) >= 0 ? path : c.Workspace.Show(c.Workspace.Resolve(path)).Replace('\\', '/');

    /// <summary>
    /// The answer of one of the tools: the index in step first (or why there is none), a budget no larger than a result kept
    /// whole, and the person's deny rules applied to every path cited.
    /// </summary>
    private static async Task<ToolResult> Answer(ToolContext c, JsonObject a, int defaultBudget, CancellationToken ct, Func<CodeSearch, int, double, Func<string, bool>?, CodeAnswer?> ask)
    {
        if (c.Code is not { } host)
        {
            return new ToolResult("The code index is not on in this session: use grep and glob.", Error: true);
        }
        if (host.Off)
        {
            return new ToolResult("The code index is off for this session (/index off): use grep and glob.", Error: true);
        }
        var index = await host.GetAsync(CodeIndexHost.FirstWait, ct);
        if (index is null)
        {
            return new ToolResult("The code index is still being built (the first time in this folder): use grep and glob meanwhile, or ask again in a minute.", Error: true);
        }
        var cpt = c.CharsPerToken?.Invoke() ?? 3.5;
        var budget = Math.Clamp(a.Int("budget") ?? defaultBudget, 300, 4000);
        if (c.ResultChars?.Invoke() is { } most)
        {
            budget = Math.Max(300, Math.Min(budget, (int)(most / cpt)));
        }
        CodeAnswer? answer;
        try
        {
            answer = ask(index.Search(), budget, cpt, c.MayRead);
        }
        catch (ToolError e)
        {
            return new ToolResult(e.Message, Error: true);
        }
        if (answer is null)
        {
            return new ToolResult("Say what to look for.", Error: true);
        }
        return new ToolResult(answer.Text) { Display = answer.Text.Split('\n')[0] };
    }
}
