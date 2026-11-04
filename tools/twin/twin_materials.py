"""手続きマテリアル。テクスチャの繰り返しを出さないため、世界座標（m）の 3D ノイズで作る。"""
from __future__ import annotations

from pathlib import Path

import bpy


TEX = Path(__file__).resolve().parent / "textures"


class G:
    """ノードを短く書くための薄い包み。"""

    def __init__(self, name: str):
        self.m = bpy.data.materials.new(name)
        self.m.use_nodes = True
        self.t = self.m.node_tree
        self.t.nodes.clear()
        self.out = self.t.nodes.new("ShaderNodeOutputMaterial")
        self.tc = self.t.nodes.new("ShaderNodeTexCoord")

    @property
    def pos(self):
        return self.tc.outputs["Object"]

    def link(self, a, b):
        self.t.links.new(a, b)

    def _set(self, sock, v):
        if hasattr(v, "is_linked") or isinstance(v, bpy.types.NodeSocket):
            self.link(v, sock)
        else:
            sock.default_value = v

    def mapping(self, v, scale=(1, 1, 1), loc=(0, 0, 0)):
        n = self.t.nodes.new("ShaderNodeMapping")
        self.link(v, n.inputs["Vector"])
        n.inputs["Scale"].default_value = scale
        n.inputs["Location"].default_value = loc
        return n.outputs[0]

    def noise(self, v, scale, detail=6.0, rough=0.55, lac=2.0, dist=0.0):
        n = self.t.nodes.new("ShaderNodeTexNoise")
        self.link(v, n.inputs["Vector"])
        n.inputs["Scale"].default_value = scale
        n.inputs["Detail"].default_value = detail
        n.inputs["Roughness"].default_value = rough
        n.inputs["Lacunarity"].default_value = lac
        n.inputs["Distortion"].default_value = dist
        return n

    def voronoi(self, v, scale, feature="F1", rand=1.0):
        n = self.t.nodes.new("ShaderNodeTexVoronoi")
        n.feature = feature
        self.link(v, n.inputs["Vector"])
        n.inputs["Scale"].default_value = scale
        n.inputs["Randomness"].default_value = rand
        return n

    def math(self, op, a, b=None, clamp=False):
        n = self.t.nodes.new("ShaderNodeMath")
        n.operation = op
        n.use_clamp = clamp
        self._set(n.inputs[0], a)
        if b is not None:
            self._set(n.inputs[1], b)
        return n.outputs[0]

    def add(self, a, b, clamp=False):
        return self.math("ADD", a, b, clamp)

    def sub(self, a, b, clamp=False):
        return self.math("SUBTRACT", a, b, clamp)

    def mul(self, a, b, clamp=False):
        return self.math("MULTIPLY", a, b, clamp)

    def smooth(self, v, lo, hi, to_lo=0.0, to_hi=1.0):
        n = self.t.nodes.new("ShaderNodeMapRange")
        n.interpolation_type = "SMOOTHERSTEP"
        n.clamp = True
        self._set(n.inputs["Value"], v)
        n.inputs["From Min"].default_value = lo
        n.inputs["From Max"].default_value = hi
        self._set(n.inputs["To Min"], to_lo)
        self._set(n.inputs["To Max"], to_hi)
        return n.outputs[0]

    def mix(self, fac, a, b, blend="MIX"):
        n = self.t.nodes.new("ShaderNodeMix")
        n.data_type = "RGBA"
        n.blend_type = blend
        n.clamp_factor = True
        self._set(n.inputs[0], fac)
        self._set(n.inputs[6], a)
        self._set(n.inputs[7], b)
        return n.outputs[2]

    def mixf(self, fac, a, b):
        n = self.t.nodes.new("ShaderNodeMix")
        n.data_type = "FLOAT"
        n.clamp_factor = True
        self._set(n.inputs[0], fac)
        self._set(n.inputs[2], a)
        self._set(n.inputs[3], b)
        return n.outputs[0]

    def vec_add(self, a, b, k=1.0):
        s = self.t.nodes.new("ShaderNodeVectorMath")
        s.operation = "SCALE"
        self.link(b, s.inputs[0])
        s.inputs["Scale"].default_value = k
        n = self.t.nodes.new("ShaderNodeVectorMath")
        n.operation = "ADD"
        self.link(a, n.inputs[0])
        self.link(s.outputs[0], n.inputs[1])
        return n.outputs[0]

    def image(self, name, vec, srgb=True, box=False, extend="REPEAT"):
        """tools/twin/textures/<name>.jpg。box=True で 3 面投影（箱や布の面ごとに UV が無くてよい）。"""
        n = self.t.nodes.new("ShaderNodeTexImage")
        path = TEX / f"{name}.jpg"
        if not path.exists():
            raise FileNotFoundError(path)
        n.image = bpy.data.images.load(str(path), check_existing=True)
        n.image.colorspace_settings.name = "sRGB" if srgb else "Non-Color"
        n.extension = extend
        n.interpolation = "Cubic"
        if box:
            n.projection = "BOX"
            n.projection_blend = 0.25
        self.link(vec, n.inputs[0])
        return n

    def lum(self, color):
        n = self.t.nodes.new("ShaderNodeRGBToBW")
        self.link(color, n.inputs[0])
        return n.outputs[0]

    def bump(self, height, dist, strength=1.0, normal=None):
        n = self.t.nodes.new("ShaderNodeBump")
        n.inputs["Strength"].default_value = strength
        n.inputs["Distance"].default_value = dist
        self.link(height, n.inputs["Height"])
        if normal is not None:
            self.link(normal, n.inputs["Normal"])
        return n.outputs[0]

    def principled(self, color=None, rough=0.7, spec=0.5, metal=0.0, normal=None, sheen=0.0, sss=0.0, sss_r=None, sss_s=0.01):
        n = self.t.nodes.new("ShaderNodeBsdfPrincipled")
        if color is not None:
            self._set(n.inputs["Base Color"], color)
        self._set(n.inputs["Roughness"], rough)
        self._set(n.inputs["Specular IOR Level"], spec)
        self._set(n.inputs["Metallic"], metal)
        if normal is not None:
            self.link(normal, n.inputs["Normal"])
        if sheen:
            n.inputs["Sheen Weight"].default_value = sheen
            n.inputs["Sheen Roughness"].default_value = 0.6
        if sss:
            n.inputs["Subsurface Weight"].default_value = sss
            n.inputs["Subsurface Scale"].default_value = sss_s
            if sss_r:
                n.inputs["Subsurface Radius"].default_value = sss_r
        return n

    def finish(self, shader, displacement=None):
        self.link(shader.outputs[0], self.out.inputs["Surface"])
        if displacement is not None:
            d = self.t.nodes.new("ShaderNodeDisplacement")
            self.link(displacement[0], d.inputs["Height"])
            d.inputs["Scale"].default_value = displacement[1]
            self.link(d.outputs[0], self.out.inputs["Displacement"])
            self.m.displacement_method = "BOTH"
        return self.m


