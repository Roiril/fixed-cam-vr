"""博士動画の目元を影に沈めて焼き直す。

顔の位置は track-doctor-face.py が書いた doctor-face-track.json を使う。
影は顔の向きと大きさに合わせて毎コマ置き直すので、頭が動いても目元から外れない。
口と顎は触らない（リップシンクの画をそのまま残す）。

  py -3.11 tools/visitor-ui/shade-doctor-eyes.py --still out.png --scene subject --frame 141
  py -3.11 tools/visitor-ui/shade-doctor-eyes.py            # 4 本を焼いて <scene>-ja-v2.mp4 と poster を書く
  py -3.11 tools/visitor-ui/shade-doctor-eyes.py --check    # 焼いた版の目元と口元の明るさを測る

光の形（顔座標。原点 = 両目の中点、単位 = 目の間隔 d、y は下向き）:
  - 下から細く絞った 1 灯が口元から下だけを照らす。目・眉・額・髪は闇に沈む
  - 闇の縁は説明の頭で目の下（0.36d）、おしまいで鼻の下（0.64d）。説明が進むほど下がる
  - 闇の中でも両目のまわりはさらに沈める（白目を拾わせない）
"""
import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
ASSET = ROOT / "tablet" / "app" / "src" / "main" / "assets" / "web" / "asset"
TRACK = ROOT / "tools" / "visitor-ui" / "doctor-face-track.json"
SCENES = ["introduction", "subject", "report", "wear"]
VERSION = "v2"

# 闇の底の明るさ（0 で純黒）。純黒にすると穴に見えるので、輪郭がかろうじて残る程度に
FLOOR = 0.10
# 闇の中で両目のまわりだけ追加で落とす割合（0.6 なら闇の底のさらに 0.4 倍）
EYE_EXTRA = 0.6
# 闇の縁の幅（目の間隔 d に対する比）
SOFT = 0.17
# 闇の縁の高さ（両目の中点から下へ、d 単位）。説明の頭からおしまいへ向けて少しずつ下がる。
# 0.36 = 目の下まで闇 / 0.64 = 鼻の下まで闇。章の境目で値がつながる
EDGE_START, EDGE_END = 0.36, 0.64
# 各章の尺（秒）。縁の高さを説明全体の時刻で決めるのに使う
SCENE_SEC = {"introduction": 15.64, "subject": 20.76, "report": 19.72, "wear": 9.52}


def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def edge_at(scene, frame_index, fps=25.0):
    total = sum(SCENE_SEC.values())
    before = 0.0
    for name in SCENES:
        if name == scene:
            break
        before += SCENE_SEC[name]
    t = min(total, before + frame_index / fps) / total
    return EDGE_START + (EDGE_END - EDGE_START) * t


def gain_map(shape, eyes, edge):
    """1 = 元のまま、FLOOR = 闇の底。eyes = [[lx, ly], [rx, ry]]（画素）。

    下から細く絞った 1 灯が口元から下だけを照らし、それより上は闇に沈む。
    縁は顔の傾きに合わせて傾き、頬の側でわずかに持ち上がる（定規の線に見せない）。
    """
    h, w = shape[:2]
    (lx, ly), (rx, ry) = eyes
    cx, cy = (lx + rx) / 2, (ly + ry) / 2
    d = float(np.hypot(rx - lx, ry - ly))
    a = np.arctan2(ry - ly, rx - lx)
    ca, sa = np.cos(a), np.sin(a)
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    px, py = xx - cx, yy - cy
    u = (px * ca + py * sa) / d
    v = (-px * sa + py * ca) / d
    bend = 0.07 * np.clip(u, -1.6, 1.6) ** 2 + 0.04 * u
    face = smoothstep(edge - SOFT, edge + SOFT, v + bend)
    # 顔から離れた背景には縁を引かない（横一直線の境目が出る）。上ほど暗いなだらかな暗がりにする
    room = 0.08 + 0.62 * smoothstep(-1.2, 3.2, v)
    # 顔の幅の外は背景の暗がりへ。肩と白衣の高さ（v > 1.6）より下は顔の光のまま
    near = (1.0 - smoothstep(0.95, 1.8, np.abs(u))) + smoothstep(1.4, 2.4, v)
    near = np.clip(near, 0.0, 1.0)
    lit = room + (face - room) * near
    gain = FLOOR + (1.0 - FLOOR) * lit
    # 闇の中でも白目が拾えてしまう（実機の明るさで冒頭の文に目が読めた）。両目のまわりだけさらに沈める。
    # 髪と額の輪郭は FLOOR のまま残す
    e = ((np.abs(u) - 0.5) / 0.36) ** 2 + ((v + 0.02) / 0.22) ** 2
    sockets = 1.0 - smoothstep(0.6, 1.4, e)
    return gain * (1.0 - EYE_EXTRA * sockets * (1.0 - lit))


def apply(frame, eyes, edge, grain_seed=None):
    g = gain_map(frame.shape, eyes, edge)[..., None]
    f = frame.astype(np.float32) * g
    if grain_seed is not None:
        # 闇の中の量子化の縞を散らす
        rng = np.random.default_rng(grain_seed)
        f += rng.normal(0.0, 1.2, f.shape[:2]).astype(np.float32)[..., None] * (1.0 - g)
    return np.clip(f, 0, 255).astype(np.uint8)


