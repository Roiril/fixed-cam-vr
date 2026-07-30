---
name: onsite-experience-test
description: "廻リ視の体験を実機で丸ごと検証する手順（[XP] テレメトリ / 自動走行 / logcat 収集の罠 / 2026-07-30 のベースライン実測）"
metadata: 
  node_type: memory
  type: project
  originSessionId: 83b2ae19-6926-4454-a4df-b5d350711b24
  modified: 2026-07-30T10:55:26.900Z
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

- ~~導入演出の最後の段 Swap が出ない~~ → **原因判明・修正済み（実機検証待ち）**。
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

## ⚠ ログ収集はまだ不安定（3 回とも別の形で失われた）

`-d` も ストリームも 5 分を安定して取り切れていない（3 回目はストリームが t=77 で切れた）。
Unity のログ量（HudDump / HmdTrace がスタック付きで毎秒）が支配的。
**確実にするなら `ShowTelemetryHost` が `[XP]` を端末内ファイルへ直接書き、後で pull する**のが本筋。
それまでは走行後すぐに `logcat -d` も併せて取り、行数（3 分の走行なら 2 万行以上）を必ず確認すること。

関連: [[quest_build_and_camera_ip]] / [[show_run_skeleton]] / [[verification_workflow]]
