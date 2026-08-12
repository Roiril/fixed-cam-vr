# -*- coding: utf-8 -*-
"""廻リ視のサウンド合成・解析キット（numpy だけで動く）。

**耳を使わずに音を作って判定するための道具。** 3 つの役割を持つ:

1. **合成の原器** — ノイズ・トーン・包絡・フィルタ。すべて float64 / 48kHz
2. **ループの保証** — 周波数領域で作るので**巻き戻りが数学的に継ぎ目ゼロ**（`loop_noise`）
3. **測る** — true peak / K 加重ラウドネス / 帯域配分 / 継ぎ目の段差 / モノ互換

⚠ **48000 Hz で作る。** Quest の音声機構は 48kHz で回るので、44.1kHz の素材は実行時に
リサンプルされる（CPU と品質を捨てる）。`sampleRateSetting: 0`（Preserve）のままで
48k を入れれば変換は起きない。

⚠ **情報を 150 Hz より下に置かない。** Quest 3 の内蔵スピーカーは開放型で低域がほとんど
出ない（ヘッドホンを挿せば出る）。低い方は「あれば良い」ボーナスとして扱い、
**伝えたいことは 150 Hz 〜 6 kHz に置く**。仕様は `.claude/rules/sound-design.md`。

⚠ **位相を逆にしない。** Quest の 2 つのスピーカーは頭の両脇の至近距離にあり、
逆相成分は空中で打ち消えて**消える**。`mono_compat()` で必ず確かめる。
"""

from __future__ import annotations

import math
import struct
import wave

import numpy as np

SR = 48000
"""標準サンプリング周波数。Quest の音声機構と同じ。"""

RNG_SEED = 20260812
"""既定の乱数種。**同じスクリプトは同じ波形を出す**（差分がノイズで埋まらない）。"""


# ---------------------------------------------------------------------------
# 基本波形
# ---------------------------------------------------------------------------


def t_axis(sec: float, sr: int = SR) -> np.ndarray:
    """0 から sec までの時刻軸（終端を含まない ＝ ループしても重複しない）。"""
    return np.arange(int(round(sec * sr)), dtype=np.float64) / sr


def loop_freqs(sec: float, freqs, sr: int = SR):
    """周波数をループ長の整数倍へ丸める（丸めないと巻き戻りで位相が飛ぶ）。

    ループ長 sec の信号が完全に周期的であるためには、成分の周波数が 1/sec の整数倍で
    なければならない。ここを守ると継ぎ目が**数学的に**消える（クロスフェードで隠すのではない）。
    """
    n = int(round(sec * sr))
    base = sr / n
    return [max(base, round(f / base) * base) for f in np.atleast_1d(freqs)]


def sine(sec: float, freq: float, amp: float = 1.0, phase: float = 0.0,
         sr: int = SR) -> np.ndarray:
    return amp * np.sin(2 * np.pi * freq * t_axis(sec, sr) + phase)


def loop_noise(sec: float, lo: float, hi: float, slope_db_oct: float = 0.0,
               seed: int | None = None, sr: int = SR) -> np.ndarray:
    """**継ぎ目の無い**帯域ノイズ。

    周波数領域で作って `irfft` で戻すので、区間の末尾と先頭が円環として繋がる
    ＝ ループ再生で段差が出ない。時間領域でノイズを作って端をクロスフェードする方法より
    厳密で、しかも中身を削らない。

    slope_db_oct: 1 オクターブあたりの傾き。0 = 白 / -3 = ピンク / -6 = 赤（茶）。
    """
    n = int(round(sec * sr))
    rng = np.random.default_rng(RNG_SEED if seed is None else seed)
    nf = n // 2 + 1
    f = np.arange(nf, dtype=np.float64) * sr / n
    mag = np.zeros(nf)
    band = (f >= lo) & (f <= hi)
    mag[band] = 1.0
    if slope_db_oct != 0.0:
        ref = max(lo, 20.0)
        with np.errstate(divide="ignore", invalid="ignore"):
            oct_from_ref = np.log2(np.maximum(f, 1e-9) / ref)
        mag[band] *= 10 ** (slope_db_oct * oct_from_ref[band] / 20.0)
    # 帯域の縁を滑らかに落とす（矩形だと時間領域でリンギングが出る）
    mag = _soften_band(mag, f, lo, hi)
    phase = rng.uniform(0, 2 * np.pi, nf)
    phase[0] = 0.0
    if n % 2 == 0:
        phase[-1] = 0.0
    spec = mag * np.exp(1j * phase)
    y = np.fft.irfft(spec, n)
    return _norm(y)


