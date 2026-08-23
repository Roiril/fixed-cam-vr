# -*- coding: utf-8 -*-
"""声の**高さ（基音）**を測る。笑いを呼びかけ（あーそぼー）へ寄せたかの物証。

```
py -3.11 tools/sound-pitch.py          # 焼いた音を並べて測る
py -3.11 tools/sound-pitch.py --gate   # 計器そのものを答えの分かる音で通す
```

⚠⚠ **画にも動画にも出ない。** 高さを動かしたかどうかを確かめる手段はここだけ
（`rules/sound-design.md` §7 — 音は録画に映らない）。

⚠ **計器を足したら答えの分かる入力で 1 度通す**（`~/.claude/rules/work-style.md` §2）。
`--gate` が 3 つ見る:

1. 合成した 110/220/330Hz → その値が返るか
2. 素材を既知の倍率で**遅く**読む → 倍率どおりに下がるか
   （⚠ **速く読む向きは見ない** — 上限 700Hz を超えたフレームが落ちて 8% ずれる。
   下げる向きしか使わないので、そちらだけを門にしてある）
3. 高さを下げる処理（`ingest-sounds.pitch_down`）→ 基音が倍率どおりに動き、
   **声色（共鳴）と尺は動かない**か
"""

from __future__ import annotations

import argparse
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import soundkit as sk  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SND = os.path.join(ROOT, "Assets", "Resources", "Sound")
RAW = os.path.join(ROOT, "logs", "sound", "ingest")

# (表示名, パス, 何と比べるか)
TARGETS = [
    ("呼びかけ あーそぼー", os.path.join(SND, "sfx_doll_call.wav"), "基準"),
    ("笑い 素材（生）", os.path.join(RAW, "src_bed_doll_swell.wav"), "加工前"),
    ("笑い 一人 3周A・B", os.path.join(SND, "bed_doll_one.wav"), "ここが主役"),
    ("笑い 2枚目 3周C", os.path.join(SND, "bed_dolls_grow_a.wav"), ""),
    ("笑い 3枚目 3周C", os.path.join(SND, "bed_dolls_grow_b.wav"), ""),
    ("笑い 群れ 4周A", os.path.join(SND, "bed_dolls_laugh.wav"), "8 体ぶんの幅がある"),
]


def f0_frames(y, sr: int, lo: float = 70.0, hi: float = 700.0,
              win: float = 0.040, hop: float = 0.010, floor_db: float = -40.0):
    """自己相関でフレームごとの基音を出す。返すのは (基音[], 強さ[])。

    ⚠ 声になっていないフレーム（息・余韻の尻）は捨てる。混ぜると高さが低い方へ流れる。
    """
    m = y.mean(axis=1) if y.ndim == 2 else y
    n = int(win * sr)
    h = int(hop * sr)
    lo_lag, hi_lag = int(sr / hi), int(sr / lo)
    peak = np.abs(m).max() + 1e-12
    out_f, out_w = [], []
    for s in range(0, len(m) - n, h):
        c = m[s:s + n].astype(np.float64)
        rms = np.sqrt((c ** 2).mean())
        if 20 * np.log10(rms / peak + 1e-12) < floor_db:
            continue
        c = (c - c.mean()) * np.hanning(n)
        f = np.fft.rfft(c, 4 * n)
        ac = np.fft.irfft(f * np.conj(f))[:hi_lag + 2]
        if ac[0] <= 0:
            continue
        seg = ac[lo_lag:hi_lag] / ac[0]
        if len(seg) < 4:
            continue
        k = int(np.argmax(seg))
        if seg[k] < 0.30:
            continue
        lag = float(lo_lag + k)
        if 0 < k < len(seg) - 1:      # 山の頂点を放物線で詰める
            a, b, cc = seg[k - 1], seg[k], seg[k + 1]
            lag += (a - cc) / (2 * (a - 2 * b + cc) + 1e-12)
        out_f.append(sr / lag)
        out_w.append(rms)
    return np.array(out_f), np.array(out_w)


def f0_median(y, sr: int, **kw):
    """**強さで重みを付けた**中央値。大きく鳴っている所の高さが体験の高さ。"""
    f, w = f0_frames(y, sr, **kw)
    if len(f) == 0:
        return float("nan"), 0
    o = np.argsort(f)
    f, w = f[o], w[o]
    cw = np.cumsum(w)
    return float(f[np.searchsorted(cw, cw[-1] * 0.5)]), len(f)


def f0_spread(y, sr: int, **kw):
    """下から 10% / 90% の高さ（**8 体の幅**を見る）。"""
    f, w = f0_frames(y, sr, **kw)
    if len(f) == 0:
        return float("nan"), float("nan")
    o = np.argsort(f)
    f, w = f[o], w[o]
    cw = np.cumsum(w) / w.sum()
    return float(f[np.searchsorted(cw, 0.10)]), float(f[np.searchsorted(cw, 0.90)])


