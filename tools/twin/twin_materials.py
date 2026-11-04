"""手続きマテリアル。テクスチャの繰り返しを出さないため、世界座標（m）の 3D ノイズで作る。"""
from __future__ import annotations

from pathlib import Path

import bpy


TEX = Path(__file__).resolve().parent / "textures"

# 写真の平均の明るさを、実物の反射率へ合わせる係数（prep_textures.py の albedo_linear を見て決める）
CURTAIN_VALUE = 0.42
STEEL_VALUE = 0.5
RUST_VALUE = 0.6
CARD_VALUE = 0.6


def tc_ceiling():
    import twin_common as tc

    return tc.ROOM_H


def tc_wall_x():
    import twin_common as tc

    return tc.ROOM_X[0]


def tc_bulb_top():
    import twin_common as tc

    return (tc.BULB.x, tc.BULB.y, tc.ROOM_H)


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

    def gray(self, v):
        """値を灰色の色として渡す（掛け算の相手に使う）。"""
        n = self.t.nodes.new("ShaderNodeCombineColor")
        for k in range(3):
            self._set(n.inputs[k], v)
        return n.outputs[0]

    def split(self, color):
        n = self.t.nodes.new("ShaderNodeSeparateColor")
        self.link(color, n.inputs[0])
        return n.outputs[0], n.outputs[1], n.outputs[2]

    def xyz(self, v):
        n = self.t.nodes.new("ShaderNodeSeparateXYZ")
        self.link(v, n.inputs[0])
        return n.outputs[0], n.outputs[1], n.outputs[2]

    def tint(self, color, sat, val):
        n = self.t.nodes.new("ShaderNodeHueSaturation")
        n.inputs["Saturation"].default_value = sat
        n.inputs["Value"].default_value = val
        self.link(color, n.inputs["Color"])
        return n.outputs[0]

    def ao(self, dist, inside=False, samples=6):
        """1 = 開けている。inside=True は裏から測るので、出っ張った縁で値が下がる。"""
        n = self.t.nodes.new("ShaderNodeAmbientOcclusion")
        n.samples = samples
        n.inside = inside
        n.inputs["Distance"].default_value = dist
        return n.outputs["AO"]

    def bevel_normal(self, radius, samples=6):
        """箱の角を法線だけ丸める（刃物のような角は CG の印）。"""
        n = self.t.nodes.new("ShaderNodeBevel")
        n.samples = samples
        n.inputs["Radius"].default_value = radius
        return n.outputs[0]

    def up(self):
        geo = self.t.nodes.new("ShaderNodeNewGeometry")
        return self.xyz(geo.outputs["Normal"])[2]

    def dust_layer(self, color, rough, amount=1.0):
        """上を向いた面に積もる埃。ムラは埃の写真から取る。"""
        v = self.mapping(self.pos, scale=(2.2, 2.2, 2.2), loc=(0.21, 0.13, 0.4))
        _, patch, fine = self.split(self.image("floor_dust_s", v, srgb=False, box=True).outputs[0])
        top = self.smooth(self.up(), 0.3, 0.92)
        w = self.mul(self.mul(top, self.smooth(patch, 0.3, 0.72, 0.08, 1.0)), amount)
        w = self.math("MINIMUM", w, 0.9)
        dust = self.mix(fine, rgb(0.13, 0.128, 0.12), rgb(0.24, 0.235, 0.22))
        return self.mix(w, color, dust), self.mixf(w, rough, 0.95)

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


