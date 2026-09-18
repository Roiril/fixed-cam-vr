#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""連絡の面の呪い（canon/LEDGER.md 0229 / 0230 / 0231）の確認用 HTML を組む（単一ファイル・動画と静止画を base64 で埋め込む）。

    py -3.11 tools/report-comms-curse.py --before-motion <改修前の motion フォルダ> --before-lie <改修前の嘘の一文 mp4> \
        --sketch <スケッチの jpg> --tests "<EditMode の 1 行>" --out reports/2026-09-18_comms-possession.html

改修後の動画は Assets/Screenshots/comms-preview/motion/（4 段）と Logs/comms-takeover-20260914/latest-render.txt が
指す render-<日時>/（嘘の一文・3 言語）から読む。見た目は既存の確認用 HTML（reports/2026-09-14_showpiece.html）の
style をそのまま使う（正本は ~/.claude/reference/design-tokens.md。ここで色や余白を決めない）。
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
LIE_LOGS = ROOT / "Logs/comms-takeover-20260914"
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
    assert path.is_file(), f"動画が無い: {path}"
    poster_attr = f' poster="{poster(poster_path)}"' if poster_path and poster_path.exists() else ""
    return (f'<video controls playsinline preload="{preload}" aria-label="{label}"{poster_attr} '
            f'src="{b64(path, "video/mp4")}"></video>')


