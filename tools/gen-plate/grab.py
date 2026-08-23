#!/usr/bin/env python3
"""配信中のカメラから無人プレートを 1 枚取る。

卓（ブラウザ）を開かなくても、その日のカメラ位置のプレートが取れる。
MJPEG を数フレーム読み、いちばんぶれていない 1 枚を書き出す。

    py -3.11 tools/gen-plate/grab.py --cam A
    py -3.11 tools/gen-plate/grab.py --cam A --host 192.168.11.23 --out plate.jpg

--cam だけ渡したときは、卓の show.json に載っている host を使う。
"""
from __future__ import annotations

import argparse
import datetime
import io
import json
import os
import sys
import urllib.request

sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(ROOT))
SHOW = os.path.join(REPO, "tools", "web-compositor", "show.json")
CAPTURES = os.path.join(REPO, "tools", "web-compositor", "captures")

SOI = b"\xff\xd8"
EOI = b"\xff\xd9"


def host_of(cam: str) -> tuple[str, int]:
    with open(SHOW, encoding="utf-8") as f:
        show = json.load(f)
    for c in show.get("cameras", []):
        if c.get("id") == cam:
            host = (c.get("host") or "").strip()
            if not host:
                raise SystemExit(f"カメラ {cam} の host が show.json に無い")
            return host, int(c.get("port") or 8080)
    raise SystemExit(f"カメラ {cam} が show.json に無い")


def read_frames(url: str, count: int, timeout: float) -> list[bytes]:
    """MJPEG から JPEG を count 枚切り出す。"""
    frames: list[bytes] = []
    buf = bytearray()
    with urllib.request.urlopen(url, timeout=timeout) as r:
        while len(frames) < count:
            chunk = r.read(8192)
            if not chunk:
                break
            buf += chunk
            while True:
                i = buf.find(SOI)
                if i < 0:
                    break
                j = buf.find(EOI, i + 2)
                if j < 0:
                    break
                frames.append(bytes(buf[i : j + 2]))
                del buf[: j + 2]
                if len(frames) >= count:
                    break
    return frames


def sharpness(jpg: bytes) -> float:
    """ぶれの少なさ。高域の分散（大きいほどぶれていない）。"""
    from PIL import Image, ImageFilter
    import numpy as np

    im = Image.open(io.BytesIO(jpg)).convert("L")
    hi = np.asarray(im.filter(ImageFilter.FIND_EDGES), dtype=float)
    return float(hi.var())


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cam", required=True, help="A / B / C")
    ap.add_argument("--host", default="", help="既定は show.json の値")
    ap.add_argument("--port", type=int, default=0)
    ap.add_argument("--path", default="/video")
    ap.add_argument("--frames", type=int, default=12, help="読む枚数（この中で一番ぶれていないものを採る）")
    ap.add_argument("--timeout", type=float, default=8.0)
    ap.add_argument("--out", default="", help="既定は captures/plate_<cam>_<日時>.jpg")
    a = ap.parse_args()

    host, port = (a.host, a.port) if a.host else host_of(a.cam)
    port = a.port or port or 8080
    url = f"http://{host}:{port}{a.path}"

    frames = read_frames(url, a.frames, a.timeout)
    if not frames:
        print(f"NG  {url} からフレームが 1 枚も取れなかった")
        return 1

    scored = [(sharpness(f), i, f) for i, f in enumerate(frames)]
    scored.sort(key=lambda t: -t[0])
    best = scored[0]

    out = a.out
    if not out:
        stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
        out = os.path.join(CAPTURES, f"plate_{a.cam}_{stamp}.jpg")
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    with open(out, "wb") as f:
        f.write(best[2])

    from PIL import Image

    im = Image.open(out)
    print(f"OK  {out}  {im.size[0]}x{im.size[1]}  {len(best[2])}B")
    print(f"    {url} から {len(frames)} 枚読み、{best[1]+1} 枚目を採った"
          f"（ぶれの少なさ {best[0]:.0f}／最低 {scored[-1][0]:.0f}）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
