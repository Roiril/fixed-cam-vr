---
name: harness_design
description: CLAUDE.md / .claude/ の役割分担と、なぜその形なのかの根拠（fixed-cam-vr 現状版）
metadata:
  node_type: memory
  type: project
  originSessionId: 52b0abed-d330-4d25-a53b-2f1e7226b4b7
  modified: 2026-10-22T10:41:27.315Z
---

# ハーネス設計（fixed-cam-vr）

## レイヤー

| 場所 | 役割 |
|---|---|
| `~/.claude/CLAUDE.md` + `~/.claude/rules/` | グローバル動作規約（承認不要・コミット規約・委譲・アドバイザー手続き） |
| `<project>/CLAUDE.md` | プロジェクト規約。**常時ロードされるので薄く保つ**（詳細は下の各層へ委譲） |
| `<project>/.claude/reference/` | **明示的に読むまで載らない資料。**`why.md`（作りこみの思想・先に読むのはこれ 1 本）/ `mcp-unity.md`（退避路の作法）/ `archive/`（旧 rule の全文。凍結） |
| `<project>/.claude/canon/` | **世界観の正本**。`LEDGER`（ユーザーの逐語）/ `NOW`（体験の現在形と根拠）/ `OPEN`（問・案・実装済み未判定）/ `OPEN-closed` / `TASTE`（シュビーの読み）/ `ROUNDS` |
| `<project>/.claude/memory/` | 技術の罠・実装の経緯（`MEMORY.md` が索引。1 行 1 フック） |
| `<project>/.claude/rules/*.md` | **現行の契約だけ**の話題別 rule（索引は `streaming.md`）。scope は **`paths:`** |
| `<project>/.claude/skills/<name>/SKILL.md` | シュビーが自律的に呼ぶスキル |
| `<project>/.claude/plans/` | 実装計画 (`YYYY-MM-DD_<slug>.md`)。**索引は `plans/README.md`**。status は active / done / archived |
| `<project>/.claude/hooks/` + `tools/harness-check.py` | 機械の門。所有ファイルの commit を止める・`.claude/` を書いた直後に検査を流す |

## 2026-10-22 の見直し（効率とデザイナー）

**実測**: 直近 40 セッションで、旧 `rules/streaming.md`（351KB・約 18 万字）が 32 回中 19 回、
旧 `show-design.md`（9.4 万字）が 14 回、該当コードを触った瞬間に全文載っていた。
日付の節を頭に足し、古い記述を「前の版」と断って下に残す書き方を続けた結果で、本文の途中を拾うと
前の版を契約として読む。MEMORY.md は 31KB（常時ロード・サブエージェントにも毎回）。

**やったこと**:
- 巨大 rule は全文を `reference/archive/*-2026-10-22.md` に凍結し、7 体で話題ごとに**現行の契約だけ**を
  抜いて 9 本の rule にした（各 6〜12KB・狭い glob）。各行に記号と出典を付け、記号はコードと機械照合した
- 契約と経緯を分ける規律を `rules/doc-sync.md` に書き、`tools/harness-check.py` と PostToolUse hook で数える
  （サイズ上限・前の版を示す語・索引の 1 対 1・リンク・plans の status・LEDGER の新しい重複番号）
- ユーザー所有ファイルの巻き込みは rule ではなく PreToolUse hook（`guard-owned-files.js`）で止める
- `canon/NOW.md` を作り、`tools/canon-now.py --check` でずれを検出する。手書きの体験仕様はずれる前提
- `canon/TASTE.md`（場面をまたぐ判定の対）を置き、`why.md` の「原則を作らない」を改訂した
- `direction-round` を実際の往復（前後・試した版・隣の場面）に書き直し、`show-review`（通し監査）を足した

**残したもの**: `rules/sound-design.md`（146KB・載るのは 32 回中 1 回）・`unity-vr.md`・`visual-verification.md` は
上限超えのまま据え置き（`harness-check.py` の GRANDFATHER。育てば落ちる）。次に音を触るときに話題別へ分ける。

## 置き場の規律（2026-08-09 に一度大掃除した）

- **`rules/` は「その paths を触るときに毎回読む価値があるもの」だけ。**
  退避路の作法・滅多に使わない手順は `reference/` へ置く。
  `paths:` が広い rule ほど、無関係な作業のたびに全文が載る
- **同じ名前のスキルをグローバルとプロジェクトの両方に置かない。**
  どちらが載るか読む側から判別できず、片方だけ古くなる。
  機体・ツール一般の知識（MCP の登録手順・prefab の SerializeField）は**グローバルが正**で、
  プロジェクト側にはこのリポジトリでしか成り立たない差分だけを書く
- **1 領域 1 入口。** 2026-08-09 に `skills/unity-status` `skills/unity-mcp` `skills/unity-prefab-fields` を
  削除した（グローバルの `unity-editor-status` / `unity-prefab-fields` と重複していた上、
  MCP 未登録のこの機体では動かない手順が「入口」を名乗っていた）

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
