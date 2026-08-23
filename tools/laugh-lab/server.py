# -*- coding: utf-8 -*-
"""笑い声のパラメータをブラウザで動かして、その場で聴くための小さなサーバ。

```
.\\tools\\laugh-lab\\serve.ps1          # → http://localhost:8130/
```

⚠⚠ **卓サーバ（`tools/web-compositor/`・8099）とは別物。** show.json も実機も一切触らない。
ここが読むのは `logs/sound/ingest/src_bed_doll_swell.wav`（笑いの種）だけで、書くのは
`tools/laugh-lab/chosen.json`（採用した値のメモ・git 管理外）だけ。

⚠⚠ **焼くのと同じ道を通す。** 音を組むのは `tools/ingest-sounds.py` の関数そのもので、
ここには**音を作るコードを 1 行も書かない**。別実装にすると、卓と実機が食い違った 5 件
（`memory/sim_device_divergence.md`）と同じ形で「見本では良かったのに焼いたら違う」が起きる。

⚠ **ここで決まるのは値だけ。** 採用しても `ingest-sounds.py` は書き換わらない
（ブラウザから追跡下のソースを書き換えない）。焼くのは人が
`py -3.11 tools/ingest-sounds.py --only ...` を打つか、シュビーに言う。
"""

from __future__ import annotations

import http.server
import importlib.util
import io
import json
import os
import socketserver
import struct
import sys
import threading
import urllib.parse

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.dirname(HERE)
ROOT = os.path.dirname(TOOLS)
sys.path.insert(0, TOOLS)
import soundkit as sk  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")


def _load(name: str, filename: str):
    """`-` を含むファイル名は普通の import ができない。"""
    spec = importlib.util.spec_from_file_location(name, os.path.join(TOOLS, filename))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


ing = _load("ingest_sounds", "ingest-sounds.py")
pitch = _load("sound_pitch", "sound-pitch.py")
prev = _load("sound_preview", "sound-preview.py")   # 劇伴の音量と復号のやり方だけ借りる

SRC_WAV = os.path.join(ROOT, "logs", "sound", "ingest", "src_bed_doll_swell.wav")
CALL_WAV = os.path.join(ROOT, "Assets", "Resources", "Sound", "sfx_doll_call.wav")
CHOSEN = os.path.join(HERE, "chosen.json")
RAW_DIR = os.path.join(ROOT, "logs", "sound", "ingest")

# 層 = 焼いている 4 本そのもの（`ingest-sounds.py` の表を読む）。
#   (表示名, 声の表, 輪の秒, 狙いの LUFS, 説明)
LAYERS = {
    "one":    ("一人（3 周 A・B）", [("bed_doll_one", 31.0, ing.SWELL_ONE)], ing.SWELL_SOLO_LUFS),
    "grow_a": ("増える 2 体（3 周 C 前半）", [("bed_dolls_grow_a", 13.0, ing.SWELL_GROW_A)], ing.SWELL_SOLO_LUFS),
    "grow_b": ("増える 4 体（3 周 C 後半）", [("bed_dolls_grow_b", 17.0, ing.SWELL_GROW_B)], ing.SWELL_SOLO_LUFS),
    "swell":  ("3 周 C の終わり（3 枚重ね）", ing.SWELL_LAYERS, ing.SWELL_SOLO_LUFS),
    "crowd":  ("群れ（4 周 A・8 体）", None, ing.CHORUS[0][2]),
}

_src_cache: dict[tuple, np.ndarray] = {}
_lock = threading.Lock()


def laugh_src(pitch_ratio: float, formant: float, speed: float) -> np.ndarray:
    """高さ・声色・速さを与えて、輪へ並べる前の 1 本を作る（重いのでここだけ覚えておく）。"""
    key = (round(pitch_ratio, 4), round(formant, 3), round(speed, 3))
    with _lock:
        hit = _src_cache.get(key)
    if hit is not None:
        return hit
    y, sr = sk.read_wav(SRC_WAV)
    src = sk.env_fade(sk.to_stereo(ing.trim(y)), 0.004, 0.02)
    if abs(speed - 1.0) > 1e-3:
        # 速さだけ変える（高さは動かない）。1.0 より小さいとゆっくり笑う。
        src = ing.time_squeeze(src, 1.0 / speed, sr)
    src = ing.pitch_down(src, pitch_ratio, sr, formant=formant)
    with _lock:
        if len(_src_cache) > 48:
            _src_cache.clear()
        _src_cache[key] = src
    return src


