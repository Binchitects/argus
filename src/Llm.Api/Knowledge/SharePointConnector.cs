using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Chat.Tools;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Knowledge;

/// <summary>
/// SharePoint sites and document libraries (a OneDrive too, by its address), read through Microsoft Graph as an app
/// of the company's Microsoft Entra tenant (a client ID and secret, with Sites.Read.All or Sites.Selected): their
/// files (Word, PowerPoint, Excel, PDF, text, Markdown, as the chat reads attachments) and their site pages. Each
/// library is read whole once a day and by Graph's changes (delta) in between, so only what changed is read again.
/// Who may read a file is its permissions in Graph: people by their email, Microsoft Entra groups as people's
/// directory groups (by the group's name or ID, as the company sign-in's groups claim). What Graph cannot tell (a
/// SharePoint group like a site's Members, a site page) is read by whom the admin chose, and the admin page says so.
/// </summary>
public sealed partial class SharePointConnector(IHttpClientFactory http, IOptions<AuthOptions> auth, AppDbContext db, TimeProvider clock)
    : IKnowledgeConnector, IConnectionTest
{
    public const string Client = "graph";
    public const string GraphUrl = "https://graph.microsoft.com/v1.0";
    public const string LoginUrl = "https://login.microsoftonline.com";
    private const long MaxBytes = 50 * 1024 * 1024;
    /// <summary>Pages of one listing at most.</summary>
    private const int MaxCalls = 2_000;
    /// <summary>A library is read whole again after this, besides its changes: what the changes missed is caught.</summary>
    private static readonly TimeSpan ReadAgain = TimeSpan.FromDays(1);
    private const string Secret = "client secret";

    /// <summary>The files read: what the chat reads as attachments.</summary>
    private static readonly HashSet<string> Readable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".pptx", ".xlsx", ".pdf", ".txt", ".md", ".markdown", ".csv", ".html", ".htm", ".odt", ".ods", ".odp", ".rtf", ".json", ".xml", ".log", ".adoc", ".rst",
    };

    private string? _token;
    private DateTimeOffset _expires;
    private readonly Dictionary<string, string?> _emails = new(StringComparer.Ordinal);

    public string Kind => "sharepoint";

    public string? Check(KnowledgeSource source) =>
        string.IsNullOrWhiteSpace(source.Tenant) || !Tenant().IsMatch(source.Tenant.Trim()) ? "Give the Microsoft Entra tenant's ID (or its domain, example.onmicrosoft.com)."
        : string.IsNullOrWhiteSpace(source.Account) ? "Give the app's client (application) ID, from its page in Microsoft Entra."
        : Lines(source.Location) is not { Count: > 0 } lines ? "Give the sites or libraries to read, by their addresses, one a line."
        : lines.FirstOrDefault(l => !Uri.TryCreate(l, UriKind.Absolute, out var u) || u.Scheme != "https") is { } bad ? $"{bad} is not an address: give each site or library as https://…"
        : SourceSecret.Problem(source, auth.Value.DataKey, Secret);

    /// <summary>The sites and libraries an admin listed, one a line.</summary>
    public static List<string> Lines(string location) =>
        [.. location.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    private sealed record SiteRef(string Id, string Name, string WebUrl);

    private sealed record DriveRef(string Id, string Name, SiteRef Site);

    /// <summary>Where a library's changes were read up to (Graph's delta link), and when it was last read whole.</summary>
    private sealed record Cursor(string Link, DateTimeOffset Full);

    /// <summary>What a line names: a site (whole: its libraries and pages), or one of its libraries.</summary>
    private sealed record Place(SiteRef? Site, List<DriveRef> Drives, string? Problem, bool Whole = false);

    public async IAsyncEnumerable<FoundDocument> ReadAsync(KnowledgeSource source, SyncPass pass, [EnumeratorCancellation] CancellationToken ct)
    {
        await TokenAsync(source, ct);
        var cursors = Parse(source.Cursor) is JsonObject saved
            ? saved.Where(c => c.Value is JsonObject).ToDictionary(c => c.Key, c => new Cursor(Str(c.Value, "link"), DateTimeOffset.TryParse(Str(c.Value, "full"), CultureInfo.InvariantCulture, out var at) ? at : default))
            : [];
        var next = new Dictionary<string, Cursor>(StringComparer.Ordinal);
        var chosen = Readers.Chosen(source.Id);
        var drives = new List<DriveRef>();
        var sites = new List<SiteRef>();
        foreach (var line in Lines(source.Location))
        {
            var place = await PlaceAsync(source, line, ct);
            if (place.Problem is { } problem)
            {
                // What it held cannot be told apart from the rest: nothing is removed this time.
                pass.Incomplete = true;
                pass.Problems.Add(problem);
                continue;
            }
            drives.AddRange(place.Drives.Where(d => drives.All(x => x.Id != d.Id)));
            if (place is { Site: { } site, Whole: true } && source.SitePages && sites.All(s => s.Id != site.Id))
            {
                sites.Add(site);
            }
        }
        foreach (var drive in drives)
        {
            var prefix = drive.Id + "/";
            var had = cursors.GetValueOrDefault(drive.Id);
            var incremental = had is { Link.Length: > 0 } && had.Full > clock.GetUtcNow() - ReadAgain && had.Link.StartsWith(GraphUrl + "/", StringComparison.Ordinal);
            var url = incremental ? had!.Link : $"{GraphUrl}/drives/{Uri.EscapeDataString(drive.Id)}/root/delta";
            var full = incremental ? had!.Full : clock.GetUtcNow();
            if (incremental)
            {
                // Only what changed is listed: the rest stays as it is.
                pass.Unchanged.Add(prefix);
            }
            var failed = new StrongBox<bool>(false);
            string? delta = null;
            var skipped = 0;
            for (var n = 0; url is not null && n < MaxCalls; n++)
            {
                var (status, body) = await GetAsync(source, url, ct, changes: true);
                if (status == 410 && incremental)
                {
                    // The changes since then are gone: the library is read whole.
                    incremental = false;
                    pass.Unchanged.Remove(prefix);
                    (url, full) = ($"{GraphUrl}/drives/{Uri.EscapeDataString(drive.Id)}/root/delta", clock.GetUtcNow());
                    continue;
                }
                if (status != 200 || body?["value"] is not JsonArray items)
                {
                    failed.Value = true;
                    if (!pass.Unchanged.Contains(prefix))
                    {
                        pass.Unchanged.Add(prefix);
                    }
                    pass.Problems.Add($"{drive.Site.Name} · {drive.Name}: its files could not be listed ({Said(status)}), so they stay as they were");
                    break;
                }
                foreach (var item in items.OfType<JsonObject>())
                {
                    var id = Str(item, "id");
                    var key = prefix + id;
                    if (item["deleted"] is not null)
                    {
                        pass.Gone.Add(key);
                        continue;
                    }
                    // Folders are not documents; their sharing changes bring their files.
                    if (item["file"] is null || id.Length == 0)
                    {
                        continue;
                    }
                    var name = Str(item, "name");
                    var size = item["size"] is JsonValue sv && sv.TryGetValue<long>(out var bytes) ? bytes : 0;
                    if (!Readable.Contains(Path.GetExtension(name)) || size is 0 or > MaxBytes)
                    {
                        // Renamed to a kind not read, or grown too big: what was read of it goes.
                        pass.Gone.Add(key);
                        skipped++;
                        continue;
                    }
                    if (await PermissionsAsync(source, drive.Id, id, ct) is not { } readers)
                    {
                        // Who may read it cannot be told now: it stays as it was, and the changes are read again next time.
                        pass.Kept.Add(key);
                        failed.Value = true;
                        continue;
                    }
                    var version = Str(item, "cTag") is { Length: > 0 } tag ? tag : Str(item, "eTag") is { Length: > 0 } etag ? etag : Str(item, "lastModifiedDateTime");
                    var (driveId, itemId) = (drive.Id, id);
                    yield return new FoundDocument(key, $"{name} ({drive.Site.Name})", Str(item, "webUrl") is { Length: > 0 } web ? web : null, version, readers, async token =>
                    {
                        var text = await ContentAsync(source, driveId, itemId, name, token);
                        if (text is null)
                        {
                            failed.Value = true;
                        }
                        return text;
                    });
                }
                url = Link(body, "@odata.nextLink");
                delta = Link(body, "@odata.deltaLink") ?? delta;
            }
            if (skipped > 0)
            {
                pass.Mirror.Add($"{drive.Site.Name} · {drive.Name}: {Count(skipped, "file is", "files are")} not read (not a kind the chat reads, empty, or over 50 MB).");
            }
            // A library read to its end goes on from there next time; otherwise from where it was.
            if (!failed.Value && delta is not null)
            {
                next[drive.Id] = new Cursor(delta, full);
            }
            else if (had is not null)
            {
                next[drive.Id] = had;
            }
        }
        if (drives.Count > 0)
        {
            pass.Mirror.Add($"{string.Join(", ", drives.Select(d => $"{d.Site.Name} · {d.Name}"))}: who may read each file is mirrored from SharePoint.");
        }
        // Counted over what the source holds, as only what changed is listed.
        var fallback = (await db.KnowledgeDocuments.AsNoTracking().Where(d => d.SourceId == source.Id && d.Readers.Contains(chosen) && !d.Key.Contains("/pages/"))
            .Select(d => d.Key).ToListAsync(ct)).Count(k => !pass.Gone.Contains(k));
        if (fallback > 0)
        {
            pass.Mirror.Add($"{Count(fallback, "file is", "files are")} shared with SharePoint groups (a site's Owners, Members or Visitors) or people Graph does not name, so the people you chose read {(fallback == 1 ? "it" : "them")} too.");
        }
        foreach (var site in sites)
        {
            var (pages, status) = await ListAsync(source, $"{GraphUrl}/sites/{Uri.EscapeDataString(site.Id)}/pages/microsoft.graph.sitePage", ct);
            if (pages is null)
            {
                pass.Unchanged.Add($"{site.Id}/pages/");
                pass.Problems.Add($"{site.Name}: its pages could not be listed ({Said(status)}), so they stay as they were");
                continue;
            }
            pass.Mirror.Add($"{site.Name}: Graph does not give its pages' permissions, so the people you chose read them.");
            foreach (var page in pages)
            {
                var id = Str(page, "id");
                var title = Str(page, "title") is { Length: > 0 } t ? t : Str(page, "name");
                var version = Str(page, "eTag") is { Length: > 0 } etag ? etag : Str(page, "lastModifiedDateTime");
                var siteId = site.Id;
                yield return new FoundDocument($"{site.Id}/pages/{id}", $"{title} ({site.Name})", Str(page, "webUrl") is { Length: > 0 } web ? web : null, version, [chosen],
                    token => PageAsync(source, siteId, id, title, token));
            }
        }
        var cursor = JsonSerializer.Serialize(next.ToDictionary(c => c.Key, c => new { link = c.Value.Link, full = c.Value.Full.ToString("O", CultureInfo.InvariantCulture) }));
        await db.KnowledgeSources.Where(s => s.Id == source.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.Cursor, cursor), ct);
    }

    public async Task<string> TestAsync(KnowledgeSource source, CancellationToken ct)
    {
        await TokenAsync(source, ct);
        var found = new List<string>();
        var problems = new List<string>();
        foreach (var line in Lines(source.Location))
        {
            var place = await PlaceAsync(source, line, ct);
            if (place.Problem is { } problem)
            {
                problems.Add(problem);
                continue;
            }
            found.Add($"{place.Site!.Name}: {Count(place.Drives.Count, "library", "libraries")} ({string.Join(", ", place.Drives.Select(d => d.Name))})");
        }
        if (found.Count == 0)
        {
            throw new KnowledgeException($"Signed in to Microsoft Graph, but nothing listed can be read: {string.Join("; ", problems)}.");
        }
        return $"Signed in to Microsoft Graph as the app. {string.Join("; ", found)}." + (problems.Count > 0 ? $" Not read: {string.Join("; ", problems)}." : "");
    }

    /// <summary>
    /// The site at a line's address and its libraries, or the one library the address names: the longest leading part
    /// of the path that is a site is the site (subsites too); the rest names a library, by its address or its name.
    /// </summary>
    private async Task<Place> PlaceAsync(KnowledgeSource source, string line, CancellationToken ct)
    {
        var u = new Uri(line);
        var parts = Uri.UnescapeDataString(u.AbsolutePath).Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        // A library's own page (…/Shared Documents/Forms/AllItems.aspx), or a page of the site.
        if (parts.FindIndex(p => p.Equals("Forms", StringComparison.OrdinalIgnoreCase)) is >= 0 and var forms)
        {
            parts = parts[..forms];
        }
        if (parts.Count > 0 && parts[^1].EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
        {
            parts.RemoveAt(parts.Count - 1);
        }
        for (var n = Math.Min(parts.Count, 5); n >= 0; n--)
        {
            var path = string.Join('/', parts.Take(n).Select(Uri.EscapeDataString));
            var (status, body) = await GetAsync(source, n == 0 ? $"{GraphUrl}/sites/{u.Host}" : $"{GraphUrl}/sites/{u.Host}:/{path}", ct);
            if (status == 200 && body is JsonObject found)
            {
                var site = new SiteRef(Str(found, "id"), Str(found, "displayName") is { Length: > 0 } d ? d : Str(found, "name"), Str(found, "webUrl"));
                var (libraries, listed) = await ListAsync(source, $"{GraphUrl}/sites/{Uri.EscapeDataString(site.Id)}/drives?$select=id,name,webUrl,driveType", ct);
                if (libraries is null)
                {
                    return new Place(null, [], $"{line}: its libraries could not be listed ({Said(listed)})");
                }
                var all = libraries.Select(l => (Drive: new DriveRef(Str(l, "id"), Str(l, "name"), site), Url: Str(l, "webUrl"))).Where(l => l.Drive.Id.Length > 0).ToList();
                if (n == parts.Count)
                {
                    return new Place(site, [.. all.Select(l => l.Drive)], null, Whole: true);
                }
                var rest = string.Join('/', parts.Skip(n));
                var library = all.Where(l => l.Drive.Name.Equals(rest, StringComparison.OrdinalIgnoreCase)
                    || (Uri.TryCreate(l.Url, UriKind.Absolute, out var lu) && Uri.UnescapeDataString(lu.AbsolutePath).TrimEnd('/').EndsWith("/" + rest, StringComparison.OrdinalIgnoreCase))).ToList();
                return library.Count > 0
                    ? new Place(site, [library[0].Drive], null)
                    : new Place(null, [], $"{line}: the site {site.Name} has no library {rest}");
            }
            if (status == 401)
            {
                throw new KnowledgeException("Microsoft Graph refused the app's token (401).");
            }
            if (status == 403)
            {
                return new Place(null, [], $"{line}: the app may not read it (403): grant it Sites.Read.All, or Sites.Selected with this site");
            }
            if (status is not (400 or 404))
            {
                return new Place(null, [], $"{line}: it could not be read ({Said(status)})");
            }
        }
        return new Place(null, [], $"{line}: there is no such site");
    }

    /// <summary>
    /// Who may read a file, from its permissions: people by their email, Microsoft Entra groups by ID and name, everyone
    /// for "Everyone except external users". A sharing link counts only for the people it names. When some grant cannot
    /// be told (a SharePoint group), whom the admin chose read it too. Null when they cannot be read.
    /// </summary>
    private async Task<List<string>?> PermissionsAsync(KnowledgeSource source, string drive, string item, CancellationToken ct)
    {
        var (grants, _) = await ListAsync(source, $"{GraphUrl}/drives/{Uri.EscapeDataString(drive)}/items/{Uri.EscapeDataString(item)}/permissions", ct);
        if (grants is null)
        {
            return null;
        }
        var readers = new SortedSet<string>(StringComparer.Ordinal);
        var mirrored = true;
        foreach (var grant in grants)
        {
            var sets = new List<JsonObject>();
            if (grant["grantedToV2"] is JsonObject one)
            {
                sets.Add(one);
            }
            sets.AddRange((grant["grantedToIdentitiesV2"] as JsonArray ?? []).OfType<JsonObject>());
            if (sets.Count == 0)
            {
                // Older answers: grantedTo and grantedToIdentities.
                if (grant["grantedTo"] is JsonObject old)
                {
                    sets.Add(old);
                }
                sets.AddRange((grant["grantedToIdentities"] as JsonArray ?? []).OfType<JsonObject>());
            }
            foreach (var set in sets)
            {
                mirrored &= await GrantAsync(source, set, readers, ct);
            }
        }
        if (!mirrored || readers.Count == 0)
        {
            readers.Add(Readers.Chosen(source.Id));
        }
        return [.. readers];
    }

    /// <summary>One grant's identity as readers; false when it cannot be told (a SharePoint group, or a person Graph does not give an email for).</summary>
    private async Task<bool> GrantAsync(KnowledgeSource source, JsonObject set, SortedSet<string> readers, CancellationToken ct)
    {
        var login = set["siteUser"] is JsonObject su ? Str(su, "loginName") : "";
        if (set["group"] is JsonObject group)
        {
            foreach (var name in new[] { Str(group, "id"), Str(group, "displayName") }.Where(n => n.Trim().Length > 0))
            {
                readers.Add(Readers.Directory(name));
            }
            return true;
        }
        if (set["user"] is JsonObject user)
        {
            var email = Str(user, "email");
            if (!email.Contains('@', StringComparison.Ordinal))
            {
                email = Claim(login) is ("person", var e) ? e : "";
            }
            if (!email.Contains('@', StringComparison.Ordinal) && Str(user, "id") is { Length: > 0 } id)
            {
                email = await EmailAsync(source, id, ct) ?? "";
            }
            if (email.Contains('@', StringComparison.Ordinal))
            {
                readers.Add(Readers.Person(email));
                return true;
            }
            return false;
        }
        if (login.Length > 0)
        {
            switch (Claim(login))
            {
                case ("person", var e):
                    readers.Add(Readers.Person(e));
                    return true;
                case ("group", var g):
                    readers.Add(Readers.Directory(g));
                    return true;
                case ("everyone", _):
                    readers.Add(Readers.Everyone);
                    return true;
            }
            return false;
        }
        // A SharePoint group cannot be read through Graph; an application or a device is nobody to read.
        return set["siteGroup"] is null;
    }

    /// <summary>What a SharePoint login name says: a person (i:0#.f|membership|jane@example.com), a Microsoft Entra group, or everyone in the company.</summary>
    private static (string Kind, string Value) Claim(string login)
    {
        var parts = login.Split('|');
        if (login.StartsWith("i:0#.f|membership|", StringComparison.OrdinalIgnoreCase) && parts[^1].Contains('@', StringComparison.Ordinal))
        {
            return ("person", parts[^1]);
        }
        if (login.StartsWith("c:0o.c|federateddirectoryclaimprovider|", StringComparison.OrdinalIgnoreCase) || login.StartsWith("c:0t.c|tenant|", StringComparison.OrdinalIgnoreCase))
        {
            // A Microsoft 365 group's owners end in _o: they are in the group too.
            var id = parts[^1];
            return ("group", id.EndsWith("_o", StringComparison.Ordinal) ? id[..^2] : id);
        }
        if (login.StartsWith("c:0-.f|rolemanager|spo-grid-all-users", StringComparison.OrdinalIgnoreCase) || login.Equals("c:0(.s|true", StringComparison.OrdinalIgnoreCase))
        {
            return ("everyone", "");
        }
        return ("", "");
    }

    /// <summary>A person's email by their Microsoft Entra ID (with User.Read.All); null when Graph does not say.</summary>
    private async Task<string?> EmailAsync(KnowledgeSource source, string id, CancellationToken ct)
    {
        if (!_emails.TryGetValue(id, out var email))
        {
            var (status, body) = await GetAsync(source, $"{GraphUrl}/users/{Uri.EscapeDataString(id)}?$select=mail,userPrincipalName", ct);
            _emails[id] = email = status == 200 && body is JsonObject u
                ? (Str(u, "mail") is { Length: > 0 } mail ? mail : Str(u, "userPrincipalName")) is { } e && e.Contains('@', StringComparison.Ordinal) ? e : null
                : null;
        }
        return email;
    }

    /// <summary>A file's text, as the chat reads an attachment; null when it cannot be downloaded now.</summary>
    private async Task<string?> ContentAsync(KnowledgeSource source, string drive, string item, string name, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{GraphUrl}/drives/{Uri.EscapeDataString(drive)}/items/{Uri.EscapeDataString(item)}/content");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(source, ct));
            using var response = await http.CreateClient(Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
            {
                return null;
            }
            return FolderConnector.Read(name, await response.Content.ReadAsByteArrayAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>A site page's text: its title, description and text web parts, under its title.</summary>
    private async Task<string?> PageAsync(KnowledgeSource source, string site, string id, string title, CancellationToken ct)
    {
        var (status, body) = await GetAsync(source, $"{GraphUrl}/sites/{Uri.EscapeDataString(site)}/pages/{Uri.EscapeDataString(id)}/microsoft.graph.sitePage?$expand=canvasLayout", ct);
        if (status != 200 || body is not JsonObject page)
        {
            return null;
        }
        var html = new StringBuilder();
        if (Str(page, "description") is { Length: > 0 } description)
        {
            html.Append("<p>").Append(System.Net.WebUtility.HtmlEncode(description)).Append("</p>");
        }
        foreach (var part in InnerHtml(page["canvasLayout"]))
        {
            html.Append(part).Append('\n');
        }
        return $"# {title}\n\n{Html.Read("<html><body>" + html + "</body></html>").Text}";
    }

    /// <summary>The HTML of every text web part of a page's layout, in order.</summary>
    private static IEnumerable<string> InnerHtml(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["innerHtml"] is JsonValue v && v.TryGetValue<string>(out var html))
                {
                    yield return html;
                }
                foreach (var (_, child) in o)
                {
                    foreach (var inner in InnerHtml(child))
                    {
                        yield return inner;
                    }
                }
                break;
            case JsonArray a:
                foreach (var child in a)
                {
                    foreach (var inner in InnerHtml(child))
                    {
                        yield return inner;
                    }
                }
                break;
        }
    }

    /// <summary>The app's token (client credentials), kept while it lasts. Throws with Microsoft Entra's reason when it is refused.</summary>
    private async Task<string> TokenAsync(KnowledgeSource source, CancellationToken ct)
    {
        if (_token is not null && clock.GetUtcNow() < _expires)
        {
            return _token;
        }
        var secret = SourceSecret.Read(source, auth.Value.DataKey) ?? throw new KnowledgeException(SourceSecret.Problem(source, auth.Value.DataKey, Secret)!);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = source.Account?.Trim() ?? "", ["client_secret"] = secret, ["scope"] = "https://graph.microsoft.com/.default", ["grant_type"] = "client_credentials",
        });
        HttpResponseMessage response;
        try
        {
            response = await http.CreateClient(Client).PostAsync(new Uri($"{LoginUrl}/{Uri.EscapeDataString(source.Tenant?.Trim() ?? "")}/oauth2/v2.0/token"), form, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new KnowledgeException("Microsoft's sign-in (login.microsoftonline.com) could not be reached.");
        }
        using (response)
        {
            var body = Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
            if (!response.IsSuccessStatusCode || body?["access_token"] is not JsonValue token || !token.TryGetValue<string>(out var value))
            {
                // Entra's own words (AADSTS…), first line only: never the secret.
                var why = Str(body, "error_description").Split('\n')[0].Trim();
                throw new KnowledgeException($"Microsoft Entra refused the app ({(int)response.StatusCode}{(Str(body, "error") is { Length: > 0 } e ? " " + e : "")}){(why.Length > 0 ? ": " + (why.Length > 300 ? why[..300] + "…" : why) : ".")} Check the tenant, the client ID and the secret.");
            }
            var seconds = 3600;
            if (body["expires_in"] is JsonValue lasts && !lasts.TryGetValue(out seconds) && !int.TryParse(lasts.ToString(), CultureInfo.InvariantCulture, out seconds))
            {
                seconds = 3600;
            }
            (_token, _expires) = (value, clock.GetUtcNow().AddSeconds(Math.Max(60, seconds - 120)));
            return value;
        }
    }

    /// <summary>Every item of a listing, following Graph's next links; null (with its status) when a page of it could not be read.</summary>
    private async Task<(List<JsonObject>? Items, int Status)> ListAsync(KnowledgeSource source, string url, CancellationToken ct)
    {
        var items = new List<JsonObject>();
        for (var (next, n) = ((string?)url, 0); next is not null && n < MaxCalls; n++)
        {
            var (status, body) = await GetAsync(source, next, ct);
            if (status != 200 || body?["value"] is not JsonArray page)
            {
                return (null, status);
            }
            items.AddRange(page.OfType<JsonObject>());
            next = Link(body, "@odata.nextLink");
        }
        return (items, 200);
    }

    /// <summary>A GET of Graph as JSON with its status (0: it could not be reached); waits and tries again when Graph says it is busy.</summary>
    private async Task<(int Status, JsonNode? Body)> GetAsync(KnowledgeSource source, string url, CancellationToken ct, bool changes = false)
    {
        // Graph's own links only: the token goes nowhere else.
        if (!url.StartsWith(GraphUrl + "/", StringComparison.Ordinal))
        {
            return (0, null);
        }
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(source, ct));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (changes)
                {
                    // Files whose sharing changed come with the changes, so their readers are read again.
                    request.Headers.TryAddWithoutValidation("Prefer", "deltashowsharingchanges");
                }
                using var response = await http.CreateClient(Client).SendAsync(request, ct);
                if ((int)response.StatusCode is 429 or 503 && attempt < 3)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * (attempt + 1));
                    await Task.Delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait, clock, ct);
                    continue;
                }
                var text = await response.Content.ReadAsStringAsync(ct);
                return ((int)response.StatusCode, response.IsSuccessStatusCode ? Parse(text) : null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return (0, null);
            }
        }
    }

    private static string? Link(JsonNode? body, string name) =>
        body?[name] is JsonValue v && v.TryGetValue<string>(out var link) && link.StartsWith(GraphUrl + "/", StringComparison.Ordinal) ? link : null;

    private static JsonNode? Parse(string? text)
    {
        try
        {
            return string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Str(JsonNode? o, string name) => o is JsonObject obj && obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n.ToString("N0", CultureInfo.InvariantCulture)} {many}";

    private static string Said(int status) => status == 0 ? "Graph could not be reached" : $"Graph answered {status.ToString(CultureInfo.InvariantCulture)}";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]{1,99}$")]
    private static partial Regex Tenant();
}
