---
status: done
created: 2026-07-18
updated: 2026-07-18
slug: connection-robustness
---

# カメラ・HMD・PC 間接続の堅牢化（端末内在 ID + 発見プロトコル + 自己修復）

## 概要

現状、カメラスロット（A/B/C = ゾーン・post・cue の割当先）と実端末の対応は **DHCP の IP 頼み**。
IP 変動・入れ替わりで「間違った映像が間違ったゾーンに出る」サイレント事故が起きる（実績あり）。
根本対策として **ID を正・IP を手段** に転換する：スマホに cameraId を刻み、発見プロトコルで
「ID→現在の IP」を実時間解決し、接続断時に自動で張り替える。

ユーザー決定（2026-07-18）: ①ID はアプリ内選択 + 巨大表示 ②PC（卓サーバ）も自動発見 ③Service 化はシュビー判断
→ **含めるが独立フェーズ・独立コミット**（不安定なら単独で巻き戻す）。

## 障害カタログ → 対策マップ

| # | 障害 | 対策 |
|---|---|---|
| F1 | DHCP 変動でスロット⇔IP 崩壊（入替=サイレント誤映像） | 端末内在 ID + 発見 + /info ID 照合（Phase 1-3） |
| F2 | アプリ/端末落ち→復帰後 IP 変化、旧 IP へ永久再接続 | 実害駆動の自動張り替え（Phase 2） |
| F3 | 画面 Doze 起動でフレーム 0 / 電源ボタン誤爆で配信停止 | Service 化（Phase 4）+ 欠落カメラ赤アラート（Phase 2/3） |
| F5 | PC 不在時、焼き込み IP が現場不一致で全黒 | Quest 単体 discovery（PC 不要・Phase 2） |
| F6/F7 | スロット取り違え / 予備機投入 | 画面巨大 ID 表示・ボタン 1 押しで再割当（Phase 1） |
| F8 | 二重 ID | **切り替えず現接続維持 + 警告**（フラッピング禁止）。uuid で「同一機のローミング」と区別 |
| F9 | PC 卓サーバの IP 変動 | PC も announce を出し Quest が発見（Phase 2/3） |
| F10 | AP クライアントアイソレーション | **技術で救えない**。疎通診断パネル（Phase 3）で即検出 → 自前 AP 持ち込み runbook |

## 設計原則（赤入れ反映済み）

1. **Quest はブロードキャストを受信しない**: Quest/PC が probe を**ブロードキャスト送信**し、スマホが**unicast で応答**。
   Quest は unicast 受信のみ → MulticastLock・カスタム manifest（TableDuo 共有資源）に触らない。
   スマホ側の broadcast 受信は自リポなので MulticastLock + `CHANGE_WIFI_MULTICAST_STATE` を streamer に追加
2. **張り替えは実害駆動**: beacon 断では何もしない。「フレーム断が 5 秒継続」した時だけ発見表を参照。
   切替前に必ず **HTTP /info を GET して cameraId + show token を照合**（beacon は候補、真実は /info）
3. **優先順位**: `manual-pin（卓の手動固定） > live discovery > show.json（ライブ>キャッシュ>焼き込み） > .asset 焼き込み`
4. **二重 ID / 二重サーバはフラップさせない**: 現接続を維持 + HUD/卓に赤警告。install uuid で roam（同一機 IP 変化=追従可）と conflict（別機同 ID=停止+警告）を判別
5. **キルスイッチ**: discovery 一式を 1 フラグで無効化でき、従来の静的 IP 運用へ即縮退できる
6. **可視化**: HUD/卓に per-camera の { id, endpoint, 接続出所レイヤ, lastSeen, 警告 } + 欠落カメラ赤アラート

## プロトコル契約 fixedcam-discovery/1（UDP :8830・JSON・512B 以下）

```jsonc
// probe（Quest / PC 卓 → ブロードキャスト。サブネットディレクテッド優先、255.255.255.255 フォールバック）
{ "proto": "fixedcam-discovery/1", "type": "probe", "show": "mawarimi", "seq": 12 }

// announce（スマホ / PC 卓サーバ → probe への unicast 応答 + 5 秒毎の定期ブロードキャスト）
{ "proto": "fixedcam-discovery/1", "type": "announce", "show": "mawarimi",
  "role": "camera",            // "camera" | "show-server"
  "id": "A",                    // camera のみ。未設定は "?"（発見はされるが自動採用されない）
  "uuid": "<install毎UUID>",    // prefs 永続。roam/conflict 判別用
  "httpPort": 8080, "version": "0.3.0", "name": "Pixel 7a" }
```

