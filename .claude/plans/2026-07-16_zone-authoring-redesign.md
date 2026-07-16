# ゾーン校正の再設計 — 「形状は PC、位置合わせは HMD 10 秒」

status: phase 1-3 実装済み・実機未検証（phase 4 = OVRSpatialAnchor 永続化は後回し）
　　　　→ **v2: PC 編集モデルを cuts（ループ切れ目）から「タイルペイント」へ変更**（2026-07-16 ユーザー指示、下記 v2 節）
date: 2026-07-16

## v2: タイルペイント・モデル（cuts を置換）

ユーザー指示：「道（ループ）じゃなく領域で決めたい。小さいタイルを 3 色に塗りつぶして、色 = カメラ担当区間」。

- フロアを正方タイル（既定 0.15m → 12×12）に分割し、各タイルへカメラ index を塗る。`'.'` = 未割当（壁・使わない領域）。
- show.json `layout.grid`：
  ```json
  "grid": {
    "tileM": 0.15, "cols": 12, "rows": 12,
    "cells": ["............", "222222222211", ...]
  }
  ```
  rows[0] = 北端（z=+0.9 側）、col 0 = 西端（x=-0.9）。cell(r,c) 中心 = `x = -w/2 + (c+0.5)·tileM`, `z = +d/2 - (r+0.5)·tileM`。文字 `'0'..'8'` = カメラ index。
- Unity 側展開：カメラ毎のタイル集合 → 貪欲矩形分解（行マージ）→ 各矩形を `overlapM/2` ずつ全方向に拡張（隣接カメラ境界で計 `overlapM` の重なり → 既存 Pick + shrink ヒステリシスがそのまま効く）→ PlayerZone 生成。Tracker 無改修は維持。
- `cuts` は後方互換で残す（grid があれば grid 優先。端末キャッシュに古い cuts だけが残っていても動く）。
- Web 卓はドラッグペイント UI（色パレット = カメラ + 消しゴム）。grid 未定義の show.json は既存 cuts から初期塗りを生成。シミュレーション・ライブドットは grid 判定に切替。

### v2 Unity 側実装状況（2026-07-16・実機/Editor 未検証・コンパイルはユーザー確認待ち）

- [`ZoneLayoutSolver`](../../Assets/Scripts/Tracking/ZoneLayoutSolver.cs) に grid 展開を追加：
  - `GridLayoutInput`（row-major int[] cells、-1=未割当 / 0..8=カメラ）+ `SolveGrid()` = **貪欲矩形分解**（左上→右下走査で未消費・同一カメラを右へ行方向マージ→列幅を保ち下へ行間マージ→消費、決定的）。各矩形を `overlapM/2` 全方向拡張。ラベルはカメラ別連番 `cam0#0`。priority 全 0（既存タイブレーク維持）。
  - `ParseGridCells(string[], rows, cols)` = show.json cells の char→int 変換。行数≠rows / 行長≠cols / 未知文字は**警告ログ + 未割当扱い**で例外を投げない。
  - `ChooseSource(hasGrid, hasCuts)` = grid 優先の選択ロジック（純関数・テスト用）。
- [`ShowControlClient`](../../Assets/Scripts/Streaming/ShowControlClient.cs)：`ShowGridDef`（tileM/cols/rows/cells）を `ShowLayoutDef.grid` に追加。`ShowLayoutDef.HasData()` = grid か cuts のどちらかがあれば present。long-poll 適用・端末キャッシュ往復の present 判定を `HasData()` に統一（grid-only layout も認識・キャッシュ復元）。
- [`ZoneLayoutApplier`](../../Assets/Scripts/Tracking/ZoneLayoutApplier.cs)：`TryResolveRects` で **grid 優先 → cuts → 内蔵既定** の順に解決。grid 寸法が floor と不一致なら警告して NW 角アンカーで展開。
- テスト [`ZoneLayoutSolverTests`](../../Assets/Tests/Tracking/ZoneLayoutSolverTests.cs)：cuts テストは維持。grid テスト追加（全塗り 1 矩形 / L 字 2 矩形カバー / 隣接カメラ overlapM 重なり / '.' 中心が無ゾーン / 不正 cells 非例外 / ParseGridCells 耐性 / ChooseSource grid 優先 / ラベル決定性）。
- **未着手（別作業）**：Web 卓のタイルペイント UI（Phase 2）。Unity 側は grid が来れば展開する状態。

