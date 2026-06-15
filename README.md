# fixed-cam-vr

1990 年代のサバイバルホラーゲームに特徴的な「固定視点カメラ」の演出を，VR と実空間
を用いて現実世界に持ち込む体験を提案する．体験者は HMD を装着し，自身が立つ実空間に設置
された複数の固定カメラ映像のみを見ながら，自分の体で歩き，しゃがみ，探索する．一人称視点
が主流となった現代 VR においてあえて第三者視点を強制することで，視界外への恐怖，被観測感 ，身体と視点の乖離による独特の不安を生み出す．映像には CG 合成とフィルタによるホラー演出を重ね，実世界とフィクションの境界を揺るがす．

> このリポジトリには本体（**廻リ視** = 固定視点ホラー VR）と、別アプリの調査用 VR
> **[TableDuo](#同居サブプロジェクト-tableduo調査-vr)**（テーブル 2 人非対称・手だけアバター調査）が同居している。
> ビルドは用途別メニューで 2 アプリを別々に出す（[ビルド & デプロイ](#ビルド--デプロイ)）。

## 目次

- [目標フェーズ](#目標フェーズ)
- [スタック](#スタック)
- [実装済みコード](#実装済みコード)
- [必要環境](#必要環境)
- [初回セットアップ](#初回セットアップ)
- [配信側（スマホ）設定例](#配信側スマホ設定例)
- [推奨ワークスペースと作業フロー](#推奨ワークスペースと作業フロー)
  - [入力早見表（HMD 装着者向け）](#入力早見表hmd-装着者向け)
- [HMD なしの検証フロー](#hmd-なしの検証フロー)
- [実機検証チェックリスト](#実機検証チェックリスト)
- [ビルド & デプロイ](#ビルド--デプロイ)
- [同居サブプロジェクト: TableDuo（調査 VR）](#同居サブプロジェクト-tableduo調査-vr)
- [開発フロー](#開発フロー)
- [ドキュメント一覧](#ドキュメント一覧)
- [ディレクトリ](#ディレクトリ)
- [制約・既知事項](#制約既知事項)

## 目標フェーズ

> **更新責務**: 各 Phase の状態は実機検証完了時にシュビーが更新する。ユーザーが「実機で動いた」と報告したら「✅ シーン配置完了・実機検証待ち」→「✅ 実機検証完了」に書き換え、必要なら CHANGELOG 相当の知見を TROUBLESHOOTING.md「実機検証で得た知見」セクションへ追記。

| Phase | 内容 | 状態 |
|---|---|---|
| 0 | プロジェクト初期化・Meta XR SDK 導入 | ✅ |
| 1 | MJPEG 受信ロジック実装（[Assets/Scripts/Streaming/](Assets/Scripts/Streaming/)） | ✅ |
| 2 | Main シーンに OVRCameraRig + Quad + MjpegScreen を配置し、Quest 3 で映像表示 | ✅ シーン配置完了・実機検証待ち |
| 2.5 | 複数配信ソース（fixed-cam-streamer / DroidCam）の切替表示（A/B ボタン or Tab/数字キー）+ 現在ソース TMP ラベル | ✅ シーン配置完了・実機検証待ち |
| 2.7 | プレイヤー位置連動カメラ切替（[Assets/Scripts/Tracking/](Assets/Scripts/Tracking/)）+ ZoneSandbox 検証シーン | ✅ プロトタイプ完了・実機検証待ち |
| 3 | パススルー暗転 + CCTV 風シェーダ + CG 合成手法の比較（[Assets/Scripts/Fx/](Assets/Scripts/Fx/)）| ✅ 4 系統プロトタイプ完了・本実装着手前 |
| 4 | スクリーン外 3D 演出 / CG 合成 | 未着手 |

## スタック

- **Unity**: 2022.3.62f2 LTS（URP）
- **XR**: Meta XR All-in-One SDK `201.0.0` + Oculus XR Plugin `4.5.4`（Standalone での Quest Link Editor Play 用）
- **ターゲット**: Quest 3（Android / IL2CPP / ARM64）
- **言語**: C#（`#nullable enable` / `async/await`）+ Shader Graph + Compute Shader
- **入力**: MJPEG over Wi-Fi（**[fixed-cam-streamer](https://github.com/Roiril/fixed-cam-streamer) 標準**、DroidCam / IP Webcam はフォールバック）→ `MjpegStreamReceiver` でデコード + `/info` で自動回転 / アスペクト補正
- **HUD**: TextMeshPro World Space Canvas + `RuntimeDebugHud`（Diagnostics）

## 実装済みコード

### Streaming（Phase 1 / 2 / 2.5）

[Assets/Scripts/Streaming/](Assets/Scripts/Streaming/) （`FixedCamVr.Streaming` namespace, asmdef 切り出し済み）

- [CameraSource.cs](Assets/Scripts/Streaming/CameraSource.cs) — ScriptableObject。host/port/path から URL 構築
- [MjpegStreamReceiver.cs](Assets/Scripts/Streaming/MjpegStreamReceiver.cs) — `multipart/x-mixed-replace` を別スレッドで受信、最新フレームを byte[] でダブルバッファ保持。再接続は exponential backoff
- [MjpegScreen.cs](Assets/Scripts/Streaming/MjpegScreen.cs) — `Renderer` の mainTexture に `Texture2D.LoadImage` で焼き込み（フレーム内 GC アロケなし）

### Tracking（Phase 2.7：プレイヤー位置連動カメラ切替）

[Assets/Scripts/Tracking/](Assets/Scripts/Tracking/) （`FixedCamVr.Tracking` namespace, asmdef 切り出し済み）

- `PlayerZone` — MonoBehaviour。AABB（中心 + halfExtents）+ 優先度 + `cameraIndex` + ラベルを持つ
- `PlayerZoneTracker` — OVRCameraRig の HMD 位置をサンプリングし、ヒステリシス（縮小 AABB の内外で切替判定 / shrink 0.15m）+ 優先度比較で `CameraStreamRegistry` のアクティブを切替（評価間隔 0.05s、全ゾーン外時は `keepLastWhenOutside` で直近維持が既定）
- 検証シーン: [Assets/Scenes/ZoneSandbox.unity](Assets/Scenes/ZoneSandbox.unity)（A_Center / B_Right / C_Left の 3 ゾーン構成）
- 計画: 完了済み（git log の `feat：プレイヤー位置連動カメラ切替（PlayerZone / PlayerZoneTracker）` を参照）

### Fx（Phase 3：映像加工 / CG 合成手法の比較検証）

[Assets/Scripts/Fx/](Assets/Scripts/Fx/) + [Assets/Art/Shaders/Fx/](Assets/Art/Shaders/Fx/) （`FixedCamVr.Fx.*` namespace, asmdef 切り出し済み）

バイオハザード固定カメラ風表現を狙い、4 系統の映像加工手法を Editor 検証可能な形で実装。本実装への昇格は次フェーズ。

| Phase | 手法 | 主要ファイル |
|---|---|---|
| 3-1 | URP `FullScreenPassRendererFeature` 用 CRT（スキャンライン + グレイン + ビネット） | `Assets/Art/Shaders/Fx/FxCrtPostFx.shader` + `Assets/Scripts/Fx/RendererFeature/FxCrtPostFxController.cs` |
| 3-2 | `Graphics.Blit` + 自前 .shader（色収差） | `Assets/Art/Shaders/Fx/FxChromaticAberration.shader` + `Assets/Scripts/Fx/Blit/FxBlitChromaticAberration.cs` |
| 3-3 | `ParticleSystem` ベース埃オーバーレイ | `Assets/Scripts/Fx/Particles/DustOverlayBuilder.cs` |
| 3-4 | Compute Shader 畳み込み（Sobel / SobelOverlay / Reinhard Tonemap 3 kernel） | `Assets/Art/Shaders/Fx/FxSobel.compute` + `Assets/Scripts/Fx/Compute/SobelEdgeRunner.cs` |

**推奨方針（[計画レポート](.claude/plans/2026-05-03_video-fx-research.md) より）**: Phase 3-1 (CRT) + 3-3 (薄い埃) の組合せが本命。Phase 3-4 (Sobel) は全画面ではなく選択的マスクで使うのが筋。

#### 利用フロー（Editor）

1. **`Tools > FixedCamVr > Setup > Setup FxSandbox Scene`** → `Assets/Scenes/FxSandbox.unity` を生成（テストパターン入力 + 各 Phase の GameObject）
2. **`Tools > FixedCamVr > Setup > Create CRT Material`** → `Assets/Art/Materials/Fx/FxCrtMaterial.mat` を生成、Phase 3-1 用の手動セットアップ手順を Console に表示
3. FxSandbox 内の `[Fx]_BlitChromatic` / `[Fx]_DustParticles` / `[Fx]_SobelCompute` を SetActive 切替で各 Phase の見た目を比較

## 必要環境

- Unity **2022.3.62f2** LTS + Android Build Support
- Quest 3（開発者モード有効、USB デバッグ許可）
- 配信用スマホ（**fixed-cam-streamer** 推奨、DroidCam / IP Webcam も可）×N、全デバイスが同一 LAN（5GHz Wi-Fi 推奨）

## 初回セットアップ

### 1. プラットフォーム切替（Android）

1. **File → Build Settings → Android → Switch Platform**
2. **Texture Compression**: ASTC
3. **Edit → Project Settings → XR Plug-in Management → Android タブ** で **Oculus** にチェック
4. **Player Settings → Other Settings**
   - Color Space: **Linear**
   - Auto Graphics API: OFF → **Vulkan** を最上位
   - Scripting Backend: **IL2CPP**
   - Target Architectures: **ARM64** のみ
   - Minimum API Level: **Android 10 (API 29)** 以上

### 2. Meta XR Project Setup Tool

エディタ右下に表示される **Project Setup Tool** で **Fix All / Apply All** を実行。

### 3. Main シーン配置（Phase 2 / 2.5 / 2.7）

`Assets/Scenes/Main.unity` を開いて **Tools > FixedCamVr > Setup > Setup Main Demo Scene** を実行。
Phase 2.7 用 `[Zones]`（Center/Right/Left）+ `[Tracker]` + HUD（`DebugHud` Canvas + `RuntimeDebugHud` + `HudToggleInput` + `HudLogDumper`）+ OvrControllerBridge.hud 結線を一発で配置する（再実行可能）。

メニューが効かない場合は手動で：OVRCameraRig プレハブ配置 → Quad に MjpegScreen アタッチ → CameraSource SO（**Create > FixedCamVr > Camera Source**）を割当。

### 4. Phase 3（FX）— URP RendererFeature 手動配線

`Assets/Settings/URP-Balanced-Renderer.asset` を Inspector で開き **Add Renderer Feature > Full Screen Pass Renderer Feature** を追加。Pass Material に `Assets/Art/Materials/Fx/FxCrtMaterial.mat`（無ければ **Tools > FixedCamVr > Setup > Create CRT Material**）。Inject Point = `After Rendering Post Processing`。

### 5. Quest Link で Editor Play する場合（反復開発用）

実機ビルドせず PC の Editor から Quest に流す経路。詳細手順は [TROUBLESHOOTING.md「Quest Link で Editor Play 時の制約」](TROUBLESHOOTING.md) 参照。要点：

1. **Build Target は Standalone (Windows) のまま**（Android にスイッチしない）
2. **Edit > Project Settings > XR Plug-in Management > Windows / Mac / Linux タブ** で **Oculus** にチェック（`com.unity.xr.oculus` 4.5.4 が必須）
3. PC で Meta Quest Link app 起動 → Quest 側で Quest Link ON
4. Unity Editor で **Ctrl+Shift+M** で Main を開いて Play

実機性能評価（90Hz 維持）は Quest Link でなく実機 APK ビルドで実施。Quest Link 接続時は **72Hz が上限** + Editor Play の重さで実 FPS が落ちる。

## 配信側（スマホ）設定例

**標準は [fixed-cam-streamer](https://github.com/Roiril/fixed-cam-streamer)**（自作 Android アプリ）。DroidCam / IP Webcam はフォールバック扱い。

| アプリ | port | videoPath | infoPath | 備考 |
|---|---|---|---|---|
| **fixed-cam-streamer** (標準) | 8080 | `/video` | `/info` | レンズ切替・露出ロック・/health・auto-IDLE 対応 |
| IP Webcam (Android) | 8080 | `/video` | （なし） | infoPath 空にすれば fallback |
| DroidCam | 4747 | `/mjpegfeed?640x480` | （なし） | infoPath 空にすれば fallback |

`CameraSource` に `host` / `port` / `videoPath` / `infoPath` / `healthPath` を分割入力。`Assets/Settings/Cameras/Phone01.asset` 等で `host` を実機 IP に書き換える運用。

### fixed-cam-streamer のセットアップ概要

詳細は [fixed-cam-streamer README](https://github.com/Roiril/fixed-cam-streamer) 参照。要点：

1. `git clone` → `./gradlew.bat assembleDebug` → APK が `app/build/outputs/apk/debug/app-debug.apk` に出る
2. `adb install -r <apk>` で各スマホへ。複数台は `adb -s <serial>` で個別指定
3. 起動 → カメラ + 通知権限を許可 → ステータス上部に `http://<phone-ip>:8080/` が出る
4. その IP を `Phone01.asset` / `Phone02.asset` の `host` に転記して Unity Play

実機運用のコツ：
- **設置前にレンズ選択・露出ロック**を済ませる（人が通った時に画が動かない）
- **15 秒で auto-IDLE** に入る（プレビュー消えて画面減光、配信は継続）。タップで復帰
- 画面ロックや別アプリへの切替で **配信が止まる可能性**あり（Foreground Service で耐えるが完全ではない）。デモ中はアプリ前面のままにしておく

## 推奨ワークスペースと作業フロー

### 推奨レイアウト

```
┌─────────────────────────────────────────────────────────────┐
│ [Toolbar: ▶ ⏸ ⏭ ]                                          │
├──────────┬───────────────────────────┬──────────────────────┤
│          │                           │                      │
│ Hierarchy│   Scene │ Game (tabs)     │     Inspector        │
│          │                           │                      │
│          ├───────────────────────────┤                      │
│          │                           ├──────────────────────┤
│ Project  │        Console            │     Test Runner      │
│          │                           │                      │
└──────────┴───────────────────────────┴──────────────────────┘
```

### 初期セットアップ（1 回だけ）

1. **Tools > FixedCamVr > Layout > Setup Recommended Workspace**
   → Main シーンを開いて必要なウィンドウを全部出す
2. 上図のとおりにドラッグして配置
3. **Tools > FixedCamVr > Layout > Save Current Layout**
   → `Assets/Editor/Layouts/FixedCamVr.wlt` に保存（コミット推奨）
4. 別マシンや復元時は **Tools > FixedCamVr > Layout > Apply FixedCamVr Layout** 一発

### 日常の作業フロー

| 目的 | 操作 |
|---|---|
| Main シーン開く | **Ctrl+Shift+M** |
| Main の統合配置を再生成 | **Tools > FixedCamVr > Setup > Setup Main Demo Scene**（Zones / Tracker / DebugHud を一発配置） |
| Debug シーン開く（HMD なし検証用） | **Ctrl+Shift+D** |
| 配信スマホが繋がるか即確認 | **Tools > FixedCamVr > Diagnostics > Ping DroidCams** → Console に結果（メニュー名は旧称、fixed-cam-streamer も同じ仕組みで疎通確認される） |
| Phone01/02 の URL/IP 編集 | Project で `Settings/Cameras/Phone01.asset` → Inspector の **Test Connection** ボタン |
| EditMode テスト実行 | **Tools > FixedCamVr > Diagnostics > Run All Tests** → Test Runner に結果 |
| Play 中にカメラ切替（HMD なし） | Hierarchy で `[Streaming]` 選択 → Inspector の **Activate / Prev / Next** ボタン |
| 受信状態の確認 | Inspector の Runtime State 行（● Connected / ○ Disconnected） |

Hierarchy のグループ見出し `=== XR ===` `=== Stage ===` `=== Logic ===` は色分けバーで強調表示される（[HierarchySeparator.cs](Assets/Scripts/Streaming/Editor/HierarchySeparator.cs)）。

### 入力早見表（HMD 装着者向け）

| 入力 | 機能 | 実装 |
|---|---|---|
| 右コントローラ **A** | 次のカメラに切替 | `OvrControllerBridge.nextButton`（`OVRInput.Button.One`）|
| 右コントローラ **B** | 前のカメラに切替 | `OvrControllerBridge.prevButton`（`OVRInput.Button.Two`）|
| 左コントローラ **X** | スクリーン head-lock 切替 | `OvrControllerBridge.anchorToggleButton`（`OVRInput.Button.Three`）|
| 左コントローラ **Y** | HUD 表示トグル | `OvrControllerBridge.hudToggleButton`（`OVRInput.Button.Four`）|
| キーボード **Tab** / **Shift+Tab** | カメラ切替（Editor / HMD なし検証） | `CameraSwitchInput` |
| キーボード **1〜9** | カメラ直接指定 | `CameraSwitchInput` |
| キーボード **Space** | スクリーン head-lock 切替 | `ScreenAnchor.Toggle` |
| キーボード **H** | HUD 表示トグル | `HudToggleInput.keyboardToggleKey` |

ゾーンに入ると自動でカメラ切替される（`PlayerZoneTracker`、Phase 2.7）。手動切替は自動切替を上書きする。

## HMD なしの検証フロー

実機 Quest 3 が無い時は以下の 3 段階で詰める。VR 必須なのは「コントローラ入力経路」と「没入感の最終評価」だけで、ストリーミング系のバグの大半はこちらで再現できる。

### 1. EditMode テスト（最優先・自動）

```
Window → General → Test Runner → EditMode → Run All
```

`Assets/Tests/Streaming/` の `WrapIndex` / `BuildUrl` 系テストが pass することを CI 的に確認。Registry のラップアラウンド境界、URL 組み立てに回帰が入っていないか。

### 2. Flat デバッグシーン（OVR を外した普通の 3D シーン）

`Assets/Scenes/Debug/FlatStreaming.unity` を開いて Play モード（**Ctrl+Shift+D**）。OVRCameraRig は無く、通常 Camera + Quad のみ。

| 操作 | 期待挙動 |
|---|---|
| Tab | 次のカメラに切替（Game ビューの映像が変わる） |
| Shift+Tab | 前のカメラに切替 |
| 1 / 2 | 直接指定 |
| Space | スクリーンの head-lock 切替（Camera 固定なので見た目は変化しない） |

スマホ側で fixed-cam-streamer（または DroidCam）が動いている前提。`Assets/Settings/Cameras/Phone01.asset` の host を実 IP に書き換えてから Play。

### 3. Main シーン + OVR Headset Emulator（VR 視点シミュ）

`Assets/Scenes/Main.unity` で Play。Game ビューでは何も見えないが、`OVRCameraRig` 配下の `OVRHeadsetEmulator` で頭の動きを模擬できる:

- **Ctrl + マウス** → 頭の回転
- **Alt + マウス** → 頭の傾き

コントローラ A/B/X はキーボードからは打てないので、ここでは検証不可（→ 実機 or XR Device Simulator 経由）。代わりに **キーボード Tab/数字** で切替動作と Space で head-lock の追従挙動を見れる。

## 実機検証チェックリスト

Phase 進捗テーブルで「実機検証待ち」になっている項目の確認手順。Quest 3 を被って 1 回通せば全部潰せる。

### Phase 2（MJPEG 受信 + Quad 描画）
- [ ] スマホで fixed-cam-streamer 起動（または DroidCam / IP Webcam）→ URL を `Assets/Settings/Cameras/Phone01.asset` の `host`/`port` に反映
- [ ] Main シーンで Play → Quad に映像が表示される
- [ ] Quest 3 にビルド & デプロイ → HMD 内 Quad に映像（暗転していない / 色が落ちていない）
- [ ] スマホ電源 OFF → 30 秒以内に reconnect ログ → 復旧

### Phase 2.5（複数カメラ切替）
- [ ] 配信スマホ 2 台以上を起動
- [ ] HMD で右コントローラ A → 次のカメラに切替、B → 前のカメラ
- [ ] 切替時にフリーズ・黒フレーム挿入が無い
- [ ] X ボタン → スクリーン head-lock の ON/OFF が効く

### Phase 2.7（プレイヤー位置連動切替）
- [ ] `Assets/Scenes/Sandbox/PlayerZone.unity` を開いて HMD で Play（**Tools > FixedCamVr > Diagnostics > Open PlayerZone Sandbox**）
- [ ] A_Center → B_Right → C_Left へ歩いて移動 → 該当カメラに自動切替
- [ ] ゾーン境界を細かく出入り → ヒステリシスで頻繁な切替が起きない（shrink 0.15m）
- [ ] 全ゾーン外に出る → 直前のカメラが維持される

### Phase 3（映像加工 4 系統）
- [ ] FxSandbox を生成（**Tools > FixedCamVr > Setup > Setup FxSandbox Scene**）
- [ ] CRT / Chromatic / Dust / Sobel をそれぞれ ON にして 90fps 維持を OVR Metrics Tool で確認
- [ ] 推奨組合せ（CRT + 薄い Dust）でフレーム落ちが無い

詳細手順は [TROUBLESHOOTING.md](TROUBLESHOOTING.md)（FPS / OVR Metrics Tool / Profiler）と [docs/onsite-checklist.md](docs/onsite-checklist.md) も参照。

## ビルド & デプロイ

このリポジトリには **2 つのアプリ**（廻リ視 / TableDuo）が同居している。**手動 Build Settings は使わない** —
同名・同パッケージ ID になって Quest 上で共存できなくなるため。専用メニュー
[BuildVariants.cs](Assets/Editor/BuildVariants.cs) 経由が唯一の正：

| メニュー | 出力 | パッケージ ID | シーン |
|---|---|---|---|
| `Tools/FixedCamVr/Build FixedCam APK（廻リ視）` | `Builds/mawarimi.apk` | `com.roiril.mawarimi` | Main.unity |
| `Tools/FixedCamVr/Build TableDuo APK` | `Builds/tableduo.apk` | `com.roiril.tableduo` | TableDuoMain.unity |
| （各 `…（Release）` 版あり） | `*-release.apk` | 同上 | Development なし |

インストールは adb（Quest 3 を USB-C 接続）：

```
adb -s <serial> install -r --no-streaming Builds\tableduo.apk
```

手順詳細・MCP からのビルド・APK 完成ポーリングは **quest-build スキル**（[.claude/skills/quest-build/SKILL.md](.claude/skills/quest-build/SKILL.md)）。

## 同居サブプロジェクト: TableDuo（調査 VR）

fixed-cam-vr 本体とは別に、**テーブルを囲む 2 人非対称マルチプレイ VR** が同居している
（コード `Assets/TableDuo/`・namespace/asmdef `TableDuoVr.*`・本体 `FixedCamVr.*` とは相互参照禁止）。

- **目的＝調査**: 「手だけアバター」との対人インタラクション観察。片方はフルアバター（発話可）、
  片方は手だけ（無言・ジェスチャーのみ）。手話を知らない人が片手でどれだけ伝え合えるか等を半構造化で観察
- **構成**: Meta XR ハンドトラッキング + Netcode for GameObjects（LAN 直結）。host/client と役割（full/hand）は
  起動フラグで独立指定。SessionLogger（動作 CSV）+ SessionReplayRecorder（全身振り記録）+ Editor リプレイビューア（stimulated recall）
- **シーン生成**: `Tools/FixedCamVr/Setup/Setup TableDuo Scene`（冪等。**ビルド直前に再実行してクリーン状態にする**）
- **実機起動（実機 2 台・同 LAN）**:
  ```
  adb -s <hostSerial>   shell am start -n com.roiril.tableduo/com.unity3d.player.UnityPlayerActivity -e tdv_mode host   -e tdv_role full
  adb -s <clientSerial> shell am start -n com.roiril.tableduo/com.unity3d.player.UnityPlayerActivity -e tdv_mode client -e tdv_ip <hostIP> -e tdv_role hand
  ```
- **状態**: 実機 2 台で接続〜役割〜目線〜手メッシュ描画まで動作確認済み（2026-06-15）。詳細・運用 runbook・既知の罠は
  [.claude/plans/2026-06-11_table-duo_study.md](.claude/plans/2026-06-11_table-duo_study.md) と
  [.claude/memory/table_duo_study_status.md](.claude/memory/table_duo_study_status.md)
- 設計 [docs/table-duo/study-design.md](docs/table-duo/study-design.md) / 実施手順 [docs/table-duo/study-protocol.md](docs/table-duo/study-protocol.md) / 同意書 [docs/table-duo/consent-template.md](docs/table-duo/consent-template.md)

## 開発フロー

- 実装計画は `.claude/plans/YYYY-MM-DD_<slug>.md` に置く
- シュビー（Claude Code）と共同開発。動作モード等は [CLAUDE.md](CLAUDE.md) 参照
- 領域別ルール: [.claude/rules/unity-vr.md](.claude/rules/unity-vr.md), [meta-xr.md](.claude/rules/meta-xr.md), [streaming.md](.claude/rules/streaming.md)
- コミット規約：日本語 `<type>：<要約>` 形式（グローバル `~/.claude/CLAUDE.md` 参照）

## ドキュメント一覧

| ファイル | 内容 |
|---|---|
| [CONTRIBUTING.md](CONTRIBUTING.md) | コミット規約・ブランチ運用・コードスタイル |
| [TROUBLESHOOTING.md](TROUBLESHOOTING.md) | fixed-cam-streamer / DroidCam 不通 / HMD 真っ黒 / FPS 低下（OVR Metrics / Profiler 含む）/ 実機検証で得た知見 |
| [Roiril/fixed-cam-streamer](https://github.com/Roiril/fixed-cam-streamer) (別リポジトリ) | 配信側 Android アプリの実装・ビルド手順・運用ノート |
| [docs/onsite-checklist.md](docs/onsite-checklist.md) | 現場（実機 Quest 3）での 60 秒チェック → Phase 別動作確認 → 切り分けフロー |
| [docs/table-duo/requirements.md](docs/table-duo/requirements.md) | 同居サブプロジェクト TableDuo（2人非対称VR）の要件 |
| [docs/table-duo/study-design.md](docs/table-duo/study-design.md) | TableDuo 調査の設計（RQ・条件・計測・限界） |
| [docs/table-duo/study-protocol.md](docs/table-duo/study-protocol.md) | TableDuo 調査の実施プロトコル（台本・観察シート・runbook） |
| [docs/table-duo/consent-template.md](docs/table-duo/consent-template.md) | TableDuo 調査の参加同意書テンプレート（学会発表前提） |
| [.claude/plans/](.claude/plans/) | 直近・次フェーズの実装計画書（完了済みは git log で参照） |
| [.github/workflows/README.md](.github/workflows/README.md) | GitHub Actions（Unity Test Runner）の設定手順 |

## ディレクトリ

```
fixed-cam-vr/
├── Assets/
│   ├── Art/
│   │   ├── Materials/        # マテリアル
│   │   └── Shaders/Fx/       # 映像加工シェーダ + Compute（Phase 3）
│   ├── Oculus/               # Meta XR SDK（自動生成、コミット）
│   ├── Resources/            # OVR/XR ランタイム設定 .asset
│   ├── Prefabs/              # 共通要素（複数シーンから参照）
│   │   ├── Stage/            # MjpegScreenStage（Quad + MjpegScreen + ScreenAnchor + SourceLabel）
│   │   ├── Logic/            # StreamingLogic（CameraStreamRegistry + 入力ブリッジ）
│   │   └── XR/               # 自前 XR Prefab 用（OVRCameraRig は Meta XR 標準を直接利用）
│   ├── Scenes/
│   │   ├── Main.unity                  # 本体の Build Settings 登録シーン（TableDuoMain と排他運用）
│   │   ├── Debug/
│   │   │   └── FlatStreaming.unity     # HMD なし検証（Editor 専用）
│   │   └── Sandbox/
│   │       └── PlayerZone.unity        # プレイヤー位置連動切替の検証（Phase 2.7、Editor 専用）
│   │   # FxSandbox.unity は Editor メニューで動的生成（git 追跡外）
│   ├── Scripts/
│   │   ├── Streaming/        # MJPEG 受信・描画（Phase 1 / 2 / 2.5）
│   │   ├── OvrBridge/        # OVRInput ↔ asmdef 境界ブリッジ
│   │   ├── Tracking/         # プレイヤー位置連動カメラ切替（Phase 2.7）
│   │   └── Fx/               # 映像加工 / CG 合成プロトタイプ（Phase 3）
│   ├── TableDuo/             # 同居サブプロジェクト：テーブル2人非対称VR（CLAUDE.md 参照）
│   └── Settings/             # URP / Quality / CameraSource SO
├── Packages/manifest.json    # Meta XR SDK 等の依存
├── ProjectSettings/          # エディタ設定
├── .claude/                  # Claude Code ハーネス（rules / plans / commands / memory / skills / settings）
├── CLAUDE.md
└── README.md
```

## 制約・既知事項

- フレーム 90Hz 維持のため、`MjpegScreen.Update` でフレーム内アロケが起きないよう `byte[]` プールと `Texture2D.LoadImage(markNonReadable: false)` で再利用
- `HttpClient` 接続失敗時は最大 30 秒の exponential backoff で再接続（[MjpegStreamReceiver.cs:86](Assets/Scripts/Streaming/MjpegStreamReceiver.cs#L86)）
- 実機検証はシュビーから自動化不可。コンパイル OK 後のビルド & 動作確認はユーザー側で行う
