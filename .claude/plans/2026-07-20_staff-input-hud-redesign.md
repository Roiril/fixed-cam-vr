# スタッフ操作の右手 4 入力集約 + HMD 表示の単一サーフェス再設計

日付: 2026-07-20 / 状態: **implemented（コンパイル・実機未検証。2026-07-21 実装）**
対象: 廻リ視（FixedCam）のみ。`Assets/Scripts/`（`FixedCamVr.*`）。TableDuo 非対象。

## 実装メモ（2026-07-21・確認点 4 つ確定反映）

確認点への回答: ①スクリーン追従凍結はコントローラから削除（`ScreenAnchor.Toggle()` と Editor Space キーは残置）/
②Normal の A=カメラ Next を残す / ③StatusHud に `autoHideSec`（既定 0=無効・登録ガイダンスには非適用）/
④Registration スティックナッジは廃止（コードごと削除）。

- **入力**: `ControllerModeLogic` を 2 状態（Normal/Registration）へ全面書き換え。長押し検出（トリガー/グリップ 2 秒・
  1 ホールド 1 発火ラッチ）を純ロジック内 `HoldLatch` に持たせテスト化。`RunResetRequested` イベントを追加。
  `OvrControllerBridge` は右手 4 入力のみ読む（左手・スティック・cue 試射・チートシート撤去）。長押し閾値は const `LongPressSec=2f`。
- **表示**: `RuntimeDebugHud` / `StaffPanel` を削除し **`StatusHud`（Diagnostics）**へ統合。緩追従（`YawFollowLogic` 流用・
  deadzone+SmoothDamp）。配置/追従/オートハイドは全 SerializeField。診断詳細（FPS/HMD 座標/DISC）は退役し HudLogDumper +
  heartbeat が担う。`HudLogDumper` は変更なし（`Diagnostics` コンテナへ移設のみ）。
- **登録**: `CourseRegistrationController` はスティックナッジと自前 TextMesh ガイダンスを削除し、`GuidanceText`/`GuidanceColor`
  プロバイダを公開（StatusHud が読む＝Tracking→Diagnostics の asmdef 依存を作らない）。要再登録警告は StatusHud が CourseFrame 直読み。
- **次の cue 予定**: `CueScheduleLogic.TryGetNext(lap,pos,order)` を新設（timeline/legacy 両経路とも有効エントリは
  CueScheduler に集約されるため 1 点で両対応）。`LapCounter.Position`/`Order` を公開。TimelineDirector 側 API は不要と判断。
- **heartbeat mode**: 値を NORMAL/REG に整合（キー名 `mode` は不変。Web 卓は raw 表示のみで equality 依存なし）。
- **仕様から変えた点**: StatusHud に `recenterAutoShowSec`（既定 5s）を追加（「発生時オートショウ」の秒数を SerializeField 化）。
  MainDemoSceneSetup は StatusHud を world-space（Logic 直下・head 非親）に置き、診断系を別 `Diagnostics` コンテナへ分離。

## 追記: コントローラ触覚（振動）フィードバック（2026-07-21 実装・実機未検証）

**動機**: 押下フィードバックが HMD 内の視覚（StatusHud）しかなく、体験者が HMD を被っている場面では
スタッフに何も見えない。「うまく押せていないのか、コントローラ未接続か」を区別できない。→ 右コントローラの
**振動**で操作の受理・進行・発火・失敗を伝え、StatusHud にコントローラ接続状態を表示する。

- **純ロジック** [`HapticSequenceLogic`](../../Assets/Scripts/Input/HapticSequenceLogic.cs)（FixedCamVr.Input・
  UnityEngine 非依存・「経過時間→振幅」）+ EditMode テスト [`HapticSequenceLogicTests`](../../Assets/Tests/Input/HapticSequenceLogicTests.cs)。
  **MonoBehaviour** [`ControllerHaptics`](../../Assets/Scripts/OvrBridge/ControllerHaptics.cs)（Assembly-CSharp）が
  毎フレーム `OVRInput.SetControllerVibration` を RTouch へ適用（非再生時も (0,0) を送って自動停止を保証）
- **ボキャブラリ**（両アプリ共通・freq 0.5 固定）: Ack=40ms/0.25（受理）・Action=80ms/0.5（アクション実行）・
  HoldTick=連続 0.10→0.30 ランプ（長押し進行）・Fire=80ms×2/0.8（発火・モード遷移・確定保存）・
  Error=50ms×3/0.6（失敗）。数値は const（旧シーン YAML で 0 に読まれる罠回避）
