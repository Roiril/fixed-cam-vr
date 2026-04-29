---
description: Unity MCP 接続状況・Editor 状態・直近のコンソールエラーをまとめて表示
---

Unity MCP 経由で以下を順に取得し、要点だけまとめて報告する：

1. `mcpforunity://instances` を読む — 接続インスタンス数 / hash / Unity バージョン
2. `mcpforunity://editor/state` を読む — `isCompiling`, `isPlayMode`, `ready_for_tools`, ステイルネス
3. `read_console action=get types=["error","warning"] count=10` — 直近のエラー / 警告

報告フォーマット（簡潔に）：

```
🟢 接続: <name@hash> / Unity <version>
状態: Play=<bool> Compiling=<bool> Ready=<bool>
エラー: <件数>件 / 警告: <件数>件
[直近のエラー要約 1-3 行]
```

問題があれば原因候補を 1 行添える（例: Stdio/HTTPLocal mismatch, trust 未承認等）。詳細手順は `.claude/memory/mcp_unity_setup.md`。
