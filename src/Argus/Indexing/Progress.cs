using System.Text.Json.Nodes;

namespace Argus.Indexing;

/// <summary>
/// How far an index pass is, for the server that started it: one line per step on
/// stdout, "@progress {json}", which the server reads into its job state (and keeps
/// out of the run log). Run by hand, the lines are plain to read too.
///
/// Each repository goes queued, fetching, then per branch files (x of y) and symbols,
/// then embedding (x of y), and ends done or failed with why.
/// </summary>
public static class Progress
{
    public const string Prefix = "@progress ";

    /// <summary>Where the lines go: stdout by default, a test's list otherwise.</summary>
    public static Action<string> Write { get; set; } = line =>
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    };

    public static void Emit(JsonObject step) => Write(Prefix + step.ToJsonString());

    /// <summary>The pass begins: the repositories it will look at, in order (each queued until its turn).</summary>
    public static void Pass(IReadOnlyList<string> repos) =>
        Emit(new JsonObject { ["stage"] = "pass", ["repos"] = repos.Count, ["names"] = new JsonArray([.. repos.Select(r => (JsonNode)r)]) });

    /// <summary>A repository's turn: its mirror is brought up to date from GitLab.</summary>
    public static void Fetching(int position, string repo) =>
        Emit(new JsonObject { ["stage"] = "fetching", ["position"] = position, ["repo"] = repo });

    /// <summary>A repository's branch begins (its place in the pass).</summary>
    public static void Branch(int position, string repo, string branch) =>
        Emit(new JsonObject { ["stage"] = "branch", ["position"] = position, ["repo"] = repo, ["branch"] = branch });

    /// <summary>Files of the branch's changes done so far (all of them: its symbols are read next).</summary>
    public static void Files(string repo, string branch, int done, int total) =>
        Emit(new JsonObject { ["stage"] = "files", ["repo"] = repo, ["branch"] = branch, ["done"] = done, ["total"] = total });

    /// <summary>Symbols are being read from the branch's files.</summary>
    public static void Symbols(string repo, string branch, int files) =>
        Emit(new JsonObject { ["stage"] = "symbols", ["repo"] = repo, ["branch"] = branch, ["total"] = files });

    /// <summary>The branch is done, and how.</summary>
    public static void BranchDone(string repo, string branch, string outcome) =>
        Emit(new JsonObject { ["stage"] = "branch_done", ["repo"] = repo, ["branch"] = branch, ["outcome"] = outcome });

    /// <summary>The repository's new symbols embedded for meaning search, so far.</summary>
    public static void Embedding(string repo, int done, int total) =>
        Emit(new JsonObject { ["stage"] = "embedding", ["repo"] = repo, ["done"] = done, ["total"] = total });

    /// <summary>The repository is done: ok, up_to_date, warning or failed, and in a sentence what came of it.</summary>
    public static void RepoDone(string repo, string outcome, string message) =>
        Emit(new JsonObject { ["stage"] = "repo_done", ["repo"] = repo, ["outcome"] = outcome, ["message"] = message });

    /// <summary>After every repository: includes, the repository graph, embeddings.</summary>
    public static void Finishing(string what) => Emit(new JsonObject { ["stage"] = "finishing", ["what"] = what });
}
