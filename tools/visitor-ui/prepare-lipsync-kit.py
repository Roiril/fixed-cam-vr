"""博士の口の動画（DomoAI）へ入れる画像と声をまとめる。いまの声（v4）用。

    py -3.11 tools/visitor-ui/prepare-lipsync-kit.py [--output output/visitor-lipsync-kit-v4]

声は場面ごとの `*-ja-v4.mp3`（アプリが流すものと同じ）。動画生成の長さは 10 秒か 20 秒なので、
長い場面は文の境で分ける。切る位置は `briefing-v1.json` の文の長さから出した境を、
いちばん近い無音の真ん中へ寄せる（言葉を切らない）。既にある出力は上書きしない。
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
ASSETS = ROOT / "tablet/app/src/main/assets/web/asset"
DEFAULT_OUTPUT = ROOT / "output/visitor-lipsync-kit-v4"
IMAGE = HERE / "doctor-centered-v1.png"
IMAGE_SHA256 = "E3688256D851F9B94DD140ADC50E378447EB6AA1452AF4A42FEDE09D8D92CA1B"
# 場面ごとの分け方（文の番号。0 始まり）。1 本は 20 秒に収める
GROUPS = {
    "introduction": [[0, 1, 2, 3]],
    "subject": [[0, 1], [2, 3], [4, 5]],
    "report": [[0, 1, 2], [3, 4]],
    "wear": [[0, 1, 2, 3]],
}
LIMIT_MS = 19600         # 20 秒の枠に入れる上限
SHORT_MS = 9600          # これ以下なら 10 秒の枠


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def duration_ms(path: Path) -> int:
    r = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "json", str(path)],
                       check=True, capture_output=True, text=True, encoding="utf-8")
    return round(float(json.loads(r.stdout)["format"]["duration"]) * 1000)


def silences(path: Path) -> list[tuple[float, float]]:
    r = subprocess.run(["ffmpeg", "-hide_banner", "-i", str(path), "-af", "silencedetect=noise=-38dB:d=0.16", "-f", "null", "-"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    starts = [float(v) for v in re.findall(r"silence_start: ([0-9.]+)", r.stderr)]
    ends = [float(v) for v in re.findall(r"silence_end: ([0-9.]+)", r.stderr)]
    return list(zip(starts, ends))


def cut(source: Path, target: Path, start_ms: int, end_ms: int) -> None:
    filt = f"atrim=start={start_ms / 1000:.3f}:end={end_ms / 1000:.3f},asetpts=PTS-STARTPTS"
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-i", str(source), "-af", filt,
                    "-ac", "1", "-ar", "48000", "-codec:a", "libmp3lame", "-b:a", "160k", str(target)], check=True)
    if abs(duration_ms(target) - (end_ms - start_ms)) > 60:
        raise ValueError(f"長さが合わない: {target}")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = ap.parse_args()
    out = args.output if args.output.is_absolute() else ROOT / args.output
    if out.exists() and any(out.iterdir()):
        raise SystemExit(f"既にある出力は上書きしない: {out}")
    if sha256(IMAGE) != IMAGE_SHA256:
        raise SystemExit("博士の画像が変わっている（口の動画と部屋の絵の顔が合わなくなる）")
    briefing = json.loads((ASSETS / "briefing-v1.json").read_text(encoding="utf-8"))
    out.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(IMAGE, out / IMAGE.name)

    rows = []
    n = 0
    for scene in briefing["scenes"]:
        sid = scene["id"]
        audio = ASSETS / scene["audio"]["ja"]
        total = duration_ms(audio)
        cues = scene["cues"]["ja"]
        scale = total / sum(c["durationMs"] for c in cues)
        bounds = [0.0]
        for c in cues:
            bounds.append(bounds[-1] + c["durationMs"] * scale)
        quiet = silences(audio)

        def snap(ms: float) -> int:
            if ms <= 0:
                return 0
            if ms >= total - 1:
                return total
            mids = [((a + b) / 2 * 1000, (b - a) * 1000) for a, b in quiet]
            near = min(mids, key=lambda m: abs(m[0] - ms))
            if abs(near[0] - ms) > 500:
                raise SystemExit(f"{sid}: {ms:.0f}ms の近くに無音が無い（いちばん近いのは {near[0]:.0f}ms）")
            return round(near[0])

        groups = GROUPS[sid]
        if sorted(i for g in groups for i in g) != list(range(len(cues))):
            raise SystemExit(f"{sid}: 分け方が文の数と合わない（文は {len(cues)} 本）")
        for g in groups:
            n += 1
            start = snap(bounds[g[0]])
            end = snap(bounds[g[-1] + 1])
            length = end - start
            if length > LIMIT_MS:
                raise SystemExit(f"{sid} {g}: {length}ms は 20 秒の枠に入らない")
            name = f"{n:02d}_{sid}_{g[0] + 1}-{g[-1] + 1}.mp3"
            if len(groups) == 1:
                shutil.copyfile(audio, out / name)      # 分けない場面は、アプリが流す声そのもの
            else:
                cut(audio, out / name, start, end)
            rows.append({
                "no": n, "file": name, "scene": sid, "cues": [g[0] + 1, g[-1] + 1],
                "startMs": start, "endMs": end, "durationMs": duration_ms(out / name),
                "videoLength": "10s" if length <= SHORT_MS else "20s",
                "text": "".join(cues[i]["text"] for i in g),
                "sourceSha256": sha256(audio),
            })

    (out / "clips.json").write_text(json.dumps({"image": IMAGE.name, "imageSha256": IMAGE_SHA256, "clips": rows}, ensure_ascii=False, indent=2), encoding="utf-8")
    shutil.copyfile(HERE / "lipsync-kit.md", out / "README.md")
    files = sorted(p for p in out.iterdir() if p.is_file())
    with ZipFile(out / "doctor-lipsync-input.zip", "w", compression=ZIP_DEFLATED) as z:
        for p in files:
            z.write(p, arcname=p.name)
    for r in rows:
        print(f"{r['no']}  {r['file']:32s} {r['durationMs'] / 1000:6.2f} 秒  枠 {r['videoLength']}  {r['text'][:28]}…")
    print(f"出力: {out}")


if __name__ == "__main__":
    main()
