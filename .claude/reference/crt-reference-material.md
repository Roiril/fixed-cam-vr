# 参照CRT材質の技術詳細

現行実装の契約。見た目の採否は`canon/OPEN.md`の0294で未判定。

- 参照筐体のRGBは元参照のCaseColor、開口alphaはFrameから読む。Reflectance/Glass/Normal/Gradeはlinear、Frame/CaseColorはsRGB。NormalはDefault importerのRGBを自前で[-1,1]へ展開する。参照素材は同じClamp/Trilinear samplerを共有する。
- 参照画面の通常時は画像由来のISP補正と単調1D曲線を合成済み映像へ掛ける。夜間では通常時の補正を弱め、既存の夜間曲線を保持する。ISPの共通実装は`GradeSensorColor`。
- 参照画面ではOSD後に表示コントラスト、Glassの透過、Normalの光路長吸収、視点依存Fresnel反射、筐体RGBの順で処理する。これらも最後の`_SwitchDim`と`_ScreenPower`で消える。低周波の参照RGB gainは診断の除去実験だけで使い、runtimeの既定は0。元参照の室内照明を別室へ焼き込む処理を採用しない。
- 参照画面の診断は`crtRef`（全素材が材質へ届いたか）・`crtCase`（物理比率）・`crtFrame`（論理比率）。ShowTelemetryHostとanalyze-xp-logを対で保つ。実描画と材質/ソースhashは`menu crt-reference`と`tools/screen-reference/verify.py`で確認する。
