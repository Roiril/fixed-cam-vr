---
name: crt-softness-glare-probe
description: PSF専用linear mipのLOD+1とglare除去を分離する2x2診断と実測。
type: project
---

`CrtReferencePreview.RunSoftnessMatrix`はEditor専用の2x2診断。2026-10-31に実描画した。
PSFは前段と同じRGBAFloat linear textureのmode1。shaderと前段helperは変更しない。
校正済みLOD=(1.2524414,1.0502930,1.0502930)と、それぞれ+1したLODを比べる。
Glareは現行0.55と0。写真・post・Grade・maps・RGB位置差・Camera・描画順は固定。
不変hashから除くpropertyは`_CrtPsfLinearLod`と`_Glare`だけ（共通の入力LiveTex除外は従来どおり）。

Glareのwideサンプルは元LiveTexを読む。PSF専用linear textureへ置き換えない。
Glare式はwideとsharpの差分を読むため、PSFのLODを変えるとsharp側を通じた交互作用はある。
2因子以外の別処理やsource差し替えは加えていない。

粗いmipの全8x8位相を覆う64個の孤立白点で、4倍解像度のARGBFloat linear readbackを測った。
点は33source pixel間隔。+1LODの支持域まで含めて各点の光量と中心二次モーメントを集計した。

| 条件 | RGB点光量 | X/YそれぞれのRGB二次モーメント |
|---|---|---|
| 校正済みLOD | 1.000000 / 1.000019 / 1.000019 | 1.359376 / .810308 / .810308 |
| LOD+1 | 1.000000 / .999990 / .999990 | 5.421881 / 3.225615 / 3.225615 |

点光量は保存され、二次モーメントは約4倍（標準偏差で約2倍）。見た目の改善判定とは別の事実。
出力は`G:/マイドライブ/shubie/reports/2026-10-30_crt-reference/processing-softness-01/`。
写真はA/B/C各`_lod0_glare_current.png`、`_lod0_glare0.png`、`_lod1_glare_current.png`、`_lod1_glare0.png`。
現行3枚のhashは直前`processing-psf-linear-01/*_linear_mip.png`と一致。
source/shader/helpers/maps/importers/全properties/写真hashと校正値はmanifest.json。マスク済み実行ログも同じ場所。
