---
name: ending
description: 最後の A・締めの線・③a・帰還 End / 人形 End・解除・別れの人形・エンドロールの現行の契約。経緯と旧い記述は reference/archive/ の凍結版
paths:
  - "Assets/Scripts/Diagnostics/ShowEndingFader.cs"
  - "Assets/Scripts/Streaming/**/Ending*.cs"
  - "Assets/Scripts/Streaming/**/Outro*.cs"
  - "Assets/Scripts/Streaming/VisitorReportTally.cs"
  - "Assets/Scripts/Diagnostics/Outro*.cs"
  - "Assets/Tests/**/Ending*Tests.cs"
  - "Assets/Tests/**/Outro*Tests.cs"
  - "Assets/Tests/Streaming/VisitorReportTallyTests.cs"
---

# 最後の A と終幕

**現行の契約だけを書く。** 変わったら行を書き換える（日付の節を足さない・`rules/doc-sync.md`）。
体験者に見えることは `canon/NOW.md`、経緯は `reference/archive/streaming-2026-10-22.md`（S:）・`reference/archive/show-design-2026-10-22.md`（D:）。出典の行番号はその凍結版のもの。

## 契約
- 最後の A は lap 4・camera 0 の固定値で判定する — `TimelineDirector.OnCameraEntered` / `EndingDecisionLogic.Tick` 〔出典: コード〕
- 締めは `run.outro.afterTakeId`（`L4C0#0`）。人形 4 秒 → `untilMark` → `live`。ライブは設定の 7.1 秒を最短とし、写真と退場が済むまで保持する — `TakeRunner.Update` 〔出典: LEDGER 0293〕
- ③a は締めの線を踏む OR 最終 A 確定進入から 3 秒。消費しない。面が空くまで待つ — `CommsCueLogic.HaltAfterClosingSec` / `TheStopCall_WaitsForThePanel_AndIsNeverLost` 〔出典: show-design-2026-10-22.md:1351〕
- 締めの線は `splitFreeze` 直前の `untilLine` の線から導く。別のキーは作らない。締めの中だけで記録し、入る前 0.6 秒の横断も数える — `TakeSchema.ResolveClosingLineId` / `TakeRunnerLogic.ClosingLineCrossed` / `LineCrossLogic.CrossLatchSec` 〔出典: memory/closing_line_halt.md〕
- ③a は打鍵なしで出る。③b は③a を読ませ終わった縁で必ず続く。報告の有無で止まらない — `CommsCueLogic.DeliveryOf` / `TheClosingCut_SaysStopFirst_ThenExplainsWhy` 〔出典: show-design:1382-1392〕
- ③a と③b は視線から 3° 下に、弱った起動で出る（顔と字に赤が残る）。電力を落とさない — `CommsPanel.ClosingPitchOffsetDeg` / `CommsRecoveryLogic` 〔出典: show-design:1309-1320, 1393〕
- 最終 A に入ってから③b を全文表示するまで、報告は件数も解除も受け付けない。締めの頭 4 秒は入力も止める — `ShowControlClient.IsVisitorMarkBlocked` / `TakeRunnerLogic.MarkGraceSec` / `ClosingReportIsRejectedUntilThePromptIsFullyShown` 〔出典: show-design:1375-1380, memory/ending_result.md〕
- Released は `MarkResult.Released` だけで成立する。締めのライブは所定位置での写真成功と人形の退場完了を待ってから自然終了する。写真待ち・退場中はカットの時限と watchdog を進めない。その他の watchdog や打ち切りは Interrupted — `TakeRunnerLogic.Tick(holdCurrentStep)` / `ShowRunDirector.TickEndingDecision` 〔出典: LEDGER 0293〕
- Trapped になるのは、③b を全文表示した後で、報告せずに 15 秒経つ（`ShowClock`。休止中は数えない）か、A の実領域を出るか、確定区間が 4-A 以外になったとき — `EndingDecisionLogic.PromptLimitSec` / `ClosingPrompt_DoesNotTrap_OnTheFirstFrameAfterSleep` 〔出典: show-design:46-53〕
- 退出は `PlayerZoneTracker` の実位置で判定する。追跡が不明な位置と、0.25 秒以上古い位置は使わない — `ShowRunDirector.NotifyClosingAreaPresence` 〔出典: memory/ending_result.md〕
- 報告前の CG 人形の足が左半分（u<0.5）に入ると Trapped。③b を待たない — `EndingDecisionLogic.IsFootOnLeftHalf` / `TakeRunner.CanUseEndingDollFoot` 〔出典: memory/ending_roll.md〕
- 報告と Trapped の条件が同じフレームに重なったら報告を優先する — `ReleasedReportWinsOverLeavingAndTimeoutInTheSameTick` 〔出典: memory/ending_result.md〕
- 終幕の合図は、演出が走っているのを見た後、止まった縁で 1 回だけ出る。介入があれば武装を外す。落ちるのは `BeginRun` だけ — `EndingCueLogic` / `ResetRun_IsTheOnlyWayBack` 〔出典: show-design:930-940〕
- 解除の時刻: 劣化は `ScreenDecayLogic.ReleaseSec`（0.5 秒）で戻り、入れ替わりは 2.6 秒で戻る。続いて④「異変を取り除きました。お疲れ様です。」が出る — `ScreenDecayLogic.ReleaseSec` / `CommsNotice.MarkCleared` 〔出典: show-design:946-958〕
- 解除の縁でスタッフ用の右コントローラを振動させる — `OvrControllerBridge`（`CurseReleasedCount`）〔出典: LEDGER 0156〕
- 台車（Romi）は使わない。Quest は台車へ何も送らない（`RomiCartLink` は 2026-10-30 に削除）〔出典: LEDGER 0291〕
- 帰還の別れの人形: 締めのライブで、入れ替わりが終わって CG 層が空いた縁に、締めの人形（人の代役でない最後の `cg`）を 1 体だけ出す。現れる 0.35 → 見つめる最短 1.15（写真を待つ）→ 後ろを向く 0.75 → 下がって消える 6.0 秒（最後 4.5 秒で消える）。写真に成功するまで回らない。消え切ってから電源断 — `EndingFarewellLogic` / `EndingFarewellDoll` 〔出典: LEDGER 0291・0293。尺は実装判断〕
- L 字壁の内側を 2.5cm 刻みで探し、人物の頭・足・胴と人形がカメラの画角内（縁から 0.5% 以上）に入り、身体の表面から壁へ 4cm 空く 2 点を選ぶ。人体は対面の yaw で回した頭半幅 0.10m・胴半幅 0.25m/厚み 0.13m・足半幅 0.11m/前後 0.20m（身長比例）の一般人体近似で扱う。人形との表面間も 4cm。カメラの intrinsics は変えない。左が人形・右が体験者。床は `room.floorY`。体験者が右の点から 0.20m 以内かつ両者が互いの方向から 35° 以内のときだけ撮る。解けなければ出さない。`pairAccepted` を渡す検証では実CPU頂点の不合格候補を飛ばして再探索する。退場は壁の頂点から人形へ向かう床上の方向 — `TrySolveAtWall` / `IsPhotoReady` 〔出典: LEDGER 0293。余白・許容値は実装判断〕
- 別れの人形は体験者の分身ではない。腕は体験者に合わせず、笑わない（`dollPresent` から外す）。立ち位置が解けるまで強さ 0 — `ShowCgLayer.BeginFarewell` / `ShowSoundDirector`（`FarewellActive`）〔出典: コード〕
- 退場は壁端を回転全身の半径・壁厚・4cm以上の余白で回る折れ線。本番人形の半径既定は CPU bounds 由来の 0.174m（背丈 0.4m 時）。人との距離は単調に増え、終点はカメラからも遠く、投影した背丈は小さくなる経路だけ採る。実道のりの最大速度は 0.35m/s 以下。向きは人とカメラへの平均方向の逆。位置と向きは `PoseAt` を本番・保存プレートで共有し、Doneでも終点を保つ。`trajectoryAccepted` で回転・退場の実CPU壁余白を事前検査し、不合格候補は再探索する 〔出典: LEDGER 0293〕
- 終幕の段は Noise 0.95 → Collapse 0.26 → Dark 1.2 → Report 1.5 → Done（保持 9.5 秒 ＋ 閉じる 4.0 秒）— `OutroTiming.Default` / `Timing_MatchesTheDoctorTabletSignalCut` 〔出典: show-design:806-826〕
- Trapped では砂嵐も潰れも出さない。同じフレームに電力を落とし、Dark から始める — `OutroDirector.Begin` / `Trapped_StartsDarkWithoutShowingLive_ThenResetsForTheNextVisitor` 〔出典: show-design:830〕
- 面と曲は `CloseLevel` 1 本の時計で閉じる。閉じた後は黒のままで、パススルーへは戻さない。閉じている途中も段は Done — `OutroLogic.CloseLevel` / `VisitorPortal.OutroCode` 〔出典: show-design:813-822〕
- 終幕の音は電源断の 1 本だけ。`PowerCutAtSec` で鳴り、段名は読まない — `Outro_FiresOnlyThePowerOff_AndOnlyOnce` 〔出典: show-design:890-898〕
- 尺の許容は砂嵐 0.3〜3 秒、潰れ 0.2〜2 秒。外れた値や欠けたキーは既定値に戻す — `OutroTiming.PickCollapse` / `ShowOutroDef.LooksUnset` 〔出典: show-design:899-907〕
- エンドロールは結末名（帰還End / 人形End / 中断）と「報告数 N」と写真。分母は出さない — `OutroReportText.Title` / `CountLine` 〔出典: memory/ending_roll.md, LEDGER 0290〕
- 報告数は押した回数。表示を始めた時点で固定する — `OutroReport.Capture`（`VisitorMarkCount`）〔出典: LEDGER 0247〕
- 写真はラン内だけ GPU に保持する。撮れなければ代わりの画を入れない — `EndingFrameCapture` / `RunRestartAndDisableDiscardBothVisitorsShots` 〔出典: memory/ending_roll.md〕
- 帰還の写真は別れの人形が見つめる段で、位置と向きが成立した 1 コマを撮る。GPU 合成成功を `NotifyEndingFarewellCaptured` で返した後は上書きしない。条件未成立・人形を出せないときは撮らず再試行を待つ — `EndingFrameCapture` / `EndingFarewellDoll.Facing` 〔出典: LEDGER 0293〕

