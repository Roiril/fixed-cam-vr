"""図の素材を figures/ へ用意する（出所を SOURCES に残す。再実行しても同じ結果になる）。

    py -3.11 paper/dcexpo/tools/prep_figures.py

- 画像は PNG/RGBA を白で平坦化して JPEG（q92）へ。PDF が肥大しないため。
- 引き伸ばし・アスペクト比の変更はしない。切り出しは黒帯（ピラーボックス）の除去のみ。
- Quest の録画からのコマ抜きは ffmpeg を使う（PATH に無ければスキップし、既存ファイルを残す）。
"""
import json
import shutil
import subprocess
import sys
from pathlib import Path

from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parents[1]          # paper/dcexpo
REPO = ROOT.parents[1]                              # リポジトリ直下
OUT = ROOT / "figures"
OUT.mkdir(exist_ok=True)
ORIG = Path(r"G:\マイドライブ\研究\IVRC2026\IVRC2026_Roiril_v1.6.5.2.docx")
QUEST = REPO / "output/quest-recordings/2026-09-27/quest-alpha/com.roiril.mawarimi-20260927-171959-0.mp4"

SOURCES = {}


def flat(im: Image.Image) -> Image.Image:
    if im.mode in ("RGBA", "LA", "P"):
        im = im.convert("RGBA")
        bg = Image.new("RGB", im.size, (255, 255, 255))
        bg.paste(im, mask=im.split()[3])
        return bg
    return im.convert("RGB")


def save(name: str, im: Image.Image, src: str, note: str = ""):
    im = flat(im)
    im.save(OUT / name, quality=92, optimize=True)
    SOURCES[name] = {"src": src, "size": list(im.size), "note": note}
    print(f"{name:22s} {im.size}  <- {src}")


def crop_box(im, box):
    return im.crop(box)


# ---- 1. 元 Word（v1.6.5.2）の図 -------------------------------------------------
import zipfile
with zipfile.ZipFile(ORIG) as z:
    def orig(n):
        with z.open(f"word/media/{n}") as f:
            return Image.open(f).copy()
    # 元図4 周回経路（元図2・元図1 は現行の構成と合わないので使わない。README 参照）
    save("route.jpg", orig("image10.png"), f"{ORIG.name}:image10.png", "元図4。周回経路とカメラ位置")


def erase_cart_doll(path):
    """元図4（周回経路）の左下に写る「台車の人形」を消す（現行の構成では人形は CG で、台車は無い）。
    床は cv2.inpaint、下に隠れていた矢印の帯は同じ色の多角形で描き直す。座標は 1920x1200 の原図のもの。"""
    import cv2
    import numpy as np
    im = cv2.imread(str(path))
    ox, oy = 250, 560
    x0, y0, x1, y1 = 340, 50, 600, 262                       # 作業領域（ox, oy からの相対）
    reg = im[oy + y0:oy + y1, ox + x0:ox + x1].copy()
    off = np.array([x0, y0], np.float32)
    poly = np.array([(362, 92), (430, 66), (512, 76), (540, 118), (566, 172), (566, 204),
                     (506, 208), (492, 236), (474, 252), (398, 254), (376, 226), (388, 182), (362, 120)], np.float32) - off
    m = np.zeros(reg.shape[:2], np.uint8)
    cv2.fillPoly(m, [poly.astype(np.int32)], 255)
    m = cv2.dilate(m, np.ones((7, 7), np.uint8))
    inp = cv2.inpaint(reg, m, 9, cv2.INPAINT_TELEA)
    band = np.array([(290, 165), (395, 258), (625, -40), (430, -40)], np.float32) - off      # 区間1の矢印の帯
    S = 4
    big = np.zeros((reg.shape[0] * S, reg.shape[1] * S), np.uint8)
    cv2.fillPoly(big, [(band * S).astype(np.int32)], 255)
    bm = cv2.resize(big, (reg.shape[1], reg.shape[0]), interpolation=cv2.INTER_AREA).astype(np.float32) / 255
    a_ = (bm * (cv2.GaussianBlur(m, (3, 3), 0).astype(np.float32) / 255))[..., None]
    cyan = np.array([198, 190, 131], np.float32)             # 矢印の BGR（原図から実測）
    im[oy + y0:oy + y1, ox + x0:ox + x1] = (inp * (1 - a_) + cyan * a_).astype(np.uint8)
    cv2.imwrite(str(path), im, [cv2.IMWRITE_JPEG_QUALITY, 92])
    SOURCES["route.jpg"]["note"] += "。台車の人形を消した（erase_cart_doll）"


