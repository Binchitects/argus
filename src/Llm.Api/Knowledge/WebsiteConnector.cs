using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Llm.Api.Chat.Tools;
using Llm.Core.Chat;

namespace Llm.Api.Knowledge;

/// <summary>
/// A website, crawled from its first page through its links, on the hosts the admin allows only, up to
/// a number of pages, minding robots.txt. Every request goes through the chat's web guard: public
/// addresses only, every redirect checked. Pages are read as the Web tool reads them (their main text;
/// PDFs and documents too). Who may read them is who the admin chose.
/// </summary>
public sealed partial class WebsiteConnector(WebFetcher web) : IKnowledgeConnector
{
    public const int MostPages = 2_000;

    public string Kind => "website";

    public string? Check(KnowledgeSource source) =>
        !Uri.TryCreate(source.Location.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")
            ? "Give the first page's full address, https://…"
            : source.MaxPages is < 1 or > MostPages ? $"Read 1 to {MostPages:N0} pages." : null;

    /// <summary>The hosts its pages may be on: the admin's, or the first page's host.</summary>
    public static string HostsOf(KnowledgeSource source) =>
        source.Hosts is { Length: > 0 } hosts ? hosts : new Uri(source.Location.Trim()).Host;

    public async IAsyncEnumerable<FoundDocument> ReadAsync(KnowledgeSource source, SyncPass pass, [EnumeratorCancellation] CancellationToken ct)
    {
        var start = Plain(new Uri(source.Location.Trim()));
        var hosts = HostsOf(source);
        var readers = Readers.For(source.Audience, source.Groups);
        var robots = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Uri>([start]);
        var queued = new HashSet<string>(StringComparer.Ordinal) { start.AbsoluteUri };
        var read = new HashSet<string>(StringComparer.Ordinal);
        var failed = 0;
        while (queue.Count > 0 && read.Count < source.MaxPages)
        {
            var url = queue.Dequeue();
            if (await ForbiddenAsync(url, hosts, robots, ct))
            {
                continue;
            }
            WebPage page;
            try
            {
                page = await web.FetchAsync(url, ct, hosts);
            }
            catch (WebAccessException ex)
            {
                if (url == start)
                {
                    throw new KnowledgeException($"The first page could not be read: {ex.Message}");
                }
                // A page that is gone goes; one that failed otherwise stays as it was until it can be read.
                if (ex.Status is not (404 or 410))
                {
                    pass.Kept.Add(url.AbsoluteUri);
                    if (++failed <= 3)
                    {
                        pass.Problems.Add($"{url}: {ex.Message}");
                    }
                }
                continue;
            }
            var at = Plain(page.Url);
            if (!read.Add(at.AbsoluteUri))
            {
                continue;
            }
            var type = page.ContentType.ToLowerInvariant();
            string title, text;
            if (type is "text/html" or "application/xhtml+xml" || (type.Length == 0 && page.Body.AsSpan(0, Math.Min(page.Body.Length, 512)).IndexOf("<html"u8) >= 0))
            {
                var html = Encoding.UTF8.GetString(page.Body);
                (title, text) = Html.Read(html);
                foreach (var link in Links(html, at))
                {
                    if (WebGuard.Allowed(link.Host, hosts) && queued.Add(link.AbsoluteUri))
                    {
                        queue.Enqueue(link);
                    }
                }
            }
            else
            {
                title = Uri.UnescapeDataString(Path.GetFileName(at.AbsolutePath));
                text = FolderConnector.Read(title.Length > 0 ? title : "page", page.Body);
            }
            if (text.Trim().Length == 0)
            {
                continue;
            }
            var name = title.Length > 0 ? title : at.AbsolutePath;
            yield return new FoundDocument(at.AbsoluteUri, name, at.AbsoluteUri, Readers.Hash(text), readers,
                _ => Task.FromResult<string?>(title.Length > 0 ? $"# {title}\n\n{text}" : text));
        }
        if (failed > 3)
        {
            pass.Problems.Add($"and {failed - 3} more pages could not be read");
        }
    }

    /// <summary>The page's links to pages (not pictures, styles, scripts or archives), without their #fragment.</summary>
    public static IEnumerable<Uri> Links(string html, Uri page)
    {
        foreach (Match m in Href().Matches(html))
        {
            if (Uri.TryCreate(page, WebUtility.HtmlDecode(m.Groups[1].Value.Trim()), out var link) && link.Scheme is "http" or "https"
                && !NotAPage().IsMatch(link.AbsolutePath))
            {
                yield return Plain(link);
            }
        }
    }

    private static Uri Plain(Uri url) => url.Fragment.Length == 0 ? url : new UriBuilder(url) { Fragment = "" }.Uri;

    /// <summary>Whether the site's robots.txt (for any crawler) keeps us from the page. Read once per host; none: nothing is.</summary>
    private async Task<bool> ForbiddenAsync(Uri url, string hosts, Dictionary<string, List<string>> robots, CancellationToken ct)
    {
        var site = url.GetLeftPart(UriPartial.Authority);
        if (!robots.TryGetValue(site, out var rules))
        {
            rules = [];
            try
            {
                var file = await web.FetchAsync(new Uri($"{site}/robots.txt"), ct, hosts);
                rules = Disallowed(Encoding.UTF8.GetString(file.Body));
            }
            catch (WebAccessException)
            {
                // No robots.txt: every page may be read.
            }
            robots[site] = rules;
        }
        var path = url.PathAndQuery;
        return rules.Any(r => path.StartsWith(r, StringComparison.Ordinal));
    }

    /// <summary>The paths robots.txt disallows for every crawler (User-agent: *), as prefixes.</summary>
    public static List<string> Disallowed(string robotsTxt)
    {
        var rules = new List<string>();
        var forAll = false;
        var agents = false;
        foreach (var raw in robotsTxt.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }
            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (field == "user-agent")
            {
                // Lines of user agents in a row name one group.
                forAll = (agents && forAll) || value == "*";
                agents = true;
                continue;
            }
            agents = false;
            if (field == "disallow" && forAll && value.Length > 0)
            {
                rules.Add(value.TrimEnd('*', '$'));
            }
        }
        return rules;
    }

    [GeneratedRegex(@"<a\b[^>]*?\bhref\s*=\s*[""']([^""'#][^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex Href();

    [GeneratedRegex(@"\.(png|jpe?g|gif|webp|svg|ico|bmp|css|js|mjs|map|zip|gz|tgz|tar|7z|rar|exe|msi|dmg|iso|mp3|mp4|m4a|wav|ogg|webm|avi|mov|woff2?|ttf|eot|otf|apk|bin)$", RegexOptions.IgnoreCase)]
    private static partial Regex NotAPage();
}
