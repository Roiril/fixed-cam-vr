# 実物の日本人形 → CG 人形（ShowActor）

買ってきた市松人形（高さ約 40cm・赤い着物・おかっぱ・白磁の顔と手）を、写真 4 枚から
CG 人形にする一式。成果物は `Assets/Art/Models/Doll/Ichimatsu.fbx` と
`Assets/Resources/ShowActors/Ichimatsu.prefab`。

## なぜこの作り方か

- **フォトグラメトリは使わない。** RealityScan を試して精度が出なかった。原理的にも、
  髪（画素以下の太さ＋半透明）は対応点が立たず、白磁の顔もつるつるで特徴が出ない
- **AI の画像→3D も使わない。** 実物が手元にあるのに学習分布から「市松人形らしいもの」を
  作ることになる。加えてこの PC は VRAM 16GB で TRELLIS.2（24GB 必須）が動かず、
  Hunyuan3D 2.1 も形状は通るがテクスチャ生成（29GB）が通らない＝色が出ない
- **正面写真のシルエットをそのまま前後へ押し出す。** 輪郭が写真そのものになり、
  UV が平面投影なのでテクスチャが原理的にずれない。合成先は 640×480 の暗い映像で
  人形は数百画素なので、前後の凹凸は観測されない

## 走らせ方

```bash
cd tools/doll-model && cp photos/*.jpg .
python cutout2.py     # 4 枚から人形を切り抜く → mask_*.png
python profile.py     # マスクを仕上げて高さごとの寸法を出す → profile.json
python texture.py     # 前面/背面を並べた 1024x1024 のアトラス → doll_albedo.png
python shell.py       # 正面マスクを押し出して殻を作る → shell.json
"/c/Program Files/Blender Foundation/Blender 4.1/blender.exe" --background \
  --python assemble.py -- "$(pwd -W)"     # 腕・ボーン・FBX
```

確認用（どちらも Blender・Play 不要）:

```bash
blender --background --python render_check.py -- <dir>   # 多角度
blender --background --python pose_check.py   -- <dir>   # 腕を振って袖が動かないこと
```

Unity 側:

1. `Ichimatsu.fbx` と `doll_albedo.png` を `Assets/Art/Models/Doll/` へ置く
2. FBX の Rig を **Generic**（Humanoid にしない）
3. FBX を選んで `Tools/FixedCamVr/Setup/Build Show Actor Prefab`
4. できたプレハブを選んで `Tools/FixedCamVr/Diagnostics/Preview Show Actor`

## 押さえておくこと

- **腕は写真に写っていない**（白磁の腕は袖の外にあり、切り抜きで落ちる）。CG 側で円柱として
  作り、UV は顔の白磁の位置へ固定している
- **袖と身頃は Root ボーンに 1.0**。`ShowActorRig` は上腕ボーンそのものを回すので、
  袖を肩に付けると腕を振るたび袖が丸ごと回る。着物の袖は実物でも上腕に追従しない
- **腕メッシュは頂点グループの印で分離する**。座標だけで判定すると、前後に薄い袖の頂点が
  「腕の筒の中」に入って一緒に持ち上がる（実測で袖の上端が山形に引っ張られた）
- **顔と机は色で区別できない**（顔 H22 S25 V207 / 机 H40 S11 V210）。髪と黒シャツも同じ。
  確実に取れるのは赤い着物だけ（S217）なので、頭部は**楕円で塗る**。GrabCut を頭に掛ける案は
  矩形をわずかに動かすだけで「頭が消える／頭上に黒い柱が生える」を往復して収束しなかった
- **最大連結成分だけ残してはいけない**。背面は帯（白い蝶結び）が頭と胴を分断するので、
  頭か袖の片方しか生き残らない
- 寸法の目測値は `cutout2.py` の `HEAD`（頭部楕円）だけ。ほかは写真から自動で測っている

## 撮り直すときの条件

人形に触れない（ポーズが変わると同一形状として扱えない）／背景に模様のあるものを敷く／
影を消す（テクスチャに焼き込まれた影はシェーダの陰影と二重になる）／正面は T ポーズ。
