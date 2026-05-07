---
description: 次セッションへの引き継ぎプロンプトを生成（コピペで貼って続行できる形）
---

現セッションの状況をまとめ、次セッション開始時にユーザーが貼ることで作業を即再開できる引き継ぎプロンプトを生成する。

以下を順に取得・整理する：

1. `git log --oneline -10` で直近のコミット
2. `git status -s` で未コミット差分
3. todo list の状態（completed / in_progress / pending）
4. Unity MCP の接続状態（`mcpforunity://instances` を読む。失敗ならその旨記載）
5. 直近のコンソール状態（`read_console` が叩けるなら最近のエラー）

出力テンプレート：

```
# 引き継ぎプロンプト（YYYY-MM-DD）
C:\Users\kouga\Projects\Unity\fixed-cam-vr で作業継続。Unity 2022.3.62f2 / URP / Meta Quest 3 ターゲット。

## 直近のコミット
<commits>

## 未コミット差分
<git status>

## 完了済みタスク
- ...

## 進行中 / 残タスク
- [ ] ...
- [ ] ...

## Unity MCP 状態
<connected? / instance / 注意点>

## 必読
- CLAUDE.md
- .claude/memory/MEMORY.md（特に <該当ファイル>）
- 領域に応じた .claude/rules/

## 次にやること
1. ...
2. ...

## 開始時の合言葉
「シュビー、続きやって」 → 上記 1 から着手。
```

**注意**：
- 機密（IP、URL、API キー等）が含まれる場合はユーザーに確認してから含める
- 引き継ぎプロンプト本文は**メモリ保存しない**（エフェメラル情報）。プロジェクト固有の永続的な事実は memory に分離保存
- 出力後、ユーザーに「保存する value（永続的な気付き）があれば教えて」と一言添える
