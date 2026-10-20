# --- added by the 3D tool before the script: part(), material() and texture() for models made of simple shapes ---
# part() builds one part from a plain description: a shape, its size in metres, where the middle of its bottom sits, a
# turn, a colour, a texture (a pattern drawn by Blender, or images made before) and a shader (glass, metal, light...).
import bpy, math, bmesh, os
from mathutils import Matrix

# The chat's files, there before the script: what it writes is what comes back.
_ARENA_INPUTS = set(os.listdir("."))

_NAMED = {"red": "#c0392b", "green": "#3a8f3a", "blue": "#2e6fbf", "yellow": "#e8c547", "orange": "#e67e22", "purple": "#8e44ad",
          "pink": "#e88aa8", "brown": "#7b5233", "black": "#1b1b1b", "white": "#f2f2f2", "grey": "#8a8a8a", "gray": "#8a8a8a",
          "beige": "#d8c8a8", "wood": "#9c6b3f", "brick": "#9e4a35", "glass": "#a8d0e6", "stone": "#9a9890", "gold": "#d4af37",
          "silver": "#c0c0c0", "darkgrey": "#4a4a4a", "darkgray": "#4a4a4a", "lightgrey": "#cfcfcf", "lightgray": "#cfcfcf",
          "sky": "#87ceeb", "grass": "#4f7f32", "sand": "#d9c48f", "water": "#3d7fa6", "concrete": "#a3a19b", "slate": "#4b5560",
          "terracotta": "#c4673f", "cream": "#f3e9d2", "navy": "#1f2f57", "teal": "#2a8c88", "olive": "#6b7a35", "copper": "#b87333",
          # A pattern's own colour, when a part names the pattern as its colour.
          "shingles": "#9a4f3c", "tiles": "#d9d4c7", "marble": "#eeeeec", "metal": "#b5b5b5", "plaster": "#e6e0d4", "fabric": "#5a6b8c",
          "checker": "#e0e0e0", "noise": "#8a8a8a"}
_MATERIALS = {}
PATTERNS = ("brick", "tiles", "shingles", "checker", "wood", "stone", "concrete", "plaster", "grass", "marble", "metal", "fabric", "noise")
SHADERS = ("matte", "plastic", "metal", "mirror", "glass", "light", "ceramic", "rubber")
SHAPES = ("box", "cylinder", "cone", "sphere", "torus", "plane", "roof", "wedge", "pyramid", "capsule", "lathe", "extrude", "tube", "text", "tree",
          "light", "figure")
LIGHTS = ("point", "spot", "area", "sun")
_WARM = {"warm": "#ffd6a0", "daylight": "#fff4e5", "cool": "#d6e6ff", "candle": "#ffb46b"}


def _rgba(color):
    c = _NAMED.get(str(color or "grey").strip().lower(), str(color or "#8a8a8a")).lstrip("#")
    if len(c) == 3:
        c = "".join(ch * 2 for ch in c)
    try:
        r, g, b = (int(c[i:i + 2], 16) / 255 for i in (0, 2, 4))
    except ValueError:
        r, g, b = 0.54, 0.54, 0.54
    return (r ** 2.2, g ** 2.2, b ** 2.2, 1.0)


def _known(color):
    c = str(color or "").strip().lower()
    return c in _NAMED or (c.startswith("#") and len(c) in (4, 7))


def _shade(rgba, f):
    return (min(rgba[0] * f, 1), min(rgba[1] * f, 1), min(rgba[2] * f, 1), 1.0)


def _set(node, name, value):
    if name in node.inputs:
        node.inputs[name].default_value = value


def _ramp(nt, fac, c1, c2, lo=0.0, hi=1.0):
    r = nt.nodes.new("ShaderNodeValToRGB")
    r.color_ramp.elements[0].position, r.color_ramp.elements[0].color = lo, c1
    r.color_ramp.elements[1].position, r.color_ramp.elements[1].color = hi, c2
    nt.links.new(fac, r.inputs["Fac"])
    return r.outputs["Color"]


def _vector(nt, units=1.0, stretch=(1, 1, 1)):
    # The texture's coordinates in metres: a part's UVs are in metres (box projected); a texture's plane is 0..1 over `units` metres.
    tc = nt.nodes.new("ShaderNodeTexCoord")
    mp = nt.nodes.new("ShaderNodeMapping")
    mp.inputs["Scale"].default_value = (units * stretch[0], units * stretch[1], units * stretch[2])
    nt.links.new(tc.outputs["UV"], mp.inputs["Vector"])
    return mp.outputs["Vector"]


def _bump(nt, p, height, strength=0.3, distance=0.02):
    b = nt.nodes.new("ShaderNodeBump")
    b.inputs["Strength"].default_value = strength
    b.inputs["Distance"].default_value = distance
    nt.links.new(height, b.inputs["Height"])
    nt.links.new(b.outputs["Normal"], p.inputs["Normal"])


