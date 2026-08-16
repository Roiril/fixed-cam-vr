# -*- coding: utf-8 -*-
"""ユーザーが持ってきた音を、この作品の音量体系へ揃えて `Assets/Resources/Sound/` へ入れる。

```
py -3.11 tools/ingest-sounds.py            # 既定の 5 本
py -3.11 tools/ingest-sounds.py --list     # 何をどこへ入れるか
```

**合成した音（`make-sounds.py`）とは別系統。** 向こうは「同じ版なら同じ波形」だが、
こちらは**元の音を持っている前提**なので、元が消えたら作り直せない。だから
`logs/sound/ingest/` に復号済みの生 WAV を残す（git 管理外・作り直しの種）。

やること 4 つ:

1. **復号**（mp3 → 48kHz ステレオ WAV）。`imageio_ffmpeg` が同梱する ffmpeg を使う
2. **端の無音を落とす**（後ろに 0.9 秒の無音がある素材があった）
3. **音量を揃える**（持続はラウドネス / 一撃は尖頭 — `rules/sound-design.md` §3）
4. **端をなめらかに落とす**（頭と尻のクリック止め）

⚠ **元の音を「良くしよう」としない。** 掛けるのは音量と端の処理だけで、
イコライザも圧縮も掛けない。ユーザーが選んだ音を別のものにしない。
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import soundkit as sk  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RAW = os.path.join(ROOT, "logs", "sound", "ingest")
OUT = os.path.join(ROOT, "Assets", "Resources", "Sound")
DOWNLOADS = os.path.join(os.path.expanduser("~"), "Downloads")

# (元ファイル名, 出力名, 揃え方, 目標, 使い先)
#
# ⚠ **「使い先」はシュビーの判断**であって、ユーザーが指定したのは
#    シネマチックなタイトル → タイトル出現 と、金属音のファイル名だけ。
#    残り 3 本の置き場は `canon/OPEN.md` に案として書いてある。
PLAN = [
    # ⚠ 尖頭で揃えると -14.0 LUFS になり、**体験で最初に聞く音がいちばん大きい**ことになる。
    #    「不気味で怖くていいけど、不快にはならないように」（2026-08-12 ユーザー指示）なので、
    #    聴感で揃えて 3dB 引く。何も競合していない場所なので、これでも充分に立つ。
    # ⚠⚠ **2026-08-15 に音源を差し替えた**（`canon/LEDGER.md` 0047・ユーザー指定）。
    #    旧: `シネマチックなタイトル.mp3`（2026-08-12 指定・11.9 秒）。復号済みは
    #    `logs/sound/ingest/src_title_cine.wav` に残してある（戻すならここのパスを差し替える）。
    ("Sonniss.com-GDC2026-GameAudioBundle1of5/344 Audio - Bass Drops & Downers Vol. 3/"
     "DSGNBass_Tone Downer (Reverb)_344 Audio_Bass Drops and Downers Vol 3.wav",
     "sfx_title_in", "lufs", -17.0,
     "タイトル出現（A を押した瞬間）。**尾は切らない** — 題字が消えた後も鳴り続けて "
     "パススルーへの継ぎ目を音が跨ぐ"),
    # ⚠ 尖頭で揃えると -12.4 LUFS で、**破砕（-14.0）より大きくなる**。
    #    導入の山は段 4 の破砕なので、隔離の音がそれを超えてはいけない。
    ("黒い中に入るときの金属音.mp3", "sfx_seal_close", "lufs", -16.0,
     "段 1 — 隔離が閉じて会場が消える。合成版を置き換える（実物の金属の重さは作れない）"),
    ("軋み.mp3", "amb_creak_1", "lufs", -26.0,
     "家鳴り。段 0 と本編にまばらに置く"),
    ("少し重い軋み.mp3", "amb_creak_2", "lufs", -26.0,
     "同上。2 種を回して同じ音が並ばないようにする"),
    # ⚠⚠ **2026-08-16 に -24.0 → -28.0 へ下げ、高い所を落とした**（`canon/LEDGER.md` 0057・
    #    ユーザー指示「もう少し音量を下げて、高すぎるところをカットして聞きやすいように」）。
    #    高域を削るのは §4.5「もらった音を良くしようとしない」の例外 — **ユーザーが指定した**。
    #    量は `TAME` に書いてある（1 か所）。
    ("鈴２.mp3", "amb_bell", "lufs", -28.0,
     "**完全にスクリーンになった所**（段 5 の入れ替えが終わった後）に 1 回だけ。"
     "誰も鳴らしていないのに鳴る"),
    # ⚠ **カメラ切替はユーザー指定の音源**（2026-08-16・`canon/LEDGER.md` 0057）。
    #    合成の 3 変種（`make-sounds.py` の `sfx_switch`）を置き換えた。
    #    1 回の体験で 9 回以上鳴るので「繰り返す一撃」の高さ（-23 LUFS・§3）へ揃える。
    # ⚠ `lufs!` — 波高 28dB の一撃なので素の音量合わせでは -3dBTP の天井に当たって
    #    -25.4 LUFS 止まりだった（他の一撃より 2.4dB 低い ＝ 暗転の中で聞こえにくい）。
    ("カメラ切り替え.mp3", "sfx_switch_1", "lufs!", -23.0,
     "カメラ切替（dip の黒の中で鳴る）。**変種は 1 本だけ** — 散らすのは音程と音量で行う"),
    # ⚠ 尖頭で揃えない。エネルギーの立ち上がりは尾が長く、尖頭で合わせると聴感が突出する。
    # ⚠⚠ **-19.0 → -17.0**（2026-08-13・`canon/LEDGER.md` 0030「何の音もなしに出るのは違和感がある」）。
    #    旧値は「導入の山（破砕）を超えないように」抑えたものだが、**破砕は同日に廃止**したので
    #    その制約はもう無い。いまの山はここなので、`make-sounds.py` の `EVENT`（-17.0）へ揃える。
    # ⚠⚠ **-17.0 では「鳴っていない」と判定された**（2026-08-13・LEDGER 0032）。本体が 0.2 秒しか
    #    無く波高 17.6dB なので、素の音量合わせでは -3dBTP の天井に当たって高さが出ない。
    #    `lufs!` は必要なぶんだけ尖頭を丸めて狙いまで持ち上げる。
    # ⚠⚠ **2026-08-16 に音源を差し替えた**（`canon/LEDGER.md` 0057・ユーザー指定）。
    #    旧: `Cyber03-mp3/Cyber03/Cyber03-2.mp3`（2026-08-13 指定）。復号済みは
    #    `logs/sound/ingest/src_screen_on_cyber03.wav` に残してある（戻すならここを差し替える）。
    ("Cyber14-mp3/Cyber14/Cyber14-1.mp3", "sfx_screen_on", "lufs!", -13.0,
     "段 4 — **現実からカメラ映像への入れ替えが終わった所**で 1 度だけ。**導入の山**。"
     "⚠ 割れる音（`sfx_shatter`）とは重ねない — 向こうが鳴り終わって静かになってから鳴る"),
    # ⚠ **周ごとの環境音**（2026-08-15・`canon/LEDGER.md` 0049 ユーザー指定）。
    #    1 周目は合成の `bed_room` のまま、2 周目・3 周目でこれらへクロスフェードする。
    #    高さは他の敷く音と同じ **-32 LUFS**（§3）。ここを外すと差し替えの瞬間に音量が動いて
    #    「切り替わった」と気づかれる ＝ ユーザー指示（気づかれないように）に反する。
    ("dragon-studio-dark-horror-ambient-05-425468.mp3", "bed_room_lap2", "lufs", -32.0,
     "2 周目の環境音。**ループする**（21.7 秒・周の途中で尺が切れる）"),
    ("universfield-dark-horror-soundscape-345814.mp3", "bed_room_lap3", "lufs", -32.0,
     "3 周目の環境音。**ループする**（69.3 秒・1 周では届かないが保険）"),
]

# ---- ループにする音の折り返し（秒）-----------------------------------------
#
# ⚠⚠ **`bed_` で始まる音はループとして焼く**（`sound-lint.py` の `is_loop` と同じ規約）。
#
# もらった環境音は頭と尻が無音へ落ちている（実測: lap2 は尻 1 秒が -50.8dB）。そのまま
# `AudioSource.loop` に任せると、**尺のたびに音が消えて戻る**（21.7 秒ごとに 1 秒の空白）。
# ユーザー指示は「周の途中で尺が切れたらループする」なので、切れ目が聞こえてはいけない。
#
# ⇒ **尻を頭へ等パワーで折り返す**（尺は `LOOP_XF` ぶん縮む）。巻き戻りの瞬間には
#    「尻 → 頭」の混ざった区間が既に鳴っているので、密度も音量も途切れない。
#
# ⚠ これは §4.5「もらった音を良くしようとしない」の例外ではなく**端の処理**の側。
#    イコライザも圧縮も掛けていない（掛けるのは音量と、この折り返しだけ）。
#
# ⚠ **長くすれば良くなるわけではない。** `bed_room_lap2` で 6 秒を試したら、継ぎ目の密度差が
#    -2.5dB → **-8.83dB へ悪化**した（実測）。折り返しは尻の分を頭へ**足す**ので、長く取るほど
#    頭が厚くなり、一方で「新しい終わり」は素材の減衰の途中へ移る。両方が同時に効いて差が開く。
#    3 秒は測って選んだ値（`jump` x1.28 / 密度差 -2.5dB・どちらも上限の内側）。
LOOP_XF = 3.0

# ---- 1 本の録音から 1 発ずつ切り出すもの ------------------------------------
#
# ⚠ **これも「もらった音」の側**（合成ではない）。掛けるのは切り出しと音量と端の処理だけで、
#    イコライザも圧縮も掛けない（`rules/sound-design.md` §4.5）。
#
# ⚠⚠ **打鍵の録音は「押し込み」と「戻り」の二山でできている**（実測: 8 打とも押し込みの
#    80〜110ms 後に戻りが来て、あいだは digital silence）。切り出すのは**押し込みだけ**。
#    連絡の面は 83ms ごとに 1 文字打つので、**戻りが居るべき所には次の字の押し込みが来る** —
#    両方入れると密度が倍（24 発/秒）になり、カタカタではなく連続音になる。
#
# (元ファイル名, 出力名の頭, 期待する打数, 連なりの目標 LUFS, 刻み秒, 使い先)
CUTS = [
    ("PC-Keyboard04-mp3/PC-Keyboard04/PC-Keyboard04-04(Single-Mid).mp3",
     "sfx_type", 8, -28.0, 1.0 / 12.0,
     "連絡の面の打鍵音（`canon/LEDGER.md` 0056）。1 文字 = 1 発・12 文字/秒。"
     "8 種を回して同じ波形が並ばないようにする"),
]

CUT_ONSET_DB = -34.0     # これを超えたら 1 発の頭
CUT_GUARD = 0.30         # 頭を拾ったら次はこの秒数を見ない（戻りを別の打と数えない）
CUT_PRE = 0.004          # 頭の手前に残す
CUT_FADE = 0.008         # 尻のクリック止め。⚠ **頭は落とさない**（本体 15〜20ms の一撃なので）

# ⚠⚠ **切り出す長さは決め打ちにする。** 減衰の終わりを自動で探そうとして 2 回外した:
#    ①「無音が 5ms 続いた所」→ 押し込みと戻りのあいだは**無音まで落ちない**
#      （床が -35〜-42dB ある）ので戻りを飛び越して 200ms まで伸びた
#    ②「戻りの頭までのいちばん静かな所」→ 減衰の途中にある**孤立した 0 標本**を掴んで
#      24ms で切れた（-28dB の途中でぶつ切り ＝ ゲートを掛けた音になる）
#    実測は「押し込みの山 → 減衰は 45ms で -35dB → 床（-35〜-42dB）→ 戻りの山は最短 76.9ms」。
#    60ms なら押し込みの減衰は丸ごと残り、いちばん早い戻りの 17ms 手前で終わる。
CUT_BODY = 0.060
CUT_RELEASE_MARGIN = 0.012   # 戻りの山までこれだけ空いていること（切れていたら言う）

# ---- もらった音を「掛ける」もの（⚠ ユーザーが指定したときだけ）--------------
#
# §4.5 は「もらった音は音量と端の処理しか掛けない」。ここはその**明示的な例外**で、
# 入れてよいのは**ユーザーが耳で聴いて指示したとき**だけ。勝手に足さない。
#
# (出力名: (この周波数より上を, これだけ下げる, 遷移の幅オクターブ))
TAME = {
    # 2026-08-16・`canon/LEDGER.md` 0057「高すぎるところをカットして聞きやすいように」。
    # 鈴は 5kHz より上に細い倍音が立っていて、内蔵スピーカーで耳へ刺さる側に出る。
    "amb_bell": (4200.0, -10.0, 1.2),
}

# ---- 1 発の録音を小刻みに並べて 1 本にするもの ------------------------------
#
# ⚠ **これも「もらった音」の側**（合成ではない）。掛けるのは並べ方・音量・音程と端の処理だけで、
#    イコライザも圧縮も掛けない。**種は固定**なので、同じ版なら同じ波形が出る。
#
# ⚠⚠ **尺は「次の音が鳴る時刻」から逆算する。** 割れる音は段 4 の頭（進み 0.02 ＝ 0.05 秒）から
#    始まり、スクリーンが出る音は入れ替えが終わる進み 0.85（＝ 2.125 秒）に鳴る。
#    ユーザー指示は「割れる音はちゃんとこれを鳴らす前に終わらせて静かにしてから」なので、
#    **1.70 秒 ＝ 鳴り終わってから 0.37 秒の静けさが残る**（`canon/LEDGER.md` 0057）。
#
# (元ファイル名, 出力名, 目標 LUFS, 使い先)
SWARMS = [
    ("PC-Mouse06-mp3/PC-Mouse06/PC-Mouse06-1.mp3", "sfx_shatter", -19.0,
     "段 4 — 現実が細かく割れてスクリーンへ吸い込まれる。2026-08-16 ユーザー指定の音源を"
     "小刻みに並べ、音程と音量を散らしながら**だんだん小さく**する"),
]

SWARM_SEC = 1.70          # 全長（上の逆算）
SWARM_GRAIN = 0.130       # 1 粒の長さ（元の一撃は本体 155ms）
SWARM_STEP_HEAD = 0.021   # 頭の刻み（≈ 48 粒/秒 ＝「ほんとに小刻み」）
SWARM_STEP_TAIL = 0.052   # 尻の刻み（散っていく ＝ まばらになる）
SWARM_STEP_JITTER = 0.18  # 刻みの揺らぎ（±・比）。等間隔だと機械の連射になる
SWARM_PITCH_HEAD = 0.92   # 音程の中心（頭）
SWARM_PITCH_TAIL = 1.38   # 同（尻）。破片が小さくなるほど高い
SWARM_PITCH_SPREAD = 0.20 # 1 粒ごとの散らし（±・比）
SWARM_TAIL_DB = -30.0     # 最後の粒の高さ（「最後らへんは本当に小さく」）
SWARM_DECAY_POW = 1.9     # 減り方の曲線（1 = 直線 / 大きいほど早く小さくなる）
SWARM_GAIN_JITTER_DB = 2.5
SWARM_PAN = 0.55          # 左右の散らし（0 = 真ん中 / 1 = 片側だけ）
SWARM_SEED = 20260816


# ---- 1 本の声を「たくさんの人形が笑っている」に組むもの ---------------------
#
# ⚠ **これも「もらった音」の側**。掛けるのは並べ方・音程・音量・左右だけで、
#    イコライザも圧縮も掛けない（`rules/sound-design.md` §4.5）。**種は固定**。
#
# ユーザー指定（2026-08-16・`canon/LEDGER.md` 0062）:
#   「いろんな人形がささやいてる感じで、音の高さや音量を変えたり、音程のカーブを、
#     だんだんと高くしたり低くしたりしたバージョンを何個か、スタート時間をちょっとずつずらして、
#     一部重なる感じとかにして、いろんな人形が笑ってるみたいな演出にしたい」
#
# ⚠ **1 声ずつ別の人形として作る。** 乱数で散らすのではなく**表に書く** — 何体が
#    どんな声で笑うかは演出そのものなので、走行ごとに変わってはいけないし、
#    「3 体目をもう少し低く」と言われたときに直す場所が要る。
#
# (始まり, 音程の始め, 音程の終わり, 音量 dB, 左右)   ※音程は 1.0 = 元のまま
CHORUS_VOICES = [
    (0.00, 1.00, 1.06, -1.0, -0.15),   # 最初の 1 体。ほぼ素のまま、わずかに上がる
    (0.40, 1.22, 1.14, -4.0, +0.55),   # 小さい人形。高い所から下りてくる
    (0.85, 0.86, 0.92, -3.0, -0.62),   # 大きい人形。低くゆっくり上がる
    (1.25, 1.34, 1.52, -7.0, +0.30),   # 遠くの 1 体。上がりきる
    (1.70, 0.78, 0.72, -4.5, -0.40),   # いちばん低い。さらに下がる
    (2.10, 1.12, 1.02, -4.0, +0.72),   # 右奥
    (2.55, 0.92, 0.88, -5.5, +0.10),   # 中央のうしろ
    (3.00, 1.45, 1.30, -8.0, -0.78),   # 左のいちばん遠く。高い所から下りる
    # ⚠⚠ ここで**ひと息あける**（0.25 秒だけ -36dB へ落ちる）。ずっと 6〜7 体だと
    #    1 枚の壁に聞こえ、逆に 0.5 秒あけると**笑いが止まった**ように聞こえる（実測で両方見た）。
    #    抜けたあと 8 体まで増えるので、人形が「増えてくる」形になる。
    (3.75, 0.68, 0.74, -3.5, +0.22),   # 大きい人形がもう 1 体
    (4.10, 1.05, 1.18, -5.0, -0.08),   # 真ん中
    (4.50, 0.83, 0.79, -4.5, -0.85),   # 左のすぐそば
    (4.85, 1.28, 1.40, -7.0, +0.44),   # 右のうしろ
    (5.25, 0.97, 0.90, -4.0, -0.30),   # 中央左
    (5.65, 1.16, 1.08, -6.0, +0.88),   # いちばん右
    (6.10, 0.74, 0.80, -5.0, +0.02),   # 低い声が真ん中から
    (6.60, 1.02, 1.12, -6.5, -0.50),   # 最後にもう 1 体
]

# ⚠ **後ろの声を前より小さくしすぎない。** 素直に減らすと「笑いが遠ざかっていく」に聞こえて、
#    人形が**出てくる**場面と逆になる（実測: -7.5 / -6.5 / -8.5 にしたら尻すぼみだった）。
#    数が減るぶんは自然に薄くなるので、1 体ずつの高さは最後まで残す。

# (元ファイル名, 出力名, 目標 LUFS, 使い先)
CHORUS = [
    ("ufufufu.mp3", "amb_dolls_laugh", -20.0,
     "最後の演出（4 周目 A の締め）で人形がたくさん出るところ。7 体ぶんを重ねて笑わせる"),
]

CHORUS_TAIL = 0.35        # 最後の声が鳴り終わってから足す余白（秒）
CHORUS_GLIDE_POW = 1.4    # 音程の動き方（1 = 直線 / 大きいほど後半で動く）


def glide(c: np.ndarray, r0: float, r1: float, sr: int) -> np.ndarray:
    """音程を <paramref name="r0"/> から <paramref name="r1"/> へ滑らせながら読む。

    ⚠ **速さごと変える**（テープと同じ）。読み取り位置を「そのときの速さ」の累積で作るので、
    区間で切って繋ぐ方式と違って**継ぎ目が原理的に出ない**。
    """
    n_in = len(c)
    # 出力長は分からないので、いちばん遅い読み方で上限を取ってから切り詰める。
    n_max = int(n_in / max(min(r0, r1), 1e-3)) + 8
    t = np.linspace(0.0, 1.0, n_max, endpoint=False)
    rate = r0 + (r1 - r0) * (t ** CHORUS_GLIDE_POW)
    pos = np.cumsum(rate) - rate[0]
    keep = pos < (n_in - 1)
    pos = pos[keep]
    return np.stack([np.interp(pos, np.arange(n_in), c[:, ch]) for ch in (0, 1)], axis=1)


def chorus_build(y, sr: int, target_lufs: float):
    """1 本の笑い声から「たくさんの人形が笑っている」を組む。"""
    src = sk.env_fade(sk.to_stereo(trim(y)), 0.004, 0.02)

    voices = []
    for at, r0, r1, db, pan in CHORUS_VOICES:
        v = glide(src, r0, r1, sr)
        lr = np.array([np.cos((pan + 1) * np.pi / 4), np.sin((pan + 1) * np.pi / 4)]) * np.sqrt(2)
        voices.append((at, v * (10 ** (db / 20.0)) * lr))

    total = max(at + len(v) / sr for at, v in voices) + CHORUS_TAIL
    out = np.zeros((int(total * sr), 2))
    for at, v in voices:
        i = int(at * sr)
        out[i:i + len(v)] += v

    out = sk.env_fade(out, 0.0, 0.12)
    out = out * 10 ** ((target_lufs - sk.lufs(out)) / 20.0)
    tp = sk.true_peak_db(out)
    if tp > -3.0:
        out = out * 10 ** ((-3.0 - tp) / 20.0)
    spans = [(at, len(v) / sr) for at, v in voices]
    return out, spans, total


def ingest_chorus(chorus, src_dir: str) -> None:
    """1 本の声を重ねて焼く（<see cref="CHORUS"/>）。"""
    for jp, name, target, _why in chorus:
        src = os.path.join(src_dir, jp)
        raw = os.path.join(RAW, f"src_{name}.wav")
        if os.path.exists(src):
            if not decode(src, raw):
                continue
        elif not os.path.exists(raw):
            print(f"  無い: {jp}（{src_dir} にも {RAW} にも）")
            continue
        else:
            print(f"  元 mp3 が無いので復号済みを使う: {name}")

        y, sr = sk.read_wav(raw)
        out, spans, total = chorus_build(y, sr, target)
        sk.write_wav(os.path.join(OUT, f"{name}.wav"), out, peak_db=-3.0)
        d = sk.describe(out)
        print(f"  {name:16s} {len(spans)} 体 / {d['sec']:.2f}s   {d['lufs']:6.1f} LUFS   "
              f"tp {d['true_peak_db']:5.1f}dB   鋭さ {d['sharp']:4.2f} 粗さ {d['rough']:4.2f}   "
              f"モノ {d['mono_db']:5.2f}dB   内蔵SP {d['speaker_db']:5.1f}dB")
        # 重なりの様子（0.5 秒ごとに何体が鳴っているか）。
        # **「一部重なる」が指定なので数で出す** — 全部 1 なら重なっていないし、
        # 常に 4 以上なら 1 つの塊に潰れている。
        marks = [str(sum(1 for at, dur in spans if at <= k * 0.5 < at + dur))
                 for k in range(int(total / 0.5) + 1)]
        print(f"    0.5 秒ごとの声の数: {' '.join(marks)}")
        # ⚠ **体数を増やすときはここを見る。** 増やしたぶん「1 つの塊」に近づくので、
        #    包絡の山（＝ 別々の笑いとして聞き分けられる数）が体数について増えているかを測る。
        #    山が体数より大きく少なければ、重なりすぎて混ざっている。
        print(f"    聞き分けられそうな山: {envelope_peaks(out)} 個 / {len(spans)} 体")


def envelope_peaks(y, sr: int = None, win: float = 0.05, drop_db: float = 4.0) -> int:
    """包絡の山を数える（**別々の音として立っている数**の目安）。

    `win` 秒ごとの RMS を取り、**前後より `drop_db` 以上高い**所を 1 山とする。
    厳密な聴覚モデルではない — 体数を増やしたときに**塊へ近づいていないか**を見るためだけの物差し。
    """
    sr = sr or sk.SR
    n = int(win * sr)
    m = np.array([np.sqrt(np.mean(np.mean(y[i:i + n], axis=1) ** 2))
                  for i in range(0, len(y) - n, n)])
    db = 20 * np.log10(np.maximum(m, 1e-9))
    peaks = 0
    for i in range(1, len(db) - 1):
        if db[i] >= db[i - 1] and db[i] > db[i + 1]:
            lo = min(db[max(0, i - 6):i].min(initial=db[i]), db[i + 1:i + 7].min(initial=db[i]))
            if db[i] - lo >= drop_db:
                peaks += 1
    return peaks


def resample(c: np.ndarray, ratio: float) -> np.ndarray:
    """音程を変える（速さごと変える ＝ テープと同じ）。`ratio` > 1 で高く短くなる。"""
    n = max(8, int(len(c) / ratio))
    x = np.linspace(0, len(c) - 1, n)
    return np.stack([np.interp(x, np.arange(len(c)), c[:, ch]) for ch in (0, 1)], axis=1)


def swarm_build(y, sr: int, target_lufs: float):
    """1 発の録音を小刻みに並べて、だんだん小さくなる群れにする。

    ⚠ **等間隔・等音量にしない。** そうすると「割れた」ではなく「連射した」に聞こえる。
    刻み・音程・音量・左右をすべて種固定の乱数で散らす。
    """
    st = sk.to_stereo(trim(y))
    env = np.max(np.abs(st), axis=1)
    head = int(np.argmax(env > 10 ** (CUT_ONSET_DB / 20))) if np.any(env > 10 ** (CUT_ONSET_DB / 20)) else 0
    a = max(0, head - int(0.003 * sr))
    grain = sk.env_fade(st[a:a + int(SWARM_GRAIN * sr)], 0.0, 0.012)

    rng = np.random.default_rng(SWARM_SEED)
    n = int(SWARM_SEC * sr) + len(grain) + int(0.05 * sr)
    out = np.zeros((n, 2))
    tail_lin = 10 ** (SWARM_TAIL_DB / 20.0)

    t, count = 0.0, 0
    while t < SWARM_SEC:
        u = t / SWARM_SEC
        g = sk.env_fade(resample(grain, SWARM_PITCH_HEAD + (SWARM_PITCH_TAIL - SWARM_PITCH_HEAD) * u
                                 + float(rng.uniform(-SWARM_PITCH_SPREAD, SWARM_PITCH_SPREAD))),
                        0.0, 0.010)
        amp = (1.0 - u) ** SWARM_DECAY_POW
        amp = tail_lin + (1.0 - tail_lin) * amp
        amp *= 10 ** (float(rng.uniform(-SWARM_GAIN_JITTER_DB, SWARM_GAIN_JITTER_DB)) / 20.0)
        # 左右は等パワーで振る（片側へ寄せるだけなのでモノにしても消えない）。
        pan = float(rng.uniform(-SWARM_PAN, SWARM_PAN))
        lr = np.array([np.cos((pan + 1) * np.pi / 4), np.sin((pan + 1) * np.pi / 4)]) * np.sqrt(2)
        i = int(t * sr)
        out[i:i + len(g)] += g * amp * lr
        step = (SWARM_STEP_HEAD + (SWARM_STEP_TAIL - SWARM_STEP_HEAD) * u)
        t += step * (1.0 + float(rng.uniform(-SWARM_STEP_JITTER, SWARM_STEP_JITTER)))
        count += 1

    out = sk.env_fade(out, 0.0, 0.05)
    out = out * 10 ** ((target_lufs - sk.lufs(out)) / 20.0)
    tp = sk.true_peak_db(out)
    if tp > -3.0:
        out = out * 10 ** ((-3.0 - tp) / 20.0)
    return out, count


def ingest_swarms(swarms, src_dir: str) -> None:
    """1 発の録音を小刻みに並べて焼く（<see cref="SWARMS"/>）。"""
    for jp, name, target, _why in swarms:
        src = os.path.join(src_dir, jp)
        raw = os.path.join(RAW, f"src_{name}.wav")
        if os.path.exists(src):
            if not decode(src, raw):
                continue
        elif not os.path.exists(raw):
            print(f"  無い: {jp}（{src_dir} にも {RAW} にも）")
            continue
        else:
            print(f"  元 mp3 が無いので復号済みを使う: {name}")

        y, sr = sk.read_wav(raw)
        out, count = swarm_build(y, sr, target)
        sk.write_wav(os.path.join(OUT, f"{name}.wav"), out, peak_db=-3.0)
        d = sk.describe(out)
        # 尻がちゃんと静かになっているか（**ユーザー指示の要**なので数字で出す）。
        last = out[-int(0.25 * sk.SR):]
        rest = 20 * np.log10(max(float(np.sqrt(np.mean(last ** 2))), 1e-9))
        print(f"  {name:16s} {count} 粒 / {d['sec']:.2f}s   {d['lufs']:6.1f} LUFS   "
              f"tp {d['true_peak_db']:5.1f}dB   最後の 0.25s {rest:5.1f}dB   "
              f"鋭さ {d['sharp']:4.2f} 粗さ {d['rough']:4.2f} 内蔵SP {d['speaker_db']:5.1f}dB")


def cut_strokes(y, sr: int, step_sec: float, target_lufs: float):
    """打鍵の録音から**押し込みだけ**を切り出し、連なりで音量を揃える。

    ⚠⚠ **1 発ずつ正規化しない。切り出した全部に同じ倍率を掛ける。**
    1 発ずつ揃えると、材料が持っている打鍵の強弱（強い打・弱い打）が消えて、
    等間隔・等音量で鳴る機械の音になる。素材の生きている所を殺さない。

    ⚠ 音量は **`step_sec` 間隔で連ねた状態**で測る。1 発の LUFS で揃えると、
    実際に耳へ届く「打っているあいだの高さ」から外れる（連なると密度のぶん上がる）。
    """
    st = sk.to_stereo(y)
    env = np.max(np.abs(st), axis=1)
    on_thr, guard = 10 ** (CUT_ONSET_DB / 20), int(CUT_GUARD * sr)

    heads, i = [], 0
    while i < len(env):
        if env[i] > on_thr:
            heads.append(i)
            i += guard
        else:
            i += 1

    cuts, rel_min = [], 1e9
    for s in heads:
        a = max(0, s - int(CUT_PRE * sr))
        cuts.append(sk.env_fade(st[a:a + int((CUT_PRE + CUT_BODY) * sr)], 0.0, CUT_FADE))
        # 戻りの山（押し込みの 40〜200ms 後でいちばん大きい所）。**床の小さな瘤に釣られない**
        # ように、閾値ではなく最大値で見る。
        lo, hi = s + int(0.04 * sr), min(len(env), s + int(0.20 * sr))
        if hi > lo:
            rel_min = min(rel_min, (lo + int(np.argmax(env[lo:hi])) - s) / sr)

    if not cuts:
        return [], 0.0, -70.0, 0.0
    stream = stroke_stream(cuts, sr, step_sec)
    gain = 10 ** ((target_lufs - sk.lufs(stream)) / 20.0)
    # 天井（-3dBTP）は連なりでも 1 発でも越えさせない。
    tp = max(sk.true_peak_db(c * gain) for c in cuts)
    if tp > -3.0:
        gain *= 10 ** ((-3.0 - tp) / 20.0)
    cuts = [c * gain for c in cuts]
    return cuts, gain, sk.lufs(stroke_stream(cuts, sr, step_sec)), rel_min


def stroke_stream(cuts, sr: int, step_sec: float, repeat: int = 3):
    """切り出しを刻み秒で並べた連なり（**測るためのもの**。焼かない）。

    順番を回すのは、同じ波形が並んだときだけ起きる干渉で音量を誤らないため。
    ⚠ 尺は 400ms の窓が何個か入る長さが要る（LUFS はゲート付きの平均なので）。
    """
    step = int(step_sec * sr)
    n = len(cuts) * repeat
    tail = max(len(c) for c in cuts)
    out = np.zeros((step * n + tail, 2))
    for k in range(n):
        c = cuts[k % len(cuts)]
        out[k * step:k * step + len(c)] += c
    return out


def norm_lufs_drive(y, target: float, max_drive_db: float = 12.0):
    """ラウドネスを target へ合わせる。**届かなければ尖頭を丸めて届かせる。**

    `make-sounds.py` の `norm_lufs` と同じ探索（丸めは必要なぶんだけ・上限つき）。
    届かなければ「届かなかった」まま返す（黙って歪ませるより数字で足りないと言う方がよい）。
    """
    drive = 0.0
    best = y * (10 ** ((target - sk.lufs(y)) / 20.0))
    while True:
        z = sk.soft_clip(y, drive) if drive > 0 else y
        z = z * (10 ** ((target - sk.lufs(z)) / 20.0))
        tp = sk.true_peak_db(z)
        if tp <= -3.0:
            return z
        best = z * (10 ** ((-3.0 - tp) / 20.0))
        if drive >= max_drive_db:
            return best
        drive = min(max_drive_db, drive + 2.0)


SILENCE_DB = -60.0     # これより静かな端は落とす
EDGE_FADE = 0.008      # 端のクリック止め（秒）


def decode(src: str, dst: str) -> bool:
    import imageio_ffmpeg
    exe = imageio_ffmpeg.get_ffmpeg_exe()
    r = subprocess.run([exe, "-y", "-v", "error", "-i", src,
                        "-ar", str(sk.SR), "-ac", "2", "-c:a", "pcm_s16le", dst],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0:
        print(f"  復号に失敗: {os.path.basename(src)}\n    {r.stderr.strip()[:200]}")
        return False
    return True


def trim(y: np.ndarray) -> np.ndarray:
    """前後の無音を落とす。**中の無音は触らない**（間も作品のうち）。"""
    m = np.max(np.abs(sk.to_stereo(y)), axis=1)
    thr = 10 ** (SILENCE_DB / 20)
    idx = np.flatnonzero(m > thr)
    if len(idx) == 0:
        return y
    a = max(0, idx[0] - int(0.005 * sk.SR))
    b = min(len(y), idx[-1] + int(0.05 * sk.SR))
    return y[a:b]


def fold_loop(y: np.ndarray, xf: float = LOOP_XF) -> np.ndarray:
    """尻 <paramref name="xf"/> 秒を頭へ等パワーで折り返し、継ぎ目の無いループにする。

    出来上がりは `xf` 秒だけ短い。巻き戻る瞬間に鳴っているのは「尻と頭が混ざった区間」なので、
    密度も音量も途切れない（`sound-lint.py` の `jump` が数値で確かめる）。
    """
    st = sk.to_stereo(y)
    n = int(xf * sk.SR)
    if n <= 0 or len(st) < n * 3:
        return st
    head, tail, body = st[:n], st[-n:], st[:-n]
    t = np.linspace(0.0, 1.0, n, endpoint=False)[:, None]
    # 等パワー（線形だと混ざっている最中に音の密度が凹む）。
    body = body.copy()
    body[:n] = head * np.sqrt(t) + tail * np.sqrt(1.0 - t)
    return body


def ingest_cuts(cuts, src_dir: str) -> None:
    """1 本の録音を 1 発ずつに切って焼く（<see cref="CUTS"/>）。"""
    for jp, name, expect, target, step, _why in cuts:
        src = os.path.join(src_dir, jp)
        raw = os.path.join(RAW, f"src_{name}.wav")
        if os.path.exists(src):
            if not decode(src, raw):
                continue
        elif not os.path.exists(raw):
            print(f"  無い: {jp}（{src_dir} にも {RAW} にも）")
            continue
        else:
            print(f"  元 mp3 が無いので復号済みを使う: {name}")

        y, sr = sk.read_wav(raw)
        segs, gain, stream_lufs, rel_min = cut_strokes(y, sr, step, target)
        if segs and rel_min < CUT_BODY + CUT_RELEASE_MARGIN:
            # ⚠ 切り出しに**戻り**が入っている ＝ 密度が倍になる。CUT_BODY を詰めること。
            print(f"  ⚠ {name}: 戻りの山まで最短 {rel_min * 1000:.1f}ms しかない"
                  f"（切り出し {CUT_BODY * 1000:.0f}ms ＋ 余白 {CUT_RELEASE_MARGIN * 1000:.0f}ms）")
        if len(segs) != expect:
            # ⚠ **黙って本数を変えない。** 出力名は `_1..N` なので、本数が変わると
            #    実行時の変種の数（`TypeAudioCue`）と食い違い、無い音を掴もうとする。
            print(f"  ⚠ {name}: {expect} 発のはずが {len(segs)} 発だった。"
                  f"元ファイルか CUT_* の閾値を確かめること")
        for i, seg in enumerate(segs):
            sk.write_wav(os.path.join(OUT, f"{name}_{i + 1}.wav"), seg, peak_db=-3.0)
        if not segs:
            continue
        print(f"  {name}_1..{len(segs)}  倍率 {20 * np.log10(max(gain, 1e-9)):+.1f}dB  "
              f"連なり {stream_lufs:.1f} LUFS（{1 / step:.0f} 発/秒）  "
              f"戻りの山まで最短 {rel_min * 1000:.1f}ms")
        for i, seg in enumerate(segs):
            d = sk.describe(seg)
            print(f"    {name}_{i + 1}  {d['sec'] * 1000:5.1f}ms  {d['lufs']:6.1f} LUFS  "
                  f"tp {d['true_peak_db']:5.1f}dB  鋭さ {d['sharp']:4.2f}  "
                  f"粗さ {d['rough']:4.2f}  内蔵SP {d['speaker_db']:5.1f}dB")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--src", default=DOWNLOADS)
    # ⚠ 1 本だけ焼き直すための口。全部を焼き直すと、**別のシュビーが直したばかりの音**まで
    #    書き戻すことになる（並列で作業しているのが普通のリポジトリなので既定にしない）。
    ap.add_argument("--only", nargs="*", default=None, metavar="名前",
                    help="この出力名だけを焼く（例: --only bed_room_lap2 bed_room_lap3）")
    a = ap.parse_args()

    if a.list:
        for jp, name, how, target, why in PLAN:
            print(f"  {name:16s} ← {jp}\n      {how} {target:+.1f} / {why}")
        for jp, name, n, target, step, why in CUTS:
            print(f"  {name+'_1..'+str(n):16s} ← {jp}\n      切り出し / 連なり {target:+.1f} LUFS"
                  f"（{1/step:.0f} 発/秒）/ {why}")
        for jp, name, target, why in SWARMS:
            print(f"  {name:16s} ← {jp}\n      小刻みに並べる / {target:+.1f} LUFS"
                  f"（{SWARM_SEC:.2f}s）/ {why}")
        return 0

    names = ({p[1] for p in PLAN} | {c[1] for c in CUTS}
             | {s[1] for s in SWARMS} | {c[1] for c in CHORUS})
    if a.only is not None and not set(a.only) <= names:
        missing = sorted(set(a.only) - names)
        print(f"  PLAN にも CUTS にも無い名前: {', '.join(missing)}")
        return 1
    plan = [p for p in PLAN if a.only is None or p[1] in a.only]
    cuts = [c for c in CUTS if a.only is None or c[1] in a.only]
    swarms = [s for s in SWARMS if a.only is None or s[1] in a.only]
    chorus = [c for c in CHORUS if a.only is None or c[1] in a.only]

    os.makedirs(RAW, exist_ok=True)
    os.makedirs(OUT, exist_ok=True)
    ingest_cuts(cuts, a.src)
    ingest_swarms(swarms, a.src)
    ingest_chorus(chorus, a.src)
    for jp, name, how, target, _why in plan:
        src = os.path.join(a.src, jp)
        raw = os.path.join(RAW, f"src_{name}.wav")
        if os.path.exists(src):
            if not decode(src, raw):
                continue
        elif not os.path.exists(raw):
            print(f"  無い: {jp}（{a.src} にも {RAW} にも）")
            continue
        else:
            print(f"  元 mp3 が無いので復号済みを使う: {name}")

        y, sr = sk.read_wav(raw)
        before = sk.describe(y)
        # ⚠ ループにする音は **端を落とさない**（頭と尻の無音がそのまま折り返しの材料になる）。
        #    切ってから折り返すと、素材の「入り」と「終わり」の空気が消えて別の音になる。
        if name.startswith("bed_"):
            y = fold_loop(y)
        else:
            y = sk.to_stereo(trim(y))
            y = sk.env_fade(y, EDGE_FADE, EDGE_FADE)

        # ⚠ **音量を揃える前に掛ける**（削った分だけラウドネスが下がるので、後に掛けると狙いが外れる）。
        if name in TAME:
            fc, db, oct_w = TAME[name]
            y = np.stack([sk.spec_shape(y[:, ch], lambda f: sk.shelf(f, fc, db, oct_w))
                          for ch in (0, 1)], axis=1)

        if how == "peak":
            tp = sk.true_peak_db(y)
            y = y * 10 ** ((target - tp) / 20.0)
        elif how == "lufs!":
            # ⚠⚠ **尖頭を丸めてでも狙いの高さまで持ち上げる**（2026-08-13・`canon/LEDGER.md` 0032）。
            #    素の音量合わせだけでは、**波高の大きい一撃は -3dBTP の天井に当たって上がらない**
            #    （`sfx_screen_on` は本体が 0.2 秒・波高 17.6dB で、-17.0 LUFS のまま
            #     「鳴っていないように聞こえる」とユーザー判定）。
            #    ⚠ これは §4.5「もらった音を良くしようとしない」の例外ではなく**音量合わせの側**。
            #       イコライザも圧縮も掛けず、必要なぶんだけ尖頭を丸めて高さを出す
            #       （合成の音が `make-sounds.norm_lufs` で受けているのと同じ扱い）。
            y = norm_lufs_drive(y, target)
        else:
            y = y * 10 ** ((target - sk.lufs(y)) / 20.0)
            tp = sk.true_peak_db(y)
            if tp > -3.0:
                y = y * 10 ** ((-3.0 - tp) / 20.0)

        sk.write_wav(os.path.join(OUT, f"{name}.wav"), y, peak_db=-3.0)
        d = sk.describe(y)
        print(f"  {name:16s} {before['sec']:5.2f}s → {d['sec']:5.2f}s   "
              f"{before['lufs']:6.1f} → {d['lufs']:6.1f} LUFS   "
              f"tp {before['true_peak_db']:5.1f} → {d['true_peak_db']:5.1f}dB   "
              f"鋭さ {d['sharp']:4.2f} 粗さ {d['rough']:4.2f} 内蔵SP {d['speaker_db']:5.1f}dB")
    print(f"\n→ {OUT}\n次: py -3.11 tools/sound-lint.py  →  .\\tools\\unity.ps1 menu sound-import")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
