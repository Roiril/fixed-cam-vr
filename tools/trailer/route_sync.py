"""ツインの通し走行の各時刻に、道（routes/forward.tsv）のどのコマが配られていたかを引く。

    py -3.11 tools/trailer/route_sync.py <xprec>/cam_Phone01.mjr <出力 route_index.json> --lead 1.31 [--pack Logs/twin-run/pack] [--fps 30] [--dur 293]

走行は束（make_pack.py の MJR）のコマを頭の位置で引いて配り、体験の記録はその JPEG をそのまま残す。
だから記録のコマと束のコマはバイト単位で一致し、ハッシュで照合すれば「目線の動画の T 秒に、道の何コマ目（15fps）に
体験者が居たか」が誤差なしで分かる。同じ道で焼いた三人称のコマ（twin_render.py seq --shotfile）をこの表で引けば、
目線の動画と体の位置が揃った三人称の動画になる。

出力: {"fps": 30, "lead": 1.31, "route_fps": 15, "idx": [...], "lap": [...]}（idx[i] は動画の i/fps 秒の道のコマ番号）
"""
import argparse
import bisect
import hashlib
import json
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def frames(path: Path):
    b = path.read_bytes()
    if b[:4] != b"MJR1":
        raise SystemExit(f"MJR1 ではない: {path}")
    o = 12
    while o + 8 <= len(b):
        n, pts = struct.unpack_from("<II", b, o)
        if o + 8 + n > len(b):
            break
        yield pts, hashlib.md5(b[o + 8:o + 8 + n]).digest()
        o += 8 + n


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("mjr", type=Path)
    ap.add_argument("out", type=Path)
    ap.add_argument("--lead", type=float, required=True, help="目線の動画が時計より遅れて始まる秒（最初の ev=sum の worst）")
    ap.add_argument("--pack", type=Path, default=ROOT / "Logs" / "twin-run" / "pack")
    ap.add_argument("--fps", type=int, default=30)
    ap.add_argument("--dur", type=float, default=0.0)
    a = ap.parse_args()
    phone = a.mjr.stem.split("_")[-1]  # cam_Phone01 → Phone01
    table = {}
    for f in sorted(a.pack.glob(f"{phone}_L*.mjr")):
        lap = int(f.stem.split("_L")[-1])
        for k, (_, h) in enumerate(frames(f)):
            table.setdefault(h, (lap, k))
    if not table:
        raise SystemExit(f"束が無い: {a.pack}/{phone}_L*.mjr")
    pts, idx, lap = [], [], []
    miss = 0
    for p, h in frames(a.mjr):
        hit = table.get(h)
        if hit is None:
            miss += 1
            continue
        pts.append(p)
        lap.append(hit[0])
        idx.append(hit[1])
    if miss:
        raise SystemExit(f"束に無いコマが {miss} 枚ある（走行のあとで束を作り直した？）")
    dur = a.dur or (pts[-1] / 1000 - a.lead)
    n = int(dur * a.fps)
    out_idx, out_lap = [], []
    for i in range(n):
        k = max(0, bisect.bisect_right(pts, (i / a.fps + a.lead) * 1000) - 1)
        out_idx.append(idx[k])
        out_lap.append(lap[k])
    a.out.write_text(json.dumps({"fps": a.fps, "lead": a.lead, "route_fps": 15, "idx": out_idx, "lap": out_lap}), encoding="utf-8")
    print(f"{a.mjr.name}: {len(pts)} コマ → {a.out}（{n} 行・道のコマ {min(out_idx)}〜{max(out_idx)}）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
