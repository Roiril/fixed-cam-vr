# Web 卓タイムライン第一級化 — 体験オーサリングの根本再設計

status: implemented・実機未検証（2026-07-19 実装完了。Unity コンパイル + EditMode 126/126・Web 卓ブラウザ検証済み）

## 検証結果（2026-07-19）

- **Unity**: `refresh_unity force` → コンパイルエラー 0・DLL 再生成確認。EditMode `FixedCamVr.Streaming.Tests` + `FixedCamVr.Tracking.Tests` = **126/126 pass**（InsertLogic 12・cue override 2・insert 凍結 3・LapCounter Insert ゲート 1 を含む）。`@override` の JsonUtility 往復は C# エスケープ識別子の実行時フィールド名が `"override"` になる仕様上、ToJson/FromJson が一致（構造上確実）
- **Web**: 全 JS `node --check` pass・capture-server.py 構文 OK。卓ロード時コンソールエラー 0。タイムラインが author 主面・カメラ列は監視専用（cue-manage 撤去確認）・セグメントインスペクタに cue/post/insert の 3 面が揃う（cue-editor 埋込動作）・**矢印キー検証エンジンが LapCounter セマンティクス（A→B→C→2周目 A、← で戻る）を正しく再現**。ローカルのみで show.json は書かない
- **未検証**: (1) Quest 実機での insert dip 演出・post 切替の見た目、(2) timeline の保存往復（POST /state → Unity 適用）— 検証時は別チャットの卓サーバが 8099 を占有しており旧プロセスの可能性（ディスクの capture-server.py は `_STATE_KEYS` に timeline を含むので、サーバ再起動で保存は通る）
- **中断ポリッシュ（未確認・軽微）**: Web 実装エージェントはセッション上限で「既存マスク付き cue を編集開始した時にプレビューへ保存済みマスクを反映する」微調整の途中で停止。インスペクタ・cue 編集器の基本動作は検証済みで壊れていない。現地運用前に要目視

## 背景・ユーザー要求

体験の核は事前焼き込み（pre-authored cue schedule）。しかし現行 Web 卓は：

- cue のオーサリング UI がカメラ列（`buildColumn`）に埋め込まれ「カメラの下に cue がぶら下がる」構造で分かりづらい
- タイムライン（schedule.js）は cueId/delaySec/once の 3 項目しか触れず、マスク・素材・フェードは cue 側でしか編集できない
- 「2 周目、A→B に切り替わる前にカメラ C に演出を加えて N 秒表示 → B へ」のようなインサートショットが表現できない
- 周×カメラ区間ごとの画像加工（post）変更が原理的に不可（カメラ単位固定）
- Web からの検証手段がない（キーボードショートカット 0 件・sim.html は schedule 発火を再現しない）

## 設計原則

1. **タイムライン（周回×ゾーン区間）が唯一のオーサリング面**。区間 = 「周回 L にゾーン（カメラ）C へ滞在する区間」。カメラ列は監視専用（ライブ映像・接続・録画）に格下げ
2. **時間軸は物理秒ではなく「周回×ゾーン進行」**（体験者の歩行速度は不定）。秒は区間内の相対時間（delaySec / durationSec）のみ
3. **show.json は Unity と共有する契約 — 後方互換必須**。旧 `schedule.entries`・旧端末キャッシュは読み続ける。`timeline` が存在すれば supersede
4. **テスト済み純ロジック（LapCounterLogic / CueScheduleLogic / SwitchDirectorLogic）の芯は変えない**。拡張は新純ロジック + EditMode テストで
5. JsonUtility 制約：null 入れ子は既定値で書かれるため、**任意入れ子には `hasXxx` present-flag 必須**（CameraDef.hasPost と同じ手法）。未知フィールド無視 = 追加は前方後方互換

## show.json スキーマ v2：`timeline`（トップレベル新キー）

