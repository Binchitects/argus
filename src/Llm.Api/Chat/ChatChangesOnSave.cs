using System.Runtime.CompilerServices;
using Llm.Core.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Llm.Api.Chat;

/// <summary>
/// Whatever saves a chat or a message of it (the web, an answer, Code Arena's sync, the queue), the chat's owner's watchers
/// hear of it once it is saved (<see cref="ChatWatch"/>): their pages and Code Arenas read it again at once.
/// </summary>
public sealed class ChatChangesOnSave(ChatWatch watch) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, List<(Guid Owner, Guid Chat, string What)>> _pending = [];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Tell(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Tell(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Forget(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(eventData.Context);
        return Task.CompletedTask;
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }
        var changes = new List<(Guid, Guid, string)>();
        foreach (var e in context.ChangeTracker.Entries<Conversation>().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            var c = e.Entity;
            changes.Add((c.UserId, c.Id, e.State == EntityState.Deleted || c.DeletedAt is not null ? "deleted" : e.State == EntityState.Added ? "added" : "chat"));
        }
        var messages = context.ChangeTracker.Entries<ChatMessage>().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => e.Entity.ConversationId).Distinct().ToList();
        if (messages.Count > 0)
        {
            // Each chat's owner: the chat held already, else read (a message saved on its own, as an answer's are).
            var held = context.ChangeTracker.Entries<Conversation>().ToDictionary(e => e.Entity.Id, e => e.Entity.UserId);
            var missing = messages.Where(m => !held.ContainsKey(m)).ToList();
            if (missing.Count > 0)
            {
                foreach (var c in context.Set<Conversation>().AsNoTracking().Where(c => missing.Contains(c.Id)).Select(c => new { c.Id, c.UserId }).ToList())
                {
                    held[c.Id] = c.UserId;
                }
            }
            changes.AddRange(messages.Where(held.ContainsKey).Select(m => (held[m], m, "messages")));
        }
        if (changes.Count > 0)
        {
            _pending.AddOrUpdate(context, changes);
        }
    }

    private void Tell(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var saved))
        {
            return;
        }
        _pending.Remove(context);
        foreach (var (owner, chat, what) in saved.Distinct())
        {
            watch.Changed(owner, chat, what);
        }
    }

    private void Forget(DbContext? context)
    {
        if (context is not null)
        {
            _pending.Remove(context);
        }
    }
}
