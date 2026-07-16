# ゾーン校正の再設計 — 「形状は PC、位置合わせは HMD 10 秒」

status: proposed（設計のみ・未実装）
date: 2026-07-16

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
