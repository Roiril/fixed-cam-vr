# fixed-cam-vr

Meta Quest 3 装着者の視界に、バイオハザード風の固定視点カメラ映像を VR 内スクリーンとして表示する実験。スマホ複数台（DroidCam / IP Webcam）の MJPEG を VR 内 Quad に描画し、後々パススルー暗転 + CCTV 風シェーダ + CG 合成 + スクリーン外 3D 演出に拡張していく。

## 目標フェーズ

| Phase | 内容 | 状態 |
|---|---|---|
| 0 | プロジェクト初期化・Meta XR SDK 導入 | ✅ |
| 1 | MJPEG 受信ロジック実装（[Assets/Scripts/Streaming/](Assets/Scripts/Streaming/)） | ✅ |
| 2 | Main シーンに OVRCameraRig + Quad + MjpegScreen を配置し、Quest 3 で映像表示 | ✅ シーン配置完了・実機検証待ち |
| 2.5 | 複数 DroidCam の切替表示（A/B ボタン or Tab/数字キー）+ 現在ソース TMP ラベル | ✅ シーン配置完了・実機検証待ち |
| 2.7 | プレイヤー位置連動カメラ切替（[Assets/Scripts/Tracking/](Assets/Scripts/Tracking/)）+ ZoneSandbox 検証シーン | ✅ プロトタイプ完了・実機検証待ち |
| 3 | パススルー暗転 + CCTV 風シェーダ + CG 合成手法の比較（[Assets/Scripts/Fx/](Assets/Scripts/Fx/)）| ✅ 4 系統プロトタイプ完了・本実装着手前 |
| 4 | スクリーン外 3D 演出 / CG 合成 | 未着手 |

## スタック

- **Unity**: 2022.3.62f2 LTS（URP）
- **XR**: Meta XR All-in-One SDK `201.0.0`（`Packages/manifest.json` 経由で導入済み）
- **ターゲット**: Quest 3（Android / IL2CPP / ARM64）
- **言語**: C#（`#nullable enable` / `async/await`）+ Shader Graph
- **入力**: MJPEG over Wi-Fi（DroidCam / IP Webcam）→ `MjpegStreamReceiver` でデコード

## 実装済みコード

### Streaming（Phase 1 / 2 / 2.5）

[Assets/Scripts/Streaming/](Assets/Scripts/Streaming/) （`FixedCamVr.Streaming` namespace, asmdef 切り出し済み）

- [CameraSource.cs](Assets/Scripts/Streaming/CameraSource.cs) — ScriptableObject。host/port/path から URL 構築
- [MjpegStreamReceiver.cs](Assets/Scripts/Streaming/MjpegStreamReceiver.cs) — `multipart/x-mixed-replace` を別スレッドで受信、最新フレームを byte[] でダブルバッファ保持。再接続は exponential backoff
- [MjpegScreen.cs](Assets/Scripts/Streaming/MjpegScreen.cs) — `Renderer` の mainTexture に `Texture2D.LoadImage` で焼き込み（フレーム内 GC アロケなし）

### Tracking（Phase 2.7：プレイヤー位置連動カメラ切替）

[Assets/Scripts/Tracking/](Assets/Scripts/Tracking/) （`FixedCamVr.Tracking` namespace, asmdef 切り出し済み）

- `PlayerZone` — ScriptableObject 風の MonoBehaviour。中心座標・半径・優先度・対応 `CameraSource` を持つ
- `PlayerZoneTracker` — OVRCameraRig の HMD 位置をサンプリングし、ヒステリシス（0.15m）+ 優先度比較で `CameraStreamRegistry` のアクティブを切替（評価間隔 0.05s、外周は直近を維持）
- 検証シーン: [Assets/Scenes/ZoneSandbox.unity](Assets/Scenes/ZoneSandbox.unity)（A_Center / B_Right / C_Left の 3 ゾーン構成）
- 計画: [.agent/plans/2026-05-03_player-zone-camera-switch.md](.agent/plans/2026-05-03_player-zone-camera-switch.md)

### Fx（Phase 3：映像加工 / CG 合成手法の比較検証）

[Assets/Scripts/Fx/](Assets/Scripts/Fx/) + [Assets/Art/Shaders/Fx/](Assets/Art/Shaders/Fx/) （`FixedCamVr.Fx.*` namespace, asmdef 切り出し済み）

バイオハザード固定カメラ風表現を狙い、4 系統の映像加工手法を Editor 検証可能な形で実装。本実装への昇格は次フェーズ。