## 背景 / 問題

- 現行の [ZoneCalibrator](../../Assets/Scripts/Tracking/ZoneCalibrator.cs) は、両グリップ長押し → レイでゾーンを選択 → トリガでドラッグ → スティックでサイズ・回転、という**ゾーンの形・位置・割当・向きの全パラメータを HMD 内スティック操作**で調整させる。体験が悪い・時間がかかる・再現性が低い。
- 基準が「起動時の立ち位置 recenter」なので、起動場所が数十 cm ズレるとレイアウト全体がズレる。
- 物理レイアウトが**確定した**：フロア 1.8m×1.8m、L 字壁（腕 1m×1m）を中心設置、フロア端〜壁 = 40cm 回廊。→ ゾーン形状はもう現地で発見するものではなく、**事前に決まっているデータ**。

## 根本思想

校正問題を 2 つに分解する。従来はこれを混ぜて全部 HMD でやらせていたのが体験の悪さの正体。

1. **形状・割当（何を切り替えるか）** — 物理レイアウトが固定なので**純データ**。PC（Web オペレータ卓）で事前に編集し、show.json で配る。HMD では一切編集しない。
2. **位置合わせ（そのレイアウトがトラッキング空間のどこにあるか）** — 剛体変換 3 DOF（XZ 平行移動 + yaw）だけ。HMD で必要な入力は**基準点 2 点のタッチのみ**（約 10 秒）。

## レイアウトのデータモデル（course space）

- 座標系「course space」：原点 = フロア中心、+Z = 壁の北腕方向。単位 m。
- show.json に `layout` セクションを追加：

```json
"layout": {
  "floor": { "w": 1.8, "d": 1.8 },
  "wall":  { "type": "L", "armX": 1.0, "armZ": 1.0 },   // 描画・登録基準点の導出用（固定値）
  "cuts":  [ { "s": 0.05, "camBefore": 2, "camAfter": 0 },
             { "s": 0.30, "camBefore": 0, "camAfter": 1 },
             { "s": 0.55, "camBefore": 1, "camAfter": 2 } ],
  "overlapM": 0.08,
  "hysteresisM": 0.12
}
```

- **カット・モデル**：プレイヤーが歩ける領域は「壁を囲む閉ループの回廊」。ループを周長パラメータ s ∈ [0,1) で表し、**カメラ区間 = ループ上の切れ目（スカラー s）で区切られた区間**。オペレータが編集するのは切れ目の位置だけ。
  - ボックスを個別にドラッグ・リサイズする操作は**存在しなくなる**。
  - 切れ目からゾーン（矩形 OBB 群）への展開は決定的なアルゴリズムで自動生成（回廊セグメント + `overlapM` のオーバーラップ + `hysteresisM`）。現行の A/B/C-north/C-west 4 分割はこの生成の一例に過ぎなくなる。
  - [PlayerZoneTracker](../../Assets/Scripts/Tracking/PlayerZoneTracker.cs) / [PlayerZone](../../Assets/Scripts/Tracking/PlayerZone.cs) は**無改修**（生成された OBB を SetRuntimeBounds/Rotation で流し込むだけ）。

## Web オペレータ卓（PC 側）

`tools/web-compositor/` に「フロアマップ」パネルを追加：

- 上から見た 2D マップ（1.8m 床・L 壁は既知寸法で固定描画）。カメラ区間を色帯で表示し、**切れ目マーカーをループに沿ってドラッグ**して調整。カメラアイコン（Phone01〜03）の想定位置もメモとして置ける。
- 保存 → show.json `layout` 更新 → 既存の long-poll で Quest に届き、次のポーリングで実機のゾーンが即差し替わる（ライブ調整）。
- **ライブモニタ**：Unity heartbeat（既存 2s）に HMD 位置（course space に逆変換した XZ）と現在ゾーンを追加。マップ上に「プレイヤードット + 今どのカメラか」がリアルタイム表示され、**切替が起きる場所を PC で見ながら切れ目を動かせる**。
- おまけ：マップ上でドットをマウスドラッグする「シミュレーションモード」。Pick と同じ純ロジックを JS 側にも持たせ、HMD なしで切替挙動を事前確認できる。

