# 参照CRT材質の技術詳細

現行実装の契約。枠なしの開口形状は0302、ユーザーがWebで調整した現在の加工は0303で本編へ採用された。独立評価の96%条件は未達で、ユーザーの採用判定とは分けて記録する。

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

`CrtSignalProfile`が0303の採用値を持つ。gain .85、warp .155、Gaussian sigma 2.25/.65/.65、convergence倍率10、ImageAdjustment.w=-.32、displayContrast1.16、glare0。ガラス前のlinear信号へ中心EV 3.63/radius 1.68/falloff3、色strength1/coolness1を一回だけ適用する。postはexposure -1.75、contrast1.12、saturation .83、temperature .38、tint0、lift0、vignette1、grain .02、scanline .68。固定ノイズ .098、帯域制限on。これらはユーザーが操作した画面から全23値を取得したもの。採用元JSONと実GPU画像は`G:/マイドライブ/shubie/reports/2026-11-01_screen-profile-adoption/`。

通常Monoは初期から.5、CoarseBlocksはBlocksFor(.5)で固定。暗部ノイズだけが確定したsegmentLapとCourseOrder内indexを読み、`clamp01(((lap-1)*N+index)/(2*N))`で.05→.2へ進む。ABC順は1A=.05、1B=.075、1C=.1、2A=.125、2B=.15、2C=.175、3A=.2。時間と最大到達履歴では進めず、逆走では区間値へ戻る。人形化後は.2を保持する。2Cの実表示中の人形視点と3A人形化では独立にMono1と既存の粗さを使う。解除は共通ReleaseKで初期Mono=.5/ノイズ=.05へ戻し、次の体験者も初期状態から始める。音が読むProgressは変更しない。

Profileの再適用は固定加工だけを所有し、Directorが書いた動的Mono/ノイズ/粗さを上書きしない。CameraFeelFxが実際に描くfeel値を書く。実ノイズと位置目標をテレメトリで対にし、material未解決や後段上書きを検出する。

選択profileは元Frameの開口の50%輪郭に沿って外側をdiscardする。曲面mesh・logical crop・合成UVは変更しない。外側へ色/depthを書かず、映像内部はalpha1を保つ。Transparent blendで内側を現実へ透かさない。開口内の筐体RGBとの境界混合は採用PNGと同じに保つ。

`MjpegScreen`の`CrtSignalProcessor`はraw LiveTextureを保持したまま、専用linear mipと2種類のfullRGB PSFを用意する。source世代・寸法・CoarseLOD等の変化でsource cacheを更新する。2倍格子の位相を合わせたH/V分離で、fractional LODのdense参照との最大差.003・二次moment差2%以内を固定した。sigma0の差分検出はraw入力を読み続ける。

全合成とOSDは1280×1030 linear Floatのcanonical preGlassへ描き、表示RGBで輝度/色差を別々に正規化filterしてgainを掛ける。曲面はsurface UVから処理済み信号を読み、Glass/Case/SwitchDim/powerを一回だけ適用する。共有本体は`CrtCompositeShared.hlsl`。player用`CRT_SIGNAL_RUNTIME`とResourcesのshader参照で診断keywordへ依存しない。

表示はmain cameraのbeginCameraRenderingで1 frameにつき一回。CG等の補助cameraでは更新しない。EndingFrameCaptureとExperienceShotCaptureは専用materialの現在値から明示captureし、前の処理済み画像を流用しない。無効化時はbindingを外してRTを解放する。

## Web調整

0305で人形化後（無人＋人形）を追加。通常/人形視点とは別保存し、Mono1/noise .2を基準にする。
最新の無人原板と実CGを別テクスチャで合成し、加工済みPNGへ再び加工を掛けない。
各カメラの区間内で全身が見える代表位置に固定した静止プレビューで、実体験中の追従と時間進行は再現しない。
CG素材ではLiveTexのPSF/色ずれの入力も有効。POV素材だけ無効にする。

`tools/unity.ps1 crt-tune`でlocalhost専用のEditor調整を起動する。Web UIは`tools/crt-tuner/`。基準と初期値は0303の採用した23値。スライダーは実Unityのmaterialへ渡し、ブラウザ側の別シェーダで模倣しない。中心/色はEditorと本編で二重適用しない。ブラウザ保存値は保持する。設定JSONやブラウザの値を本編/Questへ自動保存せず、採用の指示があったときだけ明示して本編へ反映する。

比較レポートはGoogle Docs: https://docs.google.com/document/d/1IEGXhPRB9DNk19YbKHU3UINeosqQiwHT19fW5GbEf-w/edit

0304のWeb人形視点モードは本編2-CのMono1/noise .175を基準にする。通常と人形のブラウザ保存値を分ける。
実POV5動画の各0.5秒の静止コマと、無人写真の対照を選べる。実POVは本編と同じOverlay経路へ入力する。
本編OverlayにはLiveTexのPSF/色ずれが掛からないため、実POV素材ではsigmaR/sigmaGB/convergenceを無効表示にする。Mono1では彩度も効かず、Monoを下げると有効化する。
これは加工の調整用で、動画再生・カット切替・実機表示の検証ではない。UI操作だけでは本編採用値を変更しない。
コマの生成・hash検証・API契約は`tools/crt-tuner/README.md`。生成物は`G:/マイドライブ/shubie/reports/2026-11-01_crt-pov-tuner/`。
画像・manifest・PDF検品は`G:/マイドライブ/shubie/reports/2026-10-30_crt-reference/`。0295以降の加工感評価は旧評価と区別する。
