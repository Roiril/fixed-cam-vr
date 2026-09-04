# -*- coding: utf-8 -*-
"""生成された動画を、**プレートの座標へ置き直して** cue の素材にする（`canon/LEDGER.md` 0144）。

    py -3.11 tools/gen-plate/fitvideo.py --video <もらった.mp4> \
        --plate tools/web-compositor/captures/plate_A_20260823_194234.jpg \
        --scale 0.656 --at 33,8 --reverse \
        --out tools/web-compositor/captures/gen_stainA_smile_<日付>.mp4

なぜ要るか: 動画の生成は**こちらが渡した画角を守らない**（2026-09-04 実測。4:3 で渡したのに
16:9 で返り、部屋も顔も描き直されていた）。だから「動画そのもの」は素材にできない。
**異変のある所だけを、プレートの座標へ縮めて置く**。外はプレートのままにしておけば、
実機の合成はマスクで異変の所だけを抜くので、画角の食い違いは体験者に届かない。

やること（1 コマずつ）:

  1. 音を捨てる（`--reverse` なら並びも逆にする）
  2. `--scale` で縮め、`--at` の位置へ置く。**外はプレートで埋める**
  3. プレートと同じ寸法・H.264 / yuv420p で書き出す（Android の MediaCodec が開ける形）

⚠ **倍率と位置は「異変の中の同じ点」で決める。** 顔なら目 2 つと口の重心。
   `--probe` を付けると、その 3 点を測って倍率と位置を印字するだけで終わる（当てる前に確かめる）。
⚠ **回転は入れない。** 3 点で相似変換を解くと数度の回転が出るが、掛けると幕ごと傾いて一目で分かる。
"""
from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import tempfile

import numpy as np
from PIL import Image

try:
    import imageio_ffmpeg
except ImportError:
    imageio_ffmpeg = None


def ffmpeg() -> str:
    if imageio_ffmpeg is not None:
        return imageio_ffmpeg.get_ffmpeg_exe()
    exe = shutil.which("ffmpeg")
    if not exe:
        raise SystemExit("ffmpeg が無い（imageio-ffmpeg も入っていない）")
    return exe


def centroid(img: np.ndarray, cx: int, cy: int, r: int, pct: float = 12.0):
    """(cx, cy) のまわり r 画素で、いちばん暗い pct% の重心。"""
    h, w = img.shape
    y0, y1 = max(0, cy - r), min(h, cy + r)
    x0, x1 = max(0, cx - r), min(w, cx + r)
    sub = img[y0:y1, x0:x1]
    m = sub <= np.percentile(sub, pct)
    ys, xs = np.nonzero(m)
    return x0 + xs.mean(), y0 + ys.mean()


def fit_scale_shift(V: np.ndarray, M: np.ndarray):
    """一様な拡大縮小＋平行移動だけを最小二乗で（回転は入れない）。"""
    vc, mc = V.mean(0), M.mean(0)
    v0, m0 = V - vc, M - mc
    s = float((v0 * m0).sum() / (v0 * v0).sum())
    t = mc - s * vc
    res = (V * s + t) - M
    return s, t, np.hypot(res[:, 0], res[:, 1])