def wall_material(streaks=(), dust_top=2.5):
    """古い壁。2 枚の素材写真を混ぜ、塗膜と下地の層を本物の段差にする。

    汚れは雲模様のノイズで置かず、形から決める: 隅の暗がりは AO、床際の湿りと天井際のすすは高さ、
    金具の下の錆の筋は金具の位置（streaks = (y, 上端 z, 幅, 長さ) の並び。-x の壁のみ）。
    """
    g = G("wall")
    p = g.pos
    uv = g.tc.outputs["UV"]
    b_uv = g.mapping(uv, loc=(0.37, 0.0, 0.0))
    a, sa = g.image("wall_a", uv), g.image("wall_a_s", uv, srgb=False)
    b, sb = g.image("wall_b", b_uv), g.image("wall_b_s", b_uv, srgb=False)
    warp = g.noise(p, 2.2, 6.0, 0.6).outputs["Color"]
    mask = g.smooth(g.noise(g.vec_add(p, warp, 0.08), 0.5, 6.0, 0.62).outputs["Fac"], 0.47, 0.53)
    col = g.mix(mask, a.outputs[0], b.outputs[0])
    h0, layer, _ = g.split(g.mix(mask, sa.outputs[0], sb.outputs[0]))
    tint = g.tint(col, 0.82, 0.5)
    # 塗膜の細かいひび（40cm 四方）。距離を変えても壁に細部が残るのはこの層
    dv = g.mapping(p, scale=(2.5, 2.5, 2.5), loc=(0.17, 0.41, 0.23))
    ds = g.image("wall_detail_s", dv, srgb=False, box=True)
    hd, _, dd = g.split(ds.outputs[0])
    gain = g.mixf(layer, 1.0, g.add(0.66, g.mul(dd, 0.68)))
    col2 = g.mix(1.0, tint, g.gray(gain), "MULTIPLY")
    # 隅の暗がり
    near = g.smooth(g.sub(1.0, g.ao(0.34)), 0.08, 0.72)
    breakup = g.smooth(g.noise(g.vec_add(p, warp, 0.05), 1.1, 7.0, 0.6).outputs["Fac"], 0.36, 0.66)
    col2 = g.mix(g.mul(near, g.add(0.38, g.mul(breakup, 0.34))), col2, rgb(0.05, 0.045, 0.036), "MULTIPLY")
    # 床際の湿りと天井際のすす
    x, y, z = g.xyz(p)
    damp = g.mul(g.smooth(z, 0.05, 0.62, 1.0, 0.0), g.add(0.35, g.mul(breakup, 0.65)))
    col2 = g.mix(g.mul(damp, 0.66), col2, rgb(0.14, 0.125, 0.085), "MULTIPLY")
    soot = g.mul(g.smooth(z, dust_top, tc_ceiling(), 0.0, 1.0), g.add(0.5, g.mul(breakup, 0.5)))
    col2 = g.mix(g.mul(soot, 0.45), col2, rgb(0.13, 0.12, 0.1), "MULTIPLY")
    # 金具の下の錆の筋
    if streaks:
        fine_z = g.noise(g.mapping(p, scale=(1, 34, 1.2)), 3.0, 5.0, 0.6).outputs["Fac"]
        total = None
        for yy, top, w, ln in streaks:
            across = g.smooth(g.math("ABSOLUTE", g.sub(y, yy)), 0.0, w, 1.0, 0.0)
            down = g.mul(g.smooth(z, top - ln, top - ln * 0.25), g.smooth(z, top - 0.004, top + 0.004, 1.0, 0.0))
            s = g.mul(across, down)
            total = s if total is None else g.math("MAXIMUM", total, s)
        total = g.mul(total, g.smooth(fine_z, 0.36, 0.62))
        total = g.mul(total, g.smooth(x, tc_wall_x() + 0.18, tc_wall_x() + 0.05))
        col2 = g.mix(g.mul(total, 0.72), col2, rgb(0.11, 0.05, 0.022))
    height = g.add(h0, g.mul(g.sub(hd, 0.5), 0.1))
    micro = g.mul(g.noise(p, 520.0, 3.0, 0.65).outputs["Fac"], 0.012)
    height = g.add(height, micro)
    rough = g.mixf(layer, 0.93, g.add(0.46, g.mul(g.sub(1.0, dd), 0.3)))
    sh = g.principled(col2, rough, 0.42)
    return g.finish(sh, displacement=(height, 0.0032))


