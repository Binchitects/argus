namespace Llm.Core.Code;

/// <summary>
/// A security review of a repository at a ref by the model, run by a person with their own read access to the code
/// (Argus, read-only): the places where data reaches a dangerous call (a query, a command, a path, a fetch, a
/// deserialiser, HTML) found with git grep, each traced to its callers and judged; a finding is kept only when it cites
/// the sink's line as the file has it and its weakness is a CWE the security pack knows, and each is then put to a pass
/// that tries to refute it. Nothing is written to the code host.
/// </summary>
public sealed class SecurityScan
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>Who ran it: it read the code as them, and its cost is theirs.</summary>
    public Guid UserId { get; set; }
    /// <summary>The project's path (group/project).</summary>
    public required string Project { get; set; }
    /// <summary>The branch, tag or commit read; null: the default branch.</summary>
    public string? Ref { get; set; }
    /// <summary>The commit it read, once Argus has said which.</summary>
    public string? Commit { get; set; }
    public string Status { get; set; } = Queued;
    public string? Step { get; set; }
    public string? Model { get; set; }
    /// <summary>The places found where data reaches a dangerous call, and how many of them were read (the most likely first).</summary>
    public int Candidates { get; set; }
    public int Read { get; set; }
    /// <summary>Suspicions dropped because the sink's line could not be cited or the weakness is not one the pack knows.</summary>
    public int Dropped { get; set; }
    /// <summary>The packages its lock files and manifests name, and the day the advisories they were matched against are from (null: no advisories pack).</summary>
    public int Dependencies { get; set; }
    public DateOnly? AdvisoriesAsOf { get; set; }
    /// <summary>The packages as a CycloneDX bill of materials (JSON), to download.</summary>
    public string? Sbom { get; set; }
    /// <summary>
    /// The security scanners' run, as JSON: {"tools": [{name, version, ran, seconds, findings, error}], "history": bool,
    /// "found": n, "error": why none ran}; null when none are installed.
    /// </summary>
    public string? Scanners { get; set; }
    /// <summary>What the review did and found, in Markdown.</summary>
    public string? Summary { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public List<SecurityFinding> Findings { get; set; } = [];
}

/// <summary>A weakness a security review found: where untrusted data reaches a sink, how, and how to fix it.</summary>
public sealed class SecurityFinding
{
    public const string Open = "open";
    public const string Fixed = "fixed";
    /// <summary>Kept as it is, on purpose: who decided, why, and until when.</summary>
    public const string Accepted = "accepted";
    /// <summary>Not a weakness after all: remembered for the repository.</summary>
    public const string Wrong = "wrong";

    public const string Code = "code";
    public const string Dependency = "dependency";
    public const string Secret = "secret";
    public const string Config = "config";
    /// <summary>The model's review, in <see cref="FoundBy"/> beside the scanners' names.</summary>
    public const string Model = "model";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ScanId { get; set; }
    /// <summary>
    /// code (a sink untrusted data reaches, or a weak call), dependency (a package version an advisory affects), secret
    /// (a credential in the code or its history) or config (a misconfiguration: a Dockerfile, Kubernetes, Terraform).
    /// </summary>
    public string Kind { get; set; } = Code;
    /// <summary>What found it: the model's review (model) and the scanners (opengrep, bandit, gitleaks, trivy); both is surer.</summary>
    public string[] FoundBy { get; set; } = [];
    /// <summary>The scanners' rules that matched it.</summary>
    public string? Rule { get; set; }
    /// <summary>A secret found in the history: the commit it is in (it may be gone from the code since).</summary>
    public string? Commit { get; set; }
    /// <summary>A dependency's advisory is on CISA's list of known exploited vulnerabilities.</summary>
    public bool Exploited { get; set; }
    public required string Path { get; set; }
    /// <summary>The sink's line, cited as the file has it.</summary>
    public int Line { get; set; }
    public string SinkCode { get; set; } = "";
    /// <summary>The weakness, CWE-89 for instance: one the security pack knows.</summary>
    public string Cwe { get; set; } = "";
    public string CweTitle { get; set; } = "";
    /// <summary>critical, high, medium or low.</summary>
    public string Severity { get; set; } = "medium";
    public string Title { get; set; } = "";
    /// <summary>Where the untrusted data comes in.</summary>
    public string? Source { get; set; }
    /// <summary>How it gets from there to the sink, a step a line.</summary>
    public string Flow { get; set; } = "";
    public string Exploit { get; set; } = "";
    public string Fix { get; set; } = "";
    /// <summary>The security pack's pages it rests on, as JSON: [{title, url}].</summary>
    public string Pages { get; set; } = "[]";
    /// <summary>The second pass's verdict: true when it could not refute it; false when it did (shown apart, with why).</summary>
    public bool? Verified { get; set; }
    public string? VerifyNote { get; set; }
    public string State { get; set; } = Open;
    public Guid? DecidedById { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    /// <summary>Why it was accepted or judged wrong.</summary>
    public string? Why { get; set; }
    /// <summary>An accepted risk's end, when it is to be looked at again.</summary>
    public DateTimeOffset? Until { get; set; }
}
