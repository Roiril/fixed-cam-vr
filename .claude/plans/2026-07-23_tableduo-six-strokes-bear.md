# TableDuo: あと6画のくま追加（置くだけ実装）

- **status: implemented**（2026-07-23・実機未検証）
- 依頼: 「model-lab にある『あと6画のくま』をひとつ追加。とりあえずコンポーネントを机の上に置く感じで」
- 出自: model-lab `models/six-strokes-bear/`（2人協力の制約付きお絵描きゲーム。まる役=〇×2+だ円×1 / 線役=直線×2+1折×1、合計最大6画でクマの気持ちを描く。ルール詳細は model-lab 側 README）

## 実装内容

- GLB 25 個を `Assets/TableDuo/ThirdParty/SixStrokesBear/glb/` へコピー（model-lab `exports/six-strokes-bear/glb/` 2026-07-23 ビルド版。sheet_bear / sheet_sleepy_demo はパッドと重複するため除外）
- [TableDuoSceneSetup.cs](../../Assets/TableDuo/Scripts/Editor/TableDuoSceneSetup.cs) に `PlaceSixStrokesBear` 新設 + `Game_bear` ルート（`BakeStowedState`）+ `WireGameSwitcher` 5 ゲーム目（id `bear` / 表示名「あと6画のくま」）
- [TableDuoTablePreview.cs](../../Assets/TableDuo/Scripts/Editor/TableDuoTablePreview.cs) に `Preview Table (SixStrokesBear)`（priority 217）
- レシピは [table_duo_tabletop_prop_authoring](../memory/table_duo_tabletop_prop_authoring.md) 準拠。新規ランタイムコンポーネント・スナップ・ルール裁定コードなし（「置くだけ」スコープ）

## 配置（座標定数は PlaceSixStrokesBear 冒頭に集約）

- 卓中央: 描画パッド A4（grabbable:false 素置き）／+X 0.28: お題山札 12 枚（faceDown・physics:false・top=01_sleepy）
- seat0（-Z・まる役）手前列 z=-0.26: 役割カード / 〇トークン×2 + だ円×1（1.6 倍・物理）/ 赤ペン（1.3 倍・kinematic・yaw90）
- seat1（+Z・線役）手前列 z=+0.26: x ミラー + yaw180 で対称（直線×2 + 1折×1 / 緑ペン）
- -X 側: 砂時計（物理）/ 達成条件カード / 達成トークン×3（1.6 倍・物理）/ ルールカード（yaw90）

## 検証済み / 残

- ✅ コンパイルエラー 0 / EditMode 519/519 / Setup 再実行 / Preview 4 アングル目視（配置・向き・伏せ札・自立砂時計 OK）
- ⏳ 実機: [remaining-tasks.md](../../docs/table-duo/remaining-tasks.md) C 節「あと6画のくま（07-23）」参照
- 今回スコープ外: VR 内で実際に「描く」手段（パッドは台紙のみ）。必要になったら別計画で
