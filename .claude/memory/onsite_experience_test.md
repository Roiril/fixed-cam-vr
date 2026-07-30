---
name: onsite-experience-test
description: "廻リ視の体験を実機で丸ごと検証する手順（[XP] テレメトリ / 自動走行 / logcat 収集の罠 / 2026-07-30 のベースライン実測）"
metadata: 
  node_type: memory
  type: project
  originSessionId: 83b2ae19-6926-4454-a4df-b5d350711b24
  modified: 2026-07-30T09:55:21.139Z
---

# 実機で体験を丸ごと検証する

**HMD を被らずに、導入 → 3 周 → 終了を通して観測できる。** 2026-07-30 に一式を用意した。
体験の論理（周回・演出・録画）と映像の質（受信 fps・砂嵐・遅延・表示 fps）を、
どちらもログの数値で判定する。

## 3 点セット

| 何 | どこ | 役割 |
|---|---|---|
| テレメトリ | [`ShowTelemetryHost`](../../Assets/Scripts/Diagnostics/ShowTelemetryHost.cs) | `[XP]` タグで 1 行 1 イベント + 2 秒ごとの集計。**観測専用**（既存の公開イベントとプロパティを読むだけ・状態を書き換えない）。Development ビルドでのみ `RuntimeInitializeOnLoadMethod` で自動生成されるので**シーン再生成は要らない** |
| 自動走行 | [`ShowWalkDebugDriver`](../../Assets/Scripts/Tracking/ShowWalkDebugDriver.cs) | `OVRCameraRig` を動かして歩行を合成。既存コードは無改変。経路は `layout.grid` から BFS で解く（出発と到着のタイル以外を踏まない） |
| 判定 | [`tools/analyze-xp-log.py`](../../tools/analyze-xp-log.py) | show.json を期待値にして突き合わせ。**起きなかったことを引き算で見つける**のが主目的 |

実行は [`tools/run-quest-xp-test.sh`](../../tools/run-quest-xp-test.sh):

```bash
bash tools/run-quest-xp-test.sh idle 90     # 静置（配信・砂嵐・遅延・表示 fps）
bash tools/run-quest-xp-test.sh walk 300    # 自動走行（導入 → 3 周 → 終了）
```

## 収集の罠（両方とも実際に 1 回ぶん観測を落とした）

- **`logcat -G 32M` は端末に拒否されて既定（256KB）のまま黙って据え置かれる。** Unity が
  HudDump / HmdTrace をスタック付きで毎秒出すので、既定では 90 秒で `[XP]` が 3 行しか残らない。
  **16M は通る。** 設定できたか `logcat -g` で確かめること
- **ストリーム（`logcat > file &`）はテスト中に別の adb を打つと切れる。** daemon が再起動して
  ストリームだけが黙って死ぬ（アプリは最後まで走っていたのに、ログは 50 秒で途切れた）。
  **走行中は adb を一切触らず**、終わってから `logcat -d` で一度に回収する
- HMD を被っていないと Quest がアプリをバックグラウンドへ回すことがある（コントロールバーが前面に出る）。
  ただし **Unity の Update は止まらず走り切る**（実測）。`am start` で戻せるが、戻すと
  intent extra が付かないので自動走行は再開しない

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

## ここで見つけて直した 2 件

- **導入演出が Degrade で打ち切られる** → `ShowRunLogic` の導入終了条件に「演出が進行中でない」を追加。
  契約は [streaming.md](../rules/streaming.md) の「体験の骨格」節。
  修正後は **Black → Real → Degrade → Structure → Frame** まで進むようになった（3.5 秒 → 8.7 秒）
- **lag 判定の誤爆で本編 69 秒に再接続 152 回** → 分母を `min(phone_fps, 30)` で頭打ち。
  配信 60fps のとき受信 40fps でも誤爆していた（`recv=39.8/phone=59.4`）。
  修正後は再接続の率が半分になった（0.43 回/秒 → 0.21 回/秒）

## まだ残っている 2 件（次に見るところ）

- **導入演出の最後の段 Swap（枠の中がカメラ映像へ変わる＝この演出の核心）が出ない。**
  `Frame` の尺（2.5 秒）が終わる瞬間に `stage=Off` へ飛ぶ（実測 t=27.17 Frame → t=29.70 Off）。
  `ShowRunLogic` 側は直したので、**残りは `IntroLogic` の段遷移**。Frame → Swap の境界で
  `Active` が落ちるか `Holding` が立つ 1 フレームがあると、そこで導入終了条件が通ってしまう。
  `IntroLogic` の `Swap` 段への遷移と `ShowRunDirector.Update` の実行順序を疑う
- **2 周目 A の演出が区間の滞在（9.3 秒）の中で出ず、離脱の瞬間に決着している**（2 回とも再現）。
  ログの呼び出し元が `TakeRunner.NotifyZoneCommitted` なので `ifMissed:"fireOnExit"` の経路。
  著作は `at:"enter" / offsetSec:0` なので**進入の次フレームで出るはず**。
  1 周目 B（同じ at=enter）は進入と同時に出ているので、条件付きで起きる。
  `TakeRunnerLogic` の `_suppressed` / `_running` が立っていた可能性を疑う

## ⚠ ログ収集はまだ不安定（3 回とも別の形で失われた）

`-d` も ストリームも 5 分を安定して取り切れていない（3 回目はストリームが t=77 で切れた）。
Unity のログ量（HudDump / HmdTrace がスタック付きで毎秒）が支配的。
**確実にするなら `ShowTelemetryHost` が `[XP]` を端末内ファイルへ直接書き、後で pull する**のが本筋。
それまでは走行後すぐに `logcat -d` も併せて取り、行数（3 分の走行なら 2 万行以上）を必ず確認すること。

関連: [[quest_build_and_camera_ip]] / [[show_run_skeleton]] / [[verification_workflow]]
