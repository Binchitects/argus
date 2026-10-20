

# --- added by the 3D tool after the script: pictures of the model, and the model as model.glb, unless the script made them ---
import bpy as _bpy, os as _os, math as _math, mathutils as _mu

_LOOK = globals().get("LOOK", "auto")
# day: a sky and a sun; studio: soft light from three sides, a plain backdrop; night: a dark sky, only the model's own lights.
_LIGHTING = globals().get("LIGHTING", "day")
_new = set(_os.listdir(".")) - globals().get("_ARENA_INPUTS", set())
_sc = _bpy.context.scene
# Parents and moves the script made take effect before the model is measured.
_bpy.context.view_layer.update()
_meshes = [o for o in _sc.objects if o.type in {"MESH", "CURVE", "SURFACE", "META", "FONT"} and not o.hide_render]
_want_glb = bool(_meshes) and not any(f.lower().endswith((".glb", ".gltf")) for f in _new)
_want_png = bool(_meshes) and not any(f.lower().endswith((".png", ".jpg")) for f in _new)
_BASIC = {"BSDF_PRINCIPLED", "OUTPUT_MATERIAL"}


def _principled(m):
    return m.node_tree.nodes.get("Principled BSDF") if m and m.use_nodes and m.node_tree else None


def _fancy(m):
    # A material Workbench cannot show: a texture, a node set-up of its own, glass, light.
    p = _principled(m)
    if m is None or p is None:
        return False
    if m.get("arena_textured") or any(n.type not in _BASIC for n in m.node_tree.nodes):
        return True
    w = p.inputs.get("Transmission Weight")
    e = p.inputs.get("Emission Strength")
    return bool((w and w.default_value > 0) or (e and e.default_value > 0))


def _procedural(m):
    # Its colour, roughness or bumps come from nodes other than images: they are baked into images for the .glb.
    p = _principled(m)
    if p is None:
        return False
    for name in ("Base Color", "Roughness", "Normal"):
        inp = p.inputs.get(name)
        if not (inp and inp.is_linked):
            continue
        node = inp.links[0].from_node
        # A normal map's own image is an image too.
        if node.type == "NORMAL_MAP" and node.inputs["Color"].is_linked:
            node = node.inputs["Color"].links[0].from_node
        if node.type != "TEX_IMAGE":
            return True
    return False


_materials = {s.material for o in _meshes for s in o.material_slots if s.material}
# The model's own lights, or lighting other than day, need Cycles too: Workbench lights nothing.
_realistic = _LOOK == "realistic" or (_LOOK == "auto" and (any(_fancy(m) for m in _materials) or _LIGHTING != "day"
                                                            or any(o.type == "LIGHT" for o in _sc.objects)))

if _want_png:
    _lo = _mu.Vector((min(min((o.matrix_world @ _mu.Vector(c))[i] for c in o.bound_box) for o in _meshes) for i in range(3)))
    _hi = _mu.Vector((max(max((o.matrix_world @ _mu.Vector(c))[i] for c in o.bound_box) for o in _meshes) for i in range(3)))
    _mid, _size = (_lo + _hi) / 2, max((_hi - _lo).length, 0.01)
    _sc.render.resolution_x, _sc.render.resolution_y = 1024, 768
    _sc.render.film_transparent = False
    if _realistic:
        # Cycles on the CPU: textures, glass and light as they are; a sky and a sun when the script set none.
        _sc.render.engine = "CYCLES"
        _sc.cycles.device = "CPU"
        _sc.cycles.samples = int(globals().get("SAMPLES", 16))
        try:
            _sc.cycles.use_denoising = True
        except AttributeError:
            pass
        if _sc.world is None or not _sc.world.use_nodes:
            _sc.world = _sc.world or _bpy.data.worlds.new("_tool_world")
            _sc.world.use_nodes = True
            _bg = _sc.world.node_tree.nodes.get("Background")
            _bg.inputs["Color"].default_value = {"night": (0.02, 0.025, 0.05, 1), "studio": (0.8, 0.8, 0.8, 1)}.get(_LIGHTING, (0.55, 0.65, 0.8, 1))
            _bg.inputs["Strength"].default_value = {"night": 0.15, "studio": 0.6}.get(_LIGHTING, 0.9)
        _span = max((_hi - _lo).length, 1.0)
        if _LIGHTING == "studio":
            # A key, a fill and a rim light, sized to the model.
            for _n, _yaw, _pitch, _e in (("key", -0.7, 0.7, 1.0), ("fill", 0.9, 0.4, 0.4), ("rim", 3.0, 0.8, 0.6)):
                _l = _bpy.data.objects.new(f"_tool_{_n}", _bpy.data.lights.new(f"_tool_{_n}", "AREA"))
                _l.data.size = _span * 0.8
                _l.data.energy = 120 * _e * _span * _span
                _d = _mu.Vector((_math.sin(_yaw) * _math.cos(_pitch), -_math.cos(_yaw) * _math.cos(_pitch), _math.sin(_pitch)))
                _l.location = _mid + _d * _span * 1.5
                _l.rotation_euler = (_mid - _l.location).to_track_quat("-Z", "Y").to_euler()
                _sc.collection.objects.link(_l)
        elif _LIGHTING != "night" and not any(o.type == "LIGHT" and o.data.type == "SUN" for o in _sc.objects):
            _sun = _bpy.data.objects.new("_tool_sun", _bpy.data.lights.new("_tool_sun", "SUN"))
            _sun.data.energy = 3.5
            _sun.data.angle = _math.radians(8)
            _sun.rotation_euler = (_math.radians(50), _math.radians(10), _math.radians(35))
            _sc.collection.objects.link(_sun)
    else:
        # Workbench shows a material's viewport colour: the node colour the script set, copied there.
        for _m in _materials:
            _p = _principled(_m)
            if _p is not None and not _p.inputs["Base Color"].is_linked:
                _m.diffuse_color = tuple(_p.inputs["Base Color"].default_value)
                _m.metallic, _m.roughness = _p.inputs["Metallic"].default_value, _p.inputs["Roughness"].default_value
        _sc.render.engine = "BLENDER_WORKBENCH"
        _sc.display.shading.light = "STUDIO"
        _sc.display.shading.color_type = "MATERIAL"
    _cam = _bpy.data.objects.new("_tool_camera", _bpy.data.cameras.new("_tool_camera"))
    _sc.collection.objects.link(_cam)
    _sc.camera = _cam
    # Far enough that the whole model, as a ball round it, fits the picture's narrower side, with a margin.
    _cam.data.lens = 50
    _half = _math.atan(18 / 50 * min(1.0, _sc.render.resolution_y / _sc.render.resolution_x))
    _distance = (_size / 2) / _math.sin(_half) * 1.05
    _cam.data.clip_start, _cam.data.clip_end = _distance / 100, _distance * 10
    for _name, _yaw, _pitch in (("front", 0.0, 0.15), ("three-quarter", 0.8, 0.45)):
        _dir = _mu.Vector((_math.sin(_yaw) * _math.cos(_pitch), -_math.cos(_yaw) * _math.cos(_pitch), _math.sin(_pitch)))
        _cam.location = _mid + _dir * _distance
        _cam.rotation_euler = (_mid - _cam.location).to_track_quat("-Z", "Y").to_euler()
        _sc.render.filepath = _os.path.abspath(f"view-{_name}.png")
        _bpy.ops.render.render(write_still=True)

