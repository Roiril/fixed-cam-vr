---
name: mcp-unity
description: Unity MCP for Unity の使い方規約。シーン / コンポーネント / アセット操作前に読む
globs:
  - "Assets/**/*.unity"
  - "Assets/**/*.prefab"
  - "Assets/**/*.cs"
  - "Assets/**/*.asset"
  - "Assets/**/*.mat"
  - "ProjectSettings/**"
---

# Unity MCP 使用規約

Unity プロジェクトの編集はまず MCP for Unity 経由を試し、ダメなら直接ファイル編集にフォールバックする。

## 接続前提

接続トラブルは [.claude/memory/mcp_unity_setup.md](../../.claude/memory/mcp_unity_setup.md) に詳細。要点：

- Transport は **Stdio** 必須
- 着手前に `mcpforunity://instances` を読み、`instance_count >= 1` を確認
- 複数あれば `set_active_instance` でピン留め

## 推奨フロー

1. **読み取り → 書き込み**: `mcpforunity://editor/state`, `find_gameobjects`, `read_console` で現状把握 → 編集系ツール
2. **C# 変更後**: 必ず `refresh_unity` (`mode=force, compile=request, wait_for_ready=true`) → `read_console` でエラー確認
3. **シーン保存**: `manage_scene action=save` を明示的に呼ぶ（自動保存に依存しない）
4. **ビルド対象シーンの追加**: `manage_build action=scenes` で Build Settings に登録

## ツール選択ガイド

| やりたいこと | 第一選択 | 補足 |
|---|---|---|
| GameObject 作成 | `manage_gameobject action=create` | `prefab_path` でプレハブ instantiate |
| プリミティブ単独作成 | `manage_gameobject action=create primitive_type=...` | Light だけ欲しい時は `primitive_type` を渡さず `components_to_add=["Light"]` |
| Component 追加 | `manage_components action=add` | フル型名（namespace 込み）で指定 |
| シリアライズ済みフィールド設定 | `manage_components action=set_property` | `m_Materials` など `m_` プリフィックス付きで指定する場合あり（`sharedMaterial` は不可、`m_Materials` を使う） |
| Material 作成 | `manage_asset action=create asset_type=Material` | `properties.shader` で URP/Unlit 等を指定 |
| シーン作成 | `manage_scene action=create` | デフォルトでは Camera/Light が入らない。手動で追加すること |

## 落とし穴

- `manage_components set_property sharedMaterial` は通らない → `m_Materials: [{path: "..."}]` を使う
- 新規シーンは Camera/Light が一切ない（Unity 通常の "New Scene" と挙動違い）
- `Connection closed before reading expected bytes` は Unity 側のドメインリロード/コンパイル中。`refresh_unity wait_for_ready=true` でリトライ
- **Editor が非フォーカス（裏に回っている）と `refresh_unity compile=request` は .cs 変更を拾ってもコンパイルを遅延し、DLL が再生成されない**（`Library/ScriptAssemblies/*.dll` の mtime が古いまま）。MCP 編集で .cs を直しても「コンパイル検証できない」状態になる。→ **`run_tests`（EditMode）はテスト前に必ずコンパイルを強制する**ので、非フォーカスでもこれでコンパイル＋検証できる（2026-06-29 実害: refresh を数回タイムアウトさせた末に発見）。検証は `run_tests`→`get_test_job(wait_timeout=60)` が確実。DLL mtime で再コンパイルの実否を確認できる
- **`execute_code` は Windows で「ファイル名または拡張子が長すぎます」で失敗しがち**（mono コマンドライン長制限）。live シーンの値設定等は YAML 直編集＋`manage_scene load` 再ロード、強制コンパイルは `run_tests` で代替する

## MCP 不在時のフォールバック

シーン YAML / Prefab YAML を直接編集してよいが、**Prefab Instance 内のオブジェクトは触らない**（`m_PrefabInstance` 経由の override が壊れる）。具体手順は [.claude/memory/unity_pitfalls.md](../../.claude/memory/unity_pitfalls.md) を参照。

## 禁止事項

- `Library/`, `Temp/`, `Logs/`, `UserSettings/` を MCP 経由でも直接編集しない（生成物）
- ProjectSettings/ を理由なく書き換えない（特に GraphicsSettings/QualitySettings）
- `Packages/manifest.json` の dependency 追加削除はユーザー事前報告
