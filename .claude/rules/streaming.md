---
name: streaming
description: MJPEG / WebRTC 取り込み規約。スマホからの映像受信パス
paths:
  - "Assets/**/Streaming/**"
  - "Assets/**/*Mjpeg*.cs"
  - "Assets/**/*Stream*.cs"
---

# ストリーミング取り込み規約

## 入力ソース

| ソース | プロトコル | レイテンシ実測 | 用途 |
|---|---|---|---|
| **fixed-cam-streamer** (自作 Android) | MJPEG over HTTP `:8080/video` + `/info` + `/health` | 100–200ms | **Android 標準（DroidCam から移行済み）** |
| **IP Camera Lite** (iOS) | MJPEG over HTTP `:8081/video`、**Basic 認証（既定 admin/admin）** | 未実測 | **iPhone 標準候補**（下記「iPhone（iOS）ソース」参照） |
| DroidCam (Android) | MJPEG over HTTP `:4747/mjpegfeed?WxH` | 150–300ms | フォールバック / 緊急時 |
| IP Webcam (Android) | MJPEG over HTTP `:8080/video` | 150–300ms | 代替 |
| WebRTC（自作 PWA / Larix） | WebRTC | 50–100ms | 低レイテンシ要件時 |

受信側（`MjpegStreamReceiver` / `CameraSource`）は **fixed-cam-streamer 専用ではなく汎用 MJPEG クライアント**。
boundary は Content-Type から自動検出、chunked は自動デチャンク、`X-Capture-Ns` / `X-Frame-Seq` /
`/info` / `/health` は全てオプショナル（無いサーバでは歯抜け検出・遅延推定・自動回転・lag 検出が
無効になるだけで映像は通常受信できる）。

## iPhone（iOS）ソース

fixed-cam-streamer は Android/Kotlin 専用で iPhone では動かない（iOS 移植は Mac + Xcode が必要で未着手）。
iPhone は既製の MJPEG 配信アプリで代替する。実運用想定: iPhone 13 Pro ×2 + Pixel ×1（2026-06 時点・未確定）。

- **標準: IP Camera Lite**（App Store、無料）。MJPEG を `http://<ip>:8081/video` で配信、Basic 認証（既定 admin/admin、アプリ内で変更可）。**2026-06-11 iPhone 13 Pro 実機で受信検証済み**
- CameraSource 設定: `port=8081` / `videoPath=/video` / `infoPath=""` / `healthPath=""` / `username,password` を入力
  - **Basic 認証対応は CameraSource の username/password フィールド**（2026-06-11 追加）。空なら Authorization ヘッダ自体を送らない。認証なしアクセスは 401（必須）
  - 実パスワードを設定した .asset はコミットしない（既定 admin/admin はコミット可）
- **超広角対応**: アプリ内カメラ選択で「Back Ultra Wide Camera」を選べる（13 Pro 実機確認済み）。広角配置にも iPhone を使える
- **配信プロファイル実測**（13 Pro 既定設定）: HTTP/1.0・非 chunked・`boundary=--BoundaryString`・パート毎 `Content-Length` 付き。1 フレーム ≈93KB、帯域 ≈30Mbps と **fixed-cam-streamer（quality 40 / 640x480）の数倍重い** → 3 台運用ではアプリ側で解像度/品質を下げること
- **解像度・アスペクト（2026-06-17 調査 / 2026-07-23 更新）**: 既定は **640×480（4:3）固定**。黒帯は無く素の 4:3（iPhone センサーのフル 4:3）。**streamer も v0.6.0 から 4:3（640x480）になったので既定のままでアスペクトが揃う**（旧記述「16:9 に変更して揃える」は廃止 — 16:9 は縦画角を 1.33 倍失う）。web-compositor のビューは live 実寸からアスペクトを自動追従する
- **遠隔制御・メタ endpoint は無し**: `/video?resolution=` 等の URL パラメータは効かない。`/settings` `/status` `/info` `/config` `/jpeg` `/photo.jpg` は全て 404。解像度・品質・カメラ選択は**アプリ UI でしか変えられない**。`/` も `/video` と同じ MJPEG を返す（`Server: IP Camera for iOS`）
- **Lite（無料）版は全フレームにウォーターマークが入る**。演出上問題なら有料版で除去
- `/info` 無し → 自動回転メタは来ない。**端末を横持ち固定**で運用するか、`MjpegScreen.uvRotSteps` で手動補正
- `/health` 無し → lag 検出・E2E 遅延推定は自動無効。カクつき調査はアプリ側 UI とルータで切り分け
- 401 が返ると `[MJPEG]` ログに「Basic 認証が必要」のヒント付きでエラーが出る
- 接続検証はブラウザ or `curl.exe -m 4 -s -D - -o NUL --user admin:admin http://<iphone>:8081/video` でヘッダ確認が最速（エンドレスストリームなので exit 28 = timeout が正常）
- **使えないアプリ**: iVCam は「iPhone → PC クライアント → 仮想 Web カメラ」型で iPhone 側に HTTP サーバが立たない → 本構成（Quest が直接 pull）では不可。同型（EpocCam / Camo 等）も同様

## fixed-cam-streamer エンドポイント

| URI | レスポンス | Unity 側で読む箇所 |
|---|---|---|
| `GET /video` | `multipart/x-mixed-replace; boundary=frame` の MJPEG。**各パートに `X-Width` / `X-Height` / `X-Rotation` / `X-Capture-Ns` / `X-Frame-Seq` ヘッダ付き** | [`MjpegStreamReceiver`](../../Assets/Scripts/Streaming/MjpegStreamReceiver.cs) |
| `GET /info` | `{deviceName, lensId, lensFovDeg, widthPx, heightPx, rotationDeg, isPortrait, deviceRotationDeg}` + `{tiltPitchDeg, tiltRollDeg, tiltState}`（v0.9.0〜・下記「端末の傾き」） | [`StreamMetadataFetcher`](../../Assets/Scripts/Streaming/StreamMetadataFetcher.cs)、[`CameraStream.Start()`](../../Assets/Scripts/Streaming/CameraStream.cs) で 1 回取得。`deviceRotationDeg`=端末の物理的な上方向(0/90/180/270、OrientationEventListener 検知)。配信フレームは常に正立済み（`rotationDeg=0`、横持ち=640x480 / 縦持ち=480x640。v0.6.0 から 4:3）。**tilt は Unity 側の DTO に無く JsonUtility が無視する**（使うのは卓だけ） |
| `GET /health` | `{uptimeMs, totalFrames, totalBytes, fps, sentFrames, latestFrameAgeMs, clientCount, eisMode, oisMode, afMode, cropRatio, zoomRatio, aeState, aeLock, awbLock, expUs, iso, flicker}`（v0.5.0〜 後半は CaptureResult 直読みの実効値 = 決定性の観測用）+ `{thermalStatus, thermalHeadroom, throttleStage, encodeIdle, batteryTempC, plugged}`（v0.7.0〜 発熱抑制の観測。**⚠⚠ encode が止まったかは `encodeIdle` と `latestFrameAgeMs` で見る。`fps` では判定できない** — あれは 1 秒窓の直近値なので、**encode が止まっても 30 前後を返し続ける**（2026-08-23 に実測で誤読した。`clientCount=0 / fps=29.8` を見て「回っている」と読んだが、`latestFrameAgeMs` は 148 秒で既に止まっていた）。throttleStage 1/2 は熱スロットル中＝fps/画質が自動降下している） | 任意。`CameraStream.RefreshHealthAsync()` で都度取得（HudDump からのモニタ用）。`sentFrames` と `totalFrames` の差分が広がる時は HTTP ワーカ詰まり（**⚠ sentFrames はクライアント接続ごとに加算される** — Quest + web 卓 /cam プロキシ等の多クライアント時は sent ≈ 接続数×totalFrames が正常。単一クライアント前提でしか差分ヒューリスティックを使わない）。`latestFrameAgeMs` が大きい時はカメラ stall |
| `GET /` | 簡易ステータス HTML | ブラウザ確認用 |

| `GET /record/start?shot=&maxSec=&countdownSec=` ほか | 端末内録画（v0.10.0〜）。`/record/stop` `/record/status` `/record/list` `/record/file` `/record/delete` | 卓（`tools/web-compositor`）だけが使う。Unity は触らない |

リポジトリ: [Roiril/fixed-cam-streamer](https://github.com/Roiril/fixed-cam-streamer)（private）。APK ビルド・インストール手順はそちらの README 参照。

### ⚠ 「配信が回っているか」を `/health` から読むときの 2 つの罠（2026-08-23）

どちらも現場で誤診を作る。**判定に使うのは `encodeIdle` と `latestFrameAgeMs` の 2 つ。**

- ⚠⚠ **`fps` で判定しない。** あれは 1 秒ウィンドウの**直近値**で、encode が止まっても
  最後に計算された値（30 前後）を返し続ける。実測: `clientCount=0` / `encodeIdle=true` /
  最後のフレームが 148 秒前、の端末が `fps=29.8` を返した
- ⚠⚠ **`latestFrameAgeMs` の `-1` は「たったいま」ではなく「起動してから 1 枚も作っていない」。**
  `lastPublishMs == 0` の番兵（`FrameStats.toJson`）。0 と読むと**止まっている端末を
  「回っている」と誤診する**。実際に別セッションが `-1` を「≈0 秒前」と読んだ

⇒ **止まっていることの証拠は `encodeIdle=true`。** 経過時間で言いたいなら、
`totalFrames > 0` を確かめてから `latestFrameAgeMs` を読む。

## 配信アプリの画面 — 据える人・撮る人の仕事は端末に置く（v0.13.0・2026-08-23）

**役割で機能の置き場を決める。** 三脚に据える人も、人形視点を撮る人も、見ているのは
スマホの画面であって卓ではない。それまで「据える」の補助は何も無く、「撮る」は卓からの
遠隔操作だけだった（＝撮る人が卓まで歩くか、卓の人に口で頼む形）。

### 据える — 補助線と鏡合わせ

| 出るもの | 中身 | 既定 |
|---|---|---|
| **鏡の軸** | 配信画像の**横中央**に縦線 | **カメラ A だけ ON** |
| **水平** | 縦中央に横線 ＋ 傾きが 1° 以内かで色が変わる | カメラ A だけ ON |
| **三分割** | 3×3 | 手動 |
| **🪞 鏡で確かめる** | 左半分を**右半分の鏡像**に置き換えて出す。継ぎ目が消える所が左右対称。ずれを数値でも出す | 手動 |

⚠⚠ **鏡の軸は「配信画像の横中央」であって `splitX` ではない。** 受け側は枠 UV の 0.5 で
反転する（`ScreenComposite.shader` の `uvSrc.x = 1.0 - uvSrc.x`）ので、分割位置を動かしても
軸は動かない（動くのは「どちらの半分に出るか」だけ）。同じ注意が
[`line-mirror.js`](../../tools/web-compositor/line-mirror.js) の冒頭にもある。

- **なぜ鏡合わせが要るか**: 「カメラ A を左右対称に据える」を目で測るのは難しく、壁の縁が
  数 cm ずれても気づけない。本番と同じことをその場でやれば、判断が「対称かを測る」から
  「継ぎ目が見えるか」に変わる（`canon/LEDGER.md` 0050 — A は L 字経路の手前で唯一環境が左右対称）
- **なぜ端末が持つか**: これは台本の事実で、**卓が落ちていても据えられる**必要がある。
  だから `GuideSpec.kt` に持ち、卓からは配らない
- ⚠ 鏡合わせは配信されている JPEG を読むので、**需要駆動 encode の需要として数える**
  （`DemandPolicy.hasDemand(..., localPreview)`）。数えないと encode が止まって画が固まり、
  しかも「カメラが壊れた」の顔で出る
- ⚠ **cameraId を付け替えたら補助線の選択は既定へ戻る。** A で出した鏡の軸が C の端末に残ると、
  「合わせるべき目標」に見える線が、合わせてはいけない場所に出たままになる

### 撮る — 撮影パネル（🎬）

卓が配る指示を読み、その場で撮り、尺を確かめ、卓へ渡すまでを端末で終える。

| 端末 → 卓 | 何 |
|---|---|
| `GET /shoot/plan?cam=<id>` | 今日撮るもの（指示文・尺・**画に出るのは頭 N 秒**・採用状況 ✅⚠—） |
| `POST /shoot/collect` | 撮ったテイクを卓へ。回収 → 検分 →（`adopt` なら）採用まで 1 往復 |

- ⚠⚠ **この面はプレビューを覆う。だから面の中に窓を出す**（v0.14.0）。覆ったまま窓が無いと、
  構えている人は**何が録れているか分からないまま撮る**ことになる。窓に出すのは
  **配信されている JPEG そのもの**（録画が書くのと同じ画）で、カメラのプレビューではない —
  プレビューには手ブレ補正の隠れクロップが乗ることがあり、録れる画と画角が食い違う。
  補助線も窓に引く（据えたときと同じ線で構えられる）
- **何を撮るかは卓が正**（`shots.json` ＋ show.json の timeline から導出）。端末に写しを持たせると、
  著作を直した日に手元の指示だけが古いまま残り、現場では「撮ったのに実機で出ない」としてしか現れない
- **録画中に「あと N 秒」を出す。** 要求尺に届いたかを**撮っている最中に**言う（止めてから短かったと
  分かる形にしない）。送った後は卓が測った実測で「⚠ 尺が足りない（要求 1.4 秒 / 撮れた 1.0 秒）」
- **卓が居なくても撮れる。** ファイルは端末に残り、後から送れる

### 画面の作り — 映像の上には何も置かない（v0.15.0・2026-09-05）

配信の画は 4:3。**横持ちの画面はそれより横長なので、映像を高さいっぱいに出しても左右に帯が残る**
（Pixel 7a の横持ちで片側およそ 200dp）。v0.14.0 まではそこを黒いまま空けて、
ステータスとボタンを**映像の上に重ねて**いた。据えている最中に見たいのは映像そのものなので、
重ねるほど仕事の邪魔になる。しかも右のボタン列は上の帯のぶん押し下げられ、
**横持ちでは画面の下からはみ出していた**（列の下端＝録画と保存名が読めない）。

⇒ 帯（レール）を映像の外へ出す。**横持ちは [読む][映像][押す] の 3 列、縦持ちは 3 段。**
**映像の大きさは変わらない**（もともと黒かった所を使うだけ）。

| どこ | 何 |
|---|---|
| 読む（左 / 上） | カメラ ID・状態・**傾き**（水平が出たら緑・出ていなければ橙）・補助線の説明・IP・熱・版 |
| 映像（中央） | プレビューと補助線だけ。**重なるものは無い** |
| 押す（右 / 下） | 🎬 撮影・⏺ 録画・🔒 露出・🔭 レンズ・📐 補助線・🪞 鏡 |

- 幅は [`RailMetrics`](https://github.com/Roiril/fixed-cam-streamer/blob/master/app/src/main/java/com/fixedcamvr/streamer/RailMetrics.kt)
  が帯の実寸から出す（下限 132dp / 上限 200dp）。細い端末では帯が足りないので映像が少し縮む
  —— **押せない UI より映像が小さい方がまし**、という順序で決めている
- ⚠⚠ **カメラ ID の選択とレンズの選択はダイアログへ畳んだ。** 以前は A / B / C / D / 未設定 の
  ボタン列が画面の下中央 —— ちょうど親指の位置に出しっぱなしで、本番中に触れると
  **黙ってスロットが入れ替わる**（受け側は別のカメラの画を A として読む）。
  割り当て済みから動かすときは確認を挟む
- ⭐ **レンズはボタンの字に「いま入っているもの」が出る**（`🔭 超広角 0.5x`）。
  以前は同じ見た目のボタンが 4 つ並ぶだけで、押した後どれが効いているか画面に出なかった
- ⚠ 画面に触れたら復帰と数え直しをするのは **`dispatchTouchEvent`**。以前は root の click で
  受けていたので、**ボタンを押しても数え直されず、操作している最中に暗くなった**
- ⚠ **鏡合わせ中は暗くなるまでが 180 秒**（[`IdlePolicy`](https://github.com/Roiril/fixed-cam-streamer/blob/master/app/src/main/java/com/fixedcamvr/streamer/IdlePolicy.kt)）。
  三脚を触っている手はスマホに無いので、15 秒で暗くなると 15 秒ごとに画面へ触りに行くことになり、
  **そのたび三脚が動く**。切りはしない（切ると忘れたまま全輝度で焼き続ける）

### 撮影パネルは開いた瞬間から撮れる（v0.15.0）

- ⚠⚠ **⏺ はスクロールの外（下端固定）。** 以前は指示・尺・撮れたものと同じ 1 本のスクロールに
  入っていて、5 本のショットが縦に並ぶと**開いた直後は ⏺ が画面の外**にあった。撮るのに
  スクロールが要る面を、撮る道具と呼ばない
- ショットの選択は**横並びの札**（`① 遠い` / `pov_1 ・2 カット`）。縦のリストをやめたぶんが
  指示文と ⏺ に回る
- ⚠ **卓が居なくても一覧が出る**（[`LocalShots`](https://github.com/Roiril/fixed-cam-streamer/blob/master/app/src/main/java/com/fixedcamvr/streamer/LocalShots.kt)）。
  持つのは**台本を見なくても決まる分だけ** — cue の名前・撮り方・撮る長さ。
  **画に出る頭 N 秒と採用状況（✅）は書かない**（卓しか知らない）。画面には
  「卓なし — 手元の写しで撮る」と出て、卓が来たら上書きされる。
  ⇒ 「何を撮るかは卓が正」は変えていない。**入口だけを開けた**

### 卓の在り処は発見の announce から入る

卓（`capture-server.py`）は 5 秒ごとに `fixedcam-discovery/1` の **show-server announce** を
ブロードキャストしている。端末はそれを受けて送信元 IP ＋ `httpPort` を刻む
（`DiscoveryProtocol.showServerPort` → `DeskAddress`）。**現場での設定作業も、卓の画面を
開いておく必要も無い。** 形（IPv4 ＋ ポート）が通った値だけを使う — ブロードキャストは
誰でも出せるので、パス・認証情報・他スキームは弾く。

⚠ **配信アプリは v0.14.0 以上**（補助線・鏡合わせ・撮影パネル・撮影中の窓）。v0.12.0 以前は
端末側に撮影の面が無く、当日は卓からの遠隔操作だけになる。v0.13.0 は面がプレビューを覆うので、
構えている最中に画角が見えない。**v0.15.0 で画面の作りが変わった**（上の「画面の作り」）ので、
現場の手順書に出てくるボタンの位置はそちらが正。

## MJPEG デコード

- **方針**: 自前実装（無料）でまず動かす。安定性が問題になったら AVPro Video（有料）に移行検討
- **ライブラリ**: `multipart/x-mixed-replace` の boundary パース → JPEG バイト列 → `Texture2D.LoadImage`
- **スレッド**: HTTP 受信は別スレッド or `async Task`。`Texture2D.LoadImage` はメインスレッド必須

### 性能ガイドライン（低遅延優先）

- **解像度**: 1080p は Quest 3 でも GC アロケーションが増える。fixed-cam-streamer 側は **既定 640x480（4:3・v0.6.0〜）**（`CameraController.streamWidth/Height` で変更可）。16:9(640x360) はセンサー 4:3 の縦を 1.33 倍クロップして垂直画角を失っていた（Pixel 7a 実測で純正カメラ .5x より狭く見えた真因。水平は zoom 0.53 で既に純正 .5x と一致していた）。4:3 化で純正 .5x と両軸一致（SIFT 実測 水平 0.998 / 垂直 0.989）
- **FPS**: streamer 側で `CONTROL_AE_TARGET_FPS_RANGE=[30,60]` を強制し、暗所で 15fps へ落ちないよう固定。capture フレーム間隔そのものが遅延の下限になる
- **JPEG quality**: 既定 40。視認性は維持しつつ Wi-Fi 帯域を半減 → kernel バッファ滞留減
- **バッファ**: 受信側は単一スロット最新フレーム + バッファ swap で再利用、毎フレ `new byte[]` 発生ゼロ
- **TCP**: 両側とも `TCP_NODELAY=true`（Nagle 抑止）。streamer は `SO_SNDBUF=64KB`、Unity は `SO_RCVBUF=64KB` で kernel 滞留を抑制
- **Texture**: `Texture2D` は事前確保し `LoadImage` で上書き。新規作成しない（**かつ初期化必須** — 未初期化は Quest GPU で白ノイズ化する [unity_pitfalls.md](../memory/unity_pitfalls.md)）

### 遅延対策（実装済み一覧）

| レイヤ | 対策 | 効果 |
|---|---|---|
| Capture (streamer) | `AE_TARGET_FPS_RANGE=[30,60]` で fps 下限固定 | 暗所での 15fps 化を防止（フレーム間隔 = 遅延下限） |
| Encode (streamer) | JPEG quality=40, 解像度 640x480（4:3） | エンコード時間 + 帯域を抑制（4:3 は 16:9 比 +33% 画素だが垂直画角優先） |
| Distribute (streamer) | `FrameDistributor` は単一最新フレームのみ保持 | 配信側の滞留ゼロ |
| Network (streamer→VR) | `TCP_NODELAY=true`, `SO_SNDBUF=64KB`/`SO_RCVBUF=64KB` | Nagle 待機 + kernel バッファ滞留を排除 |
| Multipart header | `X-Capture-Ns` / `X-Frame-Seq` 付与 | 受信側で歯抜け検出・古フレ判定が可能 |
| Receive (Unity) | 単一スロット + バッファ swap、毎フレ new ゼロ | GC 圧ゼロ |
| Drain (Unity) | 受信時点で「最新のみ」上書き | 古フレが Tick まで生き残らない |
| Lag detect | `recv_fps / min(phone_fps, 30) < 0.7` が 1.5s 続いたら強制再接続 | TCP cwnd 縮みっぱなし状態を自動復旧 |
| Monitor | `/health` の `latestFrameAgeMs` / `sentFrames` で原因切り分け | 配信側 stall vs ネットワーク詰まりを判別 |

## ビューア体験レイヤ（追従・切替・フェイルソフト）— 2026-07-19

計画 [.claude/plans/2026-07-19_viewer-ux.md](../plans/2026-07-19_viewer-ux.md)（設計契約・初期パラメータ表はここが正）。

- **追従の緩急**: [`ScreenAnchor`](../../Assets/Scripts/Streaming/ScreenAnchor.cs) は yaw 純スナップを廃止し
  SmoothDamp 0.3s + 角速度上限 110°/s + 逆走ガード（`YawFollowLogic` 純ロジック・テストあり）。
  提示 2.0m・中心 -8°・スクリーン 4/3 倍（角径維持）。再ロック/ロスト復帰はスナップ禁止・減衰合流
  - **⚠ 目標は「頭の正面ちょうど」で、そこで止まる**（2026-08-01 改訂・ユーザー指摘
    「頭の前までぎりぎり到達しないとかはやめて」）。`YawFollowLogic` は
    **deadzone（動き出す閾値）と trail（止まる位置）を別の引数で取る**。旧実装は 1 つの値（10°）を
    両方に使い、目標を `headYaw − sign(e)·10°` に置いていたので、**スクリーンは頭の正面へ一度も
    到達しなかった**。加えて「動き出した後も deadzone を効かせる」と目標へ寄る途中で止まるので、
    **動き出したら deadzone は見ない**（`_moving` のヒステリシス）。到達後は目標へ吸着して静止する
  - スクリーンは trail=0 / deadzone 0.5°（**非シリアライズ const** — SerializeField に戻すと
    シーン・prefab に焼かれた 10 がそのまま効いて再発する）。
    **[`StatusHud`](../../Assets/Scripts/Diagnostics/StatusHud.cs) は逆で trail=deadzone=10° のまま**
    （HUD が視界の真ん中に居座ると映像を隠す）
- **切替は [`CameraSwitchDirector`](../../Assets/Scripts/Streaming/CameraSwitchDirector.cs) に一本化**:
  クールダウン **0.5s**（画面層 `SwitchDirectorLogic`）・最小滞在 **0.5s**（dwell＝人の層 `ZoneProgressionLogic`・2026-07-25 段 B で分離）・
  **cue 再生中は自動切替凍結**（凍結するのは画面だけで時計は進む）・手動後 8s は自動抑止・
  dip-to-black 70/100ms + `SwitchAudioCue`（音源は空スロット）。Web cameraOverride は従来どおり即時
  - **dwell / クールダウン既定 0.5s（2026-07-22 改修）**: 旧 2s は 1.8m 四方の部屋（帯幅 ~0.45m を歩行 0.6〜1.5s で通過）に
    過大で「歩くと切替が起きず、止まった瞬間に遅れて dip 付きで切替」の不具合を出した。`minDwellSec` / `switchCooldownSec` は
    **非シリアライズ private**（既定 0.5s。旧 2/2 が焼き付いたシーン YAML を無効化する LongPressSec と同手法）。
  - **現場調整 = show.json control**: `control.minDwellSec` / `control.switchCooldownSec`（Web 卓「ライブ運用」パネルの数値 2 入力）。
    present 判定は **>0 で上書き / 0・未指定はコード既定 0.5s**（`SwitchDirectorLogic.ResolveTiming`）。ShowControlClient が
    ライブ / 端末キャッシュ / 焼き込みのいずれからでも `ApplyTimingOverride` で Director へ流す（`CachedConfig` へ往復＝PC 不在でも生きる）。
    ShowControlClient→Director 参照は既存シーンで未配線でも `ResolveSwitchDirector`（GetComponent→FindObjectOfType）で遅延解決。
  - **保留キャンセル**: 保留中の目標が現在表示カメラへ戻ったら pending をクリア（境界でうろついた後に古い切替が突然 commit されない）。
    無効カメラ index（ゾーン外・target<0・registry 範囲外）の要求は無視（現カメラ継続）。
  - **Web override 解除後の自己回復**（2026-07-23 修正・テスト `SwitchWiringTests.T5`）: override 中は `ShowControlClient` が
    `PlayerZoneTracker` を `enabled=false` にし、解除で `enabled=true` に戻す。再有効化は `PlayerZoneTracker.OnEnable` を発火し、
    そこで記憶ゾーン（`_current`）を無効化（`InvalidateCurrent`）する → 次 Update で現在位置から再 Pick し通常経路（RequestZone→dwell→Zone commit）で
    ゾーンカメラへ復帰する。これが無いと同一ゾーン滞在のまま `_current` が不変で、次のゾーン跨ぎまで override カメラに表示が固着していた。
    （asmdef 循環回避のため Streaming→Tracking の型参照は作らず、再有効化＝自己回復に委ねる設計。当初案の SendMessage は EditMode テストで
    `ShouldRunBehaviour` アサートを踏むため OnEnable 自己回復へ変更した。）
  - **Web override 中はゾーン自動切替を第一級凍結**（2026-07-23 監査修正・テスト `SwitchDirectorLogicTests`/`SwitchWiringTests.T6`）:
    `SwitchDirectorLogic._overrideActive`（cue/insert 凍結と対称）。`ShowControlClient.Apply` が `CameraSwitchDirector.SetOverrideActive` で
    掛け外しし、override enter/exit で **stale なゾーン保留を無条件クリア**する。旧実装は tracker 無効化だけで Director 内の保留
    （override 直前に積まれた別カメラの dwell 計時）が残り、約 cooldown+dwell 後に `SwitchSource.Zone` で commit して固定が破れ
    LapCounter も誤進行した。解除後の復帰は従来どおり PlayerZoneTracker OnEnable 自己回復が権威（保留は復活させない）
- **デッドバンド自動クランプ**: [`ZoneLayoutApplier`](../../Assets/Scripts/Tracking/ZoneLayoutApplier.cs) は tracker へ渡す
  hysteresisShrink を **`min(hysteresisM, overlapM/2)`**（`ZoneLayoutSolver.ClampHysteresis`）へクランプし、逆転時に 1 回警告。
  現 show.json（overlapM=0.08 / hysteresisM=0.12）は編集なしで実効 **0.04** になり、`Pick` の shrink 保持デッドバンドが復活する
  （shrink が overlap 帯より広いと shrink AABB を出た地点で隣ゾーンの重なり帯も抜けていてデッドバンドが消える）。grid/cuts 両経路に効く。
- **フェイルソフト**: [`SignalLostFx`](../../Assets/Scripts/Streaming/SignalLostFx.cs) — 配信断 600ms → 砂嵐へ 150ms、
  トラッキングロスト/pause 明けは追従凍結 + 弱ノイズ → 減衰合流。体験者には「信号ロスト」に見える
  - **未受信カメラも砂嵐で覆う**（2026-07-22）: 一度もフレームを受信していないカメラ（`LastFrameRealtime==0`・黒プレースホルダ表示中）は
    従来の「最終フレームから経過」判定が発火せず素の黒が露出した。アクティブ stream 参照の切替時刻を追跡し、アクティブ化から
    lostThresholdSec 超過で砂嵐化する（切替・resume-gap でタイマー再シード）。OnDisable で `_SignalLost` を 0 へ戻す
  - ⚠⚠ **砂は置き換えではなく掛け算**（2026-09-03・`canon/LEDGER.md` 0131）。旧実装は
    `lerp(col, 灰, sl)` で無彩の linear 0.34（sRGB 147）へ跳び、暗い部屋の映像（linear 0.049 /
    sRGB 52）から **6.9 倍明るく**なっていた。ユーザー「前に移っている映像との差が激しく
    目がちかちかするし、集中が途切れてしまう」。
    いまは**平均 1.0 の乗数**として乗せるので、局所の明るさと色はそのまま残り形だけが潰れる
    （粒ごとの振れは 0.18〜1.82 倍 ＝ 約 10 倍。砂には見えるが面としては跳ねない）
  - ⚠⚠ **`_SignalFloor` は「砂の下に画が 1 枚も無いか」**（1 = 無い）。**writer は `SignalLostFx` だけ**。
    掛け算なので下が真っ黒だと何も見えず、カメラが 1 台も繋がっていない現場（0025）は
    この砂だけで体験が流れる。**一度も受信していないカメラのときだけ地を持ち上げる**
    （linear 約 0.084 / sRGB 82）。画があるときはほとんど足さない（同 0.008 / 21）
  - ⚠ **地は足す。`max` で切らない** — 切ると下限より暗い所が全部ひとつの値へ潰れて
    **映像の暗部が丸ごと消える**（実測: 暗い部屋のプレートで面がまるごと平らな茶色になった）
  - ⚠ 材質の既定は **1**（明るい側へ倒す）。プレートの上に描く Editor プレビューは
    **明示的に 0 を書く**（`OsdPreview.ResetState` / `IntroPreview`）
- **cue のマスク読込失敗時は cue を中止**（2026-07-22）: [`ScreenOverlayController`](../../Assets/Scripts/Streaming/ScreenOverlayController.cs) は
  `maskUrl` 指定ありでロード失敗した場合、白フォールバック（全面差し替え＝黒背景素材なら live 全面黒）に落ちず cue 発火を中止して live を守る
  （動画/静止画パス両方）。maskUrl 未指定の意図的な全面差し替えは従来どおり白フォールバック。StopOverlay は `_current==null` でも世代を進めて
  in-flight のロード完了を無効化する（stop 後に cue が復活する穴を塞ぐ）
- **動画 cue の Prepare 失敗/タイムアウトも cue を中止**（2026-07-23 監査修正・テスト `OverlayPlaybackLogicTests`/`OverlayVideoFailureTests`）:
  DL 失敗→ストリーミング URL フォールバック→Android `NuCachedSource2 -1` で Prepare が失敗すると `_current` が残留し、
  `CameraSwitchDirector` が cueActive=true のまま自動切替（LapCounter/CueScheduler 含む）を恒久凍結する穴があった。
  `errorReceived`（`OnVideoError`）+ `prepareTimeoutSec`（既定 6s・unscaled）で**現行世代の動画 cue を `AbortCurrentCue` して live へ復帰**。
  世代/Prepare ライフサイクルは純ロジック [`OverlayPlaybackLogic`](../../Assets/Scripts/Streaming/OverlayPlaybackLogic.cs) に分離
  （Prepare 中の `_current` 先出し=切替凍結は正しい挙動として維持。失敗時のみ畳む）。`[ScreenOverlay] cue aborted` ログで検知可
- **グリップ cue トグルは無条件停止優先**（2026-07-22 / 2026-07-23 補強）: [`ShowControlClient.ToggleActiveCameraCue`](../../Assets/Scripts/Streaming/ShowControlClient.cs) は
  何か再生中（`_overlay.Current != null`）なら id 一致に依存せず無条件で停止する。**停止は server 到達時も `stopCue`（空 id）送信に加えて必ずローカル `StopOverlay` を併用する**（2026-07-23 修正・テスト `GripStopLocalTests`）。
  スケジューラ発火 cue は server の activeCue が空のままなので、stopCue 送信だけでは Apply の遷移判定（cueId != _appliedCue）が起きずローカル再生が止まらない穴があった。コマンド送信は Web 表示・heartbeat との整合維持のため残す。
  スケジューラ発火の別 id cue（cue_A_1 等）が表示中でも黒/演出を確実に止められる緊急復帰。何も再生していない時だけ `cue_<camId>` を発火
- **⚠ 卓の停止ボタンには同じ穴が 2026-07-28 まで残っていた**（グリップ側だけ 2026-07-23 に塞がれていた）:
  Web の `■ 演出を止める` / `⛑ 全部解除して自律へ` は `stopCue` + `setCameraOverride:null` しか送らず、
  **タイムラインが自動発火した演出（Take）を一切止められなかった**。Take は `activeCue` を使わず空のまま走るので
  `liveSuppressed`（`ShowControlClient.cs` の `activeCue非空 || override非空`）が false→false で
  `TakeRunner.SetSuppressed` が畳む条件に入らない。皮肉にも「📺 カメラ固定 → 🚶 解除」の 2 段だけが実際に畳んでいた（UI に記載なし）。
  → **`control.takeAbortEpoch: int`（世代カウンタ）を追加**。`runEpoch` と同じ「値の変化で伝える」流儀
  （long-poll は control の差分でしか動けないので、「空を空にする」形の停止は原理的に届かない）。
  Web の `POST /command {type:"abortTake"}` が ++ し、`ShowControlClient` の変化検知が
  [`TimelineDirector.AbortActive`](../../Assets/Scripts/Streaming/TimelineDirector.cs) →
  [`TakeRunner.AbortActive`](../../Assets/Scripts/Streaming/TakeRunner.cs) を呼ぶ。
  **抑止フラグは立てず、once・周回も保つ**（走行中の 1 本だけを畳む＝体験をやり直しにしない）。
  ボタンは `■ 画面を取り返す` に改名し `stopCue` と `abortTake` を両方送る（卓 cue と Take の両系統を畳む）。
  初回 Apply は現在値へ同期するだけで発火しない（起動のたびに中止が走らない）
- **切替 dip は unscaledDeltaTime で進行**（2026-07-22）: `CameraSwitchDirector.AdvanceDip` は timeScale=0 で黒凍結しないよう
  unscaledDeltaTime で進める（StartupFader と同流儀）。OnDisable で dip を解除（`_SwitchDim` を 0・状態 Idle 化）
- **⚠ ScreenComposite の `_SwitchDim` / `_SignalLost` は post FX 数式（Web FS_POST 一致規約）の対象外**（別系統 uniform）
- ステータス表示（[`StatusHud`](../../Assets/Scripts/Diagnostics/StatusHud.cs)）は既定 OFF（右 B で表示トグル・lap/ゾーン/次の cue/信号 ●●○/要再登録 を単一サーフェスに緩追従表示。旧 RuntimeDebugHud の STATE 行相当を統合）
- **⚠ 実機試着未実施**（追従・フェイルソフト系パラメータは SerializeField・現場調整前提）。dwell/クールダウンは
  非シリアライズ既定 0.5s + show.json control で調整（上記）。ゾーン滞在が dwell 未満で駆け抜けると切替が発生しない点は同じ（値で調整）

## スクリーン表示モデル（固定枠 + シェーダ letterbox）

**web-compositor と同じモデル**（2026-06-11 移行）。Screen Quad の Transform は authored のまま**不変**で、フィット・回転・合成・ポスト FX は全部 [`ScreenComposite.shader`](../../Assets/Art/Shaders/Streaming/ScreenComposite.shader) の UV 空間で完結する。

- [`MjpegScreen`](../../Assets/Scripts/Streaming/MjpegScreen.cs) はテクスチャ供給 + contain-fit スケール（`_LiveScale`）計算のみ。Texture2D 実寸からアスペクトを毎フレーム追従（`/info` は使わない — JPEG 実寸が真実）
- ソースは contain-fit、はみ出しは黒 letterbox。**枠サイズはカメラ切替・縦横切替でも不動**
- streamer は常に正立フレームを送る（`rotationDeg=0`）ので回転補正は不要。緊急時は `MjpegScreen.uvRotSteps`（90 度単位の UV 回転）で手動補正
- 旧 `ApplyOrient`（/info メタで Transform を回転・変形）は**廃止**。`autoOrient` / `orientTarget` / `useIsPortraitForRotation` 等のフィールドはもう存在しない

### 映像差し替え（オーバーレイ合成）

[`ScreenOverlayController`](../../Assets/Scripts/Streaming/ScreenOverlayController.cs) + [`OverlayCue`](../../Assets/Scripts/Streaming/OverlayCue.cs)（ScriptableObject、`Create > FixedCamVr > Overlay Cue`）:

- cue = 事前撮影 VideoClip / 静止画 / URL ソース + マスク Texture（R チャンネル、スクリーン枠空間、白=差し替え）+ フェード時間 + 再生区間
- 固定視点なのでマスクは事前撮影フレームから作ればそのまま位置が合う（web-compositor で検証済みの理屈）
- **⚠ マスクだけは contain-fit を通らない。座標系は必ず「スクリーン枠空間」（16:9）**（2026-07-30 に是正）。
  シェーダは live / overlay / CG を `_LiveScale` / `_OverlayScale` / `_CgScale` で contain-fit するが、
  **`_MaskScale` は存在せず生 uv で読む**（[`ScreenComposite.shader`](../../Assets/Art/Shaders/Streaming/ScreenComposite.shader) の `SampleBase`）。
  したがって 4:3 のソース座標のままマスクを焼くと、実機でだけ水平 1.33 倍・枠幅の最大 12.5% 外側へずれる。
  - **卓では原理的に見えない**バグだった（工房が 640×480 の枠でプレビューしていたため）。
    実際に現地の実素材 2 件（`cue_hand_B` / `cue_ningyo_A`）がずれたまま合成されていた
  - 生成器 3 つはすべて枠空間で焼く: 素材工房の自動差分（`atelier.js` の `bakeMask`）/
    cue エディタのスライダ / [`make-diff-mask.py`](../../tools/web-compositor/make-diff-mask.py)。
    寸法の正は `common.js` の `MW`/`MH`（640×360）で、CLI の `FRAME_W`/`FRAME_H` と対
  - **枠が 16:9 であることは Unity 側のテストが固定する**（`ScreenFrameAspectTests`）。
    Quad の localScale・`screenAspectOverride=0`・卓の 2 定数・シェーダに `_MaskScale` が無いこと を突き合わせる。
    **枠のアスペクトを変えるならこの 4 つを同時に直す**（片方だけだと沈黙して食い違う）
  - マスクは「素材のどこを使うか」ではなく「**スクリーン枠のどこを差し替えるか**」。素材が letterbox される
    領域に白を置いても、そこに overlay は無いので live が消えて黒が出るだけ（`ContainUv` が範囲外を 0 にする）
- 発火: キーボード（CueBinding.key、Editor+Link オペレータ用）/ `PlayCue()` / `StopOverlay()` / **Web オペレータ卓の演出 ON/OFF（show.json `control.activeCue`）**
- ポスト FX（vignette/grain/scanline 等）はシェーダ内 = スクリーン内容にだけかかる。視界全体への FullScreenPass とは独立

### Web 連携の挙動（2026-06-17 / show.json cue 由来）

- **フェード**: `cue.fadeIn/fadeOut`（Web の演出トグル横の秒入力）で ON=fade-in / OFF=fade-out
- **ループ無し + 再生終了で自動復帰**: `cue.loop=false`。動画が自然終端（`loopPointReached`）か `trimEnd` に達したら自動 `StopOverlay` → live へフェード復帰（[`ScreenOverlayController.Update`](../../Assets/Scripts/Streaming/ScreenOverlayController.cs) の trimEnd 監視）
- **再生区間 trim**: `cue.trimStart` へシークして再生、`trimEnd>0` で停止（`trimEnd<=0`=最後まで）
- **⚠ 動画 URL は UnityWebRequest でローカル DL してから `file://` 再生**（[`GetLocalVideoUrlAsync`](../../Assets/Scripts/Streaming/ScreenOverlayController.cs)）。Android ネイティブ VideoPlayer は Python http.server(HTTP/1.0) からの HTTP ストリーミングを扱えず `NuCachedSource2 error -1` で落ちるため（画像/マスクは UnityWebRequest なので直 URL で OK）。URL 毎にキャッシュ。スペース入りファイル名は Web 側が percent-encode
- **マスクのフェザー**は Web が cue 保存時に PNG へ焼き込む（Quest はマスクをそのままサンプル）。**色統計マッチング・ラプラシアンは Web プレビュー専用**で Quest 実機の ScreenComposite はハード合成（`lerp(live,overlay,mask)`）
- **post-FX 数式**は ScreenComposite と Web の `FS_POST` を一致させてある
  （露出 → 色温度 → **色かぶり** → コントラスト → **黒浮き** → 彩度 → ヴィネット → 走査線 → グレイン の順・式）。
  **色かぶり（tint・緑↔マゼンタ）と黒浮き（lift）は 2026-07-26 追加**（監視カメラらしさに要る 2 軸。既定 0 = 旧データと同じ絵）

## カメラ管理

- 各カメラは `ScriptableObject` で URL・解像度・名前を定義（`Assets/Settings/Cameras/` — Phone01/02/03 の 3 台。StreamingLogic prefab の sources に登録済み）
- 既定値: port=8080, videoPath=/video, infoPath=/info, healthPath=/health（fixed-cam-streamer 互換）
- DroidCam 互換時は port=4747, videoPath=/mjpegfeed?WxH, infoPath="" にする
- 切替時は **常時受信を維持**（再接続コスト回避）し、表示先 Quad の `enabled` または RenderTexture 切替で対応
- 同時 3 台までは Quest 3 で実用域、それ以上は要計測

## show.json = 設定契約（IP / カメラ別画像加工を実機へ流す）— 2026-06-16

Web オペレータ卓（`tools/web-compositor/`）の `show.json` が **Web と Quest 実機の共有設定**。
カメラの接続先（host/port/auth）と画像加工（post）をここで決め、実機が参照する。計画
[.claude/plans/2026-06-16_web-config-to-quest.md](../plans/2026-06-16_web-config-to-quest.md)。

- **`cameras[i].host/port/auth`**: 従来 Web プレビュー専用だったが、**Unity 実機も読む**ようになった。
  [`ShowControlClient.ApplyCameraEndpoints`](../../Assets/Scripts/Streaming/ShowControlClient.cs) →
  [`CameraStreamRegistry.ApplyEndpoint`](../../Assets/Scripts/Streaming/CameraStreamRegistry.cs) →
  [`CameraSource.ApplyRuntimeEndpoint`](../../Assets/Scripts/Streaming/CameraSource.cs)（`[NonSerialized]` 実行時上書き、
  **焼き込み .asset を汚さない**＝git 巻き込み防止）→ 変化時のみ [`CameraStream.ReapplyConnection`](../../Assets/Scripts/Streaming/CameraStream.cs) で MJPEG 張り直し。
  **空 host は override 解除＝焼き込み値へフォールバック**（Web 未設定カメラの保護）
- **`cameras[i].post`**（任意）: カメラ別の明るさ・色補正。アクティブカメラ切替時に
  [`ShowControlClient.ApplyPostForActive`](../../Assets/Scripts/Streaming/ShowControlClient.cs) が適用。
  未設定カメラはトップレベル `post`（global＝全体グレーディング）にフォールバック。
  JsonUtility が null 入れ子を既定値で書く罠を避けるため「個別 post を持つか」は明示 `hasPost` bool を正にする。
  **⚠ 粒度はカメラ単位のみ** — cue にも周（schedule）にも紐づかないため周ごとの画質変化はできない（下記「編集の粒度と自由度」参照）
- **永続化（PC 不在でも参照）**: 受信 show.json を `persistentDataPath/show_config.json` にキャッシュし、
  起動時に再適用。**優先順位は 焼き込み .asset < 端末キャッシュ < ライブ long-poll（後勝ち）**。
  キャッシュは「一度ライブ受信した後」生成されるので、完全新規インストール＋PC 不在の初回は焼き込み値で起動
  - **⚠ この優先順位は「APK を焼き直せば設定も新しい」を成り立たなくする。** キャッシュが焼き込みより
    上なので、古いキャッシュが残っている端末はビルドし直しても前の設定で走る。実測（2026-07-31）で
    PC の show.json・焼き込み・Quest 2 台のキャッシュの **4 者がずれていて正しいのは 1 つだけ**だった
    （片方の Quest にだけ `run.intro.startLineId` があり、導入の始まり方が機ごとに違った）。
    しかも `timeline.rev` は全部 21 で一致していて **rev では気づけない**
  - **⭐ 2026-08-14 から、APK が変わったキャッシュは起動時に捨てる**（`CachedConfig.buildGuid` ＝
    `Application.buildGUID` を照合）。焼き直した APK は**焼き込み値で始まる** ＝ スタッフの直感どおり。
    卓が生きていれば long-poll が即座に配り直すので、現場の運用は変わらない。
    ⚠ 消えるのは show の設定だけで、**位置合わせ（`registration.json`）は別ファイルなので残る**。
    手で消す `quest-fleet.py reset-config` も従来どおり使える。
    契約は `ShowConfigPrecedenceTests.LoadAndApplyCache_DropsCacheWrittenByAnotherApk` が固定する
  - → 実機は [`ShowControlClient.ConfigOrigin`](../../Assets/Scripts/Streaming/ShowControlClient.cs) /
    `DescribeConfig()` で**使った設定の出所と骨格**を持ち、`[XP] ev=config` として吐く。
    [`analyze-xp-log.py`](../../tools/analyze-xp-log.py) の `config_from_show()` が PC の show.json から
    同じ要約を作って突き合わせ、違えば **FAIL**（「出なかった演出」を設定ずれのせいで誤検出しないため）。
    **項目を足すときは C# と Python を対で直す** — 片方だけだと沈黙して食い違う。
    キャッシュの掃除は `py -3.11 tools/quest-fleet.py reset-config <serial>`
- **server 不在でも ShowControlClient は動く**（旧コードは `enabled=false` で自滅していた）。
  long-poll / heartbeat だけスキップし、キャッシュ適用とカメラ別 post のゾーン切替連動は成立する

## 事前オーサリング済み cue スケジュール（周回×ゾーン発火 + APK 焼き込み）— 2026-07-17

「何周目のどのゾーンで、どの cue を差し込むか」をビルド前に決めて APK に焼き込み、現地 PC 不在でも
Quest 単体で自動発火する仕組み。計画 [.claude/plans/2026-07-17_pre-authored-cue-schedule.md](../plans/2026-07-17_pre-authored-cue-schedule.md)。

- **cues は複数化**: id は任意（Web 卓の規約は `cue_<camId>_<n>`）。1 カメラに複数 cue を持てる。旧単数 id（`cue_<camId>`）も後方互換で動く
- **`layout.regPoints[]`**: 位置合わせのタッチ基準点 `{x, z, label}`（course 座標・順序=タッチ順・2〜5 点。Web 卓フロアマップの「📍 位置合わせ点」で配置。不在なら Unity は従来既定 2 点にフォールバック）— 2026-07-19
- **`layout.course.order`**: 順方向のカメラ巡回順（`order[0]`=スタート領域）。Web 卓フロアマップの「周回コース」で編集（grid の塗りから角度順提案 + CW/CCW トグル）
- **`schedule.entries[]`**: `{lap, camera, cueId, delaySec, once}`。lap は **1 始まり**、camera は**カメラ index**。Web 卓の**タイムライン UI**（2026-07-19〜 マトリクスから改装。「1周目: A\|B\|C → 2周目: …」を `course.order` 順に横連結・区間クリックで cueId / delaySec / once を編集）で編集
- **周回検知** = [`LapCounter`](../../Assets/Scripts/Tracking/LapCounter.cs)（進行ポインタ方式）: 確定ゾーンが `order[(i+1)%n]` に一致した時だけ前進、`order[0]` 復帰で lap++。**逆走・行き来・スキップ・境界 jitter はカウントしない**
  - **⚠ 2026-07-25（段 B）で駆動点が変わった**: 旧「Director の `SwitchCommitted` の `SwitchSource.Zone` だけを算入」→ 新
    **`CameraSwitchDirector.ZoneCommitted`（ショーの時計）を購読**。時計は [`ZoneProgressionLogic`](../../Assets/Scripts/Streaming/ZoneProgressionLogic.cs)
    が dwell だけで確定させ、**画面の凍結（cue / インサート / override）にも dip にも影響されずに発火する**。
    したがって**演出中に体験者が歩いても周回は止まらない**（設計 [2026-07-25_shot-timeline-foundation.md](../plans/2026-07-25_shot-timeline-foundation.md) 不変条件 4）。
    スタッフ手動 A・Web cameraOverride・インサートの画面切替は `ZoneCommitted` を発火しないので、構造的に周回へ入らない

#### ⚠⚠ 周回数は 2 つある — 引き返したときだけ食い違う（2026-08-17）

体験者は**演出の途中で後ろのカメラへ戻ることがある**。旧実装は進行の周をそのまま区間キーに使っていたので、
`1周目C → 2周目A → 引き返して C` が **`(2, C)`** と読まれ、
**体験者が 2 周目の B を一度も通らないまま 2 周目 C の演出が消費されていた**（`once` なので二度と出ない）。

| 名前 | 何 | 誰が読むか |
|---|---|---|
| **進行の周**（`LapCounterLogic.CurrentLap`） | 順路を 1 周踏破するたびに上がる。**単調増加で減らない** | 体験の終了判定（`lap > totalLaps`）／ StatusHud の「N周目／全3周」／ heartbeat |
| **区間の周**（`LapCounterLogic.SegmentLap`） | **いま体験者が居る区間**の周。引き返すと「前にそこに居たときの周」へ戻る | **区間キー (lap, camera)** ＝ 演出・区間 post / BGM・端末内録画・実測滞在 |

`CueScheduler.NotifyCameraEntered` と `CameraEntered` は **`(camera, segmentLap, progressLap)` の 3 引数**。
購読者に「どちらの周か」をコンパイラが必ず選ばせるためで、片方だけ暗黙に流すと選び間違いが沈黙する。

- ⚠⚠ **`ShowRunDirector.NotifyLap` へは進行の周を渡す。** 区間の周を渡すと、
  帰りの A に着いた体験者が 1 区間引き返した瞬間に周回が戻り、**体験が終わらなくなる**
  （終了判定は `lap > totalLaps` の 1 本しか無い）
- ⚠⚠ **前進の判定には「直前に居たカメラ」も要る。** 進入カメラが `order[pos+1]` と一致するかだけを
  見ると、**引き返した後の 1 歩**で外れる — `A(2周目) → C → B` のとき、ポインタは `pos=0(A)` のままなので
  B は「A の次」に見え、実際は C から戻ってきたのに前進として数えて `(2,B)` を先取りする。
  前進は **`prev == order[pos] && camera == order[pos+1]`** のときだけ。
  引き返している間ポインタは据え置かれ、順路へ戻れば元どおり進む
- ⚠ **区間の周は進行の周を追い越さない**（`LapCounterTests.SegmentLap_NeverExceedsProgressLap`）。
  追い越すと、まだ到達していない周の演出・録画が生まれる
- ⚠ **一度録れた区間は録り直さない**（`SegmentRecorder`）。`SegmentRecordWriter` は `FileMode.Create` で
  開くので、引き返して同じ区間へ戻ると**1 周目の映像が数秒の断片に置き換わる**。
  3 周目に流すのはその映像 ＝ 作品の核なので、先に録れた方を守る
- 観測は `ev=seg lap= cam= plap=`。**`lap` と `plap` が食い違っていたら引き返している。**
  1 つしか出さないと「引き返して再演された」と「著作が二重に置かれている」を走行のログから区別できない
- ラン開始でカメラごとの記憶も消す（前の体験者の足跡が次の体験者の 1 周目に化けない）

- **ラン（体験者 1 人分）**: `control.runEpoch`（int・既定 0）の**変化**で LapCounter リセット（lap=1・再シード）+ CueScheduler の once 発火済みクリア。Web ライブ運用パネルの「▶ ラン開始」= **runEpoch++ ＋ `cameraOverride=null` ＋ `activeCue=null` を 1 回の postState で同時に書く**（2026-07-25〜。旧実装は runEpoch だけで、前の体験者のカメラ固定・再生中 cue が次のランへ持ち越された。override / activeCue は端末キャッシュには載らないが show.json には永続するため、卓を立てて Quest を繋いだ瞬間に再適用される＝「歩いても切り替わらない」事故になっていた）。armed なラッチは Web 卓ヘッダ直下の警告バーが show.json 由来で常時可視化する（Unity 未接続でも出る）。PC 不在時は **右グリップ 2 秒長押し** = ローカルランリセット（2026-07-20〜。旧: Staff 左スティック押し込み）。heartbeat に `lap` / `cam` / `mode`（NORMAL/REG）を載せる（Web でラン状態が見える）
  - **2026-07-28 追記**: 「▶ ラン開始」は `slots=[]`（素材スロットの束縛解除）も書き、`abortTake` を併送する。
    旧実装は**前の体験者のために束縛した素材が次のランの演出に出る**穴があり、走行中の演出も畳めなかった。
    卓のラッチ列挙は `latches()` が単一の正で、警告バー・本番前チェック・⛑・▶ ラン開始 が同じ配列を消費する
    （固定 / 演出 / スロット = 解除対象、📌 手動固定 / 発見 OFF = 警告のみで外さない）
  - **⚠ heartbeat の `cam` は「画面に映っているカメラ」で、体験者の居場所ではない**（2026-07-28）。
    演出のカットが別カメラを映している間、両者は食い違う。人の居場所は **`zoneCam`**（ショーの時計
    `CameraSwitchDirector.TryGetCurrentZoneCamera` の確定値・-1 = 未確定）を新設して別に送る。
    卓は `cam` を「ゾーン」と称して出し、しかもその index で区間を引いていたため、
    **演出中は「このゾーン / 次の演出」が別区間の内容になっていた**。区間の照会は必ず `zoneCam` で行う。
    あわせて **`takeId`**（走行中の演出 id・空 = 演出なし）を送り、卓が「いま画面を握っているのは誰か」
    （自律 / 演出 / 卓が固定 / 手動 cue）を 1 行で出す
  - **⚠⚠ heartbeat は「送っただけ」では届いていない。受け側があるか必ず確かめる**（2026-08-07）。
    契約監査で、Unity が送っているのに卓が 1 度も読んでいないフィールドが 3 つ見つかった。
    送信側だけ見ていると完成して見えるので、**フィールドを足したら卓側の消費箇所も対で書く**。
    - **`cameraCount`** = Unity が実際に初期化できたカメラ本数。**この照合のために送っているフィールド**
      なのに、卓は `show.json` の `cameras.length` と比べていなかった。演出のカットは camera を
      **index** で指すので、ズレると狙った映像と違うカメラが出る（範囲外なら §6.4 でカットごと飛ぶ）。
      どちらも画面には「演出が出ない / 違う画」としか現れず、原因に到達できない。
      ⚠ 2026-07-31 に host 未設定の 1 台が registry の生成ループで例外を投げて**全カメラが
      初期化されない**事故が起きており、そのとき卓は全部 ✅ のままだった。
      いまは本番前チェックが ❌ で名指しする（0 台は別の文言。**送らない旧 Unity では出さない**）
    - **`endHolding`** = 終了条件は満たしたが走行中の演出を見せ切っている。この間
      `ShowRunLogic` は `_phase` を `Finished` にしない（`_endHeldSec` の分岐）ので、
      **`phase` だけ見ていると「本編」のままで、体験がもう終わっていることが分からない**
    - **`lapSec`** = いまの周の経過。企画書の「各周およそ 30 秒」は周単位でしか判定できない
  - ⚠⚠ **当日「気づけない失敗」の 2 系統を載せた**（2026-08-30・卓のライブ状態に 2 行）。
    どちらも**画にも録画にも 1 ビットも出ない**ので、卓が出さないと現場で検知できない。
    - **`sndResolved` / `sndMissing` / `sndAudible`**（`ShowSoundDirector` 由来・`-1` = 実行体が居ない）。
      `CLAUDE.md` 自身が「**音は録画に映らない**ので `ev=sfx` / `sndBuilt` が唯一の証拠」と書いているのに、
      その証拠は logcat にしか無く**当日は読めなかった** ＝ 無音のまま全員を通す経路が空いていた。
      卓は `sndMissing > 0` で ❌、`sndAudible == 0` で ⚠（導入の頭は正しく無音なので止めない）。
      ⚠ 「BGM」欄は `show.json` のトラック定義を見ているだけで、実機が鳴らせているかは見ていない
    - **`ctrlLConnected` / `ctrlLTracked` / `ctrlRConnected` / `ctrlRTracked`**
      （`OvrControllerBridge.ControllerStateProvider` 経由。Streaming は OVR を参照しない規約なので
      向こうから書きに来る）。**左は体験者の唯一の入力**で、切れると `CommsPanel.ApplyHint` が
      押し方の案内を黙って空文字にするだけ ＝ 報告が 1 件も上がらないまま終幕を迎える。
      ⚠ **接続と位置は別物**（伏せてある / 体の陰では接続だけ true で姿勢が無効 = 正常）

- **実測滞在時間**（2026-07-25〜）: 区間 (lap, camera) に体験者が実際に居た秒数を
  [`SegmentDwellLog`](../../Assets/Scripts/Streaming/SegmentDwellLog.cs)（純ロジック）が測り、heartbeat の
  `dwell[]`（`{lap,camera,sec}`）で卓へ送る。駆動は **`CueScheduler.CameraEntered`（＝ショーの時計 `ZoneCommitted` 由来）**
  だけなので、手動 A・Web 固定・インサートの**画面切替では動かない**。スタッフ介入中（`activeCue` / `cameraOverride` 非空）と
  ラン開始では計時中の区間を捨てる。送信失敗分は次の heartbeat へ戻す（上限 64・古い方から破棄）。
  卓は `capture-server.py` が `dwell_stats.json` へ集計し `GET /dwell/stats`（`POST /dwell/reset` でクリア）で返す。
  Web リボンはこれを区間フッタ（`実測 平均 8s（最短 6.5s / 2 回）`）と「開始位置が滞在を超える演出」の ⏱ 警告に使う
  — 計画 [2026-07-25_shot-timeline-foundation.md](../plans/2026-07-25_shot-timeline-foundation.md) §4 が
  「作者が『山場が出ない』危険に気づく唯一の手段」と位置づけたもの
- **発火** = [`CueScheduler`](../../Assets/Scripts/Streaming/CueScheduler.cs): (lap, camera) 一致 + delaySec 後に `ScreenOverlayController.PlayCue` を**ローカル直接**呼ぶ（サーバ不要）。**`control.activeCue` が非空の間は抑止**（ライブ手動操作が常に優先）。once=true はラン内 1 回
- **APK 焼き込み**: Web 卓「📦 ビルド用エクスポート」（`POST /export-build`）が show.json + 参照アセットを `Assets/StreamingAssets/show/` へコピーし、URL を `sa://assets/<file>` に書換。Unity 側は [`ShowAssetResolver`](../../Assets/Scripts/Streaming/ShowAssetResolver.cs) が `sa://` → `StreamingAssets/show/assets/` に解決（Android は jar: URL、動画は VideoPlayer 直接パス）。起動時に `StreamingAssets/show/show.json` を読み、優先順位は **焼き込み < 端末キャッシュ < ライブ**（従来の後勝ちを維持）
- **⚠ `Assets/StreamingAssets/show/` はコミット禁止**（gitignore 済み）。エクスポート時点のカメラ host（現場 DHCP IP）が verbatim に焼き込まれるため。ビルド直前に現場でエクスポートし直すのが正
- `CachedConfig`（端末キャッシュ）に cues / schedule / course を保存するようになった（旧: cues 欠落でオフライン発火不可だった）
- **⚠ 実機未検証**（2026-07-17 実装。コンパイル・EditMode テスト 75/75・Web UI・エクスポートは検証済み）

## タイムライン第一級オーサリング（旧 `timeline` v2）— 2026-07-19 / **2026-07-25 に読み取り専用**

> **この節は「過去データの意味」を読むための記録**。2026-07-25 に演出・カット（v3）へ一本化したので、
> **卓はもう v2 を書かないし、Unity にも v2 の実行体は無い**（旧 `InsertController` / 区間 `cues[]` の
> CueScheduler 供給は削除）。端末キャッシュ・焼き込みに残る v2 は
> [`TimelineMigration`](../../Assets/Scripts/Streaming/TimelineMigration.cs) が読み込み時に演出へ変換する。

体験オーサリングの正面を**周回×ゾーン区間（セグメント）のタイムライン**へ再設計した。計画
[.claude/plans/2026-07-19_webui-timeline-authoring.md](../plans/2026-07-19_webui-timeline-authoring.md)（スキーマ・セマンティクスの単一ソース）。
旧「カメラ列の下に cue がぶら下がる」構造と `schedule.entries` の (lap×camera) グリッドを置き換え、
区間 1 個から **cue 割当（複数・パラメータ上書き付き）・画像加工 post の上書き・インサートショット**を一括編集できる。

**セグメント = (lap, camera)**。show.json トップレベルに `timeline` を新設（後方互換：`timeline.rev>0 && segments 非空`
の時だけ Unity が `schedule.entries` を無視して supersede。無ければ従来経路が生きる）。

```jsonc
"timeline": { "rev": 1, "segments": [
  { "lap": 2, "camera": 0,                       // lap=1始まり / camera=index（course.order と同キー）
    "cues": [ { "cueId": "cue_A_1", "delaySec": 0, "once": true,
                "override": { "strength": 0.5, "fadeIn": 1.2, "fadeOut": 0.8,
                              "trimStart": 0, "trimEnd": 0 }, "hasOverride": true } ],
    "post": { /* PostParams 9 項目 */ }, "hasPost": true,          // このゾーン滞在中の post 上書き
    "insert": { "anchor": "exit", "camera": 2, "durationSec": 4,   // 別カメラを N 秒差し込む
                "delaySec": 0, "cueId": "cue_C_scare", "once": true,
                "post": { /* PostParams */ }, "hasPost": false }, "hasInsert": true } ] }
```

- **cue**: 区間進入（Zone commit）+ delaySec で発火。複数可。`override` は ResolveCue 結果（OverlayCueData 複製）への差分パッチ。`control.activeCue` 非空中は抑止（ライブ優先）
- **post 上書き**: 解決は **segment > cameras[i].post > global** の 3 段（Unity `ApplyPostForActive` を拡張。insert 中はさらに insert post が最優先の 4 段）。区間離脱（次の Zone commit）で解除。**「1 周目の B は普通・2 周目の B は赤く」が可能になった**（旧: カメラ単位固定で不可）
- **インサートショット**（旧実装 `InsertController` + `InsertLogic` は 2026-07-25 に削除。
  いまは同じ意味のものが「演出のカット（`source: live`）」として `TakeRunner` で走る）:
  - `enter`: 進入 + delaySec 後、insert.camera を durationSec 秒表示 → 最新ゾーンカメラへ復帰
  - `exit`: **このゾーンを Zone 切替で離れる瞬間**、dip の黒中に insert.camera へ差し替え durationSec 秒 → 最新ゾーンカメラへ復帰。体験者は A→黒→C(N秒)→黒→B と見え、中間カメラのフラッシュを見せない（ユーザー要求「A→B に切り替わる前に C に演出を N 秒」の実装形）
  - 切替は dip-to-black 付き（`CameraSwitchDirector` の `SwitchSource.Insert`）。insert 表示中はゾーン自動切替を凍結（cue 凍結と同型）。**周回カウントは実ゾーン移動の commit 時に通常どおり 1 回**（Insert 切替は LapCounter が数えない）
  - **insert 凍結の解除は必ず後片付け経由**（2026-07-23 修正・テスト `SwitchWiringTests.T1/T2`）: `InsertController.ResetRun` / `SetInserts` は進行中インサート（表示中）があれば先に `CleanupActiveInsert`（insert cue 停止・insert post 解除・`director.InsertReturn`）で畳んでから `InsertLogic` をリセットする。畳まないと `CameraSwitchDirector._insertActive`（凍結）が `InsertReturn` 単一経路でしか降りず、ゾーン自動切替が恒久凍結する（ショー中のタイムライン編集・体験者交代で踏む）。`SwitchDirectorLogic.Reset`（OnEnable 経路）でも `_insertActive`/`_cueActive` を false へ安全初期化する
  - **insert 中の実ゾーン移動**（2026-07-25 段 B で機構が変わった・テスト `SwitchWiringTests.T4`）: insert 表示中に体験者がゾーンを移ると、
    **その時点で時計（`ZoneCommitted`）が発火し周回・区間追跡へ即反映される**（画面は insert のまま）。insert 終了時の復帰先は
    `CameraSwitchDirector.TryGetCurrentZoneCamera`＝**時計の確定ゾーン**を読んで決める（復元ではなく再計算）。
    旧実装の `InsertReturn(returnCamera, asZone:true)`（復帰 commit を Zone に偽装して周回を辻褄合わせする）は**廃止**した
- **present-flag は宣言 bool が正**（2026-07-23 監査修正・テスト `TimelinePresentFlagsTests`/`TimelineFixtureContractTests`）:
  `hasPost` / `hasInsert` / `hasOverride` は必ず書く。Unity は [`TimelinePresentFlags.Reconcile`](../../Assets/Scripts/Streaming/TimelinePresentFlags.cs) で
  **`flag = 宣言bool && object != null`（AND）** に確定し、ライブ / 焼き込み / キャッシュの 3 経路一律に適用する。
  旧実装は `!=null` 純導出で、Web（`timeline.js serialize()`）が flag=false でも既定オブジェクトを常時出力していたため
  **全 present-flag が true に化け、全区間に幽霊 exit インサート（camera0 を 4 秒）が武装**する critical バグだった。
  Web 側も flag=false 時に入れ子キーを省略する形へ変更（`timeline-model.js` に serialize/normalize/migrate を純関数抽出・
  `node --test tools/web-compositor/*.test.mjs` で固定・共有 fixture `Assets/Tests/Fixtures/show_timeline_canonical.json` を生成）。
  **⚠ `CameraDef.hasPost` の `!=null` 導出は 2026-07-30 に破れた。** 「Web が未使用キーを削除する」
  という前提が崩れていて、卓は未設定のカメラに **`"post": null` を明示的に書いていた**。
  JsonUtility はそれを**既定値の実体**で埋めるので `post != null` が true に化け、
  **素通しのカメラ個別 post が global の加工を丸ごと上書き**する。実機は 134 サンプル中 122 が
  `sat1.00 / vig0.00`（無加工）で、3 周目の区間 post が効いた 12 サンプルだけが加工されていた。
  しかも **卓では `??` が null を global へフォールバックするので正しく見える** ＝
  「卓で見えている絵と実機が違う」という最悪の破れ方をしていた（ユーザーが実機で気づいた）。
  → 導出を **`post != null && !post.IsDefaultLike()`** に変更（`PostParams.IsDefaultLike()` は
  全 11 軸が既定かを見る）。回帰は `TimelinePresentFlagsTests` が固定する。
  卓が `post` キーごと省略するようになれば元の導出でも正しいが、**Unity 側で防御する方を正とする**
  （「卓がどう書くか」に依存する判定は、今回まさにそこで破れた）
- **JSON キー `override`** は C# 予約語のため Unity 側は `@override` フィールドで受ける（実行時フィールド名は `"override"` で JsonUtility が正しく往復。実型で確認済み）
- **CachedConfig に timeline を保存**（オフライン発火。旧「cues 欠落」事故の教訓を踏襲）
- **Web 検証モード**（実機不要）: Web 卓「▶ 検証」で **矢印キー**（→ 次ゾーン / ← 1手戻る / R 先頭 / Esc 終了）。Unity セマンティクス（LapCounter 順方向進行・CueScheduler・insert・post 3 段）を JS ミラーで再現し、発火順と「体験者に見える画」を WebGL プレビューで確認。**ローカルのみで show.json / 実機は書かない**
- **⚠ Quest 実機未検証**（2026-07-19 実装。Unity コンパイル・EditMode 126/126・Web 卓ロード + 検証エンジン + セグメントインスペクタはブラウザ検証済み。insert の dip 演出・post 切替の見た目は現場調整前提）

### 演出の 1 語彙化（show.json v3 `takes[]` / 多段カット）— 2026-07-25

**cue（オーバーレイ）と insert（カメラ差し込み）を「演出(Take) / カット(Step)」1 語彙へ畳んだ。**
設計・スキーマの正本は [2026-07-25_shot-timeline-foundation.md](../plans/2026-07-25_shot-timeline-foundation.md)（§6 が契約）。

- **実行体は [`TakeRunner`](../../Assets/Scripts/Streaming/TakeRunner.cs) 1 つ**（2026-07-25 に一本化）。
  `ShowControlClient.PushCueSource` は版を見ずに `TimelineMigration.EnsureTakes` で takes[] を確定させてから
  `TimelineDirector.SetTimeline` へ渡す。**画面の所有者は常に 1 人**という不変条件が、経路の本数からも保証される
- **区間 = (lap, camera) は変わらない**。区間に `takes[]` が 0..N 本ぶら下がる。1 本の演出が
  **カット列**を持つ（`live:<cam>` / `inherit` / `clip` / `still` ＋ オーバーレイ cueId ＋ 尺 ＋ 遷移）
- **`start.ifMissed`**: `enter+t` の演出が **t に達する前に体験者が区間を出たら、離脱の瞬間に発火**する
  （既定 `fireOnExit`）。`skip` なら出さない。**歩くのが速い体験者に山場が出ないまま終わる事故**への対策で、
  スキーマなので後から足せない＝最初から入っている
- **画面が塞がっているだけなら捨てない — 区間内で待つ**（2026-07-27 改訂・ユーザー報告による）:
  演出が走っているあいだに別の演出の開始条件が来たら、**その区間に居る限り武装したまま待ち、画面が空いた時点で出る**。
  旧実装は即座に決着させて捨てていたため、**著作した演出が黙って消えた**（実害: 3 周目 B の離脱演出 8 秒が C の滞在を
  丸ごと覆い、C の進入演出 3 カットが一度も出なかった）。開始条件は満たした瞬間に `Ready` へラッチする
  — **ライントリガーは事象で猶予 0.6 秒しか残らない**ので、ラッチしないと「別の演出が走っていた」だけで永久に失われる。
  ライブ卓の介入中（`activeCue` / `cameraOverride`）は従来どおり即決着（人間の判断が優先）
- **捨てたら必ず報告する**: `TakeRunnerLogic.TakeDropped` → 実機は `[TakeRunner] 演出が出ないまま区間が終わった` 警告、
  シミュレータは `drop` イベント → 卓のログに赤字。**黙って消えるのが最悪**で、著作者には発見手段が無い
- **作れない組み合わせは卓が選ばせない**（2026-07-27）: 「離脱時」は 1 区間 1 本まで（2 本目は配列順で必ず負ける）／
  「このラインを通過したら」はその区間のカメラ担当のラインが 1 本も無ければ選べない。追加ボタン・開始規則セレクト・
  リボンのドラッグ吸着の 3 経路すべてで塞ぐ
- **必ず終わる**: `maxDurationSec`（既定 45s）の watchdog。素材の Prepare 失敗等で止まっても画面は必ず戻る
- **戻り先は再計算**: 演出終了時は「いま体験者が居るゾーン」（時計 `ZoneCommitted` の確定値）へ。
  開始時のカメラへは戻さない
- **v2 は読み取り互換**: [`TimelineMigration`](../../Assets/Scripts/Streaming/TimelineMigration.cs) が
  `cues[]` → `inherit` カット / `insert` → `live` カット へ決定的に変換する（端末キャッシュ・焼き込みが v2 のため）。
  **1 点だけ挙動が変わる**: 旧実装は delay 待ちの cue / insert が**別の区間で遅れて誤爆**していたが、
  v3 では離脱の瞬間に決着する（設計 §8.1）
- **カットの遷移（`transition`）は source で効き方が違う**（2026-07-26 に実装）:
  `live` は画面のライブ層が 1 枚しかないので **`cut`=瞬時 / `dip`・`fade`=黒経由**。
  素材カット（`clip` / `still` / `inherit`）は **`cut`=瞬時に差し替え / `dip`=黒の瞬間に差し替え /
  `fade`=素材のクロスフェード（overlay の fadeIn。従来どおり）**。
  以前は素材カットに遷移が一切効かず、卓は全カットに遷移欄を出していた
- **§6.4 の不正値は本当に飛ばす**（2026-07-26 に実装）: 範囲外カメラの `live` カット・素材が解決できない
  `clip`/`still` カットは**画面に触らずに次のカットへ送る**。全カットが飛んだ演出は画面を掴まない
  （旧: 範囲外は registry の clamp で無言に別カメラへ / 素材無しは尺のあいだ画面が固まる）
- **`untilClipEnd` の終端はトークンで判定する**（2026-07-26 修正）: `ScreenOverlayController.PlayCue` は
  マスク / 素材のロードを await するため、`Current == null` を素朴に見ると**発火直後の 1 フレームで
  「終わった」と誤判定**する（マスク付き cue が一瞬光って消える）。`PlayCue` の戻りトークンを
  `IsFinished(token)` に渡す。**静止画は終端イベントを持たない**ので §6.4 どおり
  `durSec>0 ? durSec : 4s` で畳む（旧実装は watchdog の 45s まで画面が固まっていた）
- **ライブ卓の介入は走行中の演出も畳む**（2026-07-26 修正）: `activeCue` / `cameraOverride` が立った瞬間に
  演出を折り畳む。抑止フラグだけ立てて走らせ続けると、演出のカット終了が卓の cue を消し、
  演出の復帰 dip が卓の固定カメラを外していた。**卓がカメラも握っている（override 中）ならカメラは返さない**
- **現地のランリセット（右グリップ 2 秒長押し）も演出を畳む**（2026-07-26 修正）。卓が無い現場での唯一の出口
- **⚠ Quest 実機未検証**（2026-07-25。EditMode 655/655・fixture 契約テスト・卓のブラウザ実操作は通過）。
  **卓のオーサリング面は演出・カットのリボン 1 つ**（旧グリッド `timeline.js` は削除）。v2 の show.json も
  読み込み時に変換して表示し、💾 保存で `timeline.schema=3` として書き出す。
  **v2 データはもう「退避路」ではない**（実機も v2 を演出へ変換して同じ経路で走らせる）。
  ここまでの実装ごと戻したいときは、この一本化コミットを `git revert` する

### 演出の連続と、継ぎ目の遷移 — 2026-07-29（同日 2 回目）

「離脱時の演出があるとき、次の区間の進入演出が出せない」「演出を連続で置けるようにしたい」
「進入カットの暗転相乗りは本当に最適か」の 3 点を、実行系・卓のミラー・遷移の 3 視点で調べ直して作り替えた。
計画は [2026-07-29_take-continuity.md](../plans/2026-07-29_take-continuity.md)。

#### 判明していた実害

- 実行側は**待つ**（区間内では Ready のまま保持）。ここは正しい。壊れていたのはその先。
- 次の区間の**滞在が前の演出より短いと、待っている間に区間を出て捨てられる**。
  しかも離脱の瞬間は画面が塞がっているので `ifMissed:"fireOnExit"` のフォールバックにも入れない
  （`FindExitCandidate` の結果ごと捨てられる）＝**フォールバックにも入れずに消える**。
- 卓は**一度も警告していなかった**。離脱時の演出は次の区間ブロックに重ならないので絵からも読めない。
- 演出を 2 本並べると実行側では順に出るが、**2 本目の 1 カット目は必ず復帰の暗転（70/100ms 固定）へ
  割り込むので、作者が選んだ遷移が毎回消える**。卓は演出を足すとき常に「進入 +0s」に置くので、
  描かれた位置と実際の発火時刻も食い違っていた。
- 相乗り 3 経路（`InsertExitRedirect` / `TakeHoldBegin` / `InsertBegin`）は**どれもカット側の遷移指定を捨てる**。
  しかも効くのは Down 相の 70ms だけで、Up 相の 100ms に来た演出は今も二重暗転していた。
  「瞬時」は走行タイミングで 3 通りの見え方になっていた。
- 黒の瞬間の予約（`_blackAction` / `_blackRedirect`）は単一スロットで、**上書きされたカットは
  一度も画面に出ないまま報告もされない**。

#### 待ちの寿命 `wait`（持ち越し）

演出に `wait: "segment"（既定）| "chain"` を足した。**既定は 1 ビットも変わらない**。

`chain` は「**自分を塞いでいた演出が終わるまで、区間を出ても待つ**」。持ち越すのは因果が
はっきりしている場合だけで、4 条件すべてが要る:

1. 著作者が `chain` と明示した
2. 離脱の瞬間に**実際に画面が塞がっていた**（歩くのが速かっただけでは持ち越さない — それは `ifMissed` の担当）
3. 開始条件は既に満たしていた（Ready / DeferredToExit）
4. `at:"exit"` ではない（離脱時の演出を別の区間で出すと文脈が最も壊れる）

上限は `MaxCarrySlots = 2` / `CarryMaxWaitSec = 60s`。持ち越しは**いま居る区間の演出に劣後する**
（先に出すと、体験者が居る場所の演出が毎回後ろへずれる）。
捨てる出口（上限・寿命・卓の介入・`AbortActive`）は**すべて報告する**（`CarryExpired` / `BlockerGone` を新設）。
あわせて `policy:"yield"` の打ち切りで消える武装も報告するようにした（旧実装はここだけ漏れていた）。

#### 連続の渡し（`chainNext`）

演出が終わるとき、次に出せるものが控えていれば**画面を返さない**（復帰の暗転を打たない）。
占有は保ったままなので画面の所有者が空白になる瞬間は無い。watchdog の強制終了では立てない。

⚠ 判定と発火のあいだ（1 フレーム）に「次」が消えると占有が降りないので、
`TakeRunner` に安全網を置いてある（次のフレームで始まらなければ画面を返す + 警告）。
**凍結が解けない事故をこの codebase は 4 回踏んでいる**ので、ここを外さないこと。

**⚠ その安全網は「必ず次のフレーム以降」で判定する**（2026-08-03 に実機で発覚し修正）。
`EndTake` は `Update` の中の `Apply(d)` から呼ばれて `_chainPending` を立てるので、
**同じフレームで見ると次の演出はまだ始まりようがない**。旧実装は同フレームで判定していたため、
`chainNext` は**成立した瞬間に必ず取り消され**、避けたかった復帰の暗転（70/100ms）が毎回入っていた。
実測（`logs/capture/20260803_150205`）: 「そのまま渡す」と「取り消し」が**同じ ms** に出て、
次の演出はその **9ms 後＝次フレーム**に始まっていた（2 回とも）。
＝ **連続の渡しは一度も働いていなかった**。`_chainPendingFrame`（立てたフレーム番号）で境界を跨がせる。
安全網そのものは残っている（次フレームで始まらなければ従来どおり画面を返す）ので、凍結の危険は増えない。

#### 継ぎ目の遷移は「いま画面を取る側」が所有する（`HandoffDip`）

相乗り 3 経路を [`CameraSwitchDirector.HandoffDip`](../../Assets/Scripts/Streaming/CameraSwitchDirector.cs) 1 本へ集約した。

| 進行中の相 | 動作 |
|---|---|
| Idle | 従来どおり `StartDip` |
| Down（暗くなっている途中） | **暗さを比率で保ったまま**、残りの尺と見た目を割り込む側の指定へ差し替える |
| Up（明るくなっている途中） | いまの暗さから Down へ**折り返す**。旧実装は 0 から張り直していたので「黒 → 明るくなりかけ → また黒」だった |
| 瞬時（0/0） | 進行中の尺をそのまま使い、差し替え先だけ変える。作者の「瞬時」は「自分から暗転を足さない」意図であって「既にある暗転を打ち切る」意図ではない |

理由：体験者にとっての単位は**継ぎ目**で、継ぎ目は 1 回だけ起き、その見え方は作者が決めるのが正しい。
旧実装は前半（1 回にする）だけを満たし、後半（作者が決める）を捨てていた。しかも捨てるかどうかが
実行時のフレームタイミングで決まるので、**同じ show.json が実行のたびに違う絵になる**（再現しないものは著作できない）。

あわせて `InsertReturn` が遷移引数を取れるようにし、黒の予約の上書きを `TransitionPreempted` で報告する。

#### 卓

- 演出インスペクタに「**前の演出が終わるまで待つ**」（`at:"exit"` では選べない）
- **ひとつ前の区間の離脱時演出がこの区間の滞在を食う**危険を著作の段階で言う（実測滞在があれば具体秒で）
- 演出を足すと 2 本目以降は「前の進入演出が終わったあたり」に置く（全部 +0s に積まない）
- 遷移ラベルが `glitch` を「暗転」と表示していたのを直した
- シミュレータに持ち越し数と、新しい drop 理由 2 種を出す

⚠ 実機未検証。EditMode 861/861・node 226/226。

### 体験の骨格（導入 → 3 周 → 終了）と「映像の乱れ」— 2026-07-29

企画書が学会論文版（`PR0490_1.pdf`）に差し替わり、体験構成が「**3 区間を 3 周・導入を含め 3 分以内・各周およそ 30 秒**」に確定した。
計画・要求対応表は [2026-07-29_proposal-v3-implementation.md](../plans/2026-07-29_proposal-v3-implementation.md)。

#### run（体験 1 回の骨格）

show.json トップレベルに `run` を新設。**キーが無くてもコード既定（3 周・導入あり）で終わる** — 終端が無いことが欠落だったので、既定でも走り切ったら終わる。

```jsonc
"run": { "totalLaps": 3, "introEnabled": true, "introMinSec": 20, "introAutoAdvance": true,
         "targetSec": 180, "hardLimitSec": 300, "endFadeSec": 1.5,
         "endGraceSec": 3, "endHoldMaxSec": 60 }
```

相は `Intro`（導入）→ `Run`（本編）→ `Finished`（終了）の 3 つ。判定は純ロジック
[`ShowRunLogic`](../../Assets/Scripts/Streaming/ShowRunLogic.cs)、配線は [`ShowRunDirector`](../../Assets/Scripts/Streaming/ShowRunDirector.cs)。

##### 帰りの A まで体験する（2026-08-02）

体験は「3 周 ＋ **元の位置に戻った A 区間**」で終わる。周回は進行ポインタ方式で `order[0]` へ戻った時に
上がるので、**`lap = totalLaps + 1` の `order[0]` は構造的に必ず踏む**。`totalLaps` は 3 のままで、
この 1 区間だけを追加で到達可能として扱う（`totalLaps=4` にすると帰りの B・C まで生き、
企画書の「3 周」とも表示上食い違う）。

到達可能な区間の式は **3 箇所が同じものを持つ**（[`ShowRunReach.IsSegmentReachable`](../../Assets/Scripts/Streaming/ShowRunLogic.cs) /
卓の [`run-model.js`](../../tools/web-compositor/run-model.js) / [`analyze-xp-log.py`](../../tools/analyze-xp-log.py) の
`is_segment_reachable`）。期待値を 3 者のテストにハードコードして突き合わせてある。

```
lap <= totalLaps || (lap == totalLaps + 1 && camera == order[0])
```

- **⚠ `endGraceSec` が無いと帰りの A の演出は始まる前に暗転する。** その区間に入ったフレームで
  周回が上がって終了条件が立つ一方、`at:"enter"` の演出はその同じ連鎖では**武装されるだけ**で、
  開始は次フレーム以降の `TakeRunner.Update`。スクリプト実行順は未定義なので、「演出が走っているか」
  だけを見ると走り出す前に終わる順序が実在する。**終了条件成立後は演出が走っていなくても
  `endGraceSec`（既定 3s）のあいだ必ず待つ。**
- 走行中の演出は `endHoldMaxSec`（既定 **60s**・旧 const 12s）まで見せ切る。帰りの A で流す録画は
  前の周の実滞在ぶん（20〜40 秒）になるので、12 秒では途中で切れた。
- **時間切れ（`hardLimitSec`）では待たない。** 待つと hardLimit の意味が壊れる。
- 実機は起動時に「踏まれない区間に置かれた演出」を名指しで 1 回警告する
  （`ShowControlClient.WarnUnreachableSegments`）。**実行は止めない**。
- StatusHud の 1 行目は帰りの区間だけ「もどり ・ 経過 2:05」と出す（「4周目/全3周」は壊れて見える）。

- **導入はランの第 1 相**であって「ラン開始前の待機」ではない。Intro→Run は
  `ShowControlClient.BeginMainRun()`（周回・演出・BGM・滞在ログだけ初期化）を打ち、
  **端末内録画の世代は切り替えない**。ここを録画のリセット系統に載せるとラン開始が複数系統に増え、
  遅れて届いた runEpoch が 1 周目の録画を消す（＝ 3 周目の素材が黙って消える）。
- 導入の自動終了は「`introMinSec` 経過 **かつ** スタート区間（`course.order[0]`）に居る **かつ
  導入演出が進行中でない**」の AND。`introAutoAdvance:false` にすればスタッフの明示操作だけで進む。
  - **⚠ 3 つめ（演出が進行中でない）は 2026-07-30 の実機テストで足した。** `introElapsed` は
    アプリ起動から数え始めるので、設営や待機で `introMinSec`（既定 20 秒）はとうに過ぎている。
    そこへ体験者が開始位置に立つと、**演出が始まったその瞬間に本編へ飛ぶ**。実測では
    Real → Degrade の 3.5 秒だけ流れ、核心（枠が閉じて中がカメラ映像へ変わる Structure /
    Frame / Swap）が一度も出なかった。起動から 20 秒以内に体験者が立つ運用は非現実的なので、
    **この穴があると導入演出はほぼ常に打ち切られる**。
  - 判定は `IntroDirector.Active && !Holding`（足踏み＝開始待ちは含めない — 含めると
    開始位置に立つまで永久に本編へ進めない）。スタッフの明示操作（`RequestAdvance`）は
    演出中でも従来どおり効く（人の判断を優先する）。
  - 演出が終われば `RestartIntroClock()` で計時が 0 に戻り、そこから `introMinSec` ぶんの
    慣らし歩行が始まる ＝ 「演出 → 慣らし歩行」の順序が設計どおりになる。
- **終了の判定は必ず次フレームの `Tick`**。周回の確定と離脱時演出の発火は同じ同期連鎖の中で起きるので、
  周回の変化を受けたその場で終了させると 3 周目最後の区間の離脱時演出が始まる前に終わる。
  走行中の演出があれば見せ切る（上限 `run.endHoldMaxSec` = 既定 60s）。
- **凍結ラッチは増やしていない。** 終了しても画面のカメラ切替は裏で回り続け、見えなくなるのは
  [`ShowEndingFader`](../../Assets/Scripts/Diagnostics/ShowEndingFader.cs) の黒のおかげ。
  凍結を足すと「解除されずに残る」事故を新しく作る（この codebase は 4 回踏んでいる）。
- 導入・終了の間は [`CueScheduler.SetShowGate(false)`](../../Assets/Scripts/Streaming/CueScheduler.cs) で
  区間進行を下流へ流さない。**演出の武装・端末内録画・区間 post / BGM・実測滞在がまとめて止まる単一の首**
  （下流それぞれに条件を配ると必ず片方を忘れる）。画面のカメラ切替は Director 側なので止まらない。
  - **⚠ ゲートの適用は必ずイベント配布の前**（2026-07-29 監査で修正）。`RunBegan` の処理は
    `BeginMainRun` → `LapCounter.ResetRun` → `SeedCurrentZone` → `CueScheduler.NotifyCameraEntered` を通る。
    旧実装は `ApplyGate()` がこの後にあったため、**本編 1 周目のスタート区間の進入が閉じたゲートに捨てられていた**。
    周回は進行ポインタ方式で、次に `course.order[0]` へ入る時は lap 2 なので (1, order[0]) は二度と来ない
    → **スタート区間だけ 1 周目が録画されず**、それを背景に使う 3 周目の録画カットが実機で無言で飛ぶ。
- 現地の右グリップ長押しは `ShowControlClient.BeginNewVisitorRunLocal()` を通す（卓の ▶ ラン開始と同じ号令元）。
  旧実装は個別に叩いており、実測滞在が前の体験者と混ざる非対称があった。
- 卓: `/command` の `advanceIntro` / `endRun`（いずれも世代カウンタ）、ラン状態パネルに相・経過・目安、
  ⚙ 欄で run を編集。**本番前チェックが「走り切る周数を超えた周の演出」を ❌ で出す**（実機だけが黙って落とすため）。
- ▶ 検証（シミュレータ）も `run.totalLaps` で止まる。止めないと「3 周で終わる設定なのに 4 周目が卓では動く」嘘になり、
  逆に 3 周目最後の離脱時演出が出るかを卓で確かめられない。

#### 導入演出 — 現実が割れてスクリーンへ入る（2026-07-30 / **2026-08-15 に旧構成へ戻した**）

**企画書には無い新規追加。**
`Intro` 相の最初の **13.1 秒**で、**素通しの現実が格下げされ、割れて、スクリーンの枠へ
吸い込まれ、その中に自分が居る**。

⚠⚠ **2026-08-13〜15 の構成（6.2 秒・闇の中で管が点く 4 段）は廃止した。**
設定が「隔離された壁」から**回収されて会場に在る壁**へ変わり（`canon/LEDGER.md` 0040 / 0044）、
体験エリアを隠す必要がなくなって**封印の箱が無くなった**。箱が無ければ
「箱の中に入ってから固定視点になる」も成立しない。ユーザー宣言（2026-08-15）:

> 設定が、壁の調査になったので、見えなくなる必要がなくなりました。黒い箱は無くしましょう。
> 削除ではなくどこかに退避させといてください。なので、パススルー→2Dの演出は、以前のバージョンに
> 戻しましょう。タイトルとかそれ以外の流れは変えずに。

| 段 | 尺 | 動くもの |
|---|---|---|
| 0 `Black` | — | 素通しのパススルー。**エリアへ近づいたら**次へ（`ApproachLogic`） |
| 1 `Real` | 1.5s | 素のパススルー。何も演出しない（段 2 の変化を読ませるための比較対象） |
| 2 `Degrade` | 3.5s | 色 → コントラスト → 輪郭 → 粒 の順に足す（`degrade` / `edge` / `grain`） |
| 3 `Structure` | 1.1s（実尺） | 輪郭だけの世界。**段 2 の後半から重なる**ので単独ぶんだけ流れる |
| 4 `Frame` | 2.5s | **見えているものが割れて集まり、枠の中が現実 → カメラ映像へフェードする**（`shatter 0→1` / `live` は 0.55→0.85 で入れ替え）。枠は遅れて閉じる |
| 5 `Swap` | 4.5s | 最後の破片が消えて映像だけになる。`glitch` の一撃。枠の中に自分が居る |

⚠⚠ **割れた先はカメラ映像**（2026-08-15・`canon/LEDGER.md` 0045 / 0046）。
覆いは「スクリーンの上のセルの alpha を `_ScreenFade`（＝ 重み `live`）にする」ので、
0 なら現実の窓のまま、1 ならその画素に既に描かれている映像が出る。
**中間はそのままクロスフェード**（合成が `アプリの rgb + 現実 × (1 - alpha)` なので、
alpha がそのまま混ぜ具合）。別レイヤは足していない。

⚠ **入れ替えは割れ始めと同時ではない**（0046）。前半は現実のまま細かくなって集まり、
**枠が閉じ切る少し前**（進み 0.55 → 0.85）で入れ替わる。
⚠ **`_IntroLive` は 0/1 の門で、混ぜ具合ではない。** ここにも同じ重みを渡すと二重に掛かり、
混ざっている最中の映像が暗く沈む。

⚠ **段 1〜3 でスクリーンが見えないのは、開口いっぱいがパススルーの窓だから。**
装置は最初からそこに在って電源も入っている（`ignite = 1`）が、現実の窓がその上に重なっている。
**割れて初めて下から出てくる。** 設計ではなく覆いの仕組みからそうなる。

⚠ **封印の箱は退避しただけで消していない。** 場所と戻し方は
[reference/attic-sealed-box.md](../reference/attic-sealed-box.md)。重み `sealBox` は全段 0 で、
`IntroLogicTests.SealedBox_NeverAppears_InAnyStage` が「黙って復活しない」を固定する。

⚠ **割れるのは覆い（パススルー）だけ**になった。箱が主役だったころは段 4 の進みを
覆い 0.35 / 箱 0.65 に分けていたが、いまは**進みをまるごと覆いへ渡す**
（`IntroShatterCurve.VeilShatter` が恒等）。

##### ⚠⚠ カメラが繋がっていなくても段 5 は流れる（2026-08-13・`canon/LEDGER.md` 0025）

旧実装は段 4 で映像を 3 秒待ち、来なければ**演出ごと畳んで本編へ落としていた**
（体験者から見ると「枠になった次の瞬間に何も起きずに始まる」）。いまは待ちきれなければ
そのまま段 5 へ進み、**砂嵐が出る**（`SignalLostFx` が未受信カメラを覆う既存の経路）。
装置は枠になった、映すものが無い、という筋がそのまま画になる。

⚠ **砂嵐は `_SignalLost × _IntroLive` で切る。** 砂嵐は post のいちばん後ろに居るので、
切らないと段 0〜4 のあいだずっとスクリーンが砂嵐になり、割れて吸い込まれる山が画に出ない。
`_IntroLive` は演出の外では 1 なので、本編・終幕は 1 ビットも変わらない。

⚠ **砂嵐は管の面（`_CrtEdge` / `_CrtRound`）より前で解く。** 後ろに置くと角の丸みも縁の暗さも
上書きした**四角い砂の板**になる。砂嵐は管の中で生まれるもの。

⚠ **スクリーンの alpha は必ず 1。** 「まだ映していないスクリーンを透明にする」を試したら、
絵に部屋が透けて出た。**段 0〜4 でもスクリーンは不透明な矩形として立っている** —
`ignite = 1 / live = 0` なので**暖色に光る空の管**として見える（装置は現場に据えてあって、
電源は入っていて、まだ何も映していない）。⚠ 箱が居たころはこれが真っ黒な矩形で、
「箱の外へ黒い四角がはみ出す」問題として未解決だった。**光らせたことで消えている。**

見るのは `.\tools\unity.ps1 menu intro` の `*_nosignal.png` 3 枚。

- **`ignite` は導入の全段で 1**（点いていて、まだ何も映していない）。`ScreenComposite` の
  `_CrtIgnite` へ `IntroDirector` が書く。書く先は **`MjpegScreen` の Renderer のマテリアル**
  （`CameraFeelFx` が掴んでいるのと同じ材質）
  - ⚠⚠ **0 を書いたままにすると画がまるごと消える。** 演出を畳むすべての経路
    （終わった / 中止した / 無効の設定 / 位置合わせ中 / 相が変わった / `OnDisable`）で
    **1 を書き戻す**。「書くのをやめる」だけでは最後に書いた 0 が残る
  - ⚠ 材質を掴めなかったときは**何も書かない**（0 を書いて画を消す方向へ倒さない）。
    掴めていないことは `[XP]` の `ignite=nc` で分かる
  - ⚠ **管の点灯の演出（0→1 の過程・`canon/LEDGER.md` 0030 の褐色）は 2026-08-15 に走らなくなった。**
    シェーダの経路は残っている（`_CrtIgnite < 0.999` の分岐）ので、戻すなら重みを動かすだけでよい
- ⚠⚠ **殻（`shell`）は導入で 1 度も立てない**（2026-08-15）。枠の外の黒は覆いが持ち、
  枠の中は段 4 から映像。殻は全画面の面（queue 4910・`ZTest Always`）で**スクリーンごと
  黒く塗る**ので、ここで被せると**段 4 で出ていた映像が一度消えて戻る**。
  終幕（`OutroLogic`）は従来どおり使う
- **破砕（`shat=`）は観測へ戻した**（2026-08-15）。`shat` / `shatC` / `shatRect` は
  「重みが動いた」ではなく**画に出た**の側 — セル格子を組めていなければ 1 枚 quad のままで
  1 画素も割れない。⚠ 箱の側（旧 `shatB` / `shatBC`）は戻していない

- **始まり方は 3 つ**（いずれも **HMD を被っていることが前提**・時間では進めない）:
  1. **通過ライン**（`run.intro.startLineId` → `layout.lines[].id`）を横切る ← **2026-07-30 に追加・現行の既定運用**
  2. **開始位置**（`layout.startSpot` の円）に**外から入ってきて** 0.5 秒留まる（`startLineId` が空のとき）
     — ⚠ 2026-08-09 に「中に居る」（状態）から「入ってきた」（事象）へ変えた。詳細と再発防止は
     [show-design.md](show-design.md) の「導入の開始は『入ってきた』で判定する」
  3. スタッフの明示操作（⏭）

  ⚠⚠ **1 と 2（＝自動の出口）はすべて「人が始めた」を通る**（2026-08-14）。実体は
  **タイトルが画面を手放したか**（スタッフが A を押して題字が焼け切ったか）で、
  `ShowControlClient.StartAuthorizedProvider`。**3 は通さない**（人の判断は上書きできる）。
  加えて**箱の至近に立たされた体験者を 1 秒で救う**出口が足してある。
  設計と踏んだ穴は [show-design.md](show-design.md) の「自動の出口はすべて『人が始めた』で
  ゲートする」「箱の至近に立たされた体験者を救う」が正本。

  ⚠ **course 座標なので位置合わせが済んでいないと 1 も 2 も判定できない** — 未登録ならスタッフ操作へ
  縮退し、HMD 内に理由を出す（黙って「立っても始まらない」を起こさない）。
- **なぜ線を足したか**: 円は「その場所に立つ」＝状態、線は「横切る」＝事象。**歩いて入ってくる動きの
  まま始められる**ので、開始位置に立ち止まって待つ手順が要らなくなる。ラインは担当カメラを持つので、
  順路の入口に引けば「入ってきた人が最初に踏む線」になる。横断は 1 フレームの事象なので
  `IntroDirector` 側でラッチする（取り逃すと永久に始まらない）。
- **`UserPresentProvider`（HMD 装着）で門を閉じる**。机に置いた HMD が位置条件をたまたま満たして
  勝手に始まり、体験者が被った時には終わっている、という事故を防ぐ。Streaming asmdef は OVR を
  参照しない規約なので、`OvrControllerBridge` が `OVRPlugin.userPresent` を差し込む（未設定＝被っている扱い）。
  **被り直すとラッチも落ちる**（前の体験者が踏んだ線で次が始まらない）。
  ⚠ 自動走行（`ShowWalkDebugDriver`）は被らずに走らせるので、この provider を true で上書きする。
- `startSpot` を `regPoints` と**別の集合**にしたのは、位置合わせ点が「HMD で手が届く」
  「タッチ順に意味がある」という別の制約を持つため（兼用すると片方を動かしてもう片方が壊れる）。
- ⚠ **演出の合計秒は単純和ではない（13.1s）。** 段 3 は段 2 の後半（`StructureOverlapAt` = 0.6）から
  始まるので、重なった分は二度流れない。`IntroTiming.TotalSec` と卓の `introStageSec` が
  **同じ式**を持ち、両方のテストに 13.1 をハードコードして突き合わせてある。
  **片方だけ直すと沈黙して食い違う**

- **視点は 1 度も動かさない。** 動かすのは現実の側の身分。段は
  素通し → 格下げ → 輪郭 → 割れる → 映像 → 慣らし歩行
- **`ShowPhase` は増やさない。** `Intro` の内側のサブ状態（`IntroStage`）で、
  ゲート・終了判定・heartbeat・卓・シミュレータへの分岐を増やさない
- **判断は [`IntroLogic`](../../Assets/Scripts/Streaming/IntroLogic.cs)**（dt 注入）。
  各層への配布は `IntroWeights`（passthrough / degrade / edge / structure / frame / live / grain /
  glitch / **ignite** / shell / shellReveal / sealBox）1 本で、**見え方の判断を Director に散らさない**
- **枠は本編のスクリーンそのもの**。開口の形と不透明度だけを動かす（`IntroVeil` が
  `ScreenAnchor` の Quad の見かけの形を逆算）。だから「枠を運ぶ」処理が無く、
  「入ったのに前方にスクリーンが浮いている」矛盾も発生しない
- **⚠ 枠の判定は「眼とスクリーンの 4 辺を通る平面」**（2026-08-01 に置き換え）。
  覆いの面は head-lock（視界を必ず覆い切るため）だが、**開口はスクリーンの実位置から毎フレーム解く**。
  - 旧実装は開口を覆いの面の **uv 中心に固定**していた。本編のスクリーンは頭から
    `heightOffset`（-0.28m / 2.0m ＝ 約 8°）下に置かれるので、**半画角 18.4° に対して 43% 縦にずれていた**
    （ユーザー報告「四角だけになるこの領域とスクリーンの位置が一致していない」）
  - 中心を動かすだけでも足りない。**スクリーンは水平（ヨーだけ追従）なので、頭を上下に振ると
    スクリーン面と覆い面が必ず傾く**。矩形として投影する方式は両面が平行なときしか正しくなく、
    20° の見下ろしで四隅が約 2.8° ずれる。平面 4 枚なら頭の向き・追従の遅れ・首の傾きに関係なく厳密で、
    除算も特異点も無い（シェーダは `dot` 4 回 + `max`）
  - **縁のぼけ幅は「閉じ切った枠」を基準にする**。いまの大きさに比例させると、開いているとき
    6° 以上に膨らんで**覆いの四隅がぼけ帯に入り、段 1〜3 で現実が四隅で翳る**
  - **⚠ 全開（段 0〜3）を有限の矩形で近似してはいけない**（2026-08-09 実害）。開口は全開でも
    77.9° の矩形でしかなく、しかも**中心がスクリーン（頭から 8° 下・ピッチに追従しない）に
    固定**されていたので、頭を 30° 以上下げると下辺が視界へ入る。枠の外は不透明黒なので、
    **視界の下端から黒帯が出て、下を向くほど広がった**（ユーザー報告「下を向くとパススルーが
    途中で途切れており、そこには黒い空間が広がっている」）。
    → `frameClose ≈ 0` は**厳密に全開**へ倒し、そこから `AnchorK`(0.15) までは開口の中心と姿勢を
    頭の正面から補間する。**AnchorK 以降はスクリーンに完全固定**なので「枠はスクリーンの形のまま
    縮む」は 1 ビットも変わらない（AnchorK での開口は半画角 69° ＝ まだ視界より大きく、枠として見えない）。
    `IntroVeilApertureTests` が**頭のピッチ ±80°・ヨー遅れ 180° でも視界の四隅が全部内側**を固定する
  - C# の [`IntroVeil.SignedDistance`](../../Assets/Scripts/Streaming/IntroVeil.cs) が
    **シェーダと同じ式**を持ち、`IntroVeilApertureTests` がスクリーンの実点を食わせて
    枠の縁との一致を固定する（Play 不要）。**片方だけ直すと沈黙して食い違う**
- **⚠ `run.intro` が無い show.json では JsonUtility が「全部 0」の実体を作る**。
  `ShowIntroDef.LooksUnset` で検出して既定へ落とす — これが無いと `enabled=false` に化けて
  **演出が黙って出なくなる**（焼き込み・端末キャッシュが古いときに踏む）
- **⚠ 段 3 の構造の線は既定で出さない**（2026-08-01・ユーザー判断「雰囲気ぶち壊しだから要らない」）。
  細い寒色の線が現実に重なると計測器に見え、「現実がそのまま格下げされていく」という段 2 → 段 4 の筋を切る。
  位置合わせの現地検証は登録リチュアル（Review フェーズ）のワイヤー表示が担うので、導入から消しても検証手段は残る。
  - **⚠ 2026-08-12 に線そのものは暖色へ変えた**（`canon/LEDGER.md` 0010「全体的に暖色に寄せてほしい」）。
    `IntroStructureWire.lineColor` は (1.0, 0.66, 0.36, 0.8)。**既定 OFF は変えていない**。
    シーンに焼かれた値が優先されるので `Main.unity` 側も対で直してある（片方だけだと沈黙して食い違う）
  - **輪郭線の色（`run.intro.edgeColor`）も白 → 生成り `#ffcf9e`**。既定は
    **3 者で揃える**（`ShowIntroDef.edgeColor` / 卓の `INTRO_DEFAULT` / `capture-server.py` の
    `_default_show`。`intro-model.test.mjs` が 3 者一致を機械で固定する）
  - 既定 false は **4 者で揃える**（`ShowIntroDef` / 卓の `INTRO_DEFAULT` / `capture-server.py` の
    `_default_show` / `IntroStructureWire` の SerializeField）。卓は `src.x === true` で読む
    （`!== false` だと未指定が true に化け、欠落キーを false で埋める JsonUtility と食い違う）
  - **⚠ それまで `run.intro.showRoomWire` / `showCameraMarks` は実行体へ 1 度も届いていなかった**。
    `IntroDirector` は 2 フラグの **OR** で `Apply` を呼ぶかだけを決めており、個別の値は
    `IntroStructureWire` の SerializeField（どちらも true）のままだった ＝
    **卓で「壁の線を出さない」にしても壁の線が出続けた**。`SetSources` で渡すようにした
  - 段 3 自体は残す（線が出なくても「輪郭だけの世界」が保たれる溜めとして働く。実尺 1.1 秒）
  - 本番前チェックの幾何要求（床の寸法・部屋の壁）と「較正済みのカメラが 0 台」の警告は、
    **線を出す設定のときだけ**言う（起きようのない不備を直させない）
- **⚠ 段 3 の壁の線を出す設定にしたときは `layout.room` からしか出ない**（2026-07-30 追記）。
  `IntroStructureWireLogic` は壁と箱を `AppendRoom`（= `layout.room` だけ）から起こし、床の外周を
  `TryFloorExtents`（`layout.floor` → `room.floorW/D`）から起こす。**旧 `layout.wall` は 1 本も読まない**
  （あれは HMD 位置合わせリチュアルのワイヤー専用）。卓のフロアマップ **🧱 部屋**で壁を引いていないと、
  段 3 は床の四角とカメラの印だけになり、**§8-6 の「線が実物に重なるか＝位置合わせの現地検証」が成立しない**。
  本番前チェック（`intro-model.js` の `introWireGeometry`）もこの 2 キーを見る — 以前は `layout.wall` の
  既定値判定だったので、**壁を著作しても ❌ が消えず、指示された直し方（床の寸法入力）でも消えなかった**
- **⚠ 尺の既定は 3 箇所に現れる**（`ShowIntroDef` のフィールド初期値 / `IntroTiming.Default` / 卓の
  `INTRO_DEFAULT`・`capture-server.py` の `_default_show` の **4 箇所**）。
  13 秒へ詰めた時に `ShowIntroDef` だけ旧値（33 秒）が残っていた。
  `Sanitized()` が 0 を既定で埋めるので実害は出ていなかったが、**フィールドを直接読む経路が増えた瞬間に食い違う**
- ⚠⚠ **「右手を上げる」合図は 2026-08-13 に廃止した**（`canon/LEDGER.md` 0034
  「伏線にするのは、スマートではありません」）。HMD 内の面・スタッフの声掛け・show.json の
  `raiseHandPrompt` を全部消してある。**3 周目の反転は指示なしで成立する** — 背景は 1 周目の録画
  なので、いま歩いている自分がそこに居ない。手をあげろと言うのは種明かしを先に配るのと同じ
  - **⚠ この合図は HMD 内に出さなくなった**（2026-08-07・ユーザー指摘「体験者が被っているときに
    表示する文字、世界観を壊すので消してください。スタッフの時は表示していい」）。`IntroPrompt` は
    残っているが `StatusHud.StaffViewing`（右 B or 位置合わせ中）でしか出ない ＝ **体験者には
    1 文字も届かない**。**被せる前にスタッフが口で言う**（`docs/onsite-checklist.md` の
    「体験者に被せる前に、口で伝えること」）。伝え忘れると 3 周目の反転が丸ごと不成立になる、
    現状もっとも壊れやすい手順
- **導入で上げた手が 1 周目の録画に混入することは無い**。導入は `CueScheduler.SetShowGate(false)` の
  内側で、端末内録画はゲートで止まっている。ゲートを触るときはこの保証を壊していないか確認する
- パススルーの API の制約（切ると数百 ms 黒 / 走査線は掛けられない / ガーディアンは消せない）は
  [meta-xr.md](meta-xr.md) の「パススルー」節が正本
- ⚠ **`InsideBox` は「体験エリアの中に居るか」だけを意味するようになった**（2026-08-15）。
  閾値は境界そのもの（`InsideEnterM` = 0 / `InsideExitM` = 0.25 のヒステリシス）で、読むのは
  段 0 の安全網（外 → 中と歩いてきたら始める）と救済（中に立ったまま 1 秒で始める）の 2 つだけ。
  箱が居たころの 0.55 / 0.85 は「黒を箱の面より先に立てる」ための前倒しで、
  箱が消えた以上その理由が無い
- **⚠ Quest 実機未検証**（2026-08-15。EditMode 1224/1224・node 392/392。割れ方の見え方・
  13 秒という長さ・段 5 で「自分だと分かるか」は実機でしか判定できない）

#### 映像の乱れ（グリッチ）

企画書 2.3「ノイズやグリッチ等の乱れを一時的に重畳でき、差し替えの継ぎ目の隠蔽や、体験者の注意・移動の誘導に用いる」。

- uniform は **`_Glitch` / `_GlitchSeed`**。障害表示の `_SignalLost` とは別系統で、書き手は
  [`GlitchFx`](../../Assets/Scripts/Streaming/GlitchFx.cs) 1 つだけ（SignalLostFx と同じ流儀）。
  シェーダでは 乱れ → 障害表示 の順に掛かるので、**実際に信号が切れたら障害表示が勝つ**。
- 乱れの中身は 2 段: ①帯ごとの水平シフト + 垂直の同期ずれを**全層のサンプル前の uv** に掛ける
  （ライブ・素材・マスク・CG が一緒にずれる = 差し替えの継ぎ目も一緒に乱れる）②post の後に砂嵐混合と明滅。
- 出し方は 4 つ:
  - カットの遷移 `transition:"glitch"`（既定 220ms）。dip と同じ状態機械を通り、黒の代わりに乱れで覆って
    その最中に差し替える。live / 素材の両経路に一様に効く
  - カット頭の単発 `steps[].glitch` / `glitchSec`（注意・移動の誘導）
  - ゾーン切替への重畳 `control.switchGlitch`
  - 卓の手動 `⚡ 乱れ` → `/command {type:"glitch"}` → `control.glitchEpoch`（世代カウンタ）
- 時間包絡は純ロジック [`GlitchEnvelopeLogic`](../../Assets/Scripts/Streaming/GlitchEnvelopeLogic.cs)。
  持続と単発は **max で合成**（加算だと 1 を超えて飽和し遷移の形が潰れる）。

##### ⚠⚠ 乱れは起きるたびに大きくなる（2026-08-16・`canon/LEDGER.md` 0055）

判断は [`GlitchEscalationLogic`](../../Assets/Scripts/Streaming/GlitchEscalationLogic.cs)
（純ロジック・テスト 10 本）、**数える場所は `GlitchFx` 1 か所**。画と音が同じ進みを読む
— 別々に数えると「画は激しいのに音は同じ」が沈黙して起きる。

| 何 | 1 回目 | 12 回目以降（頭打ち） |
|---|---|---|
| 強さ | 0.32（台本 0.70 の 0.45 倍） | **0.60**（上限・2026-08-16 に 0.83 から下げた） |
| 尺 | 0.20s（台本 0.40s の 0.5 倍） | **0.50s**（上限） |
| 音量 | 0.80 | 1.00（＝ +1.94 dB） |

- ⚠⚠ **直線ではなく進みの 3 乗**（2026-08-16 にユーザーが直線を却下した）。
  4 回目 0.02 / 6 回目 0.09 / 9 回目 0.39 / 11 回目 0.75 / 12 回目 1.00 ＝
  **序盤〜中盤はほとんど動かず、最後の 3 回で跳ねる**。1 回目は**台本より軽い**
- ⚠⚠ **上限は倍率だけでは守れない。** 台本の値はカットごとに違う（強さ 0.25 / 0.70 / 0.80、
  尺 0.40s / 0.50s）ので、同じ倍率を掛けると強い方・長い方が上限を超える。**頭打ちが別に要る。**
  ⚠ **例外を作らない** — 一度「著作が上限を超えていたら著作を優先する」と書いたが、
  それは上限に穴を開けるだけだった（台本 0.59s が 0.537s まで伸びた）
- **強さの上限が 1.0 ではなく 0.60 なのは**、全面が砂で埋まると乱れではなく**信号断に見える**から
  （`canon/LEDGER.md` 0057・ユーザー指定「上限を 1 から 0.6 にしてほしい」。同日午前は 0.83 = 0055）。
  ⚠ 上限だけを下げて**倍率（`LevelAtMax` 1.19）は動かしていない** — 台本 0.70 のカットは
  10 回目あたりで頭打ちに着き、そこから 2〜3 回は張り付く。切替の乱れ（0.25）は最後まで触れない
- **置き換えではなく掛け算**。弱く置いた切替の乱れ（`switchGlitch` 0.25）は最後まで
  相対的に弱いままで、台本の強弱が壊れない
- **見る手段**: `.\tools\unity.ps1 menu glitch` → `tools/caption-glitch-preview.py` →
  `tools/make-preview-video.py`。実シェーダと実ロジックを通した動画が出る
  （帯の数値は Unity が書いた `frames.tsv` 由来。**Python 側で計算し直さない**）
- **数えるのは起きた回数**（周でも経過時間でもない）。`Pulse` は 1 回、
  `SetSustain` は**立ち上がりだけ** 1 回（毎フレーム数えると遷移 1 回で天井に張り付く）
- **ラン単位**。落とすのは `GlitchFx.ResetAll` 1 か所で、ラン開始と体験の終了の両方から呼ばれる
- ⚠⚠ **音は 1 を超えられない。** `SfxPlayer.Play` が `Clamp01(gain × masterGain)` で
  `masterGain` は 1.0。1 を超える倍率は潰れて**音は 1 ビットも変わらない**ので、
  天井の内側で下から上げてある。上げ幅が 2 dB 弱なのはユーザー指定（「深いじゃない程度に」）
- 観測は `ev=sum` の **`glN=`（起きた回数）/ `glE=`（大きくなり具合 0..1）**。
  `glitch=`（強さ）だけでは**台本が強いのか回数で育ったのか区別できない**ので対で出す。
  判定は `analyze-xp-log.py` の「## 乱れ（回数で大きくなったか）」。
  **`ShowTelemetryHost` と対で直す**

#### 撮像の質と残像（`feel` / `hold` / `burn` / `aura`）— 2026-08-06

**「加工が足りない＝怖くない」の診断は、強度ではなく『言い訳が破れていない』こと**だった。
post 12 項目もグリッチも全域に一様で、一様な乱れは全部「機材のせい」で説明が付く。だから安全に見え、
20 秒で慣れて「そういう画」になる。ここで足したのは、その説明が効かなくなる 4 つ。

**別系統である理由**: post は shader / 卓の FS_POST / common.js / pipeline.js の 4 箇所を手作業で同期していて
機械テストが無い。そこへ**時間軸**を持ち込むと沈黙した食い違いが必ず出る。だから
`_NoiseDark` / `_NoiseFixed` / `_ExposureBias` / `_VignetteBias` / `_Echo` / `_ActorFocus` は
`_Glitch` / `_SignalLost` と同じ「別 uniform・単一 writer」の枠に置いた。

| 何 | どこ | 効き目 |
|---|---|---|
| **暗部ノイズ**（`feel.noiseDark`） | 全域・常時 | 暗いところほど粒が乗る。**暗がりが物を隠せる状態**を作る。固定視点では映っていない領域の存在を確かめられないので、物を足すより観測できる面積を削るほうが安い |
| **固定パターンノイズ**（`feel.noiseFixed`） | 全域・常時 | 時間で動かない粒＝画面に貼り付いた汚れ。静止した基準ができ、**その中で動くものだけが浮く** |
| **自動露出の追従遅れ**（`feel.agc` ほか） | 全域・常時 | 映像の明るさへ**遅れて**追いつき、減衰 0.55 で**行き過ぎて戻る**。映っている範囲では何も変わっていないのに明るさが動くと「画面の外で何かが起きた」と読まれる |
| **凍らせた 1 枚**（`steps[].hold` / `burn`） | カット単位 | hold = 画が止まる（ライブも素材も一緒に）／ burn = 少し前の姿が薄く残る。**動いていない画素は同じ値なので、動いたものの跡だけが残る** |
| **人形に付き従う劣化**（`steps[].aura`） | カット単位 | 人形のまわりだけ色が抜け・沈み・粒が増える。**人形が動くと荒れも動く**ので機材の不調では説明が付かない。この作品に唯一無かった「対象に紐づく非一様性」 |

- 実装: [`CameraFeelLogic`](../../Assets/Scripts/Streaming/CameraFeelLogic.cs)（純ロジック・テスト 11 本）+
  [`CameraFeelFx`](../../Assets/Scripts/Streaming/CameraFeelFx.cs)（writer）。`aura` の中心と半径は
  `ShowCgLayer.WriteActorFocus` が人形の投影から毎フレーム供給する
- **`feel` はキーが無くても既定値で効く**（設定を書かないと怖くならない、では現場で使われない）。
  既定は **C# `ShowFeelDef` / 卓 `FEEL_DEFAULT` / `capture-server.py` の `_default_show` の 3 者**で揃える
- **ホールドは必ず秒で明ける**（`CameraFeelLogic` が保証）。加えて演出の中止・体験の終了で `ResetAll`。
  「凍結が解けない」はこの codebase が 4 回踏んだ事故の型なので二重に閉じてある

⚠⚠ **暗部ノイズの色差は「弱く・鈍く」**（2026-08-23・`canon/LEDGER.md` 0121）。
実機で「**1 周目 A だけカメラ映像がちかちかする**」として出ていたのがこれ。

- **色ノイズは輝度の粒より低い空間周波数**（塊）なので、映像のフレームごとに引き直すと
  **暗い区間では領域まるごとの色相が 30Hz で振れる**（実測: 幕が 茶 → 紫 → 茶 → 赤）
- **1-A だけ出るのは、カメラ A の画がいちばん暗いから。** ノイズの床は絶対値なので、
  暗い画では信号に対する割合が跳ね上がる（実測の明るさ 1-A 81 / 1-B 111 / 1-C 153）
- いまは **10 フレームに 1 度だけ引き直して補間**し、強さは輝度の粒の **0.55 倍**。
  ⚠ 旧実装は 1.6 倍で、**これは実物と逆**（安い ISP の NR がいちばん強く潰すのが色差。
  同じシェーダの `_ChromaKill` は既にその理屈で書かれていた）
- ⚠ **2 つの抽選を混ぜたら分散の痩せを戻す**（`rsqrt(w² + (1-w)²)`）。戻さないと、
  今度は**粒の強さ自体が 3Hz で脈打つ** ＝ 直したつもりで別の明滅を作る
- **輝度の粒は触っていない**（暗いところほど粒が乗るのは 0018 からの設計）。
  ざらつきを減らすなら動かすのは **`feel.noiseDark`（既定 0.10）1 つ**で、
  **卓から配れるので焼き直しは要らない**
- **測り方**: 走行の生録画から区間ごとに「0.4 秒の移動平均を引いた残差」を、
  **明るさと色（R−B）で別々に**出す。⚠ **明るさだけ見ると分からない** — 色ノイズは
  輝度成分を抜いてあるので、グレースケールにはほとんど出ない。
  実測（1-A の色の揺れ / 尖頭）: **4.50 / 11.62 → 0.82 / 2.20**（1-B は 0.07 のまま）
- **hold 中は差し替え素材の時計も止める**（`ScreenOverlayController.SetFrozen`）。ライブだけ止めて
  録画が動くと「装置が固まった」に見えない。解除では止まっていた分だけ開始時刻をずらす（続きから）
- **⚠ 卓のプレビューには出ない**（実機だけが持つ「装置の挙動」）。UI にその旨を明記してある。
  一様に掛かるだけでマスクや立ち位置の判断を狂わせないので、著作は数値だけで足りる
- **やってはいけないこと**（設計批評の結論・実装しなかったもの）:
  - ~~**周回で単調に劣化させない**~~ → **2026-08-12 にユーザーが覆した**（`canon/LEDGER.md` 0012
    「周を重ねるごとに着実に粗くなっていく」）。**解像度だけ**は周の進みに比例して落とす
    （[memory/screen_decay.md](../memory/screen_decay.md)）。色・粒・暗さ・乱れは従来どおり単調に動かさない。
    元の理由（post は合成の**後**なので録画も 3 周目の加工を浴びる ＝ 一番見せたい「1 周目の自分」が
    一番見えなくなる）は**消えていない** — 終端の粗さは「人型と手を上げていないことが読める」を境界にする
  - **datamosh 風のフレーム履歴を持たない**。状態を持つと卓と実機が必ず食い違う（この codebase が
    何度も踏んだ型）。`_Echo` が「指定した瞬間の 1 枚」なのはそのため — 決定的で、同じ show.json は同じ絵になる
  - **選択的彩度（特定色相だけ残す）を入れない**。装置は色を選べないので、色相キーは作者の指さしになる
  - **過去と現在で加工を変えない**（overlay 専用の post を作らない）。区別できた時点で 3 周目は種明かしになる
  - **1 フレームの顔・突然の暗転を入れない**。この作品の恐怖は「装置は正直に映している」という信頼の上に乗る
- 現行 show.json の著作（2026-08-06）: 2 周目の差し替え 3 本に `burn 0.4/2.0s`（一瞬見えたものが消えた後に
  残像＝見間違いではなかったと分かる）、3 周目と帰りの A の録画に `aura 0.55` / 無人プレートに `aura 0.45`

#### 進入カットも切替の黒に相乗りする

企画書 2.3「差し替えは体験者の歩行や提示映像の切替のタイミングに同期させ、差し替えの知覚的検出を抑える」。
`CameraSwitchDirector.InsertBegin` に「進行中の dip が Down 相なら相乗り」を追加した
（`TakeHoldBegin` / `InsertExitRedirect` には元からあり、ここだけ欠けていて **at=enter の演出が暗転を 2 回出していた**）。

#### ⚠ post は **linear 空間**で効く（明るさを触る前に必ず読む）— 2026-08-07

プロジェクトは **Linear 色空間**（`ProjectSettings` の `m_ActiveColorSpace: 1`）。シェーダに入る
`_LiveTex` は sRGB→linear 変換済みで、post 12 項目はすべて linear で計算される。
**sRGB のつもりで値を決めると直感と逆のことが起きる。**

- **`contrast` の中心は linear の 0.5 ＝ sRGB の 0.73**。実写の大半は linear 0.05〜0.25
  （sRGB 0.25〜0.55）なので、contrast を上げると**大部分は暗くなり、天井の照明だけが明るくなる**。
  実測（研究室のプレート 3 枚・生の平均輝度 0.45〜0.48）:

  | | exposure | 画面の平均 | 明るい画素(>0.45) | 潰れ(<0.04) | 人形が立つ辺り |
  |---|---|---|---|---|---|
  | contrast 1.28 | -0.60 | 0.148 | 9.8% | 53.3% | 0.044 |
  | contrast 1.20 | -0.95 | 0.147 | 5.2% | 45.6% | 0.047 |
  | **contrast 1.10** | **-2.00** | 0.146 | **0.0%** | **22.0%** | **0.079** |

  **同じ暗さなら contrast を下げて exposure で深く沈める方が全指標で良い**（天井が飛ばず、
  暗部も潰れず、人形も読める）。`exposure -2.0` は sRGB 換算では 0.5 倍程度でしかない。
- **`lift` も linear**。0.065 は sRGB では 0.28 相当の黒浮きで、見た目にはかなり大きい。
- **暗くすると粒が破綻する**。`_Grain` / `_NoiseDark` は linear 空間で**加算**するので、
  背景が linear 0.02 のときに ±0.08 を足すと sRGB では 0.16→0.35 に化ける。実測で
  exposure を -0.72 → -2.0 にしたとき、粒の指標が 24.8 → 31.0 へ跳ね上がり画が砂に埋もれた。
  **暗さを変えたら粒も必ず measure し直す**（今回は grain 0.065→0.022 / noiseDark 0.10→0.035）。
- **判断は Unity が出した PNG で行う。** 卓や Python で FS_POST を再現するなら
  **linear で計算して最後に sRGB へ戻すこと**。sRGB のまま計算した数値は丸ごと嘘になる
  （最初その誤りで「加工後 mean 0.17・白飛び 0%」と読み、実際は「mean 0.21・明るい画素 13%・
  潰れ 34%」だった）。再現が正しいかは Unity 出力と突き合わせて確かめる。
- **画面内に光源があるカメラは post では暗くできない**。カメラ B は天井の蛍光灯が 4 本直接映り、
  どう沈めても周囲より明るい。実測では 3 カメラとも `>0.45` の画素は **0.0%** で、
  数値上「明るい」画素は 1 つも無いのに、目には白く見える（周囲が 0.05 の中の 0.40 だから）。
  ここから先は**現場の照明を落とすか、カメラの向きを変える**しかない。

#### ⚠⚠ 加工の順序は「撮像の順」— レンズ → センサ → ISP（2026-08-12 に組み替えた）

出どころは `canon/LEDGER.md` 0018（「わざと加工してる感」「現実でこんな感じで粗くなることはない」）。
**強度の問題ではなく、作用点と順序の問題だった。**

旧実装は **現像 → レンズ → センサ** の逆順で、次の 2 つが起きていた。

| 何が | どう見えていたか |
|---|---|
| ヴィネットが露出・コントラスト・彩度の**後** | 角が「後から乗せた黒い楕円」。届かなかった光は後段で戻らないので、レンズは現像より前 |
| 粒がトーンカーブの**後**に加算 | contrast 1.28 / exposure −0.72 は画素の **53%** を 0.04 以下へ潰す。その真っ黒の上に最大量の砂が浮いていた |

いまの順序（`ScreenComposite.shader` の frag と 卓の `FS_POST` で**同じ**）:

```
ソース（伝送の痩せ = mip）→ CG 人形 → [レンズ: 周辺光量・周辺の解像度低下・グレア]
  → [センサ: 光ショット + 読み出しノイズ・色ノイズ・固定パターン]
  → [ISP: 露出 → ホワイトバランス → コントラスト → 黒浮き → 彩度]
  → 走査線 → aura → 乱れ → 信号断 → dip
```

**粒について直したのは 4 つ。どれも「物理的に起こりえない」を消しただけ。**

1. **枠空間 480 固定 → ソース画素で刻む。** 粗くなった画（横 83 ブロック）に 1 ブロック 6 個の粒が
   乗っていた。符号化は粒を真っ先に捨てるので、画より細かい粒は残らない
2. **`frac(_Time.y)` → `_SrcFrame`（受信フレーム番号）。** 映像は 15〜30fps しか来ないのに
   粒だけ 90Hz でざわついていた。**画が止まっているのに粒が動く**のは撮像では起こりえず、
   「映像の上に別の層が乗っている」と読まれる
3. **スカラーを RGB へ等しく加算（＝完全な無彩） → 色ノイズを足した。** 低照度の実センサは
   色ノイズが主で、しかもデモザイクと NR で低周波の塊になる
4. **固定パターンを加算 → 乗算。** 感度のばらつきなので明るい所ほど出る。加算だと黒の上に浮く

**捨てたもの**: 走査線（`scanline` 0.2 → **0**。CMOS → JPEG → 液晶 の経路に発生源が無い）/
post の粒（`grain` 0.065 → **0**。フィルムの記号。粒はセンサ段へ一本化）/
露出に連動する周辺光量（`VignettePerEv`。配信はスマホ ＝ **固定絞り**で機序が無く、
しかも符号が逆だった — 明るくするほど周辺が明るくなっていた。**テストが誤りごと固定していた**）。

**足したもの**（どれも feel 系 ＝ 実機だけ・卓には出ない）:

| uniform | 何 | 書き手 |
|---|---|---|
| `_SrcFrame` | 粒が動く時計（受信フレーム番号） | `CameraFeelFx` ← `MjpegScreen.SourceFrameId` |
| `_ChromaKill` | 暗部の色を殺す量（ISP の NR）。**一様な脱色ではなく、明るい所に色が残る** | `CameraFeelFx.ChromaKill`（const 0.85） |
| `_Glare` | レンズの内面反射。明るい所の光が暗い所へ回り込む | `CameraFeelFx.Glare`（const 0.55） |

⚠ **グレアは「ぼけた値が元より明るい所」にだけ足す**。物理的にもそれが正しく（暗い画素の隣に
光源があるときだけ回り込む）、**ソースが mipChain を持たないときに恒等 0 になる**安全網でもある。
閾値方式だと mip 無しのテクスチャで明るい画素が二重加算されて飛ぶ。

#### ブラウン管の面（2026-08-13）

`canon/LEDGER.md` 0022。**形はメッシュ、面はシェーダ**で分けてある。

| 何 | どこ | 値 |
|---|---|---|
| 曲面（中央が体験者側へ膨らむ） | [`CrtScreenMesh`](../../Assets/Scripts/Streaming/CrtScreenMesh.cs) | 膨らみ 0.055（ローカル ＝ ワールド 5.5cm）・14 分割 |
| 角の丸み | `ScreenComposite` の `_CrtRound` | 0.07 |
| 管の縁が落ちる暗さ | `_CrtEdge` / `_CrtEdgeWidth` | 0.34 / 0.17 |

⚠ **VR なので uv の樽型歪みでは足りない。** 面そのものが曲がっていないと、頭を動かしたときに
視差が出ず「絵が歪んでいる」としか読めない。

⚠⚠ **4 隅と 4 辺は動かさない**（膨らみは双放物面で、辺の上では 0）。スクリーンの隅は
`IntroVeil` の開口計算（眼と 4 辺を通る平面）と `MjpegScreen.ScreenAspect` の基準なので、
動かすとそちらが黙ってずれる。`CrtScreenMeshTests` が固定する。

⚠ **メッシュは実行時（Awake）にだけ組む。** 生成した Mesh はアセットではないので、Editor で
差し替えたままシーンを保存すると壊れた参照が焼かれる。`MainDemoSceneSetup` は
**AddComponent だけ**して Build を呼ばない。OnValidate からも触らない
（Unity が「SendMessage cannot be called during OnValidate」を出す）。

⚠ **縁の暗さはヴィネットと別物。** ヴィネット（`_Vignette`）は**レンズに光が届かない**話で
現像より前に効く。こちらは**管の形**なので post の最後、枠の座標で掛かる。混ぜると
「暗い所をもう一度暗くする」だけになって、どちらの理由も画から読めなくなる。

⚠ **Editor の合成プレビューに曲面は出ない**（平面のまま正投影で撮るため）。
角の丸みと縁の暗さは出る。曲がって見えるかは実機で被るまで分からない。

#### 装置が打っている時計（左上の日付と時刻）— 2026-08-22

逐語は `canon/LEDGER.md` 0108、設計は
[reports/2026-08-22_osd-clock.html](../../reports/2026-08-22_osd-clock.html)。
ユーザーの狙いは「**カメラが切り替わって演出が入っても、この表示が変わらずあり続けることで、
スクリーン＝現実であり合成でない感じを強めたい**」。

| | |
|---|---|
| 字の決め方 | [`OsdClockLogic`](../../Assets/Scripts/Streaming/OsdClockLogic.cs)（純ロジック・テスト 10 本） |
| 実行体 | [`ScreenOsd`](../../Assets/Scripts/Streaming/ScreenOsd.cs)（`_OsdTex` / `_OsdRect` / `_OsdOpacity` の**唯一の writer**・スクリーンの GameObject） |
| 版を焼く | `py -3.11 tools/make-osd-font.py` → `Assets/Resources/Osd/OsdGlyphs.png` |
| 見る | `.\tools\unity.ps1 menu osd`（8 状態） |
| 観測 | `ev=sum` の **`osd=<組めたか>/<刻んだ回数>/<不透明度>`** |

**書式は `2026/09/06 14:23:45`（19 字・すべて半角・秒つき）。** 秒が刻むこと自体が
「これはライブだ」という主張になる。**場所も報告数も出さない**（0108 のユーザー判定 —
各区間が短く 3 つしかない／決められた数が無い）。

⚠⚠ **大きさと詰め方は参考画像（004.jpg）の実測に合わせてある**（2026-08-22 ユーザー赤入れ
「数字のフォントや文字の詰め方等は参考画像に合わせてほしい。枠線はあっていい」）:

| | 参考（512×288） | 実装 |
|---|---|---|
| 字高 / 枠高 | 7px ＝ **2.43%** | 2.43%（＝ 見かけ **0.89°**） |
| 全幅 / 枠幅 | 95px ＝ **18.6%** | **18.3%** |
| 送り / 字高 | **0.714** | 0.706（＝ **セル幅 = フォントの送り幅**） |

⚠ **同梱フォントの数字は幅/字高 = 0.706 で、参考とほぼ同じ比率だった** — ずれていたのは
**セル幅だけ**（初版は 40px 取っていて参考より 64% 広く、字が離れて見えた）。
⚠ **版の縦横比を変えたら `ScreenOsd.CellHeightK` も直す**（あれは「セル高 / 枠高」なので、
セル高が字高の何倍かが変わると字の大きさが黙って変わる）。

⚠⚠ **虚構上は「カメラではなく観測装置（画面の側）が打っている時計」**（DVR の OSD と同じ）。
これが**合成の位置**をそのまま決めている — **砂嵐の後・管の形の前**:

```
… → 乱れ → 入れ替わりの覆い → 管の点灯 → 砂嵐 → ★OSD → 管の縁・角丸 → dip → _ScreenPower
```

| 何が起きても | 時計は |
|---|---|
| カメラ切替・差し込み・**録画**・無人プレート・左右分割 | **変わらない**（時刻は「いま」のまま進む） |
| 乱れ・信号断（砂嵐）・周回の劣化・夜間モード | **変わらない**（鮮明なまま上に乗る） |
| 切替の黒（`_SwitchDim`）・管の縁・終幕の電力（`_ScreenPower`） | **一緒に沈む**（装置側の出来事だから） |
| 導入の段 0〜3（まだ何も映していない管） | **出ない**（`_IntroLive` で切る。砂嵐と同じ切り方） |

⚠⚠ **録画へ焼き込まない。** 端末内録画は配信の生 JPEG なので OSD は入っておらず、
**3 周目に 1 周目の録画が流れても時計は「いま」のまま進む** ＝ 装置が「これは今の映像だ」と
主張し続ける。これが「合成でない感じ」を構造的に成立させている。
焼き込む方式にすると**3 周目に過去の時刻が出て種明かしになる**。

⚠⚠ **時刻で嘘をつかない**（巻き戻し・停止・加速）。「装置は正直に映している」という
3 周目の反転の土台（`rules/sound-design.md` §1）を壊す。同じ理由で
**連絡の面の文字化け（`CommsGlitchLogic`）を移植しない** — 呪いに侵されるのは AI の面で、
装置の時計まで化けると「装置も嘘をつくかもしれない」になる。装置が死ぬのは終幕の電力だけ。

⚠ **TMP の面にしない。** 面を別に立てると上の「一緒に沈む」列が全部効かなくなり、
スクリーンより奥の TMP が深度で消える罠（`canon/LEDGER.md` 0027）にも当たる。

##### 実装の罠（どれも絵で見つけた）

- ⚠⚠ **版の v は反転しない。** Unity はテクスチャを左下原点で持つので、PNG の 1 行目（字の上）は
  v=1 側。矩形の上端も `o.y=1` なのでそのまま渡す。`1.0 - o.y` にすると**字が上下逆さまに出る**
- ⚠⚠ **矩形の内外を分岐にしない**（`step` のマスクで切る）。非一様分岐の中でテクスチャを引くと
  暗黙の微分＝ミップの段の選択が壊れる
- ⚠⚠ **置くのは「枠の左上」ではなく「映像の左上」**（`MjpegScreen.ContainScale` の内側）。
  映像は 4:3・枠は 16:9 なので左右に必ず黒帯が出る。枠の隅に置くと**字が黒帯の上に乗って**
  コントラストが最大になり、主張が強くなる（参考画像の OSD も映像の中にある）
- ⚠⚠ **縁が読みやすさの主役。** 字は明るい壁の上にも砂嵐の上にも出るので、
  字の明るさだけでは沈む。実測: 縁 3px では砂嵐の上のコントラストが **9.9** しか無く読めなかった
  → 5px・不透明へ（いまは砂嵐で **50.0** / 素で 67.3）
  - ⚠ **この 3 つは砂嵐が平均輝度 140 だったころの実測**（2026-08-22）。
    2026-09-03 に砂を掛け算へ作り直して**平均輝度 54**（＝ 素の映像とほぼ同じ）になったので、
    砂嵐の上の読みやすさは**当時より上がっている**（同じ物差しで測り直した差:
    素 150.4 / 旧い砂 130.7 / いまの砂 146.8）。**縁を細くしてよい根拠にはならない** —
    字は明るい壁の上にも出る
- ⚠⚠ **版は 1 セルずつ別の画像へ描いてから貼る。** 数字は送り幅いっぱいを使うので縁は必ず
  セルの外へはみ出す。1 枚のキャンバスへ直接描くと、**はみ出した縁が隣のセルへ流れ込んで
  別の字に混ざる**（敷き直しはセル単位でコピーするので、画面では無関係な字の破片として出る）。
  ⚠ 左右の縁は原理的に残らない（参考画像も字が隙間なく並んでいて左右の縁は無い）。効くのは上下
- ⚠ **敷き直すのは秒が変わった縁だけ**（1Hz）。毎フレーム書くと 90Hz で 4 万画素を組み替える
- ⚠ **セルの並び（`GLYPHS`）は Python と C# の 2 か所にある。** 片方だけ直すと実機で別の字が出る
  → `OsdClockLogicTests.Glyphs_MatchTheBakingScript` が食い違いを落とし、
  `unity.ps1` の `$PyBakes` が焼き直し忘れを言う
- ⚠ **版は Read/Write Enabled が要る**（CPU で読んで敷き直す）。`OsdGlyphImporter` が固定する

**時刻は端末の実時刻**（`canon/OPEN.md` Q12 の既定）。当日は自動で `2026/09/06` になり、
リハの日はその日の日付が出る — どちらも現実なので嘘がない。
⚠ **Quest 2 台の時計のずれが体験者に見える**ので、当日は両機の時刻を確認する。

⚠ **1 字は見かけ 1.0°**（セル 1.19°・`ScreenOsd.CellHeightK`）。読ませる面の段（`HmdTextStyle`
の補助 1.5°）より小さいのは意図（これは画の一部）。⚠ ただし**実機で読めるかは被って確かめる** —
見かけ角の机上見積もりは 2 回続けて外した前歴がある（`memory/hmd_text_style.md`）。

⚠ **シーンに焼かないと APK に入らない**（`menu scene`）。付いていなければ `_OsdRect` は 0 のままで、
シェーダは 1 画素も触らない ＝ **時計が黙って出ない**。観測の `osd=-` がそれを名指しする。

#### ⚠⚠ 呪いが解けると視界が戻る（2026-08-17・`canon/LEDGER.md` 0083）

ユーザー逐語「エージェントがバグるのも、視界が徐々に悪くなるのも、**呪いのせい**という事にする。
なので、4 周目の A で報告して現実世界に戻ったタイミングで、**視界の悪さも元に戻そう**」。

- 引き金は **`TakeRunnerLogic.MarkResult.Released`**（締めのカット `untilMark` が報告で進んだときだけ）。
  1〜2 周目の解除（`Dismissed`）では戻さない — あれは怪異を 1 つ消しただけで呪いは解けていない
- 戻す尺は **`ScreenDecayLogic.ReleaseSec` = 1.2 秒**（smoothstep）。締めの次は `live` 3.0 秒で
  頭に乱れが乗るので、**戻り始めは乱れが覆い、晴れていく過程が 0.5 秒ぶん見える**。
  3 秒以上にすると戻り切る前に終幕（画面が死ぬ Flicker）へ入る
- **傷は残さない**（0 まで戻す）。見えない傷は検証もできない魔法の数になる

⚠⚠ **下がるのは画だけ。`ScreenDecayLogic.Progress` は単調のまま。**
同じ進みを読む先が 3 つあり、**下げてよいのは画だけ**:

| 読み手 | 何に使う | 呪いが解けたら |
|---|---|---|
| `ShowSoundDirector` | 装置の声の痩せ（`bed_device → bed_device_worn`） | **戻さない** — 戻すと音が新品になり「直った」を音で宣言する ＝ クリア演出 |
| `CommsGlitchLogic` | AI の侵食の入力（自前の山を描く） | 入力は**戻さない**。ただし**侵食そのものは 0 へ消える**（下） |
| 画（`SetCoarseBlocks` / `SetMono`） | 解像度と色 | **`Shown` を読んで 0 へ戻る** |

⇒ `ScreenDecayLogic` は **`Progress`（生・単調）** と **`Shown`（画）** を分けて持つ。
観測も対で出す（`ev=sum` の `coarse=` / `coarseShown=` / `coarseRel=`）。
`analyze-xp-log.py` が「生まで下がっていたら FAIL」を見る。

##### ⚠⚠ 呪いを消したら、エージェントも普通に戻る（2026-09-03・`canon/LEDGER.md` 0129）

ユーザー逐語「最後に報告した後、エージェントが呪いを消したはずなのに、エージェントが少しバグったような
表示になっている。**呪いを消したら普通のエージェントに戻るようにしてほしい**」。

- **`CommsGlitchLogic.RecoveredLevel`（0.12）は帰りの A の傷**で、あれは「AI が自力で持ち直した」ぶん。
  **呪いはまだ解けていない**ので残っていてよい。0083 が消すのはその先 —
  報告が通った瞬間に**傷ごと 0 になる**（文字も顔も 1 画素も壊れない）
- **時計は画と同じ 1 本**（`ScreenDecayLogic.ReleaseK` → `ShowRunDirector.ScreenDecayReleaseK` →
  `CommsGlitchLogic.CorruptionFor` の `releaseK`）。⚠ **侵食の側に別の時計を置かない** —
  呪いが解けた瞬間は 1 つしかないのに 2 つ数えると、片方だけ直したときに黙って食い違う
- **顔も一緒に戻る**（`_FaceMix` は侵食そのものを読んでいる ＝ 市松人形が消えてスイに戻る）
- ⚠ **1 段目と 2 段目を混ぜて 1 本の曲線にしない。** 混ぜると「体験者が押したから直った」が消え、
  ただ時間で治る装置になる
- 観測は `ev=sum` の `commsGl`。`analyze-xp-log.py` が
  「`coarseRel=1` の後も `commsGl` が残っていたら FAIL」を見る（**画と侵食は別々に壊れるので畳まない**）

#### 周回で色が抜けて夜間モードへ落ちる（2026-08-12）

`canon/LEDGER.md` 0019。**解像度の劣化と同じ進み**（`ScreenDecayLogic.Progress`）で色が抜け、
3 周目の A で完全な無彩になる。1 周目は暖色のまま。
⚠ 戻すときも同じ 1 本（`Shown`）なので、**解像度と色は必ず一緒に戻る**。片方だけ先に戻すと
順方向では作れない画（色があって粗い／鮮明な暗視）が数秒出る。

uniform は **`_Mono` 1 本だけ**で、夜間モードらしさはそこから派生させる。

| 派生 | 量 | なぜ |
|---|---|---|
| 露出 | **+0.95EV** | ⚠⚠ 夜間モードは**増感**で成り立つ。上げないと真っ暗（実測 1.6/255）になり「何も映っていない」画になる |
| 粒 | ×2.6 | 増感すれば粒が増える。露出とセットでないと嘘になる |
| 周辺光量 | +0.16 | 赤外の照射範囲だけが残る |
| コントラスト | +0.10 | 中間調が減って白と黒に寄る（照らされた所と、届かない闇） |
| 輝度の重み | Rec.601 → (0.52, 0.34, 0.14) | 赤外カットフィルタが外れるので**赤いものが明るく写る**。ただの脱色だと赤い着物が灰色に沈む |

⚠ **別 uniform に分けない。** 分けると「色は残っているのに粒だけ多い」ような、装置として
説明の付かない絵が作れてしまう。writer は `CameraFeelFx.SetMono`、押すのは `ShowRunDirector`
（`SetCoarseBlocks` と**同じ行**）。

⚠ **粒は捨て切らない**（符号化による減衰は `pow(grainPx, 0.6)`）。粗い画で圧縮が粒を捨てるのは
物理的に正しいが、完全に消すと「のっぺりした低解像度」になって暗視カメラの手触りが消える。

#### ⚠⚠ 周回で痩せる伝送は **mip** で作る（旧: サンプル位置の量子化）

旧 `CoarsenUv` は低域通過でも符号化でもなく、**周期的な停止と局所拡大を持つ座標変形**だった。
ブロックの内側は 1 テクセルを引き伸ばし（∂u'/∂u = 0）、境目だけが元画像を最大 2.5 倍速で走査する。

**実測（2026-08-12・`plate_C` で計測）**:

| | 格子の境目の段差 ÷ 内部 | 輝度の細かさ | 色差の細かさ |
|---|---|---|---|
| 元のプレート | 1.01 | 0.0254 | 0.0027 |
| **旧実装（当時の 83 ブロック）** | **2.91** | 0.0185 | 0.0023 |
| 面積平均で縮小 → 拡大 | 1.09 | 0.0071 | 0.0023 |
| JPEG q12（帯域が枯れた） | 0.99 | 0.0249 | 0.0019 |
| JPEG q05 | 1.00 | 0.0221 | **0.0008** |

**旧実装だけが格子を作っていた。** 実際の劣化はどれも 1.0 前後で、しかも実物は**色差が先に落ちる**
（q05 で色差は元の 30%・輝度は 87%）。旧実装は RGB 全成分に同じ uv を使うので、輝度と色差が
同じ大きさの正方形で落ちていた ＝ 実際の 4:2:0 とは逆。

いまは **mip を引く**（面積平均のピラミッド ＝「先に帯域を落としてから間引く」という実物の順序）。

```
lod = log2( ソース幅 / (枠のブロック数 × _LiveScale.x) )   // 267 ブロック・640px なら 1.68
色差はさらに +1.0                                          // 4:2:0 とクロマの粗い量子化
```

- **1 タップのまま**（trilinear が中間 LOD を補間するので、進みが連続量のまま画に出る）
- ⚠⚠ **ソースが `mipChain` を持っていないと LOD は黙って無視され、画は 1 画素も変わらない。**
  `CameraStream` / `MjpegScreen` / `RecordedFramePlayer` / `ScreenOverlayController`（素材の
  ダウンロード後に作り直す）/ `ShowCompositePreview` のプレート、**全部 mipChain:true + Trilinear**。
  1 つでも漏れると**そこだけ鮮明**になり、切り替わった瞬間に画の素性が変わって差し替えがばれる
- **著作した `_Pixelate` は硬い格子のまま**（卓の FS_POST と同じ絵でなければならない）。
  粗い方だけを掛ける規約は不変

#### post が 12 項目になった（色収差・低解像度化・走査線の本数）

`aberration` / `pixelate` / `scanlineCount` を追加。既定 0（`scanlineCount` の 0 は「未指定 → 240」）で旧データと同じ絵。

- 色収差と低解像度化は**色ではなくサンプル位置**を動かすので合成の前段に入る。実機・卓とも同じ式。
- 走査線の本数を持たせたのは、実機が material の 240 固定・卓が canvas の縦画素（360〜480）で、
  **同じ値でも縞のピッチが 1.5〜2 倍食い違っていた**ため。グレインの座標も等方 480 + `fract(時間)` へ揃えた。
- ⚠ post に項を足すときは **ScreenComposite.shader / shaders.js の FS_POST / common.js の FX 表 / pipeline.js の uniform 受け渡し**
  の 4 箇所を同時に直す（機械テストが無く、片方だけだと沈黙して食い違う）。

#### 色統計マッチングが実機に届くようになった

企画書 2.3「差し替え素材の全体には色統計マッチングを施し、実写映像と継ぎ目なく合成する」。

卓が Reinhard per-channel を解いて **RGB の gain/offset（6 float）へ落とし cue へ焼く**
（[`color-match.js`](../../tools/web-compositor/color-match.js)・node テストあり）。実機は
`_OverlayGain` / `_OverlayOffset` を掛けるだけ。**向きは「素材を実写へ寄せる」**（実機で動かせるのは素材の側だけ）。
焼くかどうかは卓の「境界ブレンド」の色統計トグルに従う。
多重帯域ブレンディングは Quest で重いので実装していない（企画書は「フェザリング**や**多重帯域」と選択で書いている）。

**焼く経路は [`bakeColorMatch`](../../tools/web-compositor/color-match.js) 1 本**（2026-07-30 に一本化）。
cue を作る面はすべてこれを通す — 素材工房は通っておらず、**卓は色を合わせた絵を出すのに実機は素の色**
という食い違いが出ていた。

- **⚠ マスクがあるなら「差し替えない所」だけで解く**（2026-08-05）。差し替える所には素材にしか
  居ないもの（人形・手形）が写っていて、実写側には居ない。そこを平均に混ぜると
  「主題が背景と違う」ぶんまで補正しようとして、**もともと合っていた背景の方がずれる**。
  実測: 全画素で解いたら背景の一致が 1.5 → 20.4 に悪化した（人形が画面の 17% を占める素材）。
  マスクの外だけで解けば 2.5 に収まる。`bakeColorMatch(live, src, cfg, maskEl)` の第 4 引数
- マスクは**枠空間（16:9）**、素材は**ソース空間（ふつう 4:3）**なので、統計を絞るときは
  contain-fit を逆にたどってマスクの中央 75% を読む（[`maskRectForSource`](../../tools/web-compositor/color-match.js)・node テストあり）
- **棚卸しは [`mask-audit.py`](../../tools/web-compositor/mask-audit.py)**。いま演出が指している素材の
  マスク・合成結果・色ずれを 1 枚の HTML に出す。素材を差し替えたら通す

- **動画は尺全体から複数点サンプルして畳む**（`statsFromMedia` → `averageStats`）。実機が持てる補正は
  1 組だけなので、保存した瞬間の 1 フレームで解くと暗→明の素材で後半が破綻する。
  プールした sd は**群平均のばらつきを含める**（単純平均だと明暗の振れ幅がまるごと落ち、gain が倍以上ずれる。
  `color-match.test.mjs` が数値で固定）
- **ラプラシアン（多重帯域）は卓プレビュー専用なので既定 OFF**（`common.js` の `blendCfg`）。
  ON にすると卓だけ継ぎ目が消えて見えるので、チェックボックスに「卓だけ」の印を出す。
  色統計は焼かれて実機に届くので既定 ON のまま。**この 2 つを同じ見た目で並べない**

#### 遅延の「管理」で測っているもの / 測っていないもの

企画書 2.3「映像伝送の視覚遅延は 100 ms 程度以内を目標として管理する」。

**絶対の end-to-end は測っていない。** `X-Capture-Ns` は配信端末の monotonic、Unity 側は自前の Stopwatch で
基準が違い、引き算すると端末間の時計のずれがそのまま遅延として出る。推定の定数を足して「100ms でした」と言うより、
測れる 3 つを分けて出す方が正しい（[`LatencyEstimatorLogic`](../../Assets/Scripts/Streaming/LatencyEstimatorLogic.cs)）:

| 出しているもの | 何 |
|---|---|
| `ArrivalJitterMs` | 到着の揺らぎ。`受信 − 撮影` の 10 秒窓の**最小値からの超過**。時計のずれは最小値の側に吸われるので差分だけは正しい |
| `DecodeMs` | 受信スレッドの払い出し → メインスレッドの展開完了 |
| `PresentMs` | 表示レートから 1.5 フレーム |
| `SourceAgeMs` | `/health.latestFrameAgeMs`（配信側のカメラ stall） |

絶対値が要るなら**配信アプリ側に「`X-Capture-Ns` と同じ基準の現在時刻」を返す口**（例 `/clock`）が要る。
それが入れば `ObserveArrival` のオフセット推定に差し替えるだけで済む形にしてある。

あわせて `/health` の熱フィールド（`thermalStatus` / `throttleStage` / `thermalHeadroom` / `batteryTempC`）を
DTO へ追加し、**熱で降格している間は lag 判定を抑止**する。旧実装は熱で落ちた fps を経路の詰まりと誤認して
5 秒ごとに MJPEG を張り直し、黒 / 砂嵐を出しながら事態を悪化させていた。

表示レートは [`DisplayRateRequester`](../../Assets/Scripts/OvrBridge/DisplayRateRequester.cs) が実行時に 90Hz を要求する
（共有 ProjectSettings は触らない）。**遅延対策の主役ではない** — 縮むのは提示ぶん約 4ms で、快適性の項目。

#### CG 人形は較正だけでも出るようになった

実機は `cameras[].pose` が無いと人形を出さず、卓は「較正があれば出る」と判定して輪郭を描いていた
（卓で ✅ に見えて実機で出ない）。較正の解から概算姿勢を作って出すようにして両者を揃えた。

**⚠ ここまで Quest 実機未検証**（2026-07-29。EditMode 849/849・node 219/219・卓はブラウザで DOM と GLSL の実測確認）。

### 開始規則「このラインを通過したら」（通過ライン）— 2026-07-27

設計の正本は [2026-07-27_position-trigger.md](../plans/2026-07-27_position-trigger.md)。v3 への**追加のみ**（既存 show.json はそのまま読める）。

- **演出（Take）の開始規則が 3 つになった**: `at:"enter"`（進入 +t 秒）/ `at:"exit"`（離脱時）/
  **`at:"line"`（床の線分を横切ったら）**。演出は今までどおり**区間 (lap, camera) に属する** — 変わるのは
  due になる条件だけなので、`once` / `ifMissed` / 「同時 1 本」/ 離脱時の決着 / ランリセットは**すべてそのまま効く**
  - 当初案は円（エリア進入・`layout.spots` / `at:"spot"`）だったが、同日にユーザー指示で
    **ライン通過へ変更**した（エリア＝状態 / ライン＝事象で、開始規則と型が合うのは後者）。円版は 1 コミットで消えたので互換読みは無い
- **線は `layout.lines[]`**（`{id, camera, x1, z1, x2, z2, dir, label}`・course 空間）。卓のフロアマップ
  **📏 通過ライン**モードでドラッグして引く。`layout` の一部なので端末キャッシュ・APK 焼き込み・shallow 置換保存に自動で乗る
  （`regPoints` と同じ立ち位置。サーバ側の変更は不要）
- **⚠ ラインは担当カメラに紐づく（二重の縛り）**: ①演出の武装は区間限定（既存機構）＋
  ②`lines[].camera` と演出の区間カメラが**一致しないと due にしない**（`TakeRunnerLogic.IsDue`）。
  これで「手違いで別の領域のラインを踏んでも何も起こらない」が構造的に成立する。卓も担当が一致する線しか選択肢に出さない
- **take 側**: `lineId`（空 / 未定義 id / 担当違いは**発火しない** + ログ 1 回）。`offsetSec` は at=line では無視
  - **⚠ 実装規約（2026-07-27 監査で両側修正）**: 使えないラインでも **`onLine` を下ろしてはいけない**。
    下ろすと時刻トリガー扱いになり `offsetSec`(=0) で**区間に入った瞬間に発火**する（契約の真逆）。
    「決して発火しない」は `lineIndex = -1`（`TakeRunnerLogic.IsDue` が常に false）で表す。武装はされるので
    `ifMissed=fireOnExit` は従来どおり効く。旧実装は実機＝空 lineId が進入即発火、卓＝3 ケースすべて進入即発火で、
    卓は「発火しません」と警告した当の演出を再生していた（`TakeWiringTests.LineTake_WithoutLineId_*` で固定）
- **通過方向** `dir`: `both`（既定）/ `fwd`（卓の矢印の向きだけ）/ `back`（逆だけ）
- **判定** = [`LineCrossLogic`](../../Assets/Scripts/Streaming/LineCrossLogic.cs)（純ロジック）:
  **毎フレームの移動線分 × ライン線分の交差**（端の外を回り込んだら横切っていない）/ 横断直後は
  線から 0.06m 離れるまで再検出しない / dt 不連続（>0.5s）・1 フレーム 1m 超の移動は数えない（HMD 着脱・recenter）/
  **高さは見ない** / 0.05m 未満の線は無効。位置は `ShowControlClient.HeadCourseXZProvider`（登録済み course 座標）
- **横断には 0.6s の猶予（`CrossLatchSec`）**: 武装はゾーン確定（dwell 既定 0.5s）で起きるので、
  ゾーンの入口すぐに引いた線は「入った瞬間に横切る → 0.5s 後に武装」で**永久に沈黙する**。
  横断時刻を覚えて猶予内なら due と認める（担当カメラ照合は猶予中も効く）
- **発火は 1 区間滞在につき最大 1 回**。`once=true` ならラン内 1 回
- **`ifMissed` はそのまま効く**（この区間に居るあいだに通らなかった時）。**卓の既定は `skip`**（時刻トリガーの `fireOnExit` と違う）
- **▶ 検証（シミュレータ）でも発火する**（JS ミラー `LineCross`）。一致は golden
  [`scenario_line.trace.json`](../../Assets/Tests/Fixtures/scenario_line.trace.json)（C# が生成・JS が照合）で機械固定
  - **⚠ シミュレータは起動時にスタート区間をシードする**（トレース先頭の `seg`）。実機の
    [`LapCounter.SeedCurrentZone`](../../Assets/Scripts/Tracking/LapCounter.cs) と対。これが無いと
    **1 周目スタート領域の演出が永久に武装されない**（2026-07-27 実害: 1 周目の通過ラインが卓で沈黙。実機では出る）
  - **⚠ ドラッグ位置は tick へ等速で分配する**（`show-sim.js` の `advance`）。1 フレーム分の tick に同じ位置を
    配ると「テレポートして立ち止まる」動きになり、1 tick の移動が 1m（`LINE_MAX_STEP_M`）を超えた瞬間に
    **横断が数えられない**（×4 / ×16 の早送りでは普通のドラッグで超える）
- **⚠ Quest 実機未検証**（2026-07-27。EditMode 730/730・node 81/81・卓はブラウザ実操作で確認）

### 演出の素材と層を増やす（録画 / スロット / CG / 演出専用カメラ）— 2026-07-26

設計の正本は [2026-07-26_show-sources-and-cg-layer.md](../plans/2026-07-26_show-sources-and-cg-layer.md)。v3 への**追加のみ**で、既存 show.json はそのまま読める。

- **`source:"rec"`（端末内録画）**: `camera` + `recLap` で「この体験の N 周目・カメラ X の区間映像」を指す。
  録画の単位は区間（`(lap,camera)`）で、駆動は**ショーの時計**（画面が何を映していても録る）。
  保存は `Application.temporaryCachePath/rec/<runEpoch>/L<lap>C<cam>.mjr` の**生 JPEG 列**
  （mp4 化しない＝再エンコード劣化ゼロ・デコード経路はライブと同一）。**ラン開始で端末の録画を全部消す**
  （現ランの epoch だけ残すと、現地リセットで進んだ番号に卓の runEpoch が後から追いつき、
  前の体験者の映像が「このランの録画」として再生される）。録れていなければそのカットを飛ばす（§6.4）
  - **⚠ 「録れた」と「画に出た」は別**（2026-08-02 に観測を分けた）。ファイルを開けただけのカットは
    絵が 1 枚も出ないまま尺を消費し、ログ上は演出が走ったように見える。**暗い現場では目視で区別できない。**
    → `[XP] ev=rec v=stop … frames=`（書けた枚数。バイト数はヘッダだけの空ファイルでも 0 にならない）と
    `ev=recplay v=open/close … presented= failed= luma=`（**実際にテクスチャへ載せた枚数**と輝度）を出す。
    `analyze-xp-log.py` の「## 端末内録画 — 録れたか / 再生されたか」が両方を FAIL 判定する
- **設定は show.json トップレベル `record`（既定 無効）** — 卓の **⏺ 端末内録画パネル**（タイムラインの下）で編集する。
  `laps` は**録る周の配列**で、既定 `[1]`。「1周目 B・1周目 C・2周目 A を録って 3 周目で流す」なら `[1,2]` が要る
  - **録画係（[`SegmentRecorder`](../../Assets/Scripts/Streaming/Recording/SegmentRecorder.cs)）はシーンに置かれていない。**
    `ShowControlClient` が `record.enabled` を見て自動生成する（`EnsureRecorder`）。生成点は
    **起動時（焼き込み / 端末キャッシュ）と ライブ受信の両方**。2026-07-29 まではライブ受信の側にしか
    無く、**卓が居ない現地では `record.enabled` を焼き込んでも 1 フレームも録れなかった**
  - **⚠⚠ 残すのは「切り替えの `tailSec` 秒前 〜 `postSec` 秒後」だけ**
    （2026-08-06 に「頭から `maxSegmentSec` 秒」を末尾方式へ置き換え、2026-08-14 に切り替え後を足した）。
    再生は 3 周目にその区間へ**入った瞬間**に始まるので、頭から録ると**映像の中の過去の自分も入口に居て、
    体験者の現在位置に立つ CG 人形と重なる**。末尾＝区間を出る直前なら、過去の自分は出口側に居て位置が分かれる。
    しかも「さっき自分が出て行った直後の残り香」という筋になり、その後の無人プレートへ自然に繋がる。
    - 実装は [`SegmentRecordWriter`](../../Assets/Scripts/Streaming/Recording/SegmentRecordWriter.cs) のメモリリング。
      区間中ずっと積み、時間と容量の 2 条件で古い側を落とす。書き出しは区間が閉じるときに 1 回だけ
      （背景スレッド。5s × 15fps × 60KB ≒ 4.5MB なので実測数十 ms）
    - **pts は先頭 0 起点へ振り直す**。振り直さないと `RecordedFramePlayer` が頭を空回りして、
      再生開始から絵が出るまで無音の間ができる
    - **1 周目 A も使える**（導入直後ではなく「A を出る直前」が残るため）。
      現行 show.json は `laps:[1,2]` で、3 周目の A/B/C ← 1 周目、帰りの A ← 2 周目 A
    - **容量が滞在時間に比例しない**のが副次的だが重要な効き目。旧方式は区間 60s × 4 = 216MB で
      `maxTotalMB` 200 を超え、**ゆっくり歩く体験者では後半の区間が録れなかった**
    - 旧キー `maxSegmentSec` は読むだけで**使わない**（古い show.json / 端末キャッシュのため）。卓の UI からは消した
  - **⚠⚠ `record.startLineId` を指すと、末尾ではなく「線を横切った所から」録る**
    （2026-08-16・`canon/LEDGER.md` 0061）。末尾方式は「区間を出る直前」を残すので、
    **そのカメラに体験者が写っていない区間では無人の部屋しか残らない**。
    実測（1 周目 A・4.89 秒）は **43 枚中 最後の 1 枚にしか人が写っていなかった**。
    - 効くのは**線の担当カメラ**（`lines[].camera`）と同じカメラの区間だけ
    - **横切らなかった区間は末尾方式のまま**（何も残らない、を作らない）
    - ⚠ **検出器は `SegmentRecorder` が自前で持つ。`TakeRunner` の線と共有しない** —
      向こうは「演出に線を使う台本があるときだけ」`LineCrossLogic` を回すので、
      演出から線を外した瞬間に**録画の起点が黙って消える**
    - ⚠ **打つのは横切ったその瞬間だけ。** 残せるのは**いまリングに載っているぶん**で、
      それより前は末尾方式の trim が既に捨てている（遡って指定しても戻らない）
    - ⚠ **2 度目の横断は無視する**（行ったり来たりで起点が動くと走行ごとに違う映像になる）
    - 観測は `ev=rec v=stop` の **`start=`**（1 = 線から / 0 = 末尾方式）。
      **枚数では区別できない**ので、「線を指したのに効いていない」はここでしか分からない
    - ⚠⚠ **3 周目 A の録画の起点は「凍結の線の画像空間の鏡」でなければならない**
      （2026-08-22・`canon/LEDGER.md` 0102）。3 周目 A は左半分だけライブを**左右反転**して読むので
      （`splitFlip`）、鏡の軸は**枠 UV の 0.5**＝ソース画像の横中央。ところが course 空間で
      対角軸に対する鏡像として置くと、**カメラ A が対称軸の真上に無い限り画面上では対称に写らない**
      （透視とレンズ歪みが軸の両側で違う効き方をする）＝「録画が凍結位置の外から始まる」。
      - **卓の 📏 通過ラインの一覧に 🪞 ボタン**がある。押すと `projectPoint` → 横中央で反転 →
        `unprojectToFloor` で床へ戻し、鏡像の線を作る / 作り直す。数学は
        [`tools/web-compositor/line-mirror.js`](../../tools/web-compositor/line-mirror.js)
        （**順投影は `calib.js` の `projectPoint` をそのまま使う** ＝ Unity / シェーダと同じ式。
        ここで別式を書くと「卓では合うのに実機で合わない」という最悪の破れ方をする）
      - 対は線の **`mirrorOf`**（この線がどの線の鏡像か）で持つ。
        ⚠ **`linesFromLayout` の白名簿から漏らさない** — 漏らすと 💾 保存の一押しで対が消え、
        本番前チェックは「鏡対応が崩れている」を二度と言えない
      - **片方を動かしたら必ず 🪞 を押す。** 押し忘れは一覧の `⚠ 鏡が Ncm ずれ` と
        本番前チェックの **`🪞 鏡の線`** 行が ❌ で名指しする（残差の許容 6cm）
      - ⚠ **鏡の軸は `splitX` そのものではない。** 実機は `splitX` の値に関係なく 0.5 で反転する
        （3 周目 A は 0.5 なので一致するが、分割位置を動かしても軸は動かない）
      - ⚠ `MjpegScreen.uvRotSteps` が 0 でないカメラでは軸が縦になるのでこの計算は成り立たない
        （あの値は show.json に無い ＝ 卓からは見えない）
      - **残るずれは 2 つ**（機構では消えない）: ②凍結は 3 周目の横断点・録画の頭は 2 周目の
        横断点なので**歩線が違えばその差だけずれる**（線を通路の狭い所に短く引き直して減らす）／
        ③継ぎ目が裸だと残差がそのまま「跳んだ」に見える（`transition:"swap"` の
        `VeilLevel` 0.55 × 0.20 秒の乱れが既に覆っている）
      - 観測は `analyze-xp-log.py` の「## 凍結から録画の頭まで」（凍結の時刻 → 録画が画へ載った時刻の間隔）と、
        `xp-evidence.py` が焼く **`_pair_*.png`**（凍結の最後のコマと録画の頭のコマを**縮小せずに**並べた 1 枚。
        ⚠ ずれは 2 枚の差でしか出ないので、1 枚ずつ眺めても分からない）
  - **⚠⚠ 区間の切れ目で録画は止まらない — 切り替え後も `postSec` 秒だけ録り続ける**（2026-08-14）。
    切り替えの瞬間で切ると、**過去の自分が角を曲がり切る前に映像が終わる**（曲がる動きはカメラが
    切り替わってからも 1〜2 秒続くので、場所によっては歩き出しの途中でぷつりと切れていた）。
    - したがって切り替えの前後は**書き手が 2 本同時に走る**（現区間 ＋ 1 つ前の追い録り）。
      3 本目は作らない — 追い録りの最中にもう一度切り替わったら、古い方をその場で閉じる
    - ⚠ **トリムの基準は切り替えの瞬間で凍らせる**（`SegmentRecordWriter.BeginPostRoll`）。
      「いま」のままだと、追い録りした秒数だけ**切り替え前が押し出されて消える**
      ＝ この機能を足したせいで一番見せたい所が減る
    - ⚠ **`postSec` は 0 を「追い録りなし」にしない**（0 以下 = コード既定 2 秒）。JsonUtility は
      キーの無い show.json でも 0 を書くので、既存の焼き込み・端末キャッシュでは**必ず 0 が入る**。
      0 を無効と読むと、この機能は設定を書き直した現場でしか効かない（`tailSec` と同じ流儀）
    - ⚠ **閉じるのは時計で判断する**（`SegmentRecorder.Update`）。フレームが来なくなっても必ず閉じる
    - ⚠ 同じカメラに 2 本ぶら下がりうる（周をまたぐ同一カメラの区間）ので、
      `CameraStream.FrameTap` は**ストリーム 1 つにつき 1 個**にして、そこから開いている区間へ配る
  - **`maxTotalMB` は「ラン全体」の上限**（区間ごとに残量を配る）。旧実装は各区間へ満額を渡していたので、
    実効は区間数倍だった。上限に達したらリングの古い側から落として入る分だけ残す（`Capped=true`）。
    ⚠ **まだ閉じていない追い録りのぶんは先に差し引く**（閉じるまで総バイト数に乗らないので、二重に配ると上限を越える）
  - **卓が対応を照合する**: 「録画」カットが指す (周, カメラ) を録る設定になっていなければ、
    ⏺ パネル・本番前チェック・カットの警告・▶ 検証がすべて ❌ と理由を出す（判定は
    [`record-model.js`](../../tools/web-compositor/record-model.js) が単一の正・node テストあり）。
    これが無いと**実機だけが黙ってカットを飛ばす**（気づけるのは実機のログだけ）
  - **尺は `tailSec + postSec` で確定する**（著作時に決まる）。卓は `≈` を付けずに
    `長さ 5s（切り替えの 3s 前 〜 2s 後）` と出す。滞在が `tailSec` より短ければ**切り替え前だけ**
    その分に縮むので、実測滞在があれば前側だけ短い方を採る（切り替え後は次の区間に居るあいだ録るので縮まない）。
    **尺を別に決めたいなら秒指定**（頭から N 秒。rec カットに trim は効かない）
  - **⚠ 再生は「体験者が切り替え位置に着いた時点」で打ち止める**。録画は切り替えの後まで持っているので、
    前の周より速く歩くと**まだ流し終わっていない**。3 周目の演出は `policy:"yield"` なので、
    ゾーン確定でその演出ごと畳まれ、**次の区間の録画が冒頭から**始まる（居ない場所の続きを見せない）。
    契約は `TakeRunnerLogicTests.Yield_CutsStillPlayingRecording_AndNextSegmentStartsFromItsHead` が固定する。
    ⚠ **`policy:"hold"` にすると成立しない**（前の区間の録画を持ったまま次の区間へ入る）
  - **観測**: `ev=rec v=start/stop`（**2026-08-14 からイベント駆動**）と `ev=sum` の `recPost=`。
    追い録りで**開始と終了が入れ子になり**、しかも枚数・バイト数は閉じた瞬間にしか確定しないので、
    ポーリングだと閉じた区間が `frames=0` に見えて解析器が偽の FAIL を出していた
- **`slot://<name>`（素材スロット）**: ラン中に卓が実体を束縛する素材（入口で撮って生成した人形動画など）。
  束縛は `POST /command {type:"bindSlot", name, url}` → `control.slots`。**timeline は触らない**のが要点で、
  timeline を保存し直すと発火済み（`once`）の演出が再武装される。未束縛のスロットを指すカットは飛ばす
- **`cg` / `cgMode`（CG 人形）**: `ScreenComposite` の 3 層目 `_CgTex` に、実カメラ姿勢
  （`cameras[].pose`・course 空間）で構えた**双子の仮想カメラ**が CG レイヤだけを描く。
  合成は**ポスト FX の前**（映像と同じ露出・走査線・グレインを浴びないと必ず浮く）。
  `follow` = 体験者の HMD XZ に立つ / `fixed` = 著作位置。**姿勢が未著作のカメラでは出さない**
- **`cameras[].role`**: `"fx"` は演出専用カメラ（＝カメラ D）。ゾーンに割り当てず、スタッフの A ボタン巡回にも出さない

### 無人プレート（`source:"plate"`）と「次にカメラが切り替わるまで」（`durKind:"untilZoneChange"`）— 2026-08-05

3 周目の「録画を流す → 流し終わったら**無人の部屋だけ**が映り、そこに体験者の分身（CG 人形）が立っている」
を作るための 2 つ。どちらも**追加のみ**で、既存 show.json はそのまま読める。

- **`source:"plate"`** = そのカメラで撮った**無人の実写プレート**（`camera` + `cueId` / `assetUrl`）。
  絵の出し方は `still` と同じだが、**CG 人形を許す点が違う**。判定は
  [`TakeSchema.MatchesCameraPerspective`](../../Assets/Scripts/Streaming/ShowTakeSchema.cs)（`rec` と `plate` だけ true）。
  一般の素材（`clip` / `still`）は「いつどこで撮ったか分からない画」なので、どのカメラの較正を当てても
  人形のパースが合わない。プレートは `rec` と同じく step.camera で撮った画なので較正がそのまま効く
  - **`camera` を必ず指す**。指さないと人形の構図が決まらない（卓の本番前チェックが ❌ で止める）
  - 画面のカメラは動かさない（`rec` と同じ非 live 経路 = `TakeHoldBegin`）。プレートが枠を覆う
- **`durKind:"untilZoneChange"`** = 体験者が**次の区間へ移るまで**そのカットを出し続ける。
  畳むのは **ショーの時計（`ZoneCommitted`）だけ**で、素材の終端では畳まない
  （[`TakeRunnerLogic.NotifyZoneChanged`](../../Assets/Scripts/Streaming/TakeRunnerLogic.cs)）。
  スタッフの手動送り・Web の cameraOverride・インサートの画面切替は時計を動かさないので終わらない
  - **尺の負値は「待つ相手」を表す**: `WaitClipEnd = -1`（素材の終端）/ `WaitZoneChange = -2`（次の区間）。
    `NotifyCurrentStepFinished` と `NotifyZoneChanged` は**それぞれ自分の待ち相手のカットしか畳まない**
  - 体験者が動かなければ終わらないので、上限は watchdog（`maxDurationSec` 既定 45s）が保証する。
    録画（前の周の滞在ぶん）＋ プレート（この区間に居るあいだ）は 45s を超えうるので、
    **3 周目の演出には `maxDurationSec` を明示する**（現行 show.json は 120）
  - **卓は秒へ推定しない**。ゾーン確定は卓にもあるので実機と同じ所で畳める
    （`show-scenario.resolveStepDuration` → `WAIT_ZONE_CHANGE` / `scenario-engine.notifyZoneChanged`）。
    リボンの幅は仮置きで、必ず `≈` が付く

**3 周目の演出は `policy:"yield"` + `wait:"segment"` + `ifMissed:"skip"`**（2026-08-05 に組み直し）。
録画の長さは「前の周にその区間へ居た時間」なので、3 周目に速く歩くと録画が終わらないまま区間を出る。
旧設定（`hold` + `chain`）だとその演出が持ち越され、**3 周目 B の演出が C の区間で 12.7 秒遅れて出ていた**
（実測）。yield なら区間を出た瞬間に畳まれ、次の区間の演出が定刻で始まる。

⚠ **その代わり「録画が終わらなければ無人プレートは出ない」**。プレートが出るのは、体験者が
3 周目にその区間へ 1 周目と同じかそれ以上とどまった時だけ。これは仕様（録画の再生が終わってから
無人へ変わる、という筋がそのまま出ている）。

**⚠ Quest 実機未検証**（2026-08-05。EditMode 1008/1008・node 386/386・卓のブラウザ実操作で
「録画 → 無人の部屋を全面」まで確認）。

### スクリーンの外の闇に開く目（`steps[].eyes`）— 2026-08-17

世界観の逐語は `canon/LEDGER.md` 0075。**画面の外だけで起きる異変**で、
スクリーン（`ScreenComposite` = Geometry / 不透明）より**後ろ**に描くので、
どの映像のカットにも足せる（`source` を選ばない）。

| | |
|---|---|
| 判断 | [`AnomalyEyesLogic`](../../Assets/Scripts/Streaming/AnomalyEyesLogic.cs)（純ロジック・テスト 21 本） |
| 座席表 | [`AnomalyEyesMesh`](../../Assets/Scripts/Streaming/AnomalyEyesMesh.cs)（候補 900 から**重ならないものだけ**を詰める・**乱数を使わない**） |
| 実行体 | [`AnomalyEyes`](../../Assets/Scripts/Streaming/AnomalyEyes.cs)（シーンの `[Eyes]`。`MainDemoSceneSetup` が置く） |
| 形 | `Assets/Art/Shaders/Streaming/AnomalyEyes.shader`（Queue **Background+100** / `Blend One OneMinusSrcAlpha`） |
| 卓 | カット詳細の「闇の目」（0..1） |
| 見る | `.\tools\unity.ps1 menu eyes`（348 コマ ＋ 段ごとの静止画 12 枚。`-Set density=0..1`） |
| 観測 | `ev=sum` の **`eyes=<組めたか>/<開いている数>/<不透明度>/<区間の進み>/<速さ>/<カットの指示>/<流しきり中か>`** |

⚠⚠ **明るさ（`gain`）の実物はシーンに焼かれている**（`Main.unity` の `[Eyes]`）。
C# の `AnomalyEyes.DefaultGain` が効くのは `[Eyes]` を作り直したときだけなので、
**片方だけ動かすと黙って食い違う**（`MainDemoSceneSetup.CreateOrUpdateEyes` は参照しか書かないので、
`menu scene` はシーンの値を上書きしない）。

- **2026-08-23 に 0.85 → 0.50**（ユーザー赤入れ「目が明るすぎる。もう少し暗くしてほしい」）。
  画に出る値で 尖頭 0.872 → 0.688・灯った画素の平均 0.397 → 0.308（`menu eyes` の `stage_4_hold` 実測）
- ⚠⚠ **`EyesPreview` も同じ値を書く。** 2026-08-23 まで `_EyeGain` / `_EyeBlink` / `_EyeColor` を
  配っておらず、**プレビューだけシェーダ既定**（明るさ 1.0・生成りに近い白）で描かれていた ＝
  **明るさを触っても絵が 1 画素も変わらない計器**だった。
  **同じ書き漏らしは 2 度目**（1 度目は 2026-08-17 の `_EyeGaze` で「動きが小さい」と誤診）なので、
  [`AnomalyEyesPreviewParityTests`](../../Assets/Tests/Streaming/AnomalyEyesPreviewParityTests.cs) が
  **本番が書く uniform をプレビューが 1 つでも落としたら落とす**（`gain` のシーンと const の
  食い違いも同じテストが見る）
- ⚠ **明るさは alpha に掛からない**（`return half4(col * _EyeGain, a)`）。下げても目の形は同じだけ闇を隠す。
  だから**灯った画素の数はほぼ変わらない**（実測 -0.2%）— 面積で判定すると「何も起きていない」に見える

⚠⚠ **開き始めと閉じ始めは、カットの尺ではなく体験者の居場所が決める**
（2026-08-19・`canon/LEDGER.md` 0093・[`EyesCueLogic`](../../Assets/Scripts/Streaming/EyesCueLogic.cs)）。
`steps[].eyes` が言うのは**どの区間で出すか**までで、いつ開きいつ閉じるかは言わない。

| 縁 | 何が起きるか |
|---|---|
| 体験者が区間へ**入った** | 兆しから始まる（＝ カットの頭。従来どおり） |
| **区間の長さの半分**まで来た | 終了演出へ入る。⚠ **まだ開き切っていなければ、閉じずに倍速で開き切ってから閉じる** |
| 途中で**カメラが切り替わった** | **打ち切らない。** 倍速で流しきる（最悪 4.1 秒はカットより長生きする） |
| ラン開始・卓の中止・位置合わせ | **1 フレームで消す**（`AnomalyEyes.Abort`）。ここだけは流しきらない |

⚠ **閉じる動きは倍速にしない。** 1 つの瞼が下りるのは 0.11 秒しかなく、
倍にすると 30fps で 1.6 コマ ＝ 0085 の赤入れ（閉じるアニメーション）が消える。
⚠ **区間の長さは「同じカメラの矩形すべて」で測る**（[`ZoneSpanMath`](../../Assets/Scripts/Tracking/ZoneSpanMath.cs)）。
貪欲分解（`SolveGrid`）は 1 つのカメラを複数の矩形に割ることがあり、入った矩形だけで測ると半ばが手前へずれる。
⚠ **0 は矩形の端ではなく「入った所」**（重なりとヒステリシスのぶん、確定するのは端より内側）。
⚠ **位置を測れない現場**（未登録 / layout 不在）では進みが **-1** で出て、従来どおりカットの終わりで畳む。
位置を必須条件にすると、位置合わせをしていない機で目が一生出なくなる。

##### ⚠⚠ 出番は「宣言された区間」でしか始まらない（2026-08-23）

ユーザー赤入れ「3-C ですべての目が閉じた後、4-A で目が一つ出てきてしまっている」。

**区間の切り替わりは 2 つの速さで進む。**

| 何 | いつ進むか |
|---|---|
| 居場所（`ZoneSpan.visit` / `camera`） | 体験者が**線を跨いだ生の瞬間**（`ZoneLayoutApplier.OnZoneChanged`） |
| ショーのカット切替（`steps[].eyes` の値） | **滞在 0.5 秒を待ってから** |

その 0.5 秒のあいだ、**3 周目 C のカットがまだ `eyes:1` を言ったまま、居場所だけが 4 周目 A**
になっている。`Spent`（同じ滞在で二度目を始めない錠）は滞在が変われば降りるので、
**そこで新しい出番が始まり、次の区間の頭で大きい目が 1 つだけ開いて閉じていた**。

⇒ [`EyesCueLogic.Declare`](../../Assets/Scripts/Streaming/EyesCueLogic.cs) が
**「言い始めた縁の居場所」**を刻み、`Tick` はそこでしか出番を始めない。
刻むのは `AnomalyEyes.Apply`（`eyes` が 0 → 正になった縁だけ）。

- **引き返して入り直したときは従来どおり頭から再演する** — カットが撃ち直されて
  新しい滞在で刻み直されるため（`ReEnteringTheZone_StartsAgain_WhenTheCutDeclaresAgain`）
- ⚠ **位置を測れない現場では刻んでも効かない**（上の ④ と同じ思想。効かない機で目が一生出なくなる方が高い）
- ⚠ **画からは「流しきり」と区別が付かない。** カットが終わっても目は数秒残るのが正しい（0093）ので、
  実機で見ても両者は同じに見える。判定は `analyze-xp-log.py` の
  **「カットが何も言っていないのに目が開き直した」** — 一度**閉じ切った**所から
  **指示 0 のまま開き直した**縁だけを見る（`eyes` の 3 つ目と 6 つ目）。
  実測 `logs/capture/20260823_151457_xp.log` t=488.1
  `lap=4 take=L4C0#0 eyes=1/1/1.00/0.01/2.00/`**`0.00`**`/1`

**段は 3 つ。尺は著作させない**（`eyes` で指せるのは「出すか」と「何割の目が開くか」だけ）。
⚠⚠ **等速で開かない。「止まる」と「一気に」の繰り返しで組んである**（`canon/LEDGER.md` 0076）:

| 段 | 尺 | 何 |
|---|---|---|
| 兆し | **1.42s** | 闇 **0.70s** → **音の頭で一気に**（0.04s で 0.45 まで）→ **音の膨らみのとおりに開き切る**（頭から 0.27s）→ 開いたまま 0.45s（**2026-09-04・0138**。それまでは 闇 0.50 → 断片 0.10 → 静止 0.50 → 見開く 0.13） |
| 凝視 | **0.5s** | 静止。途中で 1 度だけ瞬く（0.055s） |
| 開眼 | 3.0s | さざめき 0.27s → **間 0.36s** → 一気に 360 度（1.6s）。**1 つの目が開くのは 0.11 秒** |

**全開まで 4.92 秒 ＋ 閉じ 1.6 秒。**
⚠⚠ **2026-08-20 に、止まっている 3 つを 0.5 秒へ詰めた**（`canon/LEDGER.md` 0094・
早回しの動画を見たユーザーの判定「こっちのほうがいい」）。**動いている所は 1 つも触っていない** —
断片が現れる 0.10 / 見開く 0.13 / さざめき 0.27 / 間 0.36 / 一気に 1.56 は 0076 のまま。
⚠⚠ **2026-09-04（`canon/LEDGER.md` 0138）に兆しの開き方を「音の形」に合わせた。** ユーザー逐語
「最初におおきい目が開く時のアニメーションと、開く時の音が、一致感があまりない。音にアニメーションを
合わせてほしい。自然な感じに」。断片と静止をやめ、**闇 0.70 秒 → 音の頭で一気に → 音の膨らみのとおりに
0.27 秒で開き切る**（`AnomalyEyesLogic.HintOpenKnots` ＝ 焼いた `sfx_eye_big` の聴感を測った表）。
音は 1 ビットも動かしていない。正本は `rules/sound-design.md` §2.7。
⚠ **0075 の「凝視 ＝ 気づいて報告ボタンを押すくらいの時間」はここで無くなった**（0.5 秒では押し切れない）。
目の異変は `dismissible` を立てていない（0084）ので、押し切れるかは体験の成否に効かない。
⚠ 詰め方を試すときは `menu eyes` の **`-Set trim=<秒>,hold=<秒>`**（出荷の尺を変えずに早回しの版を焼く）。

⚠ **1 つの目の開き（`SwarmSpan`）を大きくしない。** 初版は 0.84 秒で、判定は
「ゆっくり過ぎて怖くないし、演出として面白くない」だった。実物の目は 0.1 秒で開く。

⚠⚠ **報告は引き金ではない**（0075）。段は押しても押さなくても同じ速さで進む。
押して消したいなら**演出の側に `dismissible` を立てる**（`LEDGER` 0050 の仕組みがそのまま効く）。

⚠⚠ **前乗算アルファ（`Blend One OneMinusSrcAlpha`）で描く**（2026-08-17 に加算から変えた）。
加算では**重なった 2 つが必ず 1 つの塊に融ける**ので、参考画像の密度にできない。
前乗算なら 1 パスで「体は隠す／暈は足す」を両方書ける ＝ 手前の目が奥の目を隠す。
⚠ 手前 / 奥は**メッシュの並び順**で決まる（座席表は**小さい順**＝ 大きい ＝ 近い目が後）。
⚠ 大きい目のまわりは **相手の大きさに応じて**空ける（`ClearDegFor`）。定数にすると、
視界を埋める目が大きい目に覆いかぶさって「白い染み」になる。

**大きさは 5 段**（`SizeTiers`）— 4〜7° / 9〜15° / **15〜23°（主成分）** / 25〜36° / **40〜54°**。

⚠⚠ **目どうしは重ねない**（2026-08-17・`canon/LEDGER.md` 0077・ユーザー判定
「目が重なってしまってるのは違和感がある」）。候補を大きい順に置き、**楕円どうしの当たり**で
入るものだけ採る（`Separated` / `RadiusToward`）。置けない席は**採らない**（押しのけない —
ずらすと別の目と重なるだけ）。
⚠ **半幅の和で詰めない。** 目は横 2 : 縦 1 なので、上下に並ぶ相手へ 2 倍以上の余白を要求してしまい、
**密度が半分に落ちる**（実測）。相手の方向へ向けた楕円の半径どうしを足す。
⚠ **虹彩は 0.5 で「直径 ＝ 目の高さ」。** 上限 0.52（参考画像は眼裂の高さの 45〜60%）。
0.78 まで上げると瞼を突き抜けて「暗い葉っぱ」になる。
⚠ **視界を埋める目を必ず混ぜる。** 参考画像の目は枠の幅の 18〜29% を占めるのに、初版は 5〜10% しかなく
「遠くの光点」にしか見えなかった（実測。`canon/LEDGER.md` 0076）。
**個体差**は縦横比 0.34〜0.66・上下瞼・目尻の傾き・虹彩の大きさ・開き切る量（半開きで止まる目）・傾き ±38°。
⚠ **quad の縦横比に「目の縦横比」をそのまま入れない**（瞼の開きが二重に掛かって笹の葉になる。
`QuadAspect` が逆算する）。

⚠⚠ **alpha は 0 を返す。** 加算では alpha も足されるので、1 を返すとパススルーが出ている場面
（位置合わせ・導入）で現実に穴を塞ぐ。闇に光を足すだけなら alpha を触る理由が無い。

⚠ **群れは頭の位置にだけ付いて動く**（向きはワールド固定）。ただし**大きい目が視界の外に
1 秒居続けたら群れごと向き直す** — 歩きながら頭を回す体験なので、固定のままだと
真後ろで開いて誰にも見られないまま兆しと凝視の 1.92 秒が終わる。
回すのは「開いているのが大きい目 1 つだけ」の間だけなので、回転は 1 画素も見えない
（残りが開き始めたら二度と回さない ＝ `AnomalyEyesLogic.AnchorLocked`）。

⚠⚠ **縁の色ずれ（RGB 分離）は入れない。** 参考画像 me3 はネオンだが、Codex の指摘どおり
**立体視では輪郭の位置ずれに見えて焦点不快感を起こす**。me3 から採るのは色相ではなく
「巨大な虹彩・黒い内部・多重の輪郭・極端な大小差・視界端で切れる配置」で、
輪郭は**暖色 2 段**（生成り → 橙）で出す。

⚠ **`AnomalyEyesLogic.EyeOpen` とシェーダの `open` は同じ式**。片方だけ直すと、
数えた本数（`eyes` の 2 つ目）と画が黙って食い違う。
⚠ シェーダは実行時 `Shader.Find` なので **Always Included に登録済み**（外すと実機だけ剥がれる）。
⚠ **`ShowTelemetryHost` と `analyze-xp-log.py` は対で直す**（「## 効果の実在」の「### 闇に開く目」節）。

#### 目の視界ジャック — 当日撮った写真をぱぱぱっと（`steps[].eyeJack`）— 2026-08-21

世界観の逐語は `canon/LEDGER.md` 0099。**3 周目 C の目は、4 周目 A に出る大量の人形の目**で、
目が開いたところで**視界を乗っ取り、当日その場で撮った写真（いろんな視点）を短く流す**。

| | |
|---|---|
| 判断 | [`EyeJackLogic`](../../Assets/Scripts/Streaming/EyeJackLogic.cs)（純ロジック・テスト 14 本） |
| 写真の入手 | [`EyeJackPhotoStore`](../../Assets/Scripts/Streaming/EyeJackPhotoStore.cs)（落とす / 貯める / デコード） |
| 実行体 | [`AnomalyEyes`](../../Assets/Scripts/Streaming/AnomalyEyes.cs) の**内側の段**（新しい実行体を作らない） |
| 形 | `Assets/Art/Shaders/Streaming/EyeJack.shader`（queue **4940** / `Blend One Zero`） |
| 卓 | カット詳細の「視界ジャック（当日写真）」＋ **👁 目の写真**パネル |
| 見る | `.\tools\unity.ps1 menu eyejack`（stationary / walker の 2 通りを通しで焼く） |
| 観測 | `ev=jack st=begin/end why=` と `ev=sum` の **`jack=<組めたか>/<写真>/<出した累計>/<乗っ取り中>`** |

**当日の手順は 2 つだけ**（「当日にドタバタしたくないので、堅牢に」）:
写真を `tools/web-compositor/eyejack/` へ入れる → 卓の **📥 取り込む**。
縮小（長辺 1280）・EXIF の向き・減光は**サーバが焼き込む**（`EYEJACK_BRIGHTNESS` = 0.72）。

⚠⚠ **正規化を端末でやらない。** 12MP をそのまま配ると `Texture2D.LoadImage` で 1 枚 50MB 級の
一時確保が走り、**Unity は EXIF を読まないので縦写真が横を向く**。失敗が実機でしか出ない ＝ 当日直せない。

##### 発火は 2 つの縁の早い方（片方だけだと多数派に出ない）

| 縁 | 誰に効くか |
|---|---|
| 全開（`Hold`）＋ `HoldBeatSec`（0.4 秒） | **足を止めた**体験者 |
| **区間の半分に達した**（`EyesCueLogic.HalfReached`） | **歩き続ける**体験者 |

⚠⚠ **全開だけを条件にすると、歩く体験者には一度も出ない。** 3:2 の実測滞在は **7.04 秒**
（`dwell_stats.json` n=98）で、半分（約 3.5 秒）が全開（4.92 秒）より先に来る。
`Wanted = !Finishing || !openDone` なので **`Hold` は 1 フレームしか存在しない**。

⚠ **`HalfReached` は `Finishing` と別**。カットの終わり・区間離脱では立たない（位置だけを見る）。
混ぜると、次の区間へ入った瞬間にジャックが走る。

##### 終わりは 2 通り（0099 の分岐そのもの）

| 終わり方 | 何が起きるか | 観測 |
|---|---|---|
| **写真が尽きた**（止まっている人） | 視界を返し、**目も閉じさせる**（`EyesCueLogic.RequestFinish`） | `why=done` |
| **区間を出た / カットが畳まれた**（歩く人） | **即座に**視界を返す。目は従来どおり流しきる | `why=cut` |
| 中止（ラン開始・卓の中止・位置合わせ） | 1 フレームで消える（`AnomalyEyes.Abort`） | `why=abort` |
| 安全網（`MaxActiveSec` 6 秒） | 強制的に返す。**出たら尺の計算が壊れている** | `why=wd`（解析器が FAIL） |

⚠ **ジャックは流しきらない**（目と違う）。乗っ取った視界を返すのは即座でなければならない —
「動き続けたら今設定してるところで止める」。

##### 尺は総尺を固定して枚数で割る

**総尺 2.4 秒**（`TotalSec`）÷ 枚数。1 枚は **0.2〜0.5 秒**にクランプし、入り切らない写真は
**後ろから落とす**（最大 12 枚）。当日「撮りすぎた」で尺が壊れない。
⚠ **順序はファイル名順**（乱数を使わない ＝ 同じ版は同じ絵）。

##### 描くときの罠

- ⚠ **queue は 4940**（隔離殻 4910 より後・文字 5000 より前）。5000 を超えると URP は
  1 度も描かない（`rules/unity-vr.md`）
- ⚠ **`Blend One Zero` で alpha に 1 を書く**。乗っ取りなので、パススルーが出ていても現実を覗かせない
- ⚠ **実行時 `Shader.Find`** → Always Included に登録済み（外すと Editor では出て実機だけ剥がれる）
- ⚠ **`DriveJack` は目の `Stage==Off` の早期 return より前**。歩く体験者では目が閉じ切った後も
  ジャックが数百 ms 残るので、後ろに置くと**その残りのあいだ面が凍る**
- ⚠ **cover-fit の式は `AnomalyEyes.CoverUv` 1 か所**（プレビューも同じ関数を呼ぶ。複製すると黙ってずれる）

##### 写真が届いたかは heartbeat にしか出ない

卓のライブ状態に **「目の写真 — Quest に N 枚 届いています」**が出る（`eyeJackReady` / `eyeJackListed`）。
⚠⚠ **2 台のうち片方だけ落とせていないのは無音の失敗**。画にも音にも出ず、当日はその機に当たった
体験者だけジャックが出ない。**30 秒ごとに自動で取り直す**が、気づく手段はこの行だけ。

⚠ **写真が 0 枚でも体験は壊れない**（ジャックが出ないだけで、目は従来どおり）。
解析器は WARN で言う（FAIL にすると、写真を配らない検証走行が毎回赤くなる）。

### 左右分割と第 2 の差し替え層（`splitX` / `overlay2CueId`）— 2026-08-15

**画面を縦に割って、左と右へ別のものを同時に出す。** 使うのは**カメラ A だけ**
（3 周目 A と 4 周目 A）。世界観の逐語は `canon/LEDGER.md` 0050。

⚠ **他のカメラでは使わない。** カメラ A は L 字経路の手前で**環境が左右対称**なので、
左半分を反転しても部屋が壊れない。B・C は対称でないので反転した瞬間に別の部屋になる。

カットが持つキー（`ShowStepDef`）:

| キー | 何 |
|---|---|
| `splitX` | 分割位置 0..1（**0 = 割らない**）。境目は環境の縦線（カーテンの合わせ目）へ置く |
| `splitFlip` | 左半分だけ**ライブを左右反転して読む**（`uv.x` を反転するだけ） |
| `splitFreeze` | 左半分だけ**そのカットの頭の画で凍る** |
| `overlay2CueId` | 第 2 の差し替え層へ出す素材の cue id（空 = 使わない） |

- **反転が効くのはライブと凍結だけ。** 差し替え素材（録画・生成画像）は反転しない —
  反転したまま録画を流すと**映像の中の自分が逆走して画面中央の境目へ入り消える**（LEDGER 0050）
- **凍結は「1 枚を捕まえてから量を上げる」**（`CameraSwitchDirector.ApplySplit`）。
  順序が逆だと 1 フレーム前の画で止まる
- **どちらの半分へ出るかを決めるのはマスク。** シェーダに左右の区別は無く、
  1 層目（`_MaskTex`）と 2 層目（`_Mask2Tex`）が別々の側を覆う
- **第 2 層は静止画専用**（載るのは無人プレートと生成画像だけ）。動画・端末内録画を指したカットは
  警告を出して**第 2 層だけ出さない**（カットごと飛ばすと画面の所有者が変わって筋が消える）
- **第 2 層はフェードしない。** 切り替えは乱れが覆う（LEDGER 0050「向きが逆になるのは一瞬なので、
  映像の乱れでごまかそう」）。フェードを掛けると、乱れの下でゆっくり混ざって「すり替わった」に見えない
- **色統計マッチングは 1 層目にしか掛からない**（`_OverlayGain` / `_OverlayOffset`）。
  第 2 層の素材は生成のパイプラインが post を抜いた素の色で入る（`tools/gen-tone.py`）

#### 持続の覆い（`swapHold` / `swapMinX`）— 2026-08-22 / **いまは誰も使っていない**（0119）

⚠⚠ **2026-08-23（`canon/LEDGER.md` 0119）に、`swapHold` を立てているカットが 0 になった。**
ユーザー逐語「2-Cで体験者→人形になるときに、黒いマスクがかかる演出を無くそう。うまく動いていなかった」。
**機構は残してある**（下の説明はそのまま正しい）。戻すなら 2 周目 C の末尾と 3 周目 A の 2 カットに
`swapHold: true`（3-A は `swapMinX: 0.5` も）を書き戻す。
⚠ **`analyze-xp-log.py` の「## 持続の覆い」節は、著作に `swapHold` が 1 つも無ければ出ない**
（起きようのないことを赤くしない）。走行レポートからこの節が消えたのは正常。

以下は**機構の説明**（0102 当時の著作を例にしている）。

**3 周目 A へ入った瞬間から、右半分の体験者は黒に包まれている**（`canon/LEDGER.md` 0102）。
2 周目 C で追いつかれた結果がそのまま続いていて、鏡映しの反対側からリアルタイムの自分が歩いてくる。

| キー | 何 |
|---|---|
| `swapHold` | このカットのあいだ、入れ替わりの覆いを**包み切った状態で保持する**（段は 1 ミリも進まない・波だけ走る） |
| `swapMinX` | 覆いを効かせる**左端**（枠 UV・0 = 制限しない） |

**使う場所は 2 つ**（2026-08-22・0103）: ①3 周目 A の 3 カット（`swapMinX: 0.5`）
②**2 周目 C の末尾**（`plate_C` の untilZoneChange・`swapMinX: 0`）— 追いつきの後、
無人の C に黒く包まれた人型を立たせ、**追いついた文脈を黒が 3 周目 A まで運ぶ**。
区間の切れ目は `ReleaseStepState` が畳んで 3 周目 A が立て直す（隙間はゾーン切替の dip が覆う）。

- 判断は [`SwapMorphLogic.BeginHold`](../../Assets/Scripts/Streaming/SwapMorphLogic.cs)、
  実体は [`SwapMorphFx.BeginHold`](../../Assets/Scripts/Streaming/SwapMorphFx.cs)、
  配線は `TakeRunner` → `CameraSwitchDirector.TakeVeilHoldBegin`
- **保持は次のカットへ持ち越される。** `transition:"swap"` のカットが**ほどける段を飛ばして
  縮む段から**引き継ぐ（`SwapMorphLogic.Begin(..., startCovered: true)`）。
  引き継がないカットへ移ると覆いは畳まれる
- ⚠⚠ **`startCovered` でも `_covered` は false のままにする。** true にすると
  `justCovered` が永久に立たず、**人 → 人形の画面差し替えが 1 度も起きない**
  （覆いの下で何も入れ替わらないまま、凍結の画が最後まで残る）。
  `SwapVeilHoldTests.ContinuingFromHold_StillSwapsTheScreen_OnTheFirstFrame` が固定する
- ⚠⚠ **`swapMinX` は左右分割と併せるときに必ず指す**（3 周目 A なら 3 カットすべて `0.5`）。
  覆いの芯は**合成後の画**と無人プレートの差で引くので、制限しないと
  **左半分の鏡映しの人物・録画の人物まで拾って包む**（同じ人が 2 か所に写っているのが 3 周目 A の作り）。
  `splitX` は覆い切った後のカットで 0 へ戻るので、**分割の値からは導かない**
- ⚠⚠ **覆いを保持している間だけ `_SwapDiffHold` が立つ。** シェーダは既定で
  「覆い切ったら CG の形へ渡す」（`hand`）が、それは**覆い切った縁で画面が無人へ差し替わる**前提。
  持続の覆いはまだ差し替えていないので、渡すと覆いが**いまの体験者の立ち位置**へ出て
  映像の中の人からずれる
- ⚠ **このカットは `cg` を指さない。** 覆いの形の供給元として人の代役（`visitor`）を
  **実体としては 1 画素も描かずに**立てる（`_SwapReal` = 0）。立てられない現場
  （較正未着・`actors[]` に `visitor` が無い）では**覆いが出ないだけ**で、
  鏡映し → 凍結 → 録画 の筋はそのまま通る
- ⚠⚠ **代役を「人形が立っている」と読ませない**（2026-08-22・実機の走行で 2 穴を踏んだ）。
  「人形か」を訊く側は **`ShowCgLayer.DollVisible`**（`IsVisible && 代役でない`）を読む:
  - **音** — `ShowSoundDirector.dollPresent` が `IsVisible` だった頃、2 周目 C の保持中に
    **入れ替わった人形の笑いが鳴り、カメラ C なので増員（swell）まで育った**
    （実測 sndSwap 0 → 2.29 / sndSwell 0 → 0.71・走行 20260822_080731）
  - **入れ替わりの向き** — `TakeRunner.fromDoll` が `IsVisible` だった頃、保持から渡る
    swap カットで「人形 → 人形 ＝ 成立しない」へ倒れ、**引き継ぎが 1 度も走らない**。
    プレビュー（`menu swap`）は TakeRunner を通らないので**絵では出ない** — 走行の
    `[TakeRunner] 入れ替わりは成立しない（人形 前=1 後=1）` 警告が唯一の手掛かり
- ⚠ **`TakeRunner` は保持中のカットで `ShowCgLayer.Hide()` を呼ばない。** 呼ぶと
  `HoldForSwap` が Hide を保留し、**入れ替わりが終わった瞬間にその保留が走って人形が消える**
  （次のカットのプレート＋人形が空になる）。代役を畳む責任は `SwapMorphFx.Cancel` が持つ
- **見る**: `.\tools\unity.ps1 menu swap -Set plate=white,hold=1`
  → 包まれたまま 45 コマ → 引き継ぎ → 縮む → 晴れる の 123 コマ。
  ⚠ **ここでしか判定できないものが 3 つある** — ①保持中に覆いが薄まらないか
  ②`swapMinX` が効いて左半分を包まないか ③引き継ぎの 1 フレーム目に「1 度晴れてまた包まれる」が出ないか
- **観測**は `ev=sum` の **`wrap=<包んだままか>/<覆いの左端>/<立てた回数>/<矩形>/<差分マスク>`** と
  `ev=wrap st=on/off why=swap|drop`。
  ⚠⚠ **`swap=` だけでは判定できない** — 包んでいるあいだ入れ替わりは `Active` だが段は進まないので、
  `swap=` を素朴に読むと「被覆 1.00 のまま止まっている壊れた入れ替わり」に見える。
  判定は `analyze-xp-log.py` の「## 持続の覆い（3 周目 A の入り）」。**C# と Python を対で直す**

#### 素材は先に落としておく（`ShowControlClient.PrefetchTimelineClips`）— 2026-08-22 / 静止画は 08-23

**カットの尺は発火時刻から数える**（`TakeRunnerLogic.StepEndTime`）ので、発火してから
素材を落とすと**その分だけ画に出る時間が減る**。2 周目 C の接近は 0.5〜1.2 秒刻みなので直接効く。

実測（2026-08-22・`logs/capture/20260822_141605` → `_143402`・testassets 24KB）:

| | 落とす | 用意（Prepare） | 合計 | 最短カット 0.5s の |
|---|---|---|---|---|
| 先読みなし | 0〜152ms | 128〜167ms | 最大 **280ms** | **56%** |
| **先読みあり** | **0ms（9/9 キャッシュ）** | 120〜166ms | 最大 **166ms** | **33%** |

- 落とすのは `ShowControlClient.PrefetchTimelineClips`（`PushCueSource` から・無人プレートの
  先読みと同じ立ち位置）。台本が参照する**動画 cue の URL 全部**を起動時に落とす
- ⚠⚠ **消えるのは落とす時間だけ。用意（Prepare）は発火のたびに必ず掛かる**（実測 135ms 前後）。
  `_player.Stop()` → url 代入 → `Prepare()` を毎カット通るためで、**同じファイルでも短絡しない**。
  これを消すには VideoPlayer を 2 本持って次カットを先行 `Prepare` するしかない（未実装）
- ⚠ **本番素材は数 MB** なので、先読みが無ければ落とす時間はテスト素材（24KB・最大 152ms）より伸びる。
  ⭐ 逆に言うと、**先読みが効いていれば素材の大きさは停滞に効かない**
- 観測は **`ev=clip id= dl= prep= tot= cache=`**（`ScreenOverlayController.ClipLatency` →
  `ShowTelemetryHost`）。`ev=step played=1` は「画面を取った」しか言わないので、停滞はここにしか出ない。
  判定は `analyze-xp-log.py` の「## 動画カットの停滞」。**C# と Python を対で直す**
- ⚠ **`ev=clip` の `dl` / `prep` / `tot` はミリ秒、著作の `durSec` は秒。**
  混ぜると割合が 1000 倍になる（実際に 56000% と出した）

##### ⚠⚠ 静止画も先読みの対象（2026-08-23 に足した）

**それまで温めていたのは `plate_*` だけ**で、生成素材（人形・染み・手形）は**発火してから
読み始めていた**。`PlayCueAsync` は**マスクを素材より先に await する**ので、2 本ぶんの
ダウンロードと mip 生成がそのまま間になる。

実測（1 周目 B の手形・541KB の PNG ＋ 21KB のマスク）:

| | 発火 → 画 | 画面が B になってから |
|---|---|---|
| 先読みなし（13:54 の走行） | **260ms** | **200ms**（録画で 10 コマ・65fps） |
| **先読みあり（14:08 の走行）** | **70ms** | **0ms**（画面切替と同じフレーム） |

残る 70ms は**カメラ切替そのもの**（`ev=step` → `ev=screen`）で、素材のロードではない。
同じ走行で 2 周目 A の染みも 210ms → 80ms。

- ⭐ **判定は `ev=step` と `ev=cue st=on` の時刻差**。`ev=clip` は**動画専用**なので静止画の停滞は
  ここに出ない（`ShowTelemetryHost` は毎フレーム回っているので 11ms の分解能がある）
- ⚠ **画で確かめるなら `index.md` の offset を信じない。** 2 回とも実測値が 2 秒以上ずれていた。
  録画の中で「画面が切り替わった瞬間」（1 コマごとの差が跳ねる所）を自分で探して、
  そこを起点に数える
- 落とすのは**逐次 1 本ずつ**。同じ Wi-Fi を 3 台ぶんの MJPEG が使っているので、
  10 本以上を一斉に投げない（体験が始まるまでに終わればよい）
- ⚠ 静止画の cue が増えたら**メモリも増える**（640x480 の mip 付きで 1 枚 1.2MB 前後）。
  現行の台本は 12 本 ＝ 15MB 程度

#### 切替音を鳴らす（`switchSfx`）— 2026-08-22

カメラを動かさない素材カット（`clip` / `plate`）でも「視点が急に切り替わった」として
**カメラ切替の音を 1 発**鳴らす（`rules/sound-design.md` の音源をそのまま使う）。
2 周目 C の接近（`canon/LEDGER.md` 0102）が使う。

- ⚠ **鳴らすのは画を差し替えるのと同じ行から**（`TakeRunner.ApplyStep`）。
  毎フレーム外から状態を見る層（`SoundCueLogic`）に置くと、0.5〜1.2 秒刻みで連打される
  2 周目 C では取りこぼす（連絡の面の打鍵が `TypeAudioCue` を直接叩くのと同じ理屈）
- ⚠ **カメラが実際に変わるカットでは立てない。** `CameraSwitchDirector` が既に鳴らすので二重になる
  （`TakeRunner` 側でも `live` かつカメラが変わる場合は撃たないよう塞いである）

⚠⚠ **カットごとに毎回書き、演出が終わったら必ず畳む。** 分割はカットの中でしか書かれないので、
畳む経路が 1 つでも抜けると**画が割れたまま・左半分が凍ったまま次の体験者へ持ち越される**。
初版（2026-08-15 の最初の 2 コミット）は畳む経路が中止（`CleanupActive`）だけにあり、
**正常終了では必ず残っていた**。いまは `TakeRunner.ReleaseStepState` が持つので
正常終了・中止・ラン開始のすべてを通る。`TakeSplitLayerTests` が固定する。


⚠⚠ **この経路は警告音つきの音で鳴る**（2026-08-22・`canon/LEDGER.md` 0106）。
`sfx_switch_1` の上にユーザー指定の警告音を 240ms だけ -12dB で重ねた
**`sfx_switch_alert`** で、正本は `rules/sound-design.md`。
**ゾーン切替・インサート・Web 固定は素の音のまま。**

- 呼び分けは `CameraSwitchDirector.PlaySwitchSfx` **1 か所**（この旗が通る唯一の経路）。
  素の音で鳴らしたい素材カットが出てきたら、**そこで分けずにカット側へ旗を足す**
- ⚠ 警告つきの音源を掴めなければ**素の切替音へ落ちる**（無音にしない）
- ⚠⚠ **画にも動画にも違いが出ない。** 観測は `ev=sum` の **`swAlert=`**
  （`nc` = 音源が無い ＝ 差し込みも素の音で鳴っている）と、機械の門は
  `SwitchAudioCueTests`。判定は `analyze-xp-log.py` の「## 音（鳴ったか）」。
  **`ShowTelemetryHost` と対で直す**

#### カットの尺「この床の線を横切るまで」（`durKind:"untilLine"` / `steps[].lineId`）— 2026-08-15

**区間の中で位置を待てる唯一の尺。** 3 周目 A の凍結点がこれを使う。

⚠⚠ **演出の開始規則 `at:"line"` では書けない。** 走行中の演出へ別の演出は割り込めず、武装は
`Ready` のまま待つ（`TakeRunnerLogic`）。3 周目 A は「区間へ入った時から画を割っておき、
**線を越えた瞬間に**左半分を凍らせる」なので、前半を演出 1・後半を演出 2 に割ると
**前半が終わるまで後半が始まらず、待っているあいだ画が素へ戻る**。だからカットの側に要る。

- 待つ線は `steps[].lineId`（`layout.lines[].id`）。**演出の `lineId` とは別物** — 演出は「いつ始めるか」、
  カットは「いつ終えるか」
- **線は担当カメラに紐づく。** カットの区間カメラと食い違う線では進まない（`at:"line"` と同じ規約）
- ⚠ **カットが始まる前の横断は数えない。** 横断には猶予（`LineCrossLogic.CrossLatchSec` = 0.6 秒）が
  あるので、これが無いと**区間へ入る途中で踏んだ線**がそのまま効いて 1 カット目が一瞬で飛ぶ。
  3 周目 A の線は区間の入口寄りにあるので実際に踏む
- ⚠ **線が未指定 / 実体が無いときは既定尺で畳む**（線待ちにしない）。実体の無い線を待つと
  watchdog（既定 45 秒）まで画が固まる — 演出側の「空 lineId は発火しない」より沈黙が痛い
- 体験者が越えなければ終わらないので、**上限は watchdog が保証する**（`untilZoneChange` と同じ）。
  加えて `policy:"yield"` なら区間を出た時点で畳まれる
- **卓のシミュレータは秒へ落とさない**（`kind:'unknown'`）。横断判定を二重実装すると必ず実機とずれるので、
  未確定として出す。リボンの幅は仮置きで `≈` が付く
- 判定は [`TakeRunnerLogic.EndStepIfLineCrossed`](../../Assets/Scripts/Streaming/TakeRunnerLogic.cs)、
  テストは `TakeStepUntilLineTests`（7 本）

#### 遷移「入れ替わりのノイズ」（`transition:"swap"`）— 2026-08-19

**全画面の砂嵐で入れ替えるのをやめ、映像の中の体験者だけが水平の線にほどけて入れ替わる。**
逐語は `canon/LEDGER.md` 0089（段の構成）と **0090**（線の作り方）。

⚠⚠ **使うのは 4 周目 A（人形 → 人）だけになった**（2026-08-23・`canon/LEDGER.md` 0119）。
ユーザー逐語「黒いマスクが出るのは、4-Aで人形→体験者に戻るときだけで」。
3 周目 A（人 → 人形）は `transition:"glitch"` ＝ **全画面の乱れ**へ戻した
（黒マスクを入れる前・`show.json` rev 35 の形）。下の「なぜ」は 0089 当時の理屈で、
**人 → 人形の向きについてはユーザーが 0119 で覆した**（実物を見ての判定なので、こちらが正）。

**なぜ**: 全域一様な乱れは「機材が壊れた」で説明が付いてしまうので、その最中に何が入れ替わっても
**入れ替わったことにならない**（`rules/sound-design.md` §4 の「対象に紐づく非一様性だけが、
原因が世界の側にあることを示せる」と同じ理屈）。

| | |
|---|---|
| 進み方 | [`SwapMorphLogic`](../../Assets/Scripts/Streaming/SwapMorphLogic.cs)（純ロジック・テストあり） |
| 実体 | [`SwapMorphFx`](../../Assets/Scripts/Streaming/SwapMorphFx.cs)（`_Swap*` uniform ＋ `_SwapMaskTex`（無人プレート）の**唯一の writer**） |
| 覆いの形 | **芯 ∪ 棒**（2026-08-21・下）。芯 = その画素の人型（**無人プレートとの差分**が正・CG へ縮退）/ 棒 = 帯ごとの黒い横棒（端が波） |
| 無人プレート | `plate_<カメラid>` cue（`TakeSchema.SwapPlateCueId`）。`ShowControlClient` が**先読み**して配線。無ければ CG の形 ＋ 警告 |
| 見る | `.\tools\unity.ps1 menu swap` → `py -3.11 tools/make-preview-video.py Assets/Screenshots/swap swap`。**帯の判定は `-Set plate=white`**（現場の映像は右半分が黒く、覆いと同化して読めない） |
| 観測 | `ev=swap st=begin/end`（`mask=`=差分で覆えたか / `vis=`=人の代役を出せたか）／ `ev=sum` の **`swap=<走っているか>/<被覆>/<背丈 m>/<矩形>/<累計>/<差分マスク>`** |

段は 3 つ（既定 2.6 秒・`transitionMs` で調整）。配分は 32% / 42% / 26%。

1. **ほどける** — 行き先の方向から前線が進み、通った行が水平に引き伸ばされて線になる
2. **縮む / 育つ** — 線の塊のまま背丈が**等比**で変わる。線形にすると前半で一気に小さくなって
   「縮んだ」ではなく「落ちた」に見える
3. **晴れる** — 人形になる向きは実体が出る／人へ戻る向きは線が引いて下の映像が出る

##### 覆いの作り方（`ScreenComposite.shader`・2026-08-21 に「芯 ∪ 棒」へ作り直し）

⚠⚠⚠ **覆いは黒。中身は 1 画素も見せない**（2026-08-19・`canon/LEDGER.md` **0091**・ユーザー逐語
「人の体が、全部黒いので覆われ、自分の体は直接は見られなくなり、黒いもののが自分の腕などに
合わせて動くことで分かるようになる」）。

**満たすべきことは 3 つ** — ①体は全部黒く覆われる ②体は直接見られない
③**黒いものが腕に合わせて動くことで自分だと分かる**（手掛かりは中身ではなく**動く形**）。

⚠⚠⚠ **覆い = 芯 ∪ 棒。「隠す」と「波を読ませる」を別の層に分ける**（2026-08-21・ユーザー報告
「体験者の姿が見えてしまい…黒いマスクに体験者が隠れ切れていない」を受けた作り直し）。
0098 の「帯を描く」版は棒の幾何（中心 sin ＋ 半長 sin）だけで人を隠そうとしていて、
①棒の中心が泳いだ分だけ半身が覆いから外れる ②行の在否を**行の中心 1 点**でしか見ないので
脚の段（中心 = 股の空白）に棒が 1 本も出ない、の 2 つで**覆い切りの 1 フレームに人が見えていた**。

| 層 | 何 | 仕事 |
|---|---|---|
| **芯** | その画素の人型。**無人プレートとの差分**（`SwapDiffAt` / `SwapDiffSil`）が正、プレートが無ければ CG のシルエットへ縮退 | **隠し切る**。棒がどう泳いでも、人が居る画素は芯が必ず黒くする |
| **棒** | 帯（`_SwapSmear.x` = 48 行）ごとに 1 本。**行の中心線で引いた CG 人型を、波の量ぶん横へ引き伸ばしたもの**（片側 3 点 ＋ 自分の列のサンプリング） | **端の波を読ませる**。端 = 体の縁 ＋ `SwapWaveExt` の連続関数なので、うねりが縦に続く |

- **波（`SwapWaveExt`）**: shift（左右の端へ逆符号 ＝ 帯が横へずれて見える）と breath（同符号 ＝
  伸縮）の 2 成分。主の周期は **12 帯前後で 1 周**（0098 続き「8〜20 帯で 1 周」）。
  ⚠ **振幅 × 周波数（隣の行との差）を帯 1 本の高さ以下に**保つ。超えると波ではなく角ブロックの階段
- ⚠ **棒の範囲をタップの min/max で推定しない**（2026-08-21 に 1 度やって捨てた）。
  タップ間隔の量子化（0.18）が波の振幅より大きく、端が**角ブロックの階段**にしか見えなかった
- 細い部位（脚）では点の間が抜けて棒が途切れるが、**筆のかすれ**として読める
  （参考画像の線も途切れている）。隠し切りは芯が保証しているので穴にはならない
- ⚠⚠⚠ **差分を引くのは人 → 人形のほどける段だけ**（2026-08-21・`canon/LEDGER.md` **0100**）。
  差分は「映像の中の当人」を拾うものなので、**映像に当人が居る間**しか出番が無い。
  人形 → 人は**差し替えが育ち切るまで来ない**（下）ので、覆っている間ずっと画は素材＝当人は居ない
- ⚠⚠ **人形 → 人で「実寸の当人の形」を足さない。** 一度そうしていた（覆い切った縁で差し替えて
  いたので、はみ出す当人を隠す必要があった）が、ユーザー赤入れ「人形→人間で、人型の黒いのが
  見えてしまう」。**はみ出す問題は差し替えの側で解く**（下）
- ⚠ 早い棄却の箱は「いまの人型」と「マスクの枠」の両方で取る（人 → 人形の縮む段で、
  実寸の当人が縮んだ枠の箱から出るため）
- ⚠ **CG の形は `_CgStrength` に依らず引く**（覆いの形の供給元であって、画に混ぜるかとは別）。
  Editor プレビューはほどけている間 cgVisible=false で回すので、`aCg` 経由だけだと芯が消える
- ⚠⚠ **人 → 人形では、ほどけている最中の CG（人の代役）を画に混ぜない**（`body` は向きで分岐）。
  当人は映像の中に居るので、CG まで描くと**同じ場所に二重の人が立つ**（実機だけの症状 —
  プレビューはこの間 CG を切っているので絵に出ない）。人形 → 人は従来どおり
  「ほどけが来ていない所は実体（人形）のまま残す」（消すと 1 フレーム目に人形が丸ごと消える）

##### 黒い波 — かっこよく・アートチックに・インタラクティブに（2026-08-22）

設計書は [reports/2026-08-21_swap-wave-design.html](../../reports/2026-08-21_swap-wave-design.html)
（**美学の柱 3 本と設計 8 本**。実装はこの節が正本）。それまでの波は
対称な sin の重ね合わせ ＝ **振り子**で、連続に読めるが穏やかで「ゆらゆらした切り絵」だった。
足りなかったのは **緩急・方向・対比・キメ**の 4 つ。

⚠⚠ **柱を外れる案は却下する。** 数値をいくら詰めても、柱を外すと別の作品になる。

1. **縦は機械、横は生き物** — 帯の格子（48 行の等間隔）は装置の走査線なので**縦は絶対に揺らさない**。
   有機的な動きは全部横（端の波・ほつれ・走り）。帯を回す・行の高さを揺らす・縦に曲げるは全部却下
2. **装置は正直、呪いは応答する** — 体験者に応えるのは**黒だけ**。映像・post・カメラ・
   乱れの育ち（`GlitchEscalation`）に体の入力を 1 ビットも混ぜない
3. **黒だけで組む**（0091）— 明るい線・色・白を出さない。かっこよさは**形と時間だけ**で作る

| 層 | 何 | 置き場 |
|---|---|---|
| **A 走る波** | 山（crest）が体を駆け抜ける。**sin ではなく非対称パルス**（前縁は体高の 5% で立ち上がり、後ろへ exp で尾を引く）。**行き先の方向**へ走り、ほどける段は前線より 1.5 倍速い**先触れ** | `SwapWaveLogic` ＋ `SwapCrest` |
| **B 細いほつれ** | 帯の縦中央だけが棒の先から伸びる針。長さは**裾の重い分布**（参考画像の実測 中央値 13px / 最大 257px ＝ `pow(u, 4.3)`）。⚠ **時間で振らず、山に引きずり出される** | `SwapNeedleFig` |
| **C 稀に跳ぶ帯** | hash 上位 10% の行だけ段あたり 1〜2 回、片側へ一瞬突き出す（立ち上がり 0.15 / 減衰 0.30 秒） | `SwapSpike` |
| **D キメの一拍** | `justSwapScreen`（画面が実際に差し替わる 1 フレーム）から 0.25 秒、最大の山が全身を走り抜ける（振幅・針 2 倍）。既存の乱れパルスと**同じ縁**なので画・乱れ・音が揃う | `SwapWaveLogic.NotifyBeat` |
| **E 全身のエネルギー** | 歩く速さが波の全体の強さになる（半減期 0.8 秒・静止でも振幅 0.55 は残す） | `SwapEnergyLogic` |
| **F 局所のホットスポット** | 手が速く動いた所だけ毛羽立つ（半減期 0.3 秒・結合 0.30） | `SwapHot` |
| **G 追従** | 差分マスクが本人を毎フレーム追う（**新規実装ゼロ**。0091 の「腕に合わせて動く」はここで成立済み） | — |
| **H 余韻** | 晴れる段は**最後の山が走り抜けたその後ろから**帯が消え、数本だけ残ってから引く。「消える」ではなく「離れていく」 | `SwapClearFade` |

- **時計と段の値は C#**（`SwapWaveLogic` / `SwapEnergyLogic` の const）、**形の寸法はシェーダ**
  （`SwapCrest` / `SwapNeedleFig` / `SwapSpike` / `SwapClearFade` の const）。**2 か所に書かない**
- **uniform は `_SwapWave`（山 y / 振幅 / 針 / 跳び）と `_SwapWave2`（経過秒 / キメ / 晴れ / エネルギー倍率）**
  ＋ 手 2 点（`_SwapHotA` / `_SwapHotB`）。**唯一の writer は `SwapMorphFx`**
- ⚠⚠ **`_SwapWave2.w`（エネルギー倍率）の既定は 1。** 0 を書くと**基本の波まで消える**
  （走っていない間・畳んだ後は必ず 1 へ戻す）
- ⚠⚠ **どの層も棒の端を伸ばす方向にしか働かない。** だから隠し切り（芯 ∪ 棒）は 1 ビットも
  弱くならない。⚠ 晴れ方（H）だけは黒を引くので、**効かせるのは緩み（1 − knot）の分だけ** —
  晴れる段の 1 フレーム目は人型を人形へ差し替える縁なので、そこで 1 行でも抜けると差し替えが見える
- ⚠ **テクスチャの読みは 1 つも増えていない**（全部 ALU・7 タップのまま）。
  隔離殻で踏んだ「全画面で毎画素の重い数式 ＝ 90fps → 39fps」の型には当たらない
- ⚠ **進行を入力にしない**（E / F が変えてよいのは見た目だけ）。総尺・段の進み・差し替えの縁は
  1 ミリも動かない（`SwapWaveLogicTests` が固定する）。動かすと
  「歩かない体験者で演出が終わらない」を作る
- ⚠ **報告ボタンを読まない**（0050。読んでよいのは `untilMark` の 1 か所だけ）
- ⚠ **乱数を使わない。** hash（行）と体の入力だけ ＝ 同じ入力なら同じ絵
- ⚠ **手の位置は実寸の人の figure 空間（`pM` ＝ `_SwapRect0` 側）で渡す。** 縮む人型の空間で
  渡すと、人型が縮むほどホットスポットが体から離れていく（映像の中の腕はそこに在り続ける）

**見る**（層は 1 つずつ入れて焼く。まとめると「どれが効いたか」が消える）:

```powershell
.\tools\unity.ps1 menu swap -Set plate=white,layers=none   # 前の版
.\tools\unity.ps1 menu swap -Set plate=white,layers=A      # A だけ（B / C / D / E / F / H も同じ）
```
```bash
py -3.11 tools/swap-motion-audit.py --label none
py -3.11 tools/swap-motion-audit.py --label all --baseline none
```

⚠⚠ **機械の門 3 つは「前の版との差」でしか判定できない**（`tools/swap-motion-audit.py` の冒頭）。
前の版にも山と**同じ向きへ動くもの**が 2 つある（覆いの前線と、縮む / 育つ人型の縁）ので、
「一方向へ動いたか」だけでは山を足したかどうかが分からない。プレビューは決定論なので、
**同じコマ番号の差 ＝ 足した層そのもの**になる。

⚠ **観測は `ev=sum` の `swap=` の 7〜9 番目（山の振幅 / エネルギー / 手の点）と
`ev=swap st=end` の `crest=` / `e=` / `hot=`**。⚠⚠ **山の振幅は「画へ書いた値」** —
走っているのに 0 なら段だけが進んでいる（`analyze-xp-log.py` が FAIL を出す）。
**エネルギーと手の点は立ち止まっている体験者では 0 が正しい**ので判定に使わない。

##### 無人プレートの実機配線（0095 の宿題・2026-08-21 に実装）

- **`plate_<カメラid>` cue**（`TakeSchema.SwapPlateCueId`）が差分の相手。`ShowControlClient` が
  cue 定義の更新（`PushCueSource`）のたびに**先読み**して `SwapMorphFx.SetPlateProvider` へ配る。
  Begin してから読むと 2.6 秒の覆いにダウンロードが間に合わない
- どのカメラのプレートかは **swap 開始時に画面へ出ているカメラ**（`registry.ActiveIndex`。
  swap はカメラを動かさずに始まり、差し替えは覆い切った縁なので）
- ⚠⚠ **持続の覆い（`swapHold`）は違う — カットが宣言したカメラで選ぶ**（2026-08-23）。
  こちらは**区間へ入った最初のフレームで立つ**ので、`ActiveIndex` を読むと
  **まだ前の区間のカメラが出ている**（画面の切替は数十 ms 遅れる）。
  実測（3 周目 A・ユーザー走行と自動走行の両方）:

      t=195.96  wrap st=on  minx=0.50    ← ここでプレートを決める
      t=196.01  screen cam=0             ← 画面がカメラ A になるのは 50ms 後

  カメラ A の映像を **plate_C（別の部屋）** と比べるので、差分が箱いっぱいで飽和し
  **右半分が真っ黒**になる。⚠ **`mask=1` は「掴めたか」しか言わない**ので、
  間違ったプレートでも 1 が出る（ログからは正常に見える）。
  ⇒ `CameraSwitchDirector.TakeVeilHoldBegin` は `cgCamera`（＝ `step.camera`）を
  プレートにも使う。**入れ替わり（`TakeSwapBegin`）の側は従来どおり**
- 掴めなければ **CG の形へ縮退 ＋ 警告 1 回**（入れ替わりは成立する）。観測は `ev=swap` の
  `mask=`（`analyze-xp-log.py` が WARN を出す）
- しきい値と mip は `SwapMorphLogic.MaskDiffLo/Hi/Lod` **1 か所**（実機と Editor プレビューが同じ値を引く）
- ⚠ 差分は照明の変化・人の影も拾いうる（しきい値 lo=0.06 の下は捨てる）。プレートは**同じ照明**で
  撮り直すのが正（卓の 📷 無人プレート）

⚠ **暗い背景の前では黒いシルエットが読めない。** 人形 → 人（4 周目 A）は人型が右の暗い所へ
育つので、育つ段の後半は形が背景へ溶ける。**黒く覆う以上これは避けられない**ので、
そこを見せたいなら立ち位置か背景の側の判定が要る。

⚠ **直していない 1 つ**（絵で確かめた上での判断）: 人形が小さすぎて「人形になった」が読めない
（0.40m ＝ 画面で 100 画素・寸法は世界観の判定なので触っていない）。
⚠ かつてここに書いていた「人形 → 人の育つ段に実寸の当人の形の黒が立つ」は
**2026-08-21 に解決した**（0100 で差し替えの縁を育ち切った縁へ移し、差分を引く区間が
人 → 人形のほどける段だけになった）。

⚠⚠ **走査線は画面の座標で刻む。figure 空間で刻んではいけない。** 人型は縮む段で 1.65m → 0.40m に
なるので、figure に紐づけると行の高さも 1/4 になり、**縮み切る前に線が 1 画素を割って消える**。
走査線は装置が持つものなので、画面に固定されているのが物理的にも正しい。

⚠ **人体検知（MediaPipe 等）で本人のマスクを取るのが目標だったが、固定視点なので背景差分で足りている**
（0095）。残る弱点は「照明が変わるとプレートが古くなる」だけで、それは撮り直しで解ける。

- ⚠⚠⚠ **画面の差し替えは「覆いが相手を隠し切れるようになった瞬間」**（`justSwapScreen`。
  素材も左右分割も第 2 層もそこで一斉に入れ替える）。早いと体験者が覆いの下ではなく画の中で
  消え、遅いと覆いの中で背景が動く。**縁は向きで違う**（2026-08-21・`canon/LEDGER.md` 0100）:
  - **人 → 人形**: ほどけ切った縁（`justCovered`）。覆いは**実寸の当人の形**で立っている
  - **人形 → 人**: **育ち切った縁**（`justSettling`）。差し替えると映像に実寸の当人が現れるが、
    覆いは人形の大きさから育つ途中で**まだ小さい**。早く差し替えると、はみ出した当人を隠すために
    実寸の黒い人型を立てることになり、**それがそのまま見えてしまう**
  - ⇒ 体験者に見えるのは「**黒い波が人の大きさへ育つ → 画が変わる → 晴れて人が現れる**」
  - ⚠ `justCovered` を直接読まない（`SwapMorphFx` / `ShowSwapPreview` とも `justSwapScreen`）。
    姿を替える縁（人形 ⇄ 人の差し替え）は従来どおり覆いの下＝ `justCovered` / `justSettling`
- ⚠⚠ **向きはデータで指定しない。** 「このカットが人形を出すか（`cg` が空でないか）」と
  「直前に人形が出ていたか」から導く。前後で同じなら入れ替わりではないので、
  **乱れ遷移（既定尺）へ倒して理由をログに出す**
- ⚠⚠ **背丈が動いている間の形は、どちらの向きでも人**（`actors[]` の **`visitor`**）。
  市松人形は頭が大きいので、人の背丈へ引き伸ばすと**人ではなく頭の巨大な塊**になる
  （2026-08-19 に絵で確かめて作り直した）。姿を替えるのは**線に覆われている間だけ** —
  人形 → 人はほどけ切った縁、人 → 人形は縮み切った縁。`visitor` が無ければ人形を引き伸ばして代用し、
  警告を 1 回出す（**入れ替わりごと止めない**）
- ⚠ **ほどけ切った瞬間に全画面の乱れを 0.55 × 0.20 秒だけ**走らせる。これは入れ替わりを隠すためではなく、
  **人型の外で同時に起きる差し替え**を覆うためのもの。要らないなら `SwapMorphLogic.VeilLevel` を 0 に
- ⚠ **帯は太い筆の横棒**（`_SwapSmear.x` = 枠の縦に対する行数・既定 **48** ＝ 実機で 15〜20 画素。
  0098 続き 2 のユーザー参考画像）。320 行の走査線には戻さない。y/z/w はもう読んでいない
- ⚠ **棒になった人型は光を遮らない**（影と接地影を 0 へ落とす）。人 → 人形は実体が出る晴れる段で戻り、
  人形 → 人は戻らない（戻る先はライブ映像で、本物の影が最初から写っている）
- ⚠ **人形が居なければ出せない**（カメラ姿勢が未著作・位置合わせが未完了・`actors[]` に無い）。
  そのときは**乱れ遷移へ倒す** — 黙って何も起きないと、体験者は画面が固まったと感じる
- ⚠ **卓はこの遷移の絵を出さない**（卓は意図的に CG 人形を描かない）。選べて保存できるだけ

#### 人形の呼びかけを鳴らす（`dollCall`）— 2026-08-22

**画の差し替えと同時に、もらった声「あーそぼー」を 1 回鳴らす**（`canon/LEDGER.md` 0109）。
立てているのは **2 周目 C の接近の最後のカット**（`pov_4` ＝ 追いつき）だけ。

- ⚠⚠ **音（1.60 秒）はカット（1.4 秒）より長いが、画は待たない。** はみ出した 0.2 秒は
  次のカットへ被る（ユーザー指定「映像はこの音を無視してそのまま先に進んで ok」）。
  カットの尺・演出の進行・終了判定に 1 ビットも影響しない
- ⚠ **鳴らすのは画を差し替えるのと同じ行から**（`TakeRunner.ApplyStep`）。毎フレーム状態を見る
  `SoundCueLogic` に置くと、0.8〜1.4 秒刻みで進む 2 周目 C では頭を取りこぼす
- ⚠ **`switchSfx` と併せて立てられる**（実際そうなっている）。層が違う音なので潰れない —
  切替音は装置の音、こちらは部屋で鳴っている声
- **音源は 1 本**（`Resources/Sound/sfx_doll_call`）。**どれを鳴らすかはデータで指せない** —
  2 本目の声が実在したときに初めて文字列の口を開ける（typo が沈黙する面を、値の無いうちに開けない）
- ⚠ 掴めなければ**警告を出してカットは無言で進む**（画は 1 ビットも変わらない）
- ⚠⚠ **画にも動画にも違いが出ない。** 観測は `ev=sfx id=DollCall`、機械の門は
  `DollCallAudioTests` と卓の `timeline-model.test.mjs`。判定は `analyze-xp-log.py` の
  「## 音（鳴ったか）」。音の設計そのものは `rules/sound-design.md` が正本

#### カットの尺「体験者が報告するまで」（`durKind:"untilMark"`）— 2026-08-15

4 周目 A の締めだけが使う（`canon/LEDGER.md` 0050「そこで体験者が報告することで、
画像生成人形が消えて実体の人形になり」）。左 X / Y の 2 秒長押しで進む。

⚠⚠ **報告を進行に使ってよいのはここだけ。** 規約は「報告は体験の進行に 1 ビットも使わない・
押さなくても同じように進む」（`rules/show-design.md`）で、それは**驚かせる仕掛けの引き金にしない**
という意味。締めの 1 か所はユーザーが明示的に許可している（0050 4 回目
「報告ボタンは演出の引き金にはしない。驚かせるのは2周目Cの全画面にしよう」＋「4周目のAで…
そこで体験者が報告することで」）。**他の尺のカットは報告では 1 ミリも動かない**
（`MarkPress_DoesNotAffectOtherStepKinds` が固定する）。

- 経路は `OvrControllerBridge` → `ShowControlClient.RecordVisitorMark` →
  `TimelineDirector.NotifyVisitorMark` → `TakeRunner.NotifyVisitorMark`
- ⚠ **カットが始まってからの報告だけを数える。** 前の区間で押した 1 回が持ち越されて
  締めのカットを素通りするのを防ぐ（線待ちと同じ理由）
- ⚠ **押さなくても必ず終わる。** watchdog（`maxDurationSec`・現行の台本は 45 秒）が畳むので、
  押せなかった体験者が置き去りになることはない
- **卓のシミュレータは秒へ落とさない**（卓に体験者の報告は無い）。`kind:'unknown'`

#### 報告で異常が消える（演出の `dismissible`）— 2026-08-17

ユーザー逐語（`canon/LEDGER.md` 0050）:

> 左半分に人形が大量にいて、それを異変だと思って**報告したらそれらが消え**、実在する人形が現れるとか。
>
> 報告ボタンは**演出の引き金にはしない**。驚かせるのは2周目Cの全画面にしよう。
>
> **推したら乱れたのちに元に戻って**終幕で。

4 周目 A だけの作り込み（`durKind:"untilMark"`）を**仕組みへ一般化した**もの。
演出（Take）に `dismissible: bool` を足し、立っていれば**報告で演出ごと畳まれ、
映像の乱れとともに現実（ライブ映像）へ戻る**。

| | |
|---|---|
| 判断 | [`TakeRunnerLogic.NotifyMarkPressed`](../../Assets/Scripts/Streaming/TakeRunnerLogic.cs) → `DismissDecision` |
| 配線・遷移 | [`TakeRunner.EndTake`](../../Assets/Scripts/Streaming/TakeRunner.cs) → `DismissReturn` |
| 卓 | 演出インスペクタの「**報告で消える**」（`ribbon.js` / `timeline-model.js`） |
| 観測 | `ev=take st=end why=mark` と `ev=sum` の **`disN`**（消えた回数） |
| テスト | `TakeVisitorDismissTests`（14 本） |

- ⚠⚠ **既定は false（消えない）。** JsonUtility は欠落キーを false で埋めるので、既存の
  show.json・端末キャッシュ・焼き込みは**挙動が 1 ビットも変わらない**。否定形（`noDismiss` 等）で
  持つと古いデータが全部「消せる」側へ倒れ、**3 周目の録画（作品の核）が押しボタン 1 つで飛ぶ**
- ⚠⚠ **旗を立てても消せない演出が 2 つある**（データで上書きできない構造ガード）:
  - **現カットが `untilMark`** — 報告はそのカットが消費する（**排他**。1 回の押下に 2 つの意味を持たせない）。
    ここで演出ごと畳むと、4 周目 A の締めで著作された「現実へ戻る 3 秒」が丸ごと消える
  - **`run.outro.afterTakeId` が指す演出** — `EndingCueLogic` は「指した演出が走らなくなった」で撃つので、
    畳むと**終幕が早撃ちされる**。`TakeRunner.SetTakes` が旗ごと落として警告を出す（卓も立てさせない）
- ⚠⚠ **報告で現れてよいのは現実だけ。** だから畳むときは
  **`chainNext` を立てない**（次の演出へ画面を渡すと現実が 1 フレームも出ない）し、
  **その瞬間に画面を取れる状態だったもの**（Ready の武装・持ち越し）は捨てる
  （捨てたら `DropReason.VisitorDismissed` で必ず報告する）。
  **まだ時刻が来ていない武装は残す** — 後から別の異常として出るのが自然で、消すと著作が黙って減る
- ⚠ **消した演出は戻らない**（`once` はそのまま）。戻ると報告が無意味になり、「作者が操作している」と読まれる
- ⚠ **押さなくても必ず終わる**（watchdog は従来どおり効く）。同じフレームで報告と watchdog が
  揃ったら**報告を理由にする**（体験者の行為の方が意味を持つ）
- **消え方は乱れ**（`TakeSchema.DismissGlitchMs` = 420ms・`GlitchTransitionLevel` = 0.85）。
  既存の `InsertReturn(…, glitch:true)` に乗るだけで、新しい状態機械は作っていない。
  乱れの音（`sfx_glitch`）は `GlitchFx.Level` が 0.15 を超えると自動で鳴る
  - ⚠ **黒の dip で返さない** —「カメラが切り替わった」の語彙になり、押した行為と画の変化が結ばれない。
    クロスフェードは「作者が消した」、砂嵐は「信号が切れた」に読まれる
  - ⚠⚠ **この乱れは「起きた回数」に数えない**（`GlitchFx.SetSustain(level, count:false)`）。
    数えると乱れの育ち方（`canon/LEDGER.md` 0055「最後にかけて粗く」）が**押した回数の関数**になり、
    同じ台本が人によって別の画と音になる ＝ 走行の再現性が消える
- ⚠⚠ **3 周目（`rec` ＝ 1 周目の録画）には旗を立てていない。** 紙が「気になるものが見えたら押してください」
  と言っている以上ほぼ全員が押すので、立てると**作品の核をほぼ全員が見ずに終わる**。
  ⭐ **これは制約ではなく設定になった**（2026-08-17・`canon/LEDGER.md` 0082）—
  解除を実行しているのはエージェントで、3 周目 A〜C は侵食されて効かない（0070）。
  4 周目 A で復帰し、そこで初めて通る
- **旗が立っているのは 1 周目 B と 2 周目の 3 本**（卓 rev 883・2026-08-17）。
  ⚠⚠ **旗と尺は対で決める。** 押し切るには「気づく 0.3 ＋ 押し始め 0.3 ＋ 長押し 1.0」＝
  最低 1.6 秒が要る。2 周目は 2.4 / 2.2 / **1.0** 秒で、**旗を立てても押し切る前に終わっていた**
  → **3.5 / 3.5 / 2.5 秒**へ。C（ジャンプスケア）だけ短いのは、
  全画面で出た**瞬間**が驚きなので居座らせすぎないため
- **演出が終わった理由が観測に出るようになった**（`ev=take st=end why=done|wd|yield|mark`）。
  無いと「効かなかった」と「効きすぎた」が尺の長短でしか区別できない。
  判定は `analyze-xp-log.py` の「## 報告で異常が消えたか」。**C# と Python を対で直す**
- **演出が終わった理由が観測に出るようになった**（`ev=take st=end why=done|wd|yield|mark`）。
  無いと「効かなかった」と「効きすぎた」が尺の長短でしか区別できない。
  判定は `analyze-xp-log.py` の「## 報告で異常が消えたか」。**C# と Python を対で直す**

#### 引き返したら、途中で切れた演出を頭から出し直す — 2026-08-17

ユーザー指定（原文）:

> 体験者が演出の途中などで後ろのカメラに戻ってしまったときに、戻る前の途中で途切れてしまった演出をどうするか。
> 異変を報告済み→再演出は無し／異変を報告していない→最初から再演出
> 1-C→2-A→1-C→2-A→2-B と戻ったようなとき、1-C は、すでに通り過ぎた演出は戻ってきてももう出さないように
> したうえで、引き返したときの演出は、報告済みでない限りはもう一度出す

**`once` が数えるのは「始めた回数」ではなく「決着した回数」**（[`TakeRunnerLogic.Outcome`](../../Assets/Scripts/Streaming/TakeRunnerLogic.cs)）。
旧実装は `StartTake` の時点で発火済みが立っていたので、1 カット目の途中で区間を出た演出は二度と出なかった。

| 終わり方 | 決着するか |
|---|---|
| 完走した（`Completed`） | **決着**（「すでに通り過ぎた演出は戻ってきてももう出さない」） |
| 体験者が報告した | **決着**（`dismissible` で消えたかどうかに関わらず） |
| watchdog が打ち切った | **決着**（同じ所でもう一度止まるだけ） |
| 人が止めた（卓の介入・`AbortActive`） | **決着**（人の判断を歩きで覆さない） |
| **体験者が区間を移って打ち切られた（`policy:"yield"`）かつ未報告** | **未決着** → その区間へ戻れば**頭から**出し直す |

- **再演の経路を別に作っていない。** 区間キーの周が引き返しで戻る（上の「周回数は 2 つある」）ので、
  同じ区間へ戻れば同じ演出が普通に武装される。未決着はその武装を通すだけ
- ⚠⚠ **報告として数えるのは走行中の押下だけ。** 終わった後の押下に猶予を作らない —
  連絡の面が出す答えは `TakeRunnerLogic.NotifyMarkPressed` の**戻り値**
  （＝ `ShowControlClient.LastMarkResolved`）で、走行中でなければ false ＝
  そこでは「異常は検出されませんでした」と表示済み。内部だけ「報告済み」にすると
  **同じ 1 回の押下について画と機械が別のことを言う**（`analyze-xp-log.py` が
  `ev=mark res=` と `ev=comms id=` の食い違いを FAIL にしているのと同じ不整合を、こちらから作ることになる）
- ⚠ **出し直せるのは `TakeRunnerLogic.MaxReplays`（2 回）まで。** ゾーン確定は dwell 0.5 秒なので、
  境界で往復されると 1 カット目だけが 1 秒おきに繰り返され、演出ではなく機械の反復に見える。
  使い切ったら決着させる
- ⚠⚠ **走行中の演出は武装しない**（`ArmEnterTakes`）。`policy:"hold"` は画面を持ったまま次の区間へ
  行けるので、そこから引き返して自分の区間へ戻ると**自分自身を武装して二重に始まる**。
  旧実装は発火済みが開始時点で立っていたので、この穴は構造的に無かった
- **`policy:"hold"` の演出は区間を移っても切れない**ので、この機構が効くのは実質 **`yield` の演出**
  （現行の台本では 3 周目の 3 本）と、**開始前に区間を出て捨てられた演出**
- 観測は `ev=sum` の **`reN`**（出し直した回数）。⚠ **引き返したこと（`lap` ≠ `plap`）とは別物** —
  引き返していないのに 0 なのは正常で、引き返したのに 0 なら（報告済みでない限り）機構が効いていない。
  判定は `analyze-xp-log.py` の「### 引き返しと再演」。**C# と Python を対で直す**
- 契約は [`TakeReplayOnBacktrackTests`](../../Assets/Tests/Streaming/TakeReplayOnBacktrackTests.cs)（10 本）と
  [`LapCounterTests`](../../Assets/Tests/Tracking/LapCounterTests.cs) の引き返し 5 本が固定する

⚠ **卓のシミュレータには体験者の報告が無い**ので、常に「報告していない」側 ＝ 必ず再演する側を見せる
（`scenario-engine.js` の `OUTCOME`）。実機では報告済みなら再演しない。

#### 卓の編集面（2026-08-15 に追加）

カット詳細に次を出す。**演出の開始規則の `at:"line"` とは別の欄**なので混ぜないこと。

| 欄 | 何 |
|---|---|
| 尺 → この床の線を横切るまで | `durKind:"untilLine"` ＋ 線のセレクト（`steps[].lineId`） |
| 尺 → 体験者が報告するまで | `durKind:"untilMark"` |
| **報告で消える**（演出の欄・2026-08-17） | `dismissible`（終幕の合図が指す演出では立てられない） |
| 左右分割 | `splitX`（0 = 割らない / 0.5 = 中央） |
| 左半分を左右反転 | `splitFlip` |
| 左半分を凍らせる | `splitFreeze` |
| 第 2 の素材 | `overlay2CueId`（**静止画の cue だけ**を出す。動画は載らない） |
| 遷移 → 入れ替わり（2026-08-19） | `transition:"swap"`。**向きは指定しない**（このカットが人形を出すかで決まる）。⚠ 卓の絵には出ない |
| 覆いを包んだまま保つ（2026-08-22） | `swapHold`。次のカットの「入れ替わり」が縮む段から引き継ぐ |
| 覆いの左端（2026-08-22） | `swapMinX`。**左右分割と併せるときは分割位置と同じ値**を入れる |
| 切替音を鳴らす（2026-08-22） | `switchSfx`。カメラを動かさない素材カットで「視点が切り替わった」音を出す |
| 人形の呼びかけを鳴らす（2026-08-22） | `dollCall`。もらった声「あーそぼー」を 1 回。**音がカットより長くても画は待たない** |

- 線のセレクトは**この区間のカメラが担当するものだけ**（別担当の線は実機で数えられない）
- 線は「この線を横切るまで」を選んでいる間だけ実体を持つ（他の尺へ切り替えると空へ倒す。
  幽霊の線待ちを作らないため — `hasPlacement` と同じ流儀）

**観測**は `ev=sum` の **`spl`（画に出た分割位置）/ `ovl2`（第 2 層の合成の重み）**。
どちらも「カットが指した」ではなく**画に出た**の側 — 分割は書く先（`CameraFeelFx` の material）を
掴めていなければ 1 画素も割れず、第 2 層は素材を非同期で読むのでマスクの読込失敗で黙って消える。
判定は `analyze-xp-log.py` の「### 左右分割と第 2 の差し替え層」節が、
**出たか**と**演出の後に残っていないか**の両方を見る。
⚠ **`ShowTelemetryHost` と `analyze-xp-log.py` は対で直す**（片方だけだと沈黙して食い違う）。

**⚠ Quest 実機未検証**（2026-08-15。EditMode 1269/1269・node 395/395）。
分割の境目が環境の縦線に乗るか、反転した自分が「もう 1 人」に見えるかは実機でしか判定できない。

### 演出専用カメラ D と CG 人形の実配線（2026-07-27）

計画 [2026-07-27_cg-actor-hand-tracking.md](../plans/2026-07-27_cg-actor-hand-tracking.md)（設計・不変条件の正本）。

- **カメラ D は 4 本目の普通のカメラ**。`Assets/Settings/Cameras/Phone04.asset`（cameraId=D）+ `StreamingLogic.prefab` の
  `sources[]` 4 本目。show.json の `cameras[3]` に `role:"fx"` を入れると、ゾーン塗りパレット（卓）・スタッフ巡回・
  ゾーン自動切替から外れ、**演出のカットからだけ映る**。配信アプリは v0.8.0 で cameraId に **D** を追加済み。
  **常時受信する**（演出のときだけ繋ぐ、はしない — 張り直しで 1〜2 秒の黒が出るため）
- **CG 人形**（`steps[].cg` / `cgMode`）は [`ShowCgLayer`](../../Assets/Scripts/Streaming/Cg/ShowCgLayer.cs) が
  実カメラ姿勢（`cameras[].pose`）の双子の仮想カメラで **ShowCg レイヤだけ**を RT へ描き、`_CgTex`（ポスト FX の前）で合成する。
  腕は体験者のハンドトラッキング（[`ShowActorRig`](../../Assets/Scripts/Streaming/Cg/ShowActorRig.cs) +
  [`TwoBoneIk`](../../Assets/Scripts/Streaming/Cg/TwoBoneIk.cs) + [`ActorArmLogic`](../../Assets/Scripts/Streaming/Cg/ActorArmLogic.cs)）。
  入力は Assembly-CSharp の [`OvrHandTrackingBridge`](../../Assets/Scripts/OvrBridge/OvrHandTrackingBridge.cs) が
  `ShowBodyInput`（頭 + 左右手のワールド位置のみ）で push する（**Streaming asmdef は OVR を参照しない**の維持）
- **course→world は `CourseFrame` 経由**。`ShowControlClient.CourseToWorldProvider` / `CourseYawProvider`
  （ZoneLayoutApplier が注入）を使う。**親子付けでは駄目**（CourseFrame は transform を動かさない）
- **決めたこと**: 手の**回転は使わない**（位置だけ。OVR の手 basis の罠を避ける）／ 全身アニメは持たない
  （T ポーズから腕だけ下ろす＝マネキン。AnimatorController 不要でどの humanoid FBX でも動く）／
  カメラ目線が欲しければ `fixed` + `fixedYawDeg`
- **フェイルソフト**: 姿勢未著作のカメラでは出さない / actor 未定義・プレハブ欠落は代用の箱 /
  手が取れない間は idle（体側）へ 0.35s で合流し**人形は消えない**

#### 腕・体の向き・立ち位置の作り直し（2026-08-05）

計画と実測値は [2026-08-05_actor-follow-and-arms.md](../plans/2026-08-05_actor-follow-and-arms.md)。
検証は `Diagnostics/Preview Actor Motion`（体験者の動きを合成して**数値と絵**を出す。Play 不要）。

- **腕は「肩から手」を腕の長さの比で写す**（[`ActorArmLogic`](../../Assets/Scripts/Streaming/Cg/ActorArmLogic.cs)）。
  旧実装は「頭から手」を**全高比**で写しており、人形と体験者の体の比率が同じことを仮定していた。
  実測でプレハブの頭−肩は身長比 **人体 9.0% / 市松人形 2.8%（3.2 倍差）**で、仮定が成り立たない。
  人体比率の Remy でも**腕を下ろした姿勢で目標が腕の届く範囲を 26% 超え**、常時 IK がクランプして
  腕が棒のように伸び切っていた（肩基準にして 5%＝人間も伸び切る範囲へ）。
  体験者の肩は頭から推定する（**目から 0.15 × 身長 下・0.104 × 身長 横 / 腕長 0.29 × 身長**。
  いずれも Remy のボーン実測）
- **人形の向きは「体験者の体の向き」**。頭の向きを半減期 0.35s で追い、**首のねじれ 50°** で引かれる
  （`ActorArmLogic.SmoothYawDeg`）。遅れだけだと「首を振った」と「体ごと回った」を区別できず、
  90° 振り向いた場面で体が置いていかれて**手が背中側へ回る**（実測）。
  ⚠ **腕の写像の正規化にも同じ体の向きを使う**（頭の向きで写すと、手が動いていないのに腕が振り回される）
- **立ち位置も腕・向きと同じ時刻を読む**（[`CourseTrack`](../../Assets/Scripts/Streaming/Cg/CourseTrack.cs)）。
  旧実装は位置だけ「いま」で、向きと腕は 0.15 秒前だった。実測で**歩行中 12cm・停止時 0cm**
#### ⚠⚠ 2026-08-15 に人形と腕の動かし方が変わった（`canon/LEDGER.md` 0051）

**下の可動域 45° は旧 `Ichimatsu`（正面写真を前後へ押し出したもの）の値。** いまの人形
`Ichimatsu2`（生成した 3D にリグを付けたもの・[tools/doll-rig/](../../tools/doll-rig/README.md)）
では 3 つ変わっている。

- **腕は上下にしか振らない**（[`ActorArmLogic.LimitToVerticalSwing`](../../Assets/Scripts/Streaming/Cg/ActorArmLogic.cs)）。
  目標を人形の前額面へ倒してから可動域へ収める。⚠ **倒すだけでは駄目** — 前へ真っ直ぐ差し出した手は
  投影がほぼ消えるので、残ったわずかな左右成分が正規化で**腕の長さまで拡大され、腕が横へ跳ね上がる**
  （実測）。投影が短いあいだは腕を下ろした向きへ滑らかに寄せる
- **可動域は 90°（腕が水平）**（`ShowActorRig.MaxSwingDeg`）。袖の重みを腕の軸からの距離で
  付けてあるので、旧人形のように布が引き伸ばされない
- **左は素手が取れなければコントローラの姿勢で代える**（[`OvrHandTrackingBridge`](../../Assets/Scripts/OvrBridge/OvrHandTrackingBridge.cs)）。
  体験者は左を持って歩くので、握った手はハンドトラッキングされず**人形の左腕が体側で止まっていた**。
  ⚠ **右は代えない** — 右を持つのはスタッフで、代えるとスタッフが手を動かすたびに人形が動く

- **人形の可動域を絞る**（[`ActorArmLogic.LimitToDollRange`](../../Assets/Scripts/Streaming/Cg/ActorArmLogic.cs)）。
  ハンドトラッキングへ 100% 追従させると、**着物の袖のように体へ付いた布が引き伸ばされる**。
  市松人形で角度を振って実測すると、腕を下ろした向きから **30° まで自然 / 45° で袖の先から手が
  突き出て破綻**した（`sweep` シナリオ）。
  - **腕を下ろした向きからの振れは 45° まで**（上限の 60% までは 1:1、そこから指数で飽和）。
    単純に切ると、体験者が動かしているのに人形が途中で止まってカクつく
  - **肩から手首は腕長の 0.85 まで**（常に肘を少し曲げておく）。角度と距離の両方を抑えると、
    45° でも袖の形が保たれる（距離を制限しない 45° は破綻する）
  - 実物の市松人形も肩の球体関節がわずかに動くだけで、真横に上げたり万歳したりはしない。
    **可動域を絞るのは妥協ではなく人形として正しい**
  - ⚠ 判定は**本番で見える大きさ**でする（`*_far.png`）。寄りでは本番より大きく見えて判断を誤る
    — 遠景では袖から覗く手は数 px にしかならない
  - **袖そのものが腕に付いてくるようにもした**（2026-08-05・`tools/doll-model/assemble.py`）。
    袖のスキニングが「腕の高さからの落差」だけで減衰していて、**前後へ膨らんだ頂点が体に残り、
    腕を動かすと手が袖を突き抜けていた**。腕の軸からの距離（上下と前後の両方）× 袖口への近さ
    へ変えたら、袖が腕と一緒に持ち上がるようになった。
    ⚠ 脇まで腕へ付けると**袖が体から剥がれて裂け目**ができる（実測）— 横方向の重みで体に残す。
    人形を作り直したら `Build Show Actor Prefab` は不要（プレハブは FBX を参照している）
  - 値は `ShowActorRig` の **const**（SerializeField にすると既存プレハブで 0 に読まれて腕が固まる）。
    人形ごとに変えたくなったら show.json の actor 定義へ出す。**Remy のような体にフィットした服の
    人形でも同じ制限が掛かる**（破綻はしないが動きは小さくなる）
- ⚠ **身長は「ボーンの最高点」だけでは測れない**。市松人形は Head が頭の中ほどまでしか無く、
  実寸を 3 割小さく見積もって **2.27m の巨人**になっていた（目標 1.6m）。メッシュ上端がボーンより
  25% 以上高ければメッシュ側を採る。**Remy は 11%**（髪と服の膨らみ）なのでボーンのまま。
  実寸は `ShowActorPrefabSizeTests` が固定する（**変わると人形の大きさが黙って変わる**）
- **Editor メニュー**: `Setup/Build Show Actor Prefab`（humanoid FBX → `Resources/ShowActors/<名前>.prefab`。
  Humanoid でも Generic でも可 — Generic は手のボーン名から親を 2 つ遡って肘・肩を取る）/
  `Diagnostics/Preview Show Actor`（Play せず 4 ポーズ × 2 角度を PNG 化）/
  **`Diagnostics/Preview Actor Motion`**（体験者の動きを時系列で合成し、腕の到達率・伸び率・
  手の前後・追従の遅れを `report.md` に、全身と腕の寄りを連番 PNG に出す。**見る人形は
  show.json の `actors[0]` から決まる** — 現場と違う人形を測っても意味が無いため）
- **人形は実物の色を持てる**（2026-08-03〜）: `ShowActor.shader` の `_BaseMap` に元モデルのアルベドが乗る。
  ビルダーが FBX のテクスチャを拾って**人形ごとのマテリアル**（`ShowActor_<名前>.mat`）を作る。
  テクスチャを持たないモデルは従来どおり共有 `ShowActor.mat` で灰色のマネキンになる（見た目不変）。
  実物をスキャンした人形（市松人形など）を入れる前提で足したもので、
  これが無いと**赤い着物を撮ってきても灰色で映る**。計画は
  [2026-08-03_doll-scan-to-showactor.md](../plans/2026-08-03_doll-scan-to-showactor.md)
  - ⚠ **テクスチャは 1 枚へまとめる**。人形は 1 マテリアルへ潰す設計で、これは影の付け方
    （`ShowCgLayer.AttachShadowMaterial` が `sharedMaterials` の末尾へ 1 枚足す）と対になっている。
    サブメッシュが複数あると**影が最後の 1 つにしか出ない**。2 枚以上あればビルダーが警告する
  - ⚠ **シェーダの陰影は wrap lighting で作る**ので、テクスチャに撮影時の影が焼き込まれていると二重になる。
    スキャン撮影で影を消す理由がこれ
  - **法線マップと鏡面反射も持てる**（2026-08-03〜）: `_BumpMap` / `_BumpScale` / `_Spec` / `_Gloss`。
    ビルダーがアルベドと同じ場所の `<...>_normal` を自動で拾い、Texture Type も Normal map へ直す。
    **鏡面は「光を動かすとハイライトが動く」ために要る** — リムは視線だけで決まるので光に反応せず、
    それ自体が「CG である」合図になっていた。法線を強くしすぎるとテクスチャの模様
    （目・眉・帯の柄）まで凹凸になって彫刻に見えるので控えめに
  - 実物の市松人形から作った人形が `Resources/ShowActors/Ichimatsu`（全高 0.40m）。
    作り方・罠は [tools/doll-model/README.md](../../tools/doll-model/README.md)。
    **フォトグラメトリでも AI 生成でもなく、正面写真のシルエットを前後へ押し出している**
    （髪は原理的に解けず、白磁の面も対応点が立たない）
- **`Diagnostics/Preview Show Actor` は Project ウィンドウで選んだ人形を見る**（2026-08-03〜）。
  `Resources/ShowActors/` 配下のプレハブを選んでから実行する。未選択なら従来どおり Remy
- **卓の著作面**: カメラ列の役割セレクトと 📐 姿勢欄・フロアマップの **📐 カメラ姿勢**モード（印ドラッグ＝位置 /
  矢印の先ドラッグ＝向き・扇＝画角）・**🎭 CG 人形**パネル（actors[]）・カットの「CG 人形」ドロップダウン
- **⚠ 実機未検証**（2026-07-27。EditMode 695/695・卓はブラウザ実操作・人形は Editor 静止画で確認済み）

### 合成の作り直し — 像空間・較正・影・部屋プロキシ（2026-07-27）

設計の正本は [2026-07-27_cg-compositing-rebuild.md](../plans/2026-07-27_cg-compositing-rebuild.md)。**Step 0（土台の是正 + スキーマ骨格）まで実装済み**。

人形が映像に合わなかった真因は姿勢の精度**以前**で、像空間の対応付けが 3 箇所ずれていた
（設計批評とコード監査が独立に同一結論へ到達）。**姿勢を完璧に測っても旧実装では合わない**：

| 何を | 旧 | 新 |
|---|---|---|
| 合成 UV | CG は生の `screenUv`（枠いっぱい） | ライブと**同じ contain-fit 枠**（`_CgScale` = [`MjpegScreen.ContainScale`](../../Assets/Scripts/Streaming/MjpegScreen.cs)）。4:3 の映像が 16:9 の枠へ letterbox される分（`_LiveScale.x=0.75`）だけ人形が水平 1.33 倍外側にずれていた |
| RT | スクリーン枠の 16:9・1280×720 | **ソース映像の実寸**（640×480）。映像より鮮明な CG は「貼り付けた絵」に見えるのでわざと同じ粗さへ落とす（`renderHeightPx` は上限） |
| 画角 | `fovDeg` を Unity は**垂直**、卓は**水平**の扇で描画（約 25% 相違） | **`hfovDeg`（水平）が正**。Unity が映像アスペクトから垂直へ変換（`ShowCgLayer.HorizontalToVerticalFovDeg`）。旧キーは読まない（卓が 4:3 前提で 1 回だけ移行して落とす） |
| 合成式 | `lerp(bg, cg.rgb, cg.a)`（straight alpha） | `bg*(1-a*s) + rgb*s`（**premultiplied over**）。影を「rgb=0 / a=濃さ」の断片として同じ RT に描けば、この式が自動的に乗算になる。不透明な人形に対しては旧式と同値 |
| 未登録時 | course→world が identity へ落ち、人形が全く違う場所に立つ（警告も無し） | **出さない**（`ShowControlClient.CourseRegisteredProvider` を `ZoneLayoutApplier` が注入）。登録が入れば次フレームから出る |
| 光の向き | `_LightDir` がワールド固定 → トラッキング原点の向き次第で部屋に対する陰影が変わる | **course 相対**（`ShowCgLayer.CourseLightDirToWorld` が MaterialPropertyBlock で毎フレーム供給。将来は `layout.room.light` が正） |

**新スキーマ（キーは切ってあり、中身を埋めるのは後段）**:

- **`cameras[].calib`** — 較正の解（卓が実映像上の床点から平面ホモグラフィで解く。位置・向き・roll・内部行列 px・k1）。
  **`pose` と同居させない**（pose = 人がフロアマップでドラッグする概算 / calib = 実測。統合すると卓のドラッグが解を壊す）。
  **レンズ・解像度に従属する**ので `srcW/srcH/lensId` で照合し、不一致なら較正無効（`ShowCameraCalibDef.MatchesSource`）。
  `refs[]` に対応点を残して再解決を 30 秒にする
- **`layout.room`** — 部屋の 3D プロキシ（壁・箱・床・光源）。**1 幾何で 3 用途**（CG のオクルーダ / 影の落ち先 / 較正参照）。
  別々に持つと必ずズレる。Blender FBX や Meta Scene API を使わないのは「卓が読めない＝シミュレータが必ず嘘をつく」から
- **`steps[].placement`** — 人形の立ち位置は**カットが持つ**。actor 側に持たせていた旧設計では
  「同じ人形を別のカットで別の場所に立たせる」ができなかった。未指定なら actor の `fixedX/Z/YawDeg` へフォールバック（後方互換）

**卓は CG 人形を描かない**（実描画の正は Unity。glTF + スキニング + IK + 影を素の WebGL2 で二重実装すると一致を検証する手段が無い）。
卓が持つのは投影計算のミラーと輪郭プロキシだけ。**シミュレータは「人形が出るカット」を必ず文字で明示する**
（`show-sim.js` の `resolveScreen`。黙って落とすのは「著作した演出が黙って消える」と同型の罪）。

#### ⚠ 自動求解は廃止した（2026-08-04）— いまは**手で合わせる**

以下の「解く」系の記述（ホモグラフィ・LM・leave-one-out・点の質・`lenses[]` の信頼判定）は
**読み取り専用の記録**。卓の UI は [`align-ui.js`](../../tools/web-compositor/align-ui.js) に置き換わり、
判断は [`align-model.js`](../../tools/web-compositor/align-model.js)（node テストあり）。
設計は [2026-08-04_manual-camera-align.md](../plans/2026-08-04_manual-camera-align.md)。

**なぜ**: 実測で **4 台とも一度も解けていなかった**（2026-07-29 に UI を全面立て直した後も 6 日間）。
失敗は数値ではなく**入力**にあった — 候補点が「既定値の壁」「仮想の格子」から作られていて、
**現場の床にその目印が実在しない**。実在しない点をどれだけ丁寧に打っても解は歪む。
現場で生きているのは人が置いた `pose`（4 台とも `hfovDeg: 77.4` は人が打った値）。

**いま**: 実映像へ部屋のワイヤーと人形を重ねて、人が見ながらカメラを動かす。
書き先は従来どおり `cameras[].calib`（**Unity 側は 1 行も変えていない**）。手で置いた印として
`method:"manual"` を立て、`rmsPx` / `accuracyM` / `refs` は**書かない**
（自動で解いたときの指標を手で置いた値に付けると嘘になる）。

**掴めるのは外部 6 自由度だけ**（x/y/z・yaw/pitch/roll）。**画角と歪みはドラッグ対象にしない** —
「画角を広げて近づける」と「狭めて遠ざける」は画の上でほぼ同じで、実測では画角を未知にすると
位置が 27cm・固定すれば 2cm。手ドラッグは曖昧さを消さず**残差の表示だけ**を消すので、
掴めるようにした瞬間 solver より悪くなる。レンズ単位で一度だけ決める。

**動かすのはカメラであって部屋ではない。** `layout.room` / `layout.floor` は **4 台 × 3 用途**
（オクルーダ / 影の落ち先 / 較正参照）の共有資産で、1 台に合わせて頂点をずらすと他のカメラの
合成が黙って狂い、人形の course 座標がカメラごとに食い違う。卓の UI は常にそれを言う。

**「卓は CG 人形を描かない」を部分的に覆した。** 卓が描くのは**幾何だけ**
（ワイヤー・人形の輪郭・足元・平面投影の影）。**陰影・素材・法線・鏡面・色温度は描かない** —
ここを移植すると「卓では正しく見えて実機が違う」を再生産する。
**合成の見た目の正は Unity**（`Preview Show Composite`）で、卓の画面に常時その旨を出す。
⚠ 人形の寸法は必ず渡すこと（`actor-proxy.js` の既定は**身長 1.6m の人間**用で、
そのまま 0.40m の人形に使うと足元の円が人形の背丈と同じ幅になる。実際そうなっていた）。

#### 端末の傾きを取り込む — 手で合わせるのは 4 自由度だけになった（2026-08-05）

配信アプリ（**streamer v0.9.0〜**）が `/info` で**配信画像の重力に対する姿勢**を配る。
卓の 🎯 カメラを合わせる の **📱 端末の傾き**パネルが取り込む。設計は
[2026-08-05_device-tilt-align.md](../plans/2026-08-05_device-tilt-align.md)。

| キー | 中身 |
|---|---|
| `tiltPitchDeg` | 上下（**+ が上向き**・`calib.pitchDeg` と同じ規約） |
| `tiltRollDeg` | 傾き（`calib.rollDeg` と同じ規約） |
| `tiltState` | `ok` / `moving`（動いている）/ `steep`（真下すぎて傾きが定義できない）/ `unknown`（センサ無し・未読）。**ok のときだけ取り込ませる** |

- **来るのは上下と傾きだけ。左右の向き（方位）は入っていない。** 方位は磁気コンパスでしか
  出せないが、室内・三脚・電子機器の近くで ±10〜30° ずれ、しかも course 空間の X/Z 軸と
  磁北の対応が未知。**磁気は送っていないし、送るようにもしない**（「参考」で出すと必ず誰かが使う）
- **上下と傾きが方位と独立に決まる理由**: 姿勢は Unity の Euler（ZXY 順・`R = Ry(yaw)·Rx(-pitch)·Rz(roll)`）で、
  yaw はワールド Y 軸まわりの最外回転だから**どのベクトルの Y 成分も変えない**。よって
  `pitch = asin(fwd·U)` / `roll = atan2(right·U, up·U)`（U = 上向き）が yaw を含まない。
  ⚠ 前提は **course 空間の Y 軸が重力の上向き**であること（位置合わせリチュアルが動かすのは
  XZ と yaw と床の高さだけなので成立している）。ここが崩れたらこの機能ごと嘘になる
- **計算は配信側でやり切る**（[`TiltMath`](https://github.com/Roiril/fixed-cam-streamer) / `TiltFilter`）。
  受信側に端末座標系を解釈させると、センサの取り付け向き・`targetRotation`・正立化が絡んだ
  二重実装になって必ず食い違う。配るのは「**いま配信されている絵**の向き」だけ
- **両側の式が繋がっていることは機械で固定してある**: streamer の `TiltMathTest`（置き方 → 期待角）と
  卓の `align-model.test.mjs`（`cameraBasis` から同じ式で pitch/roll を復元・yaw を振っても不変）。
  **片方だけ直すと沈黙して食い違う**
- **取り込みは明示操作**。ライブ値へ自動追従はしない（カメラを触っていないのに保存値が変わると、
  作業者が何をして絵が変わったのか追えなくなる）。ずれていれば「センサとのずれ 3.2°」と出す
- **取り込むと「この傾きを保つ」が入る**。以後 **X / Z 軸まわりの回転だけ**を止める
  （床を回す＝ Y 軸と平行移動は姿勢を変えないので触らない＝合わせ作業の本体は残る）。
  Y 軸回転は数学的に上下・傾きを変えないが、Euler へ戻す往復で誤差が乗るので値を焼き直している
- **`/info` は卓のサーバ経由で引く**（`GET /caminfo?host=&port=&auth=`・2s タイムアウト）。
  ブラウザ直だと端末ごとに CORS も到達性も違い、「届かない」と「その端末は /info を持たない」を
  区別できない。iPhone の IP Camera Lite は `/info` 自体が無いので、パネルは
  「この端末は傾きを送っていません」と出して従来どおり手で合わせる
- **Unity は 1 行も変えていない。** `StreamMetadata` に tilt のフィールドは無く、JsonUtility が
  未知キーを無視する。姿勢は従来どおり `cameras[].calib` から読む

#### 較正（cameras[].calib）の実装と、現場運用が「そうでなければならない」理由

数学は [`tools/web-compositor/calib.js`](../../tools/web-compositor/calib.js)（純関数・node テスト 26 本）。
床の既知点を映像上でクリックした対応から **平面ホモグラフィ**（Zhang）で姿勢・焦点距離・歪みを解く。
Unity 側の適用は [`ShowCgLayer.ApplyCameraCalib`](../../Assets/Scripts/Streaming/Cg/ShowCgLayer.cs)
（内部行列は `Camera.projectionMatrix` へ直接。physical camera を経由すると符号・軸の取り違えが必ず起きる）。

**卓と実機の投影が一致することは機械的に固定してある** — 同一の入力に対する期待画素を
`calib.test.mjs`（JS）と `CgProjectionTests.Projection_MatchesPinholeFormula`（C#・実 Camera 経由）の
両方にハードコードして突き合わせる。**片方だけ直すと沈黙して食い違う**ので必ず両方直すこと。

実測で分かった、運用に直結する 3 つの事実（`calib.test.mjs` がすべて固定している）:

| 事実 | 数値 | 運用への帰結 |
|---|---|---|
| **焦点距離を既知にすると精度が一桁変わる** | 人のクリック誤差 1.5px に対し、f も推定すると位置 **27cm** ずれる。f 固定なら **2cm** | **内部（画角）は一度だけ丁寧に / 外部（置き場所）は現場で毎回**。2 回目以降は前回の `fxPx` を固定して打ち直す（30 秒で復旧） |
| **歪みを無視すると高さが 4 割狂う** | 広角（k1=0.18）を歪み無しとして解くと、カメラ高さ 1.5m → 0.91m | k1 を「後で」にはできない。人形が床にめり込む／浮く原因になる |
| **f 未知のまま k1 を解くと縮退する** | 5 点だと歪みを f に吸わせた偽解（f 430→300）に落ちる | f 未知なら点 6 個以上（`MIN_POINTS_FOR_K1`）。f 固定なら 4 点でも歪みごと解ける |

**高さの点（壁の縦エッジ）を入れる**（2026-07-29・ユーザー報告「床だけじゃ精度に不安が残る／うまくいかない」）。
床の点だけだと**平面 1 枚しか見ていない**ので、「画角を広げて近づける解」と「狭めて遠ざける解」が
床の上ではほとんど同じ絵になる（k1 も同じ効き方なので巻き込まれる）。壁の角と**その真上 h[m]** を
対にして打つと平面の外から拘束が入り、画角・高さ・傾き・歪みがまとめて決まる。

| 打ち方 | 誤差（クリック 1.5px・カメラ位置） |
|---|---|
| 床 7 点だけ | 7.4cm（高さ 4.5cm / f 7.3px / k1 0.10 ずれ） |
| ＋ 縦エッジ 3 本 | **1.2cm**（高さ 0.6cm / f 1.0px / k1 0.01） |
| クリック誤差ゼロ・縦エッジ 3 本 | 真値へ収束（0.0cm） |

- 操作は「床の角を打つ → ▲ 高さの点を足す → **同じ角の真上**をクリック」。高さは 1 つの入力
  （壁の高さ）で、**検証ワイヤーの壁の高さと同じ値**を使う（別々に持つと打った上端と重ねた線が食い違う）
- 実装は [`refineCalib`](../../tools/web-compositor/calib.js)（Levenberg–Marquardt・数値ヤコビアン）。
  **順投影は `projectPoint` をそのまま使う**＝ Unity / シェーダと同じ式。ここで別式を書くと
  「卓では合うのに実機で合わない」という最悪の破れ方をする
- 初期解はこれまでどおり床のホモグラフィ。高さの点があるときだけ全点で精密化し、
  **同じ点集合で測った rms が改善した時だけ**採用する（点が増えたぶんの悪化を退化と誤判定しない）
- `calib.refs` は高さの点も `y` つきで残す（復元しないと次に解いた時に拘束が黙って消える）
- 高さの点が 2 本以上あれば、床の点が 6 個未満でも k1 を推定してよい（`MIN_RAISED_FOR_K1`）
- **LM の残差は理想（歪み無し）正規化座標で測る**。画素で測ると「理想 → 歪んだ」向きの変換が要り、
  除算モデルのこの向きは半径が大きいと解が無い（null）。探索の途中で予測が画面外へ飛ぶと
  残差が定義できず勾配が死ぬ。観測を歪み無しへ戻す向きは常に定義できるので、そちらで比べる
- **カメラ後方へ回った点の残差を定数にしない**（2026-07-29 実害）。定数だと数値微分が 0 になり、
  「後ろに回っている」を直す勾配が消えて、その点を置き去りにしたまま収束する
  （高さの点 3 つが投影不能のまま `誤差 3464px` と表示された）。pz に比例させて前へ押し戻す
- 解いた後に投影できない点が残るなら、**平均に混ぜず番号で名指しして失敗にする**
  （`badPoints`）。誤差の大きい点も上位 3 つを名指しし、映像では打った点と解の言う位置を赤で結ぶ
- **`layout.wall` が既定値（1m × 1m の L）のままだと、いくら丁寧に打っても合わない**。
  「壁の外角」の座標が実物と違うため。較正パネルは開いた時にこれを警告する（`wallLooksDefault`）

- レンズ歪みは**除算モデル** `r_u = r_d / (1 + k1 r_d²)`。順・逆の両方に解析解があるので、
  卓の順投影（ワイヤー重畳）と `ScreenComposite` の逆変換が**厳密に一致する**。
  多項式モデルだと互いに近似の逆になり、画面端で食い違って**較正の検証そのものが成立しない**
- 4 点ちょうどのときは **どの 3 点も一直線でないこと**が要る（DLT が正方系になるため）。卓は理由を文で返す
- 較正は **レンズ・解像度に従属**する。`ShowCgLayer.ResolveCalib` が受信フレームの実寸と `/info` の
  `lensId` を毎フレーム照合し、食い違えば概算 pose へ落として警告を出す（黙って狂わせない）

**打つ点は地図から選ぶ**（2026-07-29）。候補は `layout.calibPoints`（作者が置いた印）→ `layout.regPoints` →
部屋の角（`layout.wall` / `layout.floor` / **`layout.room` の壁の端・箱の床接地角**）→ タイルの角、の union で、
較正パネルの中の**部屋を上から見た略図**（[`floor-sketch.js`](../../tools/web-compositor/floor-sketch.js)）に
すべて描かれる。地図の点をクリックすると「打つ点」が切り替わり、［印を置く］にすると
`layout.calibPoints`（`{x, z, label}`・**ラベル必須**）を足せる（タイルの角と部屋の角へ吸着）。
- **`calibPoints` は `regPoints` と別の集合**。HMD の位置合わせ点はタッチ順という意味を持ち上限 5 点で、
  手が届く必要がある。較正点は**そのカメラに写る**必要があり、画角も解くなら 6 点以上要る。
  候補としては union で並べるが、**どちらかへコピーはしない**（2 コピーは必ずずれる）
- **地図で決めた座標は、現場の床に実物の目印が無ければ使えない**（メジャーで測らない限り同定できず、
  誤差として表面化しないまま解を歪める）。UI は「床の上・実物がある点だけ」を明示し、印にラベルを要求する
- **点の質は解く前に出す**（`pointQuality`）: 点数と必要数（画角固定なら 4 / 推定なら 6）・一直線・
  床の広がり・**画面の広がり**（床で散っていても画面の隅に固まれば解は暴れる）

**卓の較正 UI** = [`calib.js`](../../tools/web-compositor/calib.js)（描画とイベントだけ）+
[`calib-session.js`](../../tools/web-compositor/calib-session.js)（**判断**・DOM 非依存・
`calib-session.test.mjs` が固定）。カメラ列の［🎯 姿勢を合わせる］から入る。
ライブ映像を 1 枚静止させ、候補点（印 → 位置合わせ点 → 部屋の角 → タイルの角 → 手入力）を選んでクリックし、
✨ 解く → **床格子・壁・通過ラインのワイヤーを実映像に重ねて目で検証** → 💾 保存。
前回の `refs`（正規化 uv）を復元するので「ずれた点だけ直して解き直す」が回る。**解像度・レンズが食い違う較正では
ワイヤーを引かず理由を出す**（Unity の `MatchesSource` と同判定 — ずれの原因が「解」か「前提」か分からなくなるため）。

#### 較正 UI の立て直し（2026-07-29）— 判断の分離・レンズ・精度・出典

計画は [2026-07-29_calib-ui-rebuild.md](../plans/2026-07-29_calib-ui-rebuild.md)。**この道具は一度も現場で
成功していなかった**（show.json の 4 台とも `hasCalib=false`・`layout.wall` は既定値のまま）。
原因は点の打ち方ではなく、その手前の 3 つ。

**1. 状態遷移がテストを通っていなかった。** 開く / フレームを取り直す は同じ判断をしなければならないのに
別々の式で書かれ、片方だけが正しかった。判断を `calib-session.js` へ出して固定した（実バグ 5 件を同時に修正）:

| 直したもの | 壊れ方 |
|---|---|
| 🔄 で解像度が変わっても固定画角を捨てなかった | 別解像度の焦点距離で解いた嘘の解が「画角固定済み」として保存され、実機で人形が別の場所に立つ |
| 点の質判定が高さの点を床の点と混ぜた | 高さ点は基準点と (x,z) が同一 → **必ず**「3 点が一直線」と誤警告し、正しく打っているのに直せと言う |
| 映像が来る前に開くと `refs` が復元されず 🔄 でも戻らなかった | LIVE 前に開くのは現場で普通に起きる。全点を打ち直すことになる |
| 「壁の高さ」の後変更が既存点に届かなかった | 拘束は 1.80 のまま・検証線だけ 2.40 になる。**高さは点ごとに持ち、打った後も直せる**ようにした（壁と箱で高さが違ってよい） |
| 卓が lensId を照合しなかった | Unity は照合して較正を捨てる → 卓は「較正済み」なのに実機だけ黙って概算姿勢 |

ほかに、解に失敗した直後は古いワイヤーを描かない（「解けません」の横で線が合って見えた）／未保存の解を
確認なしで閉じない／復元した点に**名前を戻す**（`resolvePointLabels`・番号だけでは現場で直せない）／
🗑 全部消す（置き直し用）／↩ 元に戻す（保存の 1 世代）を足した。

**2. 実測していない幾何を実測のように扱っていた。** 既定値の壁（1m×1m の L）・定数のタイル格子
（0.15m × 12×12）から作った点は、**現場の床にその目印が無い**。座標は綺麗な数字なので打てば解は出るが、
実物と違う場所を指すので合わない。候補点と検証線に**出典**（`SOURCE_MEASURED` / `SOURCE_ASSUMED`）を持たせ、

- 候補は既定で実測点のみ（実測点が 4 個未満なら推測も出すが `⚠未測定` を付け、パネル上部で理由を言う）
- **実測点が足りないときは、較正パネルの中で床の寸法を直せる**（幅 × 奥行 → 保存）。
  メジャーで 30 秒で測れて、入れた瞬間に**四隅が打てる実測点になる**。ここを別の面（🧱 部屋）
  へ送ると、作業者はパネルを閉じて往復することになる。入れた値が既定と同じ 1.8×1.8 のときは
  「実際に測った値かどうかは判定できません」と正直に言う
- 検証線の推測部分は**点線**で描く（消すと部屋の見当が付かず、実線だと較正のせいだと誤診する）
- `wireSegments(layout, {includeAssumed:false})` で実測だけに絞れる。`hasMeasuredGeometry` で有無を判定

**3. レンズ（内部パラメータ）という概念が無かった。** 画角を固定する値の供給源が「**このカメラの前回の解**」
しか無く、4 点でフリーに解いた f が翌日「正確な内部パラメータ」として再利用される。しかも
**f を固定すると rms はむしろ下がる**ので誤りが良い数字に化ける。→ show.json トップレベルに `lenses[]` を新設:

```jsonc
"lenses": [{ "id": "lens_1", "name": "Pixel 7a 超広角", "fxPx": 431.2, "srcW": 640, "srcH": 480,
             "k1": 0.18, "measuredAtIso": "...", "pointCount": 9, "raisedCount": 3,
             "rmsPx": 0.8, "accuracyM": 0.02 }]
```

- カメラは `lensRef` で参照する。**同型機（iPhone 13 Pro ×2）で共有できる**のが要点
- 固定画角の供給源は **レンズ > 前回の解**。測定条件を一緒に焼くので素性が追える
  （`lensTrustIssues` が「高さの点 0 本で測った画角」等を選ぶ前に言う）
- **Unity は `lenses` を読まない**（解いた値は `cameras[].calib.fxPx` に入る）。契約は広がらない。
  サーバ側は `capture-server.py` の `_STATE_KEYS` と `_default_show()` に足すだけ

**4. 精度を「実際のずれ cm」で言う。** `rmsPx` は当てはまりであって精度ではない（4 点なら誤差 0 でも
解が嘘のことがある）。`leaveOneOutError` が 1 点を伏せて解き直し、伏せた点がどれだけ外れるかの中央値を
**その場所の床の上での距離**（`pixelToMeters`）へ直して結果の 1 行目に出す。`calib.accuracyM` に焼き、
カメラ列のバッジと本番前チェックが同じ数字を使う。

**5. 本番前チェックに `🎯 較正` の行**（`app.js` の `preflightRows`）。**CG 人形を出すカットが指すカメラ**が
未較正 / 解像度不一致なら ❌。人形を出さないカメラの未較正は黙る（要らないので）。

**6. 保存した映像から較正できる**（📁 保存した映像から）。現場では「撮るだけ撮って後で解く」ことがあるし、
ライブが切れている間も作業を進められる（＝**実カメラが無くても UI を検証できる**）。
- 画像の読み込みは `fetch` + `createImageBitmap`。`<img>` の `load` / `decode` は描画パイプラインに
  依存し、裏タブ・非表示ウィンドウでは**永久に解決しない**（2026-07-29 に実際に踏んだ）
- プレートの寸法は配信そのままとは限らない（Quest 経由の記録は 480×360・配信は 640×480）。
  **縦横比が同じなら保存時に内部行列を比例で移す**（`rescaleCalib`。ピンホールの内部行列は解像度に比例し、
  k1 は正規化半径なので不変、姿勢も不変）。縦横比が違えば移さず理由を出す — クロップされた映像で
  比例させると黙って嘘の較正になる
- プレートで解いている間は結果欄が「これを撮った後にカメラを動かしていたら合いません」と言い続ける

**同日の監査で直した 6 件**（新規実装に対する初回監査。いずれも実コードで確認済み）:

| 直したもの | 壊れ方 |
|---|---|
| `liveSize` がカメラを跨いで残っていた | A を開いて閉じ、映像の無い D でプレートを解くと **A の配信実寸へ移して**焼く。Unity の `MatchesSource` が落ちて人形が出ないのに卓は「較正済み」と言う。`open`/`close` で必ず null に戻す |
| プレート読み込みに世代ガードが無かった | 読み込み中に別カメラへ移ると、**B のパネルに A の絵と A の点**が載る。そのまま保存すると A 由来の較正が B に焼かれる。`gen` を open/close で ++ し、await の後に照合する |
| leave-one-out の cm 換算に 0 が混ざっていた | 高さの点の画素は水平線より上に来ることがあり `pixelToMeters` が 0 を返す。`Number.isFinite(0)` は真なので中央値が下へ引かれ、**精度が実際より良く出て** `accuracyM` に焼かれていた。cm 換算は床の点だけで測る |
| `applyFrameChange` が候補セレクトを作り直さなかった | 復元した点は候補から除かれるので、セレクトの選択値が実体を失い、映像をクリックしても打てない（＝「映像前に開いて 🔄 で復元」が行き止まり） |
| 実測点の数え方が「まだ打っていない候補」だった | 前回の点を全部復元した状態で開くと measured=0 になり、**実測済みなのに「実測点が 0 個です」と嘘**をついて未測定のタイル角を候補に混ぜていた |
| 移して保存した直後に「無効です」と言っていた | `saved` は配信実寸・`frame` はプレート寸法なので判定が偽になり、成功の瞬間にワイヤー（唯一の一次証拠）が消えていた。**縦横比が同じなら移して重ねる**（`viewCalib`）|

あわせて、床と高さの境界を数学側と揃え（`RAISED_MIN_Y = 0.01`）、置き直しの疑いの閾値を
画の対角の 3% にし（固定 px は解像度で意味が変わる）、保存判断を純関数 `decideSaveCalib` へ出した。

#### 影・接地・オクルージョン（2026-07-27）

**人形だけを描くと必ず浮いて見える**ので、同じ RT へ 3 つを重ねる。すべて「rgb=0 / a=濃さ」の
premultiplied 断片なので、合成側の over が自動的に乗算（背景を (1-a) 倍）になる。

| 何を | 実装 | 要点 |
|---|---|---|
| 床への影 | [`ShowShadowProjector.shader`](../../Assets/Art/Shaders/Cg/ShowShadowProjector.shader) | **平面投影シャドウ**。人形メッシュを光線方向で床平面へ潰して描く。URP のシャドウマップは使わない |
| 接地影 | [`ShowGroundBlob.shader`](../../Assets/Art/Shaders/Cg/ShowGroundBlob.shader) | 足元の楕円。**「浮いている」に一番効くのはこれ** |
| オクルージョン | [`ShowOccluder.shader`](../../Assets/Art/Shaders/Cg/ShowOccluder.shader) + [`ShowRoomProxy`](../../Assets/Scripts/Streaming/Cg/ShowRoomProxy.cs) | `Blend Zero One` + ZWrite On で色を書かず深度だけ置く。人形が壁・箱の裏へ回れる |

- **なぜ URP のシャドウマップを使わないか**: 人形は「シーンのライトを一切参照しない」のが不変条件
  （現場の照明・URP 設定・同居アプリの都合で見えが変わると演出が壊れる／URP の Light は cullingMask を
  尊重せず HMD が見る実 VR 空間まで照らす）。シャドウマップ方式は未検証の仕掛け（`beginCameraRendering` での
  ライト on/off とシャドウマップ culling の順序）が要り、失敗時の代替が共有 URP Asset の変更＝同居 2 アプリの規約と衝突する。
  平面投影ならライトが 1 つも要らない。セルフシャドウは出ないが、この画質では観測できない
- **⚠ ステンシルを外すと影が「濃い斑」になる**（腕と胴の投影が重なった所だけ二重に暗くなる）。
  `Stencil { Ref 1 Comp NotEqual Pass Replace }` で 1 画素 1 回だけ描く。そのため
  **RT の depth buffer は 24（depth24 + stencil8）でなければならない** — 16 に戻すと沈黙して斑に化ける
- **`layout.room` が未著作でも人形と影は出す**（床は course y=0 の無限平面）。プロキシに依存するのは
  オクルージョンだけ。ここを止めるとフェイルソフトが壊れる
- 描画順は Queue で決まる: オクルーダ(-200) → 影(-100) → 接地影(-99) → 人形(Geometry)

#### なじませ（2026-07-27）

- **映像遅延の補償**: スクリーンに映っているのは撮影から 100〜200ms 前の姿。「いま」の体験者の位置に
  人形を描くと歩行中ずっと先行してずれる（1m/s で 15cm）。[`BodyInputHistory`](../../Assets/Scripts/Streaming/Cg/BodyInputHistory.cs)
  が身体入力を時刻つきで貯め、`ShowCgLayer.CurrentBody` が **0.15 秒前を補間して読む**。
  **手の有効フラグは補間しない**（取れ／取れないの境界を混ぜると無効座標 (0,0,0) へ引っ張られて腕が飛ぶ）。
  遅延値は当面 const — `CameraStream.EstimatedLatencyMs` は「Unity 受信からテクスチャ反映まで」しか測れておらず
  （撮影・エンコード・伝送を含まない）、そのまま足すと過小補償になるので使わない
- **CG だけを鈍らせる**: `ScreenComposite` の `SampleCgSoft`（9-tap tent・既定 0.7 テクセル）。
  RT を映像実寸まで落としたうえでさらに掛ける。**premultiplied なので rgb と a を同じ重みでぼかす**
  （別々だと rgb > a の画素ができて縁に明るい滲みが出る）
- **色収差は入れない**と決めた。効果が小さい割に、premultiplied の rgb/a 整合を崩して縁を壊すリスクがある

#### なじませ 2 — 時間・接地・輪郭（2026-08-06）

「人形が CG っぽい」を実装レベルで監査した結果。**陰影や解像度の合わせ込みより、この 3 つが先に効く**。

- **⚠⚠ 人形を実写の刻みで描く**（最重要）。旧実装は仮想カメラが `enabled=true` で、
  **表示レート（72/90Hz）で毎フレーム RT を描き直していた**。実写は 15〜30fps なので、
  人形と腕だけが滑らかに動き、背景は刻んで動く。**動いている最中に一番強く「別レイヤ」に見える差**で、
  陰影をどれだけ合わせても消えない。
  → `enabled=false` + 手動 `Render()`。`CameraStream.LastFrameSeq` が進んだ時だけ描く
  （`ShowCgLayer.RenderIfDue`）。実写が止まっている間（録画・プレート・受信断）は 15fps の刻みで描く。
  **位置・腕は毎フレーム進める**（履歴と内部状態のため）。間引くのは絵にする瞬間だけ
- **接地影の下限が人間用だった**。`BlobRadiusMinM = 0.15` は成人 1.6m 前提で、全高 0.40m の市松人形では
  本来 0.088m のところ 0.15m へ**持ち上がっていた**（＝背丈の 3/4 の黒い円盤が足元に敷かれ、
  「切り抜きを板に貼った」ように見える）。下限を 0.03m へ。**人形の大きさに比例させる**
- **投影シャドウの縁がシャープすぎた**。平面投影は形をそのまま床へ潰すので縁をぼかす場所が無い。
  代わりに **`shadowSoftM` を「濃さの高さ減衰」として効かせる**（`1/(1 + h/softM)`）。
  実際の半影も遮蔽物と受光面の距離に比例して薄くなるので物理的にも正しく、
  **硬い縁が目立つのは接地点の近くだけ**になる（そこは接地影 blob の feather がぼかしている）
- **影の `ZTest` を `Always` → `LEqual`**。壁の裏に回った人形の影が壁の手前に出ていた
  （`ShowRoomProxy` は床を描かないので z-fight しない）
- **RT に MSAA 4x**。人形の輪郭だけが 1bit のギザギザで、実写側の JPEG のなまった縁と食い違っていた
- **人形の明るさを映像へ寄せる**（`ShowCgLayer.LumaGain`）。カメラごとに露出も現場の照明も違うのに、
  人形はどのカメラでも同じ明るさで出ていた。`MjpegScreen.SourceLuma`（post 前の生の平均輝度・4Hz の疎サンプル）
  を基準に、**主光源と環境光へ同じ倍率**を掛ける（片方だけだと陰影の比が変わって材質が変わって見える）。
  同じ輝度は `CameraFeelFx` の自動露出も読む

#### なじませ 3 — 光・色・判断の土台（2026-08-07）

「合成が CG に見える」を数値で詰めた回。**目視では誰も指摘できていなかった差**（人形の彩度が周囲の
3〜4 倍・白い顔が周囲の 3 倍の明るさ）が、測った瞬間に一目で分かった。逆に**数値だけでも決まらない**
（全部そろえたら絵から人形が消えた）。以後この 2 つは必ず並べて見る。

##### ⚠ まず土台が壊れていた — 合成プレビューが実機より綺麗な絵を出していた

`ShowCompositePreview` が `_NoiseDark` / `_NoiseFixed` / `_ExposureBias` / `_VignetteBias` /
`_ActorFocus` を**1 つも書いていなかった**。実機は show.json に `feel` が無くても既定値
（暗部ノイズ 0.10 / 固定ノイズ 0.035 / 自動露出 0.7）で効くので、**プレビューだけ粒も露出の揺れも無い
絵**になっていた ＝ 人形が実機より浮いて見え、馴染ませの判断が過剰になる。
直近の 7a5932b「合成プレビューが本番と違う絵を出していた」で 3 つ塞いだばかりの穴の 4 つ目。

- 解決も uniform の書き方も `CameraFeelFx.Settings.Resolve` / `CameraFeelFx.WriteUniforms` を通す
  （プレビューが独自に既定値を持つと必ず食い違う）
- 自動露出は静止画 1 枚なので `CameraFeelLogic.SteadyBiasFor`（収束値）を使う。時間で追った先と
  一致することはテストが固定する
- **較正確認（`calibcheck_*.png`）には掛けない** — ずれを見る絵に粒と露出の揺れを足すと読めなくなる

##### 陰影が albedo に比例していた（＝「切り抜きを貼った」ように見える最大の原因）

`ShowActor.shader` の影側が `lerp(_ShadeColor, albedo, _Ambient)` で、**`_LightColor` が掛からず
albedo に比例**していた。すると暗い部位ほど影と光の差が消える（実測: 白い顔で shade/lit = 0.70 に対し
赤い着物では **0.92 ＝ ほぼ陰影なし**）。人形全体の輝度レンジが実写の数分の一しか無かった。

- 影側も `albedo * _LightColor * (_Ambient + _ShadeColor)` にして、**比率が albedo に依らない**ようにした。
  `_Ambient` は「主光源に対する影側の比率」、`_ShadeColor` は環境光の色みバイアス
- **`ShowCgLayer.ApplyLight` は ambient に倍率を掛けない**（`_LightColor` 経由で一元的に効く。
  両方に掛けると二重になって暗い区間ほど陰影が潰れる）
- **リムも `_LightColor` に比例させる**。素の色で加算すると、暗い区間で縁だけ相対的に明るく残り、
  視線だけで決まるリムが「CG である合図」として一番目立つ形になる

##### 明るさは「画に出た後の・立つ場所の」明るさへ寄せる

`LumaGainFor` が **全画面平均基準・下限 0.545** で、暗い区間で頭打ちになっていた。

- **局所輝度**（[`SourceLumaMap`](../../Assets/Scripts/Streaming/SourceLumaMap.cs)）: 4×4 の粗い格子で
  場所別の明るさを持つ。`MjpegScreen.SampleLuma` の同じ走査から取るのでコスト増はゼロ。
  実測で人形が立つ場所は全画面平均の **26〜68%** しかない（黒いカーテンが画の大半を占めるため）。
  格子が粗いのは仕様 — 細かくすると人形の背後を横切る物で明るさが跳ねる
- **自動露出の後で測る**: `LumaGainFor(localLuma, appliedExposureBias)`。装置が画全体を持ち上げた後の
  明るさが体験者に見える明るさなので、生の輝度へ寄せてから露出を浴びると二重補正になる
- **`ReferenceLuma` は 0.35 → 0.20**（＝倍率 1 になる明るさ）。全画面平均に合わせた値のまま局所へ
  切り替えると基準が高すぎて**絵から人形が消える**（実測で周囲の 0.75〜0.86 倍まで沈んだ）。
  3 周目に「自分がそこに立っている」と読めなければ演出が成立しない
- 下限 0.35 → **0.15**（`LumaGainMin`）。0 にしないのは、消えると演出が成立しないため

##### 彩度は post では埋まらない — 実写だけが受けている「色の粗さ」を CG にも掛ける

実写は JPEG 4:2:0 で**色差が半解像度**、そのうえ q40 の量子化と暗所のカラーノイズ抑制で色がにじみ
彩度も落ちている。CG はどれも受けないので人形だけ色が鮮鋭で濃い。**post の彩度は乗算なので比が保存され、
原理的に埋まらない**（実測 3〜4 倍のまま）。

- `ScreenComposite` の CG 層だけに `_CgChromaBlur`（2.2 テクセル）/ `_CgChromaGain`（0.55）。
  輝度は鮮鋭なまま、色差だけ広く均して倍率を掛ける。**post には触らない**（FS_POST 4 箇所の
  手作業同期に入れない）。値は `ShowCgLayer` の const（`_CgSoften` と同じ流儀・シェーダ既定と一致させる）
- 結果、人形の彩度は**実写の上位 5〜17%** に収まった（＝実写にもありえる色の範囲）

##### 接地影が壁を突き抜けていた

`ShowGroundBlob.shader` だけ `ZTest Always` で、人形が部屋プロキシの裏へ回ると**本体と投影影は隠れるのに
足元の楕円だけが壁の上に浮く**。投影シャドウ側と同じ `LEqual` に揃えた。

##### 測る道具（絵と必ず並べて見る）

- **浮き具合の 1 行**が全カットのキャプションと Unity のログに出る:
  `blend doll/around: luma 24.9/36.4 (x0.69)  sat 13.9/5.3 (x2.60)  grain 24.5/25.1 (x0.98)`。
  人形の範囲は **CG レイヤの alpha が正**（マスクを別に作らない）、周囲は人形を囲む 1.8 倍の矩形から
  人形を除いた部分。比較は**同じ 1 枚の中**で完結する（プレートと合成を比べると post のぶんが丸ごと差に出る）
- **足元が枠外のカットを名指しで警告**する（`FEET OUT OF FRAME`）。実測で 4 カット中 3 つが該当し、
  接地の手掛かりが画から丸ごと消えていた。**立ち位置は演出の管轄なのでコードでは直さない**が、
  黙って出すと「なぜか浮いて見える」の原因に到達できない

##### やらなかったこと（理由つき・再検討する人向け）

- **URP のシャドウマップ / GI / PBR / RT 解像度を上げる / `_CgSoften` を減らす** — CG の内部整合が
  上がるほどプレートとの不整合が際立つ。「シーンのライトを参照しない」不変条件も壊れる
- **`aura` を強めて輪郭を隠す** — 追従する円形ハローは「スプライトに追従するヴィネット」に見える
- **周回で単調に劣化させる** — post は合成の後なので録画も浴びる。一番見せたい「1 周目の自分」が
  一番見えなくなる（[なじませ 2](#) と同じ理由）
- **ホールド / 焼き付き（`hold` / `burn`）を CG へ** — 現状は実写だけが止まり人形は動き続ける。
  `burn` の「動いたものの跡だけが残る」設計意図に対して**唯一の動体である人形にだけ跡が残らない**。
  現行 show.json では未使用なので見送った。直すなら `CameraFeelLogic.Frozen` を
  `ShowCgLayer.RenderIfDue` のゲートにする
- **rec / plate カットで CG がライブ基準のまま** — `RenderIfDue` の刻みも `LumaGain` も `ResolveCalib` も
  `registry.GetActive()`（ライブ受信）を見ているが、3 周目の録画・無人プレートは `_OverlayTex` に入る。
  **人形が一番効く場面で、人形の刻みと明るさが表示中の画と別クロック**。ライブが落ちていれば較正が
  外れて概算 pose へ黙って降格もする。直すなら表示中のソースを基準に切り替える（未着手・影響が広い）

⚠ **Quest 実機未検証**（2026-08-07。EditMode 1060/1060・Editor の合成プレビューで数値と絵を確認）。
暗い区間（カメラ B）で人形が読めるかは実機で被って確かめる必要がある。

#### 卓の著作面（2026-07-27）

| 何を | どこで | 実体 |
|---|---|---|
| カメラの較正 | カメラ列の［🎯 姿勢を合わせる］ | [`calib.js`](../../tools/web-compositor/calib.js) |
| 部屋のプロキシ（壁・箱・床） | フロアマップの **🧱 部屋**モード | [`room-model.js`](../../tools/web-compositor/room-model.js) + `floormap.js` |
| CG 照明 | フロアマップ下の **💡 CG 照明**パネル（モードに紐づかない） | 同上。マップ上に光の向きを ☀ + 破線矢印で描く |
| 人形の立ち位置 | 演出リボンのカット詳細 → ［📍 画面で置く］ | [`actor-proxy.js`](../../tools/web-compositor/actor-proxy.js) + `calib.unprojectToFloor` |

- **映像の床をクリックして人形を置ける**（`unprojectToFloor` = `projectPoint` の厳密な逆）。
  数値入力とも双方向。向きは矢印の先をドラッグ。**較正済みでないカメラでは「実機では人形が出ません」と出す**
- **輪郭プロキシ**（足元の楕円・身長ボックス・頭・向き矢印）を映像とシミュレータに重ねる。
  **意図的に写実にしない**（寒色の半透明の線だけ）— 写実に見えると著作者がそれを信じて Unity の最終確認を飛ばす
- **人型シルエット**（2026-07-28 追加・`actorBodyGeometry`）: 箱のワイヤーだけでは事前オーサリングの段階で
  構図（大きさ・立ち位置・向き）を判断できなかった（ユーザー指摘）。身長比で定義した骨格 13 本 + 頭を
  投影し、**太さを「その位置での 1m あたりの画素数」から解いて**線幅で描く（遠いほど細い＝距離感が出る）。
  姿勢は実機と同じ「T ポーズから腕を下ろしたマネキン」。**写実にしない方針は変えていない**
  （同色の半透明シルエット 1 色で、陰影も素材も付けない）。
  透視のため**近距離では頭が人体比率より大きく写る**が、これは正しい挙動（実測 1.5m で 18% / 4.5m で 13%）。
  テストは正射影に近い遠距離で人体比率を固定している（`actor-proxy.test.mjs`）
- **シミュレータの follow は体の向きを歩行方向で代用する**（2026-07-28）。実機の follow は
  **体験者の頭 yaw**（分身）だが、卓は頭の向きを持っていない。1cm 未満の揺れでは向きを変えず、
  止まっている間は最後の向きを保つ。**UI で必ず「歩いた方向で代用。実機は頭の向き」と注記する**
  （黙って別物を見せない）
- **照明の 7 項目はすべて絵に効く**（2026-07-27 に接続）。向き・仰角は影の方向、`tempK` × `intensity` は
  人形に当たる光の色（`ShowCgLayer.KelvinToLinearColor` → `ShowActor.shader` の `_LightColor`）、
  `ambient` は影側の持ち上げ、`shadowDensity` は影の濃さ、`shadowSoftM` は**接地影の縁のぼけ幅**。
  - 色温度の式は**卓と C# で同一**（`room-model.js` の `kelvinToRgb` / `ShowCgLayer.KelvinToLinearColor`）。
    4000K の期待値を両側のテストにハードコードして突き合わせている。**片方だけ直すと沈黙して食い違う**
  - `shadowSoftM` を**平面投影シャドウには効かせない**（形をそのまま床へ潰すので縁をぼかす場所が無い）。
    受け口だけ作って効かせないのは著作者への嘘なので、シェーダからプロパティごと削除した
- `placementOf` は「placement の 3 成分がすべて 0 なら actor の既定へ落とす」ヒューリスティックを使っている
  （正規化が常に `{0,0,0}` を作るため「著作された (0,0,0)」と「未著作」を区別できない。actor 既定も通常 0 なので実害なし）

### BGM（区間で切替・停止・ループ範囲）— 2026-07-25

旧: `[Bgm]` の AudioSource が 1 曲を起動中ずっとループ（固定）。新: **タイムライン区間で切り替わる**。

- **データ**: show.json トップレベル `bgmTracks[]`（`{id,name,url,loopStartSec,loopEndSec,volume}`）+
  `bgm`（ラン既定）+ `timeline.segments[].bgm` / `hasBgm`（区間指示）。
  区間指示 = `{action:"play"|"stop"|"continue", trackId, loop, startSec, loopStartSec, loopEndSec, volume, fadeInSec, fadeOutSec, restart}`。
  **-1 = トラック既定を継承**。present-flag は `hasBgm`（[`TimelinePresentFlags`](../../Assets/Scripts/Streaming/TimelinePresentFlags.cs) が AND で確定）
- **セマンティクス**（純ロジック [`BgmPlanLogic`](../../Assets/Scripts/Streaming/BgmPlanLogic.cs)・テスト `BgmPlanLogicTests` 16 本 /
  JS ミラー `resolveBgmLane`）: 指示の無い区間は**曲が途切れず継続**（continue が既定）/ 同一トラックは
  **Retune**（再生位置を保ちループ範囲・音量だけ更新）/ 別トラックは **Start**（クロスフェード）/ **Stop** はフェードアウト
- **再生** = [`BgmDirector`](../../Assets/Scripts/Streaming/BgmDirector.cs)（AudioSource ×2 でクロスフェード・
  ループ範囲は `Update` で監視して巻き戻す＝`AudioSource.loop` は全長専用のため）。クリップは URL から
  `UnityWebRequestMultimedia` で取得しキャッシュ（`sa://` 焼き込み / ライブ URL 双方）。**擬似トラック id
  `__default__` = APK 同梱の既定クリップ**（音源を web 側に二重に置かず「元の曲へ戻す」が書ける）
- **区間への配線**: [`TimelineDirector`](../../Assets/Scripts/Streaming/TimelineDirector.cs) がゾーン進入で
  `ApplySegment`。ラン開始（runEpoch 変化 / 右グリップ長押し）で**ラン既定へ戻る**。show.json の rev が上がる
  たびに鳴り直さないよう、既定はシグネチャ比較で変化時のみ適用する
- **演出（Take）は音も一時占有できる**（2026-07-26）: `timeline.segments[].takes[].bgm` / `hasBgm`（型は区間と同じ
  `ShowBgmDef`）。**画面と同じ規則** — 演出開始で BGM レーンを占有し、終了・中止・ライブ卓の介入・ランリセットで
  必ず返す（[`TakeRunner`](../../Assets/Scripts/Streaming/TakeRunner.cs) → [`BgmDirector.BeginTakeOverride` /
  `EndTakeOverride`](../../Assets/Scripts/Streaming/BgmDirector.cs)）。
  **戻り先は開始時のスナップショットではなく「いまのレーン」** — 演出中に体験者がゾーンを移ると、そのあいだに
  届いた区間指示は鳴らさず**戻り先だけ更新**され、演出明けにそこへ行く（画面の「戻り先は再計算」と対称）。
  同じ曲へ戻る時は中断位置から続く。判定は純ロジック `BgmPlanLogic.DecideRestore`（テストあり）。
  **指示を書かなければ演出は音に触らない＝その区間（カメラ）の曲がそのまま流れる**。
  卓は演出インスペクタに「🎵 この演出のあいだ: ◯◯（この区間の曲のまま / この演出で切り替え / 無音）」を常時表示し、
  変える時だけリボンのチップに `♪ 曲名` / `🔇` が出る（JS ミラー `resolveTakeBgm`）
- **カット（Step）はレーンそのものを書き換えられる**（2026-08-23・`canon/LEDGER.md` 0119）:
  `timeline.segments[].takes[].steps[].bgm` / `hasBgm`（型は区間・演出と同じ `ShowBgmDef`）。
  ⚠⚠ **演出の `bgm` とは意味が違う。** 演出のは**占有**（終われば戻る）、カットのは
  **レーンの書き換え**（[`TakeRunner.BeginStepBgm`](../../Assets/Scripts/Streaming/TakeRunner.cs) →
  `BgmDirector.ApplySegment`）。**演出が終わっても区間を移っても鳴り続ける**ので、
  「ここから先はこの曲」と言いたいときの唯一の口。戻すのは別の指示かランのリセット。
  - 使っているのは **2 周目 C の接近、呼びかけ（`dollCall`）の次のカット**だけ。
    区間の縁ではなくカットに載せているのは、**差し替えの合図が呼びかけだから**
    （区間に載せると 3 周目 A に入るまで鳴り始めない）
  - ⚠ **飛ばされたカットでは掛からない**（素材が無くて実機が飛ばした場合）。
    画が出なかったカットで曲だけ変わると、走行から「なぜ変わったか」が読めなくなる
  - ⚠ 同じトラックを指すカットを 2 度通っても**頭出しし直さない**（`Decide` が Retune へ倒す）。
    引き返して演出が再演されても曲は続く
  - 観測は `ev=bgm trk=<id> play=<0/1>`（レーンが変わった縁で 1 行）。判定は
    `analyze-xp-log.py` の「カットが替える劇伴」— **呼びかけより先に替わっていたら FAIL**
  - 卓に編集 UI は無い（`show.json` を手で書く）。`serializeStep` の白名簿には入っているので
    💾 保存で消えない。区間の帯には `▶ 前の曲 → 後の曲` と出る（`resolveBgmLane`）
- ⚠⚠ **2026-08-23 から、劇伴は黒から 3 周目の終わりまで鳴り続ける**
  （`canon/LEDGER.md` 0115・正本は `rules/sound-design.md` §1。0088 の「黒だけ」は置き換わった）。
  [`ShowSoundDirector`](../../Assets/Scripts/Streaming/Sound/ShowSoundDirector.cs) が毎フレーム
  `BgmDirector.SetScoreGain` を書き、**0 へ落とすのは終幕・位置合わせ・体験の終わりだけ**。
  ⚠⚠ **出口が開いたので、ここに書いてある区間 BGM・演出 BGM の著作が本編でも鳴る。**
  0088 のあいだは 1 音も鳴らなかった。⚠ ただし区間ごとに曲を変えると、0115 が退けた
  「周ごとに背景が変わる」に戻るので、それは世界観の判定（`rules/canon-boundary.md`）
- **後方互換**: show.json に bgm 指定が無ければ `BgmDirector.defaultClip`（HorrBGM）を従来どおりループ。
  **⚠ シーンは `Setup Main Demo Scene` の再実行で [Bgm] を BgmDirector 化する必要がある**（未実行なら
  BgmDirector 不在 → 区間指示は無視され旧 AudioSource の固定ループが鳴る＝安全側）
- **⚠ Quest 実機未検証**（2026-07-25。EditMode 568/568・Web 卓 UI / 検証モードはブラウザ実測）

### 編集の粒度と自由度（2026-07-19 timeline v2 で更新）

| 調整対象 | 周×カメラ（セグメント）粒度で変えられるか | どこで |
|---|---|---|
| どの cue を出すか / delaySec / once / 複数 cue | ✅ **タイムライン区間で直接** | `timeline.segments[].cues[]` |
| cue パラメータ上書き（strength・フェード・trim） | ✅ **区間ごとに直接**（同じ cue を周ごとに違う強度で使える） | `cues[].override`（hasOverride） |
| マスク領域・素材（動画/画像） | ✅ 区間インスペクタで既存 cue 割当 or その場で新規 cue 作成 | cue 単位（`cues[]` ライブラリ）。マスクはカメラ構図に対して作るため camera 帰属は維持 |
| **画像加工 post（露出・コントラスト・彩度・色温度・ヴィネット・グレイン・走査線）** | ✅ **区間ごとに上書き可**（旧: カメラ単位固定で不可だった） | `timeline.segments[].post`（hasPost）。未設定は camera→global にフォールバック |
| **別カメラのインサートショット（N 秒差し込み）** | ✅ **区間ごとに enter/exit で** | `timeline.segments[].insert`（hasInsert） |
| **BGM（曲の切替・停止・ループ範囲・音量・フェード）** | ✅ **区間ごとに**（指示の無い区間は継続） | `timeline.segments[].bgm`（hasBgm）+ `bgmTracks[]` / ラン既定 `bgm` |
| **演出中だけの BGM**（インサート・離脱時演出を含む） | ✅ **演出ごとに**（無指定＝その区間の曲のまま。終われば戻る） | `timeline.segments[].takes[].bgm`（hasBgm） |
| **ここから先の BGM**（レーンの書き換え・**戻らない**） | ✅ **カットごとに**（0119。呼びかけの次のカットが使う） | `timeline.segments[].takes[].steps[].bgm`（hasBgm） |
| **開始規則**（進入 +t 秒 / 離脱時 / **ライン通過**） | ✅ **演出ごとに**（ラインは担当カメラに紐づく共有資産。周は演出側が決める） | `takes[].at` / `lineId` + `layout.lines[]` |
| **画のホールド・焼き付き・人形に付き従う劣化** | ✅ **カットごとに** | `steps[].hold` / `burn` / `burnSec` / `aura`（卓のカット詳細） |
| **スクリーンの外の闇に開く目** | ✅ **カットごとに**（値は開く目の割合。**尺も開閉の縁も実機が持つ** — 開始・終了は体験者の居場所） | `steps[].eyes`（卓のカット詳細「闇の目」） |
| **撮像の質**（暗部ノイズ・貼り付く粒・自動露出の追従） | ❌ **ラン全体で 1 組**（装置の素性なので途中で変えない） | show.json `feel`（卓の ⚙ 欄） |

- **cue（`OverlayCueData`）自体は色補正 post を持たない**（従来どおり）。画面全体のグレーディングは segment post > camera post > global の 3 段で解決される
- キー空間は現状維持（`cues[].camera`=文字列 id / `timeline.segments[].camera`=int index。変換は Web の `cuesForCam` 流儀）。統一は Unity 共有契約の破壊を避けるため見送り

## 接続の堅牢化 — 端末内在 ID + 発見プロトコル（fixedcam-discovery/1）— 2026-07-18

スロット（A/B/C）⇔端末の対応を **DHCP の IP 頼みにしない**。ID を正・IP を手段にする。
計画 [.claude/plans/2026-07-18_connection-robustness.md](../plans/2026-07-18_connection-robustness.md)。

- **端末内在 ID**: streamer v0.3.0 が cameraId（A/B/C）を永続保持・画面巨大表示。`/info` に `cameraId` / `uuid`（install 毎）/ `show`（トークン・既定 mawarimi）が載る
- **発見**: UDP :8830。**Quest/PC が probe をブロードキャスト送信 → スマホが unicast で announce 応答**（Quest はブロードキャスト受信しない設計 = MulticastLock・共有 manifest 不要）+ スマホ/PC 卓は 5s 毎の定期 announce も送出
- **Unity 側** = [`DiscoveryClient`](../../Assets/Scripts/Streaming/DiscoveryClient.cs)（`MainDemoSceneSetup` が配線）:
  発見表 id→{ip,port,uuid,lastSeen} を維持。**張り替えは実害駆動・2 系統**（2026-07-23 監査修正） — (1) **フレーム断** 5s 継続、または (2) **id 継続照合の不一致** 5s 継続（受信中 `/info` の cameraId+show を期待スロットと毎回照合。**streamer 側で配信を続けたまま cameraId を A→B に切り替えた/2 台で入れ替えたケースは断が起きないため、旧実装では永遠に反映されなかった** — `DiscoveryLogic.IsIdentityMismatch`・テストあり）。いずれか確定 + 発見表に別エンドポイント + **HTTP /info で cameraId+show 照合 OK** の時だけ `ApplyDiscoveryEndpoint` → 再接続。beacon 断だけでは何もしない（フラッピング防止）。id 不一致でメタが古い（/info 失敗継続・4.5s 超）場合は照合しない（誤検知防止）。不一致確定でも候補が無ければ現フィード継続 + HUD ×（黒より誤映像 + 警告を選ぶ）
- **優先順位**: `manual-pin（show.json cameras[i].pinned）> live discovery > show.json（ライブ>キャッシュ>焼き込み）> .asset 焼き込み`。Web 卓で host を手入力すると自動 pin（📌 表示・解除ボタンあり）。**pin 適用時は稼働済み discovery 層を `ClearDiscoveryEndpoint` で剥がす**（2026-07-23 修正。旧実装は discovery 層が上に残り pin host が無視される優先順位逆転があった）。**断 5s + 発見表に候補なし + discovery 層稼働中**なら discovery 層を解除して show.json/焼き込みへ自動フォールバック（旧: discovery 層は失効せず貼り付いたままだった）
- **二重 ID**（同 id 別 uuid）: **切り替えず現接続維持 + 警告**（HUD / Web 卓の発見パネル）。同 uuid の IP 変化（roam）は追従
- **PC 卓** (capture-server.py): beacon 受信で `cameras[].host` を**自動追従**（グローバルトグル・pinned 除外・実測 1 秒で復元）。`GET /discovery`（発見表）/ `GET /diag`（PC→カメラ HTTP・beacon・Quest heartbeat の 3 経路診断 = **AP クライアントアイソレーション即検出**）。PC 自身も role:"show-server" で announce → Quest が卓サーバを自動発見（`ShowServerSource` runtime override）
- **キルスイッチ**: Unity は `control.discoveryEnabled`（show.json）+ DiscoveryClient の SerializeField、サーバは env `FIXEDCAM_DISCOVERY=0`。切れば従来の静的 IP 運用へ縮退
- **守れない障害**: AP のクライアントアイソレーション（c2c 全遮断）は技術で救えない → 診断パネルで即検出し**自前 AP 持ち込み**へ切替（運用の第一選択は自前 AP + MAC 静的リース。発見機構は保険）
- **⚠ Quest 実機未検証**（2026-07-18。streamer 3 台 + PC 卓は実測検証済み）

## ⚠ host 未設定のカメラは「異常」ではない（2026-07-31 実害）

現場に置いていないカメラ枠（`cameras[].host` が空・焼き込み .asset も空）は**接続しないのが正しい**。
解析器も「show.json で host 未設定（接続しないのが正しい）」と扱う。ところが実装は例外を投げていた:

- `MjpegStreamReceiver` のコンストラクタが `new Uri("http://:8080/video")` で `UriFormatException`
- `CameraStreamRegistry.Awake` は**全カメラを 1 本の for で作る**ので、1 台が投げると
  **残り全部が初期化されない** → 実測で 3 台とも `con=0`、体験が丸ごと砂嵐（96%）になった
- 表面化していなかったのは、たまたま全カメラに host が焼かれていたから。
  **`Phone04.asset` の host を空にした瞬間に全カメラが死んだ**

→ 二重に堰き止めてある: (1) receiver は `Uri.TryCreate` で失敗を許容し、`Start()` が
`_uri == null` なら何もしない（後から show.json / discovery で host が入れば `ReapplyConnection` が
作り直す）、(2) Registry の生成ループを try/catch で包み、1 台の失敗が他を巻き込まないようにした。

## エラーハンドリング

- 接続失敗時は **指数バックオフ**で再接続（1s → 2s → 4s、上限 30s）
- **バックオフは接続確立でリセット**（2026-07-22）: 一度ヘッダのパースまで到達した接続の切断は、次リトライを 1s から始める（`MjpegStreamReceiver._connectionEstablished` を確立時に立て、catch でバックオフを 1s に戻してから倍化）。長時間安定した後の単発切断が 30s 待ちにならない
- **バックオフ待機は RequestReconnect で中断可能**（2026-07-22）: バックオフ Delay 中も CTS を `_connectionCts` に公開し、stall watchdog 等の `RequestReconnect` が待機（最大 30s）を即中断して即リトライ（`DelayWithReconnect`）
- **LoadImage 失敗時の再接続**（2026-07-22）: 壊れ JPEG 連続で `Texture2D.LoadImage` が false（texture 未更新＝黒/フリーズ）のとき受信統計を進めず、連続 30 枚 or 2 秒相当を超えたら（cooldown 明けで）強制再接続（`CameraStream`）。成功時のみ `_lastFrameTime`/seq/E2E を更新するので lag 検出・stall watchdog・SignalLostFx が沈黙しない
- **エンドポイント張替（`ReapplyConnection`）の後片付け**（2026-07-23 監査修正）: ①**suspend 中でも新 receiver を必ず起動**（旧: focus 喪失中に show.json/discovery 適用で張替が走ると新 receiver が未起動のまま残り、resume も起動しないため恒久黒画面）②**エンドポイント世代トークン**で in-flight の /info・/health フェッチを無効化（旧: 旧端末のメタが書き戻され、`_metaInflight` ガードが新エンドポイントの再取得をブロック）③`X-Frame-Seq` 系列・DroppedFrames・E2E 推定をリセット（別端末の seq 系列と混線して偽 Dropped を計上していた）
- **健全性判定は純ロジック [`StreamWatchdogLogic`](../../Assets/Scripts/Streaming/StreamWatchdogLogic.cs) に集約**（2026-07-23 監査修正・テスト `StreamWatchdogLogicTests`）: suspend/resume-gap/stall/lag/decode-fail の判定を UnityEngine 非依存（時刻・dt 注入）で分離し、`CameraStream` は薄いシェル。挙動を変えるときはテストを先に直す
  - **resume-gap は suspend も解除する**（A1 修正）: HMD 着脱で OS の resume コールバック（`OnApplicationPause(false)`）が来ないまま Update が再開すると `_suspended=true` が固着し、stall watchdog・砂嵐・discovery 自動張替が全停止していた。`BeginTick` の resume-gap 検知（dt>0.5s）で `_suspended=false` を揃えて解除（Registry 側ラッチも `Update` で自己回復・二層冪等）
  - **stall watchdog は `LastFrameRealtime` を汚さない**（A2 修正）: 旧実装は再接続発火時に `_lastFrameTime=now` を代入し、SignalLostFx が「復旧した」と誤認して真の信号断中に砂嵐が約 10 秒毎に 0.6 秒消灯していた。代入を廃し、再発火ゲートを `StallReconnectSec`（10s）に変更（周期は従来どおり）
- **`ConnectionKey` はパスワード変化も検知**（A3 修正・テスト `CameraSourceTests`）: `host|port|user|FNV-1a(pass)` 形式。show.json で auth のパスワードだけ変えても `ReapplyConnection` が発火する。生パスワードはキー文字列に露出しない（指紋のみ）
- タイムアウトは 3 秒
- `/info` `/health` 取得失敗は無視（DroidCam フォールバック互換）
- 画面には接続状態を表示（VR 内デバッグ UI）

## WebRTC（将来）

- Unity WebRTC パッケージ `com.unity.webrtc` を使用
- スマホ側は `getUserMedia` + RTCPeerConnection を吐く PWA を別途用意 / もしくは fixed-cam-streamer に WebRTC 配信パスを追加
- シグナリングは小さな WebSocket サーバー（Node.js）を立てる前提
