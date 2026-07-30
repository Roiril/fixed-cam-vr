# 素材インデックス（自動生成 — 手で編集しない）

更新 2026-07-30 14:54 / rev 17 / 生成 2 件 / レシピ 10 件

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

- `/captures/gen_handB_01.png` — cue_hand_B
- `/captures/gen_monsterA_01.png` — cue_ningyo_A

