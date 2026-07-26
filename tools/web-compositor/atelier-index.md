# 素材インデックス（自動生成 — 手で編集しない）

更新 2026-07-26 09:25 / rev 13 / 生成 1 件 / レシピ 3 件

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


## カメラ C（1 件 / 採用 0）

### ― 未評価  📋 送信待ち `g_20260726_064710_118`
- 出力: **まだ無い**（生成して取り込む）
- 入力フレーム: `/recordings/camC_20260618_212125_578.jpg`
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

