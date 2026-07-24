# web compositor — 廻リ視 のタイムライン・オーサリング卓 + マルチカメラ監視（1 ページ統合）

ブラウザから廻リ視（FixedCam）の体験を**タイムライン（周回×ゾーン区間）でオーサリング**し、カメラを監視する 1 ページ UI。
状態の正は `show.json`（このサーバ）。Unity（Quest 実機）は long-poll で追従し、端末ローカルにキャッシュして
**PC 不在でも前回設定で起動**する（設計 [.claude/plans/2026-06-16_web-config-to-quest.md](../../.claude/plans/2026-06-16_web-config-to-quest.md)）。

2026-07-19 に**タイムライン第一級**へ再設計（旧: カメラ列の下に cue がぶら下がる構造）。cue オーサリングをカメラ列から
切り出し、区間 1 個から cue・画像加工 post・インサートショットを一括編集できるようにした
（設計 [.claude/plans/2026-07-19_webui-timeline-authoring.md](../../.claude/plans/2026-07-19_webui-timeline-authoring.md)）。

## モード（上部ナビで切替）

- **タイムライン（オーサリング）** … 体験の事前焼き込みを作る正面。下記
- **ライブ（運用）** … 本番前チェック・ラン状態・緊急操作・発見済み端末・疎通診断・cue 手動発火

## 本番運用の安全装置（2026-07-25）

ショー中に「壊れていることが見えない」「前の体験者の状態が残る」を潰すための仕掛け。

| 仕掛け | 何を防ぐか |
|---|---|
| **ヘッダの 2 灯**（サーバ ● / Unity ●） | 卓サーバ（capture-server.py）が落ちても画面が最後の絵で生き続ける事故。断なら赤帯 + 画面をトーンダウン + 最終応答からの経過秒 |
| **ラッチ警告バー**（ヘッダ直下・両モード） | `control.cameraOverride`（カメラ固定）/ `activeCue`（演出再生中）が残ったまま次の体験者を始めること。**show.json 由来なので Unity 未接続の準備段階でも見える**。⛑ ワンクリックで両方解除 |
| **▶ ラン開始 = フルリセット** | runEpoch++ に加えて**カメラ固定解除 + 演出停止**を同時に行う（旧: runEpoch のみ） |
| **✅ 本番前チェック**（ライブ・3 秒更新） | サーバ / Quest heartbeat + 同期 / カメラ受信 / **配信元の重複**（2 ゾーンが同じ IP＝同じ映像）/ **接続先の同一性** / ラッチ / タイムライン / 参照 cue の実在・素材 / 未保存 を 1 枚に集約 |
| **接続先の同一性照合**（PC が `/info` を 10 秒毎） | DHCP のリース移動や端末側の cameraId 付け替えで、**slot が別スロットの端末を掴んだまま HTTP 200・MJPEG も流れる**（＝ LIVE 判定が通ってしまう）事故。`cameraId` / `show` を突き合わせ、不一致ならカメラ列と本番前チェックに赤で出す。**警告のみ**で映像は止めず host も書き換えない（自動追従は beacon 経由だけ）。キルスイッチ `FIXEDCAM_IDCHECK=0` |
| **緊急操作の集約** | ▶ ラン開始・🚶 ゾーン自律へ戻す・■ 演出を止める をライブの 1 箇所に大きく（🚶 はヘッダから移設） |
| **ライブ中の画質ロック 🔒** | 本番中に画質スライダへ触れて体験者の見え方が変わる事故（ライブモードで既定 ON・🔓 で一時解除） |
| **未保存の可視化 + ガード** | ヘッダの `● 未保存` バッジ（ライブでも見える）/ タブを閉じる前の確認 / **未保存のまま 📦 エクスポートを実行させない** |
| **⟲ 元に戻す（Ctrl+Z）** | タイムラインの直前編集を 1 手戻す（30 手）。`－ 周回` は区間が残っている時だけ確認ダイアログ |
| **show.json の 1 世代バックアップ** | 保存のたびに `show.json.bak` を残し、tmp → `os.replace` で書く（書き込み中の電源断でも壊れない）。復旧は .bak をリネームしてサーバ再起動 |
| **📦 エクスポート結果に焼き込み先を表示** | 「いつ・rev いくつ・どの IP で焼いたか」+ 参照先が無い cue の警告（現地 DHCP の IP が APK に verbatim で入るため） |

## タイムライン・モードの画面構成

