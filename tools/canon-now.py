"""canon/NOW.md（体験の現在形）の骨格を出し、ずれを検査する。

    py -3.11 tools/canon-now.py --skeleton   # show.json の区間と take から周回の行の骨格を出す
    py -3.11 tools/canon-now.py --check      # ERROR / WARN を出す。ERROR があれば exit 1
    py -3.11 tools/canon-now.py --orphans    # どの行からも引かれていない LEDGER 番号を出す

NOW.md は手で書く。ただし秒数は書かない（show.json と実装が正）。
人が書くのは「体験者に見えること」と「根拠（LEDGER 番号、または 未判定）」だけ。
手書きの体験仕様は必ずずれる（rules/show-design.md は 188KB の差分ログになった）ので、
ずれを防ぐのではなく検出する:

  1. show.json の take id が NOW.md に過不足なく載っているか
  2. 根拠に書いた LEDGER 番号が実在するか（重複番号は 0075a / 0075b と出現順に呼ぶ）
  3. NOW.md の「最終確認」より新しい LEDGER 項目（場面の行を見直したか）
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parent.parent
NOW = ROOT / ".claude" / "canon" / "NOW.md"
LEDGER = ROOT / ".claude" / "canon" / "LEDGER.md"
SHOW = ROOT / "Assets" / "StreamingAssets" / "show" / "show.json"
CAM = "ABC"


def ledger_entries() -> list[tuple[str, str]]:
    """(引用キー, 見出し) を出現順に。重複番号は 2 つ目から a, b … ではなく、全出現に a, b を振る。"""
    heads = re.findall(r"^#{2,3} (\d{4}) +(.*)$", LEDGER.read_text(encoding="utf-8"), re.M)
    count: dict[str, int] = {}
    for n, _ in heads:
        count[n] = count.get(n, 0) + 1
    seen: dict[str, int] = {}
    out = []
    for n, title in heads:
        if count[n] > 1:
            k = n + "abcdefgh"[seen.get(n, 0)]
            seen[n] = seen.get(n, 0) + 1
        else:
            k = n
        out.append((k, title.strip()))
    return out


def show_takes() -> list[tuple[str, str, str]] | None:
    if not SHOW.exists():
        return None
    d = json.loads(SHOW.read_text(encoding="utf-8"))
    out = []
    for s in d.get("timeline", {}).get("segments", []):
        seg = f"{s['lap']}-{CAM[s['camera']] if 0 <= s['camera'] < 3 else s['camera']}"
        for t in s.get("takes", []):
            out.append((seg, t["id"], t.get("name", "")))
    return out


CITE = re.compile(r"(?<![\d.])(0\d{3}[a-h]?)(?![\d.])")


def now_rows() -> list[list[str]]:
    rows = []
    for line in NOW.read_text(encoding="utf-8").splitlines():
        if not line.startswith("|") or set(line) <= set("|-: "):
            continue
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if cells and cells[0] in ("場面", "区間"):
            continue
        rows.append(cells)
    return rows


def last_checked() -> str | None:
    m = re.search(r"最終確認:\s*LEDGER\s*(0\d{3}[a-h]?)", NOW.read_text(encoding="utf-8"))
    return m.group(1) if m else None


def check() -> int:
    errors, warns = [], []
    if not NOW.exists():
        print("ERROR canon/NOW.md が無い")
        return 1
    rows = now_rows()
    text = NOW.read_text(encoding="utf-8")
    keys = [k for k, _ in ledger_entries()]
    keyset = set(keys)
    dup_base = {k[:4] for k in keys if len(k) == 5}

    takes = show_takes()
    if takes is None:
        warns.append(f"{SHOW.relative_to(ROOT).as_posix()} が無いので take の過不足は見ていない")
    else:
        listed = set(re.findall(r"`(L\d+C\d+#\d+)`", text))
        want = {t for _, t, _ in takes}
        for seg, t, name in takes:
            if t not in listed:
                errors.append(f"take `{t}`（{seg} {name[:30]}）が NOW.md に無い")
        for t in sorted(listed - want):
            errors.append(f"take `{t}` は show.json に無い（消えた take の行が残っている）")

    for cells in rows:
        basis = cells[-1] if cells else ""
        for c in CITE.findall(basis):
            if c in keyset:
                continue
            if c in dup_base:
                errors.append(f"LEDGER {c} は番号が重複している。{c}a / {c}b …（出現順）で引く")
            else:
                errors.append(f"LEDGER {c} は存在しない（行: {cells[0][:20]}）")
        if not basis or basis in ("—", "-"):
            errors.append(f"根拠が空の行（{cells[0][:20]}）。LEDGER 番号か「未判定」を書く")

    lc = last_checked()
    if lc is None:
        errors.append("「最終確認: LEDGER <番号>」の行が無い")
    elif lc not in keyset:
        errors.append(f"最終確認の LEDGER {lc} が存在しない")
    else:
        newer = [(k, t) for k, t in ledger_entries()[keys.index(lc) + 1:]]
        if newer:
            warns.append(f"最終確認（LEDGER {lc}）より新しい項目が {len(newer)} 件。場面の行を見直して最終確認を進める: "
                         + " / ".join(f"{k} {t[:24]}" for k, t in newer[:6]))

    for e in errors:
        print("ERROR", e)
    for w in warns:
        print("WARN ", w)
    print(f"canon-now: ERROR {len(errors)} / WARN {len(warns)}")
    return 1 if errors else 0


def skeleton() -> int:
    takes = show_takes()
    if takes is None:
        print("show.json が無い")
        return 1
    print("| 区間 | take | 体験者に見えること | 根拠 |")
    print("|---|---|---|---|")
    for seg, t, name in takes:
        print(f"| {seg} | `{t}` | {name} | 未判定 |")
    return 0


def orphans() -> int:
    cited = set(CITE.findall(NOW.read_text(encoding="utf-8")))
    ents = ledger_entries()
    miss = [(k, t) for k, t in ents if k not in cited]
    print(f"NOW.md から引かれていない LEDGER 項目: {len(miss)} / {len(ents)}")
    for k, t in miss:
        print(f"  {k}  {t[:60]}")
    return 0


if __name__ == "__main__":
    if "--skeleton" in sys.argv:
        sys.exit(skeleton())
    if "--orphans" in sys.argv:
        sys.exit(orphans())
    sys.exit(check())
