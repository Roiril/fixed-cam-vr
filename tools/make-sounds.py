# -*- coding: utf-8 -*-
"""廻リ視の音を合成して `Assets/Art/Audio/Generated/` へ書く。

```
py -3.11 tools/make-sounds.py            # 全部
py -3.11 tools/make-sounds.py bed_seal   # 1 本だけ
```

**この作品の音は「装置の音」と「現実の音」の 2 層しか持たない。**
劇伴（作者の声）を足すと「装置は正直に映している」という前提が壊れ、
すり替えに気づく瞬間の価値が下がる（`rules/streaming.md` の「やってはいけないこと」と同じ理屈）。
設計の正本は `.claude/rules/sound-design.md`。

⚠ **音量はピークではなくラウドネス（LUFS）で揃える。** 一撃の音と持続音は同じピークでも
聴感が 20dB 違うので、ピーク合わせだと実機で必ず片方が消えるか刺さる。

⚠ **合成の乱数種は固定してある。** 同じ版のスクリプトは同じ波形を出す
（音を変えていないのに差分が出ると、何が効いたのか分からなくなる）。
"""

from __future__ import annotations

import json
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import soundkit as sk  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")

# ⚠ **`Resources/` 配下に置く。** 実行時に `Resources.Load<AudioClip>("Sound/<名前>")` で拾う
#   （フォント・人形・素材と同じ流儀）。ここへ置いたものは**使わなくてもビルドに全部入る**ので、
#   使わなくなった音は必ず消す。取り込み設定は `.\tools\unity.ps1 menu sound-import` が揃える。
OUT_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                       "Assets", "Resources", "Sound")

# ---- 音量の目標（LUFS）------------------------------------------------------
# 層ごとに 1 つずつ。実行時のゲインが 1.0 付近に収まるので、卓で音量を触らずに済む。
BED = -32.0      # 敷く音。会話の下に居る高さ
SPOT = -23.0     # 節目の一撃
EVENT = -17.0    # 導入の山（隔離が閉じる / 割れる / 装置が点く）
PEAK_DB = -3.0   # すべての素材の上限（true peak はこれ以下に収まる）


def norm_peak(y: np.ndarray, peak_db: float = PEAK_DB, drive_db: float = 4.0) -> np.ndarray:
    """**一度きりの山**（隔離が閉じる / 割れる / 装置が点く）はこちらで揃える。

    ⚠ **短い衝撃をラウドネス（LUFS）で揃えてはいけない。** LUFS は 400ms の平均なので、
    2 秒の中に 0.05 秒だけ立つ衝撃は実力よりずっと小さく出る。それを目標値まで持ち上げようと
    丸めを 14dB 掛けると、**手応え（波高率）だけが消えて音量も届かない**という最悪の形になる
    （2026-08-12 実測: `sfx_seal_close` が丸め 14dB で -20.4 LUFS 止まり）。

    繰り返す音（切替・乱れ）は逆に**必ず LUFS で揃える** — 1 回の体験で 9 回以上並ぶので、
    聴感の高さが揃っていないと機械の音に聞こえる。
    """
    z = sk.soft_clip(y, drive_db) if drive_db > 0 else y
    return z * (10 ** ((peak_db - sk.true_peak_db(z)) / 20.0))


def norm_lufs(y: np.ndarray, target: float, max_drive_db: float = 14.0) -> np.ndarray:
    """ラウドネスを target へ合わせる。**届かなければ尖頭を丸めて届かせる。**

    ピークだけを合わせると、波高率の大きい一撃の音は目標より 10dB 以上小さくなる
    （2026-08-12 実測）。丸め量は必要なぶんだけ自動で探し、それでも届かなければ
    「届かなかった」まま返す（黙って歪ませるより、数字で足りないと言う方がよい）。
    """
    drive = 0.0
    best = y * (10 ** ((target - sk.lufs(y)) / 20.0))
    while True:
        z = sk.soft_clip(y, drive) if drive > 0 else y
        z = z * (10 ** ((target - sk.lufs(z)) / 20.0))
        tp = sk.true_peak_db(z)
        if tp <= PEAK_DB:
            return z
        best = z * (10 ** ((PEAK_DB - tp) / 20.0))
        if drive >= max_drive_db:
            return best
        drive = min(max_drive_db, drive + 2.0)


# ===========================================================================
# 敷く音（ループ）
# ===========================================================================


