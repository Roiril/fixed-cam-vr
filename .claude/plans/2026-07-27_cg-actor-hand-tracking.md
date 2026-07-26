# CG 人形をハンドトラッキングで動かす（D-4 の残り）+ 演出専用カメラ D の実配線

status: implemented（2026-07-27・**Quest 実機未検証**）。
[2026-07-26_show-sources-and-cg-layer.md](2026-07-26_show-sources-and-cg-layer.md) の **D-3 実機構成**と **D-4 の残り**（腕の駆動・人形プレハブ・姿勢著作 UI）を確定して実装する文書。

## ユーザー要求（原文）

> ①周回とは関係なしに、演出としてライブのカメラDを追加する機能を追加して。もし必要であればストリーマーにも追加を。
> ②例えばmixamoとかから人形のモデルとリグを引っ張ってきて、演出の一つとして映像になじませながら、ハンドトラッキングで手を動かせるようにしたいんだけど、設計から考えて実装してほしい。

## ① カメラ D — 「機構は既にある」ので配線だけ

[前計画 F4](2026-07-26_show-sources-and-cg-layer.md) のとおり、**新しい機構は要らない**。
`role:"fx"` のカメラはスタッフ巡回とゾーン自動切替から外れる（実装済み）。残っていたのは実体の配線:

| 層 | やること |
|---|---|
| 配信アプリ | `StreamerPrefs.CAMERA_ID_CHOICES` に `"D"`（v0.8.0）。UI のボタン行は選択肢から自動生成なので追加はこの 1 箇所。5 個で狭い端末が溢れないよう最小幅を外した |
| Unity | `Assets/Settings/Cameras/Phone04.asset`（cameraId=D）+ `StreamingLogic.prefab` の `sources[]` 4 本目 |
| 卓 | show.json 既定 `cameras[]` に D（`role:"fx"`）。UI でカメラの役割を切替でき、フロアマップのゾーン塗りパレットには **fx を出さない**（塗れてしまうと「演出専用」が壊れる） |

**カメラ index = show.json cameras[] の並び = registry sources[] の並び** は従来どおり不変。D は index 3。

### 意図的にそうしていること

- **D も常時受信する**（演出のときだけ繋ぐ、はしない）。切替のたびに MJPEG を張り直すと 1〜2 秒の黒が出る。
  帯域は 640x480/q40 で 1 台 4〜8Mbps 程度、4 台で 5GHz なら収まる（3 台運用の実測から外挿）
- **`role:"fx"` は「ゾーンに割り当てない」だけ**。演出のカットからは `source:"live" camera:3` で普通に選べるし、
  録画（`rec`）の対象にもなる（録る単位は区間なので、ゾーンに出てこない D は自然と録画対象外になる）

## ② CG 人形 — 設計

### 何を作るか（体験としての定義）

**映像の中に、体験者の分身の人形が立っていて、体験者が手を動かすと人形の腕も動く。**
体験者は自分の姿を監視カメラ越しに見る。人形は体験者の位置・向き・手の位置を写す（`cgMode:"follow"`）か、
著作した位置に立って手だけを写す（`cgMode:"fixed"`）。

### 層と責務（既存の流儀に合わせる）

```
[OVR 入力]  OvrHandTrackingBridge (Assembly-CSharp / OvrBridge)
    OVRPlugin.GetHandState → 手首 world 位置 + 信頼度、HMD 頭 pose
    OVRManager.SimultaneousHandsAndControllersEnabled = true（スタッフのコントローラと同居）
        ↓ push（Streaming は OVR を参照しない = 既存 asmdef 規約）
[純データ]  ShowBodyInput（頭 pose・左右手 world 位置・有効フラグ）
        ↓
[純ロジック] ActorArmLogic（体験者 → 人形への写像・idle 合流・平滑化）+ TwoBoneIk（解析 IK）
        ↓
[リグ]     ShowActorRig（Humanoid Animator のボーンへ適用。LateUpdate）
        ↓
[層]       ShowCgLayer（仮想カメラで ShowCg レイヤだけを RT へ → シェーダ 3 層目）
```

- **Streaming asmdef は OVR に依存しない**。Bridge 側から `ShowCgLayer.SetBodyInput(...)` へ push する
  （`ZoneLayoutApplier` が `HeadCourseXZProvider` を注入するのと同じ向き）
- **人形が出ていない間は読まない**（`ShowCgLayer.WantsBody` が false の間 Bridge は GetHandState を呼ばない）

### 決めたこと（と、その理由）

