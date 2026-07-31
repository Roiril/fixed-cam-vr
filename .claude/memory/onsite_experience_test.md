---
name: onsite-experience-test
description: "廻リ視の体験を実機で丸ごと検証する手順（[XP] テレメトリ / 自動走行 / logcat 収集の罠 / 2026-07-30 のベースライン実測）"
metadata: 
  node_type: memory
  type: project
  originSessionId: 83b2ae19-6926-4454-a4df-b5d350711b24
  modified: 2026-07-31T03:36:48.059Z
---

# 実機で体験を丸ごと検証する

**HMD を被らずに、導入 → 3 周 → 終了を通して観測できる。** 2026-07-30 に一式を用意した。
体験の論理（周回・演出・録画）と映像の質（受信 fps・砂嵐・遅延・表示 fps）を、
どちらもログの数値で判定する。

## 3 点セット

| 何 | どこ | 役割 |
|---|---|---|
| テレメトリ | [`ShowTelemetryHost`](../../Assets/Scripts/Diagnostics/ShowTelemetryHost.cs) | `[XP]` タグで 1 行 1 イベント + 2 秒ごとの集計。**観測専用**（既存の公開イベントとプロパティを読むだけ・状態を書き換えない）。Development ビルドでのみ `RuntimeInitializeOnLoadMethod` で自動生成されるので**シーン再生成は要らない** |
| 自動走行 | [`ShowWalkDebugDriver`](../../Assets/Scripts/Tracking/ShowWalkDebugDriver.cs) | `OVRCameraRig` を動かして歩行を合成。既存コードは無改変。経路は `layout.grid` から BFS で解く（出発と到着のタイル以外を踏まない）。**導入の開始ライン（`run.intro.startLineId`）があれば線の中点を法線方向に横切ってから中へ入り**、被り検知（`UserPresentProvider`）を true で上書きする — HMD を被らずに走らせるので、これが無いと導入が永久に始まらない |
| 判定 | [`tools/analyze-xp-log.py`](../../tools/analyze-xp-log.py) | show.json を期待値にして突き合わせ。**起きなかったことを引き算で見つける**のが主目的 |
| 機の選択 | [`tools/quest-fleet.py`](../../tools/quest-fleet.py) | Quest 2 台を交互に使う（熱の低い方を選ぶ・使わない機は寝かせる・APK と設定を揃える）。詳細 [[quest_fleet_two_devices]] |

実行は [`tools/run-quest-xp-test.sh`](../../tools/run-quest-xp-test.sh):

```bash
bash tools/run-quest-xp-test.sh idle 90     # 静置（配信・砂嵐・遅延・表示 fps）
bash tools/run-quest-xp-test.sh walk 300    # 自動走行（導入 → 3 周 → 終了）
SERIAL=<serial> bash tools/run-quest-xp-test.sh walk 300   # 機を指名する
```

`SERIAL` を省くと fleet がいちばん冷えている機を選ぶ。走行の記録・使わない機のスリープ・
2.4GHz 警告まで込み。

## 収集の罠（3 回落として、2026-07-31 に真因が分かった）

**落とした原因は「Unity のログ量」ではなかった。** 2.18MB のログを実測したら Unity 由来は 24% で、
残り 76% は `XrCameraHal` / `PasspointManager` / `libcamerahal` ── **他プロセスがバッファを押し流していた**。
さらに Unity 由来のうち 179KB（3,941 行）は `Debug.Log` に付くスタックトレースだった。

→ 対策は 3 つで 1 組（どれか 1 つでは足りない）。すべて実装済み:

1. **タグを絞る** … `logcat -v time Unity:V DEBUG:V "*:E"`（run スクリプト）。実測 2.18MB → 0.6MB
2. **スタックを出さない** … `Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None)`
   を `ShowTelemetryHost.Bootstrap` で（Development のみ・Warning / Error のスタックは残す）
3. **バッファ拡張を実測確認** … `-G` は端末に拒否されても**黙って成功を返す**。必ず `-g` で読んで出す
   （32M は拒否される。16M は通る）

加えて:

- **ストリーム（`logcat > file &`）は走行中に別の adb を打つと切れる。** daemon が再起動して
  ストリームだけが黙って死ぬ（アプリは最後まで走っていたのに、ログは 50 秒で途切れた）。
  **走行中は adb を一切触らない。** run スクリプトはストリームとダンプ (`-d`) の**両方**を取り、
  `[XP]` 行数の多い方を採用する（片方が壊れてももう片方が残る）
- HMD を被っていないと Quest がアプリをバックグラウンドへ回すことがある（コントロールバーが前面に出る）。
  ただし **Unity の Update は止まらず走り切る**（実測）。`am start` で戻せるが、戻すと
  intent extra が付かないので自動走行は再開しない

## ⚠ 解析が嘘をつく経路（2026-07-31 に塞いだ）

解析は **PC の `tools/web-compositor/show.json` を期待値**にする。ところが実機が読むのは
**端末キャッシュ**（`persistentDataPath/show_config.json`）で、これは**焼き込みより優先される**
（焼き込み < キャッシュ < ライブ）。だから「APK を焼き直した = 設定も新しい」は成り立たない。

