---
name: twin_verify
description: HMD も Quest も無しで本番の Main シーンを導入から終幕まで通す twin-verify.py。走りは閉ループ、撮影は記録の再生。罠と実測
metadata:
  node_type: memory
  type: project
  originSessionId: 1128468e-bd6b-4231-9f7d-17f5f7eac49b
  modified: 2026-11-11T07:41:37.718Z
---

2026-11-05 ユーザー依頼「デジタルツインを新しく作り替えたので、それを使って、私がhmdを被らないでできる動作検証の仕組みも更新してほしい。」

**回し方**: `py -3.11 tools/twin-verify.py`（走行 5 分＋目線の動画 5 分）。`--no-video` は判定だけ。見る面は `preview_start twin-game` の `/verify.html`。
出力は Drive `renders/twin-verify/<時刻>/`。⚠ 同じ Unity プロジェクトを使うので、他の batchmode と同時に走らない。
自分で見つけて直す回し方は `skills/twin-loop`。

**2026-11-11 に足した 4 つ**（ユーザー依頼「デジタルツインを用いたclaudecodeによる自己改善(演出、システム、他いろいろと)を、いい感じにするためにハーネスを整備してほしい。」）:
- **錠** `Logs/twin-run/.lock`。twin-verify どうしは同時に走らない（走行と撮影の隙間・共有の `pack` と `assets` を守る）。持ち主が死んだ錠は次の走行が引き継ぐ。他の batchmode との衝突は従来どおり `unity.ps1` の `Assert-NotLocked`
- **コードの指紋** `verdict.json` の `code.run` / `code.shoot`（HEAD・`Assets/Scripts` などの未コミット差分・show.json のハッシュ）。走行と撮影のあいだに変わると WARN。11-11 の relief は走行が古いコード・動画が新しいコードだった
- **新規と既知** `tools/twin/known.json`。11-06 以降の 15 走行すべてに同じ WARN が 4 行出ていて、新しい FAIL が埋もれていた。FAIL は既知にできない（読み込みで落ちる）
- **見る道具** `tools/twin-look.py`（`scenes` / `cut` / `pair`）。それまでは 1fps の JPEG を手で切り、場面の時刻表を毎回手で書いていた

**検証用の木 `tools/twin-tree.py`（11-11）**: ユーザーの問い「ワークツリーにしたら 2 本同時に走って検証できる？」（並行して動いていた別セッションで受け取ったもの）。
`Projects/Unity/fcv-twin-tree` に常設の worktree を置き、Library（6.2GB）を複製した。`sync` は `git stash create` で未コミットの変更ごと写す。
- 向こうの木で判定だけの走行が PASS した（`20261111_113553_tree-check`。走行 294 秒・初回は取り込みで全体 676 秒）
- **2 本同時は通る**（`20261111_122438_tree-concurrent2`。メインの木で 12:22 開始の通しが動いている間に、向こうの木で判定だけの走行が PASS。走行 293.8 秒で単独と同じ）。メモリは Unity 1 本 3.0GB
- ⚠ **保存先（`persistentDataPath`）は木ごとに分ける**（`ReplayXrGuard.ProductNameFor`。メインは `mawarimi-replay`、他は `mawarimi-replay-<フォルダ名>`）。同じだと `XpTwinDriver.Load` が走り始めに `xprec/` を丸ごと消すので、後の走行が先の走行の記録を消しにいく。12:02 の同時走行は、相手が掴んでいた `cam_Phone01.mjr` で IOException になり 49 秒で落ちた。掴まれていなければ黙って消えていた
- ⚠ 走行中の木の `Assets/` に未検証の C# を置かない。次に Unity が立つ段（撮影）がコンパイルエラーで落ちる。C# は向こうの木で `unity.ps1 test` を通してから置く
- ⚠ 木の名前にこちらのパスを含めない。`unity.ps1` の `Assert-NotLocked` はコマンドラインに自分のパスを含む Unity を自分の Editor と数える
- ⚠ Library の複製（robocopy 16 スレッド・5 分）と重なった 11:30 のこちらの走行が `error: onboarding`（導入が終わらない）で止まった。複製は初回だけ
- git 管理外の入力は `sync` が運ぶ（`Assets/` の下の無視ファイル・`tools/web-compositor/show.json`・`Logs/twin-run/pack`）。持ち込まないと test-ending-log と test_onsite_operations が落ちる

**周の中でも報告する `--marks 1A,1B,2A,3B`（11-11・演出ライブラリーのため）**: 既定の走行は導入の練習と最後の A でしか押さないので、手形が消える・幕がたなびく・返事の文が 1 度も画に出ない。
挙げた区間に入って 2.5 秒後に左 X を 1 回長押しする（`XpTwinDriver.TickExtraMarks`。押下はゲートが開いているときだけ）。11-11 の走行で 1A・1B・2A・3B・4A の 5 回が `ev=mark` に出た。既定の走行の `run_args` には載せない（前の走行との突き合わせを変えない）。

