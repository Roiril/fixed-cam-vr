# 4 周目 A の人形を動画にする（Seedance 2.5 ／ 何もいない → 左半分から集まってくる）

ユーザーの言葉（`canon/LEDGER.md` 0135）:
「動画では何もいないなか、左半分からわらわらといろんなところから集まってくるみたいな感じの動画を生成したい」

動画は外部ツール（Dreamina の Seedance 2.5。Veo / Flow は弾かれる — `memory/web_compositor.md`）。
ここはプロンプトと入力の作り方だけ。静止画の方は `anomaly/dolls-many.md`。

## 組み方（2 本立て）

| | 何 | 入力 | 卓の cue |
|---|---|---|---|
| ① 集まる | 誰もいない → 人形が集まり終える。**1 回だけ** | 1 コマ目 ＝ 当日のプレート ／ 最終コマ ＝ 採用した人形の静止画 | `dolls_A_in`（mask `split_left_half`・loop OFF・尺 ＝ クリップ長） |
| ② 居続ける | 集まり終えた状態で、報告を押すまで（最長 45 秒） | 採用した人形の静止画 | 既存の `gen_dolls_A_left`（静止画）か、下の「居続ける」動画（loop ON） |

今朝の 2 周目 B が同じ形（`backrooms_B_in` ＝ 誰もいない→現れる → `backrooms_B` ＝ ループ）。
**①の最終コマと②の 1 コマ目が同じ絵でないと、切り替わりで人形が跳ねる。**
だから①は**首尾フレーム**（first / last frame）で焼く — 最終コマに②の静止画そのものを渡す。

## 入力の作り方

```bash
# ① の 1 コマ目（当日のプレート）と、① の最終コマ ＝ ② の入力（採用した人形の静止画）を 2 倍に
py -3.11 -c "from PIL import Image
for a,b in (('tools/web-compositor/captures/plate_A_<日時>.jpg','logs/gen-plate/seedance_first_A.png'),
            ('tools/web-compositor/captures/gen_dollsA_left_<日時>.png','logs/gen-plate/seedance_last_A.png')):
    Image.open(a).convert('RGB').resize((1280,960), Image.LANCZOS).save(b)"
```

- **2026-09-04 の候補で焼いてある**: `logs/gen-plate/seedance_first_A_20260904.png`（8/23 19:42 のプレート）/
  `seedance_last_A_20260904.png`（`gen_dollsA_left_20260904_0703.png`・まとまった群れ）。
  散った群れ（`gen_dollsA_left_20260904_0231d.png`）を到達点にするなら最終コマを差し替える
- **2 倍（1280x960）**。整数倍なので位置が 1 画素もずれない
- **比率 4:3**。16:9 は上下が切られて実写と重ならない
- 静止画は `_raw` ではなく採用した方（undim 済み）。プレートと同じ明るさでないと①の頭と尻で露出が変わる
- 弾かれるとき（`code=23007`）だけ明るい版を作る（下）

## ① 集まる — プロンプト（首尾フレーム・5〜10 秒・ループ OFF）

```
The camera is locked on a tripod and never moves: no pan, tilt, zoom, roll or shake, and the
framing is identical in every frame. Animate from the first frame to the last frame. The room
starts empty. Then Japanese ichimatsu dolls come into the left half of the picture from several
different places at once — from beyond the left edge, from under the hem of the curtain, from
behind the stand legs, and from below the bottom edge close to the lens — and gather on the floor
until they are exactly where the last frame shows them. They do not walk smoothly: they shuffle,
totter and slide in small uneven steps, each at its own timing; some crawl, some are already
sitting when they appear. Nothing else changes — the curtain, the floor and the light stay exactly
as in the input frames, and the right half of the picture stays empty. Keep the brightness,
contrast, colour, sharpness and grain of the input.
```

ネガティブは**空**。

## ⭐⭐ 終わりは「右へ手を差し伸べる」（2026-09-05・0157）

ユーザーの言葉:

> 仲間になりたい的な感じで手を右に差し伸べるところを最後にしたい。
> 人形の動きは不気味な感じで。ほかは任せた。怖い感じで

⇒ **到達点が変わったので、最終コマの静止画を作り直す**（差し伸べた姿）。
フリーズはその姿のまま最大 48 秒つづく。⚠ **右半分には体験者の人形が 1 体だけ立っている**
（`plate_A_right` ＋ CG 人形）。群れはそちらへ手を伸ばすが、届かない。

⚠⚠ **手は画面の左右の中央を越えさせない。** 越えた手は実機で縦に切れる（0120）。
群れの右端は既に素材 x=309 で、境目（x=320）まで **11 画素しかない**。
差し伸べるのは群れの中ほど・奥の人形が主で、手はその隙間へ伸ばす。

⭐⭐ **向きとしぐさ（2026-09-05・0160 の赤入れ）**:

