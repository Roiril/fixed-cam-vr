---
name: kids-room
description: 2 周目 B の別の場所（子供部屋の動画）。音・押し始めで人形の落ちる所へ飛ぶ・報告しても最後のコマまで見せ切るの現行の契約（0374 / 0375）
paths:
  - "Assets/Scripts/Streaming/TakeRunner.cs"
  - "Assets/Scripts/Streaming/TakeRunnerLogic.cs"
  - "Assets/Scripts/Streaming/ScreenOverlayController.cs"
  - "Assets/Scripts/Streaming/Sound/SfxPlayer.cs"
  - "Assets/Scripts/Streaming/Sound/ShowSoundDirector.cs"
  - "Assets/Tests/Streaming/KidsRoom*Tests.cs"
  - "tools/ingest-kidsroom-audio.py"
  - "tools/test_kids_room_log.py"
---

# 子供部屋（2 周目 B の別の場所）

**現行の契約だけを書く。** 変わったら行を書き換える（`rules/doc-sync.md`）。体験者に見えることは `canon/NOW.md`。

2 周目 B の演出（`L2C1#0`）は、人形が後ろを付いて来るカット → 子供部屋の動画（cue `TakeRunner.KidsRoomCueId` = `backrooms_B_in`・5.9 秒）
→ 人形がこちらを向く最後のコマ（`backrooms_B`・1.625 秒）→ ライブ。`dismissible`。
`skipIfDismissed = "L2C0#0"`: 2-A の演出が報告で畳まれていたら、この演出は丸ごと出ない（0378・`rules/mark-advance.md`）。

## 押し始めで落ちる所へ飛ぶ（0375）

- 子供部屋の動画のカットが画面に出ていて、再生位置が落ちる所（`TakeRunner.KidsRoomFallSec` = **4.2 秒**）より前のとき、
  報告の長押しを始めた瞬間に落ちる所へ飛ぶ。1 演出 1 度（引き返して頭から再演したらもう 1 度）。落ちる所以後に押したら何もしない
  — `TakeRunner.TryJumpKidsRoom` / `ShouldJumpKidsRoom`。入口は `NotifyVisitorMarkStarted` の頭（この演出は `markStartCueId` を持たない）
- 画は押した瞬間の画から落ちる所の画へ **0.4 秒**（`KidsRoomJumpFadeSec`）で重ねる。重ね始めるのは落ちる所の画が出てから
  — `ScreenOverlayController.BeginSeekJump`（`rules/screen-overlay.md`）。動画の準備中に押したら重ねず、出た時から落ちる所で流す（`mode=prep`）
- カットの尺は飛んだ分だけ縮め（`KidsRoomJumpSkipSec`）、落ちる所の画が出るまで待った分だけ延ばす（`TickKidsRoomLand`）— `TakeRunnerLogic.ShortenCurrentStep`。
  最後のコマの尺は変えない
- ⚠ 落ちる所は動画のコマで測った値。**動画を差し替えたら測り直す**（測り方は `KidsRoomFallSec` の注釈。重ね終わり 4.6 秒より後に人形が入ること）。
  `analyze-xp-log.py` の `KIDS_ROOM_FALL_SEC` と対
- ⚠ 画面（オーバーレイ）が無い・動画が出ていない（`VideoTimeSec` が NaN）なら飛ばない

## 押さずに落ちる所の線を越えたら飛ぶ（0380）

- `layout.lines` の線 `TakeRunner.KidsRoomFallLineId` = **`kids_B_fall`** の向こう側に居るあいだ、押し始めと同じ処理（`TryJumpKidsRoom`）で落ちる所へ飛ぶ
  — `TakeRunner.ShouldJumpKidsRoomByLine`。見るのは越えた事象ではなく向こう側の状態（`LineSideLogic.Beyond`）で、
  動画が出る前に越えていた人も、動画が出た最初のフレームで飛ぶ。押し始めと線のどちらが先でも 1 演出 1 度
- 枠は台本に子供部屋のカットがあるときだけ取る（`KidsRoomFallLineSlot`）。線が layout に無ければ `WarnMissingLines` が警告し、何もしない
- 線の位置は show.json（git 管理外）。B の通路の上端、2-C との境 z=0.45（x −0.9〜−0.3・`dir` both）。卓で動かせる。
  届く線 `trail_B_reach`（z=0.25）より先で、2-C の区間（z&gt;0.45）へ入る前に必ず越える所に置く

## 報告しても最後のコマまで見せ切る（0375）

- 子供部屋の動画のカットか、その次の最後のコマ（ライブでないカット）で報告が成立しても、最後のコマが尺どおり終わるまで畳まない。
  報告はその時点で成立して数える（返り値 `Dismissed`）。最後のコマの終わりで `VisitorDismissed` として乱れでライブへ戻る
  — `TakeRunner.ResolveKidsRoomDismissAfterStep` / `TakeRunnerLogic.NotifyMarkPressed(dismissAfterStep)`
- 動画の前のカット（人形が後ろを付いて来る所）は `markClear`（0382）。押すと人形だけ消え（`ClearStepMaterial` が `_cgLayer.Hide`）、演出は続いて線を越えれば子供部屋へ飛ぶ。
  旗は show.json（git 管理外）の `L2C1#0` の step 0。子供部屋での報告は別の異変に数える（`VisitorReportTally.Record(afterClear)`）
- 区間を出た（yield）・watchdog・卓の中止・ラン開始は即畳む

## 観測（`ShowTelemetryHost` と `analyze-xp-log.py` の `kids_jump_findings` を対で直す）