def _pattern(m, kind, color, color2=None, scale=1.0, units=1.0):
    # A pattern drawn by Blender, in metres (scale makes it larger or smaller): its colour, roughness and bumps.
    nt, p, L = m.node_tree, m.node_tree.nodes.get("Principled BSDF"), m.node_tree.links.new
    c1 = _rgba(color)
    c2 = _rgba(color2) if color2 else _shade(c1, 0.62)
    k = max(float(scale or 1), 0.01)
    vec = _vector(nt, units / k)
    rough = 0.6
    if kind in ("brick", "tiles", "shingles"):
        t = nt.nodes.new("ShaderNodeTexBrick")
        L(vec, t.inputs["Vector"])
        width, row, mortar, offset, mcol = {
            "brick": (0.215, 0.065, 0.006, 0.5, (0.55, 0.53, 0.5, 1)),
            "tiles": (0.3, 0.3, 0.004, 0.0, (0.75, 0.75, 0.73, 1)),
            "shingles": (0.3, 0.15, 0.008, 0.5, _shade(c1, 0.35)),
        }[kind]
        _set(t, "Scale", 1.0)
        _set(t, "Brick Width", width)
        _set(t, "Row Height", row)
        _set(t, "Mortar Size", mortar)
        _set(t, "Mortar Smooth", 0.1)
        t.offset = offset
        _set(t, "Color1", c1)
        _set(t, "Color2", c2 if kind != "tiles" else _shade(c1, 0.94))
        _set(t, "Mortar", mcol)
        L(t.outputs["Color"], p.inputs["Base Color"])
        inv = nt.nodes.new("ShaderNodeMath")
        inv.operation = "SUBTRACT"
        inv.inputs[0].default_value = 1.0
        L(t.outputs["Fac"], inv.inputs[1])
        _bump(nt, p, inv.outputs["Value"], 0.5)
        rough = {"brick": 0.85, "tiles": 0.25, "shingles": 0.75}[kind]
    elif kind == "checker":
        t = nt.nodes.new("ShaderNodeTexChecker")
        L(vec, t.inputs["Vector"])
        _set(t, "Scale", 2.0)
        _set(t, "Color1", c1)
        _set(t, "Color2", c2)
        L(t.outputs["Color"], p.inputs["Base Color"])
        rough = 0.4
    elif kind == "wood":
        n = nt.nodes.new("ShaderNodeTexNoise")
        L(vec, n.inputs["Vector"])
        _set(n, "Scale", 3.0)
        _set(n, "Detail", 4.0)
        w = nt.nodes.new("ShaderNodeTexWave")
        L(vec, w.inputs["Vector"])
        w.wave_type, w.bands_direction = "BANDS", "X"
        _set(w, "Scale", 4.0)
        _set(w, "Distortion", 8.0)
        _set(w, "Detail", 3.0)
        mix = nt.nodes.new("ShaderNodeMath")
        mix.operation = "MULTIPLY"
        L(w.outputs["Fac"], mix.inputs[0])
        L(n.outputs["Fac"], mix.inputs[1])
        L(_ramp(nt, mix.outputs["Value"], c2, c1, 0.1, 0.55), p.inputs["Base Color"])
        _bump(nt, p, w.outputs["Fac"], 0.08)
        rough = 0.55
    elif kind == "stone":
        v = nt.nodes.new("ShaderNodeTexVoronoi")
        L(vec, v.inputs["Vector"])
        _set(v, "Scale", 3.0)
        n = nt.nodes.new("ShaderNodeTexNoise")
        L(vec, n.inputs["Vector"])
        _set(n, "Scale", 12.0)
        _set(n, "Detail", 8.0)
        add = nt.nodes.new("ShaderNodeMath")
        add.operation = "ADD"
        L(v.outputs["Distance"], add.inputs[0])
        L(n.outputs["Fac"], add.inputs[1])
        L(_ramp(nt, add.outputs["Value"], c2, c1, 0.3, 1.2), p.inputs["Base Color"])
        _bump(nt, p, add.outputs["Value"], 0.6)
        rough = 0.85
    elif kind in ("concrete", "plaster", "grass", "noise", "fabric"):
        n = nt.nodes.new("ShaderNodeTexNoise")
        L(vec if kind != "fabric" else _vector(nt, units / k, (1, 1, 1)), n.inputs["Vector"])
        scale_, detail, lo, hi, strength = {
            "concrete": (8.0, 10.0, 0.35, 0.65, 0.25), "plaster": (4.0, 6.0, 0.4, 0.6, 0.15), "grass": (60.0, 6.0, 0.3, 0.7, 0.4),
            "noise": (5.0, 4.0, 0.3, 0.7, 0.2), "fabric": (200.0, 2.0, 0.4, 0.6, 0.1),
        }[kind]
        _set(n, "Scale", scale_)
        _set(n, "Detail", detail)
        L(_ramp(nt, n.outputs["Fac"], c2 if kind != "plaster" else _shade(c1, 0.9), c1, lo, hi), p.inputs["Base Color"])
        _bump(nt, p, n.outputs["Fac"], strength)
        rough = {"concrete": 0.9, "plaster": 0.85, "grass": 0.95, "noise": 0.6, "fabric": 0.95}[kind]
    elif kind == "marble":
        n = nt.nodes.new("ShaderNodeTexNoise")
        L(vec, n.inputs["Vector"])
        _set(n, "Scale", 2.0)
        _set(n, "Detail", 10.0)
        w = nt.nodes.new("ShaderNodeTexWave")
        L(vec, w.inputs["Vector"])
        _set(w, "Scale", 1.5)
        _set(w, "Distortion", 12.0)
        _set(w, "Detail", 8.0)
        L(_ramp(nt, w.outputs["Fac"], c2 if color2 else (0.25, 0.25, 0.27, 1), c1, 0.0, 0.18), p.inputs["Base Color"])
        rough = 0.12
    elif kind == "metal":
        n = nt.nodes.new("ShaderNodeTexNoise")
        L(_vector(nt, units / k, (1, 60, 1)), n.inputs["Vector"])
        _set(n, "Scale", 4.0)
        _set(n, "Detail", 2.0)
        L(_ramp(nt, n.outputs["Fac"], _shade(c1, 0.85), c1, 0.3, 0.7), p.inputs["Base Color"])
        L(_ramp(nt, n.outputs["Fac"], (0.25, 0.25, 0.25, 1), (0.45, 0.45, 0.45, 1), 0.3, 0.7), p.inputs["Roughness"])
        p.inputs["Metallic"].default_value = 1.0
        return
    p.inputs["Roughness"].default_value = rough


def texture_files(name):
    # A texture's images in the working directory: the chat's files, made by make_texture or attached by the person.
    base = os.path.splitext(str(name))[0]
    found = {}
    for kind in ("color", "normal", "roughness"):
        for ext in (".png", ".jpg", ".jpeg", ".webp"):
            if os.path.exists(f"{base}-{kind}{ext}"):
                found[kind] = f"{base}-{kind}{ext}"
                break
    if "color" not in found and os.path.exists(str(name)):
        found["color"] = str(name)
    return found


def _images(m, files, size=2.0, units=None):
    # Images on the part, one of them covering `size` metres (repeated): its colour, and its normal and roughness maps when there are.
    nt, p, L = m.node_tree, m.node_tree.nodes.get("Principled BSDF"), m.node_tree.links.new
    vec = _vector(nt, units if units is not None else 1.0 / max(float(size or 2), 0.01))
    def image(path, colour):
        t = nt.nodes.new("ShaderNodeTexImage")
        t.image = bpy.data.images.load(os.path.abspath(path), check_existing=True)
        if not colour:
            t.image.colorspace_settings.name = "Non-Color"
        L(vec, t.inputs["Vector"])
        return t
    L(image(files["color"], True).outputs["Color"], p.inputs["Base Color"])
    if "roughness" in files:
        L(image(files["roughness"], False).outputs["Color"], p.inputs["Roughness"])
    if "normal" in files:
        nm = nt.nodes.new("ShaderNodeNormalMap")
        L(image(files["normal"], False).outputs["Color"], nm.inputs["Color"])
        L(nm.outputs["Normal"], p.inputs["Normal"])


