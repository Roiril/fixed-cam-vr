---
name: quest-fleet-two-devices
description: クエストα/βの serial 対応表と、2 台を交互に使う道具（tools/quest-fleet.py）、機ごとに違って必ず食い違う 4 つ
metadata: 
  node_type: memory
  type: project
  originSessionId: 3f4ae7b7-f1c6-4dfe-ab98-36658a818997
  modified: 2026-07-31T03:37:11.360Z
---

# Quest 2 台を交互に使う

## ⚠⚠ 呼び名 — クエストα / クエストβ（2026-09-04 ユーザー宣言）

ユーザーはこの名前で機を指す。**serial からは導けない。**

| 呼び名 | serial | 役 |
|---|---|---|
| **クエストα** | `2G0YC1ZF890864` | **自動走行を回す機**（ユーザーが被って見る側） |
| **クエストβ** | `2G0YC1ZF7S06BW` | もう 1 台 |

> 「今後クエスト2台を呼び分けるために、片方をクエストα、片方をクエストβとします。
> 片方自動走行させてる間に、自動走行中のをクエストαと呼ぶように統一させてください」

- **対応表は [`tools/quest-fleet.py`](../../tools/quest-fleet.py) の `NAMES` が実体**で、
  `list` / `pick` が `a` / `b` の列に出す。⚠ **この表と対で直す**
- ⚠⚠ **`pick`（いちばん涼しい機）に名前を委ねない。** あれは走行ごとに入れ替わるので、
  α が日によって別の機になる ＝ **人と機械が別の機を指したまま会話が進む**。
  α は serial に固定してある
- ⚠ **走行は `--serial` で α を名指しする。** `pick` の既定に任せると β へ行くことがある

2026-07-31 に整えた。道具は [`tools/quest-fleet.py`](../../tools/quest-fleet.py)。

```bash
py -3.11 tools/quest-fleet.py list          # 全機の状態を 1 画面（熱・帯域・APK・登録・容量）
py -3.11 tools/quest-fleet.py pick          # 次に走らせる機の serial を 1 行
py -3.11 tools/quest-fleet.py sync          # Builds/mawarimi.apk を古い機へ配る
py -3.11 tools/quest-fleet.py reset-config  # 端末キャッシュ show_config.json を消す
py -3.11 tools/quest-fleet.py sleep --others <serial>   # 使わない機を寝かせる
```

[`run-quest-xp-test.sh`](../../tools/run-quest-xp-test.sh) は `SERIAL` を指定しなければ `pick` を呼び、
走行後に `mark` する。使わない機は走行前に寝かせる（`NOSLEEP=1` で無効化）。

## 熱で選ぶ（電池ではない）

2 台とも AC 給電なので電池残量は制約にならない。効くのは SoC 温度で、Quest 3 はファンが無いから
走るたびに温まる。**走った機は熱く、もう一方は冷えている** ── だから「温度の低い方」を選ぶと
待ち時間ゼロで自然に交互運用になる。温度が 3℃ 以内に拮抗したときだけ「前回使っていない方」で決める。

- 温度は `dumpsys thermalservice` の **`Current temperatures from HAL`** の `soc-usr`。
  同じ dumpsys の先頭にある `Cached temperatures` は更新が遅く、**スリープ中の機が 46℃ に見える**
  （実測で 11℃ ずれた）。パースするセクションを間違えない
- `Thermal Status` が 2 (MODERATE) 以上の機は候補から外す。閾値は soc 89℃ / batt 58℃ なので
  通常は当たらない（実測の稼働中は 45〜53℃）
- **スリープ / 復帰は `input keyevent KEYCODE_SLEEP` / `KEYCODE_WAKEUP` が効く**（実測で往復確認）。
  復帰は 1.5 秒。寝かせると 30 秒で 2℃ 下がった

## ⚠ 機ごとに違って必ず食い違う 4 つ

交互運用の本当の危険はここ。**同じ APK を入れただけでは同じにならない。**

| 何 | どこ | 実測した食い違い | 揃え方 |
|---|---|---|---|
| **設定キャッシュ** | `files/show_config.json` | 片方にだけ `run.intro.startLineId` が無く、**導入の始まり方が機ごとに違っていた**。`timeline.rev` は両方 21 で一致していたので rev では気づけない | `reset-config` で消す → 焼き込み / ライブで拾い直す |
| **APK** | — | 2 時間 20 分ずれていた（片方に直近の修正が入っていない）。直したはずの不具合が「再発した」ように見える | `sync`（mtime 比較で古い機だけ入れる） |
| **Wi-Fi の帯域** | — | 5GHz 866Mbps と 2.4GHz 192Mbps で**別の AP に繋がっていた** | HMD の設定で手動（下記） |
| **位置合わせ** | `files/registration.json` | origin と yaw が別物（yaw で 10° 差）。**これは正常** — トラッキング原点は機ごとに違うので、コピーすると壊れる | 各機で 1 回ずつ登録する。`list` の course 列で有無と残差が見える |

**キャッシュは焼き込み（StreamingAssets）より優先される**（焼き込み < キャッシュ < ライブ）。
だから「APK を焼き直した = 設定も新しい」は成り立たない。実測では PC の show.json・焼き込み・
2 台のキャッシュの **4 者がずれていて、正しいのは 1 つだけ**だった。

