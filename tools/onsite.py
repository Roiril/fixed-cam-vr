#!/usr/bin/env python3
"""当日の運用を 1 本にまとめる。**開場前の点検・目の写真の反映・会期中の監視**。

    py -3.11 tools/onsite.py check          # 開場前点検。全部の経路を PASS/FAIL で 1 画面
    py -3.11 tools/onsite.py check --deep   # ＋ 目の写真が 2 台とも届いたかを 20 秒かけて見る
    py -3.11 tools/onsite.py eyejack        # 写真を探して → 取り込んで → 届いたかまで確認
    py -3.11 tools/onsite.py watch          # 会期中の監視。落ちた端末を自分で起こし直す
    py -3.11 tools/onsite.py adb-open       # USB の端末に無線 adb を開ける（設営時に 1 回）
    py -3.11 tools/onsite.py fix <name>     # 個別の復旧（cameras / panel / cache）

**なぜ卓の「✅ 本番前チェック」と別に要るか。** 卓のはブラウザの中にあり、ブラウザが見ている
ものしか見えない（show.json ＋ heartbeat ＋ /diag）。ここが見るのは**その外側** — 端末に
入っている APK の版・端末キャッシュ・電池と熱・Wi-Fi の相手・露出ロック・傾きと較正の差・
卓が 2 つ立っていないか。どれも**画にも音にも卓にも出ない**が、当日いちばん高くつく。

⚠ **接続には触らない。** Wi-Fi の入れ直し・保存済みネットワークの整理は端末を別の AP へ
移すので、このツールは 1 度もやらない（`net-scan.py` と同じ規律）。直すのは
「画面を起こす」「配信アプリを起こし直す」まで。

⚠ **判定は「効果」で出す。** 状態機械が進んだかではなく、フレームが実際に出たか・写真が
実際に端末へ届いたか・音源を掴めたかを見る（`~/.claude/rules/work-style.md` §2-2）。
"""
from __future__ import annotations

import argparse
import concurrent.futures as cf
import io
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = os.path.join(ROOT, "tools")
COMPOSITOR = os.path.join(TOOLS, "web-compositor")
SHOW_JSON = os.path.join(COMPOSITOR, "show.json")
EYEJACK_DIR = os.path.join(COMPOSITOR, "eyejack")
LOG_DIR = os.path.join(ROOT, "logs", "onsite")
DESK = "http://127.0.0.1:8099"

# ---- 展示ネットワークの設計値（docs/onsite/network-setup.md が正本）--------------------
DESK_IP = "192.168.10.10"
ROUTER_IP = "192.168.10.1"
STREAMER_MIN_VERSION = (0, 13, 0)   # 撮影パネル・補助線・鏡合わせが入った版
WIDE_FOV_DEG = 104.3                # 超広角。較正がこの画角を前提にしている
QUEST_NAMES = {"2G0YC1ZF890864": "α", "2G0YC1ZF7S06BW": "β"}

# 写真の置き場を探す順。上から見て、最初に中身があったものを使う。
EYEJACK_SOURCES = [
    os.path.join(COMPOSITOR, "eyejack", "inbox"),        # 卓のスマホ用パネルが置く
    r"G:\マイドライブ\shubie\eyejack-drop",               # Google Drive 経由（スマホから）
]
PHOTO_EXT = (".jpg", ".jpeg", ".png", ".heic", ".webp")

sys.path.insert(0, TOOLS)


# ================= 出力の形 =================

class Rows:
    """点検の結果。`state` は ok / warn / ng / skip の 4 つだけ。

    ⚠ **warn を作りすぎない。** 「見たけれど止める材料ではない」だけに使う。
    NG は「これがこのままなら体験が壊れる」に限る — 混ぜると当日の朝に読み飛ばされる。
    """

    def __init__(self):
        self.items: list[dict] = []

    def add(self, sec, state, label, detail, fix=""):
        self.items.append({"sec": sec, "state": state, "label": label,
                           "detail": detail, "fix": fix})

    def counts(self):
        c = {"ok": 0, "warn": 0, "ng": 0, "skip": 0}
        for r in self.items:
            c[r["state"]] = c.get(r["state"], 0) + 1
        return c

    def render(self) -> str:
        mark = {"ok": "[ OK ]", "warn": "[warn]", "ng": "[ NG ]", "skip": "[ -- ]"}
        out, sec = [], None
        for r in self.items:
            if r["sec"] != sec:
                sec = r["sec"]
                out.append("")
                out.append(f"── {sec} " + "─" * max(0, 56 - len(sec) * 2))
            out.append(f"{mark[r['state']]} {r['label']}  {r['detail']}")
            if r["fix"] and r["state"] in ("ng", "warn"):
                out.append(f"        → {r['fix']}")
        c = self.counts()
        out.append("")
        out.append(f"NG {c['ng']} / warn {c['warn']} / OK {c['ok']}"
                   + ("   ★ NG が無ければ開場してよい" if c["ng"] == 0 else "   ⚠ NG を先に直す"))
        return "\n".join(out)


# ================= 小道具 =================

def get_json(url, timeout=3.0):
    try:
        with urllib.request.urlopen(url, timeout=timeout) as r:
            return json.loads(r.read().decode("utf-8", "replace"))
    except Exception:
        return None


def post_json(url, payload, timeout=30.0):
    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(url, data=body, method="POST",
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read().decode("utf-8", "replace"))
    except Exception as e:
        return {"ok": False, "error": str(e)}


