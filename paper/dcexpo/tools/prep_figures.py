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

# ---- 2. 体験中の撮影（左グリップの撮影機能・2026-09-30・クエストα）-------------------------
# 1 回の押下 = 1 フォルダ: 1_screen（スクリーンの最終合成）/ 2_raw（加工前の生映像）/ 3_layers（合成している層だけ）/
# 3_cg_alpha / 4_hmd（体験者の視界）。同じ瞬間の 4 種が揃うので、合成の前後を並べられる。
SHOTS = REPO / "logs/shots/2G0YC1ZF890864"


def shot(n):
    return next(SHOTS.glob(f"2*_{n}"))


def crop169(im):
    """スクリーンは 16:9 の枠に 4:3 の映像が入る（左右 160px が黒帯）。黒帯だけを除く。"""
    return im.crop((160, 0, 1120, 720))


def hmd_crop(im, pad=24):
    import numpy as np
    a = np.asarray(im.convert("RGB")).max(axis=2)
    ys, xs = np.where(a > 18)
    return im.crop((max(0, xs.min() - pad), max(0, ys.min() - pad), min(im.width, xs.max() + pad), min(im.height, ys.max() + pad)))


for tag, n in (("hand", "003"), ("back", "007"), ("cg", "011")):       # 合成の 3 場面
    d = shot(n)
    save(f"sh-{tag}-raw.jpg", Image.open(d / "2_raw.png"), f"logs/shots/.../{d.name}/2_raw.png", "加工前の生映像 640x480")
    save(f"sh-{tag}-layers.jpg", crop169(Image.open(d / "3_layers.png")), f"logs/shots/.../{d.name}/3_layers.png", "合成している層だけ（ライブは黒）")
    save(f"sh-{tag}-screen.jpg", crop169(Image.open(d / "1_screen.png")), f"logs/shots/.../{d.name}/1_screen.png", "スクリーンの最終合成")
for cam, n in (("A", "011"), ("B", "003"), ("C", "009")):              # 各カメラの生映像（Phone 01 / 02 / 03）
    d = shot(n)
    save(f"cam-{cam}.jpg", Image.open(d / "2_raw.png"), f"logs/shots/.../{d.name}/2_raw.png", f"カメラ{cam}の生映像")
d = shot("002")                                                          # 同じ瞬間の生映像と体験者の視界
save("sh-fixed-raw.jpg", Image.open(d / "2_raw.png"), f"logs/shots/.../{d.name}/2_raw.png", "生映像")
save("sh-fixed-hmd.jpg", hmd_crop(Image.open(d / "4_hmd.png")), f"logs/shots/.../{d.name}/4_hmd.png", "体験者の視界（左眼の投影）")
for tag, n in (("stain", "005"), ("pov", "009"), ("dolls", "015"), ("lap1", "003"), ("lap3", "011")):
    d = shot(n)
    save(f"sh-{tag}-screen.jpg", crop169(Image.open(d / "1_screen.png")), f"logs/shots/.../{d.name}/1_screen.png", "スクリーンの最終合成")

# ---- 3b. 図1（ユーザーが作った図。生成した設営イメージ写真に説明線を付けたもの・960x540） --------------
# 写真は生成画像（Codex）。説明線と文字はユーザーが作成。そのまま使う（画像の加工・再描画はしない）。
ENV_FIG1 = Path("C:/Users/kouga/Downloads/無題のプレゼンテーション.png")
if ENV_FIG1.exists():
    (OUT / "env-fig1.png").write_bytes(ENV_FIG1.read_bytes())
    SOURCES["env-fig1.png"] = {"src": str(ENV_FIG1), "size": list(Image.open(OUT / "env-fig1.png").size),
                               "note": "ユーザー作成の図1。生成画像の写真＋カメラA〜C・HMD・L字の壁の説明線"}
else:
    print("図1 の元ファイルが無いので複製をスキップ（既存の env-fig1.png を使う）")

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
