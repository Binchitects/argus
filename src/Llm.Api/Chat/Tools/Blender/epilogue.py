

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

# --- PRINT: model.stl and model.3mf to print it from, in millimetres, resting on the plate; how it measures and whether it is closed ---
if globals().get("PRINT") and _meshes:
    import bmesh as _bmesh, struct as _struct, zipfile as _zipfile
    from xml.sax.saxutils import quoteattr as _q
    _dg = _bpy.context.evaluated_depsgraph_get()
    _parts = []
    for _o in _meshes:
        _ev = _o.evaluated_get(_dg)
        try:
            _me = _ev.to_mesh()
        except RuntimeError:
            continue
        _me.calc_loop_triangles()
        _verts = [_o.matrix_world @ _v.co for _v in _me.vertices]
        _tris = [tuple(_t.vertices) for _t in _me.loop_triangles]
        _b = _bmesh.new()
        _b.from_mesh(_me)
        _open = sum(1 for _e in _b.edges if not _e.is_manifold)
        _b.free()
        _m = _o.active_material
        _p = _principled(_m)
        _rgb = tuple(_p.inputs["Base Color"].default_value)[:3] if _p is not None else (tuple(_m.diffuse_color)[:3] if _m else (0.8, 0.8, 0.8))
        if _tris:
            _parts.append((_o.name, _verts, _tris, _open, _rgb))
        _ev.to_mesh_clear()
    if _parts:
        _lo = [min(v[i] for _n, vs, _t, _x, _c in _parts for v in vs) for i in range(3)]
        _hi = [max(v[i] for _n, vs, _t, _x, _c in _parts for v in vs) for i in range(3)]
        _longest = max(_hi[i] - _lo[i] for i in range(3)) * 1000
        # Its real size when it fits a printer's plate (PRINT_SIZE: the longest side in millimetres), else made smaller.
        _want = float(globals().get("PRINT_SIZE") or (_longest if _longest <= 250 else 150))
        _k = 1000 * (_want / _longest if _longest > 0 else 1)
        _place = lambda v: ((v[0] - (_lo[0] + _hi[0]) / 2) * _k, (v[1] - (_lo[1] + _hi[1]) / 2) * _k, (v[2] - _lo[2]) * _k)
        with open("model.stl", "wb") as _f:
            _f.write(b"Argus Arena model".ljust(80, b" "))
            _f.write(_struct.pack("<I", sum(len(t) for _n, _v, t, _x, _c in _parts)))
            for _n, _vs, _ts, _x, _c in _parts:
                _pv = [_place(v) for v in _vs]
                for _a, _b2, _c2 in _ts:
                    _p1, _p2, _p3 = _mu.Vector(_pv[_a]), _mu.Vector(_pv[_b2]), _mu.Vector(_pv[_c2])
                    _nrm = (_p2 - _p1).cross(_p3 - _p1).normalized()
                    _f.write(_struct.pack("<12fH", *_nrm, *_p1, *_p2, *_p3, 0))
        _xml = ['<?xml version="1.0" encoding="UTF-8"?>',
                '<model unit="millimeter" xml:lang="en-US" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">', '<resources>',
                '<basematerials id="1">']
        _xml += [f'<base name={_q(n)} displaycolor="#{"".join(f"{min(255, max(0, round(c ** (1 / 2.2) * 255))):02X}" for c in rgb)}"/>' for n, _v, _t, _x, rgb in _parts]
        _xml.append('</basematerials>')
        for _i, (_n, _vs, _ts, _x, _c) in enumerate(_parts):
            _xml.append(f'<object id="{_i + 2}" type="model" name={_q(_n)} pid="1" pindex="{_i}"><mesh><vertices>')
            _xml += ['<vertex x="%.4f" y="%.4f" z="%.4f"/>' % _place(v) for v in _vs]
            _xml.append('</vertices><triangles>')
            _xml += [f'<triangle v1="{a}" v2="{b}" v3="{c}"/>' for a, b, c in _ts]
            _xml.append('</triangles></mesh></object>')
        _xml += ['</resources>', '<build>'] + [f'<item objectid="{_i + 2}"/>' for _i in range(len(_parts))] + ['</build>', '</model>']
        with _zipfile.ZipFile("model.3mf", "w", _zipfile.ZIP_DEFLATED) as _z:
            _z.writestr("[Content_Types].xml", '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                        '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
                        '<Default Extension="model" ContentType="application/vnd.ms-package.3dmanufacturing-3dmodel+xml"/></Types>')
            _z.writestr("_rels/.rels", '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                        '<Relationship Target="/3D/3dmodel.model" Id="rel0" Type="http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"/></Relationships>')
            _z.writestr("3D/3dmodel.model", "\n".join(_xml))
        _size = " × ".join(f"{(_hi[i] - _lo[i]) * _k:.1f}" for i in range(3))
        _openparts = [n for n, _v, _t, x, _c in _parts if x]
        _scale = " (its real size)" if abs(_k - 1000) <= 1 else f" (1:{1000 / _k:.3g} of its real size)" if _k < 1000 else f" ({_k / 1000:.3g} times its real size)"
        print(f"Print files: model.stl and model.3mf, {_size} mm{_scale}"
              + f", {sum(len(t) for _n, _v, t, _x, _c in _parts):,} triangles. "
              + ("Every part is closed (watertight)." if not _openparts else f"Not closed (a slicer may repair it, or close it): {', '.join(_openparts[:8])}."))

# --- TURNTABLE: the model turning once round, as turntable.mp4 ---
if globals().get("TURNTABLE") and _meshes:
    _lo = _mu.Vector((min(min((o.matrix_world @ _mu.Vector(c))[i] for c in o.bound_box) for o in _meshes) for i in range(3)))
    _hi = _mu.Vector((max(max((o.matrix_world @ _mu.Vector(c))[i] for c in o.bound_box) for o in _meshes) for i in range(3)))
    _mid, _size = (_lo + _hi) / 2, max((_hi - _lo).length, 0.01)
    if _sc.render.engine not in {"CYCLES", "BLENDER_WORKBENCH"}:
        _sc.render.engine = "BLENDER_WORKBENCH"
    _cycles = _sc.render.engine == "CYCLES"
    if _cycles:
        _sc.cycles.samples = min(_sc.cycles.samples, 8)
    else:
        # A light backdrop, as the pictures have (a video keeps no see-through background).
        _sc.world = _sc.world or _bpy.data.worlds.new("_tool_world")
        _sc.world.color = (0.92, 0.93, 0.95)
        _sc.render.film_transparent = False
    _sc.render.resolution_x, _sc.render.resolution_y = (480, 360) if _cycles else (640, 480)
    _frames = 72 if _cycles else 96
    _pivot = _bpy.data.objects.new("_tool_pivot", None)
    _sc.collection.objects.link(_pivot)
    _pivot.location = _mid
    _tcam = _bpy.data.objects.new("_tool_turn", _bpy.data.cameras.new("_tool_turn"))
    _sc.collection.objects.link(_tcam)
    _tcam.data.lens = 50
    _half = _math.atan(18 / 50 * min(1.0, _sc.render.resolution_y / _sc.render.resolution_x))
    _distance = (_size / 2) / _math.sin(_half) * 1.05
    _tcam.data.clip_start, _tcam.data.clip_end = _distance / 100, _distance * 10
    _dir = _mu.Vector((0, -_math.cos(0.35), _math.sin(0.35)))
    _tcam.location = _dir * _distance
    _tcam.rotation_euler = (-_dir).to_track_quat("-Z", "Y").to_euler()
    _tcam.parent = _pivot
    _sc.camera = _tcam
    _sc.frame_start, _sc.frame_end = 1, _frames
    _sc.render.fps = 24
    # Turning at an even speed: new keys are linear (the last frame one step short of where the first one is).
    _bpy.context.preferences.edit.keyframe_new_interpolation_type = "LINEAR"
    _pivot.rotation_euler = (0, 0, 0)
    _pivot.keyframe_insert("rotation_euler", index=2, frame=1)
    _pivot.rotation_euler = (0, 0, 2 * _math.pi * (_frames - 1) / _frames)
    _pivot.keyframe_insert("rotation_euler", index=2, frame=_frames)
    _sc.render.image_settings.file_format = "FFMPEG"
    _sc.render.ffmpeg.format = "MPEG4"
    _sc.render.ffmpeg.codec = "H264"
    _sc.render.ffmpeg.constant_rate_factor = "MEDIUM"
    _sc.render.filepath = _os.path.abspath("turntable.mp4")
    _bpy.ops.render.render(animation=True)
    print(f"Turntable: turntable.mp4, {_frames / 24:.0f} seconds.")