def stream_bytes(url, seconds=2.5, want=32768):
    """MJPEG を実際に引いて、バイトが流れるかを見る。

    ⚠ **`/health` の fps では生死を判定できない**（encode が止まっても最後の値を返す）。
    ⚠ `encodeIdle=true` は異常ではない — 需要駆動なので、誰も見ていなければ止まる。
       だから**こちらが客になって**引く。
    """
    got, t0 = 0, time.time()
    try:
        with urllib.request.urlopen(url, timeout=seconds) as r:
            while got < want and time.time() - t0 < seconds:
                chunk = r.read(8192)
                if not chunk:
                    break
                got += len(chunk)
    except Exception:
        pass
    return got


def run(cmd, timeout=20):
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8",
                           errors="replace", timeout=timeout)
        return p.returncode, (p.stdout or ""), (p.stderr or "")
    except Exception as e:
        return 1, "", str(e)


def local_ipv4() -> list[str]:
    rc, out, _ = run(["powershell", "-NoProfile", "-Command",
                      "(Get-NetIPAddress -AddressFamily IPv4).IPAddress"], timeout=25)
    return [l.strip() for l in out.splitlines() if l.strip()]


def load_show():
    try:
        with io.open(SHOW_JSON, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}


def listening_pids(port: int) -> list[str]:
    rc, out, _ = run(["netstat", "-ano"], timeout=20)
    pids = []
    for line in out.splitlines():
        if f":{port} " in line and "LISTENING" in line:
            pids.append(line.split()[-1])
    return sorted(set(pids))


def ver_tuple(s: str):
    m = re.match(r"(\d+)\.(\d+)\.(\d+)", s or "")
    return tuple(int(x) for x in m.groups()) if m else (0, 0, 0)


# ================= check =================

def check_desk(rows: Rows, show: dict):
    sec = "卓（PC）"

    pids = listening_pids(8099)
    if not pids:
        rows.add(sec, "ng", "卓サーバ", ":8099 が開いていません",
                 "py -3.11 tools/onsite.py serve  （または serve.ps1）")
        return None
    if len(pids) > 1:
        rows.add(sec, "ng", "卓サーバ", f"{len(pids)} 個立っています（pid {', '.join(pids)}）",
                 "1 つだけにする。2 つあると互いの show.json を巻き戻し合う")
    else:
        rows.add(sec, "ok", "卓サーバ", f"1 プロセス（pid {pids[0]}）")

    state = get_json(DESK + "/state", timeout=5)
    if not state:
        rows.add(sec, "ng", "卓の応答", "/state が返りません", "卓サーバを起動し直す")
        return None

    # ⚠ サーバは**メモリを正**にして配る。ディスクだけ新しいと、実機には古い方が流れる。
    disk_rev = show.get("rev")
    mem_rev = state.get("rev")
    if disk_rev is not None and mem_rev is not None and int(disk_rev) != int(mem_rev):
        rows.add(sec, "ng", "卓のメモリ", f"メモリ rev={mem_rev} / ディスク rev={disk_rev}",
                 "卓サーバを止めて立て直す（起動時にディスクを読み直す）")
    else:
        rows.add(sec, "ok", "卓のメモリ", f"rev={mem_rev}（ディスクと一致）")

    rc, out, err = run([sys.executable if "python" in sys.executable else "py", "-3.11",
                        os.path.join("tools", "export-show-build.py"), "--check"], timeout=60) \
        if False else run(["py", "-3.11", os.path.join(ROOT, "tools", "export-show-build.py"),
                           "--check"], timeout=60)
    line = (out.strip().splitlines() or [""])[0]
    if "✓" in line or "同じ" in line:
        rows.add(sec, "ok", "APK の焼き込み", line.replace("✓", "").strip())
    else:
        # ⚠ **rev の差だけで赤くしない。** 卓は beacon を受け取るたびに show.json を書き戻すので、
        #   誰も操作していなくても rev は勝手に進む。それを NG にすると常時点灯になり、
        #   **本物の NG が読み飛ばされる**。著作が変わったかは timeline.rev で見る。
        pairs = re.findall(r"rev=(\d+) timeline\.rev=(\d+)", line)
        if len(pairs) == 2 and pairs[0][1] == pairs[1][1]:
            rows.add(sec, "warn", "APK の焼き込み",
                     f"著作は同じ（timeline.rev={pairs[0][1]}）。rev だけ "
                     f"{pairs[0][0]} → {pairs[1][0]} に進んでいます",
                     "卓に一度でも繋ぐ機なら実害なし。卓なしで起動する機があるなら焼き込みを更新する")
        else:
            rows.add(sec, "ng", "APK の焼き込み", line or (err.strip()[:120] or "判定できません"),
                     "卓の 📦 ビルド用エクスポート、または py -3.11 tools/export-show-build.py")

    ips = local_ipv4()
    if DESK_IP in ips:
        rows.add(sec, "ok", "PC の IP", f"{DESK_IP} を持っています")
    else:
        rows.add(sec, "ng", "PC の IP", f"{DESK_IP} がありません（{', '.join(ips[:4])}）",
                 "有線を Aterm へ挿す。ルータの固定割当が効いているか見る")

    # ルータは ICMP を返すはず。返さないならルータが落ちているか、別 LAN に居る。
    rc, _, _ = run(["ping", "-n", "1", "-w", "700", ROUTER_IP], timeout=10)
    rows.add(sec, "ok" if rc == 0 else "warn", "ルータ",
             f"{ROUTER_IP} 応答あり" if rc == 0 else f"{ROUTER_IP} 無応答",
             "Aterm の電源と RT モードを見る（BR だと DHCP が動かない）")

    # ⚠⚠ **体験に外の回線は要らない。要るのはシュビーだけ。** ここを分けて出さないと、
    #    会場で「ネットが無い ＝ 展示が動かない」と誤読する。
    #    ⚠ Aterm は上流を持たないので `192.168.10.1` は「宛先ネットワークに到達できません」を返す。
    #      Windows はそれを見て既定経路から降格させ、外向きは Wi-Fi 側へ流す（実測）。
    #      有線の方がメトリックは低い（25 < 35）ので、**メトリックだけ見ると逆に読める**。
    rc, _, _ = run(["ping", "-n", "1", "-w", "1500", "8.8.8.8"], timeout=12)
    if rc == 0:
        _, out, _ = run(["powershell", "-NoProfile", "-Command",
                         "(Find-NetRoute -RemoteIPAddress 8.8.8.8 | "
                         "Select-Object -First 1).InterfaceAlias"], timeout=25)
        via = (out.strip().splitlines() or [""])[0]
        rows.add(sec, "ok", "外の回線（シュビー用）", f"通っています{f'（{via} 経由）' if via else ''}")
    else:
        rows.add(sec, "warn", "外の回線（シュビー用）",
                 "外へ出られません — **体験は動きます**が、シュビーには頼れません",
                 "当日パネル（onsite.html）で回す。要るならスマホのテザリングを PC へ")
    return state