- **重畳優先度（固定仕様）**: 単発は「ピーク厳密大の後着だけ差し替え、同ピーク・低ピークは再生中なら無視」。
  → Ack→Action 昇格 / Fire 中 Ack 無視 / **確定保存の二重 Fire（RegistrationConfirmed と ModeChanged）が 1 回に畳まれる**。
  HoldTick は床として単発と max 合成
- **配線**: `OvrControllerBridge` が down エッジ→Ack / Normal アクション実行→Action / 長押し進捗（trigger|grip の大）→
  SetHoldProgress / ModeChanged・RunReset→Fire。登録の節目は `CourseRegistrationController` に新設した
  `PointCaptured`（→Action）/ `FitRejected`（→Error）/ `RegistrationConfirmed`（→Fire）イベントを Bridge が購読
  （Tracking asmdef は OVRInput 非依存を維持）。`haptics` は null 許容（未配線でも全機能が従来通り動く）
- **接続表示**: `StatusHud.SetControllerConnected(bool)` を追加、4 行目に `Rコン●`/`⚠Rコン未接続`。
  Bridge が `OVRInput.IsControllerConnected(RTouch)` を毎フレーム push（Diagnostics は OVRInput 非依存のため直読み不可）
- **Setup**: `MainDemoSceneSetup` が `[Streaming]` に ControllerHaptics を冪等 get-or-add（Assembly-CSharp 型のため
  reflection で解決）し `OvrControllerBridge.haptics` へ結線 → **Setup Main Demo Scene の再実行が必要**
- **仕様判断**: 計画の「Registration の A=サンプル開始 → Action」は、0.5s ホールド平均が**失敗し得る**ため
  「A 押下＝Ack（受理）／ 0.5s 静止で点確定＝Action（PointCaptured）」に解釈した（Ack/Action の定義に厳密に一致）
- **⚠ 実機未検証**（振幅・波形の体感、Rコン未接続表示は現場調整前提）

## 追記: 操作ガイドパネル新設・StatusHud 日本語化（2026-07-23 実装・実機未検証）

**動機**: ①コントローラはスタッフ専用だが、現在モードでどのボタンが何をするかが手元に出ておらず暗記頼み。
②StatusHud の本文が記号混じり（`[NORMAL] lap 2 | zone B (cam2) / 信号 ●●○ ...`）で、現場スタッフに直感的でない。

- **ControllerGuidePanel（新規・[`Assets/Scripts/Diagnostics/ControllerGuidePanel.cs`](../../Assets/Scripts/Diagnostics/ControllerGuidePanel.cs)）**:
  右コントローラの「少し上・少し奥」に現在モード（NORMAL/REG）の操作早見表を**常時表示**する小パネル。
  OVRInput 非依存（Diagnostics asmdef）。配置は LateUpdate で `コントローラ位置 + up*heightOffset(0.12) +
  (頭→コントローラ水平単位)*awayOffset(0.06)` を smoothTime(0.15) SmoothDamp、回転は頭へ billboard（手首回転に非追従）。
  API: `SetMode("NORMAL"/"REG")` で本文切替 / `SetControllerConnected(bool)` で未接続時に非表示。フォントは
  `JapaneseHudFont.TryGet()`。本文は 4 行（NORMAL）/3 行（REG）の平文操作説明。
- **OvrControllerBridge**: `guidePanel` SerializeField を追加。`PushModeLabel` でモードを push、Update で接続状態を
  StatusHud と並べて push（`haptics` 同様 null 許容）。
- **StatusHud.BuildStatus 全面日本語化**: `2周目 ・ いまの場所: B ・ 表示中: カメラ2` / `次の演出: 3周目 カメラ1 「cue_A_1」` /
  `カメラ映像: ①● ②● ③○` / `⚠映像が届いていません（砂嵐表示中）`等 / `位置合わせ: 済み（ずれ 0.05m）` /
  `右コントローラ: 接続中` の 6 行構成。丸数字は U+2460 起点（21 台以降は `N:` フォールバック）。`[NORMAL]` 行頭
  プレフィックスは廃止（REG 中は登録ガイダンス強制表示で自明）。`SetModeLabel` API + 保持値（`ModeLabel` getter 追加）と
  heartbeat 連携は維持。StringBuilder append 主体・GC ゼロ・updateInterval 間引きは不変。