def material(color="grey", metallic=0.0, roughness=None, glass=False, texture=None, texture_size=None, color2=None, shader=None, glow=0.0, units=1.0):
    # One material: a colour, or a texture (a pattern's name, or a texture's images), with a shader. The same ones are shared.
    if texture and str(texture).lower() in PATTERNS and not _known(color):
        color = str(texture).lower()
    key = (str(color), float(metallic or 0), roughness, bool(glass), str(texture), texture_size, str(color2), str(shader), float(glow or 0), units)
    if key in _MATERIALS:
        return _MATERIALS[key]
    m = bpy.data.materials.new(name=str(texture or color))
    m.use_nodes = True
    p = m.node_tree.nodes.get("Principled BSDF")
    rgba = _rgba(color)
    p.inputs["Base Color"].default_value = rgba
    p.inputs["Metallic"].default_value = float(metallic or 0)
    p.inputs["Roughness"].default_value = 0.5 if roughness is None else float(roughness)
    if texture:
        t = str(texture).strip()
        if t.lower() in PATTERNS:
            _pattern(m, t.lower(), color, color2, texture_size or 1.0, units)
        elif texture_files(t):
            _images(m, texture_files(t), texture_size or 2.0, None if units == 1.0 else units)
        else:
            raise ValueError(f"no texture {t!r}: use a pattern ({', '.join(PATTERNS)}) or a texture made with make_texture (its name)")
        if roughness is not None and not p.inputs["Roughness"].is_linked:
            p.inputs["Roughness"].default_value = float(roughness)
    shader = "glass" if glass else (str(shader).lower() if shader else None)
    if shader:
        if shader not in SHADERS:
            raise ValueError(f"unknown shader {shader!r}: use one of {', '.join(SHADERS)}")
        if shader == "glass":
            _set(p, "Transmission Weight", 1.0)
            p.inputs["Roughness"].default_value = 0.02 if roughness is None else float(roughness)
            _set(p, "IOR", 1.45)
            _set(p, "Alpha", 0.35)
            try:
                m.surface_render_method = "BLENDED"
            except (AttributeError, TypeError):
                pass
        elif shader in ("metal", "mirror"):
            p.inputs["Metallic"].default_value = 1.0
            if not p.inputs["Roughness"].is_linked:
                p.inputs["Roughness"].default_value = 0.02 if shader == "mirror" else 0.25
        elif shader == "light":
            _set(p, "Emission Color", rgba)
            _set(p, "Emission Strength", float(glow or 5.0))
        elif shader in ("matte", "rubber", "plastic", "ceramic") and not p.inputs["Roughness"].is_linked:
            p.inputs["Roughness"].default_value = {"matte": 0.95, "rubber": 0.9, "plastic": 0.35, "ceramic": 0.12}[shader]
            if shader == "ceramic":
                _set(p, "Coat Weight", 0.6)
    if float(glow or 0) > 0 and shader != "light":
        _set(p, "Emission Color", rgba)
        _set(p, "Emission Strength", float(glow))
    m.diffuse_color = rgba
    m["arena_textured"] = bool(texture) or shader in ("glass", "light", "mirror") or float(glow or 0) > 0
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


def light(kind="point", at=(0, 0, 3), color="warm", power=None, turn=(0, 0, 0), name="light", size=None, repeat=None):
    # A light in the model: point (a bulb), spot (pointing down, turn to aim it), area (a panel), sun (daylight from its
    # turn). Power in watts (a sun's in strength); the colour a name (warm, daylight, cool, candle...) or #rrggbb. It is in
    # the .glb too (glTF lights), and the pictures are lit by it.
    kind = str(kind or "point").lower()
    if kind not in LIGHTS:
        raise ValueError(f"unknown light {kind!r}: use one of {', '.join(LIGHTS)}")
    rep = repeat or {}
    count = max(1, min(int(rep.get("count", 1)), 100))
    step = [float(x) for x in (rep.get("step") or [0, 0, 0])]
    rgba = _rgba(_WARM.get(str(color).lower(), color))
    made = []
    for i in range(count):
        data = bpy.data.lights.new(name, kind.upper())
        data.color = rgba[:3]
        data.energy = float(power if power is not None else {"point": 100, "spot": 300, "area": 150, "sun": 3}[kind])
        if kind == "spot":
            data.spot_size = math.radians(60)
            data.spot_blend = 0.3
        if kind == "area":
            d = [float(x) for x in (size or [1, 1, 0])]
            data.shape = "RECTANGLE"
            data.size, data.size_y = d[0] or 1, d[1] or 1
        if kind in ("point", "spot"):
            data.shadow_soft_size = 0.1
        ob = bpy.data.objects.new(name, data)
        bpy.context.scene.collection.objects.link(ob)
        ob.location = (at[0] + step[0] * i, at[1] + step[1] * i, at[2] + step[2] * i)
        ob.rotation_euler = [float(t) for t in turn]
        made.append(ob)
    return made


def box_uv(ob):
    # UVs in metres, each face projected along its main axis: textures keep their size on every face of any part.
    bm = bmesh.new()
    bm.from_mesh(ob.data)
    uv = bm.loops.layers.uv.verify()
    for f in bm.faces:
        n = f.normal
        axis = max(range(3), key=lambda i: abs(n[i]))
        a, b = ((1, 2), (0, 2), (0, 1))[axis]
        for loop in f.loops:
            co = loop.vert.co
            loop[uv].uv = (co[a], co[b])
    bm.to_mesh(ob.data)
    bm.free()


def part(spec=None, **kw):
    # One part: shape (box, cylinder, cone, sphere, torus, plane, roof, wedge, pyramid), size [x, y, z] in metres,
    # at [x, y, z] where the middle of its bottom sits, turn [x, y, z] in degrees, color (a name or #rrggbb), color2,
    # texture (a pattern, or a texture made before), texture_size, shader, metallic, roughness, glow, bevel (metres),
    # repeat {count, step [x, y, z]}. Returns the objects made.
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
    if shape == "figure":
        return figure(s)
    if shape == "tree":
        return tree(s)
    if shape == "light":
        return light(s.get("kind", "point"), at, s.get("color", "warm"), s.get("power"), turn, name, s.get("size"), s.get("repeat"))
    rep = s.get("repeat") or {}
    count = max(1, min(int(rep.get("count", 1)), 200))
    step = [float(x) for x in (rep.get("step") or [0, 0, 0])]
    mat = material(s.get("color", "grey"), s.get("metallic", 0), s.get("roughness"), bool(s.get("glass")), s.get("texture"),
                   s.get("texture_size"), s.get("color2"), s.get("shader"), s.get("glow", 0))
    made = []
    for i in range(count):
        x, y, z = (at[0] + step[0] * i, at[1] + step[1] * i, at[2] + step[2] * i)
        sx, sy, sz = (size[0] or 1, size[1] or 1, size[2] or 1)
        if shape in ("roof", "wedge", "capsule", "lathe", "extrude", "tube", "text"):
            if shape == "roof":
                me = _roof_mesh(name)
            elif shape == "wedge":
                me = _wedge_mesh(name)
            elif shape == "capsule":
                me = _capsule_mesh(name, size)
                sx = sy = sz = 1.0
            elif shape == "lathe":
                me = _lathe_mesh(name, s.get("profile"))
                sx = sy = sz = 1.0
            elif shape == "extrude":
                me = _extrude_mesh(name, s.get("outline"), size[2] or 0.1)
                sx = sy = sz = 1.0
            elif shape == "tube":
                me = _tube_mesh(name, s.get("path"), (size[0] or 0.05) / 2)
                sx = sy = sz = 1.0
            else:
                me = _text_mesh(name, str(s.get("text") or name), size[2] or 0.3, size[1] or 0.05)
                sx = sy = sz = 1.0
            ob = bpy.data.objects.new(name, me)
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
                sz = sz / 0.25
            elif shape == "plane":
                bpy.ops.mesh.primitive_plane_add(size=1)
                sz = 1
            ob = bpy.context.active_object
            ob.name = name
            ob.location = (x, y, z + (size[2] / 2 if shape != "plane" else 0))
        # The size goes into the mesh itself: bevels stay even and textures keep their size in metres.
        ob.data.transform(Matrix.Diagonal((sx, sy, sz, 1.0)))
        ob.data.update()
        ob.rotation_euler = turn
        box_uv(ob)
        ob.data.materials.append(mat)
        if float(s.get("bevel") or 0) > 0:
            mod = ob.modifiers.new("bevel", "BEVEL")
            mod.width = float(s["bevel"])
            mod.segments = 3
            mod.limit_method = "ANGLE"
        smooth = int(s.get("smooth") or 0)
        if smooth > 0 or shape in ("sphere", "capsule", "lathe", "tube", "torus", "cylinder", "cone"):
            for poly in ob.data.polygons:
                poly.use_smooth = True
            if shape in ("cylinder", "cone"):
                # Round sides smooth, flat ends flat.
                ob.data.set_sharp_from_angle(angle=math.radians(40)) if hasattr(ob.data, "set_sharp_from_angle") else None
        if smooth > 0:
            sub = ob.modifiers.new("smooth", "SUBSURF")
            sub.levels = sub.render_levels = max(1, min(smooth, 3))
        made.append(ob)
    return made