_bed_cache: dict[float, np.ndarray] = {}


CONSOLE = "http://localhost:8099/state"
AUDIO_DIR = os.path.join(TOOLS, "web-compositor", "audio")


def show_score():
    """**卓が本編で鳴らしている劇伴**を、卓と同じ音量で返す。返すのは (波形, 名前)。

    ⚠⚠ **敷く背景は「実際に鳴っている曲」でなければ意味が無い。** 2026-08-23 に劇伴が
    2 周目 C で `LostPlace2` へ渡るようになったので（並行セッションの著作・卓 rev 996）、
    **人形が笑う 3 周目以降の下に居るのはそちら**。HorrBGM を敷いたままだと別の曲の上で
    音量を決めることになる。

    ⚠ **卓が動いていなければ HorrBGM へ落ちる**（`sound-preview.load_score` と同じもの）。
    台は卓に依存しない — 落ちたことは名前で分かるようにしてある。
    ⚠ **卓は読むだけ**（GET /state）。`show.json` を書かない・卓を再起動しない。
    """
    try:
        import urllib.request
        with urllib.request.urlopen(CONSOLE, timeout=3) as r:
            st = json.loads(r.read().decode("utf-8"))
    except Exception:
        return prev.load_score(), "HorrBGM（卓が居ないので既定）"

    # 著作の中で最後に `play` された曲 ＝ 本編の後半（人形が笑う所）で鳴っているもの。
    last = (st.get("bgm") or {}).get("trackId")

    def walk(o):
        nonlocal last
        if isinstance(o, dict):
            b = o.get("bgm")
            if isinstance(b, dict) and b.get("action") == "play" and b.get("trackId"):
                last = b["trackId"]
            for v in o.values():
                walk(v)
        elif isinstance(o, list):
            for v in o:
                walk(v)
    walk(st.get("timeline") or {})

    for t in st.get("bgmTracks") or []:
        if t.get("id") != last:
            continue
        path = os.path.join(AUDIO_DIR, os.path.basename(t.get("url", "")))
        if not os.path.exists(path):
            break
        wav = os.path.join(RAW_DIR, "src_score_" + os.path.splitext(os.path.basename(path))[0] + ".wav")
        if not os.path.exists(wav):
            import subprocess

            import imageio_ffmpeg
            os.makedirs(RAW_DIR, exist_ok=True)
            subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(), "-y", "-v", "error", "-i", path,
                            "-ar", str(sk.SR), "-ac", "2", "-c:a", "pcm_s16le", wav], check=True)
        vol = float(t.get("volume", 0.5))
        return sk.to_stereo(sk.read_wav(wav)[0]) * vol, f"{t.get('name', last)}（卓の音量 {vol:.2f}）"
    return prev.load_score(), "HorrBGM（卓に曲が無いので既定）"


def bed_for(sec: float) -> np.ndarray:
    """**本編の背景**（劇伴 ＋ 装置の声）を、輪と同じ長さの継ぎ目なしループで返す。

    ⚠⚠ **音量の判断を無音の上でやらせないための背景。** 2026-08-23、この台で笑いの音量を
    -10.5dB と決めたが、そのとき鳴っていたのは笑いだけだった。実機では劇伴（-27.7 LUFS）と
    装置の声（-32.0）が下に居るので、**同じ音でも埋もれ方がまるで違う**。
    `rules/sound-design.md` が割れる音について書いている「⚠ 敷く音の上で聴く」と同じ罠を、
    この台が作っていた。

    ⚠ **数値（`stats`）には混ぜない。** 混ぜると音量の目安が背景こみの値になって読めなくなる。
    """
    key = round(sec, 3)
    hit = _bed_cache.get(key)
    if hit is not None:
        return hit
    n = int(sec * sk.SR)
    xf = int(ing.LOOP_XF * sk.SR)
    out = np.zeros((n, 2))
    score, _name = show_score()
    for src, gain in ((score, 1.0),
                      (sk.to_stereo(sk.read_wav(os.path.join(ROOT, "Assets", "Resources",
                                                             "Sound", "bed_device.wav"))[0]), 1.0)):
        reps = int(np.ceil((n + xf) / len(src))) + 1
        cut = np.tile(src, (reps, 1))[:n + xf]
        out += ing.fold_loop(cut)[:n] * gain
    _bed_cache[key] = out
    return out


