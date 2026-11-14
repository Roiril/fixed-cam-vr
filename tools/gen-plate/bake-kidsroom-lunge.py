"""焼けた「隙間から眼が覗く画」を参照①にして、動画の最後のフレームだけを焼き直す（anomaly/kidsroom-cat.md）。

    py -3.11 tools/gen-plate/bake-kidsroom-lunge.py [--variant last|last_close|last_fallen|last_doll] [--nook] <眼が覗く画.png> ...

--variant は最後のフレームの版（節の名前）。last は飛びかかる黒い塊と大きな眼 2 つ（0361）。
last_close は近すぎて見えない・傾いた画に眼が 1 つ、last_fallen は横倒しのカメラを覗き込む眼が 1 つ（どちらも 0368）。last_doll は上から逆さまに落ちてきた人形の顔（0370）。
--nook は人形と木馬の部屋の説明を足す（0364）。出力は同じフォルダの <variant>.png と <variant>_169.png。1 本ずつ直列。
"""
import argparse
import importlib.util
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("cat", HERE / "bake-kidsroom-cat.py")
cat = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cat)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("srcs", nargs="+")
    ap.add_argument("--variant", default="last")
    ap.add_argument("--nook", action="store_true")
    a = ap.parse_args()
    blocks = cat.REPO / "Logs" / "gen-plate" / "kr_block"
    common, _, _ = cat.sections(a.nook, a.nook)
    body = cat.section(a.variant)
    name = "lunge" if a.variant == "last" else a.variant
    bad = 0
    for src in (Path(p).resolve() for p in a.srcs):
        if not src.exists():
            sys.exit(f"無い: {src}")
        d = src.parent
        out = cat.bake(d, name, body + "\n\n## 参考（部屋の説明。参照画像①に既に写っている）\n\n" + common, [
            ("①", src, "編集する元の画。同じ動画の最初のフレーム"),
            ("③", blocks / "curtain_ref.jpg", "右の幕の布")])
        if out.exists():
            cat.pad169(out, d / f"{name}_169.png")
        else:
            bad += 1
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
