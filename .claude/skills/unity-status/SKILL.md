---
name: unity-status
description: Unity MCP 接続状況・Editor 状態（Play/Compiling/Ready）・直近のコンソールエラーを一括取得。Unity 関連作業の開始時、C# 編集後の確認、エラー調査の入り口で呼ぶ。`mcp__UnityMCP__*` ツールを使う前の健全性チェックに最適。
---

# Unity MCP / Editor 状態スナップショット

Unity 関連の作業を始める時、もしくはエラー / コンパイル / 接続不安定が疑われる時の **最初の確認**。以下を順に取得し、要点だけまとめて報告する。

## 取得手順

1. `mcpforunity://instances` を読む — 接続インスタンス数 / hash / Unity バージョン
2. `mcpforunity://editor/state` を読む — `isCompiling`, `isPlayMode`, `ready_for_tools`, ステイルネス
3. `read_console action=get types=["error","warning"] count=10` — 直近のエラー / 警告

## 報告フォーマット（簡潔に）

```
🟢 接続: <name@hash> / Unity <version>
状態: Play=<bool> Compiling=<bool> Ready=<bool>
エラー: <件数>件 / 警告: <件数>件
[直近のエラー要約 1-3 行]
```

問題があれば原因候補を 1 行添える（例: Stdio/HTTPLocal mismatch, trust 未承認等）。

## ツール未ロード / 接続失敗時

`mcp__UnityMCP__*` がそもそも見えなければ → **`unity-mcp` スキルに切り替える**（再登録 / ブリッジ起動の診断）。

詳細手順は `.claude/memory/mcp_unity_setup.md`。
