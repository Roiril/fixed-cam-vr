# 博士のリップシンク動画用入力

このキットは博士タブレットで使用していた `doctor.jpg` を中央配置に編集した画像と、日本語音声 v2 から作る。音声生成時の台詞入力は分割しない。ここでの分割は、動画生成画面で選べる長さに合わせるためのもの。完成動画の先頭フレームを各場面の静止画にも使用する。

## 入力

画像欄には全5回とも `doctor-centered-v1.png` を入れる。1672×941の16:9画像で、博士の顔と上半身を中央に置いた。`doctor-site.jpg` はサイト配信中の元画像で、比較用に残す。

音声欄と右側の設定は次の組み合わせにする。音声の長さは実ファイルで確認する。

| 順番 | 音声ファイル | 台詞の範囲 | 音声の長さ | 比率 | 動画の長さ |
|---|---|---|---:|---|---|
| 1 | `01_introduction.mp3` | 挨拶と調査依頼 | 15.638秒 | 16:9 | 20s |
| 2 | `02_subject_a.mp3` | 「こちらのカーテン」から「三周してください」まで | 8.259秒 | 16:9 | 10s |
| 3 | `03_subject_b.mp3` | 手すりの指示から慎重に歩く指示まで | 12.476秒 | 16:9 | 20s |
| 4 | `04_report.mp3` | 観測装置とエージェントの説明 | 19.710秒 | 16:9 | 20s |
| 5 | `05_wear.mp3` | 装着案内と「健闘を祈ります」 | 9.484秒 | 16:9 | 10s |

「探索方法」の元音声は20.735秒で、画面の20sを超える。2つの音声は台詞間の無音中央で分けた。言葉は切っていない。5本の生成動画は受領済み。`ingest-doctor-video.py` は2と3を8.24秒の境界で連結する。3の末尾を最終フレームで補い、4場面に戻す。生成動画の音声は使わず、現在の v2 音声を AAC で組み込む。

## プロンプト

下の英文を、各回とも「プロンプト」欄へ同じように貼る。台詞は音声ファイルが与えるため、本文を重ねて書かない。

```text
Animate the exact man in the reference image as the Doctor, a restrained Japanese researcher speaking the supplied Japanese audio. Preserve his identity, age, facial features, short dark hair, white lab coat, dark shirt, warm side lighting, deep black background, and the centered upper-body composition. Keep his face and upper torso centered in the 16:9 frame throughout the shot, with the dark background around him. Use one locked camera shot with no zoom, pan, cut, reframing, or background change. Synchronize the lips precisely with the uploaded voice. His mouth rests naturally closed during pauses. Add only subtle breathing, occasional natural blinks, and very small head movements. His gaze stays near the camera. His expression remains calm, authoritative, and quietly ominous, without exaggerated fear or theatrical gestures. Keep the shoulders and coat stable. Do not add subtitles, text, logos, other people, music, sound effects, or new speech. Preserve the supplied voice and timing.
```

## 出力の確認

顔と白衣が入力画像から変わっていないかを確認する。無音で口が動かないこと、唇と声がずれないこと、博士が画面中央から動かないことも確認する。末尾の無音で不自然な表情へ変わる場合は、動画だけ音声の終わりで切る。

別のフォルダへ再生成する場合は、リポジトリ直下で `py -3.11 tools/visitor-ui/prepare-lipsync-kit.py --output output/visitor-lipsync-kit-new` を実行する。既存の出力は上書きしない。画像の元は `tablet/app/src/main/assets/web/asset/doctor.jpg`。中央配置の入力画像は `tools/visitor-ui/doctor-centered-v1.png`。音声の正本は `tablet/app/src/main/assets/web/asset/*-ja-v2.mp3` と `tools/visitor-ui/doctor-ja-v2-original.mp3`。

調査依頼だけを再取り込みする場合は、リポジトリ直下で `py -3.11 tools/visitor-ui/ingest-doctor-video.py C:\Users\kouga\Downloads\01-rip.mp4` を実行する。全4場面を取り込む場合は、続けて `02-rip.mp4` から `05-rip.mp4` までを順に指定する。スクリプトは全入力と元音声の SHA-256 を先に検証する。完成動画と先頭フレームの静止画を一時ファイルへ生成して検証後に置き換える。1は既存の調査依頼動画と同一であるため、5引数の実行では再生成しない。
