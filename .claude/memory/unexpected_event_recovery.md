---
name: unexpected-event-recovery
description: 2026-10-21。Quest の休止・アプリの固まり・通信の揺れ・本編中の異常・演出の詰まりからの復帰の全体設計。休止を数えない時計、要再登録の永続、タブレットの強制リセット、/status の health、リセット工程の隔離。
metadata:
  node_type: memory
  type: project
  originSessionId: a623a804-563e-4c52-b7c0-4d2940ff269c
  modified: 2026-10-21T14:48:37.637Z
---

ユーザー依頼（2026-10-21）「クエストが休止してしまった、なんかバグってる、みたいな想定外の事態の想定を複数パターン検討し、すべて解決するよう、全体としてのデザインを考えて」→「設計書は書かなくて良い、自己レビューで良ければ実装」。

## 全体の考え方

1. **正は Quest。タブレットはスタッフの唯一の窓**。本編中も Quest の異常をタブレットが名指しして直し方を言う（それまでは本編中は通知の対象外だった）
2. **HMD を被らずに必ず抜け出せる**。右 A 2 秒長押しと同じリセットをタブレットの「困ったとき → 体験を中断してやり直す」（1.5 秒長押し）から呼べる。拒否は位置合わせ作業中だけ
3. **誰も見ていない時間で体験を進めない**。休止の秒数を体験の時計に入れない
4. **1 か所の失敗で全体を止めない**。リセットの各工程を隔離し、例外を数えてタブレットへ出す

## 事態ごとの扱い

| 事態 | 何が起きていたか | いま |
|---|---|---|
| HMD を外して休止 → 被り直す | 休止の秒数（実測 232 / 304 秒）が 1 フレームで入り、本編の時間切れ・締めの 15 秒で即終幕 | `ShowClock`（1 フレーム 0.25 秒で切る）を `ShowRunDirector` / `OutroDirector` が読む |
| 休止と固まりの区別 | どちらも questTick が止まるだけで、固まった Quest に「電源ボタンを押す」と案内していた | `health.paused`（OnApplicationPause で即組み直し）。paused でないのに止まったら固まり |
| 本編中の再センタリング | ずれたまま動き続け、誰にも見えない | `staffSetup.position=recenter` は本編中も出ている。タブレットが本編中も知らせる |
| アプリ再起動 | 要再登録の印が消え、ずれた位置合わせが有効として読まれた | `registration.json` に `needsReReg` を書く。B 確定で false |
| 左コントローラ断 | 気づけず帰還不能へ | `health.ctrlL` |
| 電池 | 誰も読んでいない | `health.battery`（SystemInfo・10 秒ごと） |
| 例外 | logcat だけ | `RuntimeHealth` が数え `health.exceptions` / `lastException` |
| リセット途中の例外 | 後ろの初期化が飛び一部だけ前の人の状態が残る | `RuntimeHealth.Step` / `Raise` で工程ごとに隔離（`ShowControlClient` の 2 経路・`ShowRunDirector.BeginRun`） |
| 解除〜終幕の 7.1 秒にリセット | （台車は 10-30 に廃止）別れの人形が出たまま残る | `TakeRunner.ResetRun` が `EndingFarewellDoll.ResetRun` で畳む |
| 連絡の面が組めない | 乗っ取り待ちが解けず報告が全部止まる | `TakeoverInputBlocked` に `IsBuilt` |
| 通信の一瞬の揺れ | 1 回の失敗で説明が止まる | タブレットは 2 回連続で offline |
| 体験の途中で詰まった | タブレットのリセットは本編中拒否・卓の操作は実機に届かない（`BakedOnly`） | 強制リセット（`/tablet/pulse` の `force:true` → `VisitorPortal.ForceResetProvider` → `OvrControllerBridge.TryForceResetFromTablet`） |

## タブレット側（index.html）

- `questStallKind()` が tick の止まりを paused / hung / network / legacy に分ける。hung のときは電源ボタンを案内しない（押すと起きている Quest を休止させる）。focusLost は paused 扱い
- 本編中の異常は `staffRunProblems` → `#staffRunAlerts`。6 秒続いたら名指し、音は一度だけ（休止して外した・電池・例外・台車は表示だけ）
- 「困ったとき → 体験を中断してやり直す」1.5 秒長押し → `requestStaffReset(true)`。health の無い旧 Quest に拒否されたら右 A 2 秒を案内する
- 取得失敗は 2 回連続で offline（`POLL_FAILS_BEFORE_OFFLINE`）
- 代役は `tools/visitor-ui/staff-mock-server.cjs` の場面 hung / removed / recenter / ctrl-off / aligning。検証は `verify-runtime.cjs`（89 本）と `flow-walk.cjs` の trouble 区間

## 罠

- ⚠ 卓（Web）の ⏭ / ■ / ▶ は実機では届かない（`ShowControlClient` は実機で常に BakedOnly）。現場の出口はタブレットの強制リセットと右 A
- ⚠ 強制リセットは設定の受理番号を照合しない（前の人の設定が残っていても戻すのが目的）。起動 ID（portalSessionId）だけは照合する
- ⚠ `ShowClock` は `Time.frameCount` で 1 フレーム 1 回だけ進む static。演出の時計（`Time.time`）は Unity の maximumDeltaTime が既に切っているので触っていない
- 未対応: アプリが落ちたあとの自動再起動（Quest のホーム画面が出る）。タブレットは「繋がらない」としか言えない
- 未対応: 導入の段 0 で位置が解けないまま止まる（PLAUSIBLE）。要再登録がタブレットに出るので、位置を合わせ直して強制リセット
- 実機未検証（2026-10-21 時点）。休止 → 被り直しで終幕へ飛ばないか、paused が実機で立つかは被って確かめる

関連: [[staff-setup]] [[visitor-tablet]] [[romi-cart-esp32]]
