"""焼けた「隙間から眼が覗く画」を参照①にして、飛びかかる最後のフレームだけを焼き直す（anomaly/kidsroom-cat.md の <!-- last -->）。

    py -3.11 tools/gen-plate/bake-kidsroom-lunge.py <眼が覗く画.png> [<眼が覗く画.png> ...]

出力は同じフォルダの lunge.png と lunge_169.png。1 本ずつ直列。
"""
import importlib.util
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("cat", HERE / "bake-kidsroom-cat.py")
cat = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cat)


def main():
    srcs = [Path(p).resolve() for p in sys.argv[1:]]
    if not srcs:
        sys.exit(__doc__)
    blocks = cat.REPO / "Logs" / "gen-plate" / "kr_block"
    common, _, last = cat.sections(False)
    bad = 0
    for src in srcs:
        d = src.parent
        out = cat.bake(d, "lunge", last + "\n\n## 参考（部屋の説明。参照画像①に既に写っている）\n\n" + common, [
            ("①", src, "編集する元の画。同じ動画の最初のフレーム"),
            ("③", blocks / "curtain_ref.jpg", "右の幕の布")])
        if out.exists():
            cat.pad169(out, d / "lunge_169.png")
        else:
            bad += 1
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
