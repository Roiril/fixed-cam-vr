#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""スクリーン左上の時刻表示（OSD）に使うグリフの版を焼く（canon/LEDGER.md 0108）。

**等幅セルを横 1 列に並べた 1 枚**を出す。実行時は `ScreenOsd` が、いまの時刻の
19 文字ぶんのセルを 1 枚のテクスチャへ敷き直して `ScreenComposite` へ渡す。

なぜ TMP でなく版か:
  - この字は「読ませる面」ではなく**画の一部**（監視カメラの OSD）。TMP の面を別に立てると
    `_ScreenPower`（終幕の電池切れ）・dip・管の曲面・縁の暗さに乗らず、
    「画面の上に別の層が貼ってある」＝合成の証拠そのものになる
  - 使う字が 18 種しかない（`0-9` `:` `/` `?` と `周` `目` `最` `後`）ので、静的な版で足りる

⚠ **等幅にする。** `:` と `/` は数字より狭いが、セルの中央へ置いて幅を揃える。
   揃えないと秒が刻むたびに文字列の幅が揺れて、そこだけ生き物のように見える。

⚠ **白は使わない**（生成りに寄せる）。暖色の画（canon/LEDGER.md 0010）の上で
   純白は 1 か所だけ浮く。参考画像 004.jpg の字も紙に近い白。

⚠ **縁を焼き込む。** 監視カメラの OSD は明るい床の上でも読めるように必ず縁取りがある。
   実行時に縁を付ける手段は無い（版を貼るだけなので）ので、ここで焼く。

使い方:
    py -3.11 tools/make-osd-font.py

出力:
    Assets/Resources/Osd/OsdGlyphs.png   （22 セル × CELL_W の RGBA。全角は 2 セルを占める）
    logs/osd/glyphs_preview.png          （人が見る用の拡大・地を暗くして並べた 1 枚）
