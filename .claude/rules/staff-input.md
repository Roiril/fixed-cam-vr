---
name: staff-input
description: スタッフの準備と位置合わせ・ゾーン・コントローラの役割・HMD のオンボーディング・回収とリセットの現行の契約。経緯と旧い記述は reference/archive/ の凍結版
paths:
  - "Assets/Tests/**/WalkGuide*Tests.cs"
  - "Assets/Scripts/Diagnostics/HudToggleInput.cs"
  - "Assets/Scripts/Diagnostics/VisitorMarkGuidance.cs"
  - "Assets/Scripts/Diagnostics/RecoveryGuidance.cs"
  - "Assets/Scripts/Streaming/WalkGuide*.cs"
  - "Assets/Scripts/Input/**"
  - "Assets/Scripts/Tracking/**"
  - "Assets/Scripts/OvrBridge/**"
  - "Assets/Scripts/Diagnostics/StaffSetupPanel.cs"
  - "Assets/Scripts/Diagnostics/StatusHud.cs"
  - "Assets/Scripts/Diagnostics/ControllerGuidePanel.cs"
  - "Assets/Tests/Input/**"
  - "Assets/Tests/Tracking/**"
  - "Assets/Tests/Diagnostics/StaffSetupPanelTests.cs"
---

# 準備・入力・来場者（staff-input）

**現行の契約だけを書く。** 変わったら行を書き換える（日付の節を足さない・`rules/doc-sync.md`）。
体験者に見えることは `canon/NOW.md`、経緯は `reference/archive/streaming-2026-10-22.md`（S:）・`reference/archive/show-design-2026-10-22.md`（D:）。出典の行番号はその凍結版のもの。

## 契約
### 入力
- 左は体験者、右はスタッフ。左で読むのは `RawButton.X|Y` だけ（左トリガー・握り・スティック押し込みは読まない） — `OvrControllerBridge.Update` / `TutorialDoesNotAcceptTriggerOrGripBecauseTheyAreNotInputs` 〔出典: show-design:203-211 / LEDGER 0042・0196〕
- 生の入力（接続・追跡・ボタン・被り）は `OvrControllerBridge.ReadRawInput` の 1 か所で読み、`XpReplay.Live` に置く。再生中は記録の値を返す。OVRInput を他所で直接読まない — `RawInput` 〔memory/experience_replay.md〕
- 左 X/Y 1 秒長押しが報告。1 押し 1 回。dt は 0.25 秒で切る — `VisitorMarkHoldLogic.DefaultHoldSec` 〔show-design:281 / LEDGER 0059〕
- Normal: 右 A 2 秒で体験者リセット。右 B は押している間だけステータス（離して 0.3 秒で消える）。右トリガー 2 秒で位置合わせ。2 秒は const — `ControllerModeLogic.Tick` / `OvrControllerBridge.LongPressSec` / `StatusViewLogic.ReleaseGraceSec` 〔show-design:190-192,278 / staff_status_view〕
- Registration: 右 A で点を採る（0.5 秒ホールド平均）。Verify と Review では A がやり直し。右 B は Verify で確定保存、Review で保存せず出る。右トリガー 2 秒でキャンセル — `CourseRegistrationController.Feed` 〔show-design:193-195〕
- 右グリップは読まない。左グリップは開発用の撮影だけ（本番機は `quest-shots.py off`） — `ExperienceShotCapture.Enabled` 〔show-design:196-197〕
- 右 A を押している間と離して 0.3 秒はトリガー長押しを数えない。B は対象外 — `ControllerModeLogic.FaceButtonQuietSec` 〔show-design:387 / staff_status_view〕
- 接続が戻ったら全ボタンを離した次のフレームから受ける。右の追跡が切れたら採点中の点を捨て、A を離すまで再開しない — `ControllerConnectionGate` / `RegistrationSampleInputGate` 〔show-design:1470-1474〕
- 右の振動は const。トリガーと A の頭 0.3 秒は HoldTick を鳴らさない — `HapticSequenceLogic` / `OvrControllerBridge.HoldTickDeadSec` 〔show-design:360-366〕
- 最後の異変を消した縁では右だけ震わせ、左には出さない — `CurseReleasedCount` の増分 → `ControllerHaptics.Closing` 〔LEDGER 0156〕

