"""体験の記録（xprec）のカメラ映像 cam_*.mjr を、目線の動画と同じ時計の mp4 にする。

    py -3.11 tools/trailer/mjr2mp4.py <cam_Phone01.mjr> <out.mp4> [--lead 1.80] [--fps 30] [--dur 341]

MJR1 は「len u32 / ptsMs u32 / JPEG」の繰り返し（Assets/Scripts/Streaming/Recording/RecordedSegmentFormat.cs）。
pts は記録の頭からのミリ秒。ツインの通し走行（tools/twin-verify.py）の目線の動画は、時計（tau）より
最初の詰まりの分（--lead・最初の ev=sum の worst）だけ遅れて始まるので、出力の時刻 T のコマには
pts <= (T + lead) * 1000 の最後の JPEG を置く。記録の頭は ev=xprec state=start（tau ≒ 0）。
"""
import argparse
import bisect
import struct
import subprocess
import sys
from pathlib import Path


def read_mjr(path: Path):
    b = path.read_bytes()
    if b[:4] != b"MJR1":
        raise SystemExit(f"MJR1 ではない: {path}")
    o, pts, offs = 12, [], []
    while o + 8 <= len(b):
        n, p = struct.unpack_from("<II", b, o)
        if o + 8 + n > len(b):
            break  # 末尾は壊れうる（RecordedSegmentFormat の約束）
        pts.append(p)
        offs.append((o + 8, n))
        o += 8 + n
    return b, pts, offs


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("mjr", type=Path)
    ap.add_argument("out", type=Path)
    ap.add_argument("--lead", type=float, default=0.0, help="目線の動画が時計より遅れて始まる秒")
    ap.add_argument("--fps", type=int, default=30)
    ap.add_argument("--dur", type=float, default=0.0, help="出力の長さ（0 = 記録の最後まで）")
    a = ap.parse_args()
    b, pts, offs = read_mjr(a.mjr)
    if not pts:
        raise SystemExit(f"コマが無い: {a.mjr}")
    dur = a.dur or (pts[-1] / 1000 - a.lead)
    n = int(dur * a.fps)
    enc = subprocess.Popen(["ffmpeg", "-v", "error", "-y", "-f", "image2pipe", "-c:v", "mjpeg", "-framerate", str(a.fps), "-i", "-",
                            "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p", "-c:v", "libx264", "-preset", "veryfast",
                            "-crf", "18", str(a.out)], stdin=subprocess.PIPE)
    blank = None
    for i in range(n):
        want = (i / a.fps + a.lead) * 1000
        k = bisect.bisect_right(pts, want) - 1
        if k < 0:
            if blank is None:
                o, ln = offs[0]
                blank = b[o:o + ln]  # 最初のコマより前は最初のコマで埋める
            enc.stdin.write(blank)
            continue
        o, ln = offs[k]
        enc.stdin.write(b[o:o + ln])
    enc.stdin.close()
    rc = enc.wait()
    print(f"{a.mjr.name}: {len(pts)} コマ（{pts[0] / 1000:.2f}〜{pts[-1] / 1000:.2f}s）→ {a.out} {n} コマ rc={rc}")
    return rc


if __name__ == "__main__":
    sys.exit(main())
