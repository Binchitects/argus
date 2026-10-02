using System.Text.Json.Nodes;

namespace Argus.Indexing;

/// <summary>
/// How far an index pass is, for the server that started it: one line per step on
/// stdout, "@progress {json}", which the server reads into its job state (and keeps
/// out of the run log). Run by hand, the lines are plain to read too.
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

    /// <summary>The pass begins: how many repositories it will look at.</summary>
    public static void Pass(int repos) => Emit(new JsonObject { ["stage"] = "pass", ["repos"] = repos });

    /// <summary>A repository's branch begins (its place in the pass).</summary>
    public static void Branch(int position, string repo, string branch) =>
        Emit(new JsonObject { ["stage"] = "branch", ["position"] = position, ["repo"] = repo, ["branch"] = branch });

    /// <summary>Files of the branch's changes done so far (all of them: its symbols are read next).</summary>
    public static void Files(string repo, string branch, int done, int total) =>
        Emit(new JsonObject { ["stage"] = "files", ["repo"] = repo, ["branch"] = branch, ["done"] = done, ["total"] = total });

    /// <summary>The branch is done, and how.</summary>
    public static void BranchDone(string repo, string branch, string outcome) =>
        Emit(new JsonObject { ["stage"] = "branch_done", ["repo"] = repo, ["branch"] = branch, ["outcome"] = outcome });

    /// <summary>After every repository: includes, the repository graph, embeddings.</summary>
    public static void Finishing(string what) => Emit(new JsonObject { ["stage"] = "finishing", ["what"] = what });
}