### 位置合わせとゾーン
- `layout.regPoints` の 2〜5 点を順に採り剛体フィット。最大残差 0.12m 超で全点やり直し — `RigidFit2D` / `ResidualGate_Rejects_ReturnsToCapture` 〔show-design:300-302〕
- 構える高さは `layout.regTouchHeightM`（現場値 1.0m）。床はタッチ y の中央値から引く。ばらつき 6cm 超は警告だけ — `FloorHeightSolver.SpreadWarnM` 〔LEDGER 0184 / show-design:321-337〕
- 登録はトランザクション。B 確定で Commit し registration.json を書く。キャンセルと Review-B は Rollback — `CourseFrame.BeginPreviewSession` / `CourseFramePreviewSessionTests` 〔show-design:309,345〕
- 登録済みなら Review から始まる。B で再利用できるのは `NeedsReRegistration` が無いときだけ — `CourseRegistrationController.ExitReview` / `ReviewConfirmedIsSeparateFromSaved_AndRecenterCannotReuse` 〔staff_setup〕
- OS 再センタリングで要再登録が立ち、再起動しても残る。登録中なら点を捨てて点 1 へ — `CourseRegistrationController.NotifyRecentered` / `NeedsReRegistration_SurvivesAppRestart_UntilConfirmedAgain` 〔unexpected_event_recovery〕
- ゾーンの形は show.json `layout.grid` から決定的に作る（無ければ cuts）。HMD では形を触らない — `ZoneLayoutSolver.ChooseSource` / `ZoneLayoutApplier` 〔show-design:159-162〕
- 現ゾーンは縮めた箱の中なら維持。外れたら priority、同値は配列の先頭。箱は OBB — `ZonePickLogic` / `PlayerZoneSelectionTests` 〔show-design:94-104,344〕
- 歩ける範囲は 1.8×1.8m 〔LEDGER 0017〕〔未照合〕。L 字壁は 2 辺 0.955m — `WALL_ARM`（tools/walk-guide） 〔LEDGER 0183 / l_wall_geometry〕
- 位置合わせ中はパススルーを点け、導入の覆いと黒をどける — `ShowControlClient.CourseRegistrationActive` / `PassthroughStyler` 〔show-design:429-444〕

### 準備・来場者・復帰
- 主画面は博士タブレット。Quest では位置合わせと目視確認だけ。機器確認と並行してよい — `StaffSetupLogic.Stage` 〔show-design:19-21〕
- カメラはデコード成功と /info の cameraId・show 一致が 0.5 秒続いて ok。タブレットは pulse 12 秒以内 — `StaffSetupLogic.StableSec` / `StaffSetupPanel.TabletFreshSec` 〔staff_setup〕
- 来場者は Reset → Settings → Explanation → Ready。初回もリセット必須 — `StaffSetupLogic.VisitorStage` / `FirstVisitorMustResetAfterAlignmentEvenIfOldSettingsLookApplied` 〔staff_setup〕
- 引き渡しは Welcome 段だけで全項目を検査。題字の開始押しでも再検査し、開始は 1 回。開始後はタブレット途絶で止めない — `StaffSetupLogic.HandOff` / `TryBeginExperience` / `PlayingIsNotInterruptedByTabletOrNextVisitorSettings` 〔staff_setup〕
- 右 B 短押しは予備の引き渡し。1 秒押しは準備画面を正面へ戻すだけ — `StaffSetupButtonLogic.RecallSec` 〔show-design:19-21〕
- タブレットのリセットは位置確定・Normal・登録外のときだけ。本編中と終幕中は断る — `StaffSetupLogic.CanResetFromTablet` 〔staff_setup〕
- 強制リセット（pulse の force:true）を断るのは登録中だけ。起動 ID は照合する — `OvrControllerBridge.TryForceResetFromTablet` / `ForceReset_FromAnotherQuestBoot_IsRejected` 〔unexpected_event_recovery〕
- リセットの号令は `ShowControlClient.BeginNewVisitorRunLocal` の 1 か所。工程は `RuntimeHealth.Step` で隔離 〔unexpected_event_recovery〕
- 休止の秒数を体験の時計に入れない — `ShowClock.MaxStepSec` / `Clock_DropsTheSleptSeconds` 〔unexpected_event_recovery〕

