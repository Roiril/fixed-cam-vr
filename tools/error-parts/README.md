# エラー演出の部品

第 3 版では警告全体を画像生成しない。
文字の画像と記号とノイズを分けて Unity で組む。

## 文字

`py -3.11 tools/make-error-typography.py` で文字だけを書き出す。
入力は `Assets/Art/Fonts/UnauthorizedAccess/` の未改変フォント。
出力は `Assets/Art/Textures/UnauthorizedAccess/`。

- `wordmark-v3.png`: DSEG14 Modern Bold の `WARNING`。
- `subtitle-v3.png`: DotGothic16 の `不正アクセス検出`。

書体の字形は画像生成しない。字間はスクリプトで指定する。
色と小さな発光は Unity の専用シェーダで付ける。

配布元:

- [DSEG v0.46](https://github.com/keshikan/DSEG/releases/tag/v0.46)。作者 keshikan。SIL OFL 1.1。
- [DotGothic16](https://github.com/fontworks-fonts/DotGothic16)。SIL OFL 1.1。

フォントごとのライセンスを同じディレクトリへ同梱する。

## 画像生成

Codex 組み込みの imagegen で 1 パーツずつ生成。
生成結果の透過を保持する。文字や背景や装飾を一緒に焼き込まない。

### triangle-v3.png

Create ONE isolated compositing part for a VR unauthorized-access warning, not a complete warning design. Style priority: flat monochrome raster graphic as if drawn by a vector signal display. Subject: a single upright triangular OUTLINE with clean mitered corners, approximately equilateral, consistent substantial stroke about 3% of triangle width. A few tiny hard rectangular signal dropouts interrupt less than 5% of the outline. EMPTY transparent interior. Pure white shape only on genuinely transparent background (alpha). Centered, front orthographic view, generous transparent outer margin. No exclamation mark, no text, no numbers, no letters, no stripes, no second triangle, no enclosing panel, no border. No glow, no bloom, no gradients, no light flares, no painterly texture, no metal, no ropes, no organic grunge. This will be tinted and composited with separately typeset digital fonts in Unity. Square asset.

### signal-tear-v3.png

Create ONE isolated raster compositing part: a horizontal digital signal rupture made of white broken micro-rectangles and thin horizontal dashes on genuinely transparent background. Wide aspect 3:1. A compact dense cluster toward the left-middle breaks into a few much longer thin dashed threads to the right with unequal empty gaps. Hard electronic pixel edges, monochrome white only; vary density by gaps, keep bright pixels flat white. Thin upper and lower extremities, generous transparent margins. This is only a signal fragment to animate independently in a VR warning, NOT a finished screen. No letters, no digits, no readable text, no symbols, no triangles, no hazard stripes, no complete rectangular frame. No luminous bloom, gradients, blur, sparks, lens flares, smoke, paint smears, cables, organic tendrils or photographic material texture. Sparse connected discontinuities with 70% or more negative space.