def _soften_band(mag: np.ndarray, f: np.ndarray, lo: float, hi: float) -> np.ndarray:
    """帯域の両端に 1/3 オクターブのレイズドコサインの傾斜を付ける。"""
    out = mag.copy()
    for edge, rising in ((lo, True), (hi, False)):
        w = edge * (2 ** (1 / 3) - 1)
        if w <= 0:
            continue
        a, b = (edge, edge + w) if rising else (edge - w, edge)
        sel = (f >= a) & (f <= b)
        if not np.any(sel):
            continue
        x = (f[sel] - a) / max(b - a, 1e-9)
        ramp = 0.5 - 0.5 * np.cos(np.pi * x)
        out[sel] = (ramp if rising else 1.0 - ramp)
    return out


def loop_lfo(sec: float, cycles: int, shape: str = "sin", phase: float = 0.0,
             sr: int = SR) -> np.ndarray:
    """ループ長ちょうどで整数回まわる低周波変調（0..1）。

    cycles を整数にすることが継ぎ目の条件。**秒で指定しない**のはそのため。
    """
    x = np.linspace(0, 1, int(round(sec * sr)), endpoint=False)
    ph = 2 * np.pi * (cycles * x) + phase
    if shape == "sin":
        return 0.5 + 0.5 * np.sin(ph)
    if shape == "tri":
        return np.abs(((cycles * x + phase / (2 * np.pi)) % 1.0) * 2 - 1)
    raise ValueError(shape)


# ---------------------------------------------------------------------------
# 包絡（一撃の音はここで形が決まる）
# ---------------------------------------------------------------------------


def env_ar(sec: float, attack: float, release: float, curve: float = 2.0,
           sr: int = SR) -> np.ndarray:
    """立ち上がり → 減衰。curve が大きいほど尻尾が速く消える。"""
    n = int(round(sec * sr))
    a = max(1, int(round(attack * sr)))
    e = np.zeros(n)
    a = min(a, n)
    e[:a] = np.linspace(0, 1, a, endpoint=False) ** 0.5
    rest = n - a
    if rest > 0:
        x = np.linspace(0, 1, rest, endpoint=False)
        tau = max(release, 1e-4) / max(sec, 1e-4)
        e[a:] = np.exp(-x / max(tau, 1e-6) * curve)
    return e


def env_fade(y: np.ndarray, fade_in: float, fade_out: float,
             sr: int = SR) -> np.ndarray:
    """端だけを等パワーで落とす（一撃の音の頭と尻のクリック止め）。"""
    out = y.copy()
    n = len(out)
    fi = min(int(round(fade_in * sr)), n)
    fo = min(int(round(fade_out * sr)), n - fi)
    if fi > 0:
        out[:fi] *= np.sin(np.linspace(0, np.pi / 2, fi))
    if fo > 0:
        out[n - fo:] *= np.cos(np.linspace(0, np.pi / 2, fo))
    return out


def sweep(sec: float, f0: float, f1: float, amp: float = 1.0,
          log: bool = True, sr: int = SR) -> np.ndarray:
    """周波数の掃引。log=True は聴感上の「等速」（オクターブ等分）。"""
    t = t_axis(sec, sr)
    if log and f0 > 0 and f1 > 0:
        k = math.log(f1 / f0)
        phase = 2 * np.pi * f0 * sec / k * (np.exp(k * t / sec) - 1)
    else:
        phase = 2 * np.pi * (f0 * t + (f1 - f0) * t * t / (2 * sec))
    return amp * np.sin(phase)


