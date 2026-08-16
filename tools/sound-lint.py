# -*- coding: utf-8 -*-
"""音の検査。**数値で合否を出し、波形とスペクトログラムを絵にする。**

```
py -3.11 tools/sound-lint.py
py -3.11 tools/sound-lint.py --dir Assets/Resources/Sound
```

出るもの:
  - 端末に 1 行 1 本の合否
  - `Assets/Screenshots/sound/<名前>.png`（波形 ＋ スペクトログラム）
  - `reports/<日付>_sound.html`（全部を 1 枚に並べたもの・単一ファイル自己完結）

**これは耳の代わりであって、耳ではない。** ここが全部緑でも「怖いか」「間が持つか」は
何も言っていない（`reference/why.md`「品質を採点しない」）。落とすために使う。
"""

from __future__ import annotations

import argparse
import base64
import datetime
import io
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import soundkit as sk  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_DIR = os.path.join(ROOT, "Assets", "Resources", "Sound")
PNG_DIR = os.path.join(ROOT, "Assets", "Screenshots", "sound")

# ---- 合否の規則 -------------------------------------------------------------
# 正本は `.claude/rules/sound-design.md`。ここは実行できる形の写し。
RULES = {
    "true_peak_db": (-60.0, -2.9, "尖頭が -3dBTP を超えている（実機の変換で歪む）"),
    "mono_db": (-3.5, 0.5, "モノにすると消える（左右が打ち消している。遅延で広げていないか）"),
    "dc": (0.0, 0.002, "直流が乗っている（スピーカーの可動域を無駄に使う）"),

}
SEAM_MAX = 3.0        # ループの継ぎ目の飛び（隣接標本差の何倍まで許すか）

# ⚠ **実物を録った音は突出していて当たり前**（鈴 44dB / 金属 32dB）。
#    「発振器に聞こえる」を禁じるのは**合成した音**だけなので、実録は突出の判定から外す。
#    ここに足すのは `tools/ingest-sounds.py` が焼くものだけ（合成へ戻したら消すこと）。
#    ⚠ `sfx_screen_on` は**もらった音なのにここに入っていなかった**（2026-08-13 に気づいた）。
#       純度 0.75 / 突出 35dB で NG が出るが、これは「合成でうっかり正弦波を置いた」ではなく
#       **ユーザーが選んだ音源にトーンが入っている**という事実。もらった音は音量と端の処理しか
#       掛けない（`rules/sound-design.md` §4.5）ので、通すために作り変える方が規約違反になる。
#    ⚠ 2026-08-15 追加: 周ごとの環境音 2 本（`canon/LEDGER.md` 0049 でユーザーが指定した音源）。
#       **内蔵スピーカーの判定からは外していない** — あれは「実機で聞こえるか」の話で、
#       もらった音でも聞こえないものは聞こえない（`sfx_title_in` と同じ扱い）。
#    ⚠ 2026-08-16 追加: 割れる音（もらった一撃を小刻みに並べたもの）と カメラ切替
#       （`canon/LEDGER.md` 0057 でユーザーが指定した音源）。**並べただけで素材は実録**。
RECORDED = {"amb_bell", "amb_creak_1", "amb_creak_2", "sfx_title_in", "sfx_seal_close",
            "sfx_screen_on", "bed_room_lap2", "bed_room_lap3",
            "sfx_shatter", "sfx_switch_1", "amb_dolls_laugh"}
TONAL_MAX = 25.0      # 合成音の突出の上限。実測の目安は「実物の機械 = 15dB 前後」

# 発振器が居るか（掃引する純音も捕まる）。実測: 純音 0.93〜1.00 / 帯ノイズの掃引 0.31〜0.43 /
# ノイズ 0.08〜0.21。⚠ `tonal_db` は**滑る純音を原理的に検出できない**ので、この 2 本立てが要る。
PURITY_MAX = 0.60
CLASS_TRANSIENT_DB = 14.0   # これより波高が大きい ＝ 一撃 ＝ 尖頭で揃える側


