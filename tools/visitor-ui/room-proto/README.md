# 博士の部屋（試作）

博士とテレビを 1 つの部屋の絵に置き、カメラがその中を動く形の試作（LEDGER 0335・`canon/OPEN.md`「博士とテレビを 1 つの部屋に置き、1 台のカメラで繋ぐ」）。
博士の口は DomoAI の Talking Avatar で動かしてある（`layers/face-ja.mp4`）。
タブレットのアプリは、この頁を焼いた 1 本の動画（`asset/doctor-room-ja-v*.mp4`）を流す（日本語だけ。英語と仏語は従来の静止画とテレビ）。

## 見る

`.claude/launch.json` の `room-proto`（`node tools/visitor-ui/room-proto/serve.cjs --port 8142`）→ `http://localhost:8142/`。
空白で再生と停止。左右の矢印で前後の文。声・字幕・絵はタブレットの正本（`tablet/app/src/main/assets/web/asset/`）をそのまま読む。

- `?doll=0` 人形の頭が傾く演出を外す
- `?sfx=0` 効果音を鳴らさない（声だけ）
- `?w=1280` 描く幅を固定する（書き出し用）
- `?lips=0` 口の動画を使わず、元の写真のまま

## ファイル

| | |
|---|---|
| `gen/seed.png` | Codex へ渡した土台。黒い画布に博士の元の写真（`../doctor-centered-v1.png`）だけを貼ったもの |
| `gen/plate-a1.png` | Codex が描き足した部屋（テレビは消えている）。1672×941 |
| `gen/plate-a1-lit.png` | 同じ部屋で、テレビだけを点けさせた版。使うのは光の差だけ |
| `gen/tv-crop-in.png` → `gen/tv-hi-a1.png` | テレビのまわり (920,275)-(1672,698) を切って拡大した物と、それを Codex に精細に描き直させた物 |
| `gen/doll-in.jpg` → `gen/doll-tilt-a1.png` | 人形の記録写真（`asset/doll-file-v1.jpg`）と、頭だけ傾けさせた版 |
| `build-layers.py` | 上の絵を層へ分ける → `layers/`（博士を抜いた部屋・博士・光 2 枚・ガラスの形・精細なテレビ・頭の傾いた人形・`room.json`） |
| `make-room-sound.py` | 音。`gen` は物が動く 1 発の音を ElevenLabs で作る（`sound/src/`）。`mix` は時刻表どおりに並べて `layers/stem.mp3` にする |
| `room-stage.js` | 舞台。1 枚の canvas に 部屋 → 精細なテレビ → テレビの中身 → 部屋への光ともや → 博士 → 博士への光 → 埃 → 灯り → 粒 の順で描く |
| `index.html` | 声の時刻からカメラ・ピント・テレビの中身・灯り・音を決める台本と、再生の操作 |
| `serve.cjs` | 配る。書き出しのコマも受け取る（`POST /_frame`） |
| `make-preview.py` | 書き出したコマに声と字幕を合わせて mp4 にする |
| `lipsync.py` | 口の動画。`plan` が声を無音の位置で切り（`layers/lipsync-plan.json`）、`send` が DomoAI へ投げて取り出し、`track` が時刻表に並べて `layers/face-ja.mp4` にする |
| `bake-film.py` | 書き出したコマに声と効果音を合わせて、アプリが流す 1 本と場面ごとの最初のコマにし、`briefing-v1.json` の `film` を書く |

## 通しの動画を作る

1. `http://localhost:8142/?w=1280` を開き、コンソールで `await RoomProto.postMeta()`（文と音の時刻表を送る）
2. `py -3.11 tools/visitor-ui/room-proto/make-room-sound.py mix`（時刻表を変えたら毎回。2 秒）
3. コンソールで `await RoomProto.export('full', 30)`（3700 コマ前後・2〜3 分）
4. `py -3.11 tools/visitor-ui/room-proto/make-preview.py <ローカルの出力.mp4>` → できたものを Drive `renders/doctor-room/` へ移す
5. `frames/` は消す（git 管理外）