def bed_seal(sec: float = 16.0) -> np.ndarray:
    """封印の箱の唸り。**外から見た隔離**（`SealedBox`）に定位する 3D の音。

    LEDGER 0011「長年使い込まれた金属だけど金属じゃない得体のしれない物体」
    「テカリはしない」「くっきり光る所は少なめ、暗めのところが多く、わずかに赤く光っている」
    「外側に少しだけにじみ出て不安定」から:

    - **調和しない部分音**（円板の振動モード比 1 : 2.76 : 5.40）で「金属だが楽器ではない」
    - **近接した 2 音の唸り**で「不安定」— 一定の LFO ではなく、周期の違う 2 つのうねりが
      すれ違うので、聞いていて周期が読めない
    - **高域を落とす**（3kHz 以上をほぼ捨てる）＝ テカらない。金属の輝きは倍音の上の方にある
    - 熾のはぜる音は**まばら**（毎秒 1〜2 回・不揃い）。等間隔だと機械になる
    """
    n = int(sec * sk.SR)
    base = sk.loop_freqs(sec, [58.0, 59.3, 87.0])          # 唸り 1.3Hz と 27.7Hz
    plate = sk.loop_freqs(sec, [58.0 * 2.76, 58.0 * 5.40])  # 円板モード（非調和）

    # ⚠ 基音も**帯**で置く（純音は発振器に聞こえる — 2026-08-12）。
    #    近接した 2 音の唸りという 0011 の意図は、帯どうしの重なりでむしろ強くなる。
    # ⚠ 基音も**帯ノイズ**で置く。0011 の「不安定」は、揺れている帯どうしの重なりの方が
    #   純音の唸りより強く出る（純音は「発振器 2 本」に聞こえる）。
    body = sk.mix(
        (sk.tone_band(sec, base[0], q=34.0, seed=121), 1.00),
        (sk.tone_band(sec, base[1], q=34.0, seed=122), 0.85),
        (sk.tone_band(sec, base[2], q=26.0, seed=123), 0.55),
        (sk.tone_band(sec, plate[0], q=20.0, seed=124), 0.22),
        (sk.tone_band(sec, plate[1], q=16.0, seed=125), 0.09),
    )
    # 2 つのうねりをすれ違わせる（3 回転と 7 回転 ＝ 最小公倍数がループ長）
    body *= 0.55 + 0.30 * sk.loop_lfo(sec, 3) + 0.15 * sk.loop_lfo(sec, 7, phase=1.1)

    # 空気（箱の中の圧）。**唸りの正体はここ。** 聞こえる帯域（200-1200Hz）に体を置き、
    # ゆっくり明滅させる ＝「暗い面の中でわずかに光っている」の音側。
    air = sk.loop_noise(sec, 150, 1300, slope_db_oct=-3.0, seed=101)
    air = sk.spec_shape(air, lambda f: sk.resonance(f, 240.0, 5.0, q=2.2)
                        * sk.resonance(f, 430.0, 4.0, q=2.6))
    air *= 0.45 + 0.30 * sk.loop_lfo(sec, 2, phase=0.7) + 0.12 * sk.loop_lfo(sec, 5, phase=2.0)

    # 熾（まばらな微かなはぜ）。位置は乱数だが種は固定。
    #
    # ⚠ **末尾をまたぐはぜは切らずに先頭へ回す。** 切ると減衰の途中で音が消えるので、
    #    巻き戻りにクリックが出る（2026-08-12 実測: 継ぎ目の飛びが隣接標本差の 3.41 倍。
    #    回すようにして 1 倍台へ落ちた）。円環として置けば周期性が保たれる。
    rng = np.random.default_rng(4711)
    ember = np.zeros(n)
    for _ in range(26):
        i = int(rng.integers(0, n))
        ln = int(rng.uniform(0.004, 0.020) * sk.SR)
        g = rng.uniform(0.25, 1.0)
        burst = sk.loop_noise(ln / sk.SR, 420, 2400, slope_db_oct=-2.0,
                              seed=int(rng.integers(1, 1 << 30)))
        burst = burst * np.exp(-np.linspace(0, 6, ln)) * g
        idx = (np.arange(ln) + i) % n
        np.add.at(ember, idx, burst)

    # ⚠⚠ **正体を低い方に置かない。** 58/87Hz の基音だけで作ったら内蔵スピーカーでの損失が
    #    -18.3dB ＝ **箱の声が実機で存在しないのと同じ**だった（2026-08-12 実測）。
    #    低い基音は「ヘッドホンなら効くボーナス」として残し、**物としての正体は
    #    円板モード（160/313Hz）と熾のはぜ（800-2600Hz）が担う**。
    #    ⚠ それでも 2.4kHz より上は落とす — 金属の輝きは倍音の上の方にあり、
    #      持ち上げた瞬間に LEDGER 0011 の「テカらない」を破る。
    # ⚠ **熾を主役にしない。** 一度 0.66 まで上げたら波形が平坦＋棘だけになり、
    #   波高率 21.4dB ＝ 敷く音ではなく一撃の形になった（絵を開いて気づいた。数値は緑だった）。
    #   LEDGER 0011 の「暗めのところが多く、わずかに赤く光っている」は**持続する光**が主で、
    #   熾は時々宿るもの。持続を担うのは `air`、熾は上に散る粒。
    y = sk.mix((body, 0.30), (air, 0.62), (ember, 0.20))
    y = sk.spec_shape(y, lambda f: sk.shelf(f, 2400, -18, 1.4)
                      * sk.shelf(f, 200, 15.0, 1.1)               # 中域を前へ
                      * sk.resonance(f, 430.0, 5.0, q=2.0)        # 中の空洞（大きな容器）
                      * sk.resonance(f, 780.0, 3.5, q=2.4))       # 板の鳴き
    return norm_lufs(y, BED)          # ← 3D で鳴らすので **モノ**（下の書き出しで mono=True）


