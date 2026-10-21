# 2026-10-18 警告表示の性能検証

ユーザー指示: 「警告表示中のfps低下も、採用した見た目は変えずに改善しておいて。実機で90fps出るかまで確認して。」

## 条件と変更前

- Quest α `2G0YC1ZF890864`。90Hz目標。eyeW=3264 / renderViewportScale=1.00。解像度・品質設定は変更しない。
- 前回の録画あり走行: 警告中79.7/80.8/79.3fps。冒頭の2秒標本49.9fpsは警告開始前の重いカットを含む。
- 録画なしの変更前走行: `G:/マイドライブ/shubie/renders/comms-perf-20261018/baseline/xp_walk_20261018_190615.dump.log`。Halt XP120.21秒→復帰終了127.81秒。
- 2秒標本: 50.3 / 80.4 / 83.7 / 82.4fps。開始温度34.4℃、充電中。FAIL 0 / WARN 16 / OK 83。録画だけが低下の原因ではない。
- 元APKを同Driveフォルダの `baseline.apk` と `baseline.apk.content.json` に保持。

## 数式と描画を変えない削減

- CommsPanelPlate: `_Curse=0 && _Sweep.x=0` なら結果に寄与しない旧ノイズ4回を省略する。
- CommsAvatar: FaceMix=0で旧斑を評価しない。同じwarpを顔UVと復帰マスクで再利用する。
- CommsFlat: 同一復帰マスクの再計算を省略する。
- CommsPanel: 非表示文字の頂点・色・UV再送を省略。復帰パラメータは有効中と終了の縁だけ送り、通常表示中は再送しない。
- シェーダ読込は起動時の1回に固定。文言を変更していないためフォントは再生成せず維持する（mtimeだけでフォントの焼き直し判定になるビルドは `-Force`）。
- `optimized-preview/Screenshots/comms-preview/recovery/comparison.json`。同じ24/60/135/165フレームの旧版と新版本体の実描画は全画素完全一致（max/mean差0）。
- EditModeのComms関連255件通過。

## 計器

- Development専用 `CommsPerformanceProbe` がHalt/Promptの表示中だけ1秒窓と全体のfps/deltaを記録する。
- `[CommsPerf]` にnative CPU/GPU rawとraw×1000の仮定ms、実表示Hz、compositor dropped累計/差分、eye幅、viewportを併記。取得不能は-1。SDKのC#定義にraw値の単位記載がないため、rawも必ず残す。
- `ShowTelemetryHost` のfpsは約2秒間のUpdate回数/経過時間。コンポジタの表示fpsと同一視しない。表示の開始スパイクと安定区間を分ける。

## 改善後

- 初回削減版APK SHA256 `0eb3b27e6b6430b6d8a346bd1eb30a6cab3688dfcdc2c084b46de4a65c86ccdf`、buildGuid `a19fd33910d34334ac048064c94c60d1`。
- `optimized/xp_walk_20261018_191816.log`。録画なし、90Hz/eyeW3264/rvs1.00。安定区間80.10〜85.42fps。警告全体80.99fps/7.532秒、compositor dropped増分126。冒頭最大115.689ms。まだ90fps未達。
- GPU raw×1000は安定区間8.244〜8.464ms。CPU native metricは取得不能(-1)。25〜30msフレームが周期的に出る。GPUだけを根因としない。
- CPU監査: 全CameraStreamを同じUpdateで同期LoadImageする。MjpegScreenは0.25秒毎に256点前後のGetPixel。警告中は録画再生終了済みなのでRecordedFramePlayerの展開は今回の第一候補から外す。
- 次の計測: LoadImage、CameraStream.Tick全体、SampleLumaのStopwatch累計と窓内フレーム最大。20ms超フレームと直前フレームのLoadImage合計を併記する。動作頻度は変えず計測だけ追加。
- 未達のまま90fps達成とは報告しない。非表示カメラを間引く場合も録画FrameTapと切替直後の鮮度、watchdog/Discoveryの成功展開証拠を保つ必要がある。

## CPU測定と次の削減

