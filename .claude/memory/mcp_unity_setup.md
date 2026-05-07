---
name: mcp_unity_setup
description: Unity MCP (CoplayDev) を Claude Code から確実に繋ぐ手順と落とし穴
type: feedback
---

# Unity MCP 接続のチェックリスト

## 結論：最短で繋ぐ手順

1. Unity Editor で `Tools > MCP for Unity > Open MCP Window` → Transport `Stdio` で Server "Session Active"
2. ターミナルで以下を実行（**ユーザースコープに登録すること**）:
   ```bash
   claude mcp add UnityMCP -s user -- "C:\Users\kouga\.local\bin\uvx.exe" --offline --from "mcpforunityserver==9.6.8" mcp-for-unity
   ```
   バージョン番号 `9.6.8` は Unity Editor が `~/.local/bin/uvx.exe` 経由でインストール済みのものに合わせる
3. `claude mcp list` で `UnityMCP: ✓ Connected` を確認
4. **Claude Code を `/exit` → 再起動**（deferred tools はセッション起動時にロードされる）
5. 再起動後の新セッションで `unity-status` スキルで動作確認

## Why（実際に踏んだ罠）

2026-04-30 のセッションで以下に時間を溶かした。次回以降は本書の手順で迂回する。

### 罠 1: プロジェクトスコープ登録が cwd キー揺れで見えなくなる
- Unity Editor の Auto Setup は **プロジェクトスコープ** (`~/.claude.json` の `projects."<path>".mcpServers`) に書き込むことがある
- キーは `C:/Users/kouga/Projects/Unity/fixed-cam-vr`（フォワードスラッシュ）。Claude Code が認識する cwd 表記とマッチしないと **`claude mcp list` から消える**
- **対処**: ユーザースコープ (`-s user`) で登録すれば cwd 形式に依存しない
- ⚠️ **再発注意**: ユーザースコープに移しても、Unity Editor の MCP for Unity ウィンドウから `Configure Claude Code` 等を再実行すると **プロジェクトスコープ側に書き戻されて再び不可視化** する。2026-04-30 セッションで 1 度遭遇後、同日中に再発した実績あり。Unity 側で再 Configure する場合は、その後に下記の修復ワンライナーを実行する：
  ```bash
  # ユーザースコープに UnityMCP を設置（既にあれば失敗するが無害）
  claude mcp add UnityMCP -s user "C:\Users\kouga\.local\bin\uvx.exe" -- --offline --from "mcpforunityserver==9.6.8" mcp-for-unity
  # プロジェクトスコープの重複を削除
  python -c "import json; p=r'C:\Users\kouga\.claude.json'; d=json.load(open(p,encoding='utf-8')); k='C:/Users/kouga/Projects/Unity/fixed-cam-vr'; (d['projects'][k]['mcpServers'].pop('UnityMCP', None)); json.dump(d, open(p,'w',encoding='utf-8'), indent=2)"
  ```

### 罠 2: `claude mcp add` の git+uvx 形式は動かない
- ❌ `claude mcp add unityMCP -s user -- uvx --from "git+https://github.com/CoplayDev/unity-mcp.git#subdirectory=UnityMcpBridge/UnityMcpServer" unity-mcp-server`
  → `git fetch` が失敗して `Failed to connect`
- ✅ `claude mcp add UnityMCP -s user -- "C:\Users\kouga\.local\bin\uvx.exe" --offline --from "mcpforunityserver==9.6.8" mcp-for-unity`
  → Unity Editor の Auto Setup が事前に `uv tool install mcpforunityserver` 済みなので `--offline` で解決

### 罠 3: `claude mcp add-json` は `"type":"stdio"` を弾く
- `~/.claude.json` には `"type":"stdio"` が保存されているが、`add-json` のスキーマには含めると `Invalid configuration: Invalid input` で失敗する
- **対処**: 移行は `add-json` ではなく `claude mcp add ... -- <command> <args>` で行う

### 罠 4: 命名揺れ `unityMCP` vs `UnityMCP`
- Unity Editor の Auto Setup は **大文字 U の `UnityMCP`** で登録する
- 過去のドキュメントで小文字 `unityMCP` の例があり、`.claude/settings.json` の権限 allow も両系統が混在しやすい
- **対処**: 大文字 `UnityMCP` に統一。allow リストも `mcp__UnityMCP__*` のみ残す

### 罠 5: 登録直後はツールが使えない
- `claude mcp list` で Connected でも、現セッションの ToolSearch には出てこない
- deferred tools は **Claude Code 起動時のハンドシェイク**で確定する
- **対処**: `/exit` → 再起動

### 罠 6: Transport HTTPLocal は Claude Code から見えない
- MCP for Unity Window の Transport を `HTTPLocal` にすると stdio ブリッジ越しでは認識されない
- **対処**: Transport は **Stdio** にする

## トラブルシュート対応表

| 症状 | 原因 | 対処 |
|---|---|---|
| `claude mcp list` に何も出ない | プロジェクトスコープ登録 + cwd キー揺れ | ユーザースコープに移行 |
| `claude mcp list` で `Failed to connect` | git+uvx 形式で fetch 失敗 / Unity 側ブリッジ未起動 | `--offline --from mcpforunityserver==<ver>` 形式に変更 / `Test-NetConnection 127.0.0.1 -Port 6400` で確認 |
| `claude mcp list` で Connected だが ToolSearch に出ない | セッション起動時にハンドシェイク未完 | `/exit` → 再起動 |
| `instance_count: 0` | Transport が HTTPLocal | Stdio に切替 |
| `mcp__UnityMCP__*` が出ない | `~/.claude.json` の `hasTrustDialogAccepted: false` | trust ダイアログ承認、または `/mcp` で承認 |
| `refresh_unity` が `Connection closed` | コンパイル中の Unity が応答不能 | `mode=force, compile=request, wait_for_ready=true` で再呼び出し（2 回目で通る） |

## How to apply

「Unity MCP が反応しない」と感じた瞬間：

1. まず **`unity-mcp` スキル**を呼ぶ（`.claude/skills/unity-mcp/SKILL.md` で診断パス自動化済み）
2. 自動診断で対応表のどの行に当てはまるか即特定
3. 副作用ある操作（remove / add / 再起動）はユーザに合意を得てから実行

## 関連リソース

- 現環境の uvx パス: `C:\Users\kouga\.local\bin\uvx.exe`
- 現環境の mcpforunityserver バージョン: `9.6.8`（Unity Editor の Auto Setup で確定。バージョンが上がったら本ファイル更新）
- `claude mcp list` の実行は `cwd = .claude.json の projects キーと一致する path` でないとプロジェクトスコープ entry が読まれない（ユーザースコープ統一で回避）
