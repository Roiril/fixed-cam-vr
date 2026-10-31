---
name: crt-psf-mip-color-space
description: LoadImageで作るsRGB mipの光量減衰と、PSFだけをlinear化するEditor比較の実測。
type: project
---

2026-10-31、CRTのbox mipとGaussianを「形だけ」比べる校正で光量差が見つかった。
写真と同じ640x480 RGB24・mip有効・sRGBのTexture2Dへ、既知パターンPNGをLoadImageした。
16個の孤立白点を4x4のmip位相へ置き、raw PSFを4倍解像度ARGBFloat linearで実描画した。
旧mipの点光量合計はRGB=(0.140767,0.183027,0.183027)。直接linear Gaussianは約1。
二次モーメントを合わせても光量が異なるため、最初の2条件は写真を描画する前に止めた。

LoadImageしたsourceのCPU mip値は、sRGB/linear flagの両方でlevel0=1、level1=0.24705882、level2=0.05882353。
sRGB decode後はlevel1=0.04970656、level2=0.00477695になる。encoded値の平均を後からdecodeしていた。
linear flagだけを替えたRGB24の点光量は(0.966176,0.982541,0.982541)。8bit mipの丸めも残る。
元写真のmip0をGetPixels().linearでdecodeし、RGBAFloat linear=trueへSetPixels/Apply(mips)する専用PSF textureを作った。
中間灰128/255のbaselevelは元sRGB描画と最大差0.0004883。decodeせずlinear flagだけ替える負例は0.285645で不一致。
実写真A/B/Cのbaselevel最大差は各0.00146484（合格上限0.003）。

`CrtReferencePreview.RunPsfShape`はEditor専用の3条件比較。本番の`CRT_PSF_DIAGNOSTIC`はoff既定。
旧mip、専用Float linear mip、正規化9x9 Gaussianを比較する。点光量の旧減衰へ候補を合わせない。
元LiveTexは保持し、glare・色差・その他の処理は元textureを読む。専用textureを読むのはSampleLivePsfだけ。
候補のLOD/sigmaを16点の中心二次モーメントへ合わせた。単位はsource pixel。

| 条件 | RGB点光量 | X/YそれぞれのRGB二次モーメント |
|---|---|---|
| 旧mip | .140767 / .183027 / .183027 | 1.368533 / .820542 / .820542 |
| linear mip | 1.000000 / 1.000019 / 1.000019 | 1.359376 / .810308 / .810308 |
| Gaussian | 1.000044 / 1.000055 / 1.000055 | 1.368871 / .821729 / .821729 |

旧LOD=(1.469668,1.121745,1.121745)、校正linear LOD=(1.252441,1.050293,1.050293)。
Gaussian sigma=(1.094311,.806226,.806226)。候補のmoment差は旧値の2%以内。
点像の副peak rise、斜めedgeの副peak riseと逆勾配は全3条件・全RGBで0。
edge接線むらRMSは旧(.034065,.042725,.042725)、linear mip(.030789,.036025,.036025)、Gaussian(.015768,.021283,.021283)。
これらは計器の実測。参照画像への類似度や見た目の採用を意味しない。

出力は`G:/マイドライブ/shubie/reports/2026-10-30_crt-reference/processing-psf-linear-01/`。
写真はA/B/C各`_control.png`、`_linear_mip.png`、`_gaussian.png`。control3枚はround28のhashと一致。
source・shader・helper・maps・全property・各写真hash・探索中の校正値はmanifest.json。マスク済み実行ログも同じ場所。
旧不合格出力は`processing-psf-01/`と`processing-psf-01-invalid-apply/`へ残した。