def rgb(r, g, b):
    return (r, g, b, 1.0)


def wall_material():
    """古い壁。素材写真 2 枚（下端が床際）を面の UV で貼り、低い周波数の境で混ぜる。面の細かい凹凸は手続きで足す。"""
    g = G("wall")
    p = g.pos
    uv = g.tc.outputs["UV"]
    a = g.image("wall_a", uv, extend="REPEAT")
    b_uv = g.mapping(uv, scale=(1.0, 1.0, 1.0), loc=(0.37, 0.0, 0.0))
    b = g.image("wall_b", b_uv, extend="REPEAT")
    warp = g.noise(p, 2.2, 6.0, 0.6).outputs["Color"]
    mask = g.smooth(g.noise(g.vec_add(p, warp, 0.08), 0.5, 6.0, 0.62).outputs["Fac"], 0.47, 0.53)
    col = g.mix(mask, a.outputs[0], b.outputs[0])
    # 写真の明るさのまま。実物の塗膜の暗さへ落とす
    tint = g.t.nodes.new("ShaderNodeHueSaturation")
    tint.inputs["Saturation"].default_value = 0.82
    tint.inputs["Value"].default_value = 0.5
    g.link(col, tint.inputs["Color"])
    fine = g.noise(p, 180.0, 5.0, 0.62).outputs["Fac"]
    grime = g.noise(g.vec_add(p, warp, 0.05), 1.1, 7.0, 0.6).outputs["Fac"]
    col2 = g.mix(g.mul(g.smooth(grime, 0.45, 0.72), 0.32), tint.outputs[0], rgb(0.03, 0.033, 0.026), "MULTIPLY")
    hi = g.noise(p, 520.0, 3.0, 0.65).outputs["Fac"]
    col2 = g.mix(g.mul(g.smooth(hi, 0.3, 0.7), 0.22), col2, rgb(0.5, 0.5, 0.48), "MULTIPLY")
    # 高さは写真の明暗から（端の明るい欠けと暗い割れ目がそのまま段になる）
    l = g.lum(col)
    h = g.add(g.add(g.mul(l, 0.0042), g.mul(fine, 0.00035)), g.mul(hi, 0.00012))
    macro = g.mul(g.noise(p, 1.6, 3.0, 0.5).outputs["Fac"], 0.01)
    normal = g.bump(g.add(h, macro), 1.0, 1.0)
    rough = g.add(0.55, g.mul(g.sub(1.0, l), 0.32))
    return g.finish(g.principled(col2, rough, 0.4, normal=normal))


