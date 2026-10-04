using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.Scim;

/// <summary>An answer in SCIM's media type, with a Location for a resource just made.</summary>
public sealed class ScimResult(JsonNode body, int status = StatusCodes.Status200OK, string? location = null) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = status;
        httpContext.Response.ContentType = ScimEndpoints.MediaType;
        if (location is not null)
        {
            httpContext.Response.Headers.Location = location;
        }
        await httpContext.Response.WriteAsync(body.ToJsonString(), httpContext.RequestAborted);
    }
}

/// <summary>
/// SCIM 2.0 (RFC 7643, 7644) for the company's identity provider: people and groups
/// made, changed and deactivated from there. One bearer token (Admin → Settings →
/// Company sign-in). Local admins are not seen here: they stay a way in of their own.
/// </summary>
public static class ScimEndpoints
{
    public const string Base = "/scim/v2";
    public const string MediaType = "application/scim+json";
    public const string ListSchema = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    public const string PatchSchema = "urn:ietf:params:scim:api:messages:2.0:PatchOp";
    private const string ErrorSchema = "urn:ietf:params:scim:api:messages:2.0:Error";

    /// <summary>Attribute names ignore case (RFC 7643 2.1), and so does this.</summary>
    private static readonly JsonNodeOptions Lenient = new() { PropertyNameCaseInsensitive = true };

    public static void MapScim(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup(Base).AddEndpointFilter(AuthorizeAsync);
        g.MapGet("/ServiceProviderConfig", (IOptions<AuthOptions> auth) => new ScimResult(ScimSchemas.ServiceProviderConfig(auth.Value.Origin + Base)));
        g.MapGet("/ResourceTypes", (IOptions<AuthOptions> auth) => new ScimResult(List(ScimSchemas.ResourceTypes(auth.Value.Origin + Base), null, 1)));
        g.MapGet("/ResourceTypes/{id}", (string id, IOptions<AuthOptions> auth) =>
            ScimSchemas.ResourceTypes(auth.Value.Origin + Base).FirstOrDefault(r => string.Equals(r["id"]!.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase)) is { } r
                ? new ScimResult(r) : Error(404, $"There is no resource type {id}."));
        g.MapGet("/Schemas", (IOptions<AuthOptions> auth) => new ScimResult(List(ScimSchemas.All(auth.Value.Origin + Base), null, 1)));
        g.MapGet("/Schemas/{id}", (string id, IOptions<AuthOptions> auth) =>
            ScimSchemas.All(auth.Value.Origin + Base).FirstOrDefault(s => s["id"]!.GetValue<string>() == id) is { } s
                ? new ScimResult(s) : Error(404, $"There is no schema {id}."));

        g.MapGet("/Users", ScimUsers.ListAsync);
        g.MapPost("/Users", ScimUsers.CreateAsync);
        g.MapGet("/Users/{id}", ScimUsers.GetAsync);
        g.MapPut("/Users/{id}", ScimUsers.ReplaceAsync);
        g.MapPatch("/Users/{id}", ScimUsers.PatchAsync);
        g.MapDelete("/Users/{id}", ScimUsers.DeleteAsync);

        g.MapGet("/Groups", ScimGroups.ListAsync);
        g.MapPost("/Groups", ScimGroups.CreateAsync);
        g.MapGet("/Groups/{id}", ScimGroups.GetAsync);
        g.MapPut("/Groups/{id}", ScimGroups.ReplaceAsync);
        g.MapPatch("/Groups/{id}", ScimGroups.PatchAsync);
        g.MapDelete("/Groups/{id}", ScimGroups.DeleteAsync);
        g.MapFallback(() => Error(404, "There is no such SCIM endpoint here."));
    }

    /// <summary>The bearer token, checked against the stored hash. The audit log names the caller "scim".</summary>
    private static async ValueTask<object?> AuthorizeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var ctx = context.HttpContext;
        var header = ctx.Request.Headers.Authorization.ToString();
        var tokens = ctx.RequestServices.GetRequiredService<ScimTokens>();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || !await tokens.VerifyAsync(header[7..].Trim(), ctx.RequestAborted))
        {
            ctx.Response.Headers.WWWAuthenticate = "Bearer realm=\"scim\"";
            return Error(401, "A valid SCIM token is needed. Make one in Admin → Settings → Company sign-in.");
        }
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "scim")], "scim"));
        return await next(context);
    }

    public static ScimResult Error(int status, string detail, string? scimType = null)
    {
        var body = new JsonObject
        {
            ["schemas"] = new JsonArray(ErrorSchema),
            ["status"] = status.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["detail"] = detail,
        };
        if (scimType is not null)
        {
            body["scimType"] = scimType;
        }
        return new ScimResult(body, status);
    }

    /// <summary>A ListResponse: this page of resources, and how many match in all.</summary>
    public static JsonObject List(IReadOnlyList<JsonObject> page, int? total, int startIndex) => new()
    {
        ["schemas"] = new JsonArray(ListSchema),
        ["totalResults"] = total ?? page.Count,
        ["startIndex"] = startIndex,
        ["itemsPerPage"] = page.Count,
        ["Resources"] = new JsonArray([.. page]),
    };

    /// <summary>startIndex is 1-based (RFC 7644 3.4.2.4); count is at most 500, and 0 asks only for the total.</summary>
    public static (int Start, int Count) Page(HttpRequest req)
    {
        var start = int.TryParse(req.Query["startIndex"], out var s) && s > 1 ? s : 1;
        var count = int.TryParse(req.Query["count"], out var c) ? Math.Clamp(c, 0, ScimSchemas.MaxResults) : 100;
        return (start, count);
    }

    /// <summary>The request's JSON object; null when it is not one.</summary>
    public static async Task<JsonObject?> BodyAsync(HttpRequest req)
    {
        try
        {
            return await JsonNode.ParseAsync(req.Body, Lenient, cancellationToken: req.HttpContext.RequestAborted) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ScimResult BadBody() => Error(400, "The body is not a JSON object.", "invalidSyntax");

    /// <summary>A string attribute; null when absent or not text.</summary>
    public static string? Text(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>A boolean, also as the text "True" or "false" (Entra sends them so).</summary>
    public static bool? Bool(JsonNode? node) => node is JsonValue v
        ? v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetValue<string>(), out var b) => b,
            _ => null,
        }
        : null;

    /// <summary>The operations of a PatchOp request; null when the body is not one.</summary>
    public static List<(string Op, ScimPath? Path, JsonNode? Value)>? PatchOperations(JsonObject body)
    {
        if (body["Operations"] is not JsonArray ops)
        {
            return null;
        }
        var list = new List<(string, ScimPath?, JsonNode?)>();
        foreach (var node in ops)
        {
            if (node is not JsonObject op)
            {
                return null;
            }
            var name = Text(op["op"])?.ToLowerInvariant();
            if (name is not ("add" or "replace" or "remove"))
            {
                return null;
            }
            var path = Text(op["path"]);
            var parsed = ScimFilter.ParsePath(path);
            if (path is not null && parsed is null)
            {
                return null;
            }
            list.Add((name, parsed, op["value"]));
        }
        return list;
    }
}
