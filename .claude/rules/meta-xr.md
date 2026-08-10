---
name: meta-xr
description: Meta XR SDK 利用規約。OVR* / Passthrough / CameraRig 関連
paths:
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

- **⚠ その前に `OculusProjectConfig` の `_insightPassthroughSupport` を 1 (Supported) にする**（2026-07-31 実害）。
  0 (None) のままだと実機で `Failed to initialize Insight Passthrough ... Failure_NotInitialized` が出て、
  **アプリからパススルーを一切制御できない**。画面にはシステム側のパススルーが映り続けるので
  「出ている」ように見えるが、彩度・輪郭線・不透明度の API は全部無効。導入演出は主役が現実の映像なので、
  この 1 行で演出が丸ごと死ぬ。しかも**テレメトリ上は段が進む**ので、ログを見ている限り気づけない
  （`.claude/memory/onsite_experience_test.md` の「画を見ないと分からないもの」）。
  `insightPassthroughEnabled`（bool）は **`[Obsolete]` で効果が無い** — 触るのは `_insightPassthroughSupport`。
  `Required (2)` は非対応機で起動しなくなるので `Supported (1)`。**この .asset は同居 2 アプリの共有資源**
- **有効化**: `OVRManager.isInsightPassthroughEnabled = true`
- **⚠ カメラの背景は不透明な黒 (a=1) のままにする。透明 (a=0) にしてはいけない**（2026-07-31 に
  一度 a=0 にして同日戻した）。**Underlay パススルーは「アプリが描かない画素」(alpha 0) にしか
  出ない**のは事実だが、**穴を開けるのは覆い (`IntroVeil`) の仕事で、カメラの背景ではない**。
  覆いは `Blend Zero SrcAlpha`（出力 = dst × srcAlpha）で全画面に掛かり、パススルーを見せたい段では
  srcAlpha=0 を書くので、背景が不透明でも全面パススルーになる。
  逆に背景を a=0 にすると、**乗算ブレンドは alpha を減らすことしかできない**ので 0 を 1 へ戻せず、
  **枠の外を黒く閉じる演出が原理的に起こらない**（実測: 枠の外に現実が残り続け、次の段で
  パススルーが切れて一気に黒くなった）。
  「パススルーが一切出ない」の真因は `_insightPassthroughSupport = 0` と、
  `IntroVeil` シェーダがビルドから剥がれていたこと（下記 `Shader.Find` の罠）の 2 つ。
  設定するのは `MainDemoSceneSetup.EnsurePassthroughStyler`
- **⚠ ただし「覆いが走っていない時間」には現実を見せる手段が 1 つも無かった**（2026-08-09 実害）。
  alpha 0 を書く面は `IntroVeil` だけで、しかもそれは導入・終幕の専用。つまり
  **演出の外側（起動直後・位置合わせ中・本編前）は原理的に現実が 1 画素も出ない**。
  実害はユーザー報告「トリガーを長押ししてもパススルーは何も変わらない」＝
  **位置合わせ（線を実物に重ねる作業）が成立しない**。
  → **位置合わせ中だけ、実行時にカメラ背景の alpha を 0 にする**
  （[`PassthroughStyler.SetBackgroundOpen`](../../Assets/Scripts/OvrBridge/PassthroughStyler.cs)。
  元の色を覚えてから書き換え、抜けたら必ず戻す）。
  **覆いを全開にする方法は採らない** — 覆いは queue 4900 の `Blend Zero SrcAlpha` なので、
  走っているだけで**合わせる対象である登録ワイヤーと文字（queue 3000）を黒へ潰す**。
  上の「a=1 のままにする」は**覆いが走るフレームの制約**であって、常時の制約ではない。
  ⚠ **焼き込みは従来どおり a=1**（`analyze-xp-log.py` の `boot bg=` は 0 台であることを要求する）
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