- **MainDemoSceneSetup**: `ControllerGuidePanel` を冪等生成（DeleteIfExists → CreateControllerGuidePanel →
  RightHandAnchor/CenterEyeAnchor 配線 → OvrControllerBridge.guidePanel 結線）。見た目は CreateStatusHudVisual を踏襲した
  world-space Canvas + TMP（sizeDelta 560×300 / scale 0.0005 = 0.28m 幅・fontSize 26・左寄せ）。**Setup Main Demo Scene の
  再実行が必要**。RightHandAnchor 不在時は生成スキップ（追従先が無いと常時非表示のため）。
- **⚠ Quest 実機未検証**（パネルの距離・大きさ・billboard の読みやすさ、StatusHud 新文言の可読性・行数は現場確認前提）。

## 追記: 位置合わせフィードバック改修（2026-07-21 実装・実機未検証）

**動機**: 登録フローの手応え不足を埋める。①0.5 秒ホールドの進行が見えない・鳴らない、②中断/成功の触覚が非対称、
③Verify の残差が Debug.Log だけで HMD に出ない、④確定後にズレを確認する道がなく「一度確定したら不安なまま」、
⑤登録の有無・鮮度・品質がどこにも見えない。

- **サンプルホールド進捗**: `CourseRegistrationController.SampleHoldProgress01`（非サンプル中 0）を公開。
  `OvrControllerBridge` が mode 長押し進捗と **Max 合成**して `haptics.SetHoldProgress` へ（0.5 秒ホールド中も HoldTick ランプが鳴る）。
  ガイダンス 1 行目を進捗バー付き `計測中 ▓▓▓░░ 0.3/0.5s` に（毎フレーム更新）。
- **触覚の対称化**: 新イベント `SampleAborted`（0.5 秒未満リリース → Error）/ `FitAccepted`（N 点残差通過 → Fire。
  `FitRejected`=Error と対称）を追加、Bridge が購読（Tracking asmdef は OVRInput 非依存を維持）。
- **Verify 残差表示**: 1 行目に `最大残差 0.05m（合格 ≤0.12m）` を追加（`_verifyMaxResidualM` を SolveAndVerify で確定）。
- **確認（Review）フェーズ新設**: `Phase.Review` を追加。登録開始時に `CourseFrame.HasRegistration` なら Capture でなく
  Review に着地（既存登録のワイヤー + フットプリント + `登録済み…（保存/残差/点数）` ヘッダ・recenter 時は橙警告）。
  A=点 1 から再登録 / B=保存せず終了（`RegistrationConfirmed` を発火しない）。**`ControllerModeLogic` は変更不要**
  （Review も `IsActive` = `_phase != Idle` で真、入場/退場は既存のトリガー長押し・IsActive 追従でそのまま成立）。
- **品質・鮮度の永続化**: `registration.json` に `maxResidualM` / `pointCount` / `savedAtIso` を追加（JsonUtility 欠損 default で
  後方互換）。`CourseFrame` が `HasRegistration`/`MaxResidualM`/`PointCount`/`SavedAtIso` を公開。StatusHud のステータス行に
  `⚠未登録` / `登録済(残差0.05m)` バッジ（`⚠要再登録` 優先）を追加。
- **純関数 + テスト**: 文言フォーマットを `RegistrationGuidance`（UnityEngine 非依存・進捗バー/残差/Review ヘッダ）へ切り出し
  `RegistrationGuidanceTests`（9 件）を追加。
- **Setup 再実行は不要**: 新 SerializeField を足していない（品質は private state・StatusHud は既存 courseFrame 参照・
  Bridge のイベント購読は Start のコード）。MainDemoSceneSetup 変更なし。
- **⚠ Quest 実機未検証**（進捗バー/触覚の体感・Review 着地・残差表示は現場確認前提）。

## 背景（ユーザー要求 2026-07-20）

- Quest コントローラのスタッフ操作がわかりづらい。**既存割当を全解除**し、**右コントローラの A / B / グリップ / トリガーの 4 入力だけ**で全操作を賄う
- 体験設計が事前焼き込み（show.json v2 timeline）に変わったので、**その場での合成発火（cue 試射）等は不要**。コントローラ説明表示も 4 入力なら不要
- Run/Staff/Registration のモード切替の必然性を疑う（**体験者はコントローラを持たない**ので封印モードが守る相手がいない）
- 必要なのは「位置合わせボタン」と「今タイムラインのどこかが分かるログ」程度
- 表示系の不満: コントローラ説明が「でかすぎ・近すぎ・目線に追従して読めない」、HUD は「重なる・上すぎ・追従して読めない」→ **出し方から根本再設計**

