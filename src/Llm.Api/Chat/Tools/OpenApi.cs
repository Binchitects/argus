using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Core.Chat;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Llm.Api.Chat.Tools;

/// <summary>
/// An OpenAPI 3 document (JSON or YAML) as chat functions, as GPT actions are: an operation
/// per function, its path, query and header parameters and its JSON body as the arguments
/// (references resolved), and the call made with them. Operations that change something
/// (anything but GET, HEAD and OPTIONS) ask the person first.
/// </summary>
public static partial class OpenApi
{
    public sealed record Param(string Name, string In);

    public sealed record Operation(string Function, string Method, string Path, JsonObject Definition, List<Param> Parameters, string BodyName)
    {
        public bool Writes => Method is not ("GET" or "HEAD" or "OPTIONS");
    }

    public sealed class SpecException(string message) : Exception(message);

    private const int MaxResultChars = 100_000;
    private static readonly string[] Methods = ["get", "post", "put", "patch", "delete"];
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal) { "example", "examples", "xml", "externalDocs", "deprecated", "discriminator" };

    /// <summary>The document, from JSON or YAML.</summary>
    public static JsonObject Parse(string text)
    {
        text = text.Trim();
        try
        {
            if (text.StartsWith('{'))
            {
                return JsonNode.Parse(text) as JsonObject ?? throw new SpecException("The document is not a JSON object.");
            }
            var yaml = new YamlStream();
            yaml.Load(new StringReader(text));
            return (yaml.Documents.Count > 0 ? FromYaml(yaml.Documents[0].RootNode) : null) as JsonObject ?? throw new SpecException("The document is not a YAML mapping.");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or YamlException or ArgumentException or InvalidCastException)
        {
            throw new SpecException($"The OpenAPI document does not parse: {ex.Message}");
        }
    }

    /// <summary>Where its servers say it is, made absolute against where the document came from.</summary>
    public static string? ServerUrl(JsonObject doc, Uri? from)
    {
        var url = (doc["servers"] as JsonArray)?.FirstOrDefault()?["url"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }
        var abs = Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u
            : from is not null && Uri.TryCreate(from, url, out var r) ? r : null;
        return abs?.ToString();
    }

    public static List<Operation> Operations(JsonObject doc, string prefix)
    {
        if (doc["openapi"] is null && doc["swagger"] is not null)
        {
            throw new SpecException("This is a Swagger 2 document: convert it to OpenAPI 3 first (for example with swagger2openapi).");
        }
        var ops = new List<Operation>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, raw) in doc["paths"] as JsonObject ?? [])
        {
            if (Resolve(doc, raw, 0) is not JsonObject item)
            {
                continue;
            }
            foreach (var method in Methods)
            {
                if (item[method] is not JsonObject op)
                {
                    continue;
                }
                var id = op["operationId"]?.GetValue<string>() is { Length: > 0 } given ? given : $"{method}_{path}";
                var name = prefix + Snake(id);
                name = name[..Math.Min(64, name.Length)];
                for (var n = 2; !taken.Add(name); n++)
                {
                    name = $"{name[..Math.Min(60, name.Length)]}_{n}";
                }
                var properties = new JsonObject();
                var required = new JsonArray();
                var parameters = new List<Param>();
                foreach (var p in ((item["parameters"] as JsonArray) ?? []).Concat((op["parameters"] as JsonArray) ?? []).OfType<JsonObject>())
                {
                    var where = p["in"]?.GetValue<string>();
                    if (p["name"]?.GetValue<string>() is not { Length: > 0 } pname || where is not ("path" or "query" or "header"))
                    {
                        continue;
                    }
                    var schema = p["schema"] as JsonObject ?? new JsonObject { ["type"] = "string" };
                    if (p["description"]?.GetValue<string>() is { Length: > 0 } about)
                    {
                        schema["description"] = Short(about, 200);
                    }
                    properties[pname] = schema.DeepClone();
                    parameters.RemoveAll(x => x.Name == pname);
                    parameters.Add(new Param(pname, where));
                    if (where == "path" || p["required"]?.GetValue<bool>() == true)
                    {
                        if (!required.Any(r => r?.GetValue<string>() == pname))
                        {
                            required.Add(pname);
                        }
                    }
                }
                var bodyName = properties.ContainsKey("body") ? "request_body" : "body";
                if (op["requestBody"] is JsonObject body && (body["content"] as JsonObject)?.FirstOrDefault(c => c.Key.Contains("json", StringComparison.OrdinalIgnoreCase)).Value?["schema"] is JsonObject bodySchema)
                {
                    properties[bodyName] = bodySchema.DeepClone();
                    if (body["required"]?.GetValue<bool>() == true)
                    {
                        required.Add(bodyName);
                    }
                }
                var summary = op["summary"]?.GetValue<string>() ?? op["description"]?.GetValue<string>() ?? id;
                var definition = Schema.Function(name, $"{Short(summary, 300)} ({method.ToUpperInvariant()} {path})", properties);
                definition["function"]!["parameters"]!["required"] = required;
                ops.Add(new Operation(name, method.ToUpperInvariant(), path, definition, parameters, bodyName));
            }
        }
        return ops;
    }

    /// <summary>The call, made with the model's arguments: the status and what came back, cut to fit.</summary>
    public static async Task<ToolResult> CallAsync(HttpClient http, string baseUrl, Operation op, JsonObject args, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        var path = op.Path;
        var query = new List<string>();
        foreach (var p in op.Parameters)
        {
            if (args[p.Name] is not { } value)
            {
                if (p.In == "path")
                {
                    return new ToolResult($"{p.Name} is required.", IsError: true);
                }
                continue;
            }
            var values = value is JsonArray list ? list.Select(Text) : [Text(value)];
            switch (p.In)
            {
                case "path":
                    path = path.Replace("{" + p.Name + "}", Uri.EscapeDataString(Text(value)), StringComparison.Ordinal);
                    break;
                case "query":
                    query.AddRange(values.Select(v => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(v)}"));
                    break;
            }
        }
        var url = baseUrl.TrimEnd('/') + path + (query.Count > 0 ? "?" + string.Join('&', query) : "");
        using var request = new HttpRequestMessage(new HttpMethod(op.Method), url);
        foreach (var p in op.Parameters.Where(p => p.In == "header" && args[p.Name] is not null))
        {
            request.Headers.TryAddWithoutValidation(p.Name, Text(args[p.Name]!));
        }
        foreach (var (name, value) in headers)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (args[op.BodyName] is { } body)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex) when (ServerTls.IsCertificateError(ex))
        {
            return new ToolResult(ServerTls.Untrusted(new Uri(url).Host, "Read it"), IsError: true);
        }
        catch (HttpRequestException ex) when (ServerTls.HandshakeFailed(new Uri(url).Host, ex) is { } handshake)
        {
            return new ToolResult(handshake, IsError: true);
        }
        catch (HttpRequestException ex)
        {
            return new ToolResult($"{new Uri(url).Host} could not be reached: {ex.Message}", IsError: true);
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (text.Length > MaxResultChars)
            {
                text = text[..MaxResultChars] + "\n[cut to fit]";
            }
            return new ToolResult($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n{text}", IsError: (int)response.StatusCode >= 400);
        }
    }

    private static string Text(JsonNode? node) => node is null ? "" : node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node.ToJsonString();

    /// <summary>A copy with every local reference ($ref "#/...") put in place, examples and the like left out; a cycle stops after 8 steps.</summary>
    private static JsonNode? Resolve(JsonObject doc, JsonNode? node, int depth)
    {
        switch (node)
        {
            case JsonObject o when o["$ref"]?.GetValue<string>() is { } reference && reference.StartsWith("#/", StringComparison.Ordinal):
                if (depth >= 8)
                {
                    return new JsonObject { ["type"] = "object" };
                }
                JsonNode? target = doc;
                foreach (var part in reference[2..].Split('/'))
                {
                    target = target?[part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)];
                }
                return Resolve(doc, target, depth + 1);
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (key, value) in o)
                {
                    if (!Noise.Contains(key))
                    {
                        copy[key] = Resolve(doc, value, depth);
                    }
                }
                return copy;
            case JsonArray a:
                return new JsonArray([.. a.Select(x => Resolve(doc, x, depth))]);
            default:
                return node?.DeepClone();
        }
    }

    private static JsonNode? FromYaml(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(kv => KeyValuePair.Create(((YamlScalarNode)kv.Key).Value ?? "", FromYaml(kv.Value)))),
        YamlSequenceNode seq => new JsonArray([.. seq.Children.Select(FromYaml)]),
        YamlScalarNode { Style: ScalarStyle.Plain } s => s.Value switch
        {
            null or "" or "~" or "null" or "Null" or "NULL" => null,
            "true" or "True" or "TRUE" => JsonValue.Create(true),
            "false" or "False" or "FALSE" => JsonValue.Create(false),
            var v when long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => JsonValue.Create(l),
            var v when double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
            var v => JsonValue.Create(v),
        },
        YamlScalarNode s => JsonValue.Create(s.Value ?? ""),
        _ => null,
    };

    private static string Short(string text, int max)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>"listPets" -> "list_pets"; "get_/pets/{id}" -> "get_pets_id".</summary>
    private static string Snake(string id) => NotWord().Replace(Camel().Replace(id, "$1_$2"), "_").Trim('_').ToLowerInvariant() is { Length: > 0 } s ? s : "call";

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex Camel();

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NotWord();
}

