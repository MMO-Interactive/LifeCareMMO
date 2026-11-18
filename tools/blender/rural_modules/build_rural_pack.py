"""LifeCare MMO - Rural Town module pack generator.

Builds every module of the rural starter district procedurally in Blender, exports one GLB per module,
writes manifest.json, saves a source .blend, and renders catalog sheets plus a sample town.

Run (from the repo root):
    blender -b --factory-startup -P tools/blender/rural_modules/build_rural_pack.py -- --out assets/modules/rural
Options after "--":
    --out <dir>      output folder (default assets/modules/rural)
    --no-render      skip catalog sheets and town renders
    --no-town        skip the sample town (and its renders)
    --sheets a,b     render only these catalog sheets

Conventions (Blender, Z up; glTF export maps Blender (x, y, z) to glTF (x, z, -y)):
    * 1 tile = 4 m. Story = 3.2 m. Floor slab 0..0.2 m, walls 0.2..3.2 m, 0.2 m thick (matches LotGeometry).
    * "front"/exterior is local -Y (glTF +Z). Rotate modules in 90 degree steps about the origin.
    * tile   : origin at tile centre, z=0 at level base (floors) or ground (terrain, roads).
    * edge   : origin at the edge midpoint, z=0 at level base; runs along local X; exterior faces -Y.
    * vertex : origin on the grid vertex; outer corners fill the -X/-Y quadrant.
    * roof   : origin at footprint centre, z=0 at the top of the walls (the next level's elevation).
    * free   : origin at base centre; wall-mounted items have the wall face at y=0 and project toward -Y.
"""
import bpy
import math
import json
import os
import sys
import zlib
import random
import time
from contextlib import contextmanager
from mathutils import Vector, Matrix

# ----------------------------------------------------------------------------------------------- CLI
ARGV = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []


def _arg(name, default=None):
    if name in ARGV:
        i = ARGV.index(name)
        if i + 1 < len(ARGV) and not ARGV[i + 1].startswith("--"):
            return ARGV[i + 1]
        return True
    return default


OUT = os.path.abspath(_arg("--out", "assets/modules/rural"))
DO_RENDER = not _arg("--no-render", False)
DO_TOWN = not _arg("--no-town", False)
ONLY_SHEETS = set(_arg("--sheets", "").split(",")) - {""} if isinstance(_arg("--sheets", ""), str) else set()

# ----------------------------------------------------------------------------------------------- grid
TILE = 4.0
STORY = 3.2
FLOOR_T = 0.2
WALL_T = 0.2
WALL_Z0 = FLOOR_T
WALL_Z1 = STORY
ROOF_T = 0.18
EAVE = 0.3
PITCH = 0.5
Y_EXT = -WALL_T / 2
Y_INT = WALL_T / 2
CLAD = 0.03
MAX_VERTS = 65535

# ----------------------------------------------------------------------------------------------- materials
PALETTE = {
    # name: (srgb hex, roughness, metallic, alpha, emission)
    "trim_white": ("#ECEBE4", .6, 0, 1, 0), "drywall": ("#D9D3C7", .9, 0, 1, 0),
    "siding_white": ("#E4E2D6", .7, 0, 1, 0), "siding_natural": ("#9C7550", .8, 0, 1, 0),
    "stucco": ("#CDBB9B", .95, 0, 1, 0), "batten_green": ("#4E6B4A", .75, 0, 1, 0),
    "batten_red": ("#8E2A22", .75, 0, 1, 0), "stone": ("#8A857C", .9, 0, 1, 0),
    "mortar": ("#5F5B55", .95, 0, 1, 0), "concrete": ("#9E9C96", .9, 0, 1, 0),
    "sidewalk": ("#B9B5AC", .9, 0, 1, 0), "wood_floor": ("#A0703F", .55, 0, 1, 0),
    "tile_floor": ("#DCD6C8", .35, 0, 1, 0), "grout": ("#8F8A80", .9, 0, 1, 0),
    "wood_deck": ("#8B6A45", .8, 0, 1, 0), "wood_raw": ("#7A5A3A", .85, 0, 1, 0),
    "wood_dark": ("#4D3826", .8, 0, 1, 0), "gravel": ("#7C776E", 1, 0, 1, 0),
    "dirt": ("#6B5038", 1, 0, 1, 0), "soil": ("#4A3626", 1, 0, 1, 0),
    "grass": ("#557F34", 1, 0, 1, 0), "grass_dry": ("#8F8B4C", 1, 0, 1, 0),
    "snow": ("#F2F4F7", .6, 0, 1, 0), "asphalt": ("#323335", .95, 0, 1, 0),
    "line_yellow": ("#E2B227", .7, 0, 1, 0), "line_white": ("#E8E8E2", .7, 0, 1, 0),
    "shingle": ("#46484B", .95, 0, 1, 0), "metal_green": ("#3F6047", .45, .6, 1, 0),
    "metal_galv": ("#A3A7AA", .35, .85, 1, 0), "metal_dark": ("#2B2D30", .5, .7, 1, 0),
    "roof_membrane": ("#6F7275", .9, 0, 1, 0), "glass": ("#8FB0C4", .05, 0, .35, 0),
    "screen": ("#2A2C2E", .8, .3, .55, 0), "door_green": ("#3E5A3C", .6, 0, 1, 0),
    "door_wood": ("#6E4A2C", .6, 0, 1, 0), "door_red": ("#8A2B22", .6, 0, 1, 0),
    "garage_white": ("#E3E1DA", .5, 0, 1, 0), "brick": ("#8A4A35", .9, 0, 1, 0),
    "brass": ("#B08D47", .35, 1, 1, 0), "plastic_blue": ("#2D5DA8", .5, 0, 1, 0),
    "propane": ("#EEEEEA", .4, .2, 1, 0), "ac_beige": ("#CFCBBF", .5, .3, 1, 0),
    "bark": ("#5A4632", 1, 0, 1, 0), "leaf_green": ("#4E7D30", .9, 0, 1, 0),
    "leaf_dark": ("#36602A", .9, 0, 1, 0), "leaf_autumn": ("#C4651E", .9, 0, 1, 0),
    "leaf_gold": ("#D99A2B", .9, 0, 1, 0), "pine": ("#2F4F2E", .9, 0, 1, 0),
    "flower_red": ("#C4303A", .8, 0, 1, 0), "flower_yellow": ("#E8C53A", .8, 0, 1, 0),
    "flower_purple": ("#7B4FA8", .8, 0, 1, 0), "flower_white": ("#F2EFE6", .8, 0, 1, 0),
    "sign_red": ("#B3241E", .5, 0, 1, 0), "sign_white": ("#F0F0EC", .5, 0, 1, 0),
    "sign_yellow": ("#E8C21D", .5, 0, 1, 0), "sign_green": ("#2D6B3F", .5, 0, 1, 0),
    "sign_black": ("#1C1C1C", .5, 0, 1, 0), "lamp_glow": ("#FFE2A8", .3, 0, 1, 4.0),
    "water": ("#2E4A57", .1, 0, 1, 0), "hay": ("#C9A64E", 1, 0, 1, 0),
    "insulator": ("#5E7F74", .2, 0, 1, 0), "tank_white": ("#E7E8E6", .45, .3, 1, 0),
}
MAT = {}


def _srgb(hexc):
    h = hexc.lstrip("#")
    c = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    return [x / 12.92 if x <= 0.04045 else ((x + 0.055) / 1.055) ** 2.4 for x in c]


def _principled(m):
    if m.node_tree is None:  # Blender 5+ always has a node tree; older versions need use_nodes
        m.use_nodes = True
    nt = m.node_tree
    bsdf = next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if bsdf is None:
        nt.nodes.clear()
        bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
        out = nt.nodes.new("ShaderNodeOutputMaterial")
        nt.links.new(bsdf.outputs[0], out.inputs[0])
    return bsdf


def make_materials():
    for name, (hexc, rough, metal, alpha, emit) in PALETTE.items():
        m = bpy.data.materials.new("LC_" + name)
        b = _principled(m)
        col = _srgb(hexc)
        b.inputs["Base Color"].default_value = (*col, 1)
        b.inputs["Roughness"].default_value = rough
        b.inputs["Metallic"].default_value = metal
        if alpha < 1:
            b.inputs["Alpha"].default_value = alpha
            for attr, val in (("surface_render_method", "BLENDED"), ("blend_method", "BLEND")):
                try:
                    setattr(m, attr, val)
                except Exception:
                    pass
        if emit:
            b.inputs["Emission Color"].default_value = (*col, 1)
            b.inputs["Emission Strength"].default_value = emit
        m.diffuse_color = (*col, alpha)
        MAT[name] = m


def mat(name):
    if name not in MAT:
        print(f"!! unknown material '{name}', using magenta")
        PALETTE[name] = ("#FF00FF", .5, 0, 1, 0)
        m = bpy.data.materials.new("LC_" + name)
        _principled(m).inputs["Base Color"].default_value = (1, 0, 1, 1)
        MAT[name] = m
    return MAT[name]


# ----------------------------------------------------------------------------------------------- mesh builder
BOX_F = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]


class MB:
    """Accumulates closed primitives into one mesh. Each primitive's winding is fixed by its signed volume."""

    def __init__(self):
        self.v, self.f, self.m, self.sm = [], [], [], []
        self.stack = [Matrix.Identity(4)]

    @contextmanager
    def at(self, loc=(0, 0, 0), rz=0.0, rx=0.0, ry=0.0, sc=None):
        M = (Matrix.Translation(Vector(loc)) @ Matrix.Rotation(math.radians(rz), 4, "Z")
             @ Matrix.Rotation(math.radians(ry), 4, "Y") @ Matrix.Rotation(math.radians(rx), 4, "X"))
        if sc is not None:
            s3 = (sc, sc, sc) if isinstance(sc, (int, float)) else sc
            M = M @ Matrix.Diagonal((*s3, 1))
        self.stack.append(self.stack[-1] @ M)
        try:
            yield
        finally:
            self.stack.pop()

    def poly(self, pts, faces, material, smooth=False, closed=True):
        T = self.stack[-1]
        W = [T @ Vector(p) for p in pts]
        if closed:
            vol = 0.0
            for f in faces:
                a = W[f[0]]
                for k in range(1, len(f) - 1):
                    vol += a.dot(W[f[k]].cross(W[f[k + 1]]))
            if vol < 0:
                faces = [tuple(reversed(f)) for f in faces]
        base = len(self.v)
        self.v.extend(W)
        sm = smooth if isinstance(smooth, (list, tuple)) else [smooth] * len(faces)
        for f, s in zip(faces, sm):
            self.f.append(tuple(base + i for i in f))
            self.m.append(material)
            self.sm.append(bool(s))

    # --- primitives
    def box(self, a, b, material):
        x0, x1 = sorted((a[0], b[0]))
        y0, y1 = sorted((a[1], b[1]))
        z0, z1 = sorted((a[2], b[2]))
        if x1 - x0 < 1e-4 or y1 - y0 < 1e-4 or z1 - z0 < 1e-4:
            return
        self.poly([(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0),
                   (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)], BOX_F, material)

    def boxc(self, c, size, material):
        x, y, z = c
        sx, sy, sz = size
        self.box((x - sx / 2, y - sy / 2, z), (x + sx / 2, y + sy / 2, z + sz), material)

    def hexa(self, P, material):
        self.poly(P, BOX_F, material)

    def beam(self, p, q, w, h, material, up=(0, 0, 1)):
        p, q = Vector(p), Vector(q)
        d = q - p
        if d.length < 1e-5:
            return
        d = d.normalized()
        side = d.cross(Vector(up))
        for alt in ((1, 0, 0), (0, 1, 0)):
            if side.length > 1e-5:
                break
            side = d.cross(Vector(alt))
        side = side.normalized()
        u = side.cross(d).normalized()
        sw, uh = side * (w / 2), u * (h / 2)
        self.hexa([p - sw - uh, p + sw - uh, q + sw - uh, q - sw - uh,
                   p - sw + uh, p + sw + uh, q + sw + uh, q - sw + uh], material)

    def cyl(self, base, r, h, material, seg=12, r2=None, axis="Z", smooth=True):
        r2 = r if r2 is None else r2
        rot = {"Z": {}, "X": {"ry": 90}, "Y": {"rx": -90}}[axis]
        ring = [(math.cos(2 * math.pi * k / seg), math.sin(2 * math.pi * k / seg)) for k in range(seg)]
        with self.at(base, **rot):
            pts = [(r * c, r * s, 0) for c, s in ring]
            if r2 > 1e-6:
                pts += [(r2 * c, r2 * s, h) for c, s in ring]
                F = [(k, (k + 1) % seg, seg + (k + 1) % seg, seg + k) for k in range(seg)]
                caps = [tuple(range(seg - 1, -1, -1)), tuple(range(seg, 2 * seg))]
            else:
                pts.append((0, 0, h))
                F = [(k, (k + 1) % seg, seg) for k in range(seg)]
                caps = [tuple(range(seg - 1, -1, -1))]
            self.poly(pts, F + caps, material, [smooth] * len(F) + [False] * len(caps))

    def tube(self, base, ro, ri, h, material, seg=16, smooth=True):
        ring = [(math.cos(2 * math.pi * k / seg), math.sin(2 * math.pi * k / seg)) for k in range(seg)]
        with self.at(base):
            P = []
            for r, z in ((ro, 0), (ro, h), (ri, 0), (ri, h)):
                P += [(r * c, r * s, z) for c, s in ring]
            OB, OT, IB, IT = 0, seg, 2 * seg, 3 * seg
            F, sm = [], []
            for k in range(seg):
                k1 = (k + 1) % seg
                F += [(OB + k, OB + k1, OT + k1, OT + k), (IB + k1, IB + k, IT + k, IT + k1),
                      (OT + k, OT + k1, IT + k1, IT + k), (OB + k1, OB + k, IB + k, IB + k1)]
                sm += [smooth, smooth, False, False]
            self.poly(P, F, material, sm)

    def sphere(self, c, r, material, seg=10, rings=6, sc=(1, 1, 1), smooth=True):
        pts = [(0, 0, r)]
        for i in range(1, rings):
            th = math.pi * i / rings
            for k in range(seg):
                ph = 2 * math.pi * k / seg
                pts.append((r * math.sin(th) * math.cos(ph), r * math.sin(th) * math.sin(ph), r * math.cos(th)))
        pts.append((0, 0, -r))
        pts = [(c[0] + p[0] * sc[0], c[1] + p[1] * sc[1], c[2] + p[2] * sc[2]) for p in pts]
        bot = len(pts) - 1
        F = [(0, 1 + k, 1 + (k + 1) % seg) for k in range(seg)]
        for i in range(rings - 2):
            a, b = 1 + i * seg, 1 + (i + 1) * seg
            F += [(a + k, b + k, b + (k + 1) % seg, a + (k + 1) % seg) for k in range(seg)]
        last = 1 + (rings - 2) * seg
        F += [(bot, last + (k + 1) % seg, last + k) for k in range(seg)]
        self.poly(pts, F, material, smooth)

    def prism(self, prof, axis, a0, a1, material):
        n = len(prof)
        lo, hi = sorted((a0, a1))

        def P(p, t):
            if axis == "X":
                return (t, p[0], p[1])
            if axis == "Y":
                return (p[0], t, p[1])
            return (p[0], p[1], t)

        pts = [P(p, lo) for p in prof] + [P(p, hi) for p in prof]
        F = [tuple(range(n - 1, -1, -1)), tuple(range(n, 2 * n))]
        F += [(i, (i + 1) % n, n + (i + 1) % n, n + i) for i in range(n)]
        self.poly(pts, F, material)

    def slab_top(self, top, t, material):
        """Prism hanging t metres below a planar top polygon."""
        n = len(top)
        top = [tuple(p) for p in top]
        bot = [(p[0], p[1], p[2] - t) for p in top]
        F = [tuple(range(n - 1, -1, -1)), tuple(range(n, 2 * n))]
        F += [(i, (i + 1) % n, n + (i + 1) % n, n + i) for i in range(n)]
        self.poly(bot + top, F, material)


