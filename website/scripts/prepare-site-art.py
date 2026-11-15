# 生成した原本（幕・壁・監視カメラ・髪・染みの PNG 5 枚）から、配信用の素材（assets/*.webp）を作る。
# 使い方: py -3.11 scripts/prepare-site-art.py <原本のフォルダ>
# 原本の置き場と生成条件は assets/site-art-prompt.md。
import os, sys
from PIL import Image, ImageDraw, ImageFilter
import numpy as np

if len(sys.argv) != 2:
    sys.exit("usage: prepare-site-art.py <folder with curtain.png wall.png camera.png hair.png stain.png>")
RAW = sys.argv[1].rstrip("/\\") + "/"
A = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "assets") + "/"
missing = [n for n in ["curtain", "wall", "camera", "hair", "stain"] if not os.path.isfile(RAW + n + ".png")]
if missing:
    sys.exit("missing source images: " + ", ".join(missing))

def bg_mask(rgb, thresh=236):
    """縁につながった白地だけを背景とする（内側の白い反射は残す）。"""
    mn = rgb.min(axis=2)
    cand = Image.fromarray(((mn >= thresh) * 255).astype(np.uint8), "L")
    w, h = cand.size
    pad = Image.new("L", (w + 2, h + 2), 255)
    pad.paste(cand, (1, 1))
    ImageDraw.floodfill(pad, (0, 0), 128, thresh=0)
    return np.asarray(pad.crop((1, 1, w + 1, h + 1))) == 128

def cutout(rgb, soft_lo=0.50, soft_hi=0.955, reach=3, enclosed=None, enclosed_thresh=240):
    """白地を抜く。背景から reach px 以内だけ白さで半透明にし、白を引いて色を戻す。
    enclosed は、縁につながらない白（レールと幕の間、ケーブルと腕の間）も背景にする範囲。"""
    bg = bg_mask(rgb)
    if enclosed is not None:
        bg = bg | ((rgb.min(axis=2) >= enclosed_thresh) & enclosed)
    near = np.asarray(Image.fromarray((bg * 255).astype(np.uint8)).filter(ImageFilter.MaxFilter(2 * reach + 1))) > 0
    white = rgb.min(axis=2) / 255.0
    a = np.ones(white.shape, np.float32)
    edge = np.clip((soft_hi - white) / (soft_hi - soft_lo), 0, 1)
    a[near] = edge[near]
    a[bg] = 0
    col = (rgb - (1 - a[..., None]) * 255.0) / np.maximum(a[..., None], 1e-3)
    col = np.clip(col, 0, 255)
    return np.dstack([col, a * 255]).astype(np.uint8)

def grade_curtain(rgb):
    """生成した幕は橙が強い。room-empty.webp の明部 (91,67,48) に寄せる。"""
    lum = rgb @ np.array([0.299, 0.587, 0.114], np.float32)
    out = lum[..., None] + (rgb - lum[..., None]) * 0.62
    out = out * np.array([0.90, 1.0, 1.08], np.float32)
    return np.clip(out, 0, 255)

# ── 幕：レール / ひだ / 裾 ──
src = np.asarray(Image.open(RAW + "curtain.png").convert("RGB")).astype(np.float32)
band = np.zeros(src.shape[:2], bool); band[:96] = True   # レールと幕の上端の間
# 隙間には幕の影で灰色になった白も混じる。棒・輪・布は 100 より暗いので、150 以上を全部抜く
# （抜き残すと、裏が暗い所で灰色の帯になって見える）
rgba = cutout(src, enclosed=band, enclosed_thresh=150)
rgba = np.dstack([grade_curtain(rgba[..., :3].astype(np.float32)), rgba[..., 3]]).astype(np.uint8)
im = Image.fromarray(rgba, "RGBA")
TOP, MID0, MID1, HEM1 = 0, 240, 690, 880
im.crop((0, TOP, 1536, MID0)).save(A + "drape-rail.webp", quality=82, method=6)
im.crop((0, MID0, 1536, MID1)).convert("RGB").save(A + "drape-folds.webp", quality=80, method=6)
im.crop((0, MID1, 1536, HEM1)).save(A + "drape-hem.webp", quality=82, method=6)
print("drape", MID0 - TOP, MID1 - MID0, HEM1 - MID1)

# ── 壁：上下を鏡で継いで縦に繰り返せるようにする ──
wall = Image.open(RAW + "wall.png").convert("RGB")
tile = Image.new("RGB", (1536, 2048))
tile.paste(wall, (0, 0)); tile.paste(wall.transpose(Image.FLIP_TOP_BOTTOM), (0, 1024))
tile.save(A + "wall.webp", quality=72, method=6)

# ── 監視カメラ ──
# 影は絵に焼き込む（CSS の drop-shadow を掛けると、絵の箱の形に薄い影が出た）。
cam = np.asarray(Image.open(RAW + "camera.png").convert("RGB")).astype(np.float32)
c = Image.fromarray(cutout(cam, reach=2, enclosed=np.ones(cam.shape[:2], bool)), "RGBA")
# 地に残った点（生成画像の塵）を落とす
al = c.getchannel("A")
keep = np.asarray(al.filter(ImageFilter.MedianFilter(9))) > 0
c.putalpha(Image.fromarray((np.asarray(al) * keep).astype(np.uint8)))
c = c.resize((840, 840), Image.LANCZOS)
canvas = Image.new("RGBA", (900, 900), (0, 0, 0, 0))
sh = Image.new("L", (900, 900), 0)
sh.paste(c.getchannel("A"), (14, 22))
sh = sh.filter(ImageFilter.GaussianBlur(15)).point(lambda v: int(v * 0.46))
canvas.paste(Image.new("RGBA", (900, 900), (30, 18, 6, 255)), (0, 0), sh)
canvas.alpha_composite(c, (0, 0))
canvas.save(A + "cctv.webp", quality=86, method=6)

# ── 髪：墨 1 色。濃さをそのまま不透明度にする ──
hair = np.asarray(Image.open(RAW + "hair.png").convert("L")).astype(np.float32) / 255.0
a = np.clip((0.97 - hair) / 0.9, 0, 1)
ink = np.zeros(hair.shape + (4,), np.uint8)
ink[..., 0], ink[..., 1], ink[..., 2] = 10, 8, 7
ink[..., 3] = (a * 255).astype(np.uint8)
Image.fromarray(ink, "RGBA").save(A + "hair.webp", quality=84, method=6)

# ── 染み：乗算で重ねるので、地を完全な白に揃える ──
st = np.asarray(Image.open(RAW + "stain.png").convert("RGB")).astype(np.float32)
st = np.clip(st * (255.0 / 251.0), 0, 255).astype(np.uint8)
Image.fromarray(st, "RGB").save(A + "stain.webp", quality=74, method=6)

for f in ["drape-rail", "drape-folds", "drape-hem", "wall", "cctv", "hair", "stain"]:
    p = A + f + ".webp"; i = Image.open(p)
    print(f, i.size, i.mode, os.path.getsize(p) // 1024, "KB")
