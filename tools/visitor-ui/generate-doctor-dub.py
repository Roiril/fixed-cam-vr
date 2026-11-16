"""英仏の博士の吹き替えの声を ElevenLabs で合成し、日本語の口が動く文の窓へ 1 文ずつ置いて、吹き替えの版の動画へ入れる。

    py -3.11 tools/visitor-ui/generate-doctor-dub.py --lang en fr            # 合成から
    py -3.11 tools/visitor-ui/generate-doctor-dub.py --lang en fr --reuse    # 前に合成した音から置き直すだけ

LEDGER 0394 の後のユーザー判定「声は流してok」（2026-11-16）。口は日本語のまま動き、声だけが英仏になる吹き替えの体。

することは 5 つ。
1. 日本語の声と同じ声・同じモデル（`doctor-ja-v5-original.json`）で、その言語の全部の文を 1 本に合成する（`/with-timestamps`）
2. 文ごとの時刻で切る。文の中の長い間は `PHRASE_PAUSE_MAX` まで詰める
3. 文の窓（試作の時刻表の文の頭と終わり。`frames/meta` の `cues`）に収まらない文だけ、音程を変えずに速める（上限 `TEMPO_MAX`）
4. 声の大きさを日本語の声にそろえ、各文を窓の頭へ置いた 1 本の声 `room-proto/layers/voice-<言語>.mp3` を作る
5. 吹き替えの版（`bake-film.py --lang <言語>` が焼いた効果音だけの動画）に声を混ぜ、版を 1 つ上げて書き、
   `briefing-v1.json` の `film.<言語>.file` を差し替える（画は焼き直さず複製する。版は `--base-version` の次）

鍵は環境変数 `ELEVENLABS_API_KEY`（値は表示しない。`~/.claude/rules/secrets.md`）。
合成した元の音と時刻は `tools/visitor-ui/doctor-<言語>-dub-original.{mp3,json}` に残す。台詞を変えたら作り直す。
"""
from __future__ import annotations

import argparse
import base64
import io
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

import numpy as np

sys.stdout.reconfigure(encoding="utf-8")
HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
ASSET = ROOT / "tablet/app/src/main/assets/web/asset"
BRIEFING = ASSET / "briefing-v1.json"
PROTO = HERE / "room-proto"
META = PROTO / "frames/meta/f00000.jpg"

RATE = 44100
PHRASE_PAUSE_MAX = 0.35   # 文の中の間の上限（秒）。「Welcome... to」の間が長すぎると窓に収まらない
QUIET_RMS = 0.01          # 20ms の窓の RMS がこれ未満を無音とみなす
TAIL_GAP_MS = 250         # 窓の終わりに残す無音（次の文と続けて聞こえないように）
TEMPO_MAX = 1.35          # これ以上速めると早口に聞こえる。超えたら窓からはみ出す（止めずに報告）
PAD_HEAD = 0.04
PAD_TAIL = 0.15

# 日本語 v5 の入力原稿（elevenlabs-input-ja-v5.md）の演技指定を、同じ文の位置へ写す（英語の試作 doctor-en-trial-v1 と同じ）
HEAD = "[deep, resonant male voice, calm and authoritative, restrained emotion, measured delivery, cinematic drama performance]"
TAGS = {
    ("introduction", 2): "[serious, quietly commanding]",
    ("subject", 0): "[flat, matter-of-fact, reading from a file]",
    ("subject", 1): "[lower voice, deliberate]",
    ("subject", 2): "[calm, precise]",
    ("subject", 3): "[flat, matter-of-fact, no emotion]",
    ("report", 0): "[calm, authoritative]",
    ("report", 1): "[calm, precise, instructional]",
    ("report", 5): "[serious, deliberate]",
    ("report", 6): "[calm, precise, instructional]",
    ("wear", 2): "[serious, quietly commanding]",
    ("wear", 3): "[lower voice, quiet]",
}
PAUSE_AFTER = {"introduction": "[pause]", "subject": "[pause]", "report": "[short pause]", "wear": "[pause]"}


def api_key() -> str:
    key = os.environ.get("ELEVENLABS_API_KEY", "")
    if not key:
        raise SystemExit("ELEVENLABS_API_KEY が無い（~/.claude/rules/secrets.md）")
    return key


def ja_voice() -> tuple[str, str]:
    d = json.loads((HERE / "doctor-ja-v5-original.json").read_text(encoding="utf-8"))
    return d["voice"], d["model"]


