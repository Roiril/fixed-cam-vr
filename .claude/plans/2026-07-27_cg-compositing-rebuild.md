# CG 人形の合成を作り直す — 実写になじませる基盤

status: **Step 0 / 1 / 2 / 3 / 5 完了**（2026-07-27・Quest 実機未検証）。残るは Step 4（Editor 合成プレビュー）と、
下の「積み残し」。検証: EditMode 784/784・node 188/188。

### 積み残し（この計画の中で未着手のもの）

1. **照明の 4 項目が Unity 側で未使用** — `tempK` / `intensity` / `ambient` / `shadowSoftM`。
   卓では著作できるが `ShowActor.shader` は `_LightDir` しか読まない。**著作しても何も変わらない**ので、
   これは「著作したものが黙って消える」と同型。読むようにしたら `room-model.js` の `LIGHT_FIELDS.applied` を戻す
2. **`cgMode:"follow"` × 素材カット（`clip`/`still`/`rec`）の意味が壊れている**（§5 罠 9）。
   卓は輪郭を出さず事実だけ言う実装。スキーマで禁止するか素材に calib を紐づけるかの決着が要る
3. **登録リチュアルの壁ワイヤーを `layout.room` へ寄せる**（§5 罠 6）。いまは `layout.wall` と二重管理
4. `roomOutlineSegments`（`room-model.js`）が未使用 — 較正 UI のワイヤー重畳に載せるのが本来の落とし先
前提文書: [2026-07-27_cg-actor-hand-tracking.md](2026-07-27_cg-actor-hand-tracking.md)（人形とハンドトラッキングの現行設計）/
[2026-07-26_show-sources-and-cg-layer.md](2026-07-26_show-sources-and-cg-layer.md)（CG レイヤの導入）。
**この文書は上 2 つの「合成」部分を置き換える**（腕の駆動・素材・演出の語彙はそのまま生きる）。

## ユーザー要求（原文）

> 人形を合成するシステムについて具体的に作りこんでほしい。blender でやる vfx みたいに、3D モデルの人形を、
> 違和感少なく背景の録画映像に溶け込ませれるようにしたい。なので、ライティングから、壁の位置からカメラの位置から、
> 影の計算から、クオリティ高く合成できるようにしてほしい。根本の設計から考えて。
> 現状のシステムに含まれる人形を体験者の位置に合わせるとか、決められた位置に配置するとか、
> **プレビューがないからわかりずらいし座標指定でしか人形を配置できないのも難しいし、
> カメラの位置が考慮されてないから体験者の位置に合わせるなんてそもそもできてない。**
> **壁と床がある 3D シーンをマスク用として用意し、うまいこと溶け込ませるようにしてほしい。**
> 人形合成専用のタブとかも必要であれば検討して。

---

## 1. なぜ作り直すか — 確定した欠陥

独立した 2 つの調査（設計批評 / コード監査）が**別経路で同じ結論に到達した**ものを確定として扱う。

| # | 事実 | 根拠 | 帰結 |
|---|---|---|---|
| **A** | CG は `screenUv`（枠いっぱい）でサンプルし、ライブ映像は `ContainUv(_LiveScale)` でレターボックスされる。枠は 16:9（`MjpegScreenStage.prefab` の localScale 2.3704×1.3333）、映像は 4:3 → `_LiveScale=(0.75,1)` で映像は u∈[0.125,0.875] | `ScreenComposite.shader:132` vs `:148`、`MjpegScreen.cs:146-148` | **姿勢が完璧でも CG は映像に対し水平 1.33 倍外側にずれる。**中央の人形だけ合い、端ほど破綻 |
| **B** | 仮想カメラの RT アスペクトは `MjpegScreen.ScreenAspect`＝**枠の 16:9**（映像の 4:3 ではない） | `ShowCgLayer.cs:199-207` | 水平画角の切り取り自体が実カメラと違う |
| **C** | `fovDeg` を Unity は**垂直** FOV として使い、卓のフロアマップは**水平**の扇として描く | `ShowCgLayer.cs:223` vs `floormap.js:508-521` | 著作者が見ている画角と実際が約 25% 違う |
| **D** | カメラ姿勢 6 自由度すべてが人間の手打ち。実測経路がゼロ。`/info` の実測レンズ画角 `lensFovDeg` は**画面に表示されるだけ**で姿勢へ流れていない | `ShowCameraPoseDef`、`StreamingDiagnosticsOverlay.cs:123`、grep 全件 | 較正手段が存在しない。show.json 上 A/B/C は姿勢すら持たず、D だけ手ドラッグ値 |
| **E** | HMD 位置合わせ（登録）が未完了でも CG を出す。`CourseToWorldProvider` が null なら identity へ黙って落ちる | `ShowCgLayer.cs:165-169`、`CourseFrame.cs:23-27` | 未登録・OS recenter 後は人形が全く違う場所に立つ。警告も無い |
| **F** | 人形の光の向き `_LightDir` がワールド固定 | `ShowActor.shader:17` | Quest のトラッキング原点の向き次第で部屋に対する光の向きが変わる |
| **G** | RT は 1280×720 で映像（640×480）より鮮鋭・MSAA off・アルファ実質 0/1 のハード合成 | `ShowCgLayer.cs:191,210`、`ShowActor.shader:88` | 輪郭がギザギザかつクッキリ = 「貼り付けた絵」 |
| **H** | 影・部屋プロキシ・オクルージョン・合成プレビューが存在しない | 前計画の「先送り」節 | 接地しない・壁の裏へ回れない・実機まで結果が見えない |

