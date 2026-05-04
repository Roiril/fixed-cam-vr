---
name: unity-vr
description: Unity / VR 共通規約。Assets/ 編集前に読む
globs:
  - "Assets/**/*.cs"
  - "Assets/**/*.unity"
  - "Assets/**/*.prefab"
  - "Assets/**/*.asset"
  - "ProjectSettings/**"
---

# Unity / VR 共通規約

## ディレクトリ構成（Assets/ 配下）

```
Assets/
├── Scenes/                  # *.unity（命名: PascalCase）
├── Scripts/
│   ├── Streaming/           # MJPEG / WebRTC 取り込み
│   ├── Passthrough/         # パススルー制御
│   ├── UI/                  # ワールドスペース UI
│   └── Common/              # 共有ユーティリティ
├── Prefabs/<Feature>/
├── Art/
│   ├── Materials/
│   ├── Shaders/             # ShaderGraph (.shadergraph) / HLSL (.shader)
│   └── Textures/
├── Settings/                # ScriptableObject 設定
└── ThirdParty/              # 外部アセット（Meta XR SDK は除く）
```

## C# 規約

- `namespace FixedCamVr.<Feature>`（例: `FixedCamVr.Streaming`）
- 機能単位で `.asmdef` を切る（ビルド時間短縮）
- `[SerializeField] private` を基本。`public` フィールド禁止
- `#nullable enable` を新規 .cs に付与
- 非同期処理は `async Task` + `CancellationToken`。コルーチンは VR ループ的に避ける（フレーム同期しないため）
- Update 内で `new`（特にバイト配列・文字列連結）禁止 → 90Hz 維持

## VR パフォーマンス

- **目標**: 90 FPS 維持（Quest 3 デフォルト 90Hz）
- **ドローコール**: 100 以下推奨。Static Batching / SRP Batcher 活用
- **テクスチャ**: ASTC 6x6 圧縮、ストリーミング用は別途 RGBA32 で動的更新
- **シェーダ**: 複雑なピクセルシェーダはポスト FX のみに限定。マテリアルは Lit ではなく **URP/Unlit** 基本
- **GC**: フレーム内アロケーション 0 を目標。`Profiler` で確認

## シーン管理

- Main シーンは `Assets/Scenes/Main.unity`
- デバッグ用は `Assets/Scenes/Debug/<Name>.unity`
- シーン切替は `SceneManager.LoadSceneAsync` のみ（同期版禁止：VR で長時間ブロックすると酔う）

## 入力

- コントローラ: XR Interaction Toolkit の `XRController` + `InputActionReference`
- Action 定義は `Assets/Settings/InputActions.inputactions` 単一ファイルに集約

## ビルド

- ターゲット: Android / IL2CPP / ARM64 単独
- `Development Build` 有効でデプロイし、初期は `adb logcat` でログ確認
- リリースビルドは `Development Build` を外す

## Editor 拡張メニュー設計

カスタムメニューは **`Tools/FixedCamVr/`** 配下のみ（トップレベル `FixedCamVr/` を切らない）。サブメニューは 4 階層に固定：

| サブメニュー | priority 範囲 | 用途 |
|---|---|---|
| 直下（ショートカット可） | 0〜49 | ユーザー常用（Open Main Scene 等） |
| `Setup/` | 50〜99 | シーン構築・アセット生成（Setup Main Demo Scene 等） |
| `Layout/` | 100〜199 | Editor レイアウト管理（ユーザー初期設定） |
| `Diagnostics/` | 200〜299 | シュビーが叩く検証ツール（Ping / Run Tests 等） |

新規メニュー追加時は **どの階層に置くべきか判断**してから書く。階層基準が曖昧なら直下に置かない（ゴチャゴチャ化を防ぐ）。MenuItem パスを README / TROUBLESHOOTING / docs/ で参照している箇所も同時更新する。
