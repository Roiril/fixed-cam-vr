---
name: unity-vr
description: Unity / VR 共通規約。Assets/ 編集前に読む
paths:
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
│   ├── Streaming/           # MJPEG 取り込み（asmdef: FixedCamVr.Streaming）
│   ├── Diagnostics/         # HUD / ログ / StartupFader
│   ├── Tracking/            # PlayerZone（HMD 座標駆動）
│   ├── Fx/                  # ポスト FX（Blit/Compute/Particles/Source）
│   ├── OvrBridge/           # OVRInput 接点（asmdef なし = Assembly-CSharp。意図的）
│   └── <Feature>/Editor/    # 各機能の Editor 拡張（別 asmdef）
├── Prefabs/<Feature>/
├── Settings/                # ScriptableObject 設定（Cameras/ 等）
├── TableDuo/                # 同居サブプロジェクト（CLAUDE.md 参照、相互参照禁止）
└── ThirdParty/              # 外部アセット（Meta XR SDK は除く）
```

Passthrough/・UI/・Common/・Art/ は後続フェーズで必要になったら切る（先回りで作らない）。

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

- 現状は `OvrBridge/OvrControllerBridge.cs` で OVRInput を直接参照（マッピングは Inspector で変更可）
- XRI の `InputActionReference` + `Assets/Settings/InputActions.inputactions` への集約は、入力が増えた段階で導入（現状ファイル未作成）

## ビルド

**入口は [tools/unity.ps1](../../tools/unity.ps1) だけ**（`skills/quest-build`）。Editor の GUI は使わない。

```powershell
.\tools\unity.ps1 build fixedcam            # → Builds/mawarimi.apk
.\tools\unity.ps1 build fixedcam -Release   # 提出用（Development なし）
```

**焼き直し待ちがあると `fixedcam` の build は落ちる**（2026-08-09〜）。演出はシーンに焼かれた
GameObject、HMD の日本語は静的ベイクのアトラスなので、`.cs` を直しただけでは APK に入らない。
判定は [tools/unity.ps1](../../tools/unity.ps1) の `$Menus` の `Src`（入力の .cs）対 `Out`（焼いたもの）の
mtime。**新しい焼き直し工程を足したら `Src` / `Out` と `$BakeGuard` に足す**（足さないと誰も見ない）。

- ターゲット: Android / IL2CPP / ARM64 単独
- **3 アプリ並存**: 廻リ視 / TableDuo / MyCobotHand（[BuildVariants.cs](../../Assets/Editor/BuildVariants.cs)）。
  productName / パッケージ ID をビルド時のみ切り替え → Quest 上で別アプリとして同居。
  ProjectSettings は終了後に必ず復元される（手動の Build Settings ではどれも同名・同 ID になるので使わない）
- `Development Build` 有効でデプロイし、初期は `adb logcat` でログ確認
- リリースは `-Release`（Development なし、出力名 -release）

**ビルド以外の Editor メニューも CLI から呼ぶ** → `.\tools\unity.ps1 menu`（引数なしで一覧）。

## 体験そのものの仕様はここに無い

ゾーン / 位置合わせ / コントローラ入力 / 導入・終幕 / 周回カウント / HMD に出す文言の規約は
**[show-design.md](show-design.md)**（`Assets/Scripts/{Streaming,Tracking,Diagnostics,Input,OvrBridge}`
とシーンを触るときに載る）。配信と show.json の契約は [streaming.md](streaming.md)。

## ⚠ `renderQueue` が 5000 を超えると URP はそれを 1 度も描かない（2026-07-31 実害）

URP の透明パスが描くのは **`RenderQueueRange.transparent` = [2501, 5000]** だけ
（`Library/PackageCache/com.unity.render-pipelines.universal@14.0.12/Runtime/UniversalRenderer.cs`）。
Unity 公式 API リファレンスも `Material.renderQueue` は **「[0..5000] の範囲でなければ正しく動かない」**
と書いている。**5000 を超えた値を入れると、どの描画パスにも入らず 1 ピクセルも出ない。**

実害（同日に 2 件、原因は同一）:

- **導入の構造線**（`IntroStructureWire`・部屋の壁とカメラの印）が `5000 + 100 = 5100` で、実機で **1 本も描かれていなかった**
- **HMD 内の指示テキスト**（`IntroPrompt`・「右手を上げてみてください」）も `5100` で同様。
  段 5 の合図は**言葉でしか伝えない**ので、3 周目の反転の伏線が丸ごと成立していなかった

**気づけなかった理由**: `Shader.Find` は成功し、`_renderers.Count > 0` になり、`Place()` も走って
`lr.enabled = true` になる。**コードは「出した」と思っていて警告が 1 件も出ない。**
実機録画をフル解像度で 0.2 秒刻みに見て初めて「0 本」だと分かった。

どちらも「覆い（`IntroVeil`）より後に描く」ために queue を上げていたもので、順序を作る意図は正しい。
**上げるのではなく、覆いの側を下げて全員を 5000 以内に収める**（現在は覆い 4900 / 後続 5000）。

→ **`renderQueue` を明示的に代入する / シェーダの `Queue` タグに `Overlay+N` を書くときは、
実効値が 5000 を超えていないか必ず計算する**（`Overlay` = 4000）。

## ⚠ 実行時 `Shader.Find` するシェーダはビルドで剥がれる（2026-07-31 実害）

Unity は「どのマテリアルからも参照されていないシェーダ」をビルドから外す。だから
`Shader.Find("FixedCamVr/Xxx")` で実行時に探す型のコードは、**Editor では動くのに実機で null になる**。

- 実害: `FixedCamVr/IntroVeil`（導入演出の覆い）が剥がれ、実機で
  `[IntroVeil] シェーダ FixedCamVr/IntroVeil が見つかりません` が出て**枠が一切描かれなかった**。
  `FixedCamVr/ScreenComposite` が無事だったのは、シーンの Screen Quad のマテリアルが参照しているから
- 対策: **`ProjectSettings/GraphicsSettings.asset` の `m_AlwaysIncludedShaders` に加える**
  （`- {fileID: 4800000, guid: <shader の guid>, type: 3}`）。
  マテリアル経由で参照させる／`Resources/` に置く でも可
- **新しく `Shader.Find` を書いたら、その場で Always Included に入れる。**
  忘れても Editor では動くので、実機で画が出なくなるまで気づけない

**全自作シェーダの安全性を 10 秒で確認する**（`mat`=マテリアル経由 / `always`=Always Included。
**どちらも 0 で `Shader.Find` されているものが危険**）:

```bash
for s in $(grep -rh '^Shader "' Assets/Art/Shaders --include=*.shader | sed -E 's/Shader "([^"]+)".*/\1/'); do
  f=$(grep -rl "Shader \"$s\"" Assets --include=*.shader | head -1)
  g=$(grep "guid:" "${f}.meta" | head -1 | sed -E 's/.*guid: ([a-f0-9]+).*/\1/')
  printf "  %-38s mat=%s always=%s find=%s\n" "$s" \
    "$(grep -rl "$g" Assets --include=*.mat | wc -l)" \
    "$(grep -c "$g" ProjectSettings/GraphicsSettings.asset)" \
    "$(grep -rc "Shader.Find(\"$s\")" Assets/Scripts --include=*.cs | grep -v ':0' | wc -l)"
