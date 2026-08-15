# -*- coding: utf-8 -*-
"""資料を人が読む 1 枚（HTML）にまとめる。`build.py` の後に走らせる。

```
py -3.11 tools/doll-ref/report.py
```

**単一ファイル**（画像は base64・外部 CDN 無し）。置き場は `reports/<日付>_doll-reference.html`。
規約は `~/.claude/rules/output-format.md`。
"""
from __future__ import annotations

import base64
import json
import os
import sys
from datetime import date

import cv2

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
OUT = os.path.join(HERE, "out")
REPORTS = os.path.join(ROOT, "reports")


def b64(path: str, max_w: int = 900) -> str:
    im = cv2.imread(path)
    if im is None:
        return ""
    if im.shape[1] > max_w:
        s = max_w / im.shape[1]
        im = cv2.resize(im, (max_w, int(im.shape[0] * s)), interpolation=cv2.INTER_AREA)
    ok, buf = cv2.imencode(".jpg", im, [cv2.IMWRITE_JPEG_QUALITY, 86])
    return "data:image/jpeg;base64," + base64.b64encode(buf).decode() if ok else ""


def fig(path: str, cap: str, max_w: int = 900) -> str:
    src = b64(os.path.join(OUT, path), max_w)
    if not src:
        return f"<p class='warn'>⚠ {path} が無い（build.py を走らせる）</p>"
    return f"<figure><img src='{src}' alt=''><figcaption>{cap}</figcaption></figure>"


