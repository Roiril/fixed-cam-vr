---
name: unity-status
description: 【MCP 経路の退避路。Unity 作業の入口ではない】起動中の Editor を MCP でライブ操作する必要があるときだけ使う。MCP 接続状況・Editor 状態（Play/Compiling/Ready）・直近のコンソールエラーを一括取得する。ビルド・テスト・状態確認は `tools/unity.ps1`（Unity CLI）が先。
---

# Unity MCP / Editor 状態スナップショット

> ⚠ **これは Unity 作業の入口ではない**（2026-08-08 に経路が変わった）。
> ビルド・テスト・前提確認は **`.\tools\unity.ps1`（Unity CLI）** が先。
> ここへ来るのは「**起動中の Editor をライブ操作する**」必要があるときだけ
> （シーンの手編集・Play・コンポーネント操作）。CLI の Pipeline は Unity 6.0 以上で、
> 2022.3 のこのプロジェクトには入らないので、その用途だけ MCP が唯一の経路として残っている。
>
> ⚠ **現状この PC に MCP は未登録**（`claude mcp list` が空）。下の手順はすべて失敗する。
> 使うなら先に `skills/unity-mcp` で登録する。

起動中の Editor を MCP で触る前の健全性チェック。以下を順に取得し、要点だけまとめて報告する。

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
