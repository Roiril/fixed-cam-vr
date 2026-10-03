# 博士のリップシンク動画用入力

このキットは博士タブレットで使用中の `doctor.jpg.bytes` と、日本語音声 v2 から作る。音声生成時の台詞入力は分割しない。ここでの分割は、動画生成画面で選べる長さに合わせるためのもの。

## 入力

画像欄には全5回とも `doctor-site.jpg` を入れる。1672×941のサイト配信中の JPEG とバイト単位で同じ画像。博士の顔は画面幅の約70%にあり、左に暗い余白がある。顔を中央へ切り抜かない。

音声欄と右側の設定は次の組み合わせにする。音声の長さは実ファイルで確認する。

| 順番 | 音声ファイル | 台詞の範囲 | 音声の長さ | 比率 | 動画の長さ |
|---|---|---|---:|---|---|
| 1 | `01_introduction.mp3` | 挨拶と調査依頼 | 15.638秒 | 16:9 | 20s |
| 2 | `02_subject_a.mp3` | 「こちらのカーテン」から「三周してください」まで | 8.259秒 | 16:9 | 10s |
| 3 | `03_subject_b.mp3` | 手すりの指示から慎重に歩く指示まで | 12.476秒 | 16:9 | 20s |
| 4 | `04_report.mp3` | 観測装置とエージェントの説明 | 19.710秒 | 16:9 | 20s |
| 5 | `05_wear.mp3` | 装着案内と「健闘を祈ります」 | 9.484秒 | 16:9 | 10s |

「探索方法」の元音声は20.735秒で、画面の20sを超える。2つの音声は台詞間の無音中央で分けた。言葉は切っていない。5本の生成動画を受け取ったら、音声と同じ長さへ末尾を切り、2と3を連結して4場面に戻す。サイトへ入れるときは音声を生成動画のものに置き換えず、現在の v2 音声を使う。

## プロンプト

下の英文を、各回とも「プロンプト」欄へ同じように貼る。台詞は音声ファイルが与えるため、本文を重ねて書かない。

```text
Animate the exact man in the reference image as the Doctor, a restrained Japanese researcher speaking the supplied Japanese audio. Preserve his identity, age, facial features, short dark hair, white lab coat, dark shirt, warm side lighting, deep black background, and the original wide composition. Keep him on the right side of the 16:9 frame with the empty dark space on the left. Use one locked camera shot with no zoom, pan, cut, reframing, or background change. Synchronize the lips precisely with the uploaded voice. His mouth rests naturally closed during pauses. Add only subtle breathing, occasional natural blinks, and very small head movements. His gaze stays near the camera. His expression remains calm, authoritative, and quietly ominous, without exaggerated fear or theatrical gestures. Keep the shoulders and coat stable. Do not add subtitles, text, logos, other people, music, sound effects, or new speech. Preserve the supplied voice and timing.
```

## 出力の確認

顔と白衣が入力画像から変わっていないかを確認する。無音で口が動かないこと、唇と声がずれないこと、画面左の余白が残ることも確認する。末尾の無音で不自然な表情へ変わる場合は、動画だけ音声の終わりで切る。

別のフォルダへ再生成する場合は、リポジトリ直下で `py -3.11 tools/visitor-ui/prepare-lipsync-kit.py --output output/visitor-lipsync-kit-new` を実行する。既存の出力は上書きしない。入力画像は `Assets/Resources/Visitor/doctor.jpg.bytes`。音声の正本は `Assets/Resources/Visitor/*-ja-v2.mp3.bytes` と `tools/visitor-ui/doctor-ja-v2-original.mp3`。