# ---------------------------------------------------------------------------
# フィルタ（biquad・RBJ クックブック）
# ---------------------------------------------------------------------------


def biquad(y: np.ndarray, kind: str, freq: float, q: float = 0.707,
           gain_db: float = 0.0, sr: int = SR) -> np.ndarray:
    b, a = _biquad_coef(kind, freq, q, gain_db, sr)
    return _lfilter(b, a, y)


def _biquad_coef(kind: str, freq: float, q: float, gain_db: float, sr: int):
    w = 2 * math.pi * freq / sr
    cw, sw = math.cos(w), math.sin(w)
    alpha = sw / (2 * q)
    A = 10 ** (gain_db / 40)
    if kind == "lp":
        b = [(1 - cw) / 2, 1 - cw, (1 - cw) / 2]
        a = [1 + alpha, -2 * cw, 1 - alpha]
    elif kind == "hp":
        b = [(1 + cw) / 2, -(1 + cw), (1 + cw) / 2]
        a = [1 + alpha, -2 * cw, 1 - alpha]
    elif kind == "bp":
        b = [alpha, 0, -alpha]
        a = [1 + alpha, -2 * cw, 1 - alpha]
    elif kind == "peak":
        b = [1 + alpha * A, -2 * cw, 1 - alpha * A]
        a = [1 + alpha / A, -2 * cw, 1 - alpha / A]
    elif kind == "hs":  # high shelf
        s = 2 * math.sqrt(A) * alpha
        b = [A * ((A + 1) + (A - 1) * cw + s),
             -2 * A * ((A - 1) + (A + 1) * cw),
             A * ((A + 1) + (A - 1) * cw - s)]
        a = [(A + 1) - (A - 1) * cw + s,
             2 * ((A - 1) - (A + 1) * cw),
             (A + 1) - (A - 1) * cw - s]
    else:
        raise ValueError(kind)
    a0 = a[0]
    return [x / a0 for x in b], [x / a0 for x in a]


def _lfilter(b, a, x: np.ndarray) -> np.ndarray:
    """2 次 IIR の直接形 II 転置（scipy が無いので自前）。"""
    y = np.empty_like(x)
    z1 = z2 = 0.0
    b0, b1, b2 = b
    a1, a2 = a[1], a[2]
    for i in range(len(x)):
        xi = x[i]
        yi = b0 * xi + z1
        z1 = b1 * xi - a1 * yi + z2
        z2 = b2 * xi - a2 * yi
        y[i] = yi
    return y


def biquad_fft(y: np.ndarray, kind: str, freq: float, q: float = 0.707,
               gain_db: float = 0.0, sr: int = SR) -> np.ndarray:
    """`biquad` と同じ特性を周波数領域で掛ける。**測るとき専用。**

    `_lfilter` は 1 標本ずつ回る Python のループなので、20 秒の素材に数本掛けると
    分の単位で待つことになる。測定は因果性を要らないので、伝達関数をそのまま掛ける。
    """
    b, a = _biquad_coef(kind, freq, q, gain_db, sr)
    n = len(y)
    w = 2 * np.pi * np.fft.rfftfreq(n, 1.0) / 1.0 * (1.0 / 1.0)
    w = 2 * np.pi * np.fft.rfftfreq(n, 1 / sr) / sr
    z = np.exp(-1j * w)
    h = (b[0] + b[1] * z + b[2] * z * z) / (1.0 + a[1] * z + a[2] * z * z)
    return np.fft.irfft(np.fft.rfft(y) * h, n)


