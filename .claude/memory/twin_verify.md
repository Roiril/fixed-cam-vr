---
name: twin_verify
description: HMD も Quest も無しで本番の Main シーンを導入から終幕まで通す twin-verify.py。走りは閉ループ、撮影は記録の再生。罠と実測
metadata:
  node_type: memory
  type: project
  originSessionId: 1128468e-bd6b-4231-9f7d-17f5f7eac49b
  modified: 2026-11-05T11:51:11.299Z
---

2026-11-05 ユーザー依頼「デジタルツインを新しく作り替えたので、それを使って、私がhmdを被らないでできる動作検証の仕組みも更新してほしい。」

**回し方**: `py -3.11 tools/twin-verify.py`（走行 5 分＋目線の動画 5 分）。`--no-video` は判定だけ。見る面は `preview_start twin-game` の `/verify.html`。
出力は Drive `renders/twin-verify/<時刻>/`。⚠ 同じ Unity プロジェクトを使うので、他の batchmode と同時に走らない。

**形**: `XpTwinDriver`（Editor のみ）が生の入力を `XpReplay` へ置き、`ShowWalkDebugDriver` の `IHost` として歩く。映像は `tools/twin/make_pack.py` の束（MJR）を頭の位置で引いて配る。
走った結果は `ExperienceRecorder` の記録に残り、撮影は既存の `unity.ps1 replay` がする。走る間は描画と入力の閉ループ、撮る間は開ループ。

**calib は 2 通り**: 既定の `--calib show` は APK と同じ本番の show.json。結末まで通るが、人形はツインの画に合わない（A は同じ 9/5 の写真から解いた値だが画角が違う。B と C は別の日の値）。`--calib twin` は人形の姿勢をツインの配置へ読み込み時に差し替える（`ShowControlClient.ApplyTwinCalib`。`ev=config calib=`）。人形は画に合うが、下の理由で別れの立ち位置が解けず、結末は中断になる。

**判定**: `analyze-xp-log.py --pc`（実機にしか無い受信 fps・パススルー・タブレットの口などは NA）＋ `twin-verify.py` の周の印と結末。
実機ログで偽 FAIL になる 2 つをこのとき直した。`released=1` の報告に MarkLogged を求めていた。別れの人形（笑わない契約）の標本で「笑っていない」を出していた。

**罠**:
- batchmode では `WaitForEndOfFrame` が再開しない（`XpReplayCapture` にも書いてある）。`EndingFrameCapture` が撮れず、`ready=True` のまま写真待ちで止まり `ending=Interrupted` で終わった。isBatchMode だけ次フレームの頭で撮る。Quest の記録を PC で再生するときの別れもこれで直る
- 導入の練習は案内を読み終える前の押下を受け付けない。1 回押して待つと Tutorial で止まるので、受理されるまで押し直す
- 周の印（束の右下 16px×3 マス）: 録画カットの後ろには次の周の印が映ってよい。録画は区間を出たあとも回る（`ev=rec v=stop` は次の区間の途中）。判定はカットの頭で見る
- 立ち止まりの間、人は歩きの 1 コマで止まって見える。焼いてあるのは順走だけで、逆走と引き返しの画は無い
- 撮影では目のカメラが描かれず、`MjpegScreen` の信号の前段が回らない。回さないと HMD と別のシェーダ経路になり、本編にも筐体が映る。`XpReplayCapture` が撮る直前に `PrepareSignalNow` を呼ぶ
- 動画の時刻は再生ログの `tau=`。走行側の `run_xp.log` の `t=` は別の時計で、同じ場面が約 4 秒ずれる。⚠ 11-05 の走行では段 3 の筐体と段 4 の映像が `tau` より 0.8〜0.9 秒早く画に出た（原因は未確認）。段の頭は `tau` だけで決めず、画の変化を見る
- 目線の動画ではタイトルと Walk の面が重なって映る。走行の記録は Done の 0.6 秒後に Walk で、再生はタイトルだけ遅れて出る（比較の最大差 7.84 秒。11-05）。順の判定は走行の記録で見る。名乗りは自動走行で省略され、動画に出ない
- 目線の動画は縦 52 度で撮る（紹介動画の既定 72 度だと画面が幅の 3 分の 1 しかない）。視野の縁で起きることは `--fov 72`
- 部屋は黒い（パススルーが無い）。導入の殻の破砕や周縁は確かめられない。後ろ上の動画に画面しか映らないので既定で撮らない（`--follow`）
- `show.json` は束の校正元でもある。`pack.json` の `calibGapPx` が本番 calib とツインの差（A 30.5 / B 31.5 / C 98.8px）
- `Application.Exit` の中の SaveAssets で Editor が落ちることがある（`exit=3221225620`・ProjectSettings の取り込み中。2 回に 1 回出た回もある）。走行の記録と判定は揃っているので、撮影は exit ではなく `twin_run.json` の result で決める
- A の動画の焼き直しは `seq --cams A --w 640`。`--w 960` だと `make_pack.py` が寸法で落ちる。1 台 468 コマで 40 分・npy が 1.9GB（C: の空きが尽きて途中で落ちた。Blender は exit 0 を返すので log の Traceback を見る。`seq` は続きから焼く）

