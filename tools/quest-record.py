#!/usr/bin/env python3
"""Quest の画面を録って、**HMD で見ている絵に近い片眼の動画**へ直す。

    python tools/quest-record.py --sec 45 --walk    # 自動走行させながら録る（導入演出の確認）
    python tools/quest-record.py --sec 30           # 通常起動で録る
    python tools/quest-record.py --raw <file.mp4>   # 既に撮ってある録画を変換するだけ

出力は `logs/capture/<日時>_eye.mp4`（H.264）と、変換前の生録画。

## なぜ変換が要るか

`adb shell screenrecord` が撮るのは**コンポジタが出力した最終フレーム**で、次の形をしている:

- **両眼が横に並んでいる**（左半分＝左眼 / 右半分＝右眼）
- 各眼の視野が **40 度ほど回転した台形**（Quest 3 のパンケーキレンズ用の逆歪みが掛かった状態）。
  HMD ではレンズがこの歪みを打ち消すので、生のままでは「見ている絵」にならない
- 周囲は黒

そこで **片眼だけを切り出し、台形の 4 隅を矩形へ戻す**（ホモグラフィ）。中央は実際の見た目とほぼ
一致し、周辺はレンズの樽型歪みぶんだけ残る。**厳密なレンズ補正ではない**が、演出の流れ・色・
枠の閉じ方・タイミングを見るには十分。

## パススルーは映る（重要）

導入演出は現実の映像が主役で、パススルーは OS のコンポジタが合成する。だからアプリの
フレームバッファには入っていない ── にもかかわらず、**screenrecord の出力には映る**（実測で確認）。
コンポジタ後を撮っているため。Unity 側に 2D カメラを置いて撮る方式ではパススルーが黒く抜けるので、
導入演出の確認にはこちらを使うこと。

## もっと忠実な方法（使えなかった）

Quest の内蔵録画（`/sdcard/Oculus/VideoShots/`）は **1920x1080・片眼・正立・歪み補正済み**で、
見た目としてはこちらが理想。ただし **adb からは起動できない**（`START_SPATIAL_CAPTURE` /
`vrshell LAUNCH systemux://capture` / `keyevent 130` をいずれも試して result=0）。システム権限が要る。
人が被って手で録るなら内蔵録画の方が良い。**自動で撮るならこのスクリプト。**
"""
from __future__ import annotations

import argparse
import os
import subprocess
import sys
import time
from datetime import datetime

import cv2
import numpy as np

PKG = "com.roiril.mawarimi"
ACT = f"{PKG}/com.unity3d.player.UnityPlayerActivity"
REMOTE = "/sdcard/quest-capture.mp4"
OUTDIR = os.path.join("logs", "capture")

# 非黒とみなす閾値。パススルーが暗い場面でも視野の輪郭は拾えるが、
# 真っ黒の段（導入の段 0）では輪郭が出ないので、**明るいフレームで 4 隅を決める**。
# ⚠ 8 では足りない。H.264 は黒を 0 にせず 16 前後で符号化するので、閾値が低いと
# **画面全体が輪郭として拾われ**、視野が黒枠の中に小さく収まった動画になる（実測で踏んだ）。
DARK_LEVEL = 28


def adb(serial, *args, **kw):
    cmd = ["adb"]
    if serial:
        cmd += ["-s", serial]
    cmd += [str(a) for a in args]
    env = dict(os.environ, MSYS_NO_PATHCONV="1")  # Git Bash が /sdcard/... を勝手に変換する
    return subprocess.run(cmd, capture_output=True, text=True, errors="replace",
                          env=env, **kw)


def pick_serial():
    out = subprocess.run(["python", "tools/quest-fleet.py", "pick"],
                         capture_output=True, text=True).stdout.strip()
    return out.splitlines()[0] if out else ""


# ---------------------------------------------------------------- 幾何

