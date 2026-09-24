# 2 周目 A：報告の長押しで幕の染みが動く

2026-09-24 のユーザー指定は `.claude/canon/LEDGER.md` 0249。
2026-09-25 にユーザーの動画を受領し、2 周目 A に採用した。
開始時は静止した染みを表示し、報告の長押し開始で幕を往復させる。

## 採用素材と処理

| 役割 | ファイル | 内容 |
|---|---|---|
| 受領した原本 | [stain-press-source-20260925.mp4](stain-press-source-20260925.mp4) | 640×360、24fps、96 コマ、音声あり |
| 待機姿勢 | [static-inputs/stain_A_press_idle_20260925.png](../../web-compositor/static-inputs/stain_A_press_idle_20260925.png) | 往復動画の先頭コマ |
| 往復映像 | [static-inputs/stain_A_press_pingpong_20260925.mp4](../../web-compositor/static-inputs/stain_A_press_pingpong_20260925.mp4) | 640×480、24fps、191 コマ、7.958 秒、音声なし |
| 合成範囲 | [static-inputs/stain_A_press_leftmask_20260925.png](../../web-compositor/static-inputs/stain_A_press_leftmask_20260925.png) | 640×360 の左半分 |

原本の全 96 コマを確認した。黒帯を除いた写真領域は x=75..564 の 490 px。
最大の動きは 80 コマ目。ここまでの 81 コマを 4 秒へ延ばし、逆順をつないで往復にした。
動画を 4:3 へ戻すときは写真領域を 640×480 に拡大した。
音声は除去した。待機画と動画の先頭が同一なので、開始時の画の飛びを抑えられる。
`show.json` の 2 周目 A は `cue_stain_A` 1 カットの静止画とし、演出の
`markStartCueId` は `stain_A_press_flutter`。両 cue は左半分マスクを使う。
素材は `static-inputs/` に置き、APK への書き出しで両方を含める。

## 動画生成へ渡す 2 枚（16:9）

動画生成側が 16:9 のみ対応するため、こちらを渡す。1920×1080。
中央の 4:3 写真を 1440×1080 に拡大し、左右それぞれ 240 px を完全な黒にした。
写真の切り取りはない。両フレームで余白と配置は同じ。

| 役割 | ファイル | SHA256 |
|---|---|---|
| 開始 | [frames/stain-press-20260924-first-16x9.png](frames/stain-press-20260924-first-16x9.png) | `7ccb962b4c93cce64e3812185026b165aba28f8f36676aacef36b66b722c9b81` |
| 終了 | [frames/stain-press-20260924-last-16x9.png](frames/stain-press-20260924-last-16x9.png) | `2fd78d3648e1be7ec51644f0596f7dcef9a0896deaf44e3cc587fe44af39ff86` |

## 元の 4:3 フレーム

| 役割 | ファイル | SHA256 |
|---|---|---|
| 開始：動かない顔の染み | [frames/stain-press-20260924-first.png](frames/stain-press-20260924-first.png) | `4189e73ff6fd349aaff124409d6acaa54107d4aa2bb2363c79e38d383b54d516` |
| 終了：左端がめくれ、顔の布が膨らむ | [frames/stain-press-20260924-last.png](frames/stain-press-20260924-last.png) | `40e3ff51c378e849f2db7a25de053843af8983fec6b3b4c52ef0ba6f0e4768b5` |

両方 1280×960、4:3、同じカメラ位置。後処理の暗さは入力へ焼いていない。
開始画像は `captures/gen_stainA_still_20260924.png` の 2 倍版。
終了画像は組み込み image_gen でこの写真を編集し、640×480 に縮小した後、
動かない右側と床を開始写真へ戻してから 2 倍にした。
元の写真と画像生成の記録は [stain-A-20260924.md](../stain-A-20260924.md)。
生成の生画像と比較画像は `logs/gen-plate/stain_press_20260924/`。

## 動画生成に使った指示

上記の 16:9 開始画像を first frame、終了画像を last frame に指定する。
4 秒は静止状態から最大変形までの順再生だけ。戻りは動画を受け取った後、こちらで逆再生を作る。
動画生成側のループや自動の戻りは切る。音は不要。

