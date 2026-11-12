"""stage_map.py の測定から、設計書に載せる画を作る。

  floor.png          床の図（区画・歩く道・カメラ・床の線）
  card_<台>.png      台ごとに、区間の前・中・後のコマへ「体の矩形」と「異変を置く所」を重ねた画

使い方:
  py -3.11 tools/twin/stage_cards.py --map <stage_map.py の出力フォルダ> --out <フォルダ>

置く所の座標は reports/2026-11-12_position-driven-redesign.html の案のもの（本番の台本ではない）。
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
import stage_map as sm  # noqa: E402

FONT = "C:/Windows/Fonts/YuGothB.ttc"
W, H = sm.W, sm.H

# 案の床の線（course 座標）。existing は本番の台本にもある線
LINES = [
    ("A", "LA1", (0.30, 0.02, 0.30, 0.50), "new"),
    ("A", "LA2", (-0.05, 0.02, -0.05, 0.50), "new"),
    ("A", "LA3 角", (-0.5, 0.5, 0.9, -0.9), "existing"),
    ("A", "LA4 背", (-0.5, -0.20, 0.0, -0.20), "new"),
    ("B", "LB1", (-0.9, -0.30, -0.5, -0.30), "new"),
    ("B", "LB2", (-0.9, 0.00, -0.5, 0.00), "new"),
    ("B", "LB3", (-0.9, 0.45, -0.5, 0.45), "new"),
    ("C", "LC1", (-0.15, 0.5, -0.15, 0.9), "new"),
    ("C", "LC2", (0.05, 0.5, 0.05, 0.9), "existing"),
    ("C", "LC3", (0.40, 0.5, 0.40, 0.9), "new"),
    ("C", "LC4 画の外", (0.58, 0.5, 0.58, 0.9), "new"),
]

# 異変を置く所（映像の 0〜1）と、どの帯のコマに描くか（0=前 1=中 2=後）
PLACES = {
    "A": [
        (0, (0.215, 0.0, 0.435, 0.445), "2周目 顔のシミ（入り〜角）"),
        (0, (0.20, 0.46, 0.50, 0.97), "最後 人形の群れ（行く手）"),
        (1, (0.215, 0.0, 0.435, 0.445), "角まで来たら幕へ沈む"),
        (2, (0.575, 0.0, 0.795, 0.445), "試した版 右の幕の顔"),
    ],
    "B": [
        (0, (0.0, 0.0, 0.39, 1.0), "1周目 手形（入りから壁いっぱい）"),
        (1, (0.16, 0.30, 0.34, 0.62), "手形が 1 つ擦れて下がる"),
        (1, (0.0, 0.0, 0.0, 0.0), ""),
        (2, (0.30, 0.20, 0.40, 0.62), "2周目 壁の縁の暗がりに目"),
    ],
    "C": [
        (2, (0.02, 0.62, 0.62, 0.98), "ここから先、自分は映らない"),
    ],
}
DOLL_SPOTS = {}
DOLL_H, DOLL_R = 0.40, 0.09


def font(size: int):
    return ImageFont.truetype(FONT, size)


def label(d: ImageDraw.ImageDraw, xy, text, size=15, fill=(255, 255, 255), bg=(0, 0, 0)):
    f = font(size)
    box = d.textbbox(xy, text, font=f)
    d.rectangle((box[0] - 3, box[1] - 2, box[2] + 3, box[3] + 2), fill=bg)
    d.text(xy, text, font=f, fill=fill)


def floor_png(out: Path, show: dict, pack: dict) -> None:
    s, pad = 330, 110                    # px/m と余白
    size = int(1.8 * s + pad * 2)
    img = Image.new("RGB", (size, size), (255, 255, 255))
    d = ImageDraw.Draw(img)

    def P(x, z):
        return (pad + (x + 0.9) * s, pad + (0.9 - z) * s)

    grid = show["layout"]["grid"]
    tint = {0: (244, 244, 244), 1: (228, 228, 228), 2: (212, 212, 212)}
    t = grid["tileM"]
    for r, row in enumerate(grid["cells"]):
        for c, ch in enumerate(row):
            x0, z0 = -0.9 + c * t, 0.9 - r * t
            d.rectangle((*P(x0, z0), *P(x0 + t, z0 - t)), fill=tint[int(ch)])
    d.rectangle((*P(-0.9, 0.9), *P(0.9, -0.9)), outline=(120, 120, 120), width=1)
    for w in show["layout"]["room"]["walls"]:
        d.line((*P(w["x1"], w["z1"]), *P(w["x2"], w["z2"])), fill=(0, 0, 0), width=7)
    pts = [P(x, z) for x, z in zip(pack["xs"][:pack["moveEnd"]], pack["zs"][:pack["moveEnd"]])]
    d.line(pts, fill=(90, 90, 90), width=2)
    for k in range(20, len(pts) - 6, 42):
        (x0, y0), (x1, y1) = pts[k], pts[k + 5]
        a = math.atan2(y1 - y0, x1 - x0)
        if math.hypot(x1 - x0, y1 - y0) < 2:
            continue
        tip = (x1, y1)
        d.polygon([tip, (x1 - 11 * math.cos(a - 0.45), y1 - 11 * math.sin(a - 0.45)),
                   (x1 - 11 * math.cos(a + 0.45), y1 - 11 * math.sin(a + 0.45))], fill=(90, 90, 90))
    for cam, name, (x1, z1, x2, z2), kind in LINES:
        a, b = P(x1, z1), P(x2, z2)
        if kind == "existing":
            d.line((*a, *b), fill=(0, 0, 0), width=2)
        else:
            n = 14
            for i in range(0, n, 2):
                p = (a[0] + (b[0] - a[0]) * i / n, a[1] + (b[1] - a[1]) * i / n)
                q = (a[0] + (b[0] - a[0]) * (i + 1) / n, a[1] + (b[1] - a[1]) * (i + 1) / n)
                d.line((*p, *q), fill=(0, 0, 0), width=2)
    # 線の名前は、線の外側の端に置く
    names = {"LA1": (0.30, 0.02, 4, 4), "LA2": (-0.05, 0.02, -18, 4), "LA3 角": (0.52, -0.52, 6, -4), "LA4 背": (0.0, -0.20, 6, -9),
             "LB1": (-0.9, -0.30, -44, -9), "LB2": (-0.9, 0.0, -44, -9), "LB3": (-0.9, 0.45, -44, -9),
             "LC1": (-0.15, 0.9, -16, -22), "LC2": (0.05, 0.9, -16, -22), "LC3": (0.40, 0.9, -16, -22), "LC4 画の外": (0.58, 0.9, -8, -22)}
    for name, (x, z, dx, dy) in names.items():
        px, py = P(x, z)
        d.text((px + dx, py + dy), name, font=font(14), fill=(0, 0, 0))
    cams = {c["id"]: c["calib"] for c in json.loads(pack["calibOverride"])["cams"]}
    for cid, c in cams.items():
        px, py = P(c["x"], c["z"])
        px, py = min(size - 30, max(30, px)), min(size - 30, max(30, py))
        yaw = math.radians(c["yawDeg"])
        dx, dy = math.sin(yaw), -math.cos(yaw)
        d.ellipse((px - 9, py - 9, px + 9, py + 9), fill=(0, 0, 0))
        d.line((px, py, px + dx * 46, py + dy * 46), fill=(0, 0, 0), width=3)
        # 枠の外へ寄せた台（B）は、名前が画の下へ切れないよう点の右に書く
        lx, ly = (px + 16, py - 12) if py > size - 60 else (px - dx * 30 - 8, py - dy * 30 - 10)
        d.text((lx, ly), f"カメラ{cid}", font=font(16), fill=(0, 0, 0))
    d.text(P(0.25, -0.25), "区間 A", font=font(20), fill=(70, 70, 70))
    d.text(P(-0.89, -0.52), "区間 B", font=font(16), fill=(70, 70, 70))
    d.text(P(0.20, 0.64), "区間 C", font=font(16), fill=(70, 70, 70))
    d.text(P(-0.44, 0.47), "幕（L 字）", font=font(14), fill=(0, 0, 0))
    d.text((pad, size - 62), "実線は本番の台本にある線。破線はこの案で足す線。灰色の線は歩く道（手すりから 0.16m）。", font=font(14), fill=(60, 60, 60))
    d.text((pad, size - 38), "1 辺 1.8m。カメラ B は床置きで枠の外（南西）、カメラ C は高さ 1.87m。", font=font(14), fill=(60, 60, 60))
    img.save(out / "floor.png")


def cards(out: Path, stage: dict, frames_dir: Path, pack: dict) -> None:
    calibs = {c["id"]: c["calib"] for c in json.loads(pack["calibOverride"])["cams"]}
    for cam in sm.CAMS:
        rows = stage["cams"][cam]["rows"]
        project = sm.cam_projector(calibs[cam])
        tiles = []
        for band, t in enumerate((0.15, 0.48, 0.82)):
            r = rows[int(round(t * (len(rows) - 1)))]
            arr = np.asarray(Image.open(frames_dir / cam / f"{r['frame']:04d}.png").convert("RGB")).astype(np.float32)
            img = Image.fromarray(np.clip(arr * 1.5 + 8, 0, 255).astype(np.uint8))
            d = ImageDraw.Draw(img)
            if r["box"]:
                x0, y0, x1, y1 = r["box"]
                d.rectangle((x0 * W, y0 * H, x1 * W, y1 * H), outline=(255, 255, 255), width=2)
                label(d, (x0 * W + 4, min(H - 24, y1 * H - 22)), "体", 14)
            n = 0
            for b, (x0, y0, x1, y1), text in PLACES[cam]:
                if b != band or not text:
                    continue
                d.rectangle((x0 * W, y0 * H + 1, x1 * W, y1 * H - 1), outline=(255, 214, 0), width=3)
                label(d, (x0 * W + 5, y0 * H + 6 + n * 0), text, 14, fill=(0, 0, 0), bg=(255, 214, 0))
                n += 1
            if cam == "C" and band in DOLL_SPOTS:
                x, z = DOLL_SPOTS[band]
                pts = [project((x + DOLL_R * math.cos(k * math.pi / 4), h, z + DOLL_R * math.sin(k * math.pi / 4)))
                       for h in (0.0, DOLL_H) for k in range(8)]
                pts = [p for p in pts if p]
                if pts:
                    bx0, by0 = min(p[0] for p in pts), min(p[1] for p in pts)
                    bx1, by1 = max(p[0] for p in pts), max(p[1] for p in pts)
                    d.rectangle((bx0, by0, bx1, by1), outline=(255, 214, 0), width=3)
                    label(d, (min(W - 190, bx1 + 6), by0), "2周目 人形が立つ（背後）", 14, fill=(0, 0, 0), bg=(255, 214, 0))
            label(d, (8, H - 28), f"進み {int(r['prog'] * 100)}%", 16)
            tiles.append(img)
        sheet = Image.new("RGB", (W * 3 + 16, H), (255, 255, 255))
        for i, tl in enumerate(tiles):
            sheet.paste(tl, (i * (W + 8), 0))
        sheet.save(out / f"card_{cam}.png")


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--map", required=True, help="stage_map.py の出力フォルダ")
    ap.add_argument("--frames", default=str(ROOT / "Logs/stage-map/frames"))
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    stage = json.loads((Path(a.map) / "stage_map.json").read_text(encoding="utf-8"))
    pack = json.loads(sm.PACK.read_text(encoding="utf-8"))
    show = json.loads(sm.SHOW.read_text(encoding="utf-8-sig"))
    floor_png(out, show, pack)
    cards(out, stage, Path(a.frames), pack)
    print(f"[stage-cards] 出力: {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
