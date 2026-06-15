# fixed-cam-vr

Meta Quest 3 上で、固定視点カメラ（バイオハザード風）の無線映像を VR 内スクリーンに表示するアプリ。後々、映像加工 / CG 合成 / スクリーン外演出を追加していく。

## ⚠ 同居 2 アプリ — 並列作業の干渉防止（全シュビー必読）

この Unity プロジェクトには **2 つの独立アプリが 1 プロジェクトに同居**している：**廻リ視（FixedCam・本体 / `FixedCamVr.*` / `Assets/Scripts/` / `Main.unity`）** と **TableDuo（手アバター調査 / `TableDuoVr.*` / `Assets/TableDuo/` / `TableDuoMain.unity`）**。

別シュビー（並列エージェント / worktree / 別セッション）と本シュビーが**別々のアプリを同時に進めることがある**。根底原則：

1. **コードは完全分離** — `FixedCamVr.*` ↔ `TableDuoVr.*` の asmdef 相互参照禁止。相手のコード領域・シーン・prefab に手を出さない
2. **共有資源は両方に効く** — Unity Editor（1 インスタンス）/ `ProjectSettings/` / 共有 `.asset`（URP・OVR・XR）/ `manifest.json` は 2 アプリ共有。片アプリ専用の都合で弄らない
3. **Unity Editor とビルドは奪い合わない** — MCP 編集・Play・Build は単一 Editor を共有。**並列化せず逐次**。ビルドは productName/ID を一時 swap するので **2 ビルド同時起動厳禁**（片方が他方の ID で焼ける）

詳細・並列化可否表・チェックリスト → **[.claude/rules/parallel-projects.md](.claude/rules/parallel-projects.md)（Unity / 両アプリのコードを触る前に必読）**

## スタック