## 調査で確定した現状（opus 2 体の棚卸し・親で裏取り済み）

### 入力（すべて OvrControllerBridge.cs 経由。キーボード系は Editor 専用で実機では死んでいる）

| モード | 入力 | 機能 |
|---|---|---|
| Run（既定・封印） | 両グリップ 3 秒 | Staff へ |
| Staff | 右 A / 右 B | カメラ Next / Prev |
| Staff | 左 X | スクリーン追従凍結（ScreenAnchor.Toggle） |
| Staff | 左 Y | 診断 HUD トグル |
| Staff | 左スティック押込 | ランリセット |
| Staff | 右グリップ単押し | cue 試射（ToggleActiveCameraCue） |
| Staff | 右スティック押込 | Registration へ |
| Staff | 無操作 120s / 両グリップ 3s | Run へ |
| Registration | 右 A / 右 B | 点サンプル / 確定 |
| Registration | 左スティック / 右スティック横 | 平行移動 / yaw ナッジ |
| Registration | 両グリップ 3s | キャンセル |

- **右 IndexTrigger（トリガー）は全コードで未使用**（grep 0 件・裏取り済み）＝空きスロット
- モード状態機械は `ControllerModeLogic.cs`（純ロジック・`Assets/Tests/Input/ControllerModeLogicTests.cs` あり）
- 焼き込み運用が代替する: 手動カメラ切替（Zone 自動）、cue 発火（CueScheduler/TimelineDirector）。Web 卓にも cameraOverride / activeCue 経路あり
- ローカルに残さざるを得ない: **位置合わせ**（HMD 物理タッチ必須・代替なし）、**ランリセット**（PC 不在時の体験者交代の唯一手段）

### 表示（構造問題）

| 表示物 | 配置 | 問題 |
|---|---|---|
| RuntimeDebugHud | CenterEyeAnchor 直子 **0.7m**・0.6×0.4m・**TopLeft 揃え**・剛体 head-lock・**全ハードコード**（MainDemoSceneSetup.cs:424-465） | 「上すぎ・追従して読めない」の実体 |
| StaffPanel（操作チートシート 8 行） | 直子 **1.1m**・characterSize 0.014・剛体 head-lock（StaffPanel.cs:22,75-79） | 「でかすぎ・近すぎ」の実体 |
| CourseRegGuidance | 直子 **1.2m**・characterSize 0.02・剛体 head-lock・ハードコード（CourseRegistrationController.cs:423-450） | Registration 中 HUD が消えず**前後重畳** |

- 3 者が互いを知らず独立配置（レイアウト調停なし）。Staff 中は HUD+Panel 同時、Registration 中は HUD+Guidance 重畳
- **テキスト UI だけが剛体 head-lock**（映像スクリーン ScreenAnchor は deadzone+SmoothDamp の緩追従を既に持つ）
- TMP（HUD）とレガシー TextMesh（Panel/Guidance）の 2 系統混在
- 進行状況（lap/zone/cam/cue）は heartbeat（ShowControlClient.cs:1078-1099）に全部揃っているが HMD に出していない
- CurrentSourceLabel は実装済み・未配線（死蔵）

## 新設計

### 1. 入力 — モードは 2 状態・右手 4 入力のみ

**状態: Normal / Registration**（Staff モード廃止。体験者がコントローラを持たない前提で封印不要。
両グリップ 3 秒儀式・120s idle timeout・スティック・左手の読み取りをすべて撤去）

| 状態 | 入力 | 機能 | 誤爆対策 |
|---|---|---|---|
| Normal | **A 短押し** | カメラ手動送り Next（設営・リハ確認用） | 誤爆しても Zone 自動が 8s で復帰（既存挙動） |
| Normal | **B 短押し** | ステータス表示トグル（新 StatusHud） | 無害 |
| Normal | **グリップ 2 秒長押し** | ランリセット（体験者交代） | 長押しで保護 |
| Normal | **トリガー 2 秒長押し** | 位置合わせ（Registration）入場 | 長押しで保護 |
| Registration | **A** | 点サンプル（0.5s ホールド平均・現行同様）/ Verify 中: やり直し | — |
| Registration | **B** | Verify 確定・保存・退場 | — |
| Registration | **トリガー 2 秒長押し** | キャンセル退場（入場と対称） | — |
| Registration | グリップ | 未使用（予備） | — |

