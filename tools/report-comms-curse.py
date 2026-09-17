#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""連絡の面の呪い（canon/LEDGER.md 0229）の確認用 HTML を組む（単一ファイル・動画と静止画を base64 で埋め込む）。

    py -3.11 tools/report-comms-curse.py --before <改修前の静止画フォルダ> --before-takeover <改修前の乗っ取り mp4> \
        --sketch <スケッチの jpg> --out reports/2026-09-18_comms-curse.html

見た目は既存の確認用 HTML（reports/2026-09-14_showpiece.html）の style をそのまま使う
（正本は ~/.claude/reference/design-tokens.md。ここで色や余白を決めない）。
"""
from __future__ import annotations

import argparse
import base64
import io
import json
import re
import subprocess
import sys
from pathlib import Path

from PIL import Image, ImageOps

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parent.parent
MOTION = ROOT / "Assets/Screenshots/comms-preview/motion"
STILLS = ROOT / "Assets/Screenshots/comms-preview"
STYLE_SOURCE = ROOT / "reports/2026-09-14_showpiece.html"


def b64(path: Path, mime: str) -> str:
    return f"data:{mime};base64," + base64.b64encode(path.read_bytes()).decode("ascii")


def image_data(path: Path, max_w: int = 1280, quality: int = 82, rotate: int = 0) -> str:
    im = ImageOps.exif_transpose(Image.open(path)).convert("RGB")
    if rotate:
        im = im.rotate(rotate, expand=True)
    if im.width > max_w:
        im = im.resize((max_w, int(im.height * max_w / im.width)), Image.LANCZOS)
    buf = io.BytesIO()
    im.save(buf, "JPEG", quality=quality, optimize=True)
    return "data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode("ascii")


def poster(path: Path) -> str:
    return image_data(path, 960, 70)


def style_block() -> str:
    text = STYLE_SOURCE.read_text(encoding="utf-8")
    m = re.search(r"<style>(.*?)</style>", text, re.S)
    assert m, "style block not found"
    return m.group(1)


def video_tag(path: Path, label: str, poster_path: Path | None, preload: str = "metadata") -> str:
    poster_attr = f' poster="{poster(poster_path)}"' if poster_path and poster_path.exists() else ""
    return (f'<video controls playsinline preload="{preload}" aria-label="{label}"{poster_attr} '
            f'src="{b64(path, "video/mp4")}"></video>')


def figure(path: Path, caption: str, max_w: int = 1280, rotate: int = 0) -> str:
    return (f'<figure><img src="{image_data(path, max_w, rotate=rotate)}" alt="{caption}">'
            f'<figcaption>{caption}</figcaption></figure>')


def git_short() -> str:
    try:
        return subprocess.run(["git", "status", "--short"], cwd=ROOT, capture_output=True,
                              text=True, encoding="utf-8").stdout
    except OSError:
        return ""


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--before", required=True, help="改修前の静止画フォルダ（state_000.png …）")
    ap.add_argument("--before-takeover", required=True)
    ap.add_argument("--takeover", required=True, help="改修後の乗っ取り mp4（ja）")
    ap.add_argument("--takeover-poster", default="")
    ap.add_argument("--sketch", required=True)
    ap.add_argument("--sketch-rotate", type=int, default=90, help="スケッチを読める向きへ回す角度（反時計回り）")
    ap.add_argument("--tests", default="", help="EditMode の結果の 1 行（例: 1985 件通過 / 0 件失敗）")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    evidence = json.loads((MOTION / "evidence.json").read_text(encoding="utf-8"))
    before = Path(a.before)
    levels = [("lv000", "0", "侵食度 0（1 周目〜2 周目 B）"),
              ("lv025", "0.25", "侵食度 0.25（2 周目 C・単発の人形視点のあと）"),
              ("lv075", "0.75", "侵食度 0.75（2 周目 C・連続の人形視点のあと）"),
              ("lv100", "1", "侵食度 1（3 周目 A・人形が立ったあと）")]

    parts = []
    parts.append(f'<main class="wrap"><p class="eyebrow">廻リ視 · 2026.09.18</p><h1>連絡の面の呪い</h1>')
    parts.append('<p class="sub">音付き確認映像。本体の描画を 30fps で記録。打鍵音は Unity が記録した発火時刻。</p>')
    parts.append('<p>壊れを「字が抜ける引き算」から「呪われた双子の画面が斑で重なる足し算」に替えた。'
                 '双子は、地のふちが毛羽立って黒くにじみ、顔が市松人形になり、本文の行の位置に走り書きが乗る。'
                 '面が開くたびに通常の面から始まり、1 秒で重なる。</p>')

    parts.append('<h2>① 依頼のスケッチと読み</h2>')
    parts.append(figure(Path(a.sketch), "手描きのスケッチ（2026-09-17）。通常の画 ＋ 呪われた画を「段階に応じたマスク」で重ねる。"
                                        "「出た初めはこれ ← 1s ほどで重なる」", 1200, a.sketch_rotate))
    parts.append('<p>通常の画と、呪われた画（顔は本編の人形・行は波線・りんかくは不鮮明）を、段階に応じたマスクで重ねる。'
                 '呪われ lv1 は部分的に、Max は全面。出た初めは通常の画で、1 秒ほどで重なる。ここまでがスケッチの指示。</p>')
    parts.append('<p>変えたのは 2 点。斑の面積は侵食度に等号で結ばず、0.25 で 2 割・0.75 で 5 割強・1 で全面にした'
                 '（0.75 は 2 周目 C で立つので、そのまま結ぶと 3 周目の報告の返事が 1 字も読めなくなる）。'
                 '乗っ取りの一文（侵食度 1 の報告）は文字を斑で切らず、既存の引き延ばしから崩壊までの動きを保った。'
                 '地のふちと顔だけが 1 秒で重なる。</p>')

    parts.append('<h2>② 面が開いてから引くまで（侵食度ごと）</h2>')
    parts.append('<p>同じ文面（1 周目の押し方の説明）を、侵食度 0 / 0.25 / 0.75 / 1 で通しで焼いた。'
                 '侵食度 0 は比較のための通常の面。</p>')
    parts.append('<div class="videos">')
    for key, _, title in levels:
        folder = MOTION / key
        parts.append(f'<h3>{title}</h3>')
        parts.append(video_tag(folder / "curse.mp4", title, folder / "f0036.png",
                               "metadata" if key != "lv000" else "none"))
    parts.append('</div>')

    parts.append('<h2>③ 改修前と改修後（打ち終わった面）</h2>')
    parts.append('<div class="grid">')
    for key, num, title in levels:
        code = key[2:]
        b = before / f"state_{code}.png"
        n = STILLS / f"state_{code}.png"
        if b.exists():
            parts.append(figure(b, f"改修前 · {title}", 900))
        if n.exists():
            parts.append(figure(n, f"改修後 · {title}", 900))
    parts.append('</div>')

    parts.append('<h2>④ 乗っ取りの一文（侵食度 1 の報告）</h2>')
    parts.append('<p>スイの顔で「異常なしと判定しました」を打ち始め、1 秒で地のふちと顔に人形が重なる。'
                 '文字は斑で切らず、既存のとおり縦に引かれ、短く止まり、完成前に消える。</p>')
    tp = Path(a.takeover_poster) if a.takeover_poster else None
    parts.append(video_tag(Path(a.takeover), "乗っ取りの一文（改修後・日本語）", tp))
    parts.append('<details><summary>改修前と比較する</summary>')
    parts.append(video_tag(Path(a.before_takeover), "乗っ取りの一文（改修前・日本語）", None, "none"))
    parts.append('</details>')

    parts.append('<h2>確認できたこと</h2>')
    rows = []
    if a.tests:
        rows.append(("EditMode テスト", a.tests))
    for key, num, title in levels:
        e = evidence[key]
        rows.append((f"{title} · 開いた直後（0.1 秒）",
                     f"通常の面との画素差 {e['pixel_delta_open_vs_lv000']:.3f}/255（0 が「出た初めは通常」）"))
        if float(num) > 0:
            rows.append((f"{title} · 1.2 秒",
                         f"斑 {e['curse_1_2s']:.2f}（目標 {e['target']:.2f}・届いたのは {e['ramp_frame'] / 30:.2f} 秒）。"
                         f"地の画素差 {e['pixel_delta_1_2s_plate']:.2f}/255・顔 {e['pixel_delta_1_2s_face']:.2f}/255・"
                         f"本文 {e['pixel_delta_1_2s_text']:.2f}/255。矩形の外の毛羽立ち {e['ring_dark_pixels_1_2s']} 画素"))
            rows.append((f"{title} · 打ち終わり",
                         f"斑の中で切られた字 {e['cx_typed_end']} 字（斑 {e['curse_typed_end']:.2f}）"))
    parts.append('<div class="tblwrap"><table><thead><tr><th>項目</th><th>結果</th></tr></thead><tbody>')
    for k, v in rows:
        parts.append(f'<tr><td>{k}</td><td>{v}</td></tr>')
    parts.append('</tbody></table></div>')
    parts.append('<p>Quest では未確認。装着時に斑の境目と走り書きが読めるか、ステンシルの切断が実機で効くかは走行で見る。'
                 '被って分かるもの（怖さの強度・1 秒の速さの感じ方）は溜めて 1 回にまとめる。</p>')

    parts.append('<h2>決めたこと・見送ったこと</h2>')
    parts.append('<ul>'
                 '<li>斑の面積: 0.25 で 22%・0.75 で 55%・1 で全面。侵食度に等号で結ばない。</li>'
                 '<li>立ち上がり: 面が開くたびに 0 から 1.0 秒。同じ面のまま次の文面へ繋ぐときは重なったまま。</li>'
                 '<li>乗っ取りの一文: 文字を切らない。走り書きも描かない。地と顔だけ重なる。</li>'
                 '<li>「止まってください！」以降と、呪いが解けた後: 斑 0（スイ・きれいな字・鋭い矩形）。</li>'
                 '<li>見送り: 走り書きの赤い下線（赤は嘘にだけ）／地の色相ずらし（1.2° では色として読めない）。</li>'
                 '</ul>')
    parts.append('<p>判定してほしいのは 1 点。毎回 0 から 1 秒で重なる形でよいか。'
                 '別案は「侵食度が上がった直後の 1 回だけ 0 から重なり、以後は開いた瞬間から重なっている」。'
                 '返事が無ければいまの形（毎回 0 から）で進める。</p>')

    parts.append('<details><summary>Git の記録</summary><pre>git status --short\n'
                 + git_short().replace("&", "&amp;").replace("<", "&lt;") + '</pre></details>')
    parts.append('</main>')

    html = ('<!doctype html><html lang="ja"><meta charset="utf-8">'
            '<meta name="viewport" content="width=device-width,initial-scale=1">'
            '<title>廻リ視 — 連絡の面の呪い</title><style>' + style_block() + '</style>\n' + "\n".join(parts))
    out = Path(a.out)
    out.write_text(html, encoding="utf-8")
    print(f"{out} ({out.stat().st_size / 1e6:.1f} MB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
