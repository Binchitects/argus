using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;

namespace Llm.Tests;

/// <summary>
/// Microsoft Graph and Entra's token endpoint for company knowledge: the app "app-id" of the tenant contoso.onmicrosoft.com
/// (secret <see cref="Secret"/>), the site Engineering at https://contoso.sharepoint.com/sites/eng with the libraries
/// Documents and Specs, files with their permissions, site pages, and delta queries: each change is numbered, and a
/// delta link carries the number it was made at, so the next query lists only what changed after it (sharing changes
/// only when asked for with Prefer: deltashowsharingchanges). Listings come two items a page.
/// </summary>
public sealed partial class FakeGraph : HttpMessageHandler
{
    public const string Tenant = "contoso.onmicrosoft.com";
    public const string ClientId = "app-id";
    public const string Secret = "graph-secret-for-tests";
    public const string SiteId = "contoso.sharepoint.com,site-1,web-1";
    private const string Graph = "https://graph.microsoft.com/v1.0";

    public sealed class Item
    {
        public required string Id { get; init; }
        public required string Drive { get; init; }
        public required string Name { get; set; }
        public required string Text { get; set; }
        public int Version { get; set; } = 1;
        /// <summary>The change it was last listed for: content, a rename, its sharing, or its deletion.</summary>
        public long Change { get; set; }
        public bool SharingOnly { get; set; }
        public bool Deleted { get; set; }
        public List<JsonObject> Grants { get; } = [];
    }

    public List<Item> Items { get; } = [];
    public List<(string Id, string Title, string Html, string ETag)> Pages { get; } = [];
    public List<(string Method, string PathAndQuery, string? Prefer)> Calls { get; } = [];
    /// <summary>Set: a delta query's second page answers 500 (a sync that fails half way).</summary>
    public bool FailSecondPage { get; set; }
    /// <summary>Set: delta links older than now answer 410 Gone (resyncRequired).</summary>
    public bool Expired { get; set; }
    private long _change = 1;

    public static JsonObject User(string email) => new() { ["grantedToV2"] = new JsonObject { ["user"] = new JsonObject { ["id"] = "u-" + email, ["displayName"] = email, ["email"] = email } }, ["roles"] = new JsonArray("read") };

    /// <summary>A person named only by their Entra ID, as Graph often gives them: their email comes from /users.</summary>
    public static JsonObject UserById(string id) => new() { ["grantedToV2"] = new JsonObject { ["user"] = new JsonObject { ["id"] = id, ["displayName"] = "Someone" } }, ["roles"] = new JsonArray("write") };

    public static JsonObject Group(string id, string name) => new() { ["grantedToV2"] = new JsonObject { ["group"] = new JsonObject { ["id"] = id, ["displayName"] = name } }, ["roles"] = new JsonArray("read") };

    public static JsonObject SiteGroup(string name) => new() { ["grantedToV2"] = new JsonObject { ["siteGroup"] = new JsonObject { ["id"] = "3", ["displayName"] = name, ["loginName"] = name } }, ["roles"] = new JsonArray("owner") };

    public static JsonObject OrganizationLink() => new() { ["link"] = new JsonObject { ["scope"] = "organization", ["type"] = "view" }, ["roles"] = new JsonArray("read") };

    public static JsonObject EveryoneInTheCompany() => new()
    {
        ["grantedToV2"] = new JsonObject { ["siteUser"] = new JsonObject { ["id"] = "9", ["displayName"] = "Everyone except external users", ["loginName"] = "c:0-.f|rolemanager|spo-grid-all-users/tenant-guid" } },
        ["roles"] = new JsonArray("read"),
    };

    /// <summary>
    /// Documents: deploy.md (shared with alice@example.test and the Entra group Engineering), salaries.txt (only bob, by his Entra ID),
    /// team.md (the site's Members group, which Graph cannot list), handbook.md (everyone in the company, and an organisation link), logo.png
    /// (not read). Specs: api.md (Engineering). A site page, Welcome.
    /// </summary>
    public FakeGraph()
    {
        Add("docs", "f1", "deploy.md", "# Deploy\n\n## Rollback\n\nTo roll back a SharePoint deploy, run the undo pipeline with the release tag.", User("alice@example.test"), Group("g-eng", "Engineering"));
        Add("docs", "f2", "salaries.txt", "Salaries of the codec team are reviewed every March by the board.", UserById("entra-bob"));
        Add("docs", "f3", "team.md", "# Team\n\nThe codec team meets every Thursday in the blue room.", SiteGroup("Engineering Members"));
        Add("docs", "f4", "handbook.md", "# Handbook\n\nThe office closes at seven on Fridays.", EveryoneInTheCompany(), OrganizationLink());
        Add("docs", "f5", "logo.png", "not text");
        Add("specs", "f6", "api.md", "# API\n\nThe ingest API takes protobuf frames on port 7000.", Group("g-eng", "Engineering"));
        Pages.Add(("p1", "Welcome", "<h2>Welcome</h2><p>The engineering site's front page lists the on-call rota.</p>", "\"etag-1\""));
    }