```jsonc
"timeline": {
  "rev": 1,                       // 変更検出ゲート（layout/schedule と同じ流儀）
  "segments": [
    {
      "lap": 2,                   // 1 始まり
      "camera": 0,                // カメラ index（course.order / schedule と同一キー空間）
      "cues": [                   // 進入 + delaySec で発火（複数可・従来 schedule.entries 相当）
        { "cueId": "cue_A_1", "delaySec": 0, "once": true,
          "override": { "strength": 0.5, "fadeIn": 1.2, "fadeOut": 0.8,
                        "trimStart": 0, "trimEnd": 0 },
          "hasOverride": true }   // override 使用時のみ true
      ],
      "post": { /* PostParams 7 項目 */ },
      "hasPost": true,            // セグメント post 上書きの present-flag
      "insert": {
        "anchor": "exit",         // "enter" | "exit"
        "camera": 2,              // 差し込むカメラ index
        "delaySec": 0,            // enter: 進入からの遅延 / exit: 0 固定運用（UI は enter のみ露出）
        "durationSec": 4,         // 表示秒数
        "cueId": "cue_C_scare",   // 任意。表示に合わせ PlayCue
        "once": true,
        "post": { /* PostParams */ }, "hasPost": false
      },
      "hasInsert": true
    }
  ]
}
```

### セマンティクス（Unity 側の実装契約）

- **セグメントキー = (lap, camera)**。同一キーのセグメントは 1 個（Web が保証。Unity は先勝ち）
- **cues[]**: セグメント進入（LapCounter 経由の Zone commit）+ delaySec で発火。once = ラン内 1 回（runEpoch 変化でクリア、既存規約）。`override` は ResolveCue 結果（OverlayCueData の複製）への差分パッチ。ライブ `control.activeCue` 非空中は抑止（既存規約の踏襲）
- **post**: セグメント滞在中の post 上書き。解決は 3 段 **segment post > cameras[i].post > global post**。セグメント離脱（次の Zone commit）で解除
- **insert**:
  - `enter`: 進入 + delaySec 後、insert.camera を durationSec 秒表示 → 最新ゾーンカメラへ復帰。切替は dip-to-black 付き（Director 経由）
  - `exit`: このセグメントから **Zone 切替で離脱する瞬間**（dip の黒中に registry が切替先へ commit した直後）、表示を insert.camera へ差し替えて durationSec 秒 → 最新ゾーンカメラへ復帰。**周回カウントは実ゾーン移動の commit 時に通常どおり 1 回**（Insert 切替は数えない）。体験者に見えるのは A → 黒 → C（N 秒）→ 黒 → B
  - insert 表示中：ゾーン自動切替は凍結（cue 凍結と同様に最新 pending を保持し、復帰先は**最新ゾーンカメラ**）。insert.cueId があれば表示に合わせ PlayCue / 復帰時 StopOverlay。insert 中の post は insert.hasPost ? insert.post : cameras[insert.camera].post : global
  - once = ラン内 1 回。ライブ手動（activeCue / cameraOverride）中は発火抑止
- **後方互換**: `timeline.rev > 0 && segments 非空` なら `schedule.entries` を無視（supersede）。無ければ従来どおり schedule.entries が生きる。CachedConfig に timeline を保存（オフライン発火。「cues 欠落」事故の教訓）
- **SwitchSource に `Insert` を追加**。LapCounter のゲートは Zone のみ通す現行不変（Insert が数えられないことをテストで固定）

## Unity 実装（Assets/Scripts + Assets/Tests）

| 対象 | 変更 |
|---|---|
| `ShowControlClient.cs` | TimelineDef/SegmentDef/SegmentCueDef/InsertDef パース構造体・hasXxx 確定（ライブパース直後）・CachedConfig に timeline・Apply で rev ゲート・**SetPostOverride(PostParams?) 層**（ApplyPostForActive を 3 段に拡張）・timeline→分配アダプタへの push |
| 新 `TimelineDirector.cs`（または CueScheduler 拡張） | timeline を CueScheduler（cue+override）/ InsertController / post 適用へ分配する薄いアダプタ。現在セグメント (lap, activeCam) を追跡し post 上書きを掛け外し |
| `CueScheduler.cs` / `CueScheduleLogic` | Decision に cue override を載せ、Fire で OverlayCueData 複製へパッチ |
| 新 `InsertController.cs` + `InsertLogic`（純ロジック） | enter/exit 発火判定・duration 計時・復帰先=最新ゾーンカメラ・once・抑止条件 |
| `CameraSwitchDirector.cs` / `SwitchDirectorLogic` | `SwitchSource.Insert` 追加・insert 用切替 API（dip 付き）・insert 中の zone 凍結（cue 凍結と同型） |
| `LapCounter.cs` | 変更なし（ゲート不変）。Insert source を数えないテストを追加 |
| `MainDemoSceneSetup.cs` | 新コンポーネント配線（冪等） |
| `Assets/Tests/` | InsertLogic 新規・SwitchDirectorLogic insert 凍結・CueScheduleLogic override・LapCounter Insert ゲート |

