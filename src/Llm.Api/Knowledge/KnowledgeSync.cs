using System.Threading.Channels;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Knowledge;

/// <summary>A source cannot be read as it is set, in words for the admin page.</summary>
public sealed class KnowledgeException(string message) : Exception(message);

/// <summary>
/// One sync of a source: its connector lists what it has now; a document new or changed (its version,
/// title or the embedding model) is cut into passages and embedded again; one unchanged keeps its passages
/// (its readers brought up to date); one gone is removed, unless part of the source could not be read.
/// </summary>
public sealed partial class KnowledgeSyncer(AppDbContext db, IEnumerable<IKnowledgeConnector> connectors, Embedder embedder, KnowledgeStore store, TimeProvider clock,
    ILogger<KnowledgeSyncer> logger)
{
    /// <summary>The longest key a document may have (a path, an address).</summary>
    private const int MaxKey = 1000;

    public IKnowledgeConnector? Connector(string kind) => connectors.FirstOrDefault(c => c.Kind == kind);

    public async Task SyncAsync(Guid id, CancellationToken ct)
    {
        if (await db.KnowledgeSources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) is not { } source)
        {
            return;
        }
        var started = clock.GetUtcNow();
        await db.KnowledgeSources.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.State, "syncing").SetProperty(s => s.SyncStartedAt, started), ct);
        var pass = new SyncPass();
        var state = "synced";
        string? error = null;
        try
        {
            var connector = Connector(source.Kind) ?? throw new KnowledgeException($"Nothing reads {source.Kind} sources.");
            if (connector.Check(source) is { } problem)
            {
                throw new KnowledgeException(problem);
            }
            if (!await embedder.ReadyAsync(ct))
            {
                throw new KnowledgeException("The embedder (the embed module) is not running: knowledge cannot be embedded.");
            }
            var known = await db.KnowledgeDocuments.AsNoTracking().Where(d => d.SourceId == id)
                .Select(d => new { d.Id, d.Key, d.Title, d.Url, d.Version, d.Model, d.Readers, d.Requires }).ToDictionaryAsync(d => d.Key, StringComparer.Ordinal, ct);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var found in connector.ReadAsync(source, pass, ct))
            {
                if (!seen.Add(found.Key))
                {
                    continue;
                }
                if (found.Key.Length > MaxKey)
                {
                    pass.Problems.Add($"{found.Key[..80]}…: its name or address is too long to keep");
                    continue;
                }
                var had = known.GetValueOrDefault(found.Key);
                IReadOnlyList<string> requires = found.Requires ?? [];
                if (had is not null && had.Version == found.Version && had.Model == embedder.Model && had.Title == Cut(found.Title, 500))
                {
                    if (had.Url != found.Url || !had.Readers.SequenceEqual(found.Readers) || !had.Requires.SequenceEqual(requires))
                    {
                        List<string> readers = [.. found.Readers];
                        List<string> must = [.. requires];
                        var url = found.Url;
                        await db.KnowledgeDocuments.Where(d => d.Id == had.Id)
                            .ExecuteUpdateAsync(u => u.SetProperty(d => d.Readers, readers).SetProperty(d => d.Requires, must).SetProperty(d => d.Url, url), ct);
                    }
                    continue;
                }
                if (await found.Text(ct) is not { } text)
                {
                    pass.Kept.Add(found.Key);
                    continue;
                }
                var chunks = Chunker.Split(text);
                var vectors = chunks.Count == 0 ? [] : await embedder.DocumentsAsync([.. chunks.Select(c => Chunker.ForEmbedding(found.Title, c))], ct);
                await store.SaveAsync(new KnowledgeDocument
                {
                    Id = had?.Id ?? Guid.CreateVersion7(), SourceId = id, Key = found.Key, Title = Cut(found.Title, 500)!, Url = Cut(found.Url, 2000), Version = found.Version,
                    Model = embedder.Model, Readers = [.. found.Readers], Requires = [.. requires], SyncedAt = clock.GetUtcNow(),
                }, chunks, vectors, ct);
            }
            // What the source says is gone goes; what was not seen goes too, unless part of the source could not be read.
            var gone = known.Values
                .Where(d => !seen.Contains(d.Key) && !pass.Kept.Contains(d.Key)
                    && (pass.Gone.Contains(d.Key) || (!pass.Incomplete && !pass.Unchanged.Any(p => d.Key.StartsWith(p, StringComparison.Ordinal)))))
                .Select(d => d.Id).ToList();
            foreach (var batch in gone.Chunk(500))
            {
                await db.KnowledgeDocuments.Where(d => batch.Contains(d.Id)).ExecuteDeleteAsync(ct);
            }
            error = pass.Problems.Count > 0 ? Cut(string.Join("; ", pass.Problems), 2000) : null;
            if (pass.Mirror.Count > 0)
            {
                var mirror = Cut(string.Join("\n", pass.Mirror), 2000);
                await db.KnowledgeSources.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.Mirror, mirror), ct);
            }
        }
        catch (Exception ex) when (ex is KnowledgeException or EmbedderException)
        {
            (state, error) = ("failed", ex.Message);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogFailed(logger, source.Name, ex);
            (state, error) = ("failed", Cut($"The sync stopped: {ex.Message}", 2000));
        }
        await db.KnowledgeSources.Where(s => s.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(s => s.State, state).SetProperty(s => s.Error, error).SetProperty(s => s.SyncedAt, clock.GetUtcNow()), CancellationToken.None);
    }

    private static string? Cut(string? text, int max) => text is null || text.Length <= max ? text : text[..max];

    [LoggerMessage(Level = LogLevel.Error, Message = "Knowledge source {Source} could not be synced")]
    private static partial void LogFailed(ILogger logger, string source, Exception ex);
}

