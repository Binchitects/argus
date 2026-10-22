using System.Text.Json.Nodes;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat.Tools;

/// <summary>Configuration section "Blender": the 3D sandbox (compose profile 3d).</summary>
public sealed class BlenderOptions
{
    /// <summary>The blender-jobs volume, as the app mounts it.</summary>
    public string Dir { get; set; } = "/blender";

    /// <summary>How long one run may take (a render with Cycles on the CPU is slow).</summary>
    public int TimeoutSeconds { get; set; } = 240;

    /// <summary>Of each of stdout and stderr, what the model reads.</summary>
    public int MaxOutputChars { get; set; } = 12_000;
}

/// <summary>The 3D sandbox: Blender headless in a container of its own with no network (services/blender).</summary>
public sealed class BlenderClient(IOptionsMonitor<BlenderOptions> options, TimeProvider clock)
    : JobRunner(() => options.CurrentValue.Dir, "The 3D sandbox (Blender)", "An admin turns on the 3d profile (COMPOSE_PROFILES) and applies it.", clock);

/// <summary>
/// 3D models: the model describes the model as parts (simple shapes with sizes, places, colours, textures, shaders and
/// lights) or writes a Blender Python script; Blender runs it headless in the 3D sandbox, and what it makes comes back to
/// the chat: the model as glTF (.glb, its textures and lights in it), opened in the chat's viewer or any 3D tool, and
/// pictures of it from two sides. Textures are made as images first when wanted (make_texture): from a pattern Blender
/// draws, or from a picture the image model makes, with their roughness and bumps as images too.
/// </summary>
public sealed partial class BlenderTool(BlenderClient blender, AppDbContext db, PythonTool files, IOptionsMonitor<BlenderOptions> options, ImageTool images,
    Access.AccessService access) : IChatTool
{
    public const string ToolId = "3d";

    public string Id => ToolId;
    public string Title => "3D models";
    public string Description => "Makes 3D models with Blender in a sandbox: the model (.glb) with its textures and lights, and pictures of it.";
    public string Icon => "box";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult(blender.Unavailable());

    /// <summary>Before the script: part(), light(), material() and make_texture() (Blender/builder.py).</summary>
    public static string Builder { get; } = Resource("blender-builder.py");

    /// <summary>After the script: pictures (Cycles when it has textures, glass or lights) and model.glb, its patterns baked into images (Blender/epilogue.py).</summary>
    public static string Epilogue { get; } = Resource("blender-epilogue.py");

    private static string Resource(string name)
    {
        using var stream = typeof(BlenderTool).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"{name} is not in the app");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static readonly string[] Patterns = ["brick", "tiles", "shingles", "checker", "wood", "stone", "concrete", "plaster", "grass", "marble", "metal", "fabric", "noise"];
    public static readonly string[] Shaders = ["matte", "plastic", "metal", "mirror", "glass", "light", "ceramic", "rubber"];

    private static JsonObject Vector(string description) => new()
    {
        ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" }, ["minItems"] = 3, ["maxItems"] = 3, ["description"] = description,
    };

    private static JsonObject Choice(string description, params string[] values) => new()
    {
        ["type"] = "string", ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]), ["description"] = description,
    };

    private static JsonObject Number(string description) => new() { ["type"] = "number", ["description"] = description };

    /// <summary>How the pictures look: fast (flat colours, quick) or realistic (Cycles); and the light they are taken in.</summary>
    private static void Looks(JsonObject properties)
    {
        properties["look"] = Choice("The pictures: realistic (textures, glass, lights; slower) or fast (flat colours). Default: realistic when the model has textures, glass or lights.", "auto", "realistic", "fast");
        properties["lighting"] = Choice("day (a sky and the sun, the default), studio (soft light, a plain backdrop) or night (dark: only the model's own lights).", "day", "studio", "night");
        properties["print"] = new JsonObject { ["type"] = "boolean", ["description"] = "Also model.stl and model.3mf to 3D-print it (in millimetres, resting on the plate), when the person wants to print it." };
        properties["print_size"] = Number("The printed model's longest side in millimetres (default: its real size when that is at most 250 mm, else 150).");
        properties["turntable"] = new JsonObject { ["type"] = "boolean", ["description"] = "Also turntable.mp4, a few seconds of the model turning once round, when the person wants a video of it." };
    }

    /// <summary>make_3d_parts' parameters: the parts, each a shape (or a light) with a size, a place, a turn, a colour, a texture and a shader.</summary>
    private static JsonObject PartsSchema()
    {
        var properties = new JsonObject
        {
            ["parts"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "The model's parts. Metres; z is up and z = 0 is the ground.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["name"] = Schema.Text("What the part is: walls, roof, door, window-left, lamp..."),
                        ["shape"] = Choice("figure: a whole person (face, hair, hands, clothes; the person fields); tree: a tree (kind); roof: a gable roof, its ridge " +
                            "along x; wedge: a ramp rising towards +y; capsule: a pill; lathe: a profile turned round (vases, bottles, columns, lamp posts); extrude: an " +
                            "outline raised by its height (floor plans, slabs, signs of any shape); tube: a round tube along a path (pipes, rails, handles, cables); text: " +
                            "3D letters; light: a light (kind, power), not a solid.",
                            "box", "cylinder", "cone", "sphere", "torus", "plane", "roof", "wedge", "pyramid", "capsule", "lathe", "extrude", "tube", "text", "figure", "tree", "light"),
                        ["size"] = Vector("[x, y, z]: width, depth, height in metres (a cylinder's or sphere's x and y are its diameters; an area light's x and y)."),
                        ["at"] = Vector("[x, y, z]: where the middle of the part's bottom sits (a light: where it is). A part on top of another starts at that one's height."),
                        ["turn"] = Vector("[x, y, z] rotation in degrees (optional; aims a spot or sun light)."),
                        ["color"] = Schema.Text("A name (red, white, brick, wood, glass, stone, grass, concrete, terracotta, warm for a light...) or #rrggbb."),
                        ["color2"] = Schema.Text("A pattern's second colour (optional)."),
                        ["texture"] = Schema.Text($"A pattern Blender draws ({string.Join(", ", Patterns)}), or the name of a texture made with make_texture (optional)."),
                        ["texture_size"] = Number("How large the texture is drawn: a pattern's scale (1 = real size), or the metres one texture image covers (optional)."),
                        ["shader"] = Choice("How the surface reflects light (optional).", Shaders),
                        ["metallic"] = Number("0 to 1 (optional)."),
                        ["roughness"] = Number("0 (mirror-like) to 1 (matte) (optional)."),
                        ["glow"] = Number("Light the surface gives off, 0 to 20 (a lamp's bulb, a screen) (optional)."),
                        ["glass"] = new JsonObject { ["type"] = "boolean", ["description"] = "See-through, for windows (the same as shader glass)." },
                        ["bevel"] = Number("Rounded edges, in metres (optional)."),
                        ["smooth"] = new JsonObject { ["type"] = "integer", ["description"] = "1 to 3: rounds the part off (organic shapes: rocks, cushions, bodies) (optional)." },
                        ["profile"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" } }, ["description"] = "lathe: [[radius, height], ...] from the bottom up, in metres." },
                        ["outline"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" } }, ["description"] = "extrude: [[x, y], ...] the outline, in metres; size's z is its height." },
                        ["path"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" } }, ["description"] = "tube: [[x, y, z], ...] points it passes through, from at; size's x is its thickness." },
                        ["text"] = Schema.Text("text: the words; size's z is the letters' height, y their depth."),
                        ["pose"] = Choice("figure: the pose.", "standing", "walking", "waving", "sitting", "t-pose", "arms-up"),
                        ["height"] = Number("figure: the person's height in metres (1.75 an adult, 1.2 a child)."),
                        ["build"] = Choice("figure: build.", "slim", "average", "athletic", "heavy"),
                        ["body"] = Choice("figure: body shape.", "masculine", "feminine"),
                        ["skin"] = Schema.Text("figure: skin colour (#rrggbb)."),
                        ["shirt"] = Schema.Text("figure: the top's colour."),
                        ["sleeves"] = Choice("figure: the top's sleeves.", "long", "short", "none"),
                        ["trousers"] = Schema.Text("figure: trousers', shorts' or skirt's colour."),
                        ["legs"] = Choice("figure: what covers the legs (a dress is the top's colour).", "long", "shorts", "skirt", "dress"),
                        ["shoes"] = Schema.Text("figure: shoes' colour."),
                        ["hair"] = Choice("figure: hair.", "short", "long", "bun", "ponytail", "curly", "bald"),
                        ["hair_color"] = Schema.Text("figure: hair colour."),
                        ["eyes"] = Schema.Text("figure: eye colour."),
                        ["kind"] = Choice("A light's kind: point (a bulb), spot (points down; turn aims it), area (a panel), sun; a tree's: round, pine, palm, bush.",
                            "point", "spot", "area", "sun", "round", "pine", "palm", "bush"),
                        ["power"] = Number("A light's power in watts (a bulb 60-200, a room's area light 200-1000); a sun's strength 1-10."),
                        ["repeat"] = new JsonObject
                        {
                            ["type"] = "object", ["description"] = "The part again, count times, each step further (fence posts, steps, windows in a row, lamps).",
                            ["properties"] = new JsonObject { ["count"] = new JsonObject { ["type"] = "integer" }, ["step"] = Vector("[x, y, z] in metres.") },
                        },
                    },
                    ["required"] = new JsonArray("shape", "at"),
                },
            },
        };
        Looks(properties);
        return properties;
    }

    private static JsonObject TextureSchema() => new()
    {
        ["name"] = Schema.Text("The texture's name, lowercase with dashes (red-brick, oak-floor): parts use it as their texture."),
        ["pattern"] = Choice("A pattern Blender draws.", Patterns),
        ["prompt"] = Schema.Text("Or a description for the image model to draw it from (a seamless, flat, evenly lit surface: 'weathered oak planks'), when the person wants something no pattern is."),
        ["picture"] = Schema.Text("Or the name of a picture in the chat's files to make it from (a photo the person attached)."),
        ["color"] = Schema.Text("A pattern's colour: a name or #rrggbb."),
        ["color2"] = Schema.Text("A pattern's second colour (optional)."),
        ["size"] = Number("Metres one image covers (default 2)."),
        ["resolution"] = Choice("Pixels a side (default 1024).", "512", "1024", "2048"),
    };

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [
            Schema.Function("make_3d_parts",
                "Builds a 3D model from its parts and gives it to the person: the model (.glb, with its textures and lights) and pictures of it. Each part is a simple " +
                "shape (box, cylinder, cone, sphere, torus, plane, roof, wedge, pyramid) with a size, where its bottom sits, a turn, a colour, and optionally a " +
                "texture (brick, wood, tiles, grass... or one made with make_texture) and a shader (glass, metal, light...); lights are parts too (shape light). " +
                "People are one part each (shape figure: a detailed person with a face, hair, hands and clothes, in a pose); trees too (shape tree). " +
                "For a house: walls (box, texture brick), a roof (roof, texture shingles) on top of them, a door and windows (thin boxes on the walls' faces; " +
                "windows shader glass), a chimney, a lamp. For round things turned on a lathe (a vase, a bottle, a column) use lathe; for shapes of any outline " +
                "use extrude; for pipes and rails use tube. The easiest way to make a model: use it first.",
                PartsSchema(), "parts"),
            Schema.Function("make_texture",
                "Makes a texture as images, for parts to use by its name: its colour, its roughness and its bumps (a normal map), and a preview of it on a sphere " +
                "and a cube. From a pattern Blender draws, from a description the image model draws (prompt), or from a picture in the chat. Then build the " +
                "model with make_3d_parts, giving parts texture: the texture's name.",
                TextureSchema(), "name"),
            Schema.Function("make_3d",
                "Runs a Blender 4.5 Python script (bpy) headless in a sandbox with no network, starting from an empty scene. " +
                "part(shape=..., size=[x, y, z], at=[x, y, z], turn=[x, y, z], color=..., texture=..., shader=...) and light(kind, at, color, power) are defined " +
                "for you, as in make_3d_parts (figure(...) a person, tree(...) a tree), with material(color, metallic, roughness, texture=..., shader=...) and " +
                "make_texture(name, pattern=...); use them, " +
                "and bpy for more (modifiers: bevel, subdivision, array, boolean, mirror, screw; bmesh; curves; your own shader nodes). Set LOOK = 'realistic' " +
                "or LIGHTING = 'studio' at the top to change the pictures. Unless the script writes them itself, the scene comes back as model.glb (patterns " +
                "baked into images) and two pictures; set PRINT = True (and PRINT_SIZE, the longest side in mm) for model.stl and model.3mf to print, TURNTABLE = True " +
                "for turntable.mp4; write model.blend (bpy.ops.wm.save_as_mainfile), an .obj, or your own renders when asked. " +
                $"At most {options.CurrentValue.TimeoutSeconds} seconds a run. The chat's files are in the working directory. print() what you need to read.",
                new JsonObject { ["script"] = Schema.Text("The whole Blender Python script.") }, "script"),
        ],
        "When the person asks for a 3D model, object, scene or printable part, make it with make_3d_parts (simple shapes) or make_3d (a script, for " +
        "what shapes cannot do). A person is one part, shape figure (never build people from boxes and spheres); a tree is shape tree. " +
        "Work in metres, centred at the origin, resting on z = 0, real sizes (a door 2 m high, a person 1.75 m); name the parts and give them colours, and textures and " +
        "shaders where they help (brick walls, wood floors, glass windows, metal handles, lamps that light). For a texture no pattern gives, or one the " +
        "person describes, make it first with make_texture (prompt), then use its name. Look at the pictures that come back and fix what is wrong with " +
        "another run. Never paste a script or the parts into your answer: the person wants the model, not code. When a script fails twice, build the " +
        "model with make_3d_parts instead. To print it, set print (STL and 3MF; say its printed size, and whether every part is closed, as the output says); " +
        "for a video of it, set turntable. Say what you made in a sentence or two; the person sees the pictures and opens the .glb in the chat's 3D viewer.",
        async (name, args, token) =>
        {
            if (name == "make_texture")
            {
                return await TextureAsync(context, args, token);
            }
            string body;
            var look = Schema.Str(args, "look") is "realistic" or "fast" ? Schema.Str(args, "look")! : "auto";
            var lighting = Schema.Str(args, "lighting") is "studio" or "night" ? Schema.Str(args, "lighting")! : "day";
            if (name == "make_3d_parts")
            {
                if (args["parts"] is not JsonArray parts || parts.Count == 0)
                {
                    return new ToolResult("Give the parts: [{\"shape\": \"box\", \"size\": [8, 6, 3], \"at\": [0, 0, 0], \"color\": \"white\"}, ...].", IsError: true);
                }
                if (parts.Count > 500)
                {
                    return new ToolResult("At most 500 parts: use repeat for parts that come again.", IsError: true);
                }
                // The parts go in as JSON, read by Python: nothing the model wrote runs as code.
                var size = args["print_size"] is JsonValue ps && ps.TryGetValue<double>(out var mm) && mm is >= 5 and <= 2000 ? mm.ToString(System.Globalization.CultureInfo.InvariantCulture) : "None";
                body = $"LOOK = {Python(look)}\nLIGHTING = {Python(lighting)}\nPRINT = {(Flag(args, "print") ? "True" : "False")}\nPRINT_SIZE = {size}\n" +
                    $"TURNTABLE = {(Flag(args, "turntable") ? "True" : "False")}\nimport json as _json\nfor _p in _json.loads({Python(parts.ToJsonString())}):\n    part(_p)\n";
            }
            else
            {
                body = Schema.Str(args, "script") ?? "";
                if (body.Trim().Length == 0)
                {
                    return new ToolResult("Give the Blender script in 'script'.", IsError: true);
                }
            }
            return await RunAsync(context, Builder + body + Epilogue, [], name == "make_3d"
                ? "The script failed. Fix the line the error names, or build the model with make_3d_parts instead; do not show code to the person."
                : "Fix the part the error names and run again.", token);
        }));

    /// <summary>
    /// make_texture: a pattern, a picture the image model makes from the description (as the image tool makes it: the
    /// person's picture permission, credit and daily limit), or a picture of the chat's, made into a texture's images.
    /// </summary>
    private async Task<ToolResult> TextureAsync(ToolContext context, JsonObject args, CancellationToken ct)
    {
        var name = TextureName().Replace((Schema.Str(args, "name") ?? "").Trim().ToLowerInvariant(), "-").Trim('-');
        if (name.Length is 0 or > 40)
        {
            return new ToolResult("Give the texture a name: lowercase letters, digits and dashes, up to 40 (red-brick).", IsError: true);
        }
        var pattern = Schema.Str(args, "pattern");
        var prompt = Schema.Str(args, "prompt")?.Trim();
        var picture = Schema.Str(args, "picture")?.Trim();
        if (pattern is not null && !Patterns.Contains(pattern))
        {
            return new ToolResult($"The pattern is one of {string.Join(", ", Patterns)}.", IsError: true);
        }
        var size = args["size"] is JsonValue sv && sv.TryGetValue<double>(out var sz) && sz is > 0.05 and <= 100 ? sz : 2.0;
        var resolution = Schema.Str(args, "resolution") is "512" or "2048" ? Schema.Str(args, "resolution")! : "1024";
        var extra = new List<SandboxFile>();
        var made = new List<ChatAttachment>();
        string source;
        if (pattern is not null)
        {
            source = $"pattern={Python(pattern)}";
        }
        else if (!string.IsNullOrEmpty(prompt))
        {
            if (!ToolRegistry.Allowed(await access.MembershipAsync(context.User, ct), "image") || await images.UnavailableAsync(ct) is { } none)
            {
                return new ToolResult("The image model cannot draw it here: make the texture from a pattern instead.", IsError: true);
            }
            var drawn = await (await images.StartAsync(context, ct)).CallAsync("generate_image", new JsonObject
            {
                ["prompt"] = $"Seamless tileable texture of {prompt}, seen straight on, flat and evenly lit, no shadows, no perspective, no objects, fills the whole picture",
                ["size"] = "1024x1024",
            }, ct);
            if (drawn.IsError || drawn.Files is not [{ Data: { } png } file, ..])
            {
                return new ToolResult($"The image model could not draw it: {drawn.Text}", IsError: true);
            }
            made.Add(file);
            extra.Add(new SandboxFile($"{name}-source.png", png));
            source = $"image={Python($"{name}-source.png")}";
        }
        else if (!string.IsNullOrEmpty(picture))
        {
            source = $"image={Python(picture)}";
        }
        else
        {
            return new ToolResult("Give a pattern, a prompt for the image model, or a picture of the chat's to make the texture from.", IsError: true);
        }
        var code = Builder + $"make_texture({Python(name)}, {source}, color={Python(Schema.Str(args, "color") ?? "grey")}, " +
            $"color2={(Schema.Str(args, "color2") is { } c2 ? Python(c2) : "None")}, size={size.ToString(System.Globalization.CultureInfo.InvariantCulture)}, resolution={resolution})\n";
        var result = await RunAsync(context, code, extra, "Fix what the error names (the picture's name, the pattern) and run again.", ct, made);
        return result.IsError ? result : result with
        {
            Text = result.Text + $"\nUse it: texture \"{name}\" on parts (make_3d_parts), texture_size {size.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} (metres one image covers).",
        };
    }

    /// <summary>A job in the 3D sandbox: the chat's files (and any made for it) in its folder; what it writes comes back to the person.</summary>
    private async Task<ToolResult> RunAsync(ToolContext context, string script, List<SandboxFile> extra, string onFailure, CancellationToken token, List<ChatAttachment>? before = null)
    {
        var code = "import bpy\nbpy.ops.wm.read_factory_settings(use_empty=True)\n" + script;
        SandboxResult result;
        try
        {
            var inputs = (await files.InputsAsync(context.Conversation.Id, token)).Where(f => extra.All(e => e.Name != f.Name)).Concat(extra).ToList();
            result = await blender.RunAsync(code, inputs, options.CurrentValue.TimeoutSeconds, token);
        }
        catch (SandboxException ex)
        {
            return new ToolResult(ex.Message, IsError: true);
        }
        var made = (before ?? []).Concat(result.Files.Select(f => Kept(context.User.Id, f))).ToList();
        db.ChatAttachments.AddRange(made.Where(a => db.Entry(a).State == Microsoft.EntityFrameworkCore.EntityState.Detached));
        await db.SaveChangesAsync(token);
        var max = options.CurrentValue.MaxOutputChars;
        var failed = result.Error is not null || result.TimedOut || result.Killed is not null || result.ExitCode is not 0;
        return new ToolResult(new JsonObject
        {
            ["exit_code"] = result.ExitCode,
            ["output"] = Tail(Blenders(result.Stdout), max),
            ["errors"] = Quiet(result.Stderr) is { Length: > 0 } errors ? Tail(errors, max) : null,
            ["timed_out"] = result.TimedOut ? $"Stopped after {options.CurrentValue.TimeoutSeconds} seconds." : null,
            ["problem"] = result.Error ?? result.Killed,
            ["next"] = failed ? onFailure : null,
            ["files_given_to_the_person"] = made.Count > 0 ? new JsonArray([.. made.Select(a => (JsonNode)$"{a.FileName} ({(a.Kind == "image" ? "shown as a picture" : a.Kind == "video" ? "plays as a video" : a.FileName.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) ? "opens in the 3D viewer" : "to download")})")]) : null,
            ["files_not_kept"] = result.Skipped.Count > 0 ? $"{string.Join(", ", result.Skipped)}: too many or too large" : null,
            ["seconds"] = Math.Round(result.DurationMs / 1000.0, 1),
        }.ToJsonString(Mcp.Plain), failed, made);
    }

    private static bool Flag(JsonObject args, string name) => args[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>A Python string literal of the text (JSON's escapes are Python's).</summary>
    private static string Python(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    [System.Text.RegularExpressions.GeneratedRegex("[^a-z0-9]+")]
    private static partial System.Text.RegularExpressions.Regex TextureName();

    /// <summary>A picture is shown; a model or a .blend is a file to download (never read as text).</summary>
    private static ChatAttachment Kept(Guid userId, SandboxFile f) =>
        Attachments.ImageType(f.Name, f.Bytes) is not null
            ? PythonTool.AsAttachment(userId, f)
            : f.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            ? new ChatAttachment { UserId = userId, FileName = f.Name, Size = f.Bytes.Length, Kind = "video", Data = f.Bytes, Text = "", Origin = "tool", ContentType = "video/mp4" }
            : new ChatAttachment
            {
                UserId = userId, FileName = f.Name, Size = f.Bytes.Length, Kind = "file", Data = f.Bytes, Text = "", Origin = "tool",
                ContentType = Path.GetExtension(f.Name).ToLowerInvariant() switch
                {
                    ".glb" => "model/gltf-binary",
                    ".gltf" => "model/gltf+json",
                    ".stl" => "model/stl",
                    ".3mf" => "model/3mf",
                    ".obj" => "model/obj",
                    _ => "application/octet-stream",
                },
            };

    /// <summary>Blender's own lines (its banner, "Fra:" progress) left out: the script's prints and errors stay.</summary>
    private static string Blenders(string stdout) =>
        string.Join('\n', stdout.Split('\n').Where(l => !l.StartsWith("Fra:", StringComparison.Ordinal) && !l.StartsWith("Blender ", StringComparison.Ordinal)
            && !l.StartsWith("Read prefs", StringComparison.Ordinal) && !l.StartsWith("Saved:", StringComparison.Ordinal) && !l.Contains("Time: ", StringComparison.Ordinal)
            && !l.Contains("| INFO: ", StringComparison.Ordinal) && !l.StartsWith("INFO Draco", StringComparison.Ordinal) && !l.StartsWith("Info: Baking map saved", StringComparison.Ordinal)
            && l.Length > 0));

    /// <summary>Headless OpenGL's complaints (no window to draw in), which mean nothing for a render, left out.</summary>
    private static string Quiet(string stderr) =>
        string.Join('\n', stderr.Split('\n').Where(l => !l.StartsWith("EGL Error", StringComparison.Ordinal))).Trim();

    private static string Tail(string text, int max) => text.Length > max ? "[…]\n" + text[^max..] : text;
}
