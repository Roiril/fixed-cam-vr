---
globs:
  - "Assets/**"
  - "ProjectSettings/**"
  - "Packages/**"
  - ".claude/rules/*.md"
description: 1 Unity プロジェクトに同居する 廻リ視(FixedCam) と TableDuo を並列に進める時の干渉防止規約
---

# 同居 2 プロジェクトの干渉防止（廻リ視 / TableDuo）

この Unity プロジェクトには **2 つの独立アプリが 1 つの Unity プロジェクトとして同居**している：

| アプリ | 名前空間 / asmdef | シーン | パッケージ ID | コード |
|---|---|---|---|---|
| **廻リ視（FixedCam・本体）** | `FixedCamVr.*` | `Assets/Scenes/Main.unity` | `com.roiril.mawarimi` | `Assets/Scripts/` |
| **TableDuo（手アバター調査）** | `TableDuoVr.*` | `Assets/TableDuo/Scenes/TableDuoMain.unity` | `com.roiril.tableduo` | `Assets/TableDuo/` |

別シュビー（並列エージェント / worktree / 別セッション）と本シュビーが**別々のアプリを同時に進めることがある**。下記は壊さないための絶対規約。

## 根底原則

**「2 アプリは別物。互いの領域・共有資源に踏み込まない。Unity Editor とビルドは奪い合わない」**

## 1. コードは完全分離（既存・厳守）

- `FixedCamVr.*` と `TableDuoVr.*` の **asmdef 相互参照禁止**（どちらも自己完結）
- 名前空間も混ぜない。TableDuo のコードは `Assets/TableDuo/` 配下のみ、本体は `Assets/Scripts/` 配下のみ
- 片方の作業で他方の `.cs` / `.asmdef` / シーン / prefab を触らない。触る必要が出たら**それは設計の越境**なので一旦止めてユーザーに確認

## 2. 共有資源 = 触ったら両方に効く（最重要）

1 プロジェクトなので以下は **2 アプリで共有**。片方の都合で書き換えると**もう片方を巻き込んで壊す**：

| 共有資源 | 罠 |
|---|---|
| **Unity Editor（1 インスタンス）** | MCP 編集・Play・Build が全部この 1 個を奪い合う。**2 つの作業が同時に Editor を触ると壊れる** |
| **`ProjectSettings/*`**（productName / packageID / Quality / Graphics / XR） | 1 個を共有。CLAUDE.md 禁止事項で「理由なく変更しない」。**ビルドは productName/ID を一時 swap して finally 復元する（[BuildVariants.cs](../../Assets/Editor/BuildVariants.cs)）** |
| **共有 `.asset`**（`Assets/Settings/URP-*` / OVR config / `Assets/XR/*`） | 片方のレンダリング都合で弄ると両アプリの絵が変わる |
| **開いているシーン** | Editor は同時に 1 シーンしか開けない。片方のシーンを開くともう片方の作業は中断 |
| `Packages/manifest.json` | 依存はどちらのアプリにも入る。追加削除はユーザー事前報告（CLAUDE.md） |

→ **共有資源を編集する時は、もう片方のアプリへの影響を必ず考える。** 片アプリ専用の都合で共有 .asset / ProjectSettings を弄らない。

## 3. ビルドは絶対に同時に走らせない

[BuildVariants.cs](../../Assets/Editor/BuildVariants.cs) は build 中に `PlayerSettings.productName` / `applicationIdentifier` を一時 swap し、`finally` で戻す。

- **2 つのビルドを並列起動すると swap/restore が競合**し、片方が他方の ID（例: TableDuo が `com.roiril.mawarimi`）で焼ける
- ビルドは **必ず逐次**。片方の build 完了（PlayerSettings 復元）を確認してから次を始める
- ビルドは scene を明示渡しするので Build Settings の scene list には依存しない（scene list 競合は起きない）が、**Editor 単一なので同時 build 自体が不可**

## 4. 並列化してよい作業 / だめな作業

| 作業 | 並列(worktree/別エージェント) | 理由 |
|---|---|---|
| 片アプリのドキュメント執筆 | ◎ | ファイル独立・Unity 不要 |
| 片アプリのコード執筆（コンパイル確認を後回しにできる範囲） | ○ | ただし共有 .asset / ProjectSettings に触れないこと |
| Unity MCP でのシーン・コンポーネント編集 | **×** | Editor 単一インスタンス。本シュビーが逐次で行う |
| ビルド / Play Mode 検証 | **×** | Editor・ProjectSettings 奪い合い。逐次のみ |
| 共有 .asset / ProjectSettings の変更 | **×** | 両アプリに波及。逐次 + 影響説明必須 |

worktree の罠（base が古い等）は [git-workflow.md](git-workflow.md) も参照。

## 5. 実機（Quest 2 台）運用

- adb は **必ず `-s <serial>` で対象指定**（複数台繋がると無指定コマンドは `more than one device` で失敗）
- 2 アプリはパッケージ ID が違うので 1 台の Quest に**並存できる**（上書きし合わない）
- 「最新が入ってるか」はパッケージ ID 別に `dumpsys package <id>` の `lastUpdateTime` で確認

## チェックリスト（並列で着手する前に）

1. 自分が触るのはどっちのアプリ？ → 相手のコード領域・シーン・prefab に手を出さない
2. 共有資源（ProjectSettings / URP / OVR / XR / manifest）を触る？ → 触るなら逐次 + 影響説明
3. Unity Editor を使う作業？ → 並列にしない（本シュビーが逐次）
4. ビルドする？ → 他のビルドが走っていないこと、PlayerSettings が復元済みであることを確認
