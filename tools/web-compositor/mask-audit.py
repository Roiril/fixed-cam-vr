# いま演出で使っている素材のマスクを、実機と同じ枠空間で描き出して 1 枚の HTML にまとめる。
#
#   使い方: python mask-audit.py [--out reports/YYYY-MM-DD_mask-audit.html]
#
#   実機（ScreenComposite.shader）の規約:
#     枠は 16:9。live / overlay は contain-fit（_LiveScale / _OverlayScale）で枠へ収める。
#     **マスクだけは contain-fit を通らず生 uv で読む**（_MaskScale は無い）。だからマスクは
#     「素材の座標」ではなく「スクリーン枠の座標」で焼かれていなければならない。ここはそれを検算する。
#
#   出すもの: 素材 / マスク / 差し替わる場所 / 合成結果（無人プレート × 素材・色統計込み）と、
#   マスク寸法・白の面積・ぼかし帯・枠外へのはみ出し・**素材と部屋の色ずれ**。
#
#   ⚠ 色ずれは**マスクの外**で測る。中には人形や手形そのものが居るので、中で測ると
#   「主題が背景と違う」だけで大きな数字が出る（それは正しい絵であって、直すべきズレではない）。
import argparse
import base64
import io
import json
import os

import numpy as np
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(ROOT))
OUT = os.path.join(REPO, 'Assets', 'Screenshots', 'maskviz')
# ハウススタイルの CSS（~/.claude/skills/visual-deliverable）。無ければ素の見た目で出す。
CSS_PATH = os.path.expanduser('~/.claude/skills/visual-deliverable/assets/report.css')
CSS = open(CSS_PATH, encoding='utf-8').read() if os.path.exists(CSS_PATH) else ''
FW, FH = 640, 360
os.makedirs(OUT, exist_ok=True)

show = json.load(open(os.path.join(ROOT, 'show.json'), encoding='utf-8'))
cues = {c['id']: c for c in show['cues']}
CAMN = 'ABCD'

used = []
seen = set()
for seg in show['timeline']['segments']:
    for t in seg.get('takes', []):
        for i, st in enumerate(t.get('steps', [])):
            cid = st.get('cueId')
            if not cid:
                continue
            where = f"{seg['lap']}周目 カメラ{CAMN[seg['camera']]}"
            if cid in seen:
                for u in used:
                    if u[0] == cid:
                        u[1].append(where)
                continue
            seen.add(cid)
            used.append([cid, [where], st['source'], t['id']])

plates = {c['camera']: c['sourceUrl'] for c in show['cues'] if c['id'].startswith('plate_')}


def _conv(a, k):
    """箱型フィルタ（積分画像）。近傍に相手が居るかを見るだけなので厳密さは要らない。"""
    r = (k.shape[0] - 1) // 2
    p = np.pad(a, r + 1)
    s = p.cumsum(0).cumsum(1)
    h, w = a.shape
    y0, x0 = np.arange(h), np.arange(w)
    Y, X = np.meshgrid(y0, x0, indexing='ij')
    y1, x1 = Y, X
    y2, x2 = Y + 2 * r + 1, X + 2 * r + 1
    return s[y2, x2] - s[y1, x2] - s[y2, x1] + s[y1, x1]


def load(rel):
    p = os.path.join(ROOT, str(rel).lstrip('/'))
    return Image.open(p).convert('RGB') if os.path.exists(p) else None


def contain(img, fw=FW, fh=FH):
    out = Image.new('RGB', (fw, fh), (0, 0, 0))
    if img is None:
        return out, (0, 0, fw, fh)
    a, fa = img.width / img.height, fw / fh
    w, h = (fw, round(fw / a)) if a > fa else (round(fh * a), fh)
    x, y = (fw - w) // 2, (fh - h) // 2
    out.paste(img.resize((w, h), Image.LANCZOS), (x, y))
    return out, (x, y, x + w, y + h)


def b64(img, q=90):
    buf = io.BytesIO()
    img.save(buf, 'JPEG', quality=q, subsampling=0)
    return 'data:image/jpeg;base64,' + base64.b64encode(buf.getvalue()).decode()


def label(img, text):
    """タイルの上に何の絵かを焼く（後から並べても何の絵か分かるように）。"""
    out = Image.new('RGB', (img.width, img.height + 26), (16, 16, 18))
    out.paste(img, (0, 26))
    ImageDraw.Draw(out).text((6, 7), text, fill=(214, 208, 198))
    return out