def floor_material():
    """コンクリートの床。素材写真を向きと大きさを変えて 2 層重ね（繰り返しを隠す）、埃と水気を足す。"""
    g = G("floor")
    p = g.pos
    a = g.image("floor", g.mapping(p, scale=(0.4, 0.4, 0.4), loc=(0.13, 0.27, 0)))
    rot = g.t.nodes.new("ShaderNodeMapping")
    g.link(p, rot.inputs["Vector"])
    rot.inputs["Scale"].default_value = (0.23, 0.23, 0.23)
    rot.inputs["Rotation"].default_value = (0, 0, 0.71)
    b = g.image("floor", rot.outputs[0])
    warp = g.noise(p, 1.3, 6.0, 0.6).outputs["Color"]
    m = g.smooth(g.noise(g.vec_add(p, warp, 0.2), 0.7, 6.0, 0.6).outputs["Fac"], 0.42, 0.58)
    col = g.mix(m, a.outputs[0], b.outputs[0])
    tint = g.t.nodes.new("ShaderNodeHueSaturation")
    tint.inputs["Saturation"].default_value = 0.8
    tint.inputs["Value"].default_value = 0.55
    g.link(col, tint.inputs["Color"])
    l = g.lum(col)
    fine = g.noise(p, 55.0, 6.0, 0.62).outputs["Fac"]
    grit = g.noise(p, 260.0, 3.0, 0.6).outputs["Fac"]
    stain = g.noise(g.vec_add(p, warp, 0.1), 0.8, 6.0, 0.5).outputs["Fac"]
    wet = g.smooth(g.add(stain, g.mul(fine, 0.05)), 0.635, 0.66)
    rough = g.mixf(wet, g.add(0.5, g.mul(fine, 0.32)), g.add(0.06, g.mul(grit, 0.1)))
    base = g.mix(g.mul(wet, 0.55), tint.outputs[0], rgb(0.02, 0.022, 0.02))
    h = g.add(g.mul(l, 0.0032), g.add(g.mul(fine, 0.0009), g.mul(grit, 0.0004)))
    h = g.mixf(wet, h, g.mul(h, 0.12))
    return g.finish(g.principled(base, rough, 0.5, normal=g.bump(h, 1.0, 1.0)))


def ceiling_material():
    g = G("ceiling")
    p = g.pos
    a = g.image("ceiling", g.mapping(p, scale=(0.3, 0.3, 0.3), loc=(0.2, 0.1, 0)))
    tint = g.t.nodes.new("ShaderNodeHueSaturation")
    tint.inputs["Saturation"].default_value = 0.7
    tint.inputs["Value"].default_value = 0.42
    g.link(a.outputs[0], tint.inputs["Color"])
    l = g.lum(a.outputs[0])
    fine = g.noise(p, 90.0, 5.0, 0.6).outputs["Fac"]
    h = g.add(g.mul(l, 0.005), g.mul(fine, 0.0006))
    return g.finish(g.principled(tint.outputs[0], 0.9, 0.3, normal=g.bump(h, 1.0)))


