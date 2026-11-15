"""2 周目 A の右の幕を、動画生成（Flow）の版で本番の素材にする（LEDGER 0389 の C）。

    py -3.11 tools/gen-plate/stain3/build_flow_right.py            # static-inputs へ書き、確認の画を Logs へ
    py -3.11 tools/gen-plate/stain3/build_flow_right.py --dry      # 確認の画だけ

入力は Logs/gen-plate/stain2A_right_video_20261115/flow_v2_plain_to_grin_4x3.mp4（Flow 360p の 4:3 部分を
640x480 にしたもの。作り方は README「動画生成で試す」）。Flow は部屋ごと描き直すので、最初のコマ（何も無い幕）を
無人写真 plate_A_20261115_125052.jpg へ 1 回だけ位置合わせし、その写し方と色の合わせ方を全部のコマへ同じに掛ける。

0389 の C（何も無い → しみへディゾルブ → 顔が加速して出る → 人形が覗く）を台本の段へ割る:
  ratio   しみが出きったコマ（元 2.0 秒）÷ 最初のコマ。段 1 で掛け算にして 0.25 秒で入れる（ディゾルブの代わり。
          生の映像に掛けるので、幕の前を通る体は消えない）
  pop     元 2.0〜2.9 秒を 3 倍・2.9〜4.0 秒を 1.6 倍・4.0〜5.2 秒を等速。最後のコマを 3 枚足す。段 2
  watch   pop の最後のコマ（顔と覗く人形）。段 3
  face    pop の最後のコマの顔だけ（手すりより上）。段 4 の CG の人形がついてくる間
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent))
import build_right_burst as rb  # noqa: E402

ROOT = rb.ROOT
W, H, FPS = rb.W, rb.H, rb.FPS
SRC = ROOT / "Logs/gen-plate/stain2A_right_video_20261115/flow_v2_plain_to_grin_4x3.mp4"
PLATE = ROOT / "tools/web-compositor/captures/plate_A_20261115_125052.jpg"
OUT = ROOT / "tools/web-compositor/static-inputs"
WORK = ROOT / "Logs/gen-plate/stain2A_right_video_20261115/build"
TAG = "20261116_flow"
NAMES = dict(ratio=f"stain_A_right_flow_ratio_{TAG}.png", ratio_mask=f"stain_A_right_flow_ratio_mask_{TAG}.png",
             pop=f"stain_A_right_flow_pop_{TAG}.mp4", mask=f"stain_A_right_flow_mask_{TAG}.png",
             watch=f"stain_A_right_flow_watch_{TAG}.png", face=f"stain_A_right_flow_face_{TAG}.png",
             face_mask=f"stain_A_right_flow_face_mask_{TAG}.png", meta=f"stain_A_right_flow_pop_{TAG}.json")

STAIN_AT = 2.0                      # しみが出きって、まだ膨らんでいないコマ（元の秒）
REST = [(2.0, 2.9, 3.0), (2.9, 4.0, 1.6), (4.0, 5.2, 1.0)]   # (元の始め, 終わり, 速さ)。retime_flow.py の C と同じ
HOLD_FRAMES = 3
RIGHT_BOX = (280, 0, W, H)          # Flow が変えてよい所（右の幕と、その縁の裏の人形）。左の幕は使わない
RAIL = rb.SETS["real"]["rail"]
FACE_BOX = (280, 0, 522, 245)     # 顔としみ（手すりの右端の支柱より左）。人形と右の支柱を入れない


def read_frames(path: Path) -> np.ndarray:
    raw = subprocess.run(["ffmpeg", "-loglevel", "error", "-i", str(path), "-f", "rawvideo", "-pix_fmt", "rgb24", "-"],
                         capture_output=True, check=True).stdout
    return np.frombuffer(raw, np.uint8).reshape(-1, H, W, 3).astype(np.float32)


def reg_maps(a: np.ndarray, ref: np.ndarray, deg: int = 3):
    """rb.register と同じ突き合わせで、ref の座標 → a の座標の写し方（remap の表）を返す。全コマに同じ表を掛ける。"""
    rg, ag = ref.mean(2), a.mean(2)
    pts = []
    size, step = 40, 24
    for y0 in list(range(4, H - size - 4, step)) + [H - size - 4]:
        for x0 in list(range(4, W - size - 4, step)) + [W - size - 4]:
            rx = (min(16, x0), min(16, W - x0 - size))
            ry = (min(44, y0), min(44, H - y0 - size))
            if min(*rx, *ry) < 3:
                continue
            m = rb._match(rg, ag, x0, y0, size, rx, ry)
            if m and m[2] > 0.8:
                pts.append((x0 + size / 2, y0 + size / 2, m[0], m[1]))
    p = np.array(pts, np.float64)
    X = rb._poly_terms(p[:, 0], p[:, 1], deg)
    keep = np.ones(len(p), bool)
    for _ in range(6):
        cx, *_ = np.linalg.lstsq(X[keep], p[keep, 2], rcond=None)
        cy, *_ = np.linalg.lstsq(X[keep], p[keep, 3], rcond=None)
        err = np.hypot(X @ cx - p[:, 2], X @ cy - p[:, 3])
        keep = err < max(1.0, 2.5 * np.median(err[keep]))
    gy, gx = np.mgrid[0:H, 0:W].astype(np.float64)
    T = rb._poly_terms(gx, gy, deg)
    mx = (gx + T @ cx).astype(np.float32)
    my = (gy + T @ cy).astype(np.float32)
    info = {"points": int(keep.sum()), "of": len(p), "residualPx": round(float(np.median(err[keep])), 2),
            "maxShiftPx": round(float(np.hypot(mx - gx, my - gy).max()), 1)}
    return mx, my, info


def main() -> int:
    import cv2
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry", action="store_true", help="static-inputs へ書かない")
    a = ap.parse_args()
    WORK.mkdir(parents=True, exist_ok=True)

    src = read_frames(SRC)
    plate = rb.load(PLATE)
    mx, my, info = reg_maps(src[0], plate)
    print(f"位置合わせ: {info['points']}/{info['of']} 点・残差の中央値 {info['residualPx']}px・最大のずれ {info['maxShiftPx']}px")

    def warp(f):
        return cv2.remap(np.ascontiguousarray(f), mx, my, cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE)

    f0 = warp(src[0])
    d = np.abs(f0 - plate).mean(2)
    calm = d < np.percentile(d, 55)
    kc = [np.polyfit(f0[..., c][calm], plate[..., c][calm], 1) for c in range(3)]

    def grade(f):
        f = f.copy()
        for c, (k, o) in enumerate(kc):
            f[..., c] = np.clip(f[..., c] * k + o, 0, 255)
        return f

    def at(sec):
        return grade(warp(src[min(len(src) - 1, int(round(sec * FPS)))]))

    F0 = grade(f0)
    resid = float(np.abs(F0 - plate).mean())
    print(f"最初のコマと無人写真の差（平均）: {resid:.2f} / 255")

    # 段 2 のコマ（元の時刻を速さで詰める。コマは近い方を取る。混ぜない）
    times = []
    for s0, s1, sp in REST:
        n = int(round((s1 - s0) / sp * FPS))
        times += [s0 + i * sp / FPS for i in range(n)]
    pop = [at(t) for t in times]
    pop += [pop[-1]] * HOLD_FRAMES

    box = rb.box_mask(RIGHT_BOX, 0)
    # 合成範囲: 段 2 のどのコマでも最初のコマから変わった所
    ch = np.zeros((H, W), np.float32)
    for f in pop:
        ch = np.maximum(ch, rb.soft_mask(f, F0, RIGHT_BOX, 0.03, 0.08, close=6))
    region = rb.blur(rb.grow(ch * box, 6), 4)

    # 段 1 のしみ: 比（linear）。1 を超える所は 1（掛け算は暗くするだけ）
    Fs = at(STAIN_AT)
    ms = rb.soft_mask(Fs, F0, RIGHT_BOX, 0.02, 0.06, close=4) * rb.above_rail(RAIL)
    ms = rb.blur(rb.grow(ms, 2), 1.5) * rb.box_mask(FACE_BOX, 3)
    ratio = np.clip(rb.lin(Fs) / np.maximum(rb.lin(F0), 1e-3), 0, 1)
    ratio = np.ones_like(ratio) * (1 - ms[..., None]) + ratio * ms[..., None]

    # 段 4 の顔だけ: 最後のコマの、手すりより上
    last = pop[-1]
    mf = rb.soft_mask(last, F0, RIGHT_BOX, 0.03, 0.08, close=6) * rb.above_rail(RAIL)
    mf = rb.blur(rb.grow(mf, 4), 3) * rb.box_mask(FACE_BOX, 3)

    def comp(f, m):
        return np.clip(plate * (1 - m[..., None]) + f * m[..., None], 0, 255).astype(np.uint8)

    frames = [comp(f, region) for f in pop]
    watch = frames[-1]
    face = comp(last, mf)

    out = WORK if a.dry else OUT
    tmp = WORK / "frames"
    tmp.mkdir(exist_ok=True)
    for p in tmp.glob("*.png"):
        p.unlink()
    for i, f in enumerate(frames):
        Image.fromarray(f).save(tmp / f"f{i:04d}.png")
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", str(FPS), "-i", str(tmp / "f%04d.png"),
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "16", "-g", str(FPS), "-movflags", "+faststart",
                    str(out / NAMES["pop"])], check=True)
    Image.fromarray(watch).save(out / NAMES["watch"])
    Image.fromarray(face).save(out / NAMES["face"])
    Image.fromarray(rb.srgb(ratio).astype(np.uint8)).save(out / NAMES["ratio"])

    def save_mask(m, name):
        Image.fromarray(np.clip(rb.to_frame(m) * 255, 0, 255).astype(np.uint8)).convert("RGB").save(out / name)
    save_mask(region, NAMES["mask"])
    save_mask(rb.blur(rb.grow(ms, 4), 3), NAMES["ratio_mask"])
    save_mask(mf, NAMES["face_mask"])

    meta = {"durationSec": round(len(frames) / FPS, 3), "fps": FPS,
            "register": info, "firstFrameDiff": round(resid, 2), "source": SRC.relative_to(ROOT).as_posix(),
            "rest": REST, "stainAt": STAIN_AT}
    (out / NAMES["meta"]).write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(meta, ensure_ascii=False))

    # 確認の画: 無人写真・段 1（無人写真 × 比）・段 2 の 5 コマ・段 3・段 4・マスク 3 枚
    s1 = rb.srgb(rb.lin(plate) * ratio)
    picks = [0, 3, 7, 12, len(frames) // 2, len(frames) - 1]
    tiles = [("plate", plate), ("段1 しみ（掛け算）", s1)] + [(f"段2 {i / FPS:.2f}s", frames[i]) for i in picks] + \
            [("段3 watch", watch), ("段4 face", face),
             ("mask pop / ratio / face", np.stack([region, ms, mf], 2) * 255)]
    cols = 4
    rows = (len(tiles) + cols - 1) // cols
    sheet = Image.new("RGB", (W * cols, (H + 30) * rows), (20, 20, 20))
    dr = ImageDraw.Draw(sheet)
    for k, (label, arr) in enumerate(tiles):
        x, y = (k % cols) * W, (k // cols) * (H + 30)
        sheet.paste(Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8)), (x, y + 30))
        dr.text((x + 8, y + 8), label, fill=(255, 255, 255))
    sheet.save(WORK / "sheet.png")
    print("確認の画:", (WORK / "sheet.png").relative_to(ROOT).as_posix())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
