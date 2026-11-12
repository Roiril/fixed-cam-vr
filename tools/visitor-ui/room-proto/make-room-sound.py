"""博士の部屋の試作の音を作る。

    py -3.11 tools/visitor-ui/room-proto/make-room-sound.py gen     物が動く 1 発の音を ElevenLabs の効果音生成で作る（sound/src/。在る物は作り直さない）
                                                                    鳴り続ける音（部屋・テレビ・砂嵐・低い圧）は mix が白色雑音から計算で作る
    py -3.11 tools/visitor-ui/room-proto/make-room-sound.py mix     時刻表（frames/meta）どおりに並べて 1 本にする → layers/stem.mp3

タブレットの音の決まり（rules/sound-design.md の頭）に合わせる: ノイズかカチカチだけ。音程のある音・掃引・曲は置かない。
大きさは博士の声（約 -21 LUFS）より下。乱れの音は文と文の間に置く（0332）。
鍵は環境変数 ELEVENLABS_API_KEY（値は表示しない）。
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
SRC = HERE / "sound" / "src"
SR = 44100

# 計算で作る音（鳴り続けるもの）。ElevenLabs の版は 200Hz の唸りや 4.4kHz の細い山が乗っていたので使わない。
# 白色雑音から帯を切るだけなので、音程は原理的に出ない
SYNTH = ("room", "tvhiss", "static", "rumble")

# ElevenLabs で作る音（物が動く 1 発の音）。名前: (英語の指示, 秒, 繰り返し用か)
SOUNDS = {
    "tvon": ("An old CRT television being switched on in a quiet room: a heavy plastic power button clunk, then a deep electrical thump "
             "and a brief crackle of static that settles.", 2.5, False),
    "channel1": ("Turning the mechanical channel dial of an old analog television one step: one solid plastic clunk followed by a very short burst of static noise. Dry and close.", 1.0, False),
    "channel2": ("A heavy mechanical television channel selector knob clicking over once, with a tiny crackle of analog static. Dry, close microphone.", 1.0, False),
    "channel3": ("One firm clack of an old television tuner dial, then a split second of white noise fizz. Dry and close.", 1.0, False),
    "vhs": ("A VHS cassette being pushed into an old video deck: plastic clack, the mechanism pulls the tape in, a motor whirr starts, then quiet tape hiss.", 3.0, False),
    "switch": ("A single old toggle light switch being flipped on in a silent empty room. One sharp clack with a short natural room echo.", 1.0, False),
    "tvoff": ("An old CRT television being switched off: a button click, a short electrical discharge pop and thump, then silence.", 1.5, False),
}


def api_key() -> str:
    key = os.environ.get("ELEVENLABS_API_KEY")
    if not key:
        try:
            import winreg
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as k:
                key = winreg.QueryValueEx(k, "ELEVENLABS_API_KEY")[0]
        except OSError:
            key = None
    if not key:
        raise SystemExit("ELEVENLABS_API_KEY が無い")
    return key


def gen() -> None:
    SRC.mkdir(parents=True, exist_ok=True)
    for name, (text, sec, loop) in SOUNDS.items():
        out = SRC / f"{name}.mp3"
        if out.exists() and out.stat().st_size > 2000:
            print(f"在る  {name}")
            continue
        body = {"text": text, "duration_seconds": sec, "prompt_influence": 0.5}
        if loop:
            body["loop"] = True
        req = urllib.request.Request(
            "https://api.elevenlabs.io/v1/sound-generation?output_format=mp3_44100_128",
            data=json.dumps(body).encode("utf-8"),
            headers={"xi-api-key": api_key(), "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=180) as res:
                data = res.read()
        except urllib.error.HTTPError as e:
            raise SystemExit(f"{name}: HTTP {e.code} {e.read()[:400].decode('utf-8', 'replace')}")
        tmp = out.with_suffix(".tmp")
        tmp.write_bytes(data)
        tmp.replace(out)
        print(f"作った {name}  {len(data) / 1000:.0f}KB")


def load(name: str) -> np.ndarray:
    r = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-i", str(SRC / f"{name}.mp3"),
                        "-f", "f32le", "-ac", "2", "-ar", str(SR), "-"], capture_output=True)
    if r.returncode != 0:
        raise SystemExit(f"{name}: {r.stderr.decode('utf-8', 'replace')[:300]}")
    return np.frombuffer(r.stdout, dtype=np.float32).reshape(-1, 2).astype(np.float64)


def rms_db(x: np.ndarray) -> float:
    return 20 * np.log10(max(1e-9, float(np.sqrt(np.mean(x ** 2)))))


def set_level(x: np.ndarray, db: float) -> np.ndarray:
    """鳴っている所（いちばん大きい 0.4 秒）の大きさを db に合わせる"""
    win = int(SR * 0.4)
    if len(x) <= win:
        ref = rms_db(x)
    else:
        c = np.concatenate([[0.0], np.cumsum((x ** 2).mean(axis=1))])   # 畳み込みは長い音で分単位かかった。累積和で窓の平均を出す
        e = (c[win:] - c[:-win]) / win
        ref = 10 * np.log10(max(1e-12, float(e.max())))
    return x * 10 ** ((db - ref) / 20)


def band(x: np.ndarray, lo: float = 0.0, hi: float = 0.0) -> np.ndarray:
    """帯を切る（lo より下・hi より上を落とす。なだらかに）"""
    spec = np.fft.rfft(x, axis=0)
    f = np.fft.rfftfreq(len(x), 1 / SR)
    g = np.ones_like(f)
    if lo:
        g *= 1 / np.sqrt(1 + (lo / np.maximum(f, 1e-6)) ** 4)
    if hi:
        g *= 1 / np.sqrt(1 + (f / hi) ** 4)
    return np.fft.irfft(spec * g[:, None], n=len(x), axis=0)


def looped(x: np.ndarray, n: int) -> np.ndarray:
    """n 標本ぶん繰り返す。継ぎ目は 0.5 秒重ねて溶かす"""
    xf = int(SR * 0.5)
    body = x[: len(x) - xf].copy()
    ramp = np.linspace(0, 1, xf)[:, None]
    body[:xf] = body[:xf] * ramp + x[len(x) - xf:] * (1 - ramp)
    reps = int(np.ceil(n / len(body)))
    return np.tile(body, (reps, 1))[:n]


def fade(x: np.ndarray, fin: float = 0.0, fout: float = 0.0) -> np.ndarray:
    x = x.copy()
    a = min(len(x), int(SR * fin))
    b = min(len(x), int(SR * fout))
    if a:
        x[:a] *= np.linspace(0, 1, a)[:, None] ** 2
    if b:
        x[-b:] *= np.linspace(1, 0, b)[:, None] ** 2
    return x


def synth(name: str, n: int, seed: int) -> np.ndarray:
    """鳴り続ける音を白色雑音から作る（左右は別の乱数）"""
    rng = np.random.default_rng(seed)
    w = rng.standard_normal((n, 2))
    t = np.arange(n)[:, None] / SR
    if name == "room":          # 部屋の空気。低い空調のうねりと、ごく薄いさらさら
        x = band(w, hi=160) * 6 + band(rng.standard_normal((n, 2)), lo=300, hi=2200) * 0.12
        return x * (1 + 0.1 * np.sin(2 * np.pi * 0.07 * t + 1.0))
    if name == "tvhiss":        # 点いているテレビのさーっという音（唸りは入れない）
        return band(w, lo=900, hi=5500)
    if name == "static":        # 砂嵐
        return band(w, lo=180, hi=9000)
    if name == "rumble":        # 低い圧。90Hz より下だけ
        x = band(w, hi=85)
        return x * (0.75 + 0.25 * np.sin(2 * np.pi * 0.11 * t))
    raise SystemExit(f"計算で作る音に無い名前: {name}")


def mix() -> None:
    meta = json.loads((HERE / "frames/meta/f00000.jpg").read_text(encoding="utf-8"))
    total = int(SR * (meta["total"] / 1000 + 0.5))
    out = np.zeros((total, 2))
    cache: dict[str, np.ndarray] = {}
    report: list[str] = []

    def src(name: str) -> np.ndarray:
        if name not in cache:
            cache[name] = load(name)
        return cache[name]

    def put(x: np.ndarray, at_ms: float) -> None:
        i = int(SR * at_ms / 1000)
        if i < 0:
            x = x[-i:]
            i = 0
        n = min(len(x), total - i)
        if n > 0:
            out[i:i + n] += x[:n]

    for k, ev in enumerate(meta["sound"]):
        name = ev["name"]
        if name in SYNTH:
            x = synth(name, int(SR * ev["dur"] / 1000), seed=1000 + k)
        else:
            x = src(name)
            if "dur" in ev:
                n = int(SR * ev["dur"] / 1000)
                x = looped(x, n) if ev.get("loop") else x[:n]
        if ev.get("lo") or ev.get("hi"):
            x = band(x, ev.get("lo", 0), ev.get("hi", 0))
        if ev.get("swell"):          # 小さく始まって膨らむ（終わりの砂嵐）
            x = x * (np.linspace(0.05, 1, len(x))[:, None] ** 2.2)
        x = set_level(x, ev["db"])
        if name not in SYNTH:
            # カチッという音は一瞬だけ鋭く尖る（0.4 秒の平均を -30 に合わせても尖頭は -2 だった）。尖頭を -12 で抑える
            pk = float(np.abs(x).max())
            if pk > 10 ** (-12 / 20):
                x = x * (10 ** (-12 / 20) / pk)
        x = fade(x, ev.get("fin", 0.004), ev.get("fout", 0.03))
        put(x, ev["t"])
        report.append(f"  {ev['t'] / 1000:7.2f} 秒  {name:9s} {ev['db']:4.0f} dB  尖頭 {20 * np.log10(max(1e-9, float(np.abs(x).max()))):6.1f}")

    peak = float(np.abs(out).max())
    if peak > 0.89:
        out *= 0.89 / peak
    wav = HERE / "sound" / "stem.f32"
    out.astype(np.float32).tofile(str(wav))
    dst = HERE / "layers" / "stem.mp3"
    tmp = dst.with_suffix(".tmp.mp3")
    r = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "f32le", "-ac", "2", "-ar", str(SR), "-i", str(wav),
                        "-c:a", "libmp3lame", "-b:a", "160k", str(tmp)])
    wav.unlink()
    if r.returncode != 0:
        sys.exit(r.returncode)
    tmp.replace(dst)
    sys.stdout.reconfigure(encoding="utf-8")
    for line in report:
        print(line)
    print(f"{dst}  {meta['total'] / 1000:.1f} 秒  {len(meta['sound'])} 件  全体 {rms_db(out):.1f} dBFS  尖頭 {20 * np.log10(max(1e-9, float(np.abs(out).max()))):.1f} dBFS")


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    if cmd == "gen":
        gen()
    elif cmd == "mix":
        mix()
    else:
        sys.exit(__doc__)
