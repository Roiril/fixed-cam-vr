# web compositor — オペレータ卓 + 固定視点合成の Web 検証ツール

2 つの顔を持つ：

- **🎛 コンソール（オペレータ卓）** — 体験中の **Unity（Quest）を遠隔制御**する本番運用 UI。カメラ状態の監視・手動固定、演出 cue の発火/停止、ポスト FX のライブ調整。状態の正は `show.json`（このサーバ）で、Unity は long-poll（`GET /state?rev=`）で追従する。実装計画: [.claude/plans/2026-06-11_web-operator-console.md](../../.claude/plans/2026-06-11_web-operator-console.md)
- **✂ コンポジット検証** — Unity を開かずにブラウザだけで **固定視点ホラーの映像合成**（視界の一部を事前映像へ差し替え）を試すプロトタイピング環境。WebGL2 でリアルタイム（30–60fps）。配信フレームのキャプチャ/録画と AI 動画生成プロンプトの管理も兼ねる。

## ショー制御 API（コンソールが使う / Unity が読む）

| メソッド | パス | 用途 |
|---|---|---|
| GET | `/state?rev=N` | show.json（rev > N まで最大 25s ブロックの long-poll） |
| POST | `/state` | cameras / cues / post / control の部分更新 |
| POST | `/command` | `playCue` / `stopCue` / `setCameraOverride` / `setPost` |
| POST | `/masks?name=` | マスク PNG 保存 → `/masks/<name>.png` 配信 |
| POST/GET | `/unity/heartbeat` / `/unity/status` | Unity の生存・アクティブカメラ報告 |

Unity 側の対向: `ShowControlClient`（long-poll 適用）+ `ScreenOverlayController`（URL ソースの cue 再生）。`Assets/Settings/ShowServer.asset` の host をこの PC に向ける（Editor+Link なら 127.0.0.1 のまま）。

## できること（コンポジット検証タブ）

1. **領域合成** — ⬜白スロット / ⬛黒スロットに映像を割り当て、マスクで「半分こっち/半分あっち」に切り貼り。マスク白→白スロット、黒→黒スロットが出る。運用イメージは **黒=AI生成動画 / 白=リアルタイム配信**。
2. **合成跡を消す処理**
   - **① 色統計マッチング** — Reinhard per-channel transfer（GPU リダクションで平均/分散）。
   - **② ラプラシアンピラミッドブレンディング** — Burt-Adelson、half-float RT で境界を周波数帯ごとに馴染ませる。
3. **後段フィルタ** — 露出 / コントラスト / 彩度 / 色温度 / ヴィネット / グレイン / 色収差 / 走査線。
4. **配信のキャプチャ / 録画（PC 内に保存）** — 静止画 📷 / 3秒タイマー ⏱ / 動画 ⏺。`captures/` に保存し、ギャラリーから再利用・**エクスプローラーで場所を開く**。
5. **生成プロンプト管理** — 画像生成 / 動画生成で分類して `prompts.json` に蓄積。コピー/編集/削除、種別連動の雛形。

## 起動

```powershell
# このフォルダで（python 必須）
./serve.ps1
```

`http://localhost:8099/index.html`。開くと **テスト(白) ＋ グラデ(黒) を左右合成**した状態で表示（カメラ不要）。LAN からは `http://<PC-IP>:8099/`（スマホ/Quest 内ブラウザ確認用）。

> **必ず `serve.ps1`（capture-server.py）経由で起動**すること。素の `http.server` だと保存/プロンプト API が無く、キャプチャはダウンロードにフォールバックする。getUserMedia(Webcam) は `localhost` か `https` のみ。

## 使い方の流れ

1. **ソース割り当て** — リアルタイム映像（配信スマホ MJPEG URL）を「白に接続」、AI生成動画やキャプチャを「黒に接続」等。`http://<phone>:8080/video` を URL 欄へ（streamer 側 `/video` の CORS 許可が前提）。
2. **マスク** — 分割プリセット（左右/上下…）は**境界をドラッグで移動**。矩形/円/全/ブラシ手描きも可。フェザーで境界をぼかす。
3. **跡消し** — ①②の ON/OFF と強度でつなぎ目の消え方を確認（`マスク境界をオーバーレイ` で領域可視化）。
4. **フィルタ** — 後段スライダーで質感。
5. **素材づくり** — 📷/⏱/⏺ で配信を PC に保存 → ギャラリーから黒/白に流し込み。
6. **プロンプト** — AI 動画/画像生成用プロンプトを種別ごとに保存・再利用。

## ファイル

| ファイル | 役割 |
|---|---|
| `index.html` / `style.css` | UI |
| `main.js` | ソース/マスク/UI 配線 + メインループ + キャプチャ/録画/プロンプト |
| `sources.js` | 内蔵パターン / Webcam / 動画 / MJPEG / マスク canvas |
| `pipeline.js` | 合成パイプライン（取り込み→色統計→ピラミッド→ポスト） |
| `gl.js` / `shaders.js` | WebGL2 ヘルパー / GLSL ES 3.00 全シェーダ |
| `capture-server.py` | ローカルサーバ（静的配信 + 保存/一覧/reveal/プロンプト API） |
| `serve.ps1` | 起動スクリプト |

`captures/`（保存物）と `prompts.json`（プロンプト）は PC ローカル成果物のため `.gitignore` 済み。

## サーバ API（capture-server.py）

| メソッド | パス | 用途 |
|---|---|---|
| GET | `/captures/list` | 保存物一覧（新しい順） |
| POST | `/save?type=image\|video` | body バイナリを `captures/` に保存 |
| GET | `/reveal?name=<file>` | ファイルマネージャで選択表示（Win=`explorer /select`） |
| GET/POST | `/prompts` | プロンプト一覧 / 新規・更新（`kind`=image/video） |
| POST | `/prompts/delete` | プロンプト削除 |

## Unity への移植メモ

ここで決めた **マスク形状・色統計強度・ピラミッドレベル・各フィルタ値** が、そのまま Unity 側（URP の Shader Graph / フルスクリーンパス）の実装パラメータになる。アルゴリズムは GLSL → HLSL でほぼ 1:1。`MjpegScreen` の描画後段に同じ 2 段（色統計 → ラプラシアン）を差し込む想定。

## 既知の制約

- ソースは fit（アスペクト維持・レターボックス）。`fit` OFF でストレッチ。
- 色統計は **per-channel RGB**（Lab/decorrelated は未実装）。
- ピラミッド downsample は 2x2 box（5-tap ガウシアンではない）。
