"""本番の台本（tools/web-compositor/show.json）の 2 つの異変を、滲んで現れる掛け算の合成へ書き換える（0349）。

  1 周目 B  cue_hands_B          壁の手形。比の画を掛け、順の画で 1 つずつ滲み出す
  2 周目 A  cue_stain_A          幕の顔のシミ。比の画を掛け、芯から垂れの先へ滲み出す
            stain_A_press_bulge  報告の長押しで、シミのある所の布が膨らむ（press マスクと膨らみの中心）
            stain_A_done_doll    報告の直後に出る人形を、人形の周りだけのマスクで切る

素材は tools/gen-plate/bleed_maps.py が Logs/gen-plate/bleed/out/ へ出す。先に static-inputs/ へ写す:

    copy Logs\\gen-plate\\bleed\\out\\stain_A_{ratio,reveal,pressmask,dollmask}_20261113.png tools\\web-compositor\\static-inputs\\
    copy Logs\\gen-plate\\bleed\\out\\hands_B_{ratio,reveal}_20261113.png tools\\web-compositor\\static-inputs\\

何度流しても同じ結果になる。書く前に Logs/show-backup/ へ元の show.json を写す。
卓が動いているときは、卓がメモリの版でディスクを上書きするので、卓を止めてから流す。

    py -3.11 tools/show-apply-bleed.py            # 変わる所を並べるだけ
    py -3.11 tools/show-apply-bleed.py --apply    # 書く。そのあと py -3.11 tools/export-show-build.py で焼く
    py -3.11 tools/show-apply-bleed.py --show <別の show.json>

時間（revealSec ほか）と膨らみの中心は、レシピ（tools/gen-plate/mask-recipes/bleed-*.json）の cue と
bleed_maps.py が出した maps の JSON から読む。ここに数値を写さない。
"""
from __future__ import annotations

import argparse
import copy
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RECIPES = ROOT / "tools/gen-plate/mask-recipes"
BLEED_OUT = ROOT / "Logs/gen-plate/bleed/out"
STATIC = "/static-inputs/"

STAIN_RECIPE = "bleed-stain-a-20261113.json"
HANDS_RECIPE = "bleed-hands-b-20261113.json"

# レシピの cue から cue へ写すキー（markDoneRevealOutSec は C# の定数 OverlayRevealLogic.MarkDoneOutSec が持つ）
REVEAL_KEYS = ("revealSec", "revealDelaySec", "revealOutSec")