**ユーザーの主張の判定**: 「カメラの位置が考慮されていない」は厳密には不正確（機構はあり、姿勢未著作なら出さないガードまである）。
だが**「体験者の位置に人形を合わせられていない」は完全に正しい**。合わせるための較正手段と像空間の対応付けが欠けている。

### 明示的に覆す前提

前計画 [2026-07-27_cg-actor-hand-tracking.md:121](2026-07-27_cg-actor-hand-tracking.md) の
「CG のオクルージョン（人形が実物の陰に入る表現）。**映像は 2D なので原理的に前後関係を持てない**」は **誤り**。
実映像の深度は要らない。**部屋のプロキシ幾何の深度**で足りる（人形と一緒に CG カメラへ描き、色を書かずに深度だけ書く）。

---

## 2. 設計の核 — 何を正とするか

### 2.1 二重実装の線引き（最重要）

卓（Web）と Unity の両方が「人形がどこにどう写るか」を知る必要がある。だが**両方に写実を描かせない**。

| 層 | 卓（ブラウザ・素の ES modules + WebGL2） | Unity（実機・Editor） |
|---|---|---|
| **幾何・投影** | ✅ ミラーする（course 姿勢 → 内部行列 → スクリーン座標）。**数値なので golden trace で機械照合できる** | ✅ 正 |
| **人形の輪郭** | ✅ 足元マーカー + 身長ボックス + 単色シルエット。**意図的に写実にしない** | — |
| **人形の見た目・影・IK・シェーディング** | ❌ 描かない | ✅ 正。最終品質は Unity Editor が焼く合成 PNG |

既存の「post FX 数式を Unity と卓で一致させる規約」は、RGB に対する 9 個のスカラ演算だから成立している。
人形は glTF パース + スキニング + IK + シェーディング + 影で、**固定できる有限仕様が存在しない**。
**ミラーしてよいのは数式、してはいけないのは描画器。**

> ⚠ プロキシを「それっぽく」見せたくなった瞬間にこの線を踏み越える。**半透明の輪郭・単色**を規約として守る。
> 写実に見えると著作者がそれを信じて Unity 確認を飛ばす。

### 2.2 較正（calibration）— 姿勢は測るものであって置くものではない

**床の × 印を実映像上でクリック → 平面ホモグラフィ分解で姿勢 + 焦点距離 + 歪みを解く。解くのは卓（オフライン JS）。**

- 使う点は **`layout.regPoints`（HMD 位置合わせの床基準点）をそのまま流用**する。現場のテープを増やさない
- 数学は Zhang (MSR-TR-98-71) の平面ホモグラフィ分解。`skew=0 / 主点=画像中心 / fx=fy` を仮定すれば未知は焦点距離 1 個で、1 枚から解ける
- **半径方向歪み k1 を同時に推定する**（アドバイザー指摘）。運用実機は超広角を許容している（IP Camera Lite の Ultra Wide 実績）。
  歪みを表現できないと、**唯一の検証手段であるワイヤー重畳が周縁で必ずズレ、姿勢誤差と区別できず較正ループ自体が信用を失う**。
  実装は k1 を粗探索 → 各 k1 で点を歪み補正 → ホモグラフィ → 再投影誤差最小を採る（線形解の繰り返しで済む）
- **`cameras[].pose` は残し、`cameras[].calib` を新設する**。pose = 人がフロアマップでドラッグする概算、calib = 解。
  同居させないと**卓のドラッグが解を破壊する**。calib があれば calib が勝つ
