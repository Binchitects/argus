using Argus.Configuration;
using Argus.Store;
using Microsoft.Data.Sqlite;

namespace Argus.Tests;

/// <summary>A scratch directory that deletes itself.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "argus-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string rel, string? text = null)
    {
        var full = System.IO.Path.Combine(Path, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        if (text is not null) System.IO.File.WriteAllText(full, text);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>An index database populated through the real write path.</summary>
public sealed class TestIndex : IDisposable
{
    readonly TempDir _dir = new();
    public string DbPath { get; }
    public SqliteConnection Conn { get; }

    public TestIndex()
    {
        DbPath = System.IO.Path.Combine(_dir.Path, "index.db");
        Conn = Db.Open(DbPath);
    }

    public long Repo(long gitlabId, string path, string branch = "main", string defaultBranch = "main") =>
        Writes.UpsertRepo(Conn, gitlabId, path, defaultBranch, $"http://x/{path}.git", branch);

    public long File(long repoId, string path, string content, string? lang = "c") =>
        Writes.UpsertFile(Conn, repoId, path, lang, content.Length, "sha-" + path, content);

    public void Symbol(long repoId, long fileId, string name, string kind = "function", long line = 1, long isPublic = 1,
        string? signature = "()", string? doc = null) =>
        Writes.ReplaceSymbols(Conn, repoId, fileId,
            [new Writes.SymbolRow(name, kind, line, line, signature, null, isPublic, doc)], "2:x");

    public ArgusConfig Config(string packsDir = "") => new()
    {
        GitLab = GitLabConfig.Create("http://127.0.0.1:9", "svc"),
        Index = new IndexConfig { DataDir = _dir.Path, DbPath = DbPath },
        PacksDirSetting = packsDir.Length > 0 ? packsDir : System.IO.Path.Combine(_dir.Path, "packs"),
    };

    public string Root => _dir.Path;

    public void Dispose()
    {
        Conn.Dispose();
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }
}

/// <summary>Environment variables set for one test and restored afterwards.</summary>
public sealed class EnvScope : IDisposable
{
    readonly Dictionary<string, string?> _saved = new();

    public EnvScope(params (string Name, string? Value)[] vars)
    {
        foreach (var (name, value) in vars)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
    }
}

/// <summary>Tests that touch process-wide state (environment, static handlers) run one at a time.</summary>
[CollectionDefinition("process-state", DisableParallelization = true)]
public sealed class ProcessStateCollection;