| Phase | 手法 | 主要ファイル |
|---|---|---|
| 3-1 | URP `FullScreenPassRendererFeature` 用 CRT（スキャンライン + グレイン + ビネット） | `Assets/Art/Shaders/Fx/FxCrtPostFx.shader` + `Assets/Scripts/Fx/RendererFeature/FxCrtPostFxController.cs` |
| 3-2 | `Graphics.Blit` + 自前 .shader（色収差） | `Assets/Art/Shaders/Fx/FxChromaticAberration.shader` + `Assets/Scripts/Fx/Blit/FxBlitChromaticAberration.cs` |
| 3-3 | `ParticleSystem` ベース埃オーバーレイ | `Assets/Scripts/Fx/Particles/DustOverlayBuilder.cs` |
| 3-4 | Compute Shader 畳み込み（Sobel / SobelOverlay / Reinhard Tonemap 3 kernel） | `Assets/Art/Shaders/Fx/FxSobel.compute` + `Assets/Scripts/Fx/Compute/SobelEdgeRunner.cs` |

**推奨方針（[計画レポート](.agent/plans/2026-05-03_video-fx-research.md) より）**: Phase 3-1 (CRT) + 3-3 (薄い埃) の組合せが本命。Phase 3-4 (Sobel) は全画面ではなく選択的マスクで使うのが筋。

#### 利用フロー（Editor）

1. **`FixedCamVr > Fx > Setup FxSandbox Scene`** → `Assets/Scenes/FxSandbox.unity` を生成（テストパターン入力 + 各 Phase の GameObject）
2. **`FixedCamVr > Fx > Create CRT Material`** → `Assets/Art/Materials/Fx/FxCrtMaterial.mat` を生成、Phase 3-1 用の手動セットアップ手順を Console に表示
3. FxSandbox 内の `[Fx]_BlitChromatic` / `[Fx]_DustParticles` / `[Fx]_SobelCompute` を SetActive 切替で各 Phase の見た目を比較

## 必要環境

- Unity **2022.3.62f2** LTS + Android Build Support
- Quest 3（開発者モード有効、USB デバッグ許可）
- 配信用スマホ（DroidCam または IP Webcam）×N、全デバイスが同一 LAN（5GHz Wi-Fi 推奨）

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

### 3. Main シーン配置（Phase 2）

1. `Assets/Scenes/Main.unity` を開く（現状は空）
2. `OVRCameraRig` プレハブを配置
3. Quad を配置（推奨：head から 2m 前方 / scale 1.6 × 0.9）
4. Quad に `MjpegScreen` をアタッチ
5. `Assets/Settings/` 配下に `CameraSource` ScriptableObject を作成（**Create → FixedCamVr → Camera Source**）し、配信スマホの host/port を設定して `MjpegScreen.source` に割り当て
6. URP の Unlit Material を Quad に設定（HMD 内で映像が暗くならないように）

## 配信側（スマホ）設定例

| アプリ | URL 例 |
|---|---|
| IP Webcam (Android) | `http://<phone-ip>:8080/video` |
| DroidCam | `http://<phone-ip>:4747/mjpegfeed?640x480` |

`CameraSource` に `host` / `port` / `path` を分割して入力。

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
| Debug シーン開く（HMD なし検証用） | **Ctrl+Shift+D** |
| DroidCam が繋がるか即確認 | **Tools > FixedCamVr > Ping DroidCams** → Console に結果 |
| Phone01/02 の URL/IP 編集 | Project で `Settings/Cameras/Phone01.asset` → Inspector の **Test Connection** ボタン |
| EditMode テスト実行 | **Tools > FixedCamVr > Run Streaming Tests** → Test Runner に結果 |
| Play 中にカメラ切替（HMD なし） | Hierarchy で `[Streaming]` 選択 → Inspector の **Activate / Prev / Next** ボタン |
| 受信状態の確認 | Inspector の Runtime State 行（● Connected / ○ Disconnected） |

Hierarchy のグループ見出し `=== XR ===` `=== Stage ===` `=== Logic ===` は色分けバーで強調表示される（[HierarchySeparator.cs](Assets/Scripts/Streaming/Editor/HierarchySeparator.cs)）。

## HMD なしの検証フロー

実機 Quest 3 が無い時は以下の 3 段階で詰める。VR 必須なのは「コントローラ入力経路」と「没入感の最終評価」だけで、ストリーミング系のバグの大半はこちらで再現できる。

