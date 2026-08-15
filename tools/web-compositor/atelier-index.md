# 素材インデックス（自動生成 — 手で編集しない）

更新 2026-08-14 16:48 / rev 37 / 生成 10 件 / レシピ 10 件

生成の正は `atelier.json`。このファイルはそれを人が読める形に落としたもの。

## レシピ（場所に依存しない演出テンプレ）

### 人影が立っている `r_20260726_064706_799` — 未使用
狙い: マスク合成の本命。動きが少ないほど継ぎ目が安定するので、最初の 1 本はこれから試す。
スロット: 位置 / 見た目 / 動き

```
Static locked-off tripod security camera. The camera does not move, pan, zoom, or shake at all.
A figure is standing motionless at {{位置}}, {{見た目}}. {{動き}} It never approaches the camera and stays in place for the whole clip.
The room, walls, partition panels, ceiling, floor, furniture, lighting and framing must stay EXACTLY as in the input image. Do not change color grading or exposure. The figure casts a soft contact shadow on the floor.
Photorealistic, matches the input photo's lighting and lens (wide-angle, high mounted, looking down). Practical-effects horror, no glow, no supernatural aura.
Negative: camera movement, zoom, pan, parallax, relighting, style change, text, watermark, anime, cartoon, extra limbs, deformed hands.
```

### 横切る（一瞬よぎる） `r_20260726_064707_124` — 未使用
狙い: 絵コンテ A1 / A3 の「隅で何かが一瞬よぎる」。1 秒未満で通過させ、残りは完全な無人に保つ。
スロット: 入る側 / 出る側 / 遮蔽物

```
Static locked-off tripod security camera. The camera does not move, pan, zoom, or shake at all.
A dark human silhouette crosses the frame quickly, entering from {{入る側}} and exiting at {{出る側}}, partially occluded by {{遮蔽物}}, visible for less than one second. Motion blur consistent with a phone camera at 30fps. The rest of the clip is completely empty and static.
The room, walls, partition panels, ceiling, floor, furniture, lighting and framing must stay EXACTLY as in the input image. Do not change color grading or exposure.
Photorealistic, matches the input photo's lighting and lens (wide-angle, high mounted, looking down).
Negative: camera movement, zoom, pan, parallax, relighting, style change, text, watermark, anime, cartoon.
```

### 無人のまま異変（人を出さない） `r_20260726_064707_451` — 0/1 採用
狙い: 人物ブロックの切り分けにも使える保険。これが通ればパイプライン自体は生きていると分かる。
スロット: 動くもの / 起きること

```
Static locked-off tripod security camera. The camera does not move, pan, zoom, or shake at all.
The room stays completely empty of people. {{動くもの}} {{起きること}} Nothing else in the room moves.
The room, walls, partition panels, ceiling, floor, furniture, lighting and framing must stay EXACTLY as in the input image. Do not change color grading or exposure.
Photorealistic, matches the input photo's lighting and lens (wide-angle, high mounted, looking down).
Negative: camera movement, zoom, pan, parallax, relighting, style change, text, watermark, anime, cartoon, people, figures, humans.
```

### ① 静止画インペイント（貞子を描き足す / Flux Fill・Kontext） `r_20260730_145450_996` — 未使用
狙い: 旧「生成プロンプト」から移行

```
Add a Japanese yūrei (vengeful ghost) woman standing in the far corner of THIS room, long stringy wet black hair completely covering her face, white burial kimono (shini-shōzoku), pale greyish skin, one bloodshot eye barely visible through the hair, unnaturally hunched, with a soft contact shadow on the floor beneath her feet.
Photorealistic, practical-effects horror, hyper-detailed skin and fabric texture, dim low light matching the scene, shot on a phone camera, subtle film grain.
Keep the room, walls, floor, furniture, lighting and camera framing exactly the same. Change nothing except adding the figure.
Negative: glow, ethereal, ukiyo-e, anime, cartoon, well-lit, extra limbs, deformed face.
```

### ② 画像→動画（静止カメラで貞子を動かす / Runway・Kling・Wan） `r_20260730_145450_996_1` — 未使用
狙い: 旧「生成プロンプト」から移行

