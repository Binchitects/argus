using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// Code Arena's options the environment can give (CODE_ARENA_*), each a setting too: in config.json under its key, and in
/// the IDE's Settings (User, "Code Arena on this computer"). A variable set in the environment still wins, as ARENA_URL
/// wins over "url". Read when used, so a change in the Settings applies at the next use. CODE_ARENA_HOME is read before
/// there is any config folder: its "home" is kept in config.json at the usual place, which then only points there.
/// </summary>
internal static class EnvSettings
{
    /// <param name="Variable">The environment's name for it.</param>
    /// <param name="Key">Its key in config.json.</param>
    /// <param name="Setting">Its id in the IDE's Settings.</param>
    /// <param name="Kind">file (a program's path, which must be there), folder (an absolute path, made when needed) or name (a word: letters, digits, . _ -).</param>
    public sealed record Option(string Variable, string Key, string Setting, string Kind, string What);

    public const string HomeKey = "home";

    public static readonly IReadOnlyList<Option> All =
    [
        new("CODE_ARENA_BROWSER", "browser", "codeArena.browser", "file",
            "The browser the IDE's own window and the page screenshots use: Chrome, Edge, Chromium or Brave, its program's path. Empty: the first of them installed."),
        new("CODE_ARENA_VBOXMANAGE", "vboxManage", "codeArena.vm.vboxManage", "file",
            "VirtualBox's VBoxManage, for the VM tool, when it is not on PATH or in VirtualBox's usual folder."),
        new("CODE_ARENA_VMRUN", "vmrun", "codeArena.vm.vmrun", "file",
            "VMware's vmrun (Workstation, Player or Fusion), for the VM tool, when it is not on PATH or in VMware's usual folder."),
        new("CODE_ARENA_VIRSH", "virsh", "codeArena.vm.virsh", "file",
            "libvirt's virsh, for the VM tool, when it is not on PATH."),
        new("CODE_ARENA_LIBVIRT_POOL", "libvirtPool", "codeArena.vm.libvirtPool", "name",
            "The libvirt storage pool the VM tool makes new VMs' disks in. Empty: default."),
        new("CODE_ARENA_HOME", HomeKey, "codeArena.home", "folder",
            "One folder for all of Code Arena's settings and sessions (config and data below it) instead of the system's usual places: a portable copy, or a disk of its own. From the next start; what is in the usual folders stays there."),
    ];

    /// <summary>The CODE_ARENA_* names the code reads that are no setting, and why.</summary>
    public static readonly IReadOnlyDictionary<string, string> NotSettings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CODE_ARENA_TOKEN"] = "The IDE's key for --serve, handed over by Arena's Code Studio for one run and taken out of the environment at once; a secret, never kept in a file",
        [DesktopStart.Detached] = "The mark of the IDE started again without a console window (DesktopStart, code-arena-ide.exe); no choice of the person's",
    };

    public static Option? Find(string variable) => All.FirstOrDefault(o => o.Variable == variable);

    /// <summary>The environment with config.json's values beneath it: a variable set wins; else the file's, read now.</summary>
    public static Func<string, string?> Over(Func<string, string?> env, AppPaths paths) => name =>
    {
        if (env(name) is { Length: > 0 } set)
        {
            return set;
        }
        return Find(name) is { } option && option.Key != HomeKey ? Saved(paths.ConfigFile, option.Key) : env(name);
    };

    /// <summary>The file config.json an option is kept in: the usual one for the home, which points to where the rest is.</summary>
    public static string FileOf(Option option, AppPaths paths) => option.Key == HomeKey ? paths.UsualConfigFile ?? paths.ConfigFile : paths.ConfigFile;

    /// <summary>One key of a config.json as a text, read without the rest of it (null when the file or the key is not there, or it is not JSON).</summary>
    public static string? Saved(string file, string key)
    {
        try
        {
            return File.Exists(file) && Json.ParseObject(File.ReadAllText(file)) is { } raw && raw.Str(key) is { Length: > 0 } value ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Each, with what config.json keeps and what the environment sets (which wins), for the IDE's Settings.</summary>
    public static JsonObject View(Func<string, string?> env, AppPaths paths) => new()
    {
        ["options"] = new JsonArray([.. All.Select(o => (JsonNode)new JsonObject
        {
            ["variable"] = o.Variable,
            ["key"] = o.Key,
            ["setting"] = o.Setting,
            ["kind"] = o.Kind,
            ["description"] = o.What,
            ["value"] = Saved(FileOf(o, paths), o.Key),
            ["environment"] = env(o.Variable) is { Length: > 0 } set ? set : null,
            ["file"] = FileOf(o, paths),
        })]),
    };

    /// <summary>What is wrong with a value for it (null: nothing); empty clears it.</summary>
    public static string? Problem(Option option, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        if (option.Kind == "name")
        {
            return value.Length <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? null : "A name: letters, digits, . _ and -, at most 128.";
        }
        if (value.Length > 1024 || value.Contains('\n'))
        {
            return "A path, on one line.";
        }
        if (!Path.IsPathFullyQualified(value))
        {
            return $"A full path ({(OperatingSystem.IsWindows() ? @"C:\Tools\" : "/opt/")}…), not one relative to the folder.";
        }
        return option.Kind == "file" && !File.Exists(value) ? $"There is no file {value}." : null;
    }

    /// <summary>Keeps a value in its config.json (null or empty: takes it out), the file's other keys as they are.</summary>
    public static void Save(Option option, string? value, AppPaths paths)
    {
        var file = FileOf(option, paths);
        var config = Config.Load(file);
        config.SetOption(option.Key, string.IsNullOrEmpty(value) ? null : value);
        config.Save(file);
    }
}
