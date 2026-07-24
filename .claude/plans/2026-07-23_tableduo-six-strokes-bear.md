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

## 追補（同日 2 弾）: お題カード 3×4 表向きグリッド化

裏向き山札を廃止し、難度別 3 行（黄=1/緑=2/紫=3）×4 列・全部表向きの格子へ（格子中心 +X 0.33 / 列 0.09 / 行 0.11）。

## 追補（同日 3 弾）: ペン傾き + パッド描画 + 消しゴム

opus 偵察 2 体（Grabbable 保持パイプライン / GLB 軸・パッド形状）→ 仕様確定 → opus 実装 1 体の委譲で実装。

- **① ペン保持中の自然な俯き**: [MarkerHoldTilt] + 純ロジック [MarkerTiltLogic]。
  手ローカル固定ではなく**ワールドピッチ制約**: heading は手追従・pitch = min(45°, asin(保持高/先端距離 0.0923))
  → 机が邪魔なら寝る・持ち上げるほど俯く・先端は面を割らない。適用は `[DefaultExecutionOrder(120)]` の LateUpdate
  （サーバ権威 + 保持者本人のみローカル楽観）。リリース時は heading 維持で寝かせて接地（浮き防止）
  - **⚠ 2026-07-24 に [ToolGripDriver](../../Assets/TableDuo/Scripts/Net/ToolGripDriver.cs) + [PenGripLogic](../../Assets/TableDuo/Scripts/Net/PenGripLogic.cs) へ全面置換（supersede）**。
    「手の高さで俯角を決める帯（0〜65mm）＋回転のみ上書き」は持ちにくい／書きにくいとの指摘を受け、
    VR 一般解へ寄せた: 手首→ピンチ点線で前方を作り（手の傾き・ひねりに応答）、指先近くを軸に位置と回転を両方確定、
    俯角 +15°・下向き 80° クランプ・ピン先の面クランプ・One Euro / dir slerp 平滑・保持者ローカル即時インク。
    MarkerHoldTilt / MarkerTiltLogic / MarkerTiltLogicTests は削除。詳細 → [2026-07-24_tableduo-pen-grip-redesign.md](2026-07-24_tableduo-pen-grip-redesign.md)
- **② パッド描画**: [PadPaintCanvas](../../Assets/TableDuo/Scripts/Net/PadPaintCanvas.cs)（BEAR_paint・in-scene NetworkObject）+
  [PadPaintLogic](../../Assets/TableDuo/Scripts/Net/PadPaintLogic.cs) + [PadDrawTool](../../Assets/TableDuo/Scripts/Net/PadDrawTool.cs) +
  [TableDuoPadStamp.shader](../../Assets/TableDuo/Art/Shaders/TableDuoPadStamp.shader)。パッドの 0.3mm 上に透明オーバーレイ quad
  （自前メッシュ・UV は接触式と同一）+ RT 1024×1448。サーバがペン先↔パッドの接触（純計算・物理不使用）から
  セグメントを ClientRpc 配信、全 peer が CommandBuffer で円スタンプ焼き込み。遅参加リプレイ / GameSwitcher 切替・
  reset_board（BoardReset.AfterReset 新設 = 唯一のコア変更）でクリア / SessionLogger に draw_start/draw_end
- **③ 消しゴム**: BEAR_eraser（プリミティブ 2 個・白+deep_teal・physics:false）+ PadDrawTool(toolId2, radius 10mm)。
  シェーダ pass1（Blend Zero OneMinusSrcAlpha）でペン線だけ消す（クマ下絵は GLB 側なので消えない）
- **罠（恒久知見）**: ①オーバーレイ材のベイクは **_BaseColor alpha 0** で焼く（alpha 1 だと RT 未割当の Editor/Preview で
  白 quad がパッドを覆い隠す。ランタイム RT 割当時に alpha 1 へ戻す）②接触判定の境界比較は Mono の拡張精度で
  揺れるため ±1e-6 イプシロン（PadPaintLogic）

## 検証済み / 残

- ✅ コンパイルエラー 0 / EditMode 534/534（新規 MarkerTiltLogicTests 10 + PadPaintLogicTests 5）/ Setup 再実行 /
  Preview 目視（パッド下絵の透け・消しゴム配置・グリッド OK）
- ⏳ 実機: [remaining-tasks.md](../../docs/table-duo/remaining-tasks.md) C 節「あと6画のくま（07-23）」参照
  （傾きの体感 45°・描画追従/全 peer 同期・消しゴム・遅参加リプレイは実機でしか確認できない）
