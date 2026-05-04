---
status: done
created: 2026-05-03
updated: 2026-05-03
completed: 2026-05-03
slug: video-fx-research
owner: shubie (worktree: nice-banach-968b48)
---

# 映像加工 / CG 合成手法の検討

## 概要

MJPEG で取り込んだスマホ映像（既存 `MjpegScreen` の出力 Texture）に対し、
バイオハザード固定カメラ風の表現を狙った映像加工 / CG 合成手法を 4 系統比較検証する。
成果物は **動くプロトタイプ + 比較レポート**。本実装への昇格は別フェーズ。

別スレッドの作業（プレイヤー位置連動カメラ切替）と並行するため、
**既存シーン / Streaming・OvrBridge スクリプトには一切触らない**。
作業領域はすべて新規（`Assets/Scripts/Fx/`, `Assets/Art/{Shaders,Materials}/Fx/`,
`Assets/Scenes/FxSandbox.unity`）に閉じる。

## 衝突回避ルール（厳守）

- 既存 `Main.unity` / `Debug.unity` / `SampleScene.unity` を保存しない
- 既存 `Assets/Scripts/Streaming/*` / `Assets/Scripts/OvrBridge/*` を編集しない（参照のみ可）
- `ProjectSettings/` / `Packages/manifest.json` 不触
- namespace は `FixedCamVr.Fx.*` 限定
- 新規 asmdef `Assets/Scripts/Fx/FixedCamVr.Fx.asmdef`、`FixedCamVr.Streaming` を読み取り参照のみ

## 技術前提（事前調査済み）

- URP 14.0.12 入り（Shader Graph / Blit / Compute すべて利用可）
- **VFX Graph は manifest 未追加** → Phase 3 着手時にユーザー承認を取り `com.unity.visualeffectgraph` 追加要請
- Quest 3 / 90Hz 維持が KPI。Unlit ベース、フレーム内 GC 0 を目指す
- 実機計測は親（ユーザー）依頼（自動化不可）。Editor では Frame Debugger / Profiler で代替

## 検証手法（最低 4 系統）

| # | 手法 | 入力 | 出力 | 候補表現 |
|---|---|---|---|---|
| 1 | Shader Graph ポストエフェクト（Fullscreen Pass Renderer Feature 経由） | Camera Color | 同 | CRT スキャンライン / グレイン / ビネット |
| 2 | `Graphics.Blit` + 自前 `.shader`（HLSL 直書き） | RT | RT | 古典フルスクリーンエフェクト・色収差 |
| 3 | VFX Graph（要パッケージ追加） | テクスチャサンプル | パーティクル | 霧・埃・血飛沫合成 |
| 4 | Compute Shader 畳み込み | Texture2D → RT | RT | エッジ検出・トーンマップ |

## 入力 Texture の取り方（Main 非依存）

実機なしの Editor 検証が前提なので、**Main 経由は使わない**。

- `Assets/Scenes/FxSandbox.unity`（新規）に静止画 / 動画クリップ（VideoPlayer + RenderTexture）を置く
- 必要なら薄いブリッジ `FxSourceBinder`（`Renderer.sharedMaterial.mainTexture` を読むだけ）を補助で用意
- どちらも `MjpegScreen` を改変しない

## フェーズ

### Phase 0: サンドボックス基盤（共通）

- [ ] `Assets/Scripts/Fx/FixedCamVr.Fx.asmdef` 新規作成（references: `FixedCamVr.Streaming` 読取のみ）
- [ ] `Assets/Scenes/FxSandbox.unity` 新規作成（Camera + Directional Light + Quad、`OVRCameraRig` は不要）
- [ ] テスト用入力: フリーの動画クリップを `Assets/Art/Fx/Samples/` に配置 → `VideoPlayer` で RenderTexture に出力
- [ ] `FxSourceBinder.cs`（namespace `FixedCamVr.Fx.Source`）— `Renderer.sharedMaterial.mainTexture` を `Texture` プロパティで公開する読み取り専用ブリッジ

