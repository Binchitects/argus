using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.Access;
using Argus.Server;
using Argus.Store;
using Argus.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace Argus.Platform;

/// <summary>Conversations and their messages, always scoped to their owner.</summary>
public static class Conversations
{
    public const string DefaultTitle = "New chat";

    public static List<Row> List(SqliteConnection conn, long userId) =>
        Sql.Query(conn, "SELECT id, title, model, created_at, updated_at FROM conversations WHERE user_id = ? ORDER BY updated_at DESC LIMIT 500", userId);

    public static Row Create(SqliteConnection conn, long userId, string? title, string? model)
    {
        var id = Guid.NewGuid().ToString("n");
        var now = AppDb.Now();
        Sql.Exec(conn, "INSERT INTO conversations (id, user_id, title, model, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?)",
            id, userId, Clip(string.IsNullOrWhiteSpace(title) ? DefaultTitle : title.Trim(), 120), model, now, now);
        return Get(conn, userId, id)!;
    }

    public static Row? Get(SqliteConnection conn, long userId, string id) =>
        Sql.One(conn, "SELECT id, title, model, created_at, updated_at FROM conversations WHERE id = ? AND user_id = ?", id, userId);

    public static bool Rename(SqliteConnection conn, long userId, string id, string title) =>
        Sql.Exec(conn, "UPDATE conversations SET title = ? WHERE id = ? AND user_id = ?", Clip(title.Trim(), 120), id, userId) > 0;

    public static bool Delete(SqliteConnection conn, long userId, string id) =>
        Sql.Exec(conn, "DELETE FROM conversations WHERE id = ? AND user_id = ?", id, userId) > 0;

    public static List<Row> Messages(SqliteConnection conn, string conversationId) =>
        Sql.Query(conn, "SELECT id, role, content, reasoning, tool_calls, tool_call_id, name, is_error, created_at FROM messages WHERE conversation_id = ? ORDER BY id",
            conversationId);

    public static JsonObject MessageJson(Row m)
    {
        var o = new JsonObject { ["id"] = m.Long("id"), ["role"] = m.Str("role"), ["content"] = m.Str("content") };
        if (m.StrOrNull("reasoning") is { Length: > 0 } r) o["reasoning"] = r;
        if (m.StrOrNull("tool_calls") is { Length: > 0 } tc) o["tool_calls"] = JsonNode.Parse(tc);
        if (m.StrOrNull("tool_call_id") is { } id) o["tool_call_id"] = id;
        if (m.StrOrNull("name") is { } n) o["name"] = n;
        if (m.Long("is_error") != 0) o["is_error"] = true;
        o["created_at"] = m.Double("created_at");
        return o;
    }

    public static void Add(SqliteConnection conn, string conversationId, string role, string content, string? reasoning = null,
        JsonArray? toolCalls = null, string? toolCallId = null, string? name = null, bool isError = false)
    {
        var now = AppDb.Now();
        Sql.Exec(conn, "INSERT INTO messages (conversation_id, role, content, reasoning, tool_calls, tool_call_id, name, is_error, created_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
            conversationId, role, content, string.IsNullOrEmpty(reasoning) ? null : reasoning, toolCalls?.ToJsonString(), toolCallId, name, isError ? 1 : 0, now);
        Sql.Exec(conn, "UPDATE conversations SET updated_at = ? WHERE id = ?", now, conversationId);
    }

    public static string Clip(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";
}

/// <summary>
/// One chat turn: the model, the code-index tools run in-process as the person
/// asking, and the loop between them, streamed to the browser as it happens.
/// </summary>
public sealed class ChatService(Tools tools, Gateway? gateway, Func<AppUser, Identity> identityFor, string appDbPath)
{
    public const int MaxToolRounds = 8;
    public const int MaxToolResultChars = 60_000;
    public const int MaxHistoryMessages = 60;

    static string BasePrompt =>
        Environment.GetEnvironmentVariable("ARGUS_CHAT_SYSTEM_PROMPT") is { Length: > 0 } p ? p :
            "You are the engineering assistant for this organisation. You can search the organisation's code " +
            "and its installed documentation packs with the tools provided. Use them whenever an answer depends " +
            "on the code or on an exact API detail, and cite the repository and path, or the documentation URL, you relied on. " +
            "Format answers in Markdown.";

    string? _docsFindDescription;