def _capsule_mesh(name, size):
    # A pill: a cylinder with round ends, size [x, y, height]; its bottom at z = 0.
    sx, sy, h = (size[0] or 0.2), (size[1] or size[0] or 0.2), (size[2] or 0.5)
    r = min(sx, sy) / 2
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=32, v_segments=17, radius=r)
    stretch = max(h / 2 - r, 0)
    for v in bm.verts:
        v.co.z += stretch if v.co.z > 1e-6 else -stretch if v.co.z < -1e-6 else 0
        v.co.x *= sx / (2 * r)
        v.co.y *= sy / (2 * r)
        v.co.z += max(h / 2, r)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    return me


def _lathe_mesh(name, profile, steps=48):
    # A profile turned round the z axis: [[radius, height], ...] from the bottom up (a vase, a bottle, a column, a lamp).
    pts = [(max(float(p[0]), 0.0), float(p[1])) for p in (profile or [])]
    if len(pts) < 2:
        raise ValueError("a lathe needs a profile of at least two [radius, height] points")
    verts, faces, rings = [], [], []
    for r, z in pts:
        if r < 1e-6:
            rings.append([len(verts)])
            verts.append((0, 0, z))
        else:
            ring = []
            for i in range(steps):
                a = 2 * math.pi * i / steps
                ring.append(len(verts))
                verts.append((r * math.cos(a), r * math.sin(a), z))
            rings.append(ring)
    for lo, hi in zip(rings, rings[1:]):
        if len(lo) == 1 and len(hi) == 1:
            continue
        for i in range(steps):
            j = (i + 1) % steps
            if len(lo) == 1:
                faces.append((lo[0], hi[j], hi[i]))
            elif len(hi) == 1:
                faces.append((lo[i], lo[j], hi[0]))
            else:
                faces.append((lo[i], lo[j], hi[j], hi[i]))
    # Open ends closed.
    if len(rings[0]) > 1:
        faces.append(tuple(reversed(rings[0])))
    if len(rings[-1]) > 1:
        faces.append(tuple(rings[-1]))
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.update()
    return me


def _extrude_mesh(name, outline, height):
    # An outline [[x, y], ...] (a room's plan, a sign, a slab of any shape) raised by its height.
    pts = [(float(p[0]), float(p[1])) for p in (outline or [])]
    if len(pts) < 3:
        raise ValueError("an extrude needs an outline of at least three [x, y] points")
    if sum(pts[i][0] * pts[(i + 1) % len(pts)][1] - pts[(i + 1) % len(pts)][0] * pts[i][1] for i in range(len(pts))) < 0:
        pts.reverse()
    n = len(pts)
    verts = [(x, y, 0.0) for x, y in pts] + [(x, y, float(height)) for x, y in pts]
    faces = [tuple(reversed(range(n))), tuple(range(n, 2 * n))] + [(i, (i + 1) % n, n + (i + 1) % n, n + i) for i in range(n)]
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.update()
    return me


def _to_mesh(curve_ob):
    bpy.context.view_layer.update()
    deps = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(curve_ob.evaluated_get(deps))
    data = curve_ob.data
    bpy.data.objects.remove(curve_ob)
    bpy.data.curves.remove(data)
    return me


def _tube_mesh(name, path, radius):
    # A round tube along a path [[x, y, z], ...], smoothed through its points (a pipe, a cable, a handle, a branch).
    pts = [tuple(float(c) for c in p) for p in (path or [])]
    if len(pts) < 2:
        raise ValueError("a tube needs a path of at least two [x, y, z] points")
    cu = bpy.data.curves.new(name, "CURVE")
    cu.dimensions = "3D"
    cu.bevel_depth = radius
    cu.bevel_resolution = 4
    cu.use_fill_caps = True
    sp = cu.splines.new("NURBS" if len(pts) > 2 else "POLY")
    sp.points.add(len(pts) - 1)
    for p, co in zip(sp.points, pts):
        p.co = (co[0], co[1], co[2], 1)
    if len(pts) > 2:
        sp.use_endpoint_u = True
        sp.order_u = 3
    ob = bpy.data.objects.new(name, cu)
    bpy.context.scene.collection.objects.link(ob)
    return _to_mesh(ob)


def _text_mesh(name, text, height, depth):
    # Letters standing up, facing -y: height is a capital's, depth how thick they are; centred on x.
    cu = bpy.data.curves.new(name, "FONT")
    cu.body = text[:200]
    cu.size = height * 1.4
    cu.extrude = depth / 2
    cu.align_x = "CENTER"
    ob = bpy.data.objects.new(name, cu)
    bpy.context.scene.collection.objects.link(ob)
    me = _to_mesh(ob)
    me.transform(Matrix.Rotation(math.radians(90), 4, "X") @ Matrix.Translation((0, 0, depth / 2)))
    return me


def tree(spec=None, **kw):
    # A tree: kind round (a broadleaf), pine, palm or bush; size [crown width, _, height]; at, colours.
    s = dict(spec or {}, **kw)
    kind = str(s.get("kind", "round")).lower()
    size = [float(x) for x in (s.get("size") or [3, 3, 5])]
    w, h = (size[0] or 3), (size[2] or 5)
    at = [float(x) for x in (s.get("at") or [0, 0, 0])]
    name = str(s.get("name") or "tree")
    bark = material(s.get("trunk_color", "#5b4130"), roughness=0.9, texture="wood", texture_size=0.5)
    leaf = material(s.get("color", "#3f7a35" if kind != "pine" else "#2f5d34"), roughness=0.8, texture="grass", texture_size=0.3)
    made = []
    def add(o, m):
        o.data.materials.append(m)
        made.append(o)
    if kind == "palm":
        path = [(at[0], at[1], at[2]), (at[0] + 0.2 * h * 0.1, at[1], at[2] + h * 0.5), (at[0] + 0.35 * h * 0.1, at[1], at[2] + h)]
        trunk = bpy.data.objects.new(f"{name}-trunk", _tube_mesh(f"{name}-trunk", path, h * 0.025))
        bpy.context.scene.collection.objects.link(trunk)
        add(trunk, bark)
        top = path[-1]
        for i in range(8):
            a = 2 * math.pi * i / 8
            tip = (top[0] + math.cos(a) * w / 2, top[1] + math.sin(a) * w / 2, top[2] - h * 0.12)
            midp = (top[0] + math.cos(a) * w / 4, top[1] + math.sin(a) * w / 4, top[2] + h * 0.04)
            frond = bpy.data.objects.new(f"{name}-leaf", _tube_mesh(f"{name}-leaf", [top, midp, tip], w * 0.04))
            frond.data.transform(Matrix.Identity(4))
            bpy.context.scene.collection.objects.link(frond)
            add(frond, leaf)
    else:
        trunk_h = h * (0.35 if kind == "round" else 0.25 if kind == "pine" else 0.1)
        trunk = bpy.data.objects.new(f"{name}-trunk", _lathe_mesh(f"{name}-trunk", [[w * 0.06, 0], [w * 0.045, trunk_h * 0.6], [w * 0.035, trunk_h + h * 0.2], [0, trunk_h + h * 0.21]], 16))
        bpy.context.scene.collection.objects.link(trunk)
        trunk.location = at
        add(trunk, bark)
        if kind == "pine":
            layers = 4
            for i in range(layers):
                f = 1 - i / layers
                bpy.ops.mesh.primitive_cone_add(radius1=w / 2 * f + 0.1, radius2=0, depth=(h - trunk_h) / layers * 1.6, vertices=24,
                                                location=(at[0], at[1], at[2] + trunk_h + (h - trunk_h) * (i / layers) + (h - trunk_h) / layers * 0.8))
                add(bpy.context.active_object, leaf)
        else:
            # A crown of a few overlapping lumps, bumpy.
            crown_z = at[2] + trunk_h + (h - trunk_h) / 2
            lumps = [(0, 0, 0, 0.5), (0.25, 0.1, -0.1, 0.36), (-0.22, -0.12, -0.05, 0.36), (0.05, -0.25, 0.12, 0.32), (-0.08, 0.24, 0.15, 0.3)]
            for dx, dy, dz, r in lumps[: (5 if kind == "round" else 3)]:
                bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=3, radius=1)
                o = bpy.context.active_object
                o.data.transform(Matrix.Diagonal((w * r, w * r, (h - trunk_h) * r * 1.1, 1)))
                o.location = (at[0] + dx * w, at[1] + dy * w, crown_z + dz * (h - trunk_h))
                d = o.modifiers.new("bumps", "DISPLACE")
                t = bpy.data.textures.new("leaves", "CLOUDS")
                t.noise_scale = 0.35
                d.texture, d.strength = t, w * 0.06
                for poly in o.data.polygons:
                    poly.use_smooth = True
                add(o, leaf)
    for o in made:
        box_uv(o)
    return made


