---
status: implemented
created: 2026-07-20
updated: 2026-07-20
slug: tableduo-geister
---

# TableDuo: ガイスター追加（盤 + スナップ駒・GameSwitcher 3 ゲーム目）

model-lab のガイスター GLB（盤 `geister.glb` / 駒 `ghost_blue.glb` `ghost_red.glb`）を
`Game_geister`（GameSwitcher index 2, id `geister`）として卓上へベイク。
ユーザー要求: 盤は物理・把持なし / 駒は操作性優先のスナップ（盤上=相手向き正面 + 接地、
盤外=転倒防止のみで裏面確認可） / 初期配置は geister-set の 赤8青8 を **赤4青4 へ縮約**。

## 設計（確定事項）

- **盤**: `PlaceModelRealScale(grabbable:false)` の素置き（コンポーネントゼロ・実寸 390mm・卓中央）。
  マス目 6×6 はテクスチャ表現のみ＝グリッド座標はコードの定数（cellPitch = 0.390/6 = 65mm）
- **駒 = 物理なし（Rigidbody なし）+ [GeisterPieceSnap](../../Assets/TableDuo/Scripts/Net/GeisterPieceSnap.cs)**:
  リリース後に物理が介入しない（`Grabbable.ReleaseToPhysics` が rb null で即 return）ので
  「離した姿勢のまま静止」＝転がり防止が構造的に成立。スナップは DiceRoller 同型の
  サーバ側 IsHeld 遷移ポーリングで、純ロジック
  [GeisterSnapLogic](../../Assets/TableDuo/Scripts/Net/GeisterSnapLogic.cs)（EditMode テスト 13 本）:
  - **盤上リリース**: 最寄りの**空き**セル中心へ XZ 吸着（占有は他駒の現姿勢から都度導出＝状態レス、
    36 セル ulong bitmask）+ yaw = `onBoardYawDeg`（相手方向へ正面固定＝裏の色マーカー秘匿）+ 盤上面接地
  - **盤外リリース**: XZ・yaw 維持で直立化のみ（転倒防止）+ 天板接地。捕獲駒の裏面確認・公開はここで行う
  - 保持中はスナップなし（手の中では自由に回して裏面を見られる）
  - Y は直立化後の Renderer bounds 最下点を接地面に合わせる（Grabbable.CurrentBottomOffset と同法）
- **初期配置**: 中央 4 列 × 自陣端の行（±2.5 セル）に各席 4 体。**色は席ごと 2+2 の交互**
  （geister-set は「手前全青・奥全赤」の展示配色だが、色を混ぜないと裏マーカー秘匿が無意味になるため
  ゲームの本質に合わせて変更。実プレイは各自が伏せて並べ替える前提のデモ初期形）
- **正面校正（実測）**: Blender +Y 正面 → glTFast 取込後は **transform -Z 向き**。`ghostFrontYaw=180`
  で補正（blue/red 単体 GLB に向き差は無い。180° 焼き込みは model-lab geister-set 組立時のみ）
- スケール: 駒 1.6 倍（チップ類と同率・ピンチ精度基準）。盤は実寸
- `TableDuoTablePreview` に `Preview Table (Geister)` 追加（stow ベイクで不可視のゲームを
  撮影中だけ Renderer 入れ替えで表示する汎用 `CaptureWithActiveGame`）

## 検証状態

- [x] コンパイル エラー 0 / **EditMode 171/171 pass**（新規 GeisterSnapLogicTests 13 本含む）
- [x] Setup 再生成 + TablePreview 多角度（top/両席斜め）: 盤中央・駒 8 体のセル整列・接地、
  **両席とも「自分の駒の裏マーカーが自分にだけ見え、相手の駒は正面（目）だけ見える」を目視確認**
- [ ] **Quest 実機未検証**: スナップの体感・補間の見え方・ピンチ掴み心地・GameSwitcher 切替/
  reset_board の rb 無し駒復元 → [docs/table-duo/remaining-tasks.md](../../docs/table-duo/remaining-tasks.md) §C