## 口の動画を作り直す（声を変えたとき）

口の動画は声と 1 対 1。声を作り直したら全部作り直しになる。

1. `py -3.11 tools/visitor-ui/room-proto/lipsync.py plan <作業フォルダ>`（先に `layers/lipsync-plan.json` を消す。作業フォルダはリポジトリの外）
2. `py -3.11 tools/visitor-ui/room-proto/lipsync.py send <作業フォルダ>`（30 本前後・450 クレジット前後・15 分前後。止めても続きから回る）
3. 試作の頁で `await RoomProto.postMeta()` の後、`py -3.11 tools/visitor-ui/room-proto/lipsync.py track <作業フォルダ>`
4. 取り出した動画は Drive `renders/doctor-room/lipsync-v<声の版>/` へ移す

- DomoAI の CLI は `npm install -g @domoai/cli` → `domoai login --start`（出た URL をログイン済みのブラウザで開いて承認）→ `domoai login --finish`
- 枠の値段は 5 秒 15・10 秒 45・20 秒 90 クレジット。5 秒の枠が秒あたり最安なので、声を 4.7 秒以下に切る。出来る動画は声と同じ長さ・1280×720・25fps
- どの本も元の写真の姿勢で始まり、ほぼ同じ姿勢で終わる。つなぎ目は前後 0.1 秒を重ねて溶かす。頭は話しながら上下に最大 12 画素（1280 幅）動く
- ⚠ 同時に積める本数に上限がある（`queue_full`）。`send` は積んだ分が終わるのを待って投げ直す
- ⚠ 出来た動画の URL は Python の urllib の既定の User-Agent だと 403 になる。curl で取る
- ⚠ 投げている最中に止めると、作られたのに記録に残らない本が出る。`domoai list` と `posts.json` を突き合わせる

## アプリの動画を焼く

1. `http://localhost:8142/?w=1280` を開き、コンソールで `await RoomProto.postMeta()`
2. `await RoomProto.export('film', 30, 0, RoomProto.END_AT + 400)`（声の終わりまで。終わりの砂嵐はアプリが出す）
3. `py -3.11 tools/visitor-ui/room-proto/bake-film.py --version <版>`。動画の中身を変えたら版を上げる（WebView が古い動画を持つ）
4. `frames/film/` を消す

アプリは場面ごとに `film.ja.fromMs`（その場面の分の頭）から流し、文の窓は `voiceMs` に文の頭を足した位置で切る。場面をまたぐときは同じ動画を読み直さずに続けて流す（乱れを挟まない）。最初の文の字幕は声の頭まで待つ。動画が読めないときは、その説明の間だけ声とテレビの形へ戻る。

## テレビの中身

何をどの文から映し、テレビの中でどう寄るかは `tv-programme.json`。ツインの構図は `tv-shots.json`（`twin_render.py --shotfile` が読む）。英語と仏語のアプリはこれを読まず、`briefing-v1.json` の `figures` のまま。

| 文 | テレビの中 |
|---|---|
| 調査対象 1〜2 文目 | 床すれすれから見上げた L 字のカーテン。電球が真上。機材と人は消してある。2 文かけてカーテンの角へ寄る（`tv-room-v1.jpg`） |
| 3〜4 文目 | 人形の記録写真。人形を真ん中へ寄せながら 1.4 倍まで寄る |
| 装置 2 文 | 観測装置。わずかに寄る |
| 「室内のカメラが捉えた…」 | 三脚のスマホの後ろの低い所から。下にピントの合った画面（本人が映っている）、その上にぼけた本人。画面へ寄り、最後は本人の頭が上へ外れる（`tv-ots-v1.jpg`） |
| 「…周りを三周」 | 天井近くから見下ろした 1 周を 2.5 倍速で（`tv-top-v1.mp4`） |
| 「右手で手すりを…」 | カメラ A の配信を実時間で。人が画に居る 5.4 秒までを使い、文の終わりにちょうど 5.4 秒へ着く（`landOnOut`） |
| 「…違和感を覚えたら」 | 同じ配信の 5.4 秒のコマで止め、人へ寄る。乱れも音も挟まない（`seamless`） |
| ボタンの図 | 従来どおり |