def probe_camera(cam: dict) -> dict:
    host = (cam.get("host") or "").strip()
    port = int(cam.get("port") or 8080)
    r = {"id": cam.get("id"), "host": host, "port": port}
    if not host:
        return r
    base = f"http://{host}:{port}"
    r["info"] = get_json(base + "/info", timeout=3)
    r["health"] = get_json(base + "/health", timeout=3)
    r["bytes"] = stream_bytes(base + "/video", seconds=2.5) if r["info"] else 0
    # 無線 adb が開いているか。開いていないと当日の復旧が 1 手も打てない。
    s = socket.socket()
    s.settimeout(0.8)
    try:
        s.connect((host, 5555))
        r["adb"] = True
    except Exception:
        r["adb"] = False
    finally:
        s.close()
    return r


def check_cameras(rows: Rows, show: dict):
    sec = "カメラ"
    cams = [c for c in (show.get("cameras") or [])]
    used = [c for c in cams if (c.get("host") or "").strip()]
    if not used:
        rows.add(sec, "ng", "カメラ", "show.json に host が 1 つも入っていません", "卓の 📍 場所 で設定")
        return
    with cf.ThreadPoolExecutor(max_workers=8) as ex:
        results = list(ex.map(probe_camera, used))

    diag = get_json(DESK + "/diag", timeout=6) or {}
    beacon = {row.get("id"): row for row in (diag.get("cameras") or [])}

    for r in results:
        cid, host = r["id"], r["host"]
        info, health = r.get("info"), r.get("health")
        tag = f"カメラ{cid}"
        if not info:
            rows.add(sec, "ng", tag, f"{host}:8080 が応答しません",
                     "端末の画面を点けて配信アプリが起動しているか見る。IP が静的のままか見る")
            continue

        # 名乗った ID が割当と違う ＝ 演出が別のカメラに出る（2026-08-05 に A が 2 台になった）
        named = info.get("cameraId")
        if named != cid:
            rows.add(sec, "ng", tag, f"端末は「{named}」を名乗っています（割当は {cid}）",
                     "端末画面の cameraId を直す。演出が別のカメラに出ます")
        # 実際にバイトが流れたか
        if r["bytes"] <= 0:
            rows.add(sec, "ng", tag, "/video からバイトが 1 つも出ません（画は黒になります）",
                     "端末の画面が消えていないか（ロック中はカメラが止まる）。アプリを起こし直す")
        else:
            rows.add(sec, "ok", tag, f"{host} / {info.get('appVersion')} / "
                                     f"{r['bytes'] // 1024}KB 流れました")

        if ver_tuple(info.get("appVersion", "")) < STREAMER_MIN_VERSION:
            rows.add(sec, "ng", tag + " 版",
                     f"{info.get('appVersion')} — 撮影パネルが無く、当日の素材撮りができません",
                     "skills/streamer-android-build で v0.13.0 以上を入れる")

        fov = float(info.get("lensFovDeg") or 0)
        if abs(fov - WIDE_FOV_DEG) > 2.0:
            rows.add(sec, "ng", tag + " レンズ",
                     f"画角 {fov}°（超広角は {WIDE_FOV_DEG}°）— 較正が前提にしている画角と違います",
                     "端末画面でレンズを「超広角」に戻す")

        if health:
            if not health.get("aeLock") or not health.get("awbLock"):
                rows.add(sec, "warn", tag + " 露出",
                         f"AE {health.get('aeLock')} / AWB {health.get('awbLock')} — 自動のままです",
                         "設営と照明が決まったら端末の 🔓 露出/AF ロックを押す（素材と本番で色が変わる）")
            st = int(health.get("throttleStage") or 0)
            if st > 0:
                rows.add(sec, "warn", tag + " 熱",
                         f"熱段 {st} / {health.get('batteryTempC')}℃ — 画が粗くなります",
                         "充電ケーブルを抜いて冷ます（配信を止めるだけでは下がらない）")

        # 傾きと較正の差。40° ずれていた実績がある（そのとき CG 人形が盛大にずれた）。
        cam = next((c for c in used if c.get("id") == cid), {})
        cal = cam.get("calib") or {}
        if info.get("tiltState") == "ok" and ("pitchDeg" in cal or "rollDeg" in cal):
            dp = float(info.get("tiltPitchDeg") or 0) - float(cal.get("pitchDeg") or 0)
            dr = float(info.get("tiltRollDeg") or 0) - float(cal.get("rollDeg") or 0)
            worst = max(abs(dp), abs(dr))
            if worst > 15:
                rows.add(sec, "ng", tag + " 較正",
                         f"端末の傾きと較正が {dp:+.1f}° / {dr:+.1f}° ずれています",
                         "カメラを動かしたあと較正を取り直していません。CG 人形がずれます")
            elif worst > 5:
                rows.add(sec, "warn", tag + " 較正", f"傾きの差 {dp:+.1f}° / {dr:+.1f}°",
                         "気になるなら較正を取り直す")
        elif info.get("tiltState") != "ok":
            rows.add(sec, "warn", tag + " 姿勢",
                     f"傾き {info.get('tiltState')}（pitch {info.get('tiltPitchDeg')}°）",
                     "まだ据え付けていないなら正常。据えたあとなら向きを見る")

        b = beacon.get(cid) or {}
        if b and not b.get("beacon"):
            rows.add(sec, "warn", tag + " beacon",
                     "UDP ビーコンが届いていません（HTTP は通っています）",
                     "IP を固定してあれば実害なし。発見に頼るなら AP の分離設定を見る")

        if not r["adb"]:
            rows.add(sec, "ng", tag + " 無線adb",
                     f"{host}:5555 が閉じています — 当日この端末を遠隔で直せません",
                     "USB を挿して py -3.11 tools/onsite.py adb-open（端末を再起動すると閉じる）")


