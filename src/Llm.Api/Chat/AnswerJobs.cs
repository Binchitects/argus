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

        internal Job(Guid conversation, Guid person) => (Conversation, Person) = (conversation, person);

        public Guid Conversation { get; }

        public Guid Person { get; }

        internal CancellationTokenSource Stopping { get; } = new();

        internal Task Running { get; set; } = Task.CompletedTask;

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
                // Text comes a token at a time; what is kept for a page that comes back joins it up.
                if (type is "content" or "reasoning" && _events.Count > 0 && _events[^1]["type"]?.GetValue<string>() == type)
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
            await services.GetRequiredService<ChatService>().AnswerAsync(user, conversation, question, overrides, job.EmitAsync, ct);
        });

    /// <summary>Compacts the branch down to <paramref name="leafId"/> in the background (the model writes a summary: it waits its turn as an answer does).</summary>
    public void StartCompaction(Job job, Guid leafId) =>
        Start(job, (services, user, conversation, ct) =>
            services.GetRequiredService<ChatService>().CompactAsync(user, conversation, leafId, job.EmitAsync, ct));

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
            try
            {
                place = await gate.EnterAsync(job.Person, line => job.EmitAsync(new { type = "queued", ahead = line.Ahead }), ct);
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
            Release(job);
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
}