`frame` は `[x, y, 倍率]`。x, y はその絵の中でテレビの真ん中に置く点、倍率 1 はテレビを隙間なく埋める大きさ。端に隙間が出る所は寄せて止まる（`room-stage.js` の `place`）。

### テレビの中身を撮り直す

```powershell
$S = "tools/visitor-ui/room-proto/tv-shots.json"
py -3.11 tools/twin/shoot.py --tag tvfeedA --shots A --w 960 --spp 128 --person 1 --shotfile $S          # スマホの画面に映す画（先に）
py -3.11 tools/twin/shoot.py --tag tvstills --shots tv_room,tv_ots --w 1440 --spp 384 --person 1 --shotfile $S
& "C:\Program Files\Blender Foundation\Blender 5.1\blender.exe" --background --factory-startup --python tools/twin/twin_render.py -- seq --route forward --cams tv_top --w 960 --spp 64 --plate-spp 512 --shotfile $S
py -3.11 tools/twin/seq_post.py forward --cams tv_top
py -3.11 tools/visitor-ui/room-proto/make-tv.py still <tv_room.png> tv-room-v1
py -3.11 tools/visitor-ui/room-proto/make-tv.py still <tv_ots.png> tv-ots-v1 --lift 0.268,0.668,0.628,0.9,2.6
py -3.11 tools/visitor-ui/room-proto/make-tv.py clip <game/forward/tv_top.mp4> tv-top-v1
py -3.11 tools/visitor-ui/room-proto/make-tv.py clip tablet/app/src/main/assets/web/asset/twin-walk-v1.mp4 tv-feed-a-v1 --trim 10.3
```

- 静止画は 1 枚 10〜40 秒、見下ろしの 1 周（339 コマ）は他の描画と並べて 50 分前後（RTX 4090 Laptop）
- `make-tv.py` は明るさの中央値を 44 へ沈める（ツインの仕上げは中央値 90 前後で、人形の記録写真の 21 と並べるとテレビの中で白く浮いた）。`--lift` はスマホの画面の箱だけを持ち上げる（沈めると光る画面が周りより暗くなった）
- ⚠ スマホの真後ろから撮ると、本人がスマホの陰に隠れる。画面を画の下に置き、本人を上に出す
- ⚠ 手すりを持つ手の寄りは撮ったが使っていない。代役の手が手すりに掛かって見えない（前・横・後ろの 3 方向で試した）
- ⚠ B と C の担当の配信は、人が小さいか画の端で切れる。テレビに使うのは A の頭 5.4 秒だけ

## 演出（上から起きる順）

博士の目は画面に入れない（0342）。博士が写る構図は上の端を目のすぐ下に置く。台本を変えたら、コンソールで `RoomProto.eyeCheck()` が空の配列を返すことを確かめる。