- **calib はレンズ・解像度に従属する**（アドバイザー指摘）。`/info` の `lensId` と `widthPx/heightPx` をキーに保存し、
  **不一致なら較正無効 → CG 非表示ガードへ合流**する。でないと配信設定を変えた瞬間に黙って狂う
- Unity 側は `Camera.projectionMatrix` に内部行列 K を直接入れる。`gateFit` / `lensShift` の符号問題を全部回避できる
- **PnP を実機で解かない**。実機で解くと検証手段が無い。卓で解いて数値を配る

### 2.3 影 — premultiplied over が「乗算」を運ぶ

合成式を straight alpha から **premultiplied over（Porter–Duff）** へ変える:

```hlsl
現: col = lerp(col, cg.rgb, cg.a * s);          // over のみ。影は原理的に表現不能
新: col = col * (1 - cg.a * s) + cg.rgb * s;    // premultiplied over
```

影は「**rgb=0 / a=影の濃さ**」の断片として同じ RT に描けば、over が自動的に乗算（背景が (1-d) 倍）になる。
RT を 2 枚に分ける案・R チャンネルに影を分離する案は却下（配線が増えるだけで表現力は同じ）。

- **影の生成は URP のシャドウマップ + シャドウキャッチャー床**。平面投影シャドウ（射影行列）は却下 —
  床と壁の境目で破綻し、その修正コストが結局高い。**プロキシで壁・机を作るのに、そこへ影が落ちないのは逆に不自然**
- 実装の出発点は **Meta MRUK 同梱の `Meta/MRUK/Scene/HighlightsAndShadows`**（`com.meta.xr.mrutilitykit@201.0.0`・ローカルに実在）。
  `Blend One OneMinusSrcAlpha` / `alpha = (1 - MainLightRealtimeShadow(...)) * _ShadowIntensity` という正しい答えが最初から入っている。
  passthrough 用の depth 参照だけ剥がす
- **接地の落ち込み（blob 影）を別に足す**。「浮いている」に一番効くのはこれ。SSAO は CG レイヤの深度しか知らないので実写の床と接地しない

### 2.4 ライティング — 主光源 1 灯を人が著作する

**Quest に環境光推定 API は存在しない**（Meta の Scene / Depth は幾何であって照明ではない。ARCore/ARKit の Light Estimation 相当は無い）。
照明は人が著作するしかない。

- **CG 用 directional light 1 灯 + ambient**。方位角・仰角・色温度・強さ・ambient を卓の 5 スライダで著作し `layout.room.light` に持つ
- 分離は **URP 14 の Rendering Layers**（公式に「Forward では light-only の影響は無視できる」と保証されている唯一の手段）。
  ライトを ShowCg レイヤに置く二重防御を併用
- **光源は per-camera ではなく部屋で 1 つ**。カメラごとに持つとカメラ切替のたびに人形の陰影が飛ぶ
- 環境マップ（IBL）は**持たない**。640×480 + 走査線 + グレインで潰れる情報にコストを払うことになる。
  Debevec の 3 層構成のうち、この画質で観客が読めるのは主光源の方向・色温度・強さと床の影の向きだけ
- `ShowActor.shader` の「シーンライト非依存」判断は正しいので維持。**光の向きだけ course 相対に直す**（欠陥 F）

### 2.5 部屋プロキシ — show.json に持ち、1 幾何で 3 用途を兼ねる

**`layout.room` に course 空間で記述し、Unity と卓が同一規則でメッシュ化する。**

- Blender の FBX は却下 — course 座標との整合を人手で保つ必要があり、**卓が読めない = シミュレータが必ず嘘をつく**
  （このプロジェクトは既に卓と実機の食い違いを 3 件出している → [sim_device_divergence](../memory/sim_device_divergence.md)）
- Unity Prefab も同じ理由で却下
- `layout.grid`（ゾーンのタイル塗り）からの押し出しも却下 — **「ゾーンを塗り替えたら壁が動く」最悪の結合**を作る。タイル 0.15m は壁位置の精度として粗い
- Meta Scene API / MRUK も却下 — 体験者ごとの Space Setup 運用負荷と座標対応付けの不確実性。
  **設営時に固定される既知の形状**なので、卓から編集できる手置きの箱のほうが速く正確
