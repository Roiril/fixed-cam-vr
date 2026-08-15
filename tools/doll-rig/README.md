# 生成した 3D の人形にリグを付ける

画像から起こした市松人形の GLB（Tripo 生成・`doll 3d model.glb`）にボーンと重みを付けて、
`Assets/Art/Models/Doll/Ichimatsu2.fbx` として出す一式。成果物を使うのは
`Assets/Resources/ShowActors/Ichimatsu2.prefab`（`ShowActorPrefabBuilder` が作る）。

**写真から押し出す旧 Ichimatsu（[tools/doll-model/](../doll-model/README.md)）とは別系統。**
あちらは実物の写真が正・こちらは生成物が正で、どちらも残してある。

## 走らせ方

```bash
blender --background --python rig_doll.py -- <出力ディレクトリ> [--ratio 0.03] [--check]
```

`--check` を付けると検証ポーズ（T / 左手上げ / 左手下げ）と**重みそのものの絵**を焼く。
出力（`out/`）は git に入れない（再生成できる・4096 のテクスチャが 26MB ある）。

Unity 側:

```powershell
.\tools\unity.ps1 menu actor-prefab -Set model=Assets/Art/Models/Doll/Ichimatsu2.fbx
.\tools\unity.ps1 menu actor -Set actor=Ichimatsu2          # 4 ポーズ × 2 角度
.\tools\unity.ps1 menu actor-motion -Set actor=Ichimatsu2   # 到達率・追従の遅れ
```

テクスチャは 4096 で出るので、**Assets へ入れるときは 2048 へ縮める**（人形は 640×480 の
映像に小さく合成されるので 2048 でも過剰）。名前は `ichimatsu2_albedo.png` /
`ichimatsu2_normal.png` — `ShowActorPrefabBuilder` が `_albedo` → `_normal` で相方を探す。

## 押さえておくこと

- **元は 1,891,660 三角 / 977,741 頂点。** そのままでは Quest に載らないので
  Decimate Collapse で **ratio 0.03（56,748 三角 / 43,299 頂点）** へ落とす。
  ⚠ **平面シェーディングで比べると不当に厳しい判定になる** — 実機はスムーズ ＋ 法線マップなので、
  比較レンダも `shade_smooth()` と法線マップを掛けた状態で見ること（0.02 でも顔は保つが、
  手の指が崩れ始める）
- **Blender の +X が人形の左**。FBX 変換（`axis_forward='-Z'` / `axis_up='Y'`）で Unity の -X ＝
  人形の左に来る。`ShowActorRig` は名前（`LeftHand`）でボーンを探すので、ここが逆だと腕が体を横切る
- **ボーンのローカル Y 軸は腕の長さ方向**。そこを回してもねじりにしかならない。
  検証で腕を上下に振るときは、ワールドの Y 軸まわりの回転を rest 行列で挟んでボーン空間へ落とす
  （最初これで焼いて、**腕が 60° 回っているのに袖が 1mm も動かなかった**）
- **`HeadTop` ボーンを頭頂まで通す。** `ShowActorRig.MeasureHeight` はボーンの最高点を基本に
  身長を測るので、頭の中ほどまでしかボーンが無いと実寸を 3 割小さく見積もる（旧 Ichimatsu で
  2.27m の巨人になった）

### 袖の重み（3 回焼き直した）

**袖口へ近いほど / 腕の軸へ近いほど腕に付いてくる。** 垂れた袂は動かない
（ユーザー指示「布の下らへんのウェイトペイントを工夫して、下辺の位置はあまり変わらずに垂れる」）。

| 焼いて分かったこと | 直し方 |
|---|---|
| 落差（z だけ）で測ると、腕の**前後**にある布が同じ重みになる ＝ 腕を下げると手が布を突き抜ける | 距離は前後も入れて **3D** で測る |
| 袖の下端も \|x\| 0.37 まで張り出しているので、x だけで腕を判定すると**袂の外側が腕になる** | 腕は `x >= 袖口` **かつ** `腕の軸から 0.085m 以内` |
| 脇の遷移を胴の半幅まで引き伸ばすと、腕を包む筒まで重みが薄まって手が布を出る | 体に残すのは**脇そのものだけ**（0.10 → 0.17m） |
| 指数減衰だと袖口（腕に密着した輪）でも 0.64 までしか上がらず、手（1.0）との段差で**裂ける** | **ガウシアン**（近くは強く・遠くは急に落ちる）＋ 袖口へ向かって 1 へ漸近させる |

⚠ **重みそのものを絵にしてから判断する**（`weight_front.png` / `weight_side.png`）。
ポーズを付けた絵からは「動いた結果」しか見えないので、原因が重みか回転かを切り分けられない。

⚠ **腕の周りは必ず寄って見る**（`pose_*_zoom.png`）。全身の絵では布の破綻が数画素にしかならず、
上の 4 つのうち 3 つは寄って初めて見えた。
