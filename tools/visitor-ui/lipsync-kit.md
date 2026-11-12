# 博士のリップシンク動画用入力（いまの声・v5）

⚠ 2026-11-12（0345）から、口の動画は `room-proto/lipsync.py` が DomoAI の CLI で作る（声を 4.7 秒以下に切って 30 本）。この一式は手で DomoAI の画面に入れるときの予備。画像と同じ考え方は `lipsync.py` でも使っている。

博士の口の動画を DomoAI で作るための画像と声。声はアプリが流している `*-ja-v5.mp3` と同じもの。
長い場面は文の境の無音で分けてある。言葉は切っていない。

台詞は 2026-11-12 に直した（`canon/LEDGER.md` 0338）。それより前の一式（声 v2 の 5 本・声 v4 の 7 本）は使わない。経緯は git の履歴にある。

## 入力

画像欄には毎回 `doctor-centered-v1.png` を入れる。1672×941 の 16:9。前回と同じ画像。
博士の部屋の絵（`room-proto/`）は、この写真の顔をそのまま使っている。だから口の動画もこの写真から作ると、部屋の絵にずれなく重なる。

<!-- CLIPS -->

この表は `prepare-lipsync-kit.py` が、切った声の実測から書く（リポジトリの中のこのファイルでは空のまま。一式の `README.md` と `clips.json` に出る）。

最初の 1 本の頭には 1.0 秒の無音がある（声の頭が見切れないように置いた間。0331）。口が閉じたまま始まるのが正しい。

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

できた動画は、順番の番号が分かる名前（`01.mp4` 〜 など）で渡してほしい。置き場はどこでもよい。

## 作り直す

リポジトリ直下で `py -3.11 tools/visitor-ui/prepare-lipsync-kit.py --output output/visitor-lipsync-kit-new`。既存の出力は上書きしない。
分け方は同じファイルの `GROUPS`（場面ごとの文の番号）。声を作り直したら（`generate-doctor-audio.py`）、この一式も作り直す。口の動画は声と 1 対 1 なので、声が変われば口の動画も作り直しになる。
