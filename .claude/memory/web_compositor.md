# web compositor（tools/web-compositor/）

**1 ページ統合 UI（縦割り = カメラ列）**（2026-06-16 にユーザー要望で再編、〜06-17 で機能追加多数）。旧 3 画面（コンソール / コンポジット検証 / 合成エディタ multicam.html）を 1 ページへ統合。Webcam/テスト source・ギャラリーは撤去したが、**境界ブレンド（色統計/ラプラシアン/フェザー）と生成プロンプト管理はユーザー要望で復活**。

構成: **ステータス**（Unity生存/アクティブカメラ/fps/反映rev、🚶ゾーン自律・📂撮影フォルダ）＋ **境界ブレンドバー**（フェザー/色統計+強度/ラプラシアン+レベル、全カメラ共通）＋ **マルチカメラ**（列＝カメラ A/B/C、各列は**下→上の加工フロー**＝ **① 生映像 → ② マスク → ③ 画像加工 → ④ Quest 実映像**、2026-06-18 にユーザー要望で縦フロー化）＋ 下部 **生成プロンプト**。
- **④ Quest 実映像（最上段）**: 画質+合成を当てた最終見た目。**マルチパス合成 `pipeline.js`**（取り込み→色統計マッチング→ラプラシアン→post）。カメラ実寸比に追従（黒帯ゼロ）。ヘッダに **📺切替**（=setCameraOverride、● 表示中）/ 上に **📷1枚・⏺録画（=合成済み = Quest と同じ絵→`recordings/`、cam=`A_quest`）** + **演出 ON/OFF**+fade秒（cue.fadeIn/Out へ）。デモ用 1 画面密度レイアウト
- **③ 画像加工**: カメラ別画質（7 スライダ → `cameras[i].post`）＋ **合成素材**（captures/ から select / ↻ / 📂 / 動画は再生区間 trim）→ 💾 cue 保存（1 カメラ 1 cue: id=`cue_<camId>`、loop なし・再生終了で自動復帰、URL は percent-encode）。**未保存のまま演出 ON は警告**
- **② マスク**: 白黒境界を**スライダ調整**（白の向き 左/右/上/下 ＋ 位置スライダ ＋ 取り消し）。フリーハンド系は撤去
- **① 生リアルタイム映像（最下段）**: 生 MJPEG を `<img.raw-live>` で直接表示（加工前。この img を GL テクスチャ源にも兼用＝MJPEG 接続は 1 本のまま）。上に **📷1枚・⏺録画（=生フレーム→`recordings/`、cam=`A`）** + 配信元 IP/port/auth
- **キャプチャ 2 系統**: ① 生映像の上＝生フレーム / ④ 実映像の上＝合成済み（ビデオデモ用に「実際に Quest で見える絵」を録れる）。合成済み静止画のため `gl.js` は `preserveDrawingBuffer: true`（false だと toBlob が空になる）
- **生成プロンプト**: 画像/動画 AI プロンプトを 📋コピー/編集/削除（既存 `/prompts`=prompts.json）

実装: `index.html` ＋ `app.js`（全配線、ESM）＋ `pipeline.js`（合成）＋ `gl.js`/`shaders.js`。Unity 側対向は `ShowControlClient`（long-poll + 端末キャッシュ）/ `ScreenOverlayController`（cue 再生・動画はローカル DL）。**境界ブレンドの色統計/ラプラシアンは Web プレビュー専用**（Quest はハード合成、フェザーのみ PNG 焼き込みで効く）。

**デザイン言語（cogni-storage 準拠）**: 純黒 `#000` + radial-gradient / 文字 `#fffaf0` / アクセント `#ffdead` / **角丸ゼロ** / 透明カード + 1px 罫線（`--line-soft`）/ UPPERCASE マイクロラベル / 下線型インプット。トークンは style.css の `:root` に集約。

show.json / masks/ / captures/ は gitignore（ローカル運用状態）。

**show.json は Web ↔ Quest 実機の共有設定契約（2026-06-16 拡張）**: `cameras[i]` の `host/port/auth` を
Unity 実機が読む（接続先を Web から差し替え・DHCP ズレ復旧）。`cameras[i].post`（任意）= カメラ別画像加工で、
未設定なら global `post` にフォールバック。受信内容は実機が `persistentDataPath/show_config.json` にキャッシュし
**PC 不在でも前回設定で起動**（焼き込み .asset < 端末キャッシュ < ライブ long-poll の後勝ち）。console タブの
カメラカードに「🎨 画像加工」、multicam.html の IP も localStorage → show.json に一本化。詳細は
rules/streaming.md「show.json = 設定契約」/ plans/2026-06-16_web-config-to-quest.md。

