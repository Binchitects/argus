namespace Llm.Core.Insights;

/// <summary>
/// A database an admin connects for people to ask (Ask your data): read only, the tables named, for everyone or some
/// groups. Its connection (with its password) is kept protected and never shown again.
/// </summary>
public sealed class DataSource
{
    public const string Postgres = "postgres";
    public const string Company = "company";
    public const string Groups = "groups";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    /// <summary>"postgres".</summary>
    public string Kind { get; set; } = Postgres;
    public required string ConnectionProtected { get; set; }
    /// <summary>The tables and views people may ask, "schema.name"; nothing else is read.</summary>
    public List<string> Tables { get; set; } = [];
    /// <summary>What the model is told about the data beyond its columns ("amounts are in cents").</summary>
    public string Notes { get; set; } = "";
    /// <summary>Who may ask it: everyone, or the groups named.</summary>
    public string Reach { get; set; } = Company;
    public List<Guid> GroupIds { get; set; } = [];
    /// <summary>The database's tables and columns as last read, JSON (<c>[{ name, kind, comment, columns: [{ name, type, comment }] }]</c>).</summary>
    public string Schema { get; set; } = "[]";
    public DateTimeOffset? SchemaReadAt { get; set; }
    /// <summary>The most rows an answer brings back.</summary>
    public int MostRows { get; set; } = 1000;
    /// <summary>How long one query may run.</summary>
    public int Seconds { get; set; } = 15;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One query run on a data source, by whom and why: what admins read to see what was asked.</summary>
public sealed class DataQuery
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SourceId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>The person's question, when the model wrote the SQL; null for SQL the person ran as it was, or a board's refresh.</summary>
    public string? Question { get; set; }
    public required string Sql { get; set; }
    /// <summary>"ask", "run", "chat" or "board".</summary>
    public string Via { get; set; } = "ask";
    public int Rows { get; set; }
    public int Milliseconds { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A dashboard of charts pinned from answers, refreshed on their schedules; its owner's, or shared.</summary>
public sealed class DataBoard
{
    public const string Private = "private";
    public const string Groups = "groups";
    public const string Company = "company";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OwnerId { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    /// <summary>Who may see it: its owner, the groups named, or everyone. It shows what its owner may ask.</summary>
    public string Reach { get; set; } = Private;
    public List<Guid> GroupIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A chart on a board: an answer's SQL, run again on its schedule, its last result kept.</summary>
public sealed class DataPin
{
    public const string Off = "off";
    public const string Hourly = "hourly";
    public const string Daily = "daily";

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BoardId { get; set; }
    public Guid SourceId { get; set; }
    public required string Title { get; set; }
    public string Question { get; set; } = "";
    public required string Sql { get; set; }
    /// <summary>How it is drawn, JSON (<c>{ type: bar|line|number|table, x, y: [] }</c>).</summary>
    public string Chart { get; set; } = "{}";
    /// <summary>off, hourly or daily.</summary>
    public string Refresh { get; set; } = Daily;
    public int Position { get; set; }
    /// <summary>The last result, JSON (<c>{ columns: [{ name, type }], rows: [[…]], truncated }</c>).</summary>
    public string? Result { get; set; }
    public DateTimeOffset? RanAt { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
