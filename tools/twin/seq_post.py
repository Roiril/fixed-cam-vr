"""固定カメラのコマを仕上げて動画にする。py -3.11 tools/twin/seq_post.py forward [--fps 15] [--cams A,B,C] [--keep]

入力は Logs/twin-cycles/seq/<route>/<cam>/fNNNNN.npy（twin_render.py seq）、出力は Drive の
renders/twin-cycles/game/<route>/<cam>.mp4。ノイズはコマごとに変える（固定すると絵に張り付いて見える）。
"""
from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import twin_post  # noqa: E402

ROOT = HERE.parents[1]
SEQ = ROOT / "Logs" / "twin-cycles" / "seq"
GAME = Path(r"G:\マイドライブ\shubie\renders\twin-cycles\game")


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("route")
    ap.add_argument("--fps", type=float, default=15.0)
    ap.add_argument("--cams", default="A,B,C")
    ap.add_argument("--keep", action="store_true")
    a = ap.parse_args()
    out = GAME / a.route
    out.mkdir(parents=True, exist_ok=True)
    for cid in a.cams.split(","):
        src = SEQ / a.route / cid
        files = sorted(src.glob("f*.npy"))
        if not files:
            raise SystemExit(f"[seq_post] コマが無い: {src}")
        ids = [int(p.stem[1:]) for p in files]
        if ids != list(range(ids[0], ids[0] + len(ids))):
            raise SystemExit(f"[seq_post] コマが飛んでいる: {cid}")
        # 動画は C: の一時領域で作ってから Drive へ移す（Drive 上で ffmpeg すると遅い）
        tmp = Path(tempfile.mkdtemp(prefix="twin_seq_"))
        # 露出は背景で 1 度決めて固定する（コマごとに測ると人の出入りで明滅する）
        exposure = twin_post.auto_exposure(np.load(src / "plate.npy").astype(np.float32), twin_post.LOOKS["phone"]["key"])
        try:
            for i, p in enumerate(files):
                bgr = twin_post.process(np.load(p).astype(np.float32), "phone", seed=1000 + ids[i], exposure=exposure)
                twin_post.write_image(str(tmp / f"f{i:05d}.jpg"), bgr)
            mp4 = tmp / f"{cid}.mp4"
            # キーフレームを細かく（コマ送りと切替が速い）。yuv420p でブラウザが読める
            subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", str(a.fps), "-i", str(tmp / "f%05d.jpg"),
                            "-c:v", "libx264", "-crf", "17", "-preset", "slow", "-g", "5", "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(mp4)], check=True)
            shutil.copy2(mp4, out / f"{cid}.mp4")
            # 背景の 1 枚（人が居ない絵。比較と検証用）
            plate = src / "plate.npy"
            if plate.exists():
                twin_post.write_image(str(out / f"{cid}_plate.jpg"), twin_post.process(np.load(plate).astype(np.float32), "phone", seed=1))
            print(f"[seq_post] {cid} {len(files)} コマ → {out / (cid + '.mp4')}")
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
        if not a.keep:
            for p in files:
                p.unlink()
    return 0


if __name__ == "__main__":
    sys.exit(main())
