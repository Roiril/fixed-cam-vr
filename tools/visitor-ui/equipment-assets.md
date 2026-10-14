# 博士タブレットの機材画像

2026-10-14。ユーザーが指定した4枚の写真を参照し、built-in image_genで生成した。画像の構成と色は画像生成で作る。手動の輪郭抽出や色置換は行っていない。

- 観測装置: `briefing-device-v2.png`。Questと輪のない左コントローラー。手と室内背景を除く。黒い上部ベルトとX/Yの配置を写真に合わせる。初稿の茶色い背景は画像生成で透過へ変更した。RGBA 1672×941。ベルトの内側も透明。観測装置の場面だけ額と影を外し、`contain`で全体を表示する。幅42vwで博士の左へ置く。明るさ0.8と彩度0.65で周囲に合わせる。画面共通の粒子を重ねる。切り抜きを滑らかに表示し、章移動では前の絵を溶かす。
- 装着案内: `equipment-controller-v1.png` / `equipment-quest-v1.png` / `equipment-headphones-v1.png`。白い輪郭線と赤い印だけ。生成原画は黒地のRGB PNG。装着画面の `.content` に `mix-blend-mode: screen` を適用し、黒を重ねず線だけを表示する。各項目の出現アニメーションにはfilterがあるため、画像だけへ重ね方を指定すると黒い四角が残る。出現効果の外側で適用する。
- 元写真: `C:/Users/kouga/Downloads/Photos-1-001/` の `PXL_20261014_111127307.jpg` / `PXL_20261014_111150793.jpg` / `PXL_20261014_111203608.jpg` / `PXL_20261014_111222504.jpg`。
- 全試行と実機検証の保存先: `G:/マイドライブ/shubie/renders/tablet-equipment-20261014/`。`prompts.json` は不採用の透明背景の試行も保持する。

透明背景の初稿と修正版は白い斑点が残ったため不採用。背景を純黒へ変えた新規生成で、斑点のない輪郭線を得た。下記は採用素材のプロンプト。

## observation-original

参照: `PXL_20261014_111127307.jpg` / `PXL_20261014_111150793.jpg`

背景透過: false。以下の初稿を生成した後、次の編集で背景を除いた。

```text
画風を最優先。古い怪異研究所の観測機器を紹介する、見やすいレトロな機材図版。博物館の古い技術資料の印刷図のような、繊細なインクの輪郭線と控えめな版画の線による陰影。現代の写真広告の艶、金属の反射、派手なSFの光を避ける。入力1は左コントローラーの形状とボタン配置の正本。入力2はQuestヘッドセットと装着ベルトの形状の正本。これらは被写体の参照画像であり、背景や持つ手を残す編集対象ではない。出力は横長16:9の1枚。写真に写った白いQuest 3と白い左Touchコントローラーの2点だけを、手や人なしで描き直す。左にQuestを大きく斜め正面から、右に左コントローラーを少し起こして上面のボタンが読める向きで配置。全体の輪郭が画面に入り、機材が重ならず、周囲に十分な余白。Questの前面は丸みのある白い面と縦長の黒いセンサー窓3つ。側面の白い部品と、入力2の黒い頭頂ベルトと後ろの黒いクッションを持つ。勝手に白い布ベルトへ置き換えない。コントローラーは輪のない白い曲がったグリップと黒い丸い上面。上面の左上にスティック、右上にY、その下にX、左下に小さいメニューボタン。XとYだけは正確に読める文字を残す。それ以外の文字やロゴは描かない。物の外形と実物の構造は忠実に保つ。旧式のゴーグルや架空の歯車に変えない。色は象牙色の機材、墨に近い茶黒の輪郭、少量のくすんだ赤。背景は隅から隅まで落ち着いた暗い茶色の古い図版の地。薄い紙の粒だけ。明るい機材との明度差を十分つけ、縮小しても2つの機材を認識できるようにする。手、指、腕、人体、机、椅子、棚、研究室の背景、ヘッドフォン、追加コントローラー、注釈、矢印、枠、題字は一切描かない。
```

## observation-cutout-final

編集対象: `briefing-device-v2-framed-before.png`。上記の初稿。背景透過: true。

