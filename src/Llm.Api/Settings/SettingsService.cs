using System.Globalization;
using System.Text.RegularExpressions;
using Llm.Api.Identity;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Settings;

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

public sealed partial class SettingsService(
    AppDbContext db,
    IConfiguration config,
    DatabaseConfigurationProvider provider,
    IOptions<AuthOptions> auth,
    SettingsAtStart atStart,
    IEnumerable<ISettingWarning> warnings,
    Audit audit,
    Operations.Replicas replicas)
{
    /// <summary>The other replicas read the settings again when one saves them.</summary>
    public const string ReloadTopic = "settings:reload";

    private const string Row = DatabaseConfigurationProvider.RowPrefix;

    public async Task<object> ViewAsync(CancellationToken ct = default)
    {
        var saved = (await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith(Row)).Select(s => s.Key).ToListAsync(ct))
            .Select(k => k[Row.Length..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var said = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in warnings)
        {
            if (await w.WarningAsync(ct) is { } warning)
            {
                said[w.Key] = warning;
            }
        }
        var rows = SettingsCatalog.All.Select(d => View(d, saved, said.GetValueOrDefault(d.Key))).ToList();
        return new
        {
            groups = rows.GroupBy(r => r.Group).Select(g => new { title = g.Key, settings = g.ToList() }),
            restartNeeded = rows.Any(r => r.RestartPending),
        };
    }

    public sealed record SettingView(
        string Key, string Group, string Label, string Help, string Type, string Scope,
        IReadOnlyList<string>? Options, decimal? Min, decimal? Max, string? PatternHelp, string? Unit, bool Optional,
        string? Impact, bool Dangerous, string? Default,
        string? Value, bool IsSet, string Source, string? EnvironmentValue, bool RestartPending, string? Warning);

    private SettingView View(SettingDefinition d, HashSet<string> saved, string? warning)
    {
        var effective = config[d.Key];
        var environment = d.IsSecret ? null : EnvironmentValue(d.Key);
        var source = saved.Contains(d.Key) ? "saved" : EnvironmentValue(d.Key) is not null ? "environment" : "default";
        var isSet = !string.IsNullOrEmpty(effective) || (source == "default" && !string.IsNullOrEmpty(d.Default));
        var value = d.IsSecret ? null : effective ?? d.Default;
        var restart = d.Scope == SettingScope.AppRestart && atStart.Changed(d.Key, effective);
        return new(d.Key, d.Group, d.Label, d.Help, d.Type.ToString().ToLowerInvariant(), d.Scope.ToString().ToLowerInvariant(),
            d.Options, d.Min, d.Max, d.PatternHelp, d.Unit, d.Optional, d.Impact, d.Dangerous, d.IsSecret ? null : d.Default,
            value, isSet, source, environment, restart, warning);
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
        // The alerts webhook goes only to a host an admin allowed, as a task's does (the hosts may change in the same save).
        if (normalised.FirstOrDefault(n => n.Def.Key == "Notifications:AlertsWebhook") is { Def: { } hook, Value: { Length: > 0 } url })
        {
            var hosts = normalised.FirstOrDefault(n => n.Def.Key == "Schedules:WebhookHosts") is { Def: { } h } change
                ? change.Reset ? EnvironmentValue(h.Key) ?? h.Default : change.Value
                : config["Schedules:WebhookHosts"] ?? SettingsCatalog.ByKey["Schedules:WebhookHosts"].Default;
            if (Schedules.Webhooks.Refusal(url, hosts) is { } refusal)
            {
                errors[hook.Key] = refusal;
            }
        }
        if (normalised.Any(n => n.Def.IsSecret && !n.Reset) && string.IsNullOrEmpty(auth.Value.DataKey))
        {
            errors["_"] = "APP_KEY is not set, so secrets cannot be stored.";
        }
        if (errors.Count > 0)
        {
            throw new SettingsValidationException(errors);
        }

        foreach (var (def, value, reset) in normalised)
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
        replicas.Tell(ReloadTopic);

        foreach (var (def, value, reset) in normalised)
        {
            var what = reset ? "back to the environment or default" : def.IsSecret ? "a new secret value" : $"set to \"{Shorten(value)}\"";
            await audit.WriteAsync("settings.change", def.Key, detail: what);
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
}
