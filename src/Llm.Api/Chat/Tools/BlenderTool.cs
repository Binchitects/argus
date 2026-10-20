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
/// 3D models: the model writes a Blender Python script; Blender runs it headless in the 3D sandbox, and what it makes
/// comes back to the chat: the model as glTF (.glb), opened in any 3D viewer or game engine, the .blend when asked, and
/// pictures of it from a few sides, shown in the answer.
/// </summary>
public sealed class BlenderTool(BlenderClient blender, AppDbContext db, PythonTool files, IOptionsMonitor<BlenderOptions> options) : IChatTool
{
    public const string ToolId = "3d";

    public string Id => ToolId;
    public string Title => "3D models";
    public string Description => "Makes 3D models with Blender in a sandbox: the model (.glb) and pictures of it.";
    public string Icon => "box";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult(blender.Unavailable());

    /// <summary>
    /// Before the script: part(...), which builds one part from a plain description (a shape, its size in metres, where its
    /// bottom sits, a turn, a colour), so a script, or make_3d_parts, never has to know Blender's operators.
    /// </summary>
    public const string Builder = """
        import bpy, math, bmesh
        _NAMED = {"red": "#c0392b", "green": "#3a8f3a", "blue": "#2e6fbf", "yellow": "#e8c547", "orange": "#e67e22", "purple": "#8e44ad",
                  "pink": "#e88aa8", "brown": "#7b5233", "black": "#1b1b1b", "white": "#f2f2f2", "grey": "#8a8a8a", "gray": "#8a8a8a",
                  "beige": "#d8c8a8", "wood": "#9c6b3f", "brick": "#9e4a35", "glass": "#a8d0e6", "stone": "#9a9890", "gold": "#d4af37",
                  "silver": "#c0c0c0", "darkgrey": "#4a4a4a", "darkgray": "#4a4a4a", "lightgrey": "#cfcfcf", "lightgray": "#cfcfcf", "sky": "#87ceeb"}
        _MATERIALS = {}

        def _rgba(color):
            c = _NAMED.get(str(color or "grey").strip().lower(), str(color or "#8a8a8a")).lstrip("#")
            if len(c) == 3:
                c = "".join(ch * 2 for ch in c)
            try:
                r, g, b = (int(c[i:i + 2], 16) / 255 for i in (0, 2, 4))
            except ValueError:
                r, g, b = 0.54, 0.54, 0.54
            return (r ** 2.2, g ** 2.2, b ** 2.2, 1.0)

        def material(color="grey", metallic=0.0, roughness=0.5, glass=False):
            key = (str(color), float(metallic), float(roughness), bool(glass))
            if key in _MATERIALS:
                return _MATERIALS[key]
            m = bpy.data.materials.new(name=str(color))
            m.use_nodes = True
            p = m.node_tree.nodes.get("Principled BSDF")
            rgba = _rgba(color)
            p.inputs["Base Color"].default_value = rgba
            p.inputs["Metallic"].default_value = float(metallic)
            p.inputs["Roughness"].default_value = float(roughness)
            if glass:
                p.inputs["Alpha"].default_value = 0.35
                try:
                    m.surface_render_method = "BLENDED"
                except (AttributeError, TypeError):
                    pass
            m.diffuse_color = rgba
            _MATERIALS[key] = m
            return m

        def _roof_mesh(name):
            me = bpy.data.meshes.new(name)
            v = [(-.5, -.5, 0), (.5, -.5, 0), (.5, .5, 0), (-.5, .5, 0), (-.5, 0, 1), (.5, 0, 1)]
            f = [(0, 3, 2, 1), (0, 1, 5, 4), (2, 3, 4, 5), (0, 4, 3), (1, 2, 5)]
            me.from_pydata(v, [], f)
            me.update()
            return me

        def _wedge_mesh(name):
            me = bpy.data.meshes.new(name)
            v = [(-.5, -.5, 0), (.5, -.5, 0), (.5, .5, 0), (-.5, .5, 0), (-.5, .5, 1), (.5, .5, 1)]
            f = [(0, 3, 2, 1), (0, 1, 5, 4), (2, 3, 4, 5), (0, 4, 3), (1, 2, 5)]
            me.from_pydata(v, [], f)
            me.update()
            return me

        SHAPES = ("box", "cylinder", "cone", "sphere", "torus", "plane", "roof", "wedge", "pyramid")

        def part(spec=None, **kw):
            # One part: shape (box, cylinder, cone, sphere, torus, plane, roof, wedge, pyramid), size [x, y, z] in metres,
            # at [x, y, z] where the middle of its bottom sits, turn [x, y, z] in degrees, color (a name or #rrggbb),
            # metallic, roughness, glass, bevel (metres), repeat {count, step [x, y, z]}. Returns the objects made.
            s = dict(spec or {}, **kw)
            shape = str(s.get("shape", "box")).lower()
            if shape not in SHAPES:
                raise ValueError(f"unknown shape {shape!r}: use one of {', '.join(SHAPES)}")
            size = [float(x) for x in (s.get("size") or [1, 1, 1])]
            if len(size) == 1:
                size = size * 3
            if len(size) != 3 or min(size) < 0:
                raise ValueError(f"size is [x, y, z] in metres, not {s.get('size')!r}")
            at = [float(x) for x in (s.get("at") or s.get("position") or [0, 0, 0])]
            turn = [math.radians(float(x)) for x in (s.get("turn") or s.get("rotation") or [0, 0, 0])]
            name = str(s.get("name") or shape)
            rep = s.get("repeat") or {}
            count = max(1, min(int(rep.get("count", 1)), 200))
            step = [float(x) for x in (rep.get("step") or [0, 0, 0])]
            made = []
            for i in range(count):
                x, y, z = (at[0] + step[0] * i, at[1] + step[1] * i, at[2] + step[2] * i)
                sx, sy, sz = size
                if shape in ("roof", "wedge"):
                    ob = bpy.data.objects.new(name, _roof_mesh(name) if shape == "roof" else _wedge_mesh(name))
                    bpy.context.scene.collection.objects.link(ob)
                    ob.location = (x, y, z)
                else:
                    if shape == "box":
                        bpy.ops.mesh.primitive_cube_add(size=1)
                    elif shape == "cylinder":
                        bpy.ops.mesh.primitive_cylinder_add(radius=0.5, depth=1, vertices=48)
                    elif shape == "cone":
                        bpy.ops.mesh.primitive_cone_add(radius1=0.5, radius2=0, depth=1, vertices=48)
                    elif shape == "pyramid":
                        bpy.ops.mesh.primitive_cone_add(radius1=math.sqrt(0.5), radius2=0, depth=1, vertices=4, rotation=(0, 0, math.pi / 4))
                        bpy.ops.object.transform_apply(rotation=True)
                    elif shape == "sphere":
                        bpy.ops.mesh.primitive_uv_sphere_add(radius=0.5, segments=48, ring_count=24)
                    elif shape == "torus":
                        bpy.ops.mesh.primitive_torus_add(major_radius=0.375, minor_radius=0.125)
                        sz = sz / 0.25 if sz else 1
                    elif shape == "plane":
                        bpy.ops.mesh.primitive_plane_add(size=1)
                    ob = bpy.context.active_object
                    ob.name = name
                    ob.location = (x, y, z + (size[2] / 2 if shape != "plane" else 0))
                if shape == "plane":
                    ob.scale = (sx or 1, sy or 1, 1)
                else:
                    ob.scale = (sx or 1, sy or 1, sz or 1)
                ob.rotation_euler = turn
                ob.data.materials.append(material(s.get("color", "grey"), s.get("metallic", 0), s.get("roughness", 0.5), bool(s.get("glass"))))
                if float(s.get("bevel") or 0) > 0:
                    bpy.context.view_layer.objects.active = ob
                    bpy.ops.object.transform_apply(scale=True)
                    mod = ob.modifiers.new("bevel", "BEVEL")
                    mod.width = float(s["bevel"])
                    mod.segments = 3
                made.append(ob)
            return made

        """;

