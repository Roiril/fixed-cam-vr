# web compositor（tools/web-compositor/）

**オペレータ卓（Unity 遠隔制御）+ 合成のブラウザ検証**の 2 タブ構成（2026-06-11 T4 で再編）。

**デザイン言語（2026-06-11 ユーザー指定・cogni-storage 準拠）**: 純黒 `#000` + radial-gradient / 文字 `#fffaf0` / アクセント `#ffdead` / **角丸ゼロ** / 透明カード + 1px 罫線（`--line-soft`）/ UPPERCASE マイクロラベル / 下線型インプット。トークンは style.css の `:root` に集約、multicam.html も同トークン。新 UI 追加時はこれに従う（JS でのインライン色指定は `var(--accent-color)` 等を使う）。

- **🎛 コンソール**（console.js）: show.json を正として Unity を遠隔制御。cue 発火/停止・カメラ手動固定・ポスト FX ライブ調整・Unity heartbeat 表示。Unity 側対向は `ShowControlClient`（long-poll）。API: `/state`(long-poll) `/command` `/masks` `/unity/*`。計画: `.claude/plans/2026-06-11_web-operator-console.md`
- **✂ コンポジット検証**（main.js、従来機能）: 「事前撮影映像 × リアルタイム配信」をマスクで切り貼り→合成跡を消す画像処理→フィルタ、までを WebGL2 で実装。AI 動画生成（貞子系）の素材づくり・プロンプト管理もここに集約。

show.json / masks/ は gitignore（ローカル運用状態、prompts.json と同様）。

## 場所と起動

- パス: `tools/web-compositor/`（Unity の Assets 外。Unity は読み込まない）
- 起動: `tools/web-compositor/serve.ps1`（python 必須）→ `http://localhost:8099/index.html`
  - **必ず capture-server.py 経由で起動**（素の `http.server` だと保存系 API が無くダウンロードにフォールバックする）
  - LAN からも見える（`http://<PC-IP>:8099/`）。スマホ/Quest 内ブラウザ確認用
  - getUserMedia(Webcam) は localhost か https のみ
- バックグラウンドの `python capture-server.py` はセッション跨ぎで落ちやすい → ユーザに `serve.ps1` 常駐を勧める

## 構成ファイル

| ファイル | 役割 |
|---|---|
| `index.html` / `style.css` | UI |
| `main.js` | ソース/マスク/UI 配線 + メインループ + キャプチャ/録画/プロンプト |
| `sources.js` | 内蔵パターン / Webcam / 動画 / MJPEG / マスク canvas |
| `pipeline.js` | 合成パイプライン（取り込み→色統計→ラプラシアン→ポスト） |
| `gl.js` / `shaders.js` | WebGL2 ヘルパー / GLSL 全シェーダ |
| `capture-server.py` | ローカルサーバ（静的配信 + 保存/一覧/reveal/プロンプト API） |
| `serve.ps1` | 起動スクリプト |

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

### multicam.html の ✂ 合成エディタ（2026-06-12 追加）

各カメラパネルの「✂ 合成」で、ライブ映像の上に直接マスクを描いて cue 登録できる：

- 素材選択（captures/ 一覧 or URL 直書き）+ マスク描画（矩形 / ブラシ / 消し / 半分プリセット / 反転 / クリア）+ cue 名・ループ・フェード → 💾 cue 保存
- **マスクは白=差し替えで直接描く**（Unity `_MaskTex` と同じ向き。コンポジット検証タブと違い**反転不要**）。実体 640x360、`POST /masks` に PNG 保存
- cue スキーマはコンポジット検証タブの cue-editor.js と同一（{id, name, camera, maskUrl, sourceUrl, strength, loop, fadeIn, fadeOut}）。camera id はパネル index → /state cameras[i].id で対応付け
- マスク可視化はマスク輝度→alpha 変換でアクセント色 45% 表示（destination-in 方式は不透明黒が残って全面黒被りするので不可 — 一度踏んだ）

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