→ 恒久対策として実機が `[XP] ev=config` で使った設定を報告し、`analyze-xp-log.py` が PC の
show.json と突き合わせて FAIL を出す（[[onsite_experience_test]]）。

## 帯域の効き方（2026-07-31 に両帯域で同じ APK・同じ設定で実測）

**平均は 2.4GHz でも足りる。差が出るのは最大値。**

| | 5GHz (866Mbps) | 2.4GHz (192Mbps) |
|---|---|---|
| 受信 fps（配信 49.9 に対し） | 45.4 | 43.8 |
| 到着の揺らぎ 平均 | 10.8ms | 17.0ms |
| **到着の揺らぎ 最大** | **25ms** | **125ms** |
| 取りこぼし | 529 | 1,585 |

企画書の「映像遅延 100ms 以内」に対して 5GHz は余裕、**2.4GHz は最大で超える**。→ 本番は 5GHz。

**ただし 2.4GHz でも体験は成立した**（演出 7 本すべて・導入 Swap まで・3 周完走・FAIL ゼロ）。
「2.4GHz だから演出が出ない」と決めつけないこと ── 07-30 に演出が出なかった真因は帯域ではなく
stall watchdog の誤発火（`_everReceived` ガード不在）だった。**まずログの数値を見る。**

なお導入演出の最後の段（Swap）は「映像が届いていること」(`liveFresh`) を要求し、3 秒待って
駄目なら**段を飛ばして終わる**（`IntroLogic`）。映像が細ると演出が消えうる構造は変わらないので、
`run-quest-xp-test.sh` は走行前に 2.4GHz なら警告を出す（誤診を防ぐため。禁止ではない）。

**⚠ 5GHz への切り替えは adb からできない。** `cmd wifi` にあるのは `connect-network`
（パスフレーズ必須・持っていない）と `forget-network`（**パスフレーズが無いと戻せない**）だけで、
「保存済みネットワークへ接続」に相当するサブコマンドが無い。HMD の Wi-Fi 設定で手動で選ぶ。

## adb pull は使えない

`/sdcard/Android/data/<pkg>/` は Android 11+ の scoped storage で保護されていて、
**`adb pull` は黙って 0 バイトを作る**（エラーも出ない）。`adb exec-out cat <path>` なら通る
（`shell cat` は改行が CRLF に化けるので exec-out の方）。`quest-fleet.py` の `pull` はこれで書いてある。

## 現在の 2 台（2026-07-31）

| serial | 帯域 | 備考 |
|---|---|---|
| `2G0YC1ZF7S06BW` | **5GHz** (kougaku-lab-A・866Mbps) | 検証の主力 |
| `2G0YC1ZF890864` | 2.4GHz (kougaku-lab-G・192Mbps) | **5GHz 化が要る**（手動） |

関連: [[onsite_experience_test]] / [[quest_build_and_camera_ip]] / [[show_json_is_live_config]]

## 電池は温度と同じくらい効く（2026-08-01 ユーザー指摘）

「バッテリーが意外とすぐ切れる」。走行 1 回で数分ぶん食い、**起きたまま置くだけでも減る**ので、
気づいたら「検証を続けたいのに残量が無い」になる。ルールに書くだけでは通らないので道具に入れた:

- `quest-record.py` は走行前に残量を読み、**充電していない & 25% 未満なら中止**する
  （40% 未満は警告して続行）。走り終わったら**実機を寝かせる**。続けて何度も走らせるなら `--keep-awake`
- `quest-fleet.py pick` は電池の乏しい機（充電していない & 25% 未満）を候補から外す。
  全機がそうなら「充電するか寝かせて待て」と言う
- 手で寝かせる: `py -3.11 tools/quest-fleet.py sleep <serial>` / 起こす: `wake`
- **1 回使ったらこまめに寝かせる。** 待ち時間（ビルド 10 分など）のあいだ起こしておく理由は無い

## ⚠⚠ `ANDROID_SERIAL` は `quest-record.py` に効かない。走行ごとに機が変わる（2026-08-22 実害）

**`adb` には効くが走行スクリプトには効かない。** `quest-record.py` は自分で
`quest-fleet.py pick` を呼び、**温度と最終使用で機を選び直す**（それが本来の設計）。
なので `ANDROID_SERIAL=<serial> py -3.11 tools/quest-record.py …` と書くと、
**インストール先と走行先が別の機になる**ことがある。

⇒ **走行の機は `--serial <serial>` で渡す。**

```bash
adb -s <serial> install -r --no-streaming Builds/mawarimi.apk
py -3.11 tools/quest-record.py --sec 200 --walk --serial <serial>
```

⚠⚠ **症状が「実装が効いていない」の顔で出る。** 2026-08-22 に、直したはずのテレメトリが
1 つも出ず、`swap=` が 9 つ組から 6 つ組へ**逆行**した。コードにも APK にも異常は無く、
**古い APK が入ったもう 1 台で走っていた**のが真相（10 分溶かした）。

**気づき方は 2 つ。どちらも走行の直後に 5 秒で見られる:**

- `logs/capture/<日時>_meta.json` の **`serial`** を見る（走行が実際に使った機）
- テレメトリの**キーの数が前回より減っていたら、まず APK の新旧を疑う**
  （コードを疑う前に。ログのキーは単調に増えるので、減るのは古い APK でしか起こらない）

⭐ **走行前に両機へ入れておくのがいちばん安い**（`adb -s <各 serial> install`）。
どちらが選ばれても新しい APK になる。