```
Static shot, locked-off tripod camera, no camera movement, the background stays perfectly still the whole time.
The yūrei woman slowly tilts her head, her long black hair drifting, then takes one jerky step toward the camera. Undercranked, unnatural stuttering motion like footage from an old tape. Her white kimono sways slightly.
Everything else in the room — walls, floor, furniture, lighting — stays completely static and unchanged.
Duration ~4 seconds, minimal motion. Photorealistic, dim low light, phone-camera look, film grain.
Negative: camera movement, pan, zoom, dolly, glow, ethereal, anime, cartoon, warping background, extra limbs.
```

### 貞子 / 右パネル右脇に出現（背景不変） `r_20260730_145450_996_2` — 未使用
狙い: 旧「生成プロンプト」から移行

```
Add one motionless ghostly woman standing on the right side, just past the right edge of the gray partition screen, against the white wall. She wears a long, dirty white burial gown; her face is completely hidden behind long, wet, stringy black hair hanging straight down; pale grey skin, one bare foot showing, body slightly hunched, head tilted at an unnatural angle, silently facing the camera. Keep absolutely everything else unchanged - the same room, partition, desks, chair, carpet, wall outlet, tripod and the existing cool fluorescent lighting must stay exactly as in the original photo. Match the wide-angle surveillance-camera look: same perspective, slight grain, soft focus and white balance, and cast a faint soft shadow at her feet so she looks naturally captured by the same fixed camera. Photorealistic, eerie, still, bloodless. Negative: no blood, no gore, no extra people, do not alter the background, no text or watermark.
```

### 貞子（検閲ゆるめ版・民俗劇フレーミング） `r_20260730_145450_996_3` — 未使用
狙い: 旧「生成プロンプト」から移行

```
Editorial documentary photo: a butoh / folk-theatre performer in a white mourning robe stands quietly to the right of the gray partition, against the wall, her long black hair hanging over her face, head slightly tilted toward the camera, one bare foot showing. Keep the rest of the office scene, furniture, lighting and grain exactly unchanged. Photorealistic, same wide-angle camera look, subtle shadow at her feet.
```

### 貞子 / 右端の棚脇に出現（背景不変） `r_20260730_145450_996_4` — 未使用
狙い: 旧「生成プロンプト」から移行

```
Add one motionless ghostly woman standing at the far-right edge of the frame, partly emerging from the shadows beside the shelving and wooden ladder on the right. She wears a long, dirty white burial gown; her face is completely hidden behind long, wet, stringy black hair hanging straight down; pale grey skin, body slightly hunched, head tilted at an unnatural angle, silently facing the camera, only partly inside the frame at the right edge. Keep absolutely everything else unchanged - the two gray partition panels, the whiteboard on the left, the shelves, the black round objects, the wooden ladder and equipment, and the existing lighting must stay exactly as in the original photo. Match the wide-angle surveillance-camera look: same perspective, slight grain, soft focus and white balance, with a faint soft shadow at her feet so she looks naturally captured by the same fixed camera. Photorealistic, eerie, still, bloodless. Negative: no blood, no gore, no extra people, do not alter the background, no text or watermark.
```

### 貞子 / 右端から半身→一歩（image-to-video・直球） `r_20260730_145450_996_5` — 未使用
狙い: 旧「生成プロンプト」から移行

```
Image-to-video. Fixed locked-off security camera, absolutely no camera movement. The pale woman in the white robe at the far-right edge holds perfectly still for two seconds, then makes one sudden unnatural motion - her head tilts sharply and her long black hair sways - and she slowly leans out from beside the shelving and takes one quiet, halting step toward the camera, always staring at it. Everything else in the room stays completely static (partition, whiteboard, shelves, ladder unchanged). Slow, eerie, silent, dreamlike, uncanny stillness broken by one twitch. Subtle film grain, faint fluorescent flicker. Photorealistic, bloodless, no running, no quick motion.
```

### 貞子 / 検閲ゆるめ版（民俗舞踏フレーミング・Veo向け） `r_20260730_145450_996_6` — 未使用
狙い: 旧「生成プロンプト」から移行

```
Image-to-video, static tripod shot. A butoh dance performer in a white robe stands at the right edge of the room for a quiet folk-theatre piece. She remains motionless, then slowly tilts her head and takes one slow, deliberate step forward, gazing calmly toward the camera. The rest of the room is perfectly still. Gentle, dreamlike, slow and theatrical, soft grain, photorealistic.
```


