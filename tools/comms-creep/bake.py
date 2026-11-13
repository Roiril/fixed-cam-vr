# -*- coding: utf-8 -*-
"""連絡の面の③a / ③b で、面の縁から這い込む黒髪の「到達順」の版を焼く。

入力: tools/comms-creep/src/creep-src.png（白地に黒の髪のシルエット。Codex に描かせた原画。下端が根元）
出力:
  Assets/Resources/CommsFx/CurseCreep.png        R = 到達順 0..1 / G = 房の芯までの深さ 0..1 / B = シルエット
  Assets/Resources/CommsFx/CurseCreepField.bytes 到達順だけを 256x128 の 8bit で（C# が赤い字を数えるのに読む）

版は [原画 | 左右反転] を横に並べてあるので、u を Repeat で引いても継ぎ目が出ない。
v は下端（根元）が 0。シェーダの式は Assets/Art/Shaders/Streaming/CommsRecovery.hlsl、
C# の写しは Assets/Scripts/Streaming/CommsCreepLogic.cs。--preview はその式を numpy で写した確認用。

  py -3.11 tools/comms-creep/bake.py            # 焼く
  py -3.11 tools/comms-creep/bake.py --preview <出力フォルダ>
"""
import argparse
import os
import sys

import cv2
import numpy as np

sys.stdout.reconfigure(encoding="utf-8")
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
SRC = os.path.join(HERE, "src", "creep-src.png")
OUT_PNG = os.path.join(ROOT, "Assets", "Resources", "CommsFx", "CurseCreep.png")
OUT_BYTES = os.path.join(ROOT, "Assets", "Resources", "CommsFx", "CurseCreepField.bytes")

HALF_W, TEX_H = 512, 512          # 版の半分（原画 1 枚ぶん）の画素
FIELD_W, FIELD_H = 256, 128       # C# が読む版
SPREAD_RATIO = 9.0               # 房の外は、房の中の何倍遅く広がるか
CORE_PX = 9.0                     # 房の縁から芯までをこの画素数で 0..1 にする


def imread(path):
    data = np.fromfile(path, dtype=np.uint8)
    img = cv2.imdecode(data, cv2.IMREAD_GRAYSCALE)
    if img is None:
        raise SystemExit(f"読めない: {path}")
    return img


def imwrite(path, img):
    ok, buf = cv2.imencode(".png", img)
    if not ok:
        raise SystemExit(f"PNG にできない: {path}")
    tmp = path + ".tmp"
    with open(tmp, "wb") as f:
        f.write(buf.tobytes())
    os.replace(tmp, path)


def arrival(mask):
    """下端の行から広がる到達順。房の中は速く（道のり 1 画素 = 1）、房の外は遅い（墨のにじみ）。

    房の中だけで測地距離を取って外へ貼り足すと、いちばん近い房が切り替わる所に段差の線が出る
    （最初の版で縦の線として見えた）。全体を 1 つの伝播で解けば連続になる。
    """
    h, w = mask.shape
    reachable = mask.copy()
    slow = np.float32(SPREAD_RATIO)
    cost = np.where(mask, np.float32(1.0), slow)
    d = np.full((h, w), np.float32(1e9))
    d[h - 1, :] = np.where(mask[h - 1], 0.0, 1e9)
    r2 = np.float32(np.sqrt(2.0))
    for it in range(20000):
        p = np.pad(d, 1, constant_values=1e9)
        n = np.minimum.reduce([
            p[0:-2, 1:-1] + cost, p[2:, 1:-1] + cost, p[1:-1, 0:-2] + cost, p[1:-1, 2:] + cost,
            p[0:-2, 0:-2] + cost * r2, p[0:-2, 2:] + cost * r2,
            p[2:, 0:-2] + cost * r2, p[2:, 2:] + cost * r2,
        ])
        n = np.minimum(d, n)
        if it % 32 == 31 and np.array_equal(n, d):
            break
        d = n
    return d


def build():
    src = imread(SRC)
    work = cv2.resize(src, (768, 512), interpolation=cv2.INTER_AREA)
    mask = work < 128
    d = arrival(mask)
    # 1 にするのは「いちばん高い房の先」。房の外はそれより遅れて 1 を超える（切り捨てる）。
    top = float(d[mask].max())
    t = np.clip(d / top, 0.0, 1.0).astype(np.float32)
    t = cv2.GaussianBlur(t, (0, 0), 0.6)

    inside = cv2.distanceTransform(np.where(mask, 255, 0).astype(np.uint8), cv2.DIST_L2, 5)
    core = np.clip(inside / CORE_PX, 0.0, 1.0)
    sil = cv2.GaussianBlur(mask.astype(np.float32), (0, 0), 0.7)

    def half(a):
        return cv2.resize(a, (HALF_W, TEX_H), interpolation=cv2.INTER_AREA)

    def tile(a):
        a = half(a)
        return np.concatenate([a, a[:, ::-1]], axis=1)

    return tile(t), tile(core), tile(sil)


