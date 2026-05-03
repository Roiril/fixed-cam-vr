# コントリビューションガイド

fixed-cam-vr への貢献ルール。詳細な動作規約は [CLAUDE.md](CLAUDE.md) と [.agent/rules/](.agent/rules/) を参照。

## 言語

Issue / PR / コミットメッセージ / コードコメントはすべて **日本語** を基本とする。識別子（クラス名・メソッド名）のみ英語。

## ブランチ運用

- メインブランチ: `master`
- 作業ブランチ: `feature/<slug>`（例: `feature/passthrough-dim`, `feature/cctv-shader`）
- バグ修正: `fix/<slug>`
- ドキュメント整備: `docs/<slug>`
- master への直接コミットは避け、Pull Request 経由でマージする

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

## Pull Request の流れ

1. `feature/<slug>` ブランチで作業
2. **コンパイル OK を確認**（Unity Editor で `read_console` / `refresh_unity` または `-batchmode -quit`）
3. EditMode テスト（[Assets/Tests/Streaming/](Assets/Tests/Streaming/)）が pass することを確認
4. 実機 Quest 3 動作確認は **PR 上でユーザーに依頼**（Claude Code 側からは自動化不可）
5. PR テンプレ：
   - 目的 / 変更点 / コンパイル結果 / 実機検証ステータス（未検証なら明示）
6. レビュー後 master にマージ

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
- [.agent/rules/unity-vr.md](.agent/rules/unity-vr.md) — Unity / VR 共通
- [.agent/rules/meta-xr.md](.agent/rules/meta-xr.md) — Meta XR SDK
- [.agent/rules/streaming.md](.agent/rules/streaming.md) — MJPEG / WebRTC
- [.agent/rules/mcp-unity.md](.agent/rules/mcp-unity.md) — Unity MCP 経由の編集手順
- [docs/architecture.md](docs/architecture.md) — モジュール依存関係
- [docs/performance.md](docs/performance.md) — Quest 3 90Hz 維持の計測手順