# ----------------------------------------------------------------------------------------------- registry
REG = []


def R(mid, name, cat, attach, fn, footprint=None, sheet=None, game=None, tags=()):
    REG.append(dict(id=mid, name=name, category=cat, attach=attach, fn=fn, footprint=footprint,
                    sheet=sheet or cat, gameplay=game or {}, tags=list(tags)))


# ===================================================================================== 1. FLOORS & FOUNDATIONS
def floor_wood_boards(s, rng, z0=0.0):
    s.box((-2, -2, z0), (2, 2, FLOOR_T - 0.02), "wood_raw")
    n = 28
    w = TILE / n
    for k in range(n):
        y = -2 + k * w
        j = rng.uniform(-1.4, 1.4)
        s.box((-2, y + .004, FLOOR_T - .02), (j - .003, y + w - .004, FLOOR_T), "wood_floor")
        s.box((j + .003, y + .004, FLOOR_T - .02), (2, y + w - .004, FLOOR_T), "wood_floor")


def floor_tile(s, rng):
    s.box((-2, -2, 0), (2, 2, FLOOR_T - .01), "grout")
    for i in range(8):
        for j in range(8):
            x, y = -2 + i * .5, -2 + j * .5
            s.box((x + .005, y + .005, FLOOR_T - .01), (x + .495, y + .495, FLOOR_T), "tile_floor")


def floor_raised(s, rng):
    floor_wood_boards(s, rng)
    for x in (-1.8, 1.8):
        for y in (-1.8, 1.8):
            s.boxc((x, y, -0.6), (.3, .3, .6), "concrete")


def pad(material, pebbles=0):
    def f(s, rng):
        s.box((-2, -2, -0.15), (2, 2, 0.05), material)
        for _ in range(pebbles):
            s.sphere((rng.uniform(-1.9, 1.9), rng.uniform(-1.9, 1.9), 0.05), rng.uniform(.03, .07), "gravel",
                     seg=5, rings=3, sc=(1, 1, .5), smooth=False)
    return f


def clip_line(p, d, rect):
    """Clip the infinite line p + t*d to the rectangle (x0, z0, x1, z1); returns two points or None."""
    x0, z0, x1, z1 = rect
    t0, t1 = -1e9, 1e9
    for pi, di, lo, hi in ((p[0], d[0], x0, x1), (p[1], d[1], z0, z1)):
        if abs(di) < 1e-9:
            if pi < lo or pi > hi:
                return None
            continue
        a, b = (lo - pi) / di, (hi - pi) / di
        t0, t1 = max(t0, min(a, b)), min(t1, max(a, b))
    if t1 - t0 < 1e-3:
        return None
    return (p[0] + d[0] * t0, p[1] + d[1] * t0), (p[0] + d[0] * t1, p[1] + d[1] * t1)


def skirt_lattice(s, rng):
    z0, z1 = -0.6, 0.0
    y = Y_EXT - 0.02
    s.box((-2, y - .01, z1 - .1), (2, y + .03, z1), "trim_white")
    s.box((-2, y - .01, z0), (2, y + .03, z0 + .08), "trim_white")
    s.box((-2, y + .03, z0), (2, y + .05, z1), "soil")
    for sign in (1, -1):
        for k in range(-12, 14):
            seg = clip_line((k * 0.35, z0), (1, sign), (-2, z0 + .08, 2, z1 - .1))
            if seg:
                (ax, az), (bx, bz) = seg
                s.beam((ax, y + .012 * sign, az), (bx, y + .012 * sign, bz), .05, .012, "trim_white", up=(0, -1, 0))


R("floor_concrete", "Concrete Slab", "floors", "tile", lambda s, r: s.box((-2, -2, 0), (2, 2, FLOOR_T), "concrete"),
  (1, 1), game=dict(walkable=True, flammable=False))
R("floor_wood", "Wood Floor", "floors", "tile", lambda s, r: floor_wood_boards(s, r), (1, 1),
  game=dict(walkable=True, flammable=True))
R("floor_tile", "Tile Floor", "floors", "tile", floor_tile, (1, 1),
  game=dict(walkable=True, flammable=False, cleanable=True), tags=["clinic"])
R("floor_raised_wood", "Raised Wood Floor", "floors", "tile", floor_raised, (1, 1),
  game=dict(walkable=True, flammable=True, raisedBaseMeters=0.4),
  tags=["Lift the lot base 0.4 m; piers reach 0.6 m below the level base."])
R("pad_gravel", "Gravel Pad", "floors", "tile", pad("gravel", 40), (1, 1), game=dict(walkable=True, drivable=True))
R("pad_dirt", "Dirt / Packed Earth", "floors", "tile", pad("dirt"), (1, 1), game=dict(walkable=True, drivable=True))
R("pad_grass", "Grass Plot", "floors", "tile", pad("grass"), (1, 1), game=dict(walkable=True))
R("skirt_lattice", "Lattice Skirt", "floors", "edge", skirt_lattice, (1, 0),
  tags=["Hangs below a raised floor's outside edge."])

# ===================================================================================== 2/3. WALLS, DOORS, WINDOWS
STYLES = {
    "lap_white": dict(name="Wood Siding (Painted)", pattern="lap", mat="siding_white", trim="trim_white",
                      skirt="trim_white", corner="trim_white", door="door_green", shutter="door_green", fire=20),
    "lap_natural": dict(name="Wood Siding (Natural)", pattern="lap", mat="siding_natural", trim="wood_dark",
                        skirt="wood_dark", corner="wood_dark", door="door_wood", shutter="wood_dark", fire=20),
    "stucco": dict(name="Stucco", pattern="flat", mat="stucco", trim="trim_white", skirt="concrete",
                   corner="stucco", door="door_wood", shutter="door_green", fire=60),
    "batten_green": dict(name="Board & Batten (Green)", pattern="batten", mat="batten_green", trim="trim_white",
                         skirt="trim_white", corner="trim_white", door="door_wood", shutter="trim_white", fire=20),
    "batten_red": dict(name="Board & Batten (Barn Red)", pattern="batten", mat="batten_red", trim="trim_white",
                       skirt="trim_white", corner="trim_white", door="door_wood", shutter="trim_white", fire=20),
    "stone": dict(name="Stone", pattern="stone", mat="stone", trim="trim_white", skirt="concrete",
                  corner="stone", door="door_wood", shutter="door_green", fire=120),
    "plaster": dict(name="Interior Plaster", pattern="flat", mat="drywall", trim="trim_white", skirt="trim_white",
                    corner="trim_white", door="door_wood", shutter="trim_white", fire=30),
}
COURSE = {"lap": 0.2, "flat": 0.25, "batten": 0.25, "stone": 0.25}


def cladding(s, st, x0, x1, z0, z1, zbase, wall_x0, seed):
    p, m, yi = st["pattern"], st["mat"], Y_EXT + CLAD
    if x1 - x0 < 1e-3 or z1 - z0 < 1e-3:
        return
    if p == "flat":
        s.box((x0, Y_EXT, z0), (x1, yi, z1), m)
    elif p == "batten":
        s.box((x0, Y_EXT, z0), (x1, yi, z1), m)
        k0 = math.floor((x0 - wall_x0) / 0.4) - 1
        for k in range(k0, k0 + int((x1 - x0) / 0.4) + 4):
            cx = wall_x0 + 0.2 + k * 0.4
            a, b = max(x0, cx - .03), min(x1, cx + .03)
            if b - a > .02:
                s.box((a, Y_EXT - .02, z0), (b, Y_EXT, z1), m)
    elif p == "lap":
        h = 0.2
        k = math.floor((z0 - zbase) / h + 1e-6)
        while zbase + k * h < z1 - 1e-6:
            ca = zbase + k * h
            a, b = max(z0, ca), min(z1, ca + h)
            if b - a > .005:
                ya = Y_EXT - .012 + (a - ca) / h * .022
                yb = Y_EXT - .012 + (b - ca) / h * .022
                s.hexa([(x0, ya, a), (x1, ya, a), (x1, yi, a), (x0, yi, a),
                        (x0, yb, b), (x1, yb, b), (x1, yi, b), (x0, yi, b)], m)
            k += 1
    elif p == "stone":
        s.box((x0, Y_EXT + .012, z0), (x1, yi, z1), "mortar")
        h = 0.25
        k = math.floor((z0 - zbase) / h + 1e-6)
        while zbase + k * h < z1 - 1e-6:
            ca = zbase + k * h
            a, b = max(z0, ca) + .008, min(z1, ca + h) - .008
            rng = random.Random(seed * 1009 + k * 7919)
            x = wall_x0 - rng.uniform(0, .4)
            while x < x1:
                w = rng.uniform(.3, .65)
                bx0, bx1 = max(x, x0) + .008, min(x + w, x1) - .008
                depth = rng.uniform(0, .018)
                if bx1 - bx0 > .02 and b - a > .02:
                    s.box((bx0, Y_EXT - depth, a), (bx1, yi - .005, b), m)
                x += w
            k += 1


def opening_trim(s, st, o):
    x0, x1, z0, z1 = o["x0"], o["x1"], o["z0"], o["z1"]
    t = st["trim"]
    J = 0.03
    door = o["kind"] == "door"
    s.box((x0, Y_EXT, z0), (x0 + J, Y_INT, z1), t)
    s.box((x1 - J, Y_EXT, z0), (x1, Y_INT, z1), t)
    s.box((x0, Y_EXT, z1 - J), (x1, Y_INT, z1), t)
    if not door:
        s.box((x0, Y_EXT, z0), (x1, Y_INT, z0 + J), t)
    if o.get("casing", True):
        E, ya, yb = 0.1, Y_EXT - .035, Y_EXT + CLAD
        s.box((x0 - E, ya, z0), (x0, yb, z1 + E), t)
        s.box((x1, ya, z0), (x1 + E, yb, z1 + E), t)
        s.box((x0 - E, ya, z1), (x1 + E, yb, z1 + E), t)
        s.box((x0 - E - .02, ya - .015, z1 + E), (x1 + E + .02, yb, z1 + E + .03), t)
        if not door:
            s.box((x0 - .12, Y_EXT - .06, z0 - .05), (x1 + .12, yb, z0), t)
    Ei = 0.07
    s.box((x0 - Ei, Y_INT, z0), (x0, Y_INT + .018, z1 + Ei), "trim_white")
    s.box((x1, Y_INT, z0), (x1 + Ei, Y_INT + .018, z1 + Ei), "trim_white")
    s.box((x0 - Ei, Y_INT, z1), (x1 + Ei, Y_INT + .018, z1 + Ei), "trim_white")
    if not door:
        s.box((x0 - .08, Y_INT - .03, z0 - .02), (x1 + .08, Y_INT + .045, z0), "trim_white")
        s.box((x0 - .05, Y_INT, z0 - .12), (x1 + .05, Y_INT + .018, z0 - .02), "trim_white")


def build_wall(s, st, openings=(), length=TILE, z0=WALL_Z0, z1=WALL_Z1, seed=1, skirt=True, frieze=True):
    hl = length / 2
    ops = sorted(openings, key=lambda o: o["x0"])
    regions, cur = [], -hl
    for o in ops:
        if o["x0"] > cur + 1e-6:
            regions.append((cur, o["x0"], z0, z1))
        if o["z0"] > z0 + 1e-6:
            regions.append((o["x0"], o["x1"], z0, o["z0"]))
        if o["z1"] < z1 - 1e-6:
            regions.append((o["x0"], o["x1"], o["z1"], z1))
        cur = o["x1"]
    if cur < hl - 1e-6:
        regions.append((cur, hl, z0, z1))
    for a, b, c, d in regions:
        s.box((a, Y_EXT + CLAD, c), (b, Y_INT, d), "drywall")
        cladding(s, st, a, b, c, d, z0, -hl, seed)
    if skirt:
        s.box((-hl, Y_EXT - .025, 0), (hl, Y_EXT + CLAD, z0), st["skirt"])
        for a, b, c, d in regions:
            if abs(c - z0) < 1e-6:
                s.box((a, Y_EXT - .025, z0), (b, Y_EXT + CLAD, z0 + .12), st["skirt"])
    if frieze:
        s.box((-hl, Y_EXT - .025, z1 - .16), (hl, Y_EXT + CLAD, z1), st["trim"])
    for o in ops:
        opening_trim(s, st, o)


def knob(s, x, y0, y1, z):
    s.cyl((x, y0 - .06, z), .028, .06, "brass", seg=10, axis="Y")
    s.cyl((x, y1, z), .028, .06, "brass", seg=10, axis="Y")


def leaf_panel(s, x0, x1, z0, z1, material, y0=-.065, y1=-.02):
    s.box((x0, y0, z0), (x1, y1, z1), material)
    W, H, m = x1 - x0, z1 - z0, 0.11
    pw = (W - 3 * m) / 2
    for c in range(2):
        px0 = x0 + m + c * (pw + m)
        for ra, rb in ((.12, .92), (1.02, 1.42), (1.52, H - .12)):
            if rb - ra < .1:
                continue
            s.box((px0, y0 - .012, z0 + ra), (px0 + pw, y0, z0 + rb), material)
            s.box((px0, y1, z0 + ra), (px0 + pw, y1 + .012, z0 + rb), material)
    knob(s, x1 - .08, y0, y1, z0 + .98)


def leaf_halfglass(s, x0, x1, z0, z1, material, y0=-.065, y1=-.02):
    zm, st = z0 + 1.0, 0.12
    s.box((x0, y0, z0), (x1, y1, zm), material)
    s.box((x0 + st, y0 - .012, z0 + .15), (x1 - st, y0, zm - .12), material)
    s.box((x0, y0, zm), (x0 + st, y1, z1), material)
    s.box((x1 - st, y0, zm), (x1, y1, z1), material)
    s.box((x0 + st, y0, z1 - st), (x1 - st, y1, z1), material)
    s.box((x0 + st, y0 + .018, zm), (x1 - st, y0 + .026, z1 - st), "glass")
    cx, cz = (x0 + x1) / 2, (zm + z1 - st) / 2
    s.box((cx - .015, y0 + .008, zm), (cx + .015, y0 + .036, z1 - st), material)
    s.box((x0 + st, y0 + .008, cz - .015), (x1 - st, y0 + .036, cz + .015), material)
    knob(s, x1 - .08, y0, y1, z0 + .98)


def leaf_screen(s, x0, x1, z0, z1, material):
    leaf_panel(s, x0, x1, z0, z1, material, y0=0.0, y1=.045)
    ya, yb, st, t = -.095, -.07, .08, "trim_white"
    s.box((x0, ya, z0), (x0 + st, yb, z1), t)
    s.box((x1 - st, ya, z0), (x1, yb, z1), t)
    s.box((x0, ya, z1 - st), (x1, yb, z1), t)
    s.box((x0, ya, z0), (x1, yb, z0 + .3), t)
    zm = z0 + 1.0
    s.box((x0, ya, zm - .04), (x1, yb, zm + .04), t)
    s.box((x0 + st, ya + .01, z0 + .3), (x1 - st, ya + .014, z1 - st), "screen")
    s.cyl((x1 - .1, ya - .04, z0 + 1.0), .02, .04, "brass", seg=8, axis="Y")


