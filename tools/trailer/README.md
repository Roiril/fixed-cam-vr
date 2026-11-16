# tools/trailer — 予告動画の組み直し

予告動画（廻リ視 トレイラー）を、**構成と音は旧版（v3-3）のまま、画を今の本編に差し替えて**組み直す道具。
体験者の画と三人称は、同じ走行の同じ瞬間から取る（体の位置が画と対応する）。切れ目は旧版の音の拍に置く。

| ファイル | 役目 |
|---|---|
| `edl.json` | 編集表。区間ごとに出来上がりの時刻 `t`・種類 `kind`・素材の秒 `src`・左下の札 `label` |
| `render.py` | 編集表どおりに組み、旧版と並べた確認用の動画と一覧も作る |
| `route_sync.py` | 走行の各秒に、体験者が道の何コマ目に居たかを引く（記録のコマと束のコマをハッシュで照合。誤差なし） |
| `tp-shots.json` | 三人称の構図（区画ごとに 1 台。`twin_render.py --shotfile`） |
| `tp_sync.py` | 道に沿って焼いた三人称のコマを上の表で引き、目線の動画と同じ時計の `tp.mp4` にする |
| `mjr2mp4.py` | 体験の記録（xprec）のカメラ 3 台の生映像 `cam_*.mjr` を、目線の動画と同じ時計の mp4 にする |
| `ui-shots.cjs` / `ui-shots.json` | 卓（web compositor）の画面をヘッドレス Chrome で 1920×1080 の静止画にする |

## 組み直す

```powershell
py -3.11 tools/trailer/render.py                # 全部組んで Drive へ置く（10 分前後）
py -3.11 tools/trailer/render.py --only 19,20   # その番号の区間だけ組み直す（他は前回の seg/ を使う）
py -3.11 tools/trailer/render.py --no-publish   # Drive へ写さない
```

出力は `Logs/trailer/<日時>/`。`<out_name>.mp4` が出来上がり、`compare.mp4` が旧版（左）と新版（右）の並び、
`pairs.jpg` が区間ごとの真ん中のコマの並び。区間の番号は `pairs.jpg` の左上に出る。
Drive には `G:\マイドライブ\shubie\renders\trailer\<日時>\` と、`edl.json` の `publish` に書いた場所へ置く。

## 素材を作る（本編が変わったとき）

1. **目線の動画**: ツインの通し走行を、体験の記録つきで回す。画角 40 度でテレビが画面の真ん中に大きく入る

   ```powershell
   py -3.11 tools/twin-tree.py run -- --calib twin --marks 1A,1B,2A,3B --fov 40 --keep-rec --tag trailer-fov40
   ```

   出力は `G:\マイドライブ\shubie\renders\twin-verify\<日時>_trailer-fov40\`。`edl.json` の `run` をそのフォルダ名にする
2. **カメラ 3 台の生映像**: 走行の記録（`%USERPROFILE%\AppData\LocalLow\DefaultCompany\mawarimi-replay-fcv-twin-tree\xprec\<日時>\cam_Phone0N.mjr`）を mp4 にして、走行のフォルダへ置く。
   `--lead` は `replay_xp.log` の最初の `ev=sum` の `worst`（ミリ秒を秒に）

   ```powershell
   py -3.11 tools/trailer/mjr2mp4.py <xprec>\cam_Phone01.mjr <走行のフォルダ>\cam1.mp4 --lead 1.80 --dur 341
   ```
3. **三人称**: 道に沿って構図ごとに焼き（1280 幅で 1 コマ約 3 秒）、走行の時計へ引き直して走行のフォルダへ置く。
   部屋や道を変えていなければ、焼いたコマは使い回せる（`route_sync.py` と `tp_sync.py` だけ回す）

   ```powershell
   & "C:\Program Files\Blender Foundation\Blender 5.1\blender.exe" --background --factory-startup --python tools/twin/twin_render.py -- seq --route forward --shotfile tools/trailer/tp-shots.json --cams tp_front,tp_west --w 1280 --spp 64 --plate-spp 384
   py -3.11 tools/twin/seq_post.py forward --cams tp_front,tp_west
   py -3.11 tools/trailer/route_sync.py <xprec>\cam_Phone01.mjr <走行のフォルダ>\route_index.json --lead 1.31 --dur 293
   py -3.11 tools/trailer/tp_sync.py <走行のフォルダ>\route_index.json <走行のフォルダ>\tp.mp4
   ```
4. **卓の画面**: 卓を立てて（`tools/web-compositor/serve.ps1`）撮る

   ```powershell
   node tools/trailer/ui-shots.cjs --out Logs/trailer/ui
   ```
5. **編集表の秒を直す**: 走行が変わると出来事の秒が動く。`src` は目線の動画の秒。
   出来事の秒は `tools/fx-library/build.py` と同じ読み方（`replay_xp.log` の `tau` − 最初の詰まり）で引ける

## 区間の種類

| kind | 中身 |
|---|---|
| `old` | 旧版をそのまま使う（ロゴ・題字・「システム」「演出」の札・三人称の実写・エンドカード） |
| `eye` | 目線の動画。`crop` は `tv`（テレビを 16:9 で切る）か `full`（画面外の演出が見える全体） |
| `black` | 黒（旧版で音だけ鳴る間） |
| `system` | 三人称にカメラ 3 台の位置の矢印（`rig.json` の位置を構図へ投影して引く） |
| `tppip` | 体験者の画（左）と同じ瞬間の三人称（右）。頭で三人称が中央から右下へ縮む |
| `pip` | 旧版の実写を土台に左の画だけ替える（v4 の組み方。体の位置が合わないので使わない） |
| `multicam` | 3 台の生映像・体験者に見えている映像・体験エリアの図。いま映っているカメラが走行の区間の切り替わりと同じ時刻で移る |
| `chain` | ① カメラの生の映像 → 体験者に見える画（行ごと） |
| `gen` | ② 種 → AI が出したもの → 体験者に届く画（同梱素材 `Assets/StreamingAssets/show/assets` から） |
| `tvpip` | ③④ 目線の動画の右上に、同じ瞬間の三人称の小窓 |
| `mosaic` | ⑧ 卓の画面 4 枚 |

## 罠

- **切れ目を旧版と揃えるだけでは音と合わない。** 旧版の音は 4.87 秒から 1 秒おきの刻み、15.39 秒から約 2 秒おきの山を持つ。
  カットは刻みに、異変の出る瞬間は山に置く。「旧版の音とのずれ 0ms」は同じ音を載せれば必ず出る数で、画と音が合っている証拠にならない
- **走行を撮る前に検証用の木を揃える**（`twin-tree.py sync`）。v4 は直す前のコードで撮っていた
- **体験者の画と三人称は同じ走行から取る。** 別の日の実写と並べると、立ち位置も動きも合わない
- ②の種と AI の画はツインの部屋の版（`tools/twin/assets`・`plates`）。走行の画はこちらで合成されている
- 確かめるのは区間の真ん中の 1 コマではなく、0.5 秒刻みの並び

- 目線の動画は、時計（`tau`）より最初の詰まりの分だけ遅れて始まる。動画の秒 = `tau` − 最初の `ev=sum` の `worst`
- 区間の継ぎ目はコマ番号で数える（`round(t*60)`）。各区間のコマ数が合わないと `render.py` が止まる
- G: へ直接 mp4 を書かない（Drive が書き込み途中から上げ始めて、faststart の書き直しで消える）。手元で作ってから写す
- `ui-shots.cjs` の `"full": true` は 1 画面より下が白く被る。動画に使う画は画面 1 枚分で撮る
