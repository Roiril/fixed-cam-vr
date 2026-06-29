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

| アプリ | ユーザー呼称 | 名前空間 / asmdef | シーン | パッケージ ID | コード |
|---|---|---|---|---|---|
| **廻リ視（FixedCam・本体）** | **fixedcam** / 本体 / カメラ | `FixedCamVr.*` | `Assets/Scenes/Main.unity` | `com.roiril.mawarimi` | `Assets/Scripts/` |
| **TableDuo（手アバター調査）** | **ハンド** / 手 / テーブル | `TableDuoVr.*` | `Assets/TableDuo/Scenes/TableDuoMain.unity` | `com.roiril.tableduo` | `Assets/TableDuo/` |

**⚠ 呼称で取り違えない**: ユーザーは「fixedcam」「ハンド」で呼ぶ。正式名（廻リ視 / TableDuo）からは導けない（特に「ハンド」⇄ TableDuo は名前に hand が出ない）。**作業前にこの表で対象アプリを確定**してから着手する。

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

## 6. 単一アプリ対象の作業（レビュー / 監査 / リファクタ / サブエージェント）

「fixedcam をレビューして」「ハンドのコードを直して」のように**片方のアプリだけ**を対象にする指示では、相手アプリを **視界（スコープ・プロンプト・読み込み）から完全に外す**。「壊さない」だけでなく「**持ち込まない**」。

- **対象 root を 1 つに固定する**: fixedcam = `Assets/Scripts/`（+ `FixedCamVr.*`）／ ハンド = `Assets/TableDuo/`。glob・ファイル列挙・grep は**この root 配下のみ**にする。相手 root を混ぜない
- **サブエージェントのプロンプトに相手アプリを書かない**: 文脈・アンチスコープ・「相互参照ゼロを確認」等の名目でも相手アプリ名/パスを織り込まない。混ぜると「scope が曖昧」に見え、相手アプリの所見が混入するリスクが出る（2026-06-16 実害: fixedcam レビューでアーキ観点プロンプトに TableDuo を多数織り込み、ユーザーから「スコープを理解できていない」と指摘）
- **相互参照チェックが要るなら一方向で**: 「対象アプリが相手を参照していないか」は対象アプリ側の asmdef / using を見れば足りる。相手アプリのファイルを開く必要はない
- **成果物（レビュー結果・差分）に相手アプリが出てきたら捨てる**: triage で相手アプリ由来の所見は対象外として除外し、その旨を明記する

## 7. ビルド/コンパイル中は Unity が拾うファイルを編集しない（着手前に状態を見極める）

別シュビー/ユーザーが**ビルド中・コンパイル中の間に `Assets/` 配下（.cs / .unity / .prefab / .asset / .mat / .shader）や `ProjectSettings/` を書き換えると、アセットインポータ起動・ドメインリロード・コンパイル競合でビルドが壊れる/別物が焼ける**。Unity Editor は 1 インスタンス共有なので、片方のビルドにもう片方の編集が割り込む（2026-06-29 実害: 並列でビルド中に遅延対策のコード編集へ着手しかけ、ユーザーに止められた）。

**着手前チェック（Unity が拾うファイルを触る作業すべて）**:
1. ユーザー/併走シュビーが**ビルド中・Play 中でないか**を確認（申告が最優先・最も確実。「いまビルド中」と言われたら Assets 編集は止める）
2. Unity MCP `mcpforunity://editor/state` を読む → `isCompiling=true` / `isPlaying=true` / 応答が `Connection closed`・timeout なら **Editor ビジー＝着手しない**（`unity-status` スキルが一括取得）
3. ビルド成果物（`Builds/*.apk`）の mtime が今まさに更新されている／BuildVariants のログが流れているならビルド中

**ビルド/コンパイル中にやってよいのは Assets 外だけ**: `.claude/` / `docs/` / `README` 等は Unity がインポートしないのでビルドに無関係 → 編集可。設計確定・調査・ドキュメント・ハーネス更新はこの待ち時間に進める。コードは「内容を確定して待機」までに留め、書き込みはビルド完了後。

**ビルド完了の確認**: PlayerSettings 復元（[BuildVariants](../../Assets/Editor/BuildVariants.cs) の finally）+ `editor_state` の `isCompiling=false` 復帰 + apk mtime 停止。これを確認してから Unity 系編集を再開する。自分がビルドするときも §3（同時ビルド禁止）と合わせ、**他の編集・ビルドが走っていないこと**を確認してから。

## チェックリスト（並列で着手する前に）

0. **いまビルド/コンパイル中じゃない？**（§7）→ Unity が拾うファイル（`Assets/` ・ `ProjectSettings/`）を触る前に申告 / `editor_state` で確認。ビジーなら Assets 外（`.claude/` ・ `docs/`）の作業へ切り替える
1. **どっちのアプリ？** → 呼称マッピング表（冒頭）で確定。「fixedcam／ハンド」を正式名・コード root に変換してから着手
2. 自分が触るのはどっちのアプリ？ → 相手のコード領域・シーン・prefab に手を出さない
3. 単一アプリ対象の作業（レビュー/監査等）？ → §6。相手アプリをスコープ・プロンプトに持ち込まない
4. 共有資源（ProjectSettings / URP / OVR / XR / manifest）を触る？ → 触るなら逐次 + 影響説明
5. Unity Editor を使う作業？ → 並列にしない（本シュビーが逐次）
6. ビルドする？ → 他のビルドが走っていないこと、PlayerSettings が復元済みであることを確認
