---
name: mcp_unity_setup
description: Unity MCP (CoplayDev) を Claude Code から使うときの落とし穴と確認手順
type: feedback
---

# Unity MCP 接続のチェックリスト

Unity MCP for Unity を Claude Code から確実に動かすには以下を順に確認する。

## 確認順序

1. Unity Editor で `Tools > MCP for Unity > Open MCP Window` を開く
2. **Transport は `Stdio`** にする（`HTTPLocal` だと Claude Code 側 stdio ブリッジから見えない）
3. Server が "Session Active" になっているか
4. Client Configuration で Claude Code が "Configured"（緑）になっているか
5. Claude 側で `mcpforunity://instances` を叩き、`instance_count >= 1` を確認
6. 複数インスタンスがあれば `set_active_instance` でピン留め

## トラブルシュート

| 症状 | 原因 | 対処 |
|---|---|---|
| `instance_count: 0` | Transport が HTTPLocal | Stdio に切替 |
| ツール一覧に `mcp__UnityMCP__*` が出ない | `~/.claude.json` の `hasTrustDialogAccepted: false` | Claude Code 起動直後の trust ダイアログ承認、または `/mcp` で承認 |
| 登録名が `unityMCP` / `UnityMCP` で揺れる | Unity 側 "Unregister" → "Configure All Detected Clients" の再登録時に変わることがある | ToolSearch でケース無視で確認 |
| `refresh_unity` が `Connection closed` エラー | コンパイル中の Unity が応答できない | `mode=force, compile=request, wait_for_ready=true` で再呼び出し（2 回目で通る） |

## Why
2026-04-29 のセッションで Transport 不一致と trust 未承認の両方を踏み、計 30 分以上ロスした。次回以降は本ファイルの順序で即特定する。

## How to apply
Unity MCP のツールが見当たらない / 反応がないと感じた瞬間、本チェックリストを上から順に消し込む。先に `.mcp.json` を疑わない（プロジェクトレベルではなく `~/.claude.json` の projects[] に登録される）。
