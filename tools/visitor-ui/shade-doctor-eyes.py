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

闇は光ではなく、画面の側が目を塞いでいるものとして見せる（2026-10-22 ユーザー指摘
「暗いところが目の動きに合わせてぬるぬるついてくるのは少し変」）:
  - 闇は顔に滑らかについていかない。止まったまま、目がずれたら一気に掴み直す（HOLD / SNAP）
  - 掴み直した直後は縁が荒れる（BURST）
  - 縁に近づくほど画が壊れる。横に裂ける行・ずれるブロック・色のずれ・黒い四角
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

# 闇の追従。目が SNAP_DIST（d 単位）以上ずれるか、HOLD_MIN〜HOLD_MAX コマ止まったら掴み直す
SNAP_DIST = 0.045
HOLD_MIN, HOLD_MAX = 5, 12
# 掴み直した直後の荒れ。ずれの大きさで強さを決め、1 コマごとに BURST_DECAY 倍で引く
BURST_FULL = 0.09
BURST_DECAY = 0.6
# 掴み直しが無くても、ときどき縁が荒れる（1 コマあたりの確率）
BURST_SPONTANEOUS = 0.025
# 壊れる帯の幅（d 単位）。縁から闇の内側へ GLITCH_IN、外側へ GLITCH_OUT。
# 外側は口元（1.05d ± 0.22d）にかからないように狭く取る
GLITCH_IN, GLITCH_OUT = 0.50, 0.14
# ブロックの大きさ（画素）。映像の符号化の単位に近い横長
BLOCK_W, BLOCK_H = 16, 8


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


