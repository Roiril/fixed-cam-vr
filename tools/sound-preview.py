# -*- coding: utf-8 -*-
"""**人が聴くための** 2 本を書き出す。

```
py -3.11 tools/sound-preview.py
```

出るもの（`logs/sound/`）:
  - `preview_materials.wav` … 素材を 1 本ずつ並べたもの（何がどんな音かを確かめる）
  - `preview_intro.wav` … 真っ暗 → A → 題字 → 導入 → 本編の入り口を通しで並べたもの（**流れ**）
  - どちらも波形＋スペクトログラムの PNG 付き

⚠⚠ **これは実機ではない。合否に使わない。**
実機の混ざり方は `SoundBedLogic` が毎フレーム決めていて、ここはその**近似を Python で並べ直した別物**。
卓と実機は既に 5 件食い違っている（`memory/sim_device_divergence.md`）ので、代理を厚くしない。
ここが果たす役割は 1 つだけ — **耳が聞こえないシュビーが作った音を、人が聴いて赤を入れられる形にすること**。

⚠ 段の尺は `IntroTiming.Default`（C# 側）から手で写している。**向こうを変えたらここも直す。**
"""

from __future__ import annotations

import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import soundkit as sk  # noqa: E402
import importlib  # noqa: E402

lint = importlib.import_module("sound-lint")

sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "Assets", "Resources", "Sound")
OUT = os.path.join(ROOT, "logs", "sound")

# --- 段の尺（`IntroLogic.IntroTiming.Default` の写し）------------------------
# ⚠ 2026-08-13 に導入を作り直した（`canon/LEDGER.md` 0023 ④）。旧 5 段
#   （Real / Degrade / Structure / Frame / Swap）はもう無い。いまは 4 段 6.2 秒:
#   段 1 閉じる / 段 2 闇（＋中に入るのを待つ）/ 段 3 管が点く / 段 4 自分が映る。
#   ⚠ `IntroTiming.Default` と同じ値にしておく（ずれると、聴いて決めた間が実機と違う）。
SEAL, DARK, IGNITE, LIVE = 1.4, 0.8, 1.6, 2.4
DARK_WAIT = 1.6          # 中に入るのを待つ時間（実機は上限 3 秒。近似として真ん中を置く）
# ⚠ `SoundCueLogic.ScreenNoiseAt` と同じ値（管の面が満ち始める所）。対で直す。
SCREEN_NOISE_AT = 0.55

MATERIALS = [
    ("bed_seal", "封印の箱の唸り（3D・箱に定位）", 6.0),
    ("bed_room", "隔離された部屋のトーン", 6.0),
    ("bed_device", "装置の声・新しい（1 周目）", 6.0),
    ("bed_device_worn", "装置の声・痩せた（3 周目）", 6.0),
    ("bed_static", "信号断の砂嵐", 4.0),
    ("sfx_title_in", "A で題字が立つ（もらった「シネマチックなタイトル」）", 0),
    ("sfx_title_out", "2 秒後に題字が消える（息を呑む）", 0),
    ("sfx_seal_close", "段 1 — 隔離が閉じる（もらった「黒い中に入るときの金属音」）", 0),
    ("sfx_shatter", "段 4 — 割れて吸い込まれる", 0),
    ("sfx_swap", "段 5 — 装置が点く", 0),
    ("sfx_switch_1", "カメラ切替（リレー）1", 0),
    ("sfx_switch_2", "カメラ切替（リレー）2", 0),
    ("sfx_switch_3", "カメラ切替（リレー）3", 0),
    ("sfx_glitch_1", "映像の乱れ 1", 0),
    ("sfx_glitch_2", "映像の乱れ 2", 0),
    ("sfx_glitch_3", "映像の乱れ 3", 0),
    ("sfx_shell_open", "終幕 — 隔離が開いて現実が戻る", 0),
    ("amb_creak_1", "家鳴り 1（もらった「軋み」）", 0),
    ("amb_creak_2", "家鳴り 2（もらった「少し重い軋み」）", 0),
    ("amb_bell", "鈴（もらった「鈴２」）— 段 0 に 1 回だけ", 0),
]


