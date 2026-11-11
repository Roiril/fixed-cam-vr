# Git 作業フロー（fixed-cam-vr 固有）

コミットメッセージと push の判断はグローバル `~/.claude/CLAUDE.md`。
ここはこのリポジトリでしか成り立たないことだけ。旧版の全文と実害の経緯は
`reference/archive/git-workflow-2026-10-22.md`。

## 幹は master の 1 本だけ

- **メインの木は常に master。** 全セッションが master へ直接コミットして push する。PR は作らない
- **メインの木で枝を切らない・枝へ移らない。** `hooks/guard-owned-files.js` が、master 以外へのコミットと
  `git switch -c` / `git checkout -b` / 枝への `git switch` を止める。意図して通すときだけ末尾に `# allow-branch`
- 枝を使うのは `git worktree add` で切った別の木の中だけ（エージェントの `isolation: "worktree"` を含む）。
  **取り込んだら同じ作業の中で木と枝を消す。** 取り込みは master の上で `git cherry-pick` か `git merge --no-ff`
- 残り物は `py -3.11 tools/git-tidy.py` が数える（`--apply` で取り込み済みの枝・リモートの枝・済んだ worktree を消す）。
  `tools/verify.py` の `git` の門とセッション開始時の表示が同じものを見る
- 取り込まないまま 3 日を超えた枝は、取り込むか、`git tag archive/<名前> <枝>` を付けて push してから消す
- 常設の検証用の木 `fcv-twin-tree` は detached HEAD。枝を持たせない・向こうでコミットしない
- push の前に `git fetch`。`origin/master` が先に進んでいたら `git merge --no-edit origin/master` で取り込んでから push する。
  rebase と reset はしない（並行セッションの未コミット変更が作業ツリーにある）

## 並行セッションが同じ working copy にいる

index は 1 つで共有される。**コミットは必ず明示 pathspec で、ファイル名まで列挙する。**

```bash
git commit -F - -- <ファイル> <ファイル> ... < <scratchpad のメッセージファイル>
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
  プロンプトに書く。完了したら SHA を時系列で `git cherry-pick` し、木と枝を消す（`git-tidy.py --apply`）
- C# のコンパイルとテストを別の木で確かめるなら、常設の検証用の木を使う
  （`py -3.11 tools/twin-tree.py sync` → `test`。Library を持っているので取り込み待ちが無い）