if _want_glb:
    # glTF holds images, not Blender's patterns: each part with a pattern gets its colour, roughness and bumps baked into images.
    _targets = [o for o in _meshes if o.type == "MESH" and any(_procedural(s.material) for s in o.material_slots if s.material)][:40]
    if _targets:
        _sc.render.engine = "CYCLES"
        _sc.cycles.device = "CPU"
        _sc.cycles.samples = 1
        _bake = _sc.render.bake
        _bake.use_pass_direct, _bake.use_pass_indirect, _bake.use_pass_color = False, False, True
        _bake.margin = 4
        _res = 1024 if len(_targets) <= 6 else 512
        for _o in _targets:
            for _s in _o.material_slots:
                if _s.material:
                    _s.material = _s.material.copy()
            _me = _o.data
            _main = _me.uv_layers.active
            _uv = _me.uv_layers.new(name="bake")
            for _l in _me.uv_layers:
                _l.active_render = _l.name == (_main.name if _main else "bake")
            _me.uv_layers.active = _uv
            for _x in _sc.objects:
                _x.select_set(False)
            _o.select_set(True)
            _bpy.context.view_layer.objects.active = _o
            _bpy.ops.object.mode_set(mode="EDIT")
            _bpy.ops.mesh.select_all(action="SELECT")
            _bpy.ops.uv.smart_project(island_margin=0.02)
            _bpy.ops.object.mode_set(mode="OBJECT")
            _passes = [("Base Color", "DIFFUSE", True)]
            if any((_principled(s.material) and _principled(s.material).inputs["Roughness"].is_linked) for s in _o.material_slots if s.material):
                _passes.append(("Roughness", "ROUGHNESS", False))
            if any((_principled(s.material) and _principled(s.material).inputs["Normal"].is_linked) for s in _o.material_slots if s.material):
                _passes.append(("Normal", "NORMAL", False))
            _made = {}
            for _input, _type, _colour in _passes:
                _img = _bpy.data.images.new(f"{_o.name}-{_input.lower().replace(' ', '-')}", _res, _res)
                if not _colour:
                    _img.colorspace_settings.name = "Non-Color"
                for _s in _o.material_slots:
                    if _s.material and _principled(_s.material):
                        _t = _s.material.node_tree.nodes.new("ShaderNodeTexImage")
                        _t.image = _img
                        _s.material.node_tree.nodes.active = _t
                        _made.setdefault(_input, []).append((_s.material, _t))
                _bpy.ops.object.bake(type=_type)
                _img.pack()
            # The images in place of the patterns, read through the baking UVs.
            for _input, _nodes in _made.items():
                for _m, _t in _nodes:
                    _nt, _p = _m.node_tree, _principled(_m)
                    _uvn = _nt.nodes.new("ShaderNodeUVMap")
                    _uvn.uv_map = "bake"
                    _nt.links.new(_uvn.outputs["UV"], _t.inputs["Vector"])
                    if _input == "Normal":
                        _nm = _nt.nodes.new("ShaderNodeNormalMap")
                        _nm.uv_map = "bake"
                        _nt.links.new(_t.outputs["Color"], _nm.inputs["Color"])
                        _nt.links.new(_nm.outputs["Normal"], _p.inputs["Normal"])
                    else:
                        _nt.links.new(_t.outputs["Color"], _p.inputs[_input])
            for _l in _me.uv_layers:
                _l.active_render = _l.name == "bake"
        print(f"Textures baked into the model for {len(_targets)} parts.")
    # The model's lights go too (glTF lights); the tool's own (the sun, the studio's) do not.
    for _o in [o for o in _sc.objects if o.name.startswith("_tool_")]:
        _bpy.data.objects.remove(_o)
    _bpy.ops.export_scene.gltf(filepath=_os.path.abspath("model.glb"), export_format="GLB", use_selection=False, export_lights=True)