erase_cart_doll(OUT / "route.jpg")

# ---- 2. 各カメラの実写プレート（アプリが実際に使っている最新の版・2026-09-24 撮影） ------------
PLATES = {"A": "plate_A_20260924_172209_b8e1f375.jpg",
          "B": "plate_B_20260924_172210_4abb5bae.jpg",
          "C": "plate_C_20260924_172211_a1c7c02f.jpg"}
for cam, fn in PLATES.items():
    src = REPO / "Assets/StreamingAssets/show/assets" / fn
    save(f"plate-{cam}.jpg", Image.open(src).convert("RGB"),
         f"Assets/StreamingAssets/show/assets/{fn}", "640x480。黒帯なし（そのまま）")

# ---- 3. 合成の例（染み・カメラ A） ---------------------------------------------------
cap = REPO / "tools/web-compositor/captures"
gen = REPO / "Logs/gen-plate/stain_A_20260924_mask_v1"
save("comp-plate.jpg", Image.open(REPO / "Assets/StreamingAssets/show/assets" / PLATES["A"]).convert("RGB"),
     f"Assets/StreamingAssets/show/assets/{PLATES['A']}", "実写プレート（カメラA・最新版）")
save("comp-source.jpg", Image.open(cap / "gen_stainA_still_20260924_candidate.png"),
     "tools/web-compositor/captures/gen_stainA_still_20260924_candidate.png", "生成素材（640x480）")
save("comp-mask.jpg", Image.open(gen / "mask.png").convert("RGB").crop((80, 0, 560, 360)),
     "Logs/gen-plate/stain_A_20260924_mask_v1/mask.png", "差し替え領域のマスク")
save("comp-result.jpg", Image.open(gen / "delivered.png").convert("RGB").crop((160, 0, 1120, 720)),
     "Logs/gen-plate/stain_A_20260924_mask_v1/delivered.png", "境界ブレンド・色合わせ・ポストFX 後。1280x720 から 960x720 を切り出し")

# ---- 4. 不正アクセスの演出画面 ------------------------------------------------------
save("warning.jpg", Image.open(REPO / "output/unauthorized-access/hero-v4.png"),
     "output/unauthorized-access/hero-v4.png", "1280x720")

# ---- 5. Quest 実機録画のコマ（2026-09-27・クエストα） -------------------------------
QF = {  # 名前: (秒, 切り出し方)   切り出し方: None=全体 / "auto"=黒でない領域の外接矩形
    "q-intro.jpg": (34, None),
    "q-shatter.jpg": (40, None),
    "q-fixed.jpg": (45, "auto"),
    "q-end.jpg": (207, "auto"),
    "q-lap1.jpg": (66, "auto"),
    "q-lap2.jpg": (93, "auto"),
    "q-lap3.jpg": (132, "auto"),
    "q-final.jpg": (186, "auto"),
}


def autocrop(im, thr=24, pad=16):
    """黒背景の中の映像面を、黒でない画素の外接矩形で切り出す（拡大縮小・変形はしない）。"""
    import numpy as np
    a = np.asarray(im).max(axis=2)
    ys, xs = np.where(a > thr)
    x0, x1, y0, y1 = xs.min(), xs.max(), ys.min(), ys.max()
    return im.crop((max(0, x0 - pad), max(0, y0 - pad), min(im.width, x1 + pad), min(im.height, y1 + pad)))


ff = shutil.which("ffmpeg")
if ff and QUEST.exists():
    tmp = ROOT / "out" / "_frames"
    tmp.mkdir(exist_ok=True)
    for name, (sec, box) in QF.items():
        png = tmp / (name[:-4] + ".png")
        subprocess.run([ff, "-v", "error", "-y", "-ss", str(sec), "-i", str(QUEST), "-frames:v", "1", str(png)], check=True)
        im = Image.open(png).convert("RGB")
        if box == "auto":
            im = autocrop(im)
        save(name, im, f"{QUEST.name} @ {sec}s", "Quest 実機の画面録画から")
else:
    print("ffmpeg または録画が無いので Quest のコマ抜きはスキップ")

(ROOT / "figures" / "SOURCES.json").write_text(json.dumps(SOURCES, ensure_ascii=False, indent=1), encoding="utf-8")
