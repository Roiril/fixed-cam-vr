---
status: in-progress
created: 2026-07-17
updated: 2026-07-17
slug: pre-authored-cue-schedule
---

# 事前オーサリング済み cue スケジュール（周回×ゾーン発火 + APK 焼き込み + Web オーサリング UI）

## 概要

現状 cue（映像差し替え演出）の発火は Web オペレータ卓からの遠隔操作（`control.activeCue`）とキーボードのみ。
これを「**ビルド前に、何周目のどのゾーンで、どの動画＋マスクを差し込むか**」を事前に決めて APK に焼き込み、
現地 PC 不在でも Quest 単体で自動発火する構成にする。遠隔卓は**残し、ライブ操作が常に優先**（本番リカバリ手段）。

ユーザー決定事項（2026-07-17）:
1. **APK 完全焼き込み**（show.json + 動画 + マスクを StreamingAssets 同梱。新規インストール直後から PC 不要）
2. **周回検知はフロアマップ UI と統合** — スタート領域と順方向（どっち廻り）を UI で決める。行ったり来たりはカウントしない
3. **遠隔卓は残す・ライブ操作優先**

## 調査で確定した現状（2 opus エージェント調査 2026-07-17）

- 発火は全て `ScreenOverlayController.PlayCue(OverlayCueData)` に集約（フェード・trim・世代管理は完成済み）
- `PlayerZoneTracker` に**ゾーン切替イベントは無い**（`CurrentZone` getter のみ）。カメラ切替イベントは `CameraStreamRegistry.ActiveChanged` が既存
- **周回カウントは存在しない**（grep 済み）
- show.json の `cues[]` は端末キャッシュ `CachedConfig` に**保存されない** + URL は PC の IP 解決 + 動画 DL キャッシュは `temporaryCachePath` で OnDestroy 削除 → **show.json cue はオフライン発火不可**（現状）
- Web 卓: cue はカメラ毎 1 個固定（id=`cue_<camId>` ハードコード: app.js:317,345,355,481）。発火は `POST /command {playCue}` → long-poll。フロアマップ `floormap.js createFloorMap()` の「canvas グリッド＋パレット＋dirty ガード＋postState」パターンがスケジュール UI に流用可能
- サーバ: `capture-server.py` `_STATE_KEYS=('cameras','cues','post','control','layout')` の shallow 置換 + rev++ + long-poll 起床
- StreamingAssets は未使用（secrets.example.json のみ）。ビルド焼き込み導線ゼロ

## スキーマ契約（show.json 拡張 — Unity / Web / サーバ共通の正）

```jsonc
{
  // 既存: rev / cameras / cues / post / control / layout

  // cues: 複数化。id は任意（UI 規約は cue_<camId>_<n>）。camera フィールドで所属カメラを示す（既存フィールド、継続）
  "cues": [
    { "id": "cue_A_1", "name": "手形", "camera": "A",
      "maskUrl": "/masks/cue_A_1.png", "sourceUrl": "/captures/x.mp4",
      "strength": 1.0, "loop": false, "fadeIn": 0.5, "fadeOut": 0.5,
      "trimStart": 0, "trimEnd": 0 }
  ],

  // layout.course: 周回定義（フロアマップ UI で編集・layout と同じ保存単位）
  "layout": {
    // ...既存 grid/cuts/wall/floor...
    "course": {
      "order": [0, 1, 2]   // 順方向のカメラ巡回順。order[0] = スタート領域のカメラ。
                            // UI は grid の塗りから角度順で提案し、CW/CCW トグルで反転できる
    }
  },

  // schedule: 事前オーサリングの正体。lap は 1 始まり
  "schedule": {
    "rev": 1,
    "entries": [
      { "lap": 2,            // 何周目（1-based）
        "camera": 1,          // どのゾーン（カメラ index。ゾーンは cameraIndex でキー — 生成ゾーンはインスタンス不安定のため）
        "cueId": "cue_B_1",
        "delaySec": 0,        // ゾーン進入からの遅延
        "once": true }        // true=そのランで 1 回だけ
    ]
  },

  // control.activeCue（既存・単一）はライブ手動オーバーライド専用として残す
}
```

### 焼き込み URL 規約

エクスポート時、アセット URL は `sa://assets/<file>` に書き換える。Unity 側リゾルバ:
`sa://` → `Application.streamingAssetsPath + "/show/assets/"`。
動画は Android で VideoPlayer が StreamingAssets パスを直接再生可能。テクスチャは UnityWebRequest（jar: URL 対応）。
配置先: `Assets/StreamingAssets/show/show.json` + `Assets/StreamingAssets/show/assets/*`。

### 周回カウントのセマンティクス

- lap は **1 始まり**（体験開始 = 1 周目）
- 進行ポインタ方式: 期待する次カメラ = `order[(i+1) % n]`。アクティブカメラがそれに**一致した時だけ**前進。
  逆方向・同一・スキップ遷移では前進しない（境界 jitter・行き来・逆走はカウントされない）
