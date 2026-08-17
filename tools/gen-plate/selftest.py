# -*- coding: utf-8 -*-
"""判定器そのものを試す。**線を引く前にこれを通す。**

種から 3 つの偽物を作って、判定器が期待どおり落とす／通すかを見る。

    貼り付け  つるつるの塊を硬い縁で貼る（影なし）      → 粒・縁・接地で落ちるべき
    馴染ませ  同じ塊に粒・ぼけた縁・接地の影を足す      → 通るべき
    素通し    何も足さない                              → 「足されている」で落ちるべき

「CG っぽい」を画素で捕まえられているかは、生成物を待たずにここで分かる。
生成の当たり外れ（1 回 2〜4 分）を計器の検証に使わない。

    py -3.11 tools/gen-plate/selftest.py --run logs/gen-plate/<走行>
"""
from __future__ import annotations

import argparse
import json
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

import judge
import metrics

RNG = np.random.default_rng(20260817)


def check_fit() -> int:
    """**枠への収め方が、実機の正本と一致しているか。**

    同じ量の実装が 3 か所にある — `MjpegScreen.ContainScale`（C#・シェーダへ渡す正本）、
    `compose.py` の伝送計算、`screen.py` の届いた画。2026-08-18 まで `screen.py` だけが
    **cover（枠を埋めて上下を切る）**を返していて、届いた画を 1.33 倍に拡大したうえで
    プレートの上下 12.5% ずつを捨てていた。

    ⚠ **恒等の入力ではこれは出ない**（素材にも実写にも同じ変形が掛かるので差が 0 のまま）。
    掃引でも出ない（単調に動く）。**別の実装と突き合わせる以外に出す道が無かった。**
    """
    import screen
    bad = 0
    for w, h in [(640, 480), (1280, 720), (1440, 1080), (2560, 1080), (480, 640)]:
        sa, fa = w / h, screen.FRAME_W / screen.FRAME_H
        want = (sa / fa, 1.0) if sa < fa else (1.0, fa / sa)   # ← MjpegScreen.cs 258 行と同じ式
        got = screen.contain_scale((w, h), fa)
        if abs(got[0] - want[0]) > 1e-9 or abs(got[1] - want[1]) > 1e-9:
            print(f"NG 枠への収め方 {w}x{h}: {got[0]:.3f},{got[1]:.3f} "
                  f"← 正本は {want[0]:.3f},{want[1]:.3f}（MjpegScreen.ContainScale）")
            bad += 1
    if not bad:
        print("OK 枠への収め方   C# の正本と一致（縮む側が 1 未満 ＝ 黒帯が出る）")
    return bad


def _blob(shape, cx, cy, rx, ry) -> np.ndarray:
    y, x = np.ogrid[:shape[0], :shape[1]]
    return (((x - cx) / rx) ** 2 + ((y - cy) / ry) ** 2) <= 1.0



def fakes(seed: Image.Image, man: dict) -> dict[str, Image.Image]:
    g = np.asarray(seed.convert("RGB"), dtype=np.float64)
    h, w = g.shape[:2]
    x0, y0, x1, y1 = man["target"]

    # 床の帯に「人形くらいの塊」を 8 個並べる（足元が下にあるものほど大きい）
    solid = np.zeros((h, w), dtype=bool)
    for i in range(8):
        cx = int(x0 + (i + 0.5) * (x1 - x0) / 8)
        foot = int(y1 - 6 - (i % 4) * 26)
        s = 0.7 + 0.9 * (foot - y0) / max(1, y1 - y0)
        solid |= _blob((h, w), cx, foot - int(40 * s), int(16 * s), int(42 * s))

    # 塊の色は、その場所の床より 50 明るい平らな色（種と確実に見分けが付く量）。
    # ⚠ **この偽物は場所によって強さが変わる。** 床の明るさの分布に当たる値になると、
    #   そこでは輪郭が生まれず「貼り付け」として弱くなる（実測で外周の 35% しか段差にならない
    #   場所がある）。⚠⚠ 暗い側へずらす案を試したが、**塊が暗くなるぶん粒の比が下がって
    #   別の門（粒が乗っている）が誤って落ちた**ので取り下げた。
    #   ⇒ **偽物を強くするのではなく、線を弱い偽物でも越えない位置に置く**（`judge.py` の 1.00）。
    tone = float(np.median(np.asarray(seed.convert("L"), dtype=np.float64)[y0:y1, x0:x1])) + 50.0
    body_flat = np.full_like(g, tone)

    # 貼り付け: 平らに塗る（粒なし・縁は 1 画素で切れる・影なし）
    flat = np.where(solid[..., None], body_flat, g)

    # 馴染ませ: 粒を乗せ、縁をぼかし、足元へ影を落とす
    a = np.asarray(Image.fromarray((solid * 255).astype(np.uint8))
                   .filter(ImageFilter.GaussianBlur(1.4)), dtype=np.float64)[..., None] / 255.0
    body = body_flat + RNG.normal(0.0, 6.5, size=g.shape)          # 粒
    shadow = np.zeros((h, w), dtype=np.float64)
    ys, xs = np.nonzero(solid)
    for x, y in zip(xs[::5], ys[::5]):
        shadow[min(h - 1, y + 2):min(h, y + 10), max(0, x - 4):min(w, x + 5)] = 1.0
    shadow[solid] = 0.0
    shadow = np.asarray(Image.fromarray((shadow * 255).astype(np.uint8))
                        .filter(ImageFilter.GaussianBlur(3.0)), dtype=np.float64) / 255.0
    blend = g * (1.0 - 0.40 * shadow)[..., None]
    blend = blend * (1 - a) + body * a

    to_im = lambda arr: Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8))
    return {"貼り付け": to_im(flat), "馴染ませ": to_im(blend), "素通し": seed.copy()}


EXPECT = {
    # 貼り付けは「暗部が沈む」でも落ちる（床より明るい平らな色なので、それも正しい捕まえ方）
    "貼り付け": dict(fail={"粒が乗っている", "縁が刃物でない", "接地の影がある", "暗部が沈む"}),
    "馴染ませ": dict(fail={"暗部が沈む"}),
    "素通し": dict(fail={"足されている", "塊の数"}),
}


def main() -> int:
    ap = argparse.ArgumentParser(description="判定器の自己検査")
    ap.add_argument("--run", required=True)
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    with open(os.path.join(args.run, "manifest.json"), encoding="utf-8") as f:
        man = json.load(f)
    seed = Image.open(man["seed"]).convert("RGB")

    bad = check_fit()
    for name, im in fakes(seed, man).items():
        m = metrics.measure(seed, im, man)
        checks = judge.build_checks(m, man, True)
        ng = {c[0] for c in checks if not c[1]}
        want = EXPECT[name]["fail"]
        missed, extra = want - ng, ng - want
        mark = "OK " if not missed and not extra else "NG "
        if missed or extra:
            bad += 1
        print(f"{mark}{name:6s} 落ちた: {sorted(ng) or '（無し）'}")
        print(f"        粒 {m['grain']:.2f} / 縁 {m['edge']:.2f} / 接地 {m['ground']:.2f} / "
              f"面積 {m['added_pct']:.1f}% / 塊 {m['parts']}")
        if missed:
            print(f"        ⚠ 落とせなかった: {sorted(missed)}")
        if extra:
            print(f"        ⚠ 余計に落とした: {sorted(extra)}")
    print("→", "計器は効いている" if not bad else f"計器が {bad} 件で期待と違う")
    return 0 if not bad else 1


if __name__ == "__main__":
    raise SystemExit(main())