def main() -> int:
    ap = argparse.ArgumentParser(description="生成動画をプレートの座標へ置き直して素材にする")
    ap.add_argument("--video", required=True)
    ap.add_argument("--plate", required=True, help="外を埋めるプレート（寸法もこれに合わせる）")
    ap.add_argument("--scale", type=float, default=1.0)
    ap.add_argument("--at", default="0,0", help="置く左上 x,y（プレート座標）")
    ap.add_argument("--reverse", action="store_true", help="並びを逆にする")
    ap.add_argument("--match", action="store_true",
                    help="⭐ 動画の明るさをプレートへ合わせる（マスクの中の明るい方 30% ＝ 染まっていない布で）。"
                         "生成は露出を守らないので、これが無いと届いた画が真っ黒になる")
    ap.add_argument("--mask", default="",
                    help="⭐ cue のマスク（枠空間 640x360）。渡すと**マスクの中だけ**動画にし、"
                         "外はプレートに戻す。渡さないと置いた矩形いっぱいに動画の作り物の部屋が残る")
    ap.add_argument("--fps", type=float, default=0.0, help="0 = 元のまま")
    ap.add_argument("--out", required=True)
    ap.add_argument("--frames-dir", default="", help="途中のコマを残す（既定は一時フォルダ）")
    # 当てる前に確かめる
    ap.add_argument("--probe", default="", help="倍率と位置を測るだけ。"
                    "vx1,vy1,vx2,vy2,vx3,vy3:mx1,my1,mx2,my2,mx3,my3（動画の 3 点 : 素材の 3 点）")
    ap.add_argument("--probe-frame", default="", help="--probe で使う動画のコマ（PNG）")
    ap.add_argument("--probe-material", default="", help="--probe で使う素材（PNG）")
    ap.add_argument("--probe-r", type=int, default=22)
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    if a.probe:
        vs, ms = a.probe.split(":")
        vp = [int(float(v)) for v in vs.split(",")]
        mp = [int(float(v)) for v in ms.split(",")]
        vi = np.asarray(Image.open(a.probe_frame).convert("L"), float)
        mi = np.asarray(Image.open(a.probe_material).convert("L"), float)
        V = np.array([centroid(vi, vp[i], vp[i + 1], a.probe_r) for i in range(0, 6, 2)])
        M = np.array([centroid(mi, mp[i], mp[i + 1], a.probe_r) for i in range(0, 6, 2)])
        for n, v, m in zip(("点1", "点2", "点3"), V, M):
            print(f"  {n}: 動画 ({v[0]:.1f},{v[1]:.1f}) → 素材 ({m[0]:.1f},{m[1]:.1f})")
        s, t, res = fit_scale_shift(V, M)
        print(f"\n--scale {s:.4f} --at {t[0]:.0f},{t[1]:.0f}   残差 {res.round(1)} 画素")
        return 0

    plate = Image.open(a.plate).convert("RGB")
    W, H = plate.size
    tx, ty = (int(float(v)) for v in a.at.split(","))
    ff = ffmpeg()

    tmp = a.frames_dir or tempfile.mkdtemp(prefix="fitvideo_")
    os.makedirs(tmp, exist_ok=True)
    src_dir = os.path.join(tmp, "src")
    dst_dir = os.path.join(tmp, "dst")
    for d in (src_dir, dst_dir):
        os.makedirs(d, exist_ok=True)

    # 1) コマへばらす（音はここで落ちる）
    r = subprocess.run([ff, "-y", "-i", a.video, os.path.join(src_dir, "%05d.png")],
                       capture_output=True, text=True)
    frames = sorted(os.listdir(src_dir))
    if not frames:
        print(r.stderr[-800:], file=sys.stderr)
        raise SystemExit("コマを取り出せなかった")
    fps = a.fps
    if fps <= 0:
        for line in r.stderr.splitlines():
            if " fps," in line and "Video:" in line:
                try:
                    fps = float(line.split(" fps,")[0].split(",")[-1].strip())
                except ValueError:
                    pass
        fps = fps or 24.0
    print(f"コマ {len(frames)} 枚 / {fps:g} fps  →  {W}x{H} へ置き直す"
          f"（倍率 {a.scale:.4f} / 位置 {tx},{ty}{'・逆再生' if a.reverse else ''}）")

    # 2) 縮めて置く（外はプレート）
    order = list(reversed(frames)) if a.reverse else frames
    alpha = None
    if a.mask:
        mimg = Image.open(a.mask).convert("L")
        fw, fh = mimg.size
        fa, sa = fw / fh, W / H
        if sa > fa:
            iw, ih = fw, max(1, round(fw / sa))
        else:
            iw, ih = max(1, round(fh * sa)), fh
        ox, oy = (fw - iw) // 2, (fh - ih) // 2
        alpha = np.asarray(mimg.crop((ox, oy, ox + iw, oy + ih)).resize((W, H), Image.LANCZOS),
                           dtype=np.float64)[..., None] / 255.0
        print(f"マスクの中だけ動画にする（白 {100 * float((alpha > 0.5).mean()):.1f}%）")
    parr = np.asarray(plate, dtype=np.float64)

    def place(name):
        im = Image.open(os.path.join(src_dir, name)).convert("RGB")
        nw, nh = max(1, round(im.width * a.scale)), max(1, round(im.height * a.scale))
        c = plate.copy()
        c.paste(im.resize((nw, nh), Image.LANCZOS), (tx, ty))
        return np.asarray(c, dtype=np.float64)

    gain = np.ones(3)
    if a.match:
        if alpha is None:
            raise SystemExit("--match には --mask が要る（どこの布で合わせるかが決まらない）")
        sel = (alpha[..., 0] > 0.5)
        ref = place(order[0])
        # 染まっていない布 ＝ マスクの中の明るい方 30%。そこの明るさをプレートへ合わせる
        lum = ref.mean(axis=2)
        cloth = sel & (lum >= np.percentile(lum[sel], 70))
        for c in range(3):
            gain[c] = float(np.median(parr[..., c][cloth]) / max(1.0, np.median(ref[..., c][cloth])))
        gain = np.clip(gain, 0.3, 3.0)
        print(f"明るさを合わせた: R {gain[0]:.3f} / G {gain[1]:.3f} / B {gain[2]:.3f}"
              f"（布 {int(cloth.sum())} 画素で）")

    for i, name in enumerate(order):
        arr0 = np.clip(place(name) * gain, 0, 255)
        canvas = Image.fromarray((arr0 + 0.5).astype(np.uint8))
        if alpha is not None:
            arr = np.asarray(canvas, dtype=np.float64) * alpha + parr * (1.0 - alpha)
            canvas = Image.fromarray(np.clip(arr + 0.5, 0, 255).astype(np.uint8))
        canvas.save(os.path.join(dst_dir, f"{i + 1:05d}.png"))

    # 3) 書き戻す（Android が開ける形。yuv420p は必須）
    os.makedirs(os.path.dirname(os.path.abspath(a.out)) or ".", exist_ok=True)
    r2 = subprocess.run([ff, "-y", "-framerate", f"{fps:g}",
                         "-i", os.path.join(dst_dir, "%05d.png"),
                         "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p",
                         "-profile:v", "high", "-crf", "20", a.out],
                        capture_output=True, text=True)
    if r2.returncode != 0 or not os.path.exists(a.out):
        print(r2.stderr[-1200:], file=sys.stderr)
        raise SystemExit("書き出しに失敗した")
    kb = os.path.getsize(a.out) // 1024
    print(f"{a.out}  {len(order)} コマ / {len(order) / fps:.2f} 秒 / {kb}KB（音なし）")
    if not a.frames_dir:
        shutil.rmtree(tmp, ignore_errors=True)
    else:
        print(f"途中のコマ: {dst_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