def corner_material():
    """入隅の塗り溜まり。壁の塗膜と同じ細かいひびで、暗い。"""
    g = G("corner")
    p = g.pos
    dv = g.mapping(p, scale=(2.5, 2.5, 2.5))
    d = g.image("wall_detail", dv, box=True)
    ds = g.image("wall_detail_s", dv, srgb=False, box=True)
    h, _, dd = g.split(ds.outputs[0])
    col = g.tint(d.outputs[0], 0.7, 0.2)
    grime = g.smooth(g.noise(p, 3.2, 7.0, 0.6).outputs["Fac"], 0.38, 0.62)
    col = g.mix(g.mul(grime, 0.6), col, rgb(0.08, 0.07, 0.055), "MULTIPLY")
    return g.finish(g.principled(col, g.add(0.6, g.mul(dd, 0.3)), 0.35, normal=g.bump(h, 0.002)))


def floor_material():
    """コンクリートの床。埃は壁際と隅に溜まり、人が歩く中央は擦れる。濡れは小さく 1〜2 か所だけ。"""
    g = G("floor")
    p = g.pos
    a = g.image("floor", g.mapping(p, scale=(0.4, 0.4, 0.4), loc=(0.13, 0.27, 0)))
    sa = g.image("floor_s", g.mapping(p, scale=(0.4, 0.4, 0.4), loc=(0.13, 0.27, 0)), srgb=False)
    rot = g.t.nodes.new("ShaderNodeMapping")
    g.link(p, rot.inputs["Vector"])
    rot.inputs["Scale"].default_value = (0.23, 0.23, 0.23)
    rot.inputs["Rotation"].default_value = (0, 0, 0.71)
    b = g.image("floor", rot.outputs[0])
    sb = g.image("floor_s", rot.outputs[0], srgb=False)
    warp = g.noise(p, 1.3, 6.0, 0.6).outputs["Color"]
    m = g.smooth(g.noise(g.vec_add(p, warp, 0.2), 0.7, 6.0, 0.6).outputs["Fac"], 0.42, 0.58)
    col = g.tint(g.mix(m, a.outputs[0], b.outputs[0]), 0.8, 0.55)
    h0, _, dd = g.split(g.mix(m, sa.outputs[0], sb.outputs[0]))
    fine = g.noise(p, 55.0, 6.0, 0.62).outputs["Fac"]
    grit = g.noise(p, 260.0, 3.0, 0.6).outputs["Fac"]
    # 埃の写真（60cm 四方）を、壁際ほど濃く重ねる
    dv = g.mapping(p, scale=(1.67, 1.67, 1.67), loc=(0.3, 0.07, 0))
    dust_im = g.image("floor_dust", dv)
    _, dust_mask, _ = g.split(g.image("floor_dust_s", dv, srgb=False).outputs[0])
    near = g.smooth(g.sub(1.0, g.ao(0.55)), 0.04, 0.42)
    amount = g.mul(g.add(0.14, g.mul(near, 0.8)), g.add(0.3, g.mul(dust_mask, 1.1)))
    amount = g.math("MINIMUM", amount, 0.92)
    base = g.mix(amount, col, g.tint(dust_im.outputs[0], 0.5, 0.42))
    # 濡れ: しきい値を上げ、縁をぼかして小さな溜まりにする
    stain = g.noise(g.vec_add(p, warp, 0.1), 0.6, 6.0, 0.5).outputs["Fac"]
    wet = g.mul(g.smooth(g.add(stain, g.mul(fine, 0.04)), 0.71, 0.77), g.sub(1.0, amount))
    rough = g.mixf(wet, g.mixf(amount, g.add(0.5, g.mul(fine, 0.3)), 0.94), g.add(0.1, g.mul(grit, 0.12)))
    base = g.mix(g.mul(wet, 0.4), base, rgb(0.02, 0.022, 0.02))
    h = g.add(g.mul(h0, 0.004), g.add(g.mul(fine, 0.0008), g.mul(grit, 0.0004)))
    h = g.mixf(wet, h, g.mul(h, 0.15))
    return g.finish(g.principled(base, rough, 0.46, normal=g.bump(h, 1.0, 1.0)))


