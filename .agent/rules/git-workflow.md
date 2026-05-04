---
name: git-workflow
description: シュビーの Git 作業フロー規約。worktree 並列・cherry-pick 統合・PR vs 直 push の判断基準
---

# Git 作業フロー

コミットメッセージ形式（`<type>：<日本語要約>` / 全角コロン / 50字以内）はグローバル `~/.claude/CLAUDE.md` 参照。本ファイルはこのプロジェクト固有の作業フロー判断。

## ブランチ戦略

- 主軸は **`master` 1 本**。長期ブランチは持たない。
- 中規模以上の機能変更は **`feature/<slug>`** を切って PR → merge。
- 軽微な追従コミット（typo / メタ追補 / 小さなフォローアップ）は **master 直 push 可**。

### 直 push 許容条件（すべて満たす）

1. 1〜数行の追補で、レビュー価値が低い
2. 他のブランチ作業と衝突しない
3. CI を壊さない確信がある（コンパイル確認済み等）

迷ったら PR 経由にする。

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
- それでも誤検知したらシュビー本体で書く（今セッションの `MainDemoSceneSetup.cs` は本体で実装した）

### 統合（cherry-pick）

エージェント完了後、各 worktree のコミット SHA を時系列で `git cherry-pick` で master / 統合ブランチへ取り込む。

```bash
git cherry-pick <SHA1> <SHA2> <SHA3> ...
```

衝突したら個別解決。新規ファイルだけのコミットは衝突しないので並列タスクの設計時に **「新規ファイルのみで衝突回避」を意識**する。

## WIP / stash 運用

ユーザーの作業中変更が残っている時に別ブランチで作業する場合：

```bash
git stash push -m "WIP: <理由>" -- <対象ファイル>
git checkout -b feature/<slug>
# 作業
git checkout master
git stash pop
```

### ユーザー所有ファイル（`git add` で巻き込み禁止）

シュビーがコミットに含めてはいけないファイル。`git status` で見えても `git add .` / `git add -A` は使わず、対象ファイルを **個別指定**して避ける。

| ファイル | 理由 |
|---|---|
| `.claude/settings.json` | ユーザーのローカル権限・MCP 設定 |
| `ProjectSettings/*` | Unity Project Setup Tool / Quality 設定の調整 中（CLAUDE.md 禁止事項） |
| `Packages/manifest.json` | 依存追加削除はユーザー事前報告 |
| `Assets/Settings/Cameras/Phone*.asset` の `host` 変更 | 現場で書き換えるローカル値、コミットすると次回ビルドで他の現場が壊れる |
| `docs/ivrc-*.md` | ユーザー作のドキュメント |
| `Assets/Editor/Layouts/FixedCamVr.wlt` | ユーザーが触っている可能性、変更検知時はユーザーに確認 |
| `.claude/worktrees/` | エージェント worktree の一時ディレクトリ |

ただし以下は例外的にシュビーがコミットして良い：
- 自動生成 `.meta` ファイル（Unity が必須とする）
- `Packages/packages-lock.json`（manage_packages で追加した時）
- `ProjectSettings/EditorBuildSettings.asset`（Build Settings 整理時、変更内容を明示的に説明）

## PR / merge

`gh pr create --base master --head <branch>` で作成。本文テンプレ：

```markdown
## Summary
- 箇条書き 3-5 行

## ユーザー判断要事項（あれば）
- 変更したかったが規約上触らなかった項目

## Test plan
- [ ] コンパイル確認
- [ ] 関連テスト
- [ ] 実機検証は手動依頼
```

merge は `gh pr merge <N> --merge`（squash / rebase は使わない、履歴を保つ）。

merge 後は **ローカル master を `git pull --ff-only` で同期** してから次の作業へ。

## ハンドオフ

長時間セッションの終わり / 大きな機能完了時 / 翌日に持ち越す時は **次セッション貼り付け用プロンプト** を出力する。`/handoff` skill 推奨。最低限含める：

- master HEAD SHA
- 動作確認済み / 残課題
- 直近で追加した機能の使い方
- 必読ドキュメント（README / TROUBLESHOOTING / 直近の plan）

加えて、再発しうる罠は **TROUBLESHOOTING.md** か **`.agent/rules/`** か **`.claude/memory/`** に追記して恒久化する（CLAUDE.md「自己改善ループ」原則）。

## 禁止事項（再掲）

- `git push --force` / `git reset --hard` / ブランチ削除は事前確認
- `--no-verify` / `--no-gpg-sign` でフック / 署名スキップ禁止
- 主要ブランチへの force push 禁止
- `.env` / 認証情報の add 禁止
