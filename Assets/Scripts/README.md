# コードの地図

体験 1 回の流れを、どのクラスが持っているかの順に並べたもの。初めて読むときはここから入る。
コードの契約（破ると壊れること）は `.claude/rules/` の話題別の rule（索引は `.claude/rules/streaming.md`）、
体験者に見えることは `.claude/canon/NOW.md` にある。ここは「どこを開けばよいか」だけを書く。

## 体験 1 回の流れ

| 段 | 持ち主 | 次へ渡す合図 |
|---|---|---|
| 起動 | `Streaming/ShowControlClient`（`.BakedAndCache.cs` が APK に焼いた show.json を読む） | 設定を各部品へ配る |
| スタッフの準備 | `Diagnostics/StaffSetupPanel` ＋ `Input/StaffSetupLogic`。タブレットとは `Streaming/VisitorPortal`（:8090）。位置合わせは `Tracking/CourseRegistrationController` | 引き渡し（Welcome 段の検査が通る） |
| 新しい体験者 | 右 A 2 秒・タブレット → `OvrBridge/OvrControllerBridge.VisitorReset.cs` → `ShowControlClient.BeginNewVisitorRunLocal`。卓からは `TriggerRunReset`。どちらも `ShowControlClient.ResetForNewVisitor`（`.VisitorRun.cs`）を通る | `ShowRunDirector.BeginRun` |
| HMD 内の導入（練習 → 名乗り → 題字） | 状態機械は `Input/HmdOnboardingLogic`。入力を渡すのは `OvrControllerBridge.Update`、画面に映すのは `OvrControllerBridge.Onboarding.cs`。題字は `Streaming/TitleScreen` | 題字を X / Y の短押しで閉じる |
| 導入演出（パススルーが割れてスクリーンになる） | `Streaming/IntroDirector`（段は `IntroLogic`）。床の矢印は `Streaming/WalkGuide` | `ShowRunDirector` が本編へ移し、`ShowControlClient.BeginMainRun` |
| 本編（3 区間 × 3 周） | 位置 → 区画: `Tracking/PlayerZoneTracker` → `Streaming/CameraSwitchDirector`。周の数え方: `Tracking/LapCounter`。区画へ入った知らせ（`SegmentEntry`）は `Streaming/CueScheduler.CameraEntered` から配られる | 下の「区画へ入った知らせを受け取る部品」 |
| 最後の A と終わり方 | `Streaming/EndingDecisionLogic`（`ShowRunDirector` が回す）。最後の A の区画は `ShowRunDefaults.IsClosingSegment` | 帰還End / 人形End / 中断 |
| 終幕 | `Streaming/OutroDirector`（段）・`Diagnostics/OutroReport`（エンドロール）・`Streaming/EndingFarewellDoll`（別れの人形） | 次の体験者のリセットを待つ |

### 区画へ入った知らせを受け取る部品

`CueScheduler.CameraEntered` は `SegmentEntry`（カメラ・区間の周・進行の周）を配る。周は 2 つあり、引き返すと食い違う。

| 受け取る部品 | 読む周 | 何をする |
|---|---|---|
| `Streaming/TimelineDirector` → `Streaming/TakeRunner` | 区間の周 | その区画の演出（take）を武装して走らせる |
| `Streaming/Recording/SegmentRecorder` | 区間の周 | 1・2 周目の映像を録る（3 周目の背景になる） |
| `Streaming/ShowRunDirector` | **進行の周** | 周を走り切ったかの判定 |
| `ShowControlClient`（`.VisitorRun.cs`） | 区間の周 | 区画ごとの滞在時間 |
| `Diagnostics/ShowTelemetryHost` | 両方 | ログ `ev=seg lap= plap=` |

### 画面と音

| 何 | 持ち主 |
|---|---|
| カメラ映像の受信 | `Streaming/CameraStream` ・ `MjpegStreamReceiver`（配信の契約は `.claude/rules/stream-link.md`） |
| スクリーンへの描画と加工 | `Streaming/MjpegScreen` ・ `CameraFeelFx` ・ `GlitchFx` ・ `ScreenDecayLogic`（周回の劣化） |
| CG の人形 | `Streaming/Cg/ShowCgLayer` |
| AI エージェント（スイ）の連絡の面 | `Diagnostics/CommsPanel` |
| 音 | `Streaming/Sound/ShowSoundDirector` ・ `Streaming/BgmDirector`（設計は `.claude/rules/sound-design.md`） |
| 体験者の報告（左 X / Y の 1 秒長押し） | `OvrControllerBridge.Update` → `ShowControlClient.RecordVisitorMark`（`.VisitorRun.cs`） |

