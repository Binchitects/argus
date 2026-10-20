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

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [Schema.Function("make_3d",
            "Runs a Blender 4.5 Python script (bpy) headless in a sandbox with no network, starting from an empty scene " +
            "(the default cube, camera and light removed for you). Build the model with bpy: primitives, modifiers (bevel, " +
            "subdivision, array, boolean, mirror, screw), bmesh, curves, materials (Principled BSDF base colour, metallic, " +
            "roughness). Unless the script writes them itself, the scene comes back as model.glb and as two pictures of it; " +
            "write model.blend (bpy.ops.wm.save_as_mainfile), an .stl or .obj, or your own renders when asked. " +
            $"At most {options.CurrentValue.TimeoutSeconds} seconds a run. The chat's files are in the working directory. print() what you need to read.",
            new JsonObject { ["script"] = Schema.Text("The whole Blender Python script.") }, "script")],
        "Use make_3d when the person asks for a 3D model, object, scene, or a printable part. Work in metres, centred at the " +
        "origin, resting on z = 0; give parts names and materials with colours. Look at the pictures that come back and fix " +
        "what is wrong with another run. Say what you made; the person sees the pictures and downloads the .glb.",
        async (_, args, token) =>
        {
            var script = Schema.Str(args, "script") ?? "";
            if (script.Trim().Length == 0)
            {
                return new ToolResult("Give the Blender script in 'script'.", IsError: true);
            }
            var code = "import bpy\nbpy.ops.wm.read_factory_settings(use_empty=True)\n" + script + Epilogue;
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
                ["files_given_to_the_person"] = made.Count > 0 ? new JsonArray([.. made.Select(a => (JsonNode)$"{a.FileName} ({(a.Kind == "image" ? "shown as a picture" : "to download")})")]) : null,
                ["files_not_kept"] = result.Skipped.Count > 0 ? $"{string.Join(", ", result.Skipped)}: too many or too large" : null,
                ["seconds"] = Math.Round(result.DurationMs / 1000.0, 1),
            }.ToJsonString(Mcp.Plain), failed, made);
        }));

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
