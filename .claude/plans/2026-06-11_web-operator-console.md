# Web オペレータ卓 — Unity スクリーンの遠隔制御（映像選択・合成範囲指定）

2026-06-11 起案。ビデオ審査プロトタイプ（廻リ視）に向けて、web-compositor を「合成テスト用おもちゃ」から「**Unity の本番映像を制御するオペレータ卓**」へ作り替える。企画書は運用ツールを何も想定していないため、設計は自由。既存 web は素材（WebGL 合成・マスク描画 UI・キャプチャ/ギャラリー）として流用する。

## ゴール体験

体験者が Quest で歩いている間、オペレータ（PC ブラウザ）が：

1. **カメラ状態を見る** — どのカメラがアクティブか、各 /health
2. **演出（cue）を発火する** — 「血の手形」等をボタンで ON/OFF、フェード付き
3. **合成範囲を作る** — 配信フレームをキャプチャ → マスクをブラウザで描く → そのまま Unity の合成に反映
4. **見た目を調整する** — vignette/grain/scanline 等のポスト FX を Unity にライブ反映

## アーキテクチャ

```
[スマホ×3 fixed-cam-streamer] --MJPEG--> [Quest/Editor Unity] ←─ long-poll ─┐
        │                                  （レンダラ・ゾーン切替は自律）      │
        └────── MJPEG（プレビュー用直接続） ──> [PC: server.py :8099] ←─ fetch ─ [ブラウザ UI]
                                               state の正 = show.json
                                               素材 = captures/ + masks/
```

- **状態の正は PC サーバ**（`show.json`）。Unity もブラウザもサーバから読む
- **Unity → サーバは long-poll**（HTTP GET `/state?rev=N`、変更まで最大 25s ブロック）。
  WebSocket を使わない理由: Python 標準ライブラリ + UnityWebRequest だけで完結し、依存追加ゼロ（`Packages/manifest.json` 不変、pip も不要）。発火レイテンシは実質ゼロ
- カメラ切替は従来どおり **Unity の PlayerZoneTracker が自律**。Web からは手動 override のみ（審査ビデオ撮影時の保険）

## データモデル（show.json）

```jsonc
{
  "rev": 42,                      // 変更ごと ++。long-poll の差分判定
  "cameras": [
    { "id": "A", "sourceId": "Phone01" },   // Unity 側 CameraSource 名と対応
    { "id": "B", "sourceId": "Phone02" },
    { "id": "C", "sourceId": "Phone03" }
  ],
  "cues": [
    {
      "id": "bloody-hand",
      "name": "血の手形",
      "camera": "B",                          // この cue が属するカメラ（情報用）
      "maskUrl": "/masks/bloody-hand.png",     // R ch、スクリーン枠空間
      "sourceUrl": "/captures/hand-loop.mp4",  // mp4(H.264) or png/jpg
      "strength": 1.0, "loop": true,
      "fadeIn": 0.5, "fadeOut": 0.5
    }
  ],
  "post": { "exposure": 0, "contrast": 1, "saturation": 1, "temperature": 0,
            "vignette": 0.25, "grain": 0.06, "scanline": 0 },
  "control": {
    "activeCue": null,            // 発火中 cue id（null = 停止）
    "cameraOverride": null        // null = ゾーン自律 / "A"|"B"|"C" = 手動固定
  }
}
```

## サーバ API 追加（capture-server.py → server.py）

| メソッド | パス | 用途 |
|---|---|---|
| GET | `/state?rev=N` | rev > N になるまで最大 25s ブロックして show.json を返す（long-poll）。rev 省略なら即時 |
| POST | `/state` | show.json の部分更新（UI から）。rev++ |
| POST | `/command` | `{type:"playCue"|"stopCue"|"setCameraOverride"|"setPost", ...}` → show.json 反映 + rev++ |
| POST | `/masks?name=` | マスク PNG 保存（UI のマスク canvas から） |
| GET | `/masks/<name>` | マスク配信（Unity がロード） |
| POST | `/unity/heartbeat` | Unity が現状を報告（アクティブカメラ・recv fps・発火中 cue）。UI 表示用 |
| GET | `/unity/status` | 直近 heartbeat を UI へ |

既存 API（`/save` `/captures/*` `/reveal` `/prompts*`）は温存。

## Unity 側 新規/変更

