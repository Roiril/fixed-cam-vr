# 博士のリップシンク動画用入力（いまの声・v4）

博士の口の動画を DomoAI で作るための画像と声。声はアプリが流している `*-ja-v4.mp3` と同じもの。
長い場面は文の境の無音で分けてある。言葉は切っていない。

前の一式（声 v2・5 本）は台詞が変わったので使わない。経緯は git の履歴にある。

## 入力

画像欄には 7 回とも `doctor-centered-v1.png` を入れる。1672×941 の 16:9。前回と同じ画像。
博士の部屋の絵（`room-proto/`）は、この写真の顔をそのまま使っている。だから口の動画もこの写真から作ると、部屋の絵にずれなく重なる。

| 順番 | 音声ファイル | 台詞 | 音声の長さ | 比率 | 動画の長さ |
|---|---|---|---:|---|---|
| 1 | `01_introduction_1-4.mp3` | 「ようこそ。」から「…説明します。」まで | 17.19 秒 | 16:9 | 20s |
| 2 | `02_subject_1-2.mp3` | 「調査対象は…」「…カーテンと人形です。」 | 11.74 秒 | 16:9 | 20s |
| 3 | `03_subject_3-4.mp3` | 「外見上は…」から「…あなたの任務です。」まで | 17.96 秒 | 16:9 | 20s |
| 4 | `04_subject_5-6.mp3` | 「観測装置をつけ…」「右手で手すりを…」 | 10.30 秒 | 16:9 | 20s |
| 5 | `05_report_1-3.mp3` | 「これが…観測装置です。」から「…報告してください。」まで | 17.69 秒 | 16:9 | 20s |
| 6 | `06_report_4-5.mp3` | 「報告するときは…」「…見間違いでも構いません。」 | 16.18 秒 | 16:9 | 20s |
| 7 | `07_wear_1-4.mp3` | 「調査が終わったら…」から「健闘を祈ります。」まで | 19.18 秒 | 16:9 | 20s |

長さは `clips.json` にも入っている（切った位置・元の声のハッシュつき）。7 は 20 秒の枠に 0.8 秒しか余らない。入らなければ教えてほしい（2 本に分け直す）。

1 の頭には 1.0 秒の無音がある（声の頭が見切れないように置いた間。0331）。口が閉じたまま始まるのが正しい。

## プロンプト

下の英文を、各回とも「プロンプト」欄へ同じように貼る。台詞は音声ファイルが与えるため、本文を重ねて書かない。前回と同じ文。

```text
Animate the exact man in the reference image as the Doctor, a restrained Japanese researcher speaking the supplied Japanese audio. Preserve his identity, age, facial features, short dark hair, white lab coat, dark shirt, warm side lighting, deep black background, and the centered upper-body composition. Keep his face and upper torso centered in the 16:9 frame throughout the shot, with the dark background around him. Use one locked camera shot with no zoom, pan, cut, reframing, or background change. Synchronize the lips precisely with the uploaded voice. His mouth rests naturally closed during pauses. Add only subtle breathing, occasional natural blinks, and very small head movements. His gaze stays near the camera. His expression remains calm, authoritative, and quietly ominous, without exaggerated fear or theatrical gestures. Keep the shoulders and coat stable. Do not add subtitles, text, logos, other people, music, sound effects, or new speech. Preserve the supplied voice and timing.
```

## 出力の確認

- 顔と白衣が入力画像から変わっていない
- 無音で口が動かない。唇と声がずれない
- **博士が画面の中央から動かない。肩と白衣が動かない。** 部屋の絵は、肩から下を別の絵でつないでいる。肩が動くと継ぎ目が見える
- カメラが寄らない・引かない（寄り引きは部屋の側でやる）
- 末尾の無音で不自然な表情へ変わる場合は、そのまま渡してよい（取り込むときに声の終わりで切る）

できた動画は、順番の番号が分かる名前（`01.mp4` 〜 `07.mp4` など）で渡してほしい。置き場はどこでもよい。

## 作り直す

リポジトリ直下で `py -3.11 tools/visitor-ui/prepare-lipsync-kit.py --output output/visitor-lipsync-kit-new`。既存の出力は上書きしない。
分け方は同じファイルの `GROUPS`（場面ごとの文の番号）。声を作り直したら（`generate-doctor-audio.py`）、この一式も作り直す。口の動画は声と 1 対 1 なので、声が変われば口の動画も作り直しになる。