def ceiling_material():
    """天井。電球の真上のすす、雨じみの輪郭（乾いた水の縁は濃く残る）、壁際の暗がり。"""
    g = G("ceiling")
    p = g.pos
    v = g.mapping(p, scale=(0.3, 0.3, 0.3), loc=(0.2, 0.1, 0))
    a = g.image("ceiling", v)
    s = g.image("ceiling_s", v, srgb=False)
    h0, _, dd = g.split(s.outputs[0])
    col = g.tint(a.outputs[0], 0.7, 0.42)
    warp = g.noise(p, 1.8, 5.0, 0.6).outputs["Color"]
    n = g.noise(g.vec_add(p, warp, 0.22), 0.62, 7.0, 0.62).outputs["Fac"]
    # 雨じみ: 内側は薄く、縁に濃い線
    inside = g.smooth(n, 0.60, 0.62)
    rim = g.mul(inside, g.smooth(n, 0.62, 0.68, 1.0, 0.0))
    col = g.mix(g.mul(inside, 0.3), col, rgb(0.46, 0.38, 0.25), "MULTIPLY")
    col = g.mix(g.mul(rim, 0.55), col, rgb(0.2, 0.15, 0.09), "MULTIPLY")
    # 電球の真上のすす
    bulb = g.t.nodes.new("ShaderNodeVectorMath")
    bulb.operation = "DISTANCE"
    g.link(p, bulb.inputs[0])
    bulb.inputs[1].default_value = tc_bulb_top()
    soot = g.smooth(bulb.outputs["Value"], 0.05, 0.62, 1.0, 0.0)
    col = g.mix(g.mul(soot, 0.72), col, rgb(0.12, 0.105, 0.085), "MULTIPLY")
    near = g.smooth(g.sub(1.0, g.ao(0.3)), 0.06, 0.6)
    col = g.mix(g.mul(near, 0.55), col, rgb(0.08, 0.07, 0.055), "MULTIPLY")
    fine = g.noise(p, 90.0, 5.0, 0.6).outputs["Fac"]
    h = g.add(g.mul(h0, 0.004), g.mul(fine, 0.0006))
    return g.finish(g.principled(col, g.add(0.72, g.mul(dd, 0.24)), 0.3, normal=g.bump(h, 1.0)))


def curtain_material():
    """灰色の厚手のカーテン（実プレート A）。実物は細い杢の平織りで、離れると糸は見えず、霜降りの濃淡だけが残る。"""
    g = G("curtain")
    p = g.pos
    warp = g.noise(p, 2.6, 4.0, 0.55).outputs["Color"]
    v = g.mapping(g.vec_add(p, warp, 0.012), scale=(4.2, 4.2, 4.2), loc=(0.11, 0.3, 0.07))
    im = g.image("curtain", v, box=True)
    s = g.image("curtain_s", v, srgb=False, box=True)
    h0, _, dd = g.split(s.outputs[0])
    col = g.tint(im.outputs[0], 0.72, CURTAIN_VALUE)
    # 霜降り: 縦糸に沿った濃淡（糸の束は上下に長い）
    heather = g.noise(g.mapping(p, scale=(26, 26, 5.0)), 3.0, 4.0, 0.55).outputs["Fac"]
    col = g.mix(g.mul(g.smooth(heather, 0.3, 0.7), 0.34), col, rgb(0.55, 0.55, 0.57), "MULTIPLY")
    # 裾の汚れと、手がよく触れる高さの擦れ
    _, _, z = g.xyz(p)
    mott = g.noise(g.vec_add(p, warp, 0.04), 4.5, 6.0, 0.55).outputs["Fac"]
    hem = g.mul(g.smooth(z, 0.02, 0.34, 1.0, 0.0), g.add(0.4, g.mul(g.smooth(mott, 0.38, 0.66), 0.6)))
    col = g.mix(g.mul(hem, 0.6), col, rgb(0.2, 0.17, 0.13), "MULTIPLY")
    rub = g.mul(g.smooth(z, 0.82, 0.98), g.smooth(z, 1.06, 1.24, 1.0, 0.0))
    col = g.mix(g.mul(g.mul(rub, g.smooth(mott, 0.44, 0.7)), 0.32), col, rgb(0.28, 0.27, 0.25))
    # 襞の奥は埃で少し濁る
    near = g.smooth(g.sub(1.0, g.ao(0.05)), 0.1, 0.8)
    col = g.mix(g.mul(near, 0.3), col, rgb(0.42, 0.4, 0.37), "MULTIPLY")
    # 布の小さな折れ皺は皺の写真から（手続きのノイズは粘土に見えた）。滑らかな帯のままだと金属に見える
    wr = g.split(g.image("cloth_s", g.mapping(p, scale=(1.1, 1.1, 0.8), loc=(0.3, 0.2, 0.1)), srgb=False, box=True).outputs[0])[0]
    n = g.bump(g.add(g.mul(wr, 0.0075), g.add(g.mul(h0, 0.0011), g.mul(dd, 0.00045))), 1.0, 1.0)
    return g.finish(g.principled(col, 0.96, 0.07, normal=n, sheen=0.05))


