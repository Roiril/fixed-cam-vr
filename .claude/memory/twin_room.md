---
name: twin_room
description: 2026-11-04。仮想体験のデジタルツインの部屋（裸電球 1 つの青緑の廃室とカーテンのブース）。撮影の瞬間だけ光る作り・静止画の監査ループ・GPU が落ちた罠
metadata:
  node_type: memory
  type: project
  originSessionId: 942a6d99-df28-4d79-80b4-41c250eb9322
  modified: 2026-11-03T19:37:06.037Z
---

2026-11-04 ユーザー指示「デジタルツインによる動作検証についていい感じにしてほしい。デザイン含めて…本家を超えるくらいの」
「張り付けた参考画像（裸電球 1 つ・剥がれた青緑の壁）の雰囲気を忠実に。椅子じゃなくてカーテンで作った壁」
「厳しめのデザイン監査を設定して、95/100 で合格になるまで改善を繰り返して」。

**どこ**: `VirtualRoom`（部屋）/ `TwinMeshes`（カーテン・パイプ・札の形）/ `TwinLens`（撮影と仕上げ）/ `TwinStill`（静止画）。
シェーダとテクスチャは `Assets/Editor/VirtualWalkAssets/Twin/`（Editor 専用。APK に入らない）。
テクスチャの作り直しは `tools/twin-textures/`（Codex の生成元は Drive `renders/twin/textures-src-20261104/`）。

**回し方**: `.\tools\unity.ps1 menu twin -Set tag=x` → Drive `renders/twin/<時刻>_x/` に hero / wide / low / close / overhead。
wide は `virtualwalk` の `observer.mp4` と同じ画角。動画は従来どおり `menu virtualwalk` → `virtualwalk-video.py`。

**契約（破ると検証が壊れる）**
- 部屋の光・霧・環境光・体の見た目の差し替えは `VirtualRoom.BeginShot` の間だけ。本番の CG 層（`walk.mp4` の目の画の人形）は
  部屋の光を受けない。それ以前は事務室の光が CG 層まで照らしていた（本番には無い光）
- 部屋の寸法の正は `layout.room` と `WalkNavigation`。カーテンの襞・裾の溜まりは見た目だけで、歩行の判定には入らない
- 電球の影の解像度のため、撮る間だけ URP 資産の追加ライト影の High 段を上げ、終わったら戻して `ClearDirty`（ディスクへは書かない）

**罠**
- ⚠⚠ 光の筋（`TwinVolume`）で全ライトの影を毎段引くと GPU が時間切れで落ちる（`887a0005`。2 回）。霞は主光だけを
  影つきで積分し、主光は `_TwinKeyColor`（色×強さ）で見分ける。シェーダ内で fbm を毎段回すのも落ちた → 3D テクスチャ
- 霞の 1/d² に低い上限を掛けると、円錐全体が同じ明るさの「板」になる。上限は電球際だけに効く値（4.0）
- 点光源を電球の金具の中に置くと金具が真っ白に飛び、電球が「8」の字に見えた。金具は `_LightGain=0` で影も落とさない
- 札の見え方の判定（`PrepareFor`）は描画先を付ける前に走る。`Camera.aspect` を出力の縦横比にしないと 4:3 で計算され、画面内の札が消える
- 色収差は 0.35 で線と埃が虹色に割れた。0 にしてある
- URP の影の ShadowCaster を自前で書くときは `CommonMaterial.hlsl` を `Shadows.hlsl` より先に include（`LerpWhiteTo` が無い）
- ⚠ 代役（`VisitorAvatar`）は URP/Lit。法線マップは `_NORMALMAP` キーワードが無いと読まれず、プロパティブロックでは立てられない。
  4 回「服の皺が見えない」と言われ続けた原因。`SetActor` がマテリアルにキーワードだけ立てる
- URP の `_CameraDepthTexture` は描画の外から `Shader.GetGlobalTexture` で引けない（null）。被写界深度の深度は
  MSAA なし・`requiresDepthTexture=false` でもう 1 度描き、描画先の深度を `RenderTextureSubElement.Depth` で読む
- 被写界深度はにじみの元を抜く前に掛ける。後だと電球のにじみだけがくっきり残る
- スマホの画面に 4:3 の映像を流すときは左右に黒帯（`_EmissionFit`）。引き伸ばすと別の画に見える。画面の点光源は白飛びした
- 逆光の画（rec）の灰色の人は、補助光より人の手前の霞が原因だった。頭と HMD の影は霞の中に縦の柱を作り、背中に筋が出る
- 静止画の撮り方の値（rec の距離・向き・霞・補助光・服の暗さ）は `-Set recfrac=…` 等で変えられる（`TwinStill` の `ArgFloat`）

**監査の推移**（同じ監査役を続投して基準を固定）: 51 → 58 → 67 → 71 → 74 → 77 → 80 → 85 → 88 → 89 → 92 → 93 → 94 → **95（v59・25 回目で合格）**。
合格は余裕ゼロ。残りは hero の錆（灰色のまま）・hero 右と rec 左の黒い空き・真上の HMD の板・close の腿の縁。
監査役の推す掲載順は rec → close → low（演出の判定ではないので canon に入れない）

関連: [[virtual_walk]]
