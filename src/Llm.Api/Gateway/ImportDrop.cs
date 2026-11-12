using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Llm.Api.Gateway;

/// <summary>
/// `dotnet Llm.Api.dll drop-litellm-database [--wait SECONDS]`, run in the app's container by the installer once an
/// upgrade from before 8.0 is healthy (its backup, taken before, holds the database): LiteLLM's database dropped, only
/// after <see cref="LedgerImport"/> has marked its keys and log brought over (<see cref="LedgerImport.DoneRow"/>), and
/// never the app's own. --wait waits that long for the mark (the leader imports at its first start). Written to the
/// audit log. Exit codes: 0 dropped or none there; 3 not imported yet (nothing done); 4 refused (the app's own
/// database named, or no right to drop it); 2 a wrong option.
/// </summary>
public static class ImportDrop
{
    public static async Task<int> RunAsync(IServiceProvider services, IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        var wait = TimeSpan.Zero;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--wait" && i + 1 < args.Count && int.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
            {
                wait = TimeSpan.FromSeconds(seconds);
                i++;
                continue;
            }
            await error.WriteLineAsync($"drop-litellm-database: no option {args[i]} (--wait SECONDS).");
            return 2;
        }
        var config = services.GetRequiredService<IConfiguration>();
        var builder = new NpgsqlConnectionStringBuilder(DatabaseSettings.ConnectionString(config));
        var own = builder.Database;
        var name = config["Gateway:ImportDatabase"] is { Length: > 0 } named ? named : LedgerImport.Database;
        if (string.Equals(name, own, StringComparison.Ordinal) || name is "postgres" or "template0" or "template1")
        {
            await error.WriteLineAsync($"drop-litellm-database: {name} is not LiteLLM's to drop (the app's own, or the server's).");
            return 4;
        }
        builder.Database = "postgres";
        builder.ApplicationName = "llm-app litellm drop";
        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        await using (var there = new NpgsqlCommand("select 1 from pg_database where datname = @name", conn))
        {
            there.Parameters.AddWithValue("name", name);
            if (await there.ExecuteScalarAsync(ct) is null)
            {
                await output.WriteLineAsync($"There is no database {name}: nothing to drop.");
                return 0;
            }
        }
        var until = DateTimeOffset.UtcNow + wait;
        while (!await ImportedAsync(services, ct))
        {
            if (DateTimeOffset.UtcNow >= until)
            {
                await error.WriteLineAsync($"drop-litellm-database: LiteLLM's keys and log are not brought over yet: {name} is kept (the app imports them as it starts; run this again later).");
                return 3;
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        try
        {
            // Nothing should hold it (LiteLLM is gone); a session left over would stop the drop.
            await using (var off = new NpgsqlCommand("select pg_terminate_backend(pid) from pg_stat_activity where datname = @name and pid <> pg_backend_pid()", conn))
            {
                off.Parameters.AddWithValue("name", name);
                await off.ExecuteNonQueryAsync(ct);
            }
            await using var drop = new NpgsqlCommand($"drop database if exists \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"", conn);
            await drop.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ObjectInUse)
        {
            await error.WriteLineAsync($"drop-litellm-database: {name} could not be dropped ({ex.MessageText}): its owner drops it, once its backup is kept.");
            return 4;
        }
        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<Identity.Audit>().WriteAsync("gateway.litellm_dropped", name,
                detail: "LiteLLM's database, its keys and log in the app's ledger since the upgrade");
        }
        await output.WriteLineAsync($"LiteLLM's database {name} is dropped: its keys and log are in the app's ledger.");
        return 0;
    }

    private static async Task<bool> ImportedAsync(IServiceProvider services, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Settings.AsNoTracking().AnyAsync(x => x.Key == LedgerImport.DoneRow, ct);
    }
}