- **3 用途（オクルーダ / 影の落ち先 / 較正の参照点）は 1 幾何で兼ねる**。レンダリング属性だけ分ける。別幾何を持てば必ずズレる
- **既存の `layout.wall`（L 字 3 点）と登録リチュアルのワイヤーフレームを `layout.room` へ寄せる**。
  寄せないと `layout.wall` と `layout.room` の二重管理が即日発生する

### 2.6 人形の位置は「人形」ではなく「カット」が持つ

現状 `ShowActorDef` が `fixedX/fixedZ/fixedYawDeg` を持っている（`ShowControlClient.cs:240-249`）。
これでは**同じ人形を別のカットで別の場所に立たせられない**（人形を複製する羽目になる）。

→ **位置はカット側 `steps[].placement` へ移し、actor は見た目・身長・既定値だけ持つ**。
show.json は APK に焼き込まれるので、**これは後から覆すのが最も高い決定。最初にキーを切る。**

### 2.7 人形合成専用タブは作らない

卓の既存 3 モード（🎬 事前オーサリング / 素材工房 / ライブ運用）は **「1 日の局面」軸**。
「人形」は機能軸なので、4 つ目を足すと軸が壊れる。しかも素材工房が既に「その構図の live に載せた時に破綻しないかを見る場所」という責務を持っており重なる。

導線は既存の面に置く:

```
🎬 事前オーサリング
├ 周回タイムライン ─ カット編集の「CG 人形」欄に ▸ 立ち位置［体験者／決めた位置］＋［📍 画面で置く］
├ マルチカメラ列 ─ 各列の実映像に **人形プロキシ**（輪郭＋足元）と **部屋ワイヤー**を重ねる
│                  └ 列の 📐 欄の隣に［🎯 姿勢を合わせる］＝較正の入口
├ フロアマップ ─ モードは増やさない。📐 カメラ姿勢モードに人形の印を同居させる
└ 🎭 CG 人形（既存 actors.js を改装）
   ├ カタログ（プレハブ・身長・既定向き。位置は持たなくなる）
   └ Unity が焼いた合成検証ショットのサムネ
```

---

## 3. データモデル（show.json 差分）

present-flag は既存の **AND 規約**（`TimelinePresentFlags.Reconcile`＝宣言 bool && オブジェクト非 null）に必ず合わせる。
`!=null` 純導出にすると、卓が既定オブジェクトを常時出力して**全カメラに幽霊 calib が武装する**（2026-07-23 の critical と同型）。

```jsonc
"layout": {
  "room": {                                    // 3D プロキシ = オクルーダ / 影の落ち先 / 較正参照
    "floorY": 0, "floorW": 1.8, "floorD": 1.8,
    "walls": [ { "id":"w1", "x1":-0.5,"z1":0.5, "x2":0.5,"z2":0.5, "h":1.0, "thick":0.04 } ],
    "props": [ { "kind":"box", "x":0,"z":0, "w":0.6,"d":0.4,"h":0.7, "yawDeg":0 } ],
    "light": { "yawDeg":30, "pitchDeg":55, "tempK":4000, "intensity":1.0,
               "ambient":0.35, "shadowDensity":0.55, "shadowSoftM":0.12 }
  },
  "hasRoom": true
},

"cameras": [ {
  "pose":   { "x":.., "z":.., "y":1.2, "yawDeg":.., "pitchDeg":.., "hfovDeg":74.6 },
  "hasPose": true,                             // 人がドラッグする概算。calib 不在時のフォールバック
  "calib": {                                   // 較正の解。あればこちらが勝つ
    "x":..,"z":..,"y":..,"yawDeg":..,"pitchDeg":..,"rollDeg":..,
    "fx":.., "fy":.., "cx":0.5, "cy":0.5,      // 内部行列（cx/cy は画像幅高さの比）
    "k1":0.0,                                  // 半径方向歪み 1 係数
    "srcW":640, "srcH":480, "lensId":"0",      // ★ 従属キー。不一致なら較正無効
    "rmsPx":1.8, "pointCount":5, "solvedAtIso":"2026-07-27T14:02:11",
    "refs":[ {"u":0.31,"v":0.72,"x":-0.9,"z":0.9} ]   // 再解決用に対応点を残す
  },
  "hasCalib": true
} ],

"actors": [ { "id":"doll", "name":"人形", "prefab":"ShowActors/Remy", "heightM":1.6 } ],
                                               // ★ fixedX/fixedZ/fixedYawDeg を廃止

"timeline": { "segments": [ { "takes": [ { "steps": [ {
  "cg":"doll", "cgMode":"fixed",
  "placement": { "x":0.2, "z":-0.4, "yawDeg":180 },   // ★ 位置はカットが持つ
  "hasPlacement": true
} ] } ] } ] }
```

