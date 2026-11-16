"""目線の動画と体の位置が揃った三人称の動画を作る。

    py -3.11 tools/trailer/tp_sync.py <route_index.json> <out tp.mp4> [--game <Drive の game/forward>] [--w 1280]

三人称のコマは、ツインの道（routes/forward.tsv）に沿って構図ごとに焼いてある
（twin_render.py seq --shotfile tools/trailer/tp-shots.json → seq_post.py。出力は <game>/<構図>.mp4・15fps）。
route_sync.py の表は「目線の動画の T 秒に、体験者が道の何コマ目に居たか」を持っているので、
その番号で三人称のコマを引けば、目線の動画と同じ瞬間の体になる。

構図は体験者の居る区画で選ぶ（tp-shots.json の t0〜t1 が担当。重なる所は SHOTS の先に書いた方）。
担当の構図が無い時刻は黒。
"""
import argparse
import json
import subprocess
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
GAME = Path(r"G:\マイドライブ\shubie\renders\twin-cycles\game\forward")
SHOTS = ["tp_front", "tp_west", "tp_north"]
ROUTE_FPS = 15
LAST = 468  # 道の最後のコマ。輪なので最初のコマと同じ場所


def load(path: Path, w: int, h: int) -> list[bytes]:
    r = subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-vf", f"scale={w}:{h}", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"],
                       capture_output=True)
    n = len(r.stdout) // (w * h * 3)
    return [r.stdout[i * w * h * 3:(i + 1) * w * h * 3] for i in range(n)]


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("index", type=Path)
    ap.add_argument("out", type=Path)
    ap.add_argument("--game", type=Path, default=GAME)
    ap.add_argument("--w", type=int, default=1280)
    a = ap.parse_args()
    w, h = a.w, a.w * 3 // 4
    spec = json.loads((HERE / "tp-shots.json").read_text(encoding="utf-8"))
    idx = json.loads(a.index.read_text(encoding="utf-8"))
    shots = []
    for name in SHOTS:
        mp4 = a.game / f"{name}.mp4"
        if not mp4.exists() or "t0" not in spec.get(name, {}):
            continue
        first = int(round(spec[name]["t0"] * ROUTE_FPS))
        fr = load(mp4, w, h)
        shots.append((name, first, first + len(fr) - 1, fr))
        print(f"  {name}: 道のコマ {first}〜{first + len(fr) - 1}")
    if not shots:
        raise SystemExit(f"三人称の動画が無い: {a.game}/tp_*.mp4")
    black = bytes(w * h * 3)
    enc = subprocess.Popen(["ffmpeg", "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{w}x{h}", "-r", str(idx["fps"]), "-i", "-",
                            "-c:v", "libx264", "-preset", "veryfast", "-crf", "17", "-pix_fmt", "yuv420p", str(a.out)], stdin=subprocess.PIPE)
    used = {name: 0 for name, *_ in shots}
    none = 0
    for k in idx["idx"]:
        k = 0 if k >= LAST else k
        for name, lo, hi, fr in shots:
            if lo <= k <= hi:
                enc.stdin.write(fr[k - lo])
                used[name] += 1
                break
        else:
            enc.stdin.write(black)
            none += 1
    enc.stdin.close()
    rc = enc.wait()
    print(f"→ {a.out}  {used}  担当なし {none} コマ  rc={rc}")
    return rc


if __name__ == "__main__":
    sys.exit(main())
