---
name: table_duo_tabletop_prop_authoring
description: TableDuo 卓上に新しいボードゲーム/プロップ（駒・チップ・ボード・サイコロ・トークン）を追加する再利用レシピ。配置関数・掴み/物理・TableProps レイヤー・卓上拘束・冪等 Setup・実機なし検証・GLB 前提・ハマりどころ
metadata: 
  node_type: memory
  type: project
  originSessionId: e0c32ab3-a0c4-4fa9-92da-2ae99a589cff
---

海底探検（Deep Sea Adventure）を TableDuo の卓上に「実際に遊べる形」で実装した知見を、**別のボードゲーム/卓上プロップを足すとき用の再利用レシピ**に一般化したもの（2026-07-18 抽出）。対象は TableDuo（`Assets/TableDuo/`）専用。廻リ視（`Assets/Scripts/`）とは無関係。

母体の現状・設計判断は [[table_duo_study_status]] / [[table_duo_layout_tuning]] / `.claude/plans/2026-07-08_tableduo-piece-physics.md`。ここは「新規追加の作法」に絞る（重複はリンクで逃がす）。

## 0. どこに何を書くか（全体像）

- **コード**: [TableDuoSceneSetup.cs](../../Assets/TableDuo/Scripts/Editor/TableDuoSceneSetup.cs) の `PlaceDeepSeaAdventure` / `PlaceAlgo` が手本。新ゲームは `PlaceXxx(Transform parent, float topY, float cx, float cz, float hx, float hz, ...)` を作り `Setup()` 本体から呼ぶ（topY=天板上面 Y、cx/cz=天板中心 XZ、hx/hz=天板半径、edge=縁マージン）。
- **GLB 資産**: `Assets/TableDuo/ThirdParty/<Game>/glb/`。glTFast 取込（テクスチャ埋め込み・実寸メートル）。
- **反映**: メニュー `Tools/FixedCamVr/Setup/Setup TableDuo Scene`（冪等）を再実行 → `TableDuoMain.unity` にベイクされる。**シーン差分は毎回巨大**（全ルート作り直しのため。正常）。

### 複数ゲームとランタイム切替（2026-07-18〜・[GameSwitcher](../../Assets/TableDuo/Scripts/Net/GameSwitcher.cs)）

卓上ゲームは `Props/Game_<id>` 別ルートに**全部ベイクし常時 spawn**、[GameSwitcher](../../Assets/TableDuo/Scripts/Net/GameSwitcher.cs)（server 権威 NetworkVariable）が 1 つだけ実体化する（stow/show。NGO 1.x の in-scene despawn/respawn・SetActive 地雷を回避）。**新ゲーム追加の手順**:

1. `Setup()` 内で `Game_<id>` ルートを作り `PlaceXxx` で配置、**非デフォルトゲームは `BakeStowedState(root)`**（Renderer/Collider off + kinematic ベイク＝Editor/Preview/起動直後がランタイム既定と一致）
2. `WireGameSwitcher(...)` の 3 配列（gameRoots / gameIds / displayNames）へ追加 — これだけで FacilitatorPanel のボタンと `mark?label=game_<id>` の curl 導線に自動で出る
3. **stow の掴み排除は `Grabbable.IsStowed` が担う**（掴み判定 `PinchGrabInteractor` はコライダー非依存の距離検索なので Collider off だけでは掴めてしまう — 罠）。GameSwitcher.ApplyLocal が設定するので追加作業は不要だが、独自に隠すものを作る時はこのフラグを必ず通す
4. 切替はサーバ側で両ゲームの盤面リセットを兼ねる（初期姿勢は GameSwitcher が BoardReset と同じ流儀でキャプチャ）。ゲーム別の追加リセット処理は不要

## 1. 最小レシピ（プロップ 1 個 = 2 行 + α）

```csharp
var piece = PlaceModelRealScale($"{DsaGlbDir}/<model>.glb", parent, "DSA_<name>",
    new Vector3(x, topY, z), yaw, grabbable: true, scale: chipScale, physics: true);
SetSurfaceClamp(piece, topY, cx, cz, hx, hz);   // ← 掴めるプロップは必ずセットで呼ぶ
```

- サイコロなら追加で `piece.AddComponent<DiceRoller>();`（§7）。
- 実寸不明の FBX 家具等は `PlaceModelRealScale` ではなく `InstantiateModelFitHeight`（ターゲット高さへ正規化 + 幅制限 + 接地）を使う。GLB は実寸保証があるので前者。

## 2. `PlaceModelRealScale` の引数（意味と使い分け）

`PlaceModelRealScale(glbPath, parent, name, pos, yaw, grabbable, scale=1f, physics=false, ccd=false, faceDown=false)`