    public Item Add(string drive, string id, string name, string text, params JsonObject[] grants)
    {
        var item = new Item { Id = id, Drive = drive, Name = name, Text = text, Change = _change++ };
        item.Grants.AddRange(grants);
        Items.Add(item);
        return item;
    }

    /// <summary>A file's text changed: a new version, listed in the next changes.</summary>
    public void Edit(string id, string text)
    {
        var item = Items.Single(i => i.Id == id);
        (item.Text, item.Version, item.Change, item.SharingOnly) = (text, item.Version + 1, _change++, false);
    }

    /// <summary>A file's sharing changed: listed in the next changes only to those who ask for sharing changes.</summary>
    public void Share(string id, params JsonObject[] grants)
    {
        var item = Items.Single(i => i.Id == id);
        item.Grants.Clear();
        item.Grants.AddRange(grants);
        (item.Change, item.SharingOnly) = (_change++, true);
    }

    public void Delete(string id)
    {
        var item = Items.Single(i => i.Id == id);
        (item.Deleted, item.Change, item.SharingOnly) = (true, _change++, false);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, JsonNode json) => new(code) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };

    private static JsonObject List(List<JsonNode> all, int start, Func<int, string> next, string? delta = null)
    {
        var json = new JsonObject { ["value"] = new JsonArray([.. all.Skip(start).Take(2).Select(n => n.DeepClone())]) };
        if (start + 2 < all.Count)
        {
            json["@odata.nextLink"] = next(start + 2);
        }
        else if (delta is not null)
        {
            json["@odata.deltaLink"] = delta;
        }
        return json;
    }

    private JsonObject ItemJson(Item i) => i.Deleted
        ? new JsonObject { ["id"] = i.Id, ["deleted"] = new JsonObject { ["state"] = "deleted" } }
        : new JsonObject
        {
            ["id"] = i.Id, ["name"] = i.Name, ["size"] = Encoding.UTF8.GetByteCount(i.Text), ["file"] = new JsonObject { ["mimeType"] = "text/plain" },
            ["cTag"] = $"\"c:{{{i.Id}}},{i.Version}\"", ["eTag"] = $"\"{{{i.Id}}},{i.Version}\"", ["lastModifiedDateTime"] = "2026-10-01T10:00:00Z",
            ["webUrl"] = $"https://contoso.sharepoint.com/sites/eng/{(i.Drive == "docs" ? "Shared%20Documents" : "Specs")}/{Uri.EscapeDataString(i.Name)}",
            ["parentReference"] = new JsonObject { ["driveId"] = i.Drive },
        };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var prefer = request.Headers.TryGetValues("Prefer", out var p) ? string.Join(",", p) : null;
        lock (Calls)
        {
            Calls.Add((request.Method.Method, uri.PathAndQuery, prefer));
        }
        if (uri.Host == "login.microsoftonline.com")
        {
            var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
            return uri.AbsolutePath == $"/{Tenant}/oauth2/v2.0/token" && form["client_id"] == ClientId && form["client_secret"] == Secret && form["grant_type"] == "client_credentials"
                && form["scope"] == "https://graph.microsoft.com/.default"
                ? Json(HttpStatusCode.OK, new JsonObject { ["token_type"] = "Bearer", ["expires_in"] = 3599, ["access_token"] = "graph-token" })
                : Json(HttpStatusCode.Unauthorized, new JsonObject
                {
                    ["error"] = "invalid_client",
                    ["error_description"] = "AADSTS7000215: Invalid client secret provided. Ensure the secret being sent in the request is the client secret value.\r\nTrace ID: 1234",
                });
        }
        if (uri.Host != "graph.microsoft.com" || request.Headers.Authorization?.ToString() != "Bearer graph-token")
        {
            return Json(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = new JsonObject { ["code"] = "InvalidAuthenticationToken" } });
        }
        var path = Uri.UnescapeDataString(uri.AbsolutePath)["/v1.0".Length..];
        var query = HttpUtility.ParseQueryString(uri.Query);
        var start = int.TryParse(query["start"], out var s) ? s : 0;
        var notFound = Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = new JsonObject { ["code"] = "itemNotFound" } });
        if (path == "/sites/contoso.sharepoint.com:/sites/eng")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["id"] = SiteId, ["name"] = "eng", ["displayName"] = "Engineering", ["webUrl"] = "https://contoso.sharepoint.com/sites/eng" });
        }
        if (path == "/sites/contoso.sharepoint.com:/sites/hr")
        {
            return Json(HttpStatusCode.Forbidden, new JsonObject { ["error"] = new JsonObject { ["code"] = "accessDenied" } });
        }
        if (path == $"/sites/{SiteId}/drives")
        {
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["value"] = new JsonArray(
                    new JsonObject { ["id"] = "docs", ["name"] = "Documents", ["driveType"] = "documentLibrary", ["webUrl"] = "https://contoso.sharepoint.com/sites/eng/Shared%20Documents" },
                    new JsonObject { ["id"] = "specs", ["name"] = "Specs", ["driveType"] = "documentLibrary", ["webUrl"] = "https://contoso.sharepoint.com/sites/eng/Specs" }),
            });
        }
        if (path == $"/sites/{SiteId}/pages/microsoft.graph.sitePage")
        {
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["value"] = new JsonArray([.. Pages.Select(x => (JsonNode)new JsonObject
                {
                    ["id"] = x.Id, ["name"] = x.Title + ".aspx", ["title"] = x.Title, ["eTag"] = x.ETag, ["webUrl"] = $"https://contoso.sharepoint.com/sites/eng/SitePages/{x.Title}.aspx",
                })]),
            });
        }
        if (PagePath().Match(path) is { Success: true } pm && Pages.FirstOrDefault(x => x.Id == pm.Groups[1].Value) is { Id: not null } page)
        {
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["id"] = page.Id, ["title"] = page.Title, ["description"] = "The front page.",
                ["canvasLayout"] = new JsonObject
                {
                    ["horizontalSections"] = new JsonArray(new JsonObject
                    {
                        ["columns"] = new JsonArray(new JsonObject { ["webparts"] = new JsonArray(new JsonObject { ["@odata.type"] = "#microsoft.graph.textWebPart", ["innerHtml"] = page.Html }) }),
                    }),
                },
            });
        }
        if (path == "/users/entra-bob")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["mail"] = "bob@example.test", ["userPrincipalName"] = "bob@contoso.onmicrosoft.com" });
        }
        if (DeltaPath().Match(path) is { Success: true } dm)
        {
            var drive = dm.Groups[1].Value;
            var since = long.TryParse(query["token"], out var t) ? t : 0;
            if (since > 0 && Expired)
            {
                return Json(HttpStatusCode.Gone, new JsonObject { ["error"] = new JsonObject { ["code"] = "resyncRequired" } });
            }
            if (FailSecondPage && start > 0)
            {
                return Json(HttpStatusCode.InternalServerError, new JsonObject { ["error"] = new JsonObject { ["code"] = "generalException" } });
            }
            var sharing = prefer?.Contains("deltashowsharingchanges", StringComparison.Ordinal) == true;
            // The whole library (and its root folder) at first; then what changed since the token, its sharing only when asked for.
            var listed = Items.Where(i => i.Drive == drive && (since == 0 ? !i.Deleted : i.Change > since && (sharing || !i.SharingOnly))).Select(i => (JsonNode)ItemJson(i)).ToList();
            if (since == 0)
            {
                listed.Insert(0, new JsonObject { ["id"] = "root-" + drive, ["name"] = "root", ["folder"] = new JsonObject { ["childCount"] = 5 }, ["root"] = new JsonObject() });
            }
            var token = since == 0 ? _change - 1 : since;
            token = Math.Max(token, _change - 1);
            return Json(HttpStatusCode.OK, List(listed, start, n => since == 0 ? $"{Graph}/drives/{drive}/root/delta?start={n}" : $"{Graph}/drives/{drive}/root/delta?token={since}&start={n}", $"{Graph}/drives/{drive}/root/delta?token={token.ToString(CultureInfo.InvariantCulture)}"));
        }
        if (ItemPath().Match(path) is { Success: true } im && Items.FirstOrDefault(i => i.Drive == im.Groups[1].Value && i.Id == im.Groups[2].Value && !i.Deleted) is { } item)
        {
            return im.Groups[3].Value == "permissions"
                ? Json(HttpStatusCode.OK, List([.. item.Grants], start, n => $"{Graph}/drives/{item.Drive}/items/{item.Id}/permissions?start={n}"))
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(item.Text)) };
        }
        return notFound;
    }

    [GeneratedRegex(@"^/drives/([^/]+)/root/delta$")]
    private static partial Regex DeltaPath();

    [GeneratedRegex(@"^/drives/([^/]+)/items/([^/]+)/(permissions|content)$")]
    private static partial Regex ItemPath();

    [GeneratedRegex(@"^/sites/[^/]+/pages/([^/]+)/microsoft\.graph\.sitePage$")]
    private static partial Regex PagePath();
}
