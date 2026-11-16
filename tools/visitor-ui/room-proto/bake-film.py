"""試作の部屋（口の動画入り）を、タブレットが流す 1 本の動画に焼く。

    py -3.11 tools/visitor-ui/room-proto/bake-film.py [--frames film] [--fps 30] [--version 1]
    py -3.11 tools/visitor-ui/room-proto/bake-film.py --lang en --version 3      # 英仏の吹き替えの版

1. 試作のページ（`?w=1280`）のコンソールで `await RoomProto.postMeta()` と
   `await RoomProto.export('film', 30, 0, RoomProto.END_AT + 400)`（声の終わりまで。終わりの砂嵐はアプリが出す）
2. これを回す → `asset/doctor-room-ja-v<版>.mp4` と場面ごとの最初のコマ `asset/doctor-room-<場面>-v<版>.jpg`、
   `briefing-v1.json` の各場面の `film.ja`（file・poster・fromMs・voiceMs）

字幕は焼かない（アプリが出す）。音は声と効果音（`layers/stem.mp3`）を混ぜる。

英仏（`--lang en|fr`）は吹き替えの体で焼く。口は日本語の声のまま動き、テレビの中の札だけがその言語になる
（試作のページを `?w=1280&text=en` で開いて `export('film-en', ...)`）。ここで焼くのは効果音だけの版で、
吹き替えの声は `tools/visitor-ui/generate-doctor-dub.py` が次の版に足す。
`film.<言語>` には、その動画の口の時刻で切った文の窓 `windowsMs` を書く（アプリは字幕をこの窓で出す）。
アプリは場面ごとに `fromMs`（その場面の分の頭。前の場面の声の終わり）から流し、文の窓は `voiceMs` に文の頭を足した位置で切る。
探す位置へすぐ飛べるよう、キーフレームは 0.5 秒ごと。
"""
from __future__ import annotations

import argparse
import io
import json
import os
import subprocess
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
HERE = Path(__file__).resolve().parent
ASSET = HERE.parents[2] / "tablet/app/src/main/assets/web/asset"
BRIEFING = ASSET / "briefing-v1.json"


def duration(path: Path) -> float:
    r = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "json", str(path)],
                       capture_output=True, text=True, check=True)
    return float(json.loads(r.stdout)["format"]["duration"])


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lang", choices=("ja", "en", "fr"), default="ja",
                    help="ja は声と効果音。en・fr は吹き替えの体（口は日本語のまま・札だけその言語・効果音だけ。声は generate-doctor-dub.py が足す）")
    ap.add_argument("--frames", default=None, help="既定は ja が film、ほかは film-<言語>")
    ap.add_argument("--fps", type=int, default=30)
    ap.add_argument("--version", type=int, default=1)
    ap.add_argument("--crf", type=int, default=24, help="画質。20 で 89MB・24 で約 40MB（粒の見え方は 24 まで保つ）")
    args = ap.parse_args()

    dub = args.lang != "ja"
    frames = HERE / "frames" / (args.frames or ("film" if not dub else f"film-{args.lang}"))
    meta = json.loads((HERE / "frames/meta/f00000.jpg").read_text(encoding="utf-8"))
    n = len(list(frames.glob("f*.jpg")))
    want = round((meta["end"] + 400) / 1000 * args.fps)
    if abs(n - want) > 2:
        sys.exit(f"コマの数が合わない: {n} 枚（{want} 枚のはず。export の to を END_AT + 400 にする）")
    length = n / args.fps

    briefing = json.loads(BRIEFING.read_text(encoding="utf-8"))
    starts: dict[str, int] = {}
    for c in meta["cues"]:
        starts.setdefault(c["scene"], c["s"])
    film_name = f"doctor-room-{args.lang}-v{args.version}.mp4"
    out = ASSET / film_name
    cmd = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-framerate", str(args.fps), "-i", "f%05d.jpg"]
    if dub:
        # 効果音だけを、日本語の版と同じ大きさで敷く（吹き替えの声は generate-doctor-dub.py が次の版に足す）
        cmd += ["-i", str(HERE / "layers" / "stem.mp3")]
        graph = f"[1:a]atrim=end={length:.3f},apad=whole_dur={length:.3f}[a]"
    else:
        mix = []
        for k, scene in enumerate(briefing["scenes"], start=1):
            cmd += ["-i", str(ASSET / scene["audio"]["ja"])]
            mix.append(f"[{k}:a]adelay={starts[scene['id']]}:all=1[a{k}]")
        count = len(briefing["scenes"])
        labels = "".join(f"[a{k}]" for k in range(1, count + 1))
        cmd += ["-i", str(HERE / "layers" / "stem.mp3")]
        labels += f"[{count + 1}:a]"
        graph = ";".join(mix) + f";{labels}amix=inputs={count + 1}:normalize=0,atrim=end={length:.3f}[a]"
    cmd += ["-filter_complex", graph, "-map", "0:v", "-map", "[a]",
            "-c:v", "libx264", "-profile:v", "high", "-level", "4.1", "-crf", str(args.crf), "-preset", "slow",
            "-g", str(args.fps), "-keyint_min", str(args.fps), "-sc_threshold", "0", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-t", f"{length:.3f}", "-movflags", "+faststart", str(out)]
    if subprocess.run(cmd, cwd=frames).returncode != 0:
        sys.exit("書き出しに失敗")

    prev_end = 0
    for scene in briefing["scenes"]:
        sid = scene["id"]
        voice = starts[sid]
        poster = f"doctor-room-{sid}-{args.lang}-v{args.version}.jpg" if dub else f"doctor-room-{sid}-v{args.version}.jpg"
        frame_no = min(n - 1, round(prev_end / 1000 * args.fps))
        subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(frames / f"f{frame_no:05d}.jpg"),
                        "-q:v", "3", str(ASSET / poster)], check=True)
        entry = {"file": film_name, "poster": poster, "fromMs": int(prev_end), "voiceMs": int(voice)}
        if dub:
            # 口が動く窓（試作の時刻表の文の頭と終わり）。字幕はこの窓で出す
            windows = [round(c["e"] - c["s"]) for c in sorted((c for c in meta["cues"] if c["scene"] == sid),
                                                                 key=lambda c: c["i"])]
            if len(windows) != len(scene["cues"][args.lang]):
                sys.exit(f"{sid}: 口の窓 {len(windows)} 個と {args.lang} の文 {len(scene['cues'][args.lang])} 個が合わない")
            entry["windowsMs"] = windows
        # ほかの言語の版は残す
        scene.setdefault("film", {})[args.lang] = entry
        prev_end = voice + round(duration(ASSET / scene["audio"]["ja"]) * 1000)
    tmp = BRIEFING.with_suffix(".json.tmp")
    with io.open(tmp, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(briefing, ensure_ascii=False, indent=2) + "\n")
    json.loads(tmp.read_text(encoding="utf-8"))
    os.replace(tmp, BRIEFING)
    print(f"{out}  {out.stat().st_size / 1e6:.1f}MB  {n} コマ・{length:.2f} 秒")
    for scene in briefing["scenes"]:
        print(f"  {scene['id']}: {scene['film'][args.lang]}")


if __name__ == "__main__":
    main()