| ファイル | 内容 |
|---|---|
| `Streaming/OverlayCueData.cs` 新規 | cue のプレーンデータ版（ScriptableObject 非依存）。OverlayCue(SO) は thin wrapper 化して両対応 |
| `Streaming/ScreenOverlayController.cs` 変更 | `PlayCue(OverlayCueData)` を受ける形へ。**URL ソース対応**: mask = UnityWebRequestTexture、clip = `VideoPlayer.url`（HTTP 直再生、DL 不要） |
| `Streaming/ShowControlClient.cs` 新規 | long-poll ループ（async + CancellationToken）。state 差分を Controller / Registry / マテリアル post params へ適用。heartbeat 送信（2s） |
| `Settings/ShowServer.asset` 新規 | サーバ host/port の ScriptableObject（Phone と同じ現場書き換え方式、host は commit しない） |
| `Settings/Cameras/Phone03.asset` 新規 | 3 台目 |

ポスト FX は ScreenComposite シェーダのプロパティに直接マップ（前コミットで実装済み）。

## Web UI 作り替え（index.html / main.js 再構成）

タブ 3 枚に再編：

1. **🎛 コンソール**（新規・既定タブ）
   - カメラ A/B/C カード: ライブサムネ（MJPEG 直結）+ /health + アクティブ表示（heartbeat 由来）+ 手動 override ボタン
   - cue 一覧: 発火/停止ボタン、strength スライダ
   - post FX スライダ → `/command setPost` で Unity 即反映
2. **✂ Cue エディタ**（既存マスク UI を移設）
   - カメラ選択 → 配信フレームをキャプチャ → 既存ペイント/スプリット/フェザー UI でマスク描画 → `/masks` 保存
   - ソース選択: ギャラリー（captures/）から動画 or 静止画 → cue として保存
   - プレビュー: 既存 WebGL パイプラインの **simple 合成 + post**（= ScreenComposite と同 ALU）で Unity の見た目を再現。ラプラシアン/色マッチは「プレビューのみ（Unity 未実装）」とバッジ表示
3. **📁 素材 / プロンプト**（既存ギャラリー + プロンプト管理を統合）

## フェーズ分割（各フェーズで動くものを締める）

| | 内容 | 検証 |
|---|---|---|
| **T1** | server.py: state/command/masks/heartbeat + show.json 永続化 | curl で rev 進行・long-poll ブロック確認 |
| **T2** | Unity: OverlayCueData 分離 + URL ロード対応（mask/clip） | Editor Play + ローカル png/mp4 URL で手動 PlayCue |
| **T3** | Unity: ShowControlClient（long-poll 適用 + heartbeat） | fake streamer + curl POST /command → Editor 画面で cue 発火 |
| **T4** | Web: コンソールタブ（発火・post・カメラ override・status 表示） | ブラウザ→Unity E2E、発火レイテンシ体感確認 |
| **T5** | Web: cue エディタ（キャプチャ→マスク→cue 保存の一気通貫） | 実スマホで「血の手形」cue を現場フローで作成 |
| **T6**（任意） | ゾーン進入→cue 自動発火の設定 UI / 色統計マッチングのシェーダ移植 / 1周目録画→3周目再生 | 審査ビデオの絵づくりに必要なら |

T1–T4 で「ブラウザから Unity の映像を操る」が成立。T5 で現場運用（その場でマスクを作り直す）が成立。

## リスク / 注意

- **動画コーデック**: Quest の VideoPlayer は H.264 mp4 が安全。現 UI の録画は webm(MediaRecorder) → `MediaRecorder('video/mp4;codecs=avc1')` 対応ブラウザならネイティブ mp4、ダメなら server に ffmpeg 変換エンドポイントを足す（T5 で判断）
- **Quest 実機 ←→ PC**: 同一 LAN 必須。ShowServer.asset の host は Phone*.asset 同様「現場書き換え・コミット禁止」リストに追加
- **同時編集**: UI 複数枚で開いた時の競合は rev で last-write-wins（運用 1 人前提、対策しない）
- **ScreenOverlayController の発火経路が 3 系統**（キー / Web / 将来ゾーン）になる — 単一の `PlayCue(data)` 入口に集約し、優先順位は「最後の命令が勝つ」で統一
- web-compositor README / memory/web_compositor.md / rules/streaming.md は T4 完了時に一括スイープ（ドキュメント同期ルール）

## 未決（ユーザー判断が欲しい点）

1. **Unity 合成後映像のブラウザへの戻し**（オペレータが Quest 内の絵を確認する手段）— 今回は scrcpy/キャスト併用で逃げる想定。Web に組み込むなら別フェーズ
2. cue 発火のキーボード操作は **Web と併存**でいいか（残す前提で計画）
