---
description: Unity MCP の接続診断と再接続手順を一括出力（unityMCP/UnityMCP 両命名対応）
---

Unity MCP (MCP for Unity) との接続が不安定な時に走らせる診断コマンド。
シュビーは以下を **順に** 実行し、最後にユーザーへ「次に何をすればいいか」を 1 つだけ提示する。

---

## 0. 前提

- MCP for Unity の Unity 側ブリッジは Editor 起動中のみリッスンする（既定 TCP `127.0.0.1:6400`）
- Claude Code 側のサーバ登録は **ユーザースコープ**（`~/.claude.json` の トップレベル `mcpServers`）に置くのが安定
  - プロジェクトスコープ（`projects."<path>".mcpServers`）はパスキー揺れで不可視化することがあるため避ける
- 本セッションに deferred tool として `mcp__UnityMCP__*` が出てくれば「ロード成功」（小文字 `unityMCP` は古い名前なので新規登録時は使わない）
- 詳細な落とし穴は [`.claude/memory/mcp_unity_setup.md`](../memory/mcp_unity_setup.md) 参照

---

## 1. ロード状態の判定（最優先）

ToolSearch で `select:mcp__UnityMCP__read_console,mcp__UnityMCP__manage_editor` を問い合わせ、ヒットすればロード成功。
ヒットしなかった場合は **未ロード**（`/unity-status` は失敗するため呼ばない）。
ヒット時は `manage_editor action=get_state` と `mcpforunity://instances` を読み、接続健全性を確認する（実質 `/unity-status` と同等）。

旧名 `mcp__unityMCP__*`（小文字 u）が出てくる古い登録に当たった場合は、ユーザに削除と再登録を提案する（命名統一のため）。

---

## 2. 登録状態の確認

Bash で以下を順に実行（ユーザに承認を求めず軽量・読み取りのみ）：

```bash
claude mcp list 2>&1 | head -40
cat .mcp.json 2>/dev/null
```

判定：
- `claude mcp list` に `UnityMCP` 系が無い → **登録欠落**（プロジェクトスコープに埋もれている可能性も含む — `~/.claude.json` の `projects.*.mcpServers` を python で grep して確認）
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

**フォールバック**（手動登録）:

```bash
claude mcp add UnityMCP -s user -- "C:\Users\kouga\.local\bin\uvx.exe" --offline --from "mcpforunityserver==9.6.8" mcp-for-unity
```

- `-s user` でユーザースコープ（cwd 表記揺れに左右されない）
- バージョン `9.6.8` は `~/.local/bin/uvx.exe` に install 済みのものに合わせる（Unity の Auto Setup が事前に `uv tool install mcpforunityserver` 済み前提）
- `--offline` で git fetch を回避（オンライン取得は本環境で失敗実績あり）
- 命名は **大文字 `UnityMCP`**（権限 allow リストと揃える）

**避けるべき形式**:
- ❌ `claude mcp add ... -- uvx --from "git+https://github.com/CoplayDev/unity-mcp.git#subdirectory=..."` — git fetch 失敗
- ❌ `claude mcp add-json` で `"type":"stdio"` を含む JSON — `Invalid input` で弾かれる
- ❌ プロジェクトスコープ (`-s project` / `-s local`) — cwd キー揺れで `claude mcp list` から消える

---

## 6. 補足ルール

- **副作用ある操作は必ずユーザ合意を取る**: `claude mcp remove`, `claude mcp add`, Editor 再起動依頼
- 同じ診断を 1 セッション内で 2 回繰り返すなら、グローバルメモリ追記を提案する
- 詳しい手順メモは `.claude/memory/mcp_unity_setup.md`（未存在ならこの診断後に作成提案）