**動画と揃う時計は `replay_xp.log` の `tau=`。** 11-11 から Drive に残す。それより前の走行には無く、`twin-look.py` は走行の時計（`events.json`）で切って帯に「※走行の時計」と書く。
そのときは場面の終わりのコマに次の場面が映ることがある（`compare.txt` の差は中央 0.2〜0.9 秒・最大 8 秒）。⚠ `replay_xp.log` を読む経路は合成した行のテストだけで、実走行のログではまだ通していない。

**形**: `XpTwinDriver`（Editor のみ）が生の入力を `XpReplay` へ置き、`ShowWalkDebugDriver` の `IHost` として歩く。映像は `tools/twin/make_pack.py` の束（MJR）を頭の位置で引いて配る。
走った結果は `ExperienceRecorder` の記録に残り、撮影は既存の `unity.ps1 replay` がする。走る間は描画と入力の閉ループ、撮る間は開ループ。

**calib は 2 通り**: 既定の `--calib show` は APK と同じ本番の show.json。結末まで通るが、人形はツインの画に合わない（A は同じ 9/5 の写真から解いた値だが画角が違う。B と C は別の日の値）。`--calib twin` は人形の姿勢をツインの配置へ読み込み時に差し替える（`ShowControlClient.ApplyTwinCalib`。`ev=config calib=`）。人形は画に合う。2026-11-06 に別れの立ち位置を解き直し、どちらの calib でも Released まで通る。

**合成素材は既定でツイン版へ差し替える**（2026-11-11 ユーザー指示「合成している生成画像や、動画は、デジタルツインの環境で生成し直してok。動画は、数フレームをcodexの画像生成で生成して連続させてアニメーションする感じで」）。
本番の素材は実際のオフィスで作ってあり、廃墟風のツインに重ねると別の部屋が貼られる（1-B と 2-A は左半分に縦の継ぎ目が出る）。
作り方と対応表は `tools/twin/README.md` の「合成素材」。`--real-assets` で本番のまま走る。2-C の人形視点（pov_0〜4）は実写の録画で、まだ差し替えていない。
読まれたかは判定の頭に出る（`[ShowAssetResolver] override` の行。`XpTwinDriver` が run_xp.log へ写す。Editor の全文ログにも出る）。

**A の下端の白い段々はツインの床。** カーテンの裾の下に見える床が通常の加工で白く飛び、裾の折り目で段々に見える。2 周目以降の A と別れで出る。合成素材とも覆いとも関係ない（11-11 に一度「差し替えないと覆いが段々の塊になる」と書いたのは誤り。差し替えても同じ形で出た）。本番の写真の床は暗く、出ない。
ツインの代役は黒い服なので、通常の加工で体が闇に沈み、明るい腕だけが白い三角に残る。別れの写真の読みやすさを測るときはこれを差し引く。

**`events.json` の `t` と `run_xp.log` の `t=` は別の時計。** `events.json` は録画の開始（`ev=xprec state=start`）を 0 にしていて、目線の動画の秒とほぼ揃う（揃い切らない。正は上の `tau`）。`run_xp.log` はそれより約 15.5 秒大きい（11-11 の走行）。テレメトリを動画の場面に当てるときは引いてから読む。

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

**見つけて直したこと（2026-11-05 → 11-06）**: [[ending_farewell_doll]] の立ち位置。
- 実レンズ（水平 96 度か、A だけ解いた 91.5 度）だと、旧版の立ち位置はどの写真の位置でも解けなかった。C# のログは `visitors=548 dolls=99 separated=7993 ordered=3874 framed=0`。止めていたのは体験者の条件で、足の四隅が枠の外か壁に近い
- 本番 calib で通っていたのは画角が狭い値だから。それでも人形を向いて立つ円周のうち写真が成立する割合は半径 5cm で 19%。写真が成立しないと報告の約 50 秒後に `ending=Interrupted` になった
- 11-06 に体験者の立つ点から解く形へ変えた。結果と数字は [[ending_farewell_doll]]。⚠ 「足の判定に全身の回転半径を使う」形が足を画の下端から追い出していた。Python で写す検算は C# の visitors を 1 割ほど多く数える

- ⚠ `Logs/twin-run/pack` は検証の入力。11-06 に Drive へ退避され、`make_pack.py` が `frame_count.txt` を読めず `映像の束: exit=1` で落ちた。grep で絞ると何も出ず、失敗に見えない。`archive_to_drive.py` の対象から `pack` を外した。消えたら `G:/マイドライブ/shubie/archive/Unity/fixed-cam-vr/Logs/twin-run/pack` から戻す

関連: [[twin_cycles]] / [[experience_replay]] / [[virtual_walk]]