def quest_rows(rows: Rows):
    sec = "Quest"
    try:
        import importlib
        qf = importlib.import_module("quest-fleet".replace("-", "_")) \
            if False else None
    except Exception:
        qf = None
    # quest-fleet.py はハイフン入りで import できないので、必要な処理だけここで叩く。
    rc, out, _ = run(["adb", "devices"], timeout=25)
    serials = [l.split()[0] for l in out.splitlines()[1:]
               if l.strip() and l.split()[-1] == "device"]
    if not serials:
        rows.add(sec, "ng", "Quest", "adb に 1 台も出ていません",
                 "USB を挿すか、無線 adb（192.168.10.31 / .32:5555）へ connect する")
        return []

    apk = os.path.join(ROOT, "Builds", "mawarimi.apk")
    apk_mtime = os.path.getmtime(apk) if os.path.exists(apk) else 0
    running = []
    for s in serials:
        name = QUEST_NAMES.get(s, s[-6:])
        tag = f"Quest {name}"
        _, pkg, _ = run(["adb", "-s", s, "shell", "dumpsys package com.roiril.mawarimi"], 30)
        if "Unable to find package" in pkg or not pkg.strip():
            rows.add(sec, "ng", tag, "アプリが入っていません",
                     "py -3.11 tools/quest-fleet.py sync")
            continue
        m = re.search(r"lastUpdateTime=([\d\-: ]+)", pkg)
        upd = m.group(1).strip() if m else ""
        try:
            upd_ts = datetime.strptime(upd, "%Y-%m-%d %H:%M:%S").timestamp()
        except Exception:
            upd_ts = 0
        if apk_mtime and upd_ts and upd_ts < apk_mtime - 60:
            rows.add(sec, "ng", tag + " の APK",
                     f"入っているのは {upd} / 焼いたのは "
                     f"{datetime.fromtimestamp(apk_mtime):%Y-%m-%d %H:%M}",
                     "py -3.11 tools/quest-fleet.py sync（古い APK の機は演出が別物になります）")
        else:
            rows.add(sec, "ok", tag + " の APK", f"{upd}")

        _, reg, _ = run(["adb", "-s", s, "shell",
                         "cat /sdcard/Android/data/com.roiril.mawarimi/files/registration.json"], 20)
        if reg.strip().startswith("{"):
            try:
                j = json.loads(reg)
                rows.add(sec, "ok", tag + " の位置合わせ",
                         f"残差 {float(j.get('maxResidualM', 0)) * 100:.0f}cm / "
                         f"{j.get('pointCount')} 点 / {(j.get('savedAtIso') or '')[:16]}")
            except Exception:
                rows.add(sec, "warn", tag + " の位置合わせ", "保存はあるが読めません", "現地で取り直す")
        else:
            rows.add(sec, "ng", tag + " の位置合わせ", "登録がありません",
                     "現地で右トリガー 2 秒長押しから登録する（ゾーンが実空間に合いません）")

        _, bat, _ = run(["adb", "-s", s, "shell", "dumpsys battery"], 20)
        lv = re.search(r"level: (\d+)", bat)
        ac = "true" in (re.search(r"(AC|USB) powered: (\w+)", bat) or
                        type("x", (), {"group": lambda *_: "false"})()).group(2).lower()
        level = int(lv.group(1)) if lv else -1
        rows.add(sec, "ok" if (ac or level >= 60) else "warn", tag + " の電池",
                 f"{level}% / {'充電中' if ac else '未接続'}",
                 "本番中は挿しっぱなしにする")

        _, ps, _ = run(["adb", "-s", s, "shell", "ps -A | grep mawarimi"], 20)
        if "mawarimi" in ps:
            running.append(s)
    rows.add(sec, "ok" if running else "warn", "起動中の機",
             f"{len(running)} 台でアプリが動いています"
             + (f"（{', '.join(QUEST_NAMES.get(s, s[-6:]) for s in running)}）" if running else ""),
             "点検で音・コントローラ・目の写真を見るには、本番と同じ台数を起動しておく")
    return running


