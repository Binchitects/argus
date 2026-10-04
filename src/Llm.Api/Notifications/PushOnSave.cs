using System.Runtime.CompilerServices;
using Llm.Core.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Llm.Api.Notifications;

/// <summary>
/// The bell's news goes to people's devices too: every notification saved, whoever saves
/// it (an answer that ended while away, a task's run, credit, alerts), is pushed once the
/// save succeeds (<see cref="WebPush"/>).
/// </summary>
public sealed class PushOnSave(WebPush push) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, List<Notification>> _pending = [];

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
        Push(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Push(eventData.Context);
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
        var added = context.ChangeTracker.Entries<Notification>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        if (added.Count > 0)
        {
            _pending.AddOrUpdate(context, added);
        }
    }

    private void Push(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var saved))
        {
            return;
        }
        _pending.Remove(context);
        foreach (var n in saved.Where(n => !n.Cleared))
        {
            push.Queue(n.UserId, new PushMessage(n.Title, n.Body, n.Link, n.Kind, n.Id.ToString()));
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
