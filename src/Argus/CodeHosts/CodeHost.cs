using System.Text.Json.Nodes;

namespace Argus.CodeHosts;

/// <summary>
/// A code host's merge requests (pull requests on GitHub and Gitea), their diffs, commits, discussions and
/// CI, read with Argus's READ-ONLY service credential: every call is a GET, and nothing here writes to the
/// host. GitLab is the one host today (<see cref="GitLabHost"/>); GitHub and Gitea implement the same reads
/// later. Who may read a project is not the host's to say here: the caller checks it first
/// (<see cref="Access.Identity.MayRead"/>), from the project's members.
/// </summary>
public interface ICodeHost
{
    /// <summary>gitlab, github, gitea.</summary>
    string Kind { get; }

    /// <summary>The project's change requests in this state (opened, closed, merged, all), most recently updated first.</summary>
    IReadOnlyList<ChangeRequest> ListChanges(long project, string state, string? updatedAfter, string? author, int limit);

    /// <summary>One change request; null when the host has none by that number.</summary>
    ChangeRequest? GetChange(long project, long number);

    /// <summary>Each push to the change request, newest first: what a re-review compares.</summary>
    IReadOnlyList<ChangeVersion> Versions(long project, long number);

    /// <summary>The change request's files, at its latest version or at <paramref name="version"/>.</summary>
    IReadOnlyList<FileDiff> Diff(long project, long number, long? version = null);

    IReadOnlyList<CommitSummary> Commits(long project, long number);

    IReadOnlyList<Discussion> Discussions(long project, long number);

    /// <summary>The pipelines of a change request, or of a commit, newest first.</summary>
    IReadOnlyList<Pipeline> Pipelines(long project, long? number, string? sha);

    IReadOnlyList<CiJob> Jobs(long project, long pipeline);

    /// <summary>A job's log as the host keeps it; null when there is none.</summary>
    string? JobLog(long project, long job);

    /// <summary>
    /// What <paramref name="to"/> has that <paramref name="from"/> has not: from their merge base, or with
    /// <paramref name="straight"/> from <paramref name="from"/> itself.
    /// </summary>
    Comparison Compare(long project, string from, string to, bool straight = false);

    /// <summary>A file at a commit, through the host's API (when the mirror cannot answer); null when there is none.</summary>
    string? FileAt(long project, string sha, string path);

    /// <summary>The ref a change request's head is fetched from: refs/merge-requests/N/head on GitLab, refs/pull/N/head on GitHub.</summary>
    string ChangeRef(long number);

    /// <summary>The project's issues in this state (opened, closed, all), most recently updated first; with words, those that hold them.</summary>
    IReadOnlyList<Issue> ListIssues(long project, string state, string? search, string? labels, int limit) =>
        throw new CodeHostError($"Argus reads issues on GitLab only, not on {Kind}", 501);

    /// <summary>One issue with its text; null when the host has none by that number.</summary>
    Issue? GetIssue(long project, long number) => throw new CodeHostError($"Argus reads issues on GitLab only, not on {Kind}", 501);

    /// <summary>An issue's comments, oldest first.</summary>
    IReadOnlyList<Note> IssueNotes(long project, long number) => throw new CodeHostError($"Argus reads issues on GitLab only, not on {Kind}", 501);

    /// <summary>The project's wiki: its pages' slugs and titles.</summary>
    IReadOnlyList<WikiPage> WikiPages(long project) => throw new CodeHostError($"Argus reads wikis on GitLab only, not on {Kind}", 501);

    /// <summary>One wiki page with its text; null when there is none by that slug.</summary>
    WikiPage? GetWikiPage(long project, string slug) => throw new CodeHostError($"Argus reads wikis on GitLab only, not on {Kind}", 501);
}

/// <summary>An issue: what is asked or reported, by whom, in what state, its labels and milestone; its text when read one by one.</summary>
public sealed record Issue(long Number, string Title, string State, string Author, string? WebUrl, string? CreatedAt, string? UpdatedAt)
{
    public string? Description { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<string> Assignees { get; init; } = [];
    public string? Milestone { get; init; }
    public string? ClosedAt { get; init; }
    public int Comments { get; init; }

    /// <summary>What a list shows.</summary>
    public JsonObject ToSummary() => new()
    {
        ["iid"] = Number, ["title"] = Title, ["state"] = State, ["author"] = Author, ["labels"] = ChangeRequest.Strings(Labels),
        ["milestone"] = Milestone, ["comments"] = Comments, ["updated_at"] = UpdatedAt, ["web_url"] = WebUrl,
    };

    /// <summary>All of it, the text cut to <paramref name="maxDescription"/> characters.</summary>
    public JsonObject ToJson(int maxDescription)
    {
        var o = ToSummary();
        var text = Description ?? "";
        o["description"] = text.Length > maxDescription ? text[..maxDescription] : text;
        if (text.Length > maxDescription) o["description_truncated"] = true;
        o["assignees"] = ChangeRequest.Strings(Assignees);
        o["created_at"] = CreatedAt;
        o["closed_at"] = ClosedAt;
        return o;
    }
}

/// <summary>A wiki page: its slug (its address in the wiki), title and format; its text when read one by one.</summary>
public sealed record WikiPage(string Slug, string Title, string? Format)
{
    public string? Content { get; init; }