def bed_room(sec: float = 20.0) -> np.ndarray:
    """隔離された部屋のトーン。**閉じた小さい空間**の音。

    段 1 で会場が黒へ落ち、実物の壁と足元の床だけが残る。そこは「施設の一室」なので:

    - 商用電源の唸り（50Hz 系の 100 / 150 Hz。50Hz そのものは Quest の内蔵スピーカーで
      鳴らないので、聞こえる高調波に置く）
    - 小さい部屋の色（250〜900Hz の狭い帯にわずかな山 ＝ 箱鳴り）
    - **明るいものは 1 本だけ**（3.15kHz の細い線 ＝ 蛍光灯の安定器）。
      これが「無人の施設」を 1 本で運ぶ。増やすと「機械室」になって和ホラーから離れる
    """
    # ⚠⚠ **純音を 1 本置かない。** 2026-08-12 まで 100/150/200Hz の正弦波を重ねていて、
    #    突出が **38.8dB** ＝ 発振器そのものだった（ユーザー指摘「チープな電子音はチープすぎる」）。
    #    実物のハムは鉄心の磁歪と整流の脈流が多数の高調波を**不揃いな高さ**で出したもので、
    #    1 本ずつがわずかに揺れている。`buzz` が近接部分音の帯として作る。
    mains = sk.buzz(sec, 50.0, [(2, 1.00), (3, 0.30), (4, 0.55), (6, 0.22), (8, 0.10)], seed=211)
    mains *= 0.9 + 0.1 * sk.loop_lfo(sec, 5)
    # 唸りの周りに空気を置く（線が単独で立たないよう、山に乗せる）
    mains += sk.loop_noise(sec, 80, 420, slope_db_oct=-2.0, seed=212) * 0.35

    room = sk.loop_noise(sec, 160, 3600, slope_db_oct=-3.2, seed=202)
    room = sk.spec_shape(room, lambda f: sk.resonance(f, 320.0, 4.0, q=2.5)
                         * sk.resonance(f, 640.0, 3.0, q=3.5)
                         * sk.shelf(f, 4000, -14, 1.2))

    # 蛍光灯の安定器。**1 本の線ではなく細い帯**にして、しかも揺らす。
    line = sk.cluster(sec, 3150.0, count=5, spread=0.010, seed=213)
    line *= 0.55 + 0.45 * sk.loop_lfo(sec, 11, phase=0.4)
    line += sk.loop_noise(sec, 2600, 3900, slope_db_oct=0.0, seed=214) * 0.9

    # ⚠ 唸りを大きくすると内蔵スピーカーで消える帯域に体が寄る。**残るのは中域**。
    y = sk.mix((mains, 0.16), (room, 0.72), (line, 0.010))
    return norm_lufs(sk.widen(y, 0.55, seed=2020), BED)


