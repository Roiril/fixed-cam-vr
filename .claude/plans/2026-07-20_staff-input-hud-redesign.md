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
