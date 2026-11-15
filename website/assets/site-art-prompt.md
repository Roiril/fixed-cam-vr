# 幕・壁・監視カメラ・髪・染みの生成記録 — 2026-11-15

第 3 版「幕と壁」の地と、章の境界をまたぐ物。5 枚とも Codex の画像生成で作った。
写真や実物の複製ではない。人形・人物・文字・実在のブランドは含まない。

- 原本（PNG 5 枚）とプロンプト: `G:\マイドライブ\shubie\renders\mawarimi-site-art\2026-11-15\`
- 配信用への変換: `py -3.11 scripts/prepare-site-art.py <原本のフォルダ>`
- どれも 1 回の生成で採用した（描き直しなし）

| 配信用 | 原本 | 変換 |
|---|---|---|
| `drape-rail.webp` / `drape-folds.webp` / `drape-hem.webp` | `curtain.png`（1536×1024） | 白地を抜き、レール（上 240px）・ひだ（450px）・裾（190px）に切る。橙が強いので彩度を 0.62 倍にして `room-empty.webp` の明部に寄せた |
| `wall.webp` | `wall.png`（1536×1024） | 上下を鏡で継いで 1536×2048。縦に繰り返せる |
| `cctv.webp` | `camera.png`（1254×1254） | 白地を抜き、影を焼き込む。地に残った塵を落とす |
| `hair.webp` | `hair.png`（1024×1536） | 濃さをそのまま不透明度にした墨 1 色 |
| `stain.webp` | `stain.png`（1536×1024） | 乗算で重ねるので、地を完全な白に揃える（透過ではない） |

幕だけ `room-empty.webp` を素材・色・照明の参照として添付した。ほかは文章だけ。

## プロンプト

### 幕（curtain）

Use case: photorealistic website asset (will be sliced and composited by code).

A single heavy dark brown-black blackout stage curtain hanging from a thin black metal curtain rod, photographed exactly straight-on (front elevation, no perspective, no tilt, no lens distortion). The attached image is a MATERIAL, COLOUR AND LIGHTING REFERENCE ONLY (same cloth, same dark sepia brown that is almost black inside the folds, same dim warm tungsten light with soft warm highlights on the pleat ridges). It is not an edit target.

Layout, in absolute terms:
- The rod is one straight horizontal black pipe at about 8% from the top of the image, running past both the left and right image edges. Small black curtain rings every pleat.
- Above the rod the background is pure flat white.
- The cloth spans the full image width; it continues past the left and right image edges, so no side edge of the cloth is visible.
- The fabric hangs in many deep, irregular vertical pleats (different widths, not evenly spaced), matte velvet-like, slightly brighter in the upper centre as if lit by one small spotlight from above, darker toward the left and right.
- The curtain ends at about 78% of the image height with a real heavy fabric bottom hem. The hem edge is close to horizontal but gently uneven, following the pleats.
- Below the hem everything is pure flat white (#FFFFFF): corner to corner the same single colour. NO floor, NO cast shadow, NO gradient, no objects.

No text, no logos, no people, no dolls, no furniture. Realistic photographic detail, restrained highlights, matte, not glossy, not an illustration. Landscape 3:2.

### 壁（wall）

Use case: photorealistic background texture for a website (text will be placed on top).

A flat texture photograph of an old Japanese interior plaster wall, photographed perfectly straight-on with even soft daylight, filling the entire frame edge to edge.

- Colour: warm off-white / pale greige (close to #DDD5C4), matte, chalky.
- Subtle unevenness from trowel marks. Four or five very fine hairline cracks, thin and irregular, mostly near the edges.
- Faint yellow-brown water tide marks concentrated toward the lower-left corner and along the right edge. A few tiny pinholes and specks.
- Overall LOW contrast so black text placed on top stays readable. The central 60% is calm and nearly uniform.
- No objects, no frame, no baseboard, no ceiling, no shadows cast by objects, no vignette, no light falloff, no text.

Photographic, not an illustration. Landscape 3:2.

### 監視カメラ（camera）

Use case: photorealistic cutout asset for a website.

One old analog CCTV security camera: a boxy bullet-type body with a short sunshield hood, dark charcoal grey to black painted metal, slightly dusty and aged with small scuffs. It is mounted on a short black articulated bracket whose round base plate is at the top right of the image, so the camera hangs down from the top right and points toward the lower left at about 35 degrees below horizontal.

- Side three-quarter view. The lens is dark glass with only a faint dull reflection.
- One black coaxial cable leaves the rear of the camera and goes up out of the frame at the top right.
- Background: pure flat white (#FFFFFF), corner to corner the same single colour. No shadow on the background, no gradient, no wall texture.
- No brand names, no logos, no text, no stickers, no glowing LEDs.
- Restrained highlights, matte, not a glossy luxury render, not an illustration.

Square 1:1. The camera with its bracket fills about 80% of the frame.

### 髪（hair）

Use case: cutout asset for a Japanese horror website (will be keyed by luminance).

Long straight black human hair hanging straight down from the very top edge of the image, like the ends of very long black hair draped over a ledge that is outside the frame.

- About 14 separate thin locks of clearly different lengths and widths, irregularly spaced across the width. The longest reaches about 90% of the image height, the shortest about 30%.
- Each lock tapers to fine wispy tips. A few single stray hairs between the locks.
- Asymmetric: denser and longer in the left third, sparse on the right.
- Pure matte black like sumi ink. No shine bands, no highlights, no grey.
- No scalp, no head, no face, no skin, no hands, no objects.
- Background: pure flat white (#FFFFFF), corner to corner the same single colour. No shadow, no gradient.
- No text.

Portrait 2:3.

### 染み（stain）

Use case: texture asset for a website, to be multiply-blended onto a pale plaster wall.

A large dark water-damage and mould stain, as it would look on an old plaster wall, isolated on white.

- Background: pure flat white (#FFFFFF), corner to corner the same single colour.
- The stain sits in roughly the central 70% of the image: an irregular organic blotch of dark grey-brown and soot black with soft feathered tide-line edges and several darker cores.
- Streaks run downward from it as if liquid seeped down the wall: 6 to 8 drip trails of clearly different lengths at the bottom.
- A few small satellite spots around it. Asymmetric, heavier toward the upper right.
- It must not look like a figure: no face, no body, no hand shapes, no symbols, no text.
- The stain fades to pure white before it reaches any image border.

Photographic, matte, not an illustration, no paper texture in the white. Landscape 3:2.

## 罠

- **絵に CSS の `filter: drop-shadow()` を掛けない。** 監視カメラに掛けたら、絵の箱の形に薄い影が出た（箱の内側が 10 階調ほど暗い）。影は `prepare-site-art.py` が絵に焼き込む。
- 白地を抜くとき、縁につながらない白（レールと幕の間、ケーブルと腕の間）は塗りつぶしでは抜けない。範囲を指定して白さで抜く。
- レールと幕の隙間には、幕の影で灰色になった白が混じる。240 以上だけ抜くと右半分が抜け残り、裏が暗い章（クレジット）で灰色の帯になった。棒・輪・布は 100 より暗いので 150 以上を抜く。
- 撮影用の Chrome は画像をキャッシュする。素材を作り直したら、キャッシュを切って撮る（`Network.setCacheDisabled`）。古い絵を見て原因を取り違えた。