## 場所と起動

- パス: `tools/web-compositor/`（Unity の Assets 外。Unity は読み込まない）
- 起動: `tools/web-compositor/serve.ps1`（python 必須）→ `http://localhost:8099/index.html`
  - **必ず capture-server.py 経由で起動**（素の `http.server` だと保存系 API が無くダウンロードにフォールバックする）
  - LAN からも見える（`http://<PC-IP>:8099/`）。スマホ/Quest 内ブラウザ確認用
  - getUserMedia(Webcam) は localhost か https のみ
- バックグラウンドの `python capture-server.py` はセッション跨ぎで落ちやすい → ユーザに `serve.ps1` 常駐を勧める

## 構成ファイル（2026-06-16 統合後）

| ファイル | 役割 |
|---|---|
| `index.html` / `style.css` | 1 ページ統合 UI（縦割りマトリクス） |
| `app.js` | 全配線（status / カメラ列 / IP・画質 / マスク / 合成素材 / cue / 📷）。show.json を正に I/O |
| `gl.js` / `shaders.js` | WebGL2 ヘルパー / GLSL（**ビューの最終 post は `FS_POST`** = Unity ScreenComposite と数式・順序一致。合成は pipeline.js。旧 `FS_VIEW` は未使用かつ式が古かったので 2026-06-18 削除） |
| `capture-server.py` | ローカルサーバ（静的配信 + show 制御 + /cam プロキシ + 保存 API） |
| `serve.ps1` | 起動スクリプト |
| `sim.html` / `sim.js` | Unity なしで動作確認する仮想 Quest（show.json long-poll） |

**撤去済み（2026-06-16）**: `main.js` / `sources.js` / `cue-editor.js` / `console.js` / `multicam.html`（プロンプト・ギャラリー・2 タブ・別ページ合成エディタ）。**⚠ `pipeline.js`（色統計マッチング→ラプラシアン）はユーザー要望で復活し現役**（app.js が import、境界ブレンドバーが駆動）。以降の「合成パイプライン」節は現役の説明として読む。AI 動画生成の知見は末尾に残す。

## 合成パイプライン（pipeline.js）

1. **ソース2系統**: UI 上は「⬜白スロット / ⬛黒スロット」。マスク白→白スロット、黒→黒スロットが出る（内部変数は liveSrc/preSrc = white/black）。運用イメージは **黒=AI生成動画 / 白=リアルタイム配信**。
2. **色統計マッチング**（Reinhard per-channel、GPU リダクションで平均/分散）
3. **ラプラシアンピラミッドブレンディング**（half-float RT、Burt-Adelson、境界を周波数帯ごとに馴染ませる）
4. **後段フィルタ**: 露出/コントラスト/彩度/色温度/ヴィネット/グレイン/色収差/走査線
- マスク: 分割プリセットは**境界をドラッグで移動**（スプリットモード）、矩形/円/全/ブラシ手描きはペイントモード。フェザーあり。

## キャプチャ / 録画（全て PC 内に保存・スマホには書かない）

- **📷 配信をキャプチャ**（静止画 JPEG）/ **⏱ 3秒後にキャプチャ**（セルフタイマー）/ **⏺ 配信を録画**（webm、MJPEG を canvas 経由で MediaRecorder）
- 保存先 = **`tools/web-compositor/captures/`**（`.gitignore` 済み）。`/save?type=image|video` に POST
- **💾 保存済み（PC内）** ギャラリー: 一覧表示、各々「白/黒に接続」で再利用、**サムネ/📂ボタンで保存場所をエクスプローラーで開く**（`/reveal?name=` → `explorer /select`）

## 生成プロンプト管理

- **🎬 生成プロンプト**: PC 内 `prompts.json`（`.gitignore` 済み）に保存。`GET/POST /prompts`, `POST /prompts/delete`
- **種別 `kind`**: 画像生成 / 動画生成 で分類・グループ表示。雛形ボタンは種別連動
- コピー（LAN http で Clipboard 不可なら execCommand フォールバック）/ 編集 / 削除

## サーバ API（capture-server.py）

