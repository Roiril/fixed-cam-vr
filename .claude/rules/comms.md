---
name: comms
description: エージェント画面（スイ）・会話と報告への返事・呪いの侵食・乗っ取り・③a/③bの現行の契約。経緯と旧い記述は reference/archive/ の凍結版
paths:
  - "Assets/Scripts/Streaming/Comms*.cs"
  - "Assets/Scripts/Streaming/UnauthorizedAccessEffect.cs"
  - "Assets/Scripts/Streaming/AlertEscalationLogic.cs"
  - "Assets/Scripts/Streaming/HorrorRelief.cs"
  - "Assets/Scripts/Streaming/ShowLanguage.cs"
  - "Assets/Scripts/Diagnostics/Comms*.cs"
  - "Assets/Scripts/OvrBridge/CommsPerformanceProbe.cs"
  - "Assets/Scripts/Streaming/Editor/Comms*.cs"
  - "Assets/Tests/**/Comms*.cs"
  - "Assets/Tests/Streaming/UnauthorizedAccessEffectTests.cs"
  - "Assets/Tests/Streaming/TakeoverStaticTests.cs"
  - "Assets/Art/Shaders/Streaming/Comms*"
  - "Assets/Resources/Shaders/Comms*.shader"
  - "Assets/Art/Shaders/UnauthorizedAccessFx.shader"
---

# エージェント画面（スイ）

**現行の契約だけを書く。** 変わったら行を書き換える（日付の節を足さない・`rules/doc-sync.md`）。
体験者に見えることは `canon/NOW.md`、経緯は `reference/archive/streaming-2026-10-22.md`（S:）・`reference/archive/show-design-2026-10-22.md`（D:）。出典の行番号はその凍結版のもの。