**`fovDeg` は捨てて `hfovDeg`（水平）を新設する。** 意味変更ではなくキー変更にする —
黙って垂直→水平に変えると「卓では合うのに実機が違う」を再生産する。旧キーは読まない。

`rmsPx` / `solvedAtIso` は登録リチュアルの `maxResidualM` / `savedAtIso` と同じ立ち位置（品質と鮮度が現場で見える）。

---

## 4. 段階

| Step | やること | 検証 | 体感 |
|---|---|---|---|
| **0. 土台の是正 + スキーマ骨格** | 欠陥 A/B/C/E/F/G の修正。`_CgTex` を live と同じ `uvL` でサンプル / RT アスペクト＝映像実寸 / `hfovDeg` へ一本化 / **RT 解像度を映像相当まで落とす**（鮮明すぎ対策・むしろ軽くなる）/ premultiplied 合成 / 未登録なら CG を出さないガード / 光の向きを course 相対に。同時に `calib` / `room` / `placement` のキーを切る（中身は後段で埋める） | Editor 静止画。既知の床マーカーに人形を置き、実写フレーム上の同じ点に載るか。**実機不要** | 単体では小。**ここまでやらないと以降が測定不能** |
| **1. 較正 + 検証表示** | 卓に［🎯 姿勢を合わせる］。床点クリック → k1 込みホモグラフィ分解 → `cameras[].calib`。Unity は `projectionMatrix` 直接。**実映像に部屋ワイヤーを重ねる検証表示を必ず同梱** | 卓が rmsPx を出す + ワイヤー重ね。**この重ね表示がこの体験で唯一の反証可能な一次証拠** | **最大**。人形が床に立つ |

> **Step 1 の進捗（2026-07-27）**: 数学（`tools/web-compositor/calib.js`）と Unity 側の適用
> （`ShowCgLayer.ApplyCameraCalib` / 歪みシェーダ）は完了。卓と実機の投影一致も両側のテストで固定済み。
> **残りは卓の UI**（静止フレームの上で床点をクリック → 解く → ワイヤー重畳で確認 → `cameras[].calib` へ保存）。
> 実測で判明した運用制約は [streaming.md](../rules/streaming.md) の「較正の実装と、現場運用がそうでなければならない理由」に集約した。
> 要点は **「画角は一度だけ丁寧に測って固定、置き場所は現場で毎回」**（f を推定すると位置が 27cm ずれ、固定すれば 2cm）。
| **2. 部屋プロキシ + 影 + 光** | `layout.room` の編集（卓）と生成（Unity・卓）。シャドウキャッチャー + 接地 blob。Rendering Layers で照明分離。`room.light` 著作。**登録ワイヤーを `layout.room` へ寄せる** | Editor 多角度静止画（[visual-verification](../rules/visual-verification.md)） | **大**。「浮いている」が消える |
| **3. オクルージョン + 配置 UX** | プロキシ depth-only（MRUK の `Blend Zero One` 方式）。`steps[].placement` の実装。**映像クリックで床に配置**（較正の逆写像）。卓の輪郭プロキシ + golden trace 照合 | 人形を壁の裏に置いた静止画 / node テスト | 中〜大 |
| **4. Editor 合成プレビュー** | `Diagnostics/Preview Show Composite` — 実写プレート（`captures/`）× 実カメラ姿勢 × 実リグで**カットごとの合成 PNG** を焼く。卓がサムネ表示 | PNG そのものが証拠 | 著作の速度が変わる |
| **5. 仕上げ** | 映像遅延の補償（body input のタイムスタンプ付きリングバッファ）/ 色収差 / CG 側だけの微ブラー / roll | 実機（遅延補償のみ実機必須） | 上積み |

**Step 0 と 1 の間に他を挟まない。** 壊れた投影に対して較正すると誤差を焼き込む。
**Step 0 は 1 コミットで revert 可能に閉じる**（アドバイザー指摘）。

---

## 5. 罠

1. **`fovDeg` の意味を黙って変える** — 焼き込み・端末キャッシュ・ライブの 3 経路が同じ値を持つ。キー名を変えて旧キーは読まない
2. **premultiplied 化は片方だけ入れると壊れる** — カメラ clear を (0,0,0,0)、人形マテリアルを `Blend One OneMinusSrcAlpha`、合成式、の 3 点セット。
   `_CgStrength` フェードの意味も変わる（RT が straight のままだと二重に暗くなる）
