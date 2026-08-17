# -*- coding: utf-8 -*-
"""異変（非場所依存）＋ 場所（環境依存）→ 生成プロンプト 1 本。

    py -3.11 tools/gen-plate/compose.py --anomaly dolls-many --site A_20260805 --place left-half

やること:
  1. 場所のプレートを **露出だけ**暗くして種にする（`gen-tone.py dim`。理由は同ファイルの頭）
  2. 種を測って「この場所について」の節を**機械が書く**（人が書くのはここではない）
  3. 不変部（契約 → 異変 → なじませ → 出力）と挟んで 1 本にする
  4. 走行フォルダへ種・参照画像・prompt.txt・manifest.json を置き、codex の呼び方を印字する

**プロンプトの中で場所に触れているのは「この場所について」の節だけ。**
当日は `sites/<id>.json` を 1 つ足すだけで、異変の定義は 1 文字も動かさない。
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
import shutil
import sys
import time

import numpy as np
from PIL import Image

import spec


def _load_gen_tone():
    """`tools/gen-tone.py` はハイフン入りなので import 文で読めない。"""
    path = os.path.join(spec.REPO, "tools", "gen-tone.py")
    s = importlib.util.spec_from_file_location("gen_tone", path)
    m = importlib.util.module_from_spec(s)
    s.loader.exec_module(m)
    return m


# --------------------------------------------------------------------- 測る

def measure(img: Image.Image) -> dict:
    """種を測る。**書くのは観測だけ**（狙いの数値をプロンプトへ書かない — 振れる）。"""
    g = np.asarray(img.convert("L"), dtype=np.float64)
    h, w = g.shape
    flat = np.sort(g.ravel())
    p = lambda q: float(flat[int(len(flat) * q) - 1])
    thr = p(0.95)
    ys, xs = np.nonzero(g >= thr)
    return dict(
        mean=float(g.mean()), p5=p(0.05), p50=p(0.50), p95=p(0.95),
        bright_x=float(xs.mean() / w) if len(xs) else 0.5,
        bright_y=float(ys.mean() / h) if len(ys) else 0.5,
    )


FINE_BLOCKS, END_BLOCKS = 800.0, 267.0     # ScreenDecayLogic と対（値を変えたら両方直す）


def transmission(lap: float | None, size, total_laps: float) -> tuple[dict, str]:
    """**その周で伝送がどれだけ痩せるか**を計算して、プロンプトの 1 節にする。

    出る周が分かって初めて言えることが 3 つある（`tools/gen-plate/runs.md` の実測）:
      細かさ  素材の何画素が 1 つに潰れるか（`ScreenComposite` の mip）
      色      3 周目以降は**完全な無彩**（`_Mono` は劣化と同じ進み）
      明暗差  周りとの差をどこまで落とすと粒に埋もれるか

    ⚠ 明暗差の下限は **2 点の実測を直線で結んだ目安**（1 周目 10% / 帰りの A 60%）。
      素材ができたら `limit.py` で実際に確かめる。
    """
    if lap is None:
        return {}, ""
    prog = min(max((lap - 1.0) / max(1.0, total_laps - 1.0), 0.0), 1.0)
    blocks = FINE_BLOCKS + (END_BLOCKS - FINE_BLOCKS) * prog
    contain = min(1.0, (size[0] / size[1]) / (16 / 9))      # 4:3 を 16:9 の枠へ
    src_px = size[0] / max(1.0, blocks * contain)
    floor = 0.10 + 0.50 * prog

    lines = ["## この素材が通る伝送（機械が計算した節）", ""]
    lines.append(f"- この画像は装置の伝送を通ってから体験者に届きます（{int(lap)} 周目の映像）")
    if src_px >= 1.6:
        lines.append(f"- **細かさ**: 届くときには**この画像の {src_px:.1f} 画素が 1 つに潰れます**。"
                     "それより細かい模様は 1 本も残りません")
    if prog >= 0.6:
        lines.append("- **色**: 届くときには**色が全部抜けて白黒になります**。"
                     "⚠ **色で見分けさせないでください** — 形と明暗だけで何なのか分かるようにする")
    lines.append(f"- **明暗差**: 周りとの明暗の差を **{floor * 100:.0f}%** より小さくしないでください。"
                 "それ以下は粒に埋もれて消えます")
    return dict(lap=lap, progress=prog, blocks=blocks, src_px=src_px, contrast_floor=floor), \
        "\n".join(lines)


def bright_say(m: dict) -> str:
    x, y = m["bright_x"], m["bright_y"]
    ud = "上" if y < 0.40 else ("下" if y > 0.60 else "上下の中ほど")
    lr = "左" if x < 0.40 else ("右" if x > 0.60 else "左右の中ほど")
    return f"明るい所は画面の **{ud}・{lr}** に寄っています"


# --------------------------------------------------------------------- 組む

def env_block(seed_path: str, size, m: dict, place: dict, surf_box, surf_say: str,
              refs: list[str], extends_up: bool) -> str:
    w, h = size
    lines = [
        "## この場所について（機械が測って書いた節。異変の定義とは切り離してある）",
        "",
        f"- **入力画像**: {seed_path}",
        f"- **寸法**: {w} x {h}（この寸法のまま出す）",
        f"- **明るさ**（0-255）: 平均 {m['mean']:.0f} / "
        f"暗い方から 5% の所 {m['p5']:.0f} / 明るい方から 5% の所 {m['p95']:.0f}",
        "  ⇒ 暗い部屋です。**暗いまま描いてください。** 明るく起こさない",
        f"  足したものの中でいちばん明るい所も、{m['p95']:.0f} を超えません",
        f"- {bright_say(m)}",
    ]
    if extends_up:
        lines += [
            f"- **足すのはここ**: 「{surf_say}」の {spec.box_say(surf_box, size)} に**足元を置く**",
            "  （背丈のぶんは上へはみ出してよい。左右へは広げない）",
        ]
    else:
        lines.append(f"- **足すのはここ**: 「{surf_say}」の {spec.box_say(surf_box, size)} の中")
    if place.get("keep_out"):
        lines.append(f"- **{place.get('keep_say', '反対側')}には置かないでください**"
                     "（はみ出しも、そこへ伸びる影も無し）")
    # ⚠ ここに「参照は意匠だけの見本です」と足す案を試したが、**背景の保存はむしろ下がった**
    #   （中央値 42.7 対 基準 55 前後・n=2）。参照を外す案も同様に決め手が無かった。
    #   この場所の振れ幅（27〜79）が大きすぎて、プロンプトの手当てでは動かない（runs.md）。
    for r in refs:
        lines.append(f"- **参照画像（意匠の正本）**: {r}")
    return "\n".join(lines)


def build(anomaly: dict, site: dict, place_str: str, scale: float, out_dir: str,
          full_post: bool = False, blur: float = 0.0, no_refs: bool = False,
          lap: float | None = None) -> dict:
    gen_tone = _load_gen_tone()
    plate = Image.open(site["plate_abs"]).convert("RGB")
    size = plate.size

    post = gen_tone.load_post(site.get("cam"))
    seed = gen_tone.dim(plate, post, scale, exposure_only=not full_post)
    if blur > 0:
        # 種の解像感を落として渡す試験（モデルが入力の細かさに追随するか）
        from PIL import ImageFilter
        seed = seed.filter(ImageFilter.GaussianBlur(blur))

    # ⚠ プロンプトへ書くパスは**必ず絶対**（codex は -Cwd の下しか読めない。
    #   相対で書くと「入力画像が無い」まま似た部屋を描き起こされる）
    out_dir = os.path.abspath(out_dir)
    os.makedirs(out_dir, exist_ok=True)
    seed_path = os.path.join(out_dir, "seed.png")
    seed.save(seed_path)

    place = spec.parse_place(place_str, size)
    surf = spec.surface_box(site, anomaly["surface"], size)
    target = spec.intersect(place["box"], surf)
    if target[2] <= target[0] or target[3] <= target[1]:
        raise SystemExit(f"置き場所と面が重なっていない: place={place['box']} surface={surf}")

    # 足元（面）の箱と、画素が変わってよい箱は別。床に立つものは背丈のぶん上へ出る
    extends_up = anomaly["surface"] == "floor" and anomaly["opaque"]
    if extends_up:
        region = spec.intersect(place["box"], [surf[0], 0, surf[2], size[1]])
    else:
        pad = 12
        region = spec.intersect(place["box"],
                                [surf[0] - pad, surf[1] - pad, surf[2] + pad, surf[3] + pad])

    refs = []
    for r in ([] if no_refs else anomaly["refs"]):
        dst = os.path.join(out_dir, os.path.basename(r))
        shutil.copyfile(r, dst)
        refs.append(dst)

    m = measure(seed)

    def read(name):
        with open(os.path.join(spec.PROMPT_DIR, name), encoding="utf-8") as f:
            return f.read().strip()

    fill = lambda t: (t.replace("<<TITLE>>", anomaly["title"])
                      .replace("<<W>>", str(size[0])).replace("<<H>>", str(size[1]))
                      .replace("<<SURFACE_JA>>", spec.SURFACE_JA.get(anomaly["surface"],
                                                                     anomaly["surface"]))
                      .replace("<<SURFACE_RULES>>", spec.surface_rules(anomaly["surface"])))

    total_laps = 3.0
    try:
        with open(os.path.join(spec.REPO, "tools", "web-compositor", "show.json"),
                  encoding="utf-8") as f:
            total_laps = float((json.load(f).get("run") or {}).get("totalLaps", 3))
    except (OSError, ValueError, TypeError):
        pass
    trans, trans_text = transmission(lap, size, total_laps)

    prompt = "\n\n".join(x for x in [
        fill(read("00-contract.md")),
        env_block(seed_path, size, m, place, target, spec.surface_say(site, anomaly["surface"]),
                  refs, extends_up),
        f"## 足すもの — {anomaly['title']}\n\n{anomaly['body']}",
        trans_text,
        fill(read("10-blend.md")),
        fill(read("99-output.md")),
    ] if x) + "\n"

    prompt_path = os.path.join(out_dir, "prompt.txt")
    with open(prompt_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(prompt)

    manifest = dict(
        anomaly=anomaly["id"], site=site["id"], place=place["place"], scale=scale,
        full_post=full_post, blur=blur, no_refs=no_refs,
        cam=site.get("cam"), plate=site["plate_abs"],
        seed=seed_path, prompt=prompt_path, out=os.path.join(out_dir, "out.png"),
        size=list(size), target=target, target_region=region, extends_up=extends_up,
        keep_out=place["keep_out"], surface=anomaly["surface"],
        opaque=anomaly["opaque"], min_area_pct=anomaly["min_area_pct"],
        max_area_pct=anomaly["max_area_pct"], min_parts=anomaly["min_parts"],
        persp=anomaly["persp"], seed_stats=m, transmission=trans,
    )
    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
    return manifest


def main() -> int:
    ap = argparse.ArgumentParser(description="異変 + 場所 → 生成プロンプト")
    ap.add_argument("--anomaly", required=True, help=f"{spec.list_anomalies()}")
    ap.add_argument("--site", required=True, help=f"{spec.list_sites()}")
    ap.add_argument("--place", default="full", help="left-half / right-half / left-40 / full / x0,y0,x1,y1")
    ap.add_argument("--scale", type=float, default=0.5, help="暗くする強さ（post の按分）")
    ap.add_argument("--full-post", action="store_true",
                    help="⚠ 監視カメラらしさごと種へ焼く（既定は露出だけ。理由は gen-tone.py）")
    ap.add_argument("--blur", type=float, default=0.0,
                    help="種をぼかしてから渡す（解像感を揃える試験）")
    ap.add_argument("--no-refs", action="store_true",
                    help="参照画像を渡さない（描き直しとの関係を見る試験）")
    ap.add_argument("--out-dir", default=None)
    ap.add_argument("--lap", type=float, default=None,
                    help="何周目の映像として出すか（伝送でどれだけ痩せるかを節にする）")
    ap.add_argument("--tag", default="", help="走行フォルダ名の後ろに付ける印")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    anomaly = spec.load_anomaly(args.anomaly)
    site = spec.load_site(args.site)
    stamp = time.strftime("%Y%m%d_%H%M%S")
    out_dir = args.out_dir or os.path.join(
        spec.REPO, "logs", "gen-plate",
        f"{stamp}_{anomaly['id']}_{site['id']}" + (f"_{args.tag}" if args.tag else ""))

    man = build(anomaly, site, args.place, args.scale, out_dir, args.full_post,
                args.blur, args.no_refs, args.lap)
    print(f"走行 {out_dir}")
    print(f"  種 平均輝度 {man['seed_stats']['mean']:.1f}  "
          f"足す所 {man['target']}  置かない所 {man['keep_out']}")
    print("")
    print("次にこれを実行する（PowerShell・timeout 600000）:")
    print(f'  & "$env:USERPROFILE\\.claude\\scripts\\codex-run.ps1" '
          f'-PromptFile "{man["prompt"]}" -Mode image '
          f'-Cwd "{out_dir}" -OutDir "{out_dir}" -OutImage "{man["out"]}"')
    print("")
    print(f"  py -3.11 tools/gen-plate/judge.py --run \"{out_dir}\"")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
