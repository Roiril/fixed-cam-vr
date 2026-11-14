---
name: ending-farewell-doll
description: 帰還の別れの人形。体験者のいる所に合わせて立ち、写真は時間で緩み、奥へ下がって消える。LEDGER 0291・0293
metadata:
  node_type: memory
  type: project
  originSessionId: 2b89953c-e90d-428a-bc15-5060ba8fefc5
  modified: 2026-11-11T01:41:56.761Z
---

2026-10-30 のユーザー図（0293）で、人形は左・体験者は右で向き合う写真になった。0291 で台車（Romi）は不使用。
2026-11-06 にデジタルツインの実レンズで解き直した。旧版は「L字壁の内側の決まった点へ体験者を歩かせる」形で、実レンズの A では解けず中断に落ちていた。

**本番の順序**: 報告 → 入れ替わり（人形→人）→ CG 層が空く → スイの面が閉じるのを待つ（最大 5 秒）→ `BeginFarewell` →
現れる 0.35 秒 → 見つめる最短 1.15 秒（写真未成立なら保持）→ GPU へ写真を撮れた通知 →
後ろを向く 0.75 秒 → 奥へ下がる 6.0 秒（最後 4.5 秒で薄くなる）→ カット自然終了 → 終幕。
`EndingFarewellLogic.Tick` を通さず `SampleAt` だけで描くと写真待ちが抜ける。

**立ち位置**（`TrySolveAtWall`）: 探索は 3 段。
1. 体験者がいま立っている点を固定し、人形の点だけ解く（`Anchor.fromVisitor`）。頭と胸が枠の外なら使わない
2. 全身と壁の余白が入る仮の点で人形を立てる
3. 仮の点で人形だけ先に立てる（体験者の余白を問わない）

体験者を隠れた目標へ歩かせない。`visitorTargetFoot` は仮の点で、写真の判定に使わない。
スコアは `|gap-.75| + |vu.y-du.y| + |du.x-.38|`、仮の点なら `+|vu.x-.65|`、向きが分かれば `+角度差/180`、さらに `+shrink + .5*max(0, gap-終点と体験者の距離)`。
人形は L 字の内側を 2.5cm 刻みで探す。足の枠の判定は `DollFootFrameRadiusM`=0.06m。全身の回転半径 0.174m を足へ使うと、A の画の下端で候補が全部落ちる。
本番 Toy の 45,523 CPU 頂点の bounds から回転半径は 0.17279m、既定 0.174m（背丈 .4m 時）。壁まで 4cm 空ける。
本番の人物身長は追跡した頭の床上高 +0.12m（1.2〜2.1m）。`canStand` は room.floor の外側 0.5m まで。
`pairAccepted` は保存プレート検証で実 CPU 頂点の不合格候補を拒否する。`trajectoryAccepted` は回転と退場の実 CPU 全身の壁余白を事前に測る。

**下がる道**（`SolveRetreats` / `PickRetreat`）: 24 方向、0.05m 刻み、0.2〜1.2m の直線。カメラから遠ざかる内積 ≥.5、L 字の内側、壁余白、終点で全身が枠内、像の高さ ≤.92 倍。立つ点ごとに候補を点数順に持ち、体験者ごとに足元（下腿 0.13m + 人形の半径）へ入らない最初の道を採る。
向きは進む向きとカメラから遠ざかる向きの中間（`AwayYaw`）。背中が画に映る。
⚠ 0293 の図は左の壁端の外へ出る。A では左へ回るとカメラへ近づいて足が画の下へ切れる（旧版が白い顔を見せたのも同じ理由）。図と違うのは実装判断で、未判定。
人と人形の距離は少し縮むことがある。`RetreatM=.45` は名目パラメータで実移動距離ではない。`PoseAt` は本番と保存プレートで共有し、Done でも終点を保つ。

**写真**（`PhotoBlock`）: `order` → `framing` → `visitorYaw` → `dollYaw` の順に見る。
| 見つめ始めから | framing | 人の向き |
|---|---|---|
| 0〜4 秒 | 全身が枠の中 | 互いに 35° 以内 |
| 4〜8 秒 | 頭と胸 | 同じ |
| 8〜15 秒 | 胸 | 問わない |

人形の向きは最後まで見る。実際の位置を壁で判定しない。15 秒で撮れなければ写真なしで下がる（Released のまま）。`shot=0` は `twin-verify.py` が FAIL にする。
`EndingFrameCapture` が合成 GPU 転写に成功すると `NotifyEndingFarewellCaptured` を返す。成功した 1 枚は上書きしない。
batchmode は `WaitForEndOfFrame` が再開しないので次フレームの頭で撮る。
立ち位置が解けない間は見えないまま 1 秒ごとに再試行し、Anchoring と Unavailable の累計 15 秒で Done にして締めを終える（中断へ落とさない）。

