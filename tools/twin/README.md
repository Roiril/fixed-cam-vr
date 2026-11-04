# デジタルツイン（Cycles）

部屋・カーテンの壁・棚・固定カメラの機材・体験者の代役を Blender Cycles で組み、写真に見える静止画と、
固定カメラの動画を作る。ビューアは [../twin-game/](../twin-game/)。

本番の演出ロジックは持たない。**歩く経路とカットは Unity の仮想体験（`menu virtualwalk`）の記録を読むだけ**で、
Director の二重実装を作らない。経緯と罠は [memory/twin_cycles.md](../../.claude/memory/twin_cycles.md)。

## 回し方

```powershell
py -3.11 tools/twin/check_rig.py                                   # 機材の配置を数で検査（1 秒）
py -3.11 tools/twin/shoot.py --tag x --shots hero,wide,low,close,rigA,rigB,rigC,bulb,wall,A,B,C --w 1920 --spp 384 --person 1 --time 5
py -3.11 tools/twin/audit_pack.py "<撮影フォルダ>"                   # 盲検の束を作る
py -3.11 tools/twin/audit_score.py "<監査フォルダ>" <監査役の JSON>   # 点を出す
```

動画は 1 コマ約 2 秒（RTX 4090 Laptop）。

```powershell
& "C:\Program Files\Blender Foundation\Blender 5.1\blender.exe" --background --factory-startup --python tools/twin/twin_render.py -- seq --route forward --cams A,B,C --w 960 --spp 48 --plate-spp 512
py -3.11 tools/twin/seq_post.py forward     # 仕上げて Drive の renders/twin-cycles/game/forward/<cam>.mp4 へ
py -3.11 tools/twin/export_game.py          # ビューアの data/ を更新
```

## ファイル

| ファイル | 役割 |
|---|---|
| `rig.json` | **機材の配置の正本**（企画書 `paper/dcexpo` 2 節と図 1・図 4）。Unity の `TwinRig` も読む |
| `routes/*.tsv` | Unity の仮想体験の記録（30Hz に間引き）。`frames.tsv` が元 |
| `twin_scene.py` | 部屋・カーテン・棚・三脚・床置き台・アーム・電球 |
| `twin_person.py` | 代役。黒い長袖とカーゴ・毛の髪・Quest 3。歩きと右手の手すり |
| `twin_materials.py` | 材質。`textures/` の素材写真と手続きの組み合わせ |
| `twin_post.py` | 写真の仕上げ（自動露出・レンズのかぶり・色ずれ・ノイズ・シャープ・JPEG） |
| `twin_render.py` | Cycles の設定と撮影。`still` と `seq` |
| `prep_textures.py` | 生成した素材写真のつなぎ目を消す。生成元は Drive `renders/twin-cycles/textures-src` |
| `audit_prompt.md` | 監査の文面（基準を動かさないため固定） |

## 配置の決め方

| カメラ | 企画書 | 置き方 |
|---|---|---|
| A | 「カメラAは三脚上」 | 図 4 の位置。高さ 0.94m |
| B | 「カメラBは床上」 | 図 4 より 0.32m 外（経路と足が当たらないため）。黒い台に立てて見上げる |
| C | 「カメラCは棚に取り付けたアーム」 | 図 4 の位置。棚の上段の前梁へクランプ、高さ 2.12m |

図 1・図 4 は C のアームが L の柱から出ているように描いているが、本文は棚で、実プレート C にも棚が写るので棚に従った。
`show.json` の calib（本番の合成に使う姿勢）は別物で、触らない。