def leaf_garage(s, x0, x1, z0, z1):
    n = 4
    h = (z1 - z0) / n
    for k in range(n):
        a, b = z0 + k * h + .005, z0 + (k + 1) * h - .005
        s.box((x0, -.04, a), (x1, 0.0, b), "garage_white")
        npn = 4
        pw = (x1 - x0 - .1 * (npn + 1)) / npn
        for c in range(npn):
            px = x0 + .1 + c * (pw + .1)
            if k == n - 1:
                s.box((px, -.048, a + .12), (px + pw, -.036, b - .12), "glass")
            else:
                s.box((px, -.05, a + .1), (px + pw, -.04, b - .1), "garage_white")
    cx = (x0 + x1) / 2
    s.box((cx - .15, -.07, z0 + .5), (cx + .15, -.04, z0 + .53), "metal_dark")


def barn_track(s, x0, x1, z1):
    s.box((x0 - .3, Y_EXT - .075, z1 + .08), (x1 + .3, Y_EXT - .035, z1 + .16), "metal_dark")


def leaf_barn(s, x0, x1, z0, z1, material="door_red", sides=(0, 1)):
    ya, yb, tf, fw = Y_EXT - .085, Y_EXT - .045, "trim_white", .12
    yo = ya - .02
    mid = (x0 + x1) / 2
    zb, zt = z0 - .02, z1 + .05
    zm = (zb + zt) / 2
    for side, (lx0, lx1) in enumerate(((x0 - .1, mid), (mid, x1 + .1))):
        if side not in sides:
            continue
        s.box((lx0, ya, zb), (lx1, yb, zt), material)
        s.box((lx0, yo, zb), (lx0 + fw, ya, zt), tf)
        s.box((lx1 - fw, yo, zb), (lx1, ya, zt), tf)
        s.box((lx0, yo, zt - fw), (lx1, ya, zt), tf)
        s.box((lx0, yo, zb), (lx1, ya, zb + fw), tf)
        s.box((lx0, yo, zm - fw / 2), (lx1, ya, zm + fw / 2), tf)
        for za, zz in ((zb + fw, zm - fw / 2), (zm + fw / 2, zt - fw)):
            s.beam((lx0 + fw, yo + .01, za), (lx1 - fw, yo + .01, zz), .1, .02, tf, up=(0, -1, 0))
            s.beam((lx1 - fw, yo + .01, za), (lx0 + fw, yo + .01, zz), .1, .02, tf, up=(0, -1, 0))


def window_fill(s, o, t, cols=1, double_hung=True, shutters=None):
    x0, x1, z0, z1 = o["x0"] + .03, o["x1"] - .03, o["z0"] + .03, o["z1"] - .03
    J, ya, yb = .05, -.05, .03
    s.box((x0, ya, z0), (x0 + J, yb, z1), t)
    s.box((x1 - J, ya, z0), (x1, yb, z1), t)
    s.box((x0, ya, z1 - J), (x1, yb, z1), t)
    s.box((x0, ya, z0), (x1, yb, z0 + J), t)
    s.box((x0 + J, -.012, z0 + J), (x1 - J, -.004, z1 - J), "glass")
    if double_hung:
        zm = (z0 + z1) / 2
        s.box((x0 + J, -.035, zm - .025), (x1 - J, 0.0, zm + .025), t)
    for c in range(1, cols):
        x = x0 + J + (x1 - x0 - 2 * J) * c / cols
        s.box((x - .03, -.035, z0 + J), (x + .03, 0.0, z1 - J), t)
    if shutters:
        sw = (o["x1"] - o["x0"]) / 2 + .05
        ya2, yb2 = Y_EXT - .065, Y_EXT - .04
        za, zb = o["z0"], o["z1"] + .05
        for sx0 in (o["x0"] - .12 - sw, o["x1"] + .12):
            sx1 = sx0 + sw
            s.box((sx0, ya2, za), (sx0 + .06, yb2, zb), shutters)
            s.box((sx1 - .06, ya2, za), (sx1, yb2, zb), shutters)
            s.box((sx0, ya2, za), (sx1, yb2, za + .06), shutters)
            s.box((sx0, ya2, zb - .06), (sx1, yb2, zb), shutters)
            z = za + .1
            while z < zb - .08:
                s.beam((sx0 + .06, (ya2 + yb2) / 2, z), (sx1 - .06, (ya2 + yb2) / 2, z), .05, .008, shutters,
                       up=(0, -1, 1))
                z += .065


def door_op(w, h, x=0.0, casing=True):
    return dict(kind="door", x0=x - w / 2, x1=x + w / 2, z0=WALL_Z0, z1=WALL_Z0 + h, casing=casing)


def win_op(w, h, sill, x=0.0):
    return dict(kind="window", x0=x - w / 2, x1=x + w / 2, z0=WALL_Z0 + sill, z1=WALL_Z0 + sill + h)


def wall_kind(kind):
    """Returns (display name, builder(s, st, seed), openings description)."""
    if kind == "full":
        return "Full Wall", lambda s, st, sd: build_wall(s, st, seed=sd), []
    if kind.startswith("door_"):
        spec = {"door_wood": ("Wood Door", .9, 2.1), "door_halfglass": ("Half-Window Door", .9, 2.1),
                "door_screen": ("Screen Door", .9, 2.1), "door_garage": ("Garage Door (Single)", 2.7, 2.4),
                "door_barn": ("Barn Door (Sliding)", 3.0, 2.6)}[kind]
        name, w, h = spec

        def f(s, st, sd):
            o = door_op(w, h, casing=kind != "door_barn")
            build_wall(s, st, [o], seed=sd)
            if kind == "door_barn":
                barn_track(s, o["x0"], o["x1"], o["z1"])
        return name, f, [dict(type=kind[5:], widthMeters=w, heightMeters=h, offsetMeters=0.0,
                              passable=True, vehicle=kind in ("door_garage", "door_barn"))]
    spec = {"win_small": ("Small Window", .6, .6, 1.4, 0.0, 1, False, False),
            "win_large": ("Large Window", 1.2, 1.4, .9, 0.0, 2, True, False),
            "win_shutter": ("Shutter Window", .9, 1.3, .9, 0.0, 1, True, True),
            "win_corner": ("Corner Window", .9, 1.3, .9, 1.25, 1, True, False),
            "win_wide": ("Wide Window", 2.4, 1.2, .9, 0.0, 3, False, False)}[kind]
    name, w, h, sill, x, cols, dh, sh = spec

    def g(s, st, sd):
        o = win_op(w, h, sill, x)
        build_wall(s, st, [o], seed=sd)
        window_fill(s, o, st["trim"], cols=cols, double_hung=dh, shutters=st["shutter"] if sh else None)
    return name, g, [dict(type="window", widthMeters=w, heightMeters=h, sillMeters=sill, offsetMeters=x)]


HINGE_X, HINGE_Y = -.42, -.0425


def door_leaf(kind, material):
    def f(s, r):
        x0, x1, z0, z1 = -.42, .42, WALL_Z0, WALL_Z0 + 2.1 - .03
        with s.at((-HINGE_X, -HINGE_Y, 0)):
            if kind == "wood":
                leaf_panel(s, x0, x1, z0, z1, material)
            elif kind == "halfglass":
                leaf_halfglass(s, x0, x1, z0, z1, material)
            else:
                leaf_screen(s, x0, x1, z0, z1, material)
    return f


def garage_leaf(open_):
    def f(s, r):
        x0, x1, z0, z1 = -1.32, 1.32, WALL_Z0, WALL_Z0 + 2.4 - .03
        if not open_:
            leaf_garage(s, x0, x1, z0, z1)
            return
        # Rolled up: the sections lie flat under the ceiling, reaching into the room (+Y in Blender).
        with s.at((0, .15, WALL_Z0 + 2.4 + .06), rx=-90):
            with s.at((0, 0, -z0)):
                leaf_garage(s, x0, x1, z0, z1)
    return f


WALL_KINDS = ["full", "door_wood", "door_halfglass", "door_screen", "door_garage", "door_barn",
              "win_small", "win_large", "win_shutter", "win_corner", "win_wide"]


def half_wall(s, st, sd):
    z1 = WALL_Z0 + 1.0
    build_wall(s, st, z1=z1, seed=sd, frieze=False)
    s.box((-2, -.13, z1), (2, .13, z1 + .05), st["trim"])


def gable_infill(s, st, sd, half=4.0, rise=2.0):
    # The core's outer layer uses the siding material so the stepped cladding bands read as one surface under the rake.
    s.prism([(-half, 0), (half, 0), (half, .02), (0, .02 + rise), (-half, .02)], "Y", Y_EXT, Y_EXT + CLAD, st["mat"])
    s.prism([(-half, 0), (half, 0), (half, .02), (0, .02 + rise), (-half, .02)], "Y", Y_EXT + CLAD, Y_INT, "drywall")
    h, z = COURSE[st["pattern"]], 0.0
    while z < .02 + rise - .05:
        b = min(z + h, .02 + rise)
        hw = min(half, half - (b - .02) / (rise / half))
        if hw > .05:
            cladding(s, st, -hw, hw, z, b, 0.0, -half, sd)
        z += h
    vz = rise * .42
    s.box((-.3, Y_EXT - .03, vz), (.3, Y_EXT + CLAD, vz + .5), st["trim"])
    s.box((-.23, Y_EXT - .036, vz + .06), (.23, Y_EXT - .03, vz + .44), "metal_dark")
    for k in range(5):
        z = vz + .1 + k * .07
        s.beam((-.23, Y_EXT - .045, z), (.23, Y_EXT - .045, z), .05, .008, st["trim"], up=(0, -1, 1))


def shed_infill(s, st, sd):
    rise = 1.0
    s.prism([(-2, 0), (2, 0), (2, .02 + rise), (-2, .02)], "Y", Y_EXT, Y_EXT + CLAD, st["mat"])
    s.prism([(-2, 0), (2, 0), (2, .02 + rise), (-2, .02)], "Y", Y_EXT + CLAD, Y_INT, "drywall")
    h, z = COURSE[st["pattern"]], 0.0
    while z < .02 + rise - .05:
        b = min(z + h, .02 + rise)
        xlo = max(-2.0, (b - .02) / (rise / 4) - 2)
        if 2 - xlo > .05:
            cladding(s, st, xlo, 2, z, b, 0.0, -2, sd)
        z += h


def corner_outer(s, st, sd):
    s.box((-.13, -.13, 0), (.02, .02, WALL_Z1), st["corner"])


def corner_inner(s, st, sd):
    s.box((-.16, -.16, 0), (-.1, -.1, WALL_Z1), st["corner"])
    s.box((0, 0, WALL_Z0), (.1, .1, WALL_Z1), "drywall")


def _style_reg():
    for sk, st in STYLES.items():
        sheet = "walls_" + sk
        g = dict(fireRatingMinutes=st["fire"], flammable=st["fire"] < 60, windRatingMph=110)
        for kind in WALL_KINDS:
            name, fn, ops = wall_kind(kind)
            seed = zlib.crc32(sk.encode()) % 997
            R(f"wall_{sk}_{kind}", f"{name} - {st['name']}", "walls", "edge",
              (lambda f, st_, sd: (lambda s, r: f(s, st_, sd)))(fn, st, seed), (1, 0), sheet,
              game=dict(g, openings=ops, exterior=True), tags=[sk])
        R(f"wall_{sk}_half", f"Half Wall - {st['name']}", "walls", "edge",
          (lambda st_: (lambda s, r: half_wall(s, st_, 3)))(st), (1, 0), sheet, game=dict(g, heightMeters=1.0))
        R(f"corner_{sk}_outer", f"Outer Corner - {st['name']}", "walls", "vertex",
          (lambda st_: (lambda s, r: corner_outer(s, st_, 0)))(st), (0, 0), sheet)
        R(f"corner_{sk}_inner", f"Inner Corner - {st['name']}", "walls", "vertex",
          (lambda st_: (lambda s, r: corner_inner(s, st_, 0)))(st), (0, 0), sheet)
        R(f"gable_infill_{sk}", f"Gable Infill (2 tiles) - {st['name']}", "walls", "edge",
          (lambda st_: (lambda s, r: gable_infill(s, st_, 5)))(st), (2, 0), sheet,
          tags=["Sits at roof level under a 2-tile-span gable end."])
        R(f"shed_infill_{sk}", f"Shed Infill - {st['name']}", "walls", "edge",
          (lambda st_: (lambda s, r: shed_infill(s, st_, 5)))(st), (1, 0), sheet,
          tags=["Fills the side triangle under a 1x1 shed roof; low end at local -X."])


_style_reg()

for _kind, _label in (("wood", "Wood Door"), ("halfglass", "Half-Window Door"), ("screen", "Screen Door")):
    for _colour, _material in (("green", "door_green"), ("wood", "door_wood")):
        R(f"door_leaf_{_kind}_{_colour}", f"{_label} Leaf ({_colour})", "doors", "free", door_leaf(_kind, _material), sheet="doors",
          game=dict(interactive=True, motion="swing"), tags=["Origin on the hinge axis at the level base; swings into the room (-Z)."])
R("door_leaf_garage", "Garage Door Leaf", "doors", "free", garage_leaf(False), sheet="doors", game=dict(interactive=True, motion="overhead"),
  tags=["Wall coordinates: place at the wall origin."])
R("door_leaf_garage_open", "Garage Door Leaf (Open)", "doors", "free", garage_leaf(True), sheet="doors", game=dict(interactive=True, motion="overhead"))
R("door_leaf_barn_left", "Barn Door Leaf (Left)", "doors", "free", lambda s, r: leaf_barn(s, -1.5, 1.5, WALL_Z0, WALL_Z0 + 2.6, sides=(0,)),
  sheet="doors", game=dict(interactive=True, motion="slide"), tags=["Wall coordinates; slides along -X to open."])
R("door_leaf_barn_right", "Barn Door Leaf (Right)", "doors", "free", lambda s, r: leaf_barn(s, -1.5, 1.5, WALL_Z0, WALL_Z0 + 2.6, sides=(1,)),
  sheet="doors", game=dict(interactive=True, motion="slide"), tags=["Wall coordinates; slides along +X to open."])


def low_wall_railing(s, r):
    st = STYLES["stucco"]
    z1 = WALL_Z0 + .55
    build_wall(s, st, z1=z1, seed=3, frieze=False)
    s.box((-2, -.13, z1), (2, .13, z1 + .05), "trim_white")
    railing(s, (-2, 0, 0), (2, 0, 0), z1 + .05, h=.5, spacing=.14)


# ===================================================================================== 4/5. ROOFS & ROOF DETAILS
FINISH = {
    "shingle": dict(name="Asphalt Shingle", mat="shingle", ridge="shingle", pattern="shingle"),
    "metal_green": dict(name="Metal (Green)", mat="metal_green", ridge="metal_green", pattern="metal"),
    "metal_galv": dict(name="Metal (Galvanized)", mat="metal_galv", ridge="metal_galv", pattern="metal"),
}


def roof_face(s, fin, x0, y0, L, R_, pitch, ze, interval, rmax, poly, fascia=True):
    def P(t, r):
        return Vector((x0 + t, y0 + r, ze + pitch * r))

    s.slab_top([tuple(P(t, r)) for t, r in poly], ROOF_T, fin["mat"])
    n = Vector((0, -pitch, 1)).normalized()
    if fin["pattern"] == "shingle":
        r = 0.3
        while r < R_ - .1:
            t0, t1 = interval(r)
            if t1 - t0 > .15:
                s.beam(P(t0, r) + n * .012, P(t1, r) + n * .012, .08, .024, fin["mat"], up=n)
            r += 0.3
    else:
        t = 0.15
        while t < L:
            rm = rmax(t)
            if rm > .15:
                s.beam(P(t, 0) + n * .015, P(t, rm) + n * .015, .035, .03, fin["mat"], up=n)
            t += 0.3
    if fascia:
        s.box((x0, y0 - .035, ze - ROOF_T - .1), (x0 + L, y0, ze + .005), "trim_white")


