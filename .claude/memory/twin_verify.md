---
name: twin_verify
description: HMD も Quest も無しで本番の Main シーンを導入から終幕まで通す twin-verify.py。走りは閉ループ、撮影は記録の再生。罠と実測
metadata:
  node_type: memory
  type: project
  originSessionId: 1128468e-bd6b-4231-9f7d-17f5f7eac49b
  modified: 2026-11-04T15:32:47.813Z
---

2026-11-05 ユーザー依頼「デジタルツインを新しく作り替えたので、それを使って、私がhmdを被らないでできる動作検証の仕組みも更新してほしい。」

**回し方**: `py -3.11 tools/twin-verify.py`（走行 5 分＋目線の動画 5 分）。`--no-video` は判定だけ。見る面は `preview_start twin-game` の `/verify.html`。
出力は Drive `renders/twin-verify/<時刻>/`。⚠ 同じ Unity プロジェクトを使うので、他の batchmode と同時に走らない。

**形**: `XpTwinDriver`（Editor のみ）が生の入力を `XpReplay` へ置き、`ShowWalkDebugDriver` の `IHost` として歩く。映像は `tools/twin/make_pack.py` の束（MJR）を頭の位置で引いて配る。
走った結果は `ExperienceRecorder` の記録に残り、撮影は既存の `unity.ps1 replay` がする。走る間は描画と入力の閉ループ、撮る間は開ループ。

**calib は 2 通り**: 既定の `--calib show` は APK と同じ本番の show.json。結末まで通るが、人形はツインの画に合わない（9/7 の会場の値で、9/24 の写真から解いたツインとは別の部屋）。`--calib twin` は人形の姿勢をツインの配置へ読み込み時に差し替える（`ShowControlClient.ApplyTwinCalib`。`ev=config calib=`）。人形は画に合うが、下の理由で結末は中断になる。

**判定**: `analyze-xp-log.py --pc`（実機にしか無い受信 fps・パススルー・タブレットの口などは NA）＋ `twin-verify.py` の周の印と結末。
実機ログで偽 FAIL になる 2 つをこのとき直した。`released=1` の報告に MarkLogged を求めていた。別れの人形（笑わない契約）の標本で「笑っていない」を出していた。

**罠**:
- batchmode では `WaitForEndOfFrame` が再開しない（`XpReplayCapture` にも書いてある）。`EndingFrameCapture` が撮れず、`ready=True` のまま写真待ちで止まり `ending=Interrupted` で終わった。isBatchMode だけ次フレームの頭で撮る。Quest の記録を PC で再生するときの別れもこれで直る
- 導入の練習は案内を読み終える前の押下を受け付けない。1 回押して待つと Tutorial で止まるので、受理されるまで押し直す
- 周の印（束の右下 16px×3 マス）: 録画カットの後ろには次の周の印が映ってよい。録画は区間を出たあとも回る（`ev=rec v=stop` は次の区間の途中）。判定はカットの頭で見る
- 立ち止まりの間、人は歩きの 1 コマで止まって見える。焼いてあるのは順走だけで、逆走と引き返しの画は無い
- 目線の動画は縦 52 度で撮る（紹介動画の既定 72 度だと画面が幅の 3 分の 1 しかない）。視野の縁で起きることは `--fov 72`
- 部屋は黒い（パススルーが無い）。導入の殻の破砕や周縁は確かめられない。後ろ上の動画に画面しか映らないので既定で撮らない（`--follow`）
- `show.json` は束の校正元でもある。`pack.json` の `calibGapPx` が本番 calib とツインの差（A 72.6 / B 31.5 / C 98.8px）

**見つけたこと（2026-11-05）**: [[ending_farewell_doll]] の立ち位置。
- ツインの配置の A（9/24 の写真から解いた (0.60,1.01,-0.61)・水平 96 度）だと別れの人形の立ち位置が解けない。`placement unavailable visitors=325 dolls=6 separated=32 ordered=16 framed=0`。Python の再計算でも同じ 325/6。本番 calib の A (1.04,0.94,-0.99) は 653/147
- 本番 calib でも立つ所の許容は公称の 0.20m より狭い。人形を向いて立つ円周のうち写真が成立する割合は半径 5cm で 19%、10cm で 19%、20cm で 13%（`ev=twinfarewell`）。止めるのは全身が枠に入る条件（`dollV=0.06` `visV=0.07` で下の縁に近い）
- 写真が成立しないと、報告の約 50 秒後に `ending=Interrupted`（`run.endHoldMaxSec`=60 の時間切れと読むのが自然。未確認）
- 立ち位置が解けない間は解き直しが止まらず、`placement unavailable` が 50 秒で 1,438 行出た。`phase=anchoring` と `unavailable` が約 1.5 秒ごとに往復する

関連: [[twin_cycles]] / [[experience_replay]] / [[virtual_walk]]
