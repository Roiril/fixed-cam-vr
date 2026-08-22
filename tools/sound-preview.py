# -*- coding: utf-8 -*-
"""**人が聴くための** 3 本を書き出す。

```
py -3.11 tools/sound-preview.py
```

出るもの（`logs/sound/`）:
  - `preview_materials.wav` … 素材を 1 本ずつ並べたもの（何がどんな音かを確かめる）
  - `preview_intro.wav` … 真っ暗 → A → 題字 → 導入 → 本編の入り口を通しで並べたもの（**流れ**）
  - `preview_ambient.wav` … 周ごとの環境音の入れ替え（**実機と同じ式**・ループの継ぎ目も入る）
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
    ("bed_room", "環境音・1 周目（合成の部屋のトーン）", 6.0),
    ("bed_room_lap2", "環境音・2 周目（もらった dark horror ambient）", 8.0),
    ("bed_room_lap3", "環境音・3 周目（もらった dark horror soundscape）", 8.0),
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
    ("sfx_switch_1", "カメラ切替（もらった「カメラ切り替え」・**変種は 1 本だけ**）", 0),
    ("sfx_glitch_1", "映像の乱れ 1", 0),
    ("sfx_glitch_2", "映像の乱れ 2", 0),
    ("sfx_glitch_3", "映像の乱れ 3", 0),
    ("sfx_shell_open", "終幕 — 隔離が開いて現実が戻る", 0),
    ("amb_creak_1", "【鳴らない】家鳴り 1 — 2026-08-15 に全廃（音源は残してある）", 0),
    ("amb_creak_2", "【鳴らない】家鳴り 2 — 同上", 0),
    ("amb_bell", "鈴（もらった「鈴２」）— **段 3（輪郭だけの世界）の頭**に 1 回だけ", 0),
    ("bed_dolls_laugh", "人形の群れ（4 周目 A の締め・8 体 40 回の輪）", 12.0),
    ("bed_doll_one", "入れ替わった人形 — 一人（3 周目 A・B）", 11.0),
    ("bed_dolls_grow_a", "同・増える 2 体（3 周目 C の前半）", 13.0),
    ("bed_dolls_grow_b", "同・さらに増える 4 体（3 周目 C の後半）", 17.0),
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
    # ⚠ 切替は 1 本だけ（もらった音源）。実機は音程と音量を散らすが、ここでは並べるだけ。
    for at in (t_run + 2.2, t_run + 5.6, t_run + 8.4):
        lay(mix, load("sfx_switch_1"), at, 0.9)

    print(f"  真っ暗 0.0 / A {t_a:.1f} / 題字が消え始める {t_glyph_out:.1f} / "
          f"素通し {t_black:.1f} / 段 1 {t_real:.1f} / 格下げ {t_degrade:.1f} / "
          f"輪郭 {t_structure:.1f} / 割れる {t_frame:.1f} / "
          f"スクリーンが出る {t_frame + FRAME * SCREEN_ON_AT:.1f} / 映像だけ {t_swap:.1f} / "
          f"鈴 {t_swap + BELL_AFTER_SWAP:.1f} / 本編 {t_run:.1f}")
    return mix


def build_ambient() -> np.ndarray:
    """周ごとの環境音の入れ替え（`canon/LEDGER.md` 0049）を、**実機と同じ混ぜ方**で並べる。

    ユーザー指示は「差し替えを気づかれないようにクロスフェード」で、
    **気づくかどうかは耳でしか判定できない**（数値はどれも上限の内側に収まってしまう）。
    だからここは近似ではなく、`SoundBedLogic.ApplyAmbientMix` と同じ式を写している:

      - 位置（0..2）を**半減期 2.5 秒**で寄せる
      - その小数部を等パワー（cos/sin）で 2 本へ配る ＝ 二乗の和が常に 1

    ⚠ **2 周目の尺は 18.7 秒**なので、この 25 秒の区間で 1 度ループする。
    **入れ替えと巻き戻りの両方が 1 本で聴ける。**
    """
    half_life = 2.5
    laps = [("bed_room", 14.0), ("bed_room_lap2", 25.0), ("bed_room_lap3", 16.0)]
    total = sum(sec for _n, sec in laps)
    n = int(total * sk.SR)

    beds = [tile(load(name), total) for name, _sec in laps]

    # 各標本での位置（実機は毎フレーム寄せる。ここは 1 標本ごとに同じ式で進める）
    pos = np.zeros(n)
    cur = 0.0
    dt = 1.0 / sk.SR
    k = 0.5 ** (dt / half_life)
    edges, acc = [], 0.0
    for _name, sec in laps:
        acc += sec
        edges.append(acc)
    for i in range(n):
        t = i * dt
        target = 0.0 if t < edges[0] else (1.0 if t < edges[1] else 2.0)
        cur = target + (cur - target) * k
        pos[i] = cur

    lo = np.clip(np.floor(pos), 0, 1).astype(int)
    f = pos - lo
    out_g = np.cos(f * np.pi / 2)[:, None]
    in_g = np.sin(f * np.pi / 2)[:, None]

    mix = np.zeros((n, 2))
    for i in range(3):
        w = np.where(lo == i, out_g[:, 0], 0.0) + np.where(lo + 1 == i, in_g[:, 0], 0.0)
        mix += beds[i][:n] * w[:, None]

    print(f"  1 周目 0.0〜{edges[0]:.0f}s / 2 周目 〜{edges[1]:.0f}s（18.7s で 1 度ループ）"
          f" / 3 周目 〜{edges[2]:.0f}s ・ 入れ替えは半減期 {half_life} 秒")
    return mix


# --- 連絡の面の打鍵（`canon/LEDGER.md` 0056）--------------------------------
#
# ⚠ 打つ速さ・散らし幅は **C# 側と対**（`CommsPanelLogic.CharsPerSec` /
#   `TypeAudioCue`）。片方だけ変えると、聴いて決めた密度が実機と違う。
TYPE_CPS = 12.0
TYPE_PITCH = 0.04       # ±（`TypeAudioCue.PitchSpread`）
TYPE_GAIN_DB = 2.0      # ±（`TypeAudioCue.GainSpreadDb`）
TYPE_VARIANTS = 8

# (文面, 打つ字数 ＝ **見える字だけ**。改行では鳴らさない)
COMMS = [
    ("調査を開始してください。", 12),
    ("異変を見つけたら／ボタンを長押ししてください／装置が解析して対処を試みます", 35),
    ("異変を抹消しました", 9),
    ("異常は検出されませんでした", 13),
    ("異常があなたを／取り込もうとしています。／抹消してください。", 28),
]


def type_burst(hits: int, cps: float, rng: np.random.Generator) -> np.ndarray:
    """1 通ぶんの打鍵。**実機と同じ選び方**（直前と同じ変種を引かない・音程と音量を散らす）。"""
    clips = [load(f"sfx_type_{i + 1}") for i in range(TYPE_VARIANTS)]
    step = int(sk.SR / cps)
    out = np.zeros((step * hits + max(len(c) for c in clips), 2))
    last = -1
    for i in range(hits):
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

    頭に「いまの速さ（22 文字/秒）」と「変更後（12 文字/秒）」を並べてある。
    **1 文字 1 発は 22 文字/秒だと連続音になる** — そこが判定してほしい所。
    """
    rng = np.random.default_rng(20260816)
    marks = [(1.5, 11, 22.0, "① 22 文字/秒（いまの絵の速さ）"),
             (4.0, 11, TYPE_CPS, "① 12 文字/秒（変更後）")]
    t = 7.0
    for text, hits in COMMS:
        marks.append((t, hits, TYPE_CPS, f"「{text}」{hits} 字"))
        t += hits / TYPE_CPS + 2.5
    total = t + 1.5

    # 本編の高さ（`rules/sound-design.md` §4 の表）。
    out = tile(load("bed_room"), total) * 0.34 + tile(load("bed_device"), total) * 1.0
    for at, hits, cps, label in marks:
        lay(out, type_burst(hits, cps, rng), at)
        print(f"  {at:5.1f}s  {label}")
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

    # 4 周目 ＝ 装置は痩せ切っていて、環境音は 3 周目のもの（§4 の表）。
    out = (tile(load("bed_room_lap3"), total) * 0.34
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
    out = (tile(load("bed_room_lap3"), total) * 0.34
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
    print("周ごとの環境音の入れ替え:")
    emit("preview_ambient", build_ambient(), "実機と同じ式（等パワー・半減期 2.5 秒）")
    print("連絡の面の打鍵:")
    emit("preview_comms", build_comms(), "本編の敷く音の上で。頭の 2 本は速さの比べ")
    print("最後の演出（人形がたくさん出てくる所）:")
    emit("preview_dolls", build_dolls(), "笑い → 3 秒後に③の連絡。4 周目の敷く音の上で")
    print("3 周目（入れ替わってから C で増えるまで）:")
    emit("preview_swap", build_swap(), "実機と同じ式。**尺も実機どおり** — 増え方が判定そのもの")
    print(f"\n→ {OUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
