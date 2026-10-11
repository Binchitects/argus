using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// What this computer keeps for one project that is no preference: whether its folder is trusted, its build
/// configuration (Debug or Release), the run configuration chosen, breakpoints and watch expressions. One file per
/// project folder in the data folder, never in the project (a repository cannot trust itself); the page's keys merged in
/// as it sends them (null forgets one), 1 MB at most. Trust is set only through <see cref="SetTrust"/>.
/// </summary>
internal sealed class WorkspaceState
{
    /// <summary>The most the file holds.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The key that says whether the folder is trusted: "trusted", "restricted", or missing (not asked yet).</summary>
    public const string TrustKey = "trust";

    private readonly string _file;
    private readonly string _root;
    private readonly object _gate = new();

    public WorkspaceState(string dataDir, string root)
    {
        _root = root;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16];
        _file = Path.Combine(dataDir, "workspaces", $"{hash}.json");
    }

    /// <summary>The file, for the tests.</summary>
    public string File => _file;

    /// <summary>Whether the person trusts the folder: its tasks, run configurations, language servers and formatters may run.</summary>
    public bool Trusted => Read().Str(TrustKey) == "trusted";

    /// <summary>"trusted", "restricted", or null when the person has not said.</summary>
    public string? Trust => Read().Str(TrustKey);

    /// <summary>The build configuration, Debug unless the person chose Release.</summary>
    public string BuildConfiguration => Read().Str("buildConfiguration") is "Release" ? "Release" : "Debug";

    /// <summary>What was saved; none when the file is missing or unreadable.</summary>
    public JsonObject Read()
    {
        lock (_gate)
        {
            return Load();
        }
    }

    /// <summary>The keys sent, over what was saved; the whole of it back. Trust is refused here.</summary>
    public JsonObject Merge(JsonObject change)
    {
        if (change.ContainsKey(TrustKey))
        {
            throw new IdeError(400, "invalid", "Trust the folder with its own call: /api/workspace/trust.");
        }
        if (change["buildConfiguration"] is { } build && build.GetValueKind() != System.Text.Json.JsonValueKind.Null
            && build.ToString() is not ("Debug" or "Release"))
        {
            throw new IdeError(400, "invalid", "The build configuration is Debug or Release.");
        }
        lock (_gate)
        {
            var all = Load();
            foreach (var (key, value) in change)
            {
                if (value is null)
                {
                    all.Remove(key);
                }
                else
                {
                    all[key] = value.DeepClone();
                }
            }
            Save(all);
            return all;
        }
    }

    /// <summary>The person trusts the folder (true), keeps it restricted (false), or forgets the choice (null).</summary>
    public JsonObject SetTrust(bool? trusted)
    {
        lock (_gate)
        {
            var all = Load();
            if (trusted is { } yes)
            {
                all[TrustKey] = yes ? "trusted" : "restricted";
            }
            else
            {
                all.Remove(TrustKey);
            }
            Save(all);
            return all;
        }
    }

    private void Save(JsonObject all)
    {
        all["root"] = _root;
        var text = all.ToJsonString();
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
        {
            throw new IdeError(413, "too_large", $"A project's state is kept up to {MaxBytes / 1024 / 1024} MB.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        // Whole or not at all: another run of code-arena in the same folder may read it meanwhile.
        var temp = $"{_file}.{Environment.ProcessId}.tmp";
        System.IO.File.WriteAllText(temp, text);
        System.IO.File.Move(temp, _file, overwrite: true);
    }

    private JsonObject Load()
    {
        try
        {
            var all = System.IO.File.Exists(_file) ? Json.ParseObject(System.IO.File.ReadAllText(_file)) ?? [] : [];
            // Another folder whose name hashes the same (never in practice) is not this one.
            return all.Str("root") is { } root && root != _root ? [] : all;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
