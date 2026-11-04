"""質感の盲検監査の束を作る。py -3.11 tools/twin/audit_pack.py <撮影フォルダ> [--crops 2]

撮影の PNG・その等倍の切り抜き・較正用の画像（実写のプレートと過去の URP 版）を、由来の分からない名前で 1 つのフォルダへ並べる。
由来の表は key.json（監査役には読ませない）。出力は Drive の renders/twin-cycles/audit/<撮影フォルダ名>/。
"""
from __future__ import annotations

import argparse
import hashlib
import json
import random
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
AUDIT = Path(r"G:\マイドライブ\shubie\renders\twin-cycles\audit")
PLATES = ROOT / "Assets" / "StreamingAssets" / "show" / "assets"
OLD = Path(r"G:\マイドライブ\shubie\renders\twin\20261104_043901_v59")
CROP = (1000, 640)


def name_for(tag: str, salt: str) -> str:
    return "img_" + hashlib.sha1((salt + tag).encode()).hexdigest()[:8]


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("--crops", type=int, default=2)
    a = ap.parse_args()
    src = Path(a.src)
    out = AUDIT / src.name
    out.mkdir(parents=True, exist_ok=True)
    rnd = random.Random(src.name)
    salt = src.name
    key = {}
    shots = sorted(p for p in src.glob("*.png"))
    if not shots:
        raise SystemExit(f"[audit] PNG が無い: {src}")
    for p in shots:
        im = Image.open(p).convert("RGB")
        nm = name_for("full-" + p.stem, salt)
        im.save(out / f"{nm}.png")
        key[nm] = {"kind": "render", "shot": p.stem, "view": "full"}
        w, h = im.size
        for i in range(a.crops):
            cw, ch = min(CROP[0], w), min(CROP[1], h)
            x, y = rnd.randint(0, w - cw), rnd.randint(0, h - ch)
            nm = name_for(f"crop{i}-{p.stem}", salt)
            im.crop((x, y, x + cw, y + ch)).save(out / f"{nm}.png")
            key[nm] = {"kind": "render", "shot": p.stem, "view": "crop", "box": [x, y, cw, ch]}
    for cid in "ABC":
        cands = sorted(PLATES.glob(f"plate_{cid}_2026*.jpg"))
        if not cands:
            raise SystemExit(f"[audit] 実写のプレートが無い: {cid}")
        im = Image.open(cands[-1]).convert("RGB")
        nm = name_for("real-" + cid, salt)
        im.save(out / f"{nm}.png")
        key[nm] = {"kind": "anchor_real", "shot": cands[-1].name, "view": "full"}
    for shot in ("hero", "close"):
        p = OLD / f"{shot}.png"
        if not p.exists():
            raise SystemExit(f"[audit] 過去版が無い: {p}")
        nm = name_for("old-" + shot, salt)
        Image.open(p).convert("RGB").save(out / f"{nm}.png")
        key[nm] = {"kind": "anchor_cg", "shot": shot, "view": "full"}
    (out / "key.json").write_text(json.dumps(key, ensure_ascii=False, indent=1), encoding="utf-8")
    print("AUDIT", out, len(key), "images")
    return 0


if __name__ == "__main__":
    sys.exit(main())
