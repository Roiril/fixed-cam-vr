"""長押しの間だけ人形の下からライブの自分が透ける版を、検証用の木へ当てる（本番の台本と APK には入らない）。

3 周目の 3 本（3-A・3-B・3-C）に take の旗 markBleed を立てる。働くのは人形が出ているカット
（無人の写真＋CG 人形）の間だけで、透ける量は 0.5 × 長押しの進み具合。報告が成立したら即 0 へ戻る
（C# の LiveBleedLogic・TakeRunner.IsBleedArmed）。素材は足さない。

使い方（検証用の木を sync した後）:
  py -3.11 tools/twin-tree.py sync
  py -3.11 tools/twin/variant_bleed.py --root ../fcv-twin-tree
  py -3.11 tools/twin-tree.py run -- --fast --calib twin --tag bleed-after

やること: 木の焼いた show.json の 3 周目の take に markBleed: true を立てる → manifest.json の configSha256 を合わせる。
こちらの木（メイン）には書かない。--only で場面を絞れる（a,b,c）。
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
MAIN = HERE.parents[1]
LAP = 3
CAMERAS = {"a": 0, "b": 1, "c": 2}


def patch_show(show: dict, only: set) -> list[str]:
    """3 周目の、選んだカメラの区間の take すべてに markBleed を立てる。立てた take の id を返す。"""
    want = {CAMERAS[k] for k in only}
    done = []
    seen = set()
    for seg in ((show.get("timeline") or {}).get("segments") or []):
        if seg.get("lap") != LAP or seg.get("camera") not in want:
            continue
        seen.add(seg.get("camera"))
        takes = seg.get("takes") or []
        if not takes:
            sys.exit(f"3 周目 カメラ {seg.get('camera')} の区間に take が無い")
        for t in takes:
            t["markBleed"] = True
            done.append(t.get("id") or "?")
    missing = sorted(want - seen)
    if missing:
        sys.exit(f"3 周目の区間が無い: カメラ {missing}（焼いた show.json の timeline を確かめる）")
    return done


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True, help="当てる木（検証用の木）")
    ap.add_argument("--only", default="a,b,c", help="当てる場面（3 周目の a,b,c）")
    a = ap.parse_args()
    only = {s.strip().lower() for s in a.only.split(",") if s.strip()}
    if not only or not only <= set(CAMERAS):
        sys.exit("--only は a,b,c から選ぶ")
    root = Path(a.root).resolve()
    if root == MAIN.resolve():
        sys.exit("メインの木には当てない（本番の台本を書き換えない）。検証用の木を指す")
    show_p = root / "Assets/StreamingAssets/show/show.json"
    man_p = root / "Assets/StreamingAssets/show/manifest.json"
    for p in (show_p, man_p):
        if not p.exists():
            sys.exit(f"無い: {p}（先に py -3.11 tools/twin-tree.py sync）")

    raw = show_p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    show = json.loads(raw.decode("utf-8-sig"))
    ids = patch_show(show, only)
    data = json.dumps(show, ensure_ascii=False).encode("utf-8")
    if bom:
        data = b"\xef\xbb\xbf" + data
    show_p.write_bytes(data)
    man = json.loads(man_p.read_text(encoding="utf-8-sig"))
    man["configSha256"] = hashlib.sha256(data).hexdigest()
    man_p.write_text(json.dumps(man, ensure_ascii=False, indent=2), encoding="utf-8", newline="\n")
    print(f"[variant] 当てた: {root}（markBleed: {', '.join(ids)}）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
