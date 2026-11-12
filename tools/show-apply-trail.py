"""本番の台本（tools/web-compositor/show.json）の 2 周目を、位置で振る舞いが変わる形へ書き換える（LEDGER 0347）。

  2 周目 A  顔のシミは角の線の向こう側へ出たら幕へ沈む。背の線を越えて離れると、背丈のある人形が歩いた跡をついてくる
  2 周目 B  小さい人形が跡の上を寄り続け、届いた線で、画面ごと別の場所へ飛ぶ
  2 周目 C  人形の目で見たカットを、床の線で 1 本ずつ進める。途中は音なし。通路の 6 割で追いつく

何度流しても同じ結果になる。書く前に Logs/show-backup/ へ元の show.json を写す。
卓が動いているときは、卓がメモリの版でディスクを上書きするので、卓を止めてから流す。

    py -3.11 tools/show-apply-trail.py            # 変わる所を並べるだけ
    py -3.11 tools/show-apply-trail.py --apply    # 書く。そのあと py -3.11 tools/export-show-build.py で焼く

シミのマスクは本番の素材のまま（画の左半分）。角の線で引くので体が隠れる時間は短くなるが、
角の手前では重なる。本番の部屋の写真で締めたマスクへ替えるのは別の作業。
"""
from __future__ import annotations

import argparse
import copy
import json
import os
import shutil
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SHOW = ROOT / "tools/web-compositor/show.json"
TALL = "doll_tall"

LINES = [
    ("trail_A_behind", 0, -0.5, -0.20, 0.0, -0.20, "2周目A 背の線（ここを越えて離れると人形がついてくる）"),
    ("trail_B_reach", 1, -0.9, 0.25, -0.5, 0.25, "2周目B 届く線（ここで別の場所へ飛ぶ）"),
    ("chase_C_1", 2, -0.05, 0.42, -0.05, 0.95, "2周目C 人形視点 1 本目"),
    ("chase_C_2", 2, 0.13, 0.42, 0.13, 0.95, "2周目C 人形視点 2 本目"),
    ("chase_C_3", 2, 0.27, 0.42, 0.27, 0.95, "2周目C 人形視点 3 本目"),
    ("chase_C_catch", 2, 0.40, 0.42, 0.40, 0.95, "2周目C 追いつき"),
]


def step(source: str, **kw) -> dict:
    d = {"source": source, "camera": -1, "cueId": "", "strength": -1, "fadeInSec": -1, "fadeOutSec": -1,
         "trimStartSec": -1, "trimEndSec": -1, "durKind": "sec", "lineId": "", "durSec": 0,
         "transition": "cut", "cg": "", "cgMode": "follow", "switchSfx": False}
    d.update(kw)
    return d


def trail(actor: str, gap: float, hop: float, hide: float, **kw) -> dict:
    return step("inherit", cg=actor, cgMode="trail", trailGapM=gap, trailStepM=hop, trailHideM=hide, **kw)