**待機と終了**: 帰還ライブの別れが未完了の間は `TakeRunnerLogic.Tick(holdCurrentStep:true)` を使い、
カット時限とwatchdogで終わらせない。写真待ちと退場が済むまでライブが続く。
報告待ちカットは保持しない（保持するとライブへ移る前に停止してしまう）。
固定7.1秒より写真と退場の成功を優先する。中断やランリセットは既存の終了処理を通る。

**人形の器**: `ShowCgLayer` の1体を使う。入れ替わり中に出すと取り合うのでCGが空くまで待つ。
位置が解けるまで強さ0。腕入力は `ShowBodyInput.None`。笑わない。
床への投影影と接地影は通常の人形と同じ。`room.floorY` に足元を合わせる。

**検証**: `EndingFarewellLogicTests`（両レンズの立ち位置・写真の緩み・下がる道）、`TakeRunnerLogicTests`（watchdog を超える待機後の自然終了）。
両レンズの走行は `py -3.11 tools/twin-verify.py --calib twin|show`。保存プレートは `G:\マイドライブ\shubieenders\plate-validation\`。実機 A は移動済みで、実機の画は未確認。

2026-11-06 の最終結果。どれも Released・`placement unavailable` 0 回。
- `twin-verify/20261106_131056`（calib=twin）: PASS FAIL=0。fromV=1、shrink=.78、gap=.76、人形の足 v=.04、人の足 v=-.05。報告から写真まで 12.5 秒（level=2。歩行者が人形を向かない）
- `twin-verify/20261106_132141`（calib=show）: PASS FAIL=0。fromV=1、shrink=.82、gap=.74、人形の足 v=.03、人の足 v=-.04。報告から写真まで 8.5 秒（level=1）
- `plate-validation/20261106_130610_253`: complete=True。立ち位置 (0.51,0.26) から解いて gap=.752m、下がる距離 0.35m、像の高さ 124→95px、距離の最短 0.73m、最大 0.067m/s

⚠ **`plate-validation` は別れだけ体験者を測った立ち位置に置く**（`EndingVisitorXz`）。旧版は全身が枠に入る点を前提にしていて、A の画角では全身の入る組が人形の近くにしか無く、奥へ下がる道が体に当たって解けなかった。別れの体験者は壁で判定せず、頭と胸が枠に入ることを見る（`PhotoLevelFeetOut`）。解けないときは `noVisitorFit= noRetreat= pairRejected= pathRejected=` と道の不合格の理由がログに出る。
⚠ 画で見ると、人形は画の下端に近く小さい。体験者は右に立ち、足は切れ、頭の上も枠で切れることがある。実機は未確認。

**スイの⑤ 別れの一言**（シュビーの案・未判定。`canon/OPEN.md`。文面の経緯は [[comms-reply-spine]]）:
人形が後ろを向き始めた縁（`TimelineDirector.FarewellPhase` が Turn か Retreat）で、解除済み（`ScreenDecayReleased`）なら
`CommsNotice.FarewellFinding`（いまの文面は引用の 1 行「また、あーそぼー」だけ。スイの地の文は付けない）を 1 回出す。
Fade（打鍵 0・新しい効果音なし）、MarkCleared と同じ位置・同じスイの顔（斑 0）。持続表示で、終幕の砂嵐のあいだも残り、
`OutroDirector.PowerCutAtSec` の縁で面ごと即座に消える（結果画面に残らない）。終幕を出さない設定では本編を出た縁で消える。
- 面が閉じるのを待つ `EndingNoticeOpen` には数えない（人形は⑤より先に出ている。数えると人形が待ちへ戻る）
- 出した後は `CommsCueLogic.Tick` が None を返し続ける ＝ 報告の返事（②）で上書きしない。長押しの Guide にも移さない。
  ⚠ 報告そのもの（押した回数・`ev=mark`・コントローラの振動）は止めていない。押した回数は結果画面の「報告数」に入る
- 解析器: `ending=Released` で `ev=farewell phase=turn` があるのに `ev=comms id=FarewellFinding` が無ければ FAIL「別れの解析結果が出ていない」。
  ⑤より後の報告は②の照合から外す（`tools/test-ending-log.py`）
- 外すときは `CommsCueLogic.Tick` の⑤の分岐 1 か所を止める
- 文面は静的ベイク。変えたら `.\tools\unity.ps1 menu hud-font`（`CommsPanel.cs` を入力にする `comms-horror-font` も焼き直し待ちに数えられる）

**台車を外した範囲**: Quest側の `RomiCartLink` / `RomiCartLinkLogic` / テストとテレメトリ。
タブレットの台車パネルと `tools/romi/` とファームは残っている（使わない）。

関連: [[ending-roll]] [[romi-cart-esp32]] [[swap-veil-mask]]