def roof_gable(s, fin, x0, x1, rake=False, half=4.0, pitch=PITCH):
    o = EAVE
    ze = 0.2 - pitch * o
    R_ = half + o
    L = x1 - x0
    rect = [(0, 0), (L, 0), (L, R_), (0, R_)]
    roof_face(s, fin, x0, -R_, L, R_, pitch, ze, lambda r: (0, L), lambda t: R_, rect)
    with s.at(rz=180):
        roof_face(s, fin, -x1, -R_, L, R_, pitch, ze, lambda r: (0, L), lambda t: R_, rect)
    zr = ze + pitch * R_
    s.beam((x0, 0, zr + .01), (x1, 0, zr + .01), .32, .07, fin["ridge"])
    if rake:
        for sg in (-1, 1):
            s.beam((x0 + .02, sg * R_, ze - .12), (x0 + .02, 0, zr - .12), .04, .28, "trim_white")


def roof_hip2(s, fin):
    o = EAVE
    ze = 0.2 - PITCH * o
    R_ = 4 + o
    L = 2 * R_
    for rz in (0, 90, 180, 270):
        with s.at(rz=rz):
            roof_face(s, fin, -R_, -R_, L, R_, PITCH, ze, lambda r: (r, L - r), lambda t: min(t, L - t),
                      [(0, 0), (L, 0), (L / 2, R_)])
    zr = ze + PITCH * R_
    for cx, cy in ((-R_, -R_), (R_, -R_), (R_, R_), (-R_, R_)):
        s.beam((cx, cy, ze + .01), (0, 0, zr + .01), .25, .06, fin["ridge"])


def roof_hip_end(s, fin):
    o = EAVE
    ze = 0.2 - PITCH * o
    R_ = 4 + o
    L1 = 2 + o + 2
    roof_face(s, fin, -2 - o, -R_, L1, R_, PITCH, ze, lambda r: (r, L1), lambda t: min(t, R_),
              [(0, 0), (L1, 0), (L1, R_)])
    with s.at(rz=180):
        roof_face(s, fin, -2, -R_, L1, R_, PITCH, ze, lambda r: (0, L1 - r), lambda t: min(L1 - t, R_),
                  [(0, 0), (L1, 0), (0, R_)])
    with s.at(rz=-90):
        roof_face(s, fin, -R_, -2 - o, 2 * R_, R_, PITCH, ze, lambda r: (r, 2 * R_ - r),
                  lambda t: min(t, 2 * R_ - t), [(0, 0), (2 * R_, 0), (R_, R_)])
    zr = ze + PITCH * R_
    for cy in (-R_, R_):
        s.beam((-2 - o, cy, ze + .01), (2, 0, zr + .01), .25, .06, fin["ridge"])


def roof_shed(s, fin, depth_tiles):
    o, p = EAVE, 0.25
    ze = 0.2 - p * o
    hy = 2 * depth_tiles
    R_ = 2 * hy + 2 * o
    L = 4 + 2 * o
    roof_face(s, fin, -2 - o, -hy - o, L, R_, p, ze, lambda r: (0, L), lambda t: R_,
              [(0, 0), (L, 0), (L, R_), (0, R_)])
    zt = ze + p * R_
    s.box((-2 - o, hy + o, zt - ROOF_T - .1), (2 + o, hy + o + .035, zt + .005), "trim_white")
    for sx in (-2 - o + .018, 2 + o - .018):
        s.beam((sx, -hy - o, ze - .12), (sx, hy + o, zt - .12), .035, .28, "trim_white")


def roof_flat(s, r):
    s.box((-2, -2, 0), (2, 2, .25), "roof_membrane")
    for x in (-1, 0, 1):
        s.box((x - .01, -2, .25), (x + .01, 2, .262), "roof_membrane")


def roof_flat_edge(s, r):
    s.box((-2, Y_EXT - .03, 0), (2, Y_INT, .45), "stucco")
    s.box((-2, Y_EXT - .06, .45), (2, Y_INT + .03, .5), "metal_galv")


for fk, fin in FINISH.items():
    g = dict(windRatingMph=90, roofFinish=fk, flammable=fk == "shingle")
    R(f"roof_gable_mid_{fk}", f"Gable Roof, Middle - {fin['name']}", "roofs", "roof",
      (lambda f_: (lambda s, r: roof_gable(s, f_, -2, 2)))(fin), (1, 2), game=g,
      tags=["Ridge along local X; spans 2 tiles (8 m) in local Y."])
    R(f"roof_gable_end_{fk}", f"Gable Roof, End - {fin['name']}", "roofs", "roof",
      (lambda f_: (lambda s, r: roof_gable(s, f_, -2 - EAVE, 2, rake=True)))(fin), (1, 2), game=g,
      tags=["Rake overhang at local -X; pair with gable_infill_*."])
    R(f"roof_hip_end_{fk}", f"Hip Roof, End - {fin['name']}", "roofs", "roof",
      (lambda f_: (lambda s, r: roof_hip_end(s, f_)))(fin), (1, 2), game=g,
      tags=["Hip at local -X; continue with roof_gable_mid_*."])
    R(f"roof_hip_2x2_{fk}", f"Hip Roof, 2x2 - {fin['name']}", "roofs", "roof",
      (lambda f_: (lambda s, r: roof_hip2(s, f_)))(fin), (2, 2), game=g)
    R(f"roof_shed_1x1_{fk}", f"Shed Roof (Small) - {fin['name']}", "roofs", "roof",
      (lambda f_: (lambda s, r: roof_shed(s, f_, 1)))(fin), (1, 1), game=g, tags=["High side at local +Y."])
    R(f"roof_shed_2x1_{fk}", f"Shed Roof (Large) - {fin['name']}", "roofs", "roof",
      (lambda f_: (lambda s, r: roof_shed(s, f_, 2)))(fin), (1, 2), game=g, tags=["High side at local +Y."])
R("roof_flat_1x1", "Flat Roof (Utility)", "roofs", "roof", roof_flat, (1, 1), game=dict(windRatingMph=90))
R("roof_flat_edge", "Flat Roof Parapet Edge", "roofs", "edge", roof_flat_edge, (1, 0))


def chimney(s, r):
    s.box((-.35, -.35, 0), (.35, .35, 2.2), "brick")
    for z in (.6, 1.2, 1.8):
        s.box((-.355, -.355, z), (.355, .355, z + .015), "mortar")
    s.box((-.42, -.42, 2.2), (.42, .42, 2.32), "concrete")
    s.cyl((0, 0, 2.32), .1, .25, "metal_dark", seg=10)


def vent_pipe(s, r):
    s.box((-.15, -.15, 0), (.15, .15, .03), "metal_dark")
    s.cyl((0, 0, 0), .05, .6, "metal_galv", seg=10)
    s.cyl((0, 0, .6), .07, .06, "metal_galv", seg=10)


def roof_vent(s, r):
    s.box((-.3, -.3, 0), (.3, .3, .03), "metal_dark")
    s.box((-.2, -.2, 0), (.2, .2, .18), "metal_dark")
    s.slab_top([(-.26, -.26, .26), (.26, -.26, .26), (.26, .26, .3), (-.26, .26, .3)], .04, "metal_dark")


def wind_vent(s, r):
    s.box((-.25, -.25, 0), (.25, .25, .03), "metal_galv")
    s.cyl((0, 0, 0), .15, .25, "metal_galv", seg=12)
    for k in range(14):
        a = 2 * math.pi * k / 14
        p = Vector((math.cos(a) * .2, math.sin(a) * .2, .27))
        q = Vector((math.cos(a + .5) * .2, math.sin(a + .5) * .2, .6))
        s.beam(p, q, .07, .01, "metal_galv", up=(math.cos(a), math.sin(a), 0))
    s.sphere((0, 0, .6), .17, "metal_galv", seg=12, rings=5, sc=(1, 1, .5))


def tv_antenna(s, r):
    s.box((-.1, -.1, 0), (.1, .1, .04), "metal_dark")
    s.cyl((0, 0, 0), .025, 3.0, "metal_galv", seg=8)
    for k, z in enumerate((1.9, 2.2, 2.5, 2.8)):
        L = 1.4 - k * .25
        s.beam((0, -.4, z), (0, .5, z), .03, .03, "metal_galv")
        s.beam((-L / 2, .3 - k * .2, z), (L / 2, .3 - k * .2, z), .015, .015, "metal_galv")
        s.beam((-L / 2, -.2 + k * .1, z), (L / 2, -.2 + k * .1, z), .015, .015, "metal_galv")


def gutter(s, r):
    y = -EAVE - .08
    z0 = 0.2 - PITCH * EAVE - ROOF_T - .17
    s.box((-2, y - .06, z0), (2, y + .06, z0 + .02), "metal_galv")
    s.box((-2, y - .07, z0), (2, y - .06, z0 + .13), "metal_galv")
    s.box((-2, y + .06, z0 + .04), (2, y + .07, z0 + .13), "metal_galv")


R("chimney", "Chimney", "roof_details", "free", chimney, game=dict(fireSource=True))
R("vent_pipe", "Vent Pipe", "roof_details", "free", vent_pipe, game=dict(plumbing=True))
R("roof_vent", "Roof Vent", "roof_details", "free", roof_vent)
R("wind_vent", "Wind Vent (Turbine)", "roof_details", "free", wind_vent)
R("tv_antenna", "TV Antenna", "roof_details", "free", tv_antenna, game=dict(lightningRisk=True))
R("gutter", "Gutter", "roof_details", "edge", gutter, (1, 0),
  tags=["Hangs on the eave of a 6:12 roof; origin at the wall edge, wall top."])


# ===================================================================================== 6. EXTERIOR DETAILS
def railing(s, p, q, base, h=.9, material="trim_white", spacing=.12):
    p, q = Vector(p), Vector(q)
    d = q - p
    s.beam(p + Vector((0, 0, base + h)), q + Vector((0, 0, base + h)), .07, .05, material)
    s.beam(p + Vector((0, 0, base + .1)), q + Vector((0, 0, base + .1)), .05, .04, material)
    n = max(1, int(d.length / spacing))
    for k in range(1, n):
        c = p + d * (k / n)
        s.box((c.x - .02, c.y - .02, base + .12), (c.x + .02, c.y + .02, base + h - .025), material)


def porch(s, fk, wt):
    W = 4.0 * wt
    hx, top, fr, bk = W / 2, FLOOR_T, -2.0, 2.0 - .1
    s.box((-hx, fr, 0), (hx, bk, top - .03), "wood_raw")
    n = int(W / .14)
    for k in range(n):
        x = -hx + k * W / n
        s.box((x + .004, fr, top - .03), (x + W / n - .004, bk, top), "wood_deck")
    s.box((-hx, fr - .03, 0), (hx, fr, top), "trim_white")
    posts = [-hx + .12, hx - .12] + ([-1.0, 1.0] if wt > 1 else [])
    for x in posts:
        s.box((x - .075, fr + .05, top), (x + .075, fr + .2, 2.3), "trim_white")
    s.box((-hx, fr + .03, 2.3), (hx, fr + .22, 2.45), "trim_white")
    o = .25
    R_ = bk - (fr - o)
    L = W + .3
    roof_face(s, FINISH[fk], -hx - .15, fr - o, L, R_, .15, 2.47, lambda r: (0, L), lambda t: R_,
              [(0, 0), (L, 0), (L, R_), (0, R_)])
    for sx in (-hx + .12, hx - .12):
        railing(s, (sx, fr + .2, 0), (sx, bk - .05, 0), top)
    gap = .8
    railing(s, (-hx + .2, fr + .125, 0), (-gap, fr + .125, 0), top)
    railing(s, (gap, fr + .125, 0), (hx - .2, fr + .125, 0), top)


def steps(n):
    def f(s, r):
        for k in range(n):
            s.box((-.7, -(n - k) * .3, 0), (.7, 0, (k + 1) * .2), "wood_deck")
            s.box((-.7, -(n - k) * .3 - .02, (k + 1) * .2 - .03), (.7, -(n - k) * .3 + .02, (k + 1) * .2 + .005),
                  "wood_deck")
    return f


def ramp(s, r):
    w, rise, x0, xt, x1 = .6, .6, -4.0, 3.2, 4.0

    def zr(x):
        return .03 + (min(x, xt) - x0) / (xt - x0) * (rise - .03)

    s.slab_top([(x0, -w, .03), (xt, -w, rise), (xt, w, rise), (x0, w, .03)], .25, "concrete")
    s.box((xt, -w, 0), (x1, w, rise), "concrete")
    for sy in (-w - .04, w + .04):
        for x in [x0 + .15 + k * 1.5 for k in range(5)] + [xt, x1 - .05]:
            z = zr(x)
            s.box((x - .03, sy - .03, z - .05), (x + .03, sy + .03, z + .95), "metal_galv")
        for dz, rad in ((.9, .05), (.45, .035)):
            s.beam((x0 + .1, sy, zr(x0 + .1) + dz), (xt, sy, rise + dz), rad, rad, "metal_galv")
            s.beam((xt, sy, rise + dz), (x1, sy, rise + dz), rad, rad, "metal_galv")


def awning(s, r):
    s.slab_top([(-.9, 0, 2.75), (.9, 0, 2.75), (.9, -1.0, 2.45), (-.9, -1.0, 2.45)], .05, "metal_green")
    s.box((-.9, -1.02, 2.3), (.9, -.98, 2.45), "metal_green")
    for x in (-.8, .8):
        s.beam((x, 0, 2.2), (x, -.9, 2.45), .03, .03, "metal_dark")


def post(s, r):
    s.box((-.12, -.12, 0), (.12, .12, .05), "metal_dark")
    s.box((-.075, -.075, .05), (.075, .075, 2.95), "wood_raw")
    s.box((-.1, -.1, 2.95), (.1, .1, 3.0), "wood_raw")


def column(s, r):
    s.box((-.16, -.16, 0), (.16, .16, .1), "trim_white")
    s.cyl((0, 0, .1), .12, 2.6, "trim_white", seg=16, r2=.1)
    s.box((-.15, -.15, 2.7), (.15, .15, 2.8), "trim_white")


def ext_light(s, r):
    s.box((-.08, -.03, 1.9), (.08, 0, 2.2), "metal_dark")
    s.box((-.025, -.15, 2.0), (.025, -.03, 2.03), "metal_dark")
    s.box((-.09, -.27, 1.95), (.09, -.12, 1.97), "metal_dark")
    s.box((-.09, -.27, 2.13), (.09, -.12, 2.15), "metal_dark")
    for x in (-.09, .07):
        for y in (-.27, -.14):
            s.box((x, y, 1.97), (x + .02, y + .02, 2.13), "metal_dark")
    s.box((-.06, -.24, 1.97), (.06, -.15, 2.13), "lamp_glow")
    s.cyl((0, -.195, 2.15), .13, .1, "metal_dark", seg=4, r2=0, smooth=False)


def downspout(s, r):
    s.box((-.04, -.12, .25), (.04, -.04, 3.0), "metal_galv")
    s.box((-.04, -.4, .1), (.04, -.04, .25), "metal_galv")
    s.box((-.15, -.7, 0), (.15, -.3, .04), "concrete")


