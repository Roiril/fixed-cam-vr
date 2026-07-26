# 演出の素材と層を増やす — 録画 / スロット / CG レイヤ / 演出専用カメラ

status: design-fixed（2026-07-26）。[2026-07-25_shot-timeline-foundation.md](2026-07-25_shot-timeline-foundation.md) の **段 D** の中身を確定する文書。

## ユーザー要求（原文）

> 1.１週目の映像を録画しておく 2.3周目の演出としてその映像を流す、3.体験者が人形になるような動画を生成して演出に差し込む 3.録画映像の上に、cgの人形を設置してcgがメタクエストのハンドトラッキングで腕を動かせたり、体験者の位置に合わせて動く 4.演出として、カメラDのリアルタイム映像を流す
> これらを追加したい、具体実装の前に、それぞれの機能の基盤を設計から実装までお願い。

## 1. 要求を 3 つの軸へ分解する

5 つの要求は「カットの種類を増やす」ように見えるが、**構造的に別の 3 つの軸**が混ざっている。混ぜたまま実装すると、また `cue` / `insert` のときと同じ「同じことをする概念が 2 つある」状態に戻る。

| 軸 | 何が新しいか | 該当する要求 |
|---|---|---|
| **軸1: 映すもの（source）が増える** | `rec`（端末内録画）が加わる。`live` は カメラ D で範囲が広がるだけ | 1・2・5 |
| **軸2: 素材の実体がランごとに変わる** | 今の `assetUrl` は show.json に固定で書く文字列。「1周目の録画」「いま生成した動画」は**保存時には存在しない** | 1・2・3 |
| **軸3: 画面に重ねる層が増える** | 今のシェーダは live × overlay の 2 層（静的マスク合成）。実時間で動く 3D は表現できない | 4 |

軸3 だけが本質的に新しい機構を要る。軸1 は既存スキーマの素直な拡張、軸2 は**遅延束縛**という 1 つの概念で全部片づく。

### 不変条件（[段 B/C](2026-07-25_shot-timeline-foundation.md) から継承。この文書で追加しない）

新しい source も新しい層も、**画面の所有者は常にちょうど 1 人**・**演出の所有は必ず有界**・**返しは再計算**の 3 原則の下に置く。録画も CG も `TakeRunner` の外に独立した「画面を持つ主体」を作らない。

## 2. 設計 — 4 本の基盤

### F1. 素材スロット（run-scoped asset slot）— 軸2

**問題**: 演出の作者は「3周目のBで**いま生成した人形動画**を流す」と書きたい。しかしその動画は、演出を保存する時点では存在しない。

**却下した案**: 卓から `assetUrl` を書き換えて show.json を保存し直す。
現行の `TakeRunner.SetTakes` は走行中の演出を畳み `once` の発火済み状態を作り直す（不変条件 8 の実装）。**ラン中に保存すると発火済みの演出が再武装され二度出る**。素材を差し替えたいだけの操作でタイムラインを触るのは危険で、しかも操作として重い。

**採用**: `assetUrl` に `slot://<name>` を書けるようにし、**スロット名 → 実 URL の束縛表を timeline とは独立に持つ**。

```jsonc
// show.json control（ラン状態の置き場。timeline ではない）
"control": {
  "activeCue": null, "cameraOverride": null, "autoFollow": true, "runEpoch": 3,
  "slots": [ { "name": "doll", "url": "/captures/doll_20260726_143000.mp4" } ]
}
```

- 卓の素材工房で生成物を選び **「スロットに束縛」** を押すと `slots[]` だけが更新される（`rev++`）。timeline は無傷 → **再武装が起きない**
- Unity は `ShowAssetResolver` の前段でスロットを解決する（`slot://doll` → `/captures/...` → 既存の `sa://` / 相対解決へ）
- **未束縛のスロットを指すカットは飛ばす**（§6.4「`assetUrl` 空 → その step を飛ばす」と同じ扱い）。全 step が飛んだ take は発火しない。**入口の写真を撮り忘れても体験は壊れない**のがこの設計の要点
- スロット名は show.json 側で宣言しない（自由文字列）。卓 UI は timeline 内で使われている `slot://` を集めて一覧にする