def painted_metal(color, rough=0.42, chip=0.0, chip_color=(0.09, 0.05, 0.03), dust=0.5):
    """塗装した金属。傷は鋼板の写真から、欠けは出っ張った縁（AO の裏返し）から、埃は上を向いた面から。"""
    g = G("painted_metal")
    p = g.pos
    v = g.mapping(p, scale=(3.3, 3.3, 3.3), loc=(0.3, 0.1, 0.2))
    h0, layer, dd = g.split(g.image("steel_s", v, srgb=False, box=True).outputs[0])
    c = g.mix(1.0, rgb(*color), g.gray(g.add(0.84, g.mul(dd, 0.32))), "MULTIPLY")
    r = g.add(rough, g.mul(g.sub(layer, 0.5), 0.12))
    if chip:
        edge = g.smooth(g.ao(0.006, inside=True), 0.72 - chip * 0.08, 0.96 - chip * 0.08, 1.0, 0.0)
        edge = g.mul(edge, g.smooth(layer, 0.32, 0.6))
        c = g.mix(edge, c, rgb(*chip_color))
        r = g.mixf(edge, r, 0.82)
    c, r = g.dust_layer(c, r, dust)
    normal = g.bump(g.add(g.mul(h0, 0.00012), g.mul(dd, 0.00006)), 1.0, normal=g.bevel_normal(0.0012))
    return g.finish(g.principled(c, r, 0.5, normal=normal))


def shelf_steel():
    """軽量ラックの灰色の焼付塗装。穴は形に開いているので、ここでは描かない。"""
    g = G("shelf_steel")
    p = g.pos
    v = g.mapping(p, scale=(3.3, 3.3, 3.3), loc=(0.1, 0.4, 0.2))
    im = g.image("steel", v, box=True)
    h0, layer, dd = g.split(g.image("steel_s", v, srgb=False, box=True).outputs[0])
    c = g.tint(im.outputs[0], 0.7, STEEL_VALUE)
    r = g.add(0.46, g.mul(g.sub(layer, 0.5), 0.3))
    edge = g.smooth(g.ao(0.004, inside=True), 0.62, 0.92, 1.0, 0.0)
    edge = g.mul(edge, g.smooth(layer, 0.3, 0.62))
    c = g.mix(edge, c, rgb(0.07, 0.045, 0.03))
    near = g.smooth(g.sub(1.0, g.ao(0.09)), 0.1, 0.8)
    c = g.mix(g.mul(near, 0.6), c, rgb(0.14, 0.13, 0.115), "MULTIPLY")
    c, r = g.dust_layer(c, r, 0.42)
    n = g.bump(g.add(g.mul(h0, 0.00012), g.mul(dd, 0.00005)), 1.0, normal=g.bevel_normal(0.0008, 4))
    return g.finish(g.principled(c, r, 0.45, normal=n))