def rain_barrel(s, r):
    s.cyl((0, 0, 0), .3, .9, "plastic_blue", seg=16)
    s.cyl((0, 0, .9), .31, .04, "plastic_blue", seg=16)
    for z in (.2, .7):
        s.cyl((0, 0, z), .305, .03, "plastic_blue", seg=16)
    s.cyl((0, -.32, .12), .02, .08, "brass", seg=8, axis="Y")


for fk, fin in FINISH.items():
    R(f"porch_small_{fk}", f"Porch (Small) - {fin['name']}", "exterior", "tile",
      (lambda f_: (lambda s, r: porch(s, f_, 1)))(fk), (1, 1), "exterior",
      game=dict(walkable=True), tags=["Back edge (local +Y) meets the house wall."])
    R(f"porch_large_{fk}", f"Porch (Large) - {fin['name']}", "exterior", "tile",
      (lambda f_: (lambda s, r: porch(s, f_, 2)))(fk), (2, 1), "exterior", game=dict(walkable=True))
R("steps_1", "Steps (1 rise)", "exterior", "free", steps(1), game=dict(walkable=True, riseMeters=.2))
R("steps_3", "Steps (3 rises)", "exterior", "free", steps(3), game=dict(walkable=True, riseMeters=.6))
R("ramp_2x1", "Ramp (1:12)", "exterior", "tile", ramp, (2, 1),
  game=dict(walkable=True, accessible=True, riseMeters=.6), tags=["Rises toward local +X."])
R("awning", "Awning", "exterior", "free", awning)
R("post_wood", "Post", "exterior", "free", post)
R("support_column", "Support Column", "exterior", "free", column)
R("exterior_light", "Exterior Light", "exterior", "free", ext_light, game=dict(power=True, lightSource=True))
R("downspout", "Downspout", "exterior", "free", downspout)
R("rain_barrel", "Rain Barrel", "exterior", "free", rain_barrel, game=dict(water=True))
R("wall_low_railing", "Low Wall with Railing", "exterior", "edge", low_wall_railing, (1, 0), "exterior")


# ===================================================================================== 7. INTERIOR / WALL DETAILS
def wall_trim_top(s, r):
    s.box((-2, Y_INT, WALL_Z1 - .12), (2, Y_INT + .03, WALL_Z1), "trim_white")
    s.box((-2, Y_INT, WALL_Z1 - .04), (2, Y_INT + .06, WALL_Z1), "trim_white")


def baseboard(s, r):
    s.box((-2, Y_INT, WALL_Z0), (2, Y_INT + .015, WALL_Z0 + .12), "trim_white")
    s.box((-2, Y_INT, WALL_Z0 + .1), (2, Y_INT + .022, WALL_Z0 + .12), "trim_white")


def chair_rail(s, r):
    s.box((-2, Y_INT, WALL_Z0 + .85), (2, Y_INT + .025, WALL_Z0 + .92), "trim_white")


def corner_guard(s, r):
    s.box((-.06, -.06, WALL_Z0), (0, -.054, WALL_Z0 + 1.4), "trim_white")
    s.box((-.06, -.06, WALL_Z0), (-.054, 0, WALL_Z0 + 1.4), "trim_white")


def window_trim(s, r):
    x0, x1, z0, z1 = -.6, .6, WALL_Z0 + .9, WALL_Z0 + 2.3
    E = .07
    for a, b in (((x0 - E, z0), (x0, z1 + E)), ((x1, z0), (x1 + E, z1 + E)), ((x0 - E, z1), (x1 + E, z1 + E))):
        s.box((a[0], Y_INT, a[1]), (b[0], Y_INT + .018, b[1]), "trim_white")
    s.box((x0 - .08, Y_INT, z0 - .02), (x1 + .08, Y_INT + .05, z0), "trim_white")
    s.box((x0 - .05, Y_INT, z0 - .12), (x1 + .05, Y_INT + .018, z0 - .02), "trim_white")


def door_trim(s, r):
    x0, x1, z1 = -.45, .45, WALL_Z0 + 2.1
    E = .08
    s.box((x0 - E, Y_INT, WALL_Z0), (x0, Y_INT + .02, z1 + E), "trim_white")
    s.box((x1, Y_INT, WALL_Z0), (x1 + E, Y_INT + .02, z1 + E), "trim_white")
    s.box((x0 - E, Y_INT, z1), (x1 + E, Y_INT + .02, z1 + E), "trim_white")
    s.box((x0 - E - .015, Y_INT, z1 + E), (x1 + E + .015, Y_INT + .03, z1 + E + .03), "trim_white")


def handrail(s, r):
    z = WALL_Z0 + .9
    s.cyl((-1.95, Y_INT + .07, z), .022, 3.9, "wood_floor", seg=10, axis="X")
    for x in (-1.6, 0, 1.6):
        s.box((x - .03, Y_INT, z - .06), (x + .03, Y_INT + .012, z + .02), "metal_galv")
        s.beam((x, Y_INT + .01, z - .03), (x, Y_INT + .07, z - .02), .015, .015, "metal_galv")


R("trim_wall_top", "Wall Trim (Top)", "interior", "edge", wall_trim_top, (1, 0),
  tags=["Interior face (local +Y)."])
R("trim_baseboard", "Baseboard", "interior", "edge", baseboard, (1, 0))
R("trim_chair_rail", "Chair Rail", "interior", "edge", chair_rail, (1, 0))
R("trim_corner_guard", "Corner Guard", "interior", "vertex", corner_guard, (0, 0), game=dict(clinic=True))
R("trim_window", "Window Trim", "interior", "edge", window_trim, (1, 0), tags=["Matches the Large Window opening."])
R("trim_door", "Door Trim", "interior", "edge", door_trim, (1, 0), tags=["Matches 0.9 x 2.1 m doors."])
R("trim_handrail", "Handrail", "interior", "edge", handrail, (1, 0), game=dict(accessible=True))


# ===================================================================================== 8. UTILITY & SERVICE
def electrical_box(s, r):
    s.box((-.2, -.12, 1.2), (.2, 0, 1.8), "metal_galv")
    s.box((-.18, -.125, 1.22), (.18, -.12, 1.78), "metal_galv")
    s.cyl((0, -.05, .2), .025, 1.0, "metal_galv", seg=8)


def meter(s, r):
    s.box((-.14, -.1, 1.35), (.14, 0, 1.8), "metal_galv")
    s.cyl((0, -.18, 1.6), .1, .08, "glass", seg=14, axis="Y")
    s.cyl((0, -.19, 1.6), .085, .02, "sign_white", seg=14, axis="Y")


def ac_unit(s, r):
    s.box((-.5, -.5, 0), (.5, .5, .1), "concrete")
    s.box((-.4, -.4, .1), (.4, .4, .8), "ac_beige")
    s.cyl((0, 0, .8), .3, .02, "metal_dark", seg=16)
    for k in range(9):
        x = -.36 + k * .09
        s.box((x, -.405, .15), (x + .04, -.4, .75), "metal_dark")
    s.cyl((.3, .4, .5), .02, .3, "metal_galv", seg=6, axis="Y")


def propane_tank(s, r):
    for x in (-.5, .5):
        s.box((x - .06, -.3, 0), (x + .06, .3, .4), "concrete")
    s.cyl((-.8, 0, .8), .45, 1.6, "propane", seg=16, axis="X")
    for x, sx in ((-.8, -1), (.8, 1)):
        s.sphere((x, 0, .8), .45, "propane", seg=16, rings=8, sc=(.45, 1, 1))
    s.cyl((0, 0, 1.2), .14, .15, "propane", seg=12)


def spigot(s, r):
    s.cyl((0, -.12, .5), .015, .12, "brass", seg=8, axis="Y")
    s.cyl((0, -.12, .44), .015, .07, "brass", seg=8)
    s.cyl((0, -.12, .52), .04, .01, "flower_red", seg=10)


R("electrical_box", "Electrical Box", "utility", "free", electrical_box, game=dict(power=True, fireRisk=True))
R("utility_meter", "Meter", "utility", "free", meter, game=dict(power=True))
R("ac_unit", "AC Unit (Small)", "utility", "free", ac_unit, game=dict(power=True))
R("propane_tank", "Propane Tank", "utility", "free", propane_tank, game=dict(gas=True, explosive=True))
R("water_spigot", "Water Spigot", "utility", "free", spigot, game=dict(water=True))


# ===================================================================================== 9. SITE & MISCELLANEOUS
def fence_rail(s, r):
    for x in (-1.94, 0, 1.94):
        s.box((x - .06, -.06, 0), (x + .06, .06, 1.3), "wood_raw")
    for z in (.35, .7, 1.05):
        s.box((-2, -.09, z), (2, -.06, z + .15), "wood_raw")


def fence_picket(s, r):
    for x in (-1.95, 1.95):
        s.box((x - .05, -.05, 0), (x + .05, .05, 1.1), "trim_white")
        s.cyl((x, 0, 1.1), .06, .08, "trim_white", seg=4, r2=0, smooth=False)
    for z in (.25, .75):
        s.box((-2, .05, z), (2, .09, z + .08), "trim_white")
    x = -1.85
    while x < 1.86:
        s.prism([(x - .04, .05), (x + .04, .05), (x + .04, .92), (x, 1.0), (x - .04, .92)], "Y", .09, .11,
                "trim_white")
        x += .15


def fence_wire(s, r):
    for x in (-1.94, 1.94):
        s.box((x - .06, -.06, 0), (x + .06, .06, 1.25), "wood_raw")
    s.box((-.02, -.03, 0), (.02, .03, 1.2), "metal_dark")
    for z in (.3, .55, .8, 1.05):
        s.box((-2, -.004, z), (2, .004, z + .008), "metal_galv")


def gate_wood(s, r):
    for x in (-1.92, 1.92):
        s.box((x - .08, -.08, 0), (x + .08, .08, 1.4), "wood_raw")
    x0, x1, z0, z1 = -1.82, 1.82, .12, 1.25
    s.box((x0, -.03, z0), (x0 + .1, .03, z1), "wood_raw")
    s.box((x1 - .1, -.03, z0), (x1, .03, z1), "wood_raw")
    for z in (z0, (z0 + z1) / 2 - .05, z1 - .1):
        s.box((x0, -.03, z), (x1, .03, z + .1), "wood_raw")
    s.beam((x0 + .1, -.04, z0 + .1), (x1 - .1, -.04, z1 - .1), .1, .02, "wood_raw", up=(0, -1, 0))


def gate_wire(s, r):
    for x in (-1.92, 1.92):
        s.box((x - .07, -.07, 0), (x + .07, .07, 1.3), "wood_raw")
    x0, x1 = -1.8, 1.8
    for z in (.2, .45, .7, .95, 1.2):
        s.cyl((x0, 0, z), .022, x1 - x0, "metal_galv", seg=8, axis="X")
    for x in (x0, x1 - .04):
        s.box((x, -.025, .18), (x + .04, .025, 1.24), "metal_galv")
    s.beam((x0, 0, .2), (x0 + 1.2, 0, 1.2), .03, .03, "metal_galv")


def mailbox(s, r):
    s.box((-.05, -.05, 0), (.05, .05, 1.0), "wood_raw")
    s.box((-.04, -.15, .95), (.04, .1, 1.0), "wood_raw")
    s.box((-.11, -.3, 1.0), (.11, .2, 1.13), "metal_dark")
    s.cyl((0, -.3, 1.13), .11, .5, "metal_dark", seg=12, axis="Y")
    s.box((.11, -.05, 1.1), (.13, -.03, 1.35), "sign_red")
    s.box((.11, -.05, 1.27), (.13, .1, 1.35), "sign_red")


def planter(s, r):
    s.box((-.6, -.25, 0), (.6, .25, .45), "wood_raw")
    s.box((-.55, -.2, .3), (.55, .2, .42), "soil")
    s.sphere((0, 0, .5), .5, "leaf_green", seg=10, rings=5, sc=(1, .4, .35))
    for k in range(9):
        s.sphere((r.uniform(-.48, .48), r.uniform(-.15, .15), .55 + r.uniform(0, .1)), .06,
                 r.choice(["flower_red", "flower_yellow", "flower_purple"]), seg=6, rings=4)


def wood_pile(s, r):
    for row, count in enumerate((5, 4, 3, 2)):
        for k in range(count):
            y = (k - (count - 1) / 2) * .21
            z = .1 + row * .18
            s.cyl((-.55 + r.uniform(-.04, .04), y, z), .1 + r.uniform(-.015, .01), 1.1, "bark", seg=8, axis="X")


def trash_can(s, r):
    s.cyl((0, 0, 0), .26, .8, "metal_galv", seg=16, r2=.29)
    for z in (.2, .55):
        s.cyl((0, 0, z), .28 + z * .03, .03, "metal_galv", seg=16)
    s.cyl((0, 0, .8), .31, .05, "metal_galv", seg=16)
    s.box((-.08, -.015, .85), (.08, .015, .9), "metal_galv")


def bench(s, r):
    for k in range(3):
        y = -.2 + k * .14
        s.box((-.8, y, .43), (.8, y + .11, .47), "wood_deck")
    for k in range(2):
        z = .62 + k * .16
        s.box((-.8, .2, z), (.8, .24, z + .1), "wood_deck")
    for x in (-.7, .7):
        s.box((x - .03, -.22, 0), (x + .03, -.17, .43), "metal_dark")
        s.box((x - .03, .17, 0), (x + .03, .22, .9), "metal_dark")
        s.box((x - .03, -.22, .38), (x + .03, .22, .43), "metal_dark")


def well(s, r):
    s.tube((0, 0, 0), .9, .7, .8, "stone", seg=16)
    s.cyl((0, 0, 0), .7, .45, "water", seg=16)
    for x in (-.8, .8):
        s.box((x - .07, -.07, .7), (x + .07, .07, 2.2), "wood_raw")
    s.slab_top([(-1.1, -.8, 2.1), (1.1, -.8, 2.1), (1.1, 0, 2.55), (-1.1, 0, 2.55)], .06, "shingle")
    s.slab_top([(-1.1, .8, 2.1), (1.1, .8, 2.1), (1.1, 0, 2.55), (-1.1, 0, 2.55)], .06, "shingle")
    s.cyl((-.8, 0, 1.6), .05, 1.6, "wood_raw", seg=8, axis="X")
    s.cyl((0, 0, .9), .14, .25, "wood_raw", seg=10)


def hitching_post(s, r):
    for x in (-1.0, 1.0):
        s.box((x - .06, -.06, 0), (x + .06, .06, 1.15), "wood_raw")
    s.cyl((-1.1, 0, 1.05), .05, 2.2, "wood_raw", seg=8, axis="X")


def hay_bale(s, r):
    s.cyl((0, -.6, .75), .75, 1.2, "hay", seg=16, axis="Y")


def clinic_sign(s, r):
    for x in (-.8, .8):
        s.box((x - .05, -.05, 0), (x + .05, .05, 2.0), "trim_white")
    s.box((-.95, -.04, 1.0), (.95, .04, 1.95), "sign_white")
    s.box((-.98, -.05, .97), (.98, .05, 1.0), "trim_white")
    s.box((-.98, -.05, 1.95), (.98, .05, 1.98), "trim_white")
    s.box((-.07, -.05, 1.22), (.07, -.04, 1.73), "sign_red")
    s.box((-.25, -.05, 1.405), (.25, -.04, 1.545), "sign_red")


def facade_sign(s, r):
    s.box((-1.6, -.05, 0), (1.6, 0, .6), "trim_white")
    s.box((-1.52, -.06, .08), (1.52, -.05, .52), "batten_green")


