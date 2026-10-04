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

/// <summary>A plugin's files: its manifest (and its text), its OpenAPI document when its tools are one, and the prompts it adds.</summary>
public sealed record PluginPackage(PluginManifest Manifest, string ManifestText, string? Spec, IReadOnlyList<Chat.PluginPrompt>? Prompts = null);

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
                found.Add(Read(name => File.Exists(Path.Combine(dir, name)) ? File.ReadAllText(Path.Combine(dir, name)) : null));
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
            return Read(name => zip.GetEntry(root + name) is { } entry && entry.Length < MaxZipBytes ? new StreamReader(entry.Open(), Encoding.UTF8).ReadToEnd() : null);
        }
        catch (InvalidDataException)
        {
            throw new PluginException("The file is not a zip.");
        }
    }

    private static PluginPackage Read(Func<string, string?> file)
    {
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
        return new PluginPackage(manifest, text, spec, prompts);
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