実測では PC・焼き込み・Quest 2 台のキャッシュの **4 者がずれていて、正しいのは 1 つだけ**だった
（片方の Quest にだけ `run.intro.startLineId` があり、導入の始まり方が機ごとに違った）。
しかも `timeline.rev` は全部 21 で一致していたので、**rev を見ても気づけない**。

→ 実機が `[XP] ev=config src=... tlrev=... segs=... startLine=...` で**使った設定を報告**し、
`analyze-xp-log.py` が PC の show.json から同じ要約を作って突き合わせ、違えば **FAIL** にする。
実装は `ShowControlClient.ConfigOrigin` / `DescribeConfig()` ↔ `config_from_show()`。
**項目を足すときは C# と Python を対で直す**（片方だけだと沈黙して食い違う）。

食い違ったときの直し方: `python tools/quest-fleet.py reset-config <serial>` でキャッシュを消し、
卓の「📦 ビルド用エクスポート」(`POST /export-build`) で焼き込みを更新してからビルドし直す。

## `layout.grid` を course 座標へ

`ZoneLayoutSolver.CellRect(r, c, rows, cols, tileM, ...)` が正（row 0 = 北端 z 大 / col 0 = 西端 x 小）。
ドライバも登録ビューもここを共有する。**自分で計算し直さない。**

## 2026-07-30 のベースライン（Quest 3・配信 3 台・2.4GHz）

体験の側は成立していた:

- 導入 → 本編 → 終了まで通し、3 周を 69 秒で完走
- **著作された演出 7 本すべてが著作どおりの尺で出た**（3 周目の 3 本は 6.4 / 6.5 / 8.9 秒）
- 1 周目の A・B・C が録画され、3 周目の背景として再生された（`rec/0/L1C{0,1,2}.mjr`）
- 表示 fps 平均 89.5（90Hz 維持）／**本編 69 秒間の砂嵐はゼロ**

映像の側は 2.4GHz が天井になっていた:

- Quest は `kougaku-lab-G`（2457MHz / 802.11n）に接続。同じ AP の 5GHz 側 `kougaku-lab-A`
  （5620MHz）が **RSSI −40 と 2.4GHz より強い**のに Quest に保存されていない。PC も 2.4GHz
- 受信 fps は配信の 2/3 以下（20〜23fps）。走行中の取りこぼし 9,000 フレーム超
- 到着の揺らぎ 平均 40〜64ms・最大 364ms（企画書の「100ms 以内」に対して揺らぎだけで超える）
- 配信 B だけ 60fps（他は 33fps）。`AE_TARGET_FPS_RANGE=[30,60]` が明るい場所で上限に張り付く
- `Phone04.asset` に焼かれた host（192.168.11.40・現場に不在）へ 10 秒ごとに接続を試み続ける。
  show.json の `cameras[3].host` が空でも **`EffectiveHost` は焼き込み値へフォールバックする**

## 2026-07-31 の実測（Quest 2 台・配信 3 台・同じ APK / 同じ設定）

**体験は完全に成立した。両機・両帯域とも FAIL ゼロ。**

- 導入 → 本編（t=55s）→ 終了（t=124s）／3 周完走／**演出 7 本すべてが出た**／録画 3 区間
- **導入演出が最後の段（Swap）まで進んだ**（両機）
- 表示 fps 平均 89.6 / 89.5（90Hz 維持）
- **実機は show.json と同じ設定で走った（出所 live）** ← 新しい照合が通った

映像も 07-30 から大きく改善した:

| | 07-30（2.4GHz・旧 APK） | 07-31 5GHz 機 | 07-31 2.4GHz 機 |
|---|---|---|---|
| 受信 fps（カメラ 1・配信 49.9） | 37.5 | **45.4** | 43.8 |
| 到着の揺らぎ 平均 | 68.6ms | **10.8ms** | 17.0ms |
| 到着の揺らぎ **最大** | 620ms | **25ms** | 125ms |
| 取りこぼし（カメラ 1） | 1,368 | **529** | 1,585 |
| MJPEG 再接続 | 29 回 | **2 回** | 2 回 |

**⚠ 改善の主因は 5GHz ではない。** 2.4GHz の機でも同等の受信 fps・再接続 2 回が出た。
効いたのは **`dbffa7e` の `_everReceived` ガード**（stall watchdog が「一度もフレームを受けていない
stream」では発火しないようにした）で、これが**前回のビルド後にコミットされたため 07-30 の実機には
載っていなかった**。現場に居ないカメラへ 10 秒ごとに張り直していたのが再接続の約半分。

**5GHz が効くのは揺らぎの最大値と取りこぼし。** 平均は 2.4GHz でも足りるが、
**最大が 25ms と 125ms で 5 倍違う**。企画書の「100ms 以内」に対して 5GHz は余裕、2.4GHz は超える。
→ 本番は 5GHz を使う。

残っている WARN:

- 演出 2 本が区間の滞在を超える（`L2C0#0` 尺 3.1s / 滞在 9.2s、`L3C2#0` 尺 8.9s / 滞在 7.8s）。
  **著作の問題**で、速く歩く体験者では途中で場所が変わる
- 最悪フレーム時間 301 / 307ms（起動直後の 1 回）
- 5GHz 機の砂嵐 4.9% は **起動直後の 14.4 秒**（t=5.4〜19.8・接続確立まで）。体験中はゼロ

## ここで見つけて直した 2 件

- **導入演出が Degrade で打ち切られる** → `ShowRunLogic` の導入終了条件に「演出が進行中でない」を追加。
  契約は [streaming.md](../rules/streaming.md) の「体験の骨格」節。
  修正後は **Black → Real → Degrade → Structure → Frame** まで進むようになった（3.5 秒 → 8.7 秒）
- **lag 判定の誤爆で本編 69 秒に再接続 152 回** → 分母を `min(phone_fps, 30)` で頭打ち。
  配信 60fps のとき受信 40fps でも誤爆していた（`recv=39.8/phone=59.4`）。
  修正後は再接続の率が半分になった（0.43 回/秒 → 0.21 回/秒）

## 画像加工が実機に届いていなかった（2026-07-30・ユーザーが実機で気づいた）

**卓では正しく見えて実機だけ素通し**という最悪の破れ方をしていた。実機の実効値を出したら
134 サンプル中 122 が `sat1.00 / vig0.00`（無加工）で、3 周目の区間 post が効いた 12 だけが加工されていた。

- 原因: show.json が `cameras[].post` に **`null` を明示的に書いている**。JsonUtility は
  `null` の入れ子を**既定値の実体**で埋めるので `hasPost = (post != null)` が true に化け、
  **素通しのカメラ個別 post が global の加工を上書き**していた
- 卓は JS の `??` で null → global へフォールバックするので**プレビューは正しい**。
  だから実機を見るまで誰も気づけない
- 修正: `post != null && !post.IsDefaultLike()`（全 11 軸が既定かを見る）。
  契約は [streaming.md](../rules/streaming.md)、回帰は `TimelinePresentFlagsTests`
- **教訓**: 「卓がどう書くか」に依存する present 判定は破れる。**Unity 側で防御する**。
  そして `[XP]` の `mat=` / `post=` のように**実効値をログに出す**のが唯一の発見手段だった

## まだ残っている件（次に見るところ）

- ~~導入演出の最後の段 Swap が出ない~~ → **解決済み（2026-07-30 19:53 の走行ログで実機確認）**。
  `Black → Real → Degrade → Structure → Frame → Swap → Off` の全段が出ている。以下は原因の記録：
  `introPlaying` の判定に `!Holding` を使ったのが誤りだった。`IntroLogic.Holding` は段 0 の
  開始待ちだけでなく **段 3・段 4 の条件待ち**（頭を振っている / 枠を見ていない）でも立つ。
  枠が出た直後に足踏みへ入った瞬間 `introPlaying` が false へ落ち、本編へ飛んでいた。
  → `Active && Stage != IntroStage.Black` に変更。
  **段 4 → 段 5 の進行条件（`fresh` / `centered`）を `[XP]` に出すようにした** —
  どちらで足踏みしているかはログでしか分からない
- ~~2 周目 A の演出が離脱の瞬間に決着している~~ → **仕様どおり。不具合ではない**（誤読だった）。
  この演出は `at:"line"`（`line_2` を横切ったら発火）で著作されていて、自動走行の経路が
  その線を通らなかったので due にならず、`ifMissed:"fireOnExit"` の契約どおり離脱時に決着した。
  他の演出の「遅れ」も全部 `offsetSec`（1.2 / 2.5 / 0.5 秒）と一致する。
  **教訓**: 発火時刻を「区間進入との差」だけで見ると誤診する。**必ず `at` / `offsetSec` /
  `lineId` と突き合わせる**（解析スクリプトも突き合わせるようにした）

## それでも取り切れないときの次の一手

上の 3 点セット（タグ絞り + スタック抑止 + バッファ実測確認）で 2.18MB → 0.6MB になったので、
16M のバッファなら 5 分の走行は収まる計算。**それでも落ちるなら**、`ShowTelemetryHost` が
`[XP]` を端末内ファイル（`persistentDataPath/xplog/`）へ直接書いて後で回収する方式にする。

その正当化は「ログ量」ではなく **(1) 端末側のバッファ設定が黙って失敗するのに依存しなくなる、
(2) USB を繋げない実走行（会場で来場者が被る）でも取れる** の 2 つ。会場実測を取るなら必須になる。
実装するときは `logMessageReceivedThreaded` ではなく **`logMessageReceived`（メインスレッド）** を
購読し、flush は時間周期ではなく `ev=` の遷移行ごとにする（遷移は 265 秒で 83 行しかない。
末尾が落ちると終了判定が消えるのがいちばん困る）。

関連: [[quest_build_and_camera_ip]] / [[show_run_skeleton]] / [[verification_workflow]]