## カメラ A（3 件 / 本番で使用 1）

### ✅ 使用: cue_monster_A  🎬 取り込み済み `g_20260807_102136_815`
- 出力: `/captures/gen_camA_doll_20260807_102405.png`
- 入力フレーム: `/captures/seed_A_20260807_101115.jpg`
- レシピ: (レシピなし)
- 生成条件: codex

```
カーテンの左端から、日本の市松人形が出てきて立っている。体の 3 分の 2 以上がカーテンの外に出ていて、隠れているのは左の肩と腕の側だけ。顔と体はやや右を向いていて、画面の右の方をじっとにらんでいる。首がわずかに傾いている。顔は磁器のように白く血の気がなく、瞳は黒く濁って光を返さない。表情はまったく無い。おかっぱの黒髪は艶がなく毛先が乱れている。着物は色あせて黒ずんだ臙脂色。身長は 140cm ほどで、画面の中でしっかり大きく見える。足は床に着いていて、足元に濃くはっきりした影が落ちている。周囲よりやや暗いが、顔の造作と着物の柄が読み取れる明るさは保つ。暗くしすぎない。画面の左 3 分の 1 の範囲に収める（右半分には何も足さない）。血や傷は描かない。発光・オーラ・霧などの効果は付けない。監視カメラにたまたま写り込んでしまったように、ポーズを取らせない。子供らしい可愛らしさや健康的な肌色にはしない。人間ではなく人形に見えること。
```

### ― 未使用  🎬 取り込み済み `g_20260807_101512_403`
- 出力: `/captures/gen_camA_doll_20260807_101645.png`
- 入力フレーム: `/captures/seed_A_20260807_101115.jpg`
- レシピ: (レシピなし)
- 生成条件: codex

```
カーテンの合わせ目の暗がりから、日本の市松人形が体の半分だけこちらへ出てきている。残り半分はカーテンの陰に沈んでいて見えない。顔は磁器のように白く血の気がなく、瞳は黒く濁っていて光を返さない。表情はまったく無い。首がわずかに傾いている。おかっぱの黒髪は艶がなく毛先が乱れている。着物は色あせて黒ずんだ臙脂色で、柄はほとんど読み取れない。蛍光灯の光は人形に直接当たらず、体の大半が影に沈んでいる。周囲より明らかに暗くする。身長は 130cm ほど。足は床に着いていて、足元に濃い影が落ちている。画面の左 3 分の 1 の範囲に収める（右半分には何も足さない）。血や傷は描かない。発光・オーラ・霧などの効果は付けない。監視カメラにたまたま写り込んでしまったように、ポーズを取らせない。子供らしい可愛らしさや健康的な肌色にはしない。人間ではなく人形に見えること。
```

### ― 未使用  🎬 取り込み済み `g_20260807_101115_204`
- 出力: `/captures/gen_camA_doll_20260807_101302.png`
- 入力フレーム: `/captures/seed_A_20260807_101115.jpg`
- レシピ: (レシピなし)
- 生成条件: codex

```
カーテンの合わせ目のすぐ左、床の上に、赤い着物を着た等身大の日本人形の少女が 1 体だけ立っている。おかっぱの黒い髪、青白い肌、無表情でまっすぐカメラを見ている。両腕は体の脇に下ろす。身長は 130cm ほどで、足は床に着いていて足元に淡い接地影がある。画面の左 3 分の 1 の範囲に収める（右半分には何も足さない）。血や傷は入れない。光り方や霊的なエフェクトは付けない。
```


## カメラ B（5 件 / 本番で使用 2）

### ✅ 使用: cue_hands_B  🎬 取り込み済み `g_20260807_105106_290`
- 出力: `/captures/gen_camB_hands_20260807_105326.png`
- 入力フレーム: `/captures/seed_B_20260807_103422.jpg`
- レシピ: (レシピなし)
- 生成条件: codex

