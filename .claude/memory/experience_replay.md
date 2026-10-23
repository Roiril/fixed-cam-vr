---
name: experience_replay
description: 紹介動画用に、Quest の体験 1 回を記録して PC の Editor で本番の Main シーンへ流し直し、広い画角で mp4 にする仕組み（2026-10-23）。回し方・時計・HMD 無し Play の罠
metadata:
  node_type: memory
  type: project
  originSessionId: dbe1d34e-946b-4f4e-946a-40ecfc9ae3b8
  modified: 2026-10-23T09:55:58.022Z
---

# 体験の記録と PC での撮り直し（2026-10-23）

ユーザー依頼: 「クエストデフォルトの録画機能だと、録画範囲が狭くて全然分からない」。
決めた形（ユーザー了承）: スクリーンの合成映像ではなく**合成の材料**を Quest で残し、PC で本番の処理を通し直して撮る。
パススルー（現実）は PC に無いので導入の現実は黒（「しょうがない」）。体験者本人は別に撮る。見た目の差は許容。音も撮る。

## 回し方

```bash
py -3.11 tools/quest-xprec.py on --serial <機>      # 次の引き渡しから録る（既定は OFF）
#   … 体験を 1 回 …
py -3.11 tools/quest-xprec.py pull --serial <機> --delete   # → Logs/xprec/<日時>/
.\tools\unity.ps1 replay -Set rec=latest,view=eye    # → G:\マイドライブ\shubie\renders\mawarimi-replay\<日時>_eye.mp4
.\tools\unity.ps1 replay -Set rec=latest,view=follow # 頭の後ろ上から
py -3.11 tools/quest-xprec.py compare Logs/xprec/<日時> <出力フォルダ>/replay_xp.log   # 端末と再生の出来事の突き合わせ
py -3.11 tools/quest-xprec.py off --serial <機>     # 展示本番の機は OFF
```

値: `rec`（フォルダ名 / latest）・`view`（eye / follow）・`fov`（縦の度。eye 72 / follow 62）・`w` `h`（既定 1920x1080）・
`fps`（既定 30）・`msaa`（既定 1）・`maxsec`・`out`。GUI の Editor からは `Tools/FixedCamVr/Diagnostics/Replay Recording (wide capture)`
（Play に入り、終わると Play を出る）。

## 何を残すか（`files/xprec/<日時>/`）

| ファイル | 中身 |
|---|---|
| `track.bin` | 毎フレーム: 頭と両手のワールド姿勢・生の入力ビット（`RawInput`）・言語・軽減・報告の累計 |
| `cam_Phone0N.mjr` | 受けて展開できた JPEG そのまま（MJR1・pts は記録の頭から） |
| `xp.log` | その間の `[XP]` |
| `meta.json` | 同梱 show.json の contentId・カメラの並び・registration.json の本文 |

録り始めは**スタッフの準備画面が閉じた瞬間**（`ShowControlClient.StaffSetupActive` の true→false）。
録り終わりは準備画面が再び開く・終幕から 75 秒・15 分・休止。上限 3GB を超えたら映像だけ捨てる。

## 再生の作り

- `XpReplayRun`（Editor）が Main.unity を開いて Play に入るだけ。流すのは Assembly-CSharp の `XpReplayDriver`、
  撮るのは `XpReplayCapture`。SessionState で受け渡し、`RuntimeInitializeOnLoad(BeforeSceneLoad)` で立つので、
  シーンの全員の Awake / Start が `XpReplay.Active` を見られる
- 入力は `OvrControllerBridge.ReadRawInput` へ、カメラの JPEG は `CameraStream.TryTakeFrame` へ差し戻す。
  そこから先（区間・演出・合成・エージェント画面・音）は本番の処理
- 頭は記録のワールド姿勢へ OVRCameraRig を動かして合わせる。registration.json は記録の版を
  再生専用の保存先（productName を差し替えたフォルダ）へ書くので、区間の判定が端末と揃う
- 再生中は焼き込みだけで動き、卓・heartbeat・:8090・発見・配信スマホへ繋がない（卓の操作が混ざらない）
- 題字の開閉と報告は、ボタンで再現されなかったら 1 秒後に記録へ合わせる（自動走行の記録はボタンを押していない）

## ⚠ 罠

- ⚠⚠ **HMD 無しで Main.unity を Play すると 2 か所で死ぬ。** XR の起動時初期化が HMD を待って固まる。
  MRUK のネイティブが `persistentDataPath` の日本語（productName「廻リ視」）で文脈を作れず、続く検査で Editor ごと落ちる
  （exit -1073740791）。`ReplayXrGuard` が Play の間だけ、メモリ上で `InitManagerOnStart` を下ろし、productName を英字にする。
  ディスクの設定は変えない（走らせた前後で ProjectSettings.asset と XR の asset のハッシュが一致することを確かめた）
- ⚠⚠ **`Time.captureDeltaTime` は unscaled の時計に効かない。** 本番は unscaled / realtime を約 140 か所で読むので、
  コマ送りにすると体験が実時間で進み、映像だけ縮む（20 秒の記録が 12.3 秒の動画になった）。
  `AudioRenderer` も batchmode では 1 サンプルも返さなかった。⇒ **再生は実時間**。撮影は出すべきコマの時刻が
  来たときだけ描き、遅れたコマは前の絵を繰り返す。音は AudioListener の `OnAudioFilterRead` で写す
- ⚠ batchmode では `WaitForEndOfFrame` が来ない。撮影は `[DefaultExecutionOrder(32000)]` の LateUpdate で自分で描く
- ⚠ `unity run` は `-quit` を必ず張るので、Play を続ける再生は `unity.ps1 replay` が Unity.exe を直接 batchmode で起こす
- ⚠ 撮影の重さはほぼ GPU。この PC（Intel UHD）で 1080p の 1 コマが MSAA 4 で 55ms・MSAA 1 で 22ms
  （30fps 出力で繰り返し 49% → 10%。全編 198 秒では 28ms・繰り返し 15%）。圧縮は Intel Quick Sync（`h264_qsv`）が使えればそちらで、待ちは 1ms。
  x264 を CPU で回すと再生と取り合う。終わりの行「1 コマあたり」で内訳が出る
- ⚠ `view=follow` は頭の 1.4m 後ろに立つので、導入の隔離の殻（ContainmentShell）が前を塞ぐ。本編から使う
- ⚠ 自動走行（`--walk`）の記録は HMD が机に置かれたまま。頭がスクリーンを向かないので、eye でもスクリーンが端に寄る。
  人が被った記録では正面に来る

## 検証（2026-10-23・クエストβ・自動走行 1 回）

- 記録: 198 秒・16784 行（85Hz）・カメラ 3 台 各 5700 コマ前後（毎秒 29）・367MB・stop=finished
- PC で全編を再生: 映像 198.37s / 音 198.40s / 進行 199.4s。`compare` で端末と再生の出来事 86 件中 84 件一致、
  時刻の差は中央 0.13s。食い違い 2 件は `comms id=Walk` の順序と `commsPossess` の数値の揺れ
- 同じ時刻の端末の eye.mp4 と並べ、スクリーンの中身（1 周目の手形と人形・2 周目のカーテン）が一致することを見た
- 自動走行は題字と報告をボタンを押さずに済ませるので、再生ではそれぞれ 1 回ずつ記録に合わせて補った（`[Replay] 題字を…` / `報告を…`）
- まだ: 人が被った走行の記録（ボタンだけで題字と報告が再現されるか）・Release ビルドで録らないこと
