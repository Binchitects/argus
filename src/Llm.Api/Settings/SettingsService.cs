using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Llm.Api.Identity;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Settings;

/// <summary>Configuration section "Settings": where pending stack changes are written for scripts/apply-settings.sh.</summary>
public sealed class SettingsFileOptions
{
    /// <summary>In a directory mounted from the host (deploy/config/app).</summary>
    public string PendingFile { get; set; } = "/settings/pending.env";
}

/// <summary>Restarts the app so settings read at start apply. The container's restart policy brings it back.</summary>
public interface IAppRestarter
{
    void Restart();
}

public sealed class AppRestarter(IHostApplicationLifetime lifetime) : IAppRestarter
{
    // A moment first, so the answer to the request that asked for it gets out.
    public void Restart() => _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromMilliseconds(750));
        lifetime.StopApplication();
    });
}

/// <summary>The values of the restart-bound settings when this process started, to tell whether a restart is due.</summary>
public sealed class SettingsAtStart(IConfiguration config)
{
    private readonly Dictionary<string, string?> _values = SettingsCatalog.All
        .Where(d => d.Scope == SettingScope.AppRestart)
        .ToDictionary(d => d.Key, d => config[d.Key]);

    public bool Changed(string key, string? now) => _values.TryGetValue(key, out var then) && !string.Equals(then ?? "", now ?? "", StringComparison.Ordinal);
}

public sealed record SettingChange(string Key, string? Value, bool Reset = false);

public sealed class SettingsValidationException(IReadOnlyDictionary<string, string> errors) : Exception("Some settings are not valid.")
{
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}

public sealed class SettingsUnavailableException(string message) : Exception(message);

