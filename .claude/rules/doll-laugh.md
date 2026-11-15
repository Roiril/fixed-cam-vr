---
name: doll-laugh
description: カットの dollLaugh（人形の笑い声 1 回・2 周目 A）。音源の切り出し・鳴らす縁・観測の現行の契約
paths:
  - "Assets/Scripts/Streaming/TakeRunner.cs"
  - "Assets/Scripts/Streaming/Sound/SoundCueLogic.cs"
  - "Assets/Tests/Streaming/DollLaughAudioTests.cs"
  - "tools/ingest-doll-laugh.py"
  - "tools/test_doll_laugh_log.py"
---

# 人形の笑い声 1 回（カットの `dollLaugh`）

**現行の契約だけを書く。** 変わったら行を書き換える（`rules/doc-sync.md`）。音全般は [sound-design.md](sound-design.md)。

| 何 | 値 |
|---|---|
| 音源 | `sfx_doll_laugh_once`（`SoundCue.DollLaughOnce`・変種 1） |
| 作り | `py -3.11 tools/ingest-doll-laugh.py`。`bed_doll_one.wav`（SHA-256 を定数で照合）の頭 **2.4 秒**（笑い 1 回。2.4 秒から 2 回目が始まる）を切り、最後の **0.5 秒**だけ聴感直線（`(1-t)^(1/0.6)`・`SoundFade.LoudnessExponent`）で 0 へ退かせる。音量は変えない |
| 鳴らす所 | `TakeRunner.BeginStep` の `dollCall` と同じ行。旗の立ったカットが画面を取った頭で `_sound.PlaySpot(SoundCue.DollLaughOnce)` を 1 回。飛ばしたカットでは鳴らさない。3 周目 C の増えるスクリーンの最後の顔の笑い声は別の音源（`SoundCue.ScreenFaceLaugh`・0386）で、ここでは鳴らさない |
| 定位・退き | **2D**（`MonoRequired` に入れない）。劇伴の退き無し（`DuckFor` 0） |

- 観測: 走行ログの `ev=sfx id=DollLaughOnce`。`analyze-xp-log.py` の `doll_laugh_findings` が、旗のカットが画面を取った（`ev=step played=1`）のに鳴っていなければ FAIL、旗のカットが画面を取っていないのに鳴れば FAIL（`tools/test_doll_laugh_log.py`）。増えるスクリーンの笑い声は `ev=sfx id=ScreenFaceLaugh` なので数えない
- 機械の門: `DollLaughAudioTests`（取り込み忘れ・尺・頭で 1 回・飛ばしたカットでは鳴らさない）