### F2. 端末内セグメント録画（on-device segment recording）— 要求 1・2

**録る**: `CameraStream.Tick` は既に生 JPEG バイト列を持っている（`LoadImage` に渡す直前）。ここを分岐させ、録画中なら**バイト列を複製してバックグラウンドスレッドのキューへ積む**だけにする。メインスレッドの追加コストは 60KB の memcpy × 15fps ≒ 0.9MB/s。

コンテナは自作の極小形式（`.mjr`）。ヘッダ 16B + `[u32 len][u32 ptsMs][jpeg…]` の連結。

- **なぜ mp4 にしないか**: Quest 上の H.264 エンコードは `MediaCodec` を JNI で叩く実装になり、コストとリスクが実装量に見合わない。JPEG のまま保存すれば**再エンコード劣化ゼロ**で、再生は既に実績のある `Texture2D.LoadImage` をそのまま使える（＝ライブ映像と完全に同じ絵になる）
- **なぜ卓（PC）録画にしないか**: 卓は既に全カメラを録れる。しかし体験中に mp4 を Quest へ配る経路（DL + `VideoPlayer.Prepare`）が critical path に入る。Android の `VideoPlayer` が HTTP ストリーミングを扱えず一旦ローカルへ落とす制約（[`ScreenOverlayController`](../../Assets/Scripts/Streaming/ScreenOverlayController.cs) の実装コメント）も既知。**ネットワークを本番の演出経路から外す**方が堅い。卓側録画は記録・素材づくり用として現状のまま残す

**単位は区間**。`TimelineDirector.OnCameraEntered(camera, lap)`（＝ショーの時計）で開始・切替する。画面が何を映しているかとは無関係に、**体験者がどこに居るか**で録る（演出で画面を横取りしている間も、その区間のカメラのライブを録り続ける）。

```jsonc
// show.json トップレベル
"record": { "enabled": true, "laps": [1], "maxSegmentSec": 60, "maxTotalMB": 200, "fpsCap": 15 }
```

- 保存先 `Application.temporaryCachePath/rec/<runEpoch>/L<lap>C<cam>.mjr`。**ラン開始（runEpoch 変化）で前ランを削除**（体験者の映像を端末に残さない＝運用上も重要）
- 上限超過は**古いフレームから捨てる**（リング）。録画が体験を止めることは絶対にさせない
- 容量実測見込み: 15fps × 20s × 60KB ≒ 18MB/区間。3 区間で 54MB

**流す**: 新しい source `rec`。

```jsonc
{ "source": "rec", "camera": 1, "recLap": 1, "durKind": "untilClipEnd", "transition": "cut" }
```

再生は `RecordedClipPlayer`（`.mjr` を pts どおりに `Texture2D` へ流す）。`ScreenOverlayController` に**フレーム列ソース**という 4 つ目の種別を足して受ける（既存の VideoClip / URL 動画 / 静止画と並ぶ）。`untilClipEnd` は既存の「overlay が消えたら次のカット」経路にそのまま乗る。

録画が無い（1周目を録っていない・ラン再開直後）ときは **その step を飛ばす + 警告**。

### F3. CG 合成レイヤ（virtual camera twin）— 要求 4

**問題**: 「録画映像の上に CG 人形を置く」は、今の 2 層シェーダ（live × 静的マスク）では書けない。しかも人形は**その固定カメラから見た正しいパース**で立っていないと、貼り付けた絵にしか見えない。

**採用**: 実カメラの**双子の仮想カメラ**を VR 空間に立て、CG レイヤだけを描いて 3 層目として合成する。

```
course 空間（フロアマップで著作・CourseFrame で実空間に登録済み）
   ├─ 体験者の HMD 位置        ← 既にゾーン判定に使っている
   ├─ 実カメラ A/B/C/D の姿勢  ← ★新規: 卓のフロアマップで著作する
   └─ 人形（ShowActor）        ← 体験者 XZ に追従 or 著作した固定位置

仮想カメラ（実カメラ姿勢・fov）→ RenderTexture(RGBA, CG レイヤのみ, clear=透明)
   → ScreenComposite の 3 層目 _CgTex
```