"""
from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

sys.stdout.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parent.parent
FONT = ROOT / "Assets/Art/Fonts/SourceHanSansJP-Normal.otf"
OUT = ROOT / "Assets/Resources/Osd/OsdGlyphs.png"
PREVIEW = ROOT / "logs/osd/glyphs_preview.png"

# ⚠⚠ **この並びが C# の OsdClockLogic.Glyphs と対**。片方だけ直すと、実機で別の字が出る
#    （テストが両者の一致を固定している）。末尾の空白は「何も描かない」セル。
# `?` は**異世界が映っているあいだ**（canon/LEDGER.md 0167）に時刻と周回を埋める字。
GLYPHS = "0123456789:/ ?"

# ⚠⚠ **全角の字は 2 セルぶんの幅で焼く**（canon/LEDGER.md 0167）。半角セル（24px）へ詰めると
#    漢字は潰れて実機で読めない。2 セル（48px）に 40px の字を入れる（下の wide_px）。
#    C# の OsdClockLogic.WideGlyphs と対で、セル番号は半角 14 セルの後ろに
#    「左・右」の順で並ぶ（周=14,15 / 目=16,17 / 最=18,19 / 後=20,21）。
WIDE_GLYPHS = "周目最後"

# ⚠⚠ **寸法は参考画像（004.jpg）の実測に合わせてある**（2026-08-22 ユーザー赤入れ
#    「数字のフォントや文字の詰め方等は参考画像に合わせてほしい。枠線はあっていい」）。
#
#    実測（画像 512×288 / 文字列 `2025/08/14 00:17:45`）:
#      文字列の幅 95px・字高 7px  → **送り / 字高 = 0.714**、字幅 / 字高 = 0.71
#
#    同梱フォントの数字は **幅 / 字高 = 0.706**（44px で 送り 24 / 字高 34）で、
#    比率は参考とほぼ同じだった。**ずれていたのはセル幅だけ** — 40px も取っていたので
#    送り / 字高 = 1.18 ＝ 参考より 64% 広く、字が離れて見えていた。
#    ⇒ **セル幅 = フォントの送り幅（24）**。これで詰まり具合が参考と一致する。
#
# ⚠ 縦は `/` がいちばん背が高い（数字 34 に対し 43）。縁 5px 込みで 53px 要るので 54。
CELL_W, CELL_H = 24, 54
FONT_PX = 44

# 生成り（HmdTextStyle.Ink と同じ系統。純白にしない）
INK = (232, 226, 214)
# 縁。真っ黒だと版の縁が硬いので、少しだけ持ち上げる。
# ⚠⚠ **縁が字の読みやすさの主役**（2026-08-22 に絵で見て太らせた）。字は明るい壁の上にも
#    信号断の砂嵐（平均輝度 140）の上にも出るので、字の明るさだけでは沈む。
#    実測: stroke 3 では砂嵐の上のコントラストが 9.9 しか無く、字がほぼ読めなかった。
EDGE = (6, 5, 4)
EDGE_A = 255
STROKE = 5


def main() -> int:
    if not FONT.exists():
        print(f"NG フォントが無い: {FONT}")
        return 1

    font = ImageFont.truetype(str(FONT), FONT_PX)

    # ベースラインを数字で揃える。`:` は数字より背が低いが、
    # **セルの中で上下に動かさない**（動かすと時刻が波打つ）。
    digits_bbox = font.getbbox("0123456789")
    glyph_h = digits_bbox[3] - digits_bbox[1]
    top = (CELL_H - glyph_h) // 2 - digits_bbox[1]

    # ⚠⚠ **全角は 2 セルの幅いっぱいまで大きくする。** 字高を数字（34px）へ合わせると幅が 35px に
    #    なり、2 セル（48px）の中で左右に 6.5px ずつ余る。隣り合うと **13px の隙間**（字幅の 37%）が
    #    空いて、絵で見ると「1周 目」と語が割れて見えた（2026-09-06・`menu osd` の絵で直した）。
    # ⚠ 上限は 2 つ。幅は 2 セル − 左右 4px（縁の逃げ）、高さは縁 5px 込みでセルに収まること
    #    （`/` と同じで、超えるとセルの上下端で切れる）。em 幅 ＝ フォントの大きさなので、
    #    幅の側が先に効いて **40px** になる。数字より 5px 背が高くなるが、
    #    日本語の混植では漢字が数字より大きいのが普通の形。
    wide_px = max(8, 2 * CELL_W - 8)
    wide_font = ImageFont.truetype(str(FONT), wide_px)
    wb = wide_font.getbbox(WIDE_GLYPHS)
    if wb[3] - wb[1] + 2 * STROKE > CELL_H:
        # 高さの側が先に効く比率のフォントに差し替えたとき用（いまのフォントでは通らない道）
        wide_px = max(8, round(wide_px * (CELL_H - 2 * STROKE) / (wb[3] - wb[1])))
        wide_font = ImageFont.truetype(str(FONT), wide_px)
        wb = wide_font.getbbox(WIDE_GLYPHS)
    wide_top = (CELL_H - (wb[3] - wb[1])) // 2 - wb[1]

    cell_count = len(GLYPHS) + 2 * len(WIDE_GLYPHS)
    atlas = Image.new("RGBA", (CELL_W * cell_count, CELL_H), (0, 0, 0, 0))

    # ⚠⚠ **1 セルずつ独立した画像へ描いてから貼る。** 数字は送り幅いっぱい（24px）を使うので、
    #    縁 5px は必ずセルの外へはみ出す。1 枚のキャンバスへ直接描くと、はみ出した縁が
    #    **隣のセルへ流れ込んで別の字に混ざる**（敷き直しはセル単位でコピーするため、
    #    画面では「関係ない字の縁の破片」として出る）。ここで切っておけば、
    #    並べたときに切り口どうしが接して連続した縁になる。
    # ⚠ 左右の縁は原理的に残らない（字が送り幅を使い切っている）。参考画像も字は隙間なく
    #    並んでいて左右の縁は無い。**効くのは上下の縁**で、可読性はそれで足りている（§検証）。
    def draw_cell(ch: str, cells: int, at: int, f: ImageFont.FreeTypeFont, y: int) -> None:
        """幅 `cells` セルの独立した画像へ 1 字描いてから、セル `at` へ貼る。"""
        w = CELL_W * cells
        cell = Image.new("RGBA", (w, CELL_H), (0, 0, 0, 0))
        b = f.getbbox(ch)
        # 等幅: そのグリフの実幅をセルの中央へ
        x = (w - (b[2] - b[0])) // 2 - b[0]
        ImageDraw.Draw(cell).text((x, y), ch, font=f, fill=INK + (255,),
                                  stroke_width=STROKE, stroke_fill=EDGE + (EDGE_A,))
        atlas.alpha_composite(cell, (at * CELL_W, 0))

    for i, ch in enumerate(GLYPHS):
        if ch == " ":
            continue
        draw_cell(ch, 1, i, font, top)

    # 全角は 2 セル幅。番号は半角の後ろに「左・右」の順で並ぶ（C# の WideCellIndex と対）。
    for j, ch in enumerate(WIDE_GLYPHS):
        draw_cell(ch, 2, len(GLYPHS) + 2 * j, wide_font, wide_top)

    OUT.parent.mkdir(parents=True, exist_ok=True)
    atlas.save(OUT)

    # 人が見る用。実機の地（暗い映像）に近い色を敷いて 3 倍に拡大する。
    # ⚠ 透明の市松の上で見ると縁の効きが読めない（版の縁は暗い地でこそ効く）。
    PREVIEW.parent.mkdir(parents=True, exist_ok=True)
    bg = Image.new("RGBA", atlas.size, (28, 24, 20, 255))
    bg.alpha_composite(atlas)
    bg.resize((atlas.width * 3, atlas.height * 3), Image.NEAREST).save(PREVIEW)

    # 焼けたかを数で言う（絵を開かずに「空の版を焼いた」を検出するため）
    alpha = atlas.getchannel("A")
    ink = sum(1 for a in alpha.tobytes() if a > 8)
    print(f"OK {OUT.relative_to(ROOT)}  {atlas.width}x{atlas.height}"
          f"  セル {CELL_W}x{CELL_H} × {cell_count}"
          f"（半角 {len(GLYPHS)} ＋ 全角 {len(WIDE_GLYPHS)}×2）"
          f"  不透明 {ink / (atlas.width * atlas.height):.1%}")
    print(f"   全角 {wide_px}px 字高 {wb[3] - wb[1]}px（数字 {FONT_PX}px 字高 {glyph_h}px）")
    print(f"   見る: {PREVIEW.relative_to(ROOT)}")

    # ⚠ **セルの上下端に字が届いていたら切れている**（版は縦に余白が無い）。
    #    全角を足したときにここで気づけないと、実機で漢字の頭だけが欠ける。
    # ⚠ `/` だけは版を焼いた当初から**縁の最下 1 行がはみ出している**（数字 34 に対し 43px と
    #    背が高く、縁 5px 込みで 53px。セル 54 に対して余白が 1 行しかない）。字そのものは
    #    入っていて実機で問題になっていないので、ここは既知として通す。ほかの字が触れたら NG。
    ALLOW_EDGE_TOUCH = {"/"}
    px = alpha.load()
    cells: list[tuple[str, int]] = [(ch, i) for i, ch in enumerate(GLYPHS)]
    cells += [(ch, len(GLYPHS) + 2 * j) for j, ch in enumerate(WIDE_GLYPHS)]
    bad = []
    for ch, at in cells:
        if ch == " " or ch in ALLOW_EDGE_TOUCH:
            continue
        width = CELL_W * (2 if ch in WIDE_GLYPHS else 1)
        for y in (0, CELL_H - 1):
            if any(px[x, y] > 8 for x in range(at * CELL_W, at * CELL_W + width)):
                bad.append(f"{ch}(行 {y})")
    if bad:
        print(f"NG 字がセルの上下端に届いている: {' '.join(bad)}"
              "  — CELL_H を増やすか字を小さくする")
        return 1

    if ink < atlas.width * atlas.height * 0.02:
        print("NG ほとんど空の版。フォントがグリフを持っていない可能性がある")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