def biquad_loop(y: np.ndarray, kind: str, freq: float, q: float = 0.707,
                gain_db: float = 0.0, sr: int = SR) -> np.ndarray:
    """ループ素材用のフィルタ。**前後に自分自身を継いでから濾し、真ん中を取る**。

    IIR は過渡応答を持つので素直に掛けると先頭が立ち上がり切らず、巻き戻りで段差になる。
    円環として濾すことで周期性を保つ。
    """
    n = len(y)
    pad = np.concatenate([y, y, y])
    out = _lfilter(*_biquad_coef(kind, freq, q, gain_db, sr), pad)
    return out[n:2 * n]


def spec_shape(y: np.ndarray, curve, sr: int = SR) -> np.ndarray:
    """周波数領域で任意の振幅特性を掛ける。**円環なのでループの周期性を壊さない。**

    IIR を回すより速く（20 秒素材で秒→ミリ秒）、しかも位相を動かさないので
    重ねた成分の位置関係が保たれる。curve は「周波数配列 → 倍率配列」の関数。
    """
    n = len(y)
    spec = np.fft.rfft(y)
    f = np.fft.rfftfreq(n, 1 / sr)
    out = spec * curve(f)
    out[0] = 0.0                      # 直流は常に捨てる（音にならず可動域だけ食う）
    return np.fft.irfft(out, n)


def tilt(f: np.ndarray, hinge: float, db_per_oct: float) -> np.ndarray:
    """`spec_shape` 用。hinge を軸に 1 オクターブあたり db_per_oct 傾ける。

    ⚠ **周波数に 20Hz の床を敷く。** 敷かないと直流の桶（f=0）で倍率が発散し、
    信号に直流が乗る（2026-08-12 実測: 傾き -1.6dB/oct で **1585 倍**・直流 0.31）。
    直流はスピーカーの可動域をただ食い潰すだけで音にならない。
    """
    with np.errstate(divide="ignore", invalid="ignore"):
        o = np.log2(np.maximum(f, 20.0) / hinge)
    return 10 ** (db_per_oct * o / 20.0)


def shelf(f: np.ndarray, freq: float, db: float, width_oct: float = 1.0) -> np.ndarray:
    """`spec_shape` 用。freq より上を db だけ持ち上げる / 下げる（滑らかな遷移）。"""
    with np.errstate(divide="ignore", invalid="ignore"):
        x = np.log2(np.maximum(f, 20.0) / freq) / max(width_oct, 1e-6)
    return 10 ** (db * (0.5 + 0.5 * np.tanh(x * 2.0)) / 20.0)


def resonance(f: np.ndarray, freq: float, db: float, q: float = 4.0) -> np.ndarray:
    """`spec_shape` 用。freq に山（谷）を作る。"""
    w = freq / max(q, 0.1)
    return 10 ** (db * np.exp(-((f - freq) / max(w, 1e-6)) ** 2) / 20.0)


# ---------------------------------------------------------------------------
# 合成の補助
# ---------------------------------------------------------------------------


def _norm(y: np.ndarray, peak: float = 1.0) -> np.ndarray:
    m = float(np.max(np.abs(y)))
    return y * (peak / m) if m > 1e-12 else y


def mix(*layers) -> np.ndarray:
    """(信号, 係数) の並びを足す。長さは最長へ揃える（短いものは頭から）。"""
    n = max(len(s) for s, _ in layers)
    out = np.zeros(n)
    for s, g in layers:
        out[:len(s)] += s * g
    return out


def decorrelate(y: np.ndarray, seed: int, above_hz: float = 350.0,
                sr: int = SR) -> np.ndarray:
    """振幅特性を保ったまま位相だけ散らした複製を作る（`above_hz` より上だけ）。

    **周波数領域で作るので円環＝ループの周期性を壊さない。**
    低い方を触らないのは、低域の位相をずらすと頭の中で音像が割れて気持ち悪くなるから。
    """
    n = len(y)
    spec = np.fft.rfft(y)
    f = np.fft.rfftfreq(n, 1 / sr)
    rng = np.random.default_rng(seed)
    ph = rng.uniform(0, 2 * np.pi, len(spec))
    blend = np.clip((np.log2(np.maximum(f, 1e-9) / above_hz)) / 1.0, 0, 1)
    out = spec * ((1 - blend) + blend * np.exp(1j * ph))
    return np.fft.irfft(out, n)