    JsonArray ToolDefinitions()
    {
        _docsFindDescription ??= tools.DocsFindDescription(ToolCatalog.Specs.First(s => s.Name == "docs_find").Description);
        var list = new JsonArray();
        foreach (var spec in ToolCatalog.Specs)
            list.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = spec.Name,
                    ["description"] = spec.Name == "docs_find" ? _docsFindDescription : spec.Description,
                    ["parameters"] = spec.InputSchema.DeepClone(),
                },
            });
        return list;
    }

    /// <summary>The person's own gateway key, created the first time they chat.</summary>
    public async Task<string?> KeyFor(SqliteConnection conn, AppUser user)
    {
        if (gateway is null || !gateway.CanAdminister) return null;
        if (Users.GatewayKey(conn, user.Id) is { Length: > 0 } key) return key;
        await gateway.EnsureUser(user.Email);
        var (created, _) = await gateway.CreateKey(user.Email, $"chat:{user.Username}");
        Users.SetGatewayKey(conn, user.Id, created);
        return created;
    }

    static List<JsonObject> History(List<Row> rows)
    {
        var tail = rows.Count > MaxHistoryMessages ? rows.Skip(rows.Count - MaxHistoryMessages).ToList() : rows;
        // A cut must not start inside a tool exchange: a tool message whose call was dropped is rejected upstream.
        while (tail.Count > 0 && tail[0].Str("role") == "tool") tail = tail.Skip(1).ToList();
        var messages = new List<JsonObject>();
        foreach (var m in tail)
        {
            var role = m.Str("role");
            var o = new JsonObject { ["role"] = role, ["content"] = m.Str("content") };
            if (role == "assistant" && m.StrOrNull("tool_calls") is { Length: > 0 } tc) o["tool_calls"] = JsonNode.Parse(tc);
            if (role == "tool") o["tool_call_id"] = m.Str("tool_call_id");
            messages.Add(o);
        }
        return messages;
    }

    /// <summary>Run one tool call as <paramref name="user"/>. Returns the text the model sees and whether it failed.</summary>
    public (string Text, bool IsError) RunTool(string name, string argumentsJson, AppUser user)
    {
        var spec = ToolCatalog.Specs.FirstOrDefault(s => s.Name == name);
        if (spec is null) return ($"Unknown tool: {name}", true);
        try
        {
            Dictionary<string, JsonElement> arguments;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ToolError("tool arguments must be a JSON object");
                arguments = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            }
            catch (JsonException exc) { throw new ToolError($"tool arguments are not valid JSON: {exc.Message}"); }
            var args = ToolRuntime.Validate(spec, arguments);
            Identity? identity = null;
            var result = tools.Dispatch(name, args, () =>
            {
                try { return identity ??= identityFor(user); }
                catch (AclDenied exc) { throw new ToolError(exc.Message); }
            });
            var text = result is null ? "null" : result.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            if (text.Length > MaxToolResultChars) text = text[..MaxToolResultChars] + "\n[truncated: the result was longer than the model can usefully read]";
            return (text, false);
        }
        catch (ToolError exc) { return ($"Error executing tool {name}: {exc.Message}", true); }
        catch (QueryError exc) { return ($"Error executing tool {name}: {exc.Message}", true); }
        catch (Exception exc)
        {
            Console.Error.WriteLine($"chat tool {name} failed: {exc}");
            return ($"Error executing tool {name}: {exc.Message}", true);
        }
    }

    sealed class PendingCall
    {
        public string Id = "";
        public string Name = "";
        public readonly StringBuilder Arguments = new();
    }

    public async Task Stream(HttpContext ctx, AppUser user, string conversationId, string text, string? model, bool useTools)
    {
        var ct = ctx.RequestAborted;
        var response = ctx.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        async Task Emit(JsonObject e)
        {
            await response.WriteAsync("data: " + e.ToJsonString() + "\n\n", ct);
            await response.Body.FlushAsync(ct);
        }

        using var conn = AppDb.Open(appDbPath);
        var conversation = Conversations.Get(conn, user.Id, conversationId);
        if (conversation is null)
        {
            await Emit(new JsonObject { ["type"] = "error", ["message"] = "No such conversation." });
            return;
        }
        text = text.Trim();
        if (text.Length == 0)
        {
            await Emit(new JsonObject { ["type"] = "error", ["message"] = "Nothing to send." });
            return;
        }
        model = string.IsNullOrWhiteSpace(model) ? conversation.StrOrNull("model") : model.Trim();
        if (gateway is null)
        {
            await Emit(new JsonObject { ["type"] = "error", ["message"] = "No model gateway is configured (ARGUS_GATEWAY_URL)." });
            return;
        }
        if (string.IsNullOrEmpty(model))
        {
            try { model = (await gateway.Models(await KeyFor(conn, user))).FirstOrDefault(); }
            catch (GatewayError) { }
            if (string.IsNullOrEmpty(model))
            {
                await Emit(new JsonObject { ["type"] = "error", ["message"] = "The gateway lists no model to answer with." });
                return;
            }
        }

        Conversations.Add(conn, conversationId, "user", text);
        if (conversation.Str("title") == Conversations.DefaultTitle)
            Conversations.Rename(conn, user.Id, conversationId, Conversations.Clip(text.ReplaceLineEndings(" "), 60));
        Sql.Exec(conn, "UPDATE conversations SET model = ? WHERE id = ?", model, conversationId);
        await Emit(new JsonObject { ["type"] = "start", ["conversation_id"] = conversationId, ["model"] = model });

        string? key;
        try { key = await KeyFor(conn, user); }
        catch (GatewayError exc)
        {
            await Emit(new JsonObject { ["type"] = "error", ["message"] = exc.Message });
            return;
        }

        var toolDefs = useTools ? ToolDefinitions() : null;
        JsonObject? usage = null;
        for (int round = 0; ; round++)
        {
            var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = BasePrompt + "\n\n" + ToolCatalog.ServerInstructions } };
            foreach (var m in History(Conversations.Messages(conn, conversationId))) messages.Add(m);
            var body = new JsonObject { ["model"] = model, ["messages"] = messages, ["user"] = user.Email };
            // After the last allowed round the model must answer from what it has.
            if (toolDefs is not null && round < MaxToolRounds) body["tools"] = toolDefs.DeepClone();

            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            var calls = new SortedDictionary<int, PendingCall>();
            string? finish = null;
            try
            {
                await foreach (var chunk in gateway.StreamChat(body, key, ct))
                {
                    if (chunk["usage"] is JsonObject u) usage = (JsonObject)u.DeepClone();
                    if (chunk["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice) continue;
                    if (choice["finish_reason"]?.ToString() is { Length: > 0 } f) finish = f;
                    if (choice["delta"] is not JsonObject delta) continue;
                    var r = delta["reasoning_content"]?.ToString() ?? delta["reasoning"]?.ToString();
                    if (!string.IsNullOrEmpty(r))
                    {
                        reasoning.Append(r);
                        await Emit(new JsonObject { ["type"] = "reasoning", ["text"] = r });
                    }
                    if (delta["content"]?.ToString() is { Length: > 0 } c)
                    {
                        content.Append(c);
                        await Emit(new JsonObject { ["type"] = "content", ["text"] = c });
                    }
                    if (delta["tool_calls"] is JsonArray tcs)
                        foreach (var tc in tcs.OfType<JsonObject>())
                        {
                            var index = tc["index"]?.GetValue<int>() ?? 0;
                            if (!calls.TryGetValue(index, out var pending)) calls[index] = pending = new PendingCall();
                            if (tc["id"]?.ToString() is { Length: > 0 } id) pending.Id = id;
                            if (tc["function"]?["name"]?.ToString() is { Length: > 0 } n) pending.Name += n;
                            if (tc["function"]?["arguments"]?.ToString() is { Length: > 0 } a) pending.Arguments.Append(a);
                        }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The browser went away. Keep what was said so the history stays honest.
                if (content.Length > 0) Conversations.Add(conn, conversationId, "assistant", content + "\n\n*(stopped)*", reasoning.ToString());
                return;
            }
            catch (GatewayError exc)
            {
                if (content.Length > 0) Conversations.Add(conn, conversationId, "assistant", content.ToString(), reasoning.ToString());
                await Emit(new JsonObject { ["type"] = "error", ["message"] = exc.Message });
                return;
            }

            if (calls.Count == 0 || toolDefs is null || round >= MaxToolRounds)
            {
                Conversations.Add(conn, conversationId, "assistant", content.ToString(), reasoning.ToString());
                await Emit(new JsonObject { ["type"] = "done", ["finish_reason"] = finish, ["usage"] = usage });
                return;
            }

            var callJson = new JsonArray();
            foreach (var (i, call) in calls)
            {
                if (call.Id.Length == 0) call.Id = $"call_{round}_{i}";
                callJson.Add(new JsonObject
                {
                    ["id"] = call.Id, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments.ToString() },
                });
            }
            Conversations.Add(conn, conversationId, "assistant", content.ToString(), reasoning.ToString(), callJson);
            foreach (var call in calls.Values)
            {
                await Emit(new JsonObject { ["type"] = "tool_call", ["id"] = call.Id, ["name"] = call.Name, ["arguments"] = call.Arguments.ToString() });
                var (result, isError) = await Task.Run(() => RunTool(call.Name, call.Arguments.ToString(), user), ct);
                Conversations.Add(conn, conversationId, "tool", result, toolCallId: call.Id, name: call.Name, isError: isError);
                await Emit(new JsonObject
                {
                    ["type"] = "tool_result", ["id"] = call.Id, ["name"] = call.Name,
                    ["content"] = Conversations.Clip(result, 4000), ["is_error"] = isError,
                });
            }
        }
    }
}
