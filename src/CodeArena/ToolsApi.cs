using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// Configure Tools, in the IDE and the web interface (as VS Code's and the web chat's tool customisation): every tool of
/// the session (the built-in ones, Arena's, code-intel's, the MCP servers', the person's and the project's own) with its
/// settings at each level and those in force; the project's own tools and MCP servers to enable here.
/// </summary>
internal static class ToolsApi
{
    private static string KindName(ToolDef t) => t.Kind switch
    {
        ToolKind.Read => "read",
        ToolKind.Edit => "edit",
        ToolKind.Shell => "shell",
        ToolKind.Remote => "remote",
        _ => "agent",
    };

    /// <summary>How a tool asks in force: always, never, or by its kind and the mode (asks: a remote one that changes things).</summary>
    private static string Asks(ToolDef t) => t.Approval ?? t.Kind switch
    {
        ToolKind.Read or ToolKind.Agent => "never",
        ToolKind.Remote when t.Trusted || t.ReadOnly => "never",
        _ => "mode",
    };

    public static JsonObject List(Runtime rt)
    {
        var on = rt.Tools.All.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var tools = new JsonArray();
        foreach (var t in rt.Tools.Every)
        {
            var (person, here, project) = rt.ToolSettings.Of(t.Name);
            var now = on.GetValueOrDefault(t.Name);
            tools.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["group"] = t.Group,
                ["groupTitle"] = t.Group is { } g ? rt.ToolTitle(g) : t.Origin is not null ? "Your own and the project's" : "Code Arena",
                ["server"] = t.Server,
                ["kind"] = KindName(t),
                ["origin"] = t.Origin,
                ["description"] = t.Description,
                ["arguments"] = new JsonArray([.. (t.Parameters["properties"] as JsonObject ?? []).Select(p => (JsonNode)new JsonObject
                {
                    ["name"] = p.Key, ["type"] = p.Value.Str("type"), ["description"] = p.Value.Str("description"),
                })]),
                ["enabled"] = now is not null,
                ["asks"] = now is null ? null : Asks(now),
                ["person"] = person.Json(),
                ["here"] = here.Json(),
                ["project"] = project.Json(),
                // Why it may not run unasked (Arena's admins ask first, it stops what the person let run); null: it may.
                ["allowLocked"] = ToolSettings.AllowLocked(t),
            });
        }
        return new JsonObject
        {
            ["tools"] = tools,
            ["own"] = new JsonArray([.. rt.OwnToolFiles.Select(f => (JsonNode)new JsonObject
            {
                ["key"] = "tool:" + f.Name, ["name"] = f.Name, ["description"] = f.Description, ["source"] = f.Source, ["origin"] = f.Origin,
                ["command"] = f.Argv is { } argv ? string.Join(' ', argv) : f.Shell, ["kind"] = f.Kind switch { ToolKind.Read => "read", ToolKind.Edit => "edit", _ => "shell" },
                ["enabled"] = rt.IsEnabled(f), ["file"] = rt.Workspace.Show(f.File),
            })]),
            ["servers"] = new JsonArray([.. rt.ProjectServers.Select(s => (JsonNode)new JsonObject
            {
                ["key"] = "mcp:" + s.Server.Name, ["name"] = s.Server.Name, ["command"] = s.Server.Url ?? string.Join(' ', new[] { s.Server.Command ?? "" }.Concat(s.Server.Args)),
                ["enabled"] = rt.IsEnabled(s), ["connected"] = rt.Links.Any(l => l.Name == s.Server.Name && l.State == LinkState.Connected), ["file"] = rt.Workspace.Show(s.File),
            })]),
            ["projectFile"] = rt.ProjectRoot.Length > 0 ? rt.Workspace.Show(Path.Combine(rt.ProjectRoot, ".arena", "settings.json")) : null,
        };
    }

    /// <summary>A tool's settings saved for the person (level person, every project) or for them in this project (here).</summary>
    public static JsonObject Save(Runtime rt, JsonObject body)
    {
        var name = body.Str("name") ?? throw new IdeError(400, "invalid", "Say which tool.");
        var tool = rt.Tools.Every.FirstOrDefault(t => t.Name == name) ?? throw new IdeError(404, "not_found", $"There is no tool {name} in this session.");
        var choice = ToolChoice.From(body["settings"]);
        if (body["settings"] is JsonObject s && s.Str("approval") is { } approval && !ToolChoice.Approvals.Contains(approval))
        {
            throw new IdeError(400, "invalid", "A tool asks by default, every call (ask), or never (allow).");
        }
        if (rt.ToolSettings.Save(body.Str("level") ?? "person", name, choice, tool) is { } refused)
        {
            throw new IdeError(400, "invalid", refused);
        }
        return List(rt);
    }

    /// <summary>The project's own tool or MCP server enabled here, or no longer.</summary>
    public static JsonObject Enable(Runtime rt, JsonObject body)
    {
        var (done, said) = rt.EnableOwn(body.Str("key") ?? "", body.Bool("on") ?? true);
        if (!done)
        {
            throw new IdeError(400, "invalid", said);
        }
        var list = List(rt);
        list["said"] = said;
        return list;
    }
}