def check_show_material(rows: Rows, show: dict, state: dict | None):
    sec = "素材"
    ej = get_json(DESK + "/eyejack/list", timeout=6) or {}
    items = ej.get("items") or []
    src = show.get("eyejack") or {}
    uses = False
    tl = (show.get("timeline") or {})
    for seg in (tl.get("segments") or []):
        for take in (seg.get("takes") or []):
            for st in (take.get("steps") or []):
                if isinstance(st, dict) and st.get("eyeJack"):
                    uses = True
    if not uses:
        rows.add(sec, "skip", "目の写真", "台本に視界ジャックのカットがありません")
    elif not items:
        rows.add(sec, "ng", "目の写真", "1 枚も取り込まれていません",
                 "撮って py -3.11 tools/onsite.py eyejack（ジャックは出ません）")
    else:
        rows.add(sec, "ok", "目の写真", f"卓に {len(items)} 枚（{src.get('rev', '?')}）")

    man = get_json(DESK + "/shoot/manifest", timeout=8) or {}
    adopted = {}
    for url, e in (man.get("items") or {}).items():
        if e.get("adopted"):
            adopted[e.get("shot") or ""] = url
    povs = [k for k in adopted if k.startswith("pov")]
    if povs:
        rows.add(sec, "ok", "人形視点", f"採用済み {len(povs)} 本（{', '.join(sorted(povs))}）")
    else:
        rows.add(sec, "warn", "人形視点", "採用済みのテイクがありません",
                 "当日撮る素材。撮る前ならこれで正常。撮ったのに出ないなら卓の 🎥 撮影 で採用する")


def sample_heartbeat(rows: Rows, seconds: float, running_count: int):
    """heartbeat を N 秒サンプリングして、**2 台とも**揃っているかを見る。

    ⚠ heartbeat に端末 ID が無いので、卓の `/unity/status` は 2 台ぶんが同じ 1 スロットへ
    交互に入る。だから 1 回読んでも「両方 OK」は言えない。**窓の中の最悪値**を見れば、
    片方だけ欠けている状態は必ず引っかかる（欠けている機の番が来た瞬間に値が落ちる）。
    """
    sec = "走っている機（heartbeat）"
    samples = []
    stale = 0
    t0 = time.time()
    while time.time() - t0 < seconds:
        st = get_json(DESK + "/unity/status", timeout=2)
        # ⚠⚠ **古いスナップショットで判定しない。** サーバは最後に受け取った heartbeat を
        #    何時間でも返し続ける。落ちている機の 4 時間前の値を「いま」と読むと、
        #    音もコントローラも判定できてしまう（＝ 計器が生きたまま嘘をつく形）。
        if st and float(st.get("ageSec") or 1e9) <= 6.0:
            samples.append(st)
        elif st:
            stale += 1
        time.sleep(0.4)
    if not samples:
        if running_count == 0:
            rows.add(sec, "skip", "heartbeat",
                     "Quest でアプリが動いていないので、音・コントローラ・目の写真は見ていません",
                     "開場前は本番と同じ台数を起動してから check --deep を回す")
        else:
            rows.add(sec, "ng", "heartbeat",
                     f"アプリは動いていますが卓に届いていません（古い値のみ {stale} 回）",
                     "ShowServer.asset の host が 192.168.10.10 か / 卓と同じ LAN に居るか")
        return

    def nums(key):
        out = []
        for s in samples:
            v = (s.get("status") or s).get(key)
            try:
                out.append(float(v))
            except (TypeError, ValueError):
                pass
        return out

    n = len(samples)
    note = f"{n} 回受信"
    if running_count >= 2 and seconds >= 10:
        note += "（2 台ぶんが交互に入るので、最悪値で見ています）"

    miss = nums("sndMissing")
    aud = nums("sndAudible")
    if miss and max(miss) > 0:
        rows.add(sec, "ng", "音", f"音源を最大 {int(max(miss))} 本 掴めていません",
                 "設計どおりには鳴りません。実機ログの [Sound] を見る。menu scene の焼き直し漏れも疑う")
    elif miss:
        rows.add(sec, "ok", "音", f"欠けなし / 出力 {max(aud) if aud else 0:.2f}  {note}")

    for k, jp, why in (
            ("ctrlLConnected", "左コントローラ", "体験者が異変を報告できません（画にも音にも出ません）"),
            ("ctrlRConnected", "右コントローラ", "スタッフが体験を始められません（右 A / 右グリップ長押し）")):
        vals = [bool((s.get("status") or s).get(k)) for s in samples]
        if vals and not all(vals):
            rows.add(sec, "ng", jp, f"{sum(vals)}/{len(vals)} の観測でしか繋がっていません", why)
        elif vals:
            rows.add(sec, "ok", jp, "接続")

    listed = nums("eyeJackListed")
    ready = nums("eyeJackReady")
    if listed and max(listed) > 0:
        worst = min(ready) if ready else -1
        if worst < max(listed):
            rows.add(sec, "ng", "目の写真の到達",
                     f"最悪の観測で {int(worst)}/{int(max(listed))} 枚"
                     + ("（片方の機に届いていません）" if running_count >= 2 else ""),
                     "その機に当たった体験者だけジャックが出ません。卓との接続を見る（30 秒で自動再取得）")
        else:
            rows.add(sec, "ok", "目の写真の到達", f"どの観測でも {int(max(listed))} 枚  {note}")

    st = samples[-1].get("status") or samples[-1]
    if st.get("needsReReg"):
        rows.add(sec, "ng", "位置合わせ", "トラッキング原点が変わりました",
                 "再登録するまでゾーンがズレたまま動きます")
    if st.get("mode") == "REG":
        rows.add(sec, "ng", "モード", "位置合わせモードのままです",
                 "右トリガー 2 秒長押しで抜ける（このままでは体験を始められません）")