## 契約
- 判断 `CommsCueLogic`／文面 `CommsPanel.TextFor`（ja/en/fr）／出方 `CommsCueLogic.DeliveryOf` は各 1 か所 〔出典: show-design-2026-10-22.md:1341〕
- 面は 1.5m・外 8°・下 17°、頭のヨーだけ追従。③a/③b だけ下 3°。倍率 2/3 は根 1 か所 — `CommsPanel.DistanceM/YawOffsetDeg/PitchOffsetDeg/ClosingPitchOffsetDeg/Scale` 〔出典: show-design:1139, 1393〕
- 畳まれている間は追従の種を捨て、頭の正面に開く — `CommsPanelFollowTests.WhileFolded_TheFollowSeedIsDropped` 〔出典: show-design:1086〕
- 地は黒い半透明 0.72、乗っ取り中だけ 0.96 — `CommsPanel.PanelAlpha/TakeoverPanelAlpha/MakeTranslucent` 〔出典: show-design:1114; memory/comms_takeover.md〕
- 左の 0.15m 角にスイの顔、人形は斑で重ねる。枠線なし。文面の帯の右端は動かさない — `CommsFaceLayout.CellM/StrokeK`・`CommsFaceLayoutTests.FaceFrame_IsRemoved` 〔出典: show-design:1160; memory/comms_face.md〕
- 位置合わせ・スタッフ準備中は面を隠し時計を止める — `CommsPanel.RegistrationHidden`・`CommsPanelPossessionTests.RegistrationHidesAndPausesAnActiveNoticeUntilExit` 〔出典: show-design:40〕
- 導入は接続確認 → 練習 → 成功「報告できました。装置は正常です。」→ 名乗り → Reminder。練習系は Fade（打鍵 0） — `HmdOnboardingLogic.Prompt`・`CommsPanel.SetOnboardingNotice`・`CommsNoticeTextTests.GreetingIntroducesSuiAfterTheTutorial` 〔出典: show-design:39; memory/hmd_onboarding.md〕
- 題字後は Walk（床の矢印と同じ行で出す・18 秒おきに最大 2 回再送）→ 段 0 を抜けた縁で Arrived を上書き — `CommsCueLogic.TickIntro/WalkRepeatSec/WalkRepeatMax`・`CommsPanel.Deliver`→`WalkGuide.NotifyExplaining` 〔出典: show-design:627-660〕
- 本編は Begin のみ。BeginHow は出さない（`ResetRun` が `_beginHowFired=true`）。Begin は押しのけられても消費しない — `CommsCueLogicTests.EnteringTheMainRun_DoesNotRepeatTheHowTo` 〔出典: show-design:1338-1353 の表は旧。コードが正〕
- 打つ速さ ja 12 / Latin 18 字/秒、1 字 1 発、上限 7 秒 — `CommsPanelLogic.CharsPerSecFor/MaxTypeSec`・`CommsNoticeTextTests.EveryNotice_TypesAtTheDeviceSpeed` 〔出典: show-design:1037, 1445〕
- 読ませる尺 Typed 2.0 / Fade 1.1 / Possessed 1.35、引く 0.9 — `CommsPanelLogic.HoldSecFor/OutSec` 〔出典: show-design:1338-1350〕
- 続く文面は同じ面で差し替え（旧文 0.15s → 枠 0.2s → 1 字目）。乗っ取りは差し替えない — `CommsPanelLogic.SwapTextOutSec/SwapBandSec/TakeSwapCommit` 〔出典: show-design:1121-1135〕
- 長押しの縁で面は Guide へ（文面 0.1s で消して丈だけ縮む） — `CommsPanelLogic.SetGuideWanted/GuideTextOutSec` 〔出典: show-design:1425〕
- 返事: 演出中の報告 MarkLogged / 空振り MarkNothing / 侵食度≥0.75 の空振り MarkAnalyzing。判定は中継前に凍らせた値 — `CommsCueLogic.Tick`・`ShowControlClient.LastMarkDetected` 〔出典: memory/comms_takeover.md〕
- 締めの報告で `ShowRunDirector.ScreenDecayReleased` が立った同フレームは MarkCleared（侵食度 1 でも Typed） — `CommsPanel.Deliver`・`CommsPanelPossessionTests.ReleasedClosingReplyIsTypedBySuiAtFullInvasion` 〔出典: show-design:1353〕
- 侵食度は観測でだけ進む: 2-C `pov_0` 0.25 / `pov_1..4` 0.75 / 3-A 人形表示 1。単調、ラン再開でだけ 0 — `CommsInvasionLogic.Observe/Reset`・`CommsInvasionLogicTests` 〔出典: streaming-2026-10-22.md:1432-1440〕
- 乗っ取り前は侵食度に関わらず通常のスイ（斑 0・Typed）。斑と Cursed は `TakeoverDelivered` 後だけ — `CommsPanel.PushCurseTarget/Deliver` 〔出典: show-design:1224〕
- 乗っ取りは 2-C 最後の人形視点の自然完了で 1 回。中止・素材欠落・卓の介入中・侵食度だけでは発火しない — `CommsCueLogic.HasPendingTakeover/NotifyDelivered`・`CommsCatchUpCompletionTests`・`CommsPossessionLogicTests.InvasionLevelAloneNeverFiresTakeover` 〔出典: show-design:1218〕
- 乗っ取りの共通時計: 0 WARNING / 1 不正アクセス / 2 接続元不明・面が開く / 2.57 スイ「異常を検知しました」一気表示 / 3 遮断ラベル＋空バー / 4.17〜5.67 バー充填と左→右の人形侵食が同期 / 5.92 赤い失敗 / 7.02 面と警告と空間エラーを同時消去。長文は充填開始を両方同じだけ遅らせる — `CommsPanelLogic.TakeoverFillStartSec/FailureAtSec`・`CommsPossessionLogic.SweepSec`・`CommsTakeoverErrorTests.RealBarProgressMatchesPanelSweepAndBothCloseInSameFrame` 〔出典: show-design:1219-1221; memory/comms_takeover.md〕
- 乗っ取り中は主画面を砂嵐、同時消去で解除 — `CameraFeelFx.BeginTakeoverStatic/CancelTakeoverStatic`〔出典: memory/comms_takeover.md 2026-09-25 節〕
- 発火待ち〜消去まで報告を拒否（数えない・後送りしない・離すまで再開しない） — `CommsPanel.TakeoverInputBlocked`・`CommsPossessionLogicTests.BlockedReport_DoesNotCountOrResolveAnything` 〔出典: memory/comms_takeover.md〕
- 乗っ取り後の返事は Cursed: 人形の顔と赤い `CommsPanel.TakeoverLieText`（CommsHorror SDF）だけ。走り書きなし — `CommsPanelPossessionTests.CursedReplyStartsAsTheRedLieWithoutSweepOrKeystrokes` 〔出典: show-design:1223〕
- 警告は明朝＋かすれ（v5）・朱と象牙。順・秒は不変 — `UnauthorizedAccessEffect.SampleTakeover`・`UnauthorizedAccessFx.shader` 〔出典: memory/comms_takeover.md 2026-10-21 節〕
- ③a は締めのカット中に凍結線通過か 3 秒。面が空くまで待ち必ず出す。Fade・打鍵 0 — `CommsCueLogic.HaltAfterClosingSec`・`CommsCueLogicTests.TheStopCall_WaitsForThePanel_AndIsNeverLost` 〔出典: show-design:46-49, 1387〕
- ③b は③a を読ませ終わった縁で必ず続く。③b までは締めの報告を受けない（別に入って 4 秒の猶予） — `CommsCueLogic.Tick`（closingLocked）・`TakeRunnerLogic.MarkGraceSec`・`CommsPossessionLogicTests.ClosingReportIsRejectedUntilThePromptIsFullyShown` 〔出典: show-design:41, 1375, 1382〕
- ③a/③b の復帰: ③a からの 1 本の時計。起動 0.92s は砂嵐ブロックから復号、③b の打鍵に結ぶ余震 2 回、静止した粗い斑で人形と赤が約半々、報告で 1.2s かけて 0 — `CommsRecoveryLogic.Sample/BootSec/ReleaseSec`・`CommsPanelLogic.BeginRecovery`・`CommsRecovery.hlsl` の `COMMS_RECOVERY_NOISE_PHASE` 〔出典: show-design:1309-1320; memory/comms_curse.md; memory/comms_takeover.md 2026-10-18 節〕
- 言語 `ShowLanguage.Current` と軽減 `HorrorRelief`（音だけ 0.5 倍）はタブレットの値を `VisitorPrefs` が入れ、体験者交代で `TitleScreen.BeginTitle` だけが戻す。HMD 内で選ばない 〔出典: show-design:1052-1060; memory/show_language.md〕