```
タイムライン … 周回（行）× ゾーン区間（course.order 順に横連結）。区間クリックで下にインスペクタ
  ▶ 検証     … 矢印キー（→ 次ゾーン / ← 戻る / R 先頭 / Esc）で発火順と「見える画」をローカル確認（show.json は書かない）
  区間インスペクタ（クリックで展開）
    ・cue（進入 + 遅延で発火・複数可）… 既存 cue 割当 or ＋新規 cue 作成（素材 + マスク + フェード + trim）
                                        + 遅延秒 / 1 回のみ / 強度・フェード・trim の区間上書き
    ・🎨 このゾーンの画像加工 post 上書き … 7 スライダ（segment > camera > global の 3 段解決）
    ・⏢ インサートショット … 別カメラを N 秒差し込む（退出時 / 進入時・カメラ・秒・cue・1 回・insert 中 post）
    ・🎵 BGM … この区間で「▶ 曲を切り替える / ■ 止める / ― そのまま」。ループ範囲 in-out・音量・
               フェード・頭出し。試聴しながら「ここを in / ここを out」で範囲を取れる
  🎵 BGM 帯（区間セルの下）… その区間で鳴っている曲を carry-forward で表示。▶=切替 / ≡=同じ曲のまま調整 / ■=停止
BGM ライブラリ … audio/ の音源をトラック登録（ループ範囲・音量・試聴）+ ラン既定 BGM の選択
フロアマップ … 担当カメラをタイル塗り + 周回コース（スタート/CW-CCW）+ 位置合わせ点
エクスポート … 📦 ビルド用エクスポート（show.json + 参照アセットを StreamingAssets/show/ へ焼き込み）
生成プロンプト … 画像/動画 AI 生成プロンプトを 📋コピー / 編集 / 削除（prompts.json）
マルチカメラ（常時・監視専用。列 = カメラ A / B / C。ライブ時はアクティブ列を大きく表示）
  ④ メタクエスト実映像 … 画質を当てた最終見た目（Quest と同じ絵）+ 📷1枚 / ⏺録画。ヘッダに 📺切替
  ③ 画像加工           … カメラ別 画質 7 スライダ（カメラ単位グレーディング。cue 編集は撤去 → タイムラインへ移設）
  ① 生リアルタイム映像 … 生 MJPEG + 📷1枚 / ⏺録画 / 配信元 IP・port・認証
```

- カメラ列は**監視専用に格下げ**（生映像・Quest 実映像・接続・録画・カメラ単位 post）。**cue 作成はタイムラインの区間インスペクタ**で行う
- ライブ手動 cue 発火（演出 ON/OFF → `control.activeCue`）は**ライブ・モード**へ移設
- **① 生映像の 📷/⏺** … 生フレーム（`camA_…`）／ **④ 実映像の 📷/⏺** … Quest と同じ絵（`camA_quest_…`）。ビデオデモ用
- 合成済み静止画を撮るため WebGL は `preserveDrawingBuffer: true`（[gl.js](gl.js)）

## 🎵 BGM オーサリング（2026-07-25）

従来の BGM は「[Bgm] の AudioSource が 1 曲を起動中ずっとループ」だけだった。これを
**タイムラインの区間（周回×ゾーン）で切り替え・停止・ループ範囲指定できる**ようにした。

1. `tools/web-compositor/audio/` に音源（mp3 / ogg / wav / m4a）を置く
2. 「🎵 BGM ライブラリ」で **＋ トラックに登録** → 表示名・ループ in/out・音量を設定（🔊 試聴しながら
   「ここを in / ここを out」で範囲を取れる）
3. タイムラインの区間をクリック → **🎵 このゾーンで BGM を切り替える / 止める** で指示を書く
4. **ラン既定 BGM** = 体験開始時に鳴っている曲（未設定なら APK 同梱の既定クリップ = 従来の曲）
5. 📦 エクスポートで音源も `sa://assets/` へ焼き込まれる（現地 PC 不在でも鳴る）

セマンティクス（動画編集のオーディオトラックと同じ感覚）:

- **指示の無い区間では曲が途切れず続く**（区間ごとに鳴り直さない）
- **同じトラックを指す区間は再生位置を保ったままループ範囲・音量だけ更新**（`retune`）。
  「同じ曲のまま 2 周目だけループ範囲を狭める」が書ける
- **別トラックはクロスフェード**、**停止はフェードアウト**
- ラン開始（▶ ラン開始 / 右グリップ長押し）で**ラン既定 BGM へ戻る**
- 区間の値 `-1` は「トラック既定を継承」

> **⚠ Unity 側は `Tools/FixedCamVr/Setup/Setup Main Demo Scene` を 1 回再実行**して [Bgm] を
> BgmDirector 化する必要がある（旧 AudioSource だけのシーンでは BGM 指示が無視され、従来の
> 固定ループがそのまま鳴り続ける＝安全側に縮退）。

## show.json = 設定契約（Quest が参照）

設定・マスク・合成素材の編集はすべて `show.json` に保存され、Unity（`ShowControlClient`）が読む：

