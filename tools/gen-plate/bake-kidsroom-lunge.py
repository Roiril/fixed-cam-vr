"""焼けた「隙間から眼が覗く画」を参照①にして、動画の最後のフレームだけを焼き直す（anomaly/kidsroom-cat.md）。

    py -3.11 tools/gen-plate/bake-kidsroom-lunge.py [--variant last|last_close|last_fallen|last_doll|last_floor|last_body] [--nook] [--lock-room] [--doll] <眼が覗く画.png> ...

--variant は最後のフレームの版（節の名前）。last は飛びかかる黒い塊と大きな眼 2 つ（0361）。
last_close は近すぎて見えない・傾いた画に眼が 1 つ、last_fallen は横倒しのカメラを覗き込む眼が 1 つ（どちらも 0368）。last_doll は上から逆さまに落ちてきた人形の顔（0370）。last_floor は床に落ちて割れた人形の顔（0371）。last_body は身体ごと落ちて顔だけこちらを向く人形（0372。--doll と組む）。
--doll はこのプロジェクトの人形の写真（tools/doll-ref/out/plate_front.jpg）を参照②に足す。
--lock-room は部屋を参照①とピクセル単位で同じにし、生成で変わった所だけ残す（動画の途中で部屋がゆがむのを防ぐ。0371）。
--nook は人形と木馬の部屋の説明を足す（0364）。出力は同じフォルダの <variant>.png と <variant>_169.png。1 本ずつ直列。
"""
import argparse
import importlib.util
import sys
sys.stdout.reconfigure(encoding="utf-8")
from pathlib import Path

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("cat", HERE / "bake-kidsroom-cat.py")
cat = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cat)


DOLL_REF = cat.REPO / "tools" / "doll-ref" / "out" / "plate_front.jpg"   # 研究室の市松人形（doll_reference_kit）


def lock_room(src, out):
    """部屋を参照①とピクセル単位で同じにし、生成で大きく変わった所（足した物と消した眼）だけ残す（0371）。

    生成の編集は部屋をわずかに描き直す。最初と最後のフレームで部屋がずれると、動画の生成は途中で部屋をゆがめて繋ぐ。
    生成のままの画は <name>_raw.png、残した所の白黒は <name>_mask.png に置く。
    """
    from PIL import Image, ImageChops, ImageFilter
    a = Image.open(src).convert("RGB")
    b = Image.open(out).convert("RGB")
    assert a.size == b.size, (a.size, b.size)
    b.save(out.with_name(out.stem + "_raw.png"))
    # 足した物: 暗い髪まで拾うため低いしきい値。9px の開きで、部屋の描き直しの細かいずれを落とす
    added = ImageChops.difference(a, b).convert("L").point(lambda v: 255 if v > 10 else 0)
    added = added.filter(ImageFilter.MinFilter(9)).filter(ImageFilter.MaxFilter(9))
    # 消した物: 隙間の眼のように小さく明るい物は開きで落ちるので、元の方が明るい所として別に拾う
    gone = ImageChops.subtract(a.convert("L"), b.convert("L")).point(lambda v: 255 if v > 40 else 0)
    # 足した物は広く膨らませる。暗い床に広がった髪の先は差が小さく、狭いと切れる（0372 の人形で切れた）
    mask = ImageChops.lighter(added.filter(ImageFilter.MaxFilter(55)), gone.filter(ImageFilter.MaxFilter(25)))
    mask = mask.filter(ImageFilter.GaussianBlur(10))
    mask.save(out.with_name(out.stem + "_mask.png"))
    Image.composite(b, a, mask).save(out)
    kept = sum(mask.point(lambda v: 1 if v > 127 else 0).histogram()[1:]) / (a.size[0] * a.size[1])
    print(f"lock-room: 生成から残した面積 {kept:.1%}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("srcs", nargs="+")
    ap.add_argument("--variant", default="last")
    ap.add_argument("--nook", action="store_true")
    ap.add_argument("--lock-room", action="store_true", help="部屋を参照①と同じにし、変わった所だけ残す")
    ap.add_argument("--doll", action="store_true", help="このプロジェクトの人形の写真を参照②に足す（0372）")
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
        refs = [("①", src, "編集する元の画。同じ動画の最初のフレーム")]
        if a.doll:
            refs.append(("②", DOLL_REF, "このプロジェクトの人形（実物の写真）。意匠だけを使い、色と明るさは①の部屋に合わせる"))
        refs.append(("③", blocks / "curtain_ref.jpg", "右の幕の布"))
        out = cat.bake(d, name, body + "\n\n## 参考（部屋の説明。参照画像①に既に写っている）\n\n" + common, refs)
        if out.exists():
            if a.lock_room:
                lock_room(src, out)
            cat.pad169(out, d / f"{name}_169.png")
        else:
            bad += 1
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
