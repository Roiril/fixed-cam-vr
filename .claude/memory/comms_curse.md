# 連絡の面の呪い — 呪われた双子の画面が斑で重なる

2026-09-18。`canon/LEDGER.md` 0229（ユーザーのスケッチ「エージェントスクリーンのバグり方」）。
それまでの壊れ（固定位置の字抜け・行の横ずれ・赤の残像 ＝ 引き算）を撤去し、
**通常の画面の上に、呪われた双子の画面が斑（まだら）で重なる**形にした。世界観の判定は LEDGER、
契約は `rules/show-design.md`「連絡の面は表示イベントで壊れていく」。**ここは焼く側の罠と実測だけ**。

## 部品

| 何 | どこ |
|---|---|
| 斑の場（値ノイズ 2 オクターブ・面のローカル m） | `Assets/Art/Shaders/Streaming/CommsCurse.hlsl` ＋ C# の写し `CommsCurseLogic.Field` |
| 地の双子（毛羽立ち・煙のにじみ・走り書き） | `Assets/Art/Shaders/Streaming/CommsPanelPlate.shader` |
| 文字を切るステンシル（色は書かない・別 quad） | `Assets/Art/Shaders/Streaming/CommsCurseStencil.shader` |
| 顔の斑（スイ → 市松人形） | `CommsAvatar.shader`（同じ場を `_Origin` / `_Size` で読む） |
| 斑の面積と閾値 | `CommsCurseLogic.MaskFor`（0.25 → 22% / 0.75 → 55% / 1 → 全面） |
| 1 秒の立ち上がり | `CommsPanelLogic.SetCurseTarget` / `Weights.curse`（面が開いた縁で 0 から） |
| **0.75 以降の憑依の出し方**（0230） | `CommsPossessionLogic`（時計）/ `CommsCurseLogic.SweepFrontY`（前線・HLSL と対）/ `CommsPanelLogic.PossessionSample` / `Weights.sweep`。罠は [comms_takeover.md](comms_takeover.md) |
| 配線 | `CommsPanel.PushCurseTarget` / `SetPlate` / `ApplyTextStencil` |
| 見る | `.\tools\unity.ps1 menu comms-preview -Set motion=1` → `py -3.11 tools/render-comms-curse-preview.py` |
| 観測 | `ev=sum` の `commsCurse=<シェーダ>/<斑>/<目標>`・`ev=commsCurse sec=`・`ev=comms curse=` |

## ⚠⚠ 文字はステンシルで切る（別 quad が書き、TMP の材質が読む）

TextMeshPro の面には斑を描けないので、**別の quad**（`CommsCurseStencil.shader`・`ColorMask 0`・queue 4981）が
斑の中（k ≥ 0.5）にステンシル bit 8 を書き、本文と下段の材質に
`_Stencil=8 / _StencilComp=NotEqual / ReadMask 8 / WriteMask 0` を入れて画素単位に切る（`CommsPanel.ApplyTextStencil`）。

- ⚠⚠ **同じシェーダの第 2 パスにしない。** URP は 1 つのマテリアルの LightMode の無いパスを**最初の 1 つしか
  描かない**。最初そう書いて、ステンシルのパスだけが描かれ、**地の色のパスが 1 画素も出なかった**
  （地が消えて文字だけが切れた画になる。2026-09-18）。隔離の殻が Mask / Shell の 2 シェーダなのも同じ理由
- **1 パスでは書けない。** 色を塗る画素（矩形の内側）と書き込む画素（斑の中）が食い違う
- **`fontMaterials` 全部へ書く**（フォールバックでサブメッシュの材質が増える）。`SetNotice` で組み直すたびに入れ直す
- **bit を限る**（隔離の殻は 1・導入の破砕は 32）。全ビットの Replace にすると他の層の印を潰す
- ⚠ **ステンシル面の無い深度形式だと黙って切れない**（面は正常に見える）。だから字の中心と両端が
  斑の中にある字は CPU でも alpha 0 にしてある（`ApplyGlyphMesh`）— 切れなくても斑の中の字は消える。
  端だけ千切れる字はステンシルにしか出ない
- ⚠ **新シェーダは実行時 `Shader.Find`** → `ProjectSettings/GraphicsSettings.asset` の Always Included に
  入れてある。剥がれると地は URP Unlit へ落ちて出るが、毛羽立ち・走り書き・切断が 1 画素も出ない。
  観測は `commsCurse` の 1 つ目（`PlateBuilt`）。**Editor では出るので、このビットが唯一の手掛かり**

## ⚠ 斑の面積は侵食度に等号で結ばない

設計批評（design-critic・2026-09-18）の指摘: 0.75 は 2 周目 C で立ち、そのまま閾値にすると
（値ノイズの分布は 0.5 に寄っているので）**面の 9 割が覆われ**、3 周目の報告の返事が 1 字も読めなくなる。
⇒ 覆う**面積**を決め（`CoverageAtFirstPov` 0.22 / `CoverageAtChainedPov` 0.55 / 1 は全面）、
場を格子で標本化した分位から閾値を逆算する（`MaskForCoverage`）。テストが面積を固定する。

## ⚠ 「出た初めは通常 → 1 秒で重なる」は CommsPanelLogic が持つ

