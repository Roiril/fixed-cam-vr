---
status: done
owner: shubie
created: 2026-05-03
completed: 2026-05-03
---

## 完了サマリ（2026-05-03）

- `FixedCamVr.Tracking` asmdef + `PlayerZone` / `PlayerZoneTracker` 実装、コンパイルエラー 0
- `Assets/Scenes/ZoneSandbox.unity` 新規作成（OVRCameraRig + [Streaming] + [Zones] × 3 + [Tracker]）
  - Zone_A_Center: index 0 / 緑 / Phone01
  - Zone_B_Right (x=+2.5): index 1 / 赤 / Phone02
  - Zone_C_Left  (x=-2.5): index 0 / 青 / Phone01
  - hysteresisShrink=0.15m、updateInterval=0.05s
- ビルド設定に ZoneSandbox.unity を追加
- 既存 Main.unity / Debug.unity / 既存スクリプト全て無改変
- 実機 Quest 3 検証はユーザに移譲

### worktree 落とし穴

Unity Editor は **main プロジェクト** (`C:/Users/kouga/Projects/Unity/fixed-cam-vr/`) を開いており、worktree (`.claude/worktrees/...`) のファイルは Unity から見えない。
このタスクの途中で発覚し、main プロジェクトで作業継続 → worktree に rsync する方式に切替。
詳細は [.claude/memory/unity_pitfalls.md](../../.claude/memory/unity_pitfalls.md) に追記。

---

# プレイヤー位置連動カメラ切替（バイオハザード固定カメラ風）

## 目的

VR 内のプレイヤー位置（OVRCameraRig の CenterEyeAnchor）に応じて、
表示中の DroidCam ソース（CameraStreamRegistry の active index）を自動切替する。
バイオハザード/おねがいおにいちゃん的な「部屋を移動するとカメラが切り替わる」体験。

## 大方針

- **既存 Main.unity / 既存 Scripts は無改変**。新規シーン + 新規 asmdef で完結
- 既存 API への接続点は `CameraStreamRegistry.SetActive(int)` ただ一点
- Meta XR 依存は **位置取得のみ**（`OVRCameraRig.centerEyeAnchor.position`）。
  Quest 不在時のフォールバックとして MainCamera.transform も許容
- ゾーンは **トリガー衝突ではなく距離/AABB 判定**（プレイヤーに Collider/Rigidbody を付けない方針 — CenterEyeAnchor を汚さない）

## アーキテクチャ

```
[OVRCameraRig.centerEyeAnchor]              ← 位置取得元（読み取りのみ）
        │
        ▼
[PlayerZoneTracker] ────────────────────────▶ [CameraStreamRegistry.SetActive]
        │                                          (既存・無改変)
        │ refers
        ▼
[PlayerZone × N]                            ← 各ゾーン定義（位置+範囲+対応カメラ index+優先度）
```

- `PlayerZone` は MonoBehaviour（シーン上に配置してエディタで直感編集）
  - 形状: AABB (BoxBounds) + 任意 Y 範囲
  - `cameraIndex` (registry の index)
  - `priority` (重なった時の優先順位)
  - OnDrawGizmos でワイヤフレーム可視化
- `PlayerZoneTracker` は registry と head transform を参照
  - 毎フレーム head の position を全 zone と AABB 判定
  - 該当する中で priority 最大のものを採用
  - 直近採用ゾーンと異なる場合のみ `SetActive` を呼ぶ
  - **ヒステリシス**: ゾーン境界ぎりぎりでのフリッカ防止に「現在ゾーンは縮小判定／他ゾーンは正規判定」の差を付ける
  - **どのゾーンにも入っていない時**: 直近のゾーンを維持（無音切替）

## 新規ディレクトリ・ファイル

```
Assets/
├── Scenes/
│   └── ZoneSandbox.unity                       ★ 新規
├── Scripts/
│   └── Tracking/                                ★ 新規ディレクトリ
│       ├── FixedCamVr.Tracking.asmdef           ★ 新規
│       ├── PlayerZone.cs                        ★ 新規
│       ├── PlayerZoneTracker.cs                 ★ 新規
│       └── Editor/
│           ├── FixedCamVr.Tracking.Editor.asmdef ★ 新規
│           └── PlayerZoneEditor.cs              ★ 新規（任意・Gizmo 強化）
```

asmdef 参照:
- `FixedCamVr.Tracking` → `FixedCamVr.Streaming`（read-only）
- Meta XR の OVRCameraRig には依存しない（位置だけ Transform 経由で受け取る）
  - 結果として `Assembly-CSharp` への参照不要・クリーン

## スクリプト設計

