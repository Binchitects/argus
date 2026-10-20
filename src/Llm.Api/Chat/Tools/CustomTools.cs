using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Settings;
using Llm.Core.Chat;

namespace Llm.Api.Chat.Tools;

/// <summary>One parameter of an admin's own tool.</summary>
public sealed record CustomParameter(string Name, string Type, string? Description, bool Required);

/// <summary>An admin's own tool as the form sends it.</summary>
public sealed record CustomToolRequest(
    string Name, string Kind, string? Title = null, string? Description = null, List<CustomParameter>? Parameters = null,
    string? Method = null, string? UrlTemplate = null, Dictionary<string, string>? Headers = null, string? BodyTemplate = null, string? ResultPath = null,
    bool AskFirst = false, string? Model = null, string? PromptTemplate = null, string? SystemPrompt = null);

/// <summary>
/// Tools an admin makes without code (Admin → Tools → New tool): an HTTP request to an address the admin fixed, the model's
/// arguments filling only its path, query or body; or a prompt run on a model the admin chose. Each is a chat tool of its
/// own ("custom:{id}"), held by the same rules as the others.
/// </summary>
public static partial class CustomTools
{
    public const string Prefix = "custom:";
    public const string HttpClientName = "custom-tools";
    public const int MaxResultChars = 20_000;
    private const int MaxResponseBytes = 1024 * 1024;

    public static IReadOnlyList<CustomParameter> Parameters(string json) =>
        JsonSerializer.Deserialize<List<CustomParameter>>(json, JsonSerializerOptions.Web) ?? [];

    /// <summary>The tool's one function, as the model sees it.</summary>
    public static JsonObject Function(CustomTool t) => Schema.Function(t.Name, t.Description,
        new JsonObject(Parameters(t.ParametersJson).Select(p => KeyValuePair.Create(p.Name, (JsonNode?)new JsonObject
        {
            ["type"] = p.Type is "number" or "boolean" ? p.Type : "string", ["description"] = p.Description ?? p.Name,
        }))),
        [.. Parameters(t.ParametersJson).Where(p => p.Required).Select(p => p.Name)]);