def _period(pattern):
    # A pattern's repeat in metres, [x, y]: an image of a whole number of them tiles without a seam.
    return {"brick": (0.215, 0.13), "tiles": (0.3, 0.3), "shingles": (0.3, 0.3), "checker": (1.0, 1.0)}.get(pattern)


def _save(img, path):
    img.filepath_raw = os.path.abspath(path)
    img.file_format = "PNG"
    img.save()


def make_texture(name, pattern=None, image=None, color="grey", color2=None, size=2.0, resolution=1024, preview=True):
    # A texture as images: {name}-color.png, -roughness.png and -normal.png, from a pattern Blender draws or from a picture
    # (the image model's, or the person's), its bumps and roughness read from its light and dark; and a preview of it on a
    # sphere and a cube. Returns the size in metres one image covers, [x, y].
    res = max(128, min(int(resolution or 1024), 2048))
    size = float(size or 2.0)
    pattern = str(pattern).lower() if pattern else None
    if pattern and pattern not in PATTERNS:
        raise ValueError(f"unknown pattern {pattern!r}: use one of {', '.join(PATTERNS)}")
    span = [size, size]
    if pattern and _period(pattern):
        span = [max(1, round(size / p)) * p for p in _period(pattern)]
    bpy.ops.mesh.primitive_plane_add(size=1)
    plane = bpy.context.active_object
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt, p = m.node_tree, m.node_tree.nodes.get("Principled BSDF")
    if pattern:
        tc = None
        _pattern(m, pattern, color, color2, 1.0, 1.0)
        # The plane's UVs are 0..1: the pattern's mapping takes them to metres over the image's span.
        for n in nt.nodes:
            if n.type == "MAPPING":
                s = n.inputs["Scale"].default_value
                n.inputs["Scale"].default_value = (s[0] * span[0], s[1] * span[1], s[2])
    elif image:
        if not os.path.exists(str(image)):
            raise ValueError(f"no picture {image!r} in the chat's files")
        t = nt.nodes.new("ShaderNodeTexImage")
        t.image = bpy.data.images.load(os.path.abspath(str(image)))
        nt.links.new(t.outputs["Color"], p.inputs["Base Color"])
        bw = nt.nodes.new("ShaderNodeRGBToBW")
        nt.links.new(t.outputs["Color"], bw.inputs["Color"])
        _bump(nt, p, bw.outputs["Val"], 0.6, 0.02)
        r = nt.nodes.new("ShaderNodeMapRange")
        nt.links.new(bw.outputs["Val"], r.inputs["Value"])
        r.inputs["To Min"].default_value, r.inputs["To Max"].default_value = 0.95, 0.45
        nt.links.new(r.outputs["Result"], p.inputs["Roughness"])
    else:
        raise ValueError("give a pattern, or a picture to make the texture from")
    plane.data.materials.append(m)
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.device = "CPU"
    sc.cycles.samples = 1
    bake = sc.render.bake
    bake.use_pass_direct, bake.use_pass_indirect, bake.use_pass_color = False, False, True
    bake.margin = 0
    bpy.context.view_layer.objects.active = plane
    plane.select_set(True)
    files = {}
    for kind, btype, colour in (("color", "DIFFUSE", True), ("roughness", "ROUGHNESS", False), ("normal", "NORMAL", False)):
        if kind == "normal" and not p.inputs["Normal"].is_linked:
            continue
        img = bpy.data.images.new(f"{name}-{kind}", res, res)
        if not colour:
            img.colorspace_settings.name = "Non-Color"
        node = nt.nodes.new("ShaderNodeTexImage")
        node.image = img
        nt.nodes.active = node
        bpy.ops.object.bake(type=btype)
        _save(img, f"{name}-{kind}.png")
        files[kind] = f"{name}-{kind}.png"
    if preview:
        # The images on a sphere and a cube, lit, as they will look on a model.
        bpy.data.objects.remove(plane)
        look = bpy.data.materials.new(f"{name}-preview")
        look.use_nodes = True
        _images(look, files, span[0])
        if pattern == "metal":
            look.node_tree.nodes.get("Principled BSDF").inputs["Metallic"].default_value = 1.0
        bpy.ops.mesh.primitive_uv_sphere_add(radius=0.5, location=(-0.65, 0, 0.5), segments=64, ring_count=32)
        sphere = bpy.context.active_object
        bpy.ops.mesh.primitive_cube_add(size=0.9, location=(0.65, 0, 0.45), rotation=(0, 0, math.radians(30)))
        cube = bpy.context.active_object
        for ob in (sphere, cube):
            bpy.ops.object.shade_smooth() if ob is sphere else None
            box_uv(ob)
            ob.data.materials.append(look)
        bpy.ops.mesh.primitive_plane_add(size=6, location=(0, 0, 0))
        floor = bpy.context.active_object
        floor.data.materials.append(material("lightgrey", roughness=0.8))
        cam = bpy.data.objects.new("_tex_camera", bpy.data.cameras.new("_tex_camera"))
        sc.collection.objects.link(cam)
        sc.camera = cam
        cam.location = (0, -3.2, 1.6)
        cam.rotation_euler = (math.radians(72), 0, 0)
        sun = bpy.data.objects.new("_tex_sun", bpy.data.lights.new("_tex_sun", "SUN"))
        sun.data.energy = 3.5
        sun.rotation_euler = (math.radians(45), math.radians(15), math.radians(30))
        sc.collection.objects.link(sun)
        sc.world = sc.world or bpy.data.worlds.new("_tex_world")
        sc.world.use_nodes = True
        sc.world.node_tree.nodes.get("Background").inputs["Strength"].default_value = 0.8
        sc.cycles.samples = 24
        try:
            sc.cycles.use_denoising = True
        except AttributeError:
            pass
        sc.render.resolution_x, sc.render.resolution_y = 800, 500
        sc.render.filepath = os.path.abspath(f"{name}-preview.png")
        bpy.ops.render.render(write_still=True)
        for ob in (sphere, cube, floor, cam, sun):
            bpy.data.objects.remove(ob)
    print(f"Texture {name}: {', '.join(files.values())}; one image covers {span[0]:.2f} x {span[1]:.2f} m.")
    return span


