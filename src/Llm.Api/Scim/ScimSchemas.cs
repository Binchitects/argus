using System.Text.Json.Nodes;

namespace Llm.Api.Scim;

/// <summary>What this SCIM server supports, as RFC 7643 sections 5-7 describe it: the documents a client reads first.</summary>
public static class ScimSchemas
{
    public const int MaxResults = 500;
    private const string SchemaSchema = "urn:ietf:params:scim:schemas:core:2.0:Schema";
    private const string ResourceTypeSchema = "urn:ietf:params:scim:schemas:core:2.0:ResourceType";

    public static JsonObject ServiceProviderConfig(string baseUrl) => new()
    {
        ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig"),
        ["patch"] = new JsonObject { ["supported"] = true },
        ["bulk"] = new JsonObject { ["supported"] = false, ["maxOperations"] = 0, ["maxPayloadSize"] = 0 },
        ["filter"] = new JsonObject { ["supported"] = true, ["maxResults"] = MaxResults },
        ["changePassword"] = new JsonObject { ["supported"] = false },
        ["sort"] = new JsonObject { ["supported"] = false },
        ["etag"] = new JsonObject { ["supported"] = false },
        ["authenticationSchemes"] = new JsonArray(new JsonObject
        {
            ["type"] = "oauthbearertoken",
            ["name"] = "Bearer token",
            ["description"] = "The token made in Admin → Settings → Company sign-in.",
            ["primary"] = true,
        }),
        ["meta"] = new JsonObject { ["resourceType"] = "ServiceProviderConfig", ["location"] = baseUrl + "/ServiceProviderConfig" },
    };

    public static IReadOnlyList<JsonObject> ResourceTypes(string baseUrl) =>
    [
        ResourceType("User", "/Users", ScimFilter.UserSchema, baseUrl),
        ResourceType("Group", "/Groups", ScimFilter.GroupSchema, baseUrl),
    ];

    private static JsonObject ResourceType(string name, string endpoint, string schema, string baseUrl) => new()
    {
        ["schemas"] = new JsonArray(ResourceTypeSchema),
        ["id"] = name,
        ["name"] = name,
        ["endpoint"] = endpoint,
        ["schema"] = schema,
        ["meta"] = new JsonObject { ["resourceType"] = "ResourceType", ["location"] = $"{baseUrl}/ResourceTypes/{name}" },
    };

    public static IReadOnlyList<JsonObject> All(string baseUrl) =>
    [
        Schema(ScimFilter.UserSchema, "User", "A person who can use the service.", baseUrl,
            Attr("userName", required: true, uniqueness: "server", description: "The username here: an email-like value gives its part before the @."),
            Attr("name", "complex", sub: [Attr("formatted"), Attr("givenName"), Attr("familyName")]),
            Attr("displayName"),
            Attr("emails", "complex", multi: true, required: true, sub: [Attr("value"), Attr("type"), Attr("primary", "boolean")]),
            Attr("active", "boolean", description: "False disables the person: signed out, API keys blocked."),
            Attr("groups", "complex", multi: true, mutability: "readOnly", sub: [Attr("value", mutability: "readOnly"), Attr("display", mutability: "readOnly"), Attr("$ref", "reference", mutability: "readOnly")])),
        Schema(ScimFilter.GroupSchema, "Group", "A group that access rules (tools, models) can name.", baseUrl,
            Attr("displayName", required: true, uniqueness: "server"),
            Attr("members", "complex", multi: true, sub: [Attr("value", mutability: "immutable"), Attr("display", mutability: "readOnly"), Attr("$ref", "reference", mutability: "immutable"), Attr("type", mutability: "immutable")])),
    ];

    private static JsonObject Schema(string id, string name, string description, string baseUrl, params JsonObject[] attributes) => new()
    {
        ["schemas"] = new JsonArray(SchemaSchema),
        ["id"] = id,
        ["name"] = name,
        ["description"] = description,
        ["attributes"] = new JsonArray([.. attributes]),
        ["meta"] = new JsonObject { ["resourceType"] = "Schema", ["location"] = $"{baseUrl}/Schemas/{id}" },
    };

    private static JsonObject Attr(string name, string type = "string", bool multi = false, bool required = false, string mutability = "readWrite",
        string uniqueness = "none", string? description = null, JsonObject[]? sub = null)
    {
        var a = new JsonObject
        {
            ["name"] = name,
            ["type"] = type,
            ["multiValued"] = multi,
            ["required"] = required,
            ["caseExact"] = false,
            ["mutability"] = mutability,
            ["returned"] = "default",
            ["uniqueness"] = uniqueness,
        };
        if (description is not null)
        {
            a["description"] = description;
        }
        if (sub is not null)
        {
            a["subAttributes"] = new JsonArray([.. sub]);
        }
        return a;
    }
}