def figure(path: Path, caption: str, max_w: int = 1280, rotate: int = 0) -> str:
    assert path.is_file(), f"画が無い: {path}"
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
    ap.add_argument("--before-motion", required=True, help="前の版の motion フォルダ（lv075/curse.mp4 …）")
    ap.add_argument("--before-lie", required=True, help="前の版の嘘の一文 mp4（ja）")
    ap.add_argument("--sketch", required=True)
    ap.add_argument("--sketch-rotate", type=int, default=90, help="スケッチを読める向きへ回す角度（反時計回り）")
    ap.add_argument("--tests", default="", help="EditMode の結果の 1 行（例: 1985 件通過 / 0 件失敗）")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    evidence = json.loads((MOTION / "evidence.json").read_text(encoding="utf-8"))
    lie_dir = Path((LIE_LOGS / "latest-render.txt").read_text(encoding="utf-8").strip())
    lie = {e["language"]: e for e in json.loads((lie_dir / "evidence.json").read_text(encoding="utf-8"))}
    before_motion = Path(a.before_motion)
    levels = [("lv000", "0", "侵食度 0（1 周目〜2 周目 B）"),
              ("lv025", "0.25", "侵食度 0.25（2 周目 C・単発の人形視点のあと）"),
              ("lv075", "0.75", "侵食度 0.75（2 周目 C・連続の人形視点のあと）"),
              ("lv100", "1", "侵食度 1（3 周目 A・人形が立ったあと）")]

    parts = []
    parts.append('<main class="wrap"><p class="eyebrow">廻リ視 · 2026.09.18</p><h1>連絡の面の憑依 — 塗り替えは本編をまねた乱れで</h1>')
    parts.append('<p class="sub">音付き確認映像。本体の描画を 30fps で記録。打鍵と乱れの音は Unity が記録した発火時刻。</p>')
    parts.append('<p>侵食度 0.75 以降の連絡は、全文が一気に出て、読ませてから、上から前線が降りて呪われた双子に塗り替わる。'
                 '塗り替わった先は前回の双子（人形の顔・走り書き・毛羽立った地）で、塗り替わった後は全面。'
                 '0.25 は前回のまま（打ちながら 1 秒で斑が重なる）。'
                 '塗り替わっているあいだは、面全体が本編（カメラ映像）の乱れをまねた乱れで乱れる — 4cm の帯ごとに中身が横へ切れて飛び、'
                 '帯が抜け落ち、全体が明滅する。抜けてから 0.12 秒の尾を引いて呪われた面へ落ち着く。</p>')

    parts.append('<h2>① 依頼と読み</h2>')
    parts.append('<blockquote><p>私が思っているのは、これだと乗っ取られている感じが、一部のユーザにわかりにくいのではないかということ。'
                 '初見の人視点で考えてみて。私の案としては、侵食度0.75以降ではスイの文章を、一文字ずつではなくぱっと一気に出してしまい、'
                 'スイの文章を全部見せた後に上からデジタルが侵食されるようなアニメーションで急に塗り替わるという感じ。検討して実装してみて</p></blockquote>')
    parts.append('<p>0230 の動画を見た後の返事:</p>')
    parts.append('<blockquote><p>呪いで塗り替えるのは、ただ上から変わっていくのではなく、メインスクリーンをまねした、'
                 'エージェントスクリーン用の乱れ演出を作成してそれを使うようにしてほしい。</p></blockquote>')
    parts.append('<p>案のとおりに組んだ。初見の人に「乗っ取られた」が読めるには、正常な状態が先に丸ごと見えて、それが目の前で塗り替わる順序が要る。'
                 '前回の形は打ち始めから斑が重なるので、初見には最初から壊れている装置にしか見えない。</p>')
    parts.append('<p>決めたのは 5 つ。読ませる時間は 0.9 秒に打つ尺の 0.45 倍を足す（1.0〜2.4 秒）。塗り替わりは 0.45 秒で、'
                 '前線は 4cm（本文 1 行）の帯ごとに一斉に反転する。降りているあいだは面全体が本編の乱れをまねた乱れで乱れる'
                 '（帯ごとの横飛び ±6cm・帯の脱落・全体の明滅。強さの頭打ちは本編と同じ 0.6）。'
                 '塗り替わった後は 0.75 でも 1 でも全面。3 周目 A の嘘「異常なしと判定しました」も同じ形にして、'
                 '前回までの引き延ばしから崩壊までの動きは捨てた。</p>')
    parts.append('<p>本編から採ったのは、帯ごとに立つ確率と組み替わる速さ（18Hz / 42Hz）と頭打ち。連絡の面用に変えたのは 3 つ。'
                 '帯を本文 1 行の高さにして字が帯で割れないようにした。脱落は灰の砂を混ぜず消す（黒い地と象牙の字で砂を混ぜると明るい矩形が出る）。'
                 '面の枠は動かさず、飛んで矩形の外へ出た字は切る（背景がパススルーなので、切らないと字が宙に浮く）。'
                 '本文の飛びは行ごとに引く。頂点ごとに引くと帯の境目が行を横切った字が斜体になるのを、最初の焼きで見た。</p>')
    parts.append(figure(Path(a.sketch), "前回のスケッチ（2026-09-17）。塗り替わった先はこの「呪われ Max」の画。", 1000, a.sketch_rotate))

    parts.append('<h2>② 面が開いてから引くまで（侵食度ごと）</h2>')
    parts.append('<p>同じ文面（1 周目の押し方の説明）を、侵食度 0 / 0.25 / 0.75 / 1 で通しで焼いた。'
                 '侵食度 0 は比較のための通常の面。0.75 と 1 が今回の形。</p>')
    parts.append('<div class="videos">')
    for key, _, title in levels:
        folder = MOTION / key
        e = evidence[key]
        poster_frame = folder / f"f{e['sweep_end_frame'] + 2:04d}.png" if e.get("possessed") else folder / "f0036.png"
        parts.append(f'<h3>{title}</h3>')
        parts.append(video_tag(folder / "curse.mp4", title, poster_frame,
                               "metadata" if key != "lv000" else "none"))
        if e.get("possessed"):
            b = before_motion / key / "curse.mp4"
            if b.is_file():
                parts.append('<details><summary>前回（斑が 1 秒で重なる形）と比較する</summary>')
                parts.append(video_tag(b, f"前回 · {title}", None, "none"))
                parts.append('</details>')
    parts.append('</div>')

    parts.append('<h2>③ 塗り替わりの途中（侵食度 1・①の文面）</h2>')
    e100 = evidence["lv100"]
    parts.append('<div class="grid">')
    parts.append(figure(MOTION / "lv100" / f"f{e100['shown_frame']:04d}.png", "一気に出た直後。通常の面そのもの。", 900))
    mid = (e100["sweep_start_frame"] + e100["sweep_end_frame"]) // 2
    parts.append(figure(MOTION / "lv100" / f"f{mid:04d}.png", "前線が半分まで降りた所。上の行だけが双子。", 900))
    parts.append(figure(MOTION / "lv100" / f"f{e100['sweep_end_frame'] + 2:04d}.png", "塗り替わり切った所。全面が双子。", 900))
    parts.append('</div>')

    parts.append('<h2>④ 嘘の一文（3 周目 A・侵食度 1）</h2>')
    parts.append('<p>スイの顔で「異常なしと判定しました」が一気に出る。「異常なし」は赤い。読ませたあと、上から前線が降りて人形と走り書きに塗り替わる。'
                 '赤い字も塗り替わって消える。</p>')
    ja = lie["ja"]
    parts.append(video_tag(lie_dir / "ja" / "possession.mp4", "嘘の一文（改修後・日本語）", lie_dir / "ja" / "cursed.png"))
    parts.append('<div class="grid">')
    parts.append(figure(lie_dir / "ja" / "shown.png", "読ませている所。赤い「異常なし」。", 900))
    parts.append(figure(lie_dir / "ja" / "sweep-mid.png", "前線が半分まで降りた所。", 900))
    parts.append(figure(lie_dir / "ja" / "cursed.png", "塗り替わり切った所。", 900))
    parts.append('</div>')
    parts.append('<details><summary>English / Français</summary>')
    for lang, label in (("en", "English"), ("fr", "Français")):
        parts.append(video_tag(lie_dir / lang / "possession.mp4", f"嘘の一文（改修後・{label}）", lie_dir / lang / "cursed.png", "none"))
    parts.append('</details>')
    parts.append('<details><summary>前回（印字へ侵食が追いつく形）と比較する</summary>')
    parts.append(video_tag(Path(a.before_lie), "嘘の一文（前回・日本語）", None, "none"))
    parts.append('</details>')

    parts.append('<h2>確認できたこと</h2>')
    rows = []
    if a.tests:
        rows.append(("EditMode テスト", a.tests))
    for key, num, title in levels:
        e = evidence[key]
        rows.append((f"{title} · 開いた直後（0.1 秒）",
                     f"通常の面との画素差 {e['pixel_delta_open_vs_lv000']:.3f}/255（0 が「出た初めは通常」）"))
        if e.get("possessed"):
            rows.append((f"{title} · 一気に出た直後",
                         f"打鍵 {e['keystrokes']} 発。通常の面との画素差 本文 {e['pixel_delta_shown_text']:.2f} / 顔 {e['pixel_delta_shown_face']:.2f} / "
                         f"地 {e['pixel_delta_shown_plate']:.2f}（/255）"))
            b = e.get("before") or {}
            rows.append((f"{title} · 塗り替わり",
                         f"{e['sweep_start_frame'] / 30:.2f} 秒から {e['sweep_seconds']:.2f} 秒。途中の画で矩形の外の環が変わった画素 上 {e['ring_changed_mid_top']}・"
                         f"下 {e['ring_changed_mid_bottom']}（前線は上から）。乱れの音 {e['sweep_sfx']} 発"))
            rows.append((f"{title} · 乱れ",
                         f"降りている最中の強さ {e['tear_mid']:.2f}（頭打ち 0.6）・飛んだ帯 最大 {e['torn_bands_max']} 本。"
                         f"前線がまだ上半分に居るあいだの本文の下 1/3 のコマ差 {e['motion_below_front_mean']:.2f}"
                         + (f"（前の版 {b['motion_below_front_mean']:.2f}）" if b else "") +
                         f"。抜けてから 0.12 秒後のコマ差 {e['settle_delta_max']:.3f}（0 = 落ち着いた）。"
                         f"地の矩形の明るさの最大 {e['lum_mean_max']:.1f}" + (f"（前の版 {b['lum_mean_max']:.1f}・砂を混ぜていない）" if b else "")))
            rows.append((f"{title} · 塗り替わり切った所",
                         f"本文 {e['pixel_delta_done_text']:.2f} / 顔 {e['pixel_delta_done_face']:.2f} / 地 {e['pixel_delta_done_plate']:.2f}（/255）。"
                         f"矩形の外で変わった画素 {e['ring_changed_pixels_done']}。切られた字 {e['cx_max']} 字"))
        elif float(num) > 0:
            rows.append((f"{title} · 1.2 秒",
                         f"斑 {e['curse_1_2s']:.2f}（目標 {e['target']:.2f}・届いたのは {e['ramp_frame'] / 30:.2f} 秒）。"
                         f"地の画素差 {e['pixel_delta_1_2s_plate']:.2f}/255・顔 {e['pixel_delta_1_2s_face']:.2f}/255・"
                         f"本文 {e['pixel_delta_1_2s_text']:.2f}/255。矩形の外で変わった画素 {e['ring_changed_pixels_1_2s']}"))
            rows.append((f"{title} · 打ち終わり",
                         f"斑の中で切られた字 {e['cx_typed_end']} 字（斑 {e['curse_typed_end']:.2f}）"))
    for lang, label in (("ja", "日本語"), ("en", "English"), ("fr", "Français")):
        l = lie[lang]
        rows.append((f"嘘の一文 · {label}",
                     f"打鍵 {l['keystrokes']} 発。読ませる {l['shown_seconds']:.2f} 秒（赤 {l['red_chars']} 字・赤い画素 {l['red_pixels_shown']}）→ "
                     f"塗り替わり {l['sweep_seconds']:.2f} 秒（途中の画で環の変化 上 {l['ring_changed_mid_top']}・下 {l['ring_changed_mid_bottom']}・乱れ {l['tear_mid']:.2f}・"
                     f"下 1/3 のコマ差 {l['motion_below_front_mean']:.2f}・抜けた後のコマ差 {l['settle_delta_max']:.3f}）→ "
                     f"塗り替わった後は本文 {l['pixel_delta_text']:.2f} / 顔 {l['pixel_delta_face']:.2f} の差・赤い画素 {l['red_pixels_cursed']}。乱れの音 1 発"))
    parts.append('<div class="tblwrap"><table><thead><tr><th>項目</th><th>結果</th></tr></thead><tbody>')
    for k, v in rows:
        parts.append(f'<tr><td>{k}</td><td>{v}</td></tr>')
    parts.append('</tbody></table></div>')
    parts.append('<p>Quest では未確認。装着時に読ませる時間が足りるか、前線の段が読めるかは走行で見る。'
                 '被って分かるもの（怖さの強度・0.45 秒の速さの感じ方）は溜めて 1 回にまとめる。</p>')

    parts.append('<h2>決めたこと・見送ったこと</h2>')
    parts.append('<ul>'
                 '<li>境界は 0.75。0.25 は前回のまま（打つ ＋ 1 秒で斑）。</li>'
                 '<li>出る 0.12 秒 → 読ませる 0.9 ＋ 打つ尺 × 0.45（1.0〜2.4 秒）→ 塗り替わる 0.45 秒 → 全面のまま 2 秒 → 引く。</li>'
                 '<li>前線は 4cm（本文 1 行）の帯ごとに一斉に反転する。列ごとの段は、文字と地で前線がずれると字が途中で割れるのでやめた。</li>'
                 '<li>降りているあいだ面全体が本編の乱れをまねた乱れで乱れる。帯ごとの横飛び ±6cm・帯の脱落（砂は混ぜず消す）・全体の明滅。強さは 0.6 で頭打ち。'
                 '前線の頭で 0.06 秒で立ち、抜けてから 0.12 秒の尾。帯の値は 1 か所で作って地・顔・本文へ同じ値を配る。</li>'
                 '<li>本文の飛びは行ごと。面の枠は動かさない。飛んで矩形の外へ出た字は切る。</li>'
                 '<li>塗り替わりの頭で乱れの音を 1 発（既存の 3 種を順に）。打鍵は鳴らない。</li>'
                 '<li>嘘の一文も同じ形。引き延ばし → 抵抗 → 崩壊と、嘘の後に面が死んだままになる仕様は捨てた。</li>'
                 '<li>「止まってください！」以降と呪いが解けた後は前回のまま（斑 0・打つ）。</li>'
                 '<li>見送り: 前線を斜めにする／前線の縁を光らせる／読ませる時間を報告ボタンで延ばす／縦の同期ずれ（帯 1.6° の面では読めない）／'
                 '読ませている最中の弱い前触れ（正常な状態が先に丸ごと見える順序が壊れる）／塗り替わった後に残る乱れ（壊れた装置に寄る）。</li>'
                 '</ul>')
    parts.append('<p>判定してほしいのは 3 点。読ませる時間の長さと、塗り替わりの速さと、乱れの強さ（いまは本編と同じ 0.6）。'
                 '返事が無ければいまの値で進める。</p>')

    parts.append('<details><summary>Git の記録</summary><pre>git status --short\n'
                 + git_short().replace("&", "&amp;").replace("<", "&lt;") + '</pre></details>')
    parts.append('</main>')

    html = ('<!doctype html><html lang="ja"><meta charset="utf-8">'
            '<meta name="viewport" content="width=device-width,initial-scale=1">'
            '<title>廻リ視 — 連絡の面の憑依（乱れ）</title><style>' + style_block() + '</style>\n' + "\n".join(parts))
    out = Path(a.out)
    out.write_text(html, encoding="utf-8")
    print(f"{out} ({out.stat().st_size / 1e6:.1f} MB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
