# 2 周目 A の幕の染みを動画にする（Seedance 2.5 ／ 少し不敵な笑みを浮かべる）

ユーザーの言葉（`canon/LEDGER.md` 0137）: 「シミは、少し不敵な笑みを浮かべるような動画」

静止画の方は `anomaly/face-stain.md`（候補 `captures/gen_stainA_left_20260904_0656.png`）。
動画は外部ツール（Dreamina の Seedance 2.5）。ここは入力の作り方とプロンプトだけ。

## 組み方（2 本立て。人形の `dolls-gather.md` と同じ形）

| | 何 | 入力 | 卓の cue |
|---|---|---|---|
| ① 笑い始める | 染みの顔が、ゆっくり笑みの形になる。**1 回だけ**（4〜5 秒） | 1 コマ目 ＝ 染みの静止画 ／ 最終コマ ＝ 笑った静止画（下） | `stain_A_in`（mask は笑った方の差分・loop OFF・尺 ＝ クリップ長） |
| ② 笑ったまま | 報告を押すまで（dismissible） | 笑った静止画 | `cue_stain_A` の素材を笑った静止画に差し替える（until report） |

⚠ **動画は再生が終わると自動でライブへ戻る**（`ScreenOverlayController`）。①だけだと笑った直後に消える。
②の静止画を同じ take の次の step に置く。①の最終コマと②が同じ絵なら切り替わりで跳ねない。

## まず「笑った静止画」を作る（Codex・画像）

首尾フレームの最終コマに渡す相手。動画の到達点がこれで固定される。
染みの候補（暗くした版 `logs/gen-plate/auto_20260904_065645_3/out_fit.png`）を入力にして:

```
入力画像の幕に浮いている染みの顔を、**少し不敵に笑っている形**にしてください。
変えるのは 2 か所だけ — **口の濃い楕円を横へ広げ（元の 1.5 倍の幅）、両端を上へ持ち上げる**。
**目の濃い所を少し細める**（下まぶたが上がったように、上端はそのまま・下端を上げる）。
それ以外は 1 画素も変えない。幕・襞・パイプ・棚・床・明るさ・粒はそのまま。
染みは染みのまま — 線を引かない、歯を描かない、輪郭を作らない。
濃い所は滲んで布の色へ溶け、布の襞と織りは染みの中からも透けている。
```

出力を `undim`（`--cam A`）して `captures/gen_stainA_left_<日付>_smile.png` に置き、
`make-diff-mask.py --base plate_A --gen <笑った素材>` でマスクを焼く（口が広がるので静止画のマスクは使い回さない）。

**2026-09-04 に 1 回で通った**: `logs/gen-plate/smile_20260904/out.png`（顔の外の未変更 93.9%）。
素材 `captures/gen_stainA_left_20260904_0656_smile.png`、マスク `masks/cue_stain_A_20260904_0656_smile.png`。
首尾フレームは焼いてある: `logs/gen-plate/seedance_stain_first_20260904.png` / `seedance_stain_last_20260904.png`。

## 入力の作り方（動画）

```bash
py -3.11 -c "from PIL import Image
for a,b in (('tools/web-compositor/captures/gen_stainA_left_20260904_0656.png','logs/gen-plate/seedance_stain_first.png'),
            ('tools/web-compositor/captures/gen_stainA_left_<日付>_smile.png','logs/gen-plate/seedance_stain_last.png')):
    Image.open(a).convert('RGB').resize((1280,960), Image.LANCZOS).save(b)"
```

- **2 倍（1280x960）**・**比率 4:3**。人形と同じ
- 入力は **undim 済み**（ライブと同じ明るさ）の方。暗くした `_raw` を渡さない
- 弾かれるとき（`code=23007`）だけ明るい版（下）

## ① 笑い始める — プロンプト（首尾フレーム・4〜5 秒・ループ OFF）

```
Animate from the first frame to the last frame. Only the stain on the curtain changes, very slowly,
the way a damp stain creeps through fabric: the mouth-shaped dark patch stretches wider and its two
corners rise, until the face in the stain has settled into a faint, knowing smile; the two eye
patches narrow a little. The stain stays blotchy and soft-edged — it never becomes a drawing.
Nothing else changes: no camera movement, and the curtain, its folds, the rail and the light stay
exactly as in the input frames. Keep the brightness, contrast, colour, sharpness and grain of the
input.
```

ネガティブは**空**。

**首尾フレームが使えない道具のとき**（1 コマ目だけ渡す）: 同じ文で `Animate from the first frame to the last frame.`
を `Animate this photo.` に替える。到達点は固定されないので、②の静止画は**動画の最終コマを切り出して**作る:

```bash
py -3.11 -c "import imageio_ffmpeg,subprocess,sys; subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(),'-y','-sseof','-0.1','-i',sys.argv[1],'-frames:v','1',sys.argv[2]])" <生成物.mp4> logs/gen-plate/stain_last.png
```

**ループで済ませるとき**（cue を 1 本で・`loop: true`）: 末尾に 1 文足す —
`Then, just as slowly, the stain settles back to the way it was, so the last frame matches the first.`
5 秒ごとに笑って戻るので「息をしている染み」になる。ユーザーの言葉は「笑みを浮かべる」なので、既定は 2 本立て。

足すのは、実際に破られたときだけ:

| 起きたこと | 足す 1 文 |
|---|---|
| 口が動かず全体がうねった | `The change is in the mouth and the eyes only; the rest of the stain does not move.` |
| 顔が別の顔になった | `The face keeps its shape and position; only its expression changes.` |
| 線や歯が出た | `No lines, no teeth, no outline — only soft dark patches on cloth.` |
| 幕が揺れた | `The curtain itself does not move at all.` |

⚠ **2 周目 A は 2 画素が 1 つに潰れ、暗い幕の上の変化は届きにくい**（`compose.py --lap 2`）。
笑みは「口が 1.5 倍に広がって両端が上がる」くらい大きく動かさないと届かない。
届いた画で笑みが読めるかは `screen.py --overlay <最終コマ> --lap 2` で見る。

## `code=23007` で弾かれたとき

`dolls-gather.md` と同じ。場面を describe しない。8/23 に弾かれた語（surveillance / not to be seen / ネガティブの語）を入れない。
`stain` `smile` `face` は通っている語ではない（未検証）。弾かれたら `the dark patch on the cloth` と言い換える。
画が原因なら明るい版（`gen-tone.py dim ... --scale 0.15` で戻せる）。

## 戻すとき

```bash
py -3.11 tools/web-compositor/to-mp4.py <生成物.mp4> tools/web-compositor/captures/gen_stainA_smile_in_<日付>.mp4
py -3.11 tools/gen-plate/screen.py --overlay <1 コマ目.png> --mask tools/web-compositor/masks/cue_stain_A_<日付>.png \
    --live tools/web-compositor/captures/plate_A_<日時>.jpg --lap 2 --out logs/gen-plate/screen_stain_in.png
```

卓では ① を `stain_A_in`（loop OFF・`durKind: sec`）、続けて ② を同じ take の次の step に。
①のマスクは②（笑った静止画）の差分マスクを使う（口が広がる分まで覆う）。
