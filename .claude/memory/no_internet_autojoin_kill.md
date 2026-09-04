---
name: no-internet-autojoin-kill
description: 上流の無い展示網を Android が恒久無効化し、次の電源投入で全機が戻らなくなる。繋がっている間は一切表に出ない
metadata: 
  node_type: memory
  type: project
  originSessionId: aa629b9d-978a-41e5-b9f1-d22062359145
  modified: 2026-09-04T15:33:48.368Z
---

# 上流が無いと、Android が展示網の自動接続を殺す（2026-09-05 実測）

展示のネットワーク（Aterm・`kougaku-lab-exp-a`）は **WAN に何も繋がない**設計。
体験は全部その中で閉じるので正しい設計なのだが、**Android がそれを「使えない網」と判断する。**

```
dumpsys wifi:
  NetworkSelectionStatus          NETWORK_SELECTION_PERMANENTLY_DISABLED
  mNetworkSelectionDisableReason  NETWORK_SELECTION_DISABLED_NO_INTERNET_PERMANENT
  hasEverConnected: true
  hasNeverDetectedCaptivePortal: true
→ 20〜60 秒おきに Ignoring network selection disabled SSID: "kougaku-lab-exp-a"
```

## ⚠⚠ たちの悪さ

**繋がっている間は 1 ビットも表に出ない。** 既に張れている接続はそのまま続くので、
卓も画も音も正常。**次に電源を入れ直した時に、初めて戻ってこない。**

⇒ **展示の朝、全機が同時に踏む形。** しかも「昨日まで動いていた」ので原因に到達しにくい。

**相手は Quest だけではない。配信スマホ 3 台も Android** なので同じ穴を持つ。

## 見つけた経緯（推測ではなく実測）

「電源を入れるところからの流れを作る」という依頼で、**再起動して静的 IP が戻るかを試した**のが発端。

1. Quest β を再起動 → **182 秒たっても Wi-Fi に戻らない**
2. AP は見えている（`-35dBm`）・保存済みも残っている・Wi-Fi は enabled。それでも繋がらない
3. `dumpsys wifi` で `NETWORK_SELECTION_PERMANENTLY_DISABLED` と理由を特定
4. **α を見たら、繋がっているのに同じ状態だった** ＝ 落ちていないだけで既に死んでいた
5. α も再起動 → 同じく戻らない（**2 台とも**）

⭐ 試さなければ**当日の朝まで誰も気づけなかった**。ドキュメントには「静的 IP なので会場が変わっても
戻る」と書いてあり、実際 IP の設定は正しかった。**壊れていたのは IP ではなく「そこへ繋ぎに行くか」。**

## 直す / 予防する

| | 何をする | 誰が |
|---|---|---|
| **直す**（既に付いた恒久無効を消す） | **端末の Wi-Fi 設定で、その網を 1 度手で選び直す**（手で選ぶと `User Selected` が立って解ける） | 人。⚠ **adb からは戻せない** |
| **予防**（二度と付かないようにする） | 接続チェックを切る（下） | `onsite.py wifi-guard` / `adb-open` が自動で入れる |

```bash
adb -s <serial> shell "settings put global captive_portal_mode 0"
adb -s <serial> shell "settings put global captive_portal_detection_enabled 0"
```

⚠ **予防は「これから」にしか効かない。** 既に書き込まれた恒久無効は消えない
（実測: フラグを入れてから再起動しても戻らなかった）。**順番は「手で選び直す → 予防」ではなく
「予防を入れておく → 一度手で選び直す」**で、以後は付かない。

⭐ **根っこから消すなら、Aterm の WAN を上流のある回線へ挿す。** 検証が通れば
この判定自体が起きないので、Quest もスマホも全部まとめて解決する。会場に挿せる線があるなら
それがいちばん強い（LAN 側は NAT の内側のままなので、端末が別の AP へ移る心配は増えない）。

## 道具

```bash
py -3.11 tools/onsite.py wifi-guard   # 殺されていないか見る＋予防を入れる
py -3.11 tools/onsite.py adb-open     # USB のついでに予防を入れる（配信スマホはこれしか経路が無い）
py -3.11 tools/onsite.py check        # Quest ごとに「自動接続」の行が出る
```

⚠ **配信スマホには USB を挿すしか手が無い**（`:5555` は端末を再起動すると閉じ、
2026-09-04・05 とも 3 台とも閉じていた）。設営で USB を挿す一瞬が、予防を入れられる唯一の機会。

## 同時に確かめた、問題なかったこと

- **クライアント間分離は掛かっていない。** Quest → カメラ 3 台と Quest → 卓、TCP が全部通る
  （計器の校正: 閉じたポートと居ない相手は `rc=1` を返すことを確認済み）
- **帯域は問題にならない。** 3 本同時で**合計 4.87 Mbps**・各 28〜29fps（リンクは 866Mbps）
- **5GHz は 5180MHz ＝ W52。** DFS の瞬断は起きない
- **待機中の ping が 95〜123ms なのは端末の省電力。** 引いている最中は 4〜7ms に戻る
  （⚠ 待機中の数字で電波を判定しない）
- ⚠ カメラ B は熱段 2 / `thermalStatus=3` / 40.3℃ で、昨日から下がっていない

関連: [[onsite_day_ops]] / [[camera_fleet]] / [[quest_fleet_two_devices]]
