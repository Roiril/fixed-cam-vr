---
name: harness_design
description: CLAUDE.md / .claude/ の役割分担と、なぜその形なのかの根拠（fixed-cam-vr 現状版）
type: project
---

# ハーネス設計（fixed-cam-vr）

## レイヤー

| 場所 | 役割 |
|---|---|
| `~/.claude/CLAUDE.md` + `~/.claude/rules/` | グローバル動作規約（承認不要・コミット規約・委譲・アドバイザー手続き） |
| `<project>/CLAUDE.md` | プロジェクト規約。**常時ロードされるので薄く保つ**（詳細は下の各層へ委譲） |
| `<project>/.claude/reference/why.md` | **作りこみの思想の正本。** 先に読むのはこれ 1 本 |
| `<project>/.claude/canon/` | **世界観の正本。ユーザーが言ったことだけ**（`LEDGER` / `OPEN` / `ROUNDS`） |
| `<project>/.claude/memory/` | 技術の罠・実装の経緯（`MEMORY.md` が索引） |
| `<project>/.claude/rules/*.md` | 領域別ルール。scope は **`paths:`**（`globs:` は Cursor 規約で Claude Code は認識しない） |
| `<project>/.claude/skills/<name>/SKILL.md` | シュビーが自律的に呼ぶスキル |
| `<project>/.claude/plans/` | 実装計画 (`YYYY-MM-DD_<slug>.md`) |

**`canon/` と `memory/` を分けた理由**: 前者は**人の発話が根拠**、後者は**機械と実装の事実**。
混ぜると、ユーザーの判定が 40 本の技術メモに埋もれて見えなくなる。
切り分けの判定文は「これはユーザーの発話が根拠か」。規律は `rules/canon-boundary.md`。

## 動作モード

グローバル規約を継承する。**プロジェクト側で古い作法を残さない**:

- 書き込み前の承認は不要
- **不可逆操作・依存の追加削除・外部公開は「事前確認」ではなく
  fable アドバイザー手続き**（`~/.claude/rules/fable-advisor.md`）→ 自分で決めて事後報告。
  2026-07-23 に許可要求は全廃された。以前の「事前確認」記述はこれに置き換わっている
- 例外は**世界観と UX のディレクション**。これは許可要求ではなく
  「ユーザーしか持たない事実の取得」なので訊いてよい。ただし**既定を添えて 1 点だけ**訊き、
  返事が無ければ既定で進む（`reference/why.md`「訊く。ただし止めない」）

## Unity 固有の調整（Web プロジェクトとの差分）

| 領域 | Web プロジェクト | このプロジェクト |
|---|---|---|
| Unity の操作 | — | **Unity CLI（`tools/unity.ps1`）が第一選択**。MCP は「起動中の Editor のライブ操作」専用の退避路 |
| ビルド | `npm run build` | `.\tools\unity.ps1 build <app>`（CLI が batchmode を包む。3 アプリを ID ごと焼き分ける） |
| 自動検証 | Playwright で localhost をブラウザ操作 | **実機 Quest を HMD 無しで自動走行**（`quest-record.py --walk` で画・ログ・判定・証拠 PNG が 1 回で揃う） |
| 制約 | DOM/CSS をホットリロード可 | C# 変更 → ドメインリロード待ち。プラットフォーム切替は片道数分 |

**2026-08-08 に変わったこと**: 以前は「MCP で Editor 状態を確認し、実機はユーザー手動、
実機転送は未自動化」だった。実機検証は 2026-07-30 に自動化され、Unity の操作経路は
2026-08-08 に CLI へ移った。**MCP 前提・手動前提の手順を既定に戻さない。**

**How to apply**: 設計の根拠（なぜこの構成か）が変わった時にだけ更新。
現在のルール / コマンド一覧は **CLAUDE.md が単一情報源**（ここと二重メンテしない）。