def build(layer: str, pitch_ratio: float, formant: float, speed: float, gain_db: float,
          bed: bool = True):
    """選んだ層を、いまのパラメータで組む。**組み方は `ingest-sounds.py` のまま。**"""
    sr = sk.SR
    src = laugh_src(pitch_ratio, formant, speed)
    name, table, target = LAYERS[layer]

    if layer == "crowd":
        out, spans = ing.ring_mix(src, ing.CHORUS_VOICES, ing.CHORUS_SEC, sr)
        out = out * 10 ** ((target - sk.lufs(out)) / 20.0)
        sec, count = ing.CHORUS_SEC, len(spans)
    else:
        built = [(nm, s) + ing.ring_mix(src, voices, s, sr) for nm, s, voices in table]
        scale = 10 ** ((target - sk.lufs(built[0][2])) / 20.0)
        if len(built) == 1:
            out, sec = built[0][2] * scale, built[0][1]
            count = len(built[0][3])
        else:
            # 3 枚重ね ＝ 3 周目 C の終わり。1 枚目の輪の長さで切る（実機は別々にループする）。
            sec = built[0][1]
            n = int(sec * sr)
            out = np.zeros((n, 2))
            for _nm, s, o, _sp in built:
                reps = int(np.ceil(n / len(o))) + 1
                out += np.tile(o, (reps, 1))[:n]
            out = out * scale
            count = sum(len(sp) for _nm, _s, _o, sp in built)

    out = out * 10 ** (gain_db / 20.0)
    # ⚠ 尖頭は 1 度しか測らない。倍率は線形なので、掛けたぶんは足せば分かる。
    tp = true_peak_fast(out)
    if tp > -3.0:
        out = out * 10 ** ((-3.0 - tp) / 20.0)
        tp = -3.0

    f0, _ = pitch.f0_median(out, sr)
    lo, hi = pitch.f0_spread(out, sr)
    k = int(0.05 * sr)
    rms = np.array([np.sqrt(np.mean(out[i:i + k] ** 2)) for i in range(0, len(out) - k, k)])
    db = 20 * np.log10(np.maximum(rms, 1e-9))
    stats = {
        "layer": name,
        "sec": round(sec, 2),
        "voices": count,
        "f0": round(float(f0), 1),
        "f0_lo": round(float(lo), 1),
        "f0_hi": round(float(hi), 1),
        "lufs": round(sk.lufs(out), 1),
        "peak": round(tp, 1),
        "voiced": round(100.0 * float(np.mean(db > db.max() - 25.0))),
        "bed": bool(bed),
    }
    # ⚠ 背景は**数値を出したあと**で足す（`stats` は笑いだけの値でなければ読めない）。
    if bed:
        b = bed_for(sec)
        out = out + b[:len(out)]
        tp2 = true_peak_fast(out)
        if tp2 > -3.0:
            out = out * 10 ** ((-3.0 - tp2) / 20.0)
    return out, sr, stats


def true_peak_fast(y: np.ndarray) -> float:
    """尖頭（4 倍オーバーサンプリング）を**大きい所だけ厳密に測る**。

    ⚠ これは**この台のためだけの近道**。`soundkit.true_peak_db` は 31 秒の輪を丸ごと
    4 倍へ伸ばすので **1.0 秒**かかり、つまみを動かすたびにそれを 2 回払っていた。
    標本の山より高い所は必ず山のすぐ近くにしか無いので、**上位 1.5dB の標本の周り
    （±64 標本）だけ**を厳密に測れば同じ答えが出る。
    ⚠ **焼くときはこれを使わない**（`ingest-sounds.py` は従来どおり `soundkit` を通る）。
    """
    m = sk.to_stereo(y)
    a = np.abs(m).max(axis=1)
    top = a.max()
    if top <= 1e-9:
        return -180.0
    idx = np.flatnonzero(a >= top * 10 ** (-1.5 / 20.0))
    if len(idx) > 400:                       # 密なときは大きい順に絞る
        idx = idx[np.argsort(a[idx])[-400:]]
    out = 0.0
    for i in sorted(idx):
        seg = m[max(0, i - 64):i + 65]
        if len(seg) < 8:
            continue
        out = max(out, 10 ** (sk.true_peak_db(seg) / 20.0))
    return 20 * np.log10(max(out, 1e-9))