- probe 周期: 未解決/不健全カメラがある間 2s、全健全時 10s。announce 定期 5s
- `show` トークン不一致のパケットは無視（隣ブース混線対策）。既定 "mawarimi"（streamer 設定で変更可）
- /info にも `cameraId` / `uuid` / `show` を追加（切替前照合に使う。既存フィールド不変・末尾追記）

## show.json スキーマ追加（後方互換）

- `cameras[i].pinned: bool` — 卓で host を手入力すると自動で true（自動追従・discovery とも抑止）。解除は明示 UI
- Quest も pinned を尊重（pinned カメラには discovery を適用しない）

## フェーズ

### Phase 1: streamer v0.3.0（Kotlin — opus 委譲）

- [x] cameraId 選択 UI（A/B/C/未設定ボタン + 画面に巨大表示）+ SharedPreferences 永続化（既存 streamer_prefs パターン）
- [x] install uuid 生成・永続化。/info に cameraId / uuid / show を末尾追記
- [x] DiscoveryResponder（StreamingService 内）: :8830 受信（MulticastLock 取得・`CHANGE_WIFI_MULTICAST_STATE` 追加）、
      probe に unicast announce 応答 + 5s 毎定期ブロードキャスト（subnet-directed 優先）
- [x] show トークン設定（既定 "mawarimi"・prefs）
- [x] versionName 0.3.0 / versionCode 3、README 更新

### Phase 2: Unity（`Assets/Scripts/` — opus 委譲・Phase 1/3 と並列可）

- [x] `CameraSource` に `[SerializeField] cameraId`（"A"/"B"/"C"）新設 + Phone01..03.asset へ反映（unity-prefab-fields 作法）
      ※ host と違い cameraId はコミット可（恒久設定）
- [x] `CameraSource` に discovery 用 override 層を追加（`ApplyDiscoveryEndpoint` / `ClearDiscoveryEndpoint`。
      優先: pin > discovery > runtime(show.json) > baked。ConnectionKey に反映）
- [x] `ShowServerSource` に runtime override 新設 + `ShowControlClient` のループ URL 掴み直し（_loopCts 再生成パターン）
- [x] `DiscoveryClient`（新規 MonoBehaviour）: Task.Run 受信ループ + Update drain（MjpegStreamReceiver の単一スロット作法）、
      probe 送信タイマ、発見表 id→{ip,port,uuid,lastSeen}。純ロジック（発見表・切替判定・conflict 判定）はクラス分離しテスト
- [x] 切替ポリシー: `CameraStream.IsConnected`/フレーム断 5s 継続 + 発見表に別エンドポイント + **/info 照合 OK** → ApplyDiscoveryEndpoint。
      pinned は不適用。conflict（同 id 別 uuid 複数）は切替停止 + 警告
- [x] show-server 発見: server 未設定 or 不通 N 秒の時のみ override 適用
- [ ] サブネットスイープ（フォールバック・手動/自動トリガ）: 自 IP+netmask から算出、/24 超なら無効、並列 8・
      timeout 500ms・総時間 30s 上限、off-main-thread — **未実装**（beacon+probe で十分か実運用を見て判断）
- [x] HUD: per-camera { id, endpoint, 出所(baked/cache/live/disc/pin), lastSeen, conflict/missing 警告 }
- [x] キルスイッチ: DiscoveryClient.enabled 相当の master フラグ（SerializeField + show.json `control.discoveryEnabled` 上書き）
- [x] EditMode テスト（発見表 TTL・conflict・切替判定・照合ゲート）+ `MainDemoSceneSetup` 自動配線（冪等）

### Phase 3: PC 卓（`tools/web-compositor/` — opus 委譲・並列可）

- [x] capture-server: UDP listener スレッド（announce 受信）+ probe 送信 + 発見表 API（`GET /discovery`）
- [x] show-server announce ブロードキャスト送出（role:"show-server"・5s）
- [x] 自動追従: 発見 id ↔ cameras[i]（A/B/C 規約）一致で host 自動更新（グローバルトグル・**pinned カメラは除外**・rev++）
- [x] pinned スキーマ + UI: host 手動編集で自動 pin、pin 状態表示・解除ボタン
- [x] 発見済み端末パネル（id/ip/version/lastSeen/uuid・二重 ID 赤警告）
- [x] 疎通診断パネル: PC→各カメラ /info 実接続 + beacon 受信状況 + Quest heartbeat の 3 経路を 1 画面（AP アイソレーション即検出）