## 罠（破ると壊れる）
- `Unlit/Color` は実機ビルドに入らない。URP Unlit を引き `_BaseColor` と `_Color` の両方へ書く。観測 `commsBg` 〔show-design:1328-1336〕
- 実行時 `Shader.Find` のシェーダは Always Included から外すと実機だけ無地になる（Editor では出る） 〔memory/comms_curse.md〕
- 文字のステンシルを同じシェーダの第 2 パスにしない。bit 8 を `fontMaterials` 全部へ 〔memory/comms_curse.md〕
- 文面と `CommsPanel.cs` のコメントを変えたら `menu hud-font`、赤い文は `menu comms-horror-font` 〔memory/comms_curse.md; memory/comms_takeover.md〕
- 赤は `.linear` で渡す。忘れると橙になる — `CommsPanel.LieRed` 〔memory/comms_takeover.md〕
- 乗っ取りの段境界は `CommsPossessionLogic.Sample` で共有し、呼び手で秒を組み直さない 〔memory/comms_clock_boundaries.md〕
- 打鍵しない出方は `NoticeChars=0`。字数のままだと解析器が落とす 〔show-design:1387〕
- `Begin` / `BeginHow` の名前は走行ログの綴り。入れ替えない 〔show-design:652〕
- `TakeoverInputBlocked` は面が組めていないとき拒否しない 〔CommsPanel.cs の doc〕
- 復帰用の文字シェーダは差し戻しても描画順を保つ。保たないと次の人の文字が地の下に沈む — `CommsPanelPossessionTests.RecoveryShaderSwapKeepsTheGlyphQueue_IntoTheNextRun` 〔LEDGER 0279・コミット b248481b〕
- 観測は `ShowTelemetryHost` と `tools/analyze-xp-log.py` を対で直す 〔show-design:1443〕

## 深く読むとき
- 文面・導入・①〜④の経緯 → archive show-design-2026-10-22.md:627-660, 982-1072, 1338-1450 / memory/hmd_onboarding.md
- 面の寸法・半透明・差し替え・顔 → show-design:1073-1214 / memory/comms_face.md
- 斑・前線・ステンシル → show-design:1240-1320 / memory/comms_curse.md
- 乗っ取りと不正アクセス表示・音 → show-design:1215-1236 / memory/comms_takeover.md / memory/unauthorized_access_effect.md
- 段境界のテスト → memory/comms_clock_boundaries.md
- 警告中の fps → memory/comms_performance_20261018.md
- 言語 → show-design:1009-1072 / memory/show_language.md
- 警告音の育ち（`AlertEscalationLogic`）・軽減モードの音 → `rules/sound-design.md`（この rule の対象外）
