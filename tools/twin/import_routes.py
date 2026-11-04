"""Unity の仮想体験の記録をツインの経路へ取り込む。

    .\\tools\\unity.ps1 menu virtualwalk -Set laps=1,route=all,path=rail,tag=rail
    py -3.11 tools/twin/import_routes.py "<Drive の renders/virtualwalk/<時刻>_rail>"   # 接尾辞 _forward 等は付けない

frames.tsv（90Hz）を 30Hz に間引いて routes/<route>.tsv へ、events.tsv を routes/<route>.events.tsv へ写す。
navigation.json の path が rail でなければ止まる（体を避ける検証用の道は実際の歩き方ではない）。
"""
from __future__ import annotations

import csv
import json
import os
import shutil
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
OUT = HERE / "routes"
COLUMNS = ["t", "x", "z", "bodyYaw", "screenYaw", "pick", "active", "dipping", "dim", "glitch", "speed", "meshClearance"]


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    base = sys.argv[1].rstrip("\\/")
    sources = {route: Path(f"{base}_{route}") for route in ("forward", "reverse", "backtrack")}
    for route, src in sources.items():
        for name in ("frames.tsv", "events.tsv", "navigation.json"):
            if not (src / name).is_file():
                print(f"無い: {src / name}")
                return 1
        nav = json.loads((src / "navigation.json").read_text(encoding="utf-8-sig"))
        if nav.get("path") != "rail":
            print(f"{route}: path={nav.get('path')} は手すりの道ではない")
            return 1
    for route, src in sources.items():
        with (src / "frames.tsv").open(encoding="utf-8", newline="") as f:
            rows = list(csv.DictReader(f, delimiter="\t"))
        tmp = OUT / f"{route}.tsv.tmp"
        with tmp.open("w", encoding="utf-8", newline="\n") as f:
            w = csv.writer(f, delimiter="\t", lineterminator="\n")
            w.writerow(COLUMNS)
            for r in rows[::3]:
                w.writerow([r[c] for c in COLUMNS])
        os.replace(tmp, OUT / f"{route}.tsv")
        shutil.copyfile(src / "events.tsv", OUT / f"{route}.events.tsv")
        print(route, len(rows[::3]), "行", rows[-1]["t"], "秒")
    return 0


if __name__ == "__main__":
    sys.exit(main())