def cmd_check(args):
    rows = Rows()
    show = load_show()
    state = check_desk(rows, show)
    check_cameras(rows, show)
    running = quest_rows(rows)
    if state is not None:
        check_show_material(rows, show, state)
    if args.deep:
        sample_heartbeat(rows, 20.0, len(running))
    else:
        sample_heartbeat(rows, 3.0, len(running))

    print(rows.render())
    os.makedirs(LOG_DIR, exist_ok=True)
    payload = {"at": datetime.now().isoformat(timespec="seconds"),
               "counts": rows.counts(), "rows": rows.items}
    with io.open(os.path.join(LOG_DIR, "check-latest.json"), "w",
                 encoding="utf-8", newline="\n") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)
    if args.json:
        print(json.dumps(payload, ensure_ascii=False))
    return 1 if rows.counts()["ng"] else 0


# ================= eyejack =================

def find_photos(explicit: str | None) -> tuple[str, list[str]]:
    """写真を探す。**人がどこへ置いたかを当てにしない** — 置きそうな所を順に見る。"""
    cands = [explicit] if explicit else list(EYEJACK_SOURCES)
    for d in cands:
        if not d or not os.path.isdir(d):
            continue
        files = [os.path.join(d, n) for n in sorted(os.listdir(d))
                 if n.lower().endswith(PHOTO_EXT)]
        if files:
            return d, files
    # どこにも無ければ、既に取り込みフォルダに入っているものを使う（手で置いた場合）
    if os.path.isdir(EYEJACK_DIR):
        files = [os.path.join(EYEJACK_DIR, n) for n in sorted(os.listdir(EYEJACK_DIR))
                 if n.lower().endswith(PHOTO_EXT)]
        if files:
            return EYEJACK_DIR, files
    return "", []


def cmd_eyejack(args):
    src, files = find_photos(args.src)
    if not files:
        print("写真が 1 枚も見つかりません。探した場所:")
        for d in ([args.src] if args.src else EYEJACK_SOURCES) + [EYEJACK_DIR]:
            print(f"  {d}  {'（あります）' if d and os.path.isdir(d) else '（ありません）'}")
        print("\nスマホから送るなら http://192.168.10.10:8099/onsite.html を開く")
        return 1

    os.makedirs(EYEJACK_DIR, exist_ok=True)
    if os.path.abspath(src) != os.path.abspath(EYEJACK_DIR):
        if args.replace:
            for n in os.listdir(EYEJACK_DIR):
                if n.lower().endswith(PHOTO_EXT):
                    os.remove(os.path.join(EYEJACK_DIR, n))
        # ⚠ 流れる順はファイル名順。撮った順を保つために番号を打ち直す。
        for i, fp in enumerate(files, 1):
            ext = os.path.splitext(fp)[1].lower()
            shutil.copy2(fp, os.path.join(EYEJACK_DIR, f"{i:02d}_{os.path.basename(fp)}"))
        print(f"{len(files)} 枚を {src} から取り込みフォルダへ入れました")

    res = post_json(DESK + "/eyejack/apply", {}, timeout=120)
    if not res.get("ok", True) and res.get("error"):
        print(f"取り込みに失敗しました: {res['error']}")
        print("卓サーバが動いているか見る（py -3.11 tools/onsite.py check）")
        return 1
    lst = get_json(DESK + "/eyejack/list", timeout=10) or {}
    n = len(lst.get("items") or [])
    print(f"卓に {n} 枚 用意できました（縮小・向き・明るさを揃えました）")

    # 焼き込みも更新する。卓が居ない状態で起動した機はここからしか読めない。
    if not args.no_export:
        r = post_json(DESK + "/export-build", {}, timeout=180)
        print("APK 焼き込みへも書き出しました" if r.get("ok", True) is not False
              else f"焼き込みに失敗: {r.get('error')}")

    print("\n端末へ届いたかを 20 秒見ます（30 秒ごとに自動で取り直すので、少し待ちます）")
    rows = Rows()
    sample_heartbeat(rows, 20.0, 2)
    for r in rows.items:
        if "目の写真" in r["label"] or r["label"] == "heartbeat":
            print(f"  {r['state'].upper()}  {r['label']}  {r['detail']}")
    return 0