def prompt(briefing: dict, lang: str) -> tuple[str, list[tuple[str, int, str]]]:
    lines = [HEAD, ""]
    order = []
    scenes = briefing["scenes"]
    for si, sc in enumerate(scenes):
        cues = sc["cues"][lang]
        for ci, c in enumerate(cues):
            tag = TAGS.get((sc["id"], ci))
            if tag:
                lines += [tag]
            lines += [c["text"], ""]
            order.append((sc["id"], ci, c["text"]))
            if ci < len(cues) - 1:
                lines += [PAUSE_AFTER[sc["id"]], ""]
        if si < len(scenes) - 1:
            lines += ["[long pause]", ""]
    return "\n".join(lines).strip() + "\n", order


def original_paths(lang: str) -> tuple[Path, Path]:
    return HERE / f"doctor-{lang}-dub-original.mp3", HERE / f"doctor-{lang}-dub-original.json"


def synthesize(text: str, lang: str) -> None:
    voice, model = ja_voice()
    mp3, js = original_paths(lang)
    body = {"text": text, "model_id": model, "language_code": lang}
    req = urllib.request.Request(
        f"https://api.elevenlabs.io/v1/text-to-speech/{voice}/with-timestamps?output_format=mp3_44100_128",
        data=json.dumps(body).encode("utf-8"), headers={"xi-api-key": api_key(), "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=600) as res:
            data = json.loads(res.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        raise SystemExit(f"{lang}: 合成に失敗: HTTP {e.code} {e.read().decode('utf-8', 'replace')[:400]}")
    mp3.write_bytes(base64.b64decode(data["audio_base64"]))
    align = data.get("alignment") or data.get("normalized_alignment")
    js.write_text(json.dumps({"voice": voice, "model": model, "text": text, "alignment": align}, ensure_ascii=False),
                  encoding="utf-8", newline="\n")
    print(f"{lang}: 合成 {mp3.name}")


def decode(path: Path) -> np.ndarray:
    raw = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-i", str(path), "-ac", "1", "-ar", str(RATE),
                          "-f", "s16le", "-"], check=True, capture_output=True).stdout
    return np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0


def frame_rms(x: np.ndarray, win: int = RATE // 50) -> np.ndarray:
    n = len(x) // win
    return np.sqrt((x[:n * win].reshape(n, win) ** 2).mean(axis=1)) if n else np.zeros(0)


def squeeze_pauses(x: np.ndarray) -> np.ndarray:
    """文の中の、PHRASE_PAUSE_MAX より長い無音を、その長さまで詰める（無音の真ん中を抜く）。"""
    win = RATE // 50
    quiet = frame_rms(x, win) < QUIET_RMS
    keep = np.ones(len(x), dtype=bool)
    i = 0
    limit = int(PHRASE_PAUSE_MAX * 50)
    while i < len(quiet):
        if not quiet[i]:
            i += 1
            continue
        j = i
        while j < len(quiet) and quiet[j]:
            j += 1
        # 頭と尻の無音はここでは触らない（窓の頭へ置くときの位置がずれる）
        if i > 0 and j < len(quiet) and j - i > limit:
            cut = (j - i) - limit
            mid = (i + j) // 2
            a = (mid - cut // 2) * win
            keep[a:a + cut * win] = False
        i = j
    return x[keep]


def voiced_rms(x: np.ndarray) -> float:
    """声のある 20ms の窓の大きさの 90 パーセンタイル。平均だと息や語尾の小さな窓に引っぱられ、
    英仏が日本語より 3dB 小さく聞こえた（2026-11-16 実測）。"""
    r = frame_rms(x)
    v = r[r >= QUIET_RMS]
    return float(np.percentile(v, 90)) if len(v) else 0.0


def atempo(x: np.ndarray, tempo: float) -> np.ndarray:
    raw = (np.clip(x, -1, 1) * 32767).astype(np.int16).tobytes()
    out = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "s16le", "-ar", str(RATE), "-ac", "1",
                          "-i", "-", "-filter:a", f"atempo={tempo:.4f}", "-f", "s16le", "-"],
                         input=raw, check=True, capture_output=True).stdout
    return np.frombuffer(out, dtype=np.int16).astype(np.float32) / 32768.0


def sentence_spans(text: str, align: dict, order: list) -> list[tuple[float, float]]:
    chars = align["characters"]
    starts = align["character_start_times_seconds"]
    ends = align["character_end_times_seconds"]
    joined = "".join(chars)
    if joined != text:
        # 合成が原稿を書き換えて返すことがある。そのときは返ってきた文字の列の中で探す
        print("  注意: 時刻の文字の列が原稿と一致しない。返ってきた列の中で文を探す")
    # 文字の列の位置 → chars の添字（chars が 1 文字ずつでない場合に備える）
    index = []
    for k, c in enumerate(chars):
        index += [k] * len(c)
    spans = []
    pos = 0
    for sid, ci, sentence in order:
        at = joined.find(sentence, pos)
        if at < 0:
            raise SystemExit(f"{sid} {ci}: 文が時刻の列に見つからない: {sentence[:40]}")
        a, b = index[at], index[at + len(sentence) - 1]
        voiced = [k for k in range(a, b + 1) if any(ch.isalnum() for ch in chars[k])]
        spans.append((starts[voiced[0]], ends[voiced[-1]]))
        pos = at + len(sentence)
    return spans


def quiet_runs(x: np.ndarray, lo: float, hi: float) -> list[tuple[float, float]]:
    """[lo, hi) 秒の中の、続けて静かな区間の列（秒）。"""
    win = RATE // 50
    a = max(0, int(lo * RATE))
    r = frame_rms(x[a:int(hi * RATE)], win)
    runs, i = [], 0
    while i < len(r):
        if r[i] >= QUIET_RMS:
            i += 1
            continue
        j = i
        while j < len(r) and r[j] < QUIET_RMS:
            j += 1
        runs.append((a / RATE + i / 50, a / RATE + j / 50))
        i = j
    return runs


def sentence_cuts(x: np.ndarray, spans: list[tuple[float, float]]) -> list[tuple[float, float]]:
    """文の境目を波形の無音で決める。合成が返す字の時刻は声より数百 ms 遅れることがあり、
    時刻だけで切ると次の文の頭の語が前の文の尻に入った（2026-11-16、文字起こしで英語 14 文の頭の語が欠けた）。
    前の文の尻の 0.8 秒前から次の文の頭の 0.25 秒後までで、いちばん長い無音の真ん中で切る
    （次の文の頭の「I am... your」の間を拾わないよう、後ろへは広げない）。"""
    total = len(x) / RATE
    bounds = [0.0]
    for k in range(len(spans) - 1):
        lo = max(spans[k][0] + 0.3, spans[k][1] - 0.8)
        hi = min(spans[k + 1][1] - 0.3, spans[k + 1][0] + 0.25)
        runs = quiet_runs(x, lo, hi)
        if not runs:
            raise SystemExit(f"文 {k} と {k + 1} の間に無音が見つからない（{lo:.2f}–{hi:.2f} 秒）")
        a, b = max(runs, key=lambda r: r[1] - r[0])
        bounds.append((a + b) / 2)
    bounds.append(total)
    return list(zip(bounds[:-1], bounds[1:]))


def trim_quiet(x: np.ndarray) -> np.ndarray:
    """頭と尻の無音を、PAD_HEAD / PAD_TAIL を残して落とす。"""
    win = RATE // 50
    loud = np.nonzero(frame_rms(x, win) >= QUIET_RMS)[0]
    if not len(loud):
        return x
    a = max(0, loud[0] * win - int(PAD_HEAD * RATE))
    b = min(len(x), (loud[-1] + 1) * win + int(PAD_TAIL * RATE))
    return x[a:b]


def write_mp3(x: np.ndarray, path: Path) -> None:
    raw = (np.clip(x, -1, 1) * 32767).astype(np.int16).tobytes()
    tmp = path.with_name(path.stem + ".tmp" + path.suffix)
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "s16le", "-ar", str(RATE), "-ac", "1",
                    "-i", "-", "-c:a", "libmp3lame", "-b:a", "128k", str(tmp)], input=raw, check=True)
    os.replace(tmp, path)


