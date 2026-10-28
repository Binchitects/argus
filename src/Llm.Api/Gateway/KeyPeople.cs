using System.Collections.Concurrent;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;

namespace Llm.Api.Gateway;

/// <summary>
/// Whose an API key is, for the answers' line (AnswerGate): the person, their groups' priority, and their requests at once
/// (theirs or their groups'; none set: their API requests are held only by their key's line and the models' own places).
/// Asked of the gateway and the database once a minute at most per key; a key the gateway does not know has no person
/// (the gateway refuses it as it would).
/// </summary>
public sealed class KeyPeople(IServiceScopeFactory scopes, TimeProvider clock)
{
    public sealed record Person(Guid Id, int Priority, int? AtOnce);

    private readonly ConcurrentDictionary<string, (Person? Person, DateTimeOffset At)> _known = new(StringComparer.Ordinal);

    public async Task<Person?> ForAsync(string key, CancellationToken ct)
    {
        var token = AnswerCache.HashOf(key);
        var now = clock.GetUtcNow();
        if (_known.TryGetValue(token, out var known) && now - known.At < TimeSpan.FromMinutes(1))
        {
            return known.Person;
        }
        Person? person = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            if (await services.GetRequiredService<ILiteLlm>().KeyInfoAsync(key, ct) is { UserId: { Length: > 0 } email }
                && await services.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(email) is { } user)
            {
                var access = services.GetRequiredService<Access.AccessService>();
                person = new Person(user.Id, await access.PriorityAsync(user, ct), (await access.AnswersAtOnceAsync(user, ct)).Value);
            }
        }
        catch (GatewayException)
        {
            // The gateway cannot say now: the request goes as it is, and the gateway decides.
            return null;
        }
        _known[token] = (person, now);
        return person;
    }
}