def load_track():
    return json.loads(TRACK.read_text(encoding="utf-8"))["scenes"]


def ffmpeg():
    exe = shutil.which("ffmpeg")
    if not exe:
        raise SystemExit("ffmpeg が見つからない")
    return exe


def render_scene(scene, rows):
    src = ASSET / f"{scene}-ja-v1.mp4"
    dst = ASSET / f"{scene}-ja-{VERSION}.mp4"
    poster = ASSET / f"{scene}-doctor-{VERSION}.jpg"
    cap = cv2.VideoCapture(str(src))
    w, h = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)), int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    tmp = dst.with_suffix(".tmp.mp4")
    proc = subprocess.Popen(
        [ffmpeg(), "-y", "-loglevel", "error",
         "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", f"{w}x{h}", "-r", "25", "-i", "-",
         "-i", str(src), "-map", "0:v", "-map", "1:a", "-c:a", "copy",
         "-c:v", "libx264", "-preset", "slow", "-crf", "18", "-pix_fmt", "yuv420p",
         "-movflags", "+faststart", str(tmp)],
        stdin=subprocess.PIPE)
    i = 0
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        out = apply(frame, rows[i]["eyesSmooth"], edge_at(scene, i), grain_seed=i)
        if i == 0:
            cv2.imwrite(str(poster), out, [cv2.IMWRITE_JPEG_QUALITY, 92])
        proc.stdin.write(out.tobytes())
        i += 1
    cap.release()
    proc.stdin.close()
    if proc.wait() != 0:
        raise SystemExit(f"{scene}: ffmpeg が失敗")
    if i != len(rows):
        raise SystemExit(f"{scene}: コマ数が追跡と合わない {i} != {len(rows)}")
    tmp.replace(dst)
    return dst, poster, i


def region_mean(frame, eyes, which):
    (lx, ly), (rx, ry) = eyes
    d = np.hypot(rx - lx, ry - ly)
    cx, cy = (lx + rx) / 2, (ly + ry) / 2
    if which == "eyes":
        boxes = [(lx, ly), (rx, ry)]
        r = 0.18 * d
        vals = [frame[int(y - r):int(y + r), int(x - r):int(x + r)].mean() for x, y in boxes]
        return float(np.mean(vals))
    # 口元: 両目の中点から 1.05d 下
    x, y, r = cx, cy + 1.05 * d, 0.22 * d
    return float(frame[int(y - r):int(y + r), int(x - r):int(x + r)].mean())


def check(track):
    """元と焼いた版で、目元と口元の明るさの比を出す。目は落ち、口は残っていること。"""
    bad = 0
    for scene in SCENES:
        rows = track[scene]["rows"]
        a = cv2.VideoCapture(str(ASSET / f"{scene}-ja-v1.mp4"))
        b = cv2.VideoCapture(str(ASSET / f"{scene}-ja-{VERSION}.mp4"))
        n_b = int(b.get(cv2.CAP_PROP_FRAME_COUNT))
        eye_r, mouth_r = [], []
        i = 0
        while True:
            oa, fa = a.read()
            ob, fb = b.read()
            if not (oa and ob):
                break
            if i % 10 == 0:
                e = rows[i]["eyesSmooth"]
                eye_r.append(region_mean(fb, e, "eyes") / max(1.0, region_mean(fa, e, "eyes")))
                mouth_r.append(region_mean(fb, e, "mouth") / max(1.0, region_mean(fa, e, "mouth")))
            i += 1
        worst_eye, worst_mouth = max(eye_r), min(mouth_r)
        ok = worst_eye < 0.35 and worst_mouth > 0.9 and n_b == len(rows)
        bad += not ok
        print(f"{scene}: コマ {n_b}/{len(rows)} / 目元の明るさ 最大 {worst_eye:.2f} 倍 / "
              f"口元 最小 {worst_mouth:.2f} 倍 {'OK' if ok else 'NG'}")
    return bad


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--still", type=Path)
    ap.add_argument("--scene", default="introduction")
    ap.add_argument("--frame", type=int, default=0)
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    track = load_track()
    if args.check:
        raise SystemExit(1 if check(track) else 0)
    if args.still:
        cap = cv2.VideoCapture(str(ASSET / f"{args.scene}-ja-v1.mp4"))
        cap.set(cv2.CAP_PROP_POS_FRAMES, args.frame)
        ok, frame = cap.read()
        if not ok:
            raise SystemExit("コマが読めない")
        cv2.imwrite(str(args.still), apply(frame, track[args.scene]["rows"][args.frame]["eyesSmooth"],
                                            edge_at(args.scene, args.frame), 0))
        return
    for scene in SCENES:
        dst, poster, n = render_scene(scene, track[scene]["rows"])
        print(f"{scene}: {n} コマ → {dst.name} / {poster.name}")
    raise SystemExit(1 if check(track) else 0)


if __name__ == "__main__":
    main()
