using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// The person's own settings.json and keybindings.json, as VS Code keeps them: in the config folder, for every project.
/// Read and saved by the page with the version it read (a stale one is refused, as files are); never logged. VS Code's
/// own copies on this computer can be read, to bring them over.
/// </summary>
internal sealed class UserFiles(string configDir)
{
    /// <summary>The most either file holds.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The only names served.</summary>
    public static readonly IReadOnlyList<string> Names = ["settings.json", "keybindings.json"];

    private readonly object _gate = new();

    /// <summary>The file's text and version; empty text and version "none" when it is not there yet.</summary>
    public JsonObject Read(string? name)
    {
        var full = Full(name);
        lock (_gate)
        {
            var text = File.Exists(full) ? File.ReadAllText(full) : "";
            return new JsonObject { ["name"] = name, ["text"] = text, ["version"] = IdeFiles.Version(full), ["exists"] = File.Exists(full) };
        }
    }

    /// <summary>Saves the file, unless it changed since the page read it at <paramref name="version"/> (null: overwrite).</summary>
    public JsonObject Write(string? name, string text, string? version)
    {
        var full = Full(name);
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
        {
            throw new IdeError(413, "too_large", $"{name} is kept up to {MaxBytes / 1024} KB.");
        }
        lock (_gate)
        {
            if (version is not null && IdeFiles.Version(full) != version)
            {
                throw new IdeError(409, "changed", $"{name} changed since it was opened. Reload it, or save again to overwrite it.");
            }
            Directory.CreateDirectory(configDir);
            // Whole or not at all, readable by the person alone: it may hold paths and names they keep to themselves.
            var temp = $"{full}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, text);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(temp, full, overwrite: true);
            return new JsonObject { ["name"] = name, ["version"] = IdeFiles.Version(full) };
        }
    }

    /// <summary>VS Code's own copy of the file on this computer (Code, then Insiders, then VSCodium), to bring over.</summary>
    public JsonObject FromVsCode(string? name, Func<string, string?> env)
    {
        Full(name);
        foreach (var dir in VsCodeDirs(env))
        {
            var file = Path.Combine(dir, name!);
            if (File.Exists(file) && new FileInfo(file).Length <= MaxBytes)
            {
                return new JsonObject { ["name"] = name, ["text"] = File.ReadAllText(file), ["from"] = file };
            }
        }
        throw new IdeError(404, "not_found", $"There is no {name} of VS Code on this computer.");
    }

    /// <summary>Where VS Code, Insiders and VSCodium keep the person's files, on this system.</summary>
    public static IEnumerable<string> VsCodeDirs(Func<string, string?> env)
    {
        string[] apps = ["Code", "Code - Insiders", "VSCodium"];
        var home = env("HOME") ?? env("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? root;
        if (OperatingSystem.IsWindows())
        {
            root = env("APPDATA") ?? Path.Combine(home, "AppData", "Roaming");
        }
        else if (OperatingSystem.IsMacOS())
        {
            root = Path.Combine(home, "Library", "Application Support");
        }
        else
        {
            root = env("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".config");
        }
        return apps.Select(app => Path.Combine(root, app, "User"));
    }

    private string Full(string? name) =>
        name is not null && Names.Contains(name) ? Path.Combine(configDir, name) : throw new IdeError(400, "invalid", "Ask for settings.json or keybindings.json.");
}
