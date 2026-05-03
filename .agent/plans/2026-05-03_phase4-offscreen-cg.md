---
status: planned
owner: shubie
created: 2026-05-03
slug: phase4-offscreen-cg
---

# Phase 4: スクリーン外 3D 演出 / CG 合成

## 背景と目的

Phase 3 で映像加工 4 系統（CRT FullScreen / 色収差 Blit / 埃 Particle / Sobel Compute）の比較プロトタイプが完了し、
本命方針として **CRT (Phase 3-1) + 薄い埃 (Phase 3-3)** の組合せが推奨された
（[2026-05-03_video-fx-research.md](2026-05-03_video-fx-research.md) 比較レポート参照）。

Phase 4 のゴールは、スクリーン枠の **外側（VR 空間）** に 3D 演出を配置し、
固定カメラ映像の世界と地続きの体験空間を作ることにある。
バイオハザード固定カメラ風の没入感を、VR ならではの周辺視野・立体配置で拡張する。

これまで Phase 1〜3 はすべて「映像 Quad の中身」をどう見せるかに注力していた。
Phase 4 は逆に「Quad の周囲」に何を置くかを扱う。映像の被写体と地続きに見える額縁、
スクリーン外まで漏れる光、視界周辺の動き、現実空間（パススルー）との境界処理が対象。

## 対象演出のブレインストーム

### 案 A: スクリーン周囲の暗闇 + 光漏れ（ボリュメトリックライト）

スクリーン Quad の四辺から外側に向けて、映像の輝度に応じた光漏れを出す。
URP の Volume + 自前 Decal / フォグでスクリーン周囲だけ明るく見せる。
バイオハザード初代の「テレビをつけた暗い部屋」感の再現。最も効果的だがパフォーマンスが要注意。

### 案 B: スクリーン手前を横切る粒子・霧

Quad の手前 0.3〜0.5m に薄い埃 / 霧の層を配置。
Phase 3-3 の `DustOverlayBuilder` をそのまま「スクリーン外」に拡張するイメージ。
両眼視差で「映像の前に空気がある」立体感が出るのが VR 固有のメリット。

### 案 C: スクリーン外のジャンプスケア

視界の周辺視野（HMD の左右端）に突発的にオブジェクト / 影が出る。
固定カメラ風のホラー演出として古典的だが、VR では物理的に首を振らないと正面に見えないので
没入感の落差が大きい。トリガー条件設計（ランダム / イベント駆動）が肝。

### 案 D: スクリーン両脇の物理的な「額縁」オブジェクト

CRT モニタ筐体 / 映写スクリーンの枠 / 棚 等の 3D モデルを Quad の周囲に配置する。
最も実装が単純で効果が大きい。映像が「VR 内のテレビに映っている」感が一気に出る。
Phase 4 の最初の一手として最適。

### 案 E: パススルー部分暗転と現実空間との境界演出

Phase 0 計画で言及されたパススルー暗転の延長。スクリーン周囲だけパススルーを暗くし、
スクリーンから離れるほど現実空間が見える「劇場」演出。
Meta XR の `OVRPassthroughLayer` のスタイル制御 / マスクとの組合せが必要。

### 案 F: 映像連動アンビエントライト

スクリーン内の被写体の輝度・色を毎フレーム取得し、
スクリーン外側のライト / 額縁マテリアルにフィードバックする。
映像内が赤くなったら部屋全体がうっすら赤く照らされる。
Ambilight（Philips の TV 機能）の VR 版。臨場感が一段上がる。

## 技術検証項目

- **URP のボリュメトリックライト / Volume / Decal / Shader Graph の使い分け**
  - URP 14 では Volumetric Fog が公式機能としては限定的。Decal Renderer Feature と
    自前 Quad ベースのライトシャフト / フォグ Quad のどちらが妥当か検証
- **パススルーレイヤーと CG オブジェクトの Z 順序**
  - `OVRPassthroughLayer` は `compositionDepth` で前後制御するが、CG 側の不透明オブジェクトとの
    合成順序を実機で要確認（特に案 E）
- **90Hz 維持の制約（パーティクル数 / Decal 数の上限実測）**
  - Phase 3-3 で 〜2k particles / 〜1.0ms の見立て。スクリーン外配置で両眼視差が増えるため
    実機で要再評価。Decal は枚数より重なりピクセル数が支配的なので別途計測
- **映像内の輝度・色をリアルタイムで取得する経路（案 F）**
  - 候補 1: `RenderTexture` を CPU に `ReadPixels` → 平均色（GC 大、フレーム毎は不可）
  - 候補 2: `AsyncGPUReadback` で 1〜数フレーム遅延を許容して取得
  - 候補 3: Compute Shader で平均色を縮小バッファに reduce → SetGlobalColor で他マテリアルに伝播
  - 候補 3 が最も 90Hz 親和性が高いが実装コスト中

## マイルストーン分割

### Phase 4-1: 額縁モニタ 3D モデル + マテリアル

- **目的**: 映像が「VR 内のテレビに映っている」物理的な存在感を最短で出す
- **検証シーン**: `Assets/Scenes/FrameMonitorSandbox.unity`（新規）
- **主要新規ファイル**:
  - `Assets/Art/Models/CrtMonitor/` 配下に CRT モニタ筐体メッシュ（ProBuilder で当面の暫定形状を作成）
  - `Assets/Art/Materials/Frame/CrtMonitor.mat`
  - `Assets/Prefabs/Frame/CrtMonitorRig.prefab`（モニタ + Quad（映像）+ ScreenAnchor を子に持つ）