```
画面の左側の壁一面に、手形がいくつも押されている。手前の木の合板の壁と、その奥に続く白い壁の両方にまたがって広がっている。**色は乾いて黒ずんだ暗褐色で、ほとんど黒に近い。木の壁よりもはっきり暗くする。白黒にしても濃淡が明確に残る濃さにすること（色味ではなく暗さで見せる）。****手形の向きを徹底的にばらす。指が上を向いた手形は全体の半分以下にすること。**残りは、横倒し（指が左を向くもの・右を向くもの）、斜め 45 度に傾いたもの、上下が完全に逆さで指が下を向いているものを必ず混ぜる。壁を伝って歩いた跡、ずり落ちながらすがった跡、床に近い所で這った跡が入り混じっているように見せる。大きさは大人の手と子供の手が混ざり、指の開き方もそれぞれ違う。押した強さも違い、べったり濃いもの、掠れて指の跡だけのもの、手のひらの半分だけのものがある。密集して重なっている場所と、ぽつんと 1 つだけ離れている場所がある。等間隔・格子状に並べない。何本か下へ垂れた黒い筋がある。床・天井・右のカーテン・奥の棚には何も足さない。壁そのもの・照明・カメラの構図は入力画像のまま変えない。監視カメラの映像にたまたま写り込んだように、写真として自然に。発光・オーラは付けない。
```

### ― 未使用  ⚠ 失敗 `g_20260807_104245_085`
- 出力: **まだ無い**（生成して取り込む）
- 入力フレーム: `/captures/seed_B_20260807_103422.jpg`
- レシピ: (レシピなし)
- 生成条件: codex
- メモ: 300 秒を超えました

```
画面の左側の壁一面に、手形がいくつも押されている。手前の木の合板の壁と、その奥に続く白い壁の両方にまたがって広がっている。**手形の向きを徹底的にばらす。指が上を向いた手形は全体の半分以下にすること。**残りは、横倒し（指が左を向くもの・右を向くもの）、斜め 45 度に傾いたもの、上下が完全に逆さで指が下を向いているもの、を必ず混ぜる。壁を伝って歩いた跡、ずり落ちながらすがった跡、床に近い所で這った跡、天井近くで手をついた跡が入り混じっているように見せる。大きさは大人の手と子供の手が混ざり、指の開き方もそれぞれ違う。押した強さも違う。濃くはっきり付いたもの、掠れて指の跡だけ残ったもの、手のひらの半分だけのものがある。密集して何枚も重なっている場所と、ぽつんと 1 つだけ離れている場所がある。等間隔・格子状に並べない。色は乾いて黒ずんだ赤褐色。何本か下へ垂れた筋がある。床・天井・右のカーテン・奥の棚には何も足さない。壁そのもの・照明・カメラの構図は入力画像のまま変えない。監視カメラの映像にたまたま写り込んだように、写真として自然に。発光・オーラは付けない。スタンプを繰り返し押したような均一な模様にしない。
```

### ― 未使用  🎬 取り込み済み `g_20260807_103944_180`
- 出力: `/captures/gen_camB_hands_20260807_104134.png`
- 入力フレーム: `/captures/seed_B_20260807_103422.jpg`
- レシピ: (レシピなし)
- 生成条件: codex

```
画面の左側の壁一面に、手形がいくつも押されている。手前の木の合板の壁と、その奥に続く白い壁の両方にまたがって広がっている。向きはひとつずつ違う。上下が逆さのもの、斜めに傾いたもの、横倒しのものが混ざる。大きさも大人の手と子供の手が混ざり、指の開き方もそれぞれ違う。押した強さも違う。濃くはっきり付いたもの、掠れて指の跡だけ残ったもの、手のひらの半分だけのものがある。密集して何枚も重なっている場所と、ぽつんと 1 つだけ離れている場所がある。**等間隔に並べない。格子状に並べない。**高さもばらばらで、床に近い低い位置に多く、高い位置にもまばらに点在する。色は乾いて黒ずんだ赤褐色。何本か下へ垂れた筋がある。床・天井・右のカーテン・奥の棚には何も足さない。壁そのもの・照明・カメラの構図は入力画像のまま変えない。監視カメラの映像にたまたま写り込んだように、写真として自然に。発光・オーラは付けない。イラスト調・판박이のような繰り返し模様にしない。
```

### ✅ 使用: cue_monster_B  🎬 取り込み済み `g_20260807_103741_716`
- 出力: `/captures/gen_camB_doll_20260807_103916.png`
- 入力フレーム: `/captures/seed_B_20260807_103422.jpg`
- レシピ: (レシピなし)
- 生成条件: codex