def load(name: str) -> np.ndarray:
    y, _ = sk.read_wav(os.path.join(SRC, f"{name}.wav"))
    return sk.to_stereo(y)


def tile(y: np.ndarray, sec: float) -> np.ndarray:
    """ループ素材を必要な長さまで繰り返す（**継ぎ目の確認も兼ねる**）。"""
    n = int(sec * sk.SR)
    reps = int(np.ceil(n / len(y)))
    return np.tile(y, (reps, 1))[:n]


def lay(dst: np.ndarray, src: np.ndarray, at_sec: float, gain=1.0):
    """`gain` はスカラでも包絡（長さ可変の配列）でもよい。"""
    i = int(at_sec * sk.SR)
    n = min(len(src), len(dst) - i)
    if n <= 0:
        return
    g = gain
    if isinstance(gain, np.ndarray):
        g = np.interp(np.linspace(0, 1, n), np.linspace(0, 1, len(gain)), gain)[:, None]
    dst[i:i + n] += src[:n] * g


def ramp(n: int, a: float, b: float, curve: str = "equal") -> np.ndarray:
    t = np.linspace(0, 1, n)
    if curve == "equal":
        f = np.sin(t * np.pi / 2) ** 2 if b > a else np.cos(t * np.pi / 2) ** 2
        f = f if b > a else f
        return a + (b - a) * (np.sin(t * np.pi / 2) if b > a else 1 - np.sin(t * np.pi / 2))
    return a + (b - a) * t


def build_materials() -> np.ndarray:
    parts = []
    gap = np.zeros((int(0.55 * sk.SR), 2))
    for name, _label, sec in MATERIALS:
        y = load(name)
        parts.append(tile(y, sec) if sec > 0 else y)
        parts.append(gap)
    return np.concatenate(parts)