## 体験者が替わるときのリセット

体験 1 回ぶんの状態を持つ部品は、次の 3 つのどれかで落ちる。

1. `ShowControlClient.ResetForNewVisitor` — 報告数・滞在・cue・演出・BGM・音・録画・周の数え直し
2. `ShowRunDirector.BeginRun` — 画面の劣化・画の状態・警告音・言語切替の音・終幕の判定
3. `ShowRunDirector.RunRestarted` の購読 — 連絡の面・ステータス・心音に合わせた画の加工・終幕の写真・題字・導入

どの部品をどこから落とすかの表は `Assets/Tests/Streaming/VisitorRunResetCoverageTests.cs`。
`ResetRun` を持つ部品を足して表に書かないと、このテストが落ちる。

⚠ `ShowControlClient.LapCountReset` は「周の数え直し」で、導入から本編へ移るときにも発火する。体験者が替わった合図として使わない。

## フォルダの名前と中身

フォルダ名は作り始めの役目のまま残っていて、中身と合っていない。改名していないのは、
`tools/unity.ps1` の `-executeMethod` の文字列・テストのアセンブリ名・フォントの文字の収集元など、
コンパイルで気づけない形で壊れるものが多いため。

| フォルダ | 実行時のファイル / 行 | 実際の中身 |
|---|---|---|
| `Streaming/` | 151 / 約 5.1 万 | 体験のほぼ全部。映像の受信は約 7% で、残りは進行・演出・導入と題字・画面加工・連絡の面の論理・音・CG・終幕 |
| `Streaming/Editor/` | — | **全フォルダの** Editor の道具（シーンを組む `MainSceneBuilder`・プレビュー・検証）。CLI は `tools/unity.ps1 menu` |
| `Diagnostics/` | 23 / 約 9,400 | 本来の診断（ログ・計器）は一部。体験者が見る面（`CommsPanel`・`OutroReport`）とスタッフの面（`StaffSetupPanel`・`StatusHud`）が入っている |
| `Tracking/` | 20 / 約 5,200 | 位置と区画・周の数え方・位置合わせ。PC での自動歩行の道具もここ |
| `OvrBridge/` | 12 / 約 3,100 | Meta XR SDK に触れる唯一の場所（asmdef なし = Assembly-CSharp）。コントローラ・パススルー・体験の記録と再生 |
| `Input/` | 9 / 約 1,400 | 長押し・モード・導入・準備の判定（SDK に依存しない純ロジック） |
| `Fx/` | 7 / 約 600 | 映像加工の試作。本番のシーンでは使っていない |
| `Streaming/Attic/` | — | 体験から外した封印の箱。シーンの配線は残っているが描かれも鳴りもしない（`.claude/reference/attic-sealed-box.md`） |

`ShowControlClient` と `OvrControllerBridge` は役目ごとに partial のファイルへ分けてある。一覧は各クラスの先頭のコメント。

## シーンに置かずに起動時に自分で生まれる部品

`RuntimeInitializeOnLoadMethod` で生成されるので、シーンを開いても見当たらない。

- `Diagnostics/ShowTelemetryHost` — 実機のログ `[XP]` を書く。通常のログ（`Debug.Log`）のスタックトレースをアプリ全体で切る
- `Diagnostics/VeilAlignmentProbe` / `OvrBridge/CommsPerformanceProbe` — 検証用の計器
- `OvrBridge/XpReplayDriver` — PC で体験の記録を流し直す（Editor だけ）
- `OvrBridge/XpTwinDriver` — HMD も Quest も無しでツインの映像を流し、入力だけで体験を通す（Editor だけ）
- `Tracking/RegVizDebugDriver` / `Tracking/ShowWalkDebugDriver` — 位置合わせの可視化と自動歩行（検証用）

## 確かめ方

| 何 | コマンド |
|---|---|
| Unity のテスト（約 2,300 件・実機不要） | `.\tools\unity.ps1 test` |
| tools のテスト（解析器・現場の道具・ログの書き手と読み手の照合） | `py -3.11 tools/test-tools.py` |
| 実機を 1 周走らせてログ・録画・例外を取る | `py -3.11 tools/quest-record.py --sec 45 --walk` |

実機のログのキーは C#（`ShowTelemetryHost`）と Python（`tools/analyze-xp-log.py`）の両方に書かれている。
`lap=` は区間の周、`plap=` は進行の周。片方だけ直すと `tools/test_telemetry_contract.py` が落ちる。
