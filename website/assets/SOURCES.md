# 素材の出典と生成記録

このページ用の素材。画像生成は Codex 内蔵 image_gen を使用した。生成日は 2026-09-15（JST）。監視映像は体験の構成を伝える生成イメージであり、実機の録画ではない。

## キービジュアル

- desktop / small: `tools/title-art/keyvisual-layout-v3.png` の完成原稿を WebP に縮小・圧縮した。新しい描き直しはしていない。
- mobile: 同じ完成原稿を参照し、縦長の画面向けに画像生成で再配置した。
- 題字の正本: `tools/title-art/mawarimi-title-master-v2.png`。
- キービジュアル以外に人形の素材を配置していない。

## 配信用素材

| ファイル | 寸法 | バイト |
|---|---|---:|
| hero-desktop.webp | 1672 × 941 | 109188 |
| hero-small.webp | 1024 × 576 | 54070 |
| hero-mobile.webp | 900 × 1350 | 92990 |
| camera-01.webp | 1152 × 864 | 44272 |
| camera-02.webp | 1152 × 864 | 34538 |
| camera-03.webp | 1152 × 864 | 29502 |
| crt.webp | 1448 × 1086 | 77912 |

## 書体

- [Yuji Boku](https://github.com/google/fonts/tree/main/ofl/yujiboku): 書の見出し、サイト名、銘板の背の「廻リ視」。
- [Shippori Mincho](https://github.com/google/fonts/tree/main/ofl/shipporimincho): 本文、役割名、人名、受賞と展示予定。
- [IBM Plex Mono](https://github.com/google/fonts/tree/main/ofl/ibmplexmono): データ表記とクレジットの行番号。400 と 500 の 2 ウェイト。
- [Chakra Petch](https://github.com/google/fonts/tree/main/ofl/chakrapetch): 見出しの欧文と荷札の題。600 のみ。
- Google Fonts 配布版の必要文字を含む WOFF2 をサイト内で配信する。閲覧時に Google へ接続しない。取得は `npm run fonts`（`scripts/fetch-fonts.mjs`）。和文 2 書体は `index.html` の画面に出る文字だけ、欧文 2 書体は印字できる ASCII 全部を収録する。
- ライセンスは同じディレクトリの `yuji-boku-OFL.txt` `shippori-mincho-OFL.txt` `ibm-plex-mono-OFL.txt` `chakra-petch-OFL.txt`。いずれも上流の `ofl/<書体>/OFL.txt` をそのまま取得した。
- 収録文字は `fonts-manifest.json` に書き出す。文字を追加したら `npm run fonts` を走らせる。走らせ忘れは `npm run check` が足りない字を名指しで落とす。

## バーコードと QR

```powershell
py -3.11 scripts/make-codes.py
```

出力は `scripts/codes/` の 3 本で、`index.html` へは中身をそのまま貼る（インライン）。配信はしない。色は `currentColor` なので貼った先の文字色がそのまま棒の色になる。

- `code128-ivrc2026.svg` / `code128-dcexpo2026.svg`: `IVRC2026` `DCEXPO2026` を Code 128 B で符号化した。符号表を持つ自前実装で、依存は無い。
- `qr-site.svg`: 公開URL `https://mawarimi.vercel.app/` を [segno](https://github.com/heuer/segno) で符号化した（版 3・29×29・余白なし。誤り訂正は M を指定したが、同じ版に収まるので segno が Q へ引き上げている）。
- どちらも読み取り機に通る本物で、当て字の模様ではない。600px 以下ではバーコードを隠す（幅が 2mm になって読めなくなるため）。

## 参照

[第四境界](https://www.daiyonkyokai.net/) の入口にある実物のブラウン管と画面の切替を参照した。参考サイトの画像やコードは使用していない。旧 website の画面とコードを参照せず新規作成した。

## 生成プロンプト

### cctv01

Use case: historical-scene. Asset type: a single original surveillance camera still for an atmospheric Japanese fixed-camera horror artwork website. STYLE FIRST: authentically crude 1990s analog CCTV, low resolution optical softness, crushed warm black shadows, faded gray-brown near-monochrome, electronic grain, slight interlacing, no glossy cinematic rendering. Scene: mundane dim Japanese institutional interior, a short wide corridor with linoleum floor, scuffed plaster walls, a dark door opening at the back, one weak fluorescent ceiling light. One ordinary adult human alone, short straight black hair, loose plain off-white long-sleeve shirt, charcoal trousers, dark shoes, seen full body from behind, slightly right of center. Fixed camera bolted high in a corner near the ceiling, very steep diagonal downward view, distant person occupies only 20% of frame height. Space and blind corners dominate. Empty floor in foreground. The person is standing still looking down the corridor. Landscape 4:3 frame, fill the whole image with CCTV feed. Constraints: exactly one person, no dolls, no monsters, no extra silhouettes, no gore, no writing or timestamps, no logos, no television frame, no fisheye circular border. Feels like an unremarkable security recording with unsettling isolation, not a movie poster.

### crt

Use case: product-mockup. Asset type: photoreal object plate for a real interactive CRT on a dark Japanese horror website. A single old small 1990s Japanese CRT security television, matte very dark brown charcoal plastic housing, subtle dust and worn edges, thick rounded rectangular glass screen, small mechanical controls and faint red power light on the LOWER panel below the screen. Precisely straight-on, symmetric front elevation with no perspective tilt. Entire television visible centered horizontally, taking 84 percent of image width and 88 percent of height. Screen takes most of upper television face; glass is blank uniformly almost-black with an extremely subtle dark olive tint, NO bright highlights or reflections inside the screen because a video feed will be overlaid. Lower controls occupy the bottom 16 percent. On a dark table mostly disappearing into the black background. A tiny warm soft light from the upper left reveals housing edges; visible material detail but no polished sheen. Background continuous warm black #100e0c, corners nearly black. Landscape canvas 4:3. No text, no letters, no brand, no logos, no cables across the screen, no people, no dolls, no other objects. Physical utilitarian television photographed in darkness, not a futuristic screen, not a UI drawing.

### cctv02

Use case: historical-scene. Input image is a reference for the exact location, one adult person, clothing, palette and crude analog surveillance image texture. Generate a SECOND fixed ceiling security camera angle of THIS SAME hallway and SAME single adult. This camera is located at the far end of the hallway looking diagonally down from the opposite corner, facing back toward the original camera. Keep the scuffed beige-gray plaster walls, linoleum floor, pipes and dark doorways. The SAME short black-haired adult in the loose plain off-white shirt, charcoal trousers, dark shoes is now seen from the FRONT at a distance near image center, standing alone, face indistinct in low-resolution gray-brown CCTV. Their body is small, about 22% image height. Steep high angle, floor and side walls dominate. Keep an intruding dark wall corner on the left obscuring part of the corridor. STYLE HIGHEST PRIORITY: ordinary 1990s analog security footage, low detail, soft blocky video, grain, restrained warm near monochrome, no cinematic polish. Exactly one person, no dolls, no other figures, no text, no timestamps, no television frame. Landscape 4:3 full frame.

### cctv03

Use case: historical-scene. Input image is a reference for the exact single adult and Japanese institutional hallway and old analog CCTV style. Generate a THIRD fixed ceiling security camera view in THIS SAME corridor from a side junction. Very steep high corner viewpoint looking down across the bend, with a large wall or structural pillar close to camera occupying the right third and hiding the right-hand branch of the corridor. SAME ONE short black-haired adult in off-white loose shirt, charcoal trousers and dark shoes, seen in side/back profile moving around the corner, partly concealed by that foreground wall: show head, left shoulder and one leg clearly just to left of the wall edge. Person small relative to empty linoleum floor, 25% frame height. Walls have same cream-gray worn paint and pipes and dark doorway. STYLE FIRST: crude desaturated warm-gray brown 1990s security video, low-resolution optics, analog grain, soft interlacing, flat unglamorous fluorescent illumination, ordinary lonely mundane surveillance. Preserve believable topology from reference without identical framing. No dolls, no monsters, no additional people or shadows, no text, no timestamp, no television frame. Landscape 4:3.

### hero-mobile

Use case: compositing. This is responsive web art direction of an EXISTING finished Japanese horror key visual, not a new artwork. The input is the edit target and absolute authority for all imagery and calligraphy. Recompose this exact poster into a tall PORTRAIT mobile website hero, 2:3 aspect ratio. Keep the identical existing cream-and-red brush lettering '廻リ視' and its small reading まわりみ, exact stroke character, no replacement font. Keep the exact same Japanese ichimatsu dolls with their faces and kimono, dark warm brown-black curtain, floor and dim stage light. Adapt positions: title large in upper middle/right; doll group in lower-left half; keep breathing room around title. All existing title glyphs must be fully visible with comfortable side margins. Existing text can be rearranged but must read exactly: '廻るたびに狂う世界、' 'あなたは生きて戻れるか。' as small two lines at top; '実世界 × 固定視点ホラー' just below title; '企画・監督・制作リーダー' '白石大晴' small at lower right. Do not add any text. Preserve the muted ivory, red, brown palette. Corners and edges blend naturally into warm near-black #100e0c. Original image quality and subdued illumination. No new objects, no additional dolls, no new characters, no poster border, no UI. This must feel like the same exact finished key visual art-directed for a vertical narrow phone, not a variant illustration.
## 調査依頼の資料（2026-09-15）

- `investigation-request.webp`: `docs/onsite/handout.pdf` の第1頁を Poppler で画像化して WebP に圧縮。本文は原稿のまま。原本 PDF は保持。サイト上の傾きと紙の色は CSS。
- `wall-evidence.webp`: 既存の `Assets/Resources/Visitor/briefing-wall-v1.png.bytes` を WebP に変換。現地写真としての記録ではなく、作品内の壁を示す既存の図像。