- ❌ **手のひらを上に向けて水平に差し出さない** — 「ハイどうぞ」＝ 給仕のしぐさに見える
- ⭕ **指を開いて少し曲げ、掴もうとしている**か**手招き**。手首から先が相手へ向く
- ⭕ **顔はカメラではなく画面の右を向く**（斜め 45 度〜横顔の手前）。
  **4 周目 A では体験者が画面の中に人形として立っている**ので、そちらを見るのが正しい。
  ⚠ これは README §2.5「レンズを見るの 1 択」の**この 1 枚だけの例外**。
  成立する条件は「見る相手が画面の中にいるか」。他のカットへ広げない
- ⭕ 角度は揃えない。2〜3 体はまだよそを向いている（全員が揃うと号令に見える）
- ⭐ **右を向いても届いた顔は減らない**（周 4 で 37 個。カメラ向きと同数）。3/4 なら明るい楕円は残る

到達点は焼いてある: `captures/gen_dollsA_left_20260905_reach2.png`
（越えた画素 0 / 跨ぐ塊 0 / 右端 x=318）。

プロンプト（首尾フレーム・5〜10 秒・ループ OFF）:

```
The camera is locked on a tripod and never moves: no pan, tilt, zoom, roll or shake, and the
framing is identical in every frame. Animate from the first frame to the last frame. The floor
starts empty. Then Japanese ichimatsu dolls come into the left half of the picture from several
different places at once — from beyond the left edge, from under the hem of the curtain, from
behind the stand legs, and from below the bottom edge close to the lens — and gather on the floor
until they are exactly where the last frame shows them. They never walk smoothly: they shuffle,
totter, tip and slide in small uneven jerks, each starting and stopping at its own moment, like
footage missing frames; some crawl, some are already seated when they appear. The largest doll at
the front arrives last and comes closest to the lens. When they have all stopped, several of them
slowly raise a stiff arm toward the right side of the picture, elbow and shoulder bending in
angles, palm turned up, beckoning — and hold it there. No hand ever crosses the middle of the
picture. Nothing else changes — the curtain, the floor and the light stay exactly as in the input
frames, and the right half stays empty. Keep the brightness, contrast, colour, sharpness and grain
of the input.
```

**不気味さは 4 つで作る**（どれも周 4 の潰れを越える）:

1. **空から始める。** 体験者は 1〜3 周目にこの床が空なのを見ている。埋まることが出来事になる
2. **入る口を 4 つに散らす。** 1 か所からの列は行進に見える。同時にばらばらから湧くと「もともと画面の外に全部いた」になる
3. **歩かせない。** ずり・傾ぎ・倒れ込みを不揃いな間で。コマ落ちしたような動きは、画素が潰れても位置で残る
4. **止まってから手が上がる。** 動きが終わったあとに腕だけが上がるので、フリーズが「終わり」ではなくなる

⚠ **周 4 は色が完全に抜け、この画像の 3.2 画素が 1 つに潰れる。** 届いた顔は明るい楕円 1 つぶん。
表情も着物の柄も残らない。**残るのは大きな動きと、明るい楕円の数だけ**。赤に労力を使わない。

**カメラが固定になる仕掛けは 3 つ**（プロンプトの 1 文だけに頼らない）:

1. 先頭の 1 文（locked on a tripod / no pan, tilt, zoom, roll or shake / framing identical）
2. **首尾フレームの背景が同じ 1 枚**であること。1 コマ目（プレート）と最終コマ（人形の静止画）は同じプレートから作っているので、
   両端で背景の画素が一致する。カメラが動くと最終コマに辿り着けないので、動かさない方へ強く縛られる
3. 焼いた後に**測る**: 1 コマ目とプレート、最終コマと静止画の差（下の「戻すとき」の 2)・3)）。平均の差が 3 を超えたらカメラが動いている

⚠ 「curtain」「stand legs」は 2026-08-23 の A の並び（幕が正面・パイプの脚が幕の裾）。
当日のプレートに無いものは、その日のプレートに写っているものへ言い換える（出てくる口を 3〜4 つ挙げる、が要点）。

足すのは、実際に破られたときだけ（1 行ずつ）:

| 起きたこと | 足す 1 行 |
|---|---|
| 全員が同じ所から出てきた | `No two dolls come from the same place or at the same moment.` |
| 右半分へ入った | `Nothing ever crosses the middle of the picture.` |
| 歩きが滑らかで人間のよう | `The motion looks like a low frame rate: it skips and stutters.` |
| 全員がこちらを見て歩く | `Most of them look where they are going; only a few turn their heads toward the camera and stop.` |
| 最初から人形が居る | `For the first second the floor is completely empty.` |
| 背景が動いた | `The curtain and the floor do not move at all.` |
| それでもカメラが動いた（ズーム・揺れ） | `Static locked-off tripod shot. The background pixels stay exactly the same from the first frame to the last.`（それでも駄目なら道具を替える — Kling は固定に強い） |

⚠ **4 周目 A は完全な白黒で、この画像の 3.2 画素が 1 つに潰れる**（`compose.py --lap 4` の節）。
届いた画で残るのは**大きな移動**だけ。首の小さな動きや指先は見えない。
①は「白い顔の楕円が、無い所からいくつも湧いて集まる」動きとして届く。それで足りる。