def rust_metal(scale=3.3):
    """錆びた鉄。灰色の塗膜が残る鉄の写真。オレンジ一色にしない。"""
    g = G("rust")
    p = g.pos
    warp = g.noise(p, 9.0, 4.0, 0.55).outputs["Color"]
    v = g.mapping(g.vec_add(p, warp, 0.008), scale=(scale, scale, scale), loc=(0.2, 0.33, 0.12))
    im = g.image("rust", v, box=True)
    h0, layer, dd = g.split(g.image("rust_s", v, srgb=False, box=True).outputs[0])
    c = g.tint(im.outputs[0], 0.86, RUST_VALUE)
    c, r0 = g.dust_layer(c, g.add(0.62, g.mul(g.sub(1.0, layer), 0.3)), 0.8)
    return g.finish(g.principled(c, r0, 0.34, normal=g.bump(g.add(g.mul(h0, 0.0016), g.mul(dd, 0.0003)), 1.0)))


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
    v = g.mapping(p, scale=(5.0, 5.0, 5.0), loc=(0.15, 0.2, 0.5))
    h0, layer, dd = g.split(g.image("steel_s", v, srgb=False, box=True).outputs[0])
    c = g.mix(g.gray(dd), rgb(0.009, 0.009, 0.01), rgb(0.036, 0.036, 0.036))
    edge = g.smooth(g.ao(0.004, inside=True), 0.62, 0.92, 1.0, 0.0)
    c = g.mix(g.mul(edge, 0.7), c, rgb(0.12, 0.12, 0.115))
    r = g.add(0.42, g.mul(layer, 0.24))
    r = g.mixf(edge, r, 0.72)
    c, r = g.dust_layer(c, r, 0.55)
    return g.finish(g.principled(c, r, 0.45, normal=g.bump(g.add(g.mul(h0, 0.00008), g.mul(dd, 0.00004)), 1.0, normal=g.bevel_normal(0.0012, 4))))


def cardboard():
    """古い段ボール。UV の -1 は印刷なし（box_print は範囲の外が黒 = インク無し）。"""
    g = G("cardboard")
    p = g.pos
    v = g.mapping(p, scale=(2.5, 2.5, 2.5), loc=(0.2, 0.4, 0.1))
    im = g.image("cardboard", v, box=True)
    h0, layer, dd = g.split(g.image("cardboard_s", v, srgb=False, box=True).outputs[0])
    c = g.tint(im.outputs[0], 0.78, CARD_VALUE)
    ink = g.image("box_print", g.tc.outputs["UV"], srgb=False, extend="CLIP")
    c = g.mix(g.mul(g.lum(ink.outputs[0]), 0.86), c, rgb(0.03, 0.04, 0.06))
    edge = g.smooth(g.ao(0.008, inside=True), 0.66, 0.94, 1.0, 0.0)
    edge = g.mul(edge, g.smooth(layer, 0.26, 0.62))
    c = g.mix(g.mul(edge, 0.72), c, rgb(0.34, 0.31, 0.26))
    near = g.smooth(g.sub(1.0, g.ao(0.12)), 0.08, 0.7)
    c = g.mix(g.mul(near, 0.62), c, rgb(0.16, 0.14, 0.11), "MULTIPLY")
    c, r = g.dust_layer(c, g.add(0.74, g.mul(g.sub(dd, 0.5), 0.3)), 0.5)
    return g.finish(g.principled(c, r, 0.24, normal=g.bump(g.add(g.mul(h0, 0.0007), g.mul(dd, 0.00016)), 1.0, normal=g.bevel_normal(0.0025, 4))))