    /// <summary>
    /// After the script: unless it wrote them itself, the scene as model.glb and two pictures of it (front and three
    /// quarters), lit and framed, with Workbench (quick on the CPU).
    /// </summary>
    public const string Epilogue = """


        # --- added by the 3D tool: the model as .glb, and pictures of it, unless the script made them ---
        import bpy as _bpy, os as _os, math as _math, mathutils as _mu
        _files = set(_os.listdir("."))
        _meshes = [o for o in _bpy.context.scene.objects if o.type in {"MESH", "CURVE", "SURFACE", "META", "FONT"} and not o.hide_render]
        if _meshes and not any(f.lower().endswith((".glb", ".gltf")) for f in _files):
            _bpy.ops.export_scene.gltf(filepath=_os.path.abspath("model.glb"), export_format="GLB", use_selection=False)
        if _meshes and not any(f.lower().endswith((".png", ".jpg")) for f in _files):
            _lo = _mu.Vector((min(min((o.matrix_world @ _mu.Vector(c))[i] for c in o.bound_box) for o in _meshes) for i in range(3)))
            _hi = _mu.Vector((max(max((o.matrix_world @ _mu.Vector(c))[i] for c in o.bound_box) for o in _meshes) for i in range(3)))
            _mid, _size = (_lo + _hi) / 2, max((_hi - _lo).length, 0.01)
            _sc = _bpy.context.scene
            # Workbench shows a material's viewport colour: the node colour the script set, copied there.
            for _m in _bpy.data.materials:
                _p = _m.node_tree.nodes.get("Principled BSDF") if _m.use_nodes and _m.node_tree else None
                if _p is not None:
                    _m.diffuse_color = tuple(_p.inputs["Base Color"].default_value)
                    _m.metallic, _m.roughness = _p.inputs["Metallic"].default_value, _p.inputs["Roughness"].default_value
            _sc.render.engine = "BLENDER_WORKBENCH"
            _sc.display.shading.light = "STUDIO"
            _sc.display.shading.color_type = "MATERIAL"
            _sc.render.resolution_x, _sc.render.resolution_y = 1024, 768
            _sc.render.film_transparent = False
            _cam = _bpy.data.objects.new("_tool_camera", _bpy.data.cameras.new("_tool_camera"))
            _sc.collection.objects.link(_cam)
            _sc.camera = _cam
            for _name, _yaw, _pitch in (("front", 0.0, 0.15), ("three-quarter", 0.8, 0.45)):
                _dir = _mu.Vector((_math.sin(_yaw) * _math.cos(_pitch), -_math.cos(_yaw) * _math.cos(_pitch), _math.sin(_pitch)))
                _cam.location = _mid + _dir * _size * 1.6
                _cam.rotation_euler = (_mid - _cam.location).to_track_quat("-Z", "Y").to_euler()
                _sc.render.filepath = _os.path.abspath(f"view-{_name}.png")
                _bpy.ops.render.render(write_still=True)
        """;