# ================= watch（会期中の監視と自動復旧）=================

def wake_and_restart(host: str) -> str:
    """配信端末を起こし直す。**接続には触らない**（Wi-Fi は 1 度も操作しない）。

    ⚠ Doze / 画面ロック中に `am start` しても Activity が resume されず、カメラが bind 直後に
    閉じて**フレーム 0 のまま HTTP だけ生きる**（2026-07-17 実害）。順番が要る。
    """
    dev = f"{host}:5555"
    run(["adb", "connect", dev], timeout=10)
    steps = [
        ["shell", "input", "keyevent", "KEYCODE_WAKEUP"],
        ["shell", "wm", "dismiss-keyguard"],
        ["shell", "am", "force-stop", "com.fixedcamvr.streamer"],
        ["shell", "am", "start", "-n",
         "com.fixedcamvr.streamer/.MainActivity"],
    ]
    for st in steps:
        rc, out, err = run(["adb", "-s", dev] + st, timeout=20)
        if rc != 0 and "start" in st:
            return f"起こせません: {(err or out).strip()[:80]}"
    return "起こし直しました"


def cmd_watch(args):
    show = load_show()
    cams = [c for c in (show.get("cameras") or []) if (c.get("host") or "").strip()]
    os.makedirs(LOG_DIR, exist_ok=True)
    logp = os.path.join(LOG_DIR, f"watch-{datetime.now():%Y%m%d_%H%M%S}.log")
    last_fix: dict[str, float] = {}
    print(f"監視を始めます（{args.sec} 秒ごと / 自動復旧 {'あり' if not args.no_fix else 'なし'}）")
    print(f"記録: {logp}")

    said: dict[str, float] = {}

    def say(msg, key=None, repeat_after=300.0):
        """記録する。**同じことを言い続けない。**

        ⚠ 2 日間 無人で回すので、同じ行を 20 秒ごとに書くと本物の変化が埋もれる。
        同じ key は `repeat_after` 秒に 1 回だけ。`key=None` なら毎回書く（状態の変わり目）。
        """
        if key is not None:
            if time.time() - said.get(key, 0) < repeat_after:
                return
            said[key] = time.time()
        line = f"{datetime.now():%m-%d %H:%M:%S}  {msg}"
        print(line, flush=True)
        with io.open(logp, "a", encoding="utf-8", newline="\n") as f:
            f.write(line + "\n")

    try:
        while True:
            snap = {"at": datetime.now().isoformat(timespec="seconds"), "cameras": [], "quest": {}}
            for c in cams:
                host, cid = c["host"], c.get("id")
                got = stream_bytes(f"http://{host}:8080/video", seconds=2.0)
                alive = got > 0
                snap["cameras"].append({"id": cid, "host": host, "bytes": got, "alive": alive})
                if alive:
                    # 戻ったことは必ず 1 行残す（変わり目が無いと、復旧が効いたのか
                    # たまたま直ったのかが後から分からない）。
                    if said.pop(f"dead:{cid}", None):
                        say(f"カメラ{cid}（{host}）が戻りました（{got // 1024}KB）")
                    continue
                say(f"カメラ{cid}（{host}）からバイトが出ていません", key=f"dead:{cid}")
                if args.no_fix:
                    continue
                # ⚠ 直し過ぎない。落ちている時にだけ・1 台につき 90 秒に 1 回まで。
                if time.time() - last_fix.get(host, 0) < 90:
                    say(f"  直近に手を打ったばかりなので待ちます（カメラ{cid}）")
                    continue
                last_fix[host] = time.time()
                say("  " + wake_and_restart(host))
            # ⚠⚠ **卓自身の生存をいちばん先に見る。** 卓が落ちても体験は焼き込みの値で
            #    続くので、実機には 1 ビットも出ない — 音・コントローラ・目の写真の失敗が
            #    まとめて無音になる。落ちていたら立て直す（serve は冪等で、
            #    既に開いていれば何もしない ＝ 2 つ立つ事故は起きない）。
            if not listening_pids(8099):
                say("⚠ 卓が落ちています（実機側には何も出ません）。立て直します", key="desk")
                cmd_serve(argparse.Namespace())
                time.sleep(2)

            hb = get_json(DESK + "/unity/status", timeout=2) or {}
            age = hb.get("ageSec")
            snap["quest"] = {"ageSec": age}
            if age is not None and age > 30:
                say(f"Quest の heartbeat が {age:.0f} 秒 途切れています", key="hb")
            elif age is not None and "hb" in said:
                said.pop("hb", None)
                say("Quest の heartbeat が戻りました")   # 戻りは毎回書く（変わり目なので）
            with io.open(os.path.join(LOG_DIR, "watch-latest.json"), "w",
                         encoding="utf-8", newline="\n") as f:
                json.dump(snap, f, ensure_ascii=False)
            time.sleep(max(3, args.sec))
    except KeyboardInterrupt:
        say("監視を止めました")
    return 0


# ================= adb-open / fix / serve =================

