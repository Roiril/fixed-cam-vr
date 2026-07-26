# web compositor（tools/web-compositor/）

**2026-07-26 操作性の是正（実スクショを見ながら直した分）**:

- **操作する場所と結果が見える場所を離さない**。フロアマップ（ドラッグ）と 🕹 ショーシミュレーション（結果）は
  `.sim-row` で**横並び**にした（旧: 上下に離れていて、歩かせながら画面を見られなかった）
- **隠れた前提（モードの ON/OFF）を無くす**。マップ直上に 🖌 塗る / 📍 位置合わせ点 / 🚶 歩かせる の 3 択を置き、
  下の方にあったチェックボックス 2 個を廃止。**歩かせるは押した場所にそのまま立つ**（旧: ドット半径 14px を
  狙って掴む必要があり、外すとタイル塗りに化けた）。**ドラッグすれば自動で再生も始まる**（▶ 押し忘れ対策）
- **⚠ 合成ビュー（composite-view）は画面に出ている時だけ描く**。1 ページに 6 面以上あり、全部を常時 25fps で
  回すと **GPU が詰まってページ全体が固まる**（Chrome 拡張のスクリプト注入すらタイムアウトした）。
  録画中だけ `setForced(true)` で回し続ける（止めるとコマが凍る）
- 監視列（マルチカメラ）は**作る面（オーサリング / 素材）では最後尾へ**（`order:9`）。ライブ運用では先頭のまま
- 旧・生成プロンプトのカードに **📐 レシピへ**（1 クリックで素材工房のレシピへ移行）

**2026-07-26 🧪 素材工房 = 合成の試写室**（設計 `.claude/plans/2026-07-26_material-atelier.md` / 契約は README「素材工房」が正）:

- 列 = カメラ。1 列の中を上から **① 試写 → ② 種フレーム → ③ 指示 → ④ 生成物**（重要度順。作業順ではない）。
  素材の可否は「live に載せた見え」でしか決まらないので、試写を出したまま作る面にした（初版はタブ切替 + 合成が見えず、
  確認のたびにタイムラインへ面をまたぐ必要があった）
- **⚠ `composite-view.js` は `loopPreview: true` を渡さないと、素材が 1 周した瞬間に「再生終了」判定で live に戻り続ける**
  （`overlayOn` が落ちるまで復帰しない）。ループ試写する面（工房・cue エディタ）では必須。これを踏むと
  「合成が全く効かない」ように見えるので、まずここを疑う
- **試写の平均輝度で合否を判断しない**。カメラ post（ホラー用 露出 −1.4 / 彩度 0.44 / ヴィネット 0.5）が
  合成後に掛かるので、素材を載せても平均値はほとんど動かない（実際に「描画されていない」と誤判定した）。
  差分は**画素ごとの差**で見る。UI 側の対処は「画質: 本番 / 素の色」トグル
- 「動くところだけ」= 種フレームと素材の**差分の累積最大**（瞬間差分ではない。Quest は静的マスク PNG を使うので実運用と同形）。
  💾 でマスクを 640×480 PNG に焼いて `cue_<camId>_<n>` を作る＝工房からタイムラインへ繋がる唯一の橋
- 📷 は監視列の生映像を借りる（`getLiveImg` 注入・二重接続しない）。撮ると `notifyFrameSaved` で即候補に入る（リロード不要）
- **サーバ変更なしで成立**（既存 `/save` `/atelier/*` `/captures/list` `/masks` `/state` だけ）

**2026-07-25 🕹 ショーシミュレーション**（実機なしでショーを検証。計画 `.claude/plans/2026-07-25_show-simulator.md` が正）:

- フロアマップのドットをドラッグ = 体験者の歩き → ショーが実時間で進む（画面 / 周回 / 区間 / 演出 / イベントログ）。
  歩きを記録して `scenarios/*.json` に保存 → ワンクリック再実行（既定は**いまの show.json** で、「保存時の設定」も選べる）