R("fence_wood", "Fence (Wood)", "site", "edge", fence_rail, (1, 0), game=dict(blocksMovement=True))
R("fence_picket", "Fence (Picket)", "site", "edge", fence_picket, (1, 0), game=dict(blocksMovement=True))
R("fence_wire", "Fence (Wire)", "site", "edge", fence_wire, (1, 0), game=dict(blocksMovement=True))
R("gate_wood", "Gate (Wood)", "site", "edge", gate_wood, (1, 0), game=dict(openable=True))
R("gate_wire", "Gate (Wire)", "site", "edge", gate_wire, (1, 0), game=dict(openable=True))
R("mailbox", "Mailbox", "site", "free", mailbox, game=dict(interactive=True))
R("planter_box", "Planter Box", "site", "free", planter)
R("wood_pile", "Wood Pile", "site", "free", wood_pile, game=dict(flammable=True))
R("trash_can", "Trash Can", "site", "free", trash_can, game=dict(interactive=True))
R("bench", "Bench", "site", "free", bench, game=dict(seat=True))
R("well", "Well", "site", "free", well, game=dict(water=True))
R("hitching_post", "Hitching Post", "site", "free", hitching_post)
R("hay_bale", "Hay Bale", "site", "free", hay_bale, game=dict(flammable=True))
R("sign_clinic", "Clinic Sign", "site", "free", clinic_sign)
R("sign_facade", "Facade Sign Board", "site", "free", facade_sign, tags=["Blank board for store names."])


# ===================================================================================== STREET & INFRASTRUCTURE
def utility_pole(s, r):
    s.cyl((0, 0, -1.0), .15, 11.0, "wood_dark", seg=10, r2=.12)
    s.box((-1.2, -.06, 9.2), (1.2, .06, 9.32), "wood_dark")
    for x in (-1.0, 0.0, 1.0):
        if x == 0.0:
            s.cyl((0, 0, 10.0), .05, .2, "insulator", seg=8)
        else:
            s.cyl((x, 0, 9.32), .05, .2, "insulator", seg=8)
    s.cyl((0, .35, 7.4), .28, .9, "metal_galv", seg=12)
    s.box((-.05, .1, 7.7), (.05, .2, 7.9), "metal_galv")


def street_light(s, r):
    s.cyl((0, 0, -1.0), .14, 9.0, "wood_dark", seg=10, r2=.11)
    s.beam((0, 0, 7.4), (0, -1.8, 7.8), .06, .06, "metal_galv")
    s.box((-.18, -2.3, 7.7), (.18, -1.7, 7.86), "metal_galv")
    s.box((-.14, -2.25, 7.68), (.14, -1.75, 7.7), "lamp_glow")


def sign_post(s, h=2.4):
    s.box((-.03, -.03, 0), (.03, .03, h), "metal_galv")


def street_sign(s, r):
    sign_post(s, 2.9)
    s.box((-.45, -.012, 2.55), (.45, .012, 2.72), "sign_green")
    s.box((-.012, -.45, 2.74), (.012, .45, 2.91), "sign_green")


def stop_sign(s, r):
    sign_post(s, 2.1)
    oct8 = lambda rad: [(rad * math.cos(math.pi / 8 + k * math.pi / 4), 2.1 + rad * math.sin(math.pi / 8 + k * math.pi / 4))
                        for k in range(8)]
    s.prism(oct8(.38), "Y", -.04, -.03, "sign_white")
    s.prism(oct8(.35), "Y", -.046, -.04, "sign_red")


def speed_sign(s, r):
    sign_post(s, 2.0)
    s.box((-.3, -.04, 1.6), (.3, -.03, 2.35), "sign_white")
    for a, b in (((-.27, 1.63), (-.25, 2.32)), ((.25, 1.63), (.27, 2.32)), ((-.27, 1.63), (.27, 1.65)),
                 ((-.27, 2.3), (.27, 2.32))):
        s.box((a[0], -.046, a[1]), (b[0], -.04, b[1]), "sign_black")


def crossing_sign(s, r):
    sign_post(s, 2.0)
    dia = lambda rad: [(0, 2.1 - rad), (rad, 2.1), (0, 2.1 + rad), (-rad, 2.1)]
    s.prism(dia(.45), "Y", -.04, -.03, "sign_black")
    s.prism(dia(.42), "Y", -.046, -.04, "sign_yellow")


def hydrant(s, r):
    s.cyl((0, 0, 0), .17, .07, "sign_red", seg=14)
    s.cyl((0, 0, .07), .12, .55, "sign_red", seg=14)
    s.cyl((0, 0, .62), .14, .05, "sign_red", seg=14)
    s.sphere((0, 0, .67), .12, "sign_red", seg=12, rings=6, sc=(1, 1, .6))
    s.cyl((0, 0, .72), .03, .06, "sign_red", seg=5, smooth=False)
    s.cyl((-.22, 0, .45), .045, .44, "sign_red", seg=10, axis="X")
    s.cyl((0, -.2, .45), .07, .08, "sign_red", seg=10, axis="Y")


def water_tower(s, r):
    legs = [(sx, sy) for sx in (-1, 1) for sy in (-1, 1)]
    for sx, sy in legs:
        s.beam((3.0 * sx, 3.0 * sy, 0), (2.3 * sx, 2.3 * sy, 14.2), .3, .3, "metal_galv")
        s.boxc((3.0 * sx, 3.0 * sy, -.2), (.8, .8, .4), "concrete")
    order = [(-1, -1), (1, -1), (1, 1), (-1, 1)]
    for z0, z1 in ((0.5, 5.0), (5.0, 9.5), (9.5, 14.0)):
        for k in range(4):
            a, b = order[k], order[(k + 1) % 4]
            ra = lambda z: 3.0 - .7 * z / 14.2
            pa0 = (a[0] * ra(z0), a[1] * ra(z0), z0)
            pb0 = (b[0] * ra(z0), b[1] * ra(z0), z0)
            pa1 = (a[0] * ra(z1), a[1] * ra(z1), z1)
            pb1 = (b[0] * ra(z1), b[1] * ra(z1), z1)
            s.beam(pa1, pb1, .12, .12, "metal_galv")
            s.beam(pa0, pb1, .06, .06, "metal_galv")
            s.beam(pb0, pa1, .06, .06, "metal_galv")
    s.cyl((0, 0, 0), .5, 12.8, "metal_galv", seg=12)
    s.cyl((0, 0, 12.8), 1.0, 1.2, "tank_white", seg=24, r2=3.4)
    s.cyl((0, 0, 14.0), 3.4, 4.5, "tank_white", seg=32)
    s.cyl((0, 0, 18.5), 3.5, 1.6, "tank_white", seg=32, r2=0)
    s.cyl((0, 0, 20.0), .06, .7, "metal_galv", seg=8)
    s.tube((0, 0, 14.0), 4.0, 3.4, .08, "metal_galv", seg=32)
    s.tube((0, 0, 15.05), 4.0, 3.94, .05, "metal_galv", seg=32)
    for k in range(20):
        a = 2 * math.pi * k / 20
        s.cyl((3.97 * math.cos(a), 3.97 * math.sin(a), 14.08), .025, 1.0, "metal_galv", seg=6)
    for sy in (-.25, .25):
        s.beam((3.0, sy, 0), (3.97, sy, 14.1), .05, .05, "metal_galv")
    z = .5
    while z < 14:
        f = z / 14.1
        x = 3.0 + .97 * f
        s.beam((x, -.25, z), (x, .25, z), .03, .03, "metal_galv")
        z += .35


R("utility_pole", "Utility Pole", "street", "free", utility_pole,
  game=dict(power=True, collisionHazard=True), tags=["Base sits 1 m below grade."])
R("street_light", "Street Light", "street", "free", street_light, game=dict(power=True, lightSource=True),
  tags=["Lamp arm reaches toward local -Y (the road)."])
R("sign_street", "Street Sign", "street", "free", street_sign)
R("sign_stop", "Stop Sign", "street", "free", stop_sign, game=dict(traffic="stop"))
R("sign_speed", "Speed Sign", "street", "free", speed_sign, game=dict(traffic="speedLimit", speedMph=25))
R("sign_crossing", "Crosswalk Sign", "street", "free", crossing_sign, game=dict(traffic="crossing"))
R("fire_hydrant", "Fire Hydrant", "street", "free", hydrant, game=dict(water=True, fireService=True))
R("water_tower", "Water Tower", "street", "tile", water_tower, (2, 2), game=dict(water=True, landmark=True))


# ===================================================================================== NATURE
def tree_deciduous(leaves):
    def f(s, r):
        s.cyl((0, 0, 0), .24, 3.4, "bark", seg=9, r2=.14)
        for k in range(3):
            a = 2 * math.pi * k / 3 + r.uniform(-.4, .4)
            s.beam((0, 0, 2.4 + k * .3), (math.cos(a) * 1.3, math.sin(a) * 1.3, 3.9 + k * .3), .14, .14, "bark")
        s.sphere((0, 0, 5.2), 2.0, leaves[0], seg=10, rings=7, sc=(1, 1, .85))
        for k in range(6):
            a = 2 * math.pi * k / 6 + r.uniform(-.3, .3)
            rad = r.uniform(1.15, 1.6)
            s.sphere((math.cos(a) * 1.5, math.sin(a) * 1.5, 4.2 + r.uniform(0, 1.3)), rad, leaves[k % len(leaves)],
                     seg=9, rings=6, sc=(1, 1, .85))
    return f


def tree_bare(s, r):
    s.cyl((0, 0, 0), .22, 3.0, "bark", seg=9, r2=.14)

    def branch(p, d, length, w, depth):
        q = p + d * length
        s.beam(p, q, w, w, "bark")
        if depth == 0:
            return
        for _ in range(3):
            nd = (d + Vector((r.uniform(-.8, .8), r.uniform(-.8, .8), r.uniform(.1, .6)))).normalized()
            branch(q, nd, length * .65, w * .6, depth - 1)

    branch(Vector((0, 0, 2.8)), Vector((0, 0, 1)), 1.8, .2, 3)


def tree_pine(snowy):
    def f(s, r):
        s.cyl((0, 0, 0), .2, 2.2, "bark", seg=8, r2=.14)
        for k in range(5):
            zb, rad, h = 1.4 + k * 1.45, 2.3 - k * .4, 2.6
            s.cyl((0, 0, zb), rad, h, "pine", seg=10, r2=0)
            if snowy:
                s.cyl((0, 0, zb + 1.15), rad * .58, h - 1.13, "snow", seg=10, r2=0)
                s.cyl((0, 0, zb + .05), rad * 1.01, .06, "snow", seg=10, r2=rad * .96)
    return f


def bush(s, r):
    for k in range(4):
        s.sphere((r.uniform(-.4, .4), r.uniform(-.4, .4), .45 + r.uniform(0, .2)), r.uniform(.4, .6),
                 "leaf_dark" if k % 2 else "leaf_green", seg=9, rings=6, sc=(1, 1, .8))


def hedge(s, r):
    s.box((-2, -.4, 0), (2, .4, .95), "leaf_dark")
    for k in range(9):
        s.sphere((-1.8 + k * .45, r.uniform(-.1, .1), .95), r.uniform(.32, .42), "leaf_dark", seg=8, rings=5,
                 sc=(1, 1, .6))


def flowers(s, r):
    s.sphere((0, 0, .15), .45, "leaf_green", seg=10, rings=5, sc=(1, 1, .45))
    colour = r.choice(["flower_red", "flower_yellow", "flower_purple", "flower_white"])
    for k in range(14):
        a, d = r.uniform(0, 2 * math.pi), r.uniform(0, .38)
        s.sphere((math.cos(a) * d, math.sin(a) * d, .3 + r.uniform(0, .08)), .05, colour, seg=6, rings=4)


R("tree_green", "Green Tree", "nature", "free", tree_deciduous(["leaf_green", "leaf_dark"]), sheet="nature",
  game=dict(collision=True), tags=["season:summer"])
R("tree_autumn", "Autumn Tree", "nature", "free", tree_deciduous(["leaf_autumn", "leaf_gold"]),
  game=dict(collision=True), tags=["season:autumn"])
R("tree_bare", "Bare Tree", "nature", "free", tree_bare, game=dict(collision=True), tags=["season:winter"])
R("tree_pine", "Pine Tree", "nature", "free", tree_pine(False), game=dict(collision=True))
R("tree_pine_snowy", "Snowy Pine Tree", "nature", "free", tree_pine(True), game=dict(collision=True),
  tags=["season:winter"])
R("bush", "Bush", "nature", "free", bush)
R("hedge", "Hedge", "nature", "edge", hedge, (1, 0), game=dict(blocksMovement=True))
R("flower_cluster", "Flower Cluster", "nature", "free", flowers)


# ===================================================================================== TERRAIN
def ground(material):
    def f(s, r):
        s.box((-2, -2, -0.3), (2, 2, 0), material)
    return f


def field(crops):
    def f(s, r):
        s.box((-2, -2, -0.3), (2, 2, -.05), "dirt")
        for k in range(8):
            y = -1.75 + k * .5
            s.prism([(y - .22, -.05), (y + .22, -.05), (y + .08, .08), (y - .08, .08)], "X", -2, 2, "dirt")
            if crops:
                x = -1.85
                while x < 1.9:
                    s.sphere((x + r.uniform(-.04, .04), y, .2), r.uniform(.12, .17), "leaf_green", seg=6, rings=4,
                             sc=(1, 1, 1.3))
                    x += .3
    return f


R("terrain_grass", "Grass", "terrain", "tile", ground("grass"), (1, 1), game=dict(walkable=True))
R("terrain_grass_dry", "Dry Grass", "terrain", "tile", ground("grass_dry"), (1, 1), game=dict(walkable=True),
  tags=["season:late-summer"])
R("terrain_dirt", "Dirt", "terrain", "tile", ground("dirt"), (1, 1), game=dict(walkable=True))
R("terrain_gravel", "Gravel", "terrain", "tile", ground("gravel"), (1, 1), game=dict(walkable=True, drivable=True))
R("terrain_snow", "Snow", "terrain", "tile", ground("snow"), (1, 1), game=dict(walkable=True, slippery=True),
  tags=["season:winter"])
R("terrain_field_crops", "Field (Crops)", "terrain", "tile", field(True), (1, 1), game=dict(walkable=True))
R("terrain_field_plowed", "Field (Plowed)", "terrain", "tile", field(False), (1, 1), game=dict(walkable=True))


# ===================================================================================== ROADS (4 m grid)
UTIL = ["power", "fiber", "gas", "water", "storm_drain", "sanitary_sewer"]


def asphalt(s):
    s.box((-2, -2, -0.3), (2, 2, 0), "asphalt")


def road_lane(s, r):
    asphalt(s)
    s.box((-2, 1.83, -.01), (2, 1.93, .004), "line_yellow")
    s.box((-2, -1.9, -.01), (2, -1.8, .004), "line_white")


def road_stopbar(s, r):
    road_lane(s, r)
    s.box((1.5, -1.8, -.01), (1.9, 1.8, .004), "line_white")


def road_crosswalk(s, r):
    asphalt(s)
    for y0 in (-1.7, -.5, .7):
        s.box((-1.5, y0, -.01), (1.5, y0 + .6, .004), "line_white")


def road_dirt(s, r):
    s.box((-2, -2, -0.3), (2, 2, -.06), "dirt")
    s.box((-2, -1.0, -.06), (2, 1.0, 0), "dirt")
    for y in (-1.25, 1.25):
        s.box((-2, y - .25, -.06), (2, y + .25, -.03), "dirt")
    s.box((-2, -.3, 0), (2, .3, .03), "grass_dry")
    s.box((-2, -2, -.06), (2, -1.5, .02), "grass")
    s.box((-2, 1.5, -.06), (2, 2, .02), "grass")


