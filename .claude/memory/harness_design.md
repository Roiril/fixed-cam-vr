---
name: harness_design
description: CLAUDE.md / .claude/ の役割分担と動作モードの根拠（fixed-cam-vr 現状版）
type: project
---

# ハーネス設計（fixed-cam-vr）

## レイヤー

| 場所 | 役割 |
|---|---|
| `~/.claude/CLAUDE.md` | グローバル動作規約（書き込み前承認なし、コミット規約、自己改善ループ） |
| `<project>/CLAUDE.md` | プロジェクト規約（スタック・コーディング規約・自動テスト方針・Unity 編集の標準フロー） |
| `<project>/.claude/memory/` | 自動メモリ（`MEMORY.md` がインデックス） |
| `<project>/.claude/settings.json` | プロジェクト固有 hook / 権限（Unity MCP ツール allowlist） |
| `<project>/.claude/commands/` | プロジェクト固有スラッシュコマンド |
| `<project>/.claude/rules/*.md` | 領域別ルール（YAML frontmatter の `globs:` で path-scope） |
| `<project>/.claude/plans/` | 実装計画 (`YYYY-MM-DD_<slug>.md`) |

## 現在のルール構成

`.claude/rules/`:
- `unity-vr.md` — Unity / VR 共通（`Assets/**`, `ProjectSettings/**` で発火）
- `meta-xr.md` — Meta XR SDK / OVR* 利用規約（`Assets/**/Passthrough/**` 等）
- `streaming.md` — MJPEG / WebRTC 取り込み（`Assets/**/Streaming/**`, `*Mjpeg*.cs`）
- `mcp-unity.md` — Unity MCP for Unity 経由の編集規約（`Assets/**`, `Packages/**` 等）
- `git-workflow.md` — Git ブランチ・worktree・PR 運用
- `troubleshooting.md` — 層別トラブルシュート（配信/ネット/Unity/MCP/Meta XR/ビルド）

## 現在のスラッシュコマンド

`.claude/commands/`:
- `/unity-status` — Unity MCP 接続状況・エディタ状態・直近エラーをまとめ表示
- `/handoff` — 次セッション向けの引き継ぎプロンプトを生成

## 動作モード

グローバル `~/.claude/CLAUDE.md` の規約をそのまま継承：
- 書き込み前承認は不要
- 不可逆操作（force push / reset --hard / 依存削除 / .env 編集）は事前確認
- コミットメッセージは `<type>：<日本語要約>` 形式（全角コロン）

## Unity 固有の調整（toio 系 Web プロジェクトとの差分）

| 領域 | Web プロジェクト | このプロジェクト |
|---|---|---|
| 自動検証 | Playwright で localhost をブラウザ操作 | Unity MCP で Editor 状態確認 + 実機 Quest 3 はユーザー手動 |
| ビルド | `npm run build` | Editor の Build Settings + `manage_build`、実機転送は adb（未自動化） |
| 編集ツール | Read/Edit/Grep | 同上 + `mcp__unityMCP__manage_*`（`manage_scene` / `manage_components` / `manage_asset` 等） |
| 制約 | DOM/CSS をホットリロード可 | C# 変更 → ドメインリロード待ち。`refresh_unity wait_for_ready=true` 必須 |

**How to apply**: 新しい領域別ルールやコマンドを足すときは本ファイルの「現在のルール構成」「現在のスラッシュコマンド」テーブルも同時更新。差分が増えてきたら別ファイルに分離する。
