---
name: meta-xr
description: Meta XR SDK 利用規約。OVR* / Passthrough / CameraRig 関連
globs:
  - "Assets/**/Passthrough/**"
  - "Assets/**/CameraRig/**"
  - "Assets/**/*OVR*.cs"
---

# Meta XR SDK 利用規約

## CameraRig

- **唯一の Camera は `OVRCameraRig` 配下の `CenterEyeAnchor`**。自前 `Camera` を別途配置しない
- `OVRManager` はシーンに 1 つだけ。`OVRCameraRig` プレハブに含まれている
- ヘッドトラッキングを無効化したい場合も `OVRCameraRig` を使い、`OVRManager.useRecommendedMSAALevel` 等で挙動調整

## パススルー

- **有効化**: `OVRManager.isInsightPassthroughEnabled = true`
- **レイヤー**: `OVRPassthroughLayer` コンポーネント
  - `Underlay`（背景として全画面パススルー）
  - `Overlay`（前景に部分パススルー、マスクメッシュ必要）
- **暗転（背景黒）**: パススルー OFF + Skybox を黒に
- **部分透過**: `Surface-projected passthrough` にメッシュを渡す（穴開け表示）
- パススルー切替は **シーン開始時 1 回** が安全。フレーム内動的切替は避ける（数フレームの黒画面が出る）

## Composition Layer

- 高解像度スクリーン表示には `OVROverlay` を使う（通常の Quad より鮮明・低レイテンシ）
- カメラ映像表示（MJPEG → Texture2D）はまず通常 Quad で動かし、品質要件に応じて `OVROverlay` 移行を検討

## Spatial Anchor / Scene API

- 後フェーズ（スクリーン外演出）で部屋認識が必要になったら `OVRSceneManager` を導入
- 当面は固定座標で配置

## バージョン

- Meta XR All-in-One SDK v68 以降（Unity 2022.3 LTS 公式サポート）
- バージョン更新は `Packages/manifest.json` の差分を必ずユーザーに報告

## 禁止事項

- `OVRManager` を複数シーンに配置しない（DontDestroyOnLoad で 1 つだけ）
- パススルー Layer を `Update` で生成・破棄しない（コスト高）
- Quest 専用 API（`OVR*`）を Editor の Play Mode で動かす場合は **Meta XR Simulator** を使う