def road_shoulder(s, r):
    s.box((-2, -2, -0.3), (2, -.8, 0), "gravel")
    s.slab_top([(-2, -.8, 0), (2, -.8, 0), (2, 2, .1), (-2, 2, .1)], .4, "grass")


def sidewalk_base(s, curb_ranges=((-2, 2),), walk=True):
    for a, b in curb_ranges:
        s.box((a, -2, -.3), (b, -1.85, .15), "concrete")
        s.box((a, -1.85, -.3), (b, 0, .1), "grass")
    if walk:
        s.box((-2, 0, -.3), (2, 2, .12), "gravel")
        for k in range(3):
            x = -2 + k * 4 / 3
            s.box((x + .006, 0, .12), (x + 4 / 3 - .006, 2, .15), "sidewalk")


def sidewalk(s, r):
    sidewalk_base(s)


def sidewalk_corner(s, r):
    s.box((-2, -2, -.3), (2, -1.85, .15), "concrete")
    s.box((-2, -1.85, -.3), (-1.85, 2, .15), "concrete")
    s.box((-1.85, -1.85, -.3), (2, 2, .12), "gravel")
    for i in range(3):
        for j in range(3):
            x0, y0 = -1.85 + i * 3.85 / 3, -1.85 + j * 3.85 / 3
            s.box((x0 + .006, y0 + .006, .12), (x0 + 3.85 / 3 - .006, y0 + 3.85 / 3 - .006, .15), "sidewalk")


def sidewalk_driveway(s, r):
    sidewalk_base(s, curb_ranges=((-2, -1.6), (1.6, 2)))
    s.box((-1.6, -2, -.3), (1.6, -1.85, .03), "concrete")
    s.slab_top([(-1.6, -1.85, .03), (1.6, -1.85, .03), (1.6, 0, .15), (-1.6, 0, .15)], .4, "concrete")


rg = dict(utilities=UTIL, drivable=True)
R("road_lane", "Road Lane (Straight)", "roads", "tile", road_lane, (1, 1), game=rg,
  tags=["Double yellow at local +Y edge: pair with a 180-degree copy."])
R("road_plain", "Road (Plain / Intersection)", "roads", "tile", lambda s, r: asphalt(s), (1, 1), game=rg)
R("road_lane_stopbar", "Road Lane (Stop Bar)", "roads", "tile", road_stopbar, (1, 1), game=dict(rg, traffic="stop"),
  tags=["Stop bar at local +X end."])
R("road_crosswalk", "Crosswalk", "roads", "tile", road_crosswalk, (1, 1), game=dict(rg, pedestrian=True))
R("road_dirt", "Dirt Farm Road", "roads", "tile", road_dirt, (1, 1), game=dict(drivable=True))
R("road_shoulder_gravel", "Gravel Shoulder", "roads", "tile", road_shoulder, (1, 1), game=dict(drivable=True),
  tags=["Road edge at local -Y."])
R("sidewalk", "Sidewalk (Curb, Grass Strip, Walk)", "roads", "tile", sidewalk, (1, 1),
  game=dict(walkable=True, utilities=["power", "fiber"]), tags=["Curb at local -Y; 2 m utility strip + 2 m walk."])
R("sidewalk_corner", "Sidewalk Corner", "roads", "tile", sidewalk_corner, (1, 1), game=dict(walkable=True),
  tags=["Curbs at local -X and -Y."])
R("sidewalk_driveway", "Sidewalk with Driveway Apron", "roads", "tile", sidewalk_driveway, (1, 1),
  game=dict(walkable=True, drivable=True))


# ===================================================================================== BUILD + EXPORT
def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def mesh_from(mid, s):
    me = bpy.data.meshes.new(mid)
    me.from_pydata([tuple(v) for v in s.v], [], s.f)
    order, idx = [], {}
    for m in s.m:
        if m not in idx:
            idx[m] = len(order)
            order.append(m)
    for m in order:
        me.materials.append(mat(m))
    me.polygons.foreach_set("material_index", [idx[m] for m in s.m])
    me.polygons.foreach_set("use_smooth", s.sm)
    me.update()
    uv = me.uv_layers.new(name="UVMap")
    verts = me.vertices
    for p in me.polygons:
        n = p.normal
        ax = max(range(3), key=lambda i: abs(n[i]))
        for li in p.loop_indices:
            co = verts[me.loops[li].vertex_index].co
            if ax == 0:
                u, v = co.y * (1 if n.x > 0 else -1), co.z
            elif ax == 1:
                u, v = co.x * (-1 if n.y > 0 else 1), co.z
            else:
                u, v = co.x, co.y * (1 if n.z > 0 else -1)
            uv.data[li].uv = (u * .5, v * .5)
    return me, order


def export_glb(ob, path):
    bpy.ops.object.select_all(action="DESELECT")
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_yup=True,
                              export_apply=True)


def build_all():
    scene = bpy.context.scene
    root = bpy.data.collections.new("Modules")
    scene.collection.children.link(root)
    cats = {}
    meshes, manifest, failures = {}, [], []
    for e in REG:
        try:
            s = MB()
            rng = random.Random(zlib.crc32(e["id"].encode()))
            e["fn"](s, rng)
            if not s.f:
                raise RuntimeError("no geometry")
            me, mats = mesh_from(e["id"], s)
            cat = e["category"]
            if cat not in cats:
                cats[cat] = bpy.data.collections.new(cat)
                root.children.link(cats[cat])
            ob = bpy.data.objects.new(e["id"], me)
            cats[cat].objects.link(ob)
            meshes[e["id"]] = me
            corner_upper = sum(len(f) for f in s.f)
            tris = sum(len(f) - 2 for f in s.f)
            if corner_upper > MAX_VERTS:
                print(f"!! {e['id']}: up to {corner_upper} export vertices (limit {MAX_VERTS})")
            xs = [v.x for v in s.v]
            ys = [v.y for v in s.v]
            zs = [v.z for v in s.v]
            rel = f"glb/{cat}/{e['id']}.glb"
            export_glb(ob, os.path.join(OUT, rel))
            manifest.append(dict(
                id=e["id"], name=e["name"], category=cat, attach=e["attach"], footprintTiles=e["footprint"],
                file=rel, boundsMin=[round(min(xs), 3), round(min(ys), 3), round(min(zs), 3)],
                boundsMax=[round(max(xs), 3), round(max(ys), 3), round(max(zs), 3)],
                triangles=tris, materials=["LC_" + m for m in mats], gameplay=e["gameplay"], notes=e["tags"]))
            print(f"   {e['id']:<38} {tris:>6} tris")
        except Exception as ex:  # keep going, report at the end
            import traceback
            traceback.print_exc()
            failures.append((e["id"], str(ex)))
    return meshes, manifest, failures