| いつ | 何が起きる |
|---|---|
| 始まり | 闇。カチ、と灯りが入って 2 度またたき、1 秒かけて満ちる。写っているのは博士の口元 |
| 「ようこそ。」「私は……」 | 口元から、鼻から胸までへゆっくり引く |
| 「あなたは今日から…」「これより…」 | 少し寄り直す |
| 4 文目の後 | 画面の外でテレビが点く。冷たい光が博士の右の頬と肩に当たる。1 秒後にカメラが右へ動き出し、途中から上がって（弧）、博士の肩越しのテレビになる。ピントがテレビへ移る |
| 調査対象（前の 3 文） | 肩越しのテレビ。ゆっくり寄る |
| 「…変わりません。」と「しかし、」の間 | 画が 0.16 秒裂け、人形の頭が傾いている（0337 で「残す」。`?doll=0` で外せる）。時刻は声の大きさの列（`asset/doctor-voice-ja-v*.json`）から、その文でいちばん長い無音の真ん中を取る。低い圧の音が「任務です」まで続く |
| 「その条件も…まだ判明していません。」 | カメラが下がってから左へ（弧）。左の端に博士の口元、右にテレビ |
| 「それを調査するのが、あなたの任務です。」 | 口元へ寄る |
| 装置・画面・カメラ（3 文） | 弧を描いて、少し引いたテレビへ。ゆっくり寄る |
| 「その映像を見ながら…三周」「右手で手すりを…」 | テレビの中へ入る（ガラスが画面の 7 割）。走査線と蛍光体の縞が見える |
| 「…違和感を覚えたら、報告してください。」 | 弧を描いて博士の口元へ戻る |
| ボタンの図 | もう一度テレビの中へ。「何を見たかは…」の途中で肩越しへ引く |
| 「装置の合図で…」 | 鼻から腰までの静かな引き。右奥のテレビは砂嵐 |
| 「続けられないときだけは…」「では。…」 | 文ごとに寄っていく |
| 「健闘を祈ります。」 | 口元 |
| 終わり | 砂嵐が画面全体を覆い、縦に潰れ、横一線が点になって消える |

## 音

タブレットの音の決まり（`rules/sound-design.md` の頭。ノイズかカチカチだけ・曲は作らない・声より下）に合わせた。

- 鳴り続ける音（部屋の空気・点いているテレビ・砂嵐・低い圧）は、白色雑音から帯を切って計算で作る。ElevenLabs に作らせた版は 200Hz の唸りと 4.4kHz の細い山が乗っていたので使わない
- 物が動く 1 発の音（灯りのスイッチ・テレビの電源・チャンネル 3 種・デッキ・電源断）は ElevenLabs の効果音生成。デッキは入る音の 0.65 秒だけ使う（回り出す音は音程が出る）
- 切り替えの音は、文の境の 0.31 秒前に置く（境は無音の真ん中にある。乱れと声を重ねない・0332）
- 全体は -35 LUFS（声は約 -21）。カチッという音は尖頭を -12 dBFS で抑える
- ⚠ シュビーは聴いていない。大きさと周波数の山を測っただけ

## 分かったこと

- 元の博士の写真は、部屋の絵に 幅 926・左上 (96,126) で重なる（顔の窓の差が最小の所を探した値。`build-layers.py` の `FACE_RECT`）。Codex は「1 画素も変えない」と頼んでも顔を描き直すので、顔は必ず元の写真を上から重ねる
- 光を 1 枚で足すと、ぼかした光が博士の輪郭の外へはみ出して青い縁取りに見えた。部屋の分と博士の分に分け、博士の分は博士の形で抜く
- テレビの中身の平均の色を 60ms ごとに読み、光の色と強さにしている。電源の入りの白い光も、砂嵐の揺れも、何も足さずに博士へ乗る
- `Image.decode()` と `canvas.toBlob()` は、内蔵ブラウザの枠が見えていないと戻らない・遅い（1 コマ 1 秒）。`onload` と `toDataURL` を使う
- ⚠ 台本で、鍵より前の時刻に `SHOT.doc0` そのものを返して揺れを足すと、毎コマ足されて構図が下へ流れた。写しを取る
- Codex に切り出しを「構図を動かさず精細に」と頼むと、ずれ 0.5 画素・相関 0.97 で返った（`room.json` の `tvHiFit`）。明るさは 1.1〜1.2 倍ずれるので合わせ直す
- 人形は頭のまわりの楕円だけを差し替える（Codex の版は寸法も背景も少し変わる。外側の差の平均は 2.6）
- ⚠ 大きさを測るのに `np.convolve` を使うと、2 分の音で終わらなかった。累積和で窓の平均を出す
- ⚠ Redmi の WebView で、動画を毎コマ canvas へ描いて足りる速さが出るかは未確認（博士はまだ静止画）。この版は描く物が増えている