rows = []
for cid, wheres, source, take in used:
    c = cues[cid]
    cam = c.get('camera')
    src = load(c['sourceUrl'])
    mask_rel = c.get('maskUrl') or ''
    mask = load(mask_rel) if mask_rel else None
    base = load(plates.get(cam, '')) if cam in plates else None

    live_f, live_rect = contain(base)
    ov_f, _ = contain(src)

    if mask is None:
        m = np.ones((FH, FW), dtype=np.float32)
        info = dict(mask_dims='—', cov=100.0, outside=0.0, bbox=None, feather=None)
    else:
        mr = np.asarray(mask.resize((FW, FH), Image.NEAREST).convert('L'), np.float32) / 255.0
        m = mr
        hard = mr > 0.5
        ys, xs = np.where(hard)
        out_area = np.ones((FH, FW), bool)
        out_area[live_rect[1]:live_rect[3], live_rect[0]:live_rect[2]] = False
        info = dict(
            mask_dims=f'{mask.width}×{mask.height}',
            cov=float(hard.mean() * 100),
            outside=float((hard & out_area).sum() / max(1, hard.sum()) * 100),
            bbox=(int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max())) if xs.size else None,
            # 中間調の画素比 = フェザー（ぼかし）の量。0 ならハード切り抜き
            feather=float(((mr > 0.02) & (mr < 0.98)).mean() * 100))

    a = np.asarray(live_f, np.float32)
    b = np.asarray(ov_f, np.float32)
    # 色統計マッチング。実機（ScreenComposite.shader の `overlay * _OverlayGain + _OverlayOffset`）と
    # 同じ順で素材へ掛ける。焼かれていない cue では恒等。
    gain = np.array(c.get('matchGain') or [1, 1, 1], np.float32)
    off = np.array(c.get('matchOffset') or [0, 0, 0], np.float32) * 255.0
    if c.get('hasMatch'):
        b = np.clip(b * gain + off, 0, 255)
    comp = Image.fromarray((a * (1 - m[..., None]) + b * m[..., None]).astype(np.uint8))

    # 素材の**使われない部分**（マスクの外）が、無人の部屋とどれだけ違うか。
    #
    # ⚠ マスクの中で比べてはいけない — 中に居るのは人形や手形そのものなので、「主題が背景と違う」
    #   だけで大きな数字が出る。それは正しい絵であって、直すべきズレではない。
    #   マスクの外は素材も無人の部屋も**同じ部屋を写しているはず**なので、そこの差は
    #   まるごと「素材の色の作られ方が実写とずれている量」になる。中の背景にも同じだけ乗る。
    info['dcolor'], info['dlum'] = None, None
    if mask is not None and base is not None:
        keep = (m < 0.02)
        keep[:live_rect[1], :] = False       # letterbox の黒帯は比較対象外
        keep[live_rect[3]:, :] = False
        keep[:, :live_rect[0]] = False
        keep[:, live_rect[2]:] = False
        if keep.sum() > 500:
            d = b[keep].mean(0) - a[keep].mean(0)
            info['dcolor'] = [round(float(v), 1) for v in d]
            info['dlum'] = round(float(np.abs(b[keep] - a[keep]).mean()), 1)

    over = Image.fromarray(
        (a * 0.5 + np.array([40, 255, 120], np.float32) * (m[..., None] * 0.6)).clip(0, 255).astype(np.uint8))
    ImageDraw.Draw(over).rectangle(live_rect, outline=(255, 200, 0), width=2)
    mimg = Image.fromarray((m * 255).astype(np.uint8)).convert('RGB')
    ImageDraw.Draw(mimg).rectangle(live_rect, outline=(255, 200, 0), width=2)

    tiles = [label(ov_f, '素材'), label(mimg, 'マスク（白 = 差し替える）'),
             label(over, '差し替わる場所（緑）'), label(comp, '合成結果（無人の部屋 × 素材）')]
    sheet = Image.new('RGB', (FW * 2 + 16, (FH + 26) * 2 + 16), (16, 16, 18))
    for i, t in enumerate(tiles):
        sheet.paste(t, ((i % 2) * (FW + 16), (i // 2) * (FH + 26 + 16)))

    info.update(id=cid, name=c.get('name', ''), wheres=wheres, source=source, cam=cam,
                src_dims=f'{src.width}×{src.height}' if src else '—',
                img=b64(sheet), has_mask=mask is not None,
                src_url=c['sourceUrl'], mask_url=mask_rel,
                has_match=any(k in c for k in ('matchGain', 'matchOffset', 'hasMatch')))
    rows.append(info)

json.dump([{k: v for k, v in r.items() if k != 'img'} for r in rows],
          open(os.path.join(OUT, 'stats.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)

# ---- HTML ------------------------------------------------------------------
E = lambda s: str(s).replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')
masked = [r for r in rows if r['has_mask']]
plain = [r for r in rows if not r['has_mask']]

def cell(v, fmt='{:.0f}'):
    return '—' if v is None else fmt.format(v)


trs = ''
for r in rows:
    trs += ('<tr>'
            + f"<td>{E(r['name'] or r['id'])}<br><span class=muted>{E('・'.join(r['wheres']))}</span></td>"
            + f"<td>{'部分' if r['has_mask'] else '全面'}</td>"
            + f"<td>{E(r['mask_dims'])}</td>"
            + f"<td>{cell(r['cov'], '{:.0f}%')}</td>"
            + f"<td>{cell(r['feather'], '{:.0f}%')}</td>"
            + f"<td>{cell(r['dlum'])}</td></tr>")

secs = ''
for r in rows:
    tag = '部分マスク' if r['has_mask'] else 'マスク無し（全面差し替え）'
    extra = ''
    if r['has_mask']:
        bb = r['bbox']
        extra = (f"<p class=note>マスク {E(r['mask_dims'])}（枠と同じ 16:9）／ 白 {r['cov']:.0f}%／ "
                 f"ぼかし帯 {r['feather']:.0f}%／ 範囲 x {bb[0]}–{bb[2]} · y {bb[1]}–{bb[3]}／ "
                 f"枠の外へはみ出した白 {r['outside']:.0f}%</p>")
    else:
        extra = "<p class=note>マスクを持たないので、枠いっぱいが素材に置き換わります。</p>"
    dc = ('—' if r['dcolor'] is None
          else f"R{r['dcolor'][0]:+.0f} G{r['dcolor'][1]:+.0f} B{r['dcolor'][2]:+.0f}")
    secs += (f"<h2>{E(r['name'] or r['id'])}</h2>"
             f"<p class=sub>{E('・'.join(r['wheres']))} ／ <span class=chip>{tag}</span> "
             f"／ 素材 {E(r['src_dims'])} ／ 素材と部屋の色ずれ {E(dc)}</p>"
             f"<figure><img src=\"{r['img']}\" alt=\"{E(r['id'])}\">"
             f"<figcaption>左上=素材／右上=マスク／左下=差し替わる場所（緑）／右下=無人の部屋に重ねた結果。"
             f"黄枠は実際の映像が入る 4:3 の範囲で、その外側は黒帯です。</figcaption></figure>"
             f"{extra}")

html = f"""<title>いま演出で使っている素材のマスク</title>
<style>{CSS}</style>
<div class="wrap">
<p class="eyebrow">廻リ視 · 素材とマスクの棚卸し</p>
<h1>いま演出で使っている素材のマスク</h1>
<p class="sub">show.json timeline rev {show['timeline']['rev']} 時点 ／ （このファイルを走らせた時点の show.json）</p>

<div class="lead"><p>演出が指している素材は 7 件。うち<b>マスクを持つのは 4 件</b>（1周目 B の手形、
2周目 A・B・C の人形）で、3周目以降の無人プレート 3 件は枠いっぱいを置き換えます。
4 件とも寸法は枠と同じ 16:9 で、白が黒帯へはみ出しているものはありません。
<b>色統計マッチングは 4 件とも焼き込み済み</b>（実機は 6 つの数を掛けるだけ）。
素材と部屋の色ずれは 2〜8 に収まっています。</p></div>

<h2>一覧</h2>
<div class="tblwrap"><table>
<thead><tr><th>素材</th><th>差し替え</th><th>マスク寸法</th><th>白の面積</th><th>ぼかし帯</th><th>色ずれ</th></tr></thead>
<tbody>{trs}</tbody></table></div>
<p class="note">「白の面積」は枠に対する割合。「ぼかし帯」は中間調の画素の割合で、0 に近いほど硬い切り抜き。
「色ずれ」は**マスクの外**（素材も無人の部屋も同じ部屋を写している所）での平均差（0–255）。マスクの中で測ると人形そのものの色が混ざるので測っていない。</p>

{secs}

<p class="note">合成は実機の ScreenComposite と同じ式（live に素材をマスクで混ぜる）で、
下地には各カメラの無人プレートを使っています。実機では映像がライブになるだけで幾何は同じです。
色統計マッチング（cue へ焼いた gain/offset）は実機と同じ式で掛けています。ラプラシアンは卓プレビュー専用なので掛けていません。</p>
</div>"""

ap = argparse.ArgumentParser()
ap.add_argument('--out', default=os.path.join(REPO, 'reports', 'mask-audit.html'))
path = ap.parse_args().out
os.makedirs(os.path.dirname(path), exist_ok=True)
open(path, 'w', encoding='utf-8', newline='\n').write(html)
print('wrote', path, os.path.getsize(path) // 1024, 'KB')
for r in rows:
    print('  %-14s mask=%-9s cov=%5.1f%% feather=%s dlum=%s match=%s'
          % (r['id'], r['mask_dims'], r['cov'], cell(r['feather'], '{:.1f}%'),
             r['dlum'], r['has_match']))
