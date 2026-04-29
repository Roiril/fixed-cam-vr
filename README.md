# fixed-cam-vr

Meta Quest 3 装着者の視界に、バイオハザード風の固定視点カメラ映像を VR 内スクリーンとして表示する実験。スマホ複数台（DroidCam / IP Webcam）の MJPEG を VR 内 Quad に描画し、後々パススルー暗転 + CCTV 風シェーダ + CG 合成 + スクリーン外 3D 演出に拡張していく。

## 目標フェーズ

| Phase | 内容 | 状態 |
|---|---|---|
| 0 | プロジェクト初期化・Meta XR SDK 導入 | ✅ |
| 1 | MJPEG 受信ロジック実装（[Assets/Scripts/Streaming/](Assets/Scripts/Streaming/)） | ✅ コード実装済み・シーン配置未 |
| 2 | Main シーンに OVRCameraRig + Quad + MjpegScreen を配置し、Quest 3 で映像表示 | ⏳ |
| 3 | パススルー暗転 + CCTV 風シェーダ（ノイズ・走査線・色収差） | 未着手 |
| 4 | スクリーン外 3D 演出 / CG 合成 | 未着手 |

## スタック

- **Unity**: 2022.3.62f2 LTS（URP）
- **XR**: Meta XR All-in-One SDK `201.0.0`（`Packages/manifest.json` 経由で導入済み）
- **ターゲット**: Quest 3（Android / IL2CPP / ARM64）
- **言語**: C#（`#nullable enable` / `async/await`）+ Shader Graph
- **入力**: MJPEG over Wi-Fi（DroidCam / IP Webcam）→ `MjpegStreamReceiver` でデコード

## 実装済みコード

[Assets/Scripts/Streaming/](Assets/Scripts/Streaming/) （`FixedCamVr.Streaming` namespace, asmdef 切り出し済み）

- [CameraSource.cs](Assets/Scripts/Streaming/CameraSource.cs) — ScriptableObject。host/port/path から URL 構築
- [MjpegStreamReceiver.cs](Assets/Scripts/Streaming/MjpegStreamReceiver.cs) — `multipart/x-mixed-replace` を別スレッドで受信、最新フレームを byte[] でダブルバッファ保持。再接続は exponential backoff
- [MjpegScreen.cs](Assets/Scripts/Streaming/MjpegScreen.cs) — `Renderer` の mainTexture に `Texture2D.LoadImage` で焼き込み（フレーム内 GC アロケなし）

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
│   ├── Art/Materials/        # マテリアル・シェーダ
│   ├── Oculus/               # Meta XR SDK（自動生成、コミット）
│   ├── Resources/            # OVR/XR ランタイム設定 .asset
│   ├── Scenes/Main.unity     # メインシーン（Phase 2 で構築）
│   ├── Scripts/Streaming/    # MJPEG 受信・描画
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