# --- a human figure: a skeleton Blender's skin modifier covers and subdivision smooths, then a face, hair and clothes ---

# Joints for a 1.75 m person standing, in metres; front is -y. Each pose moves some of them.
_JOINTS = {
    "pelvis": (0, 0, 0.95), "waist": (0, 0.005, 1.08), "chest": (0, 0.01, 1.28), "neck": (0, 0.01, 1.47), "head": (0, 0, 1.55),
    "hip.L": (0.095, 0, 0.92), "knee.L": (0.1, -0.01, 0.5), "ankle.L": (0.1, 0.02, 0.08), "heel.L": (0.1, 0.05, 0.03), "toe.L": (0.11, -0.13, 0.025),
    "shoulder.L": (0.185, 0.02, 1.41), "elbow.L": (0.25, 0.04, 1.13), "wrist.L": (0.28, 0.0, 0.88), "hand.L": (0.29, -0.01, 0.82),
}


def _mirror(j):
    out = dict(j)
    for k, v in j.items():
        if k.endswith(".L"):
            out[k[:-2] + ".R"] = (-v[0], v[1], v[2])
    return out


def _posed(pose):
    j = dict(_JOINTS)
    if pose == "t-pose":
        j.update({"elbow.L": (0.47, 0.02, 1.41), "wrist.L": (0.72, 0.0, 1.41), "hand.L": (0.8, -0.0, 1.41)})
    elif pose == "arms-up":
        j.update({"elbow.L": (0.24, 0.02, 1.68), "wrist.L": (0.26, 0.0, 1.93), "hand.L": (0.265, -0.01, 2.0)})
    j = _mirror(j)
    if pose == "waving":
        j.update({"elbow.R": (-0.4, 0.03, 1.36), "wrist.R": (-0.45, -0.01, 1.6), "hand.R": (-0.46, -0.02, 1.68)})
    elif pose == "walking":
        j.update({"knee.L": (0.1, -0.16, 0.52), "ankle.L": (0.1, -0.2, 0.1), "heel.L": (0.1, -0.17, 0.05), "toe.L": (0.11, -0.36, 0.03),
                  "knee.R": (-0.1, 0.1, 0.5), "ankle.R": (-0.1, 0.24, 0.13), "heel.R": (-0.1, 0.27, 0.09), "toe.R": (-0.11, 0.12, 0.03),
                  "elbow.L": (0.24, 0.12, 1.14), "wrist.L": (0.26, 0.18, 0.9), "hand.L": (0.265, 0.19, 0.83),
                  "elbow.R": (-0.24, -0.08, 1.14), "wrist.R": (-0.26, -0.2, 0.92), "hand.R": (-0.265, -0.23, 0.86)})
    elif pose == "sitting":
        for s, x in (("L", 0.1), ("R", -0.1)):
            j.update({f"hip.{s}": (x * 0.95, 0, 0.5), f"knee.{s}": (x, -0.42, 0.52), f"ankle.{s}": (x, -0.44, 0.08),
                      f"heel.{s}": (x, -0.41, 0.03), f"toe.{s}": (x * 1.1, -0.58, 0.025)})
        j.update({"pelvis": (0, 0.02, 0.53), "waist": (0, 0.03, 0.66), "chest": (0, 0.04, 0.86), "neck": (0, 0.04, 1.05), "head": (0, 0.03, 1.13),
                  "shoulder.L": (0.185, 0.05, 0.99), "elbow.L": (0.23, -0.02, 0.73), "wrist.L": (0.2, -0.27, 0.62), "hand.L": (0.19, -0.33, 0.6),
                  "shoulder.R": (-0.185, 0.05, 0.99), "elbow.R": (-0.23, -0.02, 0.73), "wrist.R": (-0.2, -0.27, 0.62), "hand.R": (-0.19, -0.33, 0.6)})
    return j


# The bones, each a joint to another, and what each one is for clothes.
_BONES = [("pelvis", "waist", "hips"), ("waist", "chest", "torso"), ("chest", "neck", "torso"), ("neck", "head", "neck"),
          ("pelvis", "hip.L", "hips"), ("hip.L", "knee.L", "thigh"), ("knee.L", "ankle.L", "shin"), ("ankle.L", "heel.L", "foot"), ("ankle.L", "toe.L", "foot"),
          ("pelvis", "hip.R", "hips"), ("hip.R", "knee.R", "thigh"), ("knee.R", "ankle.R", "shin"), ("ankle.R", "heel.R", "foot"), ("ankle.R", "toe.R", "foot"),
          ("chest", "shoulder.L", "torso"), ("shoulder.L", "elbow.L", "upperarm"), ("elbow.L", "wrist.L", "forearm"), ("wrist.L", "hand.L", "hand"),
          ("chest", "shoulder.R", "torso"), ("shoulder.R", "elbow.R", "upperarm"), ("elbow.R", "wrist.R", "forearm"), ("wrist.R", "hand.R", "hand")]

# Skin radii (x, y) at each joint, average build.
_RADII = {"pelvis": (0.15, 0.1), "waist": (0.128, 0.098), "chest": (0.16, 0.108), "neck": (0.06, 0.058), "head": (0.055, 0.055),
          "hip": (0.088, 0.088), "knee": (0.056, 0.056), "ankle": (0.036, 0.036), "heel": (0.036, 0.032), "toe": (0.042, 0.022),
          "shoulder": (0.062, 0.062), "elbow": (0.044, 0.044), "wrist": (0.03, 0.024), "hand": (0.034, 0.016)}

_HAIR = ("short", "long", "bun", "ponytail", "curly", "bald", "none")
_POSES = ("standing", "t-pose", "waving", "walking", "sitting", "arms-up")


def _seg_dist(p, a, b):
    ab = [b[i] - a[i] for i in range(3)]
    ap = [p[i] - a[i] for i in range(3)]
    t = max(0.0, min(1.0, sum(ab[i] * ap[i] for i in range(3)) / max(sum(c * c for c in ab), 1e-9)))
    return math.dist(p, [a[i] + ab[i] * t for i in range(3)]), t


