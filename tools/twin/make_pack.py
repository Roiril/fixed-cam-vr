"""ツイン走行の配信の束を作る。py -3.11 tools/twin/make_pack.py [--route forward] [--out Logs/twin-run/pack]

入力: Drive の renders/twin-cycles/game/<route>/{A,B,C}.mp4 と <cam>_plate.jpg（seq_post.py の出力）、routes/<route>.tsv、rig.json。
出力: <cam 名>_L<周>.mjr（RecordedSegmentFormat の MJR1。動画の全コマ + 最後に人の居ない写真）と pack.json。

映像と写真には、スマホが配る明るさへ直す階調（feed_tone.py）を掛ける。合成素材（make_assets.py）にも同じ表が掛かる。

周ごとに右下の 3 マスへ周の番号を焼く（16px 四方・値の下位ビットが左）。本番は 1・2 周目の録画を 3 周目に流すので、
どの周の映像が映っているかが画でも数でも分かる（XpTwinDriver.ReadBadge が ev=twinlap に出す）。

calib の差し替え（pack.json の calibOverride）は rig.json の姿勢を show.json の形へ直したもの。
本番の calib との投影差も calib_gap として書く。ツインで合っていても本番の calib が合っている証拠にはならないので、
判定の頭に必ず出す（tools/twin-verify.py）。
"""
from __future__ import annotations

import argparse
import csv
import io
import json
import math
import os
import struct
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
import feed_tone  # noqa: E402
GAME = Path(os.environ.get("TWIN_GAME", r"G:\マイドライブ\shubie\renders\twin-cycles\game"))
LAPS = 4
SRC_W, SRC_H = 640, 480
CAMS = [("A", "Phone01"), ("B", "Phone02"), ("C", "Phone03")]
JPEG_Q = 85
BADGE = 16


def read_show() -> dict:
    return json.loads((ROOT / "Assets" / "StreamingAssets" / "show" / "show.json").read_text(encoding="utf-8-sig"))


def rig_axes(cam: dict):
    """右・上・前。TwinRig.TryPose と同じ（LookRotation に Z 回転の roll を掛ける）。Unity は左手系。"""
    o = np.array(cam["pos"], float)
    f = np.array(cam["lookAt"], float) - o
    f /= np.linalg.norm(f)
    r = np.cross(np.array([0.0, 1.0, 0.0]), f)
    r /= np.linalg.norm(r)
    u = np.cross(f, r)
    a = math.radians(cam["rollDeg"])
    return o, r * math.cos(a) + u * math.sin(a), u * math.cos(a) - r * math.sin(a), f


def euler_axes(pos, yaw_deg, pitch_deg, roll_deg):
    """show.json の calib の姿勢。Quaternion.Euler(-pitch, yaw, roll) の軸（順は Z→X→Y）。"""
    x, y, z = math.radians(-pitch_deg), math.radians(yaw_deg), math.radians(roll_deg)
    cx, sx, cy, sy, cz, sz = math.cos(x), math.sin(x), math.cos(y), math.sin(y), math.cos(z), math.sin(z)
    ry = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]])
    rx = np.array([[1, 0, 0], [0, cx, -sx], [0, sx, cx]])
    rz = np.array([[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]])
    m = ry @ rx @ rz
    return np.array(pos, float), m[:, 0], m[:, 1], m[:, 2]


def rig_to_euler(cam: dict):
    """rig の軸を yaw / pitch / roll（show.json の定義）へ直す。"""
    _, r, u, f = rig_axes(cam)
    yaw = math.degrees(math.atan2(f[0], f[2]))
    pitch = math.degrees(math.asin(max(-1.0, min(1.0, f[1]))))
    # Euler の X 回転は -pitch。Z 回転の roll は right と up から読む。
    roll = math.degrees(math.atan2(r[1], u[1]))
    return yaw, pitch, roll


def project(axes, fx, fy, cx, cy, p):
    o, r, u, f = axes
    d = np.array(p, float) - o
    z = float(d @ f)
    if z < 0.05:
        return None
    return np.array([cx + fx * float(d @ r) / z, cy - fy * float(d @ u) / z])