def main() -> int:
    with open(os.path.join(OUT, "measured.json"), encoding="utf-8") as f:
        m = json.load(f)
    with open(os.path.join(HERE, "doll.json"), encoding="utf-8") as f:
        d = json.load(f)

    swatches = "".join(
        f"<div class='sw'><div class='chip' style='background:{p['hex_wb']}'></div>"
        f"<div class='chip raw' style='background:{p['hex']}'></div>"
        f"<b>{p['name']}</b><code>{p['hex_wb']}</code>"
        f"<code class='dim'>生 {p['hex']}</code></div>"
        for p in m["probes"])
    for r in m["regions"]:
        for c in r["colors"]:
            hx, share, nm = c["hex"], int(c["share"] * 100), r["name"]
            swatches += (f"<div class='sw'><div class='chip' style='background:{hx}'></div>"
                         f"<b>{nm}</b><code>{hx}</code>"
                         f"<code class='dim'>面積 {share}%</code></div>")

    def ul(key):
        # doll.json は Markdown の強調（**…**）を含む。HTML では <b> へ直す。
        items = []
        for x in d[key]:
            parts = x.split("**")
            items.append("".join(p if i % 2 == 0 else f"<b>{p}</b>" for i, p in enumerate(parts)))
        return "".join(f"<li>{x}</li>" for x in items)

    gain = m["white_balance"]["gain_bgr"]
    html = f"""<!doctype html><html lang="ja"><meta charset="utf-8">
<title>市松人形 — 生成 AI 用の資料</title>
<style>
  :root {{ --ink:#1c1a18; --dim:#6b6560; --line:#ddd8d0; --bg:#f6f4f0; --warn:#8a2f2f; }}
  * {{ box-sizing:border-box }}
  body {{ margin:0; padding:32px 20px 80px; background:var(--bg); color:var(--ink);
    font-family:"Yu Gothic UI","Hiragino Sans",system-ui,sans-serif; line-height:1.75;
    font-size:15px; }}
  main {{ max-width:980px; margin:0 auto }}
  h1 {{ font-size:26px; margin:0 0 4px; letter-spacing:.02em }}
  h2 {{ font-size:19px; margin:44px 0 10px; padding-bottom:6px; border-bottom:2px solid var(--line) }}
  h3 {{ font-size:16px; margin:26px 0 6px }}
  .lead {{ color:var(--dim); margin:0 0 8px }}
  figure {{ margin:16px 0; }}
  img {{ max-width:100%; height:auto; border:1px solid var(--line); background:#fff; display:block }}
  figcaption {{ color:var(--dim); font-size:13px; margin-top:6px }}
  .row {{ display:flex; gap:16px; flex-wrap:wrap }}
  .row figure {{ flex:1 1 260px; margin:8px 0 }}
  .warn {{ color:var(--warn); font-weight:600 }}
  .box {{ background:#fff; border:1px solid var(--line); border-left:4px solid var(--warn);
    padding:12px 16px; margin:14px 0 }}
  .ok {{ border-left-color:#3a6b4a }}
  ul {{ margin:6px 0 6px 20px; padding:0 }}
  li {{ margin:3px 0 }}
  code {{ font-family:Consolas,ui-monospace,monospace; font-size:12px }}
  .pal {{ display:grid; grid-template-columns:repeat(auto-fill,minmax(190px,1fr)); gap:10px; margin:14px 0 }}
  .sw {{ background:#fff; border:1px solid var(--line); padding:8px; font-size:12px }}
  .sw b {{ display:block; margin:6px 0 2px; font-size:12.5px }}
  .sw code {{ display:block; color:var(--dim) }}
  .chip {{ height:44px; border:1px solid var(--line) }}
  .chip.raw {{ height:12px; border-top:0 }}
  .dim {{ opacity:.65 }}
  table {{ border-collapse:collapse; width:100%; margin:12px 0; font-size:14px }}
  th,td {{ border:1px solid var(--line); padding:7px 10px; text-align:left; vertical-align:top }}
  th {{ background:#fff }}
  @media (max-width:640px) {{ body {{ padding:20px 12px 60px }} }}
</style>
<main>
<h1>市松人形 — 生成 AI に描かせるための資料</h1>
<p class="lead">{date.today().isoformat()} ・ 対象は<b>実在する 1 体</b>（研究室にある高さ約 40cm の人形）。
創作ではない。作り方は <code>tools/doll-ref/</code>、文言の正本は <code>PROMPT.md</code>。</p>

<div class="box ok">
<b>いちばん大事なこと — 正本を添付して、見た目を言葉で書かない。</b><br>
paperdoll の実測（<code>chara/README.md</code>）: 正本を添えて 3 行だけ書いた絵は、11 行の
「変えてはいけないもの」＋ 20 行の画風を付けた絵と<b>見分けがつかなかった</b>。
ここでは正本が<b>実物の写真</b>なので、その効きはさらに強い。
言葉で足すのは<b>写真に写っていないこと</b>（角度・姿勢・照明・背景）だけ。
</div>

<h2>1. 正本（実物の写真）</h2>
<p>撮影は 4 枚。<code>tools/doll-model/photos/</code> にあったものを、人形の範囲へ切って高さを揃えた。
<b>背景は落としていない</b>（理由は下）。</p>
<div class="row">
{fig("plate_front.jpg", "正面。<b>両腕は人が持って T 字に開かせている</b>（人形の姿勢ではない）", 420)}
{fig("plate_sideA.jpg", "側面。こちらは腕を下ろしている", 260)}
{fig("plate_back.jpg", "背面。帯結びは文庫", 420)}
</div>

<div class="box">
<b>⚠ 写真の姿勢は揃っていない。</b> 正面と背面は撮影者が両腕を持って T 字にしており、
側面は腕を下ろしている。そのまま渡すと<b>腕を水平に伸ばした人形</b>が出てくるので、
立ち姿が欲しいときは「腕は体の横へ自然に下ろす」と明示する。
</div>

<h3>寄り</h3>
<div class="row">
{fig("detail_face.jpg", "顔。眼は小さく白目が広い。眉は細い墨の弓なり。唇は小さく歯が少し見える", 300)}
{fig("detail_obi.jpg", "帯（前）。萌黄の織地に菊と唐草、赤の差し色", 300)}
{fig("detail_obi_back.jpg", "帯結び（後）。文庫", 300)}
{fig("detail_sleeve.jpg", "袖。赤地に同色の花の地紋と<b>金箔の粒</b>、袖口に生成りの縁", 220)}
{fig("detail_collar.jpg", "襟元。生成りの半襟・桃と生成りの絞り・金の飾り紐と白い房", 300)}
{fig("detail_nape.jpg", "後頭部。おかっぱの切り口", 300)}
</div>

<h2>2. 色</h2>
<div class="box">
<b>⚠ 写真は暗く、緑に転んでいる。</b> 人形が乗っている白い机を測ると
<code>BGR {m['white_balance']['measured_bgr']}</code>（白なら 235 前後で、しかも G が高い）。
<b>測った生の値をそのまま渡すと、暗く緑がかった別の人形になる。</b><br>
だから机の白を 235 とみなす倍率 <code>{gain}</code> を掛けた推定値を併記する。
<b>上が補正値（生成に渡すのはこちら）、下の細い帯が生の値。</b>
補正は推定であって測り直したわけではない（正しくやるならグレーカードを一緒に撮る）。
</div>
<div class="pal">{swatches}</div>
{fig("probe_map.jpg", "どこを測ったか。<b>丸が探針、四角は柄のある場所（面積の割合で色を出す）。</b>"
     "最初に当てずっぽうで置いた 11 点のうち 6 点が別のものの上に落ちていた（眉が顔、帯が袖の影、手が着物）", 560)}

<h2>3. 生成してみた（参考）</h2>
<p>この資料をそのまま使って 1 枚出した。参照は正面と側面の板 2 枚、言葉で足したのは
背景・腕の高さ・照明・画角の 4 点だけ。</p>
{fig("gen_standing.png", "生成。<b>参考であって正本ではない</b> — 次に生成するときも参照に渡すのは写真の板", 560)}
<div class="box">
<b>実物と違うところ（見て確かめた）:</b> 袖が実物より細い（実物はもっと大きく垂れる振袖）。
袖口から前腕が出ているが、実物は袖がもっと長い。<b>ここを直したければ、袖の寄り
（<code>detail_sleeve.jpg</code>）を参照に足す。</b><br>
⚠ <b>生成した絵を次の生成の参照に使わない</b>（paperdoll の罠: 検証せずに正本として再利用しない）。
</div>

<h2>4. 変えてはいけないもの</h2>
<ul>{ul("fixed")}</ul>
<h3>崩れやすいのはこの 4 つ</h3>
<table>
<tr><th>何</th><th>よくある崩れ方</th></tr>
<tr><td>髪</td><td>真っ黒にされる（実物は<b>暗い葡萄色</b>）</td></tr>
<tr><td>帯</td><td>赤や金にされる（実物は<b>萌黄＝淡い黄緑</b>）</td></tr>
<tr><td>眼</td><td>大きく・二重にされる（実物は小さく白目が広い）</td></tr>
<tr><td>着物</td><td>無地の赤にされる（実物は同色の花の地紋 ＋ <b>金箔の粒</b>）</td></tr>
</table>

<h2>5. やってはいけないこと</h2>
<ul>{ul("forbid")}</ul>

<h2>6. 分かっていないもの</h2>
<p>写真に写っていないので、<b>描き足させない</b>。</p>
<ul>{ul("unknown")}</ul>

<h2>7. 使うもの</h2>
<table>
<tr><th>ファイル</th><th>何</th></tr>
<tr><td><code>tools/doll-ref/PROMPT.md</code></td><td><b>文言の正本。</b>まずこれを読む</td></tr>
<tr><td><code>tools/doll-ref/doll.json</code></td><td>固定要素・禁止・未確定の一覧（機械が読む形）</td></tr>
<tr><td><code>tools/doll-ref/out/plate_*.jpg</code></td><td>参照に添付する板。<b>既定は正面 1 枚</b></td></tr>
<tr><td><code>tools/doll-ref/out/measured.json</code></td><td>測った色（生 / 補正）</td></tr>
<tr><td><code>tools/doll-ref/build.py</code></td><td>板と色を作り直す</td></tr>
</table>

<div class="box">
<b>⚠ 参照は増やすほど良くならない。</b> 食い違う参照は平均化と混線を起こす（paperdoll の罠）。
正面 1 枚を既定にして、要るものだけ 1 枚足す。
</div>
</main></html>"""

    os.makedirs(REPORTS, exist_ok=True)
    path = os.path.join(REPORTS, f"{date.today().isoformat()}_doll-reference.html")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(html)
    print(f"→ {path}  ({os.path.getsize(path) / 1024:.0f} KB)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
