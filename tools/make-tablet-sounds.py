"""博士タブレットの効果音を計算で作る（LEDGER 0266 / 0267）。

    py -3.11 tools/make-tablet-sounds.py                  # 書き出し → tablet/.../web/asset/
    py -3.11 tools/make-tablet-sounds.py --check          # 書かずに測るだけ
    py -3.11 tools/make-tablet-sounds.py --preview <wav>  # 人が聴く 1 本（画面の順に並べる）

0267「本編で使う音は使わないでほしくて、モーショングラフィックスの動画を作った時みたいに、
今回用に新しく作って使って」。

- **本編（Quest）の音を 1 本も読まない。** `Assets/Resources/Sound/` には触れない
  （`verify-tablet-sound.cjs` が同梱の音と本編の音のバイト一致を落とす）
- 動画のときと同じく、**画面の動きの時刻表から音を作る**。筆の書き出し 0.9 秒、朱の下線 0.5 秒、
  角印が押される 0.51 秒、博士が闇から現れる 0.76 秒、通信が切れる 0.95 + 0.26 秒
- 安い電子音にしない（rules/sound-design.md §3.6）: 正弦波を置かない。音程は狭帯域ノイズ（`tone_band`）、
  動きは帯域ノイズの掃引（`sweep_band`）、共振は整数倍に積まない。正体を 200Hz より下に置かない
- 乱数の種は固定。同じ版なら同じ波形
- 曲は作らない（0256「BGMは、タブレット上では、軽減の画面だけ」）
- 大きさは博士の声（約 -21 LUFS）より下。いちばん大きい「通信が切れる」で声の 1dB 下
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import soundkit as sk  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "tablet" / "app" / "src" / "main" / "assets" / "web" / "asset"
SR = sk.SR
PEAK_DB = -3.0
VOICE_LUFS = -21.0   # 博士の動画 4 本の実測 -20.2〜-22.2（2026-10-16）

# 説明の終わりの時刻（signal-cut.js と対。片方だけ直すと音と画がずれる）
NOISE_SEC = 0.95
OFF_SEC = 0.26
BURSTS = [(0.50, 0.58), (0.65, 0.71), (0.79, 0.85)]
WRITE_SEC = 0.9      # title-write.js の DURATION
STAMP_IMPACT_MS = 510  # 角印のキーフレーム 42%（.25s の遅れ ＋ .62s × .42）。index.html の予約と対


def t_of(sec: float) -> np.ndarray:
    return np.arange(int(round(sec * SR))) / SR


def fit(y: np.ndarray, sec: float) -> np.ndarray:
    n = int(round(sec * SR))
    return y[:n] if len(y) >= n else np.pad(y, (0, n - len(y)))


def norm_lufs(y: np.ndarray, target: float) -> np.ndarray:
    """ラウドネスを target へ。尖頭が -3dBTP を超えるなら丸めてから合わせ直す。"""
    z = y * 10 ** ((target - sk.lufs(y)) / 20)
    drive = 0.0
    while sk.true_peak_db(z) > PEAK_DB and drive < 14.0:
        drive += 2.0
        z = sk.soft_clip(y, drive)
        z = z * 10 ** ((target - sk.lufs(z)) / 20)
    if sk.true_peak_db(z) > PEAK_DB:
        z = z * 10 ** ((PEAK_DB - sk.true_peak_db(z)) / 20)
    return z


def band(sec: float, freq: float, q: float, seed: int) -> np.ndarray:
    """短い音の共振。**1 秒ぶん作ってから切り出す。** 0.12 秒のまま `tone_band` を作ると周波数の刻みが
    8.3Hz と粗く、帯の中が数本の線に偏って発振器に聞こえた（種しだいで突出 36dB・2026-10-16）。"""
    return sk.tone_band(max(sec, 1.0), freq, q=q, seed=seed)[: int(round(sec * SR))]


def grains(sec: float, rate_hz: float, seed: int, depth: float = 0.6) -> np.ndarray:
    """乾いた筆・紙の毛羽：短い粒で振幅を細かく揺らす（同じ高さで鳴り続ける機械の音にしない）。"""
    rng = np.random.default_rng(seed)
    step = max(1, int(SR / rate_hz))
    n = int(round(sec * SR))
    g = np.repeat(1 - depth + depth * rng.random(n // step + 2), step)[:n]
    return np.convolve(g, np.ones(48) / 48, mode="same")


def lobe(t: np.ndarray, start: float, end: float, attack: float = 0.02) -> np.ndarray:
    """筆 1 画ぶんの強さ。入りは速く、抜けは筆を払うように細る。"""
    u = np.clip((t - start) / max(end - start, 1e-6), 0, 1)
    rise = np.clip((t - start) / attack, 0, 1)
    return np.where((t >= start) & (t <= end + 0.08), rise * (1 - u) ** 0.8 * (0.6 + 0.4 * np.sin(np.pi * u) ** 0.5), 0.0)


# ---------------------------------------------------------------------------
# 1 本ずつ
# ---------------------------------------------------------------------------

def title_in() -> np.ndarray:
    """題字の筆の書き出し（0.9 秒）と、書き終えた後の墨のにじみ。下で低い唸りが膨らんで引く。

    筆は 3 画（廻・リ・視）に分けて和紙を擦る。乾いた毛羽を粒で付ける。
    唸りは 196Hz の狭帯域ノイズと、整数倍を外した 1.97 倍。
    """
    sec = 2.6
    t = t_of(sec)
    paper = sk.loop_noise(sec, 900, 6500, slope_db_oct=-2.5, seed=7101)
    strokes = lobe(t, 0.00, 0.30) + lobe(t, 0.30, 0.52) * 0.8 + lobe(t, 0.50, WRITE_SEC) * 1.1
    brush = paper * strokes * grains(sec, 90, 7102, depth=0.55)
    wet = sk.loop_noise(sec, 280, 1100, slope_db_oct=-4.0, seed=7103)
    wet_env = np.clip((t - 0.7) / 0.25, 0, 1) * np.exp(-np.clip(t - 0.95, 0, None) / 0.55)
    low = sk.mix((sk.tone_band(sec, 196, q=9, seed=7104), 1.0), (sk.tone_band(sec, 196 * 1.97, q=8, seed=7105), 0.45))
    low_env = np.clip((t - 0.15) / 0.8, 0, 1) ** 1.5 * np.exp(-np.clip(t - 1.1, 0, None) / 0.6)
    y = sk.mix((brush, 1.0), (wet * wet_env, 0.22), (low * low_env, 0.5))
    return norm_lufs(sk.env_fade(y, 0.003, 0.25), -26.0)


def title_out() -> np.ndarray:
    """題字が手前へ寄りながら闇へ沈む（0.9 秒）。空気が落ちていき、最後に柔らかく着く。"""
    sec = 1.25
    t = t_of(sec)
    fall = fit(sk.sweep_band(0.95, 2600, 420, q=2.4, seed=7201, grains=120), sec)
    fall_env = np.clip(t / 0.35, 0, 1) ** 1.6 * np.clip((0.95 - t) / 0.12, 0, 1)
    thud = sk.loop_noise(sec, 170, 620, slope_db_oct=-4.0, seed=7202)
    thud_env = np.where(t >= 0.86, np.exp(-np.clip(t - 0.86, 0, None) / 0.09), 0.0) * np.clip((t - 0.86) / 0.004, 0, 1)
    body = sk.tone_band(sec, 233, q=7, seed=7203) * np.where(t >= 0.86, np.exp(-np.clip(t - 0.86, 0, None) / 0.16), 0.0)
    y = sk.mix((fall * fall_env, 1.0), (thud * thud_env, 0.8), (body, 0.45))
    return norm_lufs(sk.env_fade(y, 0.004, 0.08), -28.0)


def choice(variant: int) -> np.ndarray:
    """朱の筆の下線（0.5 秒の ease-out）。筆を払う短い擦れ。2 種を交互に鳴らす。"""
    sec = 0.32
    t = t_of(sec)
    lo, hi = ((1400, 6200), (1150, 5200))[variant]
    paper = sk.loop_noise(sec, lo, hi, slope_db_oct=-2.0, seed=7301 + variant)
    # ease-out の速さで擦る：入りが強く、払って細る
    u = np.clip(t / 0.26, 0, 1)
    env = np.clip(t / 0.006, 0, 1) * (1 - u) ** 2.2
    body = band(sec, (820, 760)[variant], 6, 7303 + variant) * np.exp(-t / 0.05)
    y = sk.mix((paper * env * grains(sec, 140, 7305 + variant, depth=0.4), 1.0), (body, 0.25))
    return norm_lufs(sk.env_fade(y, 0.001, 0.04), -30.0)


def key(variant: int) -> np.ndarray:
    """押す所（輪と矢印）。木の札を指で軽く叩く。3 種を順に。共振は 1 : 2.31 で整数倍を外す。"""
    sec = 0.12
    t = t_of(sec)
    f = (1080.0, 1190.0, 1310.0)[variant]
    knock = sk.loop_noise(sec, 450, 3200, slope_db_oct=-3.0, seed=7401 + variant) * np.exp(-t / 0.012)
    # 実物の木の鳴りは帯が広く短い。細く長く鳴らすと減衰する純音（発振器）に聞こえる（q 7 で突出 34dB だった）
    wood = sk.mix((band(sec, f, 3.5, 7404 + variant), 1.0),
                  (band(sec, f * 2.31, 3.0, 7407 + variant), 0.35)) * np.exp(-t / 0.022)
    y = sk.mix((knock, 1.0), (wood, 0.4))
    return norm_lufs(sk.env_fade(y, 0.0005, 0.02), -32.0)


def send() -> np.ndarray:
    """この設定を送る（円相が回り始める）。紙が机の上を滑って向こうへ渡る。"""
    sec = 0.62
    t = t_of(sec)
    slide = fit(sk.sweep_band(0.5, 950, 2300, q=2.8, seed=7501, grains=90), sec)
    env = np.clip(t / 0.04, 0, 1) * np.clip((0.5 - t) / 0.3, 0, 1) ** 1.2
    tap = sk.loop_noise(sec, 500, 2600, slope_db_oct=-3.0, seed=7502) * np.exp(-t / 0.014)
    y = sk.mix((slide * env * grains(sec, 70, 7503, depth=0.35), 1.0), (tap, 0.6))
    return norm_lufs(sk.env_fade(y, 0.0005, 0.06), -29.0)


def stamp() -> np.ndarray:
    """研究所の角印が紙に押される。木の柄が机を打つ鈍い胴と、紙が叩かれる乾いた音。"""
    sec = 0.42
    n = int(sec * SR)
    body = sk.mix(
        (sk.loop_noise(sec, 190, 760, slope_db_oct=-3.0, seed=6101) * sk.env_ar(sec, 0.002, 0.075, curve=2.6), 1.0),
        (sk.tone_band(sec, 262, q=9, seed=6102) * sk.env_ar(sec, 0.003, 0.13, curve=2.2), 0.55),
        (sk.tone_band(sec, 262 * 1.97, q=8, seed=6103) * sk.env_ar(sec, 0.002, 0.09, curve=2.4), 0.35),
        (sk.tone_band(sec, 262 * 3.06, q=7, seed=6104) * sk.env_ar(sec, 0.002, 0.06, curve=2.6), 0.2),
    )
    slap = sk.loop_noise(sec, 1800, 7000, slope_db_oct=-2.0, seed=6105) * sk.env_ar(sec, 0.0008, 0.018, curve=3.0)
    rub = sk.loop_noise(sec, 900, 4200, slope_db_oct=-3.0, seed=6106)
    t = sk.t_axis(sec)
    rub_env = np.clip((t - 0.05) / 0.03, 0, 1) * np.exp(-np.clip(t - 0.08, 0, None) / 0.07)
    y = sk.mix((body, 1.0), (slap, 0.45), (rub * rub_env, 0.08))
    y = sk.env_fade(y[:n], 0.0005, 0.06)
    return norm_lufs(y, -26.0)


def screen_on() -> np.ndarray:
    """博士が闇から現れる（0.76 秒で溶けて出る）。暗い息のような膨らみと、フィルムが回り出すパチつき。"""
    sec = 1.7
    t = t_of(sec)
    breath = sk.loop_noise(sec, 170, 1300, slope_db_oct=-4.0, seed=7601)
    breath_env = np.clip(t / 0.7, 0, 1) ** 1.8 * np.exp(-np.clip(t - 0.75, 0, None) / 0.45)
    carrier = sk.mix((sk.tone_band(sec, 233, q=12, seed=7602), 1.0), (sk.tone_band(sec, 233 * 2.71, q=9, seed=7603), 0.35))
    carrier_env = np.clip((t - 0.1) / 0.6, 0, 1) * np.exp(-np.clip(t - 0.8, 0, None) / 0.5)
    # パチつき：疎らな粒。最初の 0.6 秒に寄せて、だんだん減る
    rng = np.random.default_rng(7604)
    crackle = np.zeros(len(t))
    for _ in range(26):
        at = int(abs(rng.normal(0, 0.28)) * SR)
        if at < len(t) - 400:
            crackle[at: at + 240] += rng.choice([-1, 1]) * np.exp(-np.arange(240) / 30) * (0.4 + 0.6 * rng.random())
    crackle = sk.biquad(crackle, "hp", 1500)
    y = sk.mix((breath * breath_env, 1.0), (carrier * carrier_env, 0.4), (crackle, 0.35))
    return norm_lufs(sk.env_fade(y, 0.01, 0.2), -27.0)


def chapter(variant: int) -> np.ndarray:
    """章の切り替え（直前の絵が溶ける）。調書を 1 枚めくる。3 種を順に。"""
    sec = 0.46
    t = t_of(sec)
    f0, f1 = ((620, 3100), (700, 2800), (560, 3400))[variant]
    flip = fit(sk.sweep_band(0.4, f0, f1, q=1.8, seed=7701 + variant, grains=80), sec)
    env = np.sin(np.pi * np.clip(t / 0.4, 0, 1)) ** 1.3
    flutter = grains(sec, 55, 7704 + variant, depth=0.5)
    y = flip * env * flutter
    return norm_lufs(sk.env_fade(y, 0.002, 0.05), -32.0)


def intensity(p: float) -> float:
    """signal-cut.js の intensity と同じ形（画の砂嵐の濃さ）。"""
    i = p ** 1.7 * 0.75
    for a, b in BURSTS:
        if a <= p < b:
            i += 0.38
    if p > 0.88:
        i = 1.0
    return min(1.0, i)


def signal_cut() -> tuple[np.ndarray, np.ndarray]:
    """説明の終わり。止まった博士に砂嵐が乗り、ざ・ざ・ざと強まり、画が線へ吸い込まれて黙る。

    砂嵐は一から作る（受信機が掴めない帯域ノイズ ＋ パチつき）。濃さは画と同じ曲線。
    潰れる段（0.95〜1.21 秒）は帯域ノイズが高い方から一気に落ちる。黒の中に鈍い響きが残る。
    戻り値は (全体, 砂嵐の部分)。砂嵐の部分は書き出す前に合成音の線で測る。
    """
    sec = 2.0
    t = t_of(sec)
    hiss = sk.loop_noise(sec, 260, 9500, slope_db_oct=-1.4, seed=7801)
    rng = np.random.default_rng(7802)
    crackle = np.zeros(len(t))
    for i in range(int(NOISE_SEC * 420)):
        at = int(rng.random() ** 0.6 * NOISE_SEC * SR)   # 終わりへ向けて密になる
        if at < len(t) - 200:
            crackle[at: at + 160] += rng.choice([-1, 1]) * np.exp(-np.arange(160) / 18) * rng.random()
    crackle = sk.biquad(crackle, "hp", 1200)
    hum = sk.tone_band(sec, 245, q=5, seed=7803)
    p = np.clip(t / NOISE_SEC, 0, 1)
    env = np.array([intensity(x) for x in p])
    env = np.maximum(env, 0.05) * grains(sec, 33, 7804, depth=0.5)
    dark = sk.biquad(hiss, "lp", 1700)
    static = dark * (1 - env) + hiss * env
    noise = sk.mix((static, 1.0), (crackle, 0.5 * env.mean() + 0.2), (hum, 0.18)) * env
    noise *= np.where(t < NOISE_SEC, 1.0, 0.0)
    # 潰れる段：画が線へ吸い込まれる。帯域が高い方から一気に落ちる
    suck = fit(np.pad(sk.sweep_band(OFF_SEC, 4200, 260, q=2.6, seed=7805, grains=60), (int(NOISE_SEC * SR), 0)), sec)
    suck_env = np.where((t >= NOISE_SEC) & (t < NOISE_SEC + OFF_SEC),
                        (1 - np.clip((t - NOISE_SEC) / OFF_SEC, 0, 1)) ** 0.7, 0.0)
    crack = sk.loop_noise(sec, 1500, 8000, slope_db_oct=-1.0, seed=7806) * np.where(
        t >= NOISE_SEC, np.exp(-np.clip(t - NOISE_SEC, 0, None) / 0.012), 0.0)
    # 黒の中に残る鈍い響き（低い胴は 160Hz より上）
    thoom = sk.loop_noise(sec, 160, 560, slope_db_oct=-4.0, seed=7807) * np.where(
        t >= NOISE_SEC + OFF_SEC, np.exp(-np.clip(t - NOISE_SEC - OFF_SEC, 0, None) / 0.28), 0.0) * np.clip(
        (t - NOISE_SEC - OFF_SEC) / 0.01, 0, 1)
    head = noise[: int(NOISE_SEC * SR)].copy()
    y = sk.mix((noise, 1.0), (suck * suck_env, 0.75), (crack, 0.5), (thoom, 0.42))
    y = sk.env_fade(y, 0.004, 0.08)
    scale = 10 ** ((-22.0 - sk.lufs(y)) / 20)
    y *= scale
    tp = sk.true_peak_db(y)
    if tp > PEAK_DB:
        y *= 10 ** ((PEAK_DB - tp) / 20)
    return y, head


SOUNDS = {
    # 名前: [(ファイル, 作り方)]。複数なら鳴らす側が順に回す（乱数で選ばない）
    "title-in": [("tablet_title_in.wav", title_in)],
    "title-out": [("tablet_title_out.wav", title_out)],
    "choice": [(f"tablet_choice_{i + 1}.wav", lambda i=i: choice(i)) for i in range(2)],
    "key": [(f"tablet_key_{i + 1}.wav", lambda i=i: key(i)) for i in range(3)],
    "send": [("tablet_send.wav", send)],
    "stamp": [("tablet_stamp.wav", stamp)],
    "screen-on": [("tablet_screen_on.wav", screen_on)],
    "chapter": [(f"tablet_chapter_{i + 1}.wav", lambda i=i: chapter(i)) for i in range(3)],
    "signal-cut": [("tablet_signal_cut.wav", lambda: signal_cut()[0])],
}
PURPOSE = {
    "title-in": "題字の筆の書き出し（3 画の擦れ・墨のにじみ・低い唸り）",
    "title-out": "題字が闇へ沈む（落ちていく空気と柔らかな着地）",
    "choice": "言語・軽減を選び替えた（朱の筆の下線を払う音・2 種を交互）",
    "key": "次へ・戻る・一時停止など（木の札を叩く・3 種を順に）",
    "send": "この設定を送る（紙が滑って渡る）",
    "stamp": "反映の判子（印が押される瞬間）",
    "screen-on": "博士が闇から現れる（暗い息とフィルムのパチつき）",
    "chapter": "章の切り替え（調書をめくる・3 種を順に）",
    "signal-cut": "説明の終わり（砂嵐 → 線へ吸い込まれる → 闇の中の響き・signal-cut.js と同じ時刻）",
}

# 試聴の並び（秒・名前）。タブレットで実際に起きる順と間
PREVIEW = [
    (0.3, "title-in"), (4.0, "title-out"),
    (6.0, "choice"), (6.8, "choice"), (8.0, "key"),
    (9.5, "choice"), (10.4, "choice"), (11.6, "key"),
    (13.0, "send"), (14.5 + STAMP_IMPACT_MS / 1000, "stamp"),
    (17.0, "screen-on"), (20.0, "chapter"), (23.0, "chapter"),
    (26.0, "signal-cut"),
]


def measure(y: np.ndarray) -> dict:
    return {
        "sec": round(len(y) / SR, 3),
        "lufs": round(sk.lufs(y), 1),
        "truePeakDb": round(sk.true_peak_db(y), 1),
        "speakerDb": round(sk.speaker_loss_db(y), 1),
    }


def write_preview(path: Path, rendered: dict) -> None:
    out = np.zeros(int(30.0 * SR))
    turns: dict[str, int] = {}
    for at, name in PREVIEW:
        files = rendered[name]
        y = files[turns.get(name, 0) % len(files)]
        turns[name] = turns.get(name, 0) + 1
        start = int(at * SR)
        out[start: start + len(y)] += y[: len(out) - start]
    path.parent.mkdir(parents=True, exist_ok=True)
    sk.write_wav(str(path), out, mono=True)
    print(f"preview {path}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="書かずに測るだけ")
    parser.add_argument("--preview", help="人が聴く 1 本（画面の順に並べる）の書き出し先")
    args = parser.parse_args()

    # 砂嵐の部分だけは合成音の線で先に測る（全体は sound-lint が見る）
    _, head = signal_cut()
    assert sk.tone_purity(head) <= 0.60, f"砂嵐に発振器が居る（純度 {sk.tone_purity(head):.2f}）"
    assert sk.tonality_db(head) <= 25.0, f"砂嵐に突出したトーン（{sk.tonality_db(head):.1f}dB）"

    manifest = {
        "version": 2,
        "note": "tools/make-tablet-sounds.py が計算で書く。本編（Quest）の音は使わない（LEDGER 0267）。手で直さない",
        "voiceLufs": VOICE_LUFS,
        "sounds": {},
    }
    rendered: dict[str, list[np.ndarray]] = {}
    for name, entries in SOUNDS.items():
        files = []
        rendered[name] = []
        for file, make in entries:
            y = make()
            info = measure(y)
            assert info["truePeakDb"] <= PEAK_DB + 0.05, f"{file} の尖頭 {info['truePeakDb']}dBTP"
            rendered[name].append(y)
            files.append({"file": file, "gainDb": 0.0, "sourceLufs": info["lufs"], "sec": info["sec"]})
            if not args.check:
                sk.write_wav(str(OUT / file), y, mono=True)
            print(f"{name:11s} {file:26s} {info}")
        manifest["sounds"][name] = {"purpose": PURPOSE[name], "files": files}

    loudest = max(f["sourceLufs"] + f["gainDb"] for s in manifest["sounds"].values() for f in s["files"])
    assert loudest < VOICE_LUFS, f"博士の声（{VOICE_LUFS}）より大きい音がある: {loudest}"
    if args.preview:
        write_preview(Path(args.preview), rendered)
    if not args.check:
        path = OUT / "tablet-sounds.json"
        tmp = path.with_suffix(".json.tmp")
        tmp.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
        json.loads(tmp.read_text(encoding="utf-8"))
        os.replace(tmp, path)
        print(f"wrote {path.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