def calib_override(rig: dict, show: dict) -> tuple[dict, dict]:
    """差し替える calib と、本番の calib との投影差（区間を歩く位置の頭と足を両方へ投影した差・px）。"""
    entries = []
    gaps = {}
    route = list(csv.DictReader((HERE / "routes" / "forward.tsv").open(encoding="utf-8"), delimiter="\t"))
    for cam in rig["cameras"]:
        fx = (SRC_W / 2) / math.tan(math.radians(cam["hfovDeg"]) / 2)
        yaw, pitch, roll = rig_to_euler(cam)
        # 変換の検算。ここが狂うと人形だけがずれて、体験の不具合の顔で出る。
        a, b = rig_axes(cam), euler_axes(cam["pos"], yaw, pitch, roll)
        for s, t in zip(a[1:], b[1:]):
            if float(np.linalg.norm(s - t)) > 1e-6:
                raise SystemExit(f"{cam['id']}: rig から Euler への変換が合わない {s} {t}")
        entries.append({"id": cam["id"], "calib": {
            "x": cam["pos"][0], "y": cam["pos"][1], "z": cam["pos"][2],
            "yawDeg": yaw, "pitchDeg": pitch, "rollDeg": roll,
            "fxPx": fx, "fyPx": fx, "cxPx": SRC_W / 2, "cyPx": SRC_H / 2, "k1": 0.0,
            "srcW": SRC_W, "srcH": SRC_H, "lensId": "", "rmsPx": 0.0, "pointCount": 0, "solvedAtIso": "twin",
        }})
        real = next(c for c in show["cameras"] if c["id"] == cam["id"]).get("calib")
        if not real:
            gaps[cam["id"]] = None
            continue
        raxes = euler_axes([real["x"], real["y"], real["z"]], real["yawDeg"], real["pitchDeg"], real["rollDeg"])
        diffs = []
        for row in route[::15]:
            if "ABC"[int(float(row["active"]))] != cam["id"]:
                continue
            for h in (0.05, 1.54):
                p = (float(row["x"]), h, float(row["z"]))
                q1 = project(a, fx, fx, SRC_W / 2, SRC_H / 2, p)
                q2 = project(raxes, real["fxPx"], real["fyPx"], real["cxPx"], real["cyPx"], p)
                if q1 is not None and q2 is not None:
                    diffs.append(float(np.linalg.norm(q1 - q2)))
        gaps[cam["id"]] = round(float(np.median(diffs)), 1) if diffs else None
    return {"cams": entries}, gaps


def burn_badge(img: Image.Image, lap: int) -> Image.Image:
    out = img.copy()
    d = ImageDraw.Draw(out)
    for i in range(3):
        on = (lap >> i) & 1
        x0 = SRC_W - BADGE * 3 + BADGE * i
        d.rectangle((x0, SRC_H - BADGE, x0 + BADGE - 1, SRC_H - 1), fill=(255, 255, 255) if on else (0, 0, 0))
    return out


def jpeg(img: Image.Image) -> bytes:
    b = io.BytesIO()
    img.save(b, "JPEG", quality=JPEG_Q)
    return b.getvalue()


