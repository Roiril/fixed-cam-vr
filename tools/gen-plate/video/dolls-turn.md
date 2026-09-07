# 4 周目 A の人形が「正面 → 右を向いて手を伸ばす」（首尾フレーム・4 秒）

ユーザーの言葉（2026-09-07）:「無人スタートではなく、正面→右を向いて手を伸ばすにする」

`video/dolls-gather.md`（無人 → 集まってくる）の差し替え。**集まる過程は動画にしない。**
群れは最初からそこに居て、動くのは**首と腕だけ**。

判定の出どころは `canon/LEDGER.md` 0157（終わりは「右へ手を差し伸べる」＝ 仲間になりたい／
動きは不気味に／止まってから腕だけが上がる）と 0160（手のひらを上に向けて水平に出す
「ハイどうぞ」は却下／体験者は画面の右にいる／カメラの方は向かなくてよい）。

## 入力（2 枚とも 2560x1440。3 倍の 1920x1440 を x=320 へ置いた 16:9）

| | 中身 | 素材 |
|---|---|---|
| 1 コマ目 | 正面を向いた群れ。**手はまだ伸ばしていない** | `captures/gen_dollsA_left_20260907_1030_lit.png` |
| 最終コマ | 右を向いて手を伸ばした群れ | `captures/gen_dollsA_left_20260907_reach_lit.png` |

⚠⚠ **2 枚の露出を揃えてから渡す。** 生成に渡した種（`logs/gen-plate/auto_*_despill/out.png`）は
露出を落としてあるので、そのまま 1 コマ目にすると**触っていない右半分が 58.4 対 117.4** ＝
動画が「暗い所から照明が点いていく」動きを勝手に作る。`relight`（触っていない側の平均と
標準偏差をプレートに合わせる線形変換）を通してから 16:9 にする。

## プロンプト（4 秒・ループ OFF）

```
The camera is locked on a tripod and never moves: no pan, tilt, zoom, roll or shake, and the
framing is identical in every frame. Animate from the first frame to the last frame. All of the
Japanese ichimatsu dolls are already in the picture at the start and none are added, removed or
replaced. Their feet never leave the spot they are standing or sitting on: nobody walks, slides
or changes place, and the ones lying on the floor stay lying down. Only heads and arms move.

First the dolls turn their heads to the right, towards someone standing off to the right side of
the picture. They do not all turn at once: each head starts at its own moment and turns in small
hard steps, the way a jointed doll moves, with pauses in between, as if frames were dropped. A
few of them never turn and keep looking down or at the doll beside them. Once the heads have
settled and are still, six to ten of them slowly raise an arm towards that same person on the
right. The arms are stiff and bend at the elbow and shoulder in hard angles, never in smooth
curves, and they come up at different heights and at different moments. The hands are open with
the fingers slightly curled, reaching to grab or beckoning that person closer. No doll holds a
flat palm up as if offering something.

No hand ever crosses the vertical middle of the picture: every arm stops before it. Nothing else
in the room changes at all — the grey curtain, the black pipe, the floor, the shadows and the
light stay exactly as they are in the first frame, and the picture ends exactly as the last frame
shows it, with the arms held out and still.
```

## 卓へ入れるまで

動画は画角を守らずに返ってくる（`memory/codex_image_pipeline.md`）。**そのままは素材にしない。**

```bash
py -3.11 tools/gen-plate/fitvideo.py --video <もらった.mp4> \
    --plate tools/web-compositor/captures/plate_A_20260907_101013.jpg \
    --out tools/web-compositor/captures/gen_dollsA_in_<日付>.mp4
```

cue は `dolls_A_in`（入り・mask `split_left_half`・loop OFF）。
そのあと居続けるのは `gen_dolls_A_left`（＝ 最終コマの静止画）。
**①の最終コマと②の静止画が同じ絵でないと、切り替わりで人形が跳ねる。**
