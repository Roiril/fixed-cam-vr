---
name: crt-processing-order-calibration
description: CRT順序比較のraw-copy校正と初回描画の混入。原因と観測を分ける。
type: project
---

`CrtReferencePreview.RunProcessingOrder`は、Gradeと参照PSF/RGB分離の前後だけを比べるEditor診断。
本番keywordはoff。`shader_feature_local CRT_ORDER_DIAGNOSTIC`の有効化は診断Materialだけ。
640x480 ARGBHalf linear・同じmip生成/Trilinear/Clampを2回通し、共通の最終描画だけがガラス/筐体を持つ。
pairではGrade/PSF順序だけが異なる。現行singlepassとの差には、両方より後へ移るlens/sensor/glareと、RTの再サンプル/baseLodの位置の差も含む。

2026-10-31の初回はA_control_stage1に筐体が混ざった。B/Cには無く、shaderエラーも無かった。
非同期compile設定をfalseにするだけではraw-copy最大誤差0.991806で失敗。
各stage専用Materialと`ShaderUtil.CompilePass(material,0,true)`を導入すると筐体混入は消えたが、
world x1000の平面投影ではraw-copy誤差0.01418996が残った。
診断stage1/2のvertexをclip空間へ直接置き、画素中心を合わせると校正が通った。
複数要素を直したため、初回混入を非同期compileだけのせいと断定しない。
vertexの構造体メンバー名を誤った試行もあり、compile後のShaderUtilエラー検査を描画前にも置いた。

計器は非対称の既知sRGBパターンでUV方向とlinear readbackを校正する。
元画像のCPU GetPixels().linearとRGBAFloat linear readbackを比較する。
既知パターンの正例誤差0.001152933、加工/筐体入り負例0.947753906で、閾値0.003は正例を通し負例を拒否した。
実写真A/B/Cのraw-copy最大誤差は各0.001229703。閾値は失敗に合わせて緩めていない。
既知パターンは計器校正にだけ使い、最終比較には実在する無人写真を使う。

有効出力: `G:/マイドライブ/shubie/reports/2026-10-30_crt-reference/processing-order-01/`。
manifestにshader/source/map/parameter hash、正負校正、raw-copy誤差、singlepassとのMAE/RMSEがある。
最終9枚は1280x1030、中間12枚は640x480。中間PNGはlinear値の表示用読み戻しであり、HDR RTそのものの保存ではない。
失敗試行は同じ親フォルダの`processing-order-01-invalid-*`へ残した。既存出力が非空なら再実行は拒否する。
singlepass対controlのMAEはA1.897/B1.983/C1.627、pair差はA0.566/B0.472/C0.513（8bit・全画面）。
順序差より共通の分離処理差の方が大きい。数値差だけで類似度改善を主張しない。