    /// <summary>What is wrong with the form; null when it can be saved.</summary>
    public static string? Invalid(CustomToolRequest r)
    {
        if (!FunctionName().IsMatch(r.Name ?? ""))
        {
            return "The name is how the model calls it: lowercase letters, digits and underscores, starting with a letter, up to 60 (lookup_order).";
        }
        if (string.IsNullOrWhiteSpace(r.Description))
        {
            return "Say what it does: the model reads it to know when to call it.";
        }
        var parameters = r.Parameters ?? [];
        if (parameters.Any(p => !ParameterName().IsMatch(p.Name ?? "")) || parameters.GroupBy(p => p.Name).Any(g => g.Count() > 1))
        {
            return "Each parameter needs a name of its own: letters, digits and underscores.";
        }
        if (r.Kind == CustomTool.Http)
        {
            if (r.Method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE"))
            {
                return "The method is GET, POST, PUT, PATCH or DELETE.";
            }
            if (!Uri.TryCreate(Placeholder().Replace(r.UrlTemplate ?? "", "x"), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            {
                return "The address is an http(s) URL; parameters go in its path or query as {name}.";
            }
            var authority = (r.UrlTemplate ?? "").Split("://", 2) is [_, var rest] ? rest.Split('/', 2)[0] : "";
            if (authority.Contains('{', StringComparison.Ordinal))
            {
                return "The address's host is fixed: parameters may fill its path and query, not where it goes.";
            }
            if (Placeholders(r.UrlTemplate! + r.BodyTemplate).FirstOrDefault(n => parameters.All(p => p.Name != n)) is { } unknown)
            {
                return $"{{{unknown}}} is not one of the parameters.";
            }
            if (r.BodyTemplate is { Length: > 0 } body && !ValidJson(Placeholder().Replace(body, "x")))
            {
                return "The body is JSON, with {name} inside its strings.";
            }
            return null;
        }
        if (r.Kind == CustomTool.Prompt)
        {
            if (string.IsNullOrWhiteSpace(r.Model) || string.IsNullOrWhiteSpace(r.PromptTemplate))
            {
                return "A prompt tool needs its model and its prompt, the parameters in it as {name}.";
            }
            return Placeholders(r.PromptTemplate).FirstOrDefault(n => parameters.All(p => p.Name != n)) is { } missing ? $"{{{missing}}} is not one of the parameters." : null;
        }
        return "A tool is an HTTP request (http) or a prompt (prompt).";
    }

    private static bool ValidJson(string text)
    {
        try
        {
            JsonNode.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static IEnumerable<string> Placeholders(string? text) => Placeholder().Matches(text ?? "").Select(m => m.Groups[1].Value).Distinct();

    /// <summary>The request a call makes: the address and body filled with the arguments (escaped for where each goes).</summary>
    public static (string Url, string? Body) Fill(CustomTool t, JsonObject args)
    {
        string Value(string name) => args[name] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : "";
        var url = Placeholder().Replace(t.UrlTemplate ?? "", m => Uri.EscapeDataString(Value(m.Groups[1].Value)));
        var body = t.BodyTemplate is { Length: > 0 } b
            ? Placeholder().Replace(b, m => JsonEncodedText.Encode(Value(m.Groups[1].Value)).ToString())
            : null;
        return (url, body);
    }

    /// <summary>The part of a JSON answer a dotted path names (data.items, results.0.title); the whole when it is not there.</summary>
    public static string Select(string text, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return text;
        }
        try
        {
            JsonNode? node = JsonNode.Parse(text);
            foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                node = node switch
                {
                    JsonObject o => o[part],
                    JsonArray a when int.TryParse(part, out var i) && i >= 0 && i < a.Count => a[i],
                    _ => null,
                };
            }
            return node?.ToJsonString(Mcp.Plain) ?? text;
        }
        catch (JsonException)
        {
            return text;
        }
    }

    public static IChatTool For(CustomTool t, IHttpClientFactory http, GatewayChat gateway, string? dataKey) =>
        t.Kind == CustomTool.Prompt ? new PromptTool(t, gateway) : new HttpTool(t, http, dataKey);

    private sealed class HttpTool(CustomTool t, IHttpClientFactory http, string? dataKey) : IChatTool
    {
        public string Id => Prefix + t.Id;
        public string Title => t.Title.Length > 0 ? t.Title : t.Name;
        public string Description => t.Description;
        public string Icon => "globe";
        public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun([Function(t)], null, async (_, args, token) =>
        {
            var (url, body) = Fill(t, args);
            using var request = new HttpRequestMessage(new HttpMethod(t.Method ?? "GET"), url);
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            foreach (var (name, value) in Headers(t, dataKey))
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
            try
            {
                using var response = await http.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                var buffer = new byte[MaxResponseBytes];
                var read = 0;
                int n;
                while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), token)) > 0)
                {
                    read += n;
                }
                var text = Select(Encoding.UTF8.GetString(buffer, 0, read), t.ResultPath);
                text = text.Length > MaxResultChars ? text[..MaxResultChars] + "\n[… cut]" : text;
                return response.IsSuccessStatusCode ? new ToolResult(text) : new ToolResult($"HTTP {(int)response.StatusCode}: {text}", IsError: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
            {
                return new ToolResult($"The request to {new Uri(url).Host} failed: {ex.Message}", IsError: true);
            }
        })
        { AsksFirst = _ => t.AskFirst });
    }

    /// <summary>The headers' values, decrypted.</summary>
    public static Dictionary<string, string> Headers(CustomTool t, string? dataKey) =>
        t.HeadersEncrypted is { Length: > 0 } enc && SettingsCrypto.Decrypt(enc, dataKey) is { } json
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []
            : [];

    private sealed class PromptTool(CustomTool t, GatewayChat gateway) : IChatTool
    {
        public string Id => Prefix + t.Id;
        public string Title => t.Title.Length > 0 ? t.Title : t.Name;
        public string Description => t.Description;
        public string Icon => "sparkles";
        public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun([Function(t)], null, async (_, args, token) =>
        {
            string Value(string name) => args[name] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : "";
            var prompt = Placeholder().Replace(t.PromptTemplate ?? "", m => Value(m.Groups[1].Value));
            var messages = new JsonArray();
            if (t.SystemPrompt is { Length: > 0 } system)
            {
                messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
            }
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = prompt });
            var request = new JsonObject
            {
                ["model"] = t.Model, ["messages"] = messages, ["stream"] = true, ["max_tokens"] = 2000, ["user"] = context.Email,
                ["metadata"] = new JsonObject { ["tags"] = new JsonArray("tool", t.Name) },
            };
            try
            {
                var said = new StringBuilder();
                await foreach (var e in gateway.StreamAsync(request, context.Email, token))
                {
                    if (e is ContentDelta c)
                    {
                        said.Append(c.Text);
                    }
                }
                return new ToolResult(said.ToString());
            }
            catch (ChatGatewayException ex)
            {
                return new ToolResult($"{t.Model} could not answer: {ex.Message}", IsError: true);
            }
        }));
    }

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]{0,59})\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,59}$")]
    private static partial Regex FunctionName();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,59}$")]
    private static partial Regex ParameterName();
}
