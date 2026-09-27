# 博士のリップシンク動画用素材

タブレットの調査説明で使う博士画像と日本語音声の複製。元データは `Assets/Resources/Visitor/doctor.jpg.bytes` と各章の `*.mp3.bytes`。画像や音声の内容は変更していない。女性版は現在のタブレットでは使われていない。

## 生成画面への入力

「画像または動画」には毎回 `doctor.jpg` を入れる。「音声」には下表の1本を入れる。4章を別々に生成する。

| 章 | 音声ファイル | 実音声の長さ | 比率 | 動画の長さ |
|---|---|---:|---|---:|
| 調査依頼 | `introduction-ja-v1.mp3` | 12.27秒 | 16:9 | 20s |
| 回収壁面 | `subject-ja-v1.mp3` | 16.05秒 | 16:9 | 20s |
| 異変の報告 | `report-ja-v1.mp3` | 18.29秒 | 16:9 | 20s |
| 装着案内 | `wear-ja-v1.mp3` | 7.51秒 | 16:9 | 10s |

「自動」でも画像の比率は判定できるが、ここでは明示的に16:9を選ぶ。5秒は全章で音声が収まらない。生成後は末尾の余りを音声の終端に合わせて切る。音声の再生速度は変えない。

## プロンプト欄

次の英文を4章とも同じように入れる。台詞は音声から与えるので、プロンプトには書かない。

```text
The man in the reference image speaks directly to the visitor in a calm, serious, authoritative manner. Synchronize his lip movements precisely to the provided Japanese audio. Keep his face, age, hairstyle, lab coat, dark curtain background, warm side lighting, framing, and the empty dark space on the left consistent with the reference image. He makes small natural blinks and very subtle head movements at pauses. His shoulders stay nearly still. Maintain direct eye contact. One continuous locked-off shot. No camera movement, cuts, zoom, scene changes, added people, hand gestures, subtitles, captions, logos, or new sounds.
```

タブレット側の台詞と時間配分の正本は `Assets/Resources/Visitor/briefing-v1.json.bytes`。サイトでは章単位で音声を再生し、文単位で途中停止・再開する。完成動画も同じ章単位で用意する。