- **合成位置は post FX の前**。CG が映像と同じ露出・彩度・ヴィネット・走査線・グレインを浴びる。「映像の中に居る」ように見えるかはここで決まる（後段に足すと必ず浮く）
- 実カメラ姿勢は course 空間で著作する。course 空間は既に `CourseFrame` の 3DOF で実空間へ登録済みなので、**体験者の位置と同じ座標系**に自動的に乗る（新しい位置合わせ作業を増やさない）

```jsonc
// show.json cameras[i] に追加
{ "id": "B", "sourceId": "Phone02", …,
  "role": "zone",            // "zone"（既定・ゾーンに割り当てる）| "fx"（演出専用）
  "hasPose": true,
  "pose": { "x": 1.2, "z": -0.8, "y": 1.15, "yawDeg": 135, "pitchDeg": -8, "fovDeg": 62 } }
```

```jsonc
// show.json トップレベル: 人形の定義
"actors": [ { "id": "doll", "name": "人形", "prefab": "ShowDoll",
              "heightM": 1.6, "fixedX": 0, "fixedZ": 0, "fixedYawDeg": 0 } ]
```

```jsonc
// step に追加
{ "source": "rec", "camera": 1, "recLap": 1, "cg": "doll", "cgMode": "follow", … }
```

- `cgMode: "follow"` = 体験者の HMD XZ に人形が立つ（要求の「体験者の位置に合わせて動く」）／`"fixed"` = actor の著作位置
- **腕はハンドトラッキング**: `OVRHand` / `OVRSkeleton` の手首位置から肩→肘→手首の 2 ボーン IK。手が検出されていない間は idle ポーズへフォールバックし、**手が無いことで演出が止まらない**
- **スタッフのコントローラと同居できるか**: できる。`OVRManager.SimultaneousHandsAndControllersEnabled`（Meta XR SDK 201 に存在することを確認済み）を立てる。コントローラはスタッフ専用・右手 4 入力のまま（[controller_input_final](../memory/controller_input_final.md) の凍結は維持し、**入力割り当ては一切増やさない**）
- 姿勢が未著作（`hasPose:false`）のカメラで `cg` を指定した場合は **CG を出さずに警告**。当てずっぽうのパースで出す方が体験を壊す

**この機能で唯一「読み」が要るのは実カメラ姿勢の精度**。ズレると人形が床に埋まる / 宙に浮く。だから姿勢は数値で著作し、卓のフロアマップ上で見ながら合わせられるようにする（実装は D-4）。

### F4. 演出専用カメラ（カメラ D）— 要求 5

構造上は**新しい機構が要らない**。registry に 4 本目を足し、どのゾーンにも割り当てなければ「演出でしか映らないカメラ」になる（[段 0 の §3](2026-07-25_shot-timeline-foundation.md) で「カメラ ≠ ゾーン」として既に想定済み）。

ただし**既存の暗黙の前提が 3 つ壊れる**ので、そこを塞ぐのが実作業:

| 壊れるもの | 症状 | 対処 |
|---|---|---|
| スタッフの A ボタン巡回（`Next()`） | D まで巡回してしまい、ゾーンの無いカメラで止まる | 巡回対象を `role:"zone"` のカメラに限定する |
| ゾーン自動復帰 | 影響なし（`SetAmbient` は現在ゾーンから引くので D は選ばれない） | 確認のみ |
| 卓 UI・フロアマップ | D がゾーン塗りの選択肢に出る／凡例で区別が付かない | `role:"fx"` を別扱いで描く |

## 3. スキーマ差分（v3 のマイナー拡張・破壊なし）

すべて**追加のみ**。既存の show.json はそのまま読める（未指定＝既定で従来挙動）。