def widen(y: np.ndarray, width: float, seed: int = 7, sr: int = SR) -> np.ndarray:
    """(n,2) のステレオへ広げる。**モノにしたときの損失は必ず −3dB 以内**。

    ⚠⚠ **遅延で広げてはいけない。** 片側を数サンプル遅らせる広げ方（Haas / 疑似ステレオ）は
    モノにすると**櫛形の谷**を作る。Quest の 2 つのスピーカーは頭のすぐ両脇にあって
    空中でほぼ合成されるので、谷に当たった帯域は**実機で消える**。
    2026-08-12 の実測: 遅延 0.9ms で作った `sfx_title_out` は **モノ互換 −11.2dB**（＝ ほぼ消滅）。

    ここでは左右に**独立に位相を散らした複製**を混ぜる。無相関な 2 つの和は
    統計的に −3dB になるだけで、特定の帯域が消えることが無い。
    """
    if width <= 0:
        return np.stack([y, y], axis=1)
    w = min(width, 1.0)
    d1 = decorrelate(y, seed, sr=sr)
    d2 = decorrelate(y, seed + 9973, sr=sr)
    return np.stack([(1 - w) * y + w * d1, (1 - w) * y + w * d2], axis=1)


def pan(y: np.ndarray, p: float) -> np.ndarray:
    """振幅だけで定位させる（-1 左 / 0 中央 / +1 右）。**モノ互換を一切壊さない。**"""
    a = (p + 1) * math.pi / 4
    return np.stack([y * math.cos(a), y * math.sin(a)], axis=1)


def soft_clip(y: np.ndarray, drive_db: float = 6.0) -> np.ndarray:
    """柔らかい頭打ち。**尖頭だけを丸めてラウドネスを稼ぐ。**

    一撃の音は波高率（peak/RMS）が大きいので、尖頭を -3dBTP へ合わせると
    聴感の音量が目標より 10dB 以上足りなくなる（2026-08-12 実測: `sfx_seal_close` が
    -28.7 LUFS ＝ 目標 -17 に対し 11.7dB 不足）。ここで丸めてから正規化すると届く。

    tanh は奇数次の歪みだけを作るので、丸めが浅いうちは「歪んだ」と気づかれにくい。
    深く掛けると音色が変わるので `crest_db` の変化量で効き目を見ること。
    """
    g = 10 ** (drive_db / 20.0)
    m = float(np.max(np.abs(y)))
    if m < 1e-12:
        return y
    x = y / m
    return np.tanh(x * g) / math.tanh(g) * m


def stereo(left: np.ndarray, right: np.ndarray | None = None,
           width: float = 0.0) -> np.ndarray:
    """(n,2) のステレオへ。1 本だけ渡したら `widen` と同じ扱い。"""
    if right is None:
        return widen(left, width)
    return np.stack([left, right], axis=1)


def to_stereo(y: np.ndarray) -> np.ndarray:
    """(n,) も (n,1) も (n,2) へ。

    ⚠ **モノの WAV を読むと (n,1) で返る**（`read_wav`）。`ndim == 2` だけを見て素通しすると、
    その後の `[:, 1]` が落ちる／指標が片チャンネルぶんしか見ない。
    """
    y = np.asarray(y)
    if y.ndim == 1:
        return np.stack([y, y], axis=1)
    if y.shape[1] == 1:
        return np.repeat(y, 2, axis=1)
    return y


# ---------------------------------------------------------------------------
# 書き出し
# ---------------------------------------------------------------------------