def curtain_material():
    """グレーの厚手のカーテン（実プレート A）。素材写真の織りと、場所ごとの汚れ。"""
    g = G("curtain")
    p = g.pos
    # 織り目を揃えない: 布の歪みで座標をずらし、場所で密度を変える
    warp = g.noise(p, 2.6, 4.0, 0.55).outputs["Color"]
    im = g.image("curtain", g.mapping(g.vec_add(p, warp, 0.05), scale=(2.3, 2.3, 1.9), loc=(0.11, 0.3, 0.07)), box=True)
    mott = g.noise(p, 3.0, 6.0, 0.55).outputs["Fac"]
    tint = g.t.nodes.new("ShaderNodeHueSaturation")
    tint.inputs["Saturation"].default_value = 0.85
    tint.inputs["Value"].default_value = 0.42
    g.link(im.outputs[0], tint.inputs["Color"])
    grime = g.smooth(g.add(g.mul(mott, 0.7), g.mul(g.noise(g.mapping(p, scale=(2, 2, 0.4)), 3.0, 5.0).outputs["Fac"], 0.4)), 0.56, 0.78)
    base = g.mix(g.mul(grime, 0.35), tint.outputs[0], rgb(0.05, 0.05, 0.045))
    n = g.bump(g.lum(im.outputs[0]), 0.0006, 1.0)
    sh = g.principled(base, 0.97, 0.12, normal=n, sheen=0.12)
    return g.finish(sh)


def painted_metal(color, rough=0.42, chip=0.0):
    g = G("painted_metal")
    p = g.pos
    fine = g.noise(p, 180.0, 5.0, 0.6).outputs["Fac"]
    scratch = g.noise(g.mapping(p, scale=(1, 1, 0.05)), 90.0, 4.0, 0.7).outputs["Fac"]
    c = g.mix(g.mul(g.smooth(scratch, 0.62, 0.76), 0.5), rgb(*color), rgb(0.16, 0.15, 0.14))
    if chip:
        m = g.smooth(g.add(g.mul(g.noise(p, 24.0, 7.0, 0.7).outputs["Fac"], 1.0), 0.0), 0.72 - chip * 0.1, 0.76 - chip * 0.1)
        c = g.mix(m, c, rgb(0.06, 0.05, 0.04))
    r = g.add(rough, g.mul(fine, 0.28))
    n = g.bump(g.add(g.mul(fine, 0.0003), g.mul(scratch, 0.0002)), 1.0)
    return g.finish(g.principled(c, r, 0.5, normal=n))


def slotted_steel(axis=None):
    """穴あきアングル。axis が "x" / "y" のときは板の幅方向を使って長穴を作る（棚板は axis=None で穴なし）。"""
    g = G("slotted_steel")
    p = g.pos
    fine = g.noise(p, 120.0, 5.0, 0.6).outputs["Fac"]
    edge = g.noise(p, 34.0, 7.0, 0.72).outputs["Fac"]
    c = g.mix(g.smooth(fine, 0.35, 0.65), rgb(0.05, 0.052, 0.046), rgb(0.1, 0.102, 0.088))
    wear = g.smooth(edge, 0.66, 0.74)
    c = g.mix(g.mul(wear, 0.5), c, rgb(0.05, 0.036, 0.025))
    h = g.add(g.mul(fine, 0.00025), g.mul(wear, 0.0002))
    rough = g.add(0.44, g.mul(wear, 0.4))
    if axis:
        z = g.t.nodes.new("ShaderNodeSeparateXYZ")
        g.link(p, z.inputs[0])
        gz = g.t.nodes.new("ShaderNodeSeparateXYZ")
        g.link(g.tc.outputs["Generated"], gz.inputs[0])
        rows = g.math("SINE", g.mul(z.outputs["Z"], 104.0))
        across = g.math("ABSOLUTE", g.sub(gz.outputs["X" if axis == "x" else "Y"], 0.5))
        hole = g.mul(g.smooth(rows, 0.72, 0.8), g.smooth(across, 0.24, 0.3, 1.0, 0.0))
        c = g.mix(hole, c, rgb(0.004, 0.004, 0.004))
        h = g.add(h, g.mul(g.sub(1.0, hole), 0.0012))
    return g.finish(g.principled(c, rough, 0.45, normal=g.bump(h, 1.0)))


