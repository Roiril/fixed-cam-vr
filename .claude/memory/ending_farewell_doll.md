---
name: ending-farewell-doll
description: 帰還の別れの人形。体験者のいる所に合わせて立ち、写真は時間で緩み、奥へ下がって消える。LEDGER 0291・0293
metadata:
  node_type: memory
  type: project
  originSessionId: 2b89953c-e90d-428a-bc15-5060ba8fefc5
  modified: 2026-11-15T05:20:30.546Z
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
本番 Toy の 45,523 CPU 頂点の bounds から回転半径は 0.17279m、既定 0.174m（背丈 .4m 時）。
壁の面までの余白は、体験者が 4cm（`PhotoWallClearanceM`）、人形は回転の包絡から 2cm（`DollWallClearanceM`。立つ点と下がる道の両方）。
本番の人物身長は追跡した頭の床上高 +0.12m（1.2〜2.1m）。`canStand` は room.floor の外側 0.5m まで。
`pairAccepted` は保存プレート検証で実 CPU 頂点の不合格候補を拒否する。`trajectoryAccepted` は回転と退場の実 CPU 全身の壁余白を事前に測る。

**下がる道**（`SolveRetreats` / `PickRetreat`）: 24 方向、0.05m 刻み、0.15〜1.2m の直線。カメラから遠ざかる内積 ≥.5、L 字の内側、壁余白、終点で全身が枠内、像の高さ ≤.92 倍。立つ点ごとに候補を点数順に持ち、体験者ごとに足元（下腿 0.13m + 人形の半径）へ入らない最初の道を採る。
⚠ 2026-11-17 会場の A（高さ 1.39m・角を正面に見下ろす）では、遠ざかる向きへ 0.15m 進むとすぐ壁の余白を割り、道が 1 本も取れなかった（`noRetreat=22 framed=0`）。人形が出ず、15 秒待って写真なしで終幕へ進んだ（β の実機とツインの両方で同じ）。道が無いときは同じ探索をもう 1 度回し、背を向けた向き（カメラから遠ざかる水平の向き）へまっすぐ歩く道を採る（`WalkAway`・`Anchor.retreatThroughWall`・ログ `through=1`）。壁に当たるなら身体が壁の向こうへ抜け切る所まで（壁に斜めに入るので抜ける量を入射の角度で割る）、枠・縮み・壁の余白は問わない。壁は `ShowRoomProxy` が深度で描くので、越えた身体は画から消える。11-18 の β では 1 度その場で薄くなる形（`inPlace=1`）を見せ、ユーザーが「向きはあってたから、奥までそのまま奥まで進んで消えるのでいい」と言った（0400）。向きが決まらないとき（カメラの真下）だけその場で薄くなる。道が取れる据え方では従来どおり下がる — `Venue_NoFramedRetreat_StillStandsForThePhoto_ThenWalksStraightAwayThroughTheWall`
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