def video_frames(path: Path):
    p = subprocess.Popen(["ffmpeg", "-v", "error", "-i", str(path), "-f", "rawvideo", "-pix_fmt", "rgb24", "-"], stdout=subprocess.PIPE)
    n = SRC_W * SRC_H * 3
    assert p.stdout is not None
    while True:
        buf = p.stdout.read(n)
        if len(buf) < n:
            break
        yield Image.frombytes("RGB", (SRC_W, SRC_H), buf)
    if p.wait() != 0:
        raise SystemExit(f"ffmpeg が失敗: {path}")


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--route", default="forward")
    ap.add_argument("--out", default=str(ROOT / "Logs" / "twin-run" / "pack"))
    ap.add_argument("--fps", type=float, default=15.0)
    a = ap.parse_args()

    src = GAME / a.route
    for cam, _ in CAMS:
        for f in (src / f"{cam}.mp4", src / f"{cam}_plate.jpg"):
            if not f.exists():
                raise SystemExit(f"無い: {f}（tools/twin/README.md の seq_post.py）")

    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    stamp = out / "source.json"
    source = {cam: {"size": (src / f"{cam}.mp4").stat().st_size, "mtime": (src / f"{cam}.mp4").stat().st_mtime} for cam, _ in CAMS}
    rig_text = (HERE / "rig.json").read_text(encoding="utf-8")
    source["rig"] = hash_text(rig_text)
    rows = list(csv.DictReader((HERE / "routes" / f"{a.route}.tsv").open(encoding="utf-8"), delimiter="\t"))
    source["route"] = hash_text((HERE / "routes" / f"{a.route}.tsv").read_text(encoding="utf-8"))
    source["tone"] = feed_tone.signature()
    rebuilt = not (stamp.exists() and json.loads(stamp.read_text(encoding="utf-8")) == source and (out / "pack.json").exists())

    # コマごとの位置（動画は fps、経路の表は 30Hz）。動いている最後のコマまでが道。
    ts = np.array([float(r["t"]) for r in rows])
    xs_all, zs_all, sp = (np.array([float(r[k]) for r in rows]) for k in ("x", "z", "speed"))
    last_move = float(ts[np.nonzero(sp > 0.02)[0][-1]])

    rig = json.loads(rig_text)
    show = read_show()
    override, gaps = calib_override(rig, show)

    if rebuilt:
        count = None
        for cam, name in CAMS:
            plate = feed_tone.apply(Image.open(src / f"{cam}_plate.jpg").convert("RGB"), cam)
            if plate.size != (SRC_W, SRC_H):
                raise SystemExit(f"{cam}_plate.jpg の寸法 {plate.size}")
            files = [open(out / f"{name}_L{lap}.mjr", "wb") for lap in range(1, LAPS + 1)]
            for f in files:
                f.write(b"MJR1" + struct.pack("<II", SRC_W, SRC_H))
            n = 0

            def put(img: Image.Image, pts: int):
                for lap, f in enumerate(files, 1):
                    data = jpeg(burn_badge(img, lap))
                    f.write(struct.pack("<II", len(data), pts))
                    f.write(data)

            for img in video_frames(src / f"{cam}.mp4"):
                put(feed_tone.apply(img, cam), int(n / a.fps * 1000))
                n += 1
            put(plate, int(n / a.fps * 1000))
            for f in files:
                f.close()
            if count is not None and count != n:
                raise SystemExit(f"カメラごとのコマ数が違う {count} / {n}")
            count = n
            print(f"{cam}: {n} コマ + 写真")
        (out / "frame_count.txt").write_text(str(count), encoding="utf-8")
    count = int((out / "frame_count.txt").read_text(encoding="utf-8"))

    times = np.arange(count) / a.fps
    fx = np.interp(times, ts, xs_all)
    fz = np.interp(times, ts, zs_all)
    move_end = int(min(count - 1, math.floor(last_move * a.fps)))
    meta = {
        "fps": a.fps, "laps": LAPS, "moveEnd": move_end,
        "xs": [round(float(v), 4) for v in fx], "zs": [round(float(v), 4) for v in fz],
        "cams": [n for _, n in CAMS], "calibOverride": json.dumps(override, ensure_ascii=False),
        "source": a.route, "calibGapPx": gaps,
    }
    (out / "pack.json").write_text(json.dumps(meta, ensure_ascii=False), encoding="utf-8")
    stamp.write_text(json.dumps(source), encoding="utf-8")
    mb = sum(p.stat().st_size for p in out.glob("*.mjr")) / 1e6
    print(f"束: {out}（{'作り直した' if rebuilt else '前回のまま'}）{count} コマ・道 {move_end} コマ・{mb:.0f}MB")
    print("本番の calib とツインの配置の差（px・中央値）:", gaps)
    return 0


def hash_text(text: str) -> str:
    import hashlib
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


if __name__ == "__main__":
    sys.exit(main())
