# スクリーンの追加加工と動画演出の試作

2026-11-01の依頼に対する独立試作。現在のスクリーン加工を含むUnity実描画を入力に使う。本編のソース・シーン・設定を変更する処理はない。

生成物の保存先: `G:\マイドライブ\shubie\reports\2026-11-01_screen-effect-study\`。

## 入力の固定

- 元スクリーンショット: `C:\Users\kouga\Downloads\Screenshot_20261101-004612.png`。
- 元動画: `C:\Users\kouga\Downloads\screen-20261101-004528.mp4`。
- 参照2点のバイトコピーとSHA-256: 保存先の `sources/` と `sources/manifest.json`。
- 初期入力: 保存先の `still/base.png`。SHA-256は `221da6ca07392595c4c04ef15f12afba28f09c8f95a3c02459df42f695f41e7a`。出所は `G:\マイドライブ\shubie\reports\2026-10-30_crt-reference\crt-tuner\screen-only.png`。
- 11版目以降の現行入力: 別スレッドが確定した `G:\マイドライブ\shubie\reports\2026-11-01_screen-adoption\gpu-complete\{A,B,C}_adopted.png` を `still/round-11/base-{A,B,C}.png` へ固定。出所とハッシュは `input-and-processing-manifest.json` に記録。確定後のファイルを読み取るだけで、元のファイルを更新しない。
- 追加加工の主比較はカメラB。固定入力BのSHA-256は `db21e8bfa1fa41d80237a46ab9567bfaefea49c7206c46d37f3998474003a2b9`。カメラの変更による点数の差と、加工の変更による差を同一視しない。

元のPNGの透明領域には筐体のRGBが残っている。画像ビューアによっては枠が見えるが、alpha合成した `base-on-black.png` が実際の開口の比較用画像。透明域のRGBを比較画像として露出しない。

## 静止画

実行フィルタは27版を試作し、最高評価は第23版 standard の83%。85%には達していない。元の依頼は試作とレポートであり、任意入力へ再利用できる実装を完了条件にするのは追加条件だったため、その後は画像編集による見た目の試作も別に作った。画像編集版の点数を実行フィルタの点数として扱わない。

画像編集4版の評価は74/76/77/77%。いずれも形状保持はtrueで、実行フィルタの最高83%を超えなかった。`still-mockup/round-NN/` に正確なImageGen指示文、入力一覧、生成直後の画像、整形後のPNG、処理記録を保存する。元のCRT描画と比べた形状保持を先に判定し、保持できた場合のみ加工感を採点する。入力は同じカメラBで固定する。

画像編集版の整形は次のコマンド。見た目の変更は内蔵image_genが行い、このスクリプトは画像サイズと元の開口を整える。既存の候補は上書きしない。`--register` は生成時に生じた小さな全体の回転・倍率・位置のずれを、下地との特徴点対応から戻す場合だけ付ける。局所的な形状変更や物体の差し替えは行わない。

```powershell
py -3.11 tools/screen-effect-study-20261101/prepare-mockup.py --generated '<ImageGenの出力PNG>' --out '<Driveの新規roundフォルダ>' --register
```

指定する新規フォルダには先に正確な `prompt.txt` と `generation-request.json` を保存する。`generated-raw.png` は生成直後のバイトコピー。`mockup.png` は元のalphaを維持した版、`mockup-on-black.png` は比較へ渡す不透明版。`preview.html` は現行描画・実行83%・画像編集版の切替であり、ライブフィルタではない。

`still/` は既存CRTへ重ねる追加処理。初期8版は `effect*.js`、9版目以降は `offline-round*.py` が計算本体。後者は既存のOpenCVとNumPyで入力の明暗に応答する加工を計算する。各版のHTMLは実計算済み画像を内蔵する比較用で、静止PNGだけをフィルタ実装と見なしているわけではない。

初回版の再生成は次の順序。`build.py` は初回用なので、後続の版を生成するときは固定コピーを使う後続ビルダーを実行する。

```powershell
py -3.11 tools/screen-effect-study-20261101/still/build.py
node tools/screen-effect-study-20261101/still/verify-js.cjs 'G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/still'
```

11版目以降の入力は固定した現行のABC描画。元の画角とalphaを維持する。透明域の残留RGBを輪郭として検出しないよう、計算時だけ有効領域のRGBを延長する。最後に元のalphaと透明域RGBを戻し、開口境界の内側2pxは元のRGBを保つ。参照の青い色相とキャラクターは再現対象に含めない。

第23版を固定素材から別フォルダへ再生成する場合は次のとおり。`--out` に過去の採点済みフォルダを指定しない。入力や以前の測定値をDriveへ固定しているので、このコードだけでは再生成できない。

```powershell
py -3.11 tools/screen-effect-study-20261101/still/offline-round23.py --camera B --atlas-dir 'G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/still/round-23' --out '<Driveの新規出力フォルダ>'
```

第28版は参照由来の明暗残差を横方向に微分し、各行で和0の49tapフィルタへ変換して全細部へ畳み込む追加実験。入力の明暗差へ応答し、平坦面のDC応答は約1e-16、画素の変更は0。正負のstep入力で残差の符号と位置を照合する。第23版の輪郭track転写と混ぜていない。独立比較の結果を待って選択する。

## 動画

最終動画は `motion/round-07/alternate-b/motion-prototype.mp4`。独立比較でB85%・A83%となり、Bを選択した。BのSHA-256は `fc99f590045d482b99e5408ed4b6fe7596129199a26984e751d8b32fdd8b5601`。以後は動画の見た目を変更しない。

7版目の入力は静止画と同じ `still/round-11/base-{A,B}.png`。6版目で11月1日の採用描画へ揃えた。5版目以前の動画は開始時に固定した10月30日の描画を使っており、入力更新による知覚差も含むため、点数差を加工変更だけの効果とは扱わない。

専用のローカルサーバーを起動する。既存の8099・8107・8137は使わない。

```powershell
node tools/screen-effect-study-20261101/motion/serve.mjs
```

`http://127.0.0.1:8143/` で再生・シークできる。`window.motionStudy.renderAt(t)` は任意秒を決定的に描画する。背景の動きと侵食と粒子は別に切り替えられる。

