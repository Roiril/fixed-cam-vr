"""本番の台本（tools/web-compositor/show.json）の 2 周目 A を、右の幕から人形が笑う形へ書き換える（LEDGER 0376）。

  段 0  入ってくると左の幕に顔のシミ（cue_stain_A・滲んで出る）。体が左の幕の端（s2a_leftend）に届くまで
  段 1  右の幕に顔のシミが出て、左へ画面の半分まで伸び出す。右の幕の裏から人形が覗いて笑う。笑い声が 1 回
  段 2  人形は幕の裏へ身を引き、黙ってこちらを見ている。少し歩く（s2a_walk）か報告で次へ
  段 3  乱れで CG の人形に替わる。幕の裏の同じ所から出て、歩いた跡をついてくる。区間を出るまで

左のシミは段 1・2 では 2 枚目の層の掛け算で保つ（1 枚目の層は右の動画・静止画が使う）。段 3 は左右のシミを 1 枚の比の画で出す。
人形は画像の人形と同じ背丈（0.8m）の CG（doll_mid）。段 3 の立ち始めは画像の人形が立っていた床の点。

何度流しても同じ結果になる。書く前に Logs/show-backup/ へ元の show.json を写す。
卓が動いているときは、卓がメモリの版でディスクを上書きするので、卓を止めてから流す。

    py -3.11 tools/show-apply-stain-right.py            # 変わる所を並べるだけ
    py -3.11 tools/show-apply-stain-right.py --apply    # 書く。そのあと py -3.11 tools/export-show-build.py で焼く

素材は static-inputs に置いてから流す（作るのは tools/gen-plate/stain3/build_right_burst.py）。
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
STATIC = ROOT / "tools/web-compositor/static-inputs"
TAKE = "L2C0#0"
DOLL = "doll_mid"
DOLL_HEIGHT_M = 0.8
# 画像の人形が幕の裏で見ている床の点（tools/gen-plate/stain3/README.md の「右の幕の人形」）。CG はここから出る
DOLL_FROM = (0.44, 0.62)
TAG = "20261115"

LINES = [
    ("s2a_leftend", 0, -0.5, -0.22, 0.3, -0.22, "2周目A 左の幕の端（体が届くと右の幕から人形が笑う）"),
    ("s2a_walk", 0, -0.5, -0.45, 0.3, -0.45, "2周目A 少し歩いた線（CG の人形に替わってついてくる）"),
]
OLD_LINES = {"trail_A_behind"}

CUES = [
    {"id": "cue_stain_A_left_hold", "name": "左の幕の顔のシミ（出きった後・2 枚目の層で保つ）", "camera": "A",
     "maskUrl": "/static-inputs/stain_A_press_leftmask_20260925.png",
     "sourceUrl": "/static-inputs/stain_A_ratio_20261113.png", "strength": 1, "loop": False,
     "fadeIn": 0, "fadeOut": 0, "blend": "multiply"},
    {"id": "stain_A_right_burst", "name": "右の幕の顔が左へ伸び出し、幕の裏の人形が笑う（2周目A）", "camera": "A",
     "maskUrl": f"/static-inputs/stain_A_right_mask_{TAG}.png",
     "sourceUrl": f"/static-inputs/stain_A_right_burst_{TAG}.mp4", "strength": 1, "loop": False,
     "fadeIn": 0, "fadeOut": 0},
    {"id": "stain_A_right_watch", "name": "右の幕の裏で人形が黙って見ている（2周目A）", "camera": "A",
     "maskUrl": f"/static-inputs/stain_A_right_mask_{TAG}.png",
     "sourceUrl": f"/static-inputs/stain_A_right_watch_{TAG}.png", "strength": 1, "loop": False,
     "fadeIn": 0, "fadeOut": 0},
    {"id": "cue_stain_A_both", "name": "左右の幕の顔のシミ（CG の人形がついてくる間）", "camera": "A",
     "maskUrl": f"/static-inputs/stain_A_both_mask_{TAG}.png",
     "sourceUrl": f"/static-inputs/stain_A_both_ratio_{TAG}.png", "strength": 1, "loop": False,
     "fadeIn": 0, "fadeOut": 0, "blend": "multiply"},
]


WATCH_MIN_SEC = 0.6


def step(source: str, **kw) -> dict:
    d = {"source": source, "camera": -1, "cueId": "", "strength": -1, "fadeInSec": -1, "fadeOutSec": -1,
         "trimStartSec": -1, "trimEndSec": -1, "durKind": "sec", "lineId": "", "durSec": 0,
         "transition": "cut", "cg": "", "cgMode": "follow", "switchSfx": False}
    d.update(kw)
    return d


def burst_sec() -> float:
    meta = STATIC / f"stain_A_right_burst_{TAG}.json"
    if meta.exists():
        return float(json.loads(meta.read_text(encoding="utf-8"))["durationSec"])
    return 2.6


def patch(show: dict) -> None:
    tall = next(a for a in show["actors"] if a["id"] in ("doll_tall", "doll"))
    show["actors"] = [a for a in show["actors"] if a["id"] != DOLL] + [
        dict(tall, id=DOLL, name="幕の裏の人形（2周目A・画像の人形と同じ背丈）", heightM=DOLL_HEIGHT_M)]

    ids = {x[0] for x in LINES} | OLD_LINES
    lines = [ln for ln in show["layout"]["lines"] if ln["id"] not in ids]
    for lid, cam, x1, z1, x2, z2, label in LINES:
        lines.append({"id": lid, "camera": cam, "x1": x1, "z1": z1, "x2": x2, "z2": z2,
                      "dir": "both", "label": label, "mirrorOf": ""})
    show["layout"]["lines"] = lines

    cue_ids = {c["id"] for c in CUES}
    show["cues"] = [c for c in show["cues"] if c["id"] not in cue_ids] + copy.deepcopy(CUES)

    for seg in show["timeline"]["segments"]:
        if int(seg.get("lap", 0)) != 2 or int(seg.get("camera", 0)) != 0:
            continue
        take = next(t for t in seg["takes"] if t["id"] == TAKE)
        take["name"] = "2周目A 左の幕のシミ → 右の幕の裏で人形が笑う → 隠れて見る → CG がついてくる"
        take["policy"] = "yield"
        take["ifMissed"] = "skip"
        take["dismissible"] = True
        # 報告の後に左の幕の縁から人形が覗く一瞬（0330）は、右の幕の人形と重なるので外す。押している間の膨らみは残す
        take.pop("markDoneCueId", None)
        take.pop("markDoneSec", None)
        take["steps"] = [
            step("inherit", cueId="cue_stain_A", durKind="untilBeyond", lineId="s2a_leftend", durSec=40),
            step("inherit", cueId="stain_A_right_burst", overlay2CueId="cue_stain_A_left_hold",
                 durSec=burst_sec(), dollLaugh=True, markAdvance=True),
            # 見ている人形は線を既に越えていても最短 WATCH_MIN_SEC は見せる。報告ならすぐ CG へ進む
            step("inherit", cueId="stain_A_right_watch", overlay2CueId="cue_stain_A_left_hold",
                 durKind="untilBeyond", lineId="s2a_walk", durSec=8, beyondMinSec=WATCH_MIN_SEC,
                 markAdvance=True),
            step("inherit", cueId="cue_stain_A_both", camera=0, cg=DOLL, cgMode="trail", transition="glitch",
                 durKind="untilZoneChange", trailGapM=0.9, trailStepM=0.45, trailHideM=0.6,
                 trailFromX=DOLL_FROM[0], trailFromZ=DOLL_FROM[1], trailHopSec=0.55),
        ]
        seg["takes"] = [t for t in seg["takes"] if t["id"] in (TAKE,) or not t["id"].startswith("L2C0#")]
    show["timeline"]["rev"] = int(show["timeline"].get("rev", 0)) + 1
    show["layout"]["rev"] = int(show["layout"].get("rev", 0)) + 1
    show["rev"] = int(show.get("rev", 0)) + 1


def summary(show: dict) -> list:
    out = []
    for seg in show["timeline"]["segments"]:
        if int(seg.get("lap", 0)) != 2 or int(seg.get("camera", 0)) != 0:
            continue
        for t in seg["takes"]:
            out.append(f"  2-A {t['id']} at={t.get('at')} policy={t.get('policy')} markStart={t.get('markStartCueId', '')}"
                       f" markDone={t.get('markDoneCueId', '')}:")
            for s in t["steps"]:
                out.append(f"    {s['source']}[{s.get('cueId') or '-'}{' +' + s['overlay2CueId'] if s.get('overlay2CueId') else ''}"
                           f"{' cg=' + s['cg'] + '/' + s['cgMode'] if s.get('cg') else ''}] {s['durKind']}"
                           f"{':' + s['lineId'] if s.get('lineId') else ''} {s.get('durSec')}s"
                           f"{' laugh' if s.get('dollLaugh') else ''}{' advance' if s.get('markAdvance') else ''}")
    return out


def missing_assets() -> list:
    out = []
    for c in CUES:
        for k in ("maskUrl", "sourceUrl"):
            name = c[k].removeprefix("/static-inputs/")
            if not (STATIC / name).exists():
                out.append(name)
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
    new = copy.deepcopy(show)
    patch(new)
    print("当てた後:")
    print("\n".join(summary(new)))
    miss = missing_assets()
    if miss:
        print("static-inputs に無い素材: " + ", ".join(miss))
    if not a.apply:
        print("書いていない（--apply で書く）")
        return 0
    if miss:
        print("素材が揃っていないので書かない")
        return 1
    bak = ROOT / "Logs/show-backup"
    bak.mkdir(parents=True, exist_ok=True)
    shutil.copy2(SHOW, bak / f"show_{time.strftime('%Y%m%d_%H%M%S')}_before-stain-right.json")
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
