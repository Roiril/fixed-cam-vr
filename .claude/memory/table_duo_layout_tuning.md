---
name: table_duo_layout_tuning
description: TableDuo の卓/椅子/席/駒の初期配置をビルド不要・数秒ループで微調整する手順（Editor 内 Setup 再生成→Remy 着座プレビュー）
metadata: 
  node_type: memory
  type: project
  originSessionId: 1da31df7-e8bb-4c10-a796-241b736694dd
---

# TableDuo レイアウト微調整フロー（ビルド不要ループ）

卓/椅子/席/駒の初期配置は [TableDuoSceneSetup](../../Assets/TableDuo/Scripts/Editor/TableDuoSceneSetup.cs)
（`Tools/FixedCamVr/Setup/Setup TableDuo Scene`）が**コード生成**する。しかも机の高さ 1 個から
天板コライダー・各駒の卓上拘束（SetSurfaceClamp）・駒の Y・ランプ位置…と派生値が連鎖する。
→ **Editor で手で動かしても Setup 再実行で消える**。根っこの値をコードで変え、Setup に派生を導出させるのが正。

## ループ（1 周 数秒・ビルドも Quest も不要）

1. `TableDuoSceneSetup.cs` の該当値を編集（机高・席・椅子距離・駒グリッド原点/間隔/scale 等）
2. MCP `execute_menu_item "Tools/FixedCamVr/Setup/Setup TableDuo Scene"`（冪等再生成）
3. MCP `execute_menu_item "Tools/FixedCamVr/Diagnostics/Preview Table + Remy seated"`
   → `Assets/Screenshots/tableduo/table/*.png`（7枚）。**両席に座位 Remy を一時生成**して寸法感を出す
   （[TableDuoTablePreview.SeatRemy](../../Assets/TableDuo/Scripts/Editor/TableDuoTablePreview.cs)、撮影後に破棄・ランタイム非汚染）
4. `Read` で PNG を見る。特に `remy_side.png`（体高 vs 天板高）・`remy_front_full.png`。ユーザーと詰める
5. `execute_menu_item` は必ず timeout を返すが**実行はされる**（fire-and-forget）。PNG mtime で完了確認

`Preview Table (screenshot)`（Remy 無し・4枚）は駒配置だけ見たい時。

## 主なパラメータの在り処（TableDuoSceneSetup.cs）

| 対象 | 場所（2026-07-08 時点） |
|---|---|
| 机の高さ/大きさ | `table.transform.localPosition=(0,0.35,0)` / `localScale=(1.2,0.7,0.8)`（フォールバック生成時）。実体は InstantiateModelFitHeight の `targetHeight:0.7f` |
| 椅子 | Chair0/1 の `new Vector3(0,0,±0.78)` + `targetHeight:0.85f` |
| 席（目線アンカー） | `eyeHeight=1.15f` / `CreateSeat(... ±0.85 ...)`（席0=人 -Z / 席1=手 +Z） |
| リグ初期位置 | `rig.SetPositionAndRotation((0,0,-0.85),...)`（席0 と揃える） |
| 駒グリッド | PlaceDeepSeaAdventure: `chipScale=1.6` / `cols=6` / `step=0.085` / `gz0`(手前縁) / 駒・サイコロの Vagra |
| 卓上拘束 | SetSurfaceClamp が topY/中心/半サイズを各 Grabbable に焼く（机を動かすと自動追従） |

## 注意

- 席を動かしたらリグ初期位置（rig.SetPositionAndRotation）も揃える。ズレると起動時の頭位置が席とずれる
- 机高を変えると driven 値（天板コライダー Y・SetSurfaceClamp の topY・駒 Y）は Setup が再導出するので
  **コードの table 定義側だけ触れば全部追従**する（手で個別に直さない）
- Setup 再実行は [Tracker] 等を作り直す（廻リ視側の注意。TableDuo では該当薄いが冪等前提）
- 「座った体で感じる正解」（机高の体感・椅子の詰め・リーチ）だけは最後に**1 ビルドで実機確認**。
  見た目の 9 割（相対配置・スケール・複製・グリッド）はこの Editor ループで確定できる
- MCP wedge に注意（[[table_duo_pc_host_and_wiretap]] / mcp-unity.md）。連続多用でデータ返却系が
  timeout し出したら Unity 再起動。本ループは menu-exec + PNG Read が主で比較的軽い

関連: [[table_duo_wrist_anchor_basis]]（Remy 駆動）/ visual-verification.md（多角度・単体隔離の原則）