- **エンジン**: Unity 2022.3.62f2 LTS
- **レンダーパイプライン**: URP
- **ターゲット**: Meta Quest 3（Android ビルド、ネイティブ実行）
- **XR SDK**: Meta XR All-in-One SDK（メイン）+ XR Interaction Toolkit（補助）+ OpenXR Plugin
- **映像入力**: スマホ複数台（機種混在可・想定 iPhone 13 Pro ×2 + Pixel）→ MJPEG over Wi-Fi → Unity 内デコード。Android は **[fixed-cam-streamer](https://github.com/Roiril/fixed-cam-streamer) 標準**（`/info` メタで自動回転、`/health` で配信品質モニタ）、iPhone は IP Camera Lite 等の汎用 MJPEG アプリ（Basic 認証対応済み）。DroidCam / IP Webcam フォールバック。詳細 [.claude/rules/streaming.md](.claude/rules/streaming.md)
- **言語**: C#（ランタイム）+ Shader Graph（映像加工・ポスト FX）

## ディレクトリ

- `Assets/` — Unity アセット（シーン・スクリプト・マテリアル）
- `Packages/` — Package Manager 管理ファイル（manifest.json はコミット）
- `ProjectSettings/` — エディタ設定（コミット）
- `Library/`, `Temp/`, `Logs/`, `UserSettings/` — Unity 生成物（gitignore）

`Assets/` 配下の推奨構成は [.claude/rules/unity-vr.md](.claude/rules/unity-vr.md) 参照。

## SDK セットアップ手順

[README.md](README.md) を参照。Meta XR SDK の導入はユーザーが Unity Editor で実行（Package Manager 経由のため）。

## コーディング規約

詳細は領域別ルールに集約（path-scope `globs:` で on-demand load）：

- **C# / Unity / VR 共通**（namespace / asmdef / SerializeField / 90Hz / シーン構成 等）→ [.claude/rules/unity-vr.md](.claude/rules/unity-vr.md)
- **Meta XR / OVR / パススルー / CameraRig** → [.claude/rules/meta-xr.md](.claude/rules/meta-xr.md)
- **MJPEG / 配信エンドポイント / GC ゼロ受信** → [.claude/rules/streaming.md](.claude/rules/streaming.md)

最低限の前提だけここに：

- ビルドターゲットは **Android / IL2CPP / ARM64**（x86 系は無効）
- パスワード・API キーをスクリプトにハードコードしない（`StreamingAssets/secrets.json` 等は `.gitignore` で除外）

## 自動テスト運用

Unity プロジェクトは Web と違い `localhost` でブラウザ検証ができないため、シュビーは以下で代替する：

1. **コンパイル検証**: Unity を `-batchmode -quit` で起動してエラー有無確認（時間かかる）
2. **Play Mode テスト**: Unity Test Framework（EditMode/PlayMode）で書ける範囲はテスト化
3. **実機 Quest 3 動作確認**: シュビーから自動化不可。ビルド & デプロイ後の動作確認はユーザーに依頼
4. 完了報告時は「コンパイル OK / 実機未検証（手動確認お願いします）」を明示

## 禁止事項

- `ProjectSettings/` を理由なく変更しない（特に GraphicsSettings / QualitySettings）
- `Packages/manifest.json` の編集はユーザーに事前報告
- `Library/` をコミットしない

## 応答スタイル

- 端的・論理的・必要最低限
- 結論から書く。前置き・総括・差分の自己解説は不要
- 表・箇条書きを優先

## Claude Code ハーネス (.claude/)

- **[memory/](.claude/memory/)** — 自動メモリ（`MEMORY.md` がインデックス）
- **[settings.json](.claude/settings.json)** — プロジェクト固有の権限・hook
- **[skills/](.claude/skills/)** — シュビーが状況に応じて自律的に呼ぶスキル群（ユーザーは打たない）
  - `unity-status` — Unity MCP / Editor 状態 / コンソールエラーを一括取得（Unity 作業の入り口）
  - `unity-mcp` — Unity MCP 接続診断と再登録（`unity-status` でツール不在を確認した時の次手）
  - `adb-logcat` — Quest 3 / Android 実機ログ取得（unity / xr / streamer / crash フィルタ）
  - `handoff` — 次セッション向けの引き継ぎプロンプト生成
  - `quest-build` — 廻リ視 / TableDuo の APK を Quest にビルド & インストール（BuildVariants メニュー / 2 アプリ分離 / adb）
  - `streamer-android-build` — 姉妹リポ APK ビルド & 実機インストール
  - `streaming-offline-test` — 実機なしで MJPEG パイプラインを Editor 単体検証
  - `unity-prefab-fields` — prefab YAML への SerializeField 反映作法

汎用 hook・グローバルスキルはグローバル `~/.claude/` を継承。動作モード（書き込み前承認なし、git コミット規約 等）も同様。

## ブラウザ検証ツール + オペレータ卓（tools/web-compositor/）

**🎛 コンソールタブ＝本番オペレータ卓**：show.json を正に Unity（`ShowControlClient`）を遠隔制御（cue 発火・カメラ固定・ポスト FX。計画 [.claude/plans/2026-06-11_web-operator-console.md](.claude/plans/2026-06-11_web-operator-console.md)）。**✂ コンポジット検証タブ**＝Unity を開かずに**映像合成（事前撮影 × リアルタイム配信）をブラウザで検証**する自作 WebGL2 ツール。マスク切り貼り → 色統計マッチング → ラプラシアンブレンディング → 後段フィルタ。配信フレームの**キャプチャ/録画（PC 内 `captures/` に保存）**、**生成プロンプト管理（画像/動画で分類、`prompts.json`）**、保存物を**エクスプローラーで開く**機能を含む。AI 動画生成（貞子系・無血ホラー）の素材づくりとプロンプトの蓄積もここ。

- 起動: `tools/web-compositor/serve.ps1`（capture-server.py 経由・python 必須）→ `http://localhost:8099/`
- 詳細（場所/パイプライン/サーバ API/AI 動画生成の運用知見）→ [.claude/memory/web_compositor.md](.claude/memory/web_compositor.md)
- 前提: streamer 側 `/video` の CORS 許可が必要（[fixed-cam-streamer](https://github.com/Roiril/fixed-cam-streamer) 側で対応済み・別途コミット要）

## 同居サブプロジェクト: TableDuo（テーブル2人非対称VR）

fixed-cam-vr 本体とは別に、**テーブルを囲む2人マルチプレイ VR**（フルアバター vs 手だけアバター、ハンドトラッキング、NGO LAN 直結）が同居している。

- 要件: [docs/table-duo/requirements.md](docs/table-duo/requirements.md) / 実行計画: [.claude/plans/2026-06-10_table-duo_phase0-3.md](.claude/plans/2026-06-10_table-duo_phase0-3.md)
- **体験の目的＝調査**: 手だけアバターとの対人インタラクション観察（手は無言・ジェスチャーのみ）。設計 [docs/table-duo/study-design.md](docs/table-duo/study-design.md) / 実施手順 [docs/table-duo/study-protocol.md](docs/table-duo/study-protocol.md) / 計画 [.claude/plans/2026-06-11_table-duo_study.md](.claude/plans/2026-06-11_table-duo_study.md)
- コード: `Assets/TableDuo/`（自己完結）。namespace `TableDuoVr.<Feature>`、asmdef `TableDuoVr.*`
- **干渉防止**: 並列作業の絶対規約は [.claude/rules/parallel-projects.md](.claude/rules/parallel-projects.md) に集約（asmdef 相互参照禁止・共有資源・ビルド逐次・並列化可否）。冒頭「⚠ 同居 2 アプリ」も参照
- シーン生成: `Tools/FixedCamVr/Setup/Setup TableDuo Scene`（冪等再実行可）
- 検証 3 層: L0=FakeHandDriver（HMD 0台）/ L1=Editor+Link↔実機1台 / L2=実機2台（フェーズ締めのみ）
- 依存追加: `com.unity.netcode.gameobjects` 1.12.0（このサブプロジェクト用）

## 姉妹リポジトリ

- **[Roiril/fixed-cam-streamer](https://github.com/Roiril/fixed-cam-streamer)** (private, Android/Kotlin) — 配信側スマホアプリ。本リポジトリの `MjpegStreamReceiver` / `MjpegScreen` / `CameraStream` が叩く `/video` `/info` `/health` エンドポイントを提供する。`/info` の `rotationDeg` で Unity 側スクリーンを自動回転させる連携は両側を同時にいじる時に注意（[.claude/rules/streaming.md](.claude/rules/streaming.md) 参照）

### 領域別ルール（該当領域の作業前に読む）

`.claude/rules/` 配下：

- [unity-vr.md](.claude/rules/unity-vr.md) — Unity / VR 共通規約（シーン構成・パフォーマンス）
- [meta-xr.md](.claude/rules/meta-xr.md) — Meta XR SDK 利用規約（パススルー・カメラリグ）
- [streaming.md](.claude/rules/streaming.md) — MJPEG 取り込み + fixed-cam-streamer エンドポイント仕様
- [mcp-unity.md](.claude/rules/mcp-unity.md) — Unity MCP 経由でシーン/コンポーネント/アセットを編集する手順と落とし穴
- [git-workflow.md](.claude/rules/git-workflow.md) — Git 作業フロー（worktree 並列・cherry-pick・PR vs 直 push 判断）
- [parallel-projects.md](.claude/rules/parallel-projects.md) — **同居 2 アプリ（廻リ視 / TableDuo）の干渉防止**（共有資源・ビルド逐次・並列化可否・コード分離）
- [troubleshooting.md](.claude/rules/troubleshooting.md) — 「動かない」時に層を切り分ける診断フロー（配信/ネット/Unity/Meta XR/ビルド の責任マップ）

### その他

- **計画**: `.claude/plans/` — 実装計画（`YYYY-MM-DD_<slug>.md`）

## Unity 編集の標準フロー

Unity プロジェクト固有の作業は MCP for Unity 経由を第一選択にする：

1. `unity-status` スキルで接続確認
2. 問題なければ MCP ツールで編集（`manage_scene` / `manage_gameobject` / `manage_components` 等）
3. C# 変更後は `refresh_unity` → `read_console` でコンパイルエラー確認
4. MCP が落ちている場合のフォールバック手順は [.claude/rules/mcp-unity.md](.claude/rules/mcp-unity.md) と [.claude/memory/unity_pitfalls.md](.claude/memory/unity_pitfalls.md)
