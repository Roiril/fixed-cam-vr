"""博士の日本語の声を ElevenLabs で 1 本に合成し、場面ごとに切ってタブレットへ組み込む。

    py -3.11 tools/visitor-ui/generate-doctor-audio.py --voice <voice_id>            # 合成して組み込む
    py -3.11 tools/visitor-ui/generate-doctor-audio.py --voice <voice_id> --reuse    # 前に合成した音から切り直すだけ

入力は `elevenlabs-input-ja-v3.md` のコードブロック（0257: 全部の台詞を一度に入れる）。
鍵は環境変数 `ELEVENLABS_API_KEY`（値は表示しない。`~/.claude/rules/secrets.md`）。

することは 4 つ。
1. `/with-timestamps` で合成する。音と一緒に、1 字ごとの時刻が返る
2. 各文の頭の語を時刻の列から探し、文と文の間の無音の真ん中で区切る
3. 場面ごとに `tablet/.../asset/<場面>-ja-v3.mp3` へ切り出す
4. `briefing-v1.json` の日本語の `durationMs` を実測へ直し、`audio.ja` を付ける

⚠ 口の動画（リップシンク）は作らない。動画が届くまで、日本語は博士の静止画と声で進む。
⚠ 合成した元の音と時刻は `tools/visitor-ui/doctor-ja-v3-original.{mp3,json}` に残す。声を替えたら作り直す。
"""
import argparse
import base64
import io
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
ASSETS = ROOT / "tablet/app/src/main/assets/web/asset"
BRIEFING = ASSETS / "briefing-v1.json"
INPUT = HERE / "elevenlabs-input-ja-v3.md"
TAKE_MP3 = HERE / "doctor-ja-v3-original.mp3"
TAKE_JSON = HERE / "doctor-ja-v3-original.json"

# 各文の頭（入力原稿の表記）。場面と文の順は briefing-v1.json と同じ。
ANCHORS = {
    "introduction": ["はじめまして。", "あなたに、ある調査を"],
    "subject": ["こちらの、カーテン", "観測装置をつけ", "その間は、右手で", "異変を見逃さないよう"],
    "report": ["これが、怪異を記録し", "怪異を、直接見ると", "異変に気づいたら", "装置のエージェントが"],
    "wear": ["では。", "健闘を、祈ります"],
}


def api_key():
    key = os.environ.get("ELEVENLABS_API_KEY")
    if not key and os.name == "nt":
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as k:
            key = winreg.QueryValueEx(k, "ELEVENLABS_API_KEY")[0]
    if not key:
        raise SystemExit("ELEVENLABS_API_KEY が無い")
    return key


def prompt_text():
    blocks = re.findall(r"```text\r?\n(.*?)\r?\n```", INPUT.read_text(encoding="utf-8"), re.S)
    if len(blocks) != 1:
        raise SystemExit(f"{INPUT.name} のコードブロックが 1 つではない")
    return blocks[0]


def synthesize(voice, model):
    body = json.dumps({"text": prompt_text(), "model_id": model}).encode("utf-8")
    req = urllib.request.Request(
        f"https://api.elevenlabs.io/v1/text-to-speech/{voice}/with-timestamps?output_format=mp3_44100_128",
        data=body, headers={"xi-api-key": api_key(), "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=300) as res:
            data = json.loads(res.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        raise SystemExit(f"合成に失敗: HTTP {e.code} {e.read().decode('utf-8', 'replace')[:400]}")
    TAKE_MP3.write_bytes(base64.b64decode(data["audio_base64"]))
    align = data.get("alignment") or data.get("normalized_alignment")
    TAKE_JSON.write_text(json.dumps({"voice": voice, "model": model, "alignment": align}, ensure_ascii=False),
                         encoding="utf-8", newline="\n")


def duration_ms(path):
    out = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "json", str(path)],
                         check=True, capture_output=True, text=True).stdout
    return round(float(json.loads(out)["format"]["duration"]) * 1000)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--voice", required=True)
    ap.add_argument("--model", default="eleven_v3")
    ap.add_argument("--reuse", action="store_true", help="合成せず、残してある音から切り直す")
    args = ap.parse_args()

    if not args.reuse:
        synthesize(args.voice, args.model)
    take = json.loads(TAKE_JSON.read_text(encoding="utf-8"))
    al = take["alignment"]
    chars = "".join(al["characters"])
    starts, ends = al["character_start_times_seconds"], al["character_end_times_seconds"]
    total_ms = duration_ms(TAKE_MP3)

    # 各文の頭の時刻と、その直前の字の終わり。区切りは無音の真ん中。
    cue_bounds, cursor = [], 0
    for scene_id, anchors in ANCHORS.items():
        for anchor in anchors:
            i = chars.find(anchor, cursor)
            if i < 0:
                raise SystemExit(f"時刻の列に「{anchor}」が無い（入力原稿と ANCHORS を揃える）")
            j = i - 1
            while j >= 0 and (chars[j].isspace() or ends[j] <= starts[j]):
                j -= 1
            prev_end = ends[j] if j >= 0 else 0.0
            cue_bounds.append((scene_id, max(0.0, (prev_end + starts[i]) / 2.0) if j >= 0 else 0.0))
            cursor = i + len(anchor)
    cut_ms = [round(t * 1000) for _, t in cue_bounds] + [total_ms]

    raw = io.open(BRIEFING, encoding="utf-8", newline="").read()
    nl = "\r\n" if "\r\n" in raw else "\n"
    data = json.loads(raw)
    report, k = {}, 0
    for scene in data["scenes"]:
        n = len(ANCHORS[scene["id"]])
        if len(scene["cues"]["ja"]) != n:
            raise SystemExit(f"{scene['id']}: 文の数が違う（JSON {len(scene['cues']['ja'])} / ANCHORS {n}）")
        start, end = cut_ms[k], cut_ms[k + n]
        target = ASSETS / f"{scene['id']}-ja-v3.mp3"
        tmp = target.with_name(target.name + ".tmp")
        subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(TAKE_MP3),
                        "-af", f"atrim=start={start / 1000:.3f}:end={end / 1000:.3f},asetpts=PTS-STARTPTS",
                        "-ac", "1", "-ar", "48000", "-codec:a", "libmp3lame", "-b:a", "128k", "-f", "mp3", str(tmp)],
                       check=True)
        tmp.replace(target)
        for c in range(n):
            scene["cues"]["ja"][c]["durationMs"] = cut_ms[k + c + 1] - cut_ms[k + c]
        scene.pop("video", None)
        scene["audio"] = {**{a: b for a, b in (scene.get("audio") or {}).items() if a != "ja"}, "ja": target.name}
        report[scene["id"]] = {"file": target.name, "ms": duration_ms(target),
                               "cues": [c["durationMs"] for c in scene["cues"]["ja"]]}
        k += n
    out = (json.dumps(data, ensure_ascii=False, indent=2) + "\n").replace("\n", nl)
    tmp = BRIEFING.with_name(BRIEFING.name + ".tmp")
    io.open(tmp, "w", encoding="utf-8", newline="").write(out)
    json.loads(tmp.read_text(encoding="utf-8"))
    os.replace(tmp, BRIEFING)
    print(json.dumps({"voice": take["voice"], "model": take["model"], "totalMs": total_ms, "scenes": report},
                     ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
