using System.Collections.Concurrent;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;

namespace Llm.Api.Gateway;

/// <summary>
/// Whose an API key is, for the answers' line (AnswerGate): the person, their groups' priority, and their requests at once
/// (theirs or their groups'; none set: their API requests are held only by their key's line and the models' own places).
/// The key's person from <see cref="KeyBook"/>; their priority and requests at once worked out once a minute at most per
/// person. A key the app does not know has no person (the gateway refuses it as it would).
/// </summary>
public sealed class KeyPeople(IServiceScopeFactory scopes, KeyBook book, TimeProvider clock)
{
    public sealed record Person(Guid Id, int Priority, int? AtOnce);

    private readonly ConcurrentDictionary<Guid, (Person Person, DateTimeOffset At)> _known = new();

    public async Task<Person?> ForAsync(string key, CancellationToken ct)
    {
        if (await book.ForAsync(key, ct) is not { } holder)
        {
            return null;
        }
        var now = clock.GetUtcNow();
        if (_known.TryGetValue(holder.UserId, out var known) && now - known.At < TimeSpan.FromMinutes(1))
        {
            return known.Person;
        }
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        if (await services.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(holder.UserId.ToString()) is not { } user)
        {
            return null;
        }
        var access = services.GetRequiredService<Access.AccessService>();
        var person = new Person(user.Id, await access.PriorityAsync(user, ct), (await access.AnswersAtOnceAsync(user, ct)).Value);
        _known[holder.UserId] = (person, now);
        return person;
    }
}
