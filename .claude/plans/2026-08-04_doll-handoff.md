# 引き継ぎ：実物の日本人形 → CG 人形（ShowActor）

2026-08-04。買ってきた市松人形を写真 4 枚から CG 化し、廻リ視の映像へ合成して腕を動かすまで。
**動く状態まで到達しているが、髪が髪に見えない。** そこが唯一の未解決。

## いまの状態

- `master` = `30afbab`、origin へ push 済み。未コミットは人形と無関係のもの（`OVRBuildConfig.asset` 等）だけ
- 成果物: `Assets/Art/Models/Doll/Ichimatsu.fbx` / `Assets/Resources/ShowActors/Ichimatsu.prefab`
  （全高 0.40m・9954 頂点・Generic・ボーン 8 本）
- 生成の一式と元写真: [tools/doll-model/](../../tools/doll-model/README.md)（README に手順と罠）

**動くと確認済み**（Unity で描画して確認）:
腕がハンドトラッキングのボーンで動く / 袖が付け根から腕に追従して下端は垂れる /
部位ごとに光り方が違う（白磁は鋭く強い・絹は広く弱い）/ 実際の合成条件（640×480・暗い）で
実物写真と区別がつかない

## 未解決：髪が髪に見えない

**これが唯一の残作業。** 側面から見ると角の丸い茶色い箱で、前後の境界線もはっきり残る。
正面も前髪の生え際が無く、額に縦縞が乗っているだけ。フル解像度で見れば一目で分かる:

```
Assets/Screenshots/doll/hi_front.png   （正面・頭だけ 1000x1100）
Assets/Screenshots/doll/hi_side.png    （真横・同上）
```

### 原因（調べて分かっていること）

**テクスチャの中身ではなく UV の構造の問題。**
このモデルは「正面写真のシルエットを前後へ押し出した殻」に**平面投影 UV**を張っている
（`u = 正規化した x`, `v = 正規化した z`）。したがって:

- 側面（メッシュの左右の縁）の頂点は u が 0 または 0.5 に張り付く
- そこは正面テクスチャの**左端 1 列**にあたる
- **その 1 列が側面全体に引き伸ばされる**

何を描いても側面は縞にしかならない。手続きの髪テクスチャを入れても直らなかったのはこのため。

### やってみて効かなかったこと（繰り返さないために）

- 前面と背面のクロスフェード（`texture.py` の `blend_seam`）→ 二重像になるだけ
- 前後の露出・色かぶり合わせ → 色差は消えたが構造は変わらない
- **髪を一方向の手続きテクスチャに置き換え、前後で同じノイズを使う**（現在の実装）
  → 繋ぎ目の明暗差は消えたが、前後がまったく同じ縞になって**かえって不自然**。
    側面の引き伸ばしは残ったまま

### 直す方針

**髪の部分だけ UV を円筒にする。** 押し出した殻という構造は保ったまま、髪の領域の頂点の u を
`atan2(y, x)` ベースに置き換え、それに合う髪テクスチャをアトラスに別枠で焼く。
側面でも縞が正しい向きに出る。

触る場所:
- `tools/doll-model/shell.py` … 髪の頂点（`hair_at(uu, vv) > 閾値`）の UV を円筒へ
- `tools/doll-model/texture.py` … アトラスに「髪タイル」の枠を足す（下段の腕ストリップの隣か、縦に拡張）
  ⚠ アトラスの高さを変えると `atlas.json` の `body_v0` が変わり、`shell.py` の v が連動する。**対で直す**

代案（未検討）: 髪を独立したメッシュにして円筒 UV を張る。形の自由度は上がるが、
いまの「写真 4 枚から全自動」の枠組みから外れる。

## 走らせ方

```bash
cd tools/doll-model && cp photos/*.jpg .
python cutout2.py && python profile.py && python texture.py && python shell.py
"/c/Program Files/Blender Foundation/Blender 4.1/blender.exe" --background \
  --python assemble.py -- "$(pwd -W)"
cp Ichimatsu.fbx doll_albedo.png doll_normal.png ../../Assets/Art/Models/Doll/
```

Unity 側は MCP の `execute_code` で `AssetDatabase.ImportAsset` → プレハブは FBX を参照しているので
自動で更新される（`Build Show Actor Prefab` の再実行は不要）。

## 踏んだ罠（README にも書いてあるが、特に効くもの）

- **Unity が非フォーカスだと C# を再コンパイルしない。** `refresh_unity` も `Assets/Refresh` も
  `RequestScriptCompilation` も効かなかった。**シェーダとアセットの ImportAsset は効く**ので、
  C# を変えずに済む方法（`execute_code` で直接処理する）を選ぶと詰まらない
- **MCP は重い処理で timeout する。** `SaveAndReimport` や FBX の ImportAsset を他の処理と
  同じ呼び出しに入れない。分ける。timeout しても Unity 側では実行されていることがあるので、
  ファイルの mtime で確認してから再実行する
- **FBX を Unity へコピーし忘れると、1 世代前を見て議論することになる**（実際に踏んだ）。
  生成したら必ず `cp` してから撮る
- 人形は Unity の慣習どおり**顔が +Z を向く**。撮影カメラは +Z 側に置く

## ⚠ 判定の規律（今回いちばん重要な学び）

[.claude/rules/visual-verification.md](../rules/visual-verification.md) **§10 を先に読むこと。**

このセッションで、**繋ぎ目が消えたことを確認して「完了」と報告したが、髪に見えていなかった**。
ユーザーの指摘で気づいた。原因は縮小した並置画像で品質を判定したこと、差分（前より良くなったか）
だけを見て絶対（髪に見えるか）を見なかったこと、実物と並べなかったこと、工数を理由に打ち切ったこと。

完了と言う前に必ず: **フル解像度で対象だけを撮る / 実物と並べる / 「何に見えるか」を言語化する /
中間生成物そのものを開いて見る。**

## 触らなくていいもの（決着済み）

袖の厚みと腕の位置（貫通は解決）/ 胴の断面（平面問題は解決）/ 部位ごとの光沢マスク /
前後の露出合わせ / 足元の切り詰め / 頭頂の球（丸くなった）/ 法線の再計算を外した件
