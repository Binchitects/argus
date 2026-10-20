using System.IO.Compression;
using System.Text;

namespace Llm.Api.Plugins;

/// <summary>
/// For plugin authors: a starter plugin (Admin → Plugins → Make a plugin) with every part a plugin can have, and a check of a
/// plugin's zip that says what would stop it installing, what looks wrong, and what it brings and asks for.
/// </summary>
public static class PluginTemplate
{
    private static readonly (string Name, string Text)[] Files =
    [
        ("plugin.yaml", """
            # The plugin's manifest: docs/plugins.md says every field.
            name: weather-desk
            version: 0.1.0
            title: Weather desk
            description: Today's weather for a city, and a forecaster assistant.
            author: Your team
            homepage: https://example.com/weather-desk
            tools:
              openapi: openapi.yaml
              url: "{api_url}"
            auth:
              per_person: none
              header: Authorization
              value: "Bearer {token}"
            settings:
              - key: api_url
                title: The weather API's address
                type: url
                required: true
                help: For example https://weather.example.com/v1
              - key: token
                title: API key
                type: secret
                required: true
            # Calls that change something ask the person first.
            writes: []
            prompts: [prompts/forecast.md]
            assistants: [assistants/forecaster.yaml]
            safeguards: [safeguards/rules.yaml]
            toolsets:
              - name: Weather
                description: The weather desk and the web.
                tools: [self, web]
            """),
        ("openapi.yaml", """
            openapi: 3.0.3
            info:
              title: Weather
              version: 1.0.0
            paths:
              /today:
                get:
                  operationId: today
                  summary: Today's weather in a city.
                  parameters:
                    - name: city
                      in: query
                      required: true
                      schema: { type: string }
                      description: The city, e.g. Berlin.
                  responses:
                    "200":
                      description: The weather.
            """),
        ("prompts/forecast.md", """
            ---
            name: forecast
            title: Forecast for a trip
            ---
            What will the weather be like in {{city}} for the next few days, and what should I pack?
            """),
        ("assistants/forecaster.yaml", """
            name: Forecaster
            description: Answers about the weather, with the weather desk.
            instructions: You are a cheerful forecaster. Use the weather tool for today's weather; say when you are guessing.
            starters:
              - What is the weather in Berlin?
              - Do I need an umbrella in London today?
            tools: [self]
            icon: cloud-sun
            color: sky
            """),
        ("safeguards/rules.yaml", """
            # Rules come in monitor mode: an admin makes them bite under Admin → Safeguards.
            rules:
              - name: Storm chasing
                detector: words
                stages: [question]
                action: warn
                patterns: "chase a tornado; storm chasing"
                message: Chasing storms is dangerous. Send it anyway?
            """),
        ("README.md", """
            # Weather desk

            A starter plugin: change the manifest, the API document, the prompt, the assistant and the rules, zip the folder,
            check it under Admin → Plugins → Check a plugin, then install it. docs/plugins.md describes every field.
            """),
    ];

    /// <summary>The starter plugin as a zip, in a folder of its name.</summary>
    public static byte[] Zip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in Files)
            {
                using var w = new StreamWriter(zip.CreateEntry($"weather-desk/{name}").Open(), new UTF8Encoding(false));
                w.Write(text.TrimStart('\n') + "\n");
            }
        }
        return ms.ToArray();
    }

    /// <summary>What a package would bring, ask for, and what looks wrong in it (it read, so nothing stops it installing but its settings).</summary>
    public static object Check(PluginPackage package)
    {
        var m = package.Manifest;
        var problems = new List<string>();
        var warnings = new List<string>();
        int? operations = null;
        if (package.Spec is { } spec)
        {
            try
            {
                var ops = Chat.Tools.OpenApi.Operations(Chat.Tools.OpenApi.Parse(spec), "");
                operations = ops.Count;
                if (ops.Count == 0)
                {
                    problems.Add("Its OpenAPI document lists no operations.");
                }
                var names = ops.Select(o => o.Function).ToHashSet(StringComparer.Ordinal);
                warnings.AddRange(m.Writes.Where(w => !names.Contains(w)).Select(w => $"writes names {w}, which is not one of its operations."));
            }
            catch (Chat.Tools.OpenApi.SpecException ex)
            {
                problems.Add(ex.Message);
            }
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(m.Version, @"^\d+\.\d+\.\d+"))
        {
            warnings.Add($"Its version {m.Version} is not like 1.2.3: updates are told apart by it.");
        }
        if (m.Description.Length == 0)
        {
            warnings.Add("It has no description: admins read it before they install.");
        }
        if (m.Url.StartsWith("http:", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Its address is plain http: calls and their keys go unencrypted.");
        }
        var settings = m.Settings.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var placeholder in System.Text.RegularExpressions.Regex.Matches(m.Url + m.Value + m.OAuth?.Authorize + m.OAuth?.Token, @"\{([a-z0-9_]+)\}").Select(x => x.Groups[1].Value).Distinct())
        {
            if (!settings.Contains(placeholder))
            {
                problems.Add($"{{{placeholder}}} is not one of its settings.");
            }
        }
        foreach (var t in m.Toolsets)
        {
            if (t.Tools.Length == 0)
            {
                warnings.Add($"The toolset {t.Name} names no tools: it has only the plugin's own.");
            }
        }
        foreach (var a in package.Assistants.Where(a => string.IsNullOrWhiteSpace(a.Instructions)))
        {
            warnings.Add($"The assistant {a.Name} has no instructions.");
        }
        return new
        {
            ok = problems.Count == 0, problems, warnings,
            m.Name, m.Version, m.Title, operations, mcp = m.Mcp,
            prompts = (package.Prompts ?? []).Select(p => p.Name), assistants = package.Assistants.Select(a => a.Name), rules = package.Rules.Select(r => r.Name),
            toolsets = m.Toolsets.Select(t => t.Name), permissions = PluginContent.Permissions(package),
        };
    }
}
