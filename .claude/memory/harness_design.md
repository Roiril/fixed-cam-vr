---
name: harness_design
description: CLAUDE.md / .claude/ / .agent/ の役割分担と動作モードの根拠
type: project
---

# ハーネス設計（fixed-cam-vr）

## レイヤー

| 場所 | 役割 |
|---|---|
| `~/.claude/CLAUDE.md` | グローバル動作規約（書き込み前承認なし、コミット規約、自己改善ループ） |
| `<project>/CLAUDE.md` | プロジェクト規約（スタック・コーディング規約・自動テスト方針） |
| `<project>/.claude/memory/` | 自動メモリ（このファイル群） |
| `<project>/.claude/settings.json` | プロジェクト固有 hook / 権限（必要時のみ作成） |
| `<project>/.agent/rules/*.md` | 領域別ルール（path-scope で必要時のみ参照） |
| `<project>/.agent/plans/` | 実装計画 (`YYYY-MM-DD_<slug>.md`) |

## toio-lab-web からの引き継ぎと差分

**Why**: シュビーが既に toio プロジェクトで運用しているハーネス設計を踏襲しつつ、Unity 特有の制約（ビルドレス禁止 → ビルド必須、ブラウザ自動検証 → 実機検証ユーザー依存）に合わせて差し替える。

**How to apply**:
- 応答スタイル・コミット規約・自動メモリ運用は完全踏襲
- 「Playwright で自動検証」→「Unity Test Framework + 実機はユーザー依頼」に置換
- 「js/config.js 編集ガード」相当の hook は当面なし（必要になったら作る）
- 領域別ルールは `unity-vr.md`, `meta-xr.md`, `streaming.md` の 3 本に再構成

## 動作モード

グローバル `~/.claude/CLAUDE.md` の規約をそのまま継承：
- 書き込み前承認は不要
- 不可逆操作（force push / reset --hard / 依存削除 / .env 編集）は事前確認
- コミットメッセージは `<type>：<日本語要約>` 形式
