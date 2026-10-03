using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>Configuration section "HuggingFace": where it is, and the token for gated repositories (HF_TOKEN).</summary>
public sealed class HuggingFaceOptions
{
    public string BaseUrl { get; set; } = "https://huggingface.co";
    public string? Token { get; set; }
}

/// <summary>Hugging Face refused or failed; the message says which, for an admin to act on.</summary>
public sealed class HuggingFaceException(string message, int status = 502) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>A repository as a search lists it.</summary>
public sealed record HfModel(string Id, long Downloads, long Likes, string? LastModified, bool Gated, string? PipelineTag, IReadOnlyList<string> Tags);

/// <summary>A file of a repository: its path, size and, for a file kept in LFS, its SHA-256.</summary>
public sealed record HfFile(string Path, long Size, string? Sha256);

/// <summary>
/// A model a repository offers as GGUF: one file, or the parts of a split one, with its
/// quantisation as the file name says (Q4_K_M, IQ4_XS, BF16...), and whether it is a
/// vision projector (mmproj) rather than a model.
/// </summary>
public sealed record HfGguf(string Name, string? Quant, long Size, IReadOnlyList<HfFile> Files, bool Projector);

/// <summary>A repository's details: its revision, licence, what GGUF says of the model, and its GGUF files grouped as models.</summary>
public sealed record HfRepo(string Id, string Sha, bool Gated, string? License, string? Architecture, long? Parameters, int? Context,
    long Downloads, long Likes, string? LastModified, IReadOnlyList<HfGguf> Models);

/// <summary>Hugging Face's API: searching GGUF models, a repository's files, and downloading one (with resume).</summary>
public sealed partial class HuggingFace(HttpClient http, IOptionsMonitor<HuggingFaceOptions> options)
{
    private string Base => options.CurrentValue.BaseUrl.TrimEnd('/');