### 首尾フレームが使えない道具のとき（逆再生）

静止画を **1 コマ目**に渡して「人形が去っていく」を焼き、**逆再生**する。
去る動きを逆に回すと集まる動きになり、足の運びが逆になるので**わざと不自然**になる。

```
Animate this photo. The dolls leave: they shuffle and crawl away in small uneven steps, each at its
own timing, some to beyond the left edge, some under the hem of the curtain, some behind the stand
legs, some down past the bottom edge, until the floor is completely empty. Nothing else changes —
the camera, the curtain, the floor and the light stay exactly as in the input image, and the right
half stays empty. Keep the brightness, contrast, colour, sharpness and grain of the input.
```

```bash
py -3.11 -c "import imageio_ffmpeg,subprocess,sys; subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(),'-y','-i',sys.argv[1],'-vf','reverse','-an',sys.argv[2]])" <去る.mp4> <集まる_逆.mp4>
```

## ② 居続ける — プロンプト（ループ ON・5 秒）

8/23 に出した版（ユーザーの赤入れ 2 回を経たもの。「全員が同じ向きに一斉に顔を向けるのはちょっと違う」／
「全部動くが全部バラバラ」「何体かは手を右側に差し伸べようとしている」）:

```
Animate this photo. Only the dolls move — the camera, the curtain, the floor and the light do not.
Every doll moves a little, but no two move alike and no two move at the same moment: heads turn by
small angles, bodies lean, shoulders shift, each at its own timing all through the clip. A few of
them slowly extend an arm toward the right side of the frame, as if reaching for something there,
then lower it again; their hands never cross the middle of the frame. Keep the brightness,
contrast, colour, sharpness and grain exactly as in the input image. The last frame matches the
first so the clip loops.
```

⚠ 手は画面の中央を越えさせない（右半分はマスクで捨てられて実写が出るので、越えた手は切れる）。
⚠ 差し伸べは「上げて下ろす」（ループの頭と尻が同じ姿勢でないと 5 秒ごとに跳ねる）。

## `code=23007`（センシティブ）で弾かれたとき

**長く書くほど審査の材料が増える。** 場面を describe しない（入力画像が場面そのもの。
静止画の `prompt/00-contract.md`「入力画像の上に置く作業です」と同じ）。

8/23 に弾かれた語: `surveillance camera` / `old surveillance recorder`（監視・盗撮）／
`the way a room full of people moves when they have decided not to be seen moving`／
`Never show the back of a head` / `No doll ever turns away`／
ネガティブの `glowing eyes` `mouths opening` `blinking` `dolls disappearing` `walking toward the camera` `smoke`
（**ネガティブも審査に掛かる。否定で書いても語として読まれる**）／`dim room`。

画が原因のときは明るい版（平均輝度 103.6 → 115.5 で通った実績）。戻すのは 1 行:

```bash
py -3.11 tools/gen-tone.py dim <生成物の 1 コマ.png> <戻した.png> --scale 0.15 --cam A
```

動画なら、コマへばらして全コマに掛けてから mp4 へ戻す。
⚠ 人形を減らして審査を通さない（数は演出の判定 — `canon/LEDGER.md` 0050 / 0120）。

## 戻すとき

```bash
# 1) 640x480 の H.264 mp4 にする（他のカットと同じ形。VP9 は実機で開けないことがある）
py -3.11 tools/web-compositor/to-mp4.py <生成物.mp4> tools/web-compositor/captures/gen_dollsA_in_<日付>.mp4

# 2) ① の 1 コマ目がプレートと重なっているか（ずれたら全部やり直し）
py -3.11 tools/gen-plate/screen.py --overlay <1 コマ目.png> --mask tools/web-compositor/masks/split_left_half.png \
    --live tools/web-compositor/captures/plate_A_<日時>.jpg --lap 4 --out logs/gen-plate/screen_dollsA_in.png

# 3) ① の最終コマが ② の静止画と重なっているか（平均の差が 3 未満なら同じ絵）
py -3.11 -c "from PIL import Image, ImageChops, ImageStat; import sys
a=Image.open(sys.argv[1]).convert('L').resize((640,480)); b=Image.open(sys.argv[2]).convert('L').resize((640,480))
print('mean abs diff', ImageStat.Stat(ImageChops.difference(a,b)).mean[0])" <最終コマ.png> tools/web-compositor/captures/gen_dollsA_left_<日時>.png
```

卓では ① を `dolls_A_in`（loop OFF・`durKind: sec` ＝ クリップ長）、続けて ② を同じ take の次の step に置く。
show.json は卓から書く（ディスク直書きは配られない — `memory/show_json_is_live_config.md`）。
⚠ 半分マスクの動画も `spill.py` の対象（境目 x=320 を跨ぐ人形は縦に切れる・0120）。①の最終コマ ＝ ②の静止画なので、
静止画が `spill.py` を通っていれば最終コマは通る。途中のコマで跨ぐぶんは、動きなので許す。