def figure(spec=None, **kw):
    # A person: height (metres), build (slim, average, heavy), body (masculine, feminine), pose (standing, t-pose, waving,
    # walking, sitting, arms-up), skin (a colour), shirt, sleeves (long, short, none), trousers, legs (long, shorts, skirt),
    # shoes, hair (short, long, bun, ponytail, curly, bald), hair_color, eyes (a colour), at, turn, name.
    s = dict(spec or {}, **kw)
    height = float(s.get("height") or (s.get("size") or [0, 0, 1.75])[2] or 1.75)
    k = height / 1.75
    build = str(s.get("build", "average")).lower()
    body = str(s.get("body", "masculine")).lower()
    pose = str(s.get("pose", "standing")).lower()
    pose = pose if pose in _POSES else "standing"
    hair = str(s.get("hair", "short")).lower()
    hair = hair if hair in _HAIR else "short"
    name = str(s.get("name") or "person")
    at = [float(x) for x in (s.get("at") or [0, 0, 0])]
    turn = [math.radians(float(x)) for x in (s.get("turn") or [0, 0, 0])]
    fat = {"slim": 0.85, "heavy": 1.3, "athletic": 1.05}.get(build, 1.0)
    fem = body.startswith("fem")
    joints = _posed(pose)
    if fem:
        for side, sign in (("L", 1), ("R", -1)):
            x, y, z = joints[f"shoulder.{side}"]
            joints[f"shoulder.{side}"] = (x * 0.9, y, z)
            x, y, z = joints[f"hip.{side}"]
            joints[f"hip.{side}"] = (x * 1.08, y, z)
    names = list(joints)
    mesh = bpy.data.meshes.new(f"{name}-body")
    verts = [tuple(c * k for c in joints[n]) for n in names]
    edges = [(names.index(a), names.index(b)) for a, b, _ in _BONES]
    mesh.from_pydata(verts, edges, [])
    ob = bpy.data.objects.new(f"{name}-body", mesh)
    bpy.context.scene.collection.objects.link(ob)
    skin = ob.modifiers.new("skin", "SKIN")
    skin.use_smooth_shade = True
    for i, n in enumerate(names):
        r = _RADII[n.split(".")[0]]
        f = fat if n.split(".")[0] in ("pelvis", "waist", "chest", "hip", "knee", "shoulder", "elbow") else (1 + (fat - 1) * 0.5)
        if fem and n == "pelvis":
            r = (r[0] * 1.1, r[1] * 1.05)
        if fem and n == "chest":
            r = (r[0] * 0.92, r[1] * 1.12)
        if fem and n == "waist":
            r = (r[0] * 0.9, r[1] * 0.95)
        mesh.skin_vertices[0].data[i].radius = (r[0] * f * k, r[1] * f * k)
        mesh.skin_vertices[0].data[i].use_root = n == "pelvis"
    sub = ob.modifiers.new("smooth", "SUBSURF")
    sub.levels = sub.render_levels = 3
    bpy.context.view_layer.objects.active = ob
    for x in bpy.context.scene.objects:
        x.select_set(False)
    ob.select_set(True)
    bpy.ops.object.modifier_apply(modifier="skin")
    bpy.ops.object.modifier_apply(modifier="smooth")
    for p in ob.data.polygons:
        p.use_smooth = True
    box_uv(ob)
    # Clothes by where each face is: the bone it is nearest, and how far along it.
    colours = {
        "skin": material(s.get("skin", "#e0b49a"), roughness=0.55),
        "shirt": material(s.get("shirt", "#3b6ea8"), roughness=0.8, texture="fabric" if s.get("fabric", True) else None),
        "trousers": material(s.get("trousers", "#2b2f3a"), roughness=0.8, texture="fabric" if s.get("fabric", True) else None),
        "shoes": material(s.get("shoes", "#2a1d15"), roughness=0.4),
    }
    order = list(colours)
    for m in colours.values():
        ob.data.materials.append(m)
    sleeves = str(s.get("sleeves", "short")).lower()
    legs = str(s.get("legs", "long")).lower()
    bones = [(tuple(c * k for c in joints[a]), tuple(c * k for c in joints[b]), what) for a, b, what in _BONES]
    waist_z = joints["waist"][2] * k - 0.04 * k
    neck_z = joints["neck"][2] * k
    trunk = [(a, b) for a, b, w in bones if w in ("torso", "hips")][:3]
    for p in ob.data.polygons:
        c = p.center
        best, along, what = 9.0, 0.0, "torso"
        for a, b, w in bones:
            d, t = _seg_dist(c, a, b)
            if d < best:
                best, along, what = d, t, w
        # The body's side under an arm is the body's, not the arm's.
        if what in ("upperarm", "forearm") and min(_seg_dist(c, a, b)[0] for a, b in trunk) < 0.175 * k * fat:
            what = "torso"
        if what in ("torso",):
            part_ = "shirt" if c.z > waist_z or legs == "dress" else "trousers"
            # The collar: the neck above it is skin.
            if c.z > neck_z - 0.015 * k and abs(c.x - joints["neck"][0] * k) < 0.075 * k * fat:
                part_ = "skin"
        elif what == "hips":
            part_ = "shirt" if legs == "dress" else "trousers"
        elif what == "upperarm":
            part_ = "shirt" if sleeves in ("short", "long") and (sleeves == "long" or along < 0.55) else "skin"
        elif what == "forearm":
            part_ = "shirt" if sleeves == "long" and along < 0.92 else "skin"
        elif what == "thigh":
            # Under a skirt the upper leg takes its colour (nothing pokes through in skin), the knee is bare.
            part_ = ("shirt" if legs == "dress" else "trousers") if legs not in ("skirt", "dress") or along < 0.6 else "skin"
            if legs not in ("skirt", "dress"):
                part_ = "trousers"
            if legs == "shorts" and along > 0.7:
                part_ = "skin"
        elif what == "shin":
            part_ = "trousers" if legs == "long" else "skin"
        elif what == "foot":
            part_ = "shoes"
        else:
            part_ = "skin"
        if what in ("shin",) and along > 0.85:
            part_ = "shoes"
        p.material_index = order.index(part_)
    made = [ob]
    if legs in ("skirt", "dress"):
        # A skirt flares from the waist (a dress from under the arms) to the knees.
        top = waist_z + 0.03 * k
        knee = (joints["knee.L"][2] + (0.06 if legs == "skirt" else -0.12)) * k
        cx, cy = joints["pelvis"][0] * k, joints["pelvis"][1] * k
        waist_r = _RADII["waist"][0] * (0.9 if fem else 1.0) * k * fat
        hip_z = joints["hip.L"][2] * k - top
        # Round the waist, out over the hips, flaring to the hem: a profile turned round, a little flatter front to back.
        profile = [[0.27 * k * fat, knee - top], [0.235 * k * fat, hip_z - 0.08 * k], [0.215 * k * fat, hip_z + 0.02 * k], [waist_r * 1.1, 0.0]]
        sk = bpy.data.objects.new(f"{name}-{legs}", _lathe_mesh(f"{name}-{legs}", profile))
        bpy.context.scene.collection.objects.link(sk)
        sk.data.polygons.foreach_set("use_smooth", [True] * len(sk.data.polygons))
        bm = bmesh.new()
        bm.from_mesh(sk.data)
        bmesh.ops.delete(bm, geom=[f for f in bm.faces if len(f.verts) > 4], context="FACES_ONLY")
        bm.to_mesh(sk.data)
        bm.free()
        sk.data.transform(Matrix.Diagonal((1.0, 0.82, 1.0, 1.0)))
        sk.location = (cx, cy, top)
        sk.modifiers.new("thick", "SOLIDIFY").thickness = 0.01 * k
        for poly in sk.data.polygons:
            poly.use_smooth = True
        box_uv(sk)
        sk.data.materials.append(colours["shirt" if legs == "dress" else "trousers"])
        made.append(sk)
    # The head: a smooth egg on the neck, its face towards -y.
    hz = joints["head"][2] * k
    hx, hy = joints["head"][0] * k, joints["head"][1] * k
    # A child's head is larger for its height.
    hs = 1 + max(0.0, 1.6 - height) * 0.45
    head_c = (hx, hy, hz + 0.085 * k * hs)
    def piece(shape, size, centre, colour, smooth=True, turn_=(0, 0, 0), nm="face"):
        bpy.ops.mesh.primitive_uv_sphere_add(radius=0.5, segments=32, ring_count=16) if shape == "sphere" else bpy.ops.mesh.primitive_cylinder_add(radius=0.5, depth=1, vertices=24)
        o = bpy.context.active_object
        o.name = f"{name}-{nm}"
        o.data.transform(Matrix.Diagonal((size[0] * k, size[1] * k, size[2] * k, 1.0)))
        o.location = centre
        o.rotation_euler = turn_
        if smooth:
            for poly in o.data.polygons:
                poly.use_smooth = True
        box_uv(o)
        o.data.materials.append(colour)
        made.append(o)
        return o
    skin_m = colours["skin"]
    head = piece("sphere", (0.155 * hs, 0.19 * hs, 0.225 * hs), head_c, skin_m, nm="head")
    # A jaw: the lower face narrower and forward.
    bm = bmesh.new()
    bm.from_mesh(head.data)
    for v in bm.verts:
        if v.co.z < -0.02 * k:
            f = 1 - min(1, (-v.co.z - 0.02 * k) / (0.09 * k)) * 0.28
            v.co.x *= f
            if v.co.y < 0:
                v.co.y *= 1 + (1 - f) * 0.3
    bm.to_mesh(head.data)
    bm.free()
    def fpiece(shape, size, centre, colour, **kw):
        return piece(shape, tuple(v * hs for v in size), centre, colour, **kw)
    kf = k * hs
    fy = hy - 0.093 * kf
    eye_col = material(s.get("eyes", "#4a6a8a"), roughness=0.2)
    white = material("#f4f1ea", roughness=0.15)
    dark = material("#1b1210", roughness=0.4)
    for sx in (-1, 1):
        ex = hx + sx * 0.033 * kf
        fpiece("sphere", (0.026, 0.02, 0.022), (ex, fy + 0.008 * kf, head_c[2] + 0.012 * kf), white, nm="eye")
        fpiece("sphere", (0.013, 0.008, 0.013), (ex, fy - 0.0015 * kf, head_c[2] + 0.012 * kf), eye_col, nm="iris")
        fpiece("sphere", (0.006, 0.004, 0.006), (ex, fy - 0.004 * kf, head_c[2] + 0.012 * kf), dark, nm="pupil")
        fpiece("sphere", (0.034, 0.008, 0.007), (ex, fy - 0.0, head_c[2] + 0.036 * kf), material(s.get("hair_color", "#3a2618"), roughness=0.7), turn_=(0, sx * 0.12, 0), nm="brow")
        fpiece("sphere", (0.016, 0.035, 0.052), (hx + sx * 0.077 * kf, hy + 0.0, head_c[2] + 0.0), skin_m, nm="ear")
    fpiece("sphere", (0.02, 0.028, 0.042), (hx, fy - 0.004 * kf, head_c[2] - 0.014 * kf), skin_m, turn_=(-0.25, 0, 0), nm="nose")
    fpiece("sphere", (0.05, 0.016, 0.014), (hx, fy + 0.008 * kf, head_c[2] - 0.052 * kf), material(s.get("lips", "#b5645a"), roughness=0.45), nm="lips")
    hair_m = material(s.get("hair_color", "#3a2618"), roughness=0.65, texture="fabric")
    if hair not in ("bald", "none"):
        cap = fpiece("sphere", (0.168, 0.2, 0.2), (hx, hy + 0.006 * kf, head_c[2] + 0.032 * kf), hair_m, nm="hair")
        bm = bmesh.new()
        bm.from_mesh(cap.data)
        # The fringe and the back: the cap cut above the brows in front, lower at the back and sides.
        doomed = [v for v in bm.verts if v.co.z < (0.012 * kf if v.co.y < -0.03 * kf else -0.05 * kf)]
        bmesh.ops.delete(bm, geom=doomed, context="VERTS")
        bm.to_mesh(cap.data)
        bm.free()
        sol = cap.modifiers.new("thick", "SOLIDIFY")
        sol.thickness = 0.012 * kf
        if hair == "curly":
            disp = cap.modifiers.new("curls", "DISPLACE")
            tex = bpy.data.textures.new("curls", "VORONOI")
            tex.noise_scale = 0.02
            disp.texture, disp.strength = tex, 0.012 * kf
        if hair == "long":
            fpiece("sphere", (0.17, 0.08, 0.3), (hx, hy + 0.075 * kf, head_c[2] - 0.13 * kf), hair_m, nm="hair-back")
        elif hair == "ponytail":
            fpiece("sphere", (0.06, 0.06, 0.22), (hx, hy + 0.12 * kf, head_c[2] - 0.06 * kf), hair_m, turn_=(0.35, 0, 0), nm="ponytail")
        elif hair == "bun":
            fpiece("sphere", (0.08, 0.08, 0.075), (hx, hy + 0.09 * kf, head_c[2] + 0.07 * kf), hair_m, nm="bun")
    # Fingers on each hand, a thumb towards the front.
    for side, sx in (("L", 1), ("R", -1)):
        w = [c * k for c in joints[f"wrist.{side}"]]
        h = [c * k for c in joints[f"hand.{side}"]]
        d = [h[i] - w[i] for i in range(3)]
        n = math.sqrt(sum(c * c for c in d)) or 1
        d = [c / n for c in d]
        for i, off in enumerate((-0.018, -0.006, 0.006, 0.018)):
            base = (h[0] + sx * 0.0, h[1] + off * k, h[2])
            length = (0.07, 0.08, 0.075, 0.06)[i] * k
            tip = [base[j] + d[j] * length for j in range(3)]
            mid = [(base[j] + tip[j]) / 2 for j in range(3)]
            f = piece("cylinder", (0.016, 0.016, length / k), mid, skin_m, nm="finger")
            f.rotation_euler = _mu_track(d)
        thumb_base = (w[0] + d[0] * 0.03 * k, w[1] - 0.025 * k, w[2] + d[2] * 0.03 * k)
        f = piece("cylinder", (0.018, 0.018, 0.055), (thumb_base[0], thumb_base[1] - 0.02 * k, thumb_base[2] - 0.01 * k), skin_m, nm="thumb")
        f.rotation_euler = _mu_track((d[0] * 0.5, -0.8, d[2] * 0.5))
    # The whole person moved and turned as one: parented to an empty.
    root = bpy.data.objects.new(name, None)
    bpy.context.scene.collection.objects.link(root)
    for o in made:
        o.parent = root
    root.location = at
    root.rotation_euler = turn
    return made


def _mu_track(direction):
    from mathutils import Vector
    return Vector(direction).to_track_quat("Z", "Y").to_euler()
