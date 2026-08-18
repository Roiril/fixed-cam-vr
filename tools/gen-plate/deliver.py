# -*- coding: utf-8 -*-
"""**届いた画の合否**を 1 コマンドで出す（走行フォルダ → 体験者が見る画）。

    py -3.11 tools/gen-plate/deliver.py --run logs/gen-plate/<走行> --lap 4

`judge.py` は**生成直後**の作りを見る。ここは**体験者が見る所**を見る。見るのは 2 つだけ:

  継ぎ目の段差    隣に実写が並ぶ所に線が出ていないか（≤ 4・素材を置かない画との差）
  読みやすさ      足したものが粒に埋もれていないか（≥ 5・Rose の基準）

⚠⚠ **この 2 つは逆を向く。** 人形を明るくすれば読みやすくなるが、
左半分の平均が上がって継ぎ目の段差も増える（実測: 読みやすさ 9.2 → 20.5 のとき 継ぎ目 1.6 → 4.2）。
**別々のコマンドで測っていると、片方だけ良くして満足できてしまう。** だから 1 枚にまとめる。
"""
from __future__ import annotations

import argparse
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

import legible
import metrics
import screen
import spec

SEAM_MAX = 4.0   # 恒等の入力で 0 になる形へ直したので引き直した（旧 8.0 は下駄込み）
READ_MIN = 5.0


def _font(size: int):
    for p in ("C:/Windows/Fonts/meiryo.ttc", "C:/Windows/Fonts/msgothic.ttc"):
        try:
            return ImageFont.truetype(p, size)
        except OSError:
            continue
    return ImageFont.load_default()


def material_from_run(run_dir: str, man: dict) -> str:
    """走行の生成物を素材（明るさを戻した状態）へ直す。無ければ作る。"""
    dst = os.path.join(run_dir, "material.png")
    src = os.path.join(run_dir, "out_fit.png")
    if not os.path.exists(src):
        src = man["out"]
    if os.path.exists(dst) and os.path.getmtime(dst) >= os.path.getmtime(src):
        return dst
    import importlib.util
    s = importlib.util.spec_from_file_location(
        "gen_tone", os.path.join(spec.REPO, "tools", "gen-tone.py"))
    gt = importlib.util.module_from_spec(s)
    s.loader.exec_module(gt)
    post = gt.load_post(man.get("cam"))
    im = Image.open(src).convert("RGB")
    if list(im.size) != man["size"]:
        im = im.resize(tuple(man["size"]), Image.LANCZOS)
    gt.undim(im, post, float(man.get("scale", 0.5)),
             exposure_only=not man.get("full_post", False)).save(dst)
    return dst


def is_split(m: np.ndarray) -> bool:
    """マスクが**縦の分割**か（左半分・右半分の類）。そうでなければシルエット。

    列ごとの平均が 0 と 1 の間をほぼ 1 度だけ跨ぐなら分割。人形の頭のようなシルエットは
    どの列も中途半端な値になるので、この判定で分かれる。
    """
    col = m.mean(axis=0)
    if col.max() - col.min() < 0.2:
        return False
    return float(((col > 0.05) & (col < 0.95)).mean()) < 0.25


def outline_step(got: np.ndarray, plain: np.ndarray, m: np.ndarray, band: int = 3) -> float:
    """**シルエットの縁**に段差が出ていないか（素材の外周と、そのすぐ外のライブとの差）。

    ⚠⚠ 縦の分割用の物差しは**シルエットには使えない**。列を 1 本選んで両側を比べる形なので、
    頭のような形では「たまたまその列の上下にある構造」を測ることになる（2026-08-18 に、
    ジャンプスケアが 5.6 と出て気づいた）。**マスクの縁に沿って測る。**

    素材を置かない画（`plain`）で同じ量を引くのは分割の側と同じ理屈 —
    縁の場所にもともとある構造が下駄になる。
    """
    inside = m > 0.5
    grow = metrics._shift_or(metrics._shift_or(inside))
    shrink = metrics._shift_and(metrics._shift_and(inside))
    for _ in range(band - 2):
        grow = metrics._shift_or(grow)
        shrink = metrics._shift_and(shrink)
    outer = grow & ~inside            # 縁のすぐ外（ライブが出ている所）
    inner = inside & ~shrink          # 縁のすぐ内（素材が出ている所）
    if outer.sum() < 40 or inner.sum() < 40:
        return float("nan")
    gap = lambda g: abs(float(g[inner].mean()) - float(g[outer].mean()))
    return gap(got) - gap(plain)