def write_wav(path: str, y: np.ndarray, sr: int = SR, peak_db: float = -3.0,
              dither: bool = True, mono: bool = False) -> dict:
    """16bit PCM の WAV を書く。**音量は触らない**（`peak_db` は上限としてだけ効く）。

    16bit で書くのは Unity が取り込み時に Vorbis へ再圧縮するため（元を 32bit float に
    しても最終的な品質は上がらず、リポジトリだけが重くなる）。

    ⚠⚠ **書き出しで音量を作り直さない。** 2026-08-12 まで無条件に尖頭を `peak_db` へ
    正規化していて、**合成側で LUFS を揃えた意味が書き出しで全部消えていた**
    （敷く音が +14.7dB 持ち上がり、一撃と同じ高さでファイルに落ちていた）。
    合成側の指標だけを見ていると気づけない — **書いたファイルを読み直して測る**
    （`sound-lint.py`）ことでしか捕まらない類の事故。

    ⚠⚠ **3D で鳴らす音は必ず `mono=True`。** Unity の spatializer（この機は Meta XR Audio）は
    **モノのクリップしか処理しない** — ステレオのまま渡すと定位せずそのまま流れる。
    「3D にしたのに位置が動かない」の唯一の原因がこれ。
    """
    y = np.asarray(y, dtype=np.float64)
    y = y.mean(axis=1) if (mono and y.ndim == 2) else (y if mono else to_stereo(y))
    m = float(np.max(np.abs(y)))
    ceil = 10 ** (peak_db / 20)
    if m > ceil:
        y = y * (ceil / m)
    if dither:
        rng = np.random.default_rng(RNG_SEED)
        y = y + (rng.random(y.shape) - rng.random(y.shape)) / 32768.0
    q = np.clip(np.round(y * 32767.0), -32768, 32767).astype("<i2")
    with wave.open(path, "wb") as w:
        w.setnchannels(1 if mono else 2)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes(q.tobytes())
    return {"path": path, "sec": len(y) / sr, "sr": sr, "mono": mono}


def read_wav(path: str):
    with wave.open(path, "rb") as w:
        sr = w.getframerate()
        ch = w.getnchannels()
        n = w.getnframes()
        raw = w.readframes(n)
    y = np.frombuffer(raw, dtype="<i2").astype(np.float64) / 32768.0
    y = y.reshape(-1, ch)
    return y, sr


# ---------------------------------------------------------------------------
# 測る（耳の代わり）
# ---------------------------------------------------------------------------


def true_peak_db(y: np.ndarray) -> float:
    """4 倍オーバーサンプリングしたピーク（dBFS）。

    標本間のピークは標本値より高くなる。ここを見ないと、数値上 -1dB なのに
    実際の変換で歪む素材ができる。

    ⚠ **補間は周波数領域で行う**（スペクトルを 0 で伸ばして戻す ＝ 厳密な帯域制限補間）。
    2026-08-12 まで「0 を挟んで時間領域の 2 次ローパスで均す」実装だったが、
    20kHz の 4 次では 24kHz から始まる像を落とし切れず、**尖頭 -3dB の素材に +4.6dB と
    出た**。あり得ない値なので気づけたが、+0.5dB のような「もっともらしい嘘」なら通っていた。
    """
    m = to_stereo(y)
    n = len(m)
    out = 0.0
    for c in range(m.shape[1]):
        spec = np.fft.rfft(m[:, c])
        pad = np.zeros(n * 2 + 1, dtype=complex)
        pad[:len(spec)] = spec
        up = np.fft.irfft(pad, n * 4) * 4.0
        out = max(out, float(np.max(np.abs(up))))
    return 20 * math.log10(max(out, 1e-9))


def _k_weight(y: np.ndarray, sr: int = SR) -> np.ndarray:
    """ITU-R BS.1770 の K 加重（高域シェルフ +4dB ＋ 38Hz ハイパス）。"""
    a = biquad_fft(y, "hs", 1500.0, 0.707, 4.0, sr)
    return biquad_fft(a, "hp", 38.0, 0.5, 0.0, sr)


