---
description: Unity MCP の接続診断と再接続手順を一括出力（unityMCP/UnityMCP 両命名対応）
---

Unity MCP (MCP for Unity) との接続が不安定な時に走らせる診断コマンド。
シュビーは以下を **順に** 実行し、最後にユーザーへ「次に何をすればいいか」を 1 つだけ提示する。

---

## 0. 前提

- MCP for Unity の Unity 側ブリッジは Editor 起動中のみリッスンする（既定 TCP `127.0.0.1:6400`）
- Claude Code 側のサーバ登録は `~/.claude.json` （ユーザ全体）か プロジェクト `.mcp.json` のいずれか
- 本セッションに deferred tool として `mcp__unityMCP__*` か `mcp__UnityMCP__*` が出てくれば「ロード成功」

---

## 1. ロード状態の判定（最優先）

ToolSearch で以下の 2 系統を順に問い合わせ、ヒットした側を「採用ネームスペース」として記録する。

- `select:mcp__unityMCP__read_console,mcp__unityMCP__manage_editor`
- `select:mcp__UnityMCP__read_console,mcp__UnityMCP__manage_editor`

両方ヒットしなかった場合は **未ロード**。`/unity-status` は失敗するため呼ばない。
ヒットした場合は採用ネームスペースで `manage_editor action=get_state` と `mcpforunity://instances` を読み、接続健全性を確認する（実質 `/unity-status` と同等）。

---

## 2. 登録状態の確認

Bash で以下を順に実行（ユーザに承認を求めず軽量・読み取りのみ）：

```bash
claude mcp list 2>&1 | head -40
cat .mcp.json 2>/dev/null
```

判定：
- `claude mcp list` に `unityMCP` も `UnityMCP` も無い → **登録欠落**
- 登録あるが `connected` でない → **接続失敗**
- 登録あり `connected` だがツール未ロード → **セッションキャッシュ不整合**（要再起動）

---

## 3. Unity 側ブリッジ確認

Bash:

```bash
powershell -NoProfile -Command "Test-NetConnection 127.0.0.1 -Port 6400 -InformationLevel Quiet"
```

`True` ならブリッジ稼働。`False` なら **Unity Editor 未起動 / MCP for Unity ブリッジ未起動**。
Editor 内で `Window → MCP for Unity → Status` を開いて bridge を Start するようユーザに案内する。

---

## 4. 出力フォーマット

```
=== Unity MCP 診断 ===
ツールロード: <namespace or 未ロード>
登録 (claude mcp list): <connected/failed/missing>
ブリッジ TCP 6400: <open/closed>

[判定]: <下記いずれか 1 行>

[次にやること]: <下記いずれか 1 行>
```

判定 → 次にやること の対応表：

| 判定 | 次にやること |
|---|---|
| ✅ 全て OK | 何もしなくて良い。`/unity-status` で詳細を見る |
| ❌ ブリッジ closed | Unity Editor で `Window → MCP for Unity → Start Bridge` を実行 |
| ❌ 登録欠落 | 下記「再登録コマンド」をユーザに提示し、合意のうえで実行 |
| ⚠️ 登録 connected だがツール未ロード | Claude Code を `/exit` → 再起動（セッションキャッシュ起因） |
| ⚠️ 登録 failed | `claude mcp remove unityMCP` 後に再登録、または Unity 側ブリッジを再起動してから Claude を再起動 |

---

## 5. 再登録の方針（提示テンプレ）

**第一選択**: Unity Editor 内の **`Window → MCP for Unity → Auto Setup`**（または `Configure Claude Code`）でユーザに登録させる。
これにより `~/.claude.json` に `UnityMCP`（大文字 U）が `uvx --offline --from mcpforunityserver==<ver> mcp-for-unity` の形で登録される。
このプロジェクトの権限 allow リストは `mcp__UnityMCP__*`（大文字 U）に揃っているので、命名を一致させる。

**フォールバック**（Unity の Auto Setup が使えない時）:

```bash
claude mcp add UnityMCP -s user -- uvx --offline --from "mcpforunityserver" mcp-for-unity
```

`--offline` の前提として、Unity Editor 側の MCP for Unity が一度でも `Auto Setup` を走らせて、`mcpforunityserver` を `uv tool install` してあること。していなければ Unity Editor で先に Auto Setup を実行。

git+uvx 経由 (`git+https://github.com/CoplayDev/unity-mcp.git#subdirectory=...`) は本環境では fetch に失敗するため避ける。

---

## 6. 補足ルール

- **副作用ある操作は必ずユーザ合意を取る**: `claude mcp remove`, `claude mcp add`, Editor 再起動依頼
- 同じ診断を 1 セッション内で 2 回繰り返すなら、グローバルメモリ追記を提案する
- 詳しい手順メモは `.claude/memory/mcp_unity_setup.md`（未存在ならこの診断後に作成提案）