def patch(show: dict) -> None:
    base = next(a for a in show["actors"] if a["id"] == "doll")
    show["actors"] = [a for a in show["actors"] if a["id"] != TALL] + [
        dict(base, id=TALL, name="背丈のある人形（跡をついてくる）", heightM=1.1)]

    ids = {x[0] for x in LINES}
    lines = [ln for ln in show["layout"]["lines"] if ln["id"] not in ids]
    for lid, cam, x1, z1, x2, z2, label in LINES:
        lines.append({"id": lid, "camera": cam, "x1": x1, "z1": z1, "x2": x2, "z2": z2,
                      "dir": "both", "label": label, "mirrorOf": ""})
    show["layout"]["lines"] = lines

    for seg in show["timeline"]["segments"]:
        lap, cam = int(seg.get("lap", 0)), int(seg.get("camera", 0))
        if lap != 2:
            continue
        takes = seg["takes"]
        if cam == 0:
            stain = next(t for t in takes if t["id"] == "L2C0#0")
            st = stain["steps"][0]
            st["durKind"] = "untilBeyond"
            st["lineId"] = "line_center_A"
            st["durSec"] = 20
            st["fadeOutSec"] = 0.4
            stain["policy"] = "yield"
            stain["ifMissed"] = "skip"
            seg["takes"] = [stain, {
                "id": "L2C0#1", "name": "2周目A 離れると、歩いた跡を人形がついてくる", "at": "line",
                "lineId": "trail_A_behind", "ifMissed": "skip", "policy": "yield", "wait": "segment",
                "once": True, "dismissible": True,
                "steps": [trail(TALL, 1.5, 0.45, 0.9, camera=0, durKind="untilZoneChange")]}]
        elif cam == 1:
            t = next(t for t in takes if t["id"] == "L2C1#0")
            t["name"] = "2周目B 人形が跡を寄ってくる → 届いた線で別の場所へ飛ぶ"
            t["policy"] = "yield"
            t["ifMissed"] = "skip"
            rest = [s for s in t["steps"] if s.get("cgMode") != "trail"]
            t["steps"] = [trail("doll", 0.8, 0.25, 0.4, camera=1, durKind="untilBeyond",
                                lineId="trail_B_reach", durSec=20)] + rest
        elif cam == 2:
            pre = next(t for t in takes if t["id"] == "L2C2#0")
            for s in pre["steps"]:
                if s.get("source") == "live":
                    s["camera"] = 2      # C の区間に 0.6 秒だけ B が映るのを直す（NOW.md の食い違い 11）
            old = next(t for t in takes if t["id"] == "L2C2#1")
            last = copy.deepcopy(old["steps"][-1])   # 追いつきの後のライブ（劇伴の切り替えを持つ）

            def pov(n, sec, **kw):
                return step("clip", camera=2, cueId=f"pov_{n}", durSec=sec, transition="glitch", **kw)

            def walk(until):
                return step("live", camera=2, durKind="untilBeyond", lineId=until, durSec=10, transition="glitch")

            seg["takes"] = [pre, {
                "id": "L2C2#1", "name": "2周目C 追いつき（歩くたびに近づく。途中は音なし）", "at": "line",
                "lineId": "chase_C_1", "ifMissed": "skip", "policy": "yield", "wait": "segment", "once": True,
                "steps": [pov(1, 0.6), walk("chase_C_2"), pov(2, 0.6), walk("chase_C_3"), pov(3, 0.6),
                          walk("chase_C_catch"), pov(4, 1.4, dollCall=True, switchSfx=True), last]}]
    show["timeline"]["rev"] = int(show["timeline"].get("rev", 0)) + 1
    show["layout"]["rev"] = int(show["layout"].get("rev", 0)) + 1
    show["rev"] = int(show.get("rev", 0)) + 1


def summary(show: dict) -> list:
    out = []
    for seg in show["timeline"]["segments"]:
        if int(seg.get("lap", 0)) != 2:
            continue
        for t in seg["takes"]:
            out.append(f"  2-{'ABC'[int(seg.get('camera', 0))]} {t['id']} at={t.get('at')} {t.get('lineId', '')} policy={t.get('policy')}: "
                       + " → ".join(f"{s['source']}[{s.get('cueId') or s.get('cg') or '-'}"
                                    f"{'/' + s['cgMode'] if s.get('cg') else ''}] {s['durKind']}{':' + s['lineId'] if s.get('lineId') else ''}"
                                    for s in t["steps"]))
    return out


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true")
    a = ap.parse_args()
    raw = SHOW.read_bytes()
    show = json.loads(raw.decode("utf-8-sig"))
    print("いま:")
    print("\n".join(summary(show)))
    already = any(ln["id"] == "trail_A_behind" for ln in show["layout"]["lines"])
    new = copy.deepcopy(show)
    if already:
        print("（既に当ててある。何も変えない）")
        return 0
    patch(new)
    print("当てた後:")
    print("\n".join(summary(new)))
    if not a.apply:
        print("書いていない（--apply で書く）")
        return 0
    bak = ROOT / "Logs/show-backup"
    bak.mkdir(parents=True, exist_ok=True)
    shutil.copy2(SHOW, bak / f"show_{time.strftime('%Y%m%d_%H%M%S')}_before-trail.json")
    data = json.dumps(new, ensure_ascii=False, indent=2).encode("utf-8")
    if raw.startswith(b"\xef\xbb\xbf"):
        data = b"\xef\xbb\xbf" + data
    tmp = SHOW.with_suffix(".json.tmp")
    tmp.write_bytes(data)
    json.loads(tmp.read_bytes().decode("utf-8-sig"))
    os.replace(tmp, SHOW)
    print(f"書いた: {SHOW}（元は {bak}）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
