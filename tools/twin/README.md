# デジタルツイン（Cycles）

部屋・カーテンの壁・棚・固定カメラの機材・体験者の代役を Blender Cycles で組み、写真に見える静止画と、
固定カメラの動画を作る。ビューアは [../twin-game/](../twin-game/)。

本番の演出ロジックは持たない。**歩く経路とカットは Unity の仮想体験（`menu virtualwalk`）の記録を読むだけ**で、
Director の二重実装を作らない。経緯と罠は [memory/twin_cycles.md](../../.claude/memory/twin_cycles.md)。

## 回し方

```powershell
py -3.11 tools/twin/check_rig.py                                   # 写真との差・歩く道・区画の切替を数で検査（数秒）
py -3.11 tools/twin/plate_fit.py --out <フォルダ> --write            # 写真からカメラの位置と画角を解き直して rig.json へ
py -3.11 tools/twin/shoot.py --tag x --shots hero,wide,low,close,rigA,rigB,rigC,bulb,wall,A,B,C --w 1920 --spp 384 --person 1 --time 5
py -3.11 tools/twin/audit_pack.py "<撮影フォルダ>"                   # 盲検の束を作る
py -3.11 tools/twin/audit_score.py "<監査フォルダ>" <監査役の JSON>   # 点を出す
```

動画は 1 コマ約 2 秒（RTX 4090 Laptop）。

```powershell
& "C:\Program Files\Blender Foundation\Blender 5.1\blender.exe" --background --factory-startup --python tools/twin/twin_render.py -- seq --route forward --cams A,B,C --w 640 --spp 48 --plate-spp 512
py -3.11 tools/twin/seq_post.py forward     # 仕上げて Drive の renders/twin-cycles/game/forward/<cam>.mp4 へ
py -3.11 tools/twin/export_game.py          # ビューアの data/ を更新
```

歩く道を作り直すとき（レイアウトの区画や枠を変えたとき）:

```powershell
.\tools\unity.ps1 menu virtualwalk -Set laps=1,route=all,path=rail,tag=rail,w=480
py -3.11 tools/twin/import_routes.py "G:\マイドライブ\shubie\renders\virtualwalk\<時刻>_rail"
py -3.11 tools/twin/check_rig.py
```

`Logs/twin-cycles/seq/<route>/` に古いコマと `plate.npy` が残っていると、`seq` はそれを再利用して焼き直さない。配置を変えたら先に消す。

## 合成素材（HMD なしの通し検証用）

本番の合成素材（1-B の手形・2-A のシミ・4-A の人形群・3 周目の無人写真）は実際の部屋で作ってあり、
ツインの映像に重ねると別の部屋の画が貼られる。`tools/twin-verify.py` は既定で、ツインの部屋で作り直した素材へ差し替えて走る
（`--real-assets` で本番のまま）。本番の show.json と APK には入らない。

- 対応表は `assets.json`（cue の id → `assets/` の素材）。`make_assets.py` が show.json の今のファイル名へ当てはめて
  `Logs/twin-run/assets/` に並べ、Editor は環境変数 `FIXEDCAM_ASSET_OVERRIDE_DIR` が指すフォルダの同名ファイルを読む
  （`ShowAssetResolver.TryOverride`。Player は読まない）。読まれた本数は判定の頭に出る
- 静止画は `tools/gen-plate` の通常の手順で作る。場所は `sites/twin_A.json` / `twin_B.json`（無人写真は `plates/twin_*_plate.jpg`）
- 動画は Codex の画像生成で同じ構図のキーフレームを数枚作り（前のコマを入力にして少しずつ動かす）、`animate_keys.py` が
  オプティカルフローで中割りして 640x480・24fps の mp4 にする。キーの生成は暗くした種の上で行い、`gen-tone.py undim` で戻してから繋ぐ

```powershell
py -3.11 tools/twin/animate_keys.py --keys k0.png k1.png k2.png --move 1.2 1.2 --hold 1.0 0.4 0.2 --keep 0,0,330,480 --out assets/x.mp4 --sheet sheet.png
py -3.11 tools/twin/make_assets.py
```

⚠ 作り直した素材は本番の素材の代わりではない。強さや顔の描き方は本番と揃っていない（2-A のシミは本番より顔がはっきりしている）。
見るのは「ツインの部屋で、その場面が体験者にどう届くか」まで。

## ファイル

| ファイル | 役割 |
|---|---|
| `rig.json` | **機材の配置と部屋の寸法の正本**。Unity の `TwinRig` も読む。カメラの値は `plate_fit.py --write` が書く |
| `plate_obs.json` | 保存した無人写真で読んだ画素と、実物の枠の寸法 |
| `plate_fit.py` | 上の観測から、カメラの位置・向き・共通の画角を解く |
| `import_routes.py` | Unity の仮想体験の記録を `routes/` へ取り込む |
| `routes/*.tsv` | Unity の仮想体験（`path=rail`）の記録。手すりから 0.16m の道を歩く。30Hz に間引き |
| `twin_scene.py` | 部屋・カーテン・棚・三脚・床置き台・アーム・電球 |
| `twin_person.py` | 代役。黒い長袖とカーゴ・毛の髪・Quest 3。歩きと右手の手すり |
| `twin_materials.py` | 材質。素材写真を土台に、汚れは形から決める（AO の隅・出っ張った縁の欠け・上向きの面の埃・高さ・金具の下の筋） |
| `twin_post.py` | 写真の仕上げ（自動露出・レンズのかぶり・色ずれ・ノイズ・シャープ・JPEG） |
| `twin_render.py` | Cycles の設定と撮影。`still` と `seq` |
| `prep_textures.py` | 生成した素材写真から色（`<名前>.jpg`）と面の情報（`<名前>_s.jpg`。R 高さ・G 層・B 細かい明暗）を作る。缶のラベルと箱の印刷はコードで組む。生成元は Drive `renders/twin-cycles/textures-src`（`v2/` が追加分） |
| `make_pack.py` | HMD なしの通し検証（`tools/twin-verify.py`）へ渡す束。焼いた動画を周の印つきの MJR と無人写真へ変える |
| `assets.json` / `assets/` / `make_assets.py` | ツインの部屋で作り直した合成素材と、本番の cue への当てはめ（上の「合成素材」） |
| `animate_keys.py` | 生成したキーフレーム数枚をオプティカルフローで中割りして動画にする |
| `audit_prompt.md` | 監査の文面（基準を動かさないため固定） |

## 配置の決め方

位置・向き・画角は保存した無人写真から解く。B と C は `plate_*_20260924_*.jpg`、A は `plates/plate_A_20260905_193511.jpg`。実物の枠の寸法（`tools/walk-guide`）を基準にし、写真の点と線に枠が重なるようにする。

A だけ写真が古いのは、9/24 の写真では A が動いているため。本番の calib は 9/5 19:39 にこの写真で解いてある。1 台だけ解き直すときは `plate_fit.py --only A --write`（画角と他の台は変わらないので、その台の動画だけ焼き直せばよい）。

| カメラ | 機材 | 解いた置き方 |
|---|---|---|
| A | 三脚 | L の角を正面に見る。高さ 0.93m |
| B | 床 | 西の壁のすぐ脇。レンズは床から 3cm で、上へ 13 度あおる |
| C | 棚のアーム | L の北面を上から見る。高さ 1.87m。横へ 30 度傾いている |

画角は 3 台共通で水平 96 度。`show.json` の `hfovDeg` 77.4 と calib（本番の合成に使う姿勢）は別物で、ここでは使わず触らない。
企画書の図 1・図 4 は生成画像に説明線を足したもので、寸法を測らない。