```
木の合板の壁の前、画面の左寄りの床に、日本の市松人形が 1 体だけ立っている。足の裏まで画面に入る位置に立たせる（足元を切らない）。顔と体はやや右を向いていて、画面の右の方をじっとにらんでいる。首がわずかに傾いている。顔は磁器のように白く血の気がなく、瞳は黒く濁って光を返さない。表情はまったく無い。おかっぱの黒髪は艶がなく毛先が乱れている。着物は色あせて黒ずんだ臙脂色。身長は 140cm ほどで、画面の中でしっかり大きく見える。足元に濃くはっきりした影が落ちている。画面の左 4 割の範囲に収める（右のカーテンの側には何も足さない）。周囲よりやや暗いが、顔の造作と着物の柄が読み取れる明るさは保つ。血や傷は描かない。発光・オーラ・霧などの効果は付けない。監視カメラにたまたま写り込んでしまったように、ポーズを取らせない。子供らしい可愛らしさや健康的な肌色にはしない。人間ではなく人形に見えること。
```

### ― 未使用  🎬 取り込み済み `g_20260807_103506_534`
- 出力: `/captures/gen_camB_hands_20260807_103656.png`
- 入力フレーム: `/captures/seed_B_20260807_103422.jpg`
- レシピ: (レシピなし)
- 生成条件: codex

```
画面の左にある木の合板の壁の面いっぱいに、手形がいくつも押されている。大人の手と子供の手が混ざり、向きはばらばらで、上下に重なり合っている。色は乾いて黒ずんだ赤褐色で、ところどころ下へ垂れた筋がある。手形は木の合板の面の中だけに収める。右のカーテン、奥の棚、床、天井には何も足さない。壁そのもの・照明・カメラの構図は入力画像のまま変えない。監視カメラの映像にたまたま写り込んだように、写真として自然に。発光・オーラ・霧などの効果は付けない。イラスト調にしない。
```


## カメラ C（2 件 / 本番で使用 0）

### ― 未使用  🎬 取り込み済み `g_20260729_191019_008`
- 出力: `/captures/gen_camC_doll_20260729_191238.png`
- 入力フレーム: `/captures/camcamC_20260729_185120_421.jpg`
- レシピ: (レシピなし)
- 生成条件: codex

```
床の上に、小さな日本人形が 1 体だけ置かれてこちらを向いている。高さ 30cm ほど。血は入れない
```

### ― 未使用  📋 送信待ち `g_20260726_064710_118`
- 出力: **まだ無い**（生成して取り込む）
- 入力フレーム: `/archive/2026-06-18_old-env/camC_20260618_212125_578.jpg`
- レシピ: 無人のまま異変（人を出さない）（動くもの=パーテーションの1枚が数センチだけひとりでに動く / 起きること=蛍光灯が一度だけ弱くまたたく）
- 生成条件: dreamina / seedance 4.0 pro / 4:3 / 4s
- メモ: （動作確認で作った下書き。不要なら 🗑 で消してください）

```
Static locked-off tripod security camera. The camera does not move, pan, zoom, or shake at all.
The room stays completely empty of people. パーテーションの1枚が数センチだけひとりでに動く 蛍光灯が一度だけ弱くまたたく Nothing else in the room moves.
The room, walls, partition panels, ceiling, floor, furniture, lighting and framing must stay EXACTLY as in the input image. Do not change color grading or exposure.
Photorealistic, matches the input photo's lighting and lens (wide-angle, high mounted, looking down).
Negative: camera movement, zoom, pan, parallax, relighting, style change, text, watermark, anime, cartoon, people, figures, humans.
```


## ⚠ 台帳に無いまま本番で使われている素材

（外部ツールで作って直接 cue にしたもの。工房の棚から「プロンプトを書き足す」で記録できる）

- `/captures/gen2_monster_C.png` — cue_monster_C
- `/captures/gen_handB_01.png` — cue_hand_B
- `/captures/gen_monsterA_01.png` — cue_ningyo_A
- `/captures/plate_A_20260807_102533.jpg` — plate_A
- `/captures/plate_B_20260807_104843.jpg` — plate_B
- `/captures/plate_C_20260805_174526.jpg` — plate_C