def bake():
    t, core, sil = build()
    os.makedirs(os.path.dirname(OUT_PNG), exist_ok=True)
    rgb = np.stack([sil, core, t], axis=-1)          # OpenCV は BGR: B=sil, G=core, R=t
    imwrite(OUT_PNG, np.round(rgb * 255).astype(np.uint8))
    field = cv2.resize(t, (FIELD_W, FIELD_H), interpolation=cv2.INTER_AREA)
    # 行 0 が v = 0（根元）になるよう上下を返す。
    raw = np.round(field[::-1] * 255).astype(np.uint8).tobytes()
    tmp = OUT_BYTES + ".tmp"
    with open(tmp, "wb") as f:
        f.write(raw)
    os.replace(tmp, OUT_BYTES)
    print(f"焼いた: {OUT_PNG} {t.shape[1]}x{t.shape[0]} / {OUT_BYTES} {len(raw)} bytes")
    print(f"到達順の分布: 10%={np.quantile(t, .1):.2f} 50%={np.quantile(t, .5):.2f} 90%={np.quantile(t, .9):.2f}")


# ---- 以下は確認用。式は CommsRecovery.hlsl / CommsCreepLogic.cs と同じ値で書く ----
TILE_M = 0.40      # 版の半分が辺に沿って占める長さ (m)
REACH_M = 0.24     # 版の高さが辺から内へ占める長さ (m)
ROOT_V = 0.12      # 版の中で根元の塊が終わる高さ
# 辺ごとの (reach 0 のときの根元の位置 m, reach 1 あたりの進み m)。負は面の外。
SIDES = {"left": (-0.20, 0.44), "right": (-0.24, 0.42), "top": (-0.20, 0.40)}
U_OFF = {"left": 0.07, "right": 0.57, "top": 0.31, "bottom": 0.83}
THETA_MAX = 0.98
SEAM_M = 0.0016    # 熾の線の芯の太さ (m)


def smooth(a, b, x):
    x = np.clip((x - a) / (b - a), 0, 1)
    return x * x * (3 - 2 * x)


def theta(r):
    return THETA_MAX * smooth(0.0, 0.85, r)


def sample(tex, u, v):
    h, w = tex.shape
    x = (u % 1.0) * w - 0.5
    y = (1.0 - np.clip(v, 0, 1)) * h - 0.5
    val = cv2.remap(tex, x.astype(np.float32), y.astype(np.float32), cv2.INTER_LINEAR,
                    borderMode=cv2.BORDER_WRAP)
    return val


def creep(tex_t, tex_core, tex_sil, px, py, rect, r, sec=0.0):
    """面のローカル座標 (m) → (前線からの深さ f（正が呪いの側）, 房の芯, 房そのもの)。"""
    cx, cy, w, h = rect
    left, right, bottom, topy = cx - w / 2, cx + w / 2, cy - h / 2, cy + h / 2
    swallow = smooth(0.72, 1.0, r)
    th = theta(r)
    best_f = np.full(px.shape, -9.0, dtype=np.float32)
    best_core = np.zeros(px.shape, dtype=np.float32)
    best_sil = np.zeros(px.shape, dtype=np.float32)
    for name, d, along, span in (
        ("left", px - left, py - bottom, w), ("right", right - px, py - bottom, w),
        ("top", topy - py, px - left, h),
    ):
        a0, a1 = SIDES[name]
        adv = a0 + a1 * r + swallow * span
        v = (d - adv) / REACH_M + ROOT_V
        u = along / (2 * TILE_M) + U_OFF[name]
        u = u + 0.006 * np.clip(v, 0, 1) * np.sin(sec * 0.7 + u * 40.0)
        f = th - sample(tex_t, u, v)
        take = f > best_f
        best_core = np.where(take, sample(tex_core, u, v), best_core)
        best_sil = np.where(take, np.where(v < 0, 1.0, sample(tex_sil, u, v)), best_sil)
        best_f = np.maximum(best_f, f)
    return best_f, best_core, best_sil