def lufs(y: np.ndarray, sr: int = SR) -> float:
    """統合ラウドネス（LUFS 近似・ゲート付き）。**素材の音量はこれで揃える。**

    ピーク合わせでは揃わない（一撃の音と持続音でピークが同じでも聴感は 20dB 違う）。
    """
    m = to_stereo(y)
    p = np.zeros(len(m))
    for c in range(m.shape[1]):
        p += _k_weight(m[:, c], sr) ** 2
    win = int(0.4 * sr)
    hop = max(1, win // 4)
    if len(p) < win:
        ms = float(np.mean(p)) if len(p) else 0.0
        return -0.691 + 10 * math.log10(max(ms, 1e-12))
    blocks = np.array([np.mean(p[i:i + win]) for i in range(0, len(p) - win, hop)])
    lv = -0.691 + 10 * np.log10(np.maximum(blocks, 1e-12))
    keep = blocks[lv > -70.0]
    if len(keep) == 0:
        return -70.0
    rel = -0.691 + 10 * math.log10(float(np.mean(keep))) - 10.0
    keep2 = blocks[(lv > -70.0) & (lv > rel)]
    use = keep2 if len(keep2) else keep
    return -0.691 + 10 * math.log10(float(np.mean(use)))


def speaker_loss_db(y: np.ndarray, sr: int = SR) -> float:
    """**Quest の内蔵スピーカーで鳴らしたとき、ラウドネスが何 dB 落ちるか。**

    Quest 3 のスピーカーは耳の脇に開いた小さなもので、200Hz より下はほとんど返さない。
    低い方に情報を置いた音は、ヘッドホンでは立派に鳴るのに**実機では消える**。

    「低域が多いか」を直接禁じるのは乱暴（部屋のトーンに唸りがあるのは自然）。
    見るべきは**残るか**なので、通した後のラウドネスと元のラウドネスを比べる。
    **-6dB より悪ければ、その音は内蔵スピーカーでは別の音になっている。**

    ⚠ これは実測した特性ではなく近似（200Hz の 2 次 ×2 ＋ 12kHz 以上の落ち）。
    ヘッドホンを繋ぐ運用ならこの物差しは効かない。
    """
    m = to_stereo(y)
    out = np.empty_like(m)
    for c in range(m.shape[1]):
        v = biquad_fft(biquad_fft(m[:, c], "hp", 200.0, 0.707, 0.0, sr),
                       "hp", 200.0, 0.707, 0.0, sr)
        out[:, c] = biquad_fft(v, "lp", 12000.0, 0.707, 0.0, sr)
    return lufs(out, sr) - lufs(m, sr)


def bands_db(y: np.ndarray, sr: int = SR) -> dict:
    """帯域ごとのエネルギー配分（dB・合計を 0 とする相対値）。

    **「暗い / 明るい」を数字で言うための表。** Quest の内蔵スピーカーが
    ほとんど返さない `sub`(<150Hz) にエネルギーが偏っていたら、その音は実機で聞こえない。
    """
    m = to_stereo(y).mean(axis=1)
    n = 1
    while n < len(m):
        n *= 2
    spec = np.abs(np.fft.rfft(m, n)) ** 2
    f = np.fft.rfftfreq(n, 1 / sr)
    edges = [("sub", 0, 150), ("low", 150, 400), ("mid", 400, 1600),
             ("high", 1600, 6000), ("air", 6000, 20000)]
    tot = float(np.sum(spec)) + 1e-12
    return {k: round(10 * math.log10(max(float(np.sum(spec[(f >= a) & (f < b)])), 1e-12) / tot), 1)
            for k, a, b in edges}


def centroid_hz(y: np.ndarray, sr: int = SR) -> float:
    """スペクトル重心。**明るさの 1 数字。**"""
    m = to_stereo(y).mean(axis=1)
    n = 1
    while n < len(m):
        n *= 2
    spec = np.abs(np.fft.rfft(m, n))
    f = np.fft.rfftfreq(n, 1 / sr)
    s = float(np.sum(spec)) + 1e-12
    return float(np.sum(f * spec) / s)


def crest_db(y: np.ndarray) -> float:
    """ピーク / 実効値。小さい ＝ 持続音、大きい ＝ 一撃。"""
    m = to_stereo(y).mean(axis=1)
    r = float(np.sqrt(np.mean(m ** 2))) + 1e-12
    return 20 * math.log10(float(np.max(np.abs(m))) / r)


def dc_offset(y: np.ndarray) -> float:
    return float(np.max(np.abs(to_stereo(y).mean(axis=0))))


def mono_compat_db(y: np.ndarray) -> float:
    """モノにしたとき何 dB 失うか。**0 に近いほど良い。**

    Quest の 2 つのスピーカーは頭のすぐ両脇にあり、逆相成分は空中で消える。
    -3dB より悪ければ位相を疑う。
    """
    m = to_stereo(y)
    st = float(np.sqrt(np.mean(m ** 2))) + 1e-12
    mo = float(np.sqrt(np.mean(m.mean(axis=1) ** 2))) + 1e-12
    return 20 * math.log10(mo / st)


def loop_seam(y: np.ndarray) -> dict:
    """巻き戻りの段差。**ループ素材の合否はここ。**

    ⚠ **段差の生の値を見ても意味が無い**（2026-08-12 に一度これで誤診した）。
    48kHz で 7kHz まで中身がある信号は、隣り合う標本どうしが元々大きく違う。
    見るべきは「巻き戻りの飛びが、**ふだんの隣どうしの差**に比べて大きいか」。

    jump  = 継ぎ目の飛び ÷ **継ぎ目の周りの**隣接標本差の実効値。
            **1.0 付近なら継ぎ目は存在しない**。3 を超えたらクリックが聞こえる
    kink  = 傾きの折れを同じ物差しで測ったもの（値が繋がっても折れていれば聞こえる）
    rms_d = 継ぎ目の前後 10ms の実効値の比（dB。素材の密度が変わっていないか）

    ⚠ **物差しは「その場の」隣接標本差**（信号全体の平均ではない）。低い音が主で
    ときどき粒が入る素材は、全体平均で割ると粒 1 つが継ぎ目の欠陥に化けて見える
    （2026-08-12 に `bed_seal` でこれを踏み、正常な素材を 2 回作り直しかけた）。
    """
    m = to_stereo(y)
    n = len(m)
    w = min(int(0.010 * SR), n // 4)
    near = np.concatenate([np.diff(m[-w:], axis=0), np.diff(m[:w], axis=0)])
    typical = float(np.sqrt(np.mean(near ** 2))) + 1e-12
    jump = float(np.max(np.abs(m[0] - m[-1]))) / typical
    kink = float(np.max(np.abs((m[0] - m[-1]) - (m[-1] - m[-2])))) / typical
    a = float(np.sqrt(np.mean(m[-w:] ** 2))) + 1e-12
    b = float(np.sqrt(np.mean(m[:w] ** 2))) + 1e-12
    return {"jump": round(jump, 2), "kink": round(kink, 2),
            "rms_d_db": round(20 * math.log10(b / a), 2)}


def describe(y: np.ndarray, sr: int = SR) -> dict:
    """1 本の音を数字で言い切る。**これが聴くことの代わり。**"""
    return {
        "sec": round(len(y) / sr, 3),
        "true_peak_db": round(true_peak_db(y), 2),
        "lufs": round(lufs(y, sr), 1),
        "crest_db": round(crest_db(y), 1),
        "centroid_hz": round(centroid_hz(y, sr)),
        "bands_db": bands_db(y, sr),
        "dc": round(dc_offset(y), 5),
        "mono_db": round(mono_compat_db(y), 2),
        "speaker_db": round(speaker_loss_db(y, sr), 1),
    }
