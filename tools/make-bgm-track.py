# -*- coding: utf-8 -*-
"""もらった曲から**劇伴のトラック**を切り出して、卓の `audio/` へ置く。

```
py -3.11 tools/make-bgm-track.py            # 表のとおりに焼く
py -3.11 tools/make-bgm-track.py --list     # 何をどこから切るか
```

**`ingest-sounds.py` とは出口が違う。** 向こうは一撃と敷く音で、出口は
`Assets/Resources/Sound/`（APK へ焼き込まれる）。こちらは**劇伴**で、出口は
`tools/web-compositor/audio/` ＝ 卓が `/audio/<名前>` で配る場所。実機は
`BgmDirector` が URL で取りに行く（`show.json` の `bgmTracks[]`）。

⚠ **`tools/web-compositor/audio/` は git 管理外**（`.gitignore` 202 行）。だから
**この表がトラックの出どころの唯一の記録**になる。元 mp3 が消えたら作り直せない。

やること 3 つだけ（`rules/sound-design.md` §4.5 — もらった音を良くしようとしない）:

1. **切り出す**（開始位置と長さ）
2. **48kHz へ揃える**（実機の音声機構が 48kHz。44.1kHz は実行時にリサンプルされる — §2）
3. **音量を測る**（揃えるのは show.json の `bgmTracks[].volume` 側。**波形は触らない**）

⚠ イコライザ・圧縮・残響は掛けない。尖頭が 0dBTP に近い素材だけ、
   復号でのはみ出しを避けるぶん（`headroom_db`）だけ下げる。
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
OUT = os.path.join(ROOT, "tools", "web-compositor", "audio")
DOWNLOADS = os.path.join(os.path.expanduser("~"), "Downloads")

# 復号でのはみ出しを避けるために下げる量（dB）。0 なら触らない。
HEADROOM_DB = 1.0

# (元ファイル名, 出力名, 開始秒, 長さ秒, 出どころと使い先)
#
# ⚠ **開始位置はユーザー指定**。長さと出力形式はシュビーの判断
#    （`canon/LEDGER.md` 0119 の「⚠ 実装値」を参照）。
TRACKS = [
    (
        "lost-place-atmospheres-vol-2-by-ende-dot-app.mp3",
        "LostPlace2.mp3",
        126.0,   # 2:06 — ユーザー指定「2:06あたりから」
        150.0,   # 2:06 〜 4:36。追いつきから終幕までを 1 周も回さずに賄う長さ
        "人形の呼びかけ「あーそぼー」の直後から終幕まで（`canon/LEDGER.md` 0119）。"
        "Sascha Ende『Lost Place Atmospheres Vol. 2』/ **CC BY 4.0**（表示が要る）",
    ),
]


def ffmpeg_exe() -> str:
    try:
        import imageio_ffmpeg
        return imageio_ffmpeg.get_ffmpeg_exe()
    except Exception as e:  # pragma: no cover - 環境依存
        raise SystemExit(f"ffmpeg が見つかりません（imageio_ffmpeg）: {e}")


def decode(ff: str, path: str, start: float | None = None, dur: float | None = None) -> np.ndarray:
    """48kHz ステレオの float32 で読む（-ss は -i の前 ＝ 速い探索）。"""
    cmd = [ff, "-v", "error"]
    if start is not None:
        cmd += ["-ss", f"{start:.3f}"]
    cmd += ["-i", path]
    if dur is not None:
        cmd += ["-t", f"{dur:.3f}"]
    cmd += ["-ac", "2", "-ar", str(sk.SR), "-f", "f32le", "-"]
    raw = subprocess.run(cmd, capture_output=True, check=True).stdout
    return np.frombuffer(raw, dtype="<f4").reshape(-1, 2).astype(np.float64)


def encode(ff: str, y: np.ndarray, path: str) -> None:
    """mp3（192kbps・48kHz ステレオ）で書く。"""
    raw = y.astype("<f4").tobytes()
    cmd = [ff, "-v", "error", "-y",
           "-f", "f32le", "-ar", str(sk.SR), "-ac", "2", "-i", "-",
           "-c:a", "libmp3lame", "-b:a", "192k", path]
    subprocess.run(cmd, input=raw, check=True)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--list", action="store_true", help="表を出すだけ")
    ap.add_argument("--only", default="", help="この出力名だけ焼く")
    args = ap.parse_args()

    if args.list:
        for src, out, start, dur, why in TRACKS:
            print(f"{out:<20} ← {src}")
            print(f"    {start:.1f}s から {dur:.1f}s（{int(start)//60}:{start%60:04.1f}"
                  f" 〜 {int(start+dur)//60}:{(start+dur)%60:04.1f}）")
            print(f"    {why}")
        return 0

    ff = ffmpeg_exe()
    os.makedirs(OUT, exist_ok=True)
    made = 0
    for src, out, start, dur, why in TRACKS:
        if args.only and args.only != out:
            continue
        src_path = src if os.path.isabs(src) else os.path.join(DOWNLOADS, src)
        if not os.path.exists(src_path):
            print(f"⚠ 元が無い: {src_path} — {out} は飛ばす")
            continue
        y = decode(ff, src_path, start, dur)
        if len(y) == 0:
            print(f"⚠ 切り出しが空: {out}")
            continue
        tp = sk.true_peak_db(y)
        # 復号でのはみ出しを避けるぶんだけ下げる（波形の形は変えない）。
        if HEADROOM_DB > 0 and tp > -HEADROOM_DB:
            y = y * (10 ** ((-HEADROOM_DB - tp) / 20.0))
        dst = os.path.join(OUT, out)
        encode(ff, y, dst)
        back = decode(ff, dst)
        print(f"{out}: {len(back)/sk.SR:.2f}s  {sk.lufs(back):+.2f} LUFS  "
              f"{sk.true_peak_db(back):+.2f} dBTP  {os.path.getsize(dst)/1e6:.2f} MB")
        print(f"    {why}")
        made += 1
    if made == 0:
        print("焼いたものは無い")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