| 引数 | 効果 |
|---|---|
| `grabbable:true` | `NetworkObject` + `NetworkTransform`(Interpolate=true, SyncScale 全 OFF) + `Grabbable` を付与＝サーバ権威のピンチ掴み対象になる |
| `physics:true` | さらに `AttachPiecePhysics` を呼ぶ＝`BoxCollider`(bounds 適合) + `Rigidbody`(mass 0.1) + `NetworkRigidbody` + `TableProps` レイヤー。掴み/投げ/転がし/卓外リスポーンが有効 |
| `physics:false`（既定） | Rigidbody 無し＝**kinematic 追従のみ**（掴んで動かせるが投げても落ちない。旧カード等） |
| `ccd:true` | `CollisionDetectionMode.ContinuousDynamic`。高速投擲のトンネリング防止。**サイコロ専用**（他は Discrete で足りる） |
| `faceDown:true` | 水平軸まわり 180° 反転して配置（宝物チップを裏向き初期配置する用途）。反転後も最下点を topY に接地するので正立する |
| `scale` | 拡大率。§5（ピンチ精度で 1.6 倍が基準） |

## 3. プロップが持つコンポーネント一式

| コンポーネント | 担当 |
|---|---|
| `NetworkObject` | NGO スポーン・同期の単位 |
| `NetworkTransform` | サーバ権威の transform 同期・補間。SyncScale は全 OFF |
| `Grabbable` | 掴み状態機械（Held/Free。§4）。サーバ駆動追従・先着裁定・卓上クランプ・投擲速度推定を全部持つ |
| `Rigidbody`（physics 時） | mass 0.1, interpolation=Interpolate |
| `BoxCollider`（physics 時） | bounds 適合。PhysicMaterial(friction 0.5 / bounciness 0.3) |
| `NetworkRigidbody`（physics 時） | 非権威クライアント側を自動 kinematic 化（物理はサーバのみ） |
| レイヤー `TableProps` | 自分同士 + 天板のみ衝突。手・アバターとは衝突させない |

## 4. 物理あり vs kinematic 追従のみ — 選択基準

`Grabbable`（[Grabbable.cs](../../Assets/TableDuo/Scripts/Net/Grabbable.cs)）は `GetComponent<Rigidbody>()` の**有無で自動分岐する**（プロップ側にコードは要らない）:

- **Held**: `isKinematic=true` でサーバが保持者の手 pose に追従。`surfaceY` クランプは保持中のみ。
- **Free**: リリース時に dynamic へ戻し、保持中の pose 履歴リングバッファから推定した線速度・角速度を与える（投げ・ひっくり返し・転がしが成立）。卓外へ落ちたら spawn 位置へ自動リスポーン。
- **速度推定は平滑化前の生 pose から**行う（平滑 Lerp 後だと速度が減衰し「投げても落ちるだけ」になる）。

判断:
- 転がす/投げる/倒す＝**物理**（`physics:true`）。ただの位置マーカーで動かすだけ＝kinematic でも可（が、統一感のため基本は物理に寄せる）。
- **`TableProps` レイヤーで手・アバターと衝突させないのは必須**。トラッキングの手にコライダーが触れると jitter で駒が爆ぜる定番事故を防ぐため。掴みは物理接触ではなくピンチ方式（サーバ駆動）。レイヤー分離は `PiecePhysicsConfig`（Systems 常駐・起動時に自己衝突のみへ設定）が担保。
- **⚠ 薄板の物理付与はトンネリング注意**。潜水艦ボード（3mm 薄板）を 2026-07-18 に物理統一したが、dynamic 薄板は落下・投擲で貫通し得る。薄板系を物理化したら実機で落下挙動を要確認（サイコロは CCD で対策済み、他の薄板は未対策）。

## 5. スケール = ハンドトラッキングのピンチ精度

実寸 3cm 程度のチップは指のピンチに対して小さすぎる → `const float chipScale = 1.6f`（掴めるチップ類の基準拡大率）。駒 1.6 / サイコロ 1.5。**ボードは 1.6x にすると連鎖配置と噛み合わないので実寸のまま**。空気マーカー（赤色マーカー）はチップより一回り小さく `chipScale*0.8`=1.28。新プロップも「ピンチで摘めるか」を基準にスケールを決める。

## 6. 卓上拘束 `SetSurfaceClamp`

掴めるプロップに `Grabbable` の `surfaceY`/`surfaceCenter`/`surfaceHalf` を焼き込む（テーブル貫通・卓外逸脱防止）。`hx-0.02`/`hz-0.02` で卓縁の少し内側までに制限。**掴めるプロップを置いたら必ず呼ぶ**（呼び忘れるとクランプ無しで手が天板下に潜るとプロップも沈む）。最下点は回転で変わるため保持中は毎フレーム再計算される。

## 7. サイコロ = 物理転がし + 静止面読み取り（`DiceRoller`）

[DiceRoller.cs](../../Assets/TableDuo/Scripts/Net/DiceRoller.cs) を `Grabbable`+`Rigidbody` と同じ GameObject に付ける（`AddComponent<DiceRoller>()`）:

- 離すと Grabbable が投擲速度を与えて物理転がり → **静止したらサーバがローカル 6 軸のうち world up と最も揃う軸を `faceValues[]` で出目に変換** → `DiceRolled` イベント → SessionLogger が CSV `dice` 行に記録。
- 静止判定: 速度閾値未満が `RestSeconds`(0.4s) 継続 or PhysX スリープ。`RollTimeout`(6s) で強制確定（縁立ち・挟まりのスタック回避）。
- ピンチ投げは手首回転が乗りにくいので、サーバが線速度に応じたタンブル回転を強制付与する（置いただけ＝0.3m/s 未満は乱さない）。
- **VR 内に数字表示は出さない設計**（「同じサイコロを見る」共同行為であることを優先。ユーザー決定）。
- **⚠ `faceValues` 既定 `{1,2,3,1,2,3}` は die.glb の実テクスチャ面と未照合**。新サイコロを足したら初回 L0/実機で面→値を校正する。

## 8. 盤面リセットは全 `Grabbable` を自動走査（`BoardReset`）

[BoardReset.cs](../../Assets/TableDuo/Scripts/Net/BoardReset.cs) はサーバ起動時に `FindObjectsOfType<Grabbable>()` 全ての初期姿勢を記憶し、`curl http://<hostIP>:7780/mark?label=reset_board` で初期配置へ復元（保持中は `ServerForceRelease` → Rigidbody は velocity ゼロ化）。**新規プロップが `Grabbable` を持っていれば個別配線ゼロで自動的にリセット対象になる**。

## 9. GLB 資産の前提（`PlaceModelRealScale` が単純に成立する理由）

- 単位はメートル実寸（宝物チップ ≈0.03unit=3cm）。glTFast 取込・テクスチャ埋め込み。
- **各単体 GLB は原点中心・水平（XZ 平面に寝かせ Y-up）で書き出す**。この前提があるから接地ロジック（bounds.min.y を topY に合わせるだけ）が成立する。新ゲームの GLB もこの向き・原点で用意する。
- GLB 生成元は別プロジェクト `model-lab`（`models/deep-sea-adventure/` にスクリプト一式）。新規ボードゲームの 3D 化はここで起こせる。

## 10. 冪等 Setup と実機なし検証（詳細は既存へ）

- `Setup()` は `DeleteRoot("[TableDuo]")` 等で既知ルートを全削除 → 再構築（確認ダイアログ無し。MCP/batchmode でモーダルが Editor をブロックした実害あり）。派生値は根の定数（`TableTopHeight` 等）から自動導出。
- 配置チューニングは **Setup 実行 → `Diagnostics/Preview Table + Remy seated`（Play 不要スクショ、[TableDuoTablePreview.cs](../../Assets/TableDuo/Scripts/Editor/TableDuoTablePreview.cs)）→ PNG 確認** のループ。手順詳細は [[table_duo_layout_tuning]]。
- **OVR シーンは Link/HMD 無しの Play がハングする**ので、Preview スクショが実質唯一の高速検証（[.claude/rules/mcp-unity.md](../rules/mcp-unity.md)）。
- 編集 → 実行の前に `Library/ScriptAssemblies/TableDuoVr.Editor.dll` の mtime が編集後になっているか確認してから `execute_menu_item` する（非フォーカス Editor はコンパイルを遅延する。mcp-unity.md 参照）。

## 11. ルール裁定はコード化しない（研究アプリの設計思想）

TableDuo は「手だけアバターとの無言交渉の観察」が目的なので、**進行管理・ターン・自動減算・勝敗判定を実装しない**（間違い→手役のジェスチャー訂正そのものが観察データ）。提供するのは「掴める・投げられる・並ぶ・リセットできる」物理と配置だけ。別ゲームを足すときもこの線を守る（ルール強制の実装は却下方針）。汎用視点では「物理・掴み・配置だけ提供し進行は人間に委ねる」選択。

## 12. 罠チェックリスト（DSA 実装で踏んだもの）

1. **glTFast 導入直後は Burst 破損で GLB import が失敗しがち**（`GLTFast.Jobs.* Burst failed to compile`）→ Unity 再起動で解決（[[table_duo_study_status]]）。
2. **`TableProps` レイヤー未確保**でピースが手・アバターと衝突する警告 → `EnsureTablePropsLayer()` が TagManager の 8 番以降の空きへ冪等追記。空きスロット枯渇時は Warning のみで進む（衝突分離が効かなくなるので要注意）。
3. **薄板の物理トンネリング**（§4 の⚠）。
4. **密集初期配置で物理オブジェクトが弾ける**: チップ数珠は「くっつくギリギリ」だが実接触にはせず +3mm マージン（ロード時に押し合って弾け飛ぶのを防ぐ）。サーペンタイン配置は弧長等間隔ではなく**直前チップからのユークリッド距離**でサンプリングする（ターンで直線距離が縮んで重なるのを防ぐ）。密集配置を足すとき共通の罠。
5. **非フォーカス Editor のコンパイル遅延 / MCP ブリッジ wedge / prefab 未反映**は [.claude/rules/mcp-unity.md](../rules/mcp-unity.md) と [[table_duo_study_status]] に既出（DLL mtime 確認・batchmode 検証・冪等 prefab 補完）。