3. **`_CgTex` を `uvL` へ移すと letterbox 帯に人形が出なくなる** — これは正しい挙動（映像の外に人形は居ない）だが「端で切れる」に見える。卓にも明示する
4. **新レイヤを足すなら `manage_editor action=add_layer`** — ファイル直編集は Unity のメモリ側に無視されて消える（[cg_actor_and_layer_traps](../memory/cg_actor_and_layer_traps.md)）
5. **calib を pose に統合しない** — 統合すると卓のドラッグが解を破壊する。「calib があるときは印を calib 値で描き、ドラッグしたら calib を破棄する」を明示的に設計する
6. **プロキシの二重管理** — `CourseRegistrationController` の壁ワイヤーと `layout.room` が別々に生きると必ずズレる。Step 2 で必ず寄せる
7. **卓のシミュレータが人形を黙殺している** — `show-sim.js:245-278` の `sampleScreen` は `step.cg` を一切見ない。
   つまり「▶ 検証」は**人形の出るカットを人形なしで再生している**。Step 3 のプロキシが入るまで
   「このカットには CG 人形があります（この画には出ていません）」を明示する。
   黙って欠落させるのは 2026-07-27 に潰したばかりの「著作した演出が黙って消える」と同型の罪
8. **Edit Mode のスキニング固着** — `ShowActorVizPreview.cs:95-99` に記録済み。`forceMatrixRecalculationPerRender` を忘れると
   **全ポーズが同一 PNG になる**（バイト単位で一致した実害）。Step 4 の合成プレビューでも同じ罠を踏む
9. **`cgMode:"follow"` × `source:"rec"` / 素材カットは意味が壊れている** — 過去の周の録画の上に「今の体験者」を立てることになる。
   さらに `TakeRunner.cs:387` は camera 未指定時に `ResolveLatestZoneCamera()` へフォールバックするため、
   **素材の構図とは無関係なカメラで人形が立つ**。スキーマで禁止するか素材にも calib を紐づけるかの決着が要る（Step 3 で決める）
10. **キャリブ点の二重定義を作らない** — `layout.regPoints`（HMD 登録用）と別に「カメラ較正用の点」を作らない。同じ床の × 印を両方で使う

---

## 6. 決めなかったこと（人間の判断が要る）

1. **部屋の照明を単一光源で近似してよいか** — 蛍光灯が複数なら影が 2 本出る。単一近似が嘘に見えるかは現地の実測で決まる
2. **プレート（背景の静止フレーム）を本番前に撮り直す運用を受け入れるか** — カメラが動くたびにプレートも古くなる
3. **人形を出すカメラは何台か** — 1 台（演出専用の D）だけなら Step 4 の合成プレビューは 1 構図で済む

---

## 7. 調査の出所

この設計は 4 体の並列調査（実写合成技術の一次調査 / 合成基盤の設計批評 / 著作 UI とプレビューの設計批評 / 現状コードの監査）と
アドバイザーの逸脱確認を統合したもの。事実 A/B/C は**独立した 2 体が別経路で到達**したため確定として扱っている。

技術の一次ソース（要点のみ。詳細は各リンク）:
- Porter & Duff, *Compositing Digital Images*, SIGGRAPH 1984 — premultiplied over の原典
- Zhang, *A Flexible New Technique for Camera Calibration*, MSR-TR-98-71 — 平面ホモグラフィ分解
- Debevec, *Rendering Synthetic Objects into Real Scenes*, SIGGRAPH 1998 — distant / local / synthetic の 3 層。
  「シャドウキャッチャー床＝local scene」という語彙の出所
- [URP 14 Rendering Layers](https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@14.0/manual/features/rendering-layers.html) —
  「Forward では light-only の rendering layers は影響が無視できる」
- [Camera.projectionMatrix](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Camera-projectionMatrix.html) —
  直接代入で `gateFit` / `lensShift` の符号問題を回避できる
- ローカルに実在する参照実装: `com.meta.xr.mrutilitykit@201.0.0` の
  `Meta/MRUK/Scene/HighlightsAndShadows`（URP シャドウキャッチャー）/ `Meta/MRUK/MixedReality/InvisibleOccluder`（`Blend Zero One`）
- 色空間は確認済みで既に正しい: プロジェクトは Linear、`CameraStream.cs:120` の Texture2D は sRGB、
  RT も sRGB → live も CG も合成時点で両方 linear
