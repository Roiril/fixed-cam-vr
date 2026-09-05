# -*- coding: utf-8 -*-
"""**人が聴くための** 3 本を書き出す。

```
py -3.11 tools/sound-preview.py
```

出るもの（`logs/sound/`）:
  - `preview_materials.wav` … 素材を 1 本ずつ並べたもの（何がどんな音かを確かめる）
  - `preview_intro.wav` … 真っ暗 → A → 題字 → 導入 → 本編の入り口を通しで並べたもの（**流れ**）
  - `preview_swap.wav` … 3 周目（入れ替わり → A・B は一人 → C で増える → 群れへ渡る）
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
# ⚠ 2026-08-15 に旧構成へ戻した（`canon/LEDGER.md` 0044）。封印の箱を退避したので
#   「箱の中に入ってから固定視点」が成立しない。いまは 5 段 13.1 秒:
#   段 1 素通し / 段 2 格下げ / 段 3 輪郭（段 2 と重なる）/ 段 4 割れる / 段 5 映像だけになる。
#   ⚠ `IntroTiming.Default` と同じ値にしておく（ずれると、聴いて決めた間が実機と違う）。
REAL, DEGRADE, STRUCTURE_SRC, FRAME, SWAP = 1.5, 3.5, 2.5, 2.5, 1.6
# 段 3 は段 2 の後半から重なるので、単独で流れるのはこれだけ（`IntroTiming.TotalSec` と同じ式）。
STRUCTURE = max(STRUCTURE_SRC - DEGRADE * (1 - 0.6), 0.5)
# ⚠⚠ **2026-08-16 に「入れ替えが終わった所」へ移した**（`canon/LEDGER.md` 0057）。
#    段 4 の `live = SmoothStep(0.55, 0.85, p)` が 1 に届く進み ＝ 0.85。
#    `SoundCueLogic.ScreenOnAt`（live の閾値）と対。片方だけ直すと聴いて決めた間が実機と違う。
SCREEN_ON_AT = 0.85
# 鈴が鳴るまで（段 5 の頭から）。`IntroLogic.SwapCrossfadeSec` ＝ `SoundCueLogic.BellAfterSwapSec`。
BELL_AFTER_SWAP = 1.2

MATERIALS = [
    ("bed_seal", "【退避中】封印の箱の唸り — 箱を外したので鳴らない", 6.0),
    ("bed_room", "部屋のトーン（合成）— **導入と終幕だけ**。本編では鳴らない（0115）", 6.0),
    ("bed_device", "装置の声・新しい（1 周目）", 6.0),
    ("bed_device_worn", "装置の声・痩せた（3 周目）", 6.0),
    ("bed_static", "信号断の砂嵐", 4.0),
    ("sfx_title_in", "A で題字が立つ（もらった「Tone Downer (Reverb)」・2026-08-15 差し替え）", 0),
    ("sfx_title_out", "2 秒後に題字が消える（息を呑む）", 0),
    ("sfx_seal_close", "【鳴らない】隔離が閉じる（もらった「黒い中に入るときの金属音」）", 0),
    ("sfx_shatter", "段 4 — 現実が割れて吸い込まれる（もらった一撃を小刻みに並べたもの）", 0),
    ("sfx_screen_on", "段 4 の終わり — スクリーンが出る（もらった Cyber14-1）。**導入の山**", 0),
    ("sfx_screen_noise", "【鳴らない】その後のノイズ — 2026-08-16 に外した（音源は残してある）", 0),
    ("sfx_swap", "【鳴らない】装置が点く — 同じ縁をもらった音（sfx_screen_on）が取った", 0),
    ("sfx_switch_1", "カメラ切替（もらった「カメラ切り替え」・**変種 1/6**。残り 5 本は preview_switch）", 0),
    ("sfx_switch_alert_1", "同・**カットの switchSfx が立つ所**（もらった「警告音」を 420ms・-4dB で頭から 35ms 遅れて重ねたもの。0134）", 0),
    ("sfx_glitch_1", "映像の乱れ 1", 0),
    ("sfx_glitch_2", "映像の乱れ 2", 0),
    ("sfx_glitch_3", "映像の乱れ 3", 0),
    ("sfx_shell_open", "【鳴らない】終幕 — 隔離が開いて現実が戻る（0048 で段ごと廃止）", 0),
    ("sfx_power_off", "終幕 — **装置の電源が落ちる**（もらった TV-Turn_Off01-1）。潰れ始めと同じ縁", 0),
    ("amb_creak_1", "【鳴らない】家鳴り 1 — 2026-08-15 に全廃（音源は残してある）", 0),
    ("amb_creak_2", "【鳴らない】家鳴り 2 — 同上", 0),
    ("amb_bell", "鈴（もらった「鈴２」）— **段 3（輪郭だけの世界）の頭**に 1 回だけ", 0),
    ("sfx_lang_1", "言語の切り替え 1（もらった switch1・**交互の 1 本目**・0153）", 0),
    ("sfx_lang_2", "同 2（switch2・**交互の 2 本目**）", 0),
    ("bed_dolls_laugh", "人形の群れ（4 周目 A の締め・8 体 40 回の輪）", 12.0),
    ("bed_doll_one", "入れ替わった人形 — 一人（3 周目 A・B）", 11.0),
    ("bed_dolls_grow_a", "同・増える 2 体（3 周目 C の前半）", 13.0),
    ("bed_dolls_grow_b", "同・さらに増える 4 体（3 周目 C の後半）", 17.0),
]


# ⚠⚠ **笑いは体ごとに焼いてある**（2026-09-04・`canon/LEDGER.md` 0139）。
#    実機は体ごとに別の方角へ置くが、**試聴は 2 本の耳で聴く**ので、ここでは足して 1 本にする。
#    ⇒ ここで聴けるのは**中身と高さ**だけ。広がりは聴けない（実機で被るか、下の
#      `preview_laugh_ring.wav` で疑似的に振ったものを聴く）。
#    焼く側の `ingest-sounds.py` の `LAUGH_BODIES` と対。
LAUGH_BODIES = {
    "bed_dolls_laugh": 8,
    "bed_doll_one": 1,
    "bed_dolls_grow_a": 2,
    "bed_dolls_grow_b": 4,
}


def load(name: str) -> np.ndarray:
    """素材を読む。**笑いは体ごとのファイルを足して返す**（0139）。"""
    n = LAUGH_BODIES.get(name, 1)
    if n > 1:
        out = None
        for i in range(1, n + 1):
            y, _ = sk.read_wav(os.path.join(SRC, f"{name}_{i}.wav"))
            y = sk.to_stereo(y)
            out = y if out is None else out + y
        return out
    y, _ = sk.read_wav(os.path.join(SRC, f"{name}.wav"))
    return sk.to_stereo(y)


# ---- 本編の背景 ＝ 劇伴（`canon/LEDGER.md` 0115）-----------------------------
#
# 2026-08-23 から 1〜3 周目の背景は `HorrBGM` 1 本になった（周ごとの環境音は退役）。
# **卓が鳴らす音なので `Assets/Resources/Sound/` には無い** — mp3 をここで復号して混ぜる。
#
# ⚠ 高さは実機と同じ: `show.json` の bgm は volume -1 ＝ トラック側の 0.5 が効く。
#   素の mp3 は -21.7 LUFS なので、掛けた後は **-27.7 LUFS**（打鍵 -32 より 4.3dB 上）。
SCORE_MP3 = os.path.join(ROOT, "Assets", "Art", "Audio", "HorrBGM.mp3")
SCORE_WAV = os.path.join(ROOT, "logs", "sound", "ingest", "src_score_horrbgm.wav")
SCORE_VOLUME = 0.5


def load_score() -> np.ndarray:
    """劇伴を**卓と同じ音量**で返す。初回だけ mp3 を復号して置いておく。"""
    if not os.path.exists(SCORE_WAV):
        import subprocess

        import imageio_ffmpeg
        os.makedirs(os.path.dirname(SCORE_WAV), exist_ok=True)
        subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(), "-y", "-v", "error", "-i", SCORE_MP3,
                        "-ar", str(sk.SR), "-ac", "2", "-c:a", "pcm_s16le", SCORE_WAV],
                       check=True)
    y, _ = sk.read_wav(SCORE_WAV)
    return sk.to_stereo(y) * SCORE_VOLUME


# ---- 追いつきの後で入れ替わる劇伴（`canon/LEDGER.md` 0119）--------------------
#
# ⚠ こちらは **卓の `audio/`** に置く（`bgmTracks[].url` が指す場所）。git 管理外。
#   焼くのは `tools/make-bgm-track.py`。素材そのものが元曲の 2:06 から切ってある。
# ⚠ 高さは実機と同じ **0.57**。素で揃えると 0.30 だが、この曲は正体が 200Hz より下にあって
#   内蔵スピーカーで -11.6dB 落ちるので、**通した後**で `HorrBGM@0.50` に揃えてある。
SCORE2_MP3 = os.path.join(ROOT, "tools", "web-compositor", "audio", "LostPlace2.mp3")
SCORE2_WAV = os.path.join(ROOT, "logs", "sound", "ingest", "src_score_lostplace2.wav")
SCORE2_VOLUME = 0.57


# ---- ホラー軽減モードで既存の音に掛かる倍率（`canon/LEDGER.md` 0154）----------
#
# ⚠ **C# の `HorrorRelief.Gain` と同じ値**。実機は `AudioListener.volume` 1 か所で掛ける。
FixedCamVr_RELIEF_GAIN = 0.5


def load_score2() -> np.ndarray:
    """入れ替わった後の劇伴を**実機と同じ音量**で返す。"""
    if not os.path.exists(SCORE2_WAV):
        import subprocess

        import imageio_ffmpeg
        os.makedirs(os.path.dirname(SCORE2_WAV), exist_ok=True)
        subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(), "-y", "-v", "error", "-i", SCORE2_MP3,
                        "-ar", str(sk.SR), "-ac", "2", "-c:a", "pcm_s16le", SCORE2_WAV],
                       check=True)
    y, _ = sk.read_wav(SCORE2_WAV)
    return sk.to_stereo(y) * SCORE2_VOLUME


def through_speaker(y: np.ndarray) -> np.ndarray:
    """**Quest の内蔵スピーカー**を通した音（`soundkit.speaker_loss_db` と同じ近似）。

    ⚠ これは「実機で耳に届く形」であって、ヘッドホンで聴いた印象ではない。
      低い方に正体がある曲は、ここを通すと**別の音になる**。
    """
    m = sk.to_stereo(y)
    out = np.empty_like(m)
    for c in range(m.shape[1]):
        v = sk.biquad_fft(sk.biquad_fft(m[:, c], "hp", 200.0, 0.707, 0.0, sk.SR),
                          "hp", 200.0, 0.707, 0.0, sk.SR)
        out[:, c] = sk.biquad_fft(v, "lp", 12000.0, 0.707, 0.0, sk.SR)
    return out


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
    """周回リセット → A → 題字 → 導入 5 段 → 本編の入り口までを、実機の順序どおりに並べる（近似）。

    ⚠ 2026-08-12 に流れが変わった（`canon/LEDGER.md` 0014）。
    **周回リセット直後は真っ暗で、A を押すまで何も起きない。**
    ⚠ 2026-08-15 に段が旧構成へ戻った（0044）。素通し → 格下げ → 輪郭 → **割れる** → 映像だけになる。
    """
    t_a = 3.0                                # A を押す（それまで真っ暗）
    t_glyph_out = t_a + 2.0                  # 2 秒で題字が消え始める
    t_black = t_glyph_out + 1.3              # 黒が開いてパススルー（段 0）
    t_real = t_black + 7.0                   # 近づいて段 1（素通し）
    t_degrade = t_real + REAL                # 段 2 — 色が抜ける
    t_structure = t_degrade + DEGRADE        # 段 3 — 輪郭だけ
    t_frame = t_structure + STRUCTURE        # 段 4 — 割れる
    t_swap = t_frame + FRAME                 # 段 5 — 映像だけになる
    t_run = t_swap + SWAP                    # 本編
    total = t_run + 10.0
    n = int(total * sk.SR)
    mix = np.zeros((n, 2))

    room = load("bed_room")
    dev = load("bed_device")
    worn = load("bed_device_worn")

    # ⚠ 封印の箱の唸り（`bed_seal`）は 2026-08-15 に黙らせた（箱を退避したので定位する先が無い）。
    #    真っ暗の下で先に鳴らす J カットもここが担っていたので、**いま黒の下は無音**。

    # 部屋 — 段 0 から。段 5 で会場が黒へ落ちるとき**帯域が閉じる**（音量ではない）
    room_len = total - t_black
    r = tile(room, room_len)
    close_at = int((t_swap - t_black) * sk.SR)
    closed = sk.spec_shape(r[:, 0], lambda f: sk.shelf(f, 420, -22, 1.0))
    closed = np.stack([closed, closed], axis=1)
    blend = np.clip((np.arange(len(r)) - close_at) / (1.5 * sk.SR), 0, 1)[:, None]
    r = r * (1 - blend) + closed * blend
    lay(mix, r, t_black, 0.85)

    # 装置 — **段 2（格下げ）から入り始める**（絵より先に来る）。3 周目へ向けて痩せる
    dev_len = total - t_degrade
    d = tile(dev, dev_len)
    w = tile(worn, dev_len)
    rise = np.clip((np.arange(len(d)) / sk.SR) / max(t_swap - t_degrade, 0.1), 0, 1) ** 1.4
    decay = np.clip(((np.arange(len(d)) / sk.SR) - (t_run - t_degrade)) / 8.0, 0, 1)
    co, ci = np.cos(decay * np.pi / 2), np.sin(decay * np.pi / 2)
    lay(mix, d * (rise * co)[:, None] + w * (rise * ci)[:, None], t_degrade, 1.0)

    # 節目の一撃
    lay(mix, load("sfx_title_in"), t_a)          # ⚠ 尾は題字が消えた後も鳴り続ける
    lay(mix, load("sfx_title_out"), t_glyph_out)
    # ⚠⚠ **導入の節目は 3 つ**（2026-08-16・`canon/LEDGER.md` 0057）。
    #    ①段 4 の頭で割れる（1.70 秒でだんだん小さく）②静けさ 0.37 秒 ③入れ替えが終わって
    #    スクリーンが出る ④段 5 ＋ 1.2 秒で**完全にスクリーンになった**鈴。
    #    **ノイズ（`sfx_screen_noise`）は鳴らさない。** ここへ戻さないこと。
    lay(mix, load("sfx_shatter"), t_frame)
    lay(mix, load("sfx_screen_on"), t_frame + FRAME * SCREEN_ON_AT)
    lay(mix, load("amb_bell"), t_swap + BELL_AFTER_SWAP)
    # ⚠ 切替は**変種を回す**（2026-08-23・`canon/LEDGER.md` 0112）。
    #    実機は音程と音量も散らすが、ここでは並べるだけ。6 本の違いは `preview_switch` で聴く。
    for i, at in enumerate((t_run + 2.2, t_run + 5.6, t_run + 8.4)):
        lay(mix, load(f"sfx_switch_{1 + (i % 6)}"), at, 0.9)

    print(f"  真っ暗 0.0 / A {t_a:.1f} / 題字が消え始める {t_glyph_out:.1f} / "
          f"素通し {t_black:.1f} / 段 1 {t_real:.1f} / 格下げ {t_degrade:.1f} / "
          f"輪郭 {t_structure:.1f} / 割れる {t_frame:.1f} / "
          f"スクリーンが出る {t_frame + FRAME * SCREEN_ON_AT:.1f} / 映像だけ {t_swap:.1f} / "
          f"鈴 {t_swap + BELL_AFTER_SWAP:.1f} / 本編 {t_run:.1f}")
    return mix


# ⚠ 周ごとの環境音の入れ替え（旧 `preview_ambient` / `canon/LEDGER.md` 0049）は
#   2026-08-23 に退役した（0115 — 3 本とも「怖くない」と退けられ、背景は劇伴 1 本になった）。
#   聴き直したくなったら git 履歴の `build_ambient` を戻す。


# --- 連絡の面の打鍵（`canon/LEDGER.md` 0056）--------------------------------
#
# ⚠ ここは**日本語の速さ**（Latin は 18 ＝ `CharsPerSecLatin`・0149）。下の見本は日本語の文面。
# ⚠ 打つ速さ・散らし幅は **C# 側と対**（`CommsPanelLogic.CharsPerSec` /
#   `TypeAudioCue`）。片方だけ変えると、聴いて決めた密度が実機と違う。
TYPE_CPS = 12.0
TYPE_PITCH = 0.04       # ±（`TypeAudioCue.PitchSpread`）
TYPE_GAIN_DB = 2.0      # ±（`TypeAudioCue.GainSpreadDb`）
TYPE_VARIANTS = 8

# (文面, 打つ字数 ＝ **見える字だけ**。改行では鳴らさない)
# 速さの聴き比べ（0149）。**①b の実際の文面**を使う ＝ 空白の分布まで実機と同じ。
COMPARE = [
    ("異変を見つけたら\nボタンを長押ししてください\n装置が解析して対処を試みます",
     12.0, "①b 日本語 12 文字/秒（変えていない）"),
    ("If you see an anomaly,\nhold down the button and\nthe device will analyse it.",
     12.0, "①b English 12 文字/秒（0149 まで・6.1 秒）"),
    ("If you see an anomaly,\nhold down the button and\nthe device will analyse it.",
     18.0, "①b English 18 文字/秒（採った速さ・4.1 秒）"),
    ("If you see an anomaly,\nhold down the button and\nthe device will analyse it.",
     22.0, "①b English 22 文字/秒（0056 が退けた速さ）"),
]

COMMS = [
    ("調査を開始してください。", 12),
    ("異変を見つけたら／ボタンを長押ししてください／装置が解析して対処を試みます", 35),
    ("異変を排除しました", 9),
    ("異常は検出されませんでした", 13),
    ("異常があなたを／取り込もうとしています。／排除してください。", 28),
]


def type_burst(hits: int, cps: float, rng: np.random.Generator) -> np.ndarray:
    """1 通ぶんの打鍵。**実機と同じ選び方**（直前と同じ変種を引かない・音程と音量を散らす）。"""
    return type_text("x" * hits, cps, rng)


def type_text(text: str, cps: float, rng: np.random.Generator) -> np.ndarray:
    """**実際の文面**を刻んで鳴らす。

    ⚠ 実機は 1 文字ぶん進むたびに 1 発だが、**空白と改行では鳴らない**（`isVisible`）。
    Latin は 15〜20% が空白なので、**同じ速さでも発音の密度は日本語より疎い** —
    「18 文字/秒は速すぎないか」を耳で判定できるように、字の並びごと再現する（0149）。
    """
    clips = [load(f"sfx_type_{i + 1}") for i in range(TYPE_VARIANTS)]
    step = int(sk.SR / cps)
    out = np.zeros((step * len(text) + max(len(c) for c in clips), 2))
    last = -1
    for i, glyph in enumerate(text):
        if glyph in (" ", "\n", "\u3000"):
            continue                # 字が出ないので鳴らさない（尺だけ進む）
        v = int(rng.integers(0, TYPE_VARIANTS - 1))
        if v >= last:
            v += 1                      # 直前と同じものを引かない
        last = v
        c = clips[v]
        p = 1.0 + float(rng.uniform(-TYPE_PITCH, TYPE_PITCH))
        n = int(len(c) / p)
        c = np.stack([np.interp(np.linspace(0, len(c) - 1, n), np.arange(len(c)), c[:, ch])
                      for ch in (0, 1)], axis=1)
        g = 10 ** (float(rng.uniform(-TYPE_GAIN_DB, TYPE_GAIN_DB)) / 20.0)
        out[i * step:i * step + n] += c * g
    return out


def build_comms() -> np.ndarray:
    """打鍵を**本編の敷く音の上**で聴く（単体で聴くと必ず大きく感じる）。

    頭に**同じ 1 通を 4 通りの速さ**で並べてある（2026-09-04・0149）。
    ①b を 日本語 12 → English 12 → English 18（採った速さ）→ English 22 の順。
    **判定してほしいのは 1 つだけ — 18 文字/秒がまだカタカタに聞こえるか**
    （22 は 0056 が「連続音になる」として退けた速さ。境目を耳で測るために並べてある）。
    """
    rng = np.random.default_rng(20260816)
    marks = []
    t = 1.5
    for text, cps, label in COMPARE:
        marks.append((t, text, cps, label))
        t += len(text) / cps + 2.0
    t += 1.0
    for text, hits in COMMS:
        marks.append((t, "x" * hits, TYPE_CPS, f"「{text}」{hits} 字"))
        t += hits / TYPE_CPS + 2.5
    total = t + 1.5

    # 本編の高さ（`rules/sound-design.md` §4 の表）。
    # ⚠ 背景は劇伴（0115）。**打鍵が大きすぎないかはこの上で聴く** — 部屋のトーンは本編に居ない。
    out = tile(load_score(), total) + tile(load("bed_device"), total) * 1.0
    for at, text, cps, label in marks:
        lay(out, type_text(text, cps, rng), at)
        print(f"  {at:5.1f}s  {label}")
    return out


def build_lang() -> np.ndarray:
    """**言語を切り替えたときの音**（0153）。注意書きが出ている黒の中 ＝ 下に居るのは劇伴だけ。

    体験者が 4 回押した所を並べてある。**交互**（1 → 2 → 1 → 2）に聞こえるか、
    2 本の大きさが揃っているか（尖頭で揃えて実測 -27.2 / -27.7 LUFS）を耳で見る。
    ⚠ 実機は音程も音量も散らさない（`LangSwitchAudioCue`）ので、ここでも散らさない。
    """
    press = [1.5, 3.0, 4.5, 6.0]
    total = press[-1] + 2.5
    # 黒の中は劇伴だけ（`rules/sound-design.md` §4 の表・リセット後の黒）。
    out = tile(load_score(), total)
    for i, at in enumerate(press):
        lay(out, load(f"sfx_lang_{i % 2 + 1}"), at)
        print(f"  {at:5.1f}s  {i + 1} 回目 → sfx_lang_{i % 2 + 1}")
    return out


def build_dolls() -> np.ndarray:
    """最後の演出（人形がたくさん出てくる所）を、**本編の敷く音の上で**聴く。

    ⚠ **報告を押すまでループする**（`canon/LEDGER.md` 0066）。ここでは輪を 2 周まわして、
    **継ぎ目が聞こえないか**を確かめられるようにしてある（12 秒 × 2）。

    並びは実機と同じ順序:
      締めのカットが待ち始める → **人形が笑い出す** → 3 秒後に③の連絡（打鍵）→
      報告を押す → 笑いが止まる。
    """
    rng = np.random.default_rng(20260816)
    t_laugh = 1.5
    t_comms = t_laugh + 3.0            # `CommsCueLogic.PromptAfterWaitSec`
    t_mark = t_laugh + 25.0            # ここで報告を押した（笑いが止まる）
    total = t_mark + 3.0

    # 4 周目 ＝ 装置は痩せ切っている。背景は劇伴（0115・笑いのあいだは実機だと 0.85 退く）。
    out = (tile(load_score(), total) * 0.15
           + tile(load("bed_device_worn"), total) * 1.0)

    # 笑いは輪。押されるまで鳴り続ける（実機は `SoundBedLogic.dolls` が音量を出し入れする）。
    laugh = load("bed_dolls_laugh")
    span = int((t_mark - t_laugh) * sk.SR)
    loops = tile(laugh, span / sk.SR)
    # 止まりは実機と同じ速さ（半減期 0.35 秒）で落とす。
    tail = np.arange(len(loops)) / sk.SR - (t_mark - t_laugh - 1.0)
    env = np.clip(0.5 ** (np.maximum(tail, 0.0) / 0.35), 0.0, 1.0)[:, None]
    lay(out, loops * env, t_laugh)
    lay(out, type_burst(20, TYPE_CPS, rng), t_comms)
    print(f"  {t_laugh:.1f}s 人形が笑い出す（8 体 / 12 秒の輪を 2 周）/ "
          f"{t_comms:.1f}s ③の連絡 / {t_mark:.1f}s 報告を押す → 止まる")
    return out


def build_swap() -> np.ndarray:
    """3 周目 — **体験者と人形が入れ替わってから、C で増えるまで**（`canon/LEDGER.md` 0086）。

    実機の式をそのまま写している（`SoundBedLogic`）:
      - 一人ぶんは入れ替わった縁から半減期 0.25 秒で立ち、消えるときは 0.8 秒
      - 増え具合は **C に居るあいだ 9 秒で 0 → 1**（直線）（`SoundBedLogic.SwellRiseSec`）
      - 2 枚目は 0〜0.55、3 枚目は 0.45〜1.0 に割り当て、**聴感直線**（t^(1/0.6)）で入れる

    ⚠ 尺は実機と同じにしてある（区間 12 秒 ＋ C は増え切るまで）。**縮めない** —
    増え方の速さがこの体験の判定そのものなので、早送りすると別のものを聴くことになる。
    """
    marks = [(3.0, 0, "3 周目 A — 入れ替わる（人形が笑い出す）"),
             (15.0, 1, "3 周目 B — まだ一人"),
             (27.0, 2, "3 周目 C — ここから増えていく"),
             (40.0, -1, "4 周目 A — 群れへ渡る（一人ぶんは引く）")]
    total = 51.0
    n = int(total * sk.SR)

    # --- 制御信号（1ms 刻み。実機は毎フレーム同じ式で進める）-------------------
    cn = int(total * 1000)
    dt = 0.001
    one = np.zeros(cn)
    swell = np.zeros(cn)
    crowd = np.zeros(cn)
    cur_one = cur_crowd = cur_swell = 0.0
    for i in range(cn):
        t = i * dt
        cam = next((c for at, c, _l in reversed(marks) if t >= at), None)
        present = cam is not None
        closing = cam == -1                       # 4 周目 A ＝ 締めの群れ（一人ぶんは黙る）
        tgt_one = 1.0 if (present and not closing) else 0.0
        tgt_crowd = 1.0 if closing else 0.0
        cur_one += (tgt_one - cur_one) * (1.0 - 0.5 ** (dt / (0.25 if tgt_one > cur_one else 0.8)))
        cur_crowd += (tgt_crowd - cur_crowd) * (1.0 - 0.5 ** (dt / 0.25))
        growing = tgt_one > 0.0 and cam == 2
        cur_swell = (min(1.0, cur_swell + dt / 9.0) if growing
                     else max(0.0, cur_swell - dt / 5.0))
        one[i], swell[i], crowd[i] = cur_one, cur_swell, cur_crowd

    def at_sr(c):
        return np.interp(np.arange(n) / sk.SR, np.arange(cn) * dt, c)[:, None]

    a = np.clip(swell / 0.55, 0, 1) ** (1 / 0.6)
    b = np.clip((swell - 0.45) / 0.55, 0, 1) ** (1 / 0.6)

    # 3 周目の敷く音（§4 の表）。装置は痩せた側が混ざっている。
    # 背景は劇伴（0115）。一人ぶんの笑いが鳴っているあいだは実機だと 0.45 退くので掛けてある。
    out = (tile(load_score(), total) * 0.55
           + tile(load("bed_device_worn"), total) * 0.9
           + tile(load("bed_device"), total) * 0.4)
    g_one = at_sr(one)
    out += tile(load("bed_doll_one"), total) * g_one
    out += tile(load("bed_dolls_grow_a"), total) * g_one * at_sr(a)
    out += tile(load("bed_dolls_grow_b"), total) * g_one * at_sr(b)
    out += tile(load("bed_dolls_laugh"), total) * at_sr(crowd)

    for at, cam, label in marks:
        print(f"  {at:5.1f}s  {label}")
    print(f"    増え切るのは {marks[2][0] + 9.0:.0f}s（C に着いてから 9 秒 ＝ `SwellRiseSec`）")
    return out


SWITCH_VARIANTS_N = 6      # ⚠ `SwitchAudioCue.DefaultVariantCount` の写し


def build_switch() -> np.ndarray:
    """カメラ切替の音（`canon/LEDGER.md` 0112 の変種 ＋ 0106 の警告つき）。

    3 つ並べる:

    1. **変種を 1 本ずつ**（無音の上で、6 本の違いだけを聴く）
    2. **旧版の鳴り方 → 新しい鳴り方**（同じ刻みで 8 発ずつ。ここが 0112 の判定）
    3. **2 周目 C の実際の刻み**（`show.json` の 1.0 / 0.9 / 0.8 / 1.4 秒・敷く音の上で）。
       ここは**警告つき**が並ぶ場所で、素と交ざる

    ⚠ 乱れの音（`sfx_glitch_*`）は入れていない。実機では継ぎ目に重なるが、ここで判定したいのは
    切替音そのものなので、まず素で聴ける形にしてある。
    ⚠ 散らし方は `SwitchAudioCue` の写し（gain 0.85 / 音程 ±3.5% / 音量 ±1.5dB /
    **直前と同じ変種は引かない**）。**向こうを変えたらここも直す。**
    """
    rng = np.random.default_rng(20260823)
    plain = [load(f"sfx_switch_{i}") for i in range(1, SWITCH_VARIANTS_N + 1)]
    alert = [load(f"sfx_switch_alert_{i}") for i in range(1, SWITCH_VARIANTS_N + 1)]

    def shot(clip: np.ndarray) -> np.ndarray:
        r = 1.0 + float(rng.uniform(-0.035, 0.035))
        m = max(8, int(len(clip) / r))
        x = np.linspace(0, len(clip) - 1, m)
        c = np.stack([np.interp(x, np.arange(len(clip)), clip[:, ch]) for ch in (0, 1)], axis=1)
        return c * 0.85 * 10 ** (float(rng.uniform(-1.5, 1.5)) / 20.0)

    last = [-1]

    def pick(n: int) -> int:
        v = int(rng.integers(0, n - 1))
        if v >= last[0]:
            v += 1
        last[0] = v
        return v

    total, bed_from, start = 34.0, 25.0, 26.0
    steps = (1.0, 0.9, 0.8, 1.4)
    out = np.zeros((int(total * sk.SR), 2))

    # ① 変種を 1 本ずつ（0.55 秒刻み）
    for i, c in enumerate(plain):
        lay(out, c * 0.85, 0.5 + 0.55 * i)
    # ①b 素 → 警告つき の対（同じ変種で続けて鳴らす）。
    #    ⚠ ここが 0106 の判定そのもの。無いと「警告が混ざっているか」を耳で確かめる場が
    #      どこにも無い（2026-08-31 に気づいた — §「人形視点が…」は「前半は素と合成の対比」と
    #      書いてあったが、実際は素の変種しか並べていなかった）。
    for i in range(SWITCH_VARIANTS_N):
        lay(out, plain[i] * 0.85, 4.6 + 1.20 * i)
        lay(out, alert[i] * 0.85, 4.6 + 1.20 * i + 0.5)
    # ② 旧版の鳴り方（1 本だけ）→ 新しい鳴り方（6 本を回す）。同じ刻みで並べる
    for i in range(8):
        lay(out, shot(plain[0]), 12.4 + 0.62 * i)
    for i in range(8):
        lay(out, shot(plain[pick(SWITCH_VARIANTS_N)]), 18.4 + 0.62 * i)

    # ③ 本編の敷く音（§4 の表・2 周目）。切替音がこの上でどう立つかを聴く。
    lay(out, tile(load_score(), total - bed_from), bed_from)
    lay(out, tile(load("bed_device"), total - bed_from), bed_from)
    at = start
    # ⚠⚠ **4 発とも警告つき。** show.json の 2 周目 C 接近（`L2C2#1`）は pov_1〜pov_4 が
    #    連続で、あいだにゾーン切替は入らない（`switchSfx` は 4 つとも true）。
    #    以前ここは 3 発に 1 発を素にしていたが、それだと警告の密度が実機より低く聞こえる。
    #    ⚠ このほかに予備動作（`L2C2#0` の pov_0）が先に 1 発ある ＝ 1 体験で計 5 発。
    for sec in steps:
        lay(out, shot(alert[pick(SWITCH_VARIANTS_N)]), at)
        at += sec
    print(f"   0.5s 変種を 1 本ずつ（{SWITCH_VARIANTS_N} 本）   "
          f"4.6s 素 → 警告つき の対（{SWITCH_VARIANTS_N} 組）   "
          f"12.4s 旧（1 本を 8 発）→ 18.4s 新（6 本を 8 発）   "
          f"{start:.0f}s 2 周目 C の刻み {' / '.join(f'{s:.1f}' for s in steps)} 秒（4 発とも警告つき）")
    return out


def build_call() -> np.ndarray:
    """**人形の呼びかけ**（`canon/LEDGER.md` 0109）。

    前半は**声だけ**（掛けた手当てそのものを聴く）、後半は**2 周目 C の接近そのまま** —
    人形視点 4 カットの刻み（1.0 / 0.9 / 0.8 秒）で警告つきの切替音が並び、
    **4 カット目（追いつき）の頭で声が重なる**。本編の敷く音（2 周目）の上。

    ⚠ ここで判定してほしいのは 2 つ: **切替音と重なって声が読めるか**と、**音量**。
    どちらも `tools/ingest-sounds.py` の `VOICES` の 1 数字（`lufs`）で動く。
    ⚠ 散らし方は実機の写し — 切替音は音程 ±3.5% / 音量 ±1.5dB、**声は散らさない**。
    """
    rng = np.random.default_rng(20260823)
    alert, call = load("sfx_switch_alert_1"), load("sfx_doll_call")

    def shot(clip: np.ndarray) -> np.ndarray:
        r = 1.0 + float(rng.uniform(-0.035, 0.035))
        m = max(8, int(len(clip) / r))
        x = np.linspace(0, len(clip) - 1, m)
        c = np.stack([np.interp(x, np.arange(len(clip)), clip[:, ch]) for ch in (0, 1)], axis=1)
        return c * 0.85 * 10 ** (float(rng.uniform(-1.5, 1.5)) / 20.0)

    total, bed_from, start = 12.0, 4.0, 5.0
    steps = (1.0, 0.9, 0.8)      # pov_1 → pov_2 → pov_3 →（この後が pov_4 ＝ 追いつき）
    out = np.zeros((int(total * sk.SR), 2))

    lay(out, call, 0.6)          # まず声だけ（無音の上で）

    # 本編の敷く音（§4 の表・2 周目）。声がこの上でどう立つかを聴く。
    lay(out, tile(load_score(), total - bed_from), bed_from)
    lay(out, tile(load("bed_device"), total - bed_from), bed_from)

    at = start
    for sec in steps:
        lay(out, shot(alert), at)
        at += sec
    lay(out, shot(alert), at)    # 4 カット目の切替音
    lay(out, call, at)           # **同じ縁で声**（散らさない・gain 1.0）
    print(f"   0.6s 声だけ   {start:.0f}s 接近の刻み "
          f"{' / '.join(f'{x:.1f}' for x in steps)} 秒 → "
          f"{at:.1f}s で 4 カット目（切替音 ＋ 声が同時）")
    return out


def build_laugh_ring() -> np.ndarray:
    """**周囲に大勢いる**を耳で確かめる（2026-09-04・`canon/LEDGER.md` 0139）。

    ユーザー指定「いっぱい、見えない者が自分の周囲にいる感じの怖さ…いろんな場所から同時に
    少しずらして鳴らすくらいしっかりしたい」。

    ⚠⚠ **これはヘッドホン用の近似で、実機の定位ではない。** 実機は Meta XR の HRTF が
    前後・上下まで解くが、ここでやるのは**左右の振り分けと、後ろの体を少し曇らせる**だけ。
    それでも「1 点から鳴っているか / 何か所からか」は判る。

    並び:
      1. **旧**（体を全部足して 1 点から）… 4 周目 A の群れが 8 体ぶん重なった 1 つの声
      2. **新**（8 体を輪へ振って）… 同じ中身が別々の場所から
      3. 3 周目 C の増え方（一人 → ＋2 体 → ＋4 体）を新しい置き方で
    """
    import math

    def spread(bodies, bearings, sec):
        out = np.zeros((int(sec * sk.SR), 2))
        for y, deg in zip(bodies, bearings):
            t = tile(y, sec)
            rad = math.radians(deg)
            # 左右は等パワー。⚠ 後ろ（|deg|>90）は**耳介の陰**ぶん高い方を落とす（前後の手掛かり）。
            pan = math.sin(rad)
            l = math.cos((pan + 1) * math.pi / 4) * math.sqrt(2)
            r = math.sin((pan + 1) * math.pi / 4) * math.sqrt(2)
            m = t[:, 0] * 0.5 + t[:, 1] * 0.5
            if math.cos(rad) < 0:
                m = sk.biquad_fft(m, "lp", 4200.0, 0.707, 0.0, sk.SR)
            out += np.stack([m * l, m * r], axis=1)
        return out

    def bodies_of(stem, n):
        return [sk.to_stereo(sk.read_wav(os.path.join(SRC, f"{stem}_{i}.wav"))[0])
                for i in range(1, n + 1)]

    swarm = bodies_of("bed_dolls_laugh", 8)
    ga = bodies_of("bed_dolls_grow_a", 2)
    gb = bodies_of("bed_dolls_grow_b", 4)
    one = load("bed_doll_one")

    seg, gap = 12.0, 1.0
    total = seg * 4 + gap * 3
    out = np.zeros((int(total * sk.SR), 2))

    # ① 旧: 8 体を足して 1 点（真正面）から
    lay(out, spread([sum(swarm[1:], swarm[0])], [0.0], seg), 0.0)
    # ② 新: 8 体を輪へ（45° ごと・少し振る）
    ring8 = [22.0, 70.0, 108.0, 156.0, 198.0, 246.0, 292.0, 334.0]
    lay(out, spread(swarm, ring8, seg), seg + gap)
    # ③ 3 周目 C の増え方（一人 → ＋2 → ＋4）を新しい置き方で
    at = (seg + gap) * 2
    lay(out, spread([one], [22.0], seg), at)
    lay(out, spread([one] + ga, [22.0, 70.0, 108.0], seg), at + seg + gap)

    print(f"   0.0s 旧（8 体を足して 1 点から）   {seg + gap:.1f}s 新（8 体を輪へ）   "
          f"{at:.1f}s 一人   {at + seg + gap:.1f}s 一人 ＋ 2 体")
    return out


REF = os.path.join(OUT, "ref")


def build_alert() -> np.ndarray:
    """**警告つきの切替音を、実機で鳴る場に置いて聴く**（`canon/LEDGER.md` 0134）。

    0106 の版（240ms / -12dB）は警告を**素の無音の上で**聴いて決めた。実機ではその下に劇伴と
    装置の唸りが敷いてあり、人形視点の 4 カットは `transition:"glitch"` なので**乱れの一撃が
    同じフレームで鳴る**。そこでは警告（-36 LUFS）が消えて「他と同じに聞こえる」と判定された。
    ここはその場を再現して、**旧の版 → 新の版**を同じ所に置く:

    1. 旧 → 新 を素の無音の上で 3 組（違いそのものを聴く。旧は `logs/sound/ref/` に退避した版）
    2. 2 周目 C の接近そのまま — 劇伴（乱れのたびに 0.35 退く）＋ 装置の唸り ＋ カットごとの乱れの一撃
       ＋ 警告つきの切替 ＋ 4 カット目で声。**旧の版 → 新の版**
    3. 同じ 2 回を**内蔵スピーカー越し**で（展示で耳に届くのはこちら）

    ⚠ 散らし方は実機の写し（切替 gain 0.85 / 音程 ±3.5% / 音量 ±1.5dB、乱れは 1 回目の 0.80）。
    ⚠ 劇伴の退きは `SoundBedLogic` の半減期 0.55 秒を包絡で写した近似。
    """
    rng = np.random.default_rng(20260904)
    new = [load(f"sfx_switch_alert_{i}") for i in range(1, SWITCH_VARIANTS_N + 1)]
    prev = []
    for i in range(1, SWITCH_VARIANTS_N + 1):
        p = os.path.join(REF, f"sfx_switch_alert_prev_{i}.wav")
        if os.path.exists(p):
            prev.append(sk.to_stereo(sk.read_wav(p)[0]))
    glitch = [load(f"sfx_glitch_{i}") for i in range(1, 4)]
    call = load("sfx_doll_call")

    def shot(clip: np.ndarray, gain: float = 0.85, pitch: float = 0.035, db: float = 1.5) -> np.ndarray:
        r = 1.0 + float(rng.uniform(-pitch, pitch))
        m = max(8, int(len(clip) / r))
        x = np.linspace(0, len(clip) - 1, m)
        c = np.stack([np.interp(x, np.arange(len(clip)), clip[:, ch]) for ch in (0, 1)], axis=1)
        return c * gain * 10 ** (float(rng.uniform(-db, db)) / 20.0)

    steps = (1.0, 0.9, 0.8, 1.4)          # pov_1 → pov_2 → pov_3 → pov_4（追いつき）
    seg = 8.0

    def approach(alerts: list) -> np.ndarray:
        one = np.zeros((int(seg * sk.SR), 2))
        score = tile(load_score(), seg)
        # 劇伴は乱れの一撃のたびに 0.35 退き、半減期 0.55 秒で戻る（`SoundCueLogic.DuckFor(Glitch)`）。
        tt = np.arange(len(score)) / sk.SR
        env = np.ones(len(score))
        at, cuts = 1.5, []
        for sec in steps:
            cuts.append(at)
            at += sec
        for tc in cuts:
            d = 0.35 * np.exp(-np.maximum(tt - (tc + 0.035), 0.0) * np.log(2) / 0.55) * (tt >= tc + 0.035)
            env = np.minimum(env, 1.0 - d)
        lay(one, score * env[:, None], 0.0)
        lay(one, tile(load("bed_device"), seg), 0.0)
        for k, tc in enumerate(cuts):
            lay(one, shot(alerts[k % len(alerts)]), tc)
            # 乱れの一撃は次のフレーム（+35ms）。1 回目の高さ 0.80 → 徐々に 1.0（ここは 0.85）。
            lay(one, shot(glitch[k % 3], gain=0.85, pitch=0.03, db=1.2), tc + 0.035)
        lay(one, call, cuts[-1])          # 4 カット目の頭で声（散らさない）
        return one

    parts: list = []
    t = 0.5
    total = 0.5 + 3 * 2.0 + 1.0 + (seg + 0.8) * 4 + 1.0
    out = np.zeros((int(total * sk.SR), 2))
    if prev:
        for i in range(3):
            lay(out, prev[i] * 0.85, t)
            lay(out, new[i] * 0.85, t + 0.8)
            t += 2.0
    else:
        for i in range(3):
            lay(out, new[i] * 0.85, t)
            t += 2.0
    t += 1.0
    marks = []
    for label, alerts in (("旧", prev if prev else new), ("新", new)):
        block = approach(alerts)
        lay(out, block, t)
        marks.append((label, t))
        t += seg + 0.8
    for label, alerts in (("旧", prev if prev else new), ("新", new)):
        block = through_speaker(approach(alerts))
        lay(out, block, t)
        marks.append((label + "・内蔵SP", t))
        t += seg + 0.8
    print("   0.5s 旧 → 新 を素で 3 組   "
          + "   ".join(f"{tt:.1f}s {lab}（接近の 4 カット・1.5s から）" for lab, tt in marks))
    return out


def build_break() -> np.ndarray:
    """**パススルーが割れて 2D に移るところ**（`canon/LEDGER.md` 0112）。

    段 4 の実際の縁で 3 回鳴らす:

    1. 新しい割れる音**だけ**（素で形を聴く）
    2. **旧版 → 新版**（同じ場所に置いて比べる。`logs/sound/ref/sfx_shatter_prev.wav` が
       あるときだけ。無ければ飛ばす）
    3. **段 4 の通し**（割れる → 0.37 秒の静けさ → スクリーンが出る → 1.2 秒後に鈴）。
       ⚠ ここが 0057 でユーザーが指定した並びで、判定はこの形でしかできない

    ⚠ 敷く音（部屋 ＋ 装置）を下に置く。**尻が敷く音へ沈むかどうか**が今回直した点なので、
    素の無音で聴くと直っているように聞こえてしまう。
    """
    new = load("sfx_shatter")
    prev_path = os.path.join(REF, "sfx_shatter_prev.wav")
    prev = None
    if os.path.exists(prev_path):
        prev, _ = sk.read_wav(prev_path)
        prev = sk.to_stereo(prev)

    total = 24.0
    out = np.zeros((int(total * sk.SR), 2))
    # ① 素で 1 回
    lay(out, new, 0.5)
    # ② 旧 → 新（敷く音の上で）
    bed_from = 3.5
    lay(out, tile(load("bed_room"), total - bed_from) * 0.85, bed_from)
    lay(out, tile(load("bed_device"), total - bed_from), bed_from)
    at = 4.5
    if prev is not None:
        lay(out, prev, at)
        at += 3.0
    lay(out, new, at)
    at += 3.5
    # ③ 段 4 の通し（0057 の並び）
    lay(out, new, at)
    lay(out, load("sfx_screen_on"), at + FRAME * SCREEN_ON_AT)
    lay(out, load("amb_bell"), at + FRAME + BELL_AFTER_SWAP)
    print(f"   0.5s 新しい割れる音だけ   "
          f"{'4.5s 旧 → 7.5s 新（敷く音の上）' if prev is not None else '（旧版が無いので比べは飛ばす）'}   "
          f"{at:.1f}s 段 4 の通し（割れる → 静けさ {FRAME * (1 - SCREEN_ON_AT):.2f}s → "
          f"スクリーン → 鈴）")
    return out


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


def build_pitch() -> np.ndarray:
    """**笑いの高さ**を呼びかけと並べて聴く（`canon/LEDGER.md` 0117）。

    ユーザー指示「人形の笑い声が全体的に高すぎるから、あーそぼーくらいの高さ前後に」。
    ⇒ **呼びかけ → 一人の笑い → 呼びかけ → 群れ → 呼びかけ** の順で、素の無音の上に並べる。

    ⚠ **敷く音を敷かない。** ここで判定するのは高さと声色だけなので、
      装置の声が乗ると 200Hz 前後が埋まって比べにくくなる（他の見本とは狙いが違う）。
    ⚠ 判定してほしいのは 2 つ: **同じ人形に聞こえるか**と、**まだ高い / 下げすぎか**。
      どちらも `ingest-sounds.py` の `LAUGH_PITCH` の 1 数字で動く（下げるほど小さい値）。
    ⚠ 数値の物証は `py -3.11 tools/sound-pitch.py`（基音 Hz と呼びかけとの比）。
    """
    call = load("sfx_doll_call")
    one, crowd = load("bed_doll_one"), load("bed_dolls_laugh")
    total = 30.0
    out = np.zeros((int(total * sk.SR), 2))
    lay(out, call, 0.5)
    lay(out, one[:int(9.0 * sk.SR)], 2.8)
    lay(out, call, 12.5)
    lay(out, crowd[:int(9.0 * sk.SR)], 14.8)
    lay(out, call, 24.5)
    lay(out, one[int(12.0 * sk.SR):int(18.0 * sk.SR)], 24.5)
    print("   0.5s あーそぼー   2.8s 一人（3 周 A・B）   12.5s あーそぼー   "
          "14.8s 群れ（4 周 A）   24.5s 声と一人を重ねる")
    return out


def build_score_swap() -> np.ndarray:
    """**呼びかけの後で劇伴が入れ替わる所**（`canon/LEDGER.md` 0119）。

    追いついた文脈を運ぶのが黒い覆いではなく曲になったので、**ここが演出そのもの**。
    判定してほしいのは 2 つ:

    1. **新しい曲の音量**（`show.json` の `bgmTracks[].volume` = 0.57）
    2. **クロスフェードの尺**（カットの `bgm.fadeInSec` = 3.0 秒）

    ⚠⚠ **同じ中身を 2 回鳴らす。** 前半はヘッドホン、後半は**内蔵スピーカーを通した形**。
      この曲は正体が 200Hz より下にあるので、**2 つは別の音に聞こえるのが正しい**。
      展示で耳に届くのは後半の方。
    """
    seg, gap = 17.0, 1.0
    call = load("sfx_doll_call")
    sw = load("sfx_switch_alert_1")
    dev = load("bed_device")
    a, b = load_score(), load_score2()

    one = np.zeros((int(seg * sk.SR), 2))
    # 装置の声（本編と同じ 0.5 前後）。曲だけで判定すると実機より静かな場に置くことになる。
    lay(one, tile(dev, seg), 0.0, 0.5)
    # 追いつきまでは HorrBGM。t=5.4 から 3 秒かけて等パワーで入れ替わる。
    fade_at, fade_sec = 5.4, 3.0
    n_fade = int(fade_sec * sk.SR)
    t = np.linspace(0.0, 1.0, n_fade)
    out_g, in_g = np.cos(t * np.pi / 2), np.sin(t * np.pi / 2)
    i0 = int(fade_at * sk.SR)
    head = int(40 * sk.SR)
    lay(one, a[head:head + i0], 0.0)                                    # 交代まで
    lay(one, a[head + i0:head + i0 + n_fade], fade_at, out_g)
    lay(one, b[:n_fade], fade_at, in_g)                                 # 入る側は曲の頭 = 2:06
    lay(one, b[n_fade:int(seg * sk.SR)], fade_at + fade_sec)
    # 呼びかけ（カットの頭 = 交代の 1.4 秒前）と、同じ縁の警告つき切替音。
    lay(one, sw, fade_at - 1.4)
    lay(one, call, fade_at - 1.4)

    total = seg * 2 + gap
    out = np.zeros((int(total * sk.SR), 2))
    lay(out, one, 0.0)
    lay(out, through_speaker(one), seg + gap)
    print(f"   0.0s HorrBGM ＋ 装置の声   {fade_at - 1.4:.1f}s あーそぼー（＋警告つき切替）   "
          f"{fade_at:.1f}s 交代（3 秒）   {seg + gap:.1f}s 同じ中身を**内蔵スピーカー越し**で")
    return out


def build_relief() -> np.ndarray:
    """**ホラー軽減モード**（`canon/LEDGER.md` 0154）。

    判定してほしいのは 2 つ:

    1. **既存の音が半分**（-6dB）で、怖さが和らいだと感じるか
    2. **陽気な曲の高さ**（焼いた -24.0 LUFS）— 主に立っているか / 大きすぎないか

    ⚠⚠ **同じ場面を 2 回鳴らす。前半が平時、後半が軽減モード。**
      片側だけ聴くと「小さくなった」も「曲が乗った」も判定できない
      （`~/.claude/rules/work-style.md` §2-3 の校正と同じ）。
    ⚠ 後半はさらに**内蔵スピーカーを通した形**も付ける。展示で耳に届くのはそちら。
    """
    seg, gap = 14.0, 1.0
    dev = load("bed_device")
    sw = load("sfx_switch_alert_1")
    laugh = load("bed_doll_one")
    score = load_score()
    relief = load("bed_relief")

    def scene(gain: float, with_relief: bool) -> np.ndarray:
        """本編のひとこま（装置の声 ＋ 劇伴 ＋ 笑い ＋ 切替が 3 発）。"""
        one = np.zeros((int(seg * sk.SR), 2))
        lay(one, tile(dev, seg), 0.0, 0.5 * gain)
        lay(one, score[int(40 * sk.SR):int((40 + seg) * sk.SR)], 0.0, gain)
        lay(one, tile(laugh, seg), 0.0, gain)
        for at in (2.6, 7.1, 11.4):
            lay(one, sw, at, gain)
        # ⚠ 陽気な曲は倍率を掛けない（実機の `ignoreListenerVolume` と同じ）。
        if with_relief:
            lay(one, tile(relief, seg), 0.0)
        return one

    plain = scene(1.0, with_relief=False)
    soft = scene(FixedCamVr_RELIEF_GAIN, with_relief=True)

    total = seg * 3 + gap * 2
    out = np.zeros((int(total * sk.SR), 2))
    lay(out, plain, 0.0)
    lay(out, soft, seg + gap)
    lay(out, through_speaker(soft), (seg + gap) * 2)
    print(f"   0.0s 平時   {seg + gap:.1f}s 軽減モード   "
          f"{(seg + gap) * 2:.1f}s 同じ軽減モードを**内蔵スピーカー越し**で")
    return out


def build_outro() -> np.ndarray:
    """**終幕**（`canon/LEDGER.md` 0111 の電源断 ＋ 0125 の電源が落ちる音）。

    判定してほしいのは 2 つ:

    1. **電源が落ちる音の高さ**（`ingest-sounds.py` の -17.0 LUFS）。装置の声と劇伴が
       まだ鳴っている上に置かれるので、埋もれても突き出てもいけない
    2. **その後の静けさ**（装置の声が 1.5 秒で引き、部屋の音が前へ出て、報告の打鍵だけが残る）

    ⚠⚠ **同じ中身を 2 回鳴らす。** 前半はヘッドホン、後半は**内蔵スピーカーを通した形**。
      展示で耳に届くのは後半の方。
    """
    seg, gap, lead = 9.0, 1.0, 3.0
    # 実機の尺（`OutroTiming.Default` / `SoundBedLogic` の写し）。向こうを変えたらここも直す。
    collapse, dark = 0.9, 1.2
    dev_fade, room_rise, score_fade = 1.5, 2.0, 2.0
    report_chars = 41                       # `OutroReportText.Compose` の見える字（改行は鳴らさない）

    dev, room = load("bed_device_worn"), load("bed_room")
    off = load("sfx_power_off")
    score = load_score2()                   # 終幕の手前は入れ替わった後の曲（0119）

    one = np.zeros((int(seg * sk.SR), 2))
    n_dev_fade = int(dev_fade * sk.SR)
    n_room = int(room_rise * sk.SR)
    n_score = int(score_fade * sk.SR)

    # 本編の続き（装置の声 1.0 / 劇伴 / 部屋は 0）→ lead 秒で終幕へ入る。
    lay(one, tile(dev, lead), 0.0, 1.0)
    lay(one, tile(dev, dev_fade), lead, ramp(n_dev_fade, 1.0, 0.0))
    lay(one, tile(score, lead), 0.0, 1.0)
    lay(one, tile(score, score_fade), lead, ramp(n_score, 1.0, 0.0))
    # 部屋の音は終幕の頭からせり上がる（装置が引いたぶん現実が前へ出る）。
    lay(one, tile(room, room_rise), lead, ramp(n_room, 0.0, 0.85))
    lay(one, tile(room, seg - lead - room_rise), lead + room_rise, 0.85)

    # ⚠ 電源が落ちる音は**終幕の頭**。画が潰れ始めるのと同じフレーム。
    lay(one, off, lead)
    # 報告の打鍵は Collapse ＋ Dark の後（＝ 装置がまだ 1 つだけ仕事をしている）。
    rng = np.random.default_rng(2026)
    lay(one, type_burst(report_chars, TYPE_CPS, rng), lead + collapse + dark)

    total = seg * 2 + gap
    out = np.zeros((int(total * sk.SR), 2))
    lay(out, one, 0.0)
    lay(out, through_speaker(one), seg + gap)
    print(f"   0.0s 本編の続き   {lead:.1f}s **電源が落ちる**（画は潰れ始める）   "
          f"{lead + collapse + dark:.1f}s 報告の打鍵（{report_chars} 字）   "
          f"{seg + gap:.1f}s 同じ中身を**内蔵スピーカー越し**で")
    return out


def main() -> int:
    print("素材を 1 本ずつ:")
    emit("preview_materials", build_materials(), "何がどんな音か")
    print("導入の流れ:")
    emit("preview_intro", build_intro(), "⚠ 近似。実機の混ざり方は SoundBedLogic が決める")
    print("連絡の面の打鍵:")
    emit("preview_comms", build_comms(), "本編の敷く音の上で。頭の 2 本は速さの比べ")
    print("最後の演出（人形がたくさん出てくる所）:")
    emit("preview_dolls", build_dolls(), "笑い → 3 秒後に③の連絡。4 周目の敷く音の上で")
    emit("preview_lang", build_lang(),
         "言語の切り替えを 4 回（交互に鳴るか・2 本の大きさが揃っているか）")
    print("3 周目（入れ替わってから C で増えるまで）:")
    emit("preview_swap", build_swap(), "実機と同じ式。**尺も実機どおり** — 増え方が判定そのもの")
    print("パススルーが割れて 2D に移るところ:")
    emit("preview_break", build_break(), "旧 → 新の比べ ＋ 段 4 の通し（0057 の並び）")
    print("カメラ切替の変種:")
    emit("preview_switch", build_switch(),
         "変種 6 本 → 旧（1 本を 8 発）→ 新（6 本を 8 発）→ 2 周目 C の刻み")
    print("人形の呼びかけ（2 周目 C の追いつき）:")
    emit("preview_call", build_call(), "前半は声だけ / 後半は切替音と重なった所")
    print("人形の笑い — 周囲に大勢いるか（0139・旧 1 点 → 新 8 か所）:")
    emit("preview_laugh_ring", build_laugh_ring(),
         "⚠ ヘッドホン用の近似（左右と前後の曇りだけ）。**何か所から鳴っているか**が判定")
    print("警告つきの切替音（0134・実機で鳴る場に置いて、旧 → 新）:")
    emit("preview_alert", build_alert(),
         "素で 3 組 → 接近そのまま（旧 → 新）→ 同じ 2 回を内蔵スピーカー越し。**警告が聞こえるか**が判定")
    print("笑いの高さ（呼びかけと並べる）:")
    emit("preview_pitch", build_pitch(),
         "呼びかけ → 一人 → 呼びかけ → 群れ → 重ねる。**同じ人形に聞こえるか**")
    print("追いつきの後で劇伴が入れ替わる所:")
    emit("preview_score_swap", build_score_swap(),
         "前半ヘッドホン / 後半は内蔵スピーカー越し。**音量と交代の尺**が判定")
    print("ホラー軽減モード（0154・平時 → 軽減 → 内蔵スピーカー越し）:")
    emit("preview_relief", build_relief(),
         "**既存の音が半分になったか**と**陽気な曲の高さ**が判定。片側だけでは決まらない")
    print("終幕（電源が落ちて、報告が打たれる）:")
    emit("preview_outro", build_outro(),
         "前半ヘッドホン / 後半は内蔵スピーカー越し。**電源が落ちる音の高さ**が判定")
    print(f"\n→ {OUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
