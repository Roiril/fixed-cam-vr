"""監査役の JSON と key.json を突き合わせて点を出す。py -3.11 tools/twin/audit_score.py <監査フォルダ> <監査役の JSON>

合否: 実写の較正 ≥ 90、過去版の較正 ≤ 75（これを外れた監査は無効）。合格はレンダの全画（切り抜き含む）の平均 > 95 かつ最低 ≥ 90。
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    folder, res = Path(sys.argv[1]), Path(sys.argv[2])
    key = json.loads((folder / "key.json").read_text(encoding="utf-8"))
    text = res.read_text(encoding="utf-8")
    m = re.search(r"\{.*\}", text, re.S)
    data = json.loads(m.group(0))
    rows = []
    for it in data["images"]:
        info = key.get(it["name"])
        if info is None:
            print("? 不明な名前", it["name"])
            continue
        rows.append((info, it))
    missing = set(key) - {it["name"] for _, it in rows}
    if missing:
        print("未採点", sorted(missing))
    real = [it["score"] for i, it in rows if i["kind"] == "anchor_real"]
    old = [it["score"] for i, it in rows if i["kind"] == "anchor_cg"]
    rend = [(i, it) for i, it in rows if i["kind"] == "render"]
    scores = [it["score"] for _, it in rend]
    by = {}
    for i, it in rend:
        by.setdefault(i["shot"], []).append(it["score"])
    print("較正 実写", real, "過去版", old)
    for k in sorted(by, key=lambda x: sum(by[x]) / len(by[x])):
        print(f"  {k:6s} {sum(by[k]) / len(by[k]):5.1f}  {by[k]}")
    mean = sum(scores) / len(scores)
    print(f"平均 {mean:.1f} 最低 {min(scores)} 枚数 {len(scores)}")
    valid = min(real) >= 90 and max(old) <= 75 and not missing
    passed = valid and mean > 95 and min(scores) >= 90
    print("監査は有効" if valid else "監査は無効", "合格" if passed else "不合格")
    print("--- CG の痕跡（低い順）")
    for i, it in sorted(rend, key=lambda r: r[1]["score"]):
        print(f"[{it['score']}] {i['shot']} {i['view']}: " + " / ".join(it.get("tells", [])))
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