def spectrogram(y: np.ndarray, w: int, h: int, sr: int = sk.SR) -> np.ndarray:
    """対数周波数のスペクトログラム（0..1 の輝度）。低い方を潰さないための対数軸。"""
    m = sk.to_stereo(y).mean(axis=1)
    n_fft = 2048
    hop = max(1, (len(m) - n_fft) // max(w, 1))
    win = np.hanning(n_fft)
    cols = []
    for i in range(w):
        a = i * hop
        seg = m[a:a + n_fft]
        if len(seg) < n_fft:
            seg = np.pad(seg, (0, n_fft - len(seg)))
        cols.append(np.abs(np.fft.rfft(seg * win)))
    S = np.array(cols).T                                   # (freq, time)
    f = np.fft.rfftfreq(n_fft, 1 / sr)
    lo, hi = 40.0, sr / 2
    targets = np.logspace(math.log10(lo), math.log10(hi), h)
    idx = np.clip(np.searchsorted(f, targets), 1, len(f) - 1)
    S = S[idx][::-1]                                       # 上が高音
    db = 20 * np.log10(np.maximum(S, 1e-9))
    return np.clip((db - (db.max() - 78.0)) / 78.0, 0, 1)


def render(name: str, y: np.ndarray, out_path: str, sr: int = sk.SR) -> None:
    """波形（上）とスペクトログラム（下）を 1 枚に。"""
    W, HW, HS, PAD = 900, 130, 260, 6
    img = Image.new("RGB", (W, HW + HS + PAD * 3), (14, 12, 11))
    d = ImageDraw.Draw(img)

    # --- 波形（左右を上下に重ねる。赤 = 左 / 生成り = 右）---
    m = sk.to_stereo(np.asarray(y))
    step = max(1, len(m) // W)
    mid = PAD + HW // 2
    for x in range(W):
        seg = m[x * step:(x + 1) * step]
        if not len(seg):
            continue
        for c, col in ((0, (196, 68, 48)), (1, (214, 178, 132))):
            a = int(mid - float(np.max(seg[:, c])) * (HW / 2 - 2))
            b = int(mid - float(np.min(seg[:, c])) * (HW / 2 - 2))
            d.line([(x, a), (x, b)], fill=col)
    d.line([(0, mid), (W, mid)], fill=(60, 52, 46))

    # --- スペクトログラム（暖色。地は黒、熾のように赤 → 生成り）---
    S = spectrogram(y, W, HS, sr)
    lut = np.zeros((256, 3), dtype=np.uint8)
    for i in range(256):
        t = i / 255.0
        lut[i] = (int(255 * min(1, t * 2.1)),
                  int(255 * max(0, min(1, (t - 0.32) * 1.7)) ** 1.1),
                  int(255 * max(0, min(1, (t - 0.62) * 2.6))))
    rgb = lut[(S * 255).astype(np.uint8)]
    img.paste(Image.fromarray(rgb, "RGB"), (0, PAD * 2 + HW))
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    img.save(out_path)


def check(name: str, y: np.ndarray, is_loop: bool) -> tuple[dict, list[str]]:
    d = sk.describe(y)
    d["bands"] = d.pop("bands_db")
    d["class"] = "一撃" if d["crest_db"] > CLASS_TRANSIENT_DB else "持続"
    flat = dict(d)
    bad = []
    for key, (lo, hi, msg) in RULES.items():
        v = flat.get(key)
        if v is None:
            continue
        if v < lo or v > hi:
            bad.append(f"{msg}（{key}={v}）")
    # ⚠ **内蔵スピーカーの線は種類で分ける。** 敷く音は 3 分間ずっと聞こえていなければ
    #    ならないので厳しく、一撃は一瞬なので緩く。同じ線で測ると
    #    低い衝撃を持つ素材（シネマチックな一撃など）が理由なく落ちる。
    lim = -6.0 if is_loop else -10.0
    if d["speaker_db"] < lim:
        bad.append(f"Quest の内蔵スピーカーで消える（情報が 200Hz より下にある・"
                   f"speaker_db={d['speaker_db']} / 下限 {lim}）")
    # 不快さ（2026-08-12 ユーザー指示「不気味で怖くていいけど、不快にはならないように」）
    # 突出（＝「安っぽい電子音」）。2026-08-12 ユーザー指摘
    # 「チープな電子音はチープすぎるからやめてほしい」。
    if name not in RECORDED and d["purity"] > PURITY_MAX:
        bad.append(f"発振器が居る（純度 {d['purity']} / 上限 {PURITY_MAX}）— "
                   "正弦波と `sk.sweep` をやめて `tone_band` / `sweep_band` にする")
    if name not in RECORDED and d["tonal_db"] > TONAL_MAX:
        bad.append(f"発振器に聞こえる（突出 {d['tonal_db']}dB / 上限 {TONAL_MAX}）— "
                   "純音をやめて狭帯域ノイズ（soundkit.tone_band）にする")
    sharp_lim, rough_lim = (2.5, 0.4) if is_loop else (3.5, 1.0)
    if d["sharp"] > sharp_lim:
        bad.append(f"耳に刺さる（鋭さ {d['sharp']} / 上限 {sharp_lim}）")
    if d["rough"] > rough_lim:
        bad.append(f"ざらついて苛立つ（粗さ {d['rough']} / 上限 {rough_lim}）")

    if is_loop:
        seam = sk.loop_seam(y)
        d["seam"] = seam
        if seam["jump"] > SEAM_MAX:
            bad.append(f"巻き戻りにクリック（飛び x{seam['jump']}・上限 x{SEAM_MAX}）")
        if abs(seam["rms_d_db"]) > 6.0:
            bad.append(f"継ぎ目で密度が変わる（{seam['rms_d_db']:+.1f}dB）")
    return d, bad


HTML_HEAD = """<meta charset="utf-8"><title>廻リ視 — 音の検査</title>
<style>
:root{color-scheme:dark;--bg:#0e0c0b;--fg:#e8ddd0;--dim:#9a8c7d;--ok:#8fbf7a;--ng:#d8623f;--line:#2a2420}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);
font:15px/1.7 "Yu Gothic UI","Hiragino Sans",system-ui,sans-serif;padding:32px 20px 80px}
.wrap{max-width:960px;margin:0 auto}h1{font-size:24px;font-weight:600;margin:0 0 4px;letter-spacing:.04em}
.sub{color:var(--dim);font-size:13px;margin-bottom:28px}
.card{border:1px solid var(--line);border-radius:6px;margin:18px 0;overflow:hidden;background:#131110}
.hd{display:flex;align-items:baseline;gap:12px;padding:12px 16px;border-bottom:1px solid var(--line)}
.hd b{font-size:16px;letter-spacing:.02em}.tag{font-size:11px;color:var(--dim);border:1px solid var(--line);
padding:1px 7px;border-radius:99px}.st{margin-left:auto;font-size:13px}.ok{color:var(--ok)}.ng{color:var(--ng)}
img{width:100%;display:block}
table{width:100%;border-collapse:collapse;font-size:13px}td{padding:5px 16px;border-top:1px solid var(--line)}
td:first-child{color:var(--dim);width:34%}
ul{margin:8px 0;padding:8px 16px 12px 34px;color:var(--ng);font-size:13px}
.note{color:var(--dim);font-size:13px;border-left:2px solid var(--line);padding:2px 0 2px 14px;margin:22px 0}
@media(prefers-color-scheme:light){:root{--bg:#faf7f2;--fg:#221c16;--dim:#6b5f52;--line:#e0d7ca}
.card{background:#fff}}
</style>"""


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=DEFAULT_DIR)
    a = ap.parse_args()

    names = sorted(f[:-4] for f in os.listdir(a.dir) if f.endswith(".wav"))
    if not names:
        print(f"WAV が無い: {a.dir}")
        return 1

    rows, fails = [], 0
    for name in names:
        y, sr = sk.read_wav(os.path.join(a.dir, f"{name}.wav"))
        is_loop = name.startswith("bed_")
        d, bad = check(name, y, is_loop)
        png = os.path.join(PNG_DIR, f"{name}.png")
        render(name, y, png, sr)
        rows.append((name, d, bad, png, is_loop))
        fails += bool(bad)
        mark = "NG" if bad else "ok"
        print(f"  [{mark}] {name:20s} {d['class']}  {d['lufs']:6.1f}LUFS "
              f"tp{d['true_peak_db']:6.1f} 波高{d['crest_db']:5.1f} mono{d['mono_db']:6.2f} "
              f"内蔵SP{d['speaker_db']:6.1f} 鋭{d['sharp']:5.2f} 純度{d['purity']:5.2f}")
        for b in bad:
            print(f"        - {b}")

    stamp = datetime.date.today().isoformat()
    out = os.path.join(ROOT, "reports", f"{stamp}_sound.html")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    h = [HTML_HEAD, '<div class="wrap"><h1>廻リ視 — 音の検査</h1>',
         f'<p class="sub">{stamp} ・ {len(names)} 本 ・ NG {fails} 本 ・ '
         f'規則の正本は <code>.claude/rules/sound-design.md</code></p>',
         '<p class="note">数値と絵は<b>落とすため</b>の道具です。ここが全部緑でも'
         '「怖いか」「間が持つか」は何も言っていません。上段が波形（赤＝左 / 生成り＝右）、'
         '下段が対数周波数のスペクトログラム（上が高音）。</p>']
    for name, d, bad, png, is_loop in rows:
        with open(png, "rb") as f:
            b64 = base64.b64encode(f.read()).decode()
        st = ('<span class="st ng">NG</span>' if bad else '<span class="st ok">ok</span>')
        seam = (f"<tr><td>巻き戻りの飛び</td><td>x{d['seam']['jump']} "
                f"（密度差 {d['seam']['rms_d_db']:+.1f}dB）</td></tr>" if is_loop else "")
        bands = " / ".join(f"{k} {v:+.1f}" for k, v in d["bands"].items())
        h.append(f"""<div class="card"><div class="hd"><b>{name}</b>
<span class="tag">{d['class']}</span><span class="tag">{d['sec']}s</span>
<span class="tag">{'ループ' if is_loop else '一発'}</span>{st}</div>
<img src="data:image/png;base64,{b64}" alt="{name}">
<table>
<tr><td>ラウドネス</td><td>{d['lufs']} LUFS</td></tr>
<tr><td>尖頭（true peak）</td><td>{d['true_peak_db']} dBTP</td></tr>
<tr><td>波高率</td><td>{d['crest_db']} dB</td></tr>
<tr><td>重心</td><td>{d['centroid_hz']} Hz</td></tr>
<tr><td>帯域配分 (dB)</td><td>{bands}</td></tr>
<tr><td>モノ互換</td><td>{d['mono_db']} dB</td></tr>
<tr><td>内蔵スピーカーでの損失</td><td>{d['speaker_db']} dB</td></tr>
<tr><td>鋭さ / 粗さ / 突出</td><td>{d['sharp']} acum / {d['rough']} asper / {d['tonal_db']} dB</td></tr>
<tr><td>発振器の純度</td><td>{d['purity']}（0 = ノイズ / 1 = 純音）</td></tr>{seam}
</table>{('<ul><li>' + '</li><li>'.join(bad) + '</li></ul>') if bad else ''}</div>""")
    h.append("</div>")
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(h))
    print(f"\n{len(names)} 本 / NG {fails} 本 → {out}")
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())
