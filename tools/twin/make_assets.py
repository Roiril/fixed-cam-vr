"""ツインの部屋で作り直した合成素材を、本番の cue のファイル名へ当てはめて並べる。

    py -3.11 tools/twin/make_assets.py [--out Logs/twin-run/assets]

tools/twin/assets.json の cue id ごとに、show.json の今の sourceUrl / maskUrl のファイル名で
tools/twin/assets/ の素材をコピーする。Editor は FIXEDCAM_ASSET_OVERRIDE_DIR にこのフォルダが
指定されているときだけ、同じ名前のファイルを焼き込みの代わりに読む（ShowAssetResolver.TryOverride）。

⚠ 同じファイルを 2 つの cue が別の中身で使っていると、どちらかが負ける。そのときは止まる。
⚠ 本番の素材は 1 つも書き換えない。出力先は git 管理外。
"""
from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
SHOW = ROOT / "Assets" / "StreamingAssets" / "show" / "show.json"


def file_of(url: str) -> str:
    return url.rsplit("/", 1)[-1] if url else ""


def build(out: Path) -> dict[str, str]:
    spec = json.loads((HERE / "assets.json").read_text(encoding="utf-8"))["cues"]
    cues = {c.get("id"): c for c in json.loads(SHOW.read_text(encoding="utf-8-sig"))["cues"]}
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)
    placed: dict[str, Path] = {}
    for cid, roles in spec.items():
        cue = cues.get(cid)
        if cue is None:
            raise SystemExit(f"show.json に cue {cid} が無い（assets.json を直す）")
        for role, key in (("source", "sourceUrl"), ("mask", "maskUrl")):
            if role not in roles:
                continue
            name = file_of(cue.get(key, ""))
            if not name:
                raise SystemExit(f"cue {cid} に {key} が無い")
            src = (HERE / "assets" / roles[role]).resolve()
            if not src.is_file():
                raise SystemExit(f"素材が無い: {src}")
            if name in placed and placed[name] != src:
                raise SystemExit(f"{name} を 2 つの素材が取り合っている: {placed[name]} / {src}")
            placed[name] = src
            shutil.copy2(src, out / name)
    return {k: str(v.relative_to(ROOT)) for k, v in placed.items()}


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=str(ROOT / "Logs" / "twin-run" / "assets"))
    a = ap.parse_args()
    placed = build(Path(a.out))
    (Path(a.out) / "assets_map.json").write_text(json.dumps(placed, ensure_ascii=False, indent=1), encoding="utf-8")
    for k, v in placed.items():
        print(f"  {k} <- {v}")
    print(f"{len(placed)} 本 → {a.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