done
```

2026-07-31 時点の結果: CG 人形の 4 つ（ShowActor / ShowGroundBlob / ShowOccluder /
ShowShadowProjector）は **mat 経由で安全**。Fx の 3 つ（ChromaticAberration / CrtPostFx /
TestPattern）はどこからも参照されていない = **現在未使用**（使うときに mat か Always Included が要る）。
IntroVeil だけが `Shader.Find` のみで、剥がれていた。

## 起動時の視界保護（StartupFader）

VR では **Play 開始から最初の安定フレームまで** の間、以下が同時に起こり「不安定な絵」が露出する：
- Quest システムの砂時計表示（OS レイヤ）
- OVRCameraRig がヘッドポーズを取得するまで数フレーム
- MJPEG ストリームの接続待ち（数百 ms 〜 数秒）
- URP の RT 確保 / ポストプロセス初期化の最初のフレーム

これを直接ユーザーに見せると **目に悪い + 体験の質を下げる**。原則：

- CenterEyeAnchor 配下に **head-locked な黒 Canvas** を Awake 時に生成し、最初から視界を覆う
- 解除条件は `(min_hold) AND (any_stream_connected OR max_wait_timeout)` の AND/OR
  - `min_hold`（既定 0.5s）: 早すぎる解除での pop-in 防止
  - `max_wait_timeout`（既定 4s）: スマホがスリープ等で永遠に繋がらない時のハードガード
- フェードアウトは 0.5s 程度の線形 alpha 1→0
- 完了したら GameObject ごと破棄（Update を残さない）

実装は [`StartupFader`](../../Assets/Scripts/Diagnostics/StartupFader.cs)。`MainDemoSceneSetup` で自動配置される。

## Editor 拡張メニュー設計

カスタムメニューは **`Tools/FixedCamVr/`** 配下のみ（トップレベル `FixedCamVr/` を切らない）。サブメニューは 4 階層に固定：

| サブメニュー | priority 範囲 | 用途 |
|---|---|---|
| 直下（ショートカット可） | 0〜49 | ユーザー常用（Open Main Scene 等） |
| `Setup/` | 50〜99 | シーン構築・アセット生成（Setup Main Demo Scene 等） |
| `Layout/` | 100〜199 | Editor レイアウト管理（ユーザー初期設定） |
| `Diagnostics/` | 200〜299 | シュビーが叩く検証ツール（Ping / Run Tests / Preview Registration Viz / Preview Show Actor / Preview Show Composite 等） |

**新しいメニューは CLI から呼べる形で書く**（2026-08-09〜。入口は `.\tools\unity.ps1 menu`）。守ることは 4 つ：

1. **`public static` の引数なし**にする（`-executeMethod` は private も引数ありも呼べない）
2. **シーンが要るなら自分で開く** — `EditorCliArgs.EnsureScene(path)`（TableDuo 側は `TableDuoEditorCli.EnsureScene()`）。
   batchmode は空シーンで始まるので、`GameObject.Find` 頼みのものは開かないと軒並み落ちる。
   **GUI では何もしない**ので、人が開いているシーンを奪わない
3. **モーダルを出さない** — batchmode の `DisplayDialog` は表示されず **false** を返す。
   分岐が無いと CLI 実行が黙って何もせず終わる。`EditorCliArgs.IsBatch` で囲う
4. 値が要るなら `-Set key=value` → `EditorCliArgs.Get("key")`

書いたら [tools/unity.ps1](../../tools/unity.ps1) の `$Menus` に 1 行足す（`Out` に出力先を書けば、
**exit 0 でも中で LogError して何もしなかった場合を捕まえられる**）。


新規メニューは **どの階層に置くか判断**してから書く。階層基準が曖昧なら直下に置かない。
MenuItem パスを README / TROUBLESHOOTING / docs/ が参照していれば同時に直す。

## レイヤの規約と、レイヤを増やす手

**CG 人形は専用レイヤ `ShowCg`(slot 9) に置き、HMD カメラの cullingMask からは外す**
（`.\tools\unity.ps1 menu scene` が自動で外す）。外すのを忘れると人形が VR 空間にそのまま浮いて見え、
「映像の中に居る」という前提が壊れる。

レイヤを増やすときは `ProjectSettings/TagManager.asset` の `m_Layers` の空きスロットに名前を書く。
**⚠ Editor が起動しているときに直編集すると、Unity のメモリ側が勝って次の保存で消える**
（[[cg_actor_and_layer_traps]]）。**Editor を閉じてから編集する**（batchmode は毎回読み直すので確実）。
起動中の Editor を閉じられない事情があるときだけ MCP の `manage_editor action=add_layer`
（[reference/mcp-unity.md](../reference/mcp-unity.md)）へ落ちる。
