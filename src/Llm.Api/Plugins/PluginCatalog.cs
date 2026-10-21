using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Llm.Api.Plugins;

/// <summary>Configuration section "Plugins".</summary>
public sealed class PluginOptions
{
    /// <summary>The plugins that come with the app (the repository's plugins/ folder, in the image).</summary>
    public string Directory { get; set; } = "/plugins";
    /// <summary>A catalog to install from besides them: an index.json listing each plugin's zip and its SHA-256.</summary>
    public string? CatalogUrl { get; set; }
    /// <summary>The catalog publisher's public key (PEM, ECDSA P-256): when set, index.json.sig must verify against it.</summary>
    public string? CatalogKey { get; set; }
}

/// <summary>An assistant a plugin brings (assistants/*.yaml): for everyone, with the plugin's tool among its own.</summary>
public sealed record PluginAssistant(string Name, string? Description, string? Instructions, string[] Starters, string[] Tools, string? Model, string? Icon, string? Color);

/// <summary>
/// A plugin's files: its manifest (and its text), its OpenAPI document when its tools are one, the prompts, assistants and
/// safeguard rules it adds; and every file it names, by name, so an earlier version can be put back as it was.
/// </summary>
public sealed record PluginPackage(PluginManifest Manifest, string ManifestText, string? Spec, IReadOnlyList<Chat.PluginPrompt>? Prompts = null)
{
    public IReadOnlyList<PluginAssistant> Assistants { get; init; } = [];
    public IReadOnlyList<Safeguards.RuleRequest> Rules { get; init; } = [];
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();

    /// <summary>Its Code Arena skills: each folder's name (as its SKILL.md names it) and its files, by their path in the folder.</summary>
    public IReadOnlyList<PluginSkill> Skills { get; init; } = [];
}

/// <summary>A Code Arena skill a plugin brings: its name and description (from its SKILL.md) and its files, by their path in its folder.</summary>
public sealed record PluginSkill(string Name, string Description, IReadOnlyDictionary<string, string> Files);

/// <summary>A plugin a catalog offers: what it is, and where its zip is (with its SHA-256), or the folder it is in.</summary>
public sealed record CatalogEntry(string Name, string Version, string Title, string Description, string Source, string? Url, string? Sha256, string? PersonAuth);

/// <summary>Where plugins come from: the app's own folder, a catalog, an upload or an address.</summary>
public sealed class PluginCatalog(IOptionsMonitor<PluginOptions> options, IHttpClientFactory http)
{
    public const string Client = "plugins";
    private const long MaxZipBytes = 10 * 1024 * 1024;

    public sealed class PluginException(string message) : Exception(message);

    /// <summary>The plugins that come with the app, each a folder with its manifest.</summary>
    public IReadOnlyList<PluginPackage> BuiltIn()
    {
        var root = options.CurrentValue.Directory;
        if (!System.IO.Directory.Exists(root))
        {
            return [];
        }
        var found = new List<PluginPackage>();
        foreach (var dir in System.IO.Directory.GetDirectories(root).Order(StringComparer.Ordinal))
        {
            if (File.Exists(Path.Combine(dir, PluginManifest.FileName)))
            {
                found.Add(Read(name => File.Exists(Path.Combine(dir, name)) ? File.ReadAllText(Path.Combine(dir, name)) : null,
                    folder => System.IO.Directory.Exists(Path.Combine(dir, folder))
                        ? System.IO.Directory.EnumerateFiles(Path.Combine(dir, folder), "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
                        : []));
            }
        }
        return found;
    }

    /// <summary>Everything installable: the app's own, then the catalog's (its error said, not thrown).</summary>
    public async Task<(List<CatalogEntry> Entries, string? Problem)> ListAsync(CancellationToken ct)
    {
        var entries = BuiltIn().Select(p => new CatalogEntry(p.Manifest.Name, p.Manifest.Version, p.Manifest.Title, p.Manifest.Description, "app", null, null, p.Manifest.PersonAuth)).ToList();
        if (options.CurrentValue.CatalogUrl is not { Length: > 0 } url)
        {
            return (entries, null);
        }
        try
        {
            entries.AddRange((await RemoteAsync(url, ct)).Where(r => entries.All(e => e.Name != r.Name)));
            return (entries, null);
        }
        catch (Exception ex) when (ex is PluginException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return (entries, $"The catalog at {url} could not be read: {ex.Message}");
        }
    }

    /// <summary>The package of a plugin by name: the app's own, or downloaded from the catalog (its SHA-256 checked).</summary>
    public async Task<PluginPackage> GetAsync(string name, CancellationToken ct)
    {
        if (BuiltIn().FirstOrDefault(p => p.Manifest.Name == name) is { } own)
        {
            return own;
        }
        if (options.CurrentValue.CatalogUrl is { Length: > 0 } url && (await RemoteAsync(url, ct)).FirstOrDefault(e => e.Name == name) is { Url: { } zip } entry)
        {
            return await DownloadAsync(new Uri(new Uri(url), zip).ToString(), entry.Sha256, ct);
        }
        throw new PluginException($"No catalog offers a plugin named {name}.");
    }

    /// <summary>A zip from an address; with a SHA-256, only when it matches.</summary>
    public async Task<PluginPackage> DownloadAsync(string address, string? sha256, CancellationToken ct)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            throw new PluginException("The plugin's address must be an http(s) URL of its zip.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var bytes = await http.CreateClient(Client).GetByteArrayAsync(uri, timeout.Token);
        if (sha256 is { Length: > 0 } expected && !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), expected.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginException($"The zip at {uri} is not the one expected: its SHA-256 differs.");
        }
        return FromZip(bytes);
    }