**カメラ A は 9/5 19:35 の写真（2026-11-05）**: ユーザー指示「カメラAはだいぶずれているので、過去に撮影したものを使って」。`tools/twin/plates/plate_A_20260905_193511.jpg` で解き直した。
- 本番 calib（9/5 19:39 に手で解いた）を写真の観測へ当てた差は 9/5 が rms 7.5px、9/24 が 20.1px（手すりが約 25px ずれる）、9/7 が 26.8px。**calib の相手は 9/5 の写真**
- 画角を水平 96 度に止めて解いた A: (0.575,0.928,-0.813)・yaw -39.7・差 2.73px。B と C は 9/24 のまま。三脚は B と C の画に入らないので A だけ焼き直した
- calib の位置 (1.04,0.94,-0.99) が遠いのは画角が 77.4 度だから。同じ写真に合わせても、狭いレンズなら遠くに解ける。**位置の数字を直接比べても「動いた」とは言えない**
- ⚠ 製品の `menu plate-validation` は StreamingAssets の 9/24 の A に 9/5 の calib を重ねている（`PlateValidationLatestPlate`）。A の枠の線は写真から約 25px ずれる

**見つけたこと（2026-11-05）**: [[ending_farewell_doll]] の立ち位置。
- 実際のレンズの値（水平 96 度か、A だけ解いた 91.5 度）だと、どの写真の位置でも別れの立ち位置が解けない。C# のログは 9/5 の位置で `visitors=548 dolls=99 separated=7993 ordered=3874 framed=0`、9/24 の位置で 325/6/32/16/0
- 止めているのは体験者の条件 `VisitorFits`。足の四隅が枠の外か、壁に近い（Python で写して数えた。9/5 f288 は 4,463 組が足で 2,287・壁で 2,176 落ちて 0 組。9/24 は 16 組すべて足）。退場の道で落ちる組は無い
- 本番 calib で通るのは画角が狭い値だから。同じ数え方で成立は 1 組だけ（格子 0.025m）。実際のレンズで calib を解き直すと別れが出なくなるはず（未確認。Python の数え方は C# の visitors を 1 割ほど多く数える）
- 本番 calib でも立つ所の許容は公称の 0.20m より狭い。人形を向いて立つ円周のうち写真が成立する割合は半径 5cm で 19%、10cm で 19%、20cm で 13%（`ev=twinfarewell`）
- 写真が成立しないと、報告の約 50 秒後に `ending=Interrupted`（`run.endHoldMaxSec`=60 の時間切れと読むのが自然。未確認）
- 立ち位置が解けない間は解き直しが止まらず、`placement unavailable` が 50 秒で 700〜1,400 行出る

関連: [[twin_cycles]] / [[experience_replay]] / [[virtual_walk]]