def cmd_adb_open(args):
    """USB で繋がっている Android に無線 adb を開ける。**設営のたびに 1 回**。

    ⚠ 端末を再起動すると閉じる。閉じていると当日の遠隔復旧が 1 手も打てない。
    """
    rc, out, _ = run(["adb", "devices"], timeout=25)
    usb = [l.split()[0] for l in out.splitlines()[1:]
           if l.strip() and l.split()[-1] == "device" and ":" not in l.split()[0]]
    if not usb:
        print("USB で繋がっている端末がありません。ケーブルを挿して、端末側の許可を出す")
        return 1
    ok = 0
    for s in usb:
        _, ip, _ = run(["adb", "-s", s, "shell", "ip -f inet addr show wlan0"], 20)
        m = re.search(r"inet (\d+\.\d+\.\d+\.\d+)", ip)
        if not m:
            print(f"{s}  Wi-Fi の IP が取れません")
            continue
        addr = m.group(1)
        run(["adb", "-s", s, "tcpip", "5555"], timeout=20)
        time.sleep(1.5)
        rc2, o2, e2 = run(["adb", "connect", f"{addr}:5555"], timeout=15)
        good = "connected" in (o2 + e2)
        print(f"{s}  {addr}:5555  {'開きました' if good else (o2 + e2).strip()[:70]}")
        ok += 1 if good else 0
    print(f"\n{ok}/{len(usb)} 台。⚠ 端末を再起動すると閉じます（設営後にもう一度)")
    return 0 if ok else 1


def cmd_fix(args):
    show = load_show()
    if args.name == "cameras":
        for c in (show.get("cameras") or []):
            h = (c.get("host") or "").strip()
            if h:
                print(f"カメラ{c.get('id')} ({h}): " + wake_and_restart(h))
        return 0
    if args.name == "panel":
        # Quest の設定パネルが生きていると 10 秒ごとの掃引で 3 台同時に途切れる
        rc, out, _ = run(["adb", "devices"], timeout=25)
        for s in [l.split()[0] for l in out.splitlines()[1:]
                  if l.strip() and l.split()[-1] == "device"]:
            run(["adb", "-s", s, "shell", "am", "force-stop",
                 "com.oculus.panelapp.settings"], 20)
            print(f"{s}: 設定パネルを閉じました")
        return 0
    if args.name == "cache":
        rc, out, _ = run(["py", "-3.11", os.path.join(ROOT, "tools", "quest-fleet.py"),
                          "reset-config"], timeout=60)
        print(out.strip())
        return 0
    print("fix の名前: cameras（配信端末を起こし直す）/ panel（Quest の設定パネルを閉じる）"
          " / cache（端末の設定キャッシュを消す）")
    return 1


def cmd_serve(args):
    """卓を立てる。**冪等** — 既に :8099 が開いていれば何もしない。

    ⚠⚠ ここが冪等でないと、ログオン自動起動と手動起動が重なって**卓が 2 つ**になる。
    2 つ立つと互いの show.json を巻き戻し合い、誰も操作していないのに設定が古い方へ戻る
    （discovery スレッドが beacon 受信で勝手に書き戻すため）。
    """
    if listening_pids(8099):
        print("卓はもう動いています（:8099）。何もしません")
        return 0
    script = os.path.join(COMPOSITOR, "capture-server.py")
    os.makedirs(LOG_DIR, exist_ok=True)
    logp = os.path.join(LOG_DIR, "desk.log")
    print("卓を起動します: " + script)
    # 親（ログオン時のコンソール）が閉じても道連れにしない。
    flags = 0
    if os.name == "nt":
        flags = subprocess.CREATE_NEW_PROCESS_GROUP | 0x00000008  # DETACHED_PROCESS
    with io.open(logp, "a", encoding="utf-8", newline="\n") as log:
        log.write(f"\n---- {datetime.now():%Y-%m-%d %H:%M:%S} 起動 ----\n")
    out = open(logp, "ab")
    subprocess.Popen(["py", "-3.11", script, "8099"], cwd=COMPOSITOR,
                     stdout=out, stderr=out, stdin=subprocess.DEVNULL,
                     creationflags=flags, close_fds=True)
    for _ in range(24):
        time.sleep(0.5)
        if listening_pids(8099):
            print("起動しました → http://192.168.10.10:8099/onsite.html")
            return 0
    print(f"起動を確認できませんでした（{logp} を見る）")
    return 1


def main(argv=None):
    ap = argparse.ArgumentParser(description="当日の点検・反映・監視")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("check", help="開場前点検")
    p.add_argument("--deep", action="store_true", help="heartbeat を 20 秒サンプリングする")
    p.add_argument("--json", action="store_true")
    p.set_defaults(func=cmd_check)

    p = sub.add_parser("eyejack", help="目の写真を取り込んで届くまで見る")
    p.add_argument("--src", help="写真のあるフォルダ（省略すると自動で探す）")
    p.add_argument("--replace", action="store_true", help="前の写真を消してから入れる")
    p.add_argument("--no-export", action="store_true", help="APK 焼き込みへの書き出しを飛ばす")
    p.set_defaults(func=cmd_eyejack)

    p = sub.add_parser("watch", help="会期中の監視と自動復旧")
    p.add_argument("--sec", type=int, default=20)
    p.add_argument("--no-fix", action="store_true")
    p.set_defaults(func=cmd_watch)

    p = sub.add_parser("adb-open", help="USB の端末に無線 adb を開ける")
    p.set_defaults(func=cmd_adb_open)

    p = sub.add_parser("fix", help="個別の復旧")
    p.add_argument("name", nargs="?", default="")
    p.set_defaults(func=cmd_fix)

    p = sub.add_parser("serve", help="卓を起動する")
    p.set_defaults(func=cmd_serve)

    args = ap.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