- 全カメラを順方向に踏破し `order[0]` に戻った時点で lap++
- リセット: 体験開始時（アプリ起動 / 明示リセット操作）

### 発火とライブ優先の調停

- `CueScheduler`（新規）が (lap, camera) 一致 + delaySec 経過で `ScreenOverlayController.PlayCue` を**ローカル直接**呼ぶ（サーバ不要）
- サーバから `control.activeCue` が**非空でセットされている間はスケジューラ抑止**（ライブ優先）。空に戻ったら再開
- スケジューラ発火状況（現在 lap・最終発火 cue）は heartbeat に載せて Web 卓で可視化（polish）

### 設定の優先順位（拡張後）

焼き込み StreamingAssets < 端末キャッシュ（persistentDataPath/show_config.json）< ライブ long-poll（後勝ち・従来通り）。
`CachedConfig` に cues / schedule / layout.course を**追加保存**する（現状 cues 欠落の修正）。

## フェーズ

### Phase 1: Unity ランタイム（`Assets/Scripts/` — opus 委譲）

- [ ] `PlayerZoneTracker` に `event Action<PlayerZone, PlayerZone> ZoneChanged` を公開（Pick 確定時に発火）
- [ ] `LapCounter`（`Assets/Scripts/Tracking/LapCounter.cs`、純ロジック分離でテスト可能に）— order/進行ポインタ/lap
- [ ] show.json パーサ拡張（`ShowControlClient`: `schedule` / `layout.course`。JsonUtility）
- [ ] `CueScheduler`（`Assets/Scripts/Streaming/CueScheduler.cs`）— (lap, camera, delaySec, once) 評価 → PlayCue。activeCue 非空で抑止
- [ ] `CachedConfig` に cues / schedule / course を追加保存
- [ ] `sa://` URL リゾルバ（OverlayCueData 解決経路 + StreamingAssets/show/show.json 起動時ロード、優先順位最下位）
- [ ] EditMode テスト: LapCounter（順方向/逆走/行き来/スキップ/1 周完了）+ CueScheduler 評価ロジック
- [ ] `MainDemoSceneSetup` への自動配線（Setup 再実行で冪等）

### Phase 2: Web 卓 + サーバ（`tools/web-compositor/` — opus 委譲・Phase 1 と並列可）

- [ ] `capture-server.py`: `_STATE_KEYS` + `_default_show()` に `schedule` 追加。layout.course も契約コメント更新
- [ ] cue 複数化: `cue_<camId>` ハードコード撤廃、1 カメラに複数 cue（追加/複製/削除、id=`cue_<camId>_<n>`）
- [ ] フロアマップにコース設定統合: スタート領域選択 + 順方向 CW/CCW トグル → `layout.course.order` 保存（grid 塗りから角度順提案）
- [ ] スケジュール UI（`schedule.js` 新設、floormap パターン流用）: 行=カメラ/ゾーン・列=周回のマトリクス。セルクリックで cue 割当・delaySec・once 編集。周回数は可変（列追加）
- [ ] エクスポート API `POST /export-build`: show.json の参照アセットを収集 → URL を `sa://` に書換 → `Assets/StreamingAssets/show/` へコピー。UI に「📦 ビルド用エクスポート」ボタン + 結果表示
- [ ] sim.html（仮想 Quest）でスケジュール発火を模擬確認できる程度の表示（現在 lap 表示）

### Phase 3: 統合検証（シュビー本体・逐次）

- [ ] Unity コンパイル確認（refresh_unity → DLL mtime → read_console エラー 0）
- [ ] EditMode テスト実行（run_tests）
- [ ] Web 卓: serve.ps1 起動 → ブラウザでスケジュール UI 操作 → show.json 差分確認 → sim.html で long-poll 受信確認
- [ ] エクスポート実行 → StreamingAssets/show/ 生成物確認 → （可能なら）Editor Play or L0 相当でオフライン発火経路確認
- [ ] 実機確認はユーザー依頼（コンパイル OK / 実機未検証を明示）

### Phase 4: ドキュメント同期（同一コミット）

- [ ] `.claude/rules/streaming.md`（show.json 契約・スケジュール・焼き込み）
- [ ] `.claude/rules/unity-vr.md`（周回カウントのセマンティクス）
- [ ] `README.md`（オーサリング→エクスポート→ビルドのフロー）
- [ ] memory `web_compositor.md`（スケジュール UI・エクスポート）

## 境界・注意

- TableDuo（`Assets/TableDuo/`）には一切触れない
- 共有資源（ProjectSettings / URP / manifest.json）は変更しない
- `Assets/Settings/Cameras/*.asset` の host / ShowServer.asset の host はコミット禁止（git-workflow.md）
- コミットは pathspec 明示（並列作業前提）
- 新規 .cs は build 前に `refresh_unity mode=force scope=all` で明示インポート（mcp-unity.md）

## 自律改善ログ

(作業中に気付いた改善点や学びをここに追記)
