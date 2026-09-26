namespace Llm.Api.Settings;

/// <summary>What kind of value a setting holds, which decides its editor and its validation.</summary>
public enum SettingType
{
    Text,
    WholeNumber,
    Number,
    Boolean,
    /// <summary>A TimeSpan ("01:30:00"), edited in <see cref="SettingDefinition.Unit"/>.</summary>
    Duration,
    /// <summary>One of <see cref="SettingDefinition.Options"/>.</summary>
    Choice,
    /// <summary>Any of <see cref="SettingDefinition.Options"/>, comma-separated.</summary>
    Choices,
    Url,
    /// <summary>Write-only: never read back, not even masked.</summary>
    Secret,
}

/// <summary>How a saved change takes effect.</summary>
public enum SettingScope
{
    /// <summary>The app's own setting, read on every use: it applies when saved.</summary>
    Live,
    /// <summary>The app's own setting, read at start: the app restarts itself to apply it.</summary>
    AppRestart,
    /// <summary>A `.env` value other services read: saved as pending, applied on the host by scripts/apply-settings.sh.</summary>
    Stack,
}

/// <summary>
/// One setting the admin can change in the app.
/// <para><b>Live</b> and <b>AppRestart</b> settings are configuration keys of the app
/// (<c>Ldap:Url</c>); their value lives in the database and wins over the environment.</para>
/// <para><b>Stack</b> settings are `.env` names (<c>MODEL_CONTEXT</c>); compose passes the
/// current value to the app as <c>StackEnv:NAME</c> (a secret only as <c>NAME_SET</c>).</para>
/// </summary>
public sealed record SettingDefinition(
    string Key,
    string Group,
    string Label,
    string Help,
    SettingType Type,
    SettingScope Scope)
{
    /// <summary>The value when nothing sets it (for app settings: the code default).</summary>
    public string? Default { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    /// <summary>A regular expression the whole value must match.</summary>
    public string? Pattern { get; init; }
    public string? PatternHelp { get; init; }
    /// <summary>For Duration: minutes, hours or days.</summary>
    public string? Unit { get; init; }
    /// <summary>Empty is allowed (and means "off" or "the default").</summary>
    public bool Optional { get; init; } = true;
    /// <summary>What changing it does beyond the obvious (a model reload, signing people out...).</summary>
    public string? Impact { get; init; }
    /// <summary>Asks for confirmation in the UI: a wrong value can take the service down.</summary>
    public bool Dangerous { get; init; }

    public bool IsSecret => Type == SettingType.Secret;
}