| 論点 | 決定 | 理由 |
|---|---|---|
| 手の**回転**を使うか | **使わない**（位置だけ） | OVR の手 basis は素直でない（[[table_duo_wrist_anchor_basis]] の実害）。640x480 の監視カメラ画質で手首の捻りは見えない。位置だけなら basis に依存せず壊れない |
| 全身アニメ | **持たない**（T ポーズから腕だけ下ろす=マネキン） | 人形は直立不動の方が怖い。AnimatorController を要求しないので **どの humanoid FBX でも即動く**。idle アニメを足したくなったら Animator に足すだけ（設計上は排除しない） |
| follow の向き | **体験者の頭 yaw に合わせる**（旧実装の「カメラの方を向く」は廃止） | 人形は体験者の分身。腕の位置と体の向きが常に整合する。カメラ目線が欲しい演出は `fixed` + `fixedYawDeg` で著作する |
| 体格差 | 人形身長 / 体験者の頭高で**手の相対ベクトルをスケール**する | 小さい人形でも「同じ動き」に見える |
| 手が取れない時 | **idle（体側に垂らす）へ 0.35s で合流**。人形は消さない | 不変条件 7（ハンドトラッキング不在でも演出は止まらない） |
| ライティング | 専用シェーダ `FixedCamVr/ShowActor`（固定方向ライト + リム）。**シーンライトに依存しない** | 現場の照明・URP 設定で人形の見えが変わらない。逆に CG レイヤ用のライトを足すと実シーンを汚す（URP は Light.cullingMask を尊重しない） |
| 実シーンへの露出 | HMD カメラの cullingMask から **ShowCg レイヤを外す**（`MainDemoSceneSetup`） | 外さないと人形が VR 空間にそのまま浮いて見える（＝映像の中に居るのではなくなる） |

### 座標の扱い（重要 — 旧実装のバグ修正を含む）

`CourseFrame` は **transform を動かさない**（`originXZ` / `yawDeg` を持ち `CourseToWorld()` で変換する）。
旧 `ShowCgLayer` は仮想カメラと人形を `courseRoot` の**子にして localPosition に course 座標を入れて**いたため、
登録（位置合わせ）が反映されていなかった。**`CourseToWorld` を注入して world へ変換する**方式へ直す
（`ZoneLayoutApplier` が `ShowControlClient.CourseToWorldProvider` / `CourseYawProvider` を注入。
`HeadCourseXZProvider` と同じ経路なので新しい配線を増やさない）。

- 仮想カメラ: `CourseToWorld(pose.xz, pose.y)`・回転 = `Quaternion.Euler(-pitch, courseYaw + yaw, 0)`
- 人形: `CourseToWorld(xz, 0)`（床に立つ）・回転 = course yaw + 体験者頭 yaw（follow）/ `fixedYawDeg`（fixed）

### 人形の用意（Mixamo など任意の humanoid FBX から）

`Tools/FixedCamVr/Setup/Build Show Actor Prefab`（Editor）が
**humanoid の FBX を 1 個選ぶだけ**で `Assets/Resources/ShowActors/<id>.prefab` を作る:

1. FBX を instantiate → `ShowActorRig` を付ける（Animator は FBX 由来の Humanoid Avatar）
2. 全 Renderer のマテリアルを `ShowActor` シェーダのマテリアルへ差し替え（人形らしい単色 + リム）
3. レイヤを `ShowCg` に統一（仮想カメラだけが描く）
4. 身長を実測して `heightM` のヒントをログに出す

show.json 側は `actors[] = [{id:"doll", name:"人形", prefab:"ShowActors/Doll", heightM:1.6, …}]`。
**`prefab` は Resources からのパス**（`Resources.Load<GameObject>(def.prefab)`）。

既定では同梱の `Assets/ThirdParty/Mixamo/Remy.fbx`（Humanoid・ライセンス同梱）を種にできる。
差し替えたいときは同じメニューで別 FBX を指すだけでよい（コード変更なし）。

### 卓（Web）で著作するもの

| 何を | どこで |
|---|---|
| カメラの姿勢（CG の視点） | フロアマップの **📐 カメラ姿勢モード**。マーカーをドラッグで位置、矢印の先をドラッグで向き。高さ・俯角・画角は数値欄 |
| カメラの役割（ゾーン / 演出専用） | カメラ列のヘッダ |
| 人形の定義（actors[]） | フロアマップ下の **🎭 CG 人形** パネル（id / 表示名 / プレハブ / 身長 / 固定位置） |
| カットで人形を出すか | 演出リボンのカット詳細（`cg` は actors から選ぶドロップダウンへ） |

### テストで固定する不変条件（EditMode）

1. IK: 目標が届く範囲なら手首が目標に一致する / 届かない距離ならまっすぐ伸びてクランプされる（発散しない）
2. IK: 上腕・前腕の長さが 0 でも NaN を出さない
3. 写像: 体験者が手を上げれば人形の手首目標も上がる（スケール比が身長比に一致）
4. idle: 手が無効な間、目標は idle へ単調に近づき、有効化で滑らかに戻る（瞬間移動しない）
5. `follow` は体験者 XZ、`fixed` は著作 XZ（既存の Apply 経路）

### 先送り（この実装に含めない）

- 手首・指の回転（上記の理由。指は監視カメラ画質で見えない）
- 脚・胴のアニメーション（マネキン方針）
- 影・接地の物理（床 y=0 決め打ち）
- 複数 actor の同時表示（1 カット 1 actor・前計画から継続）
- CG のオクルージョン（人形が実物の陰に入る表現）。映像は 2D なので原理的に前後関係を持てない