| メソッド | パス | 用途 |
|---|---|---|
| GET | `/state?rev=N` | show.json（rev > N まで最大 25s ブロックの long-poll） |
| POST | `/state` | cameras（host/port/auth/post）/ cues / post / control / layout / schedule / **timeline** の部分更新 |
| POST | `/command` | `playCue` / `stopCue` / `setCameraOverride` / `setPost` |
| POST | `/masks?name=` | マスク PNG 保存 → `/masks/<name>.png` 配信 |
| GET | `/cam?host=&port=&path=&auth=` | MJPEG プロキシ（Basic 認証肩代わり。別ポート 8100 で listen） |
| POST | `/save?type=image\|video&to=recordings&cam=A` | 📷 静止画 / ⏺ 録画の保存（`to=recordings` で撮影フォルダ、`cam` でファイル名接頭辞。生=`A` / 合成済み=`A_quest`） |
| GET | `/open-dir?dir=recordings` | 撮影フォルダをファイルマネージャで開く（📂 撮影フォルダ ボタン） |
| GET | `/captures/list` | 合成素材一覧（`captures/`、PC ローカル） |
| POST/GET | `/unity/heartbeat` / `/unity/status` | Unity の生存・アクティブカメラ報告（**卓サーバ生存判定もこの 2 秒ポーリングが担う**。`/state` は long-poll で最大 25s ブロックするため断の検知に使えない） |
| POST | `/export-build` | 焼き込み。レスポンスに `exportedAt` / `showRev` / `hosts[]`（焼き込んだカメラ接続先）/ `missingCues[]` を含む |

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
| `index.html` / `style.css` | 1 ページ統合 UI（タイムライン主面 + 監視カメラ列） |
| `app.js` | 全配線（status / モードナビ / カメラ列監視 / IP・画質 / 録画 / ライブ運用 / プロンプト）。show.json を正に I/O |
| `timeline.js` | **タイムライン第一級オーサリング**（周回×ゾーン区間・区間インスペクタ・矢印キー検証。旧 schedule.js 後継） |
| `cue-editor.js` | cue 編集器（マスク canvas + 素材 + trim + フェード + strength + 💾保存）。区間インスペクタが埋め込む |
| `composite-view.js` | WebGL 合成プレビュー（カメラ列 ④ と検証モードで共用） |
| `common.js` | 共通小物（FX 定義 / getState/postState / cue 保存 / captures / mediaCache 等） |
| `floormap.js` | フロアマップ（タイル塗り + 周回コース + 位置合わせ点） |
| `pipeline.js` | 合成パイプライン（取り込み→色統計マッチング→ラプラシアン→ポスト FX、half-float RT 多パス） |
| `gl.js` / `shaders.js` | WebGL2 ヘルパー / GLSL 全シェーダ |
| `capture-server.py` | ローカルサーバ（静的配信 + show 制御 + /cam プロキシ + 保存/プロンプト/エクスポート/discovery API） |
| `serve.ps1` | 起動スクリプト |
| `sim.html` / `sim.js` | Unity なしで動作確認する仮想 Quest（show.json を long-poll。schedule 発火再現は未対応 → タイムラインの ▶ 検証を使う） |

`show.json` / `masks/` / `captures/`（合成素材）/ `recordings/`（📷 ⏺ 撮影物）/ `prompts.json` は PC ローカル運用状態のため `.gitignore` 済み。

## 既知の制約

- ビューはカメラ実寸比に追従（黒帯を出さない）。
- cue は `cues[]`（トップレベル・カメラ帰属は文字列 id）。1 カメラ複数 cue（id = `cue_<camId>_<n>`）。**タイムライン区間のインスペクタで割当 or その場で新規作成**。素材未選択で保存 or 未保存割当は警告。
- **周×カメラ区間ごとに** cue パラメータ上書き・画像加工 post 上書き・インサートショットを持てる（`timeline.segments[]`。詳細 [rules/streaming.md](../../.claude/rules/streaming.md) の「タイムライン第一級オーサリング」）。
- **境界ブレンド（色統計/ラプラシアン）は Web プレビュー専用**。Quest 実機は ScreenComposite のハード合成（フェザーだけは cue 保存時に PNG へ焼き込まれ Quest にも効く）。
- 動画 cue は Quest 側が UnityWebRequest でローカル DL してから再生する（Android ネイティブの HTTP ストリーミング相性問題回避。詳細 [TROUBLESHOOTING / rules/streaming.md](../../.claude/rules/streaming.md)）。
- フロアマップの ◇ HMD 位置は heartbeat の `headCourseX` / `headCourseZ` / `currentZone` を読む（**2026-07-25 修正**。旧実装は `hmdX` / `hmdZ` / `zoneLabel` という存在しないキーを見ていたため、Unity が繋がっていても ◇ が一度も出なかった）。
- 合成素材は既存 `captures/` から選ぶ（📷 でその場保存も可）。AI 生成素材は外部で用意して `captures/` に置く。スペース入りファイル名も URL エンコードで対応。