## HMD 側（登録＝registration だけに縮小）

### 登録リチュアル（10 秒）

1. 両グリップ長押し（既存の入り口を流用）→「登録モード」
2. 視界に指示：「**壁の外角（L の凸角）にコントローラ先端を当てて A**」
3. 「**北腕の先端に当てて A**」
4. 2 点から平行移動 + yaw を解いて完了。即座に**壁と床端のワイヤーフレームをゴースト表示** → 実物の壁に重なっていれば OK、ズレていればやり直し（A で再登録 / B で確定）。

- 基準点は course space で既知（壁寸法が固定だから）。2 点間 1m ベースラインなら 2cm のタッチ誤差 ≈ yaw 1.1° ≈ 回廊上で 2〜3cm。40cm 回廊 + 8cm オーバーラップに対して十分。
- 保存は `registration.json`（persistentDataPath）：`{ originXZ, yawDeg }` の 1 変換のみ。起動時に適用。ゾーン個別の保存ファイル（zone_calibration.json）は廃止。
- 現行の「起動位置 recenter」は削除（登録が無い初回だけのフォールバックに格下げ）。
- 微調整：登録モード中に左スティックで ±数 cm / ±数° のナッジは残す（ワイヤーフレームを実壁に重ねながら）。**ゾーン単位の選択・ドラッグ・リサイズ操作系は全廃**。

### 頑健化

- **Quest の recenter（Oculus ボタン長押し等）でトラッキング原点が変わると登録が無効になる**。`OVRManager.display.RecenteredPose` を購読し、発生したら「要再登録」を HUD に出す（黙ってズレたまま動かない）。
- Phase 2 で **OVRSpatialAnchor** に登録を紐付ける：ガーディアン・再起動をまたいで位置合わせが自動復元し、現地では初日 1 回の登録で済む（meta-xr.md では後フェーズ扱いだったが、これがまさにその用途）。

## データフロー

```
Web 卓（cuts 編集）→ show.json layout ─ long-poll →
ShowControlClient → ZoneLayoutApplier（新規）
  = cuts → 矩形 OBB 展開（course space）
  → registration 変換（course → tracking）
  → PlayerZone.SetRuntimeBounds/Rotation
PlayerZoneTracker（無改修）→ カメラ切替
heartbeat ← HMD 位置（course space）+ 現在ゾーン → Web マップのライブドット
```

- 展開ロジックは pure static class（`ZoneLayoutSolver`）にして EditMode テストを書く（切れ目→矩形の座標、オーバーラップ、L 角の 2 分割）。
- show.json 不在時は既存パターン通り端末キャッシュ（show_config.json に layout も同載）→ 焼き込みデフォルトの順でフォールバック。

## 実装フェーズ

| Phase | 内容 | 検証 |
|---|---|---|
| 1 | `ZoneLayoutSolver`（cuts→OBB 展開・純関数）+ show.json `layout` スキーマ + ShowControlClient 適用 | EditMode テスト + streaming-offline-test |
| 2 | Web 卓フロアマップエディタ（描画・切れ目ドラッグ・保存）+ heartbeat にプレイヤードット | ブラウザ単体（シミュレーションモード） |
| 3 | HMD 2 点登録 + ワイヤーフレーム検証表示 + registration.json + recenter 検知 | 実機（現地 L 壁） |
| 4（後回し可） | OVRSpatialAnchor 永続化・ナッジ微調整の磨き | 実機 |

## 廃止・整理

- `ZoneCalibrator` のゾーン選択・ドラッグ・リサイズ・レイアウト回転の操作系 → 全廃（登録モードに置換）。床フットプリント可視化・レイ表示のコードは登録モードの表示に転用できる。
- `zone_calibration.json` → `registration.json` に置換（形状はもう端末に保存しない）。
- `MainDemoSceneSetup` の推測配置ゾーン → デフォルト layout（cuts の初期値）として show.json 側に移す。