def run(material: str, plate: str, mask: str, lap: float, out_path: str) -> int:
    with open(os.path.join(spec.REPO, "tools", "web-compositor", "show.json"),
              encoding="utf-8") as f:
        show = json.load(f)

    got, plain, inside, sigma = legible._delivered(material, plate, mask, lap, show)
    h, w = got.shape
    m = np.asarray(Image.open(mask).convert("L").resize((w, h), Image.BILINEAR),
                   dtype=np.float64) / 255.0
    added = metrics.clean(np.abs(got - plain) > max(3.0 * sigma, 4.0)) & (m > 0.5) & inside

    rows = []
    for c in metrics.components(added, min_area=120)[:24]:
        sub = np.zeros_like(added)
        sub[c["y0"]:c["y1"], c["x0"]:c["x1"]] = added[c["y0"]:c["y1"], c["x0"]:c["x1"]]
        if sub.sum() < 60:
            continue
        rows.append((legible.readability(got, plain, sub, sigma),
                     (c["x0"], c["y0"], c["x1"], c["y1"])))
    read = float(np.median([r for r, _ in rows])) if rows else float("nan")

    # 継ぎ目（マスクが 0.5 を跨ぐ列）
    # ⚠⚠ **素材を置かない画（`plain`）で測った同じ量を引く。**
    #   引かないと「その列にもともとある縦の構造」が下駄になる。恒等の入力
    #   （素材＝プレートそのもの）で **5.1** が出て気づいた（2026-08-17）。
    #   下駄はプレートごとに違うので、生の値では場所をまたいで比べられない。
    col = m.mean(axis=0)
    seam_x, seam, seam_kind = None, float("nan"), "縦の分割"
    if is_split(m):
        seam_x = int(np.argmin(np.abs(col - 0.5)))
        cross = lambda g: float(np.abs(g[:, seam_x - 1] - g[:, seam_x + 1]).mean())
        seam = cross(got) - cross(plain)
    elif (m > 0.5).any():
        seam, seam_kind = outline_step(got, plain, m), "輪郭"

    ok_seam = seam != seam or seam <= SEAM_MAX
    ok_read = read == read and read >= READ_MIN
    bad = sum(1 for r, _ in rows if r < READ_MIN)

    print(f"== 届いた画（周 {lap:.0f}）  粒 {sigma:.2f}")
    print(f"  {'OK ' if ok_seam else 'NG '}継ぎ目の段差   "
          f"{'—' if seam != seam else f'{seam:.1f}'}（{SEAM_MAX:.0f} 以下・{seam_kind}）")
    print(f"  {'OK ' if ok_read else 'NG '}読みやすさ     "
          f"{'—' if read != read else f'{read:.1f}'}（{READ_MIN:.0f} 以上・中央値。"
          f"塊 {len(rows)} 個中 {bad} 個が線を下回る）")
    print(f"  → {'合格' if ok_seam and ok_read else '不合格'}")
    print("  ⚠ この 2 つは逆を向く。片方を上げたらもう片方を必ず見直す")
    print("  ⚠ 線を下回る弱い塊は、**背景を触った跡**のことがある"
          "（それは judge.py の『背景を塗り直していない』が見る）")

    im = Image.fromarray(got.astype(np.uint8)).convert("RGB")
    d = ImageDraw.Draw(im)
    f, fs = _font(15), _font(13)
    for r, box in rows:
        c = (120, 220, 160) if r >= READ_MIN else (240, 110, 100)
        d.rectangle(box, outline=c, width=2)
        d.text((box[0] + 3, box[1] + 2), f"{r:.0f}", fill=c, font=fs)
    if seam_x:
        d.line([(seam_x, 0), (seam_x, h)], fill=(255, 220, 80), width=1)
        d.text((seam_x + 5, 6), f"継ぎ目 {seam:.1f}",
               fill=(255, 220, 80) if ok_seam else (240, 110, 100), font=f)
    d.rectangle([0, h - 26, w, h], fill=(14, 14, 16))
    d.text((6, h - 22),
           f"継ぎ目 {seam:.1f}（≤{SEAM_MAX:.0f}） / 読みやすさ {read:.1f}（≥{READ_MIN:.0f}）"
           f"   {'合格' if ok_seam and ok_read else '不合格'}",
           fill=(150, 230, 170) if (ok_seam and ok_read) else (240, 140, 120), font=f)
    os.makedirs(os.path.dirname(out_path) or ".", exist_ok=True)
    im.save(out_path)
    print(f"  絵にした: {out_path}  ← **これを開くまでが 1 周**")
    return 0 if (ok_seam and ok_read) else 1


def main() -> int:
    ap = argparse.ArgumentParser(description="届いた画の合否")
    ap.add_argument("--run", help="走行フォルダ（素材は out から自動で戻す）")
    ap.add_argument("--material", help="--run を使わないとき")
    ap.add_argument("--plate", help="ライブ側に出るプレート（既定 = その場所のプレート）")
    ap.add_argument("--mask", default=None)
    ap.add_argument("--lap", type=float, default=None)
    ap.add_argument("--out", default=None)
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    web = os.path.join(spec.REPO, "tools", "web-compositor")
    mask = args.mask or os.path.join(web, "masks", "split_left_half.png")
    material, plate, lap, out = args.material, args.plate, args.lap, args.out

    if args.run:
        with open(os.path.join(args.run, "manifest.json"), encoding="utf-8") as f:
            man = json.load(f)
        material = material or material_from_run(args.run, man)
        plate = plate or man["plate"]
        if lap is None:
            lap = float((man.get("transmission") or {}).get("lap") or 4.0)
        out = out or os.path.join(args.run, "delivered.png")
    if not material or not plate:
        raise SystemExit("--run か、--material と --plate が要る")
    return run(material, plate, mask, lap if lap is not None else 4.0,
               out or "logs/gen-plate/delivered.png")


if __name__ == "__main__":
    raise SystemExit(main())