| 場所 | 追加 | 既定 | 意味 |
|---|---|---|---|
| `steps[].source` | `"rec"` を許可 | — | 端末内録画を映す |
| `steps[].recLap` | int | `0`（＝未指定→飛ばす） | 録画元の周 |
| `steps[].assetUrl` | `slot://<name>` を許可 | — | ラン中に束縛する素材 |
| `steps[].cg` | string | `""`（出さない） | 重ねる actor の id |
| `steps[].cgMode` | string | `"follow"` | `follow` / `fixed` |
| `cameras[].role` | string | `"zone"` | `zone` / `fx` |
| `cameras[].pose` + `hasPose` | object + bool | `hasPose:false` | CG レイヤの視点 |
| `control.slots[]` | `{name,url}[]` | `[]` | 素材スロットの束縛 |
| `record` | object | `enabled:false` | 端末内録画の設定 |
| `actors[]` | `{id,name,prefab,…}[]` | `[]` | CG actor の定義 |

**既存の規約に従う**: 未知の判別子は既定へ倒して警告（`cgMode`）／present-flag は `hasPose` のみ／`0` はコード既定／入れ子は最小限（JsonUtility の null 入れ子回避）。

## 4. 段取り

依存関係で並べる。**D-1 と D-3 は独立**なので順序は入れ替えてよい。

| 段 | 内容 | 依存 | リスク |
|---|---|---|---|
| **D-0** | スキーマ拡張を両側の型 + fixture で確定（C# `ShowStepDef`/`ShowTakeSchema`/show.json 型、JS `timeline-model.js`、fixture 往復テスト） | — | 低 |
| **D-1** | 素材スロット（束縛表 + 解決 + 卓 API/UI） | D-0 | 低 |
| **D-2** | 端末内録画 + `rec` 再生 | D-0 | 中（実機ストレージ・スレッド） |
| **D-3** | カメラ D（registry 4 本目 + 巡回の限定 + 卓 UI） | D-0 | 低 |
| **D-4** | CG レイヤ（シェーダ 3 層目 + 仮想カメラ + actor + ハンド IK + 姿勢著作 UI） | D-0 | 高 |

### 実装状況（2026-07-26 時点・EditMode 671/671 pass・**実機未検証**）

| 段 | 状態 | 実体 |
|---|---|---|
| D-0 スキーマ | ✅ 完了 | C# [`ShowTakeSchema`](../../Assets/Scripts/Streaming/ShowTakeSchema.cs)（`rec` / `recLap` / `cg` / `cgMode` / `slot://`）+ `ShowCameraPoseDef` / `ShowRecordDef` / `ShowActorDef` / `ShowSlotDef` / `CameraRoles`。JS [`timeline-model.js`](../../tools/web-compositor/timeline-model.js) と fixture 往復も更新済み |
| D-1 素材スロット | ✅ 完了 | `ShowControlClient.ResolveAssetUrl` が `slot://` を解決（未束縛は空文字 → カットを飛ばす）。卓は `POST /command {type:'bindSlot'}` で `control.slots` だけを更新（timeline 不変＝ `once` の再武装なし） |
| D-2 端末内録画 | ✅ 完了（実機未検証） | [`RecordedSegmentFormat`](../../Assets/Scripts/Streaming/Recording/RecordedSegmentFormat.cs) / [`SegmentRecordWriter`](../../Assets/Scripts/Streaming/Recording/SegmentRecordWriter.cs)（背景スレッド） / [`RecordedFramePlayer`](../../Assets/Scripts/Streaming/Recording/RecordedFramePlayer.cs) / [`SegmentRecorder`](../../Assets/Scripts/Streaming/Recording/SegmentRecorder.cs)。`CameraStream.FrameTap` で生 JPEG を分岐。オーバーレイに 3 種目「フレーム列」を追加 |
| D-3 カメラ D | ✅ **完了**（2026-07-27・実機未検証）。下表は当時の記録。実配線は [2026-07-27_cg-actor-hand-tracking.md](2026-07-27_cg-actor-hand-tracking.md) | `cameras[].role:"fx"` を導入し、スタッフ巡回（A ボタン）とゾーン自動切替から除外。**4 本目の `CameraSource`（Phone04.asset）と prefab `sources[]` への追加はしていない**（実機が増えたときに行う。手順は下記） |
| D-4 CG レイヤ | ✅ **完了**（2026-07-27・実機未検証）。腕のハンドトラッキング駆動・人形プレハブ生成・卓の姿勢/actors 著作まで [2026-07-27_cg-actor-hand-tracking.md](2026-07-27_cg-actor-hand-tracking.md) | シェーダ 3 層目 `_CgTex`（post FX の**前**で合成）+ [`ShowCgLayer`](../../Assets/Scripts/Streaming/Cg/ShowCgLayer.cs)（仮想カメラ・actor 配置・follow/fixed）+ レイヤ `ShowCg`(9) を追加。**ハンドトラッキングによる腕の駆動と人形プレハブは未実装**（プレハブ未用意なら代用のカプセルが立つ） |