def rust_metal():
    g = G("rust")
    p = g.pos
    n1 = g.noise(p, 14.0, 8.0, 0.68).outputs["Fac"]
    n2 = g.noise(p, 90.0, 6.0, 0.65).outputs["Fac"]
    c = g.mix(g.smooth(n1, 0.38, 0.62), rgb(0.05, 0.03, 0.018), rgb(0.17, 0.08, 0.032))
    c = g.mix(g.mul(g.smooth(n2, 0.6, 0.75), 0.6), c, rgb(0.03, 0.02, 0.015))
    n = g.bump(g.add(g.mul(n1, 0.0015), g.mul(n2, 0.0006)), 1.0)
    return g.finish(g.principled(c, g.add(0.62, g.mul(n2, 0.3)), 0.35, normal=n))


def debris():
    g = G("debris")
    n = g.noise(g.pos, 160.0, 5.0, 0.6).outputs["Fac"]
    c = g.mix(g.smooth(n, 0.35, 0.65), rgb(0.03, 0.075, 0.07), rgb(0.13, 0.16, 0.14))
    m = g.smooth(g.noise(g.pos, 9.0, 3.0).outputs["Fac"], 0.48, 0.6)
    c = g.mix(m, c, rgb(0.22, 0.2, 0.17))
    return g.finish(g.principled(c, 0.82, 0.3, normal=g.bump(n, 0.0005)))


def plain(name, color, rough=0.5, spec=0.5, metal=0.0, bumpk=0.0, scale=300.0):
    g = G(name)
    n = None
    if bumpk:
        n = g.bump(g.noise(g.pos, scale, 4.0).outputs["Fac"], bumpk)
    return g.finish(g.principled(rgb(*color), rough, spec, metal, normal=n))


def worn_plastic():
    """擦れと白っぽい粉をふいた黒い樹脂（三脚の頭・スマホ台）。"""
    g = G("worn_plastic")
    p = g.pos
    fine = g.noise(p, 300.0, 4.0, 0.6).outputs["Fac"]
    scuff = g.noise(g.mapping(p, scale=(1, 1, 0.25)), 70.0, 6.0, 0.72).outputs["Fac"]
    dust = g.noise(p, 12.0, 5.0, 0.6).outputs["Fac"]
    c = g.mix(g.mul(g.smooth(scuff, 0.58, 0.74), 0.5), rgb(0.012, 0.012, 0.013), rgb(0.09, 0.09, 0.085))
    c = g.mix(g.mul(g.smooth(dust, 0.45, 0.7), 0.3), c, rgb(0.11, 0.105, 0.095))
    r = g.add(0.38, g.add(g.mul(fine, 0.2), g.mul(scuff, 0.26)))
    return g.finish(g.principled(c, r, 0.45, normal=g.bump(g.add(g.mul(fine, 0.00012), g.mul(scuff, 0.00022)), 1.0)))


def cardboard():
    g = G("cardboard")
    p = g.pos
    im = g.image("cardboard", g.mapping(p, scale=(1.5, 1.5, 1.5), loc=(0.2, 0.4, 0.1)), box=True)
    tint = g.t.nodes.new("ShaderNodeHueSaturation")
    tint.inputs["Saturation"].default_value = 0.85
    tint.inputs["Value"].default_value = 0.55
    g.link(im.outputs[0], tint.inputs["Color"])
    l = g.lum(im.outputs[0])
    return g.finish(g.principled(tint.outputs[0], g.add(0.72, g.mul(g.sub(1.0, l), 0.2)), 0.25, normal=g.bump(l, 0.0035)))