```text
Use case: background-extraction. Asset type: transparent apparatus illustration for an existing dark tablet presentation. Edit the provided illustration ONLY to remove the brown rectangular background. Keep the two devices exactly as they are: the ivory Quest 3 on the left with its three vertical front sensor windows, black upper strap and rear padding; the ringless left controller on the right with the joystick, menu button and clearly correct X/Y buttons. Keep the same poses, exact silhouettes, proportions, spacing, warm ivory / charcoal colors and fine engraved ink hatch lines. Preserve dark device material as opaque pixels. Remove every brown background pixel both around the devices and inside the empty open space enclosed by the head strap. Those empty spaces must be actual transparent alpha, not a checkerboard drawn into the image. Do not redesign or redraw the hardware. No shadow, ground, tabletop, vignette, frame, paper sheet, halo, extra object, labels or added linework. The output is only these two unchanged objects on a fully transparent background. Keep the whole original wide composition and margins.
```

## controller-icon-final

参照: `PXL_20261014_111127307.jpg`

背景透過: false

```text
Create a clean SIMPLE LINE ICON, like a UI outline pictogram, as a square PNG. Solid pure black background #000000 from corner to corner, and pure black throughout EVERY enclosed interior. Draw only crisp uniform white #FFFFFF contour STROKES, about 10px wide on a 1024 canvas, plus one small muted crimson #A83239 stroke accent. Do not fill the object with white. Do not fill bands between outlines. Use very few smooth curves and clear empty black spaces. No transparency, checkerboard, gray, shading, highlights, texture, scratches, grain, stippling, speckles or photographic detail. This icon style is flat and clean, NOT retro or distressed. Photo is subject shape reference only. Single centered object, no hands, no room, no frame, no labels. 12% margin on all sides. Subject: the ringless left Quest controller in the photo. Slight three-quarter front view, oval top face and curved grip. The top face has the joystick circle upper left; the Y button upper right; the X button below Y; menu circle lower left. White X and Y glyphs inside their circles. Use crimson contour strokes for the X and Y circles ONLY, white contours elsewhere. Grip interior and entire face interior solid pure black. No wrist strap, no tracking ring. Simplify to a legible single contour for the grip, one for the face and the 4 controls.
```

## quest-icon-final

参照: `PXL_20261014_111150793.jpg`

背景透過: false

```text
Create a clean SIMPLE LINE ICON, like a UI outline pictogram, as a square PNG. Solid pure black background #000000 from corner to corner, and pure black throughout EVERY enclosed interior. Draw only crisp uniform white #FFFFFF contour STROKES, about 10px wide on a 1024 canvas, plus one small muted crimson #A83239 stroke accent. Do not fill the object with white. Do not fill bands between outlines. Use very few smooth curves and clear empty black spaces. No transparency, checkerboard, gray, shading, highlights, texture, scratches, grain, stippling, speckles or photographic detail. This icon style is flat and clean, NOT retro or distressed. Photo is subject shape reference only. Single centered object, no hands, no room, no frame, no labels. 12% margin on all sides. Subject: the actual Quest 3 headset in the photo. Simplify strongly into a shallow three-quarter front outline. A broad rounded front shell, exactly three vertical capsule sensor outlines, short side attachments, and two simple arch outlines for the head strap. One small central segment at the crown of the strap is crimson. Everything else white lines. Shell interior, sensor interiors, strap interiors and padding interiors all solid pure black. No seams or tiny fittings. No brand, person, face or hands. This is a simple symbolic outline, not a shaded technical rendering.
```

## headphones-icon-final

参照: `PXL_20261014_111203608.jpg` / `PXL_20261014_111222504.jpg`

背景透過: false

```text
Create a clean SIMPLE LINE ICON, like a UI outline pictogram, as a square PNG. Solid pure black background #000000 from corner to corner, and pure black throughout EVERY enclosed interior. Draw only crisp uniform white #FFFFFF contour STROKES, about 10px wide on a 1024 canvas, plus one small muted crimson #A83239 stroke accent. Do not fill the object with white. Do not fill bands between outlines. Use very few smooth curves and clear empty black spaces. No transparency, checkerboard, gray, shading, highlights, texture, scratches, grain, stippling, speckles or photographic detail. This icon style is flat and clean, NOT retro or distressed. Photo is subject shape reference only. Single centered object, no hands, no room, no frame, no labels. 12% margin on all sides. Subject: the actual over-ear headphones in the two photos. Symmetric front-facing icon. Use just two parallel smooth arcs for the headband, two short connectors and two double-oval earcup outlines. Keep black space between the parallel arcs and between the double ovals. Crimson color only for a short center segment of the upper arc. All interiors solid pure black. No logo, hinges, surface marks or seams. This should look like a clean minimal headphone outline symbol, not a detailed drawing.
```
