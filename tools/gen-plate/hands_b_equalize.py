"""1 周目 B の手形の比の画を、壁の明るさに合わせて場所ごとに濃くする（0402・2026-11-18）。

掛け算の手形は暗くすることしかできないので、照り返しの所と影の所で埋もれる。
40px の升ごとに、写真といまの配信の平均の上で「手形と地の差」が TARGET（sRGB 0-255）に届く
指数 k（比 ** k・上限 KMAX）を求め、ぼかして掛ける。地の明るさは 1-B のカットの露出で測る。

  py -3.11 tools/gen-plate/hands_b_equalize.py <露出> <TARGET> <tag> <KMAX> <いまの配信の 1 コマ.jpg>
  2026-11-18 の採用: -1.2 34 c 2.2（出力 hands_B_ratio_c.png → static-inputs/hands_B_ratio_20261118.png）
出力と比較の画は OUT（既定は環境変数 TEMP）へ書く。
"""
import os, sys, json, numpy as np
from PIL import Image, ImageFilter
sys.stdout.reconfigure(encoding="utf-8")
S = os.environ.get("OUT", os.environ.get("TEMP", "."))
W = r"C:\Users\kouga\Projects\Unity\fixed-cam-vr\tools\web-compositor"
EXP_OLD = 2 ** -1.75
EXP_NEW = 2 ** float(sys.argv[1]) if len(sys.argv) > 1 else EXP_OLD
TARGET = float(sys.argv[2]) if len(sys.argv) > 2 else 34.0
TILE = 40
KMAX = float(sys.argv[4]) if len(sys.argv) > 4 else 3.0

def lin(c): return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)
def srgb(c):
    c = np.clip(c, 0, 1); return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)
def L(a): return (a * [0.2126, 0.7152, 0.0722]).sum(-1)
def load(p): return np.asarray(Image.open(p).convert("RGB"), dtype=float) / 255

plate = load(W + r"\captures\plate_B_20261117_145119_e8a7f00d.jpg")
live = load(sys.argv[5] if len(sys.argv) > 5 else S + r"\camB_live.jpg")
r = load(W + r"\static-inputs\hands_B_ratio_20261117.png")
m = np.asarray(Image.open(W + r"\masks\cue_hands_B_solid_20261117.png").convert("L")
               .crop((80, 0, 560, 360)).resize((640, 480), Image.BILINEAR), dtype=float) / 255
rl = L(r)
H, Wd = rl.shape

def diff(bg_lin, ratio, exp, sel):
    rr = L(ratio)[sel]; o = L(srgb(bg_lin[sel] * (1 + (ratio[sel] - 1) * m[sel][:, None]) * exp))
    if rr.size < 200: return None
    pr = rr < np.percentile(rr, 15); cl = rr > np.percentile(rr, 85)
    return (o[cl].mean() - o[pr].mean()) * 255

def bands(ratio, exp, label):
    out = {}
    for name, bg in (("写真", plate), ("いま", live)):
        bl = lin(bg); vals = []
        for y0 in range(0, 480, 60):
            sel = (m > 0.5); sel[:y0] = False; sel[y0 + 60:] = False
            d = diff(bl, ratio, exp, sel)
            if d is not None: vals.append(round(d, 1))
        out[name] = vals
    print(label, json.dumps(out, ensure_ascii=False))
    return out

before = bands(r, EXP_OLD, "前")

# 背景は写真といまの平均（照り返しの位置が少し動くので）
bg = (lin(plate) + lin(live)) / 2
k = np.ones((H, Wd))
ky = list(range(0, H, TILE)); kx = list(range(0, Wd, TILE))
kgrid = np.ones((len(ky), len(kx)))
for i, y0 in enumerate(ky):
    for j, x0 in enumerate(kx):
        sel = np.zeros((H, Wd), bool); sel[y0:y0 + TILE, x0:x0 + TILE] = True; sel &= m > 0.5
        if sel.sum() < 300: continue
        lo, hi = 1.0, KMAX
        d1 = diff(bg, r, EXP_NEW, sel)
        if d1 is None or d1 >= TARGET: continue
        for _ in range(14):
            mid = (lo + hi) / 2
            d = diff(bg, np.clip(r, 1e-4, 1) ** mid, EXP_NEW, sel)
            if d < TARGET: lo = mid
            else: hi = mid
        kgrid[i, j] = hi
kimg = Image.fromarray((kgrid / 3 * 255).astype(np.uint8)).resize((Wd, H), Image.BICUBIC).filter(ImageFilter.GaussianBlur(25))
k = np.clip(np.asarray(kimg, dtype=float) / 255 * 3, 1.0, KMAX)
k = 1 + (k - 1) * np.clip(m * 1.2, 0, 1)
r2 = np.clip(r, 1e-4, 1) ** k[..., None]
print("k", round(k[m > 0.5].min(), 2), round(float(np.median(k[m > 0.5])), 2), round(k[m > 0.5].max(), 2))
after = bands(r2, EXP_NEW, "後")
tag = sys.argv[3] if len(sys.argv) > 3 else "x"
Image.fromarray((r2 * 255 + 0.5).astype(np.uint8)).save(S + rf"\hands_B_ratio_{tag}.png")

def render(bgimg, ratio, exp):
    o = srgb(lin(bgimg) * (1 + (ratio - 1) * m[..., None]) * exp)
    return Image.fromarray((o * 255).astype(np.uint8))
row = Image.new("RGB", (640 * 4, 480 + 30), (20, 20, 20))
from PIL import ImageDraw, ImageFont
f = ImageFont.truetype(r"C:\Windows\Fonts\YuGothM.ttc", 20)
dr = ImageDraw.Draw(row)
for n, (lab, im) in enumerate((("いまの B・今の手形", render(live, r, EXP_OLD)), (f"いまの B・新しい手形 exp {np.log2(EXP_NEW):.2f}", render(live, r2, EXP_NEW)),
                              ("写真の B・今の手形", render(plate, r, EXP_OLD)), ("写真の B・新しい手形", render(plate, r2, EXP_NEW)))):
    row.paste(im, (n * 640, 30)); dr.text((n * 640 + 8, 4), lab, font=f, fill=(235, 230, 220))
row.save(S + rf"\hands_compare_{tag}.png")