/// <summary>An API an admin added by its OpenAPI document: its operations become a tool in the chat.</summary>
public sealed class OpenApiTool(McpServer server, HttpClient http, string? dataKey, TimeSpan callTimeout) : IServerTool
{
    public McpServer Server => server;
    public string Id => McpServerTool.Prefix + server.Id;
    public string Title => server.Name;
    public string Description => server.Description ?? $"Calls the {server.Name} API at {server.Url}.";
    public string Icon => "plug";
    public string Slug => McpServerTool.Slugify(server.Name);

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>Its operations, as functions.</summary>
    public List<OpenApi.Operation> Operations() => OpenApi.Operations(OpenApi.Parse(server.Spec ?? ""), Slug + "__");

    /// <summary>Each person's own account, for a plugin whose calls go as the person.</summary>
    public Plugins.PersonCredentials? People { get; init; }

    public async Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct)
    {
        var ops = Operations().ToDictionary(o => o.Function, StringComparer.Ordinal);
        var headers = await McpServerTool.PersonHeadersAsync(server, dataKey, People, context, ct);
        var prefix = Slug + "__";
        return new LocalRun(new JsonArray([.. ops.Values.Select(o => (JsonNode)o.Definition.DeepClone())]),
            $"{server.Name} is an API: a call that changes something waits for the person to allow it.",
            async (function, args, token) =>
            {
                if (!ops.TryGetValue(function, out var op))
                {
                    return new ToolResult($"There is no function {function}.", IsError: true);
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(server.CallTimeoutMinutes is { } m ? TimeSpan.FromMinutes(m) : callTimeout);
                try
                {
                    return await OpenApi.CallAsync(http, server.Url, op, args, headers, timeout.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return new ToolResult($"{server.Name} did not answer in time.", IsError: true);
                }
            })
        {
            // Anything but a read asks first; so does what a plugin names as a write.
            AsksFirst = function => ops.TryGetValue(function, out var op) && (op.Writes || server.Writes.Contains(function[prefix.Length..])),
        };
    }
}