```text
Fixed tripod shot, exactly four seconds, 16:9 frame. The central 4:3 photograph is bordered
by solid black vertical bars on the left and right; keep those bars completely black,
stationary, and the same width in every frame. Animate continuously from the provided first
frame to the provided last frame. The existing face-shaped stain stays on the same cloth and
does not change expression. First the free outer left edge of the curtain flutters in two
uneven pulses, then the stained cloth swells gently toward the camera as if pressed from
behind. The cloth deformation reaches exactly the provided last-frame pose at four seconds.
Only the left curtain cloth moves. Its top attachments and the horizontal metal bar remain
fixed. The right curtain, wall, ceiling, shelves, floor, camera framing, exposure, color and
grain remain unchanged. No person, creature, hand, silhouette, new stain, added object,
camera motion, cut, blur, or return to the first pose within this clip.
```

ネガティブ欄は空。動画で黒い余白が動いたり、写真が拡大・切り取られたり、別の部屋になった場合はそのまま採用しない。
顔だけのマスクでは左端のめくれが欠けるため、左半分マスクを採用した。

## 再生制御

| 事象 | 見せるもの |
|---|---|
| 報告を押す前 | 動かない顔の染み。動画は再生しない |
| 長押し開始 | 4 秒動画を順再生し、終点から逆再生する |
| 1 秒未満で離す | 現在の往復を続け、逆再生で開始姿勢に戻って停止する |
| 停止後に再び押す | 同じ往復を最初から始める |
| 1 秒の長押しが成立する | 報告成功を確定する。進行中の往復だけ開始姿勢まで戻し、その後は異常を再発させない |

報告判定は現行の 1 秒。動画の順再生は 4 秒。この 2 つは別の時計にする。
成功した瞬間に映像を切ると「逆再生で元の位置に戻ってから」に反する。
成功後は新しい往復を始めず、今の往復が帰るまで画面を保持する。
報告件数と成功の返事は 1 秒で確定し、画面の解除だけ往復動画の終端まで待つ。
途中で離した場合も映像は止めず、終端で静止画へ戻す。
切り替え時は動画の先頭姿勢を保持する。静止画の読み込み待ちに実写が一瞬見えるのを防ぐ。
区間を出た場合も再生中の往復は完了させる。
実機の描画と入力を通した確認は APK 更新後に行う。

## 画像生成の指示

使用したのは組み込み image_gen。入力画像は `gen_stainA_still_20260924.png` の 1 枚だけ。

```text
Use case: precise-object-edit. Asset type: final keyframe for a 4-second image-to-video shot in a fixed-camera horror installation. Image 1 is the edit target and the exact first keyframe: a real 4:3 photograph of a grey curtain with an existing face-shaped damp stain on the left panel. Create the PEAK MOTION frame of this very same shot. The curtain's free outer LEFT edge, near original image x=85–135 from y=90–370, is visibly caught in a short flutter: a modest asymmetric outward flick and two new diagonal tension folds, while its top hooks and the horizontal metal bar remain fixed. At the same instant, the cloth at the existing stained face, original x=170–270 y=55–190, is pushed gently toward the camera as if something presses from behind: a broad shallow convex bulge with realistic stretched vertical folds and a soft side shadow. Keep the face-shaped stain absorbed into the SAME cloth, in the SAME place, moving with the bulge; its existing eyes and crooked mouth remain recognizably the same and do not become a literal face. The deformation should be large enough to read in a dark reduced-resolution surveillance display yet physically plausible; no silhouette, person, hand, or creature visible behind the curtain. Preserve exactly the original camera position, 4:3 crop, curtain color and material, right panel, wall, ceiling, shelves, pipe, floor, light, grain and all objects. Do not redraw or stylize the room. No added text, symbols, glow, fog, new stains or expression change. Keep the original natural exposure without extra cinematic darkening. This is a still photograph of the END pose, not a motion-blurred frame.
```

画像生成は室内を少し描き直した。終了フレームは生成画像の左側だけを取り、
右側と床は開始画像の画素に戻した。640×480 の x≥330 は開始と終了で完全一致。
`screen.py` の 2 周目の後処理を通し、左端のめくれと顔の膨らみを視認した。