**削除する機能**:
- cue 試射（右グリップ単押し → ToggleActiveCameraCue 呼び出し）。Web 卓 activeCue / /command 経路は残す
- StaffPanel（操作チートシート）ごと削除
- Registration のスティックナッジ（平行移動・yaw）。N 点剛体フィット + 残差ガード 0.12m が精度を担保し、やり直しが約 10 秒で安いため
- カメラ Prev（Next だけで一周できる。B をステータストグルに譲る）

### 2. 表示 — 単一サーフェス StatusHud + 緩追従

**原則: HMD 内テキストサーフェスは常に最大 1 枚**。内容を状態で切り替える（重なりを構造で根絶）:

- Normal・非表示（既定）→ B で **ステータス** 表示
- Registration 中 → **登録ガイダンス** を強制表示（現 CourseRegGuidance の内容を統合）
- recenter 警告 → ステータスのバッジ行 + 発生時のみ数秒オートショウ

**ステータス内容**（heartbeat と同じ参照束: LapCounter / PlayerZoneTracker / CameraStreamRegistry /
CameraSwitchDirector / SignalLostFx / CourseFrame）:

```
lap 2 | zone B (cam1 東)
次: lap3 A → cue_A_1
信号 ●●○ | [要再登録⚠]
```

- 「次の cue」は timeline/schedule から次エントリを導出（CueScheduler に照会 API を追加）

**配置（読める head-follow）**:
- 剛体 head-lock 廃止 → **ScreenAnchor と同じ deadzone + SmoothDamp の緩追従**（yaw 追従・pitch は弱 or 固定）。頭を回すと遅れてついてくるが、視線だけ動かせば静止して読める
- 距離 ~1.6m・視線中心から下 ~15°・TMP 統一（レガシー TextMesh 廃止）・小さめフォント
- **距離・角度・サイズ・追従パラメータ・オートハイド秒はすべて SerializeField**（現場調整可。現状の全ハードコードを解消）
- 診断詳細（FPS・HMD 座標・DISC 等）は HMD から退役 → 既存の [HudDump] ログ + Web 卓 heartbeat が担う

**注意（ユーザー確認点 3）**: HMD は体験者が装着するので、ラン中に B で出すと体験者に見える。
ラン中の進行確認の主経路は Web 卓 / キャスト。StatusHud は設営・リハ・交代時用。
オートハイド（表示 N 秒で自動 OFF・0=無効）をオプションで持つ。

### 3. 実装ステップ（ビルド完了後に着手）

1. `ControllerModeLogic` を 2 状態（Normal/Registration）+ 新ジェスチャ（トリガー/グリップ長押し）に改修 — **テスト先行**（ControllerModeLogicTests を新セマンティクスへ）
2. `OvrControllerBridge` 入力マップ書き換え: 左手・スティック・cue 試射・チートシート呼び出しの撤去、トリガー/グリップ長押し検出の追加
3. `StaffPanel.cs` 削除。`RuntimeDebugHud` を `StatusHud`（単一サーフェス・緩追従・TMP）へ置き換え or 全面改修
4. `CourseRegistrationController`: スティックナッジ撤去・ガイダンス出力先を StatusHud へ（自前 TextMesh 生成を廃止）
5. `CueScheduler` に「次の発火予定」照会 API 追加
6. `MainDemoSceneSetup` 配線更新（冪等・SerializeField 既定値も setup が書く — prefab YAML 未反映罠に注意 = unity-prefab-fields）
7. `HudToggleInput` / `CameraSwitchInput` 等の Editor 系はそのまま（実機に影響なし）。`ShowControlClient.ToggleActiveCameraCue` は Web 経路用に残す
8. **ドキュメント同期（同一コミット）**: `.claude/rules/unity-vr.md`（リチュアル表を全面書き換え）、`.claude/rules/streaming.md`（HUD 記述）、README、この plan を implemented に
9. コンパイル + EditMode テスト → 実機検証項目: 長押し閾値の体感 / StatusHud の読みやすさ（距離・角度）/ 登録フロー一周 / ランリセット

### 4. ユーザー確認点（実装前に要回答）

1. **スクリーン追従凍結（旧 左X・ScreenAnchor.Toggle）の行き先** — コントローラから外す。完全削除でよいか、Web 卓に移すか（現状 Web 経路なし）
2. **A = カメラ手動送りを残すか** — リハ・設営確認用に残す提案。不要ならトリガー/グリップ長押し 2 つだけの超シンプル構成も可
3. **ラン中ステータス表示が体験者に見える件** — オートハイド + Web 卓/キャスト運用で許容か
4. **Registration ナッジ廃止** — やり直しベースで OK か