- `cpu-probe/xp_walk_20261018_193128.log`。警告安定78.05〜83.35fps、全体79.21fps。LoadImageは89〜91回/秒で373〜385ms/秒。同じフレームで3回重なると12〜14ms。20ms超フレームの直前にも同じ3回が記録される。
- SampleLumaは1回約0.1ms。CameraStream.Tickのほぼ全時間がLoadImage。全台同時展開が90Hzの11.1msを超えることを確認。
- 採用する負荷分散: 表示中とFrameTapありは毎フレーム優先。残り背景だけ順番に1描画フレームで最大1台展開する。2台×30fpsに対して80〜90回/秒の機会がある。全体1枚制限や背景2Hz化は採用しない。
- 非選択台は最新JPEG slotを消費せず監視・メタ更新のみ続ける。切替先は公開前に専用展開メソッドで最新を反映する。通常Tickの二重呼出は監視時間を二重加算するため禁止。
- 同期展開を分散する変更は実装済み。展開fps・画像年齢・録画・再接続と実fpsを実機で確認する。
- 通信関連EditModeテストは1,714件通過（新規の巡回テスト7ケースを含む）。
- 分散初回: `scheduling/xp_walk_20261018_194420.log`。安定87.00〜90.18fps、全体85.79fps。展開は最大2回/フレーム、約8〜9msに減少。開始の113.867msは残る。shader切替は120.278秒/frame9771で0.043ms、停止は120.856秒/frame9806（直前LoadImage4.571ms/1回）なので、setter自体の停止ではない。
- 初回で登録カメラは4台、cam3は未受信だった。無フレームの4台目も巡回枠を消費し、cam1/2が27〜29fpsに低下。受信slotの有無で背景候補を絞る修正を追加する。候補判定と優先判定は別配列にし、監視と録画の契約を維持する。

## 最終候補と未完の検証

- 無フレームの背景を除外する修正まで実装済み。関連テスト10件通過。APKビルド成功、αへ導入成功（lastUpdateTime 2026-10-18 19:54:21）。
- 最終候補SHA256 `a1d0a985bb30550f2368547378875f90f50b745515bc9c4425b594f936cc3f23`、buildGuid `47c8c8716cfb464da952286a11acbfa6`。contentIdは変更なし。
- 最終測定は走行中にQuestがADBから消えたため中断。直前は充電中1%、温度48.8℃、thermal NONE。起動後の途中ログ55行のみでHalt/Promptへ到達していない。最終版の性能証拠には使わない。
- ユーザーは遠隔におり電源を入れられない。走行後のスリープは実行していたが、充電表示を根拠に1%で導入・反復走行を続けた判断が不十分だった。電源断の有無自体は未確認。現地での電源操作を今すぐ求めて再開を待たない。quest-build/quest-captureに、導入後の待ち時間も即スリープし、低残量ではスリープ中の回復を実測する注意を追加。
- 中断ログは `G:/マイドライブ/shubie/renders/comms-perf-20261018/ready-scheduling-aborted-usb/`。アプリの完走不良とUSB切断を混同しない。止まった自分のlogcat回収プロセスだけ終了した。
- 直前の完走版の同じ窓（RecoverySec>=3、1秒窓5本）の加重平均は81.43→89.123fps。窓内90.18fpsに達したが、compositor欠落と開始113.867msは残るため常時90fps達成ではない。
- 接続・電源復帰後に最終候補の録画なし155秒走行をやり直す。次に4周目付近だけ録画して、採用画の維持と排除後の通常スイ復帰を最終版でも確認する。以前のAPKでは通常復帰確認済みだが、その画像で今回を確認済みにしない。
- 再開用: `C:/Users/kouga/AppData/Local/Temp/codex-comms-recovery-20261018/extract-final-perf.py`（完走ログを要求）、`capture-final-perf.py`（起動114秒後から27秒録画してDriveへ保存）。`run-quest-xp-test.sh` は `SERIAL=2G0YC1ZF890864`、OUTDIR末尾 `ready-scheduling` で再実行する。