`CommsPanel.Apply` には dt が無い（プレビューは `logic.Tick` → `Apply` の順で回す）ので、
立ち上がりの時計は `CommsPanelLogic` に置いた。面が畳まれた所から開く縁（`Begin` / `SetGuideWanted`）で
0 から数え直し、同じ面のまま次の文面へ繋ぐ（chained）ときは重なったまま。開いている最中に目標が変われば
いまの量から 1 秒で寄る。**目標は毎フレーム `PushCurseTarget` が押し込む**（`Update` の Tick の前・`Deliver`・
プレビューの `SetDecayForPreview`）。

批評の別案（侵食度が上がった直後の 1 回だけ 0 から・以後は開いた瞬間から目標）は `canon/OPEN.md` に置いた。
判定は動画で。

## ⚠⚠ 侵食度 0.75 以降は斑ではなく前線（2026-09-18・0230）

ここまでの「1 秒で斑が重なる」は **0.25 だけ**になった。0.75 と 1 は憑依の出し方
（一気に出る → 読ませる → 上から前線が降りて全面が双子）。ユーザーの判定「これだと乗っ取られている感じが、
一部のユーザにわかりにくい」「初見の人視点で」。

- 斑（`_Curse`）と前線（`_Sweep`）は同じ k に `max` で合流する。顔・地・ステンシルの 3 シェーダが**同じ 2 つの値**を読む
- 憑依の出し方では `Weights.curse` は塗り替わる前 0・後 1（1 秒の立ち上がりは効かない）。`SetCurseTarget` の目標
  （`MaskFor(0.75)` = 0.477）は使われない — 塗り替わった後は 0.75 でも全面（0229 の「0.75 → 55%」は打つ出し方の話に縮んだ）
- `CorruptedChars` は前線の上側の字も数える（`CommsCurseLogic.IsSwept`）。塗り替わった後は全字
- 嘘の一文も同じ形。旧「文字を切らない・引き延ばし → 抵抗 → 崩壊」は捨てた（[comms_takeover.md](comms_takeover.md)）

## 走り書きの作り方（シェーダ）

正弦波にしない（波形 ＝ 装置に戻る）。1 行につき主線 1 本 ＋ 少し上の副線 1 本。
主線 = ゆっくりした揺れ（振幅は場所ごとに不規則）＋ 細かい震え ＋ 三角波の折れ。太さは字の高さの 8〜13%
（1.5m 先で 2〜3 画素）。線に沿って濃さが揺れ、ときどき掠れて切れる。行頭の少し手前から始まり、
**印字が進んだ所で切れる**（人形が「打たれた分」を塗りつぶしている）。斑の中（k ≥ 0.5 の前後 0.15）だけ。
⚠ `fwidth` は分岐の外で取る（非一様な分岐の中で微分を取らない）。

## ⚠ 暗い背景の前では黒い地の毛羽立ちが読めない

地は黒い半透明（alpha 0.72）なので、毛羽立ちも煙のにじみも「暗くする」効果でしかない。プレビューの背景
（14,13,13）の上では**矩形の外で暗くなった画素が 0**だった。実機ではパススルーや主映像の上に乗るので
出るが、暗い場面ではやはり消える。⇒ 縁に**途切れた象牙の糸くず**（同じ墨・alpha 0.42 × k）を足した。
背景が何であれ輪郭が崩れて見える（矩形の外で変わった画素: 0.25 で 993 / 0.75 で 1711 / 1 で 2604）。

## 実測（Editor の実描画・2026-09-18・`Assets/Screenshots/comms-preview/motion/evidence.json`）

| 侵食度 | 斑の目標 | 届く | 打ち終わりに切られた字 | 1.2 秒の画素差 地/顔/本文 | 矩形の外で変わった画素 |
|---|---|---|---|---|---|
| 0.25 | 0.330（面積 22%） | 0.97 秒 | 3 / 35 字 | 2.2 / 5.3 / 2.0 | 993 |
| 0.75 | 0.477（面積 55%） | 0.97 秒 | 13 / 35 字 | 5.6 / 13.2 / 4.9 | 1711 |
| 1 | 1.000（全面） | 0.97 秒 | 35 / 35 字 | 11.2 / 30.0 / 8.7 | 2604 |

開いて 0.1 秒の画は 3 段とも通常の面と画素差 0（「出た初めは通常」）。
⚠ **字の中心が斑に入る数は面積より少ない**（0.25 で 9%・0.75 で 37%）。字の端が千切れる方が画では目立つ。
EditMode 1965/1965。実機は未走行（並行セッションが Editor を握っていた・`menu hud-font` が向こうの未コミットの
フォント資産を上書きするため）。

## 計器の罠（このセッションで踏んだ）

- 画面座標を **畳んだ後の quad** から測って外れた（`GeometryJson` を最後に呼んでいた）。矩形は通常の面の画素
  （地 ≤ 11・象牙の墨）から測る（`render-comms-curse-preview.py` の `measure_geometry`）
- プレビューの文面ごとの静止画は**侵食度を立てる前に `Deliver` していた**（0230 で気づいた）。出し方は届いた瞬間の
  侵食度で決まるので、順序が逆だと 0.75 の面が「打つ出し方 ＋ 斑 55%」という本番に無い姿で焼ける。
  侵食度 → `Deliver` の順にした（`RunFor` / `ShootReviewFrames` / `ShootMotion` の 3 か所）
- HLSL の予約語 `line` を引数名にして落ちた（`rules/show-design.md` に書いてある罠）。`Shader error` は
  `unity.ps1 test` には出ない — プレビューの後に Editor.log を `grep "Shader error"` する
