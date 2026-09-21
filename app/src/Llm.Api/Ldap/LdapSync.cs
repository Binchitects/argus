using Llm.Api.Identity;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Ldap;

/// <summary>
/// Keeps directory people current between sign-ins. Someone removed from the
/// directory, or from the sign-in group, is disabled: their sessions end and
/// their API keys are blocked. They come back if the directory lets them again.
/// </summary>
public sealed partial class LdapSync(IServiceScopeFactory scopes, IOptions<LdapOptions> options, ILogger<LdapSync> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }
        if (options.Value.IgnoreCertificateErrors)
        {
            LogInsecure(logger);
        }
        using var timer = new PeriodicTimer(options.Value.SyncInterval);
        do
        {
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
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
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
        var disabled = 0;
        foreach (var user in directoryPeople)
        {
            var person = await ldap.FindByDnAsync(user.LdapDn!, ct);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ldap:IgnoreCertificateErrors is on: the directory's certificate is NOT checked. Use it for testing only.")]
    private static partial void LogInsecure(ILogger logger);
}
