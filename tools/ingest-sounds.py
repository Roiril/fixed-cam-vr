# -*- coding: utf-8 -*-
"""ユーザーが持ってきた音を、この作品の音量体系へ揃えて `Assets/Resources/Sound/` へ入れる。

```
py -3.11 tools/ingest-sounds.py            # 既定の 5 本
py -3.11 tools/ingest-sounds.py --list     # 何をどこへ入れるか
```

**合成した音（`make-sounds.py`）とは別系統。** 向こうは「同じ版なら同じ波形」だが、
こちらは**元の音を持っている前提**なので、元が消えたら作り直せない。だから
`logs/sound/ingest/` に復号済みの生 WAV を残す（git 管理外・作り直しの種）。

やること 4 つ:

1. **復号**（mp3 → 48kHz ステレオ WAV）。`imageio_ffmpeg` が同梱する ffmpeg を使う
2. **端の無音を落とす**（後ろに 0.9 秒の無音がある素材があった）
3. **音量を揃える**（持続はラウドネス / 一撃は尖頭 — `rules/sound-design.md` §3）
4. **端をなめらかに落とす**（頭と尻のクリック止め）

⚠ **元の音を「良くしよう」としない。** 掛けるのは音量と端の処理だけで、
イコライザも圧縮も掛けない。ユーザーが選んだ音を別のものにしない。
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import soundkit as sk  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RAW = os.path.join(ROOT, "logs", "sound", "ingest")
OUT = os.path.join(ROOT, "Assets", "Resources", "Sound")
DOWNLOADS = os.path.join(os.path.expanduser("~"), "Downloads")

# (元ファイル名, 出力名, 揃え方, 目標, 使い先)
#
# ⚠ **「使い先」はシュビーの判断**であって、ユーザーが指定したのは
#    シネマチックなタイトル → タイトル出現 と、金属音のファイル名だけ。
#    残り 3 本の置き場は `canon/OPEN.md` に案として書いてある。
PLAN = [
    # ⚠ 尖頭で揃えると -14.0 LUFS になり、**体験で最初に聞く音がいちばん大きい**ことになる。
    #    「不気味で怖くていいけど、不快にはならないように」（2026-08-12 ユーザー指示）なので、
    #    聴感で揃えて 3dB 引く。何も競合していない場所なので、これでも充分に立つ。
    ("シネマチックなタイトル.mp3", "sfx_title_in", "lufs", -17.0,
     "タイトル出現（A を押した瞬間）。**13 秒の尾は切らない** — 題字が消えた後も鳴り続けて "
     "パススルーへの継ぎ目を音が跨ぐ"),
    # ⚠ 尖頭で揃えると -12.4 LUFS で、**破砕（-14.0）より大きくなる**。
    #    導入の山は段 4 の破砕なので、隔離の音がそれを超えてはいけない。
    ("黒い中に入るときの金属音.mp3", "sfx_seal_close", "lufs", -16.0,
     "段 1 — 隔離が閉じて会場が消える。合成版を置き換える（実物の金属の重さは作れない）"),
    ("軋み.mp3", "amb_creak_1", "lufs", -26.0,
     "家鳴り。段 0 と本編にまばらに置く"),
    ("少し重い軋み.mp3", "amb_creak_2", "lufs", -26.0,
     "同上。2 種を回して同じ音が並ばないようにする"),
    ("鈴２.mp3", "amb_bell", "lufs", -24.0,
     "段 0 に **1 回だけ**。誰も鳴らしていないのに鳴る"),
    # ⚠ 尖頭で揃えない。エネルギーの立ち上がりは尾が長く、尖頭で合わせると聴感が突出する。
    #    タイトル（-17.0）より控えめにして、導入の山（破砕）を超えないようにする。
    ("vadim_makes_sound-sci-fi-energy-shield-activation-566095.mp3", "sfx_screen_on", "lufs", -19.0,
     "スクリーンが立ち上がる（ホログラムのように現れる）。2026-08-13 ユーザー指定の音源"),
]

SILENCE_DB = -60.0     # これより静かな端は落とす
EDGE_FADE = 0.008      # 端のクリック止め（秒）


def decode(src: str, dst: str) -> bool:
    import imageio_ffmpeg
    exe = imageio_ffmpeg.get_ffmpeg_exe()
    r = subprocess.run([exe, "-y", "-v", "error", "-i", src,
                        "-ar", str(sk.SR), "-ac", "2", "-c:a", "pcm_s16le", dst],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0:
        print(f"  復号に失敗: {os.path.basename(src)}\n    {r.stderr.strip()[:200]}")
        return False
    return True


def trim(y: np.ndarray) -> np.ndarray:
    """前後の無音を落とす。**中の無音は触らない**（間も作品のうち）。"""
    m = np.max(np.abs(sk.to_stereo(y)), axis=1)
    thr = 10 ** (SILENCE_DB / 20)
    idx = np.flatnonzero(m > thr)
    if len(idx) == 0:
        return y
    a = max(0, idx[0] - int(0.005 * sk.SR))
    b = min(len(y), idx[-1] + int(0.05 * sk.SR))
    return y[a:b]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--src", default=DOWNLOADS)
    a = ap.parse_args()

    if a.list:
        for jp, name, how, target, why in PLAN:
            print(f"  {name:16s} ← {jp}\n      {how} {target:+.1f} / {why}")
        return 0

    os.makedirs(RAW, exist_ok=True)
    os.makedirs(OUT, exist_ok=True)
    for jp, name, how, target, _why in PLAN:
        src = os.path.join(a.src, jp)
        raw = os.path.join(RAW, f"src_{name}.wav")
        if os.path.exists(src):
            if not decode(src, raw):
                continue
        elif not os.path.exists(raw):
            print(f"  無い: {jp}（{a.src} にも {RAW} にも）")
            continue
        else:
            print(f"  元 mp3 が無いので復号済みを使う: {name}")

        y, sr = sk.read_wav(raw)
        before = sk.describe(y)
        y = sk.to_stereo(trim(y))
        y = sk.env_fade(y, EDGE_FADE, EDGE_FADE)

        if how == "peak":
            tp = sk.true_peak_db(y)
            y = y * 10 ** ((target - tp) / 20.0)
        else:
            y = y * 10 ** ((target - sk.lufs(y)) / 20.0)
            tp = sk.true_peak_db(y)
            if tp > -3.0:
                y = y * 10 ** ((-3.0 - tp) / 20.0)

        sk.write_wav(os.path.join(OUT, f"{name}.wav"), y, peak_db=-3.0)
        d = sk.describe(y)
        print(f"  {name:16s} {before['sec']:5.2f}s → {d['sec']:5.2f}s   "
              f"{before['lufs']:6.1f} → {d['lufs']:6.1f} LUFS   "
              f"tp {before['true_peak_db']:5.1f} → {d['true_peak_db']:5.1f}dB   "
              f"鋭さ {d['sharp']:4.2f} 粗さ {d['rough']:4.2f} 内蔵SP {d['speaker_db']:5.1f}dB")
    print(f"\n→ {OUT}\n次: py -3.11 tools/sound-lint.py  →  .\\tools\\unity.ps1 menu sound-import")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