def cep_env(y, sr: int, n: int = 8192, lifter: int = 168):
    """平均の包絡（倍音を均した「声色」）。返すのは (周波数, dB)。"""
    m = y.mean(axis=1)
    acc, cnt = None, 0
    for i in range(0, max(1, len(m) - n), n // 2):
        c = m[i:i + n]
        if len(c) < n or np.sqrt((c ** 2).mean()) < 1e-4:
            continue
        mag = np.abs(np.fft.rfft(c * np.hanning(n)))
        cep = np.fft.irfft(np.log(mag + 1e-9))
        cep[lifter:-lifter] = 0.0
        acc = np.exp(np.fft.rfft(cep).real) if acc is None else acc + np.exp(np.fft.rfft(cep).real)
        cnt += 1
    return np.fft.rfftfreq(n, 1 / sr), 20 * np.log10(acc / max(cnt, 1) + 1e-12)


def formants(y, sr: int, lo: float = 300.0, hi: float = 3500.0, top: int = 3):
    f, db = cep_env(y, sr)
    b = (f >= lo) & (f <= hi)
    ff, dd = f[b], db[b]
    idx = [i for i in range(1, len(dd) - 1) if dd[i] > dd[i - 1] and dd[i] >= dd[i + 1]]
    idx.sort(key=lambda i: -dd[i])
    return sorted(round(float(ff[i])) for i in idx[:top])


# ---------------------------------------------------------------- 門
def gate() -> int:
    bad = 0
    sr = sk.SR
    t = np.arange(int(sr * 1.0)) / sr
    for hz in (110.0, 220.0, 330.0):
        v = sk.to_stereo(sum(np.sin(2 * np.pi * hz * k * t) / k for k in range(1, 12)) * 0.2)
        got, _ = f0_median(v, sr)
        err = 100 * (got / hz - 1)
        ok = abs(err) < 1.0
        bad += not ok
        print(f"  [{'OK' if ok else 'NG'}] 合成 {hz:5.0f}Hz → {got:7.2f}Hz  ({err:+.2f}%)")

    y, s = sk.read_wav(os.path.join(RAW, "src_bed_doll_swell.wav"))
    base, _ = f0_median(y, s)
    for r in (0.80, 0.70, 0.55):
        got, _ = f0_median(_tape(y, r), s)
        err = 100 * (got / (base * r) - 1)
        ok = abs(err) < 3.0
        bad += not ok
        print(f"  [{'OK' if ok else 'NG'}] 素材を {r:.2f} 倍で遅く読む → {got:6.1f}Hz "
              f"（狙い {base * r:.1f}・{err:+.1f}%）")

    ing = _load_ingest()
    v = sk.to_stereo(sum(np.sin(2 * np.pi * 200 * k * t) / k for k in range(1, 40)) * 0.15)
    v = sk.biquad(v, "peak", 700.0, 5.0, 16.0)
    v = sk.biquad(v, "peak", 1900.0, 5.0, 14.0)
    f_before = formants(v, sr)
    for keep in (True, False):
        z = ing.pitch_down(v, 0.5, sr, formant=keep)
        got, _ = f0_median(z, sr, lo=60.0, hi=900.0)
        f_after = formants(z, sr)
        moved = max(abs(a / b - 1) for a, b in zip(f_after[-2:], f_before[-2:]))
        ok = abs(got / 100.0 - 1) < 0.03 and len(z) == len(v) and (moved < 0.10) == keep
        bad += not ok
        print(f"  [{'OK' if ok else 'NG'}] 合成母音 0.5 倍 声色を戻す={str(keep):5s} → "
              f"基音 {got:6.1f}Hz（狙い 100）共鳴 {f_before} → {f_after}  "
              f"尺 {len(z) / sr:.3f}s（元 {len(v) / sr:.3f}）")
    print(f"\n{'門は全部通った' if bad == 0 else f'⚠ {bad} 件落ちた'}")
    return 1 if bad else 0


def _tape(c: np.ndarray, ratio: float) -> np.ndarray:
    n = max(8, int(len(c) / ratio))
    x = np.linspace(0, len(c) - 1, n)
    return np.stack([np.interp(x, np.arange(len(c)), c[:, ch]) for ch in (0, 1)], axis=1)


def _load_ingest():
    """`ingest-sounds.py` は `-` を含むので普通の import ができない。"""
    import importlib.util
    p = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ingest-sounds.py")
    spec = importlib.util.spec_from_file_location("ingest_sounds", p)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--gate", action="store_true", help="計器そのものを答えの分かる音で通す")
    a = ap.parse_args()
    if a.gate:
        return gate()

    ref = None
    print(f"{'':22s} {'基音':>8s} {'下10%':>8s} {'上90%':>8s} {'呼びかけ比':>10s}   共鳴")
    for label, path, note in TARGETS:
        if not os.path.exists(path):
            print(f"{label:22s} 無い: {path}")
            continue
        y, s = sk.read_wav(path)
        med, n = f0_median(y, s)
        lo, hi = f0_spread(y, s)
        if ref is None:
            ref = med
        rel = med / ref
        print(f"{label:22s} {med:7.1f}Hz {lo:7.1f}Hz {hi:7.1f}Hz {rel:9.2f}x   "
              f"{formants(y, s)}  {note}")
    ing = _load_ingest()
    print(f"\nいまの値: 高さ x{ing.LAUGH_PITCH:.2f} / 速さ x{ing.LAUGH_SPEED:.2f}"
          "（`tools/ingest-sounds.py`）")
    print("⚠ **狙いの倍率は決まっていない。** 0117 は「あーそぼーくらい」＝ 呼びかけの 1.0 倍前後まで"
          "下げたが、\n   0118 で「ちょっと低すぎるかも」と退けられ、**1.8 倍前後**へ戻した"
          "（ユーザーが調整台で耳で決めた値）。\n"
          "   ここは合否を出す表ではない — 値を動かしたときに**どこへ着いたか**を見るためのもの。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
