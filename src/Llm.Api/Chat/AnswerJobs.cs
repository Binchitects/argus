using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>
/// Answers run here, not in the request that asked for them: closing the page,
/// reloading it or losing the connection does not stop one. Each answer keeps its
/// events so far, so a page that comes back (or a second tab) is shown the answer
/// from its start and then live. Stopping is its own request. One answer at a time
/// per chat; shutting down stops them all, and each keeps what it had.
/// </summary>
public sealed partial class AnswerJobs(IServiceScopeFactory scopes, AnswerGate gate, ILogger<AnswerJobs> logger) : IHostedService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();

    /// <summary>One answer being written: its events so far and the pages watching it.</summary>
    public sealed class Job
    {
        private readonly Lock _lock = new();
        private readonly List<JsonObject> _events = [];
        private readonly List<Channel<string>> _watchers = [];
        private bool _ended;
        private string? _outcome;
        private string? _error;

        internal Job(Guid conversation, Guid person) => (Conversation, Person) = (conversation, person);

        public Guid Conversation { get; }

        public Guid Person { get; }

        internal CancellationTokenSource Stopping { get; } = new();

        internal Task Running { get; set; } = Task.CompletedTask;

        /// <summary>"Answer now": the person asked it to stop thinking.</summary>
        public Hurry Hurry { get; } = new();

        /// <summary>How long it waited in line for a place (AnswerGate), for its trace.</summary>
        internal int QueuedMs { get; set; }

        /// <summary>Its end goes to the person's bell when no page watched it (off for a scheduled task's, which says so itself, and a compaction).</summary>
        public bool Notify { get; set; } = true;

        /// <summary>How it ended (done, error, stopped), with the error's words; and whether a page was watching then.</summary>
        internal (string? Outcome, string? Error, bool Watched) Ending()
        {
            lock (_lock)
            {
                return (_outcome, _error, _watchers.Count > 0);
            }
        }

        public Task EmitAsync(object e)
        {
            Emit(e);
            return Task.CompletedTask;
        }

        public void Emit(object e)
        {
            var node = JsonSerializer.SerializeToNode(e, Json)!.AsObject();
            var line = node.ToJsonString(Json);
            var type = node["type"]?.GetValue<string>();
            lock (_lock)
            {
                if (_ended)
                {
                    return;
                }
                if (type is "done" or "error" or "stopped")
                {
                    (_outcome, _error) = (type, type == "error" ? node["message"]?.GetValue<string>() : null);
                }
                // Text comes a token at a time; what is kept for a page that comes back joins it up
                // (a sub-agent's too, while it is the same one's same kind of text).
                if (_events.Count > 0 && Joins(_events[^1], node, type))
                {
                    _events[^1]["text"] = _events[^1]["text"]!.GetValue<string>() + node["text"]!.GetValue<string>();
                }
                else
                {
                    _events.Add(node);
                }
                foreach (var w in _watchers)
                {
                    w.Writer.TryWrite(line);
                }
            }
        }

        /// <summary>
        /// The events so far, then each as it comes, until the answer ends or
        /// <paramref name="ct"/> fires. An empty string now and then while nothing
        /// happens (a long tool call, a wait in line) keeps proxies from closing the stream.
        /// </summary>
        private static bool Joins(JsonObject last, JsonObject next, string? type) => type switch
        {
            "content" or "reasoning" => last["type"]?.GetValue<string>() == type,
            "agent" => next["event"]?.GetValue<string>() is "content" or "reasoning" && last["type"]?.GetValue<string>() == "agent"
                && last["event"]?.GetValue<string>() == next["event"]?.GetValue<string>()
                && last["id"]?.GetValue<string>() == next["id"]?.GetValue<string>() && last["index"]?.GetValue<int>() == next["index"]?.GetValue<int>(),
            _ => false,
        };

        public async IAsyncEnumerable<string> WatchAsync(TimeSpan heartbeat, [EnumeratorCancellation] CancellationToken ct)
        {
            var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            List<string> sofar;
            lock (_lock)
            {
                sofar = [.. _events.Select(e => e.ToJsonString(Json))];
                if (_ended)
                {
                    channel.Writer.TryComplete();
                }
                else
                {
                    _watchers.Add(channel);
                }
            }
            try
            {
                foreach (var line in sofar)
                {
                    yield return line;
                }
                while (true)
                {
                    bool? more;
                    using (var tick = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        tick.CancelAfter(heartbeat);
                        try
                        {
                            more = await channel.Reader.WaitToReadAsync(tick.Token);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            more = null;
                        }
                    }
                    if (more is null)
                    {
                        yield return "";
                        continue;
                    }
                    if (more == false)
                    {
                        yield break;
                    }
                    while (channel.Reader.TryRead(out var line))
                    {
                        yield return line;
                    }
                }
            }
            finally
            {
                lock (_lock)
                {
                    _watchers.Remove(channel);
                }
            }
        }

        internal void End()
        {
            lock (_lock)
            {
                _ended = true;
                foreach (var w in _watchers)
                {
                    w.Writer.TryComplete();
                }
                _watchers.Clear();
            }
        }
    }

    /// <summary>Takes the chat for a new answer; null when it is answering already.</summary>
    public Job? Reserve(Guid conversation, Guid person)
    {
        var job = new Job(conversation, person);
        return _jobs.TryAdd(conversation, job) ? job : null;
    }

    /// <summary>Gives the chat back when its answer never started (the question was refused).</summary>
    public void Release(Job job)
    {
        _jobs.TryRemove(new KeyValuePair<Guid, Job>(job.Conversation, job));
        job.End();
    }

    /// <summary>The answer this chat is writing, if any.</summary>
    public Job? Find(Guid conversation) => _jobs.GetValueOrDefault(conversation);

    public bool IsAnswering(Guid conversation) => _jobs.ContainsKey(conversation);

    /// <summary>Stops the chat's answer; it keeps what it has. False when it is not answering.</summary>
    public bool Stop(Guid conversation)
    {
        if (!_jobs.TryGetValue(conversation, out var job))
        {
            return false;
        }
        job.Stopping.Cancel();
        return true;
    }

    /// <summary>Answers <paramref name="questionId"/> in the background, with services of its own (the request that asked may end first).</summary>
    public void Start(Job job, Guid questionId, AnswerOverrides overrides) =>
        Start(job, async (services, user, conversation, ct) =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var question = await db.ChatMessages.SingleOrDefaultAsync(m => m.Id == questionId && m.ConversationId == conversation.Id, CancellationToken.None);
            if (question is null)
            {
                job.Emit(new { type = "error", message = "The question is gone." });
                return;
            }
            await services.GetRequiredService<ChatService>().AnswerAsync(user, conversation, question, overrides with { Hurry = job.Hurry, QueuedMs = job.QueuedMs }, job.EmitAsync, ct);
        });

    /// <summary>Compacts the branch down to <paramref name="leafId"/> in the background (the model writes a summary: it waits its turn as an answer does).</summary>
    public void StartCompaction(Job job, Guid leafId)
    {
        job.Notify = false;
        Start(job, (services, user, conversation, ct) =>
            services.GetRequiredService<ChatService>().CompactAsync(user, conversation, leafId, job.EmitAsync, ct));
    }

    private void Start(Job job, Func<IServiceProvider, AppUser, Conversation, CancellationToken, Task> work) =>
        job.Running = Task.Run(() => RunAsync(job, work));

    private async Task RunAsync(Job job, Func<IServiceProvider, AppUser, Conversation, CancellationToken, Task> work)
    {
        var ct = job.Stopping.Token;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<AppDbContext>();
            var user = await services.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(job.Person.ToString());
            var conversation = await db.Conversations.SingleOrDefaultAsync(c => c.Id == job.Conversation, CancellationToken.None);
            if (user is null || conversation is null)
            {
                job.Emit(new { type = "error", message = "This chat is gone." });
                return;
            }

            // Fair use: a place of the few the model serves at once, in turn (AnswerGate).
            AnswerGate.Place place;
            var waited = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                place = await gate.EnterAsync(job.Person, line => job.EmitAsync(new { type = "queued", ahead = line.Ahead }), ct);
                job.QueuedMs = (int)waited.ElapsedMilliseconds;
            }
            catch (TimeoutException ex)
            {
                job.Emit(new { type = "error", message = ex.Message });
                return;
            }
            using (place)
            {
                await work(services, user, conversation, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped before a word was written (in line, or reading the chat).
            job.Emit(new { type = "stopped", id = (Guid?)null });
        }
        catch (Exception ex)
        {
            LogFailed(logger, job.Conversation, ex);
            job.Emit(new { type = "error", message = "The answer failed on the server. Try again, and tell an admin if it keeps failing." });
        }
        finally
        {
            var (outcome, error, watched) = job.Ending();
            Release(job);
            if (job.Notify && !watched && outcome is "done" or "error")
            {
                await NotifyAsync(job, outcome == "error" ? error ?? "The answer failed." : null);
            }
        }
    }

    /// <summary>An answer that ended while no page watched it (the person left, or closed the tab): the bell says so.</summary>
    private async Task NotifyAsync(Job job, string? error)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var title = await db.Conversations.AsNoTracking().Where(c => c.Id == job.Conversation).Select(c => c.Title).SingleOrDefaultAsync();
            if (title is null)
            {
                return; // deleted meanwhile
            }
            var said = error ?? await db.ChatMessages.AsNoTracking()
                .Where(m => m.ConversationId == job.Conversation && m.Role == "assistant" && m.Content != "")
                .OrderByDescending(m => m.Sequence).Select(m => m.Content).FirstOrDefaultAsync();
            await scope.ServiceProvider.GetRequiredService<Notifications.Notifier>().SendAsync(job.Person,
                new Notifications.News("answer", error is null ? $"Answer ready: {title}" : $"Answer failed: {title}", said?.Trim(), $"/chat/{job.Conversation}"));
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            LogNotifyFailed(logger, job.Conversation, ex);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Shutting down: every answer stops and keeps what it has.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var running = _jobs.Values.ToList();
        foreach (var job in running)
        {
            await job.Stopping.CancelAsync();
        }
        try
        {
            await Task.WhenAll(running.Select(j => j.Running)).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The host would not wait longer.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Conversation {Conversation}: the answer failed")]
    private static partial void LogFailed(ILogger logger, Guid conversation, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation {Conversation}: the bell could not be told the answer ended")]
    private static partial void LogNotifyFailed(ILogger logger, Guid conversation, Exception ex);
}
