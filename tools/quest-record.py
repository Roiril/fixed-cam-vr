#!/usr/bin/env python3
"""Quest の画面を録って、**HMD で見ている絵に近い片眼の動画**へ直す。

    py -3.11 tools/quest-record.py --sec 45 --walk    # 自動走行させながら録る（導入演出の確認）
    py -3.11 tools/quest-record.py --sec 30           # 通常起動で録る
    py -3.11 tools/quest-record.py --raw <file.mp4>   # 既に撮ってある録画を変換するだけ

走行 1 回で次が揃う（`--no-log` / `--no-evidence` で個別に外せる）:

  - `logs/capture/<日時>_eye.mp4` … 片眼の動画（H.264）と、変換前の生録画
  - `logs/capture/<日時>_meta.json` … **録画開始の壁時計**とカメラの健康。xp-evidence.py が時刻を
    合わせるのに使う（これが無いと対応は推定になる）
  - `logs/capture/<日時>_xp.log` … 今回の走行ぶんだけに絞った logcat
  - `logs/capture/<日時>-report.md` … analyze-xp-log.py の判定
  - `logs/evidence/<日時>/` … xp-evidence.py が切り出した「その瞬間の画」

## 走行の前後でカメラの健康を控える

映像が暗くなった・粗くなったときに、原因が自分の修正なのか部屋の照明なのかを後から切り分けられない。
配信側の `/health` を走行の前後で控えて、**ISO が 2 倍以上動いていたら環境が変わったと言う**。
注意: 走行前は視聴者が居ないので `fps=0` が正常（需要駆動でエンコードを止めている）。ISO は
撮像側の実効値なので、その状態でも読める。

（この docstring は `--help` にそのまま出る。警告記号などの cp932 に無い文字を入れると
Windows 端末で `--help` が落ちるので、注意書きは「注意:」と書く）

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
import base64
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.request
from datetime import datetime

import cv2
import numpy as np

PKG = "com.roiril.mawarimi"
ACT = f"{PKG}/com.unity3d.player.UnityPlayerActivity"
REMOTE = "/sdcard/quest-capture.mp4"
OUTDIR = os.path.join("logs", "capture")
SHOW_JSON = os.path.join("tools", "web-compositor", "show.json")

# /health から控える項目。前半が映像の質、後半が熱による降格（fps が落ちた理由の切り分け）。
HEALTH_KEYS = ("iso", "expUs", "fps", "thermalStatus", "throttleStage", "latestFrameAgeMs")
HEALTH_TIMEOUT = 3.0
# ISO がこの倍率を超えて動いたら「部屋の明るさが変わった」と言う。
ISO_RATIO_WARN = 2.0

# logcat の取り方は run-quest-xp-test.sh と揃える。他プロセスがバッファを押し流すので、
# タグを絞らないと Unity の行が消える（実測でバッファの 76% が他プロセスだった）。
LOG_TAGS = ["Unity:V", "DEBUG:V", "*:E"]
XP_PID = re.compile(r"/\w+\s*\(\s*(\d+)\s*\)")

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
    # ⚠ encoding を書かないと locale 既定（Windows は cp932）で復号され、実機ログの日本語が
    # 壊れる。壊れた字は U+FFFD になるので**復元できない**。analyze-xp-log.py の
    # JP_WARN（見つかりません・失敗・出ません）が永久に一致しなくなる＝ 2026-07-31 に
    # 「日本語の警告を埋もれさせない」ために入れた仕掛けが丸ごと死ぬ。
    return subprocess.run(cmd, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", env=env, **kw)


def require_adb():
    """adb が無いと以降の呼び出しが全部空振りし、「実機が反応しない」に見える。ここで落とす。"""
    if shutil.which("adb") is None:
        print("adb not found in PATH", file=sys.stderr)
        sys.exit(2)


def pick_serial():
    # ⚠ PATH の `python` は Microsoft Store のスタブで、呼んでも版すら返さず空を出す。
    # 空を返されると「使える Quest が無い」に化ける。今動いている実体（sys.executable）を
    # そのまま使えば、どの呼び方（py -3.11 等）で入ってきても同じ Python に届く。
    p = subprocess.run([sys.executable, "tools/quest-fleet.py", "pick"],
                       capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    if p.returncode != 0:
        sys.stderr.write(p.stderr or "quest-fleet.py pick failed\n")
        return ""
    out = (p.stdout or "").strip()
    return out.splitlines()[0] if out else ""


# ---------------------------------------------------------------- カメラの健康

def read_cameras(show_path=SHOW_JSON):
    """show.json の配信カメラを読む。**読むだけで書かない**（現場設定は git 管理外）。"""
    if not os.path.exists(show_path):
        return []
    try:
        with open(show_path, "r", encoding="utf-8") as fh:
            show = json.load(fh)
    except (OSError, json.JSONDecodeError) as e:
        print("could not read %s: %s" % (show_path, e), file=sys.stderr)
        return []
    cams = []
    for i, c in enumerate(show.get("cameras") or []):
        host = (c.get("host") or "").strip()
        if not host:
            continue          # 現場に置いていないカメラ枠。接続しないのが正しい
        cams.append({"idx": i, "id": c.get("id") or str(i), "host": host,
                     "port": c.get("port") or 8080, "auth": c.get("auth") or ""})
    return cams


def probe_health(cam):
    """配信カメラの /health を 1 回叩く。届かなければ None。"""
    url = "http://%s:%s/health" % (cam["host"], cam["port"])
    req = urllib.request.Request(url)
    if cam["auth"]:
        token = base64.b64encode(cam["auth"].encode("utf-8")).decode("ascii")
        req.add_header("Authorization", "Basic %s" % token)
    try:
        body = urllib.request.urlopen(req, timeout=HEALTH_TIMEOUT).read(4096)
        j = json.loads(body.decode("utf-8"))
    except Exception as e:                       # 届かない・JSON でない・認証違い を区別しない
        return {"error": str(e)[:100]}
    return {k: j.get(k) for k in HEALTH_KEYS if k in j}


def health_snapshot(cams):
    return {str(c["idx"]): probe_health(c) for c in cams}


def compare_health(before, after):
    """走行の前後を比べて、ASCII の注意書きを返す（cp932 端末へ出すので日本語にしない）。"""
    notes = []
    for key in sorted(before, key=lambda k: int(k)):
        b, a = before.get(key) or {}, after.get(key) or {}
        if "error" in b and "error" in a:
            notes.append("cam%s: /health unreachable before and after (%s)" % (key, a["error"]))
            continue
        if "error" in a:
            notes.append("cam%s: /health went unreachable during the run (%s)" % (key, a["error"]))
            continue
        if "error" in b:
            notes.append("cam%s: /health was unreachable before the run" % key)
        iso_b, iso_a = b.get("iso"), a.get("iso")
        if isinstance(iso_b, (int, float)) and isinstance(iso_a, (int, float)) and iso_b > 0 and iso_a > 0:
            ratio = max(iso_a / iso_b, iso_b / iso_a)
            if ratio >= ISO_RATIO_WARN:
                notes.append("cam%s: ISO %d -> %d (x%.1f) - the room got %s, not your change"
                             % (key, iso_b, iso_a, ratio, "darker" if iso_a > iso_b else "brighter"))
        if a.get("throttleStage") in (1, 2):
            # streamer v0.11.0 からアプリは絞らない。これは「この先 OS が絞る」の警告。
            notes.append("cam%s: the phone is hot (stage %s) - the OS may throttle it; cool it down"
                         % (key, a.get("throttleStage")))
    return notes


def fmt_health(snap):
    out = []
    for key in sorted(snap, key=lambda k: int(k)):
        d = snap[key] or {}
        if "error" in d:
            out.append("cam%s unreachable" % key)
        else:
            out.append("cam%s " % key + " ".join("%s=%s" % (k, d[k]) for k in HEALTH_KEYS if k in d))
    return out


# ---------------------------------------------------------------- ログ

def slice_run(lines):
    """最後の走行ぶんだけを切り出す。(切り出した行, ログに居た走行数) を返す。

    ⚠ 最後の `ev=boot` 行から切ってはいけない。起動直後の警告（シェーダが見つからない 等）は
      boot より前に出る。切るのは **Unity プロセス（pid）の始まり**から。
    複数の走行が混ざったまま解析すると、演出も導入も 2 回ぶん出て判定が二重になる。
    """
    boots = [i for i, ln in enumerate(lines) if "ev=boot" in ln]
    if not boots:
        return lines, 0
    m = XP_PID.search(lines[boots[-1]])
    if not m:
        return lines[boots[-1]:], len(boots)
    pid = m.group(1)
    same = re.compile(r"\(\s*%s\s*\):" % re.escape(pid))
    start = next((i for i, ln in enumerate(lines) if same.search(ln)), boots[-1])
    return lines[start:], len(boots)


def collect_log(serial, dump_path, xp_path):
    """走行後のバッファを落として保存し、今回の走行ぶんを別ファイルへ切り出す。"""
    r = adb(serial, "logcat", "-d", "-v", "time", *LOG_TAGS)
    with open(dump_path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(r.stdout or "")
    lines = (r.stdout or "").splitlines(keepends=True)
    kept, n_boot = slice_run(lines)
    with open(xp_path, "w", encoding="utf-8", newline="\n") as fh:
        fh.writelines(kept)
    return n_boot, sum(1 for ln in kept if "[XP]" in ln)


def run_tool(script, *argv):
    """付属のツールを呼ぶ。落ちても録画そのものは残っているので、止めずに続ける。"""
    cmd = [sys.executable, os.path.join("tools", script), *[str(a) for a in argv]]
    print("$ " + " ".join(cmd))
    # 子ツールの print も UTF-8 で揃える（pipe 越しだと子側は locale 既定＝cp932 になり、
    # 日本語の要約が壊れて返る）。
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    r = subprocess.run(cmd, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", env=env)
    out = (r.stdout or "").strip()
    if out:
        print("\n".join("  " + ln for ln in out.splitlines()))
    if r.returncode != 0:
        err = (r.stderr or "").strip().splitlines()
        print("  ! %s failed (%d): %s" % (script, r.returncode, err[-1] if err else ""),
              file=sys.stderr)
    return r.returncode == 0


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

def clock_skew(serial: str) -> float | None:
    """**実機の時計 − PC の時計**（秒）。取れなければ None。

    ⚠⚠ **サイドカーの時刻と `[XP]` の時刻は別々の時計で刻まれている。**
    `record_started_iso` は PC の `datetime.now()`、`t=0 の壁時計` は logcat の行頭 ＝ **実機の時計**。
    2 つを引き算すると、**時計のずれがそのまま画と音のずれになる**。

    実測（2026-09-03）: この Quest は PC より **9.70 秒進んでいた**。おかげで
    `xp-evidence.py` が出した対応表は 9.35 秒ずれ、目の場面を切り出すと別の瞬間が出ていた。
    """
    try:
        t0 = datetime.now()
        r = subprocess.run(["adb", "-s", serial, "shell", "date", "+%s.%N"],
                           capture_output=True, text=True, timeout=20)
        t1 = datetime.now()
        dev = float(r.stdout.strip())
    except (ValueError, OSError, subprocess.SubprocessError):
        return None
    pc = (t0.timestamp() + t1.timestamp()) / 2.0
    return dev - pc


def record(serial, secs, walk, size=None, warmup=5.0, with_log=True):
    """アプリを起動してから録る。起動と録画開始の**壁時計**を返す。

    **⚠ 順序を逆にしてはいけない。** screenrecord を先に始めて VR アプリを起動すると、
    アプリが VR モードへ入るときの画面モード変更で **録画が黙って止まり 0 バイトになる**（実測）。
    エラーも出ない。アプリを先に起動し、VR モードが安定してから録り始める。

    導入演出は「開始ラインを横切る」まで真っ暗のまま待つので、数秒遅れても全段撮れる
    （実測では起動から 21 秒後に段 1 が始まった）。

    返す時刻はサイドカーに焼いて xp-evidence.py が使う。**ここを控えないと、ログと録画の対応は
    ファイル名からの推定になり数秒ずれる。**
    """
    adb(serial, "shell", "input", "keyevent", "KEYCODE_WAKEUP")
    adb(serial, "shell", "am", "force-stop", PKG)
    adb(serial, "shell", "rm", "-f", REMOTE)

    if with_log:
        # バッファは 16M へ広げる。`-G` は端末に拒まれても黙って成功を返すので、`-g` で読んで出す。
        adb(serial, "logcat", "-G", "16M")
        buf = (adb(serial, "logcat", "-g").stdout or "").splitlines()
        print("logcat buffer: %s" % (buf[0].strip() if buf else "?"))
        adb(serial, "logcat", "-c")   # 前回の走行を残さない（切り出しが確実になる）

    start = ["shell", "am", "start"]
    if walk:
        start += ["-e", "xpwalk", "1"]
    start += ["-n", ACT]
    adb(serial, *start)
    app_started = datetime.now()
    print("app started; waiting %.0fs for VR mode" % warmup)
    time.sleep(warmup)

    args = ["shell", "screenrecord", "--time-limit", str(min(secs + 2, 180))]
    if size:
        args += ["--size", size]
    args += [REMOTE]
    env = dict(os.environ, MSYS_NO_PATHCONV="1")
    proc = subprocess.Popen(["adb", "-s", serial] + args, env=env,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    rec_started = datetime.now()
    print("recording %ds ..." % secs)
    time.sleep(secs)
    try:
        proc.wait(timeout=40)
    except subprocess.TimeoutExpired:
        proc.kill()
    time.sleep(2)  # mp4 の moov atom が書かれるのを待つ
    return app_started, rec_started


# ---- 電池 -------------------------------------------------------------------
#
# Quest は思ったより早く空になる。走行のたびに数分ずつ食うので、気づいたら
# 「検証を続けたいのに残量が無い」になる（2026-08-01 ユーザー指摘）。
#   - 走る前に残量を見て、**充電されていない & 少ない**なら止める
#   - 走り終わったら**寝かせる**。起きたまま放置すると何もしていなくても減る
LOW_BATTERY_PCT = 25      # これを下回っていて充電もされていなければ走らない
WARN_BATTERY_PCT = 40     # 走るが、先に充電を勧める


def read_battery(serial):
    """(残量%, 充電中か) を返す。読めなければ (None, None)。"""
    try:
        out = subprocess.run(["adb", "-s", serial, "shell", "dumpsys", "battery"],
                             capture_output=True, text=True,
                             encoding="utf-8", errors="replace", timeout=15).stdout
    except Exception:
        return None, None
    level = charging = None
    for ln in out.splitlines():
        k, _, v = ln.partition(":")
        k, v = k.strip(), v.strip()
        if k == "level" and v.isdigit():
            level = int(v)
        elif k in ("AC powered", "USB powered", "Wireless powered", "Dock powered"):
            charging = charging or (v == "true")
    return level, charging


def check_battery(serial):
    """走ってよいかを返す。False なら呼び手は中止する。"""
    level, charging = read_battery(serial)
    if level is None:
        print("  battery: 読めなかった（続行する）")
        return True
    state = "充電中" if charging else "充電していない"
    print("  battery: %d%% / %s" % (level, state))
    if charging or level >= WARN_BATTERY_PCT:
        return True
    if level < LOW_BATTERY_PCT:
        print("  ! 残量 %d%% で充電もされていない。走行を中止する。" % level,
              file=sys.stderr)
        print("    充電ケーブルを挿すか、`py -3.11 tools/quest-fleet.py sleep %s` で"
              "寝かせて回復を待つ。" % serial, file=sys.stderr)
        return False
    print("  ! 残量が少ない。続けるが、充電ケーブルを挿しておくと途中で切れない。")
    return True


def sleep_device(serial):
    """走り終わったら寝かせる。起きたまま放置すると何もしなくても減る。"""
    try:
        subprocess.run(["adb", "-s", serial, "shell", "input", "keyevent", "KEYCODE_SLEEP"],
                       capture_output=True, timeout=15)
        print("  device   : スリープさせた（次に使うときは自動で起きる）")
    except Exception as e:
        print("  device   : スリープさせられなかった: %s" % e)


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
    ap.add_argument("--show", default=SHOW_JSON, help="カメラの接続先を読む show.json")
    ap.add_argument("--no-log", action="store_true", help="logcat の収集と判定を飛ばす")
    ap.add_argument("--no-evidence", action="store_true", help="画の切り出しを飛ばす")
    ap.add_argument("--keep-awake", action="store_true",
                    help="走行後に実機を寝かせない（続けて何度も走らせるとき）")
    args = ap.parse_args()

    os.makedirs(OUTDIR, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")

    if args.raw:
        # 変換だけ。走行していないのでログもカメラの健康も無い。
        out = os.path.join(OUTDIR, f"{stamp}_eye.mp4")
        return 0 if convert(args.raw, out, right_eye=not args.left, scale=args.scale) else 1

    require_adb()   # --raw は実機を触らないので、ここから先だけで要る
    serial = args.serial or pick_serial()
    if not serial:
        print("no usable Quest found", file=sys.stderr)
        return 1
    print("device: %s" % serial)
    if not check_battery(serial):
        return 2

    cams = read_cameras(args.show)
    before = health_snapshot(cams)
    for ln in fmt_health(before):
        print("  before: " + ln)

    app_started, rec_started = record(serial, args.sec, args.walk, args.size,
                                      with_log=not args.no_log)

    # ログはバッファに溜まる一方なので、重い pull より先に落とす。
    dump_path = os.path.join(OUTDIR, f"{stamp}_logcat.log")
    xp_path = os.path.join(OUTDIR, f"{stamp}_xp.log")
    n_boot = n_xp = 0
    if not args.no_log:
        n_boot, n_xp = collect_log(serial, dump_path, xp_path)
        print("log: %s ([XP] %d lines / %d run(s) in the buffer)" % (xp_path, n_xp, n_boot))
        if n_xp < 20:
            print("  ! too few [XP] lines - check that this is a Development build", file=sys.stderr)

    after = health_snapshot(cams)
    for ln in fmt_health(after):
        print("  after:  " + ln)

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

    # 実機と PC の時計のずれ（サイドカーの時刻を `[XP]` と突き合わせるのに要る）。
    skew = clock_skew(serial)
    if skew is None:
        print("  ⚠ 実機の時計を読めなかった（画と音の対応が数秒ずれる）")
    elif abs(skew) > 1.0:
        print("  実機の時計は PC より %+.2f 秒（サイドカーに書いた。対応表はこれで補正する）" % skew)

    # サイドカーは変換の前に書く。ここが xp-evidence.py の時刻対応の正になる。
    meta = {
        "serial": serial,
        "walk": bool(args.walk),
        "sec": args.sec,
        "app_started_iso": app_started.isoformat(timespec="milliseconds"),
        "record_started_iso": rec_started.isoformat(timespec="milliseconds"),
        # ⚠⚠ **上の 2 つは PC の時計。`[XP]` の時刻は実機の時計（logcat の行頭）。**
        #    引き算するときはこのずれを足さないと、その分だけ画と音がずれる（2026-09-03 実測 9.70 秒）。
        "device_clock_skew_sec": skew,
        "camera_health_before": before,
        "camera_health_after": after,
    }
    meta_path = os.path.join(OUTDIR, f"{stamp}_meta.json")
    with open(meta_path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(meta, fh, ensure_ascii=False, indent=1)

    out = os.path.join(OUTDIR, f"{stamp}_eye.mp4")
    if not convert(raw, out, right_eye=not args.left, scale=args.scale):
        return 1

    report = os.path.join(OUTDIR, f"{stamp}-report.md")
    evidence = os.path.join("logs", "evidence", stamp)
    if not args.no_log:
        run_tool("analyze-xp-log.py", xp_path, "--show", args.show, "--out", report)
        if not args.no_evidence:
            run_tool("xp-evidence.py", xp_path, out, "--out", evidence)

    # ---- まとめ（1 画面に収める）
    print("")
    print("== summary ==")
    print("  video    : %s" % out)
    if args.no_log:
        print("  log      : skipped (--no-log)")
    else:
        print("  log      : %s ([XP] %d lines)" % (xp_path, n_xp))
        print("  report   : %s" % report)
        if os.path.exists(report):
            with open(report, "r", encoding="utf-8") as fh:
                body = fh.read()
            print("  verdict  : FAIL=%d WARN=%d OK=%d"
                  % (body.count("- ❌"), body.count("- ⚠"), body.count("- ✅")))
        print("  evidence : %s" % ("skipped (--no-evidence)" if args.no_evidence else evidence))
    notes = compare_health(before, after)
    if notes:
        print("  cameras  :")
        for n in notes:
            print("    ! " + n)
    elif cams:
        print("  cameras  : no change worth reporting (%d checked)" % len(cams))
    if not args.keep_awake:
        sleep_device(serial)
    return 0


if __name__ == "__main__":
    sys.exit(main())