### PlayerZone.cs
```csharp
namespace FixedCamVr.Tracking
{
    [ExecuteAlways]
    public sealed class PlayerZone : MonoBehaviour
    {
        [SerializeField] private Vector3 halfExtents = new(1f, 2f, 1f);
        [SerializeField, Min(0)] private int cameraIndex = 0;
        [SerializeField] private int priority = 0;
        [SerializeField] private Color gizmoColor = new(0f, 1f, 0.5f, 0.25f);
        [SerializeField] private string label = "";

        public int CameraIndex => cameraIndex;
        public int Priority => priority;
        public string Label => string.IsNullOrEmpty(label) ? name : label;

        public bool Contains(Vector3 worldPos, float shrink = 0f) { /* AABB */ }
        private void OnDrawGizmos() { /* 半透明 box + label */ }
    }
}
```

### PlayerZoneTracker.cs
```csharp
namespace FixedCamVr.Tracking
{
    public sealed class PlayerZoneTracker : MonoBehaviour
    {
        [SerializeField] private CameraStreamRegistry? registry;
        [SerializeField] private Transform? headTransform; // OVRCameraRig.centerEyeAnchor
        [SerializeField] private PlayerZone[] zones = Array.Empty<PlayerZone>();
        [SerializeField, Min(0f)] private float hysteresisShrink = 0.15f; // m
        [SerializeField, Min(0f)] private float updateInterval = 0.05f;   // 20Hz
        [SerializeField] private bool logChanges = true;

        private PlayerZone? _current;
        private float _accumTime;

        private void Update() { /* update head pos, throttled re-evaluate, SetActive */ }
        private PlayerZone? Pick(Vector3 head) { /* current は shrink 適用 → 他 zone 優先度比較 */ }
    }
}
```

## 検証用シーン (ZoneSandbox.unity)

- OVRCameraRig（手動配置）
- `[Streaming]` 空 GameObject（Main.unity からプレハブ化していないので、同等構成を新規作成）
  - **重要**: Main.unity の `[Streaming]` GameObject を **プレハブ化しておくと両シーンで使い回せる**。
    既存改変なしの方針上、本フェーズではプレハブ化はせず、ZoneSandbox に同等構成を別途作る
- `[Zones]` 空 GameObject 配下に PlayerZone × 3（A/B/C ゾーン）
- `[Tracker]` 空 GameObject に PlayerZoneTracker、registry/headTransform/zones を assign
- 動作確認: Editor Play Mode で OVRCameraRig を WASD で動かし、ゾーン跨ぎで切替が起きること
  - Meta XR Simulator が無くても CenterEyeAnchor の position は動かせる

## 実装ステップ

1. asmdef 2 つ + ディレクトリ作成
2. `PlayerZone.cs` 実装 + Gizmo 確認
3. `PlayerZoneTracker.cs` 実装（ヒステリシス込み）
4. EditMode 単体テスト（任意・後回し可）: `PlayerZone.Contains` と Pick ロジックの境界条件
5. シーン `ZoneSandbox.unity` 作成 → MCP `manage_scene` 経由
6. `OVRCameraRig` / `[Streaming]` / `[Zones]` / `[Tracker]` 配置
7. `manage_build action=scenes` で Build Settings に登録
8. `refresh_unity` → `read_console` でコンパイル確認
9. **実機 Quest 3 検証はユーザー依頼**

## 受け入れ基準

- ZoneSandbox.unity で Editor 上、OVRCameraRig を移動するとゾーン境界跨ぎで `CameraStreamRegistry.SetActive` が呼ばれる
- ヒステリシスにより、境界上で振動移動しても切替が連発しない（≥ 0.15m 余分に超えるまで切替不可）
- Gizmo で各ゾーンが半透明 box として可視化され、対応する cameraIndex/label がエディタで判別可能
- 既存 Main.unity / 既存 Scripts に diff が一切入らない
- コンパイルエラー 0

## 受け入れ基準外

- 実機 Quest 3 動作確認（ユーザー）
- 部屋認識（OVRSceneManager）連動 — 後フェーズ
- ゾーン切替時のフェード/トランジション演出 — Fx スレッド側で扱う
- ゾーン定義の ScriptableObject 化 / シーン非依存化 — 必要が出たら次フェーズ

## リスク

- **ゾーンが重なった時の優先度**: priority 同値だと不定。ドキュメントに明示
- **Quest 実機での座標系**: floor-level vs eye-level でゾーン Y 範囲が変わる → ZoneSandbox で実機検証必須
- **registry が未生成のフレーム**: Awake 順依存。`Update` 冒頭で null guard

## 触るファイル一覧

すべて新規:
- `Assets/Scripts/Tracking/FixedCamVr.Tracking.asmdef`
- `Assets/Scripts/Tracking/PlayerZone.cs`
- `Assets/Scripts/Tracking/PlayerZoneTracker.cs`
- `Assets/Scripts/Tracking/Editor/FixedCamVr.Tracking.Editor.asmdef`（任意）
- `Assets/Scripts/Tracking/Editor/PlayerZoneEditor.cs`（任意）
- `Assets/Scenes/ZoneSandbox.unity`

既存無改変:
- `Assets/Scenes/Main.unity` / `Debug.unity` / `SampleScene.unity`
- `Assets/Scripts/Streaming/*` / `Assets/Scripts/OvrBridge/*`