### 1. EditMode テスト（最優先・自動）

```
Window → General → Test Runner → EditMode → Run All
```

`Assets/Tests/Streaming/` の `WrapIndex` / `BuildUrl` 系テストが pass することを CI 的に確認。Registry のラップアラウンド境界、URL 組み立てに回帰が入っていないか。

### 2. Flat デバッグシーン（OVR を外した普通の 3D シーン）

`Assets/Scenes/Debug.unity` を開いて Play モード。OVRCameraRig は無く、通常 Camera + Quad のみ。

| 操作 | 期待挙動 |
|---|---|
| Tab | 次のカメラに切替（Game ビューの映像が変わる） |
| Shift+Tab | 前のカメラに切替 |
| 1 / 2 | 直接指定 |
| Space | スクリーンの head-lock 切替（Camera 固定なので見た目は変化しない） |

スマホ側 DroidCam が動いている前提。`Assets/Settings/Cameras/Phone01.asset` の host を実 IP に書き換えてから Play。

### 3. Main シーン + OVR Headset Emulator（VR 視点シミュ）

`Assets/Scenes/Main.unity` で Play。Game ビューでは何も見えないが、`OVRCameraRig` 配下の `OVRHeadsetEmulator` で頭の動きを模擬できる:

- **Ctrl + マウス** → 頭の回転
- **Alt + マウス** → 頭の傾き

コントローラ A/B/X はキーボードからは打てないので、ここでは検証不可（→ 実機 or XR Device Simulator 経由）。代わりに **キーボード Tab/数字** で切替動作と Space で head-lock の追従挙動を見れる。

## ビルド & デプロイ

```
File → Build Settings → Build And Run
```

Quest 3 を USB-C 接続。adb 経由のワイヤレスデプロイも可。

## 開発フロー

- 実装計画は `.agent/plans/YYYY-MM-DD_<slug>.md` に置く
- シュビー（Claude Code）と共同開発。動作モード等は [CLAUDE.md](CLAUDE.md) 参照
- 領域別ルール: [.agent/rules/unity-vr.md](.agent/rules/unity-vr.md), [meta-xr.md](.agent/rules/meta-xr.md), [streaming.md](.agent/rules/streaming.md)
- コミット規約：日本語 `<type>：<要約>` 形式（グローバル `~/.claude/CLAUDE.md` 参照）

## ディレクトリ

```
fixed-cam-vr/
├── Assets/
│   ├── Art/
│   │   ├── Materials/        # マテリアル
│   │   └── Shaders/Fx/       # 映像加工シェーダ + Compute（Phase 3）
│   ├── Oculus/               # Meta XR SDK（自動生成、コミット）
│   ├── Resources/            # OVR/XR ランタイム設定 .asset
│   ├── Scenes/
│   │   ├── Main.unity        # メインシーン（Phase 2 / 2.5）
│   │   ├── Debug.unity       # HMD なし検証シーン
│   │   ├── ZoneSandbox.unity # プレイヤー位置連動切替の検証シーン（Phase 2.7）
│   │   └── FxSandbox.unity   # 映像加工 4 系統の検証シーン（Phase 3、Editor メニューで生成）
│   ├── Scripts/
│   │   ├── Streaming/        # MJPEG 受信・描画（Phase 1 / 2 / 2.5）
│   │   ├── OvrBridge/        # OVRInput ↔ asmdef 境界ブリッジ
│   │   ├── Tracking/         # プレイヤー位置連動カメラ切替（Phase 2.7）
│   │   └── Fx/               # 映像加工 / CG 合成プロトタイプ（Phase 3）
│   └── Settings/             # URP / Quality / CameraSource SO
├── Packages/manifest.json    # Meta XR SDK 等の依存
├── ProjectSettings/          # エディタ設定
├── .agent/                   # 共有ハーネス（rules / plans）
├── .claude/                  # Claude Code ハーネス（settings / memory）
├── CLAUDE.md
└── README.md
```

## 制約・既知事項

- フレーム 90Hz 維持のため、`MjpegScreen.Update` でフレーム内アロケが起きないよう `byte[]` プールと `Texture2D.LoadImage(markNonReadable: false)` で再利用
- `HttpClient` 接続失敗時は最大 30 秒の exponential backoff で再接続（[MjpegStreamReceiver.cs:86](Assets/Scripts/Streaming/MjpegStreamReceiver.cs#L86)）
- 実機検証はシュビーから自動化不可。コンパイル OK 後のビルド & 動作確認はユーザー側で行う