### Phase 1: Shader Graph ポストエフェクト

- [ ] `Assets/Art/Shaders/Fx/CrtPostFx.shadergraph` 作成（スキャンライン + グレイン + ビネット）
- [ ] URP `FullScreenPassRendererFeature` を **新規 Renderer Asset**（`Assets/Settings/Fx/FxRenderer.asset`）に追加。**既存 URP Renderer は触らない**
- [ ] `FxSandbox` の Camera にこの新 Renderer を割り当て
- [ ] スクショ取得 → レポートに添付

### Phase 2: Blit + 自前 .shader

- [ ] `Assets/Art/Shaders/Fx/BlitChromaticAberration.shader`（Unlit / HLSL）
- [ ] `Assets/Scripts/Fx/Blit/FxBlitPass.cs`（`MonoBehaviour` 経由で `Graphics.Blit` を `OnRenderImage` 風に呼ぶ。URP では `RenderTexture` ↔ Quad の自前パス）
- [ ] `_ScreenParams` / 時間 uniform の供給ヘルパ

### Phase 3: VFX Graph 合成（パッケージ追加判断あり）

- [ ] **着手前にユーザーへ `com.unity.visualeffectgraph` 追加可否を確認** — 不可なら本フェーズ Skip し代替案（`ParticleSystem` ベース）に切替
- [ ] OK の場合: `Assets/Art/Fx/VFX/DustOverlay.vfx` で映像 Quad の前面にパーティクル、Sample Texture ノードで映像色から発生位置を変調
- [ ] VFX 単体 GPU コスト測定（Frame Debugger）

### Phase 4: Compute Shader 畳み込み

- [ ] `Assets/Art/Shaders/Fx/SobelEdge.compute`（Sobel エッジ検出）
- [ ] `Assets/Scripts/Fx/Compute/SobelEdgeRunner.cs`（ディスパッチ + RT 確保再利用）
- [ ] トーンマップ（Reinhard）も同シェーダ内 kernel として追加

### Phase 5: 計測 & レポート

各手法について以下を記載（実機未検証は明記）：

- Quest 3 90Hz 維持可否（Editor 計測 + 理論値）
- フレーム内 GC アロケーション（Profiler スクショ or 理論値）
- 実装コスト（行数 / セットアップ手順数）
- バイオハザード固定カメラ風の表現適性（主観 ★1〜5）
- スクリーンショット 1 枚

レポートは本ファイル末尾に追記する形（別ファイルにしない）。

## 成果物

- 新規シーン: `Assets/Scenes/FxSandbox.unity`
- 新規スクリプト: `Assets/Scripts/Fx/**/*.cs`（namespace `FixedCamVr.Fx.*`）
- 新規シェーダ / マテリアル: `Assets/Art/Shaders/Fx/`, `Assets/Art/Materials/Fx/`
- 新規 Renderer Asset: `Assets/Settings/Fx/FxRenderer.asset`
- レポート: 本ファイル `## 比較レポート` セクション

## 報告ポイント

1. **本計画提示時点（今）** — ユーザーに見せて承認待ち
2. **Phase 3 着手前** — VFX Graph パッケージ追加可否確認
3. **Phase 5 完了時** — レポート追記して完了報告。実機ビルドはユーザー依頼

## 比較レポート（2026-05-03）

### 検証範囲

- 4 系統すべてのプロトタイプを実装済み（906 行: C# 581 + HLSL/Compute 245 + asmdef 等 80）
- **実機 / Editor での動作検証は未実施**（worktree が Unity Editor の参照外のため）。merge 後にユーザー側で `FixedCamVr/Fx/Setup FxSandbox Scene` メニューを実行して確認する想定
- 計測値は理論値・Unity / Quest 3 の標準的なベンチに基づく見積り

### 動作確認手順（merge 後にユーザーが実行）