### HMD 内の導入
- 練習（X/Y 1 秒）→ 受理の文 → スイの名乗り → 念押し → 題字。練習は件数に入れない — `HmdOnboardingLogic.Tick` / `TutorialComesFirst_ThenAcknowledgementGreetingAndReminderWaitForReading` 〔show-design:27-28〕
- 左が 0.5 秒続けて接続・追跡されるまで練習を数えない。15 秒でスタッフを呼ぶ文へ — `HmdOnboardingLogic.ControllerTroubleSec` 〔hmd_onboarding〕
- 題字は時間で閉じない。X/Y を 0.5 秒以内で押して離すと閉じる。持ち越しと長押しは不可 — `HmdOnboardingLogic.TapMaxSec` / `TitleNeedsANewShortPressAndNeverAcceptsAHold` 〔hmd_onboarding〕
- 位置合わせ中と準備前は導入の時計を止め入力を捨てる — `HmdOnboardingInput.inputBlocked` 〔show-design:28〕
- 言語と軽減はタブレットで決め、HMD では選び直さない。`TitleNotice` は出さない。`VisitorPrefs.ApplyAtTitle` が戻してから載せる 〔show-design:201 / show_language〕
- スタッフ面は日本語だけ。門は `StatusHud.StaffViewing` — `StaffViewing_ClosedByDefault` 〔show_language〕

## 罠（破ると壊れる）
- `Button.Three/Four` を `Controller.LTouch` と組むと常に false 〔show-design:213-218〕
- 右の閾値と振動の値を SerializeField にしない（旧 YAML で 0 と読まれる）。SerializeField を足したら prefab YAML と `StreamingLogicPrefabFieldsTests` を同時に直す 〔show-design:278,315〕
- 接続しているだけで手元の面を出さない。追跡していないアンカーは床の原点に落ちる 〔show-design:1450〕
- HMD の文言を変えたら `menu hud-font`。収集元を足すときは `CollectHudCharset` と `unity.ps1` の `hud-font.Src` の両方に足す 〔show-design:350 / show_language〕
- 卓の ⏭・■・▶ は実機に届かない（`ShowControlClient.BakedOnly`）。現場の出口はタブレットの強制リセットと右 A 〔unexpected_event_recovery〕
- 確定し直したかを要再登録の解除で判定しない（プレビューでも解除される）。見るのは `SavedAtIso` の変化 〔show-design:424-427〕
- `room.walls` が等長でなければ、計算より先に `WALL_ARM` を疑う。`layout.wall`（1.0/1.0）は位置合わせの既定点で別物 〔l_wall_geometry〕
- 点の集合は 4 つ（regPoints・較正の印・通過ライン・開始位置）。兼用しない 〔show-design:177〕
- シーン YAML では角括弧の名前が `'[Tracker]'` とクォートされる。数えるなら script guid で 〔show-design:104-116〕

## 深く読むとき
- 入力割り当ての経緯 → archive show-design:182-292 / memory/controller_input_final.md
- 位置合わせ（Review・床の高さ・可視化・RegViz） → show-design:293-351
- 振動 → show-design:352-394
- ゾーン設計と Pick → show-design:85-181
- タブレットとの照合（pulse・セッション・設定番号） → memory/staff_setup.md / memory/visitor_tablet.md / streaming:3295-3317
- 異常からの復帰 → memory/unexpected_event_recovery.md
- 右 B ステータスの描画（黒の上に出す 3 点） → memory/staff_status_view.md
- 文字の大きさと text-audit → show-design:1484-1613 / memory/hmd_text_style.md
- 準備から回収までの通し撮影 → memory/flow_walk_harness.md
