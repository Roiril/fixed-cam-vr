# web compositor — 廻リ視 のマルチカメラ設定 + オペレータ卓（1 ページ統合）

ブラウザから廻リ視（FixedCam）のカメラを**縦割り（列＝カメラ）**で設定・監視する 1 ページ UI。
状態の正は `show.json`（このサーバ）。Unity（Quest 実機）は long-poll で追従し、端末ローカルにキャッシュして
**PC 不在でも前回設定で起動**する（設計 [.claude/plans/2026-06-16_web-config-to-quest.md](../../.claude/plans/2026-06-16_web-config-to-quest.md)）。

2026-06-16 に旧 3 画面（コンソール / コンポジット検証 / 合成エディタ）を**この 1 ページへ統合**。
境界ブレンド（色統計マッチング・ラプラシアン・フェザー）と生成プロンプト管理はユーザー要望で**復活**させた。

## 画面構成（縦割り = カメラ列。各列は下→上の加工フロー）

各列は配信の流れと同じ向き（下＝入力 → 上＝Quest 出力）に積む。**📷/⏺ キャプチャは 2 か所**：

```
ステータス  … Unity 生存 / アクティブカメラ / fps / 反映 rev / server / ⏺全カメラ録画(生)/(Quest) / 🚶ゾーン自律 / 📂撮影フォルダ
境界ブレンド … フェザー / 色統計 ON+強度 / ラプラシアン ON+レベル（全カメラ共通）
マルチカメラ（列 = カメラ A / B / C）
  ④ メタクエスト実映像 … 画質+合成を当てた最終見た目（Quest と同じ絵）
                         + 📷1枚 / ⏺録画（=合成済みを recordings/）/ 演出 ON/OFF + fade秒。ヘッダに 📺切替
  ③ 画像加工           … カメラ別 画質 7 スライダ（露出/コントラスト/彩度/色温度/ヴィネット/グレイン/走査線）
                         + 合成素材（captures/ 選択・↻・📂・動画は再生区間 trim）→ 💾 cue 保存
  ② マスク領域調整     … 白黒の境界をスライダ調整。白の向き（左/右/上/下）＋位置＋取り消し（白=差し替え）
  ① 生リアルタイム映像 … 生 MJPEG を直接表示（加工前）
                         + 📷1枚 / ⏺録画（=生フレームを recordings/）/ 配信元 IP・port・認証
下部: 生成プロンプト … 画像/動画 AI 生成プロンプトを 📋コピー / 編集 / 削除（prompts.json）
```

- **① 生映像の 📷/⏺** … 加工・合成なしの**生フレーム**（ファイル名 `camA_…`）
- **④ 実映像の 📷/⏺** … 画質+合成を当てた**Quest と同じ絵**（ファイル名 `camA_quest_…`）。ビデオデモ用
- **ヘッダの ⏺全カメラ録画(生)/(Quest)** … 全列を**同時に**録画開始 / もう一度押すと全停止。各列のタグ + ほぼ同一タイムスタンプで recordings/ へ（ビデオデモで全アングルを一括収録）。ライブ未到達の列は生録画をスキップ（graceful）
- ビュー（④）はスライダを動かすと**その場で反映**（画質も境界ブレンドも見える）
- 合成済み静止画を撮るため WebGL は `preserveDrawingBuffer: true`（[gl.js](gl.js)）

## show.json = 設定契約（Quest が参照）

設定・マスク・合成素材の編集はすべて `show.json` に保存され、Unity（`ShowControlClient`）が読む：

