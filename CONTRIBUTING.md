# コントリビューションガイド

fixed-cam-vr への貢献ルール。詳細な動作規約は [CLAUDE.md](CLAUDE.md) と [.claude/rules/](.claude/rules/) を参照。

## 言語

Issue / PR / コミットメッセージ / コードコメントはすべて **日本語** を基本とする。識別子（クラス名・メソッド名）のみ英語。

## ブランチ運用

**幹は `master` の 1 本だけ。** 作業はメインの木で master へ直接コミットして push する。Pull Request は使わない。

- メインの木（このフォルダ）で枝を切らない・枝へ移らない（hook が止める）
- 枝を使うのは `git worktree add` で切った別の木の中だけ。取り込んだら同じ作業の中で木と枝を消す
- 残り物の点検と片づけは `py -3.11 tools/git-tidy.py`（`--apply` で消す）
- 詳細は [.claude/rules/git-workflow.md](.claude/rules/git-workflow.md)

## コミットメッセージ規約

```
<type>：<日本語の要約>
```

- **区切り文字**: 全角コロン `：`（半角 `:` ではない）
- **要約**: 日本語、1 行 50 文字以内目安
- **type**: 以下のいずれか
  - `feat` — 新機能
  - `fix` — バグ修正
  - `refactor` — リファクタ（挙動変更なし）
  - `chore` — 雑務（依存更新・ビルド設定等）
  - `docs` — ドキュメントのみ
  - `test` — テストのみ
  - `style` — フォーマットのみ
  - `perf` — パフォーマンス改善
  - `build` — ビルドシステム
  - `ci` — CI 設定

大規模変更時は 1 行空けて本文に箇条書きで詳細を書く。

### 例

```
feat：MJPEG 受信時の再接続を exponential backoff 化
```

```
refactor：Tracking 名前空間を Streaming から分離

- PlayerZone / PlayerZoneTracker を新 asmdef へ移動
- README のモジュール表を更新
```

## 変更の流れ

1. 直す。途中の確認は `py -3.11 tools/verify.py --quick`（数秒。道具のテスト・ハーネス・焼き直し待ち・枝の残り物）
2. 区切りで `py -3.11 tools/verify.py`（1〜2 分。上に加えて C# の全テストと、HMD なしの通し走行）
3. 通ったら、明示したファイルだけを master へコミットして push する
4. 見た目・音・実機でしか出ないもの（シェーダの剥がれ・パススルー・IL2CPP）は `py -3.11 tools/quest-record.py --sec 45 --walk`

## コードスタイル

### C#

- `#nullable enable` を新規 .cs に付与
- namespace は `FixedCamVr.<Feature>` 形式（例: `FixedCamVr.Streaming`, `FixedCamVr.Tracking`, `FixedCamVr.Fx`）
- MonoBehaviour のフィールドは `[SerializeField] private` を基本とし、外部公開はプロパティ経由
- `public` フィールドは禁止
- 非同期処理は `async/await` + `CancellationToken`（将来 UniTask に移行予定）
- フレーム内 GC アロケを避ける（特に MJPEG デコード経路、Update / FixedUpdate）

### アセンブリ定義（.asmdef）

- 機能単位で `.asmdef` を切る（ビルド時間短縮 + 依存方向の明示）
- 既存例:
  - `Assets/Scripts/Streaming/FixedCamVr.Streaming.asmdef`
  - `Assets/Scripts/Tracking/FixedCamVr.Tracking.asmdef`（→ Streaming に依存）
  - `Assets/Scripts/Fx/FixedCamVr.Fx.asmdef`（→ Streaming + URP に依存）
- Editor 専用コードは `Editor/` サブフォルダ + `*.Editor.asmdef`（`includePlatforms: ["Editor"]`）に分離

### Unity 規約

- シーン: `Assets/Scenes/<feature>.unity`
- prefab: `Assets/Prefabs/<Feature>/`
- マテリアル / シェーダ: `Assets/Art/Materials/`, `Assets/Art/Shaders/`
- ScriptableObject 設定: `Assets/Settings/`
- マジックナンバー（URL・ポート・解像度）はハードコードせず ScriptableObject 化

### Quest 3 / VR 固有

- `OVRCameraRig` を使用、自前 Camera は置かない
- 90Hz 維持を最優先
- ビルドターゲット: Android / IL2CPP / ARM64 のみ

## 禁止事項

- `ProjectSettings/` の理由なき変更（特に Graphics / Quality）
- `Packages/manifest.json` の編集はユーザーへ事前報告
- `Library/` のコミット
- 認証情報のハードコード（`StreamingAssets/secrets.json` 等を `.gitignore` 管理）

## 参考

- [CLAUDE.md](CLAUDE.md) — プロジェクト全体規約
- [.claude/rules/unity-vr.md](.claude/rules/unity-vr.md) — Unity / VR 共通
- [.claude/rules/meta-xr.md](.claude/rules/meta-xr.md) — Meta XR SDK
- [.claude/rules/streaming.md](.claude/rules/streaming.md) — MJPEG / WebRTC
- [.claude/reference/mcp-unity.md](.claude/reference/mcp-unity.md) — Unity MCP 経由の編集手順
- [README.md](README.md) — モジュール構成・ディレクトリ・実装済みコード
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — Quest 3 90Hz 維持の計測手順（OVR Metrics / Profiler）と現場対処