    private HttpRequestMessage Request(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        if (options.CurrentValue.Token is { Length: > 0 } token)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return req;
    }

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, url);
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new HuggingFaceException("Hugging Face did not answer: is the internet reachable from the app?");
        }
        using (res)
        {
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new HuggingFaceException(Gated(), 403);
            }
            if (res.StatusCode == HttpStatusCode.NotFound)
            {
                throw new HuggingFaceException("Hugging Face has no such repository.", 404);
            }
            if (!res.IsSuccessStatusCode)
            {
                throw new HuggingFaceException($"Hugging Face answered HTTP {(int)res.StatusCode}.");
            }
            return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        }
    }

    private string Gated() => string.IsNullOrEmpty(options.CurrentValue.Token)
        ? "This repository is gated: accept its terms on huggingface.co, and set a token of that account (HF_TOKEN, Settings → Model)."
        : "This repository is gated: accept its terms on huggingface.co with the account whose token is set (HF_TOKEN).";

    /// <summary>GGUF repositories matching the words, most downloaded first.</summary>
    public async Task<IReadOnlyList<HfModel>> SearchAsync(string query, CancellationToken ct)
    {
        var url = $"{Base}/api/models?search={Uri.EscapeDataString(query)}&filter=gguf&sort=downloads&direction=-1&limit=30";
        var list = await GetJsonAsync(url, ct) as JsonArray ?? [];
        return [.. list.OfType<JsonObject>().Select(m => new HfModel(
            m["id"]?.GetValue<string>() ?? "", Long(m["downloads"]), Long(m["likes"]), m["lastModified"]?.GetValue<string>() ?? m["createdAt"]?.GetValue<string>(),
            m["gated"] is JsonValue g && (g.TryGetValue<bool>(out var b) ? b : g.TryGetValue<string>(out var s) && s.Length > 0),
            m["pipeline_tag"]?.GetValue<string>(), [.. (m["tags"] as JsonArray ?? []).Select(t => t?.GetValue<string>() ?? "").Where(t => t.Length > 0).Take(12)]))];
    }

    /// <summary>A repository: its current revision and its GGUF files as models.</summary>
    public async Task<HfRepo> RepoAsync(string id, CancellationToken ct)
    {
        if (!RepoId().IsMatch(id))
        {
            throw new HuggingFaceException("A repository is owner/name, e.g. Qwen/Qwen3-8B-GGUF.", 400);
        }
        var (info, sha, files) = await TreeAsync(id, ct);
        var gguf = info["gguf"] as JsonObject;
        return new HfRepo(id, sha, info["gated"] is JsonValue g && (g.TryGetValue<bool>(out var b) ? b : g.TryGetValue<string>(out var s) && s.Length > 0),
            info["cardData"]?["license"]?.ToString(), gguf?["architecture"]?.GetValue<string>(), gguf?["total"] is { } total ? Long(total) : null,
            gguf?["context_length"] is { } ctx ? (int)Long(ctx) : null, Long(info["downloads"]), Long(info["likes"]), info["lastModified"]?.GetValue<string>(),
            Group(files));
    }

    /// <summary>A repository's details, its current revision, and every file in it.</summary>
    public async Task<(JsonObject Info, string Sha, IReadOnlyList<HfFile> Files)> TreeAsync(string id, CancellationToken ct)
    {
        var info = await GetJsonAsync($"{Base}/api/models/{id}", ct) as JsonObject ?? throw new HuggingFaceException("Hugging Face sent nothing for this repository.");
        var sha = info["sha"]?.GetValue<string>() ?? "main";
        var tree = await GetJsonAsync($"{Base}/api/models/{id}/tree/{sha}?recursive=true", ct) as JsonArray ?? [];
        var files = tree.OfType<JsonObject>().Where(f => f["type"]?.GetValue<string>() == "file")
            .Select(f => new HfFile(f["path"]?.GetValue<string>() ?? "", Long(f["lfs"]?["size"]) is > 0 and var l ? l : Long(f["size"]), f["lfs"]?["oid"]?.GetValue<string>()))
            .ToList();
        return (info, sha, files);
    }

    /// <summary>GGUF files as models: a split model's parts together, each with its quantisation and size; vision projectors apart.</summary>
    public static IReadOnlyList<HfGguf> Group(IEnumerable<HfFile> files)
    {
        var models = new List<HfGguf>();
        foreach (var group in files.Where(f => f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(f => Split().Replace(f.Path, ".gguf"), StringComparer.Ordinal))
        {
            var parts = group.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
            var name = System.IO.Path.GetFileName(group.Key);
            models.Add(new HfGguf(group.Key, QuantOf(group.Key), parts.Sum(f => f.Size), parts, name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)));
        }
        return [.. models.OrderBy(m => m.Projector).ThenBy(m => m.Size)];
    }

    /// <summary>The quantisation a file name says: Q4_K_M, IQ4_XS, Q8_0, BF16, UD-Q4_K_XL...</summary>
    public static string? QuantOf(string path)
    {
        var m = Quant().Match(System.IO.Path.GetFileNameWithoutExtension(path));
        return m.Success ? m.Value.ToUpperInvariant() : null;
    }

    /// <summary>Opens a file of a repository at a revision, from <paramref name="from"/> bytes on (to resume a download).</summary>
    public async Task<HttpResponseMessage> OpenAsync(string repo, string revision, string path, long from, CancellationToken ct)
    {
        var url = $"{Base}/{repo}/resolve/{revision}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";
        var req = Request(HttpMethod.Get, url);
        if (from > 0)
        {
            req.Headers.Range = new RangeHeaderValue(from, null);
        }
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            req.Dispose();
            throw new HuggingFaceException($"Hugging Face did not answer for {path}.");
        }
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            res.Dispose();
            throw new HuggingFaceException(Gated(), 403);
        }
        if (!res.IsSuccessStatusCode)
        {
            var code = (int)res.StatusCode;
            res.Dispose();
            throw new HuggingFaceException($"Hugging Face answered HTTP {code} for {path}.");
        }
        return res;
    }

    private static long Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : n is JsonValue d && d.TryGetValue<double>(out var x) ? (long)x : 0;

    [GeneratedRegex(@"^[A-Za-z0-9][\w.-]*/[\w.-]+$")]
    private static partial Regex RepoId();

    [GeneratedRegex(@"-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex Split();

    [GeneratedRegex(@"(UD-)?(I?Q\d(_K)?(_[A-Z]+)?(_\d)?|BF16|F16|F32|MXFP4(_MOE)?)(?=$|[-.])", RegexOptions.IgnoreCase)]
    private static partial Regex Quant();
}
