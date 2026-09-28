---
name: work-round
description: 廻リ視の作りこみを 1 周回す型。演出・絵・物語を触るときに毎回これを通す。発動例：「演出を足して」「ここを怖くして」「絵を詰めて」「1 周回して」。焼く前に予測を書き、安い門を通し、実機で見て、差だけを台帳へ。
---

# 作りこみ 1 周

**1 周 = 1 焼き。** 終わりの判定は「良くなった」ではなく、**`canon/ROUNDS.md` に差が 1 行入ったか**。

## 0. 焼く前に書く（省略しない）

`canon/ROUNDS.md` に**賭け 1 文と予測 3 行**。

- 賭け = 「何をどう変えたら、何が起きるはず」の 1 文
- 予測 = 焼いた後に**確かめられる**形で 3 つ。「良くなる」は予測ではない
- **書けないなら焼かない。** 書けないのは、何を変えているか自分で分かっていないということ

⚠ 後から書き換えない。**外した予測こそ残す。** 進むのは差の側だけ。

## 1. 安い門（落とすためだけに使う）

実機まで持っていく前に、ここで落ちるものを落とす。**通ったことを合格の根拠にしない**
（卓と実機は既に 5 件食い違っている — `memory/sim_device_divergence.md`）。

| 見るもの | 手段 | 秒 |
|---|---|---|
| 尺・滞在・cue の並び | 卓のシミュレータ（node で `show.json` を直接食わせる） | 秒 |
| 合成の絵と数値（luma / sat / grain の比） | `.\tools\unity.ps1 menu composite` → `Assets/Screenshots/cgviz/` | 分 |
| HMD 内の文字 | `.\tools\unity.ps1 menu hud` → `Assets/Screenshots/hud-preview/` | 分 |
| 人形の腕・追従 | `.\tools\unity.ps1 menu actor-motion` → `Assets/Screenshots/actormotion/` | 分 |
| 純ロジック | `.\tools\unity.ps1 test` | 分 |

**HMD を被らないどころか Editor も開かない。** `menu` は batchmode で Unity を起こして
メニューを 1 つ実行し、出力が更新されたかまで見て返る（`unity.ps1 menu` で一覧）。

**焼き直し（シーン / HMD のフォント）は覚えなくてよい。** 忘れたまま焼こうとすると
`unity.ps1 build fixedcam` が打つべきコマンドを名指しして落ちる（2026-08-09〜）。
言われたものを打つ。どちらも冪等。

## 2. 焼いて、走らせて、撮る

```powershell
.\tools\unity.ps1 build fixedcam
```

```bash
py -3.11 tools/quest-record.py --sec 45 --walk --serial <serial>
```

⚠ **`--serial` を渡す（`ANDROID_SERIAL` は効かない）。** 焼いた APK は**両機に入れてから**走る —
古い APK の機に当たると「実装が効いていない」に見える（`memory/quest_fleet_two_devices.md`）。

走行 1 回で画・ログ・判定・切り出しが揃う（`logs/capture/` と `logs/evidence/`）。
**HMD を被らずにここまで来る。**

## 3. 見る

**必ず自分で PNG を開く。** 判定に使うのは `logs/evidence/<日時>/` の**フル解像度の個別 PNG**。
コンタクトシートは索引にすぎない（縮小すると 1cm の線が消える — 2026-07-31 に誤診しかけている）。

機械の側は `logs/capture/<日時>-report.md`（`analyze-xp-log.py`）を読む。ここが言えるのは
**「出るはずのものが出なかった」だけ**で、良し悪しは言えない（`reference/why.md`「品質を採点しない」）。

⚠ **自分のタグだけ見ない。** 締めに実機ログ全体を当たる:

```bash
grep -iE "見つかりません|Failed to|Error|Exception" logs/capture/<日時>_xp.log
```

## 4. 演出を足したなら、観測も足す

新しい演出のキーは **C# の `ShowTelemetryHost` と `analyze-xp-log.py` を対で直す**。
片方だけだと沈黙して食い違う。

**観測するのは「段が進んだ」ではなく「画に何かが出た」。**
2026-07-31、判定が「FAIL ゼロ・演出 7 本すべて OK」と出した走行の画に、導入演出が 1 段も
出ていなかった。段の遷移は完璧に進んでいた。以後 `veil` / `wire` / `cg` / `bgm` / `font` /
`ev=step` のような**効果の実在**を判定に入れている。同じ形で足す。

## 5. 台帳へ

`canon/ROUNDS.md` に**予測との差だけ**を書く。合っていたものは書かなくてよい。

- ユーザーの言葉が出たら `canon/LEDGER.md` へ**逐語で**（`rules/canon-boundary.md`）
- 世界観の案を思いついたら `canon/OPEN.md` の「案」へ。**`LEDGER.md` には入れない**

## 被らないと分からないもの

立体感・スケール・酔い・**怖さの強度**は録画では判定できない（`rules/visual-verification.md`）。
これらは**その場で確かめず、溜めて 1 回にまとめる**（`canon/OPEN.md` Q4）。

**初見（初めて被る人）は消耗品。** 同じ人は 2 回驚けない。
機械で分かることを全部潰してから呼ぶ。配給は `canon/ROUNDS.md`。

## やってはいけないこと

- ❌ 予測を書かずに焼く → 出てきた絵はいつも「まあこんなもの」に見えて、外したことに気づけない
- ❌ 安い門が通ったことを合格の根拠にする → 代理を厚くするほど嘘が増える
- ❌ PNG を開かずにレポートの PASS で済ませる → 演出は目でしか見えない
- ❌ 「まだ壊れている」を見つけるために初見を呼ぶ → 最悪の使い方
- ❌ 企画書（`docs/proposal/` `docs/archive/`）を開く → 2026-08-08 ユーザー宣言
