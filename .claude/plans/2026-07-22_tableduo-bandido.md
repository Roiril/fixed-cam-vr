---
status: implemented
created: 2026-07-22
updated: 2026-07-22
slug: tableduo-bandido
---

# TableDuo: バンディド追加（トンネル札 32 枚・格子スナップ・GameSwitcher 4 ゲーム目）

model-lab のバンディド GLB（開始札 `bandy.glb` / トンネル札 `g1..g24.glb` / 特殊札 `l1..l7.glb` = 32 枚）を
`Game_bandido`（GameSwitcher index 3, id `bandido`）として卓上へベイク。協力トンネルパズルなので
「1×2 縦長カードを格子に並べて道を繋ぐ」体験を、既存のグリッドゲーム作法（ガイスター）を 1×2 カードへ拡張して実装する。

## GLB 直パース実測（一次証拠）

`bandy.glb` / `g1.glb` / `l1.glb` の accessor min/max（3 枚とも同一）:
- size(X,Y,Z) = **0.044 / 0.0015 / 0.088** m → 短辺 44mm=**X**・厚 1.5mm=**Y**・長辺 88mm=**Z**（1:2 縦長・XZ 寝かせ Y-up）
- min=(-0.022, 0, -0.044) / max=(0.022, 0.0015, 0.044) → X/Z は原点中心・Y は底面 0（接地ロジックがそのまま成立）
- タスク前提（短辺=X・長辺=Z）と一致。定数の軸割当は実測どおり。

## 設計（確定事項）

- **全カード physics:false**（Rigidbody なし = kinematic 追従 + サイドカー）。ガイスターで確立した
  「グリッドゲーム駒 = 物理なし + スナップサイドカー」パターン。Rigidbody が無いので山札の沈み込み対策
  （restKinematic）も不要 = リリース後は物理が一切介入せず、スナップした姿勢がそのまま静止。
- **スケール 1.2 倍**（ピンチ精度と卓面積の折衷。algo は 1.3）。スケール後 短辺 W≈52.8mm / 長辺 H≈105.6mm / 厚 1.8mm。
  **格子ピッチ p = W**（1:2 なので長辺の 1/2）。
- **格子スナップの数理**（[BandidoSnapLogic](../../Assets/TableDuo/Scripts/Net/BandidoSnapLogic.cs)・純ロジック・EditMode 6 本）:
  bandy 中心 = 格子原点。u = pos - origin。
  - 縦置き（yaw 0/180・長辺 Z）: ux/uz → 最寄り k·p（整数格子）
  - 横置き（yaw 90/270・長辺 X）: ux/uz → 最寄り (k+0.5)·p（半セル格子）
  - この規則で 1×2 カードは縦横どちらでもセル境界（縦置きカード右端 = origin+p/2 等）が一致し、道が繋がる。
  - yaw は水平 yaw を最寄り 90° へ丸め（`GeisterSnapLogic.ExtractYawDeg` 再利用）、[0,360) 正規化。
  - 表裏保持: `dot(rot*up, up)>=0` で表/裏を判定し、傾いたまま置かず平置き化（裏は長軸まわり 180° ロール）。
- **Y = 積み上げ対応**（[BandidoCardSnap](../../Assets/TableDuo/Scripts/Net/BandidoCardSnap.cs)・GeisterPieceSnap 同型）:
  リリース時、自分の XZ フットプリント（yaw スナップ済み AABB）と重なる他カード（非保持）の bounds 上面最大値へ +0.4mm 接地。
  無ければ天板 +0.4mm。山札へ戻す・重ね置きの z-fight を回避。占有状態は都度 bounds から導出＝状態レス。
- **スタック最上段優先ピンチ**（[StackTopPickLogic](../../Assets/TableDuo/Scripts/Net/StackTopPickLogic.cs)・純ロジック・EditMode 6 本）:
  `PinchGrabInteractor.FindNearestFree` を改修。半径内最寄り候補と XZ 15mm 以内の候補群のうち Y 最大へ差し替え。
  バンディド山札・アルゴ山札（真上積み）で「一番上を取る」が両方成立。**ガイスター駒 65mm / 海底探検チップ 33mm 間隔は
  15mm 許容外なので巻き込まれず挙動不変**（共通改修だが既存ゲームに副作用なし）。
- **初期配置**: 中央に開始札 bandy（表向き・格子原点）/ 各席の手前（±0.24m）に手札 3 枚（表向き平置き・yaw seat 別）/
  +X 端（cx+hx-0.07）に山札 25 枚（裏向き・deckLift 2.2mm 積み）。合計 1+6+25 = 32 枚。
  カード→スロット割当は固定シード（20260722）Fisher-Yates（冪等ベイク・bandy は混ぜない）。
- **配り直し**（[BandidoDealer](../../Assets/TableDuo/Scripts/Net/BandidoDealer.cs)・AlgoDealer 同型）:
  開始札 `BANDIDO_bandy` をスロット捕捉から除外し、残り 31 枚を手札 6 + 山札 25 スロットへ完全ランダム permute。
  ホスト UI「バンディド配り直し」/ `mark?label=bandido_deal`。physics:false なので velocity ゼロ化は防御的 no-op。

## 変更・新規ファイル

新規:
- `Assets/TableDuo/ThirdParty/Bandido/glb/*.glb`（32 枚コピー）
- `Assets/TableDuo/Scripts/Net/BandidoSnapLogic.cs`（格子スナップ純ロジック）
- `Assets/TableDuo/Scripts/Net/BandidoCardSnap.cs`（サーバ側スナップサイドカー）
- `Assets/TableDuo/Scripts/Net/StackTopPickLogic.cs`（スタック最上段優先の純ロジック・共通）
- `Assets/TableDuo/Scripts/Net/BandidoDealer.cs`（配り直し）
- `Assets/Tests/TableDuo/BandidoSnapLogicTests.cs` / `StackTopPickLogicTests.cs`

改修:
- `Assets/TableDuo/Scripts/Editor/TableDuoSceneSetup.cs`（PlaceBandido + Game_bandido + GameSwitcher 4 配列 + BandidoDealer 配線）
- `Assets/TableDuo/Scripts/Net/PinchGrabInteractor.cs`（FindNearestFree にスタック最上段優先）
- `Assets/TableDuo/Scripts/Net/FacilitatorPanel.cs`（「バンディド配り直し」ボタン）
- `Assets/TableDuo/Scripts/Net/FacilitatorMarkServer.cs`（`bandido_deal` ラベル）
- `Assets/TableDuo/Scripts/Editor/TableDuoTablePreview.cs`（`Preview Table (Bandido)`）

## 検証状態

- [ ] コンパイル・EditMode（新規 12 本）・Setup 再生成・TablePreview 多角度 — **親（シュビー本体）が実行**
- [ ] **Quest 実機未検証**（パラメータは全て SerializeField / 定数・現場調整前提）:
  格子スナップの体感・カードスケール 1.2・山札位置（+X 端）・表裏保持の見え方・スタック最上段ピンチの掴み心地
  → [docs/table-duo/remaining-tasks.md](../../docs/table-duo/remaining-tasks.md) §C