| メソッド | パス | 用途 |
|---|---|---|
| GET | `/state?rev=N` | show.json（rev > N まで最大 25s ブロックの long-poll） |
| POST | `/state` | cameras（host/port/auth/post）/ cues / post / control の部分更新 |
| POST | `/command` | `playCue` / `stopCue` / `setCameraOverride` / `setPost` |
| POST | `/masks?name=` | マスク PNG 保存 → `/masks/<name>.png` 配信 |
| GET | `/cam?host=&port=&path=&auth=` | MJPEG プロキシ（Basic 認証肩代わり。別ポート 8100 で listen） |
| POST | `/save?type=image\|video&to=recordings&cam=A` | 📷 静止画 / ⏺ 録画の保存（`to=recordings` で撮影フォルダ、`cam` でファイル名接頭辞。生=`A` / 合成済み=`A_quest`） |
| GET | `/open-dir?dir=recordings` | 撮影フォルダをファイルマネージャで開く（📂 撮影フォルダ ボタン） |
| GET | `/captures/list` | 合成素材一覧（`captures/`、PC ローカル） |
| POST/GET | `/unity/heartbeat` / `/unity/status` | Unity の生存・アクティブカメラ報告 |

- `cameras[i].host/port/auth` を **Unity 実機が読む**（DHCP ズレを Web から復旧。変化時のみ再接続）
- `cameras[i].post`（任意）= カメラ別画質。未設定は global `post` にフォールバック
- `Assets/Settings/ShowServer.asset` の host をこの PC に向ける（Editor+Link は 127.0.0.1、Quest 単体は LAN IP）
- 詳細は [.claude/rules/streaming.md](../../.claude/rules/streaming.md)「show.json = 設定契約」

## 起動

```powershell
# このフォルダで（python 必須）
./serve.ps1
```

`http://localhost:8099/`。LAN からは `http://<PC-IP>:8099/`（スマホ/Quest 内ブラウザ確認用）。
MJPEG プロキシは `<メインポート+1>`（8100）で別 listen（同一オリジン 6 接続制限の回避。JS が自動算出）。

> **必ず `serve.ps1`（capture-server.py）経由で起動**すること。素の `http.server` だと show 制御 / 保存 API が無い。

## ファイル

| ファイル | 役割 |
|---|---|
| `index.html` / `style.css` | 1 ページ統合 UI（縦割りマトリクス） |
| `app.js` | 全配線（status / 境界ブレンド / カメラ列 / IP・画質 / マスク / 合成素材 / cue / 📷⏺ / プロンプト）。show.json を正に I/O |
| `pipeline.js` | ビューの合成パイプライン（取り込み→色統計マッチング→ラプラシアン→ポスト FX、half-float RT 多パス） |
| `gl.js` / `shaders.js` | WebGL2 ヘルパー / GLSL 全シェーダ |
| `capture-server.py` | ローカルサーバ（静的配信 + show 制御 + /cam プロキシ + 保存/プロンプト API） |
| `serve.ps1` | 起動スクリプト |
| `sim.html` / `sim.js` | Unity なしで動作確認する仮想 Quest（show.json を long-poll） |

`show.json` / `masks/` / `captures/`（合成素材）/ `recordings/`（📷 ⏺ 撮影物）/ `prompts.json` は PC ローカル運用状態のため `.gitignore` 済み。

## 既知の制約

- ビューはカメラ実寸比に追従（黒帯を出さない）。素材を演出 ON にすると合成プレビューが出る。
- 1 カメラ 1 cue（id = `cue_<camId>`）。素材+マスクを選び 💾 保存 → 演出 ON で発火。**未保存のまま演出 ON は警告のみ**。
- **境界ブレンド（色統計/ラプラシアン）は Web プレビュー専用**。Quest 実機は ScreenComposite のハード合成（フェザーだけは cue 保存時に PNG へ焼き込まれ Quest にも効く）。
- 動画 cue は Quest 側が UnityWebRequest でローカル DL してから再生する（Android ネイティブの HTTP ストリーミング相性問題回避。詳細 [TROUBLESHOOTING / rules/streaming.md](../../.claude/rules/streaming.md)）。
- 合成素材は既存 `captures/` から選ぶ（📷 でその場保存も可）。AI 生成素材は外部で用意して `captures/` に置く。スペース入りファイル名も URL エンコードで対応。
