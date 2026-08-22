#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
動画を **Quest で再生できる mp4(H.264)** へ変換する（`canon/LEDGER.md` 0102 の当日素材）。

⚠⚠ **Quest へ配る動画は mp4 でなければならない。** Unity の VideoPlayer が Android で
  VP9(webm) を再生できるかは端末依存で、**当日に「動画のカットだけ出ない」で詰む**
  （いま動いている cue はすべて mp4）。卓の ⏺ 録画は保存の瞬間に自動変換するので、
  この道具が要るのは**スマホや外から持ち込んだ動画**だけ。

使い方:
    py -3.11 tools/web-compositor/to-mp4.py <ファイル or フォルダ> [...]

  - 変換後は元のファイルの隣へ `<名前>.mp4` を置く（元は消さない — 持ち込み素材は撮り直せない）
  - 既に mp4 なら**中身を確かめて**、H.264 / yuv420p ならそのまま通す（無駄な再圧縮をしない）
"""
from __future__ import annotations

import os
import shutil
import subprocess
import sys

VIDEO_EXT = {".webm", ".mov", ".avi", ".mkv", ".m4v", ".mp4", ".3gp"}


def ffmpeg_exe() -> str | None:
    """ffmpeg の実行ファイル。PATH に無ければ imageio-ffmpeg の同梱版へ落ちる。"""
    exe = shutil.which("ffmpeg")
    if exe:
        return exe
    try:
        import imageio_ffmpeg
        return imageio_ffmpeg.get_ffmpeg_exe()
    except Exception:
        return None


def probe(exe: str, path: str) -> str:
    """ffmpeg の -i の stderr（コーデックと画素形式を読むため）。"""
    r = subprocess.run([exe, "-i", path], capture_output=True)
    return (r.stderr or b"").decode("utf-8", "replace")


def already_ok(info: str) -> bool:
    """H.264 かつ yuv420p なら再圧縮しない（劣化を重ねない）。"""
    return "Video: h264" in info and "yuv420p" in info


def convert(exe: str, src: str) -> tuple[bool, str]:
    base, ext = os.path.splitext(src)
    if ext.lower() == ".mp4":
        if already_ok(probe(exe, src)):
            return True, "そのまま使える（H.264 / yuv420p）"
        dst = base + "_h264.mp4"
    else:
        dst = base + ".mp4"

    # ⚠ `-pix_fmt yuv420p` は必須。スマホや MediaRecorder の出力は yuv444 のことがあり、
    #   Android の MediaCodec がそれを開けない（画が出ないのに警告も出ない）。
    cmd = [exe, "-y", "-loglevel", "error", "-i", src,
           "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
           "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-an", dst]
    try:
        r = subprocess.run(cmd, capture_output=True, timeout=900)
    except Exception as e:
        return False, f"変換に失敗: {e}"
    if r.returncode != 0 or not os.path.exists(dst) or os.path.getsize(dst) == 0:
        err = (r.stderr or b"").decode("utf-8", "replace").strip().splitlines()
        return False, "変換に失敗: " + (err[-1] if err else f"exit {r.returncode}")
    return True, f"→ {os.path.basename(dst)}（{os.path.getsize(dst) // 1024} KB）"


def collect(targets: list[str]) -> list[str]:
    out: list[str] = []
    for t in targets:
        if os.path.isdir(t):
            for name in sorted(os.listdir(t)):
                if os.path.splitext(name)[1].lower() in VIDEO_EXT:
                    out.append(os.path.join(t, name))
        elif os.path.isfile(t):
            out.append(t)
        else:
            print(f"  ⚠ 見つからない: {t}")
    return out


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    exe = ffmpeg_exe()
    if not exe:
        print("ffmpeg が無い。`py -3.11 -m pip install imageio-ffmpeg` で入る")
        return 1

    files = collect(sys.argv[1:])
    if not files:
        print("変換するものが無い")
        return 1
    bad = 0
    for f in files:
        ok, note = convert(exe, f)
        print(f"  {'✓' if ok else '✗'} {os.path.basename(f)}  {note}")
        bad += 0 if ok else 1
    print(f"\n{len(files) - bad}/{len(files)} 本")
    return 1 if bad else 0


if __name__ == "__main__":
    raise SystemExit(main())