## Web 実装（tools/web-compositor）

| 対象 | 変更 |
|---|---|
| 新 `cue-editor.js` | `buildColumn` から cue 編集器（マスク canvas・素材 select・trim・fade・strength・💾保存）を抽出した独立モジュール。deps 注入パターン |
| 新 `timeline.js`（schedule.js 後継） | セグメントグリッド（lap 行 × course.order 順区間）+ **セグメントインスペクタ**：cue 割当・インライン cue 編集（cue-editor 埋込）・post スライダ（セグメント上書き・ON/OFF）・insert 編集（anchor/カメラ/秒/cue）・境界チップ（exit insert の可視化） |
| `app.js` | カメラ列を監視専用に縮小（①生+④Quest 実映像+接続+録画。cue-manage 撤去）。timeline.js / sim 配線 |
| 新 検証モード（timeline.js 内 or `simulate.js`） | **矢印キー →** = 次ゾーンへ進行（course.order 順・JS ミラーの LapCounterLogic/CueScheduleLogic/insert/post を駆動）、**←** = 1 手戻る（先頭からリプレイ）、**R** = ラン先頭、**Esc** = 終了。WebGL プレビューで「体験者に見える画」（アクティブカメラ+cue 合成+post+insert シーケンス）を再現し、タイムライン上の現在区間をハイライト |
| `capture-server.py` | `_STATE_KEYS` に `'timeline'` 追加。export-build の cue 参照走査に timeline の cueId（segments[].cues[] + insert.cueId）を含める |
| migration | load 時：timeline 空 && schedule.entries 非空 → timeline へ変換して編集開始。保存時：timeline を書き、schedule.entries は空化（rev++）。Unity 側 supersede と整合 |
| `index.html` / `style.css` | タイムラインを author モードの主面に昇格。カメラ列縮小のレイアウト |

- cue は従来どおりトップレベル `cues[]`（ライブラリ）に置き、camera 帰属（マスクはそのカメラの構図に対して作るため）は維持。セグメントインスペクタは区間のカメラで cue を絞り込み、**その場で新規 cue 作成**もできる
- キー空間は現状維持（cues.camera = 文字列 id / timeline.camera = int index。変換は既存 `cuesForCam` 流儀）。統一は今回見送り（Unity 共有契約の破壊を避ける）

## 検証

- Unity: EditMode テスト全緑（新規含む）+ refresh_unity → コンパイルエラー 0 + DLL mtime 確認
- Web: 卓を起動しタイムライン編集（cue 割当・インライン編集・post・insert）→ show.json 保存 → 検証モード矢印キーで発火順を確認、スクショで記録
- **実機 Quest 未検証**と明記（insert の dip 演出・post 切替の見た目は現場調整前提）

## 見送り（今回スコープ外・将来課題）

- Web → 実機 Quest のシミュレート駆動（control.sim 注入レバー）。本番 show.json に sim 状態が残る事故リスクの設計が必要
- cue の跨カメラ再利用・キー空間統一（id vs index）
- セグメント複数 insert（現状 1 区間 1 insert）
- exit insert の切替先フィルタ（`toCamera`。現状は離脱先を問わず発火）

## 経緯

- 2026-07-19: opus 調査 2 体（Web 卓構造 / Unity 契約）→ 設計統合。スキーマは「schedule.entries 拡張」「機能別配列」「timeline 新設」の 3 案から timeline 新設を採用（タイムライン第一級の UX と 1:1 対応・既存 schedule は無改造で後方互換）