**カメラ D を実機で足すときの手順**（コード変更は不要）:
1. `Assets/Settings/Cameras/Phone04.asset` を作り `cameraId` を `D` にする
2. `Assets/Prefabs/Logic/StreamingLogic.prefab` の `CameraStreamRegistry.sources` に 4 本目として追加
3. 卓の show.json `cameras[]` に 4 本目を足し `"role": "fx"` を入れる（フロアマップのグリッドには塗らない）
4. 演出のカットで `映すもの = ライブカメラ / カメラ D` を選ぶ

**D-4 の残り**: `OVRHand` / `OVRSkeleton` から肩→肘→手首の 2 ボーン IK で腕を駆動する。
スタッフのコントローラと同居させるため `OVRManager.SimultaneousHandsAndControllersEnabled` を立てる
（Meta XR SDK 201 に存在することは確認済み）。カメラ姿勢（`cameras[].pose`）の著作 UI も卓のフロアマップへ未実装。

**カット可能点は D-4**。D-0〜D-3 だけでも「1周目を録って3周目に流す」「生成動画を差し込む」「カメラ D を出す」は成立し、出展できる。D-4 が間に合わなければ人形は**焼き込み済みの動画**（[段 0 §7 の既定](2026-07-25_shot-timeline-foundation.md)）で代替する。

### 各段でテストに落として固定する不変条件

1. 録画は**体験を止めない**（ディスク満杯 / 書き込み失敗でもライブ表示と演出が継続する）
2. 録画が無い `rec` カットは飛ばす。飛ばした結果 step が全滅した take は発火しない
3. 未束縛の `slot://` カットは飛ばす（同上）
4. スロット束縛は timeline を変更しない（`once` の発火済み状態を保つ）
5. ラン開始で前ランの録画が消える
6. 姿勢未著作のカメラでは CG を出さない
7. ハンドトラッキング不在でも CG は idle ポーズで出続ける（演出は止まらない）
8. `role:"fx"` のカメラはスタッフ巡回に現れず、ゾーン自動切替でも選ばれない

## 5. 意図的に先送りするもの

- **人形だけをマスクで抜いて別レイヤ合成する**（実時間合成の品質が読めない）。CG は 3 層目として素直に重ねる
- **録画の音声**（そもそも MJPEG に音声が無い）
- **録画の周をまたぐ結合**（区間ごとの独立ファイルで足りる）
- **CG の影・接地判定**（人形は接地位置を著作値/HMD 高から決め打ちする）
- **複数 actor の同時表示**（1 カット 1 actor）

## 6. 参照

- 3 層モデルと v3 の契約: [2026-07-25_shot-timeline-foundation.md](2026-07-25_shot-timeline-foundation.md)
- 契約の正本: [.claude/rules/streaming.md](../rules/streaming.md)
- 卓の運用と素材工房: [.claude/memory/web_compositor.md](../memory/web_compositor.md)
- コントローラ入力の凍結: [.claude/memory/controller_input_final.md](../memory/controller_input_final.md)
