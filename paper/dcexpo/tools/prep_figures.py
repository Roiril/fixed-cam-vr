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


# ---- 1. ユーザーが作った図 1・図 4（生成した設営イメージの写真＋説明線・960x540）-------------------
# 図1 = 設営を斜めから見た写真、図4 = 真上から見た周回経路（区間1〜3・カメラA〜C）。そのまま使う（加工・再描画しない）。
# 元の Word（v1.6.5.2）の図は現行の構成と合わないので使わない。README 参照。
for name, src in (("env-fig1.png", "C:/Users/kouga/Downloads/無題のプレゼンテーション.png"),
                  ("route-fig4.png", "C:/Users/kouga/Downloads/無題のプレゼンテーション (2).png")):
    sp = Path(src)
    if sp.exists():
        (OUT / name).write_bytes(sp.read_bytes())
        SOURCES[name] = {"src": str(sp), "size": list(Image.open(OUT / name).size),
                         "note": "ユーザー作成の図（生成画像の写真＋説明線）"}
    else:
        print(f"{name}: 元ファイルが無いので複製をスキップ（既存の {name} を使う）")

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
# 体験者の視界（HMD）はユーザー指定の画像（Codex 生成・1448x1086）を使う。周囲の黒い余白だけ除く。
HMD_VIEW = Path("C:/Users/kouga/.codex/generated_images/01a0f140-a3ac-7390-9ca5-7f63a803aaaf/exec-aee560f7-7692-424e-b407-691b171fb769.png")
if HMD_VIEW.exists():
    save("sh-fixed-hmd.jpg", hmd_crop(Image.open(HMD_VIEW), pad=12), str(HMD_VIEW), "図2 右。体験者の視界（ユーザー指定の画像・黒余白を除去）")
else:
    print("図2 の HMD 画像が無いので複製をスキップ（既存の sh-fixed-hmd.jpg を使う）")
for tag, n in (("stain", "005"), ("pov", "009"), ("dolls", "015"), ("lap1", "003"), ("lap3", "011")):
    d = shot(n)
    save(f"sh-{tag}-screen.jpg", crop169(Image.open(d / "1_screen.png")), f"logs/shots/.../{d.name}/1_screen.png", "スクリーンの最終合成")

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