def glass_emission():
    """電球のガラス。すりガラスの球で、中心が強く縁で落ちる（真っ白な滴にしない）。"""
    g = G("bulb_glass")
    lw = g.t.nodes.new("ShaderNodeLayerWeight")
    lw.inputs["Blend"].default_value = 0.42
    facing = g.smooth(lw.outputs["Facing"], 0.0, 1.0, 1.0, 0.14)
    grain = g.noise(g.pos, 800.0, 2.0).outputs["Fac"]
    n = g.t.nodes.new("ShaderNodeEmission")
    n.inputs["Color"].default_value = (1.0, 0.8, 0.56, 1.0)
    g.link(g.mul(g.mul(facing, 46.0), g.add(0.9, g.mul(grain, 0.2))), n.inputs["Strength"])
    gloss = g.principled(rgb(0.9, 0.9, 0.9), 0.14, 0.5)
    add = g.t.nodes.new("ShaderNodeAddShader")
    g.link(n.outputs[0], add.inputs[0])
    g.link(gloss.outputs[0], add.inputs[1])
    g.link(add.outputs[0], g.out.inputs["Surface"])
    return g.m


def glass_clear(tint=(0.92, 0.95, 0.98)):
    g = G("glass")
    n = g.principled(rgb(*tint), 0.02, 0.5)
    n.inputs["Transmission Weight"].default_value = 1.0
    n.inputs["IOR"].default_value = 1.5
    return g.finish(n)


def phone_screen(image_path=None):
    g = G("phone_screen")
    em = g.t.nodes.new("ShaderNodeEmission")
    lw = g.t.nodes.new("ShaderNodeLayerWeight")
    lw.inputs["Blend"].default_value = 0.3
    g.link(g.smooth(lw.outputs["Facing"], 0.0, 1.0, 0.02, 0.003), em.inputs["Strength"])
    if image_path:
        tx = g.t.nodes.new("ShaderNodeTexImage")
        tx.image = bpy.data.images.load(str(image_path), check_existing=True)
        g.link(g.tc.outputs["UV"], tx.inputs[0])
        g.link(tx.outputs[0], em.inputs["Color"])
    else:
        em.inputs["Color"].default_value = (0.1, 0.13, 0.13, 1)
    glossy = g.t.nodes.new("ShaderNodeBsdfPrincipled")
    glossy.inputs["Base Color"].default_value = (0, 0, 0, 1)
    glossy.inputs["Roughness"].default_value = 0.11
    glossy.inputs["Specular IOR Level"].default_value = 0.8
    add = g.t.nodes.new("ShaderNodeAddShader")
    g.link(em.outputs[0], add.inputs[0])
    g.link(glossy.outputs[0], add.inputs[1])
    g.link(add.outputs[0], g.out.inputs["Surface"])
    return g.m


def volume_material(density=0.016):
    m = bpy.data.materials.new("air")
    m.use_nodes = True
    t = m.node_tree
    t.nodes.clear()
    out = t.nodes.new("ShaderNodeOutputMaterial")
    tc = t.nodes.new("ShaderNodeTexCoord")
    noise = t.nodes.new("ShaderNodeTexNoise")
    t.links.new(tc.outputs["Object"], noise.inputs["Vector"])
    noise.inputs["Scale"].default_value = 0.55
    noise.inputs["Detail"].default_value = 3.0
    rng = t.nodes.new("ShaderNodeMapRange")
    t.links.new(noise.outputs["Fac"], rng.inputs["Value"])
    rng.inputs["From Min"].default_value = 0.3
    rng.inputs["From Max"].default_value = 0.72
    rng.inputs["To Min"].default_value = density * 0.5
    rng.inputs["To Max"].default_value = density * 1.6
    v = t.nodes.new("ShaderNodeVolumeScatter")
    t.links.new(rng.outputs[0], v.inputs["Density"])
    v.inputs["Anisotropy"].default_value = 0.45
    v.inputs["Color"].default_value = (0.86, 0.9, 0.9, 1)
    t.links.new(v.outputs[0], out.inputs["Volume"])
    return m
