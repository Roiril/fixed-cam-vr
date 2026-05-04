# fixed-cam-vr

Meta Quest 3 上で、固定視点カメラ（バイオハザード風）の無線映像を VR 内スクリーンに表示するアプリ。後々、映像加工 / CG 合成 / スクリーン外演出を追加していく。

## スタック

- **エンジン**: Unity 2022.3.62f2 LTS
- **レンダーパイプライン**: URP
- **ターゲット**: Meta Quest 3（Android ビルド、ネイティブ実行）
- **XR SDK**: Meta XR All-in-One SDK（メイン）+ XR Interaction Toolkit（補助）+ OpenXR Plugin
- **映像入力**: スマホ複数台 (**[fixed-cam-streamer](https://github.com/Roiril/fixed-cam-streamer) 標準**、DroidCam / IP Webcam フォールバック) → MJPEG over Wi-Fi → Unity 内デコード。`/info` メタで自動回転、`/health` で配信品質モニタ
- **言語**: C#（ランタイム）+ Shader Graph（映像加工・ポスト FX）

## ディレクトリ

- `Assets/` — Unity アセット（シーン・スクリプト・マテリアル）
- `Packages/` — Package Manager 管理ファイル（manifest.json はコミット）
- `ProjectSettings/` — エディタ設定（コミット）
- `Library/`, `Temp/`, `Logs/`, `UserSettings/` — Unity 生成物（gitignore）

`Assets/` 配下の推奨構成は [.agent/rules/unity-vr.md](.agent/rules/unity-vr.md) 参照。

## SDK セットアップ手順

[README.md](README.md) を参照。Meta XR SDK の導入はユーザーが Unity Editor で実行（Package Manager 経由のため）。

## コーディング規約

### C# 共通
- **namespace**: `FixedCamVr.<Feature>` （例: `FixedCamVr.Streaming`, `FixedCamVr.Passthrough`）
- **アセンブリ定義**: 機能単位で `.asmdef` を切る（ビルド時間短縮 + 依存明確化）
- **null 安全**: `#nullable enable` を新規 .cs に付与
- **MonoBehaviour**: フィールドは `[SerializeField] private` を基本。`public` フィールドは禁止（プロパティ経由）
- **コルーチン**: ストリーミング系は **`UniTask` か `async/await`** を優先（後で UniTask 導入予定）。当面は素の `async Task` + `CancellationToken` で書く

### Unity 規約
- **シーン**: `Assets/Scenes/` 直下。命名は `<feature>.unity`（例: `Main.unity`, `DebugRoom.unity`）
- **prefab**: `Assets/Prefabs/<Feature>/`
- **マテリアル / シェーダ**: `Assets/Art/Materials/`, `Assets/Art/Shaders/`
- **ScriptableObject 設定**: `Assets/Settings/`
- **ハードコード禁止**: マジックナンバー（カメラ URL・ポート・解像度）は ScriptableObject か `appsettings.json` 相当に切り出す

### Quest 3 / VR 固有
- **CameraRig**: Meta XR の `OVRCameraRig` を使用。自前 Camera は置かない
- **パススルー**: `OVRPassthroughLayer` 経由。`OVRManager.isInsightPassthroughEnabled` で制御
- **フレームレート**: 90Hz 維持を最優先。GC アロケーションをフレーム内で発生させない（特に MJPEG デコード経路）
- **ビルドターゲット**: Android（IL2CPP / ARM64）。x86 系は無効化

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
- パスワード・API キーをスクリプトにハードコードしない（`StreamingAssets/secrets.json` 等を `.gitignore` で除外）

## 応答スタイル

- 端的・論理的・必要最低限
- 結論から書く。前置き・総括・差分の自己解説は不要
- 表・箇条書きを優先

## Claude Code ハーネス (.claude/)

- **[memory/](.claude/memory/)** — 自動メモリ（`MEMORY.md` がインデックス）
- **[settings.json](.claude/settings.json)** — プロジェクト固有の権限・hook
- **[commands/](.claude/commands/)** — プロジェクト固有スラッシュコマンド
  - `/unity-status` — Unity MCP 接続状況・エディタ状態・直近エラーをまとめて表示
  - `/handoff` — 次セッション向けの引き継ぎプロンプトを生成

汎用 hook・スラッシュコマンドはグローバル `~/.claude/` を継承。動作モード（書き込み前承認なし、git コミット規約 等）も同様。

## 姉妹リポジトリ

- **[Roiril/fixed-cam-streamer](https://github.com/Roiril/fixed-cam-streamer)** (private, Android/Kotlin) — 配信側スマホアプリ。本リポジトリの `MjpegStreamReceiver` / `MjpegScreen` / `CameraStream` が叩く `/video` `/info` `/health` エンドポイントを提供する。`/info` の `rotationDeg` で Unity 側スクリーンを自動回転させる連携は両側を同時にいじる時に注意（[.agent/rules/streaming.md](.agent/rules/streaming.md) 参照）

## 共有ハーネス (.agent/)

### 領域別ルール（該当領域の作業前に読む）

- [unity-vr.md](.agent/rules/unity-vr.md) — Unity / VR 共通規約（シーン構成・パフォーマンス）
- [meta-xr.md](.agent/rules/meta-xr.md) — Meta XR SDK 利用規約（パススルー・カメラリグ）
- [streaming.md](.agent/rules/streaming.md) — MJPEG 取り込み + fixed-cam-streamer エンドポイント仕様
- [mcp-unity.md](.agent/rules/mcp-unity.md) — Unity MCP 経由でシーン/コンポーネント/アセットを編集する手順と落とし穴
- [git-workflow.md](.agent/rules/git-workflow.md) — Git 作業フロー（worktree 並列・cherry-pick・PR vs 直 push 判断）

### その他

- **計画**: `.agent/plans/` — 実装計画（`YYYY-MM-DD_<slug>.md`）

## Unity 編集の標準フロー

Unity プロジェクト固有の作業は MCP for Unity 経由を第一選択にする：

1. `/unity-status` で接続確認
2. 問題なければ MCP ツールで編集（`manage_scene` / `manage_gameobject` / `manage_components` 等）
3. C# 変更後は `refresh_unity` → `read_console` でコンパイルエラー確認
4. MCP が落ちている場合のフォールバック手順は [.agent/rules/mcp-unity.md](.agent/rules/mcp-unity.md) と [.claude/memory/unity_pitfalls.md](.claude/memory/unity_pitfalls.md)