    /// <summary>A plugin as a zip: its manifest at the top (or in one folder), and the files it names.</summary>
    public static PluginPackage FromZip(byte[] bytes)
    {
        if (bytes.Length > MaxZipBytes)
        {
            throw new PluginException("A plugin's zip is at most 10 MB.");
        }
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var manifest = zip.Entries.Where(e => e.Name == PluginManifest.FileName).OrderBy(e => e.FullName.Length).FirstOrDefault()
                ?? throw new PluginException($"The zip has no {PluginManifest.FileName}.");
            var root = manifest.FullName[..^PluginManifest.FileName.Length];
            return Read(name => zip.GetEntry(root + name) is { } entry && entry.Length < MaxZipBytes ? new StreamReader(entry.Open(), Encoding.UTF8).ReadToEnd() : null,
                folder => zip.Entries.Where(e => e.FullName.StartsWith(root + folder + "/", StringComparison.Ordinal) && !e.FullName.EndsWith('/')).Select(e => e.FullName[root.Length..]));
        }
        catch (InvalidDataException)
        {
            throw new PluginException("The file is not a zip.");
        }
    }

    /// <summary>A package from the files kept with an installed version (its release).</summary>
    public static PluginPackage FromFiles(IReadOnlyDictionary<string, string> files) =>
        Read(name => files.GetValueOrDefault(name), folder => files.Keys.Where(k => k.StartsWith(folder + "/", StringComparison.Ordinal)));

    /// <summary>The most one skill's files may hold, and the most files it may have.</summary>
    public const int MostSkillChars = 1_000_000, MostSkillFiles = 100;

    /// <param name="list">The files under a folder of the plugin, by their names in it (for its skills).</param>
    private static PluginPackage Read(Func<string, string?> read, Func<string, IEnumerable<string>> list)
    {
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        string? file(string name)
        {
            var text = read(name);
            if (text is not null)
            {
                kept[name] = text;
            }
            return text;
        }
        var text = file(PluginManifest.FileName) ?? throw new PluginException($"There is no {PluginManifest.FileName}.");
        var manifest = PluginManifest.Parse(text);
        string? spec = null;
        if (manifest.OpenApi is { } name)
        {
            spec = file(name) ?? throw new PluginException($"The plugin has no {name}, which its manifest names.");
        }
        var prompts = manifest.Prompts.Select(f => Chat.PromptLibrary.FromFile(f, file(f) ?? throw new PluginException($"The plugin has no {f}, which its manifest names."))).ToList();
        if (prompts.GroupBy(p => p.Name).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            throw new PluginException($"The prompt /{twice.Key} is in the plugin twice.");
        }
        var assistants = manifest.Assistants.Select(f => Assistant(f, file(f) ?? throw new PluginException($"The plugin has no {f}, which its manifest names."))).ToList();
        var rules = manifest.Safeguards.SelectMany(f => Rules(f, file(f) ?? throw new PluginException($"The plugin has no {f}, which its manifest names."))).ToList();
        var skills = new List<PluginSkill>();
        foreach (var folder in manifest.Skills)
        {
            var names = list(folder).Order(StringComparer.Ordinal).ToList();
            if (!names.Contains(folder + "/SKILL.md"))
            {
                throw new PluginException($"The skill {folder} has no SKILL.md.");
            }
            if (names.Count > MostSkillFiles)
            {
                throw new PluginException($"The skill {folder} has more than {MostSkillFiles} files.");
            }
            var files = names.ToDictionary(n => n[(folder.Length + 1)..], n => file(n) ?? "", StringComparer.Ordinal);
            if (files.Values.Sum(f => f.Length) > MostSkillChars)
            {
                throw new PluginException($"The skill {folder} is larger than {MostSkillChars / 1_000_000} MB.");
            }
            var (skillName, description) = SkillHead(files["SKILL.md"], Path.GetFileName(folder));
            if (skills.Any(x => x.Name == skillName))
            {
                throw new PluginException($"The skill {skillName} is in the plugin twice.");
            }
            skills.Add(new PluginSkill(skillName, description, files));
        }
        return new PluginPackage(manifest, text, spec, prompts) { Assistants = assistants, Rules = rules, Files = kept, Skills = skills };
    }

    /// <summary>A SKILL.md's name and description, from its front matter (name: and description:), else its folder and first line.</summary>
    public static (string Name, string Description) SkillHead(string text, string folder)
    {
        string? name = null, description = null;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var body = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---")
                {
                    body = i + 1;
                    break;
                }
                var colon = lines[i].IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && lines[i][..colon].Trim() is var key && key is "name" or "description")
                {
                    var value = lines[i][(colon + 1)..].Trim().Trim('"', '\'');
                    if (key == "name") name = value;
                    else description = value;
                }
            }
        }
        name = (name ?? folder).ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]{0,63}$"))
        {
            throw new PluginException($"\"{name}\" is not a skill name: lowercase letters, digits and dashes.");
        }
        return (name, description ?? lines.Skip(body).Select(l => l.Trim().TrimStart('#').Trim()).FirstOrDefault(l => l.Length > 0) ?? name);
    }

    private static JsonObject Yaml(string file, string text)
    {
        try
        {
            return Chat.Tools.OpenApi.Parse(text);
        }
        catch (Chat.Tools.OpenApi.SpecException ex)
        {
            throw new PluginException($"{file}: {ex.Message.Replace("OpenAPI document", "file", StringComparison.Ordinal)}");
        }
    }

    private static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;

    private static string[] List(JsonNode? n) => [.. (n as JsonArray ?? []).Select(S).OfType<string>()];

    private static PluginAssistant Assistant(string file, string text)
    {
        var a = Yaml(file, text);
        return new PluginAssistant(S(a["name"]) ?? throw new PluginException($"{file}: an assistant needs a name."), S(a["description"]), S(a["instructions"]),
            List(a["starters"]), List(a["tools"]), S(a["model"]), S(a["icon"]), S(a["color"]));
    }

    /// <summary>A rules file: one rule, or "rules:" with several; each as Admin → Safeguards takes it.</summary>
    private static IEnumerable<Safeguards.RuleRequest> Rules(string file, string text)
    {
        var doc = Yaml(file, text);
        foreach (var r in doc["rules"] is JsonArray many ? many.OfType<JsonObject>() : [doc])
        {
            Safeguards.RuleRequest rule;
            try
            {
                rule = System.Text.Json.JsonSerializer.Deserialize<Safeguards.RuleRequest>(r, System.Text.Json.JsonSerializerOptions.Web) ?? throw new PluginException($"{file}: a rule is empty.");
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new PluginException($"{file}: {ex.Message}");
            }
            if (Safeguards.SafeguardEndpoints.Invalid(rule) is { } wrong)
            {
                throw new PluginException($"{file}, {rule.Name}: {wrong}");
            }
            yield return rule;
        }
    }

    private async Task<List<CatalogEntry>> RemoteAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var client = http.CreateClient(Client);
        var bytes = await client.GetByteArrayAsync(url, timeout.Token);
        if (options.CurrentValue.CatalogKey is { Length: > 0 } pem)
        {
            var signature = Convert.FromBase64String((await client.GetStringAsync(url + ".sig", timeout.Token)).Trim());
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            if (!key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            {
                throw new PluginException("its signature does not verify against the catalog key (Settings → Plugins).");
            }
        }
        var index = JsonNode.Parse(bytes) as JsonObject ?? throw new PluginException("its index is not a JSON object.");
        return [.. (index["plugins"] as JsonArray ?? []).OfType<JsonObject>().Select(p => new CatalogEntry(
            p["name"]?.GetValue<string>() ?? "", p["version"]?.GetValue<string>() ?? "0", p["title"]?.GetValue<string>() ?? p["name"]?.GetValue<string>() ?? "",
            p["description"]?.GetValue<string>() ?? "", "catalog", p["url"]?.GetValue<string>(), p["sha256"]?.GetValue<string>(), p["per_person"]?.GetValue<string>()))
            .Where(e => e.Name.Length > 0 && e.Url is not null)];
    }
}
