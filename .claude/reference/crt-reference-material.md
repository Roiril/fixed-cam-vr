# 参照CRT材質の技術詳細

現行実装の契約。0298で明るさ＋解像感の方向が選ばれ、0300で通常加工を重ねた右下を次の基準にした。見た目の96%条件は未達。

- 参照筐体のRGBは元参照のCaseColor、開口alphaはFrameから読む。Reflectance/Glass/Normal/Gradeはlinear、Frame/CaseColorはsRGB。NormalはDefault importerのRGBを自前で[-1,1]へ展開する。参照素材は同じClamp/Trilinear samplerを共有する。
- 参照画面の通常時は画像由来のISP補正と単調1D曲線を合成済み映像へ掛ける。夜間では通常時の補正を弱め、既存の夜間曲線を保持する。ISPの共通実装は`GradeSensorColor`。
- 参照画面ではOSD後に表示コントラスト、Glassの透過、Normalの光路長吸収、視点依存Fresnel反射、筐体RGBの順で処理する。これらも最後の`_SwitchDim`と`_ScreenPower`で消える。低周波の参照RGB gainは診断の除去実験だけで使い、runtimeの既定は0。元参照の室内照明を別室へ焼き込む処理を採用しない。
- 参照画面の診断は`crtRef`（全素材が材質へ届いたか）・`crtCase`（物理比率）・`crtFrame`（論理比率）。ShowTelemetryHostとanalyze-xp-logを対で保つ。実描画と材質/ソースhashは`menu crt-reference`と`tools/screen-reference/verify.py`で確認する。

## 加工感の診断

`CrtReferencePreview.RunProcessingOrder`はEditor専用の順序比較。`CRT_ORDER_DIAGNOSTIC`の既定はoffで、本番materialから有効化しない。640×480 ARGBHalf linearのmip付きRTを2回通し、raw→PSF→GradeとGrade→PSFを同条件で比べる。CG=0・mono=0・field=0。glass/refraction/caseは共通の最終stageだけに掛ける。両条件はGradeとlens/sensor/glareの相対順序および再サンプリングがsinglepassと異なるため、その差を順序交換だけの効果とは扱わない。

診断keywordは`shader_feature_local`。各stage専用materialを`ShaderUtil.CompilePass`で同期コンパイルし、source-space stageのvertexはclip-spaceへ直接置く。非対称patternと未加工写真のraw-copy誤差0.003以内の正例、誤った加工済み画像を読む負例の両方で校正してから比較する。初回の筐体混入や投影誤差が残る出力は採点へ使わない（経緯は`memory/crt_processing_order_calibration.md`）。

- `RunOpticsMatrix`は本番singlepassでspread倍率1/0.5とconvergence倍率1/2.5だけを変えるA/B/Cの12枚。現行3枚は28のhashへ一致させる。
- `RunPsfShape`は旧mip/専用Float linear mip/9×9 Gaussianの3条件。`CRT_PSF_DIAGNOSTIC`はoff既定。専用`_CrtPsfLinearTex`はPSFだけへ使い、glare等は元LiveTexを保つ。LoadImage後のencoded mipを後段decodeする光量減衰を正負校正で確認し、候補は光量約1と旧の二次moment差2%以内へ合わせる。raw PNGの見た目ではなくARGBFloat readbackで測る（`memory/crt_psf_mip_color_space.md`）。
- `RunSoftnessMatrix`は校正済みlinearLODとLOD+1、glare0.55/0の2×2でA/B/Cの12枚。前段linearMipと同じ3枚はhash一致。64点で光量約1と二次moment約4倍を測る（`memory/crt_softness_glare_probe.md`）。

これらは診断候補。光量保存の校正を参照への類似度と混同せず、本番のGrade・素材6点・既定値を変更しない。出力先が非空なら拒否し、画像・全property・source/shader/helper/map hashをmanifestへ保存する。

`RunFadedLensMatrix`（CrtReferenceFadedLensPreview.cs）は0296の4条件をA/B/Cで比較する。`CRT_STYLE_DIAGNOSTIC`と_CrtLensWarpはoff/0既定。heartUvからsampleUvの直前で共通logical座標を逆写像p/(1+k*r²)へ写し、OSD用screenUvと筐体・反射は固定する。k=.06の正負校正、4層GPU差0、点像誤差最大.02363px、旧28/基準03/keyword off/係数0のhash一致を確認。新shader後のEditMode2288/2288通過、22source前後差0。原寸写真15枚と校正、sourceの全22点はDriveのprocessing-faded-lens-01に保存。

同比較の脱彩度条件は0297でユーザーから訂正された。warpOnlyを次の基準にし、色の振幅を下げない方向で比較を続けた。

`RunSignalMatrix`はpreGlass取得→表示RGB gain/輝度・色差の空間filter→共通Glass/Case/powerの順で比較する。SIGNAL診断のfrag出力はfloat4、内部colはhalf3据え置き。無処理対照と旧warpOnly3枚のhash一致、定数色/広域差の保存と高周波の減衰を正負校正した。1byte差の分離は`memory/crt_signal_stage_precision.md`。

## 本編の信号処理

`CrtSignalProfile`が選択された方向の試作値を持つ。gain .85、warp .06、Gaussian sigma .85/.65/.65、convergence倍率1.8、ImageAdjustment.w=-.32、displayContrast1.16、glare0。具体値はシュビーの試作値。通常Mono/CoarseBlocksは従来のDirectorから受け取り、.5へ固定しない。

`MjpegScreen`の`CrtSignalProcessor`はraw LiveTextureを保持したまま、専用linear mipと2種類のfullRGB PSFを用意する。source世代・寸法・CoarseLOD等の変化でsource cacheを更新する。2倍格子の位相を合わせたH/V分離で、fractional LODのdense参照との最大差.003・二次moment差2%以内を固定した。sigma0の差分検出はraw入力を読み続ける。

全合成とOSDは1280×1030 linear Floatのcanonical preGlassへ描き、表示RGBで輝度/色差を別々に正規化filterしてgainを掛ける。曲面はsurface UVから処理済み信号を読み、Glass/Case/SwitchDim/powerを一回だけ適用する。共有本体は`CrtCompositeShared.hlsl`。player用`CRT_SIGNAL_RUNTIME`とResourcesのshader参照で診断keywordへ依存しない。

表示はmain cameraのbeginCameraRenderingで1 frameにつき一回。CG等の補助cameraでは更新しない。EndingFrameCaptureとExperienceShotCaptureは専用materialの現在値から明示captureし、前の処理済み画像を流用しない。無効化時はbindingを外してRTを解放する。

## Web調整

`tools/unity.ps1 crt-tune`でlocalhost専用のEditor調整を起動する。Web UIは`tools/crt-tuner/`。0300の右下（通常Mono .5、gain .85）を比較用の固定基準にし、中心EVと薄い中性〜寒色の乗算をガラス前へ加える。スライダーは実Unityのmaterialへ渡し、ブラウザ側の別シェーダで模倣しない。追加0では基準へ戻す。中心/色はEditor専用の試作で、設定JSONやブラウザの値を本編/Questへ自動保存しない。

比較レポートはGoogle Docs: https://docs.google.com/document/d/1IEGXhPRB9DNk19YbKHU3UINeosqQiwHT19fW5GbEf-w/edit
画像・manifest・PDF検品は`G:/マイドライブ/shubie/reports/2026-10-30_crt-reference/`。0295以降の加工感評価は旧評価と区別する。