def wav_bytes(y: np.ndarray, sr: int) -> bytes:
    """16bit PCM の WAV をメモリ上で作る（`soundkit.write_wav` はファイル前提なので分けてある）。"""
    d = np.clip(y, -1.0, 1.0)
    pcm = (d * 32767.0).astype("<i2").tobytes()
    hdr = b"RIFF" + struct.pack("<I", 36 + len(pcm)) + b"WAVEfmt " + \
        struct.pack("<IHHIIHH", 16, 1, 2, sr, sr * 4, 4, 16) + \
        b"data" + struct.pack("<I", len(pcm))
    return hdr + pcm


def reference() -> dict:
    y, sr = sk.read_wav(CALL_WAV)
    f0, _ = pitch.f0_median(y, sr)
    src, ssr = sk.read_wav(SRC_WAV)
    raw, _ = pitch.f0_median(src, ssr)
    return {"call_f0": round(float(f0), 1), "raw_f0": round(float(raw), 1),
            "baked": ing.LAUGH_PITCH, "speed": ing.LAUGH_SPEED,
            "score": show_score()[1],
            "layers": {k: v[0] for k, v in LAYERS.items()}}


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **kw):
        super().__init__(*a, directory=HERE, **kw)

    def log_message(self, fmt, *args):
        if "/api/render" in (args[0] if args else ""):
            return
        super().log_message(fmt, *args)

    def _json(self, obj, code=200):
        b = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def do_GET(self):
        u = urllib.parse.urlparse(self.path)
        q = urllib.parse.parse_qs(u.query)

        def num(k, d):
            try:
                return float(q.get(k, [d])[0])
            except ValueError:
                return d

        if u.path == "/api/state":
            return self._json(reference())

        if u.path == "/api/call":
            y, sr = sk.read_wav(CALL_WAV)
            b = wav_bytes(y, sr)
            self.send_response(200)
            self.send_header("Content-Type", "audio/wav")
            self.send_header("Content-Length", str(len(b)))
            self.end_headers()
            return self.wfile.write(b)

        if u.path == "/api/render":
            layer = q.get("layer", ["one"])[0]
            if layer not in LAYERS:
                return self._json({"error": f"知らない層: {layer}"}, 400)
            try:
                y, sr, stats = build(layer,
                                     max(0.20, min(1.60, num("pitch", ing.LAUGH_PITCH))),
                                     max(0.0, min(1.0, num("formant", 1.0))),
                                     max(0.60, min(1.60, num("speed", 1.0))),
                                     max(-24.0, min(12.0, num("gain", 0.0))),
                                     q.get("bed", ["1"])[0] != "0")
            except Exception as e:  # 落とさない — ブラウザ側で赤く出す
                return self._json({"error": f"{type(e).__name__}: {e}"}, 500)
            b = wav_bytes(y, sr)
            self.send_response(200)
            self.send_header("Content-Type", "audio/wav")
            self.send_header("Content-Length", str(len(b)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Stats", urllib.parse.quote(json.dumps(stats, ensure_ascii=False)))
            self.end_headers()
            return self.wfile.write(b)

        if u.path == "/":
            self.path = "/index.html"
        return super().do_GET()

    def do_POST(self):
        u = urllib.parse.urlparse(self.path)
        if u.path != "/api/choose":
            return self._json({"error": "知らない口"}, 404)
        n = int(self.headers.get("Content-Length", 0))
        try:
            body = json.loads(self.rfile.read(n).decode("utf-8"))
        except Exception as e:
            return self._json({"error": str(e)}, 400)
        with io.open(CHOSEN, "w", encoding="utf-8", newline="\n") as f:
            json.dump(body, f, ensure_ascii=False, indent=2)
            f.write("\n")
        print(f"  採用: {json.dumps(body, ensure_ascii=False)}")
        return self._json({"ok": True, "path": CHOSEN})


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def main() -> int:
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8130
    r = reference()
    print(f"笑い声の調整台 → http://localhost:{port}/")
    print(f"  呼びかけ（あーそぼー） {r['call_f0']:.0f}Hz / 素材 {r['raw_f0']:.0f}Hz "
          f"/ いま焼いてある倍率 {r['baked']}")
    print(f"  敷く劇伴: {show_score()[1]}")
    print("  ⚠ 卓サーバ（8099）とは別。読むだけで show.json は書かない")
    with Server(("127.0.0.1", port), Handler) as httpd:
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            print("\n止めた")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