def find_eye_quad(frame, right_eye=True):
    """片眼の視野（回転した台形）の 4 隅を返す。反時計回り。"""
    h, w = frame.shape[:2]
    half = frame[:, w // 2:] if right_eye else frame[:, : w // 2]
    g = cv2.cvtColor(half, cv2.COLOR_BGR2GRAY)
    _, m = cv2.threshold(g, DARK_LEVEL, 255, cv2.THRESH_BINARY)
    m = cv2.morphologyEx(m, cv2.MORPH_CLOSE, np.ones((9, 9), np.uint8))
    cnts, _ = cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not cnts:
        return None, half.shape
    c = max(cnts, key=cv2.contourArea)
    # 視野は画面のそれなりの割合を占める。小さすぎるものはノイズ。
    if cv2.contourArea(c) < 0.02 * half.shape[0] * half.shape[1]:
        return None, half.shape
    peri = cv2.arcLength(c, True)
    quad = None
    for eps in (0.03, 0.04, 0.05, 0.06, 0.08):
        ap = cv2.approxPolyDP(c, eps * peri, True)
        if len(ap) == 4:
            quad = ap.reshape(4, 2).astype(np.float32)
            break
    if quad is None:                      # 4 頂点に落ちなければ最小外接矩形で代用
        quad = cv2.boxPoints(cv2.minAreaRect(c)).astype(np.float32)
    ctr = quad.mean(axis=0)
    ang = np.arctan2(quad[:, 1] - ctr[1], quad[:, 0] - ctr[0])
    quad = quad[np.argsort(ang)]                       # 反時計回りに並べる
    # 出力の左上を「元画像でいちばん左上にある隅」に固定する。角度ソートの開始点は視野の回転量で
    # 変わるので、これが無いと**同じコードで 90 度回った動画ができる**（実測で踏んだ）。
    quad = np.roll(quad, -int(np.argmin(quad[:, 0] + quad[:, 1])), axis=0)
    return quad, half.shape


def build_warp(quad):
    """4 隅 → 矩形のホモグラフィと出力サイズ。

    **1 フレームで求めて全フレームに使い回す。** 視野の位置は動かないので毎フレーム検出する必要が無く、
    検出が揺れて画がガタつくのを防げる（暗い段では輪郭が取れないという事情もある）。
    """
    d = [float(np.linalg.norm(quad[i] - quad[(i + 1) % 4])) for i in range(4)]
    W = int(round((d[0] + d[2]) / 2))
    H = int(round((d[1] + d[3]) / 2))
    W -= W % 2
    H -= H % 2
    dst = np.float32([[0, 0], [W, 0], [W, H], [0, H]])
    return cv2.getPerspectiveTransform(quad, dst), W, H


def brightest_frame_quad(path, right_eye=True, probe=40):
    """明るいフレームを探して 4 隅を決める。導入は真っ黒から始まるので先頭では取れない。"""
    cap = cv2.VideoCapture(path)
    n = int(cap.get(cv2.CAP_PROP_FRAME_COUNT)) or 1
    best, best_lum = None, -1.0
    for i in np.linspace(0, max(n - 1, 0), num=min(probe, max(n, 1)), dtype=int):
        cap.set(cv2.CAP_PROP_POS_FRAMES, int(i))
        ok, fr = cap.read()
        if not ok:
            continue
        lum = float(fr.mean())
        if lum > best_lum:
            q, _ = find_eye_quad(fr, right_eye)
            if q is not None:
                best, best_lum = q, lum
    cap.release()
    return best


# ---------------------------------------------------------------- 変換

def convert(raw_path, out_path, right_eye=True, scale=1.0):
    import imageio_ffmpeg as iio

    quad = brightest_frame_quad(raw_path, right_eye)
    if quad is None:
        print("could not find the eye viewport (screen stayed black?)", file=sys.stderr)
        return False
    M, W, H = build_warp(quad)
    if scale != 1.0:
        W = int(W * scale) // 2 * 2
        H = int(H * scale) // 2 * 2

    cap = cv2.VideoCapture(raw_path)
    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    fw = cap.get(cv2.CAP_PROP_FRAME_WIDTH)
    print("convert: src %dpx wide / %d frames / %.0ffps -> %dx%d" % (int(fw), total, fps, W, H))

    writer = iio.write_frames(out_path, (W, H), fps=fps, codec="libx264",
                              quality=6, macro_block_size=1, pix_fmt_in="rgb24")
    writer.send(None)
    i = 0
    while True:
        ok, fr = cap.read()
        if not ok:
            break
        half = fr[:, fr.shape[1] // 2:] if right_eye else fr[:, : fr.shape[1] // 2]
        eye = cv2.warpPerspective(half, M, (W, H), flags=cv2.INTER_LINEAR)
        writer.send(cv2.cvtColor(eye, cv2.COLOR_BGR2RGB).tobytes())
        i += 1
    writer.close()
    cap.release()
    print("wrote: %s (%d frames)" % (out_path, i))
    return True


# ---------------------------------------------------------------- 録画

def record(serial, secs, walk, size=None, warmup=5.0):
    """アプリを起動してから録る。

    **⚠ 順序を逆にしてはいけない。** screenrecord を先に始めて VR アプリを起動すると、
    アプリが VR モードへ入るときの画面モード変更で **録画が黙って止まり 0 バイトになる**（実測）。
    エラーも出ない。アプリを先に起動し、VR モードが安定してから録り始める。

    導入演出は「開始ラインを横切る」まで真っ暗のまま待つので、数秒遅れても全段撮れる
    （実測では起動から 21 秒後に段 1 が始まった）。
    """
    adb(serial, "shell", "input", "keyevent", "KEYCODE_WAKEUP")
    adb(serial, "shell", "am", "force-stop", PKG)
    adb(serial, "shell", "rm", "-f", REMOTE)

    start = ["shell", "am", "start"]
    if walk:
        start += ["-e", "xpwalk", "1"]
    start += ["-n", ACT]
    adb(serial, *start)
    print("app started; waiting %.0fs for VR mode" % warmup)
    time.sleep(warmup)

    args = ["shell", "screenrecord", "--time-limit", str(min(secs + 2, 180))]
    if size:
        args += ["--size", size]
    args += [REMOTE]
    env = dict(os.environ, MSYS_NO_PATHCONV="1")
    proc = subprocess.Popen(["adb", "-s", serial] + args, env=env,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    print("recording %ds ..." % secs)
    time.sleep(secs)
    try:
        proc.wait(timeout=40)
    except subprocess.TimeoutExpired:
        proc.kill()
    time.sleep(2)  # mp4 の moov atom が書かれるのを待つ
    return True


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--sec", type=int, default=45, help="録る秒数（screenrecord の上限は 180）")
    ap.add_argument("--walk", action="store_true", help="自動走行させる（導入が自動で始まる）")
    ap.add_argument("--serial", help="省略すると quest-fleet.py pick が選ぶ")
    ap.add_argument("--raw", help="録らずに、既にある mp4 を変換するだけ")
    ap.add_argument("--left", action="store_true", help="右眼でなく左眼を使う")
    ap.add_argument("--size", help='screenrecord の解像度 (例 "2064x2208")')
    ap.add_argument("--scale", type=float, default=1.0, help="出力の倍率")
    args = ap.parse_args()

    os.makedirs(OUTDIR, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")

    if args.raw:
        raw = args.raw
    else:
        serial = args.serial or pick_serial()
        if not serial:
            print("no usable Quest found", file=sys.stderr)
            return 1
        print("device: %s" % serial)
        record(serial, args.sec, args.walk, args.size)
        raw = os.path.join(OUTDIR, f"{stamp}_raw.mp4")
        r = adb(serial, "pull", REMOTE, raw)
        size_mb = os.path.getsize(raw) / 1048576 if os.path.exists(raw) else 0
        if size_mb < 0.1:
            print("recording came back empty: " + (r.stderr or r.stdout).strip(), file=sys.stderr)
            print("  (screenrecord stops silently if the display mode changes mid-record)",
                  file=sys.stderr)
            return 1
        print("pulled: %s (%.1f MB)" % (raw, size_mb))
        adb(serial, "shell", "rm", "-f", REMOTE)

    out = os.path.join(OUTDIR, f"{stamp}_eye.mp4")
    return 0 if convert(raw, out, right_eye=not args.left, scale=args.scale) else 1


if __name__ == "__main__":
    sys.exit(main())
