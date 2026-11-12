"""博士の日本語の声を ElevenLabs で合成し、場面ごとに切ってタブレットへ組み込む。

    py -3.11 tools/visitor-ui/generate-doctor-audio.py --voice <voice_id> --take v6-wear   # その合成だけ作り直して組み込む
    py -3.11 tools/visitor-ui/generate-doctor-audio.py --voice <voice_id> --reuse          # 前に合成した音から切り直すだけ

合成（take）は複数ある。`TAKES` が合成ごとの入力原稿と文の頭を、`SCENE_TAKE` が場面ごとにどの合成から切るかを持つ。
v5 は全部の台詞を一度に入れた 1 本（0257）。v6-wear は最後の場面だけを入れ直した 1 本（0348）。
最後の場面は後ろに何も無いので、そこだけ入れ替えても前の場面の音・時刻・口の動画は変わらない。
鍵は環境変数 `ELEVENLABS_API_KEY`（値は表示しない。`~/.claude/rules/secrets.md`）。

することは 6 つ（5 は 2 段）。
1. `/with-timestamps` で合成する。音と一緒に、1 字ごとの時刻が返る
2. 各文の頭の語を時刻の列から探し、文と文の間の無音の真ん中で区切る
3. 場面ごとに `tablet/.../asset/<場面>-ja-<合成の版>.mp3` へ切り出す
4. `briefing-v1.json` の日本語の `durationMs` を実測へ直し、`audio.ja` を付ける
5. 長い間を詰める（`PHRASE_PAUSE_MAX` / `SENTENCE_PAUSE_MAX`。頭だけ遅く聞こえるのを防ぐ）
   文ごとの話す速さ（拍/秒）を測り、v5 の 20 文の中央値へ揃える（`readings`。合成は頭の文ほどゆっくり読む）
6. 声の大きさの列を `asset/doctor-voice-ja-<ENVELOPE_VERSION>.json` に書く（テレビの下の波形が使う）

切り出すときに `--tempo`（既定 1.2）で速める。最初の場面の頭には `--lead-in`（既定 1.0 秒）の無音を置く。
原稿は読み違えやすい語をかなで書く（「装置」→「そうち」。anchors も原稿の字に合わせる）。残してある元の音は等速のまま。
口の動画は `room-proto/lipsync.py`（声を変えたら、変わった場面だけ `plan --scene` から作り直す）。
⚠ 合成した元の音と時刻は `tools/visitor-ui/doctor-ja-<合成>-original.{mp3,json}` に残す。声を替えたら作り直す。
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
# 合成ごとの入力原稿・文の頭（anchors。入力原稿の表記）・文の読み（readings。かな）。
# anchors と readings は、その合成に入っている全部の場面を、合成の中の順に持つ（場面を使わなくても、
# 区切りと速さの基準のために要る）。読みは話す速さ（拍/秒）を文ごとに測って揃えるのに使う（0344）。
# 台詞を変えたら両方直す（数が合わなければ止まる）。
TAKES = {
    # 全部の台詞を一度に入れた 1 本（0338 の台詞）。wear の 4 文は 0348 で使わなくなったが、
    # 速さの基準（RATE_REF_TAKE）がこの 20 文の中央値なので消さない
    "v5": {
        "version": "v5",
        "input": "elevenlabs-input-ja-v5.md",
        "anchors": {
            "introduction": ["ようこそ", "私は……", "あなたは、今日から", "これより、"],
            "subject": ["調査対象は、", "ある空き家の", "どちらも、", "その条件も"],
            "report": ["これが、怪異を記録し", "そうちをつけると", "そこに、室内の", "その映像を見ながら", "右手で手すりを持ち",
                       "映像に、少しでも", "報告するときは", "何を見たかは"],
            "wear": ["そうちの合図で", "続けられないときだけは", "では。", "健闘を、祈ります"],
        },
        "readings": {
            "introduction": ["ようこそ。にほんかいいけんきゅうじょへ。", "わたしは、あなたのじょうし、とでもいっておきましょうか。",
                             "あなたはきょうから、このけんきゅうじょの、ちょうさいんです。", "これより、ちょうさたいしょうについて、せつめいします。"],
            "subject": ["ちょうさたいしょうは、かんりばんごうぜろごーいちよんにしていされた、いじょうそんざいです。",
                        "あるあきやの、こどもべやからみつかった、かーてんと、にんぎょうです。",
                        "どちらも、がいけんじょうは、いっぱんてきなものと、かわりません。しかし、とくていのじょうけんかでのみ、いじょうせいが、はつげんします。",
                        "そのじょうけんも、いじょうせいのしょうたいも、まだ、はんめいしていません。それをちょうさするのが、あなたのにんむです。"],
            "report": ["これが、かいいをきろくし、かいせきするための、かんそくそうちです。", "そうちをつけると、めのまえにがめんが、あらわれます。",
                       "そこに、しつないのかめらがとらえた、あなたじしんのすがたが、うつります。", "そのえいぞうをみながら、このかーてんのまわりを、さんしゅうしてください。",
                       "みぎてでてすりをもち、けっしてはなさず、ゆっくり、あるいてください。", "えいぞうに、すこしでもいわかんをおぼえたら、ほうこくしてください。",
                       "ほうこくするときは、ひだりこんとろーらーのぼたんを、おやゆびで、いちびょうかん、ながおししてください。えっくすとわい、どちらでも、かまいません。",
                       "なにをみたかは、そうちにとうさいされたえーじぇんとが、かいせきします。みまちがいでも、なんどほうこくしても、かまいません。"],
            "wear": ["そうちのあいずで、あるきはじめてください。おわりのあいずのあとは、かかりのものがそうちをはずすまで、うごかずに、おまちください。",
                     "つづけられないときだけは、そのばでたちどまり、てすりをはなして、そうちをはずしてください。",
                     "では。かかりのもののあんないにしたがって、かんそくそうちを、つけてください。", "けんとうを、いのります。"],
        },
    },
    # 最後の場面だけを入れ直した 1 本（0348。係の者の 2 か所を、装置内のエージェントの指示に従う形へ）
    "v6-wear": {
        "version": "v6",
        "input": "elevenlabs-input-ja-v6-wear.md",
        "anchors": {
            "wear": ["そうちをつけたら", "続けられないときだけは", "では。", "健闘を、祈ります"],
        },
        "readings": {
            "wear": ["そうちをつけたら、そうちないのえーじぇんとのしじにしたがって、あるきはじめてください。ちょうさがおわったあとも、そのまま、えーじぇんとのしじにしたがってください。",
                     "つづけられないときだけは、そのばでたちどまり、てすりをはなして、そうちをはずしてください。",
                     "では。かんそくそうちを、つけてください。", "けんとうを、いのります。"],
        },
    },
}
# 場面ごとに、どの合成から切るか
SCENE_TAKE = {"introduction": "v5", "subject": "v5", "report": "v5", "wear": "v6-wear"}
# 話す速さを揃える先は、この合成の全部の文の中央値（7.68 拍/秒）。場面だけ入れ直しても、ほかの場面の倍率が変わらない
RATE_REF_TAKE = "v5"
# 声の大きさの列のファイルの版。中身が変わったら上げる（アプリは force-cache で読む）。
# 上げたら tablet の index.html の loadVoiceEnvelope と room-proto/index.html の voiceFile も直す
ENVELOPE_VERSION = "v6"


def take_paths(name):
    return HERE / f"doctor-ja-{name}-original.mp3", HERE / f"doctor-ja-{name}-original.json"
# 文ごとの速さの直し幅。遅い文は最大 1.25 倍まで速め、速い文は 0.95 倍までしか遅くしない
RATE_FIX_MIN = 0.95
RATE_FIX_MAX = 1.25


def mora(text):
    """拍の数（かなの数から、句読点と小さい ゃゅょぇ を除く）"""
    return len(re.sub(r"[、。ゃゅょぇ]", "", text))


def voiced_seconds(samples, rate, spans):
    """区間の列のうち、声が鳴っている長さ（20ms の窓の RMS が QUIET_RMS 以上の窓を数える）"""
    import numpy as np
    win = int(rate * 0.02)
    total = 0
    for a, b in spans:
        for w in range(int(a * rate) // win, int(b * rate) // win):
            if float(np.sqrt(np.mean(samples[w * win:(w + 1) * win] ** 2))) >= QUIET_RMS:
                total += 1
    return total * 0.02


def api_key():
    key = os.environ.get("ELEVENLABS_API_KEY")
    if not key and os.name == "nt":
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as k:
            key = winreg.QueryValueEx(k, "ELEVENLABS_API_KEY")[0]
    if not key:
        raise SystemExit("ELEVENLABS_API_KEY が無い")
    return key


def prompt_text(take):
    path = HERE / TAKES[take]["input"]
    blocks = re.findall(r"```text\r?\n(.*?)\r?\n```", path.read_text(encoding="utf-8"), re.S)
    if len(blocks) != 1:
        raise SystemExit(f"{path.name} のコードブロックが 1 つではない")
    return blocks[0]


def synthesize(take, voice, model):
    take_mp3, take_json = take_paths(take)
    body = json.dumps({"text": prompt_text(take), "model_id": model}).encode("utf-8")
    req = urllib.request.Request(
        f"https://api.elevenlabs.io/v1/text-to-speech/{voice}/with-timestamps?output_format=mp3_44100_128",
        data=body, headers={"xi-api-key": api_key(), "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=300) as res:
            data = json.loads(res.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        raise SystemExit(f"合成に失敗: HTTP {e.code} {e.read().decode('utf-8', 'replace')[:400]}")
    take_mp3.write_bytes(base64.b64decode(data["audio_base64"]))
    align = data.get("alignment") or data.get("normalized_alignment")
    take_json.write_text(json.dumps({"voice": voice, "model": model, "alignment": align}, ensure_ascii=False),
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
# 聴いて判定をもらった声を保ったまま 1 文だけ外すときに、原稿の字のまま書く（0334 の「足元は、見えません。」で使った）。
# v5 は合成し直したので空。
DROP_PHRASES = []


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


def analyse(name, voice_hint=None):
    """1 本の合成を、文の境・詰める間・文ごとの話す速さへ分ける。"""
    spec = TAKES[name]
    take_mp3, take_json = take_paths(name)
    take = json.loads(take_json.read_text(encoding="utf-8"))
    al = take["alignment"]
    chars = "".join(al["characters"])
    starts, ends = al["character_start_times_seconds"], al["character_end_times_seconds"]
    total_ms = duration_ms(take_mp3)

    samples, rate = decode_mono(take_mp3)
    voiced = voiced_indices(al["characters"], starts, ends)
    # 各文の頭と、その直前の声になる字の終わり。区切りはその間で波形がいちばん長く静かな所の真ん中。
    cue_bounds, first, cursor = [], {}, 0
    for scene_id, anchors in spec["anchors"].items():
        first[scene_id] = len(cue_bounds)
        for anchor in anchors:
            i = chars.find(anchor, cursor)
            if i < 0:
                raise SystemExit(f"{name}: 時刻の列に「{anchor}」が無い（入力原稿と anchors を揃える）")
            prev = [v for v in voiced if v < i]
            if not cue_bounds or not prev:
                cue_bounds.append((scene_id, 0.0))   # 最初の文は合成の頭から取る（声の頭を削らない。0331）
            else:
                q = quiet_run(samples, rate, ends[prev[-1]], starts[i])
                if not q:
                    raise SystemExit(f"{name}: 「{anchor}」の手前に無音が見つからない（区切ると声が切れる）")
                cue_bounds.append((scene_id, (q[0] + q[1]) / 2))
            cursor = i + len(anchor)
    cut_s = [t for _, t in cue_bounds] + [total_ms / 1000]
    pauses = merge_spans(long_pauses(al["characters"], starts, ends, samples, rate)
                         + dropped_spans(al["characters"], starts, ends, samples, rate))
    cut_ms = [round(remap(t, pauses) * 1000) for t in cut_s]   # 間を詰めた後の文の境

    # 文ごとの話す速さ（0344「〜調査員です。までが、読み上げ速度自体が他よりゆっくり」。
    # 測ると頭の 3 文が 7.0〜8.4 拍/秒、後半が 9.0〜9.5 拍/秒だった）
    flat = [r for sid in spec["anchors"] for r in spec["readings"][sid]]
    if len(flat) != len(cue_bounds):
        raise SystemExit(f"{name}: readings の文の数が anchors と違う（{len(flat)} / {len(cue_bounds)}）")
    rates = []
    for i in range(len(flat)):
        v = voiced_seconds(samples, rate, kept(cut_s[i], cut_s[i + 1], pauses))
        rates.append(mora(flat[i]) / max(0.2, v))
    return {"name": name, "mp3": take_mp3, "voice": take["voice"], "model": take["model"], "first": first,
            "cut_s": cut_s, "cut_ms": cut_ms, "pauses": pauses, "rates": rates}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--voice", required=True)
    ap.add_argument("--model", default="eleven_v3")
    ap.add_argument("--take", choices=sorted(TAKES), help="合成し直す 1 本。ほかの合成は残してある音を使う")
    ap.add_argument("--reuse", action="store_true", help="合成せず、残してある音から切り直す")
    ap.add_argument("--tempo", type=float, default=1.2,
                    help="読み上げの速さ。合成した音を高さを変えずに速める（0327 の続き「今の1.2倍くらい」）")
    ap.add_argument("--lead-in", type=float, default=1.0,
                    help="最初の場面の頭に置く無音の秒数（0331「最初、少し間をおいてから」。間が無いと声の頭が見切れる）")
    args = ap.parse_args()

    if args.reuse == bool(args.take):
        raise SystemExit("--take <合成> か --reuse のどちらか 1 つを付ける")
    if args.take:
        synthesize(args.take, args.voice, args.model)
    takes = {name: analyse(name) for name in sorted(set(SCENE_TAKE.values()) | {RATE_REF_TAKE})}
    ref = takes[RATE_REF_TAKE]["rates"]
    median = sorted(ref)[len(ref) // 2]
    for t in takes.values():
        t["fix"] = [min(RATE_FIX_MAX, max(RATE_FIX_MIN, median / r)) for r in t["rates"]]

    raw = io.open(BRIEFING, encoding="utf-8", newline="").read()
    nl = "\r\n" if "\r\n" in raw else "\n"
    data = json.loads(raw)
    report = {}
    for si, scene in enumerate(data["scenes"]):
        t = takes[SCENE_TAKE[scene["id"]]]
        spec = TAKES[t["name"]]
        n = len(spec["anchors"][scene["id"]])
        k = t["first"][scene["id"]]
        cut_s, cut_ms, pauses, fix = t["cut_s"], t["cut_ms"], t["pauses"], t["fix"]
        if len(scene["cues"]["ja"]) != n:
            raise SystemExit(f"{scene['id']}: 文の数が違う（JSON {len(scene['cues']['ja'])} / anchors {n}）")
        lead_ms = round(args.lead_in * 1000) if si == 0 else 0
        target = ASSETS / f"{scene['id']}-ja-{spec['version']}.mp3"
        tmp = target.with_name(target.name + ".tmp")
        # 文ごとに切り出して、その文の倍率で速さを直してから繋ぐ
        chains, labels, idx = [], [], 0
        for c in range(n):
            parts = kept(cut_s[k + c], cut_s[k + c + 1], pauses)
            names = []
            for a, b in parts:
                chains.append(f"[0:a]atrim=start={a:.4f}:end={b:.4f},asetpts=PTS-STARTPTS[p{idx}]")
                names.append(f"[p{idx}]")
                idx += 1
            chains.append("".join(names) + f"concat=n={len(names)}:v=0:a=1,atempo={args.tempo * fix[k + c]:.4f}[c{c}]")
            labels.append(f"[c{c}]")
        graph = ";".join(chains) + ";" + "".join(labels) + f"concat=n={n}:v=0:a=1"
        graph += (f",adelay={lead_ms}:all=1,asetpts=N/SR/TB" if lead_ms else "") + "[out]"
        subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(t["mp3"]),
                        "-filter_complex", graph, "-map", "[out]",
                        "-ac", "1", "-ar", "48000", "-codec:a", "libmp3lame", "-b:a", "128k", "-f", "mp3", str(tmp)],
                       check=True)
        tmp.replace(target)
        for c in range(n):
            scene["cues"]["ja"][c]["durationMs"] = round((cut_ms[k + c + 1] - cut_ms[k + c]) / (args.tempo * fix[k + c])) + (lead_ms if c == 0 else 0)
        scene.pop("video", None)
        scene["audio"] = {**{a: b for a, b in (scene.get("audio") or {}).items() if a != "ja"}, "ja": target.name}
        report[scene["id"]] = {"take": t["name"], "file": target.name, "ms": duration_ms(target),
                               "cues": [c["durationMs"] for c in scene["cues"]["ja"]],
                               "rates": [round(r, 2) for r in t["rates"][k:k + n]],
                               "rateFix": [round(f, 3) for f in fix[k:k + n]]}
    # 声の波形の列。全場面を同じ基準（全体の 97 パーセンタイル）で 0–9 にする
    import numpy as np
    hz = 20
    env = {scene["id"]: voice_envelope(ASSETS / scene["audio"]["ja"], hz) for scene in data["scenes"]}
    top = float(np.percentile(np.concatenate(list(env.values())), 97)) or 1.0
    voice = {"hz": hz, "scenes": {k: "".join(str(int(min(9, round(9 * (v / top) ** 0.7)))) for v in vals) for k, vals in env.items()}}
    voice_path = ASSETS / f"doctor-voice-ja-{ENVELOPE_VERSION}.json"
    io.open(voice_path, "w", encoding="utf-8", newline="\n").write(json.dumps(voice, ensure_ascii=False) + "\n")
    out = (json.dumps(data, ensure_ascii=False, indent=2) + "\n").replace("\n", nl)
    tmp = BRIEFING.with_name(BRIEFING.name + ".tmp")
    io.open(tmp, "w", encoding="utf-8", newline="").write(out)
    json.loads(tmp.read_text(encoding="utf-8"))
    os.replace(tmp, BRIEFING)
    print(json.dumps({"tempo": args.tempo, "rateMedian": round(median, 4),
                      "takes": {n: {"voice": t["voice"], "model": t["model"], "pausesTrimmed": len(t["pauses"]),
                                    "trimmedSec": round(sum(b - a for a, b in t["pauses"]), 2)} for n, t in takes.items()},
                      "totalMs": sum(v["ms"] for v in report.values()),
                      "scenes": report},
                     ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