| メソッド | パス | 用途 |
|---|---|---|
| GET | `/captures/list` | 保存物一覧（新しい順） |
| POST | `/save?type=image\|video` | body のバイナリを captures/ に保存 |
| GET | `/reveal?name=<file>` | captures/ の当該ファイルをファイルマネージャで選択表示（Win=explorer /select、mac=open -R、Linux=xdg-open）。パストラバーサル拒否 |
| GET | `/cam?host=&port=&path=&auth=user:pass` | **MJPEG プロキシ**。Basic 認証をサーバが肩代わり（ブラウザは `<img>` の URL 埋め込み認証をブロックするため iPhone/IP Camera Lite はこれ必須）。`multicam.html`（3 台同時ビュー、IP 編集可・localStorage 保存・自動再接続・クリック拡大）とコンソールのカメラカードが利用 |

### multicam.html の合成エディタ（2026-06-12、3 段マトリクス構成）

**列=カメラ、段=Live / Mask（白黒） / Composite（合成プレビュー）+ 最下段に cue 保存**：

- Mask 段は白黒画像を直接描画（矩形 / ブラシ / 消し / 半分プリセット / 反転 / クリア）。**白=差し替え**（Unity `_MaskTex` と同じ向き。コンポジット検証タブと違い**反転不要**）。実体 640x360、`POST /masks` に PNG 保存
- Composite 段は「ライブ contain + 素材を枠全面に引き伸ばし → マスク白領域だけ destination-in で残して重ねる」をブラウザ 2D canvas でリアルタイム合成（Unity ScreenComposite と同モデル）
- cue スキーマは cue-editor.js と同一（{id, name, camera, maskUrl, sourceUrl, strength, loop, fadeIn, fadeOut}）。camera id は列 index → /state cameras[i].id
- **罠 3 つ（踏んだ）**: ① 別ポートの /cam を canvas に描くと taint → live `<img>` に `crossOrigin='anonymous'` 必須（ACAO:* は送信済み）。② 合成ループに RAF を使うと非表示タブで完全停止 → setInterval を使う。③ マスク可視化に destination-in は不可（不透明黒が残り全面黒被り）→ 輝度→alpha 変換で

### ストリーミングの安定性（2026-06-12 の教訓・重要）

- **`/cam` は メインポート+1（既定 8100）で別 listen**（capture-server.py が両ポート同時起動、JS は `location.port+1` を自動算出）。MJPEG は接続を張りっぱなしにするため、同一オリジンに同居させると**ブラウザの同時接続上限（6/origin）**を食い潰し、/state long-poll や他カメラの接続が詰まる＝「接続が不安定」になる
- **console.js のカメラカードは差分更新**（`camCards` Map で `<img>` を保持）。innerHTML 全再構築にすると state 更新のたびに全 MJPEG が再接続して「1 個繋ぐと全部暗転」が再発する。ストリーム URL は `camStreamKey`（host|port|auth）が変わった時だけ張り替えること
| GET/POST | `/prompts` | プロンプト一覧 / 新規・更新（kind 含む） |
| POST | `/prompts/delete` | プロンプト削除 |
- 全レスポンスに CORS `*` と `Cache-Control: no-store`（開発中キャッシュ無効化）

## 姉妹リポ streamer 側の前提（要再ビルド済み）

[fixed-cam-streamer](https://github.com/Roiril/fixed-cam-streamer) を本ツール用に拡張（**別リポ・別途コミット要**）:
- `HttpServer.kt`: `/video` に **`Access-Control-Allow-Origin: *`** 追加（無いと WebGL が cross-origin MJPEG をテクスチャ化できない）
- `FrameRecorder.kt` + `/record/*` エンドポイント（スマホ側録画）。ただし本ツールは録画を **PC 側に移行済み**なので現状 UI からは未使用

## AI 動画生成の運用知見（貞子系・無血ホラー）

- Google **Flow/Veo は最も厳しい**。`horror/scary/ghost/blood/burial/attack/toward the camera` 等で弾かれる
- 通すコツ: **怖さは静止画に入れ、動画プロンプトは“演劇・民俗・夢”の語で包む**。画像→動画にして動きの指示を穏やかに
- 怖くするコツ: 綺麗な動きはNG。**「静止→異常な一拍」+ 違和感(首が傾きすぎ/片目の開示) + こちらを注視**。走らせるとシュール→**瞬間移動（モニタの陰で移動を隠す）**が J-ホラー的で AI も破綻しにくい
- 寛容な代替: **Kling**（image-to-video が緩い）、**ローカル Wan 2.2 / VACE**（無検閲・背景保持の video inpainting）
- 確実に瞬間移動させるなら **2クリップ分割（遠くで出現 / 机奥で停止）→繋ぐ**