def shade(shape, eyes, edge):
    """闇の倍率 gain（1 = 元のまま、FLOOR = 闇の底）と、壊れる帯の強さ band（0〜1）を返す。

    eyes = [[lx, ly], [rx, ry]]（画素）。縁は顔の傾きに合わせて傾き、頬の側でわずかに持ち上がる。
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
    s = v + 0.07 * np.clip(u, -1.6, 1.6) ** 2 + 0.04 * u
    face = smoothstep(edge - SOFT, edge + SOFT, s)
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
    gain = gain * (1.0 - EYE_EXTRA * sockets * (1.0 - lit))
    # 壊れる帯: 闇の奥では静か、縁に近づくほど強い。縁を越えるとすぐ消える。顔の幅の外には出さない
    band = smoothstep(edge - GLITCH_IN, edge - 0.03, s) * (1.0 - smoothstep(edge + 0.02, edge + GLITCH_OUT, s))
    band *= 1.0 - smoothstep(1.0, 1.7, np.abs(u))
    band *= 1.0 - smoothstep(1.3, 1.6, v)
    # 闇の奥まで浅く染み込ませる（縁だけの帯に見せない）
    band = band ** 0.7
    return gain, band, d


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
            burst = max(burst, min(1.0, 0.25 + dev / BURST_FULL))
        else:
            held += 1
        if rng.random() < BURST_SPONTANEOUS:
            burst = max(burst, float(rng.uniform(0.5, 1.0)))
        out.append((cur.tolist(), burst))
        burst *= BURST_DECAY
    return out


def block_field(shape):
    h, w = shape[:2]
    by = (np.arange(h) // BLOCK_H)[:, None]
    bx = (np.arange(w) // BLOCK_W)[None, :]
    return by, bx, (h + BLOCK_H - 1) // BLOCK_H, (w + BLOCK_W - 1) // BLOCK_W


def block_mean(a, nby, nbx):
    h, w = a.shape
    p = np.zeros((nby * BLOCK_H, nbx * BLOCK_W), np.float32)
    p[:h, :w] = a
    return p.reshape(nby, BLOCK_H, nbx, BLOCK_W).mean(axis=(1, 3))


def glitch(img, band, d, burst, frame_index, seed):
    """縁の帯の中で画を壊す。img は闇を掛けた後の float の画。"""
    h, w = band.shape
    # ブロックの模様は 2 コマごとに変える（12.5fps でかくつく）。行の裂けは毎コマ
    rb = np.random.default_rng((seed, frame_index // 2))
    rr = np.random.default_rng((seed, frame_index, 1))
    amp = 0.55 + 0.75 * burst
    by, bx, nby, nbx = block_field(band.shape)
    xs = np.arange(w)[None, :].repeat(h, 0)
    rows = np.arange(h)[:, None].repeat(w, 1)
    out = img.copy()

    # 1) 行が横に裂ける。数行ずつの束が帯の中だけ横へずれる
    row_band = band.max(axis=1)
    y = 0
    while y < h:
        hgt = int(rr.integers(1, 7))
        if row_band[y] > 0.05 and rr.random() < row_band[y] * 0.55 * amp:
            dx = int(rr.normal(0, 0.16 + 0.22 * burst) * d)
            sl = slice(y, min(h, y + hgt))
            src = np.clip(xs[sl] - dx, 0, w - 1)
            torn = img[rows[sl], src]
            hit = band[sl] > rr.uniform(0.08, 0.5)
            out[sl][hit] = torn[hit]
        y += hgt

    # 2) ブロック単位の破損。縁に近いブロックほど壊れる
    bb = block_mean(band, nby, nbx)
    eaten = rb.random((nby, nbx)) < np.clip(bb ** 1.2 * 0.9 * amp, 0, 0.95)
    kind = rb.random((nby, nbx))
    dxb = (rb.integers(-6, 7, (nby, nbx)) * BLOCK_W * (0.6 + burst)).astype(np.int32)
    # 下の明るい顔を闇の中へ引きずり上げるブロックも混ぜる（縦は 0〜2 ブロック）
    dyb = (rb.integers(0, 3, (nby, nbx)) * BLOCK_H).astype(np.int32)
    e_px = eaten[by, bx]
    k_px = kind[by, bx]
    #  a. 黒い四角。闇が角張った形で縁を押し出す
    black = e_px & (k_px < 0.42)
    out[black] *= 0.12
    #  b. 横にずれたブロック。別の場所の画が四角く差し込まれる
    shift = e_px & (k_px >= 0.42) & (k_px < 0.78)
    src = np.clip(xs + dxb[by, bx], 0, w - 1)
    srcy = np.clip(rows + dyb[by, bx], 0, h - 1)
    out[shift] = img[srcy, src][shift]
    #  c. 色の割れ。赤と青が左右へ分かれ、階調が潰れる
    split = e_px & (k_px >= 0.78)
    k = max(2, int(0.05 * d * (1 + burst)))
    sp = img.copy()
    sp[..., 2] = img[rows, np.clip(xs + k, 0, w - 1), 2]
    sp[..., 0] = img[rows, np.clip(xs - k, 0, w - 1), 0]
    sp = np.floor(sp / 48.0) * 48.0 + 12.0
    out[split] = sp[split]

    # 3) 走査線の欠け。帯の中の 1 行だけが暗く抜ける / 白く光る
    for yy in np.nonzero(row_band > 0.3)[0]:
        p = rr.random()
        if p < 0.05 * amp:
            hit = band[yy] > 0.3
            out[yy, hit] *= 0.2
        elif p < 0.058 * amp:
            # 白く光るのは行の一部だけ（全幅だと目隠しの棒の縁取りに見える）
            x0 = int(rr.integers(0, w))
            seg = np.zeros(w, bool)
            seg[x0:x0 + int(rr.uniform(0.2, 0.9) * d)] = True
            hit = (band[yy] > 0.45) & seg
            out[yy, hit] = out[yy, hit] * 0.4 + 140.0
    return out


def apply(frame, anchor, edge, burst, frame_index, seed, grain=True):
    gain, band, d = shade(frame.shape, anchor, edge)
    f = frame.astype(np.float32) * gain[..., None]
    f = glitch(f, band, d, burst, frame_index, seed)
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
        return
    for scene in SCENES:
        dst, poster, n = render_scene(scene, track[scene]["rows"])
        print(f"{scene}: {n} コマ → {dst.name} / {poster.name}")
    raise SystemExit(1 if check(track) else 0)


if __name__ == "__main__":
    main()
