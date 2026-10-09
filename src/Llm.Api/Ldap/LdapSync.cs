using Llm.Api.Identity;
using Llm.Api.Operations;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Ldap;

/// <summary>
/// Keeps directory people current between sign-ins. Someone removed from the
/// directory, or from the sign-in group, is disabled: their sessions end and
/// their API keys are blocked. They come back if the directory lets them again.
/// With several replicas, the one that leads checks.
/// </summary>
public sealed partial class LdapSync(IServiceScopeFactory scopes, IOptionsMonitor<LdapOptions> options, Replicas replicas, ILogger<LdapSync> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The settings can change at any time (the Settings page): they are read
        // again every round, and a change wakes the loop so it applies at once.
        var warnedInsecure = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var o = options.CurrentValue;
            var lead = replicas.IsLeader;
            if (o.Enabled && lead)
            {
                if (o.IgnoreCertificateErrors && !warnedInsecure)
                {
                    LogInsecure(logger);
                }
                warnedInsecure = o.IgnoreCertificateErrors;
                try
                {
                    var (checkedCount, disabled) = await RunOnceAsync(stoppingToken);
                    LogSynced(logger, checkedCount, disabled);
                }
                catch (LdapUnavailableException ex)
                {
                    // Never disable anyone because the directory is down.
                    LogUnavailable(logger, ex.Message);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Anything else (the database, say): the next round tries again. An exception
                    // left to escape here would stop the whole app, not just the check.
                    LogFailed(logger, ex);
                }
            }
            using var changed = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            using var subscription = options.OnChange((_, _) => changed.Cancel());
            try
            {
                var wait = !o.Enabled ? Timeout.InfiniteTimeSpan : !lead || o.SyncInterval < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : o.SyncInterval;
                await Task.Delay(wait, changed.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // The settings changed: go round again with the new ones.
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task<(int Checked, int Disabled)> RunOnceAsync(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var users = sp.GetRequiredService<UserManager<AppUser>>();
        var ldap = sp.GetRequiredService<ILdapDirectory>();
        var signIn = sp.GetRequiredService<SignInService>();
        var people = sp.GetRequiredService<PeopleService>();

        var directoryPeople = await users.Users.Where(u => u.Source == UserSource.Ldap && u.LdapDn != null).ToListAsync(ct);
        var found = new List<(AppUser User, LdapPerson? Person)>(directoryPeople.Count);
        foreach (var user in directoryPeople)
        {
            found.Add((user, await ldap.FindByDnAsync(user.LdapDn!, ct)));
        }
        // Someone to disable for not being in the required group, and nobody in it at all: a typo in its
        // name reads just the same. Unless the group is found, nobody is changed and the check says why.
        if (found.Any(f => f.Person is { } p && !ldap.IsAllowed(p) && !f.User.IsDisabled) && !found.Any(f => f.Person is { } p && ldap.IsAllowed(p)))
        {
            await ldap.CheckRequiredGroupAsync(ct);
        }
        var disabled = 0;
        foreach (var (user, person) in found)
        {
            if (person is not null && ldap.IsAllowed(person))
            {
                await signIn.SyncFromDirectoryAsync(person);
                continue;
            }
            if (!user.IsDisabled)
            {
                await people.SetDisabledAsync(PeopleService.DirectorySync, user, disabled: true, reason: "ldap");
                disabled++;
            }
        }
        return (directoryPeople.Count, disabled);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Directory sync: checked {Checked} people, disabled {Disabled}")]
    private static partial void LogSynced(ILogger logger, int @checked, int disabled);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Directory sync skipped, nobody changed: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Directory sync failed; it runs again at the next check")]
    private static partial void LogFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ldap:IgnoreCertificateErrors is on: the directory's certificate is NOT checked. Use it for testing only.")]
    private static partial void LogInsecure(ILogger logger);
}