1. Unity Editor で本ブランチを取り込み、`Assets/Scenes/FxSandbox.unity` を生成するために
   メニュー **`Tools > FixedCamVr > Setup > Setup FxSandbox Scene`** を実行
2. **`Tools > FixedCamVr > Setup > Create CRT Material`** を実行（FxCrtMaterial.mat 生成）
3. Phase 1 を有効化したい場合は、ログ指示に従い `Assets/Settings/Fx/FxRenderer.asset` を
   手動作成し、`FullScreenPassRendererFeature` に FxCrtMaterial を割り当て、
   Camera の Renderer 欄に当てる
4. Phase 2 / 3 / 4 はシーンの `[Fx]_BlitChromatic` / `[Fx]_DustParticles` / `[Fx]_SobelCompute`
   GameObject を SetActive 切り替えで個別検証

### 各手法の比較

| 項目 | Phase 1: URP FullScreen Renderer Feature | Phase 2: Blit + 自前 .shader | Phase 3: ParticleSystem オーバーレイ | Phase 4: Compute Shader 畳み込み |
|---|---|---|---|---|
| 入力 | カメラ Color RT（パイプライン内） | 任意 Texture（手動駆動） | なし（重ねるだけ） | 任意 Texture |
| 出力 | カメラ Color RT 上書き | 専用 RT → Renderer | パーティクル描画 | 専用 RT → Renderer |
| 90Hz 維持 | ◎ 1280x720 で 0.2-0.3ms 想定 | ○ 同等。Blit 1 回 + frag pass | ○ 〜2k particles なら余裕 | △ Sobel は 8x8 dispatch が密、要検証 |
| GC アロケーション | 0（マテリアル設定のみ） | 0（フィールド再利用） | 0（コンフィグは Reset 時のみ） | 0（SetTexture 系は no-alloc） |
| 実装コスト | 中（シェーダ + Renderer Asset 手動セットアップが必要） | 低（シェーダ + MonoBehaviour） | 低（ParticleSystem 設定のみ） | 中（Compute + Runner、enableRandomWrite RT） |
| 行数 | 66 (shader) + 38 (controller) = 104 | 53 (shader) + 83 (runner) = 136 | 79 (builder) | 67 (compute) + 95 (runner) = 162 |
| バイオハザード適性 | ★★★★★ 全画面に質感を載せる王道 | ★★★★ レンズの古さを表現可能 | ★★★ 空気感補強。単独だと弱い | ★★★★ 暗部輪郭強調が固定カメラ向き |
| 現実装の限界 | Renderer Asset 手動セットアップが残る | スクリーン Quad 単体のみ（パイプライン全体には乗らない） | ParticleSystem は VFX Graph に比べて GPU 効率劣る | Quest 3 のモバイル GPU で Compute は 90Hz 維持の安全マージン未確認 |

### Quest 3 90Hz 維持の見立て（要実機検証）

- **CRT (Phase 1)**: 720p フルスクリーン 1 pass。Adreno 740 で 0.3ms 程度。安全マージン大
- **色収差 (Phase 2)**: RGB 3 サンプル。同 0.4ms 程度。マルチエフェクト合成しなければ余裕
- **パーティクル (Phase 3)**: max 2048 で 0.8-1.2ms 程度。Billboard で OK。VR 両眼で 2 倍コスト要注意
- **Sobel (Phase 4)**: 8 サンプル + 算術。720p で 0.5-0.7ms と推定。複数フィルタチェーンするなら本実装で要再評価
- 全部同時に乗せると 90Hz 厳しい。**本実装ではどれか 1 系統 + 軽量補助** が現実解

### 推奨される本実装方針（次フェーズ向け）

1. **第一候補: Phase 1 (CRT FullScreen) + Phase 3 (薄い埃パーティクル)**
   - 全画面の質感とスクリーン外への空気の連続性を両立。コスト低
