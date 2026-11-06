using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// One of the person's assistants in Arena (theirs, shared with them, or the company's): its instructions, the model and
/// thinking it starts at, the Arena tools it has on (null: those on in new chats), and the skills it uses. A session works
/// as one when the person picks it (/assistant, the IDE's Assistant menu); a sub-agent works as one when the agent names it.
/// </summary>
internal sealed record ArenaAssistant(string Id, string Name, string? Description, string? Instructions, string? Model, string? Thinking,
    IReadOnlyList<string>? Tools, IReadOnlyList<string> Skills, string Icon = "bot", string Color = "blue")
{
    public static ArenaAssistant? From(JsonObject? o) => o?.Str("id") is { Length: > 0 } id && o.Str("name") is { Length: > 0 } name
        ? new ArenaAssistant(id, name, o.Str("description"), o.Str("instructions"), o.Str("model"), o.Str("thinking"),
            o["tools"] is JsonArray tools ? Names(tools) : null, o["skills"] is JsonArray skills ? Names(skills) : [], o.Str("icon") ?? "bot", o.Str("color") ?? "blue")
        : null;

    private static List<string> Names(JsonArray a) => [.. a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>()];

    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["name"] = Name, ["description"] = Description, ["instructions"] = Instructions, ["model"] = Model, ["thinking"] = Thinking,
        ["tools"] = Tools is null ? null : new JsonArray([.. Tools.Select(t => (JsonNode)t)]), ["skills"] = new JsonArray([.. Skills.Select(s => (JsonNode)s)]),
        ["icon"] = Icon, ["color"] = Color,
    };
}

/// <summary>The session's assistant, and the person's assistants in Arena.</summary>
internal sealed partial class Runtime
{
    // The person's assistants as Arena last listed them (null: not asked yet, or Arena out of reach).
    private IReadOnlyList<ArenaAssistant>? _assistants;
    private ArenaAssistant? _assistant;
    // The tool groups the assistant turned off as it was picked: on again once it is put down.
    private IReadOnlyList<string> _offByAssistant = [];

    /// <summary>The assistant the session works as; null: none (Code Arena's own way).</summary>
    public ArenaAssistant? Assistant => Volatile.Read(ref _assistant);

    /// <summary>The person's assistants as last listed: for the prompt's list (the task tool names them) without asking Arena.</summary>
    public IReadOnlyList<ArenaAssistant> KnownAssistants => Volatile.Read(ref _assistants) ?? [];