**検証**: `EndingFarewellLogicTests`（9/5 の据え方の本番較正とツインの実レンズ、9/24 の据え方の A の立ち位置・写真の緩み・下がる道）、`TakeRunnerLogicTests`（watchdog を超える待機後の自然終了）。
走行は `py -3.11 tools/twin-verify.py --calib twin|show`。保存プレートは `G:\マイドライブ\shubie\renders\plate-validation\`。実機の画は未確認。

⚠ **A は 2026-11-15 に 9/24 の据え方へ戻った**（show.json とツインの rig が同じ較正。水平 96 度、角 (-0.5,0.5) から水平 1.57m）。
床が画に入るのは奥行き 1.17m より先で、L 字の内側でそこに入るのは角の手前の 0.15〜0.2m の三角だけ。人形の足は必ず画の下端（v≈.04）に掛かる。
壁余白 4cm・道の下限 0.2m のときは人形の候補 28 点の全部で下がる道が壁に当たり、`placement unavailable ... framed=0 noVisitorFit=100 noRetreat=235` で人形が出なかった。
人形の余白を包絡から 2cm、道の下限を 0.15m にして、候補 45 点・道のある点 8 点になった。体験者が (0,0.26) より左だと人形の道が足元に近すぎ、仮の点へ落ちる。
右寄り（x≥0.4）では 1.65m の体験者の頭が画の上で切れるので、写真は 8 秒の段（胸だけ）まで待つ。

2026-11-15 の結果（9/24 の A）。どれも Released・`placement unavailable` 0 回・shot=1。
- `twin-verify/20261115_141439_farewell-fix`（calib=show）: PASS FAIL=0。体験者 (0.507,0.261) から fromV=1、人形 (-0.225,0.125)、終点 (-0.264,0.270)、shrink=.90、gap=.745、人形の足 v=.04、人の足 v=-.24。写真は見つめ始めから 8.0 秒（level=2）
- `twin-verify/20261115_141633_farewell-fix-twin`（calib=twin）: 同じ立ち位置・同じ数字で PASS FAIL=0
- `plate-validation`: 別れより前の `center_idle` で止まる（A の全身の Remy が入る点が 0 個。`PlateValidation` 側の探索）。別れの保存プレートは未確認

⚠ **`plate-validation` は別れだけ体験者を測った立ち位置に置く**（`EndingVisitorXz`）。旧版は全身が枠に入る点を前提にしていて、A の画角では全身の入る組が人形の近くにしか無く、奥へ下がる道が体に当たって解けなかった。別れの体験者は壁で判定せず、頭と胸が枠に入ることを見る（`PhotoLevelFeetOut`）。解けないときは `noVisitorFit= noRetreat= pairRejected= pathRejected=` と道の不合格の理由がログに出る。
⚠ 画で見ると、人形は画の下端に近く小さい。体験者は右に立ち、足は切れ、頭の上も枠で切れることがある。実機は未確認。
⚠ 保存プレート検証の `trajectoryAccepted` は実 CPU の全身で壁まで 4cm を求める。9/24 の A で解ける道は包絡から 2cm で、実寸の向きによっては 3.5cm 前後になるので、そこで落ちうる（未確認）。

**スイの⑤ 別れの一言**（`canon/LEDGER.md` 0363「一瞬人形化したスイが言う」・0365「また、あそぼ」。文面の約束は [[comms-reply-spine]]）:
人形が後ろを向き始めた縁（`TimelineDirector.FarewellPhase` が Turn か Retreat）で、解除済み（`ScreenDecayReleased`）で、
④の 2 枚（「遮断できました」→「データは十分です／お疲れ様でした」）を読ませ終わっていれば、`CommsNotice.FarewellFinding` を 1 回出す。
出方は Cursed（顔は最初から人形・赤い字・打鍵 0）。持続表示ではなく、`HoldSec` + `OutSec` で自分で引く。
引き切る前に電源断が来たら `OutroDirector.PowerCutAtSec` の縁で面ごと消える（結果画面に残らない）。
- 人形は④の 2 枚が読み終わるまで面を待つ。2 枚のあいだに 1 フレーム面が空くので、`CommsPanel` は
  `CommsCueLogic.ClearedClosePending` の間も `EndingNoticeOpen` を立てる。待ちの上限 `EndingFarewellLogic.NoticeWaitMaxSec` は 8 秒
- ⑤そのものは `EndingNoticeOpen` に数えない（人形は⑤より先に出ている。数えると人形が待ちへ戻る）
- 出した後は `CommsCueLogic.Tick` が None を返し続ける ＝ 報告の返事（②）で上書きしない。長押しの Guide にも移さない。
  ④の 1 枚目を出した時点で、以後の報告には返事をしない。
  ⚠ 報告そのもの（押した回数・`ev=mark`・コントローラの振動）は止めていない。押した回数は結果画面の「報告数」に入る
- 解析器: `ending=Released` で `ev=farewell phase=turn` があるのに `ev=comms id=FarewellFinding` が無ければ FAIL「別れの解析結果が出ていない」。
  delivery が Cursed でなければ FAIL。④の 2 枚目（`MarkClearedClose`）より先に出ても FAIL。
  ④の 1 枚目より後と⑤より後の報告は②の照合から外す（`tools/test-ending-log.py`）
- 外すときは `CommsCueLogic.Tick` の⑤の分岐 1 か所を止める
- 文面は静的ベイク。変えたら `.\tools\unity.ps1 menu hud-font` と `menu comms-horror-font`（赤い字は別のアトラス）

**台車を外した範囲**: Quest側の `RomiCartLink` / `RomiCartLinkLogic` / テストとテレメトリ。
タブレットの台車パネルと `tools/romi/` とファームは残っている（使わない）。

関連: [[ending-roll]] [[romi-cart-esp32]] [[swap-veil-mask]]

## 写真と退場の契約（2026-11-15 に rules/ending.md から移した。破ると壊れる）

- 写真は左右の並びと枠と向きで決まる（`PhotoBlock`）。見つめ始めて 4 秒までは全身が枠に入って互いに 35° 以内。4 秒から足が切れてもよい（頭と胸は枠の中）。8 秒から胸が枠に入れば撮り、人の向きを問わない。人形の向きは最後まで見る。15 秒で撮れなければ写真なしで退場し、結末は Released のまま。人形を出せなくても 15 秒で Done。実際の位置は壁で判定しない — `PhotoStrictSec` / `PhotoAnyYawSec` / `PhotoGiveUpSec` / `Photo_LoosensWithTime_ThenTheDollLeavesWithoutIt` 〔実装判断〕
- 退場は L 字の奥へ下がる直線。全身を画に残し、カメラから遠ざかり、像の高さが 0.92 倍以下に縮み、体験者の足元へ入らない道だけ採る。図 0293 の左の壁端の外へは回らない。位置と向きは `PoseAt` を本番・保存プレートで共有し、Done でも終点を保つ — `SolveRetreats` / `PickRetreat` 〔出典: LEDGER 0291。向きと道は実装判断〕
- 帰還の写真は別れの人形が見つめる段で、写真の条件が成立した 1 コマを撮る。GPU 合成成功を `NotifyEndingFarewellCaptured` で返した後は上書きしない。条件未成立・人形を出せないときは撮らず再試行を待つ。batchmode は `WaitForEndOfFrame` が再開しないので次フレームの頭で撮る — `EndingFrameCapture` / `EndingFarewellDoll.Facing` 〔出典: LEDGER 0293〕