    private static JsonObject Vector(string description) => new()
    {
        ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" }, ["minItems"] = 3, ["maxItems"] = 3, ["description"] = description,
    };

    /// <summary>make_3d_parts' parameters: the parts, each a shape with a size, a place, a turn and a colour.</summary>
    private static JsonObject PartsSchema() => new()
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
                    ["name"] = Schema.Text("What the part is: walls, roof, door, window-left..."),
                    ["shape"] = new JsonObject
                    {
                        ["type"] = "string", ["enum"] = new JsonArray("box", "cylinder", "cone", "sphere", "torus", "plane", "roof", "wedge", "pyramid"),
                        ["description"] = "roof: a gable roof, its ridge along x; wedge: a ramp rising towards +y; the rest as named.",
                    },
                    ["size"] = Vector("[x, y, z]: width, depth, height in metres (a cylinder's or sphere's x and y are its diameters)."),
                    ["at"] = Vector("[x, y, z]: where the middle of the part's bottom sits. A part on top of another starts at that one's height."),
                    ["turn"] = Vector("[x, y, z] rotation in degrees (optional)."),
                    ["color"] = Schema.Text("A name (red, white, brick, wood, glass, stone, grey...) or #rrggbb."),
                    ["metallic"] = new JsonObject { ["type"] = "number", ["description"] = "0 to 1 (optional)." },
                    ["roughness"] = new JsonObject { ["type"] = "number", ["description"] = "0 to 1 (optional)." },
                    ["glass"] = new JsonObject { ["type"] = "boolean", ["description"] = "See-through, for windows (optional)." },
                    ["bevel"] = new JsonObject { ["type"] = "number", ["description"] = "Rounded edges, in metres (optional)." },
                    ["repeat"] = new JsonObject
                    {
                        ["type"] = "object", ["description"] = "The part again, count times, each step further (fence posts, steps, windows in a row).",
                        ["properties"] = new JsonObject { ["count"] = new JsonObject { ["type"] = "integer" }, ["step"] = Vector("[x, y, z] in metres.") },
                    },
                },
                ["required"] = new JsonArray("shape", "size", "at"),
            },
        },
    };

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [
            Schema.Function("make_3d_parts",
                "Builds a 3D model from its parts and gives it to the person: the model (.glb) and pictures of it. Each part is a simple shape " +
                "(box, cylinder, cone, sphere, torus, plane, roof, wedge, pyramid) with a size, where its bottom sits, a turn and a colour. " +
                "For a house: walls (box), a roof (roof) on top of them, a door and windows (thin boxes on the walls' faces), a chimney. " +
                "The easiest way to make a model: use it first.",
                PartsSchema(), "parts"),
            Schema.Function("make_3d",
                "Runs a Blender 4.5 Python script (bpy) headless in a sandbox with no network, starting from an empty scene. " +
                "part(shape=..., size=[x, y, z], at=[x, y, z], turn=[x, y, z], color=...) is defined for you, as in make_3d_parts, and " +
                "material(color, metallic, roughness); use them, and bpy for more (modifiers: bevel, subdivision, array, boolean, mirror, " +
                "screw; bmesh; curves). Unless the script writes them itself, the scene comes back as model.glb and as two pictures of it; " +
                "write model.blend (bpy.ops.wm.save_as_mainfile), an .stl or .obj, or your own renders when asked. " +
                $"At most {options.CurrentValue.TimeoutSeconds} seconds a run. The chat's files are in the working directory. print() what you need to read.",
                new JsonObject { ["script"] = Schema.Text("The whole Blender Python script.") }, "script"),
        ],
        "When the person asks for a 3D model, object, scene or printable part, make it with make_3d_parts (simple shapes) or make_3d (a script, for " +
        "what shapes cannot do). Work in metres, centred at the origin, resting on z = 0; name the parts and give them colours. Look at the " +
        "pictures that come back and fix what is wrong with another run. Never paste a script or the parts into your answer: the person wants " +
        "the model, not code. When a script fails twice, build the model with make_3d_parts instead. Say what you made in a sentence or two; " +
        "the person sees the pictures and downloads the .glb.",
        async (name, args, token) =>
        {
            string body;
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
                body = "import json as _json\nfor _p in _json.loads(" + Python(parts.ToJsonString()) + "):\n    part(_p)\n";
            }
            else
            {
                body = Schema.Str(args, "script") ?? "";
                if (body.Trim().Length == 0)
                {
                    return new ToolResult("Give the Blender script in 'script'.", IsError: true);
                }
            }
            var code = "import bpy\nbpy.ops.wm.read_factory_settings(use_empty=True)\n" + Builder + body + Epilogue;
            SandboxResult result;
            try
            {
                result = await blender.RunAsync(code, await files.InputsAsync(context.Conversation.Id, token), options.CurrentValue.TimeoutSeconds, token);
            }
            catch (SandboxException ex)
            {
                return new ToolResult(ex.Message, IsError: true);
            }
            var made = result.Files.Select(f => Kept(context.User.Id, f)).ToList();
            db.ChatAttachments.AddRange(made);
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
                ["next"] = failed
                    ? name == "make_3d" ? "The script failed. Fix the line the error names, or build the model with make_3d_parts instead; do not show code to the person." : "Fix the part the error names and run again."
                    : null,
                ["files_given_to_the_person"] = made.Count > 0 ? new JsonArray([.. made.Select(a => (JsonNode)$"{a.FileName} ({(a.Kind == "image" ? "shown as a picture" : "to download")})")]) : null,
                ["files_not_kept"] = result.Skipped.Count > 0 ? $"{string.Join(", ", result.Skipped)}: too many or too large" : null,
                ["seconds"] = Math.Round(result.DurationMs / 1000.0, 1),
            }.ToJsonString(Mcp.Plain), failed, made);
        }));

    /// <summary>A Python string literal of the text (JSON's escapes are Python's, but for non-ASCII left as is).</summary>
    private static string Python(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    /// <summary>A picture is shown; a model or a .blend is a file to download (never read as text).</summary>
    private static ChatAttachment Kept(Guid userId, SandboxFile f) =>
        Attachments.ImageType(f.Name, f.Bytes) is not null
            ? PythonTool.AsAttachment(userId, f)
            : new ChatAttachment
            {
                UserId = userId, FileName = f.Name, Size = f.Bytes.Length, Kind = "file", Data = f.Bytes, Text = "", Origin = "tool",
                ContentType = Path.GetExtension(f.Name).ToLowerInvariant() switch
                {
                    ".glb" => "model/gltf-binary",
                    ".gltf" => "model/gltf+json",
                    ".stl" => "model/stl",
                    ".obj" => "model/obj",
                    _ => "application/octet-stream",
                },
            };

    /// <summary>Blender's own lines (its banner, "Fra:" progress) left out: the script's prints and errors stay.</summary>
    private static string Blenders(string stdout) =>
        string.Join('\n', stdout.Split('\n').Where(l => !l.StartsWith("Fra:", StringComparison.Ordinal) && !l.StartsWith("Blender ", StringComparison.Ordinal)
            && !l.StartsWith("Read prefs", StringComparison.Ordinal) && !l.StartsWith("Saved:", StringComparison.Ordinal) && !l.Contains("Time: ", StringComparison.Ordinal)));

    /// <summary>Headless OpenGL's complaints (no window to draw in), which mean nothing for a render, left out.</summary>
    private static string Quiet(string stderr) =>
        string.Join('\n', stderr.Split('\n').Where(l => !l.StartsWith("EGL Error", StringComparison.Ordinal))).Trim();

    private static string Tail(string text, int max) => text.Length > max ? "[…]\n" + text[^max..] : text;
}
