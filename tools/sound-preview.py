# -*- coding: utf-8 -*-
"""**人が聴くための** 2 本を書き出す。

```
py -3.11 tools/sound-preview.py
```

出るもの（`logs/sound/`）:
  - `preview_materials.wav` … 素材を 1 本ずつ並べたもの（何がどんな音かを確かめる）
  - `preview_intro.wav` … 導入 13.1 秒＋本編の入り口を通しで並べたもの（**流れ**を確かめる）
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
REAL, DEGRADE, STRUCTURE, FRAME, SWAP = 1.5, 3.5, 2.5, 2.5, 4.5
STRUCTURE_OWN = max(STRUCTURE - DEGRADE * (1 - 0.6), 0.5)     # 段 3 は段 2 と重なる

MATERIALS = [
    ("bed_seal", "封印の箱の唸り（3D・箱に定位）", 6.0),
    ("bed_room", "隔離された部屋のトーン", 6.0),
    ("bed_device", "装置の声・新しい（1 周目）", 6.0),
    ("bed_device_worn", "装置の声・痩せた（3 周目）", 6.0),
    ("bed_static", "信号断の砂嵐", 4.0),
    ("sfx_title_in", "タイトルが立つ（りん）", 0),
    ("sfx_title_out", "A を押してタイトルが閉じる（息を呑む）", 0),
    ("sfx_seal_close", "段 1 — 隔離が閉じる", 0),
    ("sfx_shatter", "段 4 — 割れて吸い込まれる", 0),
    ("sfx_swap", "段 5 — 装置が点く", 0),
    ("sfx_switch_1", "カメラ切替（リレー）1", 0),
    ("sfx_switch_2", "カメラ切替（リレー）2", 0),
    ("sfx_switch_3", "カメラ切替（リレー）3", 0),
    ("sfx_glitch_1", "映像の乱れ 1", 0),
    ("sfx_glitch_2", "映像の乱れ 2", 0),
    ("sfx_glitch_3", "映像の乱れ 3", 0),
    ("sfx_shell_open", "終幕 — 隔離が開いて現実が戻る", 0),
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
    """タイトル → 導入 5 段 → 本編の入り口までを、実機の順序どおりに並べる（近似）。"""
    title_sec = 5.0
    t_black = title_sec + 1.2               # 段 0（箱の外に立っている）
    t_real = t_black + 2.5
    t_deg = t_real + REAL
    t_str = t_deg + DEGRADE
    t_frame = t_str + STRUCTURE_OWN
    t_swap = t_frame + FRAME
    t_run = t_swap + SWAP
    total = t_run + 10.0
    n = int(total * sk.SR)
    mix = np.zeros((n, 2))

    seal = load("bed_seal")
    room = load("bed_room")
    dev = load("bed_device")
    worn = load("bed_device_worn")

    # 封印の箱 — タイトルの黒の下で先に鳴り始め（J カット）、段 4 で食われる
    seal_len = t_frame + FRAME - 0.0
    g = np.concatenate([
        np.linspace(0, 0.55, int(title_sec * sk.SR)),
        np.full(int((t_frame - title_sec) * sk.SR), 1.0),
        1 - np.sin(np.linspace(0, np.pi / 2, int(FRAME * sk.SR))),
    ])
    lay(mix, tile(seal, seal_len), 0.0, g)

    # 部屋 — 段 0 から。隔離が閉じたら**帯域が閉じる**（音量ではない）
    room_len = total - t_black
    r = tile(room, room_len)
    close_at = int((t_real - t_black) * sk.SR)
    closed = sk.spec_shape(r[:, 0], lambda f: sk.shelf(f, 420, -20, 1.0))
    closed = np.stack([closed, closed], axis=1)
    blend = np.clip((np.arange(len(r)) - close_at) / (1.5 * sk.SR), 0, 1)[:, None]
    r = r * (1 - blend) + closed * blend
    lay(mix, r, t_black, 0.85)

    # 装置 — **段 2 から入り始める**（絵より先に来る）。3 周目へ向けて痩せる
    dev_len = total - t_deg
    d = tile(dev, dev_len)
    w = tile(worn, dev_len)
    rise = np.clip((np.arange(dev_len * 0 + len(d)) / sk.SR) / (t_swap - t_deg), 0, 1) ** 1.4
    decay = np.clip(((np.arange(len(d)) / sk.SR) - (t_run - t_deg)) / 8.0, 0, 1)
    co, ci = np.cos(decay * np.pi / 2), np.sin(decay * np.pi / 2)
    lay(mix, d * (rise * co)[:, None] + w * (rise * ci)[:, None], t_deg, 1.0)

    # 節目の一撃
    lay(mix, load("sfx_title_in"), 0.4)
    lay(mix, load("sfx_title_out"), title_sec)
    lay(mix, load("sfx_seal_close"), t_real - 0.35)
    lay(mix, load("sfx_shatter"), t_frame + FRAME * 0.30)
    lay(mix, load("sfx_swap"), t_swap)
    for k, at in enumerate((t_run + 2.2, t_run + 5.6, t_run + 8.4)):
        lay(mix, load(f"sfx_switch_{k + 1}"), at, 0.9)

    print(f"  段の頭（秒）: タイトル 0.0 / 箱の外 {t_black:.1f} / 現実 {t_real:.1f} / "
          f"格下げ {t_deg:.1f} / 構造 {t_str:.1f} / 枠 {t_frame:.1f} / すり替え {t_swap:.1f} / "
          f"本編 {t_run:.1f}")
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