/// <summary>
/// Keeps company knowledge current: each source synced when it is due (Knowledge:SyncEvery) or when an
/// admin asks (Sync now), one at a time; and GitLab projects' members read again every 15 minutes, so
/// someone who leaves a project stops reading its documents within that. Confluence's and SharePoint's
/// permissions are read again at each sync of their source.
/// </summary>
public sealed partial class KnowledgeSync(IServiceScopeFactory scopes, IOptionsMonitor<KnowledgeOptions> options, TimeProvider clock, ILogger<KnowledgeSync> logger)
    : BackgroundService
{
    public static readonly TimeSpan MembersEvery = TimeSpan.FromMinutes(15);

    private readonly Channel<Guid> _asked = Channel.CreateUnbounded<Guid>();

    /// <summary>Syncs the source as soon as the sync running now (if any) is done.</summary>
    public void Now(Guid id) => _asked.Writer.TryWrite(id);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // A sync the app stopped in the middle of is done again.
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().KnowledgeSources.Where(s => s.State == "syncing")
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.State, "failed").SetProperty(s => s.Error, "The app restarted during the sync; it is read again.")
                    .SetProperty(s => s.SyncedAt, (DateTimeOffset?)null), stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            LogFailed(logger, ex);
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Guid? asked = null;
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    wait.CancelAfter(TimeSpan.FromMinutes(1));
                    try
                    {
                        asked = await _asked.Reader.ReadAsync(wait.Token);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // A minute went by: what is due.
                    }
                }
                if (asked is { } id)
                {
                    await SyncAsync(id, stoppingToken);
                    continue;
                }
                await DueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the app keeps running: a background failure must never stop it.
                LogFailed(logger, ex);
            }
        }
    }

    private async Task SyncAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<KnowledgeSyncer>().SyncAsync(id, ct);
    }

    private async Task DueAsync(CancellationToken ct)
    {
        List<Guid> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var since = clock.GetUtcNow() - options.CurrentValue.SyncEvery;
            due = await db.KnowledgeSources.AsNoTracking().Where(s => s.State != "syncing" && (s.SyncedAt == null || s.SyncedAt < since))
                .OrderBy(s => s.SyncedAt).Select(s => s.Id).ToListAsync(ct);
        }
        foreach (var id in due)
        {
            // What an admin asked for goes first.
            while (_asked.Reader.TryRead(out var asked))
            {
                await SyncAsync(asked, ct);
            }
            await SyncAsync(id, ct);
        }
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var gitlab = scope.ServiceProvider.GetRequiredService<GitLabConnector>();
            var stale = clock.GetUtcNow() - MembersEvery;
            // Confluence's readers are read again at each sync of their source.
            foreach (var row in await db.KnowledgeReaders.Where(r => r.FetchedAt < stale && r.Key.StartsWith("gitlab:")).OrderBy(r => r.FetchedAt).Take(100).ToListAsync(ct))
            {
                // GitLab does not answer: the next minute tries again, with one request.
                if (!await gitlab.RefreshAsync(row, ct))
                {
                    break;
                }
                await db.SaveChangesAsync(ct);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Company knowledge's sync loop failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