### Phase 4: streamer Service 化（独立コミット・Phase 1-3 検証後）

- [x] CameraX bind を Service（自前 LifecycleRegistry で LifecycleOwner 化）へ移管。Activity は UI 専任・bindService
- [x] 実機確認（Pixel 7a）: 前面29.9 / HOME background 29.6 / 別アプリ 29.6 / 画面OFF 29.9 fps（v0.3.0 は 2.5fps）。discovery/cameraId も維持。3 台 v0.4.0 統一（streamer 8369635）
- [x] BOOT_COMPLETED 自動復帰は入れない（Android 14 の camera FGS 制約。方針どおり）。プロセス完全死からの自動再起動は非保証（runbook 対応）

### Phase 5: 統合検証（シュビー本体・逐次）

- [x] streamer ビルド → 3 台へインストール → PC から python で probe → 3 台の announce 応答を実測（ID 永続・即時反映も確認）
- [x] Unity コンパイル（エラー 0）+ EditMode テスト 93/93 pass ／ [ ] Setup 再実行（Editor が TableDuoMain 使用中でブロック・後続）
- [x] Web 卓：/discovery・/diag 実測、host 破壊→自動追従 1 秒復元を確認
- [x] 廻リ視 APK → Quest インストール → 実機検証（opus デバッグエージェント）:
      **クリーン再起動 + 壊れ IP 起動 → 4.9s（設計値 5s）でフレーム断 → /info ID 照合 → 正 IP へ自動張替を確認**。
      ライブ破壊テストも張替成功（100ms・断アキュムレータ飽和済みだったため）。churn/フラップ無し
- [ ] ドキュメント同期（streaming.md 契約 / README / troubleshooting.md 逆引き / camera_fleet memory）+ pathspec コミット

## 境界・注意

- TableDuo・ProjectSettings・共有 .asset に触れない。**カスタム AndroidManifest（Plugins/Android）は作らない**（設計で回避済み）
- Phone*.asset の host はコミット禁止のまま。**cameraId フィールド追加はコミット可**
- 実装エージェントの完了報告は親が `git status` で物証検証（delegation.md 2026-07-17）
- 運用対策（機構外・runbook 行き）: 自前 AP + MAC 静的リースが第一選択、発見機構は保険。現地セットアップ時に疎通診断パネルで AP を判定

## 自律改善ログ

- 2026-07-18 Phase 1-3 実装完了（opus 3 並列・全報告を git 物証照合）。streamer v0.3.0 は実機 3 台で検証しコミット（e4b46d2）
- 実測: probe→3 台 unicast 応答 / ID 再起動永続 / PC 卓 /discovery・/diag / **host 破壊→自動追従 1 秒復元**
- 設計転換の記録: Quest は broadcast を受信しない（probe 送信+unicast 受信のみ）→ MulticastLock・共有 manifest 改変を回避
- 逸脱採用: /info 照合は UnityWebRequest でなく既存 StreamMetadataFetcher（HttpClient）経路（Allow HTTP 設定の罠を回避する実績経路）。サブネットスイープは未実装（フォールバック未着・必要になったら追加）
- 2026-07-18 Quest 実機検証完了（discovery 張替は設計どおり機能）。シーン配線コミット b79c4a9
- **実機検証で発見した設計ギャップ**: 「正 IP なのに受信 0」（half-open/dead socket）は discovery（同一 IP を候補にしない）でも lag-detect（/health fps 必要）でも救えない → CameraStream に **stall watchdog**（無フレーム 10s + cooldown で同一エンドポイント強制再接続）を追加して閉じた
- 残: HMD 装着時 suspend 挙動・二重 ID conflict・show-server 発見の実機確認（次回現場テストで）、Phase 4 Service 化
- 2026-07-18 Phase 4 完了（streamer v0.4.0・独立コミット 8369635）。Service 化で background fps 激減を解消。3 台 v0.4.0 統一・Quest 全系疎通確認。**この計画の実装フェーズは完了**（残: HMD 装着 suspend・二重 ID conflict・show-server 発見の現場実機確認は次回テスト）
- 逸脱→修正: opus の Service 化に Kotlin ネストブロックコメントの罠（`/**...*/` 内の `/record/*` の `/*` がネスト開始と誤解釈され Unclosed comment）。親が該当コメントを書き換えて解消