- **判定は独自実装しない**。`scenario-engine.js` は Unity 純ロジック（ZonePickLogic / ZoneProgressionLogic /
  SwitchDirectorLogic / LapCounterLogic / TakeRunnerLogic / ShowScenarioRunner）の 1 対 1 移植で、
  **golden トレース `Assets/Tests/Fixtures/scenario_walk.trace.json` との一致を node テストで固定**（`scenario-engine.test.mjs`）。
  **食い違ったら直すのは JS 側**（C# が正）。fixture を書き換えて通すのは禁止
- **JS は時刻計算だけ `Math.fround`** で C# float に合わせる（double のままだと 1 tick ずれる箇所がある）。
  1 tick の評価順（ゾーン判定 → 時計 → ambient → 画面 commit → ゾーン通知 → 演出 Tick）は**動かすとトレースが壊れる**
- ゾーンは**生タイルではなく `ZoneLayoutSolver.SolveGrid` と同じ矩形展開**で判定する（`zone-layout.js`）。
  **grid 専用**（cuts のみの show.json では実行しない＝黙って別の答えを出さない）
- `untilClipEnd` の尺は卓では分からない → cue の trim から推定 / 不能なら watchdog まで（どちらも UI に ⚠）
- **`POST /scenarios/save` はサーバ再起動まで効かない**（2026-07-25 追加）。使えない間は自動で localStorage 退避 + その旨を表示
- ⚠ フロアマップのドットは**ドットの上から掴む**。外すとタイル塗りになる（既存挙動。パネルに注意書きあり）
- できないこと（パネル下部にも常時明示）: VR のスケール感・立体視、dip の体感時間、MJPEG の実レイテンシ・砂嵐、
  位置合わせ・recenter・触覚、Quest の性能。**卓で通っても実機確認は要る**

**2026-07-25 BGM オーサリング**（契約は rules/streaming.md「BGM」が正）:

- 旧 = `[Bgm]` の AudioSource 1 本で固定ループ。新 = **タイムライン区間で切替/停止/ループ範囲**（`bgmTracks[]` + ラン既定 `bgm` + `segments[].bgm`）
- 音源は `tools/web-compositor/audio/`（**gitignore**）。卓の「🎵 BGM ライブラリ」で登録 → 区間インスペクタで指示 → 📦 エクスポートで `sa://assets/` へ焼き込み
- タイムライン下の **BGM 帯**が「今どの曲が鳴っているか」を carry-forward で表示（▶切替 / ≡同曲調整 / ■停止）。JS ミラー `resolveBgmLane` は Unity `BgmPlanLogic` と同セマンティクス（テストで固定）
- **⚠ Unity は `Setup Main Demo Scene` の再実行が要る**（[Bgm] を BgmDirector 化。未実行なら旧固定ループのまま＝安全側に縮退）
- 擬似トラック `__default__` = APK 同梱クリップ（音源を二重に置かず「元の曲へ戻す」を書ける）

**2026-07-25 本番運用の安全装置**（設計判断ごと README「本番運用の安全装置」が正。ここは要点だけ）:

- **ラッチ（cameraOverride / activeCue）は show.json に永続する**＝前の体験者の固定が次のランへ持ち越される。ヘッダ直下の警告バーで常時可視化（**show.json 由来なので Unity 未接続でも出る**。旧実装は Unity heartbeat 由来でしか出ず、準備段階で見えなかった）。**▶ ラン開始は runEpoch++ ＋ 固定解除 ＋ 演出停止のフルリセット**
- **卓サーバ生存判定は `/unity/status` の 2 秒ポーリング**。`/state` は long-poll（最大 25s ブロック）なので断の検知に使ってはいけない。断は赤帯 + `body.server-down` でトーンダウン
- **✅ 本番前チェック**（ライブ・自動更新）に「**配信元の重複**」判定あり — 2 カメラが同じ host を指すと別ゾーンで同じ映像が出るのに LIVE 判定は通ってしまう（実際に検証中に B/C が同 IP になっていて発覚）
- **接続先の同一性照合**（PC が `/info` を 10s 毎に引き cameraId/show を slot と突合・`FIXEDCAM_IDCHECK=0` で無効化）。**Quest 側は前から `DiscoveryLogic.IsIdentityMismatch` で防いでいた**が PC 卓に同等の防御が無かった。**警告のみ**（映像を止めない・host を書き換えない）が設計の要点 — 自動貼り替えは物理的な置き間違いを「直った」ように見せ、ID 入替時にフラッピングする。stale IP 事故の根治は自前 AP + MAC 静的リース（DHCP 再割当を消す）で、照合は「気づける」ための保険
- 未保存ガード: ヘッダ `● 未保存` バッジ / beforeunload / **未保存のまま 📦 エクスポート不可**。タイムラインに ⟲ undo（Ctrl+Z・30 手）。`show.json` は保存ごとに `.bak` 1 世代 + tmp→`os.replace`
- 🚶 ゾーン自律はヘッダから**ライブの緊急バーへ移設**（▶ ラン開始 / 🚶 / ■ 演出停止 を 1 箇所に大きく）。ライブ中は**画質スライダを既定ロック**
- **heartbeat のキー名は `headCourseX` / `headCourseZ` / `currentZone`**。floormap.js が旧名 `hmdX` / `hmdZ` / `zoneLabel` を見ていて ◇ HMD 位置が一度も出ていなかった（2026-07-25 修正・両方受ける）
- **Unity 側 heartbeat に位置合わせ品質（残差・要再登録）は載っていない** → 本番前チェックに登録状態の行は無い。入れるなら ShowControlClient に provider を足す Unity 側作業（Assets 変更・Editor 検証が要る）

**2026-07-19 再構成**: 上部ナビで「🎬 事前オーサリング」（cue 作成・フロアマップ・タイムライン・エクスポート）と「🚨 ライブ運用」（発見/診断・ラン状態・▶ ラン開始 = control.runEpoch++・演出手動・カメラ固定・緊急手動接続）の 2 モード。**カメラカードから IP 常設入力を追放**（発見ベース読み取り専用表示・手動入力は「🚨 緊急: 手動接続」折りたたみ＝pinned 化）。

**1 ページ統合 UI（縦割り = カメラ列）**（2026-06-16 にユーザー要望で再編、〜06-17 で機能追加多数）。旧 3 画面（コンソール / コンポジット検証 / 合成エディタ multicam.html）を 1 ページへ統合。Webcam/テスト source・ギャラリーは撤去したが、**境界ブレンド（色統計/ラプラシアン/フェザー）と生成プロンプト管理はユーザー要望で復活**。

構成: **ステータス**（Unity生存/アクティブカメラ/fps/反映rev、🚶ゾーン自律・📂撮影フォルダ）＋ **境界ブレンドバー**（フェザー/色統計+強度/ラプラシアン+レベル、全カメラ共通）＋ **マルチカメラ**（列＝カメラ A/B/C、各列は**下→上の加工フロー**＝ **① 生映像 → ② マスク → ③ 画像加工 → ④ Quest 実映像**、2026-06-18 にユーザー要望で縦フロー化）＋ 下部 **生成プロンプト**。
- **④ Quest 実映像（最上段）**: 画質+合成を当てた最終見た目。**マルチパス合成 `pipeline.js`**（取り込み→色統計マッチング→ラプラシアン→post）。カメラ実寸比に追従（黒帯ゼロ）。ヘッダに **📺切替**（=setCameraOverride、● 表示中）/ 上に **📷1枚・⏺録画（=合成済み = Quest と同じ絵→`recordings/`、cam=`A_quest`）** + **演出 ON/OFF**+fade秒（cue.fadeIn/Out へ）。デモ用 1 画面密度レイアウト
- **③ 画像加工**: カメラ別画質（7 スライダ → `cameras[i].post`）＋ **合成素材**（captures/ から select / ↻ / 📂 / 動画は再生区間 trim）→ 💾 cue 保存（1 カメラ 1 cue: id=`cue_<camId>`、loop なし・再生終了で自動復帰、URL は percent-encode）。**未保存のまま演出 ON は警告**
- **② マスク**: 白黒境界を**スライダ調整**（白の向き 左/右/上/下 ＋ 位置スライダ ＋ 取り消し）。フリーハンド系は撤去
- **① 生リアルタイム映像（最下段）**: 生 MJPEG を `<img.raw-live>` で直接表示（加工前。この img を GL テクスチャ源にも兼用＝MJPEG 接続は 1 本のまま）。上に **📷1枚・⏺録画（=生フレーム→`recordings/`、cam=`A`）** + 配信元 IP/port/auth
- **キャプチャ 2 系統**: ① 生映像の上＝生フレーム / ④ 実映像の上＝合成済み（ビデオデモ用に「実際に Quest で見える絵」を録れる）。合成済み静止画のため `gl.js` は `preserveDrawingBuffer: true`（false だと toBlob が空になる）
- **全カメラ同時録画（2026-06-18）**: ヘッダに `⏺全カメラ録画(生)` / `(Quest)`。押すと全列で一斉録画開始、もう一度で全停止。各列の録画は `refs.startRecord(kind)/stopRecord()/isRecording()` を公開し個別ボタンとヘッダの一括が同じ経路を叩く（`recordAll`/`renderGlobalRecState` が全列集約）。1 列 1 レコーダ。ライブ未到達の列は生録画を graceful スキップ。ファイルは各列タグ + ほぼ同一タイムスタンプで `recordings/`
- **生成プロンプト**: 画像/動画 AI プロンプトを 📋コピー/編集/削除（既存 `/prompts`=prompts.json）