def duration(path: Path) -> float:
    r = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "json", str(path)],
                       capture_output=True, text=True, check=True)
    return float(json.loads(r.stdout)["format"]["duration"])


def ja_level(briefing: dict) -> float:
    return float(np.mean([voiced_rms(decode(ASSET / sc["audio"]["ja"])) for sc in briefing["scenes"]]))


def dub(lang: str, briefing: dict, meta: dict, reuse: bool, ref_level: float, base_version: int) -> None:
    text, order = prompt(briefing, lang)
    mp3, js = original_paths(lang)
    if not reuse or not mp3.exists():
        synthesize(text, lang)
    saved = json.loads(js.read_text(encoding="utf-8"))
    if saved.get("text") != text:
        raise SystemExit(f"{lang}: 残してある合成の原稿が今の台詞と違う。--reuse を外して合成し直す")
    x = decode(mp3)
    spans = sentence_spans(text, saved["alignment"], order)

    windows = {(c["scene"], c["i"]): (c["s"], c["e"]) for c in meta["cues"]}
    cuts = sentence_cuts(x, spans)
    pieces = []
    for k, ((sid, ci, sentence), (a0, b0)) in enumerate(zip(order, cuts)):
        seg = squeeze_pauses(trim_quiet(x[int(a0 * RATE):int(b0 * RATE)]))
        s, e = windows[(sid, ci)]
        avail = (e - s - TAIL_GAP_MS) / 1000
        tempo = max(1.0, len(seg) / RATE / avail)
        over = tempo > TEMPO_MAX
        if tempo > 1.0:
            seg = atempo(seg, min(tempo, TEMPO_MAX))
        pieces.append((s, seg))
        note = "  ⚠ 窓からはみ出す" if over else ""
        print(f"  {sid} {ci}: 窓 {(e - s) / 1000:.2f}s  声 {len(seg) / RATE:.2f}s  速さ ×{min(tempo, TEMPO_MAX):.2f}{note}")

    level = voiced_rms(np.concatenate([p for _, p in pieces]))
    gain = ref_level / level if level else 1.0
    # 混ぜる相手は bake-film.py が焼いた効果音だけの版。声を入れた版は 1 つ上の版の番号で書く
    base = ASSET / f"doctor-room-{lang}-v{base_version}.mp4"
    if not base.exists():
        raise SystemExit(f"{lang}: 効果音だけの版が無い: {base.name}（bake-film.py --lang {lang} --version {base_version}）")
    length = duration(base)
    track = np.zeros(int(length * RATE) + RATE, dtype=np.float32)
    for s, seg in pieces:
        at = int(s / 1000 * RATE)
        n = min(len(seg), len(track) - at)
        track[at:at + n] += seg[:n] * gain
    track = track[:int(length * RATE)]
    voice_path = PROTO / "layers" / f"voice-{lang}.mp3"
    write_mp3(track, voice_path)
    print(f"  声の大きさ ×{gain:.2f}（日本語の声にそろえた） → {voice_path.relative_to(ROOT)}")

    out_name = f"doctor-room-{lang}-v{base_version + 1}.mp4"
    out = ASSET / out_name
    tmp = out.with_name(out.stem + ".tmp.mp4")
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(base), "-i", str(voice_path),
                    "-filter_complex", f"[0:a][1:a]amix=inputs=2:normalize=0:duration=first,atrim=end={length:.3f}[a]",
                    "-map", "0:v", "-map", "[a]", "-c:v", "copy", "-c:a", "aac", "-b:a", "160k", "-ar", "48000",
                    "-movflags", "+faststart", str(tmp)], check=True)
    os.replace(tmp, out)
    for sc in briefing["scenes"]:
        sc["film"][lang]["file"] = out_name
    print(f"  {out_name}  {out.stat().st_size / 1e6:.1f}MB（画は {base.name} の複製）")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lang", nargs="+", choices=("en", "fr"), default=["en", "fr"])
    ap.add_argument("--reuse", action="store_true", help="前に合成した音から置き直すだけ（合成しない）")
    ap.add_argument("--base-version", type=int, default=3, help="bake-film.py が焼いた効果音だけの版の番号。声入りはその次の番号")
    args = ap.parse_args()
    briefing = json.loads(BRIEFING.read_text(encoding="utf-8"))
    meta = json.loads(META.read_text(encoding="utf-8"))
    ref = ja_level(briefing)
    for lang in args.lang:
        print(f"{lang}:")
        dub(lang, briefing, meta, args.reuse, ref, args.base_version)
    tmp = BRIEFING.with_suffix(".json.tmp")
    with io.open(tmp, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(briefing, ensure_ascii=False, indent=2) + "\n")
    json.loads(tmp.read_text(encoding="utf-8"))
    os.replace(tmp, BRIEFING)


if __name__ == "__main__":
    main()
