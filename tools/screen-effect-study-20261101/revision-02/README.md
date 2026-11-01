# revision-02 の独立レポート

保存先は `G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/report.html`。旧レポートは変更しない。

`report.py` は全選択を引数で受け取る。入力欠損時は例外にする。別画像への代用はしない。各担当の制作と最終選択が終わるまで最終ビルドしない。

最終の目視確認用選択は静止画 `still/pass-08/B-pass08.png` と同opaque、動画 `motion/pass-05/motion-revision.mp4`、空間 `spatial/coverage/coverage-preview.html`。静止08の主観点は `audits/static08.json` の74点。選択動画の新しい独立点は、ユーザーの終了指示により未取得。85点到達を保証する完成版ではなく、本編採用は未判定。

固定した明示引数は `build-selected.ps1` に保存した。再生成は `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/screen-effect-study-20261101/revision-02/build-selected.ps1`。この中で `report.py` → `check-report.mjs` を実行する。その後、親の `verify-selected.ps1` を実行する。QAを重複して実行する必要はない。

world再生成前は、Drive `revision-02/inputs/show-calibration-source.json` をC側のgitignoreされた `tools/screen-effect-study-20261101/revision-02/spatial/snapshot/show.json` へ復元する。ローカルhostを含むこの入力はGitへ追加しない。

静止画は `--still-dir`（manifest/verificationの場所）、`--still-image`（選択RGBA）、`--still-view`（選択opaque）、`--still-preview`（HTML）、`--still-base`（加工前opaque）をすべて明示する。`--still-native <PNG>` は原寸比較画像ごとに繰り返す。manifestの`output_sha256`が選択RGBAと一致しなければ失敗する。coverage全項目とverificationをその候補から表示する。他候補の保存則や画素数を転用しない。

空間の `--spatial-dir` は共通のcamera/assets/source-manifestがある場所。`--spatial-variant-dir` は選択したcoverage版のmanifest等の場所。`--spatial-preview`、`--spatial-verification`、繰り返し可能な `--spatial-image` へ実在する選択ファイルを明示する。主選択はworld局所変形→固定camera投影→計測2D相似→1.35crop/固定glass。純粋な3Dカメラ復元ではない。輪郭付近12pxで局所振幅を抑える制約を本文に明記する。

```powershell
$study = 'G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study'
# report.py --helpで必須引数を確認し、最終選択の実在パスをすべて渡す。
py -3.11 tools/screen-effect-study-20261101/revision-02/report.py --help
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# 完成版report.htmlの生成後に実行する。
node tools/screen-effect-study-20261101/revision-02/check-report.mjs "$study/revision-02"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

独立監査が完成したら `--audit <raw.json>` を各原データごとに追加する。`selected_candidate_path` と `candidate_sha256` を実物に厳密照合する。別候補への監査も対象path/hashを明記して表示するが、主選択の点数にはしない。点数は自動で足したり平均しない。主選択が未取得なら空欄にする。85点未達は未達と表示する。旧83点・85点を今回の評価に転用しない。

比較manifestの契約は `entries:[{time,reference:{path,actualTime,sha256?},revision:{path,actualTime,sha256?}}]`。pathは絶対またはmanifestからの相対。最低でも3秒付近と5秒付近の原寸フレームが必要。参照動画は元PTSを併記する。

`report-manifest.json` は使用入力と出力のSHA-256を記録する。`evaluation_target_hash` は静止RGBA/opaqueと今回のMP4。監査の対象ハッシュ・使用ソースハッシュは独立監査原データと照合する。

QAは自分専用のheadless Chrome、新規profile、自動割当portを使う。既存browser/Unity/serverへ接続・停止しない。動画metadata、0秒開始、3〜5秒の反復、停止後の反復解除、11秒再生、原寸表示、旧/新/元切替を検査する。375px/1400pxの明暗を撮影し、横overflow・壊れた画像・リンク・runtime例外を検査する。`report-check.json`と`report-*-QA.png`を同じDriveフォルダへ出す。