    /// <summary>The assistants the person may use in Arena, asked for now; null when Code Arena is not signed in or Arena did not answer.</summary>
    public async Task<IReadOnlyList<ArenaAssistant>?> AssistantsAsync(CancellationToken ct)
    {
        if (Config.Url is not { Length: > 0 } url || Config.ApiKey is not { Length: > 0 } key)
        {
            return null;
        }
        try
        {
            using var req = ChatSync.Request(HttpMethod.Get, url.TrimEnd('/') + "/api/code-arena/assistants", key, null);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            using var res = await Http.SendAsync(req, limit.Token);
            if (res.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }
            var list = (JsonNode.Parse(await res.Content.ReadAsStringAsync(limit.Token)) as JsonArray ?? [])
                .OfType<JsonObject>().Select(ArenaAssistant.From).OfType<ArenaAssistant>().ToList();
            Volatile.Write(ref _assistants, list);
            return list;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or System.Text.Json.JsonException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>An assistant by its name or id (Arena asked when it is not among those known); null: none such the person may use.</summary>
    public async Task<ArenaAssistant?> FindAssistantAsync(string nameOrId, CancellationToken ct)
    {
        static ArenaAssistant? In(IEnumerable<ArenaAssistant> list, string key) =>
            list.FirstOrDefault(a => a.Id == key) ?? list.FirstOrDefault(a => string.Equals(a.Name, key, StringComparison.OrdinalIgnoreCase));
        return In(KnownAssistants, nameOrId) ?? (await AssistantsAsync(ct) is { } fresh ? In(fresh, nameOrId) : null);
    }

    /// <summary>
    /// The session works as <paramref name="assistant"/> from now on (null: as Code Arena's own again): its instructions in the
    /// prompt, its model and thinking when it names them (and the gateway serves the model), its Arena tools on and the others
    /// off, its skills named for the model. Kept in the session, so a resumed one works as it did. What changed, in words.
    /// </summary>
    public string UseAssistant(ArenaAssistant? assistant)
    {
        var said = new List<string>();
        // The groups the one before turned off come back on.
        if (_offByAssistant.Count > 0)
        {
            Demand.SetOff(Demand.Off.Except(_offByAssistant));
            _offByAssistant = [];
        }
        Volatile.Write(ref _assistant, assistant);
        if (assistant is null)
        {
            Session.Assistant(null);
            return "No assistant: the session works as Code Arena's own again.";
        }
        said.Add($"Working as the assistant {assistant.Name}");
        if (assistant.Model is { Length: > 0 } model)
        {
            if (Models.FirstOrDefault(m => m.Id == model) is { } info)
            {
                if (info.Id != Model.Name)
                {
                    SwitchModel(info);
                    said.Add($"model {info.Id}");
                }
            }
            else
            {
                said.Add($"its model {model} is not offered here, so {Model.Name} stays");
            }
        }
        if (assistant.Thinking is { Length: > 0 } thinking && ModelState.Levels.Contains(thinking))
        {
            Model.Thinking = thinking;
            said.Add($"thinking {thinking}");
        }
        if (assistant.Tools is { } tools)
        {
            // Arena's groups it does not have go off; the person's own servers and the local tools stay as they are.
            var groups = Tools.All.Select(t => t.Group).OfType<string>().Where(g => !g.StartsWith("mcp:", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
            var off = groups.Where(g => !tools.Contains(g) && !Demand.IsOff(g)).ToList();
            if (off.Count > 0)
            {
                Demand.SetOff(Demand.Off.Union(off));
                _offByAssistant = off;
                said.Add($"{string.Join(", ", off.Select(ToolTitle))} off");
            }
        }
        var skills = assistant.Skills.Where(s => Extensions.Skills.Any(x => x.Name == s)).ToList();
        if (skills.Count > 0)
        {
            said.Add($"its skills {string.Join(", ", skills.Select(s => "/" + s))}");
        }
        var kept = assistant.ToJson();
        kept["turnedOff"] = new JsonArray([.. _offByAssistant.Select(g => (JsonNode)g)]);
        Session.Assistant(kept);
        return string.Join("; ", said) + ".";
    }

    /// <summary>A resumed session's assistant, as it was kept (Arena not asked: it works offline too).</summary>
    private void RestoreAssistant(JsonObject? kept)
    {
        Volatile.Write(ref _assistant, ArenaAssistant.From(kept));
        _offByAssistant = kept?["turnedOff"] is JsonArray off ? [.. off.Select(n => n?.GetValue<string>()).OfType<string>()] : [];
    }

    /// <summary>/assistant: the person's assistants, the one in use; NAME or N works as one; off works as none. The text to show.</summary>
    public async Task<string> AssistantCommandAsync(string arg, CancellationToken ct)
    {
        var word = arg.Trim();
        if (word.Equals("off", StringComparison.OrdinalIgnoreCase) || word.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return UseAssistant(null);
        }
        var list = await AssistantsAsync(ct);
        if (word.Length == 0)
        {
            if (list is null)
            {
                return (Assistant is { } now ? $"Working as the assistant {now.Name}. " : "") +
                    "Arena's assistants are listed once Code Arena is signed in to Arena (code-arena login) and Arena answers.";
            }
            if (list.Count == 0)
            {
                return "You have no assistants in Arena yet: make one there (Assistants), then /assistant NAME works as it.";
            }
            var lines = list.Select((a, i) => $"  {i + 1}. {a.Name}{(Assistant?.Id == a.Id ? " (in use)" : "")}{(a.Description is { Length: > 0 } d ? " — " + Fmt.OneLine(d, 80) : "")}");
            return $"Your assistants in Arena:\n{string.Join("\n", lines)}\n/assistant N or NAME works as one; /assistant off as none.";
        }
        ArenaAssistant? found = null;
        if (int.TryParse(word, out var n) && list is not null && n >= 1 && n <= list.Count)
        {
            found = list[n - 1];
        }
        found ??= await FindAssistantAsync(word, ct);
        return found is null ? $"You have no assistant {word} in Arena (/assistant lists yours)." : UseAssistant(found);
    }
}