実装: `index.html` ＋ `app.js`（全配線、ESM）＋ `pipeline.js`（合成）＋ `gl.js`/`shaders.js`。Unity 側対向は `ShowControlClient`（long-poll + 端末キャッシュ）/ `ScreenOverlayController`（cue 再生・動画はローカル DL）。**境界ブレンドの色統計/ラプラシアンは Web プレビュー専用**（Quest はハード合成、フェザーのみ PNG 焼き込みで効く）。

**デザイン言語（cogni-storage 準拠）**: 純黒 `#000` + radial-gradient / 文字 `#fffaf0` / アクセント `#ffdead` / **角丸ゼロ** / 透明カード + 1px 罫線（`--line-soft`）/ UPPERCASE マイクロラベル / 下線型インプット。トークンは style.css の `:root` に集約。**例外: IP/port/認証 入力だけは下線型でなく「黒地 + 外枠 + アクセント文字」**（2026-06-18）— ブラウザ autofill が背景を白系に上書きしクリーム文字が消える対策。`input:-webkit-autofill` を inset box-shadow で黒塗りにしてある。下線型に戻さないこと。

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
| `sim.html` / `sim.js` | Unity なしで動作確認する仮想 Quest（show.json long-poll。schedule / course.order も表示） |
| `schedule.js` | **タイムライン UI**（2026-07-19 マトリクスから全面改装）。「1周目: A\|B\|C → 2周目: …」を course.order 順に横連結、区間クリックで cue 割当（delaySec / once）。データモデルは schedule.entries のまま。**⚠ タイムラインで直接いじれるのは cueId / delaySec / once の 3 つだけ**。マスク・素材・フェード・trim は cue 側（カメラ列で作成 → 周ごとに別 cue を割当）、**画像加工 post はカメラ単位で周ごと変更不可**（rules/streaming.md「編集の粒度と自由度」が正） |

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
| POST | `/export-build` | **ビルド焼き込みエクスポート**（2026-07-17〜）。show.json + cue 参照アセットを `Assets/StreamingAssets/show/` へコピーし URL を `sa://assets/<file>` に書換（欠損参照はエラーにせず verbatim 残置・毎回 assets/ 掃除・同一ファイル dedupe）。**カメラ host も verbatim 焼き込み**なのでビルド直前に現場でエクスポートし直す。出力はコミット禁止（gitignore 済み） |

**cue の複数化（2026-07-17〜）**: `cue_<camId>` 固定 1 個 → 1 カメラに複数 cue（`cue_<camId>_<n>`、新規/複製/削除/選択編集）。演出 ON は選択中 cue を発火。周回スケジュール（`schedule.entries[] = {lap, camera(index), cueId, delaySec, once}`）と `layout.course.order`（フロアマップの「周回コース」で編集、order[0]=スタート、CW/CCW トグル、grid 塗りから角度順提案）は Unity 側 LapCounter / CueScheduler が消費。詳細契約は rules/streaming.md。

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
