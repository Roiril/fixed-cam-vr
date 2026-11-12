"""博士の日本語の声を ElevenLabs で 1 本に合成し、場面ごとに切ってタブレットへ組み込む。

    py -3.11 tools/visitor-ui/generate-doctor-audio.py --voice <voice_id>            # 合成して組み込む
    py -3.11 tools/visitor-ui/generate-doctor-audio.py --voice <voice_id> --reuse    # 前に合成した音から切り直すだけ

入力は `elevenlabs-input-ja-v4.md` のコードブロック（0257: 全部の台詞を一度に入れる）。
鍵は環境変数 `ELEVENLABS_API_KEY`（値は表示しない。`~/.claude/rules/secrets.md`）。

することは 6 つ。
1. `/with-timestamps` で合成する。音と一緒に、1 字ごとの時刻が返る
2. 各文の頭の語を時刻の列から探し、文と文の間の無音の真ん中で区切る
3. 場面ごとに `tablet/.../asset/<場面>-ja-v4.mp3` へ切り出す
4. `briefing-v1.json` の日本語の `durationMs` を実測へ直し、`audio.ja` を付ける
5. 長い間を詰める（`PHRASE_PAUSE_MAX` / `SENTENCE_PAUSE_MAX`。頭だけ遅く聞こえるのを防ぐ）
6. 声の大きさの列を `asset/doctor-voice-ja-v4.json` に書く（テレビの下の波形が使う）

切り出すときに `--tempo`（既定 1.2）で速める。最初の場面の頭には `--lead-in`（既定 1.0 秒）の無音を置く。
原稿は読み違えやすい語をかなで書く（「装置」→「そうち」。ANCHORS も原稿の字に合わせる）。残してある元の音は等速のまま。
⚠ 口の動画（リップシンク）は作らない。動画が届くまで、日本語は博士の静止画と声で進む。
⚠ 合成した元の音と時刻は `tools/visitor-ui/doctor-ja-v4-original.{mp3,json}` に残す。声を替えたら作り直す。
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
VERSION = "v4"
INPUT = HERE / f"elevenlabs-input-ja-{VERSION}.md"
TAKE_MP3 = HERE / f"doctor-ja-{VERSION}-original.mp3"
TAKE_JSON = HERE / f"doctor-ja-{VERSION}-original.json"

# 各文の頭（入力原稿の表記）。場面と文の順は briefing-v1.json と同じ。
ANCHORS = {
    "introduction": ["ようこそ", "私は……", "あなたは、今日から", "これより、"],
    "subject": ["調査対象は、", "ある空き家の", "外見上は、", "その条件も", "かんそくそうちをつけ", "右手で手すりを持ち"],
    "report": ["これが、怪異を記録し", "そうちには、", "どのような異変が", "報告するときは", "報告の内容は"],
    "wear": ["調査が終わったら", "続けられなくなったら", "では。", "健闘を、祈ります"],
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


def voice_envelope(path, hz=20):
    """切り出した音から、声の大きさの列（0–9 の数字の文字列・1 秒あたり hz 個）を作る。
    タブレットのテレビの下の波形が、再生位置からこの列を引いて動く（tv-live.js）。"""
    import numpy as np
    raw = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-i", str(path), "-ac", "1", "-ar", "8000",
                          "-f", "s16le", "-"], check=True, capture_output=True).stdout
    x = np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0
    step = 8000 // hz
    n = len(x) // step
    rms = np.sqrt((x[:n * step].reshape(n, step) ** 2).mean(axis=1))
    return rms


# 間の上限（合成した音の秒。速める前）。合成は「……」と [pause] の多い頭の文ほど間を長く取り、
# 話す速さは一定なのに、頭が遅く後ろへ行くほど速く聞こえた（2026-11-12 実測: 間を除いた速さは
# 文ごとに 7〜9 拍/秒でほぼ一定、間を含めると頭の 4 文が 2.6〜4.6 拍/秒・後半が 5.5〜7 拍/秒）。
# 文の中の間と文と文の間を、それぞれこの長さまで詰める。詰めるのは波形が静かな所だけ。
PHRASE_PAUSE_MAX = 0.35
SENTENCE_PAUSE_MAX = 0.90
QUIET_RMS = 0.01      # 20ms の窓の RMS がこれ未満を無音とみなす（約 -40 dBFS）


def is_voiced(c):
    return c.isalnum() or c == "ー"


def decode_mono(path, rate=44100):
    import numpy as np
    raw = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-i", str(path), "-ac", "1", "-ar", str(rate),
                          "-f", "s16le", "-"], check=True, capture_output=True).stdout
    return np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0, rate


def quiet_run(samples, rate, lo, hi):
    """[lo, hi) の中で、波形がいちばん長く続けて静かな区間（秒）。無ければ None。"""
    import numpy as np
    win = int(rate * 0.02)
    i0, i1 = int(lo * rate) // win, int(hi * rate) // win
    quiet = [float(np.sqrt(np.mean(samples[w * win:(w + 1) * win] ** 2))) < QUIET_RMS for w in range(i0, i1)]
    best, run = (0, 0), None
    for w, q in enumerate(quiet + [False]):
        if q and run is None:
            run = w
        elif not q and run is not None:
            if w - run > best[1] - best[0]:
                best = (run, w)
            run = None
    if best[1] == best[0]:
        return None
    return (i0 + best[0]) * win / rate, (i0 + best[1]) * win / rate


def voiced_indices(chars, starts, ends):
    """声になる字の位置。演技の指定（[...] の中）は除く。指定の字にも時刻が付いており、
    それを前の字と取り違えると、文の境が次の文の声の中に入った（2026-11-12 実測。場面の終わりに次の頭の語が混ざった）。"""
    voiced, depth = [], 0
    for k, c in enumerate(chars):
        if c == "[":
            depth += 1
        elif c == "]":
            depth = max(0, depth - 1)
        elif depth == 0 and is_voiced(c) and ends[k] > starts[k]:
            voiced.append(k)
    return voiced


def long_pauses(chars, starts, ends, samples, rate):
    """詰める区間（秒）の列。字の時刻から長い間を探し、その中の波形が静かな部分だけを返す。"""
    voiced = voiced_indices(chars, starts, ends)
    cuts = []
    for a, b in zip(voiced, voiced[1:]):
        between = chars[a + 1:b]
        cap = SENTENCE_PAUSE_MAX if ("。" in between or "？" in between) else PHRASE_PAUSE_MAX
        # 間の長さは波形の無音で測る。字の時刻は声より 0.4 秒ほど遅れることがあり（2026-11-12 実測）、
        # 字の時刻から詰めると、次の文の側に残すはずの無音が消えて声の出だしに接した
        q = quiet_run(samples, rate, ends[a], starts[b])
        if not q:
            continue
        lo, hi = q[0] + cap / 2, q[1] - cap / 2
        if hi - lo >= 0.05:
            cuts.append((lo, hi))
    return cuts


# 原稿から外したが、残してある合成の音にはまだ入っている文（--reuse で切り直すときに音から切り取る）。
# 0334「足元は見えません。は不要」。聴いて判定をもらった声を保つため、合成し直さずに切り取った。
# 次に合成し直したら、この文は音に無いので何もしない。
DROP_PHRASES = ["足元は、見えません。"]


def dropped_spans(chars, starts, ends, samples, rate):
    """DROP_PHRASES の文を、手前の無音の真ん中から次の文の手前の無音の真ん中まで切り取る区間。"""
    voiced = voiced_indices(chars, starts, ends)
    text = "".join(chars)
    spans = []
    for phrase in DROP_PHRASES:
        i = text.find(phrase)
        if i < 0:
            continue
        inside = [v for v in voiced if i <= v < i + len(phrase)]
        before = [v for v in voiced if v < i]
        after = [v for v in voiced if v >= i + len(phrase)]
        if not inside or not before or not after:
            raise SystemExit(f"「{phrase}」の前後が取れない")
        q0 = quiet_run(samples, rate, ends[before[-1]], starts[inside[0]])
        q1 = quiet_run(samples, rate, ends[inside[-1]], starts[after[0]])
        if not q0 or not q1:
            raise SystemExit(f"「{phrase}」の前後に無音が見つからない（切ると声が切れる）")
        spans.append(((q0[0] + q0[1]) / 2, (q1[0] + q1[1]) / 2))
    return spans


def merge_spans(spans):
    out = []
    for a, b in sorted(spans):
        if out and a <= out[-1][1]:
            out[-1] = (out[-1][0], max(out[-1][1], b))
        else:
            out.append((a, b))
    return out


def remap(t, cuts):
    """元の時刻 t を、間を詰めた後の時刻へ。"""
    return t - sum(max(0.0, min(t, b) - a) for a, b in cuts)


def kept(start, end, cuts):
    """[start, end) から詰める区間を除いた残りの区間の列。"""
    out, at = [], start
    for a, b in cuts:
        if b <= at or a >= end:
            continue
        if a > at:
            out.append((at, a))
        at = max(at, b)
    if at < end:
        out.append((at, end))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--voice", required=True)
    ap.add_argument("--model", default="eleven_v3")
    ap.add_argument("--reuse", action="store_true", help="合成せず、残してある音から切り直す")
    ap.add_argument("--tempo", type=float, default=1.2,
                    help="読み上げの速さ。合成した音を高さを変えずに速める（0327 の続き「今の1.2倍くらい」）")
    ap.add_argument("--lead-in", type=float, default=1.0,
                    help="最初の場面の頭に置く無音の秒数（0331「最初、少し間をおいてから」。間が無いと声の頭が見切れる）")
    args = ap.parse_args()

    if not args.reuse:
        synthesize(args.voice, args.model)
    take = json.loads(TAKE_JSON.read_text(encoding="utf-8"))
    al = take["alignment"]
    chars = "".join(al["characters"])
    starts, ends = al["character_start_times_seconds"], al["character_end_times_seconds"]
    total_ms = duration_ms(TAKE_MP3)

    samples, rate = decode_mono(TAKE_MP3)
    voiced = voiced_indices(al["characters"], starts, ends)
    # 各文の頭と、その直前の声になる字の終わり。区切りはその間で波形がいちばん長く静かな所の真ん中。
    cue_bounds, cursor = [], 0
    for scene_id, anchors in ANCHORS.items():
        for anchor in anchors:
            i = chars.find(anchor, cursor)
            if i < 0:
                raise SystemExit(f"時刻の列に「{anchor}」が無い（入力原稿と ANCHORS を揃える）")
            prev = [v for v in voiced if v < i]
            if not cue_bounds or not prev:
                cue_bounds.append((scene_id, 0.0))   # 最初の文は合成の頭から取る（声の頭を削らない。0331）
            else:
                q = quiet_run(samples, rate, ends[prev[-1]], starts[i])
                if not q:
                    raise SystemExit(f"「{anchor}」の手前に無音が見つからない（区切ると声が切れる）")
                cue_bounds.append((scene_id, (q[0] + q[1]) / 2))
            cursor = i + len(anchor)
    cut_s = [t for _, t in cue_bounds] + [total_ms / 1000]
    pauses = merge_spans(long_pauses(al["characters"], starts, ends, samples, rate)
                         + dropped_spans(al["characters"], starts, ends, samples, rate))
    cut_ms = [round(remap(t, pauses) * 1000) for t in cut_s]   # 間を詰めた後の文の境

    raw = io.open(BRIEFING, encoding="utf-8", newline="").read()
    nl = "\r\n" if "\r\n" in raw else "\n"
    data = json.loads(raw)
    report, k = {}, 0
    for scene in data["scenes"]:
        n = len(ANCHORS[scene["id"]])
        if len(scene["cues"]["ja"]) != n:
            raise SystemExit(f"{scene['id']}: 文の数が違う（JSON {len(scene['cues']['ja'])} / ANCHORS {n}）")
        lead_ms = round(args.lead_in * 1000) if k == 0 else 0
        target = ASSETS / f"{scene['id']}-ja-{VERSION}.mp3"
        tmp = target.with_name(target.name + ".tmp")
        parts = kept(cut_s[k], cut_s[k + n], pauses)
        graph = ";".join(f"[0:a]atrim=start={a:.4f}:end={b:.4f},asetpts=PTS-STARTPTS[p{i}]" for i, (a, b) in enumerate(parts))
        graph += ";" + "".join(f"[p{i}]" for i in range(len(parts))) + f"concat=n={len(parts)}:v=0:a=1,atempo={args.tempo:.3f}"
        graph += (f",adelay={lead_ms}:all=1,asetpts=N/SR/TB" if lead_ms else "") + "[out]"
        subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(TAKE_MP3),
                        "-filter_complex", graph, "-map", "[out]",
                        "-ac", "1", "-ar", "48000", "-codec:a", "libmp3lame", "-b:a", "128k", "-f", "mp3", str(tmp)],
                       check=True)
        tmp.replace(target)
        for c in range(n):
            scene["cues"]["ja"][c]["durationMs"] = round((cut_ms[k + c + 1] - cut_ms[k + c]) / args.tempo) + (lead_ms if c == 0 else 0)
        scene.pop("video", None)
        scene["audio"] = {**{a: b for a, b in (scene.get("audio") or {}).items() if a != "ja"}, "ja": target.name}
        report[scene["id"]] = {"file": target.name, "ms": duration_ms(target),
                               "cues": [c["durationMs"] for c in scene["cues"]["ja"]]}
        k += n
    # 声の波形の列。全場面を同じ基準（全体の 97 パーセンタイル）で 0–9 にする
    import numpy as np
    hz = 20
    env = {scene["id"]: voice_envelope(ASSETS / scene["audio"]["ja"], hz) for scene in data["scenes"]}
    top = float(np.percentile(np.concatenate(list(env.values())), 97)) or 1.0
    voice = {"hz": hz, "scenes": {k: "".join(str(int(min(9, round(9 * (v / top) ** 0.7)))) for v in vals) for k, vals in env.items()}}
    voice_path = ASSETS / f"doctor-voice-ja-{VERSION}.json"
    io.open(voice_path, "w", encoding="utf-8", newline="\n").write(json.dumps(voice, ensure_ascii=False) + "\n")
    out = (json.dumps(data, ensure_ascii=False, indent=2) + "\n").replace("\n", nl)
    tmp = BRIEFING.with_name(BRIEFING.name + ".tmp")
    io.open(tmp, "w", encoding="utf-8", newline="").write(out)
    json.loads(tmp.read_text(encoding="utf-8"))
    os.replace(tmp, BRIEFING)
    print(json.dumps({"voice": take["voice"], "model": take["model"], "tempo": args.tempo,
                      "pausesTrimmed": len(pauses), "trimmedSec": round(sum(b - a for a, b in pauses), 2),
                      "totalMs": round(remap(total_ms / 1000, pauses) * 1000 / args.tempo), "scenes": report},
                     ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
