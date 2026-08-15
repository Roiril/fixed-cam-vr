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
    # ⚠⚠ **2026-08-15 に音源を差し替えた**（`canon/LEDGER.md` 0047・ユーザー指定）。
    #    旧: `シネマチックなタイトル.mp3`（2026-08-12 指定・11.9 秒）。復号済みは
    #    `logs/sound/ingest/src_title_cine.wav` に残してある（戻すならここのパスを差し替える）。
    ("Sonniss.com-GDC2026-GameAudioBundle1of5/344 Audio - Bass Drops & Downers Vol. 3/"
     "DSGNBass_Tone Downer (Reverb)_344 Audio_Bass Drops and Downers Vol 3.wav",
     "sfx_title_in", "lufs", -17.0,
     "タイトル出現（A を押した瞬間）。**尾は切らない** — 題字が消えた後も鳴り続けて "
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
    # ⚠⚠ **-19.0 → -17.0**（2026-08-13・`canon/LEDGER.md` 0030「何の音もなしに出るのは違和感がある」）。
    #    旧値は「導入の山（破砕）を超えないように」抑えたものだが、**破砕は同日に廃止**したので
    #    その制約はもう無い。いまの山はここなので、`make-sounds.py` の `EVENT`（-17.0）へ揃える。
    # ⚠⚠ **-17.0 では「鳴っていない」と判定された**（2026-08-13・LEDGER 0032）。本体が 0.2 秒しか
    #    無く波高 17.6dB なので、素の音量合わせでは -3dBTP の天井に当たって高さが出ない。
    #    `lufs!` は必要なぶんだけ尖頭を丸めて狙いまで持ち上げる。
    ("Cyber03-mp3/Cyber03/Cyber03-2.mp3", "sfx_screen_on", "lufs!", -13.0,
     "段 3 — 闇の中で管に電源が入る。**導入の山**。2026-08-13 ユーザー指定の音源"),
]

def norm_lufs_drive(y, target: float, max_drive_db: float = 12.0):
    """ラウドネスを target へ合わせる。**届かなければ尖頭を丸めて届かせる。**

    `make-sounds.py` の `norm_lufs` と同じ探索（丸めは必要なぶんだけ・上限つき）。
    届かなければ「届かなかった」まま返す（黙って歪ませるより数字で足りないと言う方がよい）。
    """
    drive = 0.0
    best = y * (10 ** ((target - sk.lufs(y)) / 20.0))
    while True:
        z = sk.soft_clip(y, drive) if drive > 0 else y
        z = z * (10 ** ((target - sk.lufs(z)) / 20.0))
        tp = sk.true_peak_db(z)
        if tp <= -3.0:
            return z
        best = z * (10 ** ((-3.0 - tp) / 20.0))
        if drive >= max_drive_db:
            return best
        drive = min(max_drive_db, drive + 2.0)


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
        elif how == "lufs!":
            # ⚠⚠ **尖頭を丸めてでも狙いの高さまで持ち上げる**（2026-08-13・`canon/LEDGER.md` 0032）。
            #    素の音量合わせだけでは、**波高の大きい一撃は -3dBTP の天井に当たって上がらない**
            #    （`sfx_screen_on` は本体が 0.2 秒・波高 17.6dB で、-17.0 LUFS のまま
            #     「鳴っていないように聞こえる」とユーザー判定）。
            #    ⚠ これは §4.5「もらった音を良くしようとしない」の例外ではなく**音量合わせの側**。
            #       イコライザも圧縮も掛けず、必要なぶんだけ尖頭を丸めて高さを出す
            #       （合成の音が `make-sounds.norm_lufs` で受けているのと同じ扱い）。
            y = norm_lufs_drive(y, target)
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