def tin_can():
    """ペンキ缶の胴。ブリキの地に、上から垂れて乾いた塗料と、底のふちの錆。座標は Generated（缶ごとに 0〜1）。"""
    g = G("tin_can")
    p = g.pos
    gen = g.tc.outputs["Generated"]
    v = g.mapping(p, scale=(5.0, 5.0, 5.0), loc=(0.4, 0.1, 0.3))
    h0, layer, dd = g.split(g.image("steel_s", v, srgb=False, box=True).outputs[0])
    _, _, gz = g.xyz(gen)
    r = g.add(0.34, g.mul(layer, 0.28))
    tin = g.mix(g.gray(dd), rgb(0.26, 0.26, 0.25), rgb(0.5, 0.5, 0.48))
    # 垂れは 1 缶に 2〜3 本。幅も長さも揃えない（等間隔に並ぶと柵に見える）
    drip_a = g.noise(g.mapping(p, scale=(13, 13, 0.7)), 2.0, 2.0, 0.45).outputs["Fac"]
    drip_b = g.noise(g.mapping(p, scale=(70, 70, 1.8)), 3.0, 3.0, 0.5).outputs["Fac"]
    drip = g.mul(g.smooth(g.add(g.add(drip_a, g.mul(drip_b, 0.18)), g.mul(gz, 0.2)), 0.82, 0.86), g.smooth(gz, 0.82, 0.97, 1.0, 1.0))
    base = g.smooth(gz, 0.0, 0.1, 1.0, 0.0)
    rust_v = g.mapping(p, scale=(5.0, 5.0, 5.0))
    rust = g.image("rust", rust_v, box=True).outputs[0]
    rust_k = g.mul(base, g.smooth(layer, 0.28, 0.6))
    metal = g.sub(1.0, g.math("MAXIMUM", drip, rust_k))
    c = g.mix(rust_k, tin, g.tint(rust, 0.86, RUST_VALUE))
    c = g.mix(drip, c, rgb(0.46, 0.45, 0.41))
    r = g.mixf(drip, r, 0.6)
    near = g.smooth(g.sub(1.0, g.ao(0.1)), 0.1, 0.8)
    c = g.mix(g.mul(near, 0.5), c, rgb(0.18, 0.17, 0.15), "MULTIPLY")
    c, r = g.dust_layer(c, r, 0.9)
    return g.finish(g.principled(c, r, 0.5, metal=metal, normal=g.bump(g.add(g.mul(h0, 0.0003), g.mul(drip, 0.0007)), 1.0, normal=g.bevel_normal(0.001, 4))))


def paper_label(name):
    """缶に巻いた紙ラベル。"""
    g = G("label_" + name)
    im = g.image(name, g.tc.outputs["UV"], extend="EXTEND")
    h0 = g.noise(g.pos, 90.0, 6.0, 0.62).outputs["Fac"]
    # 紙は焼けて色が抜け、水を吸った染みが残る
    warp = g.noise(g.pos, 18.0, 5.0, 0.6).outputs["Color"]
    st = g.noise(g.vec_add(g.pos, warp, 0.012), 16.0, 6.0, 0.6).outputs["Fac"]
    col = g.tint(im.outputs[0], 0.58, 0.6)
    col = g.mix(g.mul(g.smooth(st, 0.5, 0.62), 0.6), col, rgb(0.5, 0.42, 0.3), "MULTIPLY")
    c, r = g.dust_layer(col, 0.72, 0.8)
    return g.finish(g.principled(c, r, 0.3, normal=g.bump(h0, 0.0004)))


def packing_tape():
    g = G("tape")
    n = g.noise(g.mapping(g.pos, scale=(1, 26, 1)), 8.0, 4.0, 0.55).outputs["Fac"]
    c = g.mix(g.smooth(n, 0.35, 0.65), rgb(0.13, 0.085, 0.045), rgb(0.21, 0.145, 0.08))
    c, r = g.dust_layer(c, g.add(0.26, g.mul(n, 0.26)), 1.0)
    return g.finish(g.principled(c, r, 0.5, normal=g.bump(n, 0.0005)))


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