def main_tree_show() -> Path:
    """この木の show.json。worktree で走らせたときは、メインの木の show.json を返す（git 管理外のため木には無い）。"""
    own = ROOT / "tools/web-compositor/show.json"
    if own.exists():
        return own
    try:
        common = subprocess.run(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"],
                                cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip()
    except (OSError, subprocess.CalledProcessError):
        return own
    return Path(common).parent / "tools/web-compositor/show.json"


def load_json(path: Path) -> dict:
    return json.loads(path.read_bytes().decode("utf-8-sig"))


def load_plan() -> dict:
    """レシピと maps の JSON から、cue ごとに当てる値を組む。"""
    stain = load_json(RECIPES / STAIN_RECIPE)
    hands = load_json(RECIPES / HANDS_RECIPE)
    maps_path = BLEED_OUT / stain["outputs"]["maps"]
    if not maps_path.exists():
        raise SystemExit(f"maps が無い: {maps_path}（先に py -3.11 tools/gen-plate/bleed_maps.py "
                         f"--recipe tools/gen-plate/mask-recipes/{STAIN_RECIPE}）")
    center = load_json(maps_path).get("pressCenter")
    if not (isinstance(center, list) and len(center) == 2):
        raise SystemExit(f"maps に pressCenter が無い: {maps_path}")

    def reveal(recipe: dict) -> dict:
        out = recipe["outputs"]
        d = {"blend": "multiply", "sourceUrl": STATIC + out["ratio"], "revealUrl": STATIC + out["reveal"]}
        for k in REVEAL_KEYS:
            d[k] = float(recipe["cue"][k])
        return d

    so = stain["outputs"]
    return {
        "cue_hands_B": reveal(hands),
        "cue_stain_A": reveal(stain),
        "stain_A_press_bulge": {"pressMaskUrl": STATIC + so["pressMask"],
                                "pressCenter": [round(float(center[0]), 4), round(float(center[1]), 4)]},
        "stain_A_done_doll": {"maskUrl": STATIC + so["dollMask"]},
    }


def needed_files(plan: dict) -> list[str]:
    names = []
    for fields in plan.values():
        for k, v in fields.items():
            if isinstance(v, str) and v.startswith(STATIC) and k.endswith("Url"):
                names.append(v[len(STATIC):])
    return sorted(set(names))


def patch(show: dict, plan: dict) -> list[str]:
    """show を書き換え、変わったキーを 1 行ずつ返す。無い cue は飛ばして報告する。"""
    lines = []
    cues = {c.get("id"): c for c in show.get("cues", [])}
    for cue_id, fields in plan.items():
        cue = cues.get(cue_id)
        if cue is None:
            lines.append(f"  ! {cue_id}: show.json に無い（飛ばす）")
            continue
        for k, v in fields.items():
            if cue.get(k) != v:
                lines.append(f"  {cue_id}.{k}: {json.dumps(cue.get(k), ensure_ascii=False)} → "
                             f"{json.dumps(v, ensure_ascii=False)}")
                cue[k] = v
    if any(not ln.startswith("  !") for ln in lines):
        show["rev"] = int(show.get("rev", 0)) + 1
    return lines


def press_take_check(show: dict) -> list[str]:
    """膨らみは、take の markStartCueId に名指しされた cue でしか起きない。名指しが無ければ知らせる。"""
    named = set()
    for seg in show.get("timeline", {}).get("segments", []):
        for t in seg.get("takes", []):
            if t.get("markStartCueId"):
                named.add(t["markStartCueId"])
    if "stain_A_press_bulge" in named:
        return []
    return ["  ! stain_A_press_bulge を markStartCueId に持つ take が無い。膨らみは起きない（take は書き換えない）"]


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true", help="show.json へ書く（既定は並べるだけ）")
    ap.add_argument("--show", type=Path, default=None, help="書き換える show.json（既定はメインの木の版）")
    a = ap.parse_args()

    show_path = a.show or main_tree_show()
    if not show_path.exists():
        print(f"show.json が無い: {show_path}")
        return 1
    plan = load_plan()
    static_dir = show_path.parent / "static-inputs"
    missing = [n for n in needed_files(plan) if not (static_dir / n).exists()]

    raw = show_path.read_bytes()
    show = json.loads(raw.decode("utf-8-sig"))
    new = copy.deepcopy(show)
    changes = patch(new, plan)
    changes += press_take_check(new)

    print(f"対象: {show_path}")
    real = [ln for ln in changes if not ln.startswith("  !")]
    if not real:
        print("（既に当ててある。何も変えない）")
    else:
        print("変わる所:")
    print("\n".join(changes))
    if missing:
        print(f"static-inputs に無い素材（{static_dir}）:")
        for n in missing:
            src = BLEED_OUT / n
            print(f"  {n}  ← {src}{'' if src.exists() else '（これも無い。bleed_maps.py を先に流す）'}")
    if not real:
        return 0
    if not a.apply:
        print("書いていない（--apply で書く）")
        return 0
    if missing:
        print("素材が揃っていないので書かない")
        return 1

    bak = ROOT / "Logs/show-backup"
    bak.mkdir(parents=True, exist_ok=True)
    shutil.copy2(show_path, bak / f"show_{time.strftime('%Y%m%d_%H%M%S')}_before-bleed.json")
    data = json.dumps(new, ensure_ascii=False, indent=2).encode("utf-8")
    if raw.startswith(b"\xef\xbb\xbf"):
        data = b"\xef\xbb\xbf" + data
    tmp = show_path.with_suffix(".json.tmp")
    tmp.write_bytes(data)
    json.loads(tmp.read_bytes().decode("utf-8-sig"))
    os.replace(tmp, show_path)
    print(f"書いた: {show_path}（元は {bak}）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
