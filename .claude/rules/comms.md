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
- 文面どうしの約束（同じ動詞／空振りと赤い嘘は同じ文／⑤は判定を言わず「声」／⓪a が職掌／③b は「あなたと私を」／侵食中も一人称／⓪c は起動・①は開始／キー名は練習だけ）は `CommsNoticeTextTests` 〔memory/comms_reply_spine.md〕
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
- 返事: 演出中 MarkLogged（消せなければ MarkStuck）/ 空振り MarkNothing / 侵食度≥0.75 では MarkAnalyzing。判定は中継前に凍らせる — `CommsCueLogic.Tick`・`ShowControlClient.LastMarkDetected` 〔memory/comms_reply_spine.md〕
- 締めの報告で `ShowRunDirector.ScreenDecayReleased` が立った同フレームは MarkCleared（侵食度 1 でも Typed） — `CommsPanel.Deliver`・`CommsPanelPossessionTests.ReleasedClosingReplyIsTypedBySuiAtFullInvasion` 〔出典: show-design:1353〕
- ⑤ FarewellFinding は解除済みで別れの人形が Turn（か Retreat）に入った縁に 1 回、Fade・持続表示。出した後のランは返事も Guide も出さず、終幕の電源断で即座に消す。別れの人形の面待ちには数えない — `CommsCueLogic.Tick/FarewellFindingStays`・`CommsPanel.ObserveFarewellFindingEnd`・`CommsCueLogicTests.FarewellFinding_*` 〔シュビーの案・未判定（canon/OPEN.md）〕
- 侵食度は観測でだけ進む: 2-C `pov_0` 0.25 / `pov_1..4` 0.75 / 3-A 人形表示 1。単調、ラン再開でだけ 0 — `CommsInvasionLogic.Observe/Reset`・`CommsInvasionLogicTests` 〔出典: streaming-2026-10-22.md:1432-1440〕
- 乗っ取り前は侵食度に関わらず通常のスイ（斑 0・Typed）。斑と Cursed は `TakeoverDelivered` 後だけ — `CommsPanel.PushCurseTarget/Deliver` 〔出典: show-design:1224〕
- 乗っ取りは 2-C の追いつきの成立で 1 回。成立は人形視点の自然完了か、人形視点が 1 カットでも出た後に前へ抜けた yield（次の区間の頭）。引き返し・watchdog・中止・開始前の前進・卓の介入中・侵食度では発火しない — `CommsCueLogic.HasPendingTakeover`・`TakeRunner.ShouldCountForwardYieldCatchUp`・`CommsCatchUpCompletionTests`・`CommsPossessionLogicTests.InvasionLevelAloneNeverFiresTakeover` 〔memory/comms_takeover.md〕
- 乗っ取りが届いた後の契約（共通時計・主画面の砂嵐・報告の拒否・赤い嘘の返事・警告の字と断片）は [memory/comms_takeover.md](../memory/comms_takeover.md) の「現行の契約」。**あそこが正本**
- ③a は締めのカット中に凍結線通過か 3 秒。面が空くまで待ち必ず出す。Fade・打鍵 0 — `CommsCueLogic.HaltAfterClosingSec`・`CommsCueLogicTests.TheStopCall_WaitsForThePanel_AndIsNeverLost` 〔出典: show-design:46-49, 1387〕
- ③b は③a を読ませ終わった縁で必ず続く。③b までは締めの報告を受けない（別に入って 4 秒の猶予） — `CommsCueLogic.Tick`（closingLocked）・`TakeRunnerLogic.MarkGraceSec`・`CommsPossessionLogicTests.ClosingReportIsRejectedUntilThePromptIsFullyShown` 〔出典: show-design:41, 1375, 1382〕
- ③b は持続表示。自分では引かず、MarkCleared が上書きするか本編を出た縁で消す。長押しを離すと打鍵なしで全文へ戻る — `CommsPanel.ObserveClosingPromptEnd`・`CommsPanelLogic.Restoring`・`CommsPanelLogicTests.ClosingPrompt_*` 〔シュビーの判断・未判定。memory/closing_line_halt.md〕
- ③a/③b の復帰: ③a からの 1 本の時計。起動 0.92s は砂嵐ブロックから復号 → スイが押し返す（1 回滑る）→ ③b の打鍵に結ぶ余震 2 回 → 落ち着く → 報告で 1.2s かけて 0 — `CommsRecoveryLogic.Sample/BootSec/PushEndSec/ReleaseSec`・`CommsPanelLogic.BeginRecovery`
- 呪いは縁から這い込む髪（左・上・右）。焼いた到達順の版 `Resources/CommsFx/CurseCreep*` を量 1 本（`residue`）で切る。地・顔・字・罫線が同じ場。寸法と式は `CommsRecovery.hlsl` の `CommsCreepEval`・`CommsCreepLogic`・`tools/comms-creep/bake.py` の 3 つが同じ値 — 直すときは 3 つとも
- 髪の下の字は画素ごとに朱（`CommsRecoveryText.shader`）。命令の句は象牙のまま（帯 `_RecoveryGuard` を `ApplySplitTruthMesh` が毎フレーム組む）。朱の字数は `CommsCreepLogic.Depth` で数える 〔memory/comms_curse.md; LEDGER 0350〕
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

経緯の地図（文面・面・斑・乗っ取り・言語・音がどこにあるか） → [reference/comms-deep-reading.md](../reference/comms-deep-reading.md)