- **実機検証項目**:
  1. モニタ枠と映像 Quad の位置ずれ / Z ファイティングがないこと
  2. 90Hz が維持されること（メッシュ自体は数百 tris で軽量）
  3. 既存の `MjpegScreen` がそのまま Quad に機能すること

### Phase 4-2: スクリーン周囲のボリュメトリックライト + Volume プロファイル

- **目的**: 暗い部屋でテレビをつけたときの光漏れを再現
- **検証シーン**: `Assets/Scenes/HaloLightSandbox.unity`（新規）
- **主要新規ファイル**:
  - `Assets/Settings/Fx/PostExposure.asset`（URP Volume プロファイル、Vignette / Tonemap）
  - `Assets/Art/Shaders/Frame/HaloGlow.shader`（Quad ベースの放射状フォグ）
  - `Assets/Scripts/Frame/HaloGlowController.cs`（namespace `FixedCamVr.Frame`）
- **実機検証項目**:
  1. ハロー Quad のアルファブレンドが両眼で違和感なく見えること
  2. シーン内の実環境（パススルー）と CG 光源の境界が破綻しないこと
  3. 90Hz 維持（フォグ Quad の overdraw が支配的なので注意）

### Phase 4-3: 映像連動アンビエントライト（RenderTexture → 平均色）

- **目的**: 映像の色が周囲の額縁 / ハローに反映される Ambilight 体験
- **検証シーン**: `Assets/Scenes/AmbilightSandbox.unity`（新規）
- **主要新規ファイル**:
  - `Assets/Art/Shaders/Frame/AverageColorReduce.compute`（縮小バッファに reduce）
  - `Assets/Scripts/Frame/AmbilightSampler.cs`（namespace `FixedCamVr.Frame.Ambilight`）
  - `Assets/Scripts/Frame/AmbilightTargetMaterial.cs`（Sampler の出力を購読してマテリアル色を更新）
- **実機検証項目**:
  1. 映像の色変化に対して 1〜2 フレーム遅延以内で追従すること
  2. `AsyncGPUReadback` 採用時、コールバックが GC を発生させないこと
  3. 90Hz 維持（Compute reduce 1 回 + Readback で 0.3〜0.5ms 想定）

### Phase 4-4: 没入演出（霧 / パーティクル / ジャンプスケア）

- **目的**: 視界周辺の動きで「世界が生きている」感を出す
- **検証シーン**: `Assets/Scenes/AmbientFxSandbox.unity`（新規）
- **主要新規ファイル**:
  - `Assets/Prefabs/AmbientFx/Dust.prefab`（Phase 3-3 の `DustOverlayBuilder` を流用）
  - `Assets/Prefabs/AmbientFx/PeripheralStartle.prefab`（案 C 用、コントローラ / イベントトリガ）
  - `Assets/Scripts/AmbientFx/PeripheralStartleTrigger.cs`（namespace `FixedCamVr.AmbientFx`）
- **実機検証項目**:
  1. パーティクルが両眼視差で「奥行きを持って」見えること
  2. ジャンプスケアトリガ後の GC スパイクがないこと
  3. 90Hz 維持（複数演出を同時 ON で要再評価）

## リスク・未解決事項

- **パススルーと CG の合成順序**
  - 案 E は Meta XR の合成パスに介入する必要があり、実機での挙動が読みにくい。
    Editor では再現できないので最初に切り分け検証が必須
- **パフォーマンス（特に Phase 4-3 の RenderTexture サンプリング頻度）**
  - 毎フレーム reduce + Readback は 90Hz 維持の安全マージンを削る。
    更新間隔を 30Hz / 15Hz に落とす設定可否を最初から組み込む
- **スクリーン外演出が没入を阻害するリスク**
  - 額縁 (Phase 4-1) は安全側だが、ハロー (4-2) / 霧 (4-4) は「気が散る」「酔う」側に倒れやすい。
    各マイルストーン完了時に **強度スライダ + ON/OFF トグル** を残し、ユーザーテストで判断
- **既存 Phase 3 の本実装方針との依存**
  - Phase 4-3 は CRT FullScreen Renderer Feature の最終出力 RT から色をサンプルするのが理想だが、
    Phase 3 の本実装はまだ未着手。Phase 4-3 着手時には Phase 3 の本実装方針を先に確定する必要あり

## 次の一手

**Phase 4-1（額縁モニタ）から着手することを推奨**。理由:

1. 既存資産（`MjpegScreen` / `ScreenAnchor`）にそのまま乗る最小変更
2. パススルーや URP 拡張機能に依存せず、純粋な MeshRenderer + Material のみ
3. 体験インパクトが最も大きい（「VR 内に CRT がある」という基本フィクションが立つ）
4. 4-2 以降の検証時にも常に背景として使える（基盤）

ディレクトリ命名は既存 `Assets/Art/Materials/Fx/` / `Assets/Prefabs/`（現状空）に倣い、
`Assets/Art/Models/CrtMonitor/`、`Assets/Prefabs/Frame/`、`Assets/Scripts/Frame/` を新設する。
namespace は `FixedCamVr.Frame.*`。asmdef は `FixedCamVr.Frame`（`FixedCamVr.Streaming` を読み取り参照）。

着手前に確認したい点:

- 額縁モニタのメッシュは ProBuilder の暫定形状で進めて良いか（後で Blender 製モデルに差し替え前提）
- Phase 4-3 着手は Phase 3 本実装後に回すか、並行でプロト先行するか
