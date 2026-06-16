# web compositor — 廻リ視 のマルチカメラ設定 + オペレータ卓（1 ページ統合）

ブラウザから廻リ視（FixedCam）のカメラを**縦割り（列＝カメラ）**で設定・監視する 1 ページ UI。
状態の正は `show.json`（このサーバ）。Unity（Quest 実機）は long-poll で追従し、端末ローカルにキャッシュして
**PC 不在でも前回設定で起動**する（設計 [.claude/plans/2026-06-16_web-config-to-quest.md](../../.claude/plans/2026-06-16_web-config-to-quest.md)）。

2026-06-16 に旧 3 画面（コンソール / コンポジット検証 / 合成エディタ）を**この 1 ページへ統合**。
人間が触らない検証機能（色統計マッチング・ラプラシアン・プロンプト管理・ギャラリー）は撤去した。

## 画面構成（縦割り = カメラ列）

```
ステータス  … Unity 生存 / アクティブカメラ / fps / 反映 rev / server
マルチカメラ（列 = カメラ A / B / C）
  ビュー      … 画質を当てた最終見た目（Unity ScreenComposite を WebGL で再現＝Quest と同じ絵）
                + ▶発火 / ⏹停止 / 🔒固定 / 📷保存
  設定        … IP / port / 認証 ＋ カメラ別 画質（露出・コントラスト・彩度・色温度・ヴィネット・グレイン・走査線）
  マスク      … 白黒で差し替え領域を指定（白 = 差し替え）。矩形 / ブラシ / 消し / 半分 / 反転 / クリア
  合成素材    … captures/ から画像 or 動画を選択（or URL）→ 💾 cue 保存
```

ビューはスライダを動かすと**その場で反映**する（画質をいじった結果が見える）。

## show.json = 設定契約（Quest が参照）

設定・マスク・合成素材の編集はすべて `show.json` に保存され、Unity（`ShowControlClient`）が読む：

| メソッド | パス | 用途 |
|---|---|---|
| GET | `/state?rev=N` | show.json（rev > N まで最大 25s ブロックの long-poll） |
| POST | `/state` | cameras（host/port/auth/post）/ cues / post / control の部分更新 |
| POST | `/command` | `playCue` / `stopCue` / `setCameraOverride` / `setPost` |
| POST | `/masks?name=` | マスク PNG 保存 → `/masks/<name>.png` 配信 |
| GET | `/cam?host=&port=&path=&auth=` | MJPEG プロキシ（Basic 認証肩代わり。別ポート 8100 で listen） |
| GET | `/captures/list` ・ POST `/save?type=image` | 📷 保存物の一覧 / 保存（`captures/`、PC ローカル） |
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
| `app.js` | 全配線（status / カメラ列 / IP・画質 / マスク / 合成素材 / cue / 📷）。show.json を正に I/O |
| `gl.js` / `shaders.js` | WebGL2 ヘルパー / GLSL（ビューは `FS_VIEW` = ScreenComposite 移植） |
| `capture-server.py` | ローカルサーバ（静的配信 + show 制御 + /cam プロキシ + 保存 API） |
| `serve.ps1` | 起動スクリプト |
| `sim.html` / `sim.js` | Unity なしで動作確認する仮想 Quest（show.json を long-poll） |

`show.json` / `masks/` / `captures/` は PC ローカル運用状態のため `.gitignore` 済み。

## 既知の制約

- ビューは live を contain-fit（アスペクト維持・レターボックス）。Quest と同じモデル。
- 1 カメラ 1 cue（id = `cue_<camId>`）。💾 保存で上書き、▶ 発火で出る。
- 合成素材は既存 `captures/` から選ぶ（📷 でその場保存も可）。AI 生成素材は外部で用意して `captures/` に置く。
