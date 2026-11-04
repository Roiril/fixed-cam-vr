---
name: streaming
description: 廻リ視のコードの索引。どの話題の rule がどのファイルを持つかと、全体にまたがる契約 5 つ。経緯の全文は reference/archive/ の凍結版
paths:
  - "Assets/Scripts/**"
  - "Assets/Tests/**"
---

# 廻リ視のコード — 索引と、全体にまたがる契約

2026-10-22 に、351KB あった旧 `streaming.md` と 188KB あった旧 `show-design.md` を、
**現行の契約だけの話題別 rule** に分けた。話題別の rule は、該当ファイルを触ると自動で載る。

## 話題別の rule

| rule | 持つもの（接頭辞） |
|---|---|
| [stream-link.md](stream-link.md) | Mjpeg* / Stream* / CameraStream* / CameraSource* / Discovery / ShowServerSource / ShowControlClient / BakedShowManifest / ShowAssetResolver / VisitorPortal*（:8090）/ 診断 |
| [show-run.md](show-run.md) | ShowRun* / ShowClock / Take* / Timeline* / CueScheduler / Zone* / Segment* / LineCross / StartSpot / Approach / Overlay* / CameraSwitch* / Bgm* / LapCounter |
| [intro-title.md](intro-title.md) | Intro* / Title* / ContainmentShell / CrtScreenMesh / Attic / HmdOnboardingLogic |
| [comms.md](comms.md) | Comms* / UnauthorizedAccessEffect / AlertEscalation / HorrorRelief / ShowLanguage |
| [ending.md](ending.md) | Ending* / Outro* / VisitorReportTally |
| [screen-post.md](screen-post.md) | ScreenComposite・MjpegScreen / CameraFeel* / ScreenDecay / Osd* / SignalLost / Heartbeat* / Fx/** |
| [screen-swap-eyes.md](screen-swap-eyes.md) | Glitch* / Swap* / AnomalyEyes* / Eye* |
| [screen-cg.md](screen-cg.md) | Streaming/Cg/** と CG 人形の合成 |
| [staff-input.md](staff-input.md) | Input/** / Tracking/** / OvrBridge/** / StaffSetup・StatusHud・ControllerGuide / WalkGuide* |
| [sound-design.md](sound-design.md) | Streaming/Sound/** と音の道具（まだ分けていない） |

Editor のプレビュー（`Streaming/Editor/`）は、プレビューする対象と同じ接頭辞の rule に従う。

⚠ **rule に書いた数値より、横に書いた記号（定数・メソッド）の値が正。** 2026-10-22 の抽出は 7 体で分担したので、
担当をまたぐ数値は凍結版の古い値を写していることがある（終幕の劣化の戻りが 1.2 秒と書かれ、コードは 0.5 秒だった）。
食い違いを見つけたらコードを正として rule の行を直す。

## 全体にまたがる契約

- **Player は焼き込み専用。** ライブ・端末キャッシュの経路は Editor だけに残る（`ShowControlClient.BakedOnly`）。
  詳しくは [stream-link.md](stream-link.md)
- **show.json は git 管理外の現場設定。** 卓のサーバはメモリの版を配るので、ディスクを直接書いても届かない
  （[memory/show_json_is_live_config.md](../memory/show_json_is_live_config.md)）
- **観測項目は `ShowTelemetryHost`（C#）と `tools/analyze-xp-log.py` を対で直す。** 片方だけだと沈黙して食い違う。
  観測するのは「段が進んだ」ではなく「画に何かが出た」
- **HMD 内の文言を変えたらフォントを焼き直す**（静的ベイク。[memory/hud_font_and_preview.md](../memory/hud_font_and_preview.md)）。
  文字の大きさの正は `HmdTextStyle`（[memory/hmd_text_style.md](../memory/hmd_text_style.md)）
- **体験者に見えることが変わったら `canon/NOW.md` の行も直す**（`rules/doc-sync.md`）

## 経緯を読むとき

旧本文は凍結してある（追記しない。行番号は腐らない）。話題別 rule の出典 `S:` / `D:` はこの行番号。

- `reference/archive/streaming-2026-10-22.md`（S:）— 配信 61〜230 / 表示モデル 336 / show.json の契約 387 / cue スケジュール 427 /
  タイムライン・演出・合成・BGM の大節 536〜3295（演出の語彙 598・連続と継ぎ目 652・骨格と乱れ 736・通過ライン 1581・
  素材と層 1622・闇の目 1801・左右分割 2045・CG 人形 2655・合成 2768・BGM 3218）/ :8090 3295 / 発見 3338
- `reference/archive/show-design-2026-10-22.md`（D:）— 座標駆動 85 / ゾーン校正 155 / 振動 352 / 骨格 395 / 接近 1614 / 周回と cue 1810
- `reference/archive/troubleshooting-2026-10-22.md` と `git-workflow-2026-10-22.md` も同じ日に凍結した
