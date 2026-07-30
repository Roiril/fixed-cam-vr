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

**2026-07-30 に実パッケージ（`com.meta.xr.sdk.core@201.0.0`）のソースで確認し直した節。**
以前ここに書いてあった「部分透過は Surface-projected passthrough」は**非推奨になった**（下記）。
導入演出での使い方は [2026-07-30_intro-passthrough-to-screen.md](../plans/2026-07-30_intro-passthrough-to-screen.md) §6 が正本。

- **有効化**: `OVRManager.isInsightPassthroughEnabled = true`
- **レイヤー**: `OVRPassthroughLayer` コンポーネント
  - `Underlay`（背景として全画面パススルー）
  - `Overlay`（前景。`textureOpacity` が VR とのブレンドになる）
- **⚠ on/off で切ると数百 ms の黒が出る**（公式に "black flicker" と明記・非同期に有効化されるため）。
  演出中は**切らない**。`textureOpacity`（0〜1）と style API で見せる。復帰検知は `passthroughLayerResumed`
- **`textureOpacity` は毎フレーム変えてよい**。setter は `styleDirty` を立てるだけで、LateUpdate で
  1 回だけ `SetInsightPassthroughStyle` を呼ぶ（**レイヤ再生成なし**）
- **⚠ `textureOpacity` の意味が `overlayType` 依存**。Underlay のまま 0 にしても「黒へ」ではなく
  「暗くなる」だけ。枠の外を黒にするのは alpha を書く面（下記）の仕事
- **見た目を変える API はすべて実行時に連続変化できる**（いずれも `styleDirty` 経由）:
  `SetBrightnessContrastSaturation` / `SetColorMapControls`（`Grayscale` と `GrayscaleToColor` のみ受け付ける）/
  **`SetColorLut(source, target, weight)`（2 枚の LUT 補間・滑らかな遷移用の口）** /
  `SetColorMapMonochromatic` / `DisableColorMap` / **`edgeRenderingEnabled` + `edgeColor`（実物の輪郭線）**
- **⚠ 走査線・粒状感・低解像度化・色収差はパススルーに掛けられない**（OS のコンポジタが合成するため
  全画面シェーダが通らない）。アプリ側の面を重ねて出す — **加算とアルファは通る / 引き算と彩度操作は通らない**
- **部分透過（枠・穴あけ）は Passthrough Windows**: フレームバッファの alpha を書く方式。
  alpha 0 の領域だけパススルーが透ける。`float4(0,0,0,alpha)` / `BlendOp Add` / `Blend Zero SrcAlpha` /
  render queue 5000。**`AddSurfaceGeometry`（surface-projected）は 201.0.0 で `[Obsolete]`** なので使わない
- **⚠ 深度が効かない**。パススルー面と VR オブジェクトの前後関係は深度で解決されない。
  重ね順は `compositionDepth` と Overlay/Underlay で設計する
- **⚠ `projectionSurfaceType` はインスタンス化直後しか変えられない**。実行時に変えるならレイヤの
  disable → enable が必要
- **⚠ ガーディアン表示はコードから消せない**（`OVRBoundary.SetVisible` が 201.0.0 で `[Obsolete]`・
  「will not be supported in OpenXR」）。視点を動かす演出では実頭位置が境界に触れる前提で設計する

## Composition Layer

- 高解像度スクリーン表示には `OVROverlay` を使う（通常の Quad より鮮明・低レイテンシ）
- カメラ映像表示（MJPEG → Texture2D）はまず通常 Quad で動かし、品質要件に応じて `OVROverlay` 移行を検討

## Spatial Anchor / Scene API

- 後フェーズ（スクリーン外演出）で部屋認識が必要になったら `OVRSceneManager` を導入
- 当面は固定座標で配置（部屋の幾何は show.json の `layout.room` を著作する側で持っている）
- **Scene model は OS が管理・永続し、全アプリから読める**（公式）。つまり
  **設営時に 1 回 Space Setup すれば、来場者は何もしなくてよい**。将来 `layout.room` の手著作を
  やめたくなったときの選択肢

## バージョン

- **実際に入っているのは `com.meta.xr.sdk.core` / `com.meta.xr.sdk.all` の `201.0.0`**
  （`Packages/packages-lock.json` で確認。2026-07-30 時点）
- 公式ドキュメントと実パッケージが食い違ったら**実パッケージのソースを優先する**
  （`Library/PackageCache/com.meta.xr.sdk.core@<ver>/Scripts/` を読む）。ドキュメントは更新日が本文に出ない
- バージョン更新は `Packages/manifest.json` の差分を必ずユーザーに報告

## 禁止事項

- `OVRManager` を複数シーンに配置しない（DontDestroyOnLoad で 1 つだけ）
- パススルー Layer を `Update` で生成・破棄しない（コスト高）
- Quest 専用 API（`OVR*`）を Editor の Play Mode で動かす場合は **Meta XR Simulator** を使う
