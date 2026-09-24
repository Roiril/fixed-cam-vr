# 2 周目 A：報告の長押しで幕の染みが動く

2026-09-24 のユーザー指定は `.claude/canon/LEDGER.md` 0249。画像の首尾フレームだけ制作済み。
4 秒の動画生成はユーザーが行う。動画の受領、往復再生の実装、`show.json` への採用は未着手。
現行の 2 周目 A は旧素材のまま。

## 動画生成へ渡す 2 枚

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

## ユーザーが作る 4 秒動画

開始画像を first frame、終了画像を last frame に指定する。
4 秒は静止状態から最大変形までの順再生だけ。戻りは動画を受け取った後、こちらで逆再生を作る。
動画生成側のループや自動の戻りは切る。音は不要。

```text
Fixed tripod shot, exactly four seconds, 4:3. Animate continuously from the provided first
frame to the provided last frame. The existing face-shaped stain stays on the same cloth and
does not change expression. First the free outer left edge of the curtain flutters in two
uneven pulses, then the stained cloth swells gently toward the camera as if pressed from
behind. The cloth deformation reaches exactly the provided last-frame pose at four seconds.
Only the left curtain cloth moves. Its top attachments and the horizontal metal bar remain
fixed. The right curtain, wall, ceiling, shelves, floor, camera framing, exposure, color and
grain remain unchanged. No person, creature, hand, silhouette, new stain, added object,
camera motion, cut, blur, or return to the first pose within this clip.
```

ネガティブ欄は空。動画が 16:9 や別の部屋になった場合は、そのまま採用しない。
以前の動画には画角と顔の位置のずれがあった。受領後に `fitvideo.py` で位置を合わせ、
全コマを走査してマスクを作る。顔だけのマスクでは左端のめくれが欠ける。
2026-09-24 に試した画面左半分マスク `masks/split_left_half.png` は、首尾フレームの確認用。
動画全コマに対する採用マスクは動画を受け取ってから決める。

## 再生制御の接続案

| 事象 | 見せるもの |
|---|---|
| 報告を押す前 | 動かない顔の染み。動画は再生しない |
| 長押し開始 | 4 秒動画を順再生し、終点から逆再生する |
| 1 秒未満で離す | 現在の往復を続け、逆再生で開始姿勢に戻って停止する |
| 停止後に再び押す | 同じ往復を最初から始める |
| 1 秒の長押しが成立する | 報告成功を確定する。進行中の往復だけ開始姿勢まで戻し、その後は異常を再発させない |

報告判定は現行の 1 秒。動画の順再生は 4 秒。この 2 つは別の時計にする。
成功した瞬間に映像を切ると「逆再生で元の位置に戻ってから」に反する。
成功後は新しい往復を始めず、今の往復が帰るのを待つ案とした。
これはユーザーの逐語を超える実装上の読みであり、動画が届いた後の動作確認で調整する。
動画自体は 0→4 秒の 1 本を受け取り、4→0 秒はその逆再生から作る。

## 画像生成の指示

使用したのは組み込み image_gen。入力画像は `gen_stainA_still_20260924.png` の 1 枚だけ。

```text
Use case: precise-object-edit. Asset type: final keyframe for a 4-second image-to-video shot in a fixed-camera horror installation. Image 1 is the edit target and the exact first keyframe: a real 4:3 photograph of a grey curtain with an existing face-shaped damp stain on the left panel. Create the PEAK MOTION frame of this very same shot. The curtain's free outer LEFT edge, near original image x=85–135 from y=90–370, is visibly caught in a short flutter: a modest asymmetric outward flick and two new diagonal tension folds, while its top hooks and the horizontal metal bar remain fixed. At the same instant, the cloth at the existing stained face, original x=170–270 y=55–190, is pushed gently toward the camera as if something presses from behind: a broad shallow convex bulge with realistic stretched vertical folds and a soft side shadow. Keep the face-shaped stain absorbed into the SAME cloth, in the SAME place, moving with the bulge; its existing eyes and crooked mouth remain recognizably the same and do not become a literal face. The deformation should be large enough to read in a dark reduced-resolution surveillance display yet physically plausible; no silhouette, person, hand, or creature visible behind the curtain. Preserve exactly the original camera position, 4:3 crop, curtain color and material, right panel, wall, ceiling, shelves, pipe, floor, light, grain and all objects. Do not redraw or stylize the room. No added text, symbols, glow, fog, new stains or expression change. Keep the original natural exposure without extra cinematic darkening. This is a still photograph of the END pose, not a motion-blurred frame.
```

画像生成は室内を少し描き直した。終了フレームは生成画像の左側だけを取り、
右側と床は開始画像の画素に戻した。640×480 の x≥330 は開始と終了で完全一致。
`screen.py` の 2 周目の後処理を通し、左端のめくれと顔の膨らみを視認した。