```powershell
py -3.11 tools/screen-effect-study-20261101/motion/prepare_round.py
node tools/screen-effect-study-20261101/motion/capture.mjs
node tools/screen-effect-study-20261101/motion/capture.mjs --alternate
py -3.11 tools/screen-effect-study-20261101/motion/build.py
```

`prepare_round.py` は凍結した6版目の素材をSHA照合して複製し、7版目の透過帯を再生成する。コマ抽出・画素検査・素材・過去版の固定データはDrive上の出力フォルダを前提とする。再生成コマンドは同じ7版目の出力先へ書くので、確定済みの成果物は先にコピーして保存する。既に8143でこの試作サーバーが動いている場合は新しいサーバーを重ねて起動しない。

書き出しには既存の `C:/Users/kouga/.cache/remotion-chs/chrome-headless-shell.exe` とFFmpegを使う。新しい依存は追加していない。完成MP4は一時名へ書き、ffmpeg正常終了とffprobeの検査後に公開名へ置き換える。音声は含めない。

初期3版は計算で作る薄片・文字・粒と侵食を使用。4版目では参照から効果だけを分離した素材へ切り替える。文字のalpha、薄片の無彩色の形、侵食のスカラー場、単色に再構成した白と赤の粒を抽出する。元の部屋や人物を含む背景フレームを再生する方式は使わない。素材を単独で検品し、別の現行入力にも合成して確認する。

7版目は硬い型抜き形状を避け、幅5〜12pxの不均一な透過帯と小さな欠けで閉じる。残った背景だけの明るさ・コントラスト・ぼけも変える。背景の動きは実測した相似変換を駆動元に、見た目を合わせる透視変形を追加したもの。実測した3D運動とは呼ばない。13.6秒中央完全黒と14.4秒の侵食OFF対照差0を別々に確認した。

## 比較評価とレポート

実装担当と別のサブエージェントが参照と候補を比較する。目標点と前の採点と実装情報を渡さない。評価原文は保存先の `audits/*.json`。点数は主観的な類似度で、物理測定の誤差率ではない。

静止画はフル画像2枚を開いて比較する。動画は両MP4の時刻対応フレームと高密度の連続コマで比較する。動画の実時間再生をした評価としては扱わない。初回や不採用の版と評価は消さずに残す。

`build-report.py` は明示した最終候補と評価記録を受け取る。いずれかの総合点が85未満なら最終レポート生成を停止する。最高点のファイルを自動選択する処理はない。`--draft` なら未達を明記した `report-progress.html` と `manifest-progress.json` を作り、最終版とは別に保存する。

画像編集版を選択した場合は `kind:image_edit_mockup` と形状保持判定を検査し、レポートの冒頭にも実行フィルタ83%との違いを明記する。画像生成は同じ指示文から同じ画素が出ることを保証しないため、保存したPNGとSHA-256を比較の正本とする。

`check-report.mjs` は新規のレポートだけを隔離したheadlessブラウザで描画し、幅375px・明暗テーマ・欠損画像・動画のメタデータ・11秒へのシーク・原寸切替を検査する。ユーザーのブラウザプロファイルには接続しない。`verify-delivery.py` は処理記録に載せたファイルとソースのSHA照合、所有ソースの構文検査、同じレポートを描画したQA結果かの照合を行う。

デスクトップのブラウザ実操作はComputer Useが現在のURLを十分に確定できず停止した。静止画の自動 `file://` 表示もURL policyで拒否されており、同じ対象への別経路によるUI操作は行っていない。画像の数値検証と、既存の独立headless描画による生成物検証を分けて記録している。

## 依存と境界

Python 3.11・Pillow・NumPy・OpenCV・Node.js・FFmpeg。すべて既存の環境を使用。Unityの起動・ビルド・本編への反映・共用canon台帳への追記は行わない。

最終的に選択した成果物と評価とソースのhashは保存先の `manifest.json` に記録する。Drive上の生成物を削除すると、このリポジトリのコードだけでは固定入力や比較資料を復元できない。参照と入力の固定コピーも同じDriveフォルダに残す。
