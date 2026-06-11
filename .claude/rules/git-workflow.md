---
name: git-workflow
description: fixed-cam-vr 固有の Git 作業フロー補完。基本ルールはグローバル `~/.claude/CLAUDE.md` を参照
---

# Git 作業フロー（fixed-cam-vr 固有）

ブランチ戦略 / merge / push / 禁止事項などの **基本ルールはグローバル** [`~/.claude/CLAUDE.md`](file:///C:/Users/kouga/.claude/CLAUDE.md) を参照。本ファイルはこのプロジェクトでしか成り立たない補完情報のみ。

## 並列タスクと worktree

複数の独立した変更を同時進行するときは Agent ツールに `isolation: "worktree"` を渡して隔離 worktree で起動する。

### 起動の判断

| 状況 | 並列化 |
|---|---|
| 領域が独立（例：Streaming / Tracking / Fx / Editor / Config） | ◎ |
| 同じファイルを触る可能性 | × 単発で逐次 |
| Unity MCP のシーン編集が必要 | × シュビー本体で（MCP は単一インスタンス） |
| 計画書 1 本の執筆 | × 一貫性が崩れる |

### worktree の罠（重要）

エージェントの worktree は **起動時点の `master` を base** にする。後続で master を進めても **その worktree からは見えない**。前提が古いと「該当ファイルが存在しない」と誤検知して中断することがある。

→ 対策：
- エージェント起動前に master を最新化しておく
- プロンプトに「該当ファイルが直近で追加された可能性あり」と明記する
- それでも誤検知したらシュビー本体で書く

### 統合（cherry-pick）

エージェント完了後、各 worktree のコミット SHA を時系列で `git cherry-pick` で master へ取り込む。

```bash
git cherry-pick <SHA1> <SHA2> <SHA3> ...
```

衝突したら個別解決。新規ファイルだけのコミットは衝突しないので並列タスクの設計時に **「新規ファイルのみで衝突回避」を意識**する。

## ユーザー所有ファイル（`git add` で巻き込み禁止）

シュビーがコミットに含めてはいけないファイル。`git status` で見えても `git add .` / `git add -A` は使わず、対象ファイルを **個別指定**して避ける。

| ファイル | 理由 |
|---|---|
| `.claude/settings.json` | ユーザーのローカル権限・MCP 設定 |
| `ProjectSettings/*` | Unity Project Setup Tool / Quality 設定の調整中（CLAUDE.md 禁止事項） |
| `Packages/manifest.json` | 依存追加削除はユーザー事前報告 |
| `Assets/Settings/Cameras/Phone*.asset` の `host` 変更 | 現場で書き換えるローカル値、コミットすると次回ビルドで他の現場が壊れる |
| `Assets/Settings/ShowServer.asset` の `host` 変更 | 同上（オペレータ卓サーバの接続先。Editor+Link は 127.0.0.1、Quest 単体は PC の LAN IP） |
| `docs/ivrc-*.md` | ユーザー作のドキュメント |
| `Assets/Editor/Layouts/FixedCamVr.wlt` | ユーザーが触っている可能性、変更検知時はユーザーに確認 |
| `.claude/worktrees/` | エージェント worktree の一時ディレクトリ |

ただし以下は例外的にシュビーがコミットして良い：
- 自動生成 `.meta` ファイル（Unity が必須とする）
- `Packages/packages-lock.json`（manage_packages で追加した時）
- `ProjectSettings/EditorBuildSettings.asset`（Build Settings 整理時、変更内容を明示的に説明）

## ドキュメント同期チェック（コミット前）

構造を変えるコミット（ディレクトリ新設 / 標準ツール移行 / サブプロジェクト追加 / メニュー追加 / シーン構成変更）では、**同じコミットで参照ドキュメントもスイープする**：

- `unity-vr.md` のディレクトリ構成図 / `README.md` のディレクトリ・ドキュメント一覧
- 旧標準に言及する memory（例: DroidCam 標準時代の記述）→ 非推奨マーク or 更新
- 逆方向も同じ：rules/memory に新しい罠・規約を書いた時は、既存コードを grep してスイープする（2026-06-10 の MjpegScreen Texture2D 未初期化の教訓 — [fixed_cam_review_backlog.md](../memory/fixed_cam_review_backlog.md)）

「コードだけ直してドキュメントが古いまま」を 2 回検出済み（2026-06-10 レビューで一括是正）。3 回目を出さない。

## ハンドオフ

長時間セッションの終わり / 大きな機能完了時 / 翌日に持ち越す時は **次セッション貼り付け用プロンプト** を出力する。`handoff` スキル推奨。最低限含める：

- master HEAD SHA
- 動作確認済み / 残課題
- 直近で追加した機能の使い方
- 必読ドキュメント（README / TROUBLESHOOTING / 直近の plan）

加えて、再発しうる罠は **TROUBLESHOOTING.md** か **`.claude/rules/`** か **`.claude/memory/`** に追記して恒久化する（CLAUDE.md「自己改善ループ」原則）。
