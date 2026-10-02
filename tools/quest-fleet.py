#!/usr/bin/env python3
"""接続中の Quest を 2 台以上まとめて見て、検証の走行を熱の低い機へ振り分ける。

    py -3.11 tools/quest-fleet.py list           # 全機の状態を 1 画面で
    py -3.11 tools/quest-fleet.py pick           # 次に走らせる機の serial を 1 行だけ返す
    py -3.11 tools/quest-fleet.py sleep <serial> # 寝かせる（--others <serial> で「それ以外全部」）
    py -3.11 tools/quest-fleet.py wake  <serial>
    py -3.11 tools/quest-fleet.py sync           # Builds/mawarimi.apk を古い機へ配る
    py -3.11 tools/quest-fleet.py mark  <serial> --sec 300   # 走行を記録（run スクリプトが呼ぶ）
    py -3.11 tools/quest-fleet.py pull  <serial> [--out logs/device]

**なぜ熱で選ぶか。** 2 台とも AC 給電なので電池残量は制約にならない。実際の制約は SoC 温度で、
Quest 3 はファンを持たないため走行のたびに温まり、下がるのに数分かかる。走った直後の機は熱く、
もう一方は冷えている ── だから「温度の低い方」を選ぶと、待ち時間ゼロで自然に交互運用になる。
温度が拮抗したときだけ「前回使っていない方」で決める。

温度は `dumpsys thermalservice` の **Current temperatures from HAL** を読む。同じ dumpsys の
先頭にある `Cached temperatures` は更新が遅れていて、スリープ中の機が 46℃ に見えるなど実測と
10℃ 以上ずれる（実機で確認済み）。

標準出力は ASCII だけにしてある（Windows 端末は cp932 で日本語が壊れるため）。
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
from datetime import datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from android_devices import (AdbNotFoundError, adb_command, list_android_devices,
                             resolve_adb)

PKG = "com.roiril.mawarimi"
FILES_DIR = f"/sdcard/Android/data/{PKG}/files"
CACHE_DIR = f"/sdcard/Android/data/{PKG}/cache"
XPLOG_DIR = f"{FILES_DIR}/xplog"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
STATE_PATH = os.path.join(ROOT, "logs", "quest-fleet.json")
DEFAULT_APK = os.path.join(ROOT, "Builds", "mawarimi.apk")

# 温度がこの幅に収まっていたら「同じくらい」とみなし、前回使っていない方を選ぶ。
TEMP_TIE_C = 3.0
# Android の thermal status。2 (MODERATE) 以上は走らせない方が良い。
THERMAL_NAMES = {0: "NONE", 1: "LIGHT", 2: "MODERATE", 3: "SEVERE",
                 4: "CRITICAL", 5: "EMERGENCY", 6: "SHUTDOWN"}
THERMAL_BLOCK = 2


# ---------------------------------------------------------------- adb

def adb(serial, *args, timeout=20):
    try:
        cmd = adb_command(resolve_adb(), serial, *args)
    except AdbNotFoundError as e:
        return 127, "", str(e)
    try:
        # encoding 明示は必須。locale 既定（cp932）だと実機ログ・端末名の日本語が
        # U+FFFD へ潰れて復元できなくなる（quest-record.py の adb() と同じ理由）。
        p = subprocess.run(cmd, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=timeout)
        return p.returncode, p.stdout.replace("\r", ""), p.stderr.replace("\r", "")
    except subprocess.TimeoutExpired:
        return 124, "", "timeout"
    except FileNotFoundError as e:
        return 127, "", str(e)


_SKIP_REPORTED = set()


def list_devices(all_devices=False):
    """
    `adb devices -l` の結果を [{serial,state,model}] で返す。

    ⚠ **Quest 以外は既定で外す。** この機体には配信用の Pixel も刺さっていて、
    `sync` が **VR の APK を配信スマホへも入れていた**（2026-08-13 実害。103MB が黙って居座る）。
    `stop` / `sleep` も同じ list を使うので、配信中のスマホを止めに行く形が原理的にありえた。

    ⚠ **model が読めない機は落とさない。** `unauthorized` の Quest は model を出さないので、
    そこで消すと「繋がらない機を調べる」用途（`memory/quest_adb_auth.md`）が成り立たなくなる。
    落とすのは「model が読めて、しかも Quest ではない」ときだけ。
    """
    try:
        found = list_android_devices(
            lambda cmd, timeout=20: _run_adb_command(cmd, timeout),
            resolve_adb(), NAMES)
    except (AdbNotFoundError, RuntimeError) as e:
        if "adb" not in _SKIP_REPORTED:
            _SKIP_REPORTED.add("adb")
            print(str(e), file=sys.stderr)
        return []
    res = []
    for item in found:
        if not all_devices and item["state"] == "device" and not item["isQuest"]:
            if item["serial"] not in _SKIP_REPORTED:
                _SKIP_REPORTED.add(item["serial"])
                print(f"  {item['serial']}  {item['model'] or 'Android'} は Quest ではないので対象外",
                      file=sys.stderr)
            continue
        res.append(item)
    return res


def _run_adb_command(cmd, timeout=20):
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8",
                           errors="replace", timeout=timeout)
        return p.returncode, p.stdout.replace("\r", ""), p.stderr.replace("\r", "")
    except subprocess.TimeoutExpired:
        return 124, "", "timeout"
    except FileNotFoundError as e:
        return 127, "", str(e)


# ---------------------------------------------------------------- 観測

_TEMP_RE = re.compile(r"mValue=([-\d.]+),\s*mType=(\d+),\s*mName=([\w-]+)")


def read_thermal(serial):
    """SoC / バッテリー温度と thermal status。**Current temperatures from HAL** の側を読む。"""
    _, out, _ = adb(serial, "shell", "dumpsys thermalservice")
    status = None
    m = re.search(r"Thermal Status:\s*(\d+)", out)
    if m:
        status = int(m.group(1))

    # "Current temperatures from HAL:" 以降だけを見る（Cached は古い）。
    idx = out.find("Current temperatures from HAL")
    body = out[idx:] if idx >= 0 else out
    end = body.find("Current cooling devices")
    if end > 0:
        body = body[:end]

    temps = {}
    for mv, _mt, name in _TEMP_RE.findall(body):
        try:
            temps[name] = float(mv)
        except ValueError:
            pass
    return {
        "thermal_status": status,
        "soc_c": temps.get("soc-usr"),
        "batt_c": temps.get("batt-virt-usr"),
        "pcb_c": temps.get("pcb1-usr"),
    }


def read_battery(serial):
    _, out, _ = adb(serial, "shell", "dumpsys battery")
    d = {}
    for line in out.splitlines():
        line = line.strip()
        if ":" not in line:
            continue
        k, _, v = line.partition(":")
        d[k.strip()] = v.strip()
    level = d.get("level")
    return {
        "level": int(level) if level and level.isdigit() else None,
        "charging": d.get("AC powered") == "true" or d.get("USB powered") == "true",
    }


def read_wake(serial):
    _, out, _ = adb(serial, "shell", "dumpsys power | grep mWakefulness=")
    m = re.search(r"mWakefulness=(\w+)", out)
    return m.group(1) if m else "?"


def read_app(serial):
    """インストール済み APK の更新時刻とバージョン。未インストールなら None。"""
    _, out, _ = adb(serial, "shell", f"dumpsys package {PKG}")
    if "Unable to find package" in out or not out.strip():
        return {"installed": False, "updated": None, "version": None}
    upd = re.search(r"lastUpdateTime=([\d\- :]+)", out)
    ver = re.search(r"versionName=(\S+)", out)
    return {
        "installed": True,
        "updated": upd.group(1).strip() if upd else None,
        "version": ver.group(1) if ver else None,
    }


def read_registration(serial):
    """位置合わせ（course 登録）。**端末ローカルの物理的事実なので機の間でコピーできない。**

    トラッキング原点が機ごとに違うため、同じ現場でも originXZ / yawDeg は一致しない
    （実測: 2 台で yaw が 10 度違った）。交互運用の前提は「各機が自分の登録を持っていること」。
    """
    _, out, _ = adb(serial, "shell", f"cat {FILES_DIR}/registration.json 2>/dev/null")
    out = out.strip()
    if not out or out.startswith("cat:"):
        return {"has": False}
    try:
        j = json.loads(out)
    except json.JSONDecodeError:
        return {"has": False, "broken": True}
    return {
        "has": True,
        "residual_m": j.get("maxResidualM"),
        "points": j.get("pointCount"),
        "saved": j.get("savedAtIso"),
    }


def read_wifi(serial):
    """繋がっている AP の帯域とリンク速度。

    **これは検証結果を左右する一次条件。** 2.4GHz だと MJPEG の受信 fps が配信の 2/3 まで落ち、
    導入演出の最後の段（Swap）は「映像が届いていること」を要求するので、帯域が細いだけで
    **演出が出ない**（原因をコードだと取り違える）。実測で 2 台が別々の AP に繋がっていたので、
    走らせる前に必ず見えるようにしておく。
    """
    _, out, _ = adb(serial, "shell", "dumpsys wifi | grep -m1 'mWifiInfo SSID'")
    ssid = re.search(r'SSID: "([^"]*)"', out)
    freq = re.search(r"Frequency: (\d+)MHz", out)
    speed = re.search(r"Link speed: (\d+)Mbps", out)
    rssi = re.search(r"RSSI: (-?\d+)", out)
    mhz = int(freq.group(1)) if freq else 0
    return {
        "ssid": ssid.group(1) if ssid else "-",
        "mhz": mhz,
        "band": "5G" if mhz >= 5000 else ("2.4G" if mhz else "-"),
        "mbps": int(speed.group(1)) if speed else 0,
        "rssi": int(rssi.group(1)) if rssi else 0,
    }


def read_storage(serial):
    """端末内録画とログの占有量 (MB)。走行を重ねると溜まるので見えるようにする。"""
    def du(path):
        _, out, _ = adb(serial, "shell", f"du -sk {path} 2>/dev/null")
        m = re.match(r"(\d+)", out.strip())
        return round(int(m.group(1)) / 1024.0, 1) if m else 0.0
    return {"rec_mb": du(f"{CACHE_DIR}/rec"), "log_mb": du(XPLOG_DIR)}


def probe(serial, deep=True):
    d = {"serial": serial}
    d.update(read_thermal(serial))
    d.update({"batt": read_battery(serial)})
    d["wake"] = read_wake(serial)
    d["app"] = read_app(serial)
    d["wifi"] = read_wifi(serial)
    if deep:
        d["reg"] = read_registration(serial)
        d["storage"] = read_storage(serial)
    return d


# ---------------------------------------------------------------- 状態ファイル

def load_state():
    try:
        with open(STATE_PATH, "r", encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, json.JSONDecodeError):
        return {"lastUsed": None, "runs": {}}


def save_state(st):
    os.makedirs(os.path.dirname(STATE_PATH), exist_ok=True)
    with open(STATE_PATH, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(st, fh, ensure_ascii=False, indent=2)


# ---------------------------------------------------------------- 選択

# 充電していない機がこれを下回っていたら pick から外す（走行の途中で切れると撮り直しになる）。
LOW_BATTERY_PCT = 25


def _battery_weak(dev):
    batt = dev.get("batt") or {}
    if batt.get("charging"):
        return False
    level = batt.get("level")
    return level is not None and level < LOW_BATTERY_PCT


def score(dev, state):
    """小さいほど「次に走らせるのに向いている」。"""
    soc = dev.get("soc_c")
    return soc if soc is not None else 999.0


def pick(devs, state, require_app=True, require_reg=False):
    """走らせる機を 1 台選ぶ。戻り値は (選んだ dev, 理由の文字列, 警告リスト)。"""
    warns = []
    cand = [d for d in devs if d.get("_online")]
    if not cand:
        return None, "no online device", warns

    if require_app:
        with_app = [d for d in cand if d["app"]["installed"]]
        if not with_app:
            warns.append("no device has %s installed" % PKG)
        else:
            if len(with_app) < len(cand):
                warns.append("%d device(s) lack the app; excluded" % (len(cand) - len(with_app)))
            cand = with_app

    if require_reg:
        with_reg = [d for d in cand if d.get("reg", {}).get("has")]
        if not with_reg:
            warns.append("no device has registration.json (course alignment)")
        else:
            cand = with_reg

    # 電池が乏しい機は外す。走行 1 回で数分ずつ食うので、選んだ先で切れると走行が無駄になる
    # （2026-08-01 ユーザー指摘「バッテリーが意外とすぐ切れる」）。充電中なら気にしない。
    weak = [d for d in cand if _battery_weak(d)]
    if weak and len(weak) < len(cand):
        warns.append("%d device(s) low on battery and not charging; excluded" % len(weak))
        cand = [d for d in cand if d not in weak]
    elif weak:
        warns.append("ALL devices are low on battery -- charge or sleep them and wait")

    hot = [d for d in cand
           if d.get("thermal_status") is not None and d["thermal_status"] >= THERMAL_BLOCK]
    if hot and len(hot) < len(cand):
        warns.append("%d device(s) throttling; excluded" % len(hot))
        cand = [d for d in cand if d not in hot]
    elif hot:
        warns.append("ALL devices are throttling -- results will be degraded")

    cand.sort(key=lambda d: score(d, state))
    best = cand[0]
    reason = "coolest (soc %.1fC)" % best["soc_c"] if best.get("soc_c") is not None else "only choice"

    # 温度が拮抗しているなら、前回使っていない方に回す（自然に交互になる）。
    if len(cand) > 1 and cand[0].get("soc_c") is not None and cand[1].get("soc_c") is not None:
        if abs(cand[0]["soc_c"] - cand[1]["soc_c"]) <= TEMP_TIE_C:
            last = state.get("lastUsed")
            alt = [d for d in cand if d.get("physicalSerial", d["serial"]) != last]
            if last and alt and best.get("physicalSerial", best["serial"]) == last:
                best = alt[0]
                reason = "temps within %.0fC; rotating away from last used" % TEMP_TIE_C
    return best, reason, warns


# ---------------------------------------------------------------- 表示

# ⚠⚠ **2 台の呼び名**（2026-09-04 ユーザー宣言）。ユーザーはこの名前で機を指す。
#    **serial から名前は導けないので、ここが唯一の対応表**（`.claude/memory/quest_fleet_two_devices.md` と対）。
#    α = **自動走行を回す機**（ユーザーが被って見る側）／ β = もう 1 台。
#    ⚠ 機を入れ替えたらここを直す。直さないと、人と機械が別の機を指したまま会話が進む。
NAMES = {
    "2G0YC1ZF890864": "a",   # クエストα
    "2G0YC1ZF7S06BW": "b",   # クエストβ
}


def name_of(serial):
    """serial → 'a' / 'b'（未登録は '?'）。人へ出すときは「クエストα」「クエストβ」。"""
    return NAMES.get(serial, "?")


def fmt_row(d, state):
    soc = "%.1f" % d["soc_c"] if d.get("soc_c") is not None else "-"
    bt = "%.1f" % d["batt_c"] if d.get("batt_c") is not None else "-"
    ts = d.get("thermal_status")
    tname = THERMAL_NAMES.get(ts, "?") if ts is not None else "?"
    lv = d["batt"]["level"]
    lvs = ("%d%%" % lv) if lv is not None else "-"
    if d["batt"]["charging"]:
        lvs += "+"
    app = d["app"]
    apps = (app["updated"] or "?") if app["installed"] else "NOT INSTALLED"
    reg = d.get("reg", {})
    if reg.get("has"):
        r = reg.get("residual_m")
        regs = "ok %.0fcm" % (r * 100) if isinstance(r, (int, float)) else "ok"
    else:
        regs = "MISSING"
    stg = d.get("storage", {})
    used = "rec%.0f" % stg.get("rec_mb", 0)
    wf = d.get("wifi", {})
    wifi = "%s %dM %d" % (wf.get("band", "-"), wf.get("mbps", 0), wf.get("rssi", 0))
    mark = "*" if state.get("lastUsed") == d.get("physicalSerial", d["serial"]) else " "
    return "%s %-4s %-15s %-6s %6s %-5s %-8s %-14s %-19s %-9s %s" % (
        mark, name_of(d.get("physicalSerial", d["serial"])), d["serial"], d["wake"], soc, lvs, tname,
        wifi, apps, regs, used)


# APK より新しいソースがあるかを見るディレクトリ。**Assets/ 全部は走査しない**
# （Library/ と違って数は少ないが、Meta XR SDK の中身まで見ると遅いだけで意味が無い）。
# ここに無いものを変えても APK には入らない、という対応にしてある。
SOURCE_DIRS = [
    os.path.join(ROOT, "Assets", "Scripts"),
    os.path.join(ROOT, "Assets", "Scenes"),
    os.path.join(ROOT, "Assets", "Art"),
    os.path.join(ROOT, "Assets", "Editor"),
    os.path.join(ROOT, "Assets", "Settings"),
    os.path.join(ROOT, "Assets", "Resources"),
    os.path.join(ROOT, "Assets", "StreamingAssets"),
    os.path.join(ROOT, "ProjectSettings"),
]

# ビルド中に触られたファイルを「APK より新しいソース」と読まないための猶予（秒）。
BUILD_TOUCH_SLACK_SEC = 180


def newest_source():
    """APK に入るソースのうち、いちばん新しいものの (mtime, パス) を返す。"""
    newest, newest_path = 0.0, ""
    for root in SOURCE_DIRS:
        if not os.path.isdir(root):
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d not in ("Editor Default Resources",)]
            for fn in filenames:
                if fn.endswith(".meta"):
                    continue   # .meta は中身と一緒に動くので見る意味が無い
                p = os.path.join(dirpath, fn)
                try:
                    m = os.path.getmtime(p)
                except OSError:
                    continue
                if m > newest:
                    newest, newest_path = m, p
    return newest, newest_path


def apk_freshness(apk=DEFAULT_APK):
    """APK とソースの新旧を比べる。返り値は (行, 古いか)。APK が無ければ (行, True)。

    「ビルドし直したつもりで古い APK を配る」は実際に起きる（2026-07-31 に手で git log と
    mtime を突き合わせて確認した）。毎回やる比較なので道具の側に持たせる。
    """
    if not os.path.exists(apk):
        return "apk: %s が無い（まだビルドしていない）" % apk, True
    apk_m = os.path.getmtime(apk)
    src_m, src_p = newest_source()
    apk_s = datetime.fromtimestamp(apk_m).strftime("%m-%d %H:%M")
    # ⚠ ビルド自身が触るファイルを「新しいソース」と読まない。BuildVariants は productName と
    #    applicationIdentifier を書き換えて finally で戻すので、ProjectSettings.asset は
    #    **必ず APK とほぼ同時刻**になる。閾値なしだと毎回この偽の警告が出る。
    if src_m <= apk_m + BUILD_TOUCH_SLACK_SEC:
        return "apk: %s（ソースより新しい）" % apk_s, False
    gap = (src_m - apk_m) / 60.0
    src_s = datetime.fromtimestamp(src_m).strftime("%m-%d %H:%M")
    return ("apk: %s ← ソースの方が %.0f 分新しい（%s %s）。焼き直さないと直したものが入らない"
            % (apk_s, gap, src_s, src_p)), True


def cmd_list(args):
    state = load_state()
    devs = collect()
    print("  %-4s %-15s %-6s %6s %-5s %-8s %-14s %-19s %-9s %s" % (
        "name", "serial", "wake", "soc C", "level", "thermal", "wifi",
        "apk updated", "course", "disk"))
    print("-" * 117)
    for d in devs:
        if not d.get("_online"):
            print("  %-4s %-15s %s" % (
                name_of(d.get("physicalSerial", d["serial"])), d["serial"],
                d.get("state", "offline")))
            continue
        print(fmt_row(d, state))
    print()
    runs = state.get("runs", {})
    if runs:
        print("runs so far:")
        for s, r in runs.items():
            print("  %-4s %-15s %d run(s), %d s total, last %s" % (
                name_of(s), s, r.get("count", 0), int(r.get("sec", 0)),
                r.get("lastAtIso", "-")))
    line, stale = apk_freshness()
    print()
    print(("WARN: " if stale else "") + line)
    best, reason, warns = pick(devs, state)
    for w in warns:
        print("WARN: " + w)
    if best:
        print("\nnext -> %s %s  (%s)" % (name_of(best["serial"]), best["serial"], reason))
    return 0


def collect(deep=True):
    devs = []
    for d in list_devices():
        if d["state"] != "device":
            devs.append({"serial": d["serial"], "state": d["state"], "_online": False})
            continue
        info = probe(d["serial"], deep=deep)
        info["_online"] = True
        info["model"] = d["model"]
        info["physicalSerial"] = d.get("physicalSerial") or d["serial"]
        devs.append(info)
    return devs


# ---------------------------------------------------------------- 操作

def cmd_pick(args):
    state = load_state()
    devs = collect(deep=not args.fast)
    best, reason, warns = pick(devs, state, require_reg=args.require_registration)
    for w in warns:
        print("WARN: " + w, file=sys.stderr)
    if not best:
        print("no usable device", file=sys.stderr)
        return 1
    print(best["serial"])
    if args.verbose:
        print("# %s" % reason, file=sys.stderr)
    return 0


def sleep_device(serial):
    adb(serial, "shell", "input", "keyevent", "KEYCODE_SLEEP")
    time.sleep(1.5)
    return read_wake(serial)


def wake_device(serial):
    adb(serial, "shell", "input", "keyevent", "KEYCODE_WAKEUP")
    time.sleep(1.5)
    return read_wake(serial)


def cmd_sleep(args):
    devices = [d for d in list_devices() if d["state"] == "device" and d.get("isQuest")]
    targets = []
    if args.others:
        keep = next((d.get("physicalSerial") for d in devices
                     if d["serial"] == args.others or d.get("physicalSerial") == args.others),
                    args.others)
        targets = [d["serial"] for d in devices if d.get("physicalSerial") != keep]
    elif args.serial:
        selected = next((d for d in devices
                         if d["serial"] == args.serial or d.get("physicalSerial") == args.serial), None)
        targets = [selected["serial"]] if selected else []
    else:
        targets = [d["serial"] for d in devices]
    for s in targets:
        # 走行中のアプリを起こしたまま寝かせない（次の起動が中途半端な状態から始まる）。
        adb(s, "shell", "am", "force-stop", PKG)
        st = sleep_device(s)
        print("%s -> %s" % (s, st))
    return 0


def cmd_wake(args):
    devices = [d for d in list_devices() if d["state"] == "device" and d.get("isQuest")]
    targets = ([d["serial"] for d in devices
                if d["serial"] == args.serial or d.get("physicalSerial") == args.serial]
               if args.serial else [d["serial"] for d in devices])
    for s in targets:
        print("%s -> %s" % (s, wake_device(s)))
    return 0


def cmd_sync(args):
    """APK を全機へ揃える。**交互運用は 2 台が同じビルドであることが前提**。

    片方だけ古いビルドのまま交代すると、直したはずの不具合が「再発した」ように見える
    （実測: 2 台の APK に 2 時間 20 分の差があった）。
    """
    apk = args.apk or DEFAULT_APK
    if not os.path.exists(apk):
        print("apk not found: %s" % apk, file=sys.stderr)
        return 1
    apk_mtime = datetime.fromtimestamp(os.path.getmtime(apk))
    print("apk: %s (%s, %.0f MB)" % (apk, apk_mtime.strftime("%Y-%m-%d %H:%M:%S"),
                                     os.path.getsize(apk) / 1048576.0))
    rc = 0
    for d in list_devices():
        if d["state"] != "device":
            print("  %-15s skip (%s)" % (d["serial"], d["state"]))
            continue
        if not d.get("isQuest"):
            print("  %-15s skip (Quest ではない)" % d["serial"])
            continue
        s = d["serial"]
        print("  %-15s installing..." % s)
        wake_device(s)  # スリープ中は install が固まることがある
        # --no-streaming: Quest は streaming install が固まる（quest-build スキルの罠）
        code, out, err = adb(s, "install", "-r", "--no-streaming", os.path.abspath(apk),
                             timeout=600)
        tail = (out + err).strip().splitlines()
        print("  %-15s %s" % (s, tail[-1] if tail else "rc=%d" % code))
        if code != 0:
            rc = 1
        else:
            after = read_app(s)
            print("  %-15s now %s" % (s, after["updated"]))
    return rc


def cmd_mark(args):
    st = load_state()
    try:
        resolve_adb()
        connected = list_devices(all_devices=True)
    except AdbNotFoundError:
        connected = []
    physical = next((d.get("physicalSerial") for d in connected
                     if d["serial"] == args.serial or d.get("physicalSerial") == args.serial),
                    args.serial)
    st["lastUsed"] = physical
    r = st.setdefault("runs", {}).setdefault(physical, {"count": 0, "sec": 0})
    r["count"] += 1
    r["sec"] += args.sec
    r["lastAtIso"] = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    if args.mode:
        r["lastMode"] = args.mode
    save_state(st)
    print("%s: %d run(s), %d s" % (physical, r["count"], r["sec"]))
    return 0


def cat_file(serial, remote, local):
    """端末のファイルを 1 つ取る。

    **`adb pull` は使えない。** /sdcard/Android/data/<pkg>/ は Android 11+ の scoped storage で
    保護されていて pull は黙って 0 バイトを作る（実測）。`exec-out cat` なら通る
    （`shell cat` だと改行が CRLF へ化けるので exec-out の方）。
    """
    code, out, err = adb(serial, "exec-out", "cat %s" % remote, timeout=120)
    if code != 0 or not out or out.startswith("cat:"):
        return False
    os.makedirs(os.path.dirname(local) or ".", exist_ok=True)
    with open(local, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(out)
    return True


def cmd_pull(args):
    """端末内に置かれた設定と登録を回収する。"""
    devices = [d for d in list_devices() if d["state"] == "device" and d.get("isQuest")]
    selected = next((d for d in devices
                     if d["serial"] == args.serial or d.get("physicalSerial") == args.serial), None)
    if not selected:
        print("refusing non-Quest or disconnected serial: %s" % args.serial, file=sys.stderr)
        return 1
    serial = selected["serial"]
    outdir = args.out or os.path.join("logs", "device", serial)
    os.makedirs(outdir, exist_ok=True)
    got = []
    for name in ("registration.json", "show_config.json"):
        if cat_file(serial, "%s/%s" % (FILES_DIR, name), os.path.join(outdir, name)):
            got.append(name)
        elif args.verbose:
            print("  (skip %s)" % name)
    print("pulled into %s: %s" % (outdir, ", ".join(got) if got else "(nothing)"))
    return 0


def cmd_reset_config(args):
    """端末キャッシュ show_config.json を消す。

    **キャッシュは APK の焼き込みより優先される**（焼き込み < キャッシュ < ライブ）。だから古い
    キャッシュが残っていると、ビルドし直しても実機は前の設定で走る。実測 (2026-07-31) では 2 台の
    キャッシュで run.intro.startLineId が食い違い、導入の始まり方が機ごとに違っていた
    （timeline.rev は両方 21 で一致していたので、rev を見ても気づけない）。

    消すと次の起動は焼き込み → ライブ の順で拾い直す。**卓が立っていないなら焼き込みが正**になるので、
    卓の「ビルド用エクスポート」を通した APK であることを確かめてから消すこと。
    """
    devices = [d for d in list_devices() if d["state"] == "device" and d.get("isQuest")]
    if args.serial:
        targets = [d["serial"] for d in devices
                   if d["serial"] == args.serial or d.get("physicalSerial") == args.serial]
        if not targets:
            print("refusing non-Quest or disconnected serial: %s" % args.serial, file=sys.stderr)
            return 1
    else:
        targets = [d["serial"] for d in devices]
    for s in targets:
        adb(s, "shell", f"rm -f {FILES_DIR}/show_config.json")
        _, out, _ = adb(s, "shell", f"ls {FILES_DIR}/show_config.json 2>&1")
        gone = "No such file" in out or not out.strip()
        print("%s -> %s" % (s, "cleared" if gone else "STILL THERE: " + out.strip()))
    return 0


def cmd_clean(args):
    """端末内の録画とログを消す。走行を重ねると溜まる（録画は 1 ラン数十 MB）。"""
    for d in list_devices():
        if d["state"] != "device" or not d.get("isQuest"):
            continue
        s = d["serial"]
        if args.serial and s != args.serial and d.get("physicalSerial") != args.serial:
            continue
        adb(s, "shell", f"rm -rf {CACHE_DIR}/rec")
        if args.logs:
            adb(s, "shell", f"rm -rf {XPLOG_DIR}")
        st = read_storage(s)
        print("%s -> rec %.0f MB / log %.0f MB" % (s, st["rec_mb"], st["log_mb"]))
    return 0


# ---------------------------------------------------------------- main

def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)

    sub.add_parser("list", help="show every connected Quest").set_defaults(fn=cmd_list)

    p = sub.add_parser("pick", help="print the serial of the device to run next")
    p.add_argument("--fast", action="store_true", help="skip registration/storage probes")
    p.add_argument("--require-registration", action="store_true")
    p.add_argument("-v", "--verbose", action="store_true")
    p.set_defaults(fn=cmd_pick)

    p = sub.add_parser("sleep", help="put device(s) to sleep")
    p.add_argument("serial", nargs="?")
    p.add_argument("--others", metavar="KEEP", help="sleep everything except this serial")
    p.set_defaults(fn=cmd_sleep)

    p = sub.add_parser("wake", help="wake device(s)")
    p.add_argument("serial", nargs="?")
    p.set_defaults(fn=cmd_wake)

    p = sub.add_parser("sync", help="install Builds/mawarimi.apk onto every device that is behind")
    p.add_argument("--apk")
    p.add_argument("--force", action="store_true")
    p.set_defaults(fn=cmd_sync)

    p = sub.add_parser("mark", help="record that a run happened on this device")
    p.add_argument("serial")
    p.add_argument("--sec", type=int, default=0)
    p.add_argument("--mode")
    p.set_defaults(fn=cmd_mark)

    p = sub.add_parser("pull", help="pull on-device config/registration")
    p.add_argument("serial")
    p.add_argument("--out")
    p.add_argument("-v", "--verbose", action="store_true")
    p.set_defaults(fn=cmd_pull)

    p = sub.add_parser("reset-config",
                       help="delete the on-device show_config.json cache (it outranks the baked one)")
    p.add_argument("serial", nargs="?")
    p.set_defaults(fn=cmd_reset_config)

    p = sub.add_parser("clean", help="delete on-device recordings (and logs with --logs)")
    p.add_argument("serial", nargs="?")
    p.add_argument("--logs", action="store_true")
    p.set_defaults(fn=cmd_clean)

    args = ap.parse_args(argv)
    if args.cmd != "mark":
        try:
            resolve_adb()
        except AdbNotFoundError as e:
            print(str(e), file=sys.stderr)
            return 2
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
