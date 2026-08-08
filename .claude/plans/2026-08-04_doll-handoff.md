# 実物の日本人形 → CG 人形（ShowActor）

2026-08-04。買ってきた市松人形を写真 4 枚から CG 化し、廻リ視の映像へ合成して腕を動かすまで。
**髪が髪に見えない問題は解消した**（同日の後半）。以下は完了記録と、残っている限界。

## いまの状態

- 成果物: `Assets/Art/Models/Doll/Ichimatsu.fbx` / `Assets/Resources/ShowActors/Ichimatsu.prefab`
  （全高 0.41m・Generic・ボーン 8 本）
- 生成の一式と元写真: [tools/doll-model/](../../tools/doll-model/README.md)（README に手順・罠・今回の記録）
- 見た目の証拠: `Assets/Screenshots/doll/hi_{front,side,q45,top,back}.png`（頭だけフル解像度）。
  **`hi_*_before.png` が修正前**。並べると差が分かる（⚠ git 管理外のディレクトリなので
  before は再生成できない。消さないこと）

**確認済み**: 腕がハンドトラッキングのボーンで動く / 袖が付け根から腕に追従して下端は垂れる /
部位ごとに光り方が違う / Unity で正しいテクスチャ・寸法で描画される（EditMode 977/977 pass）

## 髪の件で何が壊れていたか

**一度に 4 つ壊れていて、どれも単独では直らなかった。** 詳細と実測値は
[tools/doll-model/README.md](../../tools/doll-model/README.md) の「髪が『角の丸い茶色い箱』に
見えていた件」に置いた。要点だけ:

1. `synth_hair()` が実写の髪を縦縞へ置き換えていた（しかもマスクが幾何的に破綻していて、
   縞は**背景と額**に塗られ、本物の後頭部の髪はそのまま残っていた）
2. 前後の継ぎ目が**頭ではブレンドされていなかった**（頭のシルエットは列 167〜346 なのに、
   混ぜていたのは列 0..72 / 440..512 ＝ 重なりゼロ）
3. UV が平面投影なので**側面にテクセルが回っていなかった**（弧長の 38% に対し列は 3%）
4. **頭の断面が本当に箱だった**（テクスチャではなく形。固定 `body_half`=54mm に対し
   頭の半幅は 42〜47mm なので、楕円のてっぺんしか使っていなかった）

あわせて、`hair_at` が赤い着物を髪と誤判定して**胴の奥行きが 1.22〜1.26 倍に膨らんでいた**
のも直した（正面以外のどの角度でもシルエットが太かった）。

## 構造の変更

- **[`geom.py`](../../tools/doll-model/geom.py) を新設し、幾何の式を 1 本にした。**
  `shell.py`（メッシュ）と [`bake.py`](../../tools/doll-model/bake.py)（テクスチャ）が同じ関数を呼ぶ。
  テクスチャは「モデルのこの点が写真のどこに写っているか」を知らないと焼けないので、
  両者が別々に式を持つと沈黙して食い違う
- **テクスチャは 4 枚からの投影ベイクになった。** sideA/sideB は奥行きを測るだけで
  テクスチャには一度も使われていなかった ＝ **側面に実データが 1 画素も無かった**
- `shell.py` が `texture.py` の出力を読む**隠れた帰還路**を消した（焼き直すたびに形が変わっていた）

## 残っている限界（この作り方では消せない）

- **横から見た顔の輪郭（鼻・唇）は出ない。** 殻は正面シルエットの押し出しなので鼻が無い
- **毛先が輪郭からほつれる感じは出ない。** シルエットは滑らかなまま
- どちらも 640×480 の暗い映像では観測されない。気になるなら殻ではなく別の作り方が要る

## 走らせ方

```bash
cd tools/doll-model && cp photos/*.jpg .
py -3.11 cutout2.py && py -3.11 profile.py && py -3.11 shell.py && py -3.11 texture.py
"/c/Program Files/Blender Foundation/Blender 4.1/blender.exe" --background \
  --python assemble.py -- "$(pwd -W)"
cp Ichimatsu.fbx doll_albedo.png doll_normal.png ../../Assets/Art/Models/Doll/
```

Unity 側は MCP の `execute_code` で `AssetDatabase.ImportAsset`（プレハブは FBX を参照して
いるので自動で更新される）。**テクスチャの importer は `npotScale = None`** にしてある
（既定の `ToNearest` だと 1024×1184 が 1024×1024 へ縮み、縦の解像度を 13.5% 捨てる）。

## 踏んだ罠

- **コンパイルエラーが 1 個あると、Unity は古い DLL のまま動き続ける。**
  `ShowActorPrefabBuilder.cs` に CS0136（変数名の衝突）が**コミット済みで入っていた**ため、
  `Preview Show Actor` を叩いても前の版が走り、選んだ人形ではなく既定の Remy が出続けた。
  メニューは「成功」を返すので気づけない。→ **メニューを叩く前に `read_console types=["error"]` で 0 件を確認する**
- **`Selection.objects` は同じ `execute_code` 呼び出しの中では効かない。** 選択を設定する
  呼び出しと、それを読むメニュー実行は**分ける**
- **`head_check.py` のライトは距離の 2 乗で効く。** `render_check.py` の値を頭へ寄せて使うと
  約 19 倍明るくなり、暗い髪が真っ白に飛んで**何も判定できない**（最初にこれをやった）
- **Unity が非フォーカスだと C# を再コンパイルしない。** `refresh_unity` の後は
  `Library/ScriptAssemblies/*.dll` の mtime を見る
- **FBX を Unity へコピーし忘れると、1 世代前を見て議論することになる**
- 人形は Unity の慣習どおり**顔が +Z を向く**。撮影カメラは +Z 側に置く

## ⚠ 判定の規律

[.claude/rules/visual-verification.md](../rules/visual-verification.md) **§10 を先に読むこと。**
今回も 2 回、**縮小した画像・中間の理屈で「直った」と思いかけた**（縞を消したら別の縞が出た /
弧長 UV にしたら側面に顔が回り込んだ）。どちらもフル解像度で 1 枚ずつ見て気づいた。

判定に使う道具は揃えてある:

```bash
blender --background --python head_check.py -- <dir>   # 頭だけ 1000px × 5 角度
py -3.11 compare_head.py side                            # 実物写真と横並び
```

**並置画像は「どこを見るか」を決めるためだけに使う。判定は 1 枚ずつフル解像度で。**

## 触らなくていいもの（決着済み）

袖の厚みと腕の位置（貫通は解決・実測でクリアランス 13.6mm vs 腕半径 6.0mm）/ 胴の断面 /
部位ごとの光沢マスク / 足元の切り詰め / 法線の再計算を外した件