2. **第二候補: Phase 2 (色収差) を MjpegScreen の出力 RT に直結する形で本実装**
   - Renderer Feature の手動セットアップを避けたい場合。スクリーン Quad だけ加工する
3. **Phase 4 (Sobel/Tonemap) は表現として強すぎ**、固定カメラ風には全画面適用ではなく
   選択的（ScreenAnchor 領域のみ・暗部マスク）に使うのが筋
4. **VFX Graph (パッケージ追加) は次フェーズで再評価**
   - 埃 / 飛沫 を映像のエッジに沿って配置するなら Texture サンプラ必須で導入価値あり

### 既存コードへの影響

- **既存 Streaming / OvrBridge / 既存シーンには一切変更を加えていない**
- 新規 asmdef `FixedCamVr.Fx` は `FixedCamVr.Streaming` を読み取り参照のみ
  （実際の依存は `FxSourceBinder.cs` で `Renderer.sharedMaterial.mainTexture` を参照する経路のみ。
  Streaming 型自体は import していないので、参照削除も容易）
- merge 後にコンパイルエラーが出ないことを Unity Editor 側で確認するのはユーザー作業

### TODO（次フェーズに引き継ぐ）

- [ ] 実機 Quest 3 で各 Phase の GPU タイム計測（OVRMetrics or Frame Debugger）
- [ ] Phase 1 の Renderer Asset を `.asset` 自動生成スクリプトで完全自動化
  （URP 14 の `FullScreenPassRendererFeature` インスタンス化 API 確認）
- [ ] 複数 Phase を同時適用する合成パスを実装（CRT + 色収差を 1 シェーダにまとめる等）
- [ ] バイオハザード参考映像と並べた主観評価セッション


## 自律改善ログ

### 2026-05-03: 親プロジェクト経由でのコンパイル検証

- 親プロジェクト（既に Unity Editor + MCP が動いている）に Fx 関連ファイルだけ一時コピー
- `refresh_unity` + `read_console` で **C# 全ファイル + シェーダ 4 / Compute 1** がコンパイル通ることを確認
- メニュー `FixedCamVr/Fx/Create CRT Material` と `Setup FxSandbox Scene` の実行成功も確認
- 検出した修正点: `FxCrtPostFx.shader` の include パス
  `Packages/com.unity.render-pipelines.universal/Shaders/PostProcessing/Common/Common.hlsl`
  は URP 14 では存在しない → 削除（必要な定義は `Blit.hlsl` で揃う）
- 検証後、親プロジェクトに降ろしたファイルはすべて削除し、別シュビーの作業差分（Tracking/, ZoneSandbox.unity 等）に影響を与えていないことを `git status` で確認

### 2026-05-03: Unity MCP 不採用 / Phase 1 を HLSL シェーダに変更

- Unity Editor が見ているのは親ディレクトリ `C:/Users/kouga/Projects/Unity/fixed-cam-vr/Assets`。worktree（`.claude/worktrees/nice-banach-968b48/`）は別物
- Unity MCP 経由で作成したアセットは親に書かれ、worktree ブランチのコミットに含められない
- → 本作業はファイルシステムで worktree 内に直接書き込む。Unity MCP は使わない（読み取りも使わず、ユーザーが merge 後に Editor で開いて確認する想定）
- 副作用として `.shadergraph`（バイナリ的 YAML）の手書きが非現実的 → Phase 1 を **HLSL `.shader` + URP `FullScreenPassRendererFeature`** に変更
- Phase 1 と Phase 2 の差別化は維持: Phase 1 = URP パイプライン組込み（Renderer Feature）、Phase 2 = MonoBehaviour から `Graphics.Blit` 手動駆動（パイプライン外）
- VFX Graph も同様の理由で `.vfx` 手書きが困難 → Phase 3 は **`ParticleSystem` + Texture Sheet で映像前面合成** に切り替え（パッケージ追加不要のメリットも）

