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
SHAPES = ("box", "cylinder", "cone", "sphere", "torus", "plane", "roof", "wedge", "pyramid", "light")
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
        made.append(ob)
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
