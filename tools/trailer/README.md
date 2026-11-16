# tools/trailer — 予告動画の組み直し

予告動画（廻リ視 トレイラー）を、**構成と時刻は旧版のまま、体験の画だけ今の本編に差し替えて**組み直す道具。
音も旧版のものをそのまま載せる。

| ファイル | 役目 |
|---|---|
| `edl.json` | 編集表。区間ごとに出来上がりの時刻 `t`・種類 `kind`・素材の秒 `src`・左下の札 `label` |
| `render.py` | 編集表どおりに組み、旧版と並べた確認用の動画と一覧も作る |
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
3. **卓の画面**: 卓を立てて（`tools/web-compositor/serve.ps1`）撮る

   ```powershell
   node tools/trailer/ui-shots.cjs --out Logs/trailer/ui
   ```
4. **編集表の秒を直す**: 走行が変わると出来事の秒が動く。`src` は目線の動画の秒。
   出来事の秒は `tools/fx-library/build.py` と同じ読み方（`replay_xp.log` の `tau` − 最初の詰まり）で引ける

## 区間の種類

| kind | 中身 |
|---|---|
| `old` | 旧版をそのまま使う（ロゴ・題字・「システム」「演出」の札・三人称の実写・エンドカード） |
| `eye` | 目線の動画。`crop` は `tv`（テレビを 16:9 で切る）か `full`（画面外の演出が見える全体） |
| `pip` | 旧版のコマを土台にし、左の体験者の画だけを差し替える。三人称の実写の動きは旧版のまま |
| `multicam` | 3 台の生映像・体験者に見えている映像・体験エリアの図。いま映っているカメラが走行の区間の切り替わりと同じ時刻で移る |
| `chain` | ① カメラの生の映像 → 体験者に見える画（行ごと） |
| `gen` | ② 種 → AI が出したもの → 体験者に届く画（同梱素材 `Assets/StreamingAssets/show/assets` から） |
| `tvpip` | ③④ 目線の動画の右上に、旧版の同じ時刻の三人称の小窓 |
| `mosaic` | ⑧ 卓の画面 4 枚 |

## 罠

- 目線の動画は、時計（`tau`）より最初の詰まりの分だけ遅れて始まる。動画の秒 = `tau` − 最初の `ev=sum` の `worst`
- 区間の継ぎ目はコマ番号で数える（`round(t*60)`）。各区間のコマ数が合わないと `render.py` が止まる
- G: へ直接 mp4 を書かない（Drive が書き込み途中から上げ始めて、faststart の書き直しで消える）。手元で作ってから写す
- `ui-shots.cjs` の `"full": true` は 1 画面より下が白く被る。動画に使う画は画面 1 枚分で撮る