def build_intro() -> np.ndarray:
    """周回リセット → A → 題字 → 導入 4 段 → 本編の入り口までを、実機の順序どおりに並べる（近似）。

    ⚠ 2026-08-12 に流れが変わった（`canon/LEDGER.md` 0014）。
    **周回リセット直後は真っ暗で、A を押すまで何も起きない。**
    ⚠ 2026-08-13 に段そのものが変わった（0023 ④）。閉じる → 闇 → **管が点く** → 自分が映る。
    """
    t_a = 3.0                                # A を押す（それまで真っ暗）
    t_glyph_out = t_a + 2.0                  # 2 秒で題字が消え始める
    t_black = t_glyph_out + 1.3              # 黒が開いてパススルー（段 0・箱の外）
    t_seal = t_black + 7.0                   # 近づいて隔離が閉じる（段 1）
    t_dark = t_seal + SEAL                   # 段 2 — 全黒。ここで箱の中へ入る
    t_ignite = t_dark + DARK + DARK_WAIT     # 段 3 — 闇の中で管が点く
    t_live = t_ignite + IGNITE               # 段 4 — 自分が映る
    t_run = t_live + LIVE                    # 本編
    total = t_run + 10.0
    n = int(total * sk.SR)
    mix = np.zeros((n, 2))

    seal = load("bed_seal")
    room = load("bed_room")
    dev = load("bed_device")
    worn = load("bed_device_worn")

    # 封印の箱 — **真っ暗の下で先に鳴り始める**（J カット）。段 1 で引く
    seal_len = t_dark
    g = np.concatenate([
        np.linspace(0, 0.55, int(t_a * sk.SR)),
        np.full(int((t_black - t_a) * sk.SR), 0.55),
        np.linspace(0.55, 1.0, int(0.8 * sk.SR)),
        np.full(max(0, int((t_seal - t_black - 0.8) * sk.SR)), 1.0),
        1 - np.sin(np.linspace(0, np.pi / 2, int(SEAL * sk.SR))),
    ])
    lay(mix, tile(seal, seal_len), 0.0, g)

    # 部屋 — 段 0 から。隔離が閉じたら**帯域が閉じる**（音量ではない）
    room_len = total - t_black
    r = tile(room, room_len)
    close_at = int((t_seal - t_black) * sk.SR)
    closed = sk.spec_shape(r[:, 0], lambda f: sk.shelf(f, 420, -22, 1.0))
    closed = np.stack([closed, closed], axis=1)
    blend = np.clip((np.arange(len(r)) - close_at) / (1.5 * sk.SR), 0, 1)[:, None]
    r = r * (1 - blend) + closed * blend
    lay(mix, r, t_black, 0.85)

    # 装置 — **段 2（闇）から入り始める**（絵より先に来る）。3 周目へ向けて痩せる
    dev_len = total - t_dark
    d = tile(dev, dev_len)
    w = tile(worn, dev_len)
    rise = np.clip((np.arange(len(d)) / sk.SR) / max(t_live - t_dark, 0.1), 0, 1) ** 1.4
    decay = np.clip(((np.arange(len(d)) / sk.SR) - (t_run - t_dark)) / 8.0, 0, 1)
    co, ci = np.cos(decay * np.pi / 2), np.sin(decay * np.pi / 2)
    lay(mix, d * (rise * co)[:, None] + w * (rise * ci)[:, None], t_dark, 1.0)

    # 節目の一撃
    lay(mix, load("sfx_title_in"), t_a)          # ⚠ 尾は 12 秒。題字が消えた後も鳴り続ける
    lay(mix, load("sfx_title_out"), t_glyph_out)
    lay(mix, load("amb_bell"), t_black + 6.0)    # 段 0 に 1 回だけ
    lay(mix, load("amb_creak_1"), t_black + 2.4)
    lay(mix, load("amb_creak_2"), t_run + 6.8)
    lay(mix, load("sfx_seal_close"), t_seal - 0.9)
    # ⚠⚠ **管が点く所は 2 発**（LEDGER 0030）。①もらった音源 → ②面が満ちる所でノイズ。
    #    2 発目の位置は実機と同じ `ignite >= 0.55`（＝ 段の 55%）。
    lay(mix, load("sfx_screen_on"), t_ignite)
    lay(mix, load("sfx_screen_noise"), t_ignite + IGNITE * SCREEN_NOISE_AT)
    lay(mix, load("sfx_swap"), t_live)
    for k, at in enumerate((t_run + 2.2, t_run + 5.6, t_run + 8.4)):
        lay(mix, load(f"sfx_switch_{k + 1}"), at, 0.9)

    print(f"  真っ暗 0.0 / A {t_a:.1f} / 題字が消え始める {t_glyph_out:.1f} / "
          f"箱の外 {t_black:.1f} / 閉じる {t_seal:.1f} / 闇 {t_dark:.1f} / "
          f"管が点く {t_ignite:.1f} / 自分が映る {t_live:.1f} / 本編 {t_run:.1f}")
    return mix


def emit(name: str, y: np.ndarray, note: str):
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, f"{name}.wav")
    tp = sk.true_peak_db(y)
    if tp > -3.0:
        y = y * 10 ** ((-3.0 - tp) / 20.0)
    sk.write_wav(path, y, peak_db=-1.0)
    lint.render(name, y, os.path.join(OUT, f"{name}.png"))
    d = sk.describe(y)
    print(f"  {name:20s} {d['sec']:6.1f}s  {d['lufs']:6.1f} LUFS  "
          f"tp {d['true_peak_db']:5.1f}dB  内蔵SP {d['speaker_db']:5.1f}dB  — {note}")


def main() -> int:
    print("素材を 1 本ずつ:")
    emit("preview_materials", build_materials(), "何がどんな音か")
    print("導入の流れ:")
    emit("preview_intro", build_intro(), "⚠ 近似。実機の混ざり方は SoundBedLogic が決める")
    print(f"\n→ {OUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