def write_manifest(manifest):
    data = dict(
        pack="lifecare-rural-town", version=1,
        generator="tools/blender/rural_modules/build_rural_pack.py",
        units="meters",
        axes="glTF Y-up. Blender (x, y, z) = glTF (x, z, -y). Module front/exterior faces glTF +Z.",
        grid=dict(tileMeters=TILE, subCellMeters=0.5, storyMeters=STORY, floorThicknessMeters=FLOOR_T,
                  wallThicknessMeters=WALL_T, wallTopMeters=WALL_Z1, roofPitch="6:12 (0.5)", eaveOverhangMeters=EAVE),
        attach={
            "tile": "Origin at tile centre; y=0 is the level base (floors) or ground (terrain, roads).",
            "edge": "Origin at the tile-edge midpoint at the level base; runs along X; exterior faces +Z.",
            "vertex": "Origin on the grid vertex; an outer corner fills the -X/+Z quadrant.",
            "roof": "Origin at the footprint centre at the wall top (the next level's elevation).",
            "free": "Origin at base centre; wall-mounted items have the wall face at z=0 and project toward +Z.",
        },
        materials={("LC_" + k): dict(baseColor=v[0], roughness=v[1], metallic=v[2], alpha=v[3], emission=v[4])
                   for k, v in PALETTE.items()},
        modules=manifest)
    with open(os.path.join(OUT, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1)


# ===================================================================================== RENDERING
def set_engine(scene, samples=48):
    for eng in ("BLENDER_EEVEE", "BLENDER_EEVEE_NEXT"):
        try:
            scene.render.engine = eng
            break
        except Exception:
            continue
    try:
        scene.eevee.taa_render_samples = samples
    except Exception:
        pass
    for attr in ("use_shadows", "use_gtao", "use_raytracing"):
        try:
            setattr(scene.eevee, attr, True)
        except Exception:
            pass


def render_to(scene, path):
    scene.render.filepath = path
    scene.render.image_settings.file_format = "PNG"
    try:
        bpy.ops.render.render(write_still=True)
    except Exception as ex:
        print("!! EEVEE render failed, falling back to Cycles:", ex)
        scene.render.engine = "CYCLES"
        scene.cycles.samples = 24
        scene.cycles.device = "CPU"
        bpy.ops.render.render(write_still=True)


def world_setup(scene, cam_color, light_color, strength=1.0):
    w = bpy.data.worlds.new("World")
    scene.world = w
    if w.node_tree is None:
        w.use_nodes = True
    nt = w.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputWorld")
    lp = nt.nodes.new("ShaderNodeLightPath")
    mix = nt.nodes.new("ShaderNodeMixShader")
    bl = nt.nodes.new("ShaderNodeBackground")
    bc = nt.nodes.new("ShaderNodeBackground")
    bl.inputs[0].default_value = (*light_color, 1)
    bl.inputs[1].default_value = strength
    bc.inputs[0].default_value = (*cam_color, 1)
    bc.inputs[1].default_value = 1.0
    nt.links.new(lp.outputs["Is Camera Ray"], mix.inputs[0])
    nt.links.new(bl.outputs[0], mix.inputs[1])
    nt.links.new(bc.outputs[0], mix.inputs[2])
    nt.links.new(mix.outputs[0], out.inputs[0])


def emission_mat(name, rgb, strength=1.0):
    m = bpy.data.materials.new(name)
    if m.node_tree is None:
        m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    e = nt.nodes.new("ShaderNodeEmission")
    o = nt.nodes.new("ShaderNodeOutputMaterial")
    e.inputs[0].default_value = (*rgb, 1)
    e.inputs[1].default_value = strength
    nt.links.new(e.outputs[0], o.inputs[0])
    return m


def add_sun(coll, rot, energy=3.0, color=(1, 1, 1), angle=3.0):
    ld = bpy.data.lights.new("Sun", "SUN")
    ld.energy = energy
    ld.color = color
    ld.angle = math.radians(angle)
    ob = bpy.data.objects.new("Sun", ld)
    ob.rotation_euler = [math.radians(a) for a in rot]
    coll.objects.link(ob)
    return ob


def text_obj(coll, body, loc, size, material, rot, align="CENTER"):
    cu = bpy.data.curves.new("txt", "FONT")
    cu.body = body
    cu.size = size
    cu.align_x = align
    cu.align_y = "CENTER"
    ob = bpy.data.objects.new("txt", cu)
    ob.data.materials.append(material)
    ob.location = loc
    ob.rotation_euler = rot
    coll.objects.link(ob)
    return ob


SHEET_TITLES = {
    "floors": "Foundations & Floors", "roofs": "Roofs", "roof_details": "Roof Details",
    "exterior": "Exterior Details", "interior": "Interior / Wall Details", "utility": "Utility & Service",
    "site": "Site & Miscellaneous", "street": "Street & Infrastructure", "nature": "Nature",
    "terrain": "Terrain", "roads": "Roads & Sidewalks (4 m grid)", "doors": "Door Leaves",
}
for _k, _st in STYLES.items():
    SHEET_TITLES["walls_" + _k] = "Walls, Doors & Windows - " + _st["name"]


def render_sheets(meshes):
    scene = bpy.context.scene
    set_engine(scene, 32)
    world_setup(scene, (0.012, 0.016, 0.022), (0.5, 0.52, 0.55), 0.9)
    cam_rot = (math.radians(60), 0, 0)
    right = Vector((1, 0, 0))
    up = Vector((0, .5, math.sqrt(3) / 2))
    view = Vector((0, math.sqrt(3) / 2, -.5))
    m_label = emission_mat("LBL_white", (0.9, 0.92, 0.95), 1.0)
    m_sub = emission_mat("LBL_grey", (0.35, 0.42, 0.5), 1.0)
    m_title = emission_mat("LBL_title", (0.55, 0.75, 0.95), 1.2)
    sheets = {}
    for e in REG:
        if e["id"] in meshes:
            sheets.setdefault(e["sheet"], []).append(e)
    os.makedirs(os.path.join(OUT, "previews"), exist_ok=True)
    for key, items in sheets.items():
        if ONLY_SHEETS and key not in ONLY_SHEETS:
            continue
        coll = bpy.data.collections.new("Sheet")
        scene.collection.children.link(coll)
        cols = 6 if len(items) > 8 else max(4, len(items))
        cw, ch = 10.0, 8.6
        rows = math.ceil(len(items) / cols)
        W = cols * cw
        top = 3.4
        for i, e in enumerate(items):
            r_, c_ = divmod(i, cols)
            cu = c_ * cw + cw / 2
            cv = -(r_ * ch + ch / 2)
            me = meshes[e["id"]]
            rz = math.radians(-32)
            cs, sn = math.cos(rz), math.sin(rz)
            us, vs = [], []
            for v in me.vertices:
                x, y, z = v.co
                xr, yr = x * cs - y * sn, x * sn + y * cs
                us.append(xr)
                vs.append(.5 * yr + math.sqrt(3) / 2 * z)
            du, dv = max(us) - min(us), max(vs) - min(vs)
            sc = min(cw * .8 / max(du, 1e-3), (ch - 1.9) * .9 / max(dv, 1e-3))
            umid, vmid = (max(us) + min(us)) / 2 * sc, (max(vs) + min(vs)) / 2 * sc
            target_v = cv + .55
            ob = bpy.data.objects.new("S_" + e["id"], me)
            ob.scale = (sc, sc, sc)
            ob.rotation_euler = (0, 0, rz)
            ob.location = (cu - umid, 2 * (target_v - vmid), 0)
            coll.objects.link(ob)
            vl = cv - ch / 2 + .95
            text_obj(coll, e["name"], (cu, 2 * vl, 0), .36, m_label, cam_rot)
            text_obj(coll, e["id"], (cu, 2 * (vl - .45), 0), .26, m_sub, cam_rot)
        text_obj(coll, "RURAL TOWN MODULES  /  " + SHEET_TITLES.get(key, key).upper(), (.6, 2 * 2.1, 0), 1.0,
                 m_title, cam_rot, align="LEFT")
        text_obj(coll, f"{len(items)} modules  |  1 tile = 4 m  |  story 3.2 m  |  front = -Y (glTF +Z)",
                 (.6, 2 * 1.1, 0), .42, m_sub, cam_rot, align="LEFT")
        add_sun(coll, (50, 0, -35), 3.2)
        add_sun(coll, (60, 0, 140), 0.8)
        H = rows * ch + top
        uc, vc = W / 2, (top - rows * ch) / 2
        cd = bpy.data.cameras.new("cam")
        cd.type = "ORTHO"
        cd.ortho_scale = W + 1.0
        cd.clip_end = 2000
        cam = bpy.data.objects.new("cam", cd)
        cam.location = right * uc + up * vc - view * 300
        cam.rotation_euler = cam_rot
        coll.objects.link(cam)
        scene.camera = cam
        scene.render.resolution_x = 2000
        scene.render.resolution_y = int(2000 * H / (W + 1.0))
        scene.render.resolution_percentage = 100
        render_to(scene, os.path.join(OUT, "previews", f"sheet_{key}.png"))
        for ob in list(coll.objects):
            bpy.data.objects.remove(ob, do_unlink=True)
        bpy.data.collections.remove(coll)


# ===================================================================================== SAMPLE TOWN
def build_town(meshes):
    scene = bpy.context.scene
    coll = bpy.data.collections.new("Town")
    scene.collection.children.link(coll)

    def put(mid, loc, rz=0.0, parent=None):
        ob = bpy.data.objects.new(mid, meshes[mid])
        coll.objects.link(ob)
        ob.location = loc
        ob.rotation_euler = (0, 0, math.radians(rz))
        if parent is not None:
            ob.parent = parent
        return ob

    def tc(i, j, z=0.0):
        return (4 * i + 2, 4 * j + 2, z)

    used = set()
    I0, I1, J0, J1 = -13, 12, -9, 10

    # main street along X (j = -1, 0), T-junction with a cross street going north (i = -1, 0)
    for i in range(I0, I1 + 1):
        inter = i in (-1, 0)
        put("road_lane", tc(i, -1), 0)
        put("road_plain" if inter else "road_lane", tc(i, 0), 0 if inter else 180)
        put("sidewalk", tc(i, -2), 180)
        used |= {(i, -1), (i, 0), (i, -2)}
        if not inter and i not in (-2, 1):
            put("sidewalk", tc(i, 1), 0)
            used.add((i, 1))
    put("sidewalk_corner", tc(-2, 1), 90)
    put("sidewalk_corner", tc(1, 1), 0)
    used |= {(-2, 1), (1, 1)}
    for j in range(1, J1 + 1):
        put("road_lane_stopbar" if j == 1 else "road_lane", tc(-1, j), -90)
        put("road_lane", tc(0, j), 90)
        used |= {(-1, j), (0, j)}
        if j >= 2:
            put("sidewalk", tc(-2, j), 90)
            put("sidewalk", tc(1, j), -90)
            used |= {(-2, j), (1, j)}

    def building(i0, j0, w, d, facing, style, finish, front, back, left, right, floor="floor_wood", base=0.0,
                 roof="gable", porches=(), extras=()):
        g = bpy.data.objects.new("bldg", None)
        coll.objects.link(g)
        if facing == "S":
            g.location, rz = (4 * i0, 4 * j0, base), 0
            tiles = [(i0 + a, j0 + b) for a in range(w) for b in range(d)]
        elif facing == "N":
            g.location, rz = (4 * (i0 + w), 4 * (j0 + d), base), 180
            tiles = [(i0 + a, j0 + b) for a in range(w) for b in range(d)]
        elif facing == "W":
            g.location, rz = (4 * i0, 4 * (j0 + w), base), -90
            tiles = [(i0 + b, j0 + a) for a in range(w) for b in range(d)]
        else:
            g.location, rz = (4 * (i0 + d), 4 * j0, base), 90
            tiles = [(i0 + b, j0 + a) for a in range(w) for b in range(d)]
        g.rotation_euler = (0, 0, math.radians(rz))
        for a in range(w):
            for b in range(d):
                put(floor, (4 * a + 2, 4 * b + 2, 0), 0, g)
        def wall(k, loc, r):
            put(f"wall_{style}_{k}", loc, r, g)
            # Door leaves are separate modules now: hinged leaves sit on the hinge, garage and barn leaves at the wall origin.
            colour = "green" if style == "lap_white" else "wood"
            leaves = {"door_wood": [f"door_leaf_wood_{colour}"], "door_halfglass": [f"door_leaf_halfglass_{colour}"],
                      "door_screen": [f"door_leaf_screen_{colour}"], "door_garage": ["door_leaf_garage"],
                      "door_barn": ["door_leaf_barn_left", "door_leaf_barn_right"]}.get(k, [])
            for leaf in leaves:
                hinge = leaf.startswith(("door_leaf_wood", "door_leaf_halfglass", "door_leaf_screen"))
                cs, sn = math.cos(math.radians(r)), math.sin(math.radians(r))
                hx, hy = (HINGE_X, HINGE_Y) if hinge else (0, 0)
                put(leaf, (loc[0] + hx * cs - hy * sn, loc[1] + hx * sn + hy * cs, loc[2]), r, g)

        for a, k in enumerate(front):
            wall(k, (4 * a + 2, 0, 0), 0)
        for a, k in enumerate(back):
            wall(k, (4 * a + 2, 4 * d, 0), 180)
        for b, k in enumerate(left):
            wall(k, (0, 4 * b + 2, 0), -90)
        for b, k in enumerate(right):
            wall(k, (4 * w, 4 * b + 2, 0), 90)
        for (x, y), r in (((0, 0), 0), ((4 * w, 0), 90), ((4 * w, 4 * d), 180), ((0, 4 * d), -90)):
            put(f"corner_{style}_outer", (x, y, 0), r, g)
        for a in range(w):
            end = roof == "gable" and "roof_gable_end" or "roof_hip_end"
            if a == 0:
                put(f"{end}_{finish}", (4 * a + 2, 4, STORY), 0, g)
            elif a == w - 1:
                put(f"{end}_{finish}", (4 * a + 2, 4, STORY), 180, g)
            else:
                put(f"roof_gable_mid_{finish}", (4 * a + 2, 4, STORY), 0, g)
        if roof == "gable":
            put(f"gable_infill_{style}", (0, 4, STORY), -90, g)
            put(f"gable_infill_{style}", (4 * w, 4, STORY), 90, g)
        for a, mid, span in porches:
            put(mid, (4 * a + 2 * span, -2, 0), 0, g)
        for mid, loc, r in extras:
            put(mid, loc, r, g)
        used.update(tiles)
        return g

    # Country home
    building(2, 3, 3, 2, "S", "lap_white", "shingle",
             ["win_shutter", "door_wood", "win_shutter"], ["win_small", "door_screen", "win_large"],
             ["win_large", "win_shutter"], ["win_shutter", "win_small"],
             porches=[(1, "porch_small_shingle", 1)],
             extras=[("chimney", (10.6, 5.8, STORY), 0), ("ac_unit", (12.9, 2.5, 0), 0),
                     ("downspout", (0.0, 0.3, 0), -90), ("utility_meter", (12.0, 6.0, 0), 90),
                     ("water_spigot", (12.0, 2.0, 0), 90)])
    for i in (1, 2, 4, 5):
        put("fence_picket", (4 * i + 2, 8, 0), 0)
    put("gate_wood", (14, 8, 0), 0)
    for (x, y) in ((22, 10), (22, 14), (22, 18)):
        put("fence_picket", (x, y, 0), 90)
    put("mailbox", (17.4, 7.6, 0), 180)
    put("tree_green", (6.5, 10.5, 0))
    put("tree_green", (21.0, 22.5, 0))
    put("flower_cluster", (9.5, 11.4, 0))
    put("flower_cluster", (18.5, 11.4, 0))
    put("bush", (5.2, 13.0, 0))

    # General store
    building(-8, 3, 3, 2, "S", "batten_green", "metal_galv",
             ["win_wide", "door_halfglass", "win_wide"], ["full", "door_wood", "full"],
             ["win_small", "full"], ["win_small", "full"], floor="floor_wood",
             porches=[(0, "porch_large_metal_galv", 2), (2, "porch_small_metal_galv", 1)],
             extras=[("sign_facade", (6.0, -3.97, 2.45), 0), ("bench", (2.5, -2.6, FLOOR_T), 0),
                     ("trash_can", (9.3, -3.2, FLOOR_T), 0), ("rain_barrel", (12.5, 7.0, 0), 0),
                     ("hitching_post", (6.0, -5.4, 0), 0)])
    put("tree_autumn", (-34.5, 10.0, 0))
    put("planter_box", (-21.0, 7.4, 0))

    # Rural clinic (raised floor, hip roof, ramp)
    g = building(7, 3, 3, 2, "S", "stucco", "shingle",
                 ["win_large", "door_halfglass", "win_large"], ["win_small", "door_wood", "win_small"],
                 ["win_large", "win_large"], ["win_large", "win_large"], floor="floor_raised_wood", base=0.4,
                 roof="hip",
                 extras=[("vent_pipe", (3.0, 6.0, STORY + 1.0), 0), ("exterior_light", (7.0, -0.1, 0), 0),
                         ("electrical_box", (12.0, 2.0, 0), 90), ("awning", (6.0, -0.1, 0), 0)])
    for a in range(3):
        put("skirt_lattice", (4 * a + 2, 0, 0), 0, g)
        put("skirt_lattice", (4 * a + 2, 8, 0), 180, g)
    for b in range(2):
        put("skirt_lattice", (0, 4 * b + 2, 0), -90, g)
        put("skirt_lattice", (12, 4 * b + 2, 0), 90, g)
    put("ramp_2x1", (30.9, 11.25, 0), 0)
    put("sign_clinic", (39.0, 9.2, 0), 0)
    put("flower_cluster", (36.0, 10.8, 0))
    put("tree_pine", (43.0, 16.0, 0))
    put("sign_crossing", (44.8, 4.9, 0), 180)

    # Volunteer fire station (south side, faces the road)
    building(2, -5, 3, 2, "N", "batten_red", "metal_galv",
             ["door_garage", "door_garage", "door_wood"], ["win_small", "door_wood", "win_small"],
             ["win_large", "win_large"], ["win_large", "win_large"], floor="floor_concrete",
             extras=[("tv_antenna", (10.0, 3.0, STORY + 1.4), 0), ("exterior_light", (8.7, -0.1, 0), 0),
                     ("exterior_light", (3.3, -0.1, 0), 0), ("fire_hydrant", (12.6, -1.4, 0), 0)])
    for i in (2, 3, 4):
        put("floor_concrete", tc(i, -3, -0.18), 0)
        used.add((i, -3))
    put("sidewalk_driveway", tc(2, -2), 180)
    put("sidewalk_driveway", tc(3, -2), 180)

    # Farm garage (south side)
    building(-8, -5, 2, 2, "N", "lap_natural", "metal_green",
             ["door_barn", "door_garage"], ["full", "win_small"], ["win_small", "full"], ["full", "win_small"],
             floor="floor_concrete",
             extras=[("wood_pile", (-1.2, 2.0, 0), -90), ("propane_tank", (10.0, 6.0, 0), 90),
                     ("wind_vent", (4.0, 5.5, STORY + 1.3), 0)])
    for i in (-8, -7):
        put("pad_gravel", tc(i, -3), 0)
        used.add((i, -3))
    put("sidewalk_driveway", tc(-8, -2), 180)
    put("sidewalk_driveway", tc(-7, -2), 180)
    put("hay_bale", (-38.0, -14.0, 0), 20)
    put("hay_bale", (-38.5, -17.0, 0), -15)

    # Stone cottage on the cross street (faces west)
    building(2, 6, 2, 2, "W", "stone", "shingle",
             ["win_shutter", "door_wood"], ["win_small", "win_small"], ["win_shutter", "full"],
             ["win_shutter", "win_shutter"], porches=[(1, "porch_small_shingle", 1)],
             extras=[("chimney", (2.0, 6.0, STORY), 0)])
    put("well", (16.5, 38.0, 0))
    put("tree_bare", (18.0, 31.0, 0))

    # Farm fields with a wire fence
    for i in range(-13, -9):
        for j in range(-8, -3):
            put("terrain_field_crops" if (i + j) % 3 else "terrain_field_plowed", tc(i, j), 0)
            used.add((i, j))
    for j in range(-8, -3):
        put("fence_wire", (-36, 4 * j + 2, 0), 90)
    put("gate_wire", (-38, -12, 0), 0)
    for i in range(-13, -10):
        put("road_dirt", tc(i, -3), 0)
        used.add((i, -3))

    # Water tower landmark
    put("water_tower", (36.0, -26.0, 0))
    used |= {(8, -7), (9, -7), (8, -8), (9, -8)}

    # Street furniture
    for x in range(-50, 50, 24):
        put("utility_pole", (x, -5.0, 0), 0)
    for x in range(-38, 50, 24):
        if not (-10 < x < 6):
            put("street_light", (x, 5.0, 0), 0)
    put("sign_stop", (-5.0, 5.2, 0), 0)
    put("sign_street", (5.0, 5.0, 0), 0)
    put("sign_speed", (-20.0, -5.2, 0), 180)
    put("fire_hydrant", (-14.0, 5.4, 0))
    put("trash_can", (-6.0, 6.0, 0))
    for x, y, t in ((-46, 16, "tree_green"), (-44, 30, "tree_pine"), (-26, 22, "tree_green"), (26, 30, "tree_autumn"),
                    (42, 30, "tree_green"), (46, -14, "tree_pine"), (24, -26, "tree_green"),
                    (-20, -26, "tree_pine_snowy"), (-4, -30, "tree_green"), (8, 26, "hedge"),
                    (-14, 30, "bush"), (12, 30, "tree_pine"), (-30, 38, "tree_green")):
        put(t, (x, y, 0))

    # ground everywhere else
    for i in range(I0, I1 + 1):
        for j in range(J0, J1 + 1):
            if (i, j) not in used:
                put("terrain_grass", tc(i, j), 0)
    return coll


def render_town():
    scene = bpy.context.scene
    set_engine(scene, 64)
    sky = (0.42, 0.58, 0.85)
    world_setup(scene, sky, (0.55, 0.65, 0.8), 1.0)
    lc = bpy.data.collections.new("TownLights")
    scene.collection.children.link(lc)
    add_sun(lc, (52, 0, 38), 4.0, color=(1.0, 0.93, 0.82), angle=1.5)
    shots = {
        "town_aerial": ((58, -66, 46), (0, 4, 0), 30),
        "town_street": ((-50, -2.2, 1.8), (30, 2.8, 3.2), 28),
        "town_home": ((4.5, -3.0, 4.0), (14, 14, 2.6), 32),
        "town_store": ((-12, -4.0, 3.5), (-26, 14, 2.6), 30),
        "town_firestation": ((26, 1.5, 4.5), (14, -15, 2.4), 30),
        "town_clinic": ((22, 1.0, 4.2), (34, 14, 2.4), 30),
    }
    scene.render.resolution_x, scene.render.resolution_y = 1920, 1080
    for name, (loc, tgt, lens) in shots.items():
        cd = bpy.data.cameras.new(name)
        cd.lens = lens
        cd.clip_end = 1000
        cam = bpy.data.objects.new(name, cd)
        cam.location = loc
        cam.rotation_euler = (Vector(tgt) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()
        lc.objects.link(cam)
        scene.camera = cam
        render_to(scene, os.path.join(OUT, "previews", f"{name}.png"))


# ===================================================================================== MAIN
def main():
    t0 = time.time()
    reset_scene()
    make_materials()
    os.makedirs(OUT, exist_ok=True)
    print(f"== building {len(REG)} modules into {OUT}")
    meshes, manifest, failures = build_all()
    write_manifest(manifest)
    scene = bpy.context.scene
    for lc in scene.view_layers[0].layer_collection.children:
        if lc.name == "Modules":
            lc.exclude = True
    if DO_RENDER:
        render_sheets(meshes)
    if DO_TOWN:
        build_town(meshes)
        if DO_RENDER:
            render_town()
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, "rural_modules.blend"), compress=True)
    print(f"== {len(manifest)} modules exported, {len(failures)} failed, {time.time() - t0:.0f}s")
    for mid, msg in failures:
        print(f"!! FAILED {mid}: {msg}")
    if failures:
        sys.exit(1)


main()