- `ev=kidsjump take= from=<飛んだ時の再生秒> to=4.20 mode=xfade|prep by=mark|line n=` — 押し始め（mark）か落ちる所の線（line）で飛んだ。
  `by` の語は `TakeRunner.LastKidsRoomJumpBy` と解析器の `KIDS_JUMP_BY` が対
- 台本があれば、子供部屋の動画のカットが出たのに飛ばず、人形が入る前（`KIDS_ROOM_DOLL_IN_SEC` = 112/24 秒）に区間を出た演出を WARN にする（人形が落ちる所を見ていない）
- `ev=kidsland wait=<秒> timedOut=0|1 n=` — 落ちる所の画が出た。飛んだのに続かなければ FAIL
- `ev=kidshold take= step= until= n=` — 報告を保った。その演出が `why=done` で終わる・最後のコマ（`until`）が出る前に `why=mark` で畳む、なら FAIL
- 機械の門: `KidsRoomJumpTests`（C#）と `tools/test_kids_room_log.py`（書き手と読み手の照合）

## 音（0374）

2 周目 B で飛ぶ別の場所は子供部屋の動画（cue `backrooms_B_in`・5.9 秒）→ 静止画（`backrooms_B`）。
動画は `audioOutputMode = None` で再生し、その動画の音を一撃として別に鳴らす。

| 何 | 値 |
|---|---|
| 音源 | `sfx_kidsroom`（`SoundCue.KidsRoom`・変種 1）。電灯と木馬が揺れ、人形が畳に落ちる鈍い音 |
| 作り | `py -3.11 tools/ingest-kidsroom-audio.py`。音声付きの原本 `tools/web-compositor/captures/kidsroomB_noeyes_20261114_audio.mp4` の音を**無加工**で 48kHz・ステレオの PCM へ抜く。原本の SHA-256 は定数で照合する |
| 高さ | **-24.6 LUFS / 尖頭 -0.45 dBTP**（素のまま）。尖頭は -3dBTP の線を超えて `sound-lint.py` が NG を出すが、音量を変えない（ユーザーは動画ごと聴いて採った） |
| 尺 | **6.04 秒**。動画のカット（5.9 秒）より長く、静止画のカットの頭まで鳴る |
| 鳴らす所 | `TakeRunner.BeginStep` の `dollCall` と同じ行。cueId が `TakeRunner.KidsRoomCueId` のカットが画面を取った頭で `_sound.PlaySpot(SoundCue.KidsRoom)` を 1 回。飛ばしたカットでは鳴らさない |
| 定位 | **2D**（`MonoRequired` に入れない）。動画の音そのままなので出どころを作らない |
| 退き | 劇伴の退き無し（`DuckFor` 0）。別の場所のあいだ劇伴は風へ入れ替わっている |

- **止める縁**: 別の場所が途中で畳まれたとき（報告で畳まれた `VisitorDismissed`・区間を移った `Yielded`・watchdog・卓の中止・ラン開始）だけ、
  `ShowSoundDirector.StopSpot(SoundCue.KidsRoom, TakeRunner.KidsRoomStopFadeSec)` で **0.2 秒**の聴感直線で退かせる
  （`SfxPlayer.FadeOut`。その音の声だけを退かせ、他の一撃は残す）。判定は `TakeRunner.EndTake`（`Completed` 以外）と `CleanupActive`
- ⚠ **静止画のカットへ自然に進んだとき・演出が自然に終わったときは止めない。** 6 秒の音を鳴り切らせる
- **押し始めで落ちる所へ飛んだとき**（0375）は、落ちる所の画が出た瞬間（`ScreenOverlayController.SeekShownCount` の増分）に
  `ShowSoundDirector.RestartSpotAt(SoundCue.KidsRoom, TakeRunner.KidsRoomFallSec, TakeRunner.KidsRoomJumpFadeSec)` で鳴らし直す。
  いまの声を **0.4 秒**の聴感直線で退かせ、音源の **4.2 秒**から新しい声を 0.4 秒の等パワーで立ち上げて重ねる（`SfxPlayer.PlayFrom`）。
  ⚠ 鳴らし直しは `PlayedCount` / `LastCue` を動かさない（`ev=sfx id=KidsRoom` は頭の 1 行のまま）
- **報告しても畳まない**（0375）: 動画のカットか最後のコマで報告が成立しても、最後のコマが終わるまで畳まない。その間は音も退かせない
  （畳む縁は最後のコマの後の `VisitorDismissed` で、その時点で音はほぼ鳴り終わっている）
- ⚠ **風（`bed_wind`）は鳴り続ける。** 別の場所のあいだ劇伴を切って風を鳴らす（0131）契約は変わらない。この音は風の上に重なる
- ⚠ **cue id の頭は `backrooms` のまま**（`TakeRunner.OtherworldCuePrefix`）。変えると風と時計の `?` も黙って止まる
- 観測: 走行ログの **`ev=sfx id=KidsRoom`**。`analyze-xp-log.py` の `kids_room_findings` が、
  動画のカットが画面を取った（`ev=step played=1`）のに鳴っていなければ FAIL にする（`KIDS_ROOM_CUE` と `KidsRoomCueId` は対・`tools/test_kids_room_log.py`）。
  ⚠ 同じフレームで別の一撃が鳴ると `ev=sfx` は 1 行にまとまる（`LastCue` が 1 つしか出ない）
- 機械の門: `KidsRoomAudioTests`（取り込み忘れ・尺が縮んでいないか・頭で 1 回・自然な進行では止めない・畳まれたら退かせる・飛ばしたカットでは鳴らさない）。
  鳴らし直しの口は `KidsRoomJumpTests`（数えない・前の声は退き、新しい声は立ち上がる・音源の外の秒は鳴らさない）

