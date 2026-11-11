#!/usr/bin/env python3
"""固定刻み（--fast）の走行が実時間の走行と同じ体験になっているかを突き合わせる（時計の校正）。

    py -3.11 tools/twin-clock-compare.py <基準の走行> <比べる走行>
    py -3.11 tools/twin-clock-compare.py cal-real-single cal-fast2     # タグの末尾でも引ける

2 つの走行の events.json（twin-verify.py が残す。t は録画の開始を 0 にした体験の時計）を、
出来事の並び（ev と、数字を落とした本文）で対応づけ、順序の一致率と時刻の差を出す。

合否:
- 並びの一致率が MIN_MATCH 以上
- 対応した出来事の時刻差から全体のずれ（中央）を引いた残りの 95% 点が MAX_SPREAD_P95_SEC 以下
- 全体のずれそのものが MAX_OFFSET_SEC 以下（t の 0 点は録画の開始で、走行ごとに 1 秒弱動く）
同じ指標を実時間どうしで取ったものが基準のばらつき（閉ループなので実時間どうしでも揃い切らない）。

SimTime・WallClock・ScreenOverlayController・XpTwinDriver を触ったら回す。
"""
from __future__ import annotations

import difflib
import json
import re
import statistics
import sys
from pathlib import Path

DRIVE = Path("G:/マイドライブ/shubie/renders/twin-verify")
MIN_MATCH = 0.95
MAX_OFFSET_SEC = 2.0
MAX_SPREAD_P95_SEC = 1.0
# 実時間でしか意味の無い行・走るたびに数が変わる行は並びに入れない
SKIP_EV = {"clock", "xprec", "sum", "perf"}
NUM = re.compile(r"-?\d+(?:\.\d+)?")


def find(arg: str) -> Path:
    p = Path(arg)
    if p.is_dir():
        return p
    hits = sorted(d for d in DRIVE.iterdir() if d.is_dir() and d.name.endswith(arg))
    if not hits:
        raise SystemExit(f"走行が見つからない: {arg}")
    return hits[-1]


def key_of(e: dict) -> str:
    """出来事の同一性。数字は走行ごとに揺れるので落とす（区間と周を表す先頭の語は残る）。"""
    return e["ev"] + " " + NUM.sub("#", e.get("text", ""))


def load(d: Path) -> list[dict]:
    ev = json.loads((d / "events.json").read_text(encoding="utf-8"))
    return [e for e in ev if e["ev"] not in SKIP_EV]


def compare(a: list[dict], b: list[dict]) -> dict:
    ka, kb = [key_of(e) for e in a], [key_of(e) for e in b]
    sm = difflib.SequenceMatcher(None, ka, kb, autojunk=False)
    pairs, only_a, only_b = [], [], []
    for tag, i1, i2, j1, j2 in sm.get_opcodes():
        if tag == "equal":
            pairs += [(a[i1 + k], b[j1 + k]) for k in range(i2 - i1)]
        else:
            only_a += a[i1:i2]
            only_b += b[j1:j2]
    signed = [y["t"] - x["t"] for x, y in pairs]
    offset = statistics.median(signed) if signed else 0.0
    diffs = sorted(abs(d - offset) for d in signed)
    worst = sorted(pairs, key=lambda p: -abs(p[1]["t"] - p[0]["t"] - offset))[:5]
    return {
        "n_a": len(a), "n_b": len(b), "matched": len(pairs),
        "match": len(pairs) / max(1, max(len(a), len(b))),
        "offset": offset,
        "median": statistics.median(diffs) if diffs else 0.0,
        "p95": diffs[int(len(diffs) * 0.95)] if diffs else 0.0,
        "max": diffs[-1] if diffs else 0.0,
        "worst": worst, "only_a": only_a, "only_b": only_b,
    }


def verdict(r: dict) -> list[str]:
    bad = []
    if r["match"] < MIN_MATCH:
        bad.append(f"並びの一致率 {r['match']:.1%} < {MIN_MATCH:.0%}")
    if abs(r["offset"]) > MAX_OFFSET_SEC:
        bad.append(f"全体のずれ {r['offset']:+.2f}s が {MAX_OFFSET_SEC}s を超える")
    if r["p95"] > MAX_SPREAD_P95_SEC:
        bad.append(f"ずれを引いた時刻差の 95% 点 {r['p95']:.2f}s > {MAX_SPREAD_P95_SEC}s")
    return bad


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    if len(sys.argv) != 3:
        print(__doc__)
        return 2
    da, db = find(sys.argv[1]), find(sys.argv[2])
    r = compare(load(da), load(db))
    print(f"基準: {da.name}（{r['n_a']} 件）\n比較: {db.name}（{r['n_b']} 件）")
    print(f"並びの一致 {r['matched']} 件 = {r['match']:.1%} / 全体のずれ {r['offset']:+.2f}s / "
          f"ずれを引いた時刻差 中央 {r['median']:.2f}s・95% 点 {r['p95']:.2f}s・最大 {r['max']:.2f}s")
    for x, y in r["worst"]:
        print(f"  差 {abs(y['t'] - x['t'] - r['offset']):5.2f}s  {x['t']:7.2f} / {y['t']:7.2f}  {x['ev']} {x.get('text', '')[:60]}")
    for name, rows in (("基準だけ", r["only_a"]), ("比較だけ", r["only_b"])):
        for e in rows[:12]:
            print(f"  {name} {e['t']:7.2f}  {e['ev']} {e.get('text', '')[:70]}")
        if len(rows) > 12:
            print(f"  {name} ほか {len(rows) - 12} 件")
    bad = verdict(r)
    print("NOT PASS: " + " / ".join(bad) if bad else "PASS")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