def _device(sec: float, worn: float, seed: int) -> np.ndarray:
    """装置（カメラ・伝送・スクリーン）の音。`worn` 0 = 新しい / 1 = 痩せた。

    LEDGER 0012「周を重ねるごとに着実に粗くなっていく」に**音でも対応する**。
    絵の解像度が落ちるのと同じ進行度で `bed_device` → `bed_device_worn` を混ぜる。

    痩せると:
      - 搬送トーンが**わずかに下がって濁る**（水晶が狂った、ではなく「電源が弱っている」）
      - 量子化ノイズが増える（帯域が上へ広がり、粒が粗くなる）
      - 電源の唸りが前へ出る（信号が痩せた分だけ地の音が相対的に立つ）

    ⚠ **一段ずつ落とす音は作らない。** LEDGER 0012 は「急に落ちるではなくばれないように」で、
    段の音を鳴らすと劣化そのものを名指ししてしまう。連続量で混ぜるだけにする。
    """
    # ⚠⚠ **搬送を純音で置かない。** 2026-08-12 まで 1900Hz の正弦波＋整数倍の高調波で、
    #    突出が **41.2dB**。整数倍はさらに「発振器」を強める（実物の共振は非調和）。
    #    痩せた側（`worn`）は既に粒へ埋めてあって 12.9dB しかなく、**そちらが正解だった**。
    fc = 1900.0 * (1.0 - 0.06 * worn)
    # ⚠⚠ **トーンそのものをノイズにする**（`tone_band`）。純音でも近接部分音の束でも
    #    突出は 100dB を超える（実測）。狭帯域ノイズなら音程は聞こえたまま 20dB 台に収まる。
    tone = sk.mix(
        (sk.tone_band(sec, fc, q=24.0, seed=331), 1.00),
        (sk.tone_band(sec, fc * 1.97, q=18.0, seed=332), 0.16),   # ← 2.00 にしない（非調和）
        (sk.tone_band(sec, fc * 3.06, q=14.0, seed=333), 0.06),
    )
    tone += sk.loop_noise(sec, fc * 0.72, fc * 1.34, slope_db_oct=0.0, seed=334) * 0.60
    # 痩せるほど揺れる（水晶ではなく電源の揺らぎ）
    tone *= 1.0 - (0.06 + 0.22 * worn) * sk.loop_lfo(sec, 13, phase=0.3)

    psu = sk.buzz(sec, 60.0, [(2, 1.00), (4, 0.45), (6, 0.20), (8, 0.09)], seed=335)
    psu += sk.loop_noise(sec, 95, 330, slope_db_oct=-2.0, seed=336) * 0.45

    hiss_hi = 7000 + 1500 * worn        # ⚠ 上へ伸ばしすぎると刺さる（3 分間かけ続ける音）
    hiss = sk.loop_noise(sec, 900, hiss_hi, slope_db_oct=-1.5 + 1.0 * worn, seed=seed)
    if worn > 0:
        # 粗い粒（量子化の階段）。標本を間引いて保持すると階段状の歪みが出る
        hold = max(1, int(round(3 + 9 * worn)))
        idx = (np.arange(len(hiss)) // hold) * hold
        hiss = (1 - worn) * hiss + worn * hiss[idx % len(hiss)]

    # ⚠ 「電源の唸りが前へ出る」は低域を増やすことなので**やめた**（内蔵スピーカーで消える方向）。
    #   痩せた手触りは中域の濁りとざらつきだけで作る。
    # ⚠ **ノイズを主役にする。** 装置の音は「ヒスの中から唸りが覗く」であって
    #    「唸りにヒスが添えてある」ではない。トーンを 0.26 → 0.15 へ引き、ヒスを上げた。
    y = sk.mix((tone, 0.15 - 0.05 * worn), (psu, 0.13 - 0.05 * worn),
               (hiss, 0.52 + 0.30 * worn))
    # ⚠ **痩せた側を「暗くする」で作らない。** 傾きだけで暗くしたら内蔵スピーカーでの損失が
    #   -7.9dB になり、**3 周目の装置の声が実機で消えた**（2026-08-12 実測）。
    #   低解像度の手触りは「上を落とす ＋ **中域を濁らせる**」で作る。中域は必ず残る帯域。
    y = sk.spec_shape(y, lambda f: sk.tilt(f, 1000, -1.0)
                   * sk.resonance(f, 620.0, 6.0 * worn, q=1.4)    # 濁り
                   * sk.resonance(f, 1500.0, 4.0 * worn, q=1.2)   # ざらつき（残る帯域に置く）
                   * sk.shelf(f, 4500, -9.0 * worn, 1.1)          # 上だけ落とす ＝ 解像度が落ちる
                   * sk.shelf(f, 300, -9.0 * worn, 1.1))          # 低い方を削って中域を立てる
    return norm_lufs(sk.widen(y, 0.30, seed=3030), BED)


def bed_device(sec: float = 12.0) -> np.ndarray:
    return _device(sec, worn=0.0, seed=303)


def bed_device_worn(sec: float = 12.0) -> np.ndarray:
    return _device(sec, worn=1.0, seed=303)


def bed_static(sec: float = 6.0) -> np.ndarray:
    """信号断の砂嵐。`SignalLostFx` の絵と対。

    映像の砂嵐は「無」ではなく「受信機が何かを受けている」音。だから真っ白ではなく、
    低い方に体があり、上がざらついている。**速い明滅**を掛けて「掴めていない」を出す。
    """
    y = sk.loop_noise(sec, 180, 11000, slope_db_oct=-1.2, seed=404)
    flutter = 0.72 + 0.28 * sk.loop_lfo(sec, 97, shape="tri")
    rumble = sk.loop_noise(sec, 90, 260, slope_db_oct=-6.0, seed=405)
    y = sk.mix((y * flutter, 0.85), (rumble, 0.28))
    return norm_lufs(sk.widen(y, 0.70, seed=4040), BED + 4.0)


# ===========================================================================
# 一撃の音
# ===========================================================================


def sfx_switch(variant: int = 0) -> np.ndarray:
    """カメラ切替のリレー。**暗転（下り 70ms）の中に収める**ので全体 140ms。

    実物のリレーは 2 回鳴る（可動鉄片が動く音と、接点が当たる音）。1 発にすると
    「ボタンの効果音」になり、装置の物としての手応えが消える。

    3 種類あるのは、1 回の体験で 9 回以上鳴るから。同じ波形が並ぶと機械に聞こえる。
    """
    seeds = [(510, 0.0068, 1.00, 2350.0), (511, 0.0081, 0.92, 2120.0), (512, 0.0059, 1.06, 2580.0)]
    seed, gap, g, ring_f = seeds[variant % 3]
    sec = 0.14
    n = int(sec * sk.SR)
    y = np.zeros(n)

    def tick(at: float, amp: float, lo: float, hi: float, dec: float, sd: int):
        i = int(at * sk.SR)
        ln = int(0.012 * sk.SR)
        b = sk.loop_noise(ln / sk.SR, lo, hi, seed=sd)
        b = b * np.exp(-np.linspace(0, dec, ln)) * amp
        y[i:i + ln] += b[:max(0, min(ln, n - i))][:n - i]

    tick(0.000, 1.00, 900, 6500, 9.0, seed)          # 可動鉄片
    tick(gap, 0.62, 1600, 9000, 14.0, seed + 1)      # 接点

    # 金属の短いリング（非調和・すぐ消える）
    # 金属の短いリング。**純音の重ねではなく帯**（実物の接点は 1 つの音程を持たない）。
    ring_len = 0.075
    rt = sk.t_axis(ring_len)
    ring = sk.mix(
        (sk.tone_band(ring_len, ring_f, q=16.0, seed=seed + 7), 1.00),
        (sk.tone_band(ring_len, ring_f * 2.41, q=12.0, seed=seed + 8), 0.35),
        (sk.tone_band(ring_len, ring_f * 4.13, q=9.0, seed=seed + 9), 0.12),
    )
    ring += sk.loop_noise(ring_len, ring_f * 0.6, ring_f * 1.6, seed=seed + 10) * 0.70
    ring *= np.exp(-rt * 46.0)
    ri = int(gap * sk.SR)
    y[ri:ri + len(ring)] += ring[:min(len(ring), n - ri)] * 0.30

    y = sk.biquad(y, "hp", 220.0, 0.7)
    y = sk.env_fade(y, 0.0003, 0.010)
    return norm_lufs(sk.widen(y * g, 0.18, seed=5050 + variant), SPOT)


def sfx_glitch(variant: int = 0) -> np.ndarray:
    """映像の乱れ。`GlitchFx` の `_Glitch` と同時に鳴る。

    絵の乱れは「帯ごとの水平シフト ＋ 垂直の同期ずれ」なので、音も**帯が飛ぶ**形にする。
    連続したノイズではなく、短い区間が別の帯域へ跳ぶ。
    """
    sec = 0.26
    n = int(sec * sk.SR)
    rng = np.random.default_rng(600 + variant)
    y = np.zeros(n)
    pos = 0
    while pos < n:
        ln = int(rng.uniform(0.008, 0.035) * sk.SR)
        ln = min(ln, n - pos)
        lo = float(rng.uniform(300, 2500))
        hi = lo * float(rng.uniform(2.0, 6.0))
        seg = sk.loop_noise(ln / sk.SR, lo, min(hi, 15000), seed=int(rng.integers(1, 1 << 30)))
        y[pos:pos + ln] = seg * float(rng.uniform(0.35, 1.0))
        pos += ln
    y *= sk.env_ar(sec, 0.001, 0.10, curve=2.6)
    y = sk.env_fade(y, 0.0005, 0.012)
    return norm_lufs(sk.widen(y, 0.65, seed=6060 + variant), SPOT)


def sfx_seal_close() -> np.ndarray:
    """段 1 — 隔離が閉じる。**導入で最初に来る山。**

    会場が黒へ落ち、実物の壁と足元の床だけが残る。世界が急に小さくなる音にする:

    1. 息を吸うような上昇（周囲の空気が持っていかれる）
    2. 低い衝撃（閉じた）
    3. **明るい尾を途中で断ち切る** — 部屋が小さくなると残響が消える。
       尾を自然に減衰させると「広い所で鳴った」に聞こえるので、わざと切る
    """
    sec = 2.4
    n = int(sec * sk.SR)

    pull = sk.sweep_band(1.15, 220, 1450, q=4.0, seed=705)
    pull *= np.linspace(0, 1, len(pull)) ** 2.2
    pull = sk.biquad(pull, "bp", 700, 0.8)

    air = sk.loop_noise(1.15, 300, 5200, slope_db_oct=-2.0, seed=701)
    air *= np.linspace(0, 1, len(air)) ** 3.0

    hit_len = 0.9
    hit = sk.mix(
        (sk.sweep_band(hit_len, 210, 46, q=3.0, seed=706), 1.0),
        (sk.sweep_band(hit_len, 96, 38, q=3.0, seed=707), 0.7),
        (sk.loop_noise(hit_len, 120, 900, slope_db_oct=-6.0, seed=702), 0.5),
    )
    hit *= sk.env_ar(hit_len, 0.004, 0.20, curve=2.2)

    tail_len = 0.34
    tail = sk.loop_noise(tail_len, 900, 7000, slope_db_oct=-3.0, seed=703)
    tail *= np.exp(-np.linspace(0, 4.5, len(tail)))
    tail[int(0.20 * sk.SR):] = 0.0                     # ← 断ち切る（部屋が小さくなった）
    tail = sk.env_fade(tail, 0.001, 0.004)

    # ⚠ 低い掃引だけの衝撃は内蔵スピーカーで**何も起きなかったこと**になる（実測 -6.5dB）。
    #   実際の扉や蓋も、低い響きと同時に中域の当たりが鳴る。その当たりを必ず足す。
    knock_len = 0.22
    knock = sk.loop_noise(knock_len, 280, 2000, slope_db_oct=-2.2, seed=704)
    knock = knock * sk.env_ar(knock_len, 0.001, 0.05, curve=3.0)

    y = np.zeros(n)
    y[:len(pull)] += pull * 0.42 + air * 0.30
    hi = int(1.12 * sk.SR)
    y[hi:hi + len(hit)] += hit[:n - hi] * 1.0
    y[hi:hi + len(knock)] += knock[:n - hi] * 1.15
    y[hi:hi + len(tail)] += tail[:n - hi] * 0.30
    y = sk.env_fade(y, 0.002, 0.05)
    return norm_peak(sk.widen(y, 0.35, seed=7070))


def sfx_shatter() -> np.ndarray:
    """段 4 — 現実が細かなセルに割れて、スクリーンの矩形へ吸い込まれる。

    **画と同じことを音でやる。** 破片は放射状に集まってひとつの矩形へ入るので:

    - 粒の密度が**上がって**いく（割れが進む）
    - 音像の広がりが**畳まれて**いく（左右にばらけた粒が中央へ集まる ＝ 吸い込み）
    - 最後にひとつの飲み込み

    ⚠ 広がりの畳み込みは「左右をだんだん同じにする」で作る。逆相で広げてから戻すと
    Quest のスピーカーでは最初から聞こえない（`mono_compat_db` が落ちる）。
    """
    sec = 1.9
    n = int(sec * sk.SR)
    rng = np.random.default_rng(801)
    left = np.zeros(n)
    right = np.zeros(n)

    grains = 260
    for k in range(grains):
        # 時間的に後ろへ寄せる（密度が上がる）
        u = float(rng.random()) ** 0.55
        at = u * 0.80 * sec
        i = int(at * sk.SR)
        ln = int(rng.uniform(0.004, 0.026) * sk.SR)
        if i + ln >= n:
            continue
        lo = 700 * (1 + 2.6 * u)                       # 進むほど上へ（吸い上げられる）
        g = sk.loop_noise(ln / sk.SR, lo, min(lo * 4.5, 16000),
                          seed=int(rng.integers(1, 1 << 30)))
        g = g * np.exp(-np.linspace(0, 5.5, ln)) * float(rng.uniform(0.3, 1.0))
        pan = float(rng.uniform(-1, 1)) * (1.0 - u) ** 1.4   # 進むほど中央へ
        left[i:i + ln] += g * (0.5 - 0.5 * pan)
        right[i:i + ln] += g * (0.5 + 0.5 * pan)

    # ⚠⚠ **正弦波の掃引を使わない**（2026-08-12 ユーザー指摘「ポウンという電子音」）。
    #    滑る純音は SF の効果音の語彙で、実物の「飲み込まれる」は必ず幅を持つ。
    #    `sweep_band` は帯域ノイズの粒を重ねて中心周波数を動かす（動きは残り、線が消える）。
    swallow_len = 0.75
    sw = sk.mix(
        (sk.sweep_band(swallow_len, 1300, 120, q=5.0, seed=803), 1.0),
        (sk.loop_noise(swallow_len, 200, 2600, slope_db_oct=-4.0, seed=802), 0.55),
    )
    sw *= sk.env_ar(swallow_len, 0.010, 0.22, curve=2.0)
    si = int(1.05 * sk.SR)
    left[si:si + len(sw)] += sw[:n - si] * 0.85
    right[si:si + len(sw)] += sw[:n - si] * 0.85

    y = np.stack([sk.env_fade(left, 0.001, 0.06), sk.env_fade(right, 0.001, 0.06)], axis=1)
    return norm_peak(y)


def sfx_swap() -> np.ndarray:
    """段 5 — 枠の中がカメラ映像になる。**装置が点く。**

    末尾が `bed_device` と同じ音（1900Hz の搬送 ＋ 電源の唸り）で終わるので、
    一撃から敷く音へ**そのまま渡る**。渡らないと「効果音が鳴って、別に地の音が始まった」に聞こえる。
    """
    sec = 1.3
    n = int(sec * sk.SR)
    t = sk.t_axis(sec)

    # ⚠ 下降する正弦波は「合成のバスドラム」の語彙。帯で置く。
    thump = sk.mix(
        (sk.sweep_band(0.45, 150, 58, q=3.0, seed=903), 1.0),
        (sk.loop_noise(0.45, 80, 700, slope_db_oct=-6.0, seed=901), 0.45),
    )
    thump *= sk.env_ar(0.45, 0.002, 0.13, curve=2.4)

    # 搬送が立ち上がる（無 → 定常）。**末尾は `bed_device` と同じ作り**にしないと、
    # 一撃から敷く音へ渡った瞬間に音色が変わって継ぎ目が出る。
    rise = np.clip((t - 0.10) / 0.85, 0, 1) ** 1.6
    carrier = sk.mix(
        (sk.tone_band(sec, 1900.0, q=24.0, seed=331), 1.00),
        (sk.tone_band(sec, 1900.0 * 1.97, q=18.0, seed=332), 0.16),
    ) * rise
    carrier += sk.loop_noise(sec, 1368, 2546, seed=334) * 0.60 * rise
    psu = sk.buzz(sec, 60.0, [(2, 1.0), (4, 0.45), (6, 0.20)], seed=335) * rise
    hiss = sk.loop_noise(sec, 900, 7000, slope_db_oct=-1.5, seed=902) * rise

    y = np.zeros(n)
    y[:len(thump)] += thump * 0.9
    y += carrier * 0.08 + psu * 0.05 + hiss * 0.20
    y = sk.env_fade(y, 0.001, 0.008)
    return norm_peak(sk.widen(y, 0.25, seed=9090), PEAK_DB - 2.0)


def sfx_shell_open() -> np.ndarray:
    """終幕 — 隔離が開いて現実が戻る。**段 1 の逆だが、山にしない。**

    終わりに一撃を置くと「決め台詞」になる。閉じたときの半分の高さで、
    上へ抜けて**何も残らない**（最後に鳴っているのは部屋のトーンだけ、が正しい）。
    """
    sec = 2.0
    n = int(sec * sk.SR)
    t = sk.t_axis(sec)

    # ⚠⚠ **正弦波の掃引を使わない**（2026-08-12 ユーザー指摘「ジューン⤴という電子音」）。
    #    ここは「隔離が開いて空気が抜ける」なので、そもそも幅のある音の方が正しい。
    # ⚠ q=3 でもまだ純度 0.50（境目）だった。**帯を広げるほどノイズに寄る。**
    release = sk.sweep_band(1.5, 90, 620, q=1.8, seed=1003)
    release *= np.sin(np.linspace(0, np.pi, len(release))) ** 1.3
    release += sk.loop_noise(1.5, 380, 1400, slope_db_oct=-2.0, seed=1002) * 0.85

    breath = sk.loop_noise(sec, 260, 4200, slope_db_oct=-3.5, seed=1001)
    breath *= np.exp(-((t - 0.55) / 0.42) ** 2)

    y = np.zeros(n)
    y[:len(release)] += release * 0.34
    y += breath * 0.40
    y = sk.env_fade(y, 0.02, 0.35)
    return norm_lufs(sk.widen(y, 0.45, seed=1010), -26.0)   # 閉じた音より下（山にしない）


def sfx_title_in() -> np.ndarray:
    """タイトルが立つ。**りん（仏具の鈴）の当たりを 1 つだけ。**

    和ホラーで最も効く単音で、しかも合成できる（非調和の部分音 ＋ 長い減衰 ＋ 唸り）。
    旋律を持たないので劇伴にならない。地は低い持ち上がりだけ。
    """
    sec = 3.4
    n = int(sec * sk.SR)
    t = sk.t_axis(sec)

    # りん: 基音 と、そこから外れた部分音。近接ペアの唸りが「余韻が揺れる」を作る
    f0 = 523.0
    partials = [(1.000, 1.00, 2.6), (1.006, 0.85, 2.6),      # 唸り 3.1Hz
                (2.756, 0.42, 4.4), (2.771, 0.34, 4.4),
                (5.404, 0.16, 7.5), (8.933, 0.06, 11.0)]
    bell = np.zeros(n)
    for r, a, dec in partials:
        bell += a * np.sin(2 * np.pi * f0 * r * t) * np.exp(-t * dec)
    strike = sk.loop_noise(0.012, 1800, 12000, seed=1101) * np.exp(-np.linspace(0, 7, int(0.012 * sk.SR)))
    bell[:len(strike)] += strike * 0.45

    swell = sk.mix(
        (sk.sine(sec, 87.0), 1.0),
        (sk.sine(sec, 130.5), 0.5),
        (sk.loop_noise(sec, 120, 900, slope_db_oct=-5.0, seed=1102), 0.8),
    )
    swell *= np.clip(t / 1.4, 0, 1) ** 1.8 * np.clip((sec - t) / 1.2, 0, 1) ** 0.6

    y = sk.mix((bell, 0.80), (swell, 0.14))   # ⚠ 地を厚くすると りん が低域に埋もれる
    y = sk.env_fade(y, 0.0005, 0.30)
    return norm_lufs(sk.widen(y, 0.40, seed=1110), -24.0)


def sfx_title_out() -> np.ndarray:
    """A を押してタイトルが閉じ、封印の箱の立つパススルーへ渡る。

    **息を呑む。** 上へ吸って、りんを手で押さえたように余韻を止める。
    ここで大きな音を出すと「始まった」の合図になってしまい、
    体験者はまだ何も始まっていない（箱の外に立っている）ことと食い違う。
    """
    sec = 1.6
    n = int(sec * sk.SR)
    t = sk.t_axis(sec)

    inhale = sk.loop_noise(0.9, 400, 6000, slope_db_oct=-2.5, seed=1201)
    inhale *= np.linspace(0, 1, len(inhale)) ** 2.6
    inhale = sk.biquad(inhale, "bp", 1500, 0.6)

    # ⚠ **合成のりんはやめた**（2026-08-12）。突出 37dB で発振器に寄っていたうえ、
    #   タイトルの音はユーザー提供のシネマチックに変わったので、ここは「息を呑む」だけでよい。
    #   実物の鈴は `amb_bell` として段 0 に置いてある。
    damp = sk.tone_band(sec, 523.0, q=9.0, seed=1221)
    damp += sk.loop_noise(sec, 300, 900, slope_db_oct=-1.0, seed=1224) * 0.55
    damp *= np.exp(-t * 1.4)
    damp[int(0.42 * sk.SR):] *= np.exp(-np.linspace(0, 26, n - int(0.42 * sk.SR)))  # 手で止める

    y = sk.mix((inhale, 0.55), (damp, 0.22))
    y = sk.env_fade(y, 0.004, 0.12)
    return norm_lufs(sk.widen(y, 0.30, seed=1210), -25.0)   # りんより静か（息を呑む）


# ===========================================================================

# (合成関数, ループするか, モノで書くか)
#
# ⚠ **モノ = 3D で鳴らす音。** Unity の spatializer はモノのクリップしか処理しない。
#    ステレオで書くと `spatialBlend=1` にしても定位せず、頭の中で鳴り続ける。
REGISTRY = {
    # ループ（敷く音）
    "bed_seal": (bed_seal, True, True),        # ← 封印の箱に定位する（3D）
    "bed_room": (bed_room, True, False),
    "bed_device": (bed_device, True, False),
    "bed_device_worn": (bed_device_worn, True, False),
    "bed_static": (bed_static, True, False),
    # 一撃
    "sfx_switch_1": (lambda: sfx_switch(0), False, False),
    "sfx_switch_2": (lambda: sfx_switch(1), False, False),
    "sfx_switch_3": (lambda: sfx_switch(2), False, False),
    "sfx_glitch_1": (lambda: sfx_glitch(0), False, False),
    "sfx_glitch_2": (lambda: sfx_glitch(1), False, False),
    "sfx_glitch_3": (lambda: sfx_glitch(2), False, False),
    # ⚠ `sfx_seal_close` は 2026-08-12 にユーザー提供の「黒い中に入るときの金属音」へ
    #    置き換えた（`tools/ingest-sounds.py` が焼く）。**ここに戻すと上書きしてしまう。**
    #    合成版の関数は設計の記録として残してある。
    "sfx_shatter": (sfx_shatter, False, False),
    "sfx_swap": (sfx_swap, False, False),
    "sfx_shell_open": (sfx_shell_open, False, False),
    # sfx_title_in は 2026-08-12 にユーザー提供の「シネマチックなタイトル」へ置き換えた
    # (tools/ingest-sounds.py が焼く)。合成版の関数は設計の記録として残してある。
    "sfx_title_out": (sfx_title_out, False, False),
}


def main(argv):
    os.makedirs(OUT_DIR, exist_ok=True)
    want = argv[1:] or list(REGISTRY)
    report = {}
    for name in want:
        if name not in REGISTRY:
            print(f"  ? 未知: {name}")
            continue
        fn, is_loop, is_mono = REGISTRY[name]
        y = fn()
        if is_mono and np.asarray(y).ndim == 2:
            y = np.asarray(y).mean(axis=1)
        path = os.path.join(OUT_DIR, f"{name}.wav")
        sk.write_wav(path, y, peak_db=PEAK_DB, dither=True, mono=is_mono)
        d = sk.describe(y)
        d["loop"] = is_loop
        d["mono"] = is_mono
        if is_loop:
            d["seam"] = sk.loop_seam(y)
        report[name] = d
        seam = f"  継ぎ目 x{d['seam']['jump']:.2f}" if is_loop else ""
        ch = "mono" if is_mono else f"mono {d['mono_db']:5.2f}dB"
        print(f"  {name:20s} {d['sec']:6.2f}s  {d['lufs']:6.1f} LUFS  "
              f"tp {d['true_peak_db']:5.1f}dB  重心 {d['centroid_hz']:5d}Hz  "
              f"波高 {d['crest_db']:4.1f}dB  {ch}{seam}")
    with open(os.path.join(OUT_DIR, "_generated.json"), "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)
    print(f"\n{len(report)} 本 → {OUT_DIR}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