    public JsonObject ToSummary() => new() { ["slug"] = Slug, ["title"] = Title, ["format"] = Format };

    public JsonObject ToJson(int maxContent)
    {
        var o = ToSummary();
        var text = Content ?? "";
        o["content"] = text.Length > maxContent ? text[..maxContent] : text;
        if (text.Length > maxContent) o["content_truncated"] = true;
        return o;
    }
}

/// <summary>The host answered with an error, or could not be reached.</summary>
public sealed class CodeHostError(string message, int status = 0, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The HTTP status, or 0 when the host could not be reached.</summary>
    public int Status { get; } = status;
}

/// <summary>A change request's three commits: where its target branch was, their merge base, and its head.</summary>
public sealed record DiffRefs(string BaseSha, string StartSha, string HeadSha)
{
    public JsonObject ToJson() => new() { ["base_sha"] = BaseSha, ["start_sha"] = StartSha, ["head_sha"] = HeadSha };
}

/// <summary>A pipeline's state, in short.</summary>
public sealed record PipelineRef(long Id, string Status, string? Sha, string? Ref, string? WebUrl)
{
    public JsonObject ToJson() => new() { ["id"] = Id, ["status"] = Status, ["sha"] = Sha, ["ref"] = Ref, ["web_url"] = WebUrl };
}

/// <summary>A merge request (a pull request elsewhere): who, what, where to, and in what state.</summary>
public sealed record ChangeRequest(
    long Number, string Title, string State, bool Draft, string Author, string SourceBranch, string TargetBranch,
    string? Sha, string? WebUrl, string? CreatedAt, string? UpdatedAt)
{
    public string? Description { get; init; }
    public DiffRefs? Refs { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<string> Reviewers { get; init; } = [];
    public IReadOnlyList<string> Assignees { get; init; } = [];
    /// <summary>Whether it can be merged, in the host's words (mergeable, conflict, ci_must_pass ...).</summary>
    public string? MergeStatus { get; init; }
    public bool? HasConflicts { get; init; }
    public PipelineRef? HeadPipeline { get; init; }
    public string? MergedAt { get; init; }
    public string? ClosedAt { get; init; }
    /// <summary>How many files it changes, in the host's words ("12", "1000+").</summary>
    public string? ChangesCount { get; init; }

    public bool Open => State is "opened" or "open" or "locked";

    /// <summary>What a list shows.</summary>
    public JsonObject ToSummary() => new()
    {
        ["iid"] = Number, ["title"] = Title, ["state"] = State, ["draft"] = Draft, ["author"] = Author,
        ["source_branch"] = SourceBranch, ["target_branch"] = TargetBranch, ["sha"] = Sha,
        ["labels"] = Strings(Labels), ["updated_at"] = UpdatedAt, ["web_url"] = WebUrl,
    };

    /// <summary>All of it, the description cut to <paramref name="maxDescription"/> characters.</summary>
    public JsonObject ToJson(int maxDescription)
    {
        var o = ToSummary();
        var text = Description ?? "";
        o["description"] = text.Length > maxDescription ? text[..maxDescription] : text;
        if (text.Length > maxDescription) o["description_truncated"] = true;
        o["diff_refs"] = Refs?.ToJson();
        o["reviewers"] = Strings(Reviewers);
        o["assignees"] = Strings(Assignees);
        o["merge_status"] = MergeStatus;
        o["has_conflicts"] = HasConflicts;
        o["head_pipeline"] = HeadPipeline?.ToJson();
        o["changes_count"] = ChangesCount;
        o["created_at"] = CreatedAt;
        o["merged_at"] = MergedAt;
        o["closed_at"] = ClosedAt;
        return o;
    }

    internal static JsonArray Strings(IEnumerable<string> items) => new([.. items.Select(i => (JsonNode)i)]);
}

/// <summary>One push to a change request: its head, and the target branch it was compared with.</summary>
public sealed record ChangeVersion(long Id, string HeadSha, string BaseSha, string StartSha, string? CreatedAt)
{
    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["head_sha"] = HeadSha, ["base_sha"] = BaseSha, ["start_sha"] = StartSha, ["created_at"] = CreatedAt,
    };
}

/// <summary>One file of a diff: its paths, what happened to it, and its hunks (unified diff text, from the first @@).</summary>
public sealed record FileDiff(string OldPath, string NewPath, bool NewFile, bool DeletedFile, bool RenamedFile, string Diff)
{
    /// <summary>The host left the text out: too large, or collapsed.</summary>
    public bool TooLarge { get; init; }
    /// <summary>Binary content, which has no hunks.</summary>
    public bool Binary { get; init; }