def render(t, core, sil, r, w=0.955, h=0.20, ppm=1400.0, sec=0.0):
    margin = 0.05
    xs = np.arange(-(w / 2 + margin), w / 2 + margin, 1 / ppm)
    ys = np.arange(h / 2 + margin, -(h / 2 + margin), -1 / ppm)
    px, py = np.meshgrid(xs.astype(np.float32), ys.astype(np.float32))
    inside = ((np.abs(px) <= w / 2) & (np.abs(py) <= h / 2)).astype(np.float32)
    f, c, s = creep(t, core, sil, px, py, (0, 0, w, h), r, sec)
    # f の勾配で画素幅へ直す（シェーダの fwidth と同じ役）
    gy, gx = np.gradient(f)
    fw = np.maximum(np.abs(gx) + np.abs(gy), 1e-5)
    k = np.clip(f / fw + 0.5, 0, 1) * inside

    rng = np.random.default_rng(3)
    bg = np.full(px.shape + (3,), 0.16, dtype=np.float32) + rng.normal(0, 0.02, px.shape + (1,)).astype(np.float32)
    plate = np.array([0.031, 0.035, 0.039], dtype=np.float32)        # BGR
    img = bg * (1 - 0.72 * inside[..., None]) + plate * 0.72 * inside[..., None]
    ivory = np.array([0.72, 0.78, 0.82], dtype=np.float32)
    ember = np.array([0.07, 0.20, 0.95], dtype=np.float32)
    lie = np.array([0.06, 0.13, 0.80], dtype=np.float32)
    # 顔の枠と文字の行の当たり
    face = ((np.abs(px + w / 2 - 0.085) < 0.075) & (np.abs(py) < 0.075)).astype(np.float32)
    ring = face * (np.abs(np.hypot(px + w / 2 - 0.085, py) - 0.045) < 0.004)
    text = np.zeros(px.shape, dtype=np.float32)
    for i, ly in enumerate((0.055, 0.0, -0.055)):
        line = (px > -w / 2 + 0.21) & (px < w / 2 - 0.12 - 0.1 * i) & (np.abs(py - ly) < 0.016)
        text = np.maximum(text, line * ((px * 60) % 1.0 < 0.72))
    glyph = np.maximum(ring, text)

    # 呪いの側: 房は黒、にじみは暗い朱。前線に近いほど朱が透ける。
    depth = np.clip(f, 0, 1)
    stain = np.array([0.006, 0.010, 0.085], dtype=np.float32) * np.exp(-depth / 0.10)[..., None]
    hair = np.array([0.004, 0.004, 0.006], dtype=np.float32) + np.array([0.004, 0.012, 0.11], dtype=np.float32) \
        * ((1 - c) ** 2)[..., None] * 0.55
    cursed = stain + (hair - stain) * s[..., None]
    a_c = 0.95
    cur_img = bg * (1 - a_c) + cursed * a_c
    img = img + (cur_img - img) * k[..., None]
    col = ivory[None, None] + (lie - ivory)[None, None] * k[..., None]
    img = img + (col - img) * glyph[..., None]
    seam_px = np.abs(f) / fw
    core_line = np.exp(-(seam_px / (SEAM_M * ppm)) ** 2) * inside
    halo = np.exp(-seam_px / (SEAM_M * ppm * 2.5)) * inside * 0.3
    img = img + (np.array([0.66, 0.86, 1.0], dtype=np.float32) - img) * core_line[..., None] * 0.9
    img = img + ember * halo[..., None] * (1 - core_line[..., None])
    out = np.clip(img, 0, 1) ** (1 / 2.2)
    cover = float(k[inside > 0].mean())
    return np.round(out * 255).astype(np.uint8), cover


def preview(out_dir):
    t, core, sil = build()
    os.makedirs(out_dir, exist_ok=True)
    rows = []
    for r in (0.92, 0.72, 0.6, 0.52, 0.46, 0.38, 0.22):
        img, cover = render(t, core, sil, r)
        cv2.putText(img, f"reach {r:.2f} cover {cover:.2f}", (8, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.6,
                    (210, 210, 210), 1)
        rows.append(img)
    imwrite(os.path.join(out_dir, "creep-preview.png"), np.concatenate(rows, axis=0))
    small, _ = render(t, core, sil, 0.46, ppm=667.0)
    imwrite(os.path.join(out_dir, "creep-preview-device.png"), small)
    print("確認用:", os.path.join(out_dir, "creep-preview.png"))


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--preview")
    a = ap.parse_args()
    if a.preview:
        preview(a.preview)
    else:
        bake()