public sealed partial class SettingsService(
    AppDbContext db,
    IConfiguration config,
    DatabaseConfigurationProvider provider,
    IOptions<SettingsFileOptions> files,
    IOptions<AuthOptions> auth,
    SettingsAtStart atStart,
    Audit audit)
{
    private const string Row = DatabaseConfigurationProvider.RowPrefix;

    public async Task<object> ViewAsync(CancellationToken ct = default)
    {
        var saved = (await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith(Row)).Select(s => s.Key).ToListAsync(ct))
            .Select(k => k[Row.Length..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = ReadPending(prune: true);
        var rows = SettingsCatalog.All.Select(d => View(d, saved, pending)).ToList();
        return new
        {
            groups = rows.GroupBy(r => r.Group).Select(g => new { title = g.Key, settings = g.ToList() }),
            pendingStack = pending.Count,
            restartNeeded = rows.Any(r => r.RestartPending),
            pendingFileWritable = PendingDirectoryWritable(),
        };
    }

    public sealed record SettingView(
        string Key, string Group, string Label, string Help, string Type, string Scope,
        IReadOnlyList<string>? Options, decimal? Min, decimal? Max, string? PatternHelp, string? Unit, bool Optional,
        string? Impact, bool Dangerous, string? Default,
        string? Value, bool IsSet, string Source, string? EnvironmentValue,
        string? Pending, bool PendingSet, bool RestartPending);

    private SettingView View(SettingDefinition d, HashSet<string> saved, Dictionary<string, string> pending)
    {
        string? value;
        bool isSet;
        string source;
        string? environment = null;
        string? pendingValue = null;
        var pendingSet = false;
        var restart = false;
        if (d.Scope == SettingScope.Stack)
        {
            value = d.IsSecret ? null : config[$"StackEnv:{d.Key}"];
            isSet = d.IsSecret ? config[$"StackEnv:{d.Key}_SET"] == "yes" : !string.IsNullOrEmpty(value);
            source = "stack";
            if (pending.TryGetValue(d.Key, out var p))
            {
                pendingSet = true;
                pendingValue = d.IsSecret ? null : p;
            }
        }
        else
        {
            var effective = config[d.Key];
            environment = EnvironmentValue(d.Key);
            source = saved.Contains(d.Key) ? "saved" : environment is not null ? "environment" : "default";
            isSet = !string.IsNullOrEmpty(effective) || (source == "default" && !string.IsNullOrEmpty(d.Default));
            value = d.IsSecret ? null : effective ?? d.Default;
            if (d.IsSecret)
            {
                environment = null;
            }
            restart = d.Scope == SettingScope.AppRestart && atStart.Changed(d.Key, effective);
        }
        return new(d.Key, d.Group, d.Label, d.Help, d.Type.ToString().ToLowerInvariant(), d.Scope.ToString().ToLowerInvariant(),
            d.Options, d.Min, d.Max, d.PatternHelp, d.Unit, d.Optional, d.Impact, d.Dangerous, d.IsSecret ? null : d.Default,
            value, isSet, source, environment, pendingValue, pendingSet, restart);
    }

    /// <summary>What the configuration says without the Settings page (environment, appsettings).</summary>
    private string? EnvironmentValue(string key)
    {
        if (config is not IConfigurationRoot root)
        {
            return null;
        }
        foreach (var p in root.Providers.Reverse())
        {
            if (!ReferenceEquals(p, provider) && p.TryGet(key, out var v))
            {
                return v;
            }
        }
        return null;
    }

    /// <summary>Validates every change first; saves them all or none.</summary>
    public async Task SaveAsync(IReadOnlyList<SettingChange> changes, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var normalised = new List<(SettingDefinition Def, string? Value, bool Reset)>();
        foreach (var c in changes)
        {
            if (!SettingsCatalog.ByKey.TryGetValue(c.Key, out var def))
            {
                errors[c.Key] = "There is no such setting.";
                continue;
            }
            if (c.Reset)
            {
                normalised.Add((def, null, true));
                continue;
            }
            if (Normalise(def, c.Value, out var value) is { } error)
            {
                errors[c.Key] = error;
                continue;
            }
            normalised.Add((def, value, false));
        }
        if (normalised.Any(n => n.Def.IsSecret && n.Def.Scope != SettingScope.Stack && !n.Reset) && string.IsNullOrEmpty(auth.Value.DataKey))
        {
            errors["_"] = "APP_DATA_KEY is not set, so secrets cannot be stored.";
        }
        if (errors.Count > 0)
        {
            throw new SettingsValidationException(errors);
        }

        var stack = normalised.Where(n => n.Def.Scope == SettingScope.Stack).ToList();
        if (stack.Count > 0)
        {
            var pending = ReadPending(prune: false);
            foreach (var (def, value, reset) in stack)
            {
                if (reset)
                {
                    pending.Remove(def.Key);
                }
                else
                {
                    pending[def.Key] = value!;
                }
            }
            WritePending(pending);
        }

        foreach (var (def, value, reset) in normalised.Where(n => n.Def.Scope != SettingScope.Stack))
        {
            var key = Row + def.Key;
            var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, ct);
            if (reset)
            {
                if (row is not null)
                {
                    db.Settings.Remove(row);
                }
                continue;
            }
            var stored = def.IsSecret && !string.IsNullOrEmpty(value) ? SettingsCrypto.Encrypt(value, auth.Value.DataKey!) : value!;
            if (row is null)
            {
                db.Settings.Add(new Setting { Key = key, Value = stored });
            }
            else
            {
                row.Value = stored;
                row.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        await db.SaveChangesAsync(ct);
        provider.Reload();

        foreach (var (def, value, reset) in normalised)
        {
            var what = reset
                ? def.Scope == SettingScope.Stack ? "pending change discarded" : "back to the environment or default"
                : def.IsSecret ? "a new secret value" : $"set to \"{Shorten(value)}\"";
            var where = def.Scope == SettingScope.Stack && !reset ? " (pending: apply-settings.sh)" : "";
            await audit.WriteAsync("settings.change", def.Key, detail: what + where);
        }
    }

    private static string Shorten(string? v) => v is null ? "" : v.Length > 120 ? v[..117] + "..." : v;

    /// <summary>The value in its stored form, or why it is not acceptable.</summary>
    public static string? Normalise(SettingDefinition d, string? raw, out string? value)
    {
        value = (raw ?? "").Trim();
        if (value.Length == 0)
        {
            return d.Optional ? null : "Required.";
        }
        if (d.Scope == SettingScope.Stack && value.IndexOfAny(['\r', '\n', '"', '\'', '`', '$', '\\', '#']) >= 0)
        {
            return "Quotes, $, #, backslashes and line breaks are not allowed: .env would read them differently.";
        }
        if (value.Length > 4096)
        {
            return "Too long.";
        }
        var inv = CultureInfo.InvariantCulture;
        switch (d.Type)
        {
            case SettingType.WholeNumber:
                if (!long.TryParse(value, NumberStyles.Integer, inv, out var i))
                {
                    return "A whole number.";
                }
                if (Range(d, i) is { } ri)
                {
                    return ri;
                }
                value = i.ToString(inv);
                break;
            case SettingType.Number:
                if (!decimal.TryParse(value, NumberStyles.Number, inv, out var m))
                {
                    return "A number, like 0.25.";
                }
                if (Range(d, m) is { } rm)
                {
                    return rm;
                }
                value = m.ToString(inv);
                break;
            case SettingType.Boolean:
                value = value.ToLowerInvariant() switch
                {
                    "true" or "on" or "yes" or "1" => "true",
                    "false" or "off" or "no" or "0" => "false",
                    _ => null,
                };
                if (value is null)
                {
                    return "On or off.";
                }
                break;
            case SettingType.Duration:
                if (!TimeSpan.TryParse(value, inv, out var t) || t <= TimeSpan.Zero)
                {
                    return "A duration.";
                }
                var amount = (decimal)(d.Unit switch { "hours" => t.TotalHours, "days" => t.TotalDays, _ => t.TotalMinutes });
                if (Range(d, amount) is { } rt)
                {
                    return rt;
                }
                value = t.ToString("c", inv);
                break;
            case SettingType.Choice:
                if (!d.Options!.Contains(value, StringComparer.Ordinal))
                {
                    return $"One of: {string.Join(", ", d.Options!)}.";
                }
                break;
            case SettingType.Choices:
                var chosen = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
                if (chosen.FirstOrDefault(c => !d.Options!.Contains(c, StringComparer.Ordinal)) is { } unknown)
                {
                    return $"\"{unknown}\" is not one of: {string.Join(", ", d.Options!)}.";
                }
                value = string.Join(',', d.Options!.Where(chosen.Contains));
                if (value.Length == 0 && !d.Optional)
                {
                    return "Choose at least one.";
                }
                break;
            case SettingType.Text or SettingType.Url:
                if (d.Max is { } maxLength && value.Length > maxLength)
                {
                    return $"At most {maxLength} characters.";
                }
                break;
            case SettingType.Secret:
                break;
        }
        if (d.Pattern is { } pattern && !Regex.IsMatch(value, $"^(?:{pattern})$", RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            return d.PatternHelp is { } help ? $"Expected {help}." : "Not in the expected form.";
        }
        return null;
    }

    private static string? Range(SettingDefinition d, decimal v)
    {
        var unit = d.Unit is { } u && u != "bytes" ? " " + u : "";
        return d.Min is { } min && v < min ? $"At least {min.ToString(CultureInfo.InvariantCulture)}{unit}."
            : d.Max is { } max && v > max ? $"At most {max.ToString(CultureInfo.InvariantCulture)}{unit}."
            : null;
    }

    // ------------------------------------------------------------ pending (.env) --

    /// <summary>
    /// Stack changes waiting for scripts/apply-settings.sh. With <paramref name="prune"/>,
    /// changes that are already in effect (applied by hand, or by the script) are dropped.
    /// </summary>
    public Dictionary<string, string> ReadPending(bool prune)
    {
        var path = files.Value.PendingFile;
        var pending = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return pending;
        }
        foreach (var line in File.ReadAllLines(path))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (line.StartsWith('#') || eq <= 0)
            {
                continue;
            }
            var key = line[..eq].Trim();
            if (SettingsCatalog.ByKey.TryGetValue(key, out var d) && d.Scope == SettingScope.Stack)
            {
                pending[key] = line[(eq + 1)..];
            }
        }
        if (prune)
        {
            var applied = pending.Where(p => !SettingsCatalog.ByKey[p.Key].IsSecret && string.Equals(config[$"StackEnv:{p.Key}"] ?? "", p.Value, StringComparison.Ordinal))
                .Select(p => p.Key).ToList();
            if (applied.Count > 0 && PendingDirectoryWritable())
            {
                applied.ForEach(k => pending.Remove(k));
                WritePending(pending);
            }
        }
        return pending;
    }

    private bool PendingDirectoryWritable()
    {
        var dir = Path.GetDirectoryName(files.Value.PendingFile);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return false;
        }
        try
        {
            var probe = Path.Combine(dir, $".probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void WritePending(Dictionary<string, string> pending)
    {
        var path = files.Value.PendingFile;
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir))
        {
            throw new SettingsUnavailableException($"The app cannot save stack settings: {dir} is not mounted. Update the stack (docker compose up -d) so the app has its settings directory.");
        }
        if (pending.Count == 0)
        {
            File.Delete(path);
            return;
        }
        var sb = new StringBuilder()
            .AppendLine("# Settings saved in the app, waiting to be applied. Apply them on the host with")
            .AppendLine("#   ./scripts/apply-settings.sh")
            .AppendLine("# which shows each change, asks, writes .env and recreates what changed.");
        foreach (var d in SettingsCatalog.StackSettings)
        {
            if (pending.TryGetValue(d.Key, out var v))
            {
                sb.Append(d.Key).Append('=').AppendLine(v);
            }
        }
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, sb.ToString());
            if (!OperatingSystem.IsWindows())
            {
                // It can hold a secret (a GitLab token): the owner only, like .env.
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingsUnavailableException($"The app cannot write {path}: {ex.Message}");
        }
    }
}
