---
name: git-workflow
description: fixed-cam-vr 固有の Git 作業フロー補完（並行セッション前提の pathspec コミット・ユーザー所有ファイル）。基本ルールはグローバル `~/.claude/CLAUDE.md`
---

# Git 作業フロー（fixed-cam-vr 固有）

基本（branch / merge / push の判断・コミットメッセージ）はグローバル `~/.claude/CLAUDE.md`。
ここはこのリポジトリでしか成り立たないことだけ。旧版の全文と実害の経緯は
`reference/archive/git-workflow-2026-10-22.md`。

## 並行セッションが同じ working copy にいる

index は 1 つで共有される。**コミットは必ず明示 pathspec で、ファイル名まで列挙する。**

```bash
git commit -F <scratchpad のメッセージファイル> -- <ファイル> <ファイル> ...
```

- **ディレクトリを渡さない。** 中にある他セッションの未コミット変更が丸ごと入る
- 打つ前に `git status --short -- <列挙したパス>` の行数と列挙数が一致するか数える
- 相手も同じファイルを編集中なら、pathspec でも相手の行が入る。コミット直後に
  `git show --stat HEAD` を自分の差分と比べる。混ざったら作業ツリーは戻さず、自分の版を
  `git hash-object -w` → `git update-index --cacheinfo` で入れ直し、相手に `SendMessage` で知らせる
- コミットに含めた差分が新しい型・メソッドを呼ぶなら、**定義側のファイルも同じコミットに入っているか**見る。
  dirty な作業ツリーでのコンパイル成功は、コミット内容の成功を意味しない

## ユーザー所有ファイル

`hooks/guard-owned-files.js` が、次を含む `git commit` と `git commit -a` を止める。
意図して含めるときだけ、コマンド末尾に `# allow-owned` を付け、理由を報告に書く。

| ファイル | 理由 |
|---|---|
| `.claude/settings.json` | ユーザーのローカル権限・MCP 設定 |
| `ProjectSettings/*`（`EditorBuildSettings.asset` を除く） | 調整中 |
| `Packages/manifest.json` | 依存の追加削除は fable アドバイザー手続き |
| `Assets/Settings/Cameras/Phone*.asset` / `Assets/Settings/ShowServer.asset` | 現場で書き換える host |
| `Assets/Settings/URP-*.asset` / `Assets/XR/**/*.asset` | ビルドの副産物 |
| `docs/ivrc-*.md` / `Assets/Editor/Layouts/FixedCamVr.wlt` | ユーザー作 |
| `.claude/worktrees/` | エージェント worktree |

コミットしてよい例外: 自動生成の `.meta`、`Packages/packages-lock.json`（パッケージを足した時）、
`ProjectSettings/EditorBuildSettings.asset`（変更内容を説明する）。

## 並列エージェントと worktree

- 領域が独立しているときだけ `isolation: "worktree"` で並列にする。同じファイル・Unity のシーン編集・
  計画書 1 本の執筆は逐次
- worktree は**起動時点の HEAD** を base にする。起動前に最新化し、「直近で足したファイルがある」と
  プロンプトに書く。完了したら SHA を時系列で `git cherry-pick` する
