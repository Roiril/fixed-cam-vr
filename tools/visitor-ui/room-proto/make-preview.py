"""試作ページが書き出したコマに、博士の声と字幕を合わせて 1 本の mp4 にする。

    py -3.11 tools/visitor-ui/room-proto/make-preview.py <出力.mp4> [--frames full] [--fps 30]

コマは serve.cjs の POST /_frame が frames/<名前>/ に置いたもの（RoomProto.export）。
文の時刻は frames/meta/f00000.jpg（中身は JSON。同じ口で受け取った）から読む。
出力はローカルに書く（Drive へ直接書かせない。faststart が書いた後で中身を並べ替えるため）。
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ASSET = HERE.parents[2] / "tablet/app/src/main/assets/web/asset"


def ass_time(ms: float) -> str:
    cs = int(round(ms / 10))
    return f"{cs // 360000}:{cs // 6000 % 60:02d}:{cs // 100 % 60:02d}.{cs % 100:02d}"


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--frames", default="full")
    ap.add_argument("--fps", type=int, default=30)
    ap.add_argument("--no-sfx", action="store_true", help="効果音を入れない（声だけ）")
    args = ap.parse_args()

    frames = HERE / "frames" / args.frames
    meta = json.loads((HERE / "frames/meta/f00000.jpg").read_text(encoding="utf-8"))
    n = len(list(frames.glob("f*.jpg")))
    if n == 0:
        sys.exit(f"コマが無い: {frames}")
    want = round(meta["total"] / 1000 * args.fps)
    if abs(n - want) > 2:
        sys.exit(f"コマの数が合わない: {n} 枚（{want} 枚のはず）")

    lines = [
        "[Script Info]", "ScriptType: v4.00+", "PlayResX: 1280", "PlayResY: 800", "WrapStyle: 0", "",
        "[V4+ Styles]",
        "Format: Name, Fontname, Fontsize, PrimaryColour, OutlineColour, BackColour, Bold, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV",
        "Style: s,Yu Gothic UI,30,&H00DFECF2,&H00000000,&H96000000,0,1,1.2,2.5,2,110,110,58", "",
        "[Events]", "Format: Layer, Start, End, Style, Text",
    ]
    for c in meta["cues"]:
        lines.append(f"Dialogue: 0,{ass_time(c['s'])},{ass_time(min(c['e'] + 250, meta['end'] + 500))},s,{c['t']}")
    (frames / "sub.ass").write_text("\n".join(lines) + "\n", encoding="utf-8")

    briefing = json.loads((ASSET / "briefing-v1.json").read_text(encoding="utf-8"))
    voice = {s["id"]: s["audio"]["ja"] for s in briefing["scenes"]}     # 場面の声のファイル名（作り直すと版の番号が変わる）
    starts: dict[str, int] = {}
    for c in meta["cues"]:
        starts.setdefault(c["scene"], c["s"])
    cmd = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-framerate", str(args.fps), "-i", "f%05d.jpg"]
    mix = []
    for k, (scene, ms) in enumerate(starts.items(), start=1):
        cmd += ["-i", str(ASSET / voice[scene])]
        mix.append(f"[{k}:a]adelay={ms}:all=1[a{k}]")
    labels = "".join(f"[a{k}]" for k in range(1, len(starts) + 1))
    count = len(starts)
    stem = HERE / "layers" / "stem.mp3"          # 効果音（make-room-sound.py mix）
    if stem.exists() and not args.no_sfx:
        cmd += ["-i", str(stem)]
        labels += f"[{count + 1}:a]"
        count += 1
    graph = ";".join(mix) + f";{labels}amix=inputs={count}:normalize=0[a];[0:v]subtitles=sub.ass[v]"
    out = Path(args.out).resolve()
    out.parent.mkdir(parents=True, exist_ok=True)
    cmd += ["-filter_complex", graph, "-map", "[v]", "-map", "[a]", "-c:v", "libx264", "-crf", "19", "-preset", "medium",
            "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-t", f"{meta['total'] / 1000:.3f}", "-movflags", "+faststart", str(out)]
    r = subprocess.run(cmd, cwd=frames)
    if r.returncode != 0:
        sys.exit(r.returncode)
    print(f"{out}  {out.stat().st_size / 1e6:.1f}MB  {n} コマ")


if __name__ == "__main__":
    main()