    public (int Added, int Removed) Counts()
    {
        int added = 0, removed = 0;
        foreach (var line in Diff.Split('\n'))
        {
            if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)) added++;
            else if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal)) removed++;
        }
        return (added, removed);
    }

    /// <summary>Its paths, state and counts, with the diff text when <paramref name="withText"/>.</summary>
    public JsonObject ToJson(bool withText)
    {
        var (added, removed) = Counts();
        var o = new JsonObject
        {
            ["old_path"] = OldPath, ["new_path"] = NewPath, ["new_file"] = NewFile, ["deleted_file"] = DeletedFile,
            ["renamed_file"] = RenamedFile, ["additions"] = added, ["deletions"] = removed,
        };
        if (TooLarge) o["too_large"] = true;
        if (Binary) o["binary"] = true;
        if (withText) o["diff"] = Diff;
        return o;
    }
}

public sealed record CommitSummary(string Sha, string Title, string Message, string Author, string? AuthoredAt)
{
    public JsonObject ToJson() => new() { ["sha"] = Sha, ["title"] = Title, ["message"] = Message, ["author"] = Author, ["authored_at"] = AuthoredAt };
}

/// <summary>Where a note is anchored in the diff, in the host's own terms (posting it back needs no conversion).</summary>
public sealed record NotePosition(string? BaseSha, string? StartSha, string? HeadSha, string? OldPath, string? NewPath, long? OldLine, long? NewLine)
{
    public JsonObject ToJson() => new()
    {
        ["base_sha"] = BaseSha, ["start_sha"] = StartSha, ["head_sha"] = HeadSha, ["old_path"] = OldPath, ["new_path"] = NewPath,
        ["old_line"] = OldLine, ["new_line"] = NewLine,
    };
}

public sealed record Note(long Id, string Author, string Body, string? CreatedAt, bool System, bool Resolvable, bool Resolved, NotePosition? Position)
{
    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["author"] = Author, ["body"] = Body, ["created_at"] = CreatedAt, ["system"] = System,
        ["resolvable"] = Resolvable, ["resolved"] = Resolved, ["position"] = Position?.ToJson(),
    };
}

/// <summary>A thread: its notes in order, and whether it is resolved.</summary>
public sealed record Discussion(string Id, IReadOnlyList<Note> Notes)
{
    public bool Resolvable => Notes.Any(n => n.Resolvable);
    public bool Resolved => Resolvable && Notes.Where(n => n.Resolvable).All(n => n.Resolved);
    /// <summary>Only the host's own notes (pushes, label changes), none written by a person.</summary>
    public bool System => Notes.Count > 0 && Notes.All(n => n.System);
}

public sealed record Pipeline(long Id, string Status, string? Sha, string? Ref, string? WebUrl, string? CreatedAt, string? UpdatedAt)
{
    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["status"] = Status, ["sha"] = Sha, ["ref"] = Ref, ["web_url"] = WebUrl, ["created_at"] = CreatedAt, ["updated_at"] = UpdatedAt,
    };
}

public sealed record CiJob(long Id, string Name, string Stage, string Status, string? FailureReason, bool AllowFailure, double? Duration, string? WebUrl)
{
    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["name"] = Name, ["stage"] = Stage, ["status"] = Status, ["failure_reason"] = FailureReason,
        ["allow_failure"] = AllowFailure, ["duration"] = Duration, ["web_url"] = WebUrl,
    };
}

/// <summary>Two refs compared: the commits between them and the files they change.</summary>
public sealed record Comparison(IReadOnlyList<CommitSummary> Commits, IReadOnlyList<FileDiff> Diffs, bool TimedOut);
