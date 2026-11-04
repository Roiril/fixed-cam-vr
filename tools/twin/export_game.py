"""見る面（tools/twin-game）が読むデータを書く。py -3.11 tools/twin/export_game.py

twin.json: 機材・壁・区画・カメラの投影（オーバーレイ用）。routes/<route>.json: 30Hz の表と出来事。
カットは Unity の仮想体験と同じく active / dim に従う（pick ではない）。
"""
from __future__ import annotations

import csv
import json
import math
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
OUT = HERE.parent / "twin-game" / "data"
SHOW = ROOT / "Assets" / "StreamingAssets" / "show" / "show.json"
CYCLE_M = 0.86


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    OUT.mkdir(parents=True, exist_ok=True)
    rig = json.loads((HERE / "rig.json").read_text(encoding="utf-8"))
    show = json.loads(SHOW.read_text(encoding="utf-8-sig"))
    layout = show["layout"]
    twin = {
        "room": {"x": [rig["room"]["xMin"], 4.6], "z": [-4.6, rig["room"]["zMax"]], "h": 3.2, "bulb": [0.02, 2.55, -0.02]},
        "walls": layout["room"]["walls"],
        "grid": layout["grid"],
        "cameras": rig["cameras"],
        "shelf": rig["shelf"],
    }
    (OUT / "twin.json").write_text(json.dumps(twin, ensure_ascii=False, indent=1), encoding="utf-8")
    for route in ("forward", "reverse", "backtrack"):
        rows = list(csv.DictReader((HERE / "routes" / f"{route}.tsv").open(encoding="utf-8"), delimiter="\t"))
        table = []
        dist = 0.0
        step = 0
        for i, r in enumerate(rows):
            if i:
                dist += math.hypot(float(r["x"]) - float(rows[i - 1]["x"]), float(r["z"]) - float(rows[i - 1]["z"]))
            table.append([float(r["t"]), float(r["x"]), float(r["z"]), float(r["bodyYaw"]), int(float(r["active"])), float(r["dim"]), float(r["glitch"]), float(r["speed"]), round(dist, 4), float(r["meshClearance"])])
        ev_path = HERE / "routes" / f"{route}.events.tsv"
        events = []
        if ev_path.exists():
            for e in csv.DictReader(ev_path.open(encoding="utf-8"), delimiter="\t"):
                events.append([float(e["t"]), e["kind"], e["detail"]])
        (OUT / f"route_{route}.json").write_text(json.dumps({"route": route, "columns": ["t", "x", "z", "yaw", "active", "dim", "glitch", "speed", "dist", "clear"], "cycleM": CYCLE_M, "table": table, "events": events}, ensure_ascii=False), encoding="utf-8")
        print(route, len(table), "行", len(events), "件", table[-1][0], "秒")
    return 0


if __name__ == "__main__":
    sys.exit(main())