## 罠（破ると壊れる）
- `ReportedAnomalyCount` と `VisitorReportTally` のコメントは「終幕はこれを出す」と書くが、実装は `VisitorMarkCount` を出す（0247 が 0234 を覆した）。コメントを信じて差し替えない 〔コード〕
- 武装と結末を落とすのは `ShowRunDirector.BeginRun` の 1 か所だけ 〔show-design:940〕
- `afterTakeId` の演出を `dismissible` にすると、終幕が早く撃たれる 〔streaming:2543〕
- 締めのカットを伸ばすと watchdog（48 秒）の猶予が減る 〔show-design:959〕
- 終幕を畳むときは必ず `OutroDirector.ResetScreen` を通す。既定値の向きが逆なので、1 つずつ戻さない 〔show-design:863〕
- `CurseReleasedCount` はラン開始でも 0 に戻らない。増えた縁で見る 〔`OvrControllerBridge`〕
- 未修正: 4-A で左コントローラの接続が切れると帰還不能になる 〔scenario_audit_20261022〕
- 写真待ちを時刻表の `SampleAt` だけで再現しない。`Tick` と撮影成功通知を通す。写真を撮れない場合は Face を保持する 〔コード〕
- 別れの人形は `ShowCgLayer` の 1 体の器を使う。入れ替わりの人形が消える前（`SwapActive`）に出そうとしない 〔コード〕
- 観測 `ev=farewell` / `ev=sum farewell=` は `ShowTelemetryHost` と `analyze-xp-log.py` を対で直す 〔コード〕

## 深く読むとき
- 終幕の段・シェーダ・観測 → show-design-2026-10-22.md:789-929 / memory/ending_result.md
- 合図と 7.1 秒の内訳 → show-design:930-962
- ③a・③b → show-design:1300-1450 / memory/closing_line_halt.md
- untilMark・解除 → streaming-2026-10-22.md:1404-1420, 2495-2560
- エンドロール → memory/ending_roll.md　別れの人形 → memory/ending_farewell_doll.md　台車（不使用）→ memory/romi_cart_esp32.md
