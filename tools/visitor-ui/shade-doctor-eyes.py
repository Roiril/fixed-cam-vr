"""博士動画の目元を闇に沈めて焼き直す。

顔の位置は track-doctor-face.py が書いた doctor-face-track.json を使う。
口と顎は触らない（リップシンクの画をそのまま残す）。

  py -3.11 tools/visitor-ui/shade-doctor-eyes.py --still out.png --scene subject --frame 141
  py -3.11 tools/visitor-ui/shade-doctor-eyes.py            # 4 本を焼いて <scene>-ja-v2.mp4 と poster を書く
  py -3.11 tools/visitor-ui/shade-doctor-eyes.py --check    # 焼いた版の目元と口元の明るさを測る

闇の形（顔座標。原点 = 両目の中点、単位 = 目の間隔 d、y は下向き）:
  - 目・眉・額・髪は闇に沈み、口元から下だけが見える
  - 闇の縁は説明の頭で目の下（0.36d）、おしまいで鼻の下（0.64d）。説明が進むほど下がる
  - 闇の中でも両目のまわりはさらに沈める（白目を拾わせない）

闇は照明の陰ではなく、画面の側が目を描くのを拒んでいるものとして見せる（2026-10-22）:
  - 闇は顔に滑らかについていかない。止まったまま、目がずれたら一気に掴み直す（HOLD / SNAP）
    （1 回目の指摘「暗いところが目の動きに合わせてぬるぬるついてくるのは少し変」）
  - 闇の奥は静かな黒のまま。壊れるのは縁だけ。縁の上の数行は、行の途中まで描かれて止まった走査線になる。
    縁に近い行ほど多く長く、奥ほど少なく短い。画面が目の高さの行を描き切れずにいる
  - ときどき顔の数行だけが横に滑り、闇の中へはみ出す。色は元の画の色だけで、明るさは元を超えない
  - 荒れるのは闇が掴み直す瞬間だけ。それ以外のコマは縁の段がわずかに揺れるだけ
  （2〜4 回目でブロック・白く光る縞・UI 色の縞を試し、「粗目」「色が浮く」「白が多い・グリッチが多すぎる」で
  すべて捨てた。足すほど安っぽくなる。控えめにすること自体が「力で塞いでいる」見え方になる）
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
# 闇の縁の幅（目の間隔 d に対する比）。照明のぼけに見せないよう、元の 0.17 より狭い
SOFT = 0.07
# 闇の縁の高さ（両目の中点から下へ、d 単位）。説明の頭からおしまいへ向けて少しずつ下がる。
# 0.36 = 目の下まで闇 / 0.64 = 鼻の下まで闇。章の境目で値がつながる
EDGE_START, EDGE_END = 0.36, 0.64
# 各章の尺（秒）。縁の高さを説明全体の時刻で決めるのに使う
SCENE_SEC = {"introduction": 15.64, "subject": 20.76, "report": 19.72, "wear": 9.52}

# 闇の追従。目が SNAP_DIST（d 単位）以上ずれるか、HOLD_MIN〜HOLD_MAX コマ止まったら掴み直す
SNAP_DIST = 0.045
HOLD_MIN, HOLD_MAX = 5, 12
# 掴み直した直後の荒れ。ずれが BURST_FROM〜BURST_FULL（d 単位）で 0〜1。小さな掴み直しは荒らさない。
# 1 コマごとに BURST_DECAY 倍で引く
BURST_FROM, BURST_FULL = 0.06, 0.14
BURST_DECAY = 0.55
# 掴み直しが無くても、ときどき縁が荒れる（1 コマあたりの確率）
BURST_SPONTANEOUS = 0.012

# 縁の上で、行の途中まで描かれて止まった走査線が残る深さ（d 単位）。静かなときと荒れたとき
SCAN_CALM, SCAN_BURST = 0.20, 0.38
# 走査線の描き直しの間隔（コマ）。毎コマ変えると細かく震えて砂嵐に見える
STEP_HOLD = 3
# 顔の行が横に滑って闇へはみ出す本数（1 コマあたりの平均）。静かなときと荒れたとき
SLIP_CALM, SLIP_BURST = 0.6, 7.0
# はみ出した行の明るさ（元の何倍か）と、彩度をどこまで残すか
SLIP_GAIN = 0.7
SLIP_SATURATE = 0.6


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


def face_frame(shape, eyes):
    """顔座標。u = 横（d 単位）、v = 縦（d 単位・下向き）、s = 縁を測る縦（頬の側で少し持ち上がる）"""
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
    s = v + 0.07 * np.clip(u, -1.6, 1.6) ** 2 + 0.04 * u
    return u, v, s, d, cx


def gain_map(u, v, s, edge, scan):
    """1 = 元のまま、FLOOR = 闇の底。scan は縁の上に残る走査線（scan_cut）。"""
    # 縁から下は顔がそのまま見え、縁から上は走査線だけが描かれる
    face = np.maximum(smoothstep(edge - 0.3 * SOFT, edge + 0.5 * SOFT, s), scan)
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


def plan_scene(rows, seed):
    """各コマで闇が掴んでいる目の位置と、縁の荒れの強さを決める。

    闇は顔に滑らかについていかない。止まったまま待ち、目がずれたら一気に掴み直す。
    """
    rng = np.random.default_rng(seed)
    out = []
    cur = None
    held = 0
    limit = HOLD_MIN
    burst = 0.0
    for r in rows:
        e = np.asarray(r["eyesSmooth"], dtype=np.float64)
        d = float(np.hypot(*(e[1] - e[0])))
        dev = 0.0 if cur is None else float(np.abs(e - cur).max()) / d
        if cur is None or dev > SNAP_DIST or held >= limit:
            cur = e.copy()
            held = 0
            limit = int(rng.integers(HOLD_MIN, HOLD_MAX + 1))
            # 小さな掴み直しは荒らさない。大きく外れかけたときだけ
            burst = max(burst, float(smoothstep(BURST_FROM, BURST_FULL, dev)))
        else:
            held += 1
        if rng.random() < BURST_SPONTANEOUS:
            burst = max(burst, float(rng.uniform(0.4, 0.8)))
        out.append((cur.tolist(), burst))
        burst *= BURST_DECAY
    return out


def _row_noise(rng, h, sigma):
    """行ごとの乱数（標準偏差 1）。近い行どうしが似る"""
    n = rng.normal(0.0, 1.0, h + 64)
    k = np.exp(-0.5 * (np.arange(-24, 25) / sigma) ** 2)
    k /= np.sqrt((k ** 2).sum())
    return np.convolve(n, k, "same")[32:32 + h]


def scan_cut(u, s, edge, d, burst, frame_index, seed):
    """縁の上の、行の途中まで描かれて止まった走査線（0〜1）。

    縁に近い行ほど多く・長く描かれ、闇の奥へ行くほど本数が減って短くなる。どの行がどこまで描かれるかは
    STEP_HOLD コマ続ける。描かれた部分は元の画の色のまま、奥ほど暗い。
    """
    h, w = s.shape
    rng = np.random.default_rng((seed, frame_index // STEP_HOLD))
    depth = SCAN_CALM + (SCAN_BURST - SCAN_CALM) * burst
    r = (edge - s) / depth                       # 0 = 縁、1 = 走査線が届く奥
    on = rng.random(h)[:, None]                  # 行ごとの「生き残りやすさ」
    centre = rng.uniform(-0.7, 0.7, h)[:, None]  # 描かれる区間の中心（u）。行ごとにばらす
    reach = (0.25 + 1.0 * rng.random(h) ** 2)[:, None]  # 区間の長さの個体差（短い行が多い）
    half = np.sqrt(np.clip(1.0 - r, 0.0, 1.0)) * reach  # 奥ほど短い
    ends = 0.02
    seg = 1.0 - smoothstep(half - ends, half + ends, np.abs(u - centre))
    alive = (on > r) & (r >= 0.0) & (r < 1.0)
    return seg * alive * (1.0 - 0.55 * np.clip(r, 0.0, 1.0))


def slip(out, frame, u, s, edge, d, cx, burst, frame_index, seed):
    """顔の数行が横に滑って闇へはみ出す。縁のすぐ下の行の画を、闇の側の行へ横にずらして置く。"""
    h, w = out.shape[:2]
    rng = np.random.default_rng((seed, frame_index, 1))
    n = rng.poisson(SLIP_CALM + (SLIP_BURST - SLIP_CALM) * burst)
    if n == 0:
        return out
    col = np.arange(w, dtype=np.float32)
    mid = int(np.clip(cx, 0, w - 1))
    # 顔の中心の列で、縁の高さにある行
    edge_y = int(np.argmin(np.abs(s[:, mid] - edge)))
    for _ in range(n):
        hgt = int(rng.integers(1, 3 + int(2 * burst)))
        # はみ出す先: 縁から闇の側へ（荒れるほど奥まで）
        y = edge_y - int(rng.uniform(0.02, 0.15 + 0.2 * burst) * d)
        # 元の行: 縁のすぐ下の、まだ見えている顔
        ys = edge_y + int(rng.uniform(0.04, 0.16) * d)
        if y < 0 or ys + hgt > h:
            continue
        dx = int(rng.choice([-1, 1]) * rng.uniform(0.04, 0.25 + 0.35 * burst) * d)
        length = rng.uniform(0.4, 1.1 + 0.6 * burst) * d
        x0 = mid + rng.uniform(-0.8, 0.8) * d - length / 2
        # 端は数画素でほどける（定規で切った帯に見せない）
        ramp = 0.06 * d
        a = smoothstep(x0, x0 + ramp, col) * (1.0 - smoothstep(x0 + length - ramp, x0 + length, col))
        a = a * (1.0 - smoothstep(1.2, 1.6, np.abs(u[y, :])))
        src = frame[ys:ys + hgt][:, np.clip(np.arange(w) - dx, 0, w - 1)].astype(np.float32)
        lum = (src @ np.array([0.114, 0.587, 0.299], np.float32))[..., None]
        src = (lum + SLIP_SATURATE * (src - lum)) * SLIP_GAIN
        alpha = a[None, :, None]
        out[y:y + hgt] = out[y:y + hgt] * (1.0 - alpha) + src * alpha
    return out


def apply(frame, anchor, edge, burst, frame_index, seed, grain=True):
    u, v, s, d, cx = face_frame(frame.shape, anchor)
    scan = scan_cut(u, s, edge, d, burst, frame_index, seed)
    gain = gain_map(u, v, s, edge, scan)
    f = frame.astype(np.float32) * gain[..., None]
    f = slip(f, frame, u, s, edge, d, cx, burst, frame_index, seed)
    if grain:
        # 闇の中の量子化の縞を散らす
        rng = np.random.default_rng((seed, frame_index, 2))
        f += rng.normal(0.0, 1.2, f.shape[:2]).astype(np.float32)[..., None] * (1.0 - gain[..., None])
    return np.clip(f, 0, 255).astype(np.uint8)


def load_track():
    return json.loads(TRACK.read_text(encoding="utf-8"))["scenes"]


def scene_seed(scene):
    return SCENES.index(scene) + 1


def ffmpeg():
    exe = shutil.which("ffmpeg")
    if not exe:
        raise SystemExit("ffmpeg が見つからない")
    return exe


def render_scene(scene, rows):
    src = ASSET / f"{scene}-ja-v1.mp4"
    dst = ASSET / f"{scene}-ja-{VERSION}.mp4"
    poster = ASSET / f"{scene}-doctor-{VERSION}.jpg"
    plan = plan_scene(rows, scene_seed(scene))
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
        if i >= len(plan):
            raise SystemExit(f"{scene}: コマ数が追跡より多い")
        anchor, burst = plan[i]
        out = apply(frame, anchor, edge_at(scene, i), burst, i, scene_seed(scene))
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
    """元と焼いた版で、目元と口元の明るさの比を出す。目は落ち、口は残っていること。

    目元は闇が掴んでいる位置ではなく、追跡した実際の目で測る（止まっている間に目が闇から出ていないか）。
    """
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
            if i % 5 == 0:
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
        anchor, burst = plan_scene(track[args.scene]["rows"], scene_seed(args.scene))[args.frame]
        cv2.imwrite(str(args.still), apply(frame, anchor, edge_at(args.scene, args.frame), burst,
                                            args.frame, scene_seed(args.scene)))
        print(f"burst {burst:.2f}")
        return
    for scene in SCENES:
        dst, poster, n = render_scene(scene, track[scene]["rows"])
        print(f"{scene}: {n} コマ → {dst.name} / {poster.name}")
    raise SystemExit(1 if check(track) else 0)


if __name__ == "__main__":
    main()
